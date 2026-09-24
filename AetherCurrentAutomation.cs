using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

/// <summary>
/// Läuft nacheinander alle aktuell noch fehlenden Ätherströmungen der Zone ab (siehe
/// CollectionData/aethercurrents.json - Weltpositionen aus einem Community-Export, da weder das
/// Lumina-Sheet "AetherCurrent" selbst noch "MapMarker" dafür Positionsdaten liefern - siehe
/// Plugin.DumpAetherCurrentDebugInfo(AllZones), mit dem das empirisch ausgeschlossen wurde).
/// Anders als bei Aetheryten gibt es keinen dauerhaften Kartenpin, dafür (bestätigt) eine explizite
/// Klick-Interaktion - die Automation läuft daher zum nächsten ObjectKind.EventObj bei der
/// Zielposition und interagiert damit, genau wie AetheryteAutomation.UpdateInteracting es für
/// Aetheryten tut, nur mit EventObj statt ObjectKind.Aetheryte als Objektart.
/// </summary>
public sealed class AetherCurrentAutomation
{
    private enum State
    {
        Idle,
        Mounting,
        MovingTo,
        Interacting,
    }

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);

    // Ätherströmungen haben (anders als Aetheryten) kein festes Weltobjekt, an das man exakt
    // heranlaufen könnte - die Ankunftstoleranz ist daher etwas großzügiger als
    // AetheryteAutomation.InteractDistance, um trotz ungenauer Community-Koordinaten trotzdem noch
    // in den (unbekannten, vermutlich mehrere Yalm großen) Entdeckungsradius zu kommen.
    private const float ArrivalTolerance = 6f;
    private const float SprintDisableDistance = 8f;
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);

    // Umkreis um die Zielposition, in dem nach dem interagierbaren Weltobjekt gesucht wird -
    // Ätherströmungen haben keine eigene ObjectKind, sondern tauchen (Vermutung, analog zu anderen
    // Feldinteraktionen) als ObjectKind.EventObj auf. Großzügiger als AetheryteAutomation's 15f, da
    // die Community-Koordinaten hier ungenauer sein können als die aus dem Spiel selbst berechneten
    // Aetheryte-Positionen.
    private const float InteractObjectSearchRadius = 20f;

    // Wie lange nach Ankunft ohne passendes Objekt in der Nähe gewartet wird, bevor aufgegeben wird
    // (kurze Ladeverzögerung wie bei AetheryteAutomation.InteractObjectGracePeriod).
    private static readonly TimeSpan InteractObjectGracePeriod = TimeSpan.FromSeconds(5);

    // Wie lange nach dem Interagieren auf die tatsächliche Freischaltung gewartet wird (der
    // "Entdecken"-Effekt braucht evtl. einen kurzen Moment, ähnlich AetheryteAutomation.
    // UnlockWaitTimeout).
    private static readonly TimeSpan UnlockWaitTimeout = TimeSpan.FromSeconds(15);

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
    private bool hasSeenPathRunning;
    private DateTime lastRemountAttempt = DateTime.MinValue;
    private readonly NavigationStuckDetector stuckDetector = new();
    private readonly FlightPathUpgrade flightUpgrade = new(); // siehe Plugin.FlightPathUpgrade (Flugverbots-Bereiche)
    private bool hasInteractedThisCycle;
    private DateTime? interactObjectNotFoundSince;

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

    public AetherCurrentAutomation()
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
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell fehlenden Ätherströmungen
    /// DER AKTUELLEN ZONE aufgerufen werden.
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> aetherCurrentsInZone)
    {
        if (!IsActive)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(aetherCurrentsInZone);
                    break;

                case State.Mounting:
                    UpdateMounting();
                    break;

                case State.MovingTo:
                    UpdateMoving(aetherCurrentsInZone);
                    break;

                case State.Interacting:
                    UpdateInteracting(aetherCurrentsInZone);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler bei der Ätherströmungs-Automation - wird gestoppt.");
            StatusText = Loc.T("Fehler bei vnavmesh - Automation gestoppt.", "Error talking to vnavmesh - automation stopped.");
            Stop();
        }
    }

    private static bool StillNeeded(IReadOnlyList<CollectibleEntry> entries, uint id) => entries.Any(e => e.Id == id);

    private void TryStartNext(IReadOnlyList<CollectibleEntry> entries)
    {
        // HasGoToTarget statt nur WorldPosition.HasValue - Ätherströmungen kommen (anders als
        // Hunting-Log-Monster) meist als reine Kartenkoordinate (VendorMapX/Y) aus einem Community-
        // Export, ohne eigene rohe Weltposition (siehe aethercurrents.json-Kommentar in
        // CollectionData.cs). Plugin.OpenEntryMap in StartMovingTo kommt mit beiden Varianten klar.
        var candidates = entries.Where(e => e.HasGoToTarget && !skippedIds.Contains(e.Id)).ToList();
        if (candidates.Count == 0)
        {
            StatusText = Loc.T(
                "Keine Ätherströmungen mit bekannter Position mehr in dieser Zone.",
                "No aether currents with a known position left in this zone.");
            Stop();
            return;
        }

        // Nur sortierbar für Einträge MIT roher Weltposition - reine Kartenkoordinaten-Einträge
        // bleiben einfach in der gegebenen Reihenfolge (kommt praktisch aufs Gleiche raus, da ohnehin
        // alle nacheinander abgelaufen werden).
        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var next = candidates
            .OrderBy(e => e.WorldPosition.HasValue ? Vector3.Distance(playerPos, e.WorldPosition.Value) : float.MaxValue)
            .ThenBy(e => e.Name)
            .First();
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

        // Genau derselbe Trick wie bei GoToAutomation/HuntingLogAutomation: die Karten-Flagge auf
        // die (rohe) Zielposition setzen und vnavmesh nach einem begehbaren Punkt in deren Nähe
        // fragen - Ätherströmungen liegen oft in der Luft/an Klippenkanten, eine reine
        // Koordinatensuche (PointOnFloor) fände dort häufig gar keinen begehbaren Punkt.
        Plugin.OpenEntryMap(entry, showMapWindow: false);
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
        hasInteractedThisCycle = false;
        interactObjectNotFoundSince = null;

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
        var flyingAccepted = false;

        // Fliegend zuerst versuchen, aber nur wenn Plugin.CanFly gerade true ist (Fliegen in dieser
        // Zone bereits freigeschaltet) - vnavmesh nimmt einen Flugauftrag sonst teils trotzdem an,
        // obwohl der Charakter gar nicht abheben kann, und hüpft nur sinnlos am Boden herum statt zu
        // laufen. Ätherströmungen liegen zwar oft erhöht/schwer zu Fuß erreichbar, aber genau die
        // ersten einer Zone müssen ohnehin ohne Fliegen erreichbar sein (Henne-Ei: Fliegen schaltet
        // erst frei, wenn alle Strömungen der Zone eingesammelt sind) - zu Fuß ist also immer ein
        // gültiger Fallback.
        if (mounted && Plugin.CanFly)
            accepted = flyingAccepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, true, ArrivalTolerance);

        if (!accepted)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, ArrivalTolerance);

        if (!accepted)
        {
            SkipCurrent(Loc.T("vnavmesh lehnt Laufweg ab", "vnavmesh rejected the path"));
            return;
        }

        state = State.MovingTo;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
        stuckDetector.Reset();
        flightUpgrade.OnPathStarted(flyingAccepted);
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

    private void UpdateMoving(IReadOnlyList<CollectibleEntry> entries)
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        if (!StillNeeded(entries, currentTargetEntry.Id))
        {
            FinishCurrent();
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

            // Aus einem Flugverbots-Bereich heraus (siehe FlightPathUpgrade) - jetzt fliegend weiter.
            if (flightUpgrade.ShouldReplanFlying(playerPos, currentTargetPosition))
            {
                StopPath();
                BeginPathfind();
                return;
            }

            // Steckengeblieben (z.B. gegen eine Wand) - Pfad neu anfordern statt untätig zu warten.
            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[AetherCurrentAutomation] UpdateMoving({currentTargetEntry.Name}): scheinbar steckengeblieben - Laufweg wird neu angefordert.");
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
            StatusText = Loc.T($"Interagiere: {currentTargetEntry.Name}...", $"Interacting: {currentTargetEntry.Name}...");
            return;
        }

        if (Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
            SkipCurrent(Loc.T("Laufweg nie gestartet", "Movement never started"));
    }

    /// <summary>
    /// Sucht das nächstgelegene ObjectKind.EventObj bei der Zielposition und interagiert damit -
    /// analog zu AetheryteAutomation.FindNearestAetheryteObject/UpdateInteracting, nur mit EventObj
    /// statt der eigenen Aetheryte-Objektart (Ätherströmungen haben keine dedizierte ObjectKind).
    /// </summary>
    private void UpdateInteracting(IReadOnlyList<CollectibleEntry> entries)
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        if (!StillNeeded(entries, currentTargetEntry.Id))
        {
            FinishCurrent();
            return;
        }

        var gameObject = FindNearestEventObj(currentTargetPosition, InteractObjectSearchRadius);
        if (gameObject == null)
        {
            interactObjectNotFoundSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - interactObjectNotFoundSince.Value < InteractObjectGracePeriod)
                return;

            SkipCurrent(Loc.T("Kein interagierbares Objekt in der Nähe gefunden", "No interactable object found nearby"));
            return;
        }

        if (!hasInteractedThisCycle)
        {
            // Interact braucht das Objekt als aktuelles Ziel - das muss erst einen Frame lang
            // angewendet worden sein, bevor der eigentliche Interact-Aufruf greift (siehe
            // AetheryteAutomation.UpdateInteracting).
            if (!Plugin.IsCurrentTarget(gameObject))
            {
                Plugin.SetTarget(gameObject);
                return;
            }

            Plugin.InteractWithGameObject(gameObject);
            hasInteractedThisCycle = true;
            stateEnteredAt = DateTime.UtcNow;
            Plugin.Log.Info($"[AetherCurrentAutomation] UpdateInteracting({currentTargetEntry.Name}): interagiert mit BaseId={gameObject.BaseId} @ {gameObject.Position}, warte auf Freischaltung...");
            return;
        }

        if (Plugin.IsAetherCurrentUnlocked(currentTargetEntry.Id))
        {
            Plugin.Log.Info($"[AetherCurrentAutomation] UpdateInteracting({currentTargetEntry.Name}): freigeschaltet.");
            FinishCurrent();
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > UnlockWaitTimeout)
            SkipCurrent(Loc.T("Freischalten hat nicht geklappt", "unlocking did not go through"));
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearestEventObj(Vector3 nearPosition, float maxDistance)
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? nearest = null;
        var bestDistance = maxDistance;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventObj)
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

    private void FinishCurrent()
    {
        Plugin.Log.Info($"[AetherCurrentAutomation] FinishCurrent({currentTargetEntry?.Name}): freigeschaltet.");
        attemptCounts.Remove(currentTargetEntry!.Id);
        currentTargetEntry = null;
        state = State.Idle;
    }

    private void SkipCurrent(string reason)
    {
        Plugin.Log.Info($"[AetherCurrentAutomation] SkipCurrent({currentTargetEntry?.Name}): {reason}");
        if (currentTargetEntry != null)
        {
            skippedIds.Add(currentTargetEntry.Id);
            StatusText = $"{Loc.T("Übersprungen", "Skipped")} ({reason}): {currentTargetEntry.Name}";
        }

        StopPath();
        currentTargetEntry = null;
        state = State.Idle;
    }
}
