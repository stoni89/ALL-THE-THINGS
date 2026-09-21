using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

/// <summary>
/// Läuft nacheinander alle aktuell noch fehlenden Sightseeing-Log-Einträge ("Adventure" im Lumina-
/// Sheet) der Zone ab. Anders als Ätherströmungen haben diese eine echte Weltposition direkt im
/// Sheet (siehe Plugin.ComputeLiveZoneEntries), kein Community-Export nötig. Manche Aussichtspunkte
/// schalten erst durch einen bestimmten Emote am Zielort frei (CollectibleEntry.RequiredEmoteCommand,
/// z.B. "/sit"), die meisten wohl schon durch reine Nähe - siehe UpdateWaitingForUnlock.
/// </summary>
public sealed class SightseeingAutomation
{
    private enum State
    {
        Idle,
        Mounting,
        MovingTo,
        WaitingForUnlock,
    }

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);
    private const float ArrivalTolerance = 4f;
    private const float SprintDisableDistance = 8f;
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);

    // Wie lange nach Ankunft (und ggf. dem nötigen Emote) auf die Freischaltung gewartet wird.
    private static readonly TimeSpan UnlockWaitTimeout = TimeSpan.FromSeconds(15);

    // Kurze Pause zwischen Ankunft und Emote-Ausführung - unmittelbar nach dem Stillstehen
    // angewendet, spielt der Emote manchmal nicht zuverlässig an (Bewegungs-Cancel).
    private static readonly TimeSpan PreEmoteDelay = TimeSpan.FromSeconds(1);

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
    private bool hasSentEmote;

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

    public SightseeingAutomation()
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
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell fehlenden Sightseeing-
    /// Einträgen DER AKTUELLEN ZONE aufgerufen werden.
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> sightseeingInZone)
    {
        if (!IsActive)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(sightseeingInZone);
                    break;

                case State.Mounting:
                    UpdateMounting();
                    break;

                case State.MovingTo:
                    UpdateMoving(sightseeingInZone);
                    break;

                case State.WaitingForUnlock:
                    UpdateWaitingForUnlock(sightseeingInZone);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler bei der Sightseeing-Automation - wird gestoppt.");
            StatusText = Loc.T("Fehler bei vnavmesh - Automation gestoppt.", "Error talking to vnavmesh - automation stopped.");
            Stop();
        }
    }

    private static bool StillNeeded(IReadOnlyList<CollectibleEntry> entries, uint id) => entries.Any(e => e.Id == id);

    private void TryStartNext(IReadOnlyList<CollectibleEntry> entries)
    {
        var candidates = entries.Where(e => e.WorldPosition.HasValue && !skippedIds.Contains(e.Id)).ToList();
        if (candidates.Count == 0)
        {
            StatusText = Loc.T(
                "Keine Sightseeing-Punkte mit bekannter Position mehr in dieser Zone.",
                "No sightseeing points with a known position left in this zone.");
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

        // Genau derselbe Trick wie bei GoToAutomation/HuntingLogAutomation: die Karten-Flagge auf
        // die Zielposition setzen und vnavmesh nach einem begehbaren Punkt in deren Nähe fragen -
        // Aussichtspunkte liegen oft an Klippenkanten/erhöhten Stellen, eine reine Koordinatensuche
        // (PointOnFloor) fände dort häufig gar keinen begehbaren Punkt.
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
        hasSentEmote = false;

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
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, true, ArrivalTolerance);

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

            // Steckengeblieben (z.B. gegen eine Wand) - Pfad neu anfordern statt untätig zu warten.
            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[SightseeingAutomation] UpdateMoving({currentTargetEntry.Name}): scheinbar steckengeblieben - Laufweg wird neu angefordert.");
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
            state = State.WaitingForUnlock;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Warte auf Freischaltung: {currentTargetEntry.Name}...", $"Waiting to unlock: {currentTargetEntry.Name}...");
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > PathStartGracePeriod)
            SkipCurrent(Loc.T("Laufweg nie gestartet", "Movement never started"));
    }

    /// <summary>
    /// Nach Ankunft: falls dieser Punkt einen bestimmten Emote braucht (RequiredEmoteCommand),
    /// diesen einmal ausführen, dann in jedem Fall auf IsAdventureComplete warten - die meisten
    /// Punkte scheinen bereits durch reine Nähe freizuschalten.
    /// </summary>
    private void UpdateWaitingForUnlock(IReadOnlyList<CollectibleEntry> entries)
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

        if (!hasSentEmote)
        {
            if (string.IsNullOrEmpty(currentTargetEntry.RequiredEmoteCommand))
            {
                hasSentEmote = true;
            }
            else if (DateTime.UtcNow - stateEnteredAt > PreEmoteDelay)
            {
                try
                {
                    // Echter Spiel-Emote-Befehl (z.B. "/sit"), kein von einem Plugin registrierter
                    // Befehl - siehe Plugin.SendGameChatCommand, ICommandManager.ProcessCommand würde
                    // hier kommentarlos nichts bewirken.
                    Plugin.SendGameChatCommand(currentTargetEntry.RequiredEmoteCommand);
                    Plugin.Log.Info($"[SightseeingAutomation] UpdateWaitingForUnlock({currentTargetEntry.Name}): Emote '{currentTargetEntry.RequiredEmoteCommand}' ausgeführt.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Fehler beim Ausführen des Sightseeing-Emotes.");
                }

                hasSentEmote = true;
                stateEnteredAt = DateTime.UtcNow;
            }

            return;
        }

        if (Plugin.IsAdventureComplete(currentTargetEntry.Id))
        {
            Plugin.Log.Info($"[SightseeingAutomation] UpdateWaitingForUnlock({currentTargetEntry.Name}): freigeschaltet.");
            FinishCurrent();
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > UnlockWaitTimeout)
            SkipCurrent(Loc.T("Nicht automatisch freigeschaltet (evtl. falscher Emote oder zu weit weg)", "Not unlocked automatically (maybe the wrong emote or too far away)"));
    }

    private void FinishCurrent()
    {
        Plugin.Log.Info($"[SightseeingAutomation] FinishCurrent({currentTargetEntry?.Name}): freigeschaltet.");
        attemptCounts.Remove(currentTargetEntry!.Id);
        currentTargetEntry = null;
        state = State.Idle;
    }

    private void SkipCurrent(string reason)
    {
        Plugin.Log.Info($"[SightseeingAutomation] SkipCurrent({currentTargetEntry?.Name}): {reason}");
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
