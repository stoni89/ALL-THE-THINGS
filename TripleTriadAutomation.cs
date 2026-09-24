using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

/// <summary>
/// Zugriff auf Saucys Einstellung "Open window when challenging an NPC" (Saucy.Configuration.
/// OpenAutomatically) - Saucy hat keine IPC dafür, daher per Reflection auf dessen statische
/// Konfiguration Saucy.Saucy.C (per Saucy-Quelltext Saucy/Core/Plugin/Saucy.cs verifiziert). Nur im
/// Speicher des laufenden Saucy geändert, die Konfigurationsdatei wird nicht angefasst. Jeder
/// Fehler (Saucy nicht geladen, anderer Aufbau nach einem Update) führt still zu null/false.
/// </summary>
internal static class SaucyConfigBridge
{
    private static System.Reflection.PropertyInfo? FindOpenAutomaticallyProperty(out object? config)
    {
        config = null;
        try
        {
            // Dalamud lädt jedes Plugin in einen eigenen AssemblyLoadContext - über die AppDomain sind
            // trotzdem alle geladenen Assemblies sichtbar. Bei mehreren (z.B. nach Neuladen) die mit
            // gesetzter Konfiguration nehmen.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Reverse())
            {
                if (assembly.GetName().Name != "Saucy")
                    continue;

                var configProperty = assembly.GetType("Saucy.Saucy")?.GetProperty("C",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var value = configProperty?.GetValue(null);
                var openProperty = value?.GetType().GetProperty("OpenAutomatically");
                if (value == null || openProperty == null || openProperty.PropertyType != typeof(bool))
                    continue;

                config = value;
                return openProperty;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[TripleTriadAutomation] Saucy-Konfiguration nicht erreichbar.");
        }

        return null;
    }

    public static bool? TryGetOpenAutomatically()
    {
        var property = FindOpenAutomaticallyProperty(out var config);
        try
        {
            return property != null ? (bool?)property.GetValue(config) : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TrySetOpenAutomatically(bool value)
    {
        var property = FindOpenAutomaticallyProperty(out var config);
        if (property == null || !property.CanWrite)
            return false;

        try
        {
            property.SetValue(config, value);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[TripleTriadAutomation] Saucy-Option konnte nicht gesetzt werden.");
            return false;
        }
    }
}

/// <summary>
/// Läuft nacheinander alle Triple-Triad-NPC-Gegner der aktuellen Zone ab, bei denen noch eine Karte
/// zu holen ist (siehe Plugin.GetTripleTriadNpcEntries - nur Einträge ohne "Bedingung nicht
/// erfüllt", nicht auf der Blacklist, noch nicht gelernt), und lässt dort das Fremdplugin "Saucy"
/// so lange spielen, bis ALLE Karten dieses Gegners mindestens einmal gedroppt sind. Danach werden
/// die gewonnenen Karten aus dem Inventar benutzt (gelernt) und es geht zum nächsten Gegner.
///
/// Saucy bietet keine IPC - angesteuert wird es über seine Chat-Befehle (per Dekompilieren/Quelltext
/// Saucy/Core/Plugin/Saucy.cs verifiziert): "/saucy tt cards all" (Modus "spielen, bis alle NPC-
/// Karten einmal gedroppt sind"), "/saucy tt go" (Automation starten - Gegner ist das aktuelle
/// Spielziel), "/saucy tt stop". Saucy übernimmt dann Anmeldung, Deckwahl, Partien und Revanchen
/// selbst. "Fertig" wird hier selbst erkannt: alle Karten des Gegners gelernt oder im Inventar UND
/// kein Triple-Triad-Fenster mehr offen.
///
/// Laufen/Fliegen nach demselben Muster wie ChocobokeepAutomation (Mount rufen, Karten-Flagge +
/// vnavmesh.Query.Mesh.FlagToPoint, Feinanflug zum echten NPC-Objekt, erst dann absteigen).
/// </summary>
public sealed class TripleTriadAutomation
{
    private enum State
    {
        Idle,
        Mounting,
        MovingTo,
        Approaching,
        StartingMatch,
        Playing,
        UsingCards,
    }

    private const string SaucyInternalName = "Saucy";

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MountRetryInterval = TimeSpan.FromSeconds(2);
    private const float PathTolerance = 10f;
    private const float InteractDistance = 3.5f;
    private const float NpcSearchRadius = 30f;
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NpcNotFoundGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DismountSettleDelay = TimeSpan.FromSeconds(1);

    // Nach dem Ansprechen: so lange auf das erste Triple-Triad-Fenster warten, bevor erneut angesprochen wird.
    private static readonly TimeSpan MatchStartTimeout = TimeSpan.FromSeconds(20);
    private const int MaxMatchStartAttempts = 3;

    // Obergrenze für das Farmen EINES Gegners (Saucy spielt, bis alle Karten gedroppt sind - bei
    // seltenen Drops kann das dauern, aber nicht endlos).
    private static readonly TimeSpan MaxPlayDuration = TimeSpan.FromMinutes(45);

    // Alle Karten da, aber erst als "fertig" werten, wenn so lange kein Triple-Triad-Fenster mehr offen war
    // (Saucy schließt das Ergebnisfenster/lehnt die Revanche ab).
    private static readonly TimeSpan FinishedSettleDuration = TimeSpan.FromSeconds(3);

    // Karten-Items nacheinander benutzen (jedes Benutzen hat eine kurze Animation).
    private static readonly TimeSpan UseCardInterval = TimeSpan.FromSeconds(2);

    // Gegner, die vnavmesh nicht selbst erreicht (z.B. in Gebäuden): fester Laufweg. Der erste Punkt
    // wird normal (auch fliegend) angesteuert, ab dort wird gelaufen - zuletzt zum NPC. Zurück geht es
    // denselben Weg rückwärts, aber nur, wenn danach noch ein Gegner in der Zone offen ist.
    private static readonly Dictionary<string, Vector3[]> NpcEntryRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Trachtoum"] = new[]
        {
            new Vector3(704.27924f, 65.783325f, -287.72842f),
            new Vector3(712.80835f, 66.027f, -279.32993f),
        },
    };

    private const float RouteTolerance = 0.5f;

    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> navmeshIsReady;
    private readonly ICallGateSubscriber<Vector3?> queryFlagToPoint;

    private State state = State.Idle;
    private uint currentNpcId;
    private string currentNpcName = string.Empty;
    private List<CollectibleEntry> currentCards = new();
    private Vector3 currentTargetPosition;
    private float currentPathTolerance = PathTolerance;
    private float pendingPathTolerance = PathTolerance;
    private bool currentAllowFly = true;

    // Fester Laufweg (siehe NpcEntryRoutes): noch anzusteuernde Punkte, ob es der Rückweg ist, ob der
    // aktuelle Gegner einen Laufweg hat bzw. wir schon drinnen sind, und der Rückweg für den nächsten Start.
    private readonly Queue<Vector3> routeQueue = new();
    private bool routeIsExit;
    private Vector3[]? activeRoute;
    private bool routeEntered;
    private Vector3[]? pendingExitRoute;
    private DateTime stateEnteredAt;
    private bool hasSeenPathRunning;
    private DateTime? npcNotFoundSince;
    private DateTime? dismountedAt;
    private DateTime? allCardsObtainedSince;
    private DateTime lastMountAttempt = DateTime.MinValue;
    private DateTime lastRemountAttempt = DateTime.MinValue;
    private DateTime lastCardUseAt = DateTime.MinValue;
    private DateTime playStartedAt;
    private int matchStartAttempts;
    private DateTime? questAcceptedAt;
    private DateTime? questSettledSince;
    private DateTime lastQuestAcceptClick = DateTime.MinValue;
    private bool hasIntentionallyDismounted;
    private readonly HashSet<uint> finishedNpcIds = new();
    private readonly HashSet<uint> skippedNpcIds = new();
    private readonly NavigationStuckDetector stuckDetector = new();
    private readonly FlightPathUpgrade flightUpgrade = new(); // siehe Plugin.FlightPathUpgrade (Flugverbots-Bereiche)

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

    public TripleTriadAutomation()
    {
        pathfindAndMoveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        navmeshIsReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        queryFlagToPoint = Plugin.PluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
    }

    public static bool IsSaucyAvailable() =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == SaucyInternalName && p.IsLoaded);

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

    // Ob Saucys "Open window when challenging an NPC" vor dem Start an war (siehe Start/Stop) - dann
    // am Ende wieder einschalten.
    private bool restoreSaucyAutoOpen;

    public void Start()
    {
        // Saucys Fenster soll während der Automation nicht bei jeder Herausforderung aufgehen.
        if (SaucyConfigBridge.TryGetOpenAutomatically() == true && SaucyConfigBridge.TrySetOpenAutomatically(false))
        {
            restoreSaucyAutoOpen = true;
            Plugin.Log.Info("[TripleTriadAutomation] Saucy-Option \"Open window when challenging an NPC\" für die Automation deaktiviert.");
        }

        IsActive = true;
        state = State.Idle;
        finishedNpcIds.Clear();
        skippedNpcIds.Clear();
        currentNpcId = 0;
        ResetRoute();
        pendingExitRoute = null;
        StatusText = Loc.T("Automation gestartet...", "Automation started...");
    }

    public void Stop()
    {
        var wasPlaying = state is State.StartingMatch or State.Playing;
        IsActive = false;
        state = State.Idle;
        currentNpcId = 0;
        ResetRoute();
        pendingExitRoute = null;
        StopPath();
        if (wasPlaying)
            SendCommand("/saucy tt stop");
        Plugin.ClearNavigationTarget();

        if (restoreSaucyAutoOpen)
        {
            restoreSaucyAutoOpen = false;
            if (SaucyConfigBridge.TrySetOpenAutomatically(true))
                Plugin.Log.Info("[TripleTriadAutomation] Saucy-Option \"Open window when challenging an NPC\" wieder aktiviert.");
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

    private static void SendCommand(string command)
    {
        try
        {
            Plugin.CommandManager.ProcessCommand(command);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Fehler beim Senden von '{command}'.");
        }
    }

    /// <summary>
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell noch fehlenden, erreichbaren
    /// Triple-Triad-NPC-Karten DER AKTUELLEN ZONE aufgerufen werden (siehe CompactOverlayWindow).
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> missingNpcCardsInZone)
    {
        if (!IsActive)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(missingNpcCardsInZone);
                    break;
                case State.Mounting:
                    UpdateMounting();
                    break;
                case State.MovingTo:
                    UpdateMoving();
                    break;
                case State.Approaching:
                    UpdateApproaching();
                    break;
                case State.StartingMatch:
                    UpdateStartingMatch();
                    break;
                case State.Playing:
                    UpdatePlaying();
                    break;
                case State.UsingCards:
                    UpdateUsingCards();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler bei der Triple-Triad-Automation - wird gestoppt.");
            StatusText = Loc.T("Fehler bei vnavmesh/Saucy - Automation gestoppt.", "Error talking to vnavmesh/Saucy - automation stopped.");
            Stop();
        }
    }

    // Eine Karte gilt als "geholt", sobald sie gelernt ist ODER als Item im Inventar liegt (Saucy
    // zählt einen Drop - das Lernen passiert hier erst am Ende, siehe UpdateUsingCards).
    private bool IsCardObtained(CollectibleEntry card) =>
        Plugin.Instance.IsOwned(card) || Plugin.Instance.GetCurrencyAmount(Plugin.GetUnlockItemId(card)) > 0;

    private void TryStartNext(IReadOnlyList<CollectibleEntry> missingNpcCardsInZone)
    {
        var currentTerritory = Plugin.ClientState.TerritoryType;
        var byNpc = missingNpcCardsInZone
            .Where(e => e.EventNpcId != 0 && e.WorldPosition.HasValue && e.TerritoryTypeId == currentTerritory)
            .Where(e => !finishedNpcIds.Contains(e.EventNpcId) && !skippedNpcIds.Contains(e.EventNpcId))
            .GroupBy(e => e.EventNpcId)
            // Karten, die schon im Inventar liegen (nur noch nicht gelernt), brauchen keinen Kampf mehr.
            .Where(g => g.Any(card => !IsCardObtained(card)))
            .ToList();

        if (byNpc.Count == 0)
        {
            StatusText = Loc.T("Keine Triple-Triad-Gegner mit fehlenden Karten mehr in dieser Zone.", "No Triple Triad opponents with missing cards left in this zone.");
            Stop();
            return;
        }

        if (!navmeshIsReady.InvokeFunc())
        {
            StatusText = Loc.T("Warte auf vnavmesh-Navmesh für diese Zone...", "Waiting for vnavmesh's navmesh for this zone...");
            return;
        }

        // Vom letzten Gegner mit festem Laufweg erst wieder hinaus, bevor der nächste angesteuert wird.
        if (pendingExitRoute != null)
        {
            var exitRoute = pendingExitRoute;
            pendingExitRoute = null;
            ResetRoute();
            routeIsExit = true;
            foreach (var point in exitRoute)
                routeQueue.Enqueue(point);

            Plugin.Log.Info("[TripleTriadAutomation] Laufe festen Laufweg zurück.");
            BeginRouteLeg();
            return;
        }

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var next = byNpc.OrderBy(g => Vector3.Distance(playerPos, g.First().WorldPosition!.Value)).First();

        currentNpcId = next.Key;
        currentCards = next.ToList();
        currentNpcName = currentCards[0].Vendor;
        currentTargetPosition = currentCards[0].WorldPosition!.Value;
        hasIntentionallyDismounted = false;
        dismountedAt = null;
        npcNotFoundSince = null;
        matchStartAttempts = 0;
        allCardsObtainedSince = null;

        Plugin.Log.Info($"[TripleTriadAutomation] Nächster Gegner: {currentNpcName} (#{currentNpcId}), Karten: {string.Join(", ", currentCards.Select(c => c.Name))}.");

        // Karten-Flagge auf den Gegner setzen und vnavmesh nach einem begehbaren Punkt fragen (wie
        // ChocobokeepAutomation) - schlägt das fehl, direkt die rohe NPC-Position nehmen.
        Plugin.OpenEntryMap(currentCards[0], showMapWindow: false);
        currentTargetPosition = queryFlagToPoint.InvokeFunc() ?? currentTargetPosition;
        pendingPathTolerance = PathTolerance;

        ResetRoute();
        if (NpcEntryRoutes.TryGetValue(currentNpcName, out var route) && route.Length > 0)
        {
            // Erster Routenpunkt normal (auch fliegend) anfliegen, der Rest wird gelaufen.
            activeRoute = route;
            currentTargetPosition = route[0];
            pendingPathTolerance = RouteTolerance;
            foreach (var point in route.Skip(1))
                routeQueue.Enqueue(point);

            Plugin.Log.Info($"[TripleTriadAutomation] {currentNpcName}: fester Laufweg mit {route.Length} Punkten.");
        }

        lastMountAttempt = DateTime.UtcNow;
        if (Plugin.TryRequestAetheryteMount())
        {
            state = State.Mounting;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Rufe Mount, dann: {currentNpcName}...", $"Summoning mount, then: {currentNpcName}...");
            return;
        }

        BeginPathfind(currentTargetPosition, pendingPathTolerance);
    }

    private void UpdateMounting()
    {
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            BeginPathfind(currentTargetPosition, pendingPathTolerance);
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > MountWaitTimeout)
        {
            BeginPathfind(currentTargetPosition, pendingPathTolerance);
            return;
        }

        // Direkt nach einer Partie/einem Karten-Benutzen lehnt das Spiel den ersten "/mount" oft ab.
        if (DateTime.UtcNow - lastMountAttempt > MountRetryInterval && !Plugin.IsAnimationLocked())
        {
            lastMountAttempt = DateTime.UtcNow;
            Plugin.TryRequestAetheryteMount();
        }
    }

    private void BeginPathfind(Vector3 destination, float tolerance, bool allowFly = true)
    {
        var accepted = false;
        var flyingAccepted = false;
        if (allowFly && Plugin.Condition[ConditionFlag.Mounted] && Plugin.CanFly)
            accepted = flyingAccepted = pathfindAndMoveCloseTo.InvokeFunc(destination, true, tolerance);

        if (!accepted)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(destination, false, tolerance);

        if (!accepted)
        {
            SkipCurrent(Loc.T("vnavmesh lehnt Laufweg ab", "vnavmesh rejected the path"));
            return;
        }

        currentPathTolerance = tolerance;
        currentAllowFly = allowFly;
        state = State.MovingTo;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
        stuckDetector.Reset();
        flightUpgrade.OnPathStarted(flyingAccepted);
        StatusText = Loc.T($"Unterwegs zu: {currentNpcName}...", $"Traveling to: {currentNpcName}...");
    }

    private void UpdateMoving()
    {
        if (pathIsRunning.InvokeFunc())
        {
            hasSeenPathRunning = true;
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;

            if (!hasIntentionallyDismounted)
                Plugin.TryRemountAfterForcedDismount(ref lastRemountAttempt);

            // Aus einem Flugverbots-Bereich heraus (siehe FlightPathUpgrade) - jetzt fliegend weiter.
            if (currentAllowFly && flightUpgrade.ShouldReplanFlying(playerPos, currentTargetPosition))
            {
                StopPath();
                BeginPathfind(currentTargetPosition, currentPathTolerance);
                return;
            }

            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[TripleTriadAutomation] Unterwegs zu {currentNpcName}: scheinbar steckengeblieben - Laufweg wird neu angefordert.");
                StopPath();
                BeginPathfind(currentTargetPosition, currentPathTolerance, currentAllowFly);
                return;
            }

            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
                SkipCurrent(Loc.T("Laufweg dauert zu lange", "Path is taking too long"));

            return;
        }

        if (hasSeenPathRunning)
        {
            // Fester Laufweg: nächsten Punkt zu Fuß ansteuern bzw. Rückweg beenden.
            if (routeQueue.Count > 0)
            {
                // Nach dem Anflug auf den ersten Punkt erst landen - zu Fuß geht es nur am Boden weiter.
                if (Plugin.Condition[ConditionFlag.InFlight])
                {
                    if (DateTime.UtcNow - lastRemountAttempt > MountRetryInterval)
                    {
                        lastRemountAttempt = DateTime.UtcNow;
                        Plugin.TryDismount();
                    }
                    return;
                }

                BeginRouteLeg();
                return;
            }

            if (routeIsExit)
            {
                Plugin.Log.Info("[TripleTriadAutomation] Rückweg abgeschlossen.");
                ResetRoute();
                state = State.Idle;
                return;
            }

            if (activeRoute != null)
                routeEntered = true;

            state = State.Approaching;
            stateEnteredAt = DateTime.UtcNow;
            npcNotFoundSince = null;
            return;
        }

        if (Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
            SkipCurrent(Loc.T("Laufweg nie gestartet", "Movement never started"));
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNpcObject()
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? nearest = null;
        var bestDistance = NpcSearchRadius;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.BaseId != currentNpcId)
                continue;

            var distance = Vector3.Distance(obj.Position, currentCards[0].WorldPosition!.Value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = obj;
            }
        }

        return nearest;
    }

    private void UpdateApproaching()
    {
        var npc = FindNpcObject();
        if (npc == null)
        {
            npcNotFoundSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - npcNotFoundSince.Value > NpcNotFoundGracePeriod)
                SkipCurrent(Loc.T("NPC trotz Ankunft nicht gefunden", "NPC not found despite arriving"));
            return;
        }

        // Feinanflug zum echten NPC-Objekt (die Karten-Flagge ist nur grob) - beritten/fliegend,
        // damit Mauern/Gebäude übersprungen statt zu Fuß umlaufen werden.
        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? npc.Position;
        if (Vector3.Distance(playerPos, npc.Position) > InteractDistance)
        {
            currentTargetPosition = npc.Position;
            BeginPathfind(npc.Position, InteractDistance - 0.5f, allowFly: activeRoute == null);
            return;
        }

        // Nah genug - erst jetzt absteigen (beritten lässt sich nicht spielen).
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            Plugin.TryDismount();
            hasIntentionallyDismounted = true;
            dismountedAt = null;
            return;
        }

        dismountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - dismountedAt.Value < DismountSettleDelay)
            return;

        if (!Plugin.IsCurrentTarget(npc))
        {
            Plugin.SetTarget(npc);
            return;
        }

        // Saucy im Modus "bis alle Karten gedroppt sind" starten - Gegner ist das gerade gesetzte Ziel.
        Plugin.Log.Info($"[TripleTriadAutomation] Starte Saucy gegen {currentNpcName}.");
        SendCommand("/saucy tt cards all");
        SendCommand("/saucy tt go");
        Plugin.InteractWithGameObject(npc);
        matchStartAttempts++;
        questAcceptedAt = null;
        questSettledSince = null;

        state = State.StartingMatch;
        stateEnteredAt = DateTime.UtcNow;
        StatusText = Loc.T($"Fordere heraus: {currentNpcName}...", $"Challenging: {currentNpcName}...");
    }

    private void UpdateStartingMatch()
    {
        // Manche Gegner bieten erst ein Auswahlmenü an (Quest/Gespräch/Triple Triad) - den Triple-
        // Triad-Eintrag wählen. Reine Gesprächsfenster übernimmt Saucy (Dialog-Skip) bzw. hier.
        Plugin.TrySelectStringContaining("Triple Triad");

        // Manche Gegner (z.B. Mimidoa) bieten zuerst eine Quest an und lassen sich erst nach deren
        // Annahme herausfordern: Quest annehmen, Gespräch durchklicken, dann erneut ansprechen.
        if (Plugin.IsJournalAcceptOpen())
        {
            if (DateTime.UtcNow - lastQuestAcceptClick > TimeSpan.FromSeconds(1) && Plugin.TryAcceptJournalQuest())
            {
                lastQuestAcceptClick = DateTime.UtcNow;
                questAcceptedAt = DateTime.UtcNow;
                StatusText = Loc.T($"Nehme Quest von {currentNpcName} an...", $"Accepting quest from {currentNpcName}...");
            }
            return;
        }

        Plugin.TryAdvanceTalkDialogue();

        if (questAcceptedAt.HasValue && !Plugin.IsTripleTriadUiOpen())
        {
            // Warten, bis das Quest-Gespräch vorbei ist, dann ohne verbrauchten Versuch neu ansprechen.
            if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] || Plugin.Condition[ConditionFlag.OccupiedInEvent]
                || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Plugin.IsAnimationLocked())
            {
                questSettledSince = null;
                return;
            }

            questSettledSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - questSettledSince.Value < TimeSpan.FromSeconds(1.5))
                return;

            Plugin.Log.Info($"[TripleTriadAutomation] Quest von {currentNpcName} angenommen - fordere erneut heraus.");
            questAcceptedAt = null;
            questSettledSince = null;
            matchStartAttempts = Math.Max(0, matchStartAttempts - 1);
            state = State.Approaching;
            stateEnteredAt = DateTime.UtcNow;
            dismountedAt = DateTime.UtcNow - DismountSettleDelay;
            return;
        }

        if (Plugin.IsTripleTriadUiOpen())
        {
            state = State.Playing;
            stateEnteredAt = DateTime.UtcNow;
            playStartedAt = DateTime.UtcNow;
            allCardsObtainedSince = null;
            StatusText = Loc.T($"Spiele gegen {currentNpcName}...", $"Playing against {currentNpcName}...");
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt < MatchStartTimeout)
            return;

        if (matchStartAttempts >= MaxMatchStartAttempts)
        {
            SendCommand("/saucy tt stop");
            SkipCurrent(Loc.T("Partie ließ sich nicht starten", "Couldn't start a match"));
            return;
        }

        // Nochmal ansprechen (z.B. Gespräch wurde abgebrochen).
        Plugin.Log.Info($"[TripleTriadAutomation] Keine Partie gegen {currentNpcName} gestartet - neuer Versuch.");
        state = State.Approaching;
        stateEnteredAt = DateTime.UtcNow;
        dismountedAt = DateTime.UtcNow - DismountSettleDelay;
    }

    private void UpdatePlaying()
    {
        var obtained = currentCards.Count(IsCardObtained);
        StatusText = Loc.T(
            $"Spiele gegen {currentNpcName}... ({obtained}/{currentCards.Count} Karten)",
            $"Playing against {currentNpcName}... ({obtained}/{currentCards.Count} cards)");

        var uiOpen = Plugin.IsTripleTriadUiOpen() || Plugin.Condition[ConditionFlag.OccupiedInEvent];
        if (obtained >= currentCards.Count)
        {
            if (uiOpen)
            {
                allCardsObtainedSince = null;
                return;
            }

            allCardsObtainedSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - allCardsObtainedSince.Value < FinishedSettleDuration)
                return;

            Plugin.Log.Info($"[TripleTriadAutomation] Alle Karten von {currentNpcName} erhalten - lerne sie.");
            SendCommand("/saucy tt stop");
            state = State.UsingCards;
            stateEnteredAt = DateTime.UtcNow;
            lastCardUseAt = DateTime.MinValue;
            return;
        }

        allCardsObtainedSince = null;

        if (DateTime.UtcNow - playStartedAt > MaxPlayDuration)
        {
            SendCommand("/saucy tt stop");
            SkipCurrent(Loc.T("Karten-Drops dauern zu lange", "Card drops are taking too long"));
            return;
        }

        // Saucy hat aufgehört (z.B. Deck ungeeignet/Fehler) und kein Fenster ist mehr offen, obwohl
        // noch Karten fehlen - erneut herausfordern.
        if (!uiOpen && DateTime.UtcNow - stateEnteredAt > MatchStartTimeout)
        {
            Plugin.Log.Info($"[TripleTriadAutomation] Keine Partie mehr offen, aber noch Karten von {currentNpcName} offen - fordere erneut heraus.");
            state = State.Approaching;
            stateEnteredAt = DateTime.UtcNow;
            dismountedAt = DateTime.UtcNow - DismountSettleDelay;
            matchStartAttempts = 0;
        }
        else if (uiOpen)
        {
            stateEnteredAt = DateTime.UtcNow;
        }
    }

    private void UpdateUsingCards()
    {
        StatusText = Loc.T($"Lerne Karten von {currentNpcName}...", $"Learning cards from {currentNpcName}...");

        if (Plugin.IsAnimationLocked() || Plugin.Condition[ConditionFlag.Casting] || Plugin.IsTripleTriadUiOpen())
            return;

        if (DateTime.UtcNow - lastCardUseAt < UseCardInterval)
            return;

        // Nächste gewonnene, noch nicht gelernte Karte benutzen.
        var toLearn = currentCards.FirstOrDefault(c => !Plugin.Instance.IsOwned(c) && Plugin.Instance.GetCurrencyAmount(Plugin.GetUnlockItemId(c)) > 0);
        if (toLearn != null)
        {
            lastCardUseAt = DateTime.UtcNow;
            var used = Plugin.TryUseInventoryItem(Plugin.GetUnlockItemId(toLearn));
            Plugin.Log.Info($"[TripleTriadAutomation] Lerne Karte {toLearn.Name}: {(used ? "benutzt" : "nicht gefunden")}.");
            return;
        }

        Plugin.Log.Info($"[TripleTriadAutomation] Gegner {currentNpcName} erledigt.");
        finishedNpcIds.Add(currentNpcId);
        currentNpcId = 0;
        QueueExitRouteIfInside();
        state = State.Idle;
    }

    private void SkipCurrent(string reason)
    {
        // Abgelehnter Laufweg auf dem Rückweg: einfach normal weiter (TryStartNext plant neu).
        if (routeIsExit)
        {
            Plugin.Log.Info($"[TripleTriadAutomation] Rückweg abgebrochen: {reason}");
            StopPath();
            ResetRoute();
            state = State.Idle;
            return;
        }

        Plugin.Log.Info($"[TripleTriadAutomation] Überspringe {currentNpcName}: {reason}");
        if (currentNpcId != 0)
            skippedNpcIds.Add(currentNpcId);

        StatusText = Loc.T($"Übersprungen ({reason}): {currentNpcName}", $"Skipped ({reason}): {currentNpcName}");
        StopPath();
        currentNpcId = 0;
        QueueExitRouteIfInside();
        state = State.Idle;
    }

    private void ResetRoute()
    {
        routeQueue.Clear();
        routeIsExit = false;
        activeRoute = null;
        routeEntered = false;
    }

    // Nach einem Gegner mit festem Laufweg: den Weg rückwärts merken - TryStartNext läuft ihn nur,
    // wenn danach noch ein Gegner in der Zone offen ist (sonst endet die Automation einfach).
    private void QueueExitRouteIfInside()
    {
        if (activeRoute != null && routeEntered)
            pendingExitRoute = activeRoute.Reverse().ToArray();

        ResetRoute();
    }

    private void BeginRouteLeg()
    {
        currentTargetPosition = routeQueue.Dequeue();
        BeginPathfind(currentTargetPosition, RouteTolerance, allowFly: false);
        if (state == State.MovingTo)
            StatusText = routeIsExit
                ? Loc.T("Laufe festen Laufweg zurück...", "Walking the fixed route back...")
                : Loc.T($"Laufe festen Laufweg zu: {currentNpcName}...", $"Walking the fixed route to: {currentNpcName}...");
    }
}
