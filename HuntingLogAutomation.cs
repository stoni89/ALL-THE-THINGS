using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace AllTheThings;

/// <summary>
/// Läuft nacheinander alle aktuell noch fehlenden Hunting-Log-Ziele der Zone ab (siehe
/// Plugin.GetHuntingLogEntries - bereits auf den aktiven Rang der aktuellen Klasse gefiltert) und
/// tötet dort die jeweils benötigte Anzahl. Anders als AetheryteAutomation/QuestAutomation bleibt
/// sie IMMER auf die aktuelle Zone beschränkt (kein Bezirkswechsel per Lifestream) - die übergebene
/// Eintragsliste ist ohnehin schon auf genau diese Zone eingegrenzt.
///
/// Für den eigentlichen Kampf wird (vorerst, siehe Klassenkommentar-Aufruf des Nutzers - eine
/// eigene Einstellung für die Kampf-Engine folgt später) das Fremdplugin "RotationSolver Reborn"
/// angesteuert: über dessen Chat-Befehl "/rotation Auto" für den Auto-Kampfmodus (ein Enum-Typ als
/// IPC-Parameter wäre nur mit einer Kompilierzeit-Referenz auf RotationSolver.Basic.dll möglich,
/// die es als NuGet-Paket nicht gibt) und über die IPC-Endpunkte AddPriorityNameID/
/// RemovePriorityNameID (reine uint-Parameter, dafür sicher per IPC aufrufbar), damit RotationSolver
/// gezielt das gefundene Hunting-Log-Monster statt eines zufälligen Nachbarn angreift. IPC-
/// Namensraum "RotationSolverReborn" und die Chat-Befehle "/rotation Auto"/"/rotation Off" sind
/// durch Dekompilieren der installierten RotationSolver.dll verifiziert (EzIPC.Init(this,
/// "RotationSolverReborn") bzw. die per Svc.Commands.AddHandler registrierten Befehlsnamen selbst),
/// nicht nur vermutet.
/// </summary>
public sealed class HuntingLogAutomation
{
    private enum State
    {
        Idle,
        Mounting,
        MovingTo,
        SearchingMonster,
        ApproachingMonster,
        Fighting,
    }

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);
    private const float PathTolerance = 10f;
    private const float SprintDisableDistance = 8f;
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);

    // Umkreis, in dem nach einem lebenden Exemplar des Zielmonsters gesucht wird, sobald die
    // gespeicherte Position erreicht ist - großzügig, da Monster umherlaufen und die von Hand/per
    // Scraper hinterlegte Position nur ein ungefährer, typischer Fundort ist. Auch beim erneuten
    // Suchen nach einem Kill (siehe UpdateFighting) - das nächste Exemplar kann durchaus weiter weg
    // stehen, als es innerhalb der Objekttabellen-Ladeentfernung ohnehin noch sichtbar wäre, daher
    // bewusst großzügig statt eng gewählt.
    private const float MonsterSearchRadius = 150f;
    private static readonly TimeSpan MonsterSearchTimeout = TimeSpan.FromSeconds(45);

    // Ab dieser Entfernung (Yalms) zum Monster wird nicht mehr nachgesteuert, sondern gekämpft -
    // RotationSolver übernimmt ab hier die eigentlichen Angriffe, wir müssen nur noch nah genug dran
    // sein.
    private const float AttackRange = 3.5f;

    // Verhindert endloses Warten, falls RotationSolver aus irgendeinem Grund nicht killt (z.B.
    // Monster zu stark, RotationSolver falsch konfiguriert) - danach wird dieses Ziel übersprungen.
    private static readonly TimeSpan FightTimeout = TimeSpan.FromMinutes(3);

    private const int MaxAttemptsPerTarget = 2;
    private readonly Dictionary<uint, int> attemptCounts = new();
    private readonly HashSet<uint> skippedIds = new();

    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> navmeshIsReady;
    private readonly ICallGateSubscriber<Vector3?> queryFlagToPoint;

    // RotationSolver-IPC (siehe Klassenkommentar) - nur die beiden primitiven uint-Endpunkte, der
    // Kampfmodus selbst läuft bewusst über den Chat-Befehl "/rotation Auto"/"/rotation Off"
    // statt über ChangeOperatingMode(StateCommandType) per IPC, da dessen Enum-Typ ohne eigene
    // Assembly-Referenz auf RotationSolver.Basic.dll nicht typgleich nachgebildet werden kann.
    private readonly ICallGateSubscriber<bool> rsrAutorotationActive;
    private readonly ICallGateSubscriber<uint, object> rsrAddPriorityNameId;
    private readonly ICallGateSubscriber<uint, object> rsrRemovePriorityNameId;

    private State state = State.Idle;
    private CollectibleEntry? currentTargetEntry;
    private uint? currentPriorityNameId;
    private Vector3 currentTargetPosition;
    private DateTime stateEnteredAt;
    private bool hasSeenPathRunning;
    private DateTime lastDiagnosticLogAt = DateTime.MinValue;
    private static readonly TimeSpan DiagnosticLogInterval = TimeSpan.FromSeconds(5);

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

    public HuntingLogAutomation()
    {
        pathfindAndMoveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        navmeshIsReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        queryFlagToPoint = Plugin.PluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");

        rsrAutorotationActive = Plugin.PluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
        rsrAddPriorityNameId = Plugin.PluginInterface.GetIpcSubscriber<uint, object>("RotationSolverReborn.AddPriorityNameID");
        rsrRemovePriorityNameId = Plugin.PluginInterface.GetIpcSubscriber<uint, object>("RotationSolverReborn.RemovePriorityNameID");
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

    // Nur informativ (siehe StatusText an der Aufrufstelle) - blockiert NICHT mehr das Starten der
    // Automation. RotationSolvers AddPriorityNameID/RemovePriorityNameID/AutorotationActive-IPC ist
    // bei mindestens einer Installation trotz aktivem, funktionierendem Plugin (Chat-Befehl
    // "/rotation Auto" hat nachweislich funktioniert) nicht über Dalamuds IPC-Registry auffindbar -
    // ob das an dieser konkreten Installation oder generell liegt, ist ungeklärt. Die Priorität ist
    // ohnehin nur eine Zusatzabsicherung (siehe UpdateApproachingMonster/ClearRotationSolverPriority,
    // beide bereits mit HasAction abgesichert) - ohne sie läuft die Automation trotzdem, nur ohne die
    // Garantie, dass RotationSolver exakt das per SetTarget gewählte Monster statt eines zufälligen
    // Nachbarn angreift.
    public bool IsRotationSolverAvailable()
    {
        try
        {
            return rsrAutorotationActive.HasFunction && rsrAddPriorityNameId.HasFunction;
        }
        catch
        {
            return false;
        }
    }

    // Nur vnavmesh ist eine harte Voraussetzung (ohne das kann gar nicht gelaufen werden) - siehe
    // IsRotationSolverAvailable-Kommentar, warum RotationSolver selbst kein hartes Gate mehr ist.
    public bool IsAvailable() => IsVNavmeshAvailable();

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

    private void ClearRotationSolverPriority()
    {
        if (currentPriorityNameId == null)
            return;

        try
        {
            if (rsrRemovePriorityNameId.HasAction)
                rsrRemovePriorityNameId.InvokeAction(currentPriorityNameId.Value);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Entfernen der RotationSolver-Priorität.");
        }

        currentPriorityNameId = null;
    }

    private void SetRotationSolverAutoMode(bool enabled)
    {
        try
        {
            Plugin.CommandManager.ProcessCommand(enabled ? "/rotation Auto" : "/rotation Off");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Umschalten des RotationSolver-Kampfmodus.");
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
        ClearRotationSolverPriority();
        SetRotationSolverAutoMode(false);
    }

    public void MarkUnavailable()
    {
        StatusText = Loc.T("vnavmesh nicht gefunden - bitte installieren.", "vnavmesh not found - please install it.");
    }

    /// <summary>
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell fehlenden Hunting-Log-
    /// Einträgen DER AKTUELLEN ZONE aufgerufen werden (siehe Plugin.GetHuntingLogEntries - bereits
    /// auf Klasse/aktiven Rang gefiltert).
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> huntingLogEntriesInZone)
    {
        if (!IsActive)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(huntingLogEntriesInZone);
                    break;

                case State.Mounting:
                    UpdateMounting();
                    break;

                case State.MovingTo:
                    UpdateMoving(huntingLogEntriesInZone);
                    break;

                case State.SearchingMonster:
                    UpdateSearchingMonster(huntingLogEntriesInZone);
                    break;

                case State.ApproachingMonster:
                    UpdateApproachingMonster(huntingLogEntriesInZone);
                    break;

                case State.Fighting:
                    UpdateFighting(huntingLogEntriesInZone);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler bei der Hunting-Log-Automation - wird gestoppt.");
            StatusText = Loc.T("Fehler bei vnavmesh/RotationSolver - Automation gestoppt.", "Error talking to vnavmesh/RotationSolver - automation stopped.");
            Stop();
        }
    }

    /// <summary>
    /// Ist dieser Eintrag (per Id, siehe CollectibleEntry.Id = MonsterNoteTarget-RowId) noch in der
    /// aktuellen Liste? Wenn nicht mehr, ist die benötigte Anzahl erreicht (oder der aktive
    /// Rang/die Klasse hat sich geändert) - beides zählt als "fertig damit".
    /// </summary>
    private static bool StillNeeded(IReadOnlyList<CollectibleEntry> entries, uint id) => entries.Any(e => e.Id == id);

    /// <summary>
    /// Liefert den aktuellen (frischen) Eintrag mit seinem live nachgeführten Fortschritt im Namen
    /// (z.B. "Hammer Beak (1/3)") - anders als currentTargetEntry, dessen Name der Stand von beim
    /// Anlaufen ist und sich nach einem Kill nicht mehr von selbst aktualisiert.
    /// </summary>
    private static CollectibleEntry? FindCurrent(IReadOnlyList<CollectibleEntry> entries, uint id) => entries.FirstOrDefault(e => e.Id == id);

    /// <summary>Kurzform von FindCurrent für die StatusText-Anzeige, siehe dortigen Kommentar.</summary>
    private string FreshName(IReadOnlyList<CollectibleEntry> entries) => FindCurrent(entries, currentTargetEntry!.Id)?.Name ?? currentTargetEntry!.Name;

    private void TryStartNext(IReadOnlyList<CollectibleEntry> entries)
    {
        var candidates = entries.Where(e => e.WorldPosition.HasValue && !skippedIds.Contains(e.Id)).ToList();
        if (candidates.Count == 0)
        {
            StatusText = Loc.T(
                "Keine Hunting-Log-Ziele mit bekannter Position mehr in dieser Zone.",
                "No hunting log targets with a known position left in this zone.");
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

        // Genau derselbe Trick wie bei GoToAutomation/AetheryteAutomation: die Karten-Flagge auf
        // die (rohe, per Plugin.OpenEntryMap umgerechnete) Zielposition setzen und vnavmesh nach
        // einem begehbaren Punkt in deren Nähe fragen.
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

        if (mounted)
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

        if (pathIsRunning.InvokeFunc())
        {
            hasSeenPathRunning = true;

            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            if (Vector3.Distance(playerPos, currentTargetPosition) > SprintDisableDistance)
                Plugin.TryUseSprint();

            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
                SkipCurrent(Loc.T("Laufweg dauert zu lange", "Path is taking too long"));

            return;
        }

        if (hasSeenPathRunning)
        {
            state = State.SearchingMonster;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Suche Monster: {FreshName(entries)}...", $"Looking for monster: {FreshName(entries)}...");
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > PathStartGracePeriod)
            SkipCurrent(Loc.T("Laufweg nie gestartet", "Movement never started"));
    }

    private void UpdateSearchingMonster(IReadOnlyList<CollectibleEntry> entries)
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

        // Siehe identischen Kommentar in UpdateApproachingMonster - jeden Frame neu setzen, nicht
        // nur beim Betreten dieses Zustands.
        StatusText = Loc.T($"Suche Monster: {FreshName(entries)}...", $"Looking for monster: {FreshName(entries)}...");

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
        var monster = Plugin.FindNearestLiveMonster(currentTargetEntry.BNpcNameId!.Value, playerPos, MonsterSearchRadius);
        if (monster == null)
        {
            if (DateTime.UtcNow - lastDiagnosticLogAt > DiagnosticLogInterval)
            {
                lastDiagnosticLogAt = DateTime.UtcNow;
                var nearbyHostiles = string.Join(", ", Plugin.ObjectTable
                    .OfType<Dalamud.Game.ClientState.Objects.Types.IBattleNpc>()
                    .Where(b => b.IsTargetable && b.CurrentHp > 0 && Vector3.Distance(b.Position, playerPos) < MonsterSearchRadius)
                    .Select(b => $"{b.Name}(NameId={b.NameId},dist={Vector3.Distance(b.Position, playerPos):F1})"));
                Plugin.Log.Info($"[HuntingLogAutomation] SearchingMonster({currentTargetEntry.Name}): gesucht BNpcNameId={currentTargetEntry.BNpcNameId}, " +
                                 $"nichts gefunden. Lebende Ziele in {MonsterSearchRadius}y: [{nearbyHostiles}]");
            }

            if (DateTime.UtcNow - stateEnteredAt > MonsterSearchTimeout)
                SkipCurrent(Loc.T("Monster nicht gefunden (evtl. gerade nicht gespawnt)", "Monster not found (maybe not spawned right now)"));

            return;
        }

        Plugin.Log.Info($"[HuntingLogAutomation] SearchingMonster({currentTargetEntry.Name}): gefunden {monster.Name} @ {monster.Position}, NameId={monster.NameId}.");
        Plugin.SetTarget(monster);
        var accepted = pathfindAndMoveCloseTo.InvokeFunc(monster.Position, false, AttackRange);
        if (!accepted)
        {
            SkipCurrent(Loc.T("vnavmesh lehnt Anlauf zum Monster ab", "vnavmesh rejected the approach to the monster"));
            return;
        }

        state = State.ApproachingMonster;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
        StatusText = Loc.T($"Nähere mich: {FreshName(entries)}...", $"Approaching: {FreshName(entries)}...");
    }

    private void UpdateApproachingMonster(IReadOnlyList<CollectibleEntry> entries)
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

        // Jeden Frame neu setzen (nicht nur beim Betreten dieses Zustands) - der Kill-Zähler im
        // Hunting Log kann minimal (ein paar Frames) NACH dem eigentlichen Ableben/Verschwinden des
        // vorigen Monsters aktualisiert werden. Würde der Text nur einmal beim Übergang in diesen
        // Zustand gesetzt, stünde er bis zum nächsten Zustandswechsel mit dem alten (zu niedrigen)
        // Stand da - genau das hat "Approaching X (0/3)" statt "(1/3)" angezeigt.
        StatusText = Loc.T($"Nähere mich: {FreshName(entries)}...", $"Approaching: {FreshName(entries)}...");

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
        var monster = Plugin.FindNearestLiveMonster(currentTargetEntry.BNpcNameId!.Value, playerPos, MonsterSearchRadius);
        if (monster == null)
        {
            // Auf dem Weg gestorben/verschwunden (z.B. von jemand anderem getötet) - erneut suchen.
            state = State.SearchingMonster;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        var distance = Vector3.Distance(playerPos, monster.Position);
        if (distance <= AttackRange)
        {
            StopPath();
            Plugin.SetTarget(monster);
            currentPriorityNameId = currentTargetEntry.BNpcNameId;
            var rsrPriorityOk = false;
            try
            {
                if (rsrAddPriorityNameId.HasAction)
                {
                    rsrAddPriorityNameId.InvokeAction(currentTargetEntry.BNpcNameId!.Value);
                    rsrPriorityOk = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Fehler beim Setzen der RotationSolver-Priorität.");
            }

            SetRotationSolverAutoMode(true);
            Plugin.Log.Info($"[HuntingLogAutomation] ApproachingMonster({currentTargetEntry.Name}): in Reichweite (distance={distance:F1}), " +
                             $"Ziel gesetzt={Plugin.IsCurrentTarget(monster)}, RotationSolver-Priorität gesetzt={rsrPriorityOk}, '/rotation Auto' gesendet.");

            state = State.Fighting;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Kämpfe: {FreshName(entries)}...", $"Fighting: {FreshName(entries)}...");
            return;
        }

        if (DateTime.UtcNow - lastDiagnosticLogAt > DiagnosticLogInterval)
        {
            lastDiagnosticLogAt = DateTime.UtcNow;
            Plugin.Log.Info($"[HuntingLogAutomation] ApproachingMonster({currentTargetEntry.Name}): distance={distance:F1} (Ziel <= {AttackRange}), " +
                             $"pathIsRunning={pathIsRunning.InvokeFunc()}, hasSeenPathRunning={hasSeenPathRunning}.");
        }

        if (pathIsRunning.InvokeFunc())
        {
            hasSeenPathRunning = true;
            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
                SkipCurrent(Loc.T("Anlauf zum Monster dauert zu lange", "Approaching the monster is taking too long"));

            return;
        }

        // Erst NACHDEM der Laufauftrag mindestens einmal sichtbar aktiv war (oder eine großzügige
        // Anlaufzeit verstrichen ist) als "fertig/feststeckend" werten und neu anfragen - direkt
        // nach dem allerersten Auftrag (siehe SearchingMonster) ist PathIsRunning oft noch einen
        // Frame lang false, bevor vnavmesh die Berechnung überhaupt sichtbar startet. Ohne diese
        // Gnadenfrist wurde hier fälschlich sofort ein zweiter Auftrag losgeschickt, den vnavmesh
        // ablehnt, weil der erste (mittlerweile doch angelaufene) Weg noch läuft - siehe
        // AetheryteAutomation.PathStartGracePeriod (identisches Problem/dieselbe Lösung).
        if (hasSeenPathRunning || DateTime.UtcNow - stateEnteredAt > PathStartGracePeriod)
        {
            // Monster hat sich bewegt (Ziel erreicht laut vnavmesh, aber laut eigener
            // Distanzmessung noch zu weit weg) oder der erste Auftrag ist nie sichtbar gestartet -
            // in beiden Fällen zur aktuellen Monsterposition neu anfragen.
            var accepted = pathfindAndMoveCloseTo.InvokeFunc(monster.Position, false, AttackRange);
            if (!accepted)
            {
                SkipCurrent(Loc.T("vnavmesh lehnt Anlauf zum Monster ab", "vnavmesh rejected the approach to the monster"));
                return;
            }

            hasSeenPathRunning = false;
            stateEnteredAt = DateTime.UtcNow;
        }
    }

    private void UpdateFighting(IReadOnlyList<CollectibleEntry> entries)
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        if (!StillNeeded(entries, currentTargetEntry.Id))
        {
            // Benötigte Anzahl erreicht.
            FinishCurrent();
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > FightTimeout)
        {
            SkipCurrent(Loc.T("Kampf dauert zu lange", "Fight is taking too long"));
            return;
        }

        // Fortschritt (z.B. "1/3") kommt aus der FRISCHEN Liste, nicht aus dem beim Anlaufen
        // zwischengespeicherten currentTargetEntry - dessen Name bliebe sonst nach einem Kill
        // stehen, obwohl der Zähler im Hunting Log längst hochgezählt hat.
        var freshName = FindCurrent(entries, currentTargetEntry.Id)?.Name ?? currentTargetEntry.Name;
        StatusText = Loc.T($"Kämpfe: {freshName}...", $"Fighting: {freshName}...");

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
        var monster = Plugin.FindNearestLiveMonster(currentTargetEntry.BNpcNameId!.Value, playerPos, MonsterSearchRadius);
        if (monster == null)
        {
            Plugin.Log.Info($"[HuntingLogAutomation] Fighting({currentTargetEntry.Name}): kein lebendes Exemplar mehr in der Nähe, aber laut Hunting Log noch nicht fertig - suche nächstes.");
            // Aktuelles Exemplar tot, aber noch mehr Kills nötig (StillNeeded oben wäre sonst schon
            // false gewesen) - nächstes Exemplar suchen.
            state = State.SearchingMonster;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Suche nächstes Monster: {freshName}...", $"Looking for the next monster: {freshName}...");
            return;
        }

        // Das gerade bekämpfte Exemplar ist tot (fällt aus dem 50y-Umkreis der Leiche irgendwann
        // heraus oder wird nicht mehr als lebend gefunden), aber ein ANDERES, weiter entferntes
        // Exemplar der Art existiert schon - ohne diese Distanzprüfung würde hier für immer
        // untätig neben der Leiche gewartet, statt zum neuen Exemplar hinzulaufen.
        var distanceToMonster = Vector3.Distance(playerPos, monster.Position);
        if (distanceToMonster > AttackRange)
        {
            Plugin.Log.Info($"[HuntingLogAutomation] Fighting({currentTargetEntry.Name}): aktuelles Exemplar außer Reichweite (distance={distanceToMonster:F1}) - laufe zum nächsten.");
            state = State.ApproachingMonster;
            stateEnteredAt = DateTime.UtcNow;
            hasSeenPathRunning = false;
            StatusText = Loc.T($"Nähere mich: {freshName}...", $"Approaching: {freshName}...");
            return;
        }

        if (DateTime.UtcNow - lastDiagnosticLogAt > DiagnosticLogInterval)
        {
            lastDiagnosticLogAt = DateTime.UtcNow;
            var rsrActive = false;
            try { rsrActive = rsrAutorotationActive.HasFunction && rsrAutorotationActive.InvokeFunc(); } catch { /* siehe IsRotationSolverAvailable-Kommentar */ }
            Plugin.Log.Info($"[HuntingLogAutomation] Fighting({currentTargetEntry.Name}): Ziel={monster.Name}, HP={monster.CurrentHp}/{monster.MaxHp}, " +
                             $"aktuelles Spielziel={Plugin.IsCurrentTarget(monster)}, RotationSolver aktiv laut IPC={rsrActive}.");
        }
    }

    /// <summary>
    /// Aktuelles Ziel erfolgreich erledigt (die benötigte Anzahl ist laut Hunting Log erreicht) -
    /// RotationSolver-Priorität/Auto-Modus wieder zurücknehmen und mit dem nächsten Ziel weitermachen.
    /// </summary>
    private void FinishCurrent()
    {
        Plugin.Log.Info($"[HuntingLogAutomation] FinishCurrent({currentTargetEntry?.Name}): laut Hunting Log erledigt.");
        ClearRotationSolverPriority();
        SetRotationSolverAutoMode(false);
        attemptCounts.Remove(currentTargetEntry!.Id);
        currentTargetEntry = null;
        state = State.Idle;
    }

    private void SkipCurrent(string reason)
    {
        Plugin.Log.Info($"[HuntingLogAutomation] SkipCurrent({currentTargetEntry?.Name}): {reason}");
        if (currentTargetEntry != null)
        {
            skippedIds.Add(currentTargetEntry.Id);
            StatusText = $"{Loc.T("Übersprungen", "Skipped")} ({reason}): {currentTargetEntry.Name}";
        }

        StopPath();
        ClearRotationSolverPriority();
        SetRotationSolverAutoMode(false);
        currentTargetEntry = null;
        state = State.Idle;
    }
}
