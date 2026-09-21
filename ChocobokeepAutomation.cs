using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Ipc;

namespace AllTheThings;

/// <summary>
/// Läuft nacheinander alle aktuell noch nicht freigeschalteten Chocobo-Reitstände der Zone ab
/// (siehe Plugin.GetChocobokeepEntries) und interagiert mit dem jeweiligen Chocobokeep-NPC -
/// dieselbe Grundfunktionsweise wie AetheryteAutomation (Mount rufen, mit vnavmesh hinlaufen,
/// interagieren, auf den Freischalt-Abschluss warten), aber ohne dessen MapMarker-Auflösung/
/// Bezirkswechsel-per-Lifestream: Chocobokeep-Einträge haben schon eine rohe Weltposition (siehe
/// CollectibleEntry.WorldPosition, von Hand erfasst - siehe Plugin.ChocobokeepLocations), es wird
/// daher wie bei HuntingLogAutomation per Karten-Flagge + vnavmesh.Query.Mesh.FlagToPoint dorthin
/// gelaufen. Der Freischalt-Abschluss selbst wird (wie bei Aetheryten) über einen echten
/// Spielstand-Flag geprüft - Plugin.IsChocoboTaxiStandUnlocked (UIState.IsChocoboTaxiStandUnlocked).
/// </summary>
public sealed class ChocobokeepAutomation
{
    private enum State
    {
        Idle,
        Mounting,
        MovingTo,
        Interacting,
    }

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);
    private const float PathTolerance = 10f;
    private const float FinalApproachDistance = 3.5f;
    private const float SprintDisableDistance = 8f;
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InteractObjectGracePeriod = TimeSpan.FromSeconds(5);

    // Wie lange nach der Interaktion auf den tatsächlichen Freischalt-Abschluss gewartet wird
    // (siehe Plugin.IsChocoboTaxiStandUnlocked) - danach gilt der Versuch als gescheitert.
    private static readonly TimeSpan UnlockWaitTimeout = TimeSpan.FromSeconds(15);

    // UIState.IsChocoboTaxiStandUnlocked wird oft schon true, BEVOR das Talk-Fenster überhaupt
    // sichtbar wird (nicht erst danach, wie ursprünglich angenommen) - eine reine
    // "Fenster gerade nicht offen"-Prüfung direkt nach der Interaktion sieht daher fälschlich
    // "nichts offen", obwohl der Dialog nur noch nicht aufgepoppt ist. Deshalb wird nach dem
    // Interagieren immer mindestens so lange gewartet (und dabei weiter TryAdvanceTalkDialogue/
    // TryDismissChocobokeepSelectString aufgerufen), bevor überhaupt geprüft wird, ob alles zu ist.
    private static readonly TimeSpan MinInteractionSettleDuration = TimeSpan.FromSeconds(2);

    // Verhindert eine Endlosschleife, falls ein Chocobokeep aus irgendeinem Grund nicht erreicht/
    // gefunden werden kann - nach so vielen Versuchen wird derselbe Eintrag übersprungen.
    private const int MaxAttemptsPerTarget = 2;
    private readonly Dictionary<uint, int> attemptCounts = new();
    private readonly HashSet<uint> skippedIds = new();

    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> navmeshIsReady;
    private readonly ICallGateSubscriber<Vector3?> queryFlagToPoint;

    private State state = State.Idle;
    private CollectibleEntry? currentTargetEntry;
    private Vector3 currentTargetPosition;
    private DateTime stateEnteredAt;
    private bool hasInteractedThisCycle;
    private bool didFinalApproach;
    private bool hasSeenPathRunning;
    private DateTime? interactObjectNotFoundSince;
    private DateTime lastRemountAttempt = DateTime.MinValue;
    private readonly NavigationStuckDetector stuckDetector = new();

    public bool IsActive { get; private set; }

    private static readonly TimeSpan StatusLingerDuration = TimeSpan.FromSeconds(8);
    private string statusText = string.Empty;
    private DateTime statusSetAt = DateTime.MinValue;

    public string StatusText
    {
        get => statusText;
        private set
        {
            statusText = value;
            statusSetAt = DateTime.UtcNow;
        }
    }

    public bool ShouldShowStatusText => IsActive || DateTime.UtcNow - statusSetAt < StatusLingerDuration;

    public ChocobokeepAutomation()
    {
        pathfindAndMoveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        navmeshIsReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        queryFlagToPoint = Plugin.PluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
    }

    public bool IsVNavmeshAvailable()
    {
        try
        {
            return pathfindAndMoveCloseTo.HasFunction && pathIsRunning.HasFunction && navmeshIsReady.HasFunction;
        }
        catch
        {
            return false;
        }
    }

    private void StopPath()
    {
        try
        {
            if (pathStop.HasAction)
                pathStop.InvokeAction();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Stoppen von vnavmesh.");
        }
    }

    public void Start()
    {
        IsActive = true;
        state = State.Idle;
        currentTargetEntry = null;
        skippedIds.Clear();
        attemptCounts.Clear();
        interactObjectNotFoundSince = null;
        StatusText = Loc.T("Automation gestartet...", "Automation started...");
    }

    public void Stop()
    {
        IsActive = false;
        state = State.Idle;
        currentTargetEntry = null;
        StopPath();
        Plugin.ClearNavigationTarget();
    }

    public void MarkUnavailable()
    {
        StatusText = Loc.T("vnavmesh nicht gefunden - bitte installieren.", "vnavmesh not found - please install it.");
    }

    /// <summary>
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell noch nicht besuchten
    /// Chocobokeep-Einträgen DER AKTUELLEN ZONE aufgerufen werden.
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> missingChocobokeepsInZone)
    {
        if (!IsActive)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(missingChocobokeepsInZone);
                    break;

                case State.Mounting:
                    UpdateMounting();
                    break;

                case State.MovingTo:
                    UpdateMoving();
                    break;

                case State.Interacting:
                    UpdateInteracting();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler bei der Chocobokeep-Automation - wird gestoppt.");
            StatusText = Loc.T("Fehler bei vnavmesh - Automation gestoppt.", "Error talking to vnavmesh - automation stopped.");
            Stop();
        }
    }

    private void TryStartNext(IReadOnlyList<CollectibleEntry> missingChocobokeepsInZone)
    {
        // Anders als AetheryteAutomation kein Bezirkswechsel per Lifestream - in geteilten
        // Hauptstädten kann die übergebene Liste (siehe CompactOverlayWindow.allForZone) auch
        // Chocobokeeps aus einem NACHBARBEZIRK enthalten. Deren Weltposition liegt aber in einer
        // anderen, hier gar nicht geladenen Zoneninstanz - ohne diesen Filter würde vnavmesh dorthin
        // einen sinnlosen Laufauftrag bekommen. Bewusst auf die TATSÄCHLICHE aktuelle Zone
        // beschränkt, nicht die für Aetheryten/Quests "aufgelöste" effectiveTerritoryId.
        var currentTerritory = Plugin.ClientState.TerritoryType;
        var candidates = missingChocobokeepsInZone
            .Where(e => e.WorldPosition.HasValue && e.TerritoryTypeId == currentTerritory && !skippedIds.Contains(e.Id))
            .ToList();
        if (candidates.Count == 0)
        {
            // Kein Bezirkswechsel (siehe Kommentar oben) - auch wenn laut Zonen-Liste noch
            // Chocobokeeps in einem Nachbarbezirk fehlen, endet der Lauf hier; dorthin muss man
            // selbst laufen und die Automation neu starten.
            StatusText = Loc.T("Keine Chocobokeeps mehr in dieser Zone.", "No chocobokeeps left in this zone.");
            Stop();
            return;
        }

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var next = candidates.OrderBy(e => Vector3.Distance(playerPos, e.WorldPosition!.Value)).First();
        StartMovingTo(next);
    }

    private void StartMovingTo(CollectibleEntry entry)
    {
        var attempts = attemptCounts.GetValueOrDefault(entry.Id, 0) + 1;
        attemptCounts[entry.Id] = attempts;
        if (attempts > MaxAttemptsPerTarget)
        {
            skippedIds.Add(entry.Id);
            StatusText = Loc.T($"Übersprungen (zu oft versucht): {entry.Name}", $"Skipped (too many attempts): {entry.Name}");
            state = State.Idle;
            return;
        }

        if (!navmeshIsReady.InvokeFunc())
        {
            StatusText = Loc.T("Warte auf vnavmesh-Navmesh für diese Zone...", "Waiting for vnavmesh's navmesh for this zone...");
            return;
        }

        // Genau derselbe Trick wie bei HuntingLogAutomation/AetheryteAutomation: die Karten-Flagge
        // auf die (rohe) Zielposition setzen und vnavmesh nach einem begehbaren Punkt in deren Nähe
        // fragen.
        Plugin.OpenEntryMap(entry);
        var floorPoint = queryFlagToPoint.InvokeFunc();
        if (floorPoint == null)
        {
            skippedIds.Add(entry.Id);
            StatusText = Loc.T($"Übersprungen (nicht erreichbar): {entry.Name}", $"Skipped (not reachable): {entry.Name}");
            state = State.Idle;
            return;
        }

        currentTargetEntry = entry;
        currentTargetPosition = floorPoint.Value;
        didFinalApproach = false;

        if (Plugin.TryRequestAetheryteMount())
        {
            state = State.Mounting;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Rufe Mount, dann: {entry.Name}...", $"Summoning mount, then: {entry.Name}...");
            return;
        }

        BeginPathfind();
    }

    private void BeginPathfind()
    {
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var accepted = false;

        // Fliegend nur versuchen, wenn Plugin.CanFly gerade true ist - sonst nimmt vnavmesh einen
        // Flugauftrag teils trotzdem an, obwohl der Charakter gar nicht abheben kann, und hüpft nur
        // sinnlos am Boden herum statt zu laufen.
        if (mounted && Plugin.CanFly)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, true, PathTolerance);

        if (!accepted)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, PathTolerance);

        if (!accepted)
        {
            SkipCurrent(Loc.T("vnavmesh lehnt Laufweg ab", "vnavmesh rejected the path"));
            return;
        }

        state = State.MovingTo;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
        stuckDetector.Reset();
        StatusText = Loc.T($"Laufe zu: {currentTargetEntry?.Name}...", $"Walking to: {currentTargetEntry?.Name}...");
    }

    private void UpdateMounting()
    {
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            BeginPathfind();
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > MountWaitTimeout)
            BeginPathfind();
    }

    private void UpdateMoving()
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        if (pathIsRunning.InvokeFunc())
        {
            hasSeenPathRunning = true;

            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            if (Vector3.Distance(playerPos, currentTargetPosition) > SprintDisableDistance)
                Plugin.TryUseSprint();

            // Falls unterwegs durch Schwimmen zwangsweise abgestiegen wurde - sobald wieder Land
            // erreicht ist, erneut aufsitzen.
            Plugin.TryRemountAfterForcedDismount(ref lastRemountAttempt);

            // Steckengeblieben (z.B. gegen eine Wand) - Pfad neu anfordern statt untätig zu warten.
            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[ChocobokeepAutomation] UpdateMoving({currentTargetEntry.Name}): scheinbar steckengeblieben - Laufweg wird neu angefordert.");
                StopPath();
                BeginPathfind();
                return;
            }

            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
                SkipCurrent(Loc.T("Laufweg dauert zu lange", "Path is taking too long"));

            return;
        }

        if (hasSeenPathRunning)
        {
            state = State.Interacting;
            stateEnteredAt = DateTime.UtcNow;
            hasInteractedThisCycle = false;
            interactObjectNotFoundSince = null;
            StatusText = Loc.T("Interagiere...", "Interacting...");
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > PathStartGracePeriod)
            SkipCurrent(Loc.T("Laufweg nie gestartet", "Movement never started"));
    }

    private void UpdateInteracting()
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        // Bewusst die von Hand erfasste Position aus ChocobokeepLocations als Suchanker (nicht
        // currentTargetPosition, den vnavmesh-aufgelösten Bodenpunkt) - in mehrstöckigen Zonen kann
        // FlagToPoint einen Punkt auf einer anderen Ebene/Plattform liefern (hier z.B. 25y zu hoch),
        // während der NPC selbst fast exakt an der manuell erfassten Koordinate steht.
        var gameObject = FindNearestChocobokeepObject(currentTargetEntry.WorldPosition!.Value, 15f);
        if (gameObject == null)
        {
            interactObjectNotFoundSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - interactObjectNotFoundSince.Value < InteractObjectGracePeriod)
                return;

            SkipCurrent(Loc.T("Objekt trotz Ankunft nicht gefunden", "object not found despite arriving"));
            return;
        }

        if (!hasInteractedThisCycle && !didFinalApproach)
        {
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            var distanceToObject = Vector3.Distance(playerPos, gameObject.Position);
            if (distanceToObject > FinalApproachDistance)
            {
                didFinalApproach = true;
                currentTargetPosition = gameObject.Position;

                var accepted = pathfindAndMoveCloseTo.InvokeFunc(gameObject.Position, false, FinalApproachDistance);
                if (!accepted)
                {
                    SkipCurrent(Loc.T("Laufweg zum Objekt abgelehnt", "vnavmesh rejected the approach"));
                    return;
                }

                state = State.MovingTo;
                stateEnteredAt = DateTime.UtcNow;
                hasSeenPathRunning = false;
                return;
            }
        }

        if (!hasInteractedThisCycle)
        {
            if (!Plugin.IsCurrentTarget(gameObject))
            {
                Plugin.Log.Info($"[ChocobokeepAutomation] UpdateInteracting(#{currentTargetEntry.Id}): setze Ziel auf '{gameObject.Name}'.");
                Plugin.SetTarget(gameObject);
                return;
            }

            Plugin.Log.Info($"[ChocobokeepAutomation] UpdateInteracting(#{currentTargetEntry.Id}): interagiere mit '{gameObject.Name}' @ {gameObject.Position} (Distanz={Vector3.Distance(Plugin.ObjectTable.LocalPlayer?.Position ?? gameObject.Position, gameObject.Position)}).");
            Plugin.InteractWithGameObject(gameObject);
            hasInteractedThisCycle = true;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        // Anders als bei Aetheryten poppt hier nach der Interaktion erst ein mehrzeiliges NPC-
        // Gespräch ("Well met, traveler!..."), danach ein SelectString-Menü ("Reitvogel-
        // Passagierdienst nutzen?") auf - beides blockiert die Freischaltung, bis es geschlossen
        // wird. Jeden Frame erneut versucht, bis keins von beidem mehr offen ist (einmaliger Aufruf
        // würde bei mehrzeiligem Text nicht reichen).
        Plugin.TryAdvanceTalkDialogue();
        Plugin.TryDismissChocobokeepSelectString();

        // Wie bei Aetheryten: die Freischaltung selbst braucht nach der Interaktion noch einen
        // Moment (z.B. eine Bestätigung im Auswahlmenü), bevor UIState.IsChocoboTaxiStandUnlocked
        // wirklich auf true springt - erst danach als erledigt werten.
        if (Plugin.IsChocoboTaxiStandUnlocked(currentTargetEntry.Id))
        {
            // Der Flag wird oft schon true, WÄHREND der NPC gerade erst zu reden anfängt (nicht erst
            // danach) - eine reine "Fenster gerade nicht offen"-Prüfung direkt nach der Interaktion
            // sieht dann fälschlich "nichts offen", obwohl der Dialog nur noch nicht aufgepoppt ist
            // (siehe MinInteractionSettleDuration). Ohne die zusätzliche Prüfung auf ein noch offenes
            // Talk-/SelectString-Fenster würde hier sofort "fertig" gemeldet und niemand würde das
            // Gespräch weiterklicken - es bliebe für immer offen stehen, weil UpdateInteracting
            // danach nicht mehr aufgerufen wird.
            if (DateTime.UtcNow - stateEnteredAt < MinInteractionSettleDuration)
                return;

            if (Plugin.IsChocobokeepDialogueOpen() || Plugin.Condition[ConditionFlag.OccupiedInEvent] || Plugin.Condition[ConditionFlag.Occupied])
                return;

            Plugin.Log.Info($"[ChocobokeepAutomation] UpdateInteracting(#{currentTargetEntry.Id}): freigeschaltet.");
            currentTargetEntry = null;
            state = State.Idle;
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > UnlockWaitTimeout)
            SkipCurrent(Loc.T("Freischalten hat nicht geklappt", "unlocking did not go through"));
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearestChocobokeepObject(Vector3 nearPosition, float maxDistance)
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? nearest = null;
        var bestDistance = maxDistance;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventNpc)
                continue;
            if (!string.Equals(obj.Name.TextValue, "Chocobokeep", StringComparison.OrdinalIgnoreCase))
                continue;

            var distance = Vector3.Distance(obj.Position, nearPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = obj;
            }
        }

        return nearest;
    }

    private void SkipCurrent(string reason)
    {
        Plugin.Log.Info($"[ChocobokeepAutomation] SkipCurrent(#{currentTargetEntry?.Id}): {reason}");

        if (currentTargetEntry != null)
            skippedIds.Add(currentTargetEntry.Id);

        StatusText = Loc.T($"Übersprungen ({reason})", $"Skipped ({reason})");

        StopPath();

        state = State.Idle;
        currentTargetEntry = null;
    }
}
