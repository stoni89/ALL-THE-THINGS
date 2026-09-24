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
        WalkingToLocalAethernet,
        TravelingToDistrict,
        Mounting,
        MovingTo,
        WaitingForUnlock,
        EnsuringExactPosition,
        WalkingOut,
        JumpingPuzzle,
    }

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);
    private const float ArrivalTolerance = 4f;

    // Sightseeing-Punkte schalten offenbar erst frei, wenn man wirklich GENAU auf der animierten
    // Kugel steht, nicht nur grob in der Nähe (anders als z.B. Aetheryten mit echtem Klick-Radius) -
    // nach dem groben Laufweg (der über den navmesh-genähten "floorPoint" nur die Erreichbarkeit
    // sicherstellt, siehe StartMovingTo) folgt daher ein zweiter, viel engerer Laufauftrag direkt zur
    // echten geloggten Position, siehe BeginFinalApproach.
    // Buchstäblich 0 lässt den fliegenden Anflug (Schweben mit dem Mount) hier und da nie exakt
    // "ankommen" (pathIsRunning bleibt endlos true) - deshalb ein winziger, aber nicht-null Wert.
    private const float FinalApproachTolerance = 0.1f;

    // Toleranz für den (optionalen) letzten Schritt NACH dem Abmounten am Zielpunkt, siehe Plugin.
    // SightseeingExactStandPositions/UpdateEnsuringExactPosition - noch enger als
    // FinalApproachTolerance, da hier wirklich exakt die zur Freischaltung nötige Stelle erreicht
    // werden muss, nicht nur "nah genug dran".
    private const float ExactPositionTolerance = 0.15f;

    // Toleranz für die Landekorrektur nach dem fliegenden ersten Zwischenstopp (siehe UpdateMoving/
    // hasLandedAtFirstApproachWaypoint) - eng genug, um eine spürbare Restschwebehöhe zu erzwingen,
    // aber nicht so eng wie ExactPositionTolerance (hier geht es nur ums Landen, nicht um einen
    // exakten Freischalt-Punkt).
    private const float ApproachWaypointLandingTolerance = 0.5f;

    // Kurze Pause zwischen den einzelnen von Hand hinterlegten Zwischenstopps (siehe Plugin.
    // SightseeingApproachWaypoints/UpdateMoving), bevor jeweils zum nächsten weitergelaufen/
    // -geflogen wird.
    private static readonly TimeSpan InterWaypointPauseDuration = TimeSpan.FromSeconds(0.1);

    // Ankunftstoleranz beim Zwischenstopp an einem Aethernetz-Kristall (siehe BeginWalkToLocalAethernet)
    // - dieselben Werte wie AetheryteAutomation.SmallAetheryteFinalApproachDistance/
    // BigAetheryteFinalApproachDistance: eng genug, um wirklich in Aethernetz-Reichweite zu stehen
    // (statt "in der Nähe" daran vorbeizulaufen), aber nicht so eng, dass vnavmesh gegen den
    // Kristallsockel selbst läuft.
    private const float SmallAetheryteArrivalTolerance = 3.5f;
    private const float BigAetheryteArrivalTolerance = 6f;

    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);

    // Wie lange nach Ankunft (und ggf. dem nötigen Emote) auf die Freischaltung gewartet wird.
    private static readonly TimeSpan UnlockWaitTimeout = TimeSpan.FromSeconds(15);

    // Kurze Pause zwischen Ankunft und Emote-Ausführung - unmittelbar nach dem Stillstehen
    // angewendet, spielt der Emote manchmal nicht zuverlässig an (Bewegungs-Cancel).
    private static readonly TimeSpan PreEmoteDelay = TimeSpan.FromSeconds(1);

    // Für bereits aufgezeichnete Punkte (v.a. im Simulation-Modus, siehe Configuration.
    // SimulateSightseeingAutomation, der bewusst auch schon abgeschlossene Punkte zu Testzwecken
    // erneut anlaufen lässt) - kein Emote nötig, nur kurz am Punkt stehen bleiben, damit man den
    // erreichten Punkt optisch bestätigt bekommt, dann weiter zum nächsten.
    private static readonly TimeSpan AlreadyCompleteLingerDuration = TimeSpan.FromSeconds(1.5);

    // Condition[Mounted] wird schon VOR dem Ende der sichtbaren Absteige-/Lande-Animation false -
    // nach dem Abmounten (siehe UpdateWaitingForUnlock) zusätzlich noch kurz warten, damit der
    // Emote nicht mitten in dieser Animation ins Leere läuft. Gleicher Wert/Begründung wie
    // AetheryteAutomation.DismountSettleDelay.
    private static readonly TimeSpan DismountSettleDelay = TimeSpan.FromSeconds(1);

    // Wie lange maximal auf eine Lifestream-Reise in einen Nachbarbezirk gewartet wird (Ladebildschirm
    // + eventuelles eigenes Laufen von Lifestream zum Ziel-Aetheryten) - siehe GoToAutomation/
    // AetheryteAutomation.DistrictTravelTimeout (identische Begründung).
    private static readonly TimeSpan DistrictTravelTimeout = TimeSpan.FromSeconds(60);

    // Nach "Lifestream.IsBusy() == false" kann es noch einen Moment dauern, bis Plugin.ClientState.
    // TerritoryType tatsächlich auf die neue Zone aktualisiert ist - siehe GoToAutomation.
    // DistrictTravelSettleDelay (identische Begründung).
    private static readonly TimeSpan DistrictTravelSettleDelay = TimeSpan.FromSeconds(2);

    private const int MaxAttemptsPerTarget = 2;
    private readonly Dictionary<uint, int> attemptCounts = new();
    private readonly HashSet<uint> skippedIds = new();

    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> navmeshIsReady;
    private readonly ICallGateSubscriber<Vector3?> queryFlagToPoint;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> moveToPath; // gerade Linie ohne Wegsuche (Jumping Puzzles)
    private readonly ICallGateSubscriber<float> pathGetTolerance;
    private readonly ICallGateSubscriber<float, object> pathSetTolerance;

    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleport;
    private readonly ICallGateSubscriber<uint, bool> lifestreamAethernetTeleportById;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;
    private readonly ICallGateSubscriber<object> lifestreamAbort;

    private State state = State.Idle;
    private CollectibleEntry? currentTargetEntry;
    private Vector3 currentTargetPosition;
    private DateTime stateEnteredAt;
    private bool hasSeenPathRunning;
    private DateTime lastRemountAttempt = DateTime.MinValue;
    private readonly NavigationStuckDetector stuckDetector = new();
    private readonly FlightPathUpgrade flightUpgrade = new(); // siehe Plugin.FlightPathUpgrade (Flugverbots-Bereiche)
    private bool hasSentEmote;
    private bool didFinalApproach;
    private DateTime? interWaypointPauseStartedAt;
    private DateTime? districtTravelFinishedAt;
    private DateTime? dismountedAt;
    private IReadOnlyList<Plugin.SightseeingApproachWaypoint>? pendingApproachWaypoints;
    private int pendingApproachWaypointIndex;
    private bool hasLandedAtFirstApproachWaypoint;
    private IReadOnlyList<Vector3>? pendingPostCompletionWaypoints;
    private int postCompletionWaypointIndex;
    private bool hasEnsuredExactPosition;

    // Ob das AKTUELLE Teilstück (siehe currentTargetPosition) fliegend versucht werden darf - true
    // für den Normalfall (Karten-Flagge/FlagToPoint-Umweg, erster Zwischenstopp, finaler Schritt),
    // false für einen Zwischenstopp mit AllowFlying=false (z.B. ein Durchgang wie eine Tür, durch
    // die man nicht hindurchfliegen kann - siehe Plugin.SightseeingApproachWaypoints). Nur von
    // BeginPathfind gelesen, das bei erneuten Versuchen (Steckengeblieben, nach dem Aufsteigen) für
    // GENAU DASSELBE Teilstück erneut aufgerufen wird.
    private bool currentLegAllowsFlying = true;

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
        moveToPath = Plugin.PluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        pathGetTolerance = Plugin.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Path.GetTolerance");
        pathSetTolerance = Plugin.PluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");

        lifestreamTeleport = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        lifestreamAethernetTeleportById = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById");
        lifestreamIsBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lifestreamAbort = Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
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

    private bool IsLifestreamAvailable()
    {
        try
        {
            return lifestreamTeleport.HasFunction && lifestreamIsBusy.HasFunction;
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

    private void StopLifestream()
    {
        try
        {
            if (lifestreamAbort.HasAction)
                lifestreamAbort.InvokeAction();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Abbrechen von Lifestream.");
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
        RestorePathTolerance();

        // Zusätzlich zum IPC-Stop (StopPath) noch den echten Chat-Befehl absetzen - manuell
        // angefordert, offenbar bricht das den laufenden vnavmesh-Pfad zuverlässiger komplett ab.
        Plugin.SendGameChatCommand("/vnav stop");

        StopLifestream();
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
    /// <param name="pendingInZone">
    /// Punkte, die nur wegen Wetter/Uhrzeit gerade nicht erledigbar sind (siehe
    /// Plugin.IsSightseeingOnlyTemporarilyUnavailable) - solange davon welche übrig sind, wartet die
    /// Automation im Leerlauf darauf, statt sich zu beenden (AFK-Modus, ausdrücklicher Nutzerwunsch).
    /// </param>
    public void Update(IReadOnlyList<CollectibleEntry> sightseeingInZone, IReadOnlyList<CollectibleEntry> pendingInZone)
    {
        if (!IsActive)
            return;

        // Nach einem Fehler kurz pausieren, dann einfach weitermachen (nie selbst abbrechen).
        if (DateTime.UtcNow < pausedUntil)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(sightseeingInZone, pendingInZone);
                    break;

                case State.WalkingToLocalAethernet:
                    UpdateWalkingToLocalAethernet(sightseeingInZone);
                    break;

                case State.TravelingToDistrict:
                    UpdateTravelingToDistrict(sightseeingInZone);
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

                case State.EnsuringExactPosition:
                    UpdateEnsuringExactPosition();
                    break;

                case State.WalkingOut:
                    UpdateWalkingOut();
                    break;

                case State.JumpingPuzzle:
                    UpdateJumpingPuzzle(sightseeingInZone);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Nicht mehr stoppen (AFK-Modus) - aktuellen Punkt aufgeben, kurz pausieren, dann weiter.
            RestorePathTolerance();
            Plugin.Log.Error(ex, "Fehler bei der Sightseeing-Automation - pausiere kurz und mache dann weiter.");
            StatusText = Loc.T("Fehler bei vnavmesh - neuer Versuch in Kürze...", "Error talking to vnavmesh - retrying shortly...");
            StopPath();
            currentTargetEntry = null;
            state = State.Idle;
            pausedUntil = DateTime.UtcNow + ErrorPauseDuration;
        }
    }

    // Siehe Update - Pause nach einem Fehler, und wann übersprungene Punkte erneut versucht werden.
    private static readonly TimeSpan ErrorPauseDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SkippedRetryDelay = TimeSpan.FromSeconds(15);
    private DateTime pausedUntil = DateTime.MinValue;
    private DateTime? retrySkippedAt;
    private string lastSkipReason = string.Empty;

    private static bool StillNeeded(IReadOnlyList<CollectibleEntry> entries, uint id) => entries.Any(e => e.Id == id);

    private void TryStartNext(IReadOnlyList<CollectibleEntry> entries, IReadOnlyList<CollectibleEntry> pendingInZone)
    {
        var available = entries.Where(e => e.WorldPosition.HasValue).ToList();
        var candidates = available.Where(e => !skippedIds.Contains(e.Id)).ToList();
        if (candidates.Count == 0)
        {
            // Simulation (Testmodus) läuft wie bisher einmal durch und endet dann.
            var pending = pendingInZone.Where(e => e.WorldPosition.HasValue).ToList();
            if (Plugin.Instance.Configuration.SimulateSightseeingAutomation || (available.Count == 0 && pending.Count == 0))
            {
                // In der Zone ist gar kein Punkt mehr zu erledigen (auch später nicht) - erst jetzt beenden.
                StatusText = Loc.T(
                    "Keine Sightseeing-Punkte mehr in dieser Zone zu erledigen.",
                    "No sightseeing points left to complete in this zone.");
                Stop();
                return;
            }

            // Übersprungene, aber gerade verfügbare Punkte nach einer Pause erneut versuchen (AFK-Modus -
            // ein einzelner Fehlschlag soll nicht dauerhaft liegen bleiben).
            if (available.Count > 0)
            {
                retrySkippedAt ??= DateTime.UtcNow + SkippedRetryDelay;
                if (DateTime.UtcNow >= retrySkippedAt.Value)
                {
                    Plugin.Log.Info($"[SightseeingAutomation] Versuche {available.Count} übersprungene Punkte erneut.");
                    retrySkippedAt = null;
                    skippedIds.Clear();
                    attemptCounts.Clear();
                    return;
                }
            }

            // Gerade aktive (nur übersprungene) Punkte haben Vorrang vor dem Warten auf Wetter/Uhrzeit -
            // dann das anzeigen, nicht einen erst in einer Stunde verfügbaren Punkt.
            if (available.Count > 0)
            {
                var retryIn = retrySkippedAt.HasValue ? Math.Max(0, (int)(retrySkippedAt.Value - DateTime.UtcNow).TotalSeconds) : 0;
                var names = string.Join(", ", available.Select(e => e.Name));
                var reason = string.IsNullOrEmpty(lastSkipReason) ? string.Empty : $" ({lastSkipReason})";
                StatusText = Loc.T(
                    $"Aktiv, aber übersprungen{reason}: {names} - neuer Versuch in {retryIn}s...",
                    $"Active but skipped{reason}: {names} - retrying in {retryIn}s...");
                return;
            }

            // Leerlauf: auf den Punkt warten, der als nächstes (Wetter/Uhrzeit) verfügbar wird.
            var soonest = pending
                .OrderBy(e => Plugin.GetSightseeingAvailableIn(e) ?? TimeSpan.MaxValue)
                .FirstOrDefault();
            if (soonest != null)
            {
                var availableIn = Plugin.GetSightseeingAvailableInText(soonest);
                StatusText = string.IsNullOrEmpty(availableIn)
                    ? Loc.T($"Warte auf Wetter/Uhrzeit: {soonest.Name}...", $"Waiting for weather/time: {soonest.Name}...")
                    : Loc.T($"Warte auf Wetter/Uhrzeit: {soonest.Name} (in {availableIn})...", $"Waiting for weather/time: {soonest.Name} (in {availableIn})...");
            }
            else
            {
                var retryIn = retrySkippedAt.HasValue ? Math.Max(0, (int)(retrySkippedAt.Value - DateTime.UtcNow).TotalSeconds) : 0;
                StatusText = Loc.T(
                    $"Warte - übersprungene Punkte werden in {retryIn}s erneut versucht...",
                    $"Waiting - skipped points will be retried in {retryIn}s...");
            }

            return;
        }

        retrySkippedAt = null;

        // Solange vnavmesh das Navmesh noch lädt (Start, nach Zonen-/Bezirkswechsel), NICHT als
        // Versuch zählen - sonst war ein Punkt schon nach zwei Frames "zu oft versucht".
        if (!navmeshIsReady.InvokeFunc())
        {
            StatusText = Loc.T("Warte auf vnavmesh-Navmesh für diese Zone...", "Waiting for vnavmesh's navmesh for this zone...");
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
            lastSkipReason = Loc.T("zu oft versucht", "too many attempts");
            StatusText = Loc.T($"Übersprungen (zu oft versucht): {entry.Name}", $"Skipped (too many attempts): {entry.Name}");
            state = State.Idle;
            return;
        }

        currentTargetEntry = entry;
        hasSentEmote = false;
        didFinalApproach = false;
        interWaypointPauseStartedAt = null;
        dismountedAt = null;
        pendingApproachWaypoints = null;
        pendingApproachWaypointIndex = 0;
        hasLandedAtFirstApproachWaypoint = false;
        pendingPostCompletionWaypoints = null;
        postCompletionWaypointIndex = 0;
        lastPathRetryAt = DateTime.MinValue;
        hasEnsuredExactPosition = false;
        currentLegAllowsFlying = true;
        currentPuzzle = Plugin.TryGetSightseeingJumpingPuzzle(entry.Id, out var puzzle) ? puzzle : null;
        puzzleAttempts = 0;
        puzzleStepRetries = 0;

        // Sightseeing-Punkte einer geteilten Hauptstadt können in einem ANDEREN Bezirk liegen als
        // dem, in dem man gerade steht (siehe siblingTerritories-Filter in CompactOverlayWindow, z.B.
        // "Barracuda Piers" in den Limsa Upper Decks) - vnavmesh kann nicht über eine Ladezone hinweg
        // navigieren, daher zuerst per Lifestream in den Zielbezirk reisen, genau wie
        // AetheryteAutomation/GoToAutomation (siehe TryTravelToDistrict). Anders als bei diesen beiden
        // (die den Bezirkswechsel erst anstoßen, nachdem sie ohnehin schon direkt an einem Kristall
        // im aktuellen Bezirk stehen) muss hier zuerst noch zu einem nahen Kristall HINGELAUFEN
        // werden (siehe BeginWalkToLocalAethernet) - Lifestreams Aethernetz-Sprung funktioniert nur
        // aus der Reichweite eines Aethernetz-Punkts heraus, nicht von einer beliebigen Position wie
        // mitten an einem Sightseeing-Punkt.
        var currentTerritory = Plugin.ResolveEffectiveTerritoryId(Plugin.ClientState.TerritoryType);
        if (entry.TerritoryTypeId != currentTerritory)
        {
            BeginWalkToLocalAethernet(entry);
            return;
        }

        BeginNavigateToEntry(entry);
    }

    private void BeginNavigateToEntry(CollectibleEntry entry)
    {
        if (!navmeshIsReady.InvokeFunc())
        {
            // Nicht als Versuch zählen (siehe TryStartNext) - im nächsten Frame neu auswählen.
            attemptCounts[entry.Id] = Math.Max(0, attemptCounts.GetValueOrDefault(entry.Id, 1) - 1);
            currentTargetEntry = null;
            state = State.Idle;
            StatusText = Loc.T("Warte auf vnavmesh-Navmesh für diese Zone...", "Waiting for vnavmesh's navmesh for this zone...");
            return;
        }

        // Von Hand hinterlegte Zwischenstopps (siehe Plugin.SightseeingApproachWaypoints) haben
        // Vorrang vor dem Karten-Flagge-Umweg - für Punkte, bei denen selbst der darüber gefundene
        // grobe Punkt noch gegen eine Wand/ein Geländer führt, oder ein enger Durchgang (z.B. eine
        // Tür) einen bestimmten Anflugweg braucht. Läuft sie der Reihe nach ab (siehe UpdateMoving),
        // der eigentliche letzte, enge Schritt zur echten Position passiert unverändert danach über
        // BeginFinalApproach.
        if (currentPuzzle != null)
        {
            // Jumping Puzzle: erst normal (auch beritten/fliegend) in die Nähe des Startpunkts, der
            // Rest läuft über UpdateJumpingPuzzle.
            currentTargetPosition = currentPuzzle.Start;
        }
        else if (Plugin.TryGetSightseeingApproachWaypoints(entry.Id, out var waypoints))
        {
            pendingApproachWaypoints = waypoints;
            pendingApproachWaypointIndex = 0;
            currentTargetPosition = waypoints[0].Position;
            currentLegAllowsFlying = waypoints[0].AllowFlying;
        }
        else
        {
            // Genau derselbe Trick wie bei GoToAutomation/HuntingLogAutomation: die Karten-Flagge auf
            // die Zielposition setzen und vnavmesh nach einem begehbaren Punkt in deren Nähe fragen -
            // Aussichtspunkte liegen oft an Klippenkanten/erhöhten Stellen, eine reine Koordinatensuche
            // (PointOnFloor) fände dort häufig gar keinen begehbaren Punkt.
            Plugin.OpenEntryMap(entry, showMapWindow: false);
            var floorPoint = queryFlagToPoint.InvokeFunc();
            if (floorPoint == null)
            {
                skippedIds.Add(entry.Id);
                lastSkipReason = Loc.T("nicht erreichbar", "not reachable");
                StatusText = Loc.T($"Übersprungen (nicht erreichbar): {entry.Name}", $"Skipped (not reachable): {entry.Name}");
                currentTargetEntry = null;
                state = State.Idle;
                return;
            }

            currentTargetPosition = floorPoint.Value;
        }

        if (Plugin.TryRequestAetheryteMount())
        {
            state = State.Mounting;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Rufe Mount, dann: {entry.Name}...", $"Summoning mount, then: {entry.Name}...");
            return;
        }

        BeginPathfind();
    }

    /// <summary>
    /// Läuft (innerhalb des AKTUELLEN Bezirks, ganz normal per vnavmesh) zum nächstgelegenen bereits
    /// freigeschalteten Aetheryte/Aethernetz-Kristall - Lifestreams Aethernetz-Sprung (siehe
    /// TryTravelToDistrict) funktioniert nur aus der Reichweite eines solchen Punkts heraus, ein
    /// Sightseeing-Punkt (anders als bei AetheryteAutomation, die zwischen Kristallen selbst hin und
    /// her läuft) liegt aber normalerweise nicht in dieser Reichweite. Kein eigener Kristall im
    /// aktuellen Bezirk bekannt/erreichbar? Dann direkt den (auch von weiter weg funktionierenden,
    /// aber kostenpflichtigen) Teleport probieren statt hier hängen zu bleiben.
    /// </summary>
    private void BeginWalkToLocalAethernet(CollectibleEntry entry)
    {
        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var localCrystal = Plugin.FindNearestUnlockedAetheryteInZone(Plugin.ClientState.TerritoryType, playerPos);

        // Echte Weltposition (MapMarker-Pixelformel bzw. manuell nachgetragener Wert, siehe
        // Plugin.ResolveAetheryteWorldPosition) statt der Karten-Flagge/FlagToPoint-Näherung -
        // dieselbe, bereits bei AetheryteAutomation bewährte Quelle. Der Flaggen-Umweg lieferte hier
        // teils einen Punkt spürbar neben/hinter dem Kristall, an dem vorbeigelaufen wurde, statt
        // direkt bei ihm stehen zu bleiben.
        var crystalPosition = localCrystal != null ? Plugin.ResolveAetheryteWorldPosition(localCrystal.Id) : null;
        if (localCrystal == null || crystalPosition == null)
        {
            TryTravelToDistrict(entry);
            return;
        }

        currentTargetPosition = crystalPosition.Value;
        var tolerance = Plugin.IsBigAetheryte(localCrystal.Id) ? BigAetheryteArrivalTolerance : SmallAetheryteArrivalTolerance;

        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var accepted = false;
        if (mounted && Plugin.CanFly)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, true, tolerance);

        if (!accepted)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, tolerance);

        if (!accepted)
        {
            TryTravelToDistrict(entry);
            return;
        }

        state = State.WalkingToLocalAethernet;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
        stuckDetector.Reset();
        StatusText = Loc.T(
            $"Laufe zum nächsten Aethernetz-Kristall, dann weiter zu: {entry.Name}...",
            $"Walking to the nearest aethernet crystal, then on to: {entry.Name}...");
    }

    private void UpdateWalkingToLocalAethernet(IReadOnlyList<CollectibleEntry> entries)
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

            Plugin.TryRemountAfterForcedDismount(ref lastRemountAttempt);

            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[SightseeingAutomation] UpdateWalkingToLocalAethernet({currentTargetEntry.Name}): scheinbar steckengeblieben - versuche direkt den Bezirkswechsel.");
                StopPath();
                TryTravelToDistrict(currentTargetEntry);
                return;
            }

            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
            {
                StopPath();
                TryTravelToDistrict(currentTargetEntry);
            }

            return;
        }

        // Angekommen (oder nie richtig losgelaufen, siehe PathStartGracePeriod) - jetzt den
        // Aethernetz-Sprung probieren. Ist man doch noch zu weit vom Kristall weg, lehnt Lifestream
        // selbst ab und TryTravelToDistrict fällt auf den bezahlten Teleport zurück.
        if (hasSeenPathRunning || Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
            TryTravelToDistrict(currentTargetEntry);
    }

    /// <summary>
    /// Reist per Lifestream in den Bezirk dieses Ziels - erst kostenlos per Aethernetz zum zum
    /// eigentlichen Sightseeing-Punkt nächstgelegenen schon freigeschalteten Kristall dort (statt
    /// "irgendeinem", der unnötig weit vom Ziel entfernt liegen kann), sonst per bezahlter
    /// Teleport-Aktion zum großen Aetheryten. Klappt keins von beidem, wird dieser Punkt übersprungen
    /// statt endlos zu warten - siehe AetheryteAutomation/GoToAutomation.TryTravelToDistrict
    /// (ähnliche Logik, dort allerdings ohne Entfernungs-Auswahl).
    /// </summary>
    private void TryTravelToDistrict(CollectibleEntry entry)
    {
        if (!IsLifestreamAvailable())
        {
            skippedIds.Add(entry.Id);
            lastSkipReason = Loc.T("Lifestream nicht gefunden", "Lifestream not found");
            StatusText = Loc.T(
                $"Übersprungen (Lifestream nicht gefunden): {entry.Name}",
                $"Skipped (Lifestream not found): {entry.Name}");
            currentTargetEntry = null;
            state = State.Idle;
            return;
        }

        var targetTerritory = entry.TerritoryTypeId;
        var destinationCrystal = entry.WorldPosition is { } targetPos
            ? Plugin.FindNearestUnlockedAetheryteInZone(targetTerritory, targetPos)
            : null;
        if (destinationCrystal != null && lifestreamAethernetTeleportById.InvokeFunc(destinationCrystal.Id))
        {
            state = State.TravelingToDistrict;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Reise (Aethernetz) in den Bezirk von: {entry.Name}...", $"Traveling (aethernet) to the district of: {entry.Name}...");
            return;
        }

        var mainAetheryteId = Plugin.FindUnlockedMainAetheryteId(targetTerritory);
        var accepted = mainAetheryteId.HasValue && lifestreamTeleport.InvokeFunc(mainAetheryteId.Value, (byte)0);
        if (!accepted)
        {
            skippedIds.Add(entry.Id);
            lastSkipReason = Loc.T("Bezirk nicht erreichbar", "district not reachable");
            StatusText = Loc.T(
                $"Übersprungen (Bezirk nicht erreichbar): {entry.Name}",
                $"Skipped (district not reachable): {entry.Name}");
            currentTargetEntry = null;
            state = State.Idle;
            return;
        }

        state = State.TravelingToDistrict;
        stateEnteredAt = DateTime.UtcNow;
        StatusText = Loc.T($"Reise in den Bezirk von: {entry.Name}...", $"Traveling to the district of: {entry.Name}...");
    }

    private void UpdateTravelingToDistrict(IReadOnlyList<CollectibleEntry> entries)
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

        if (!lifestreamIsBusy.InvokeFunc())
        {
            // Kurz warten, bis Plugin.ClientState.TerritoryType tatsächlich auf die neue Zone
            // aktualisiert ist - siehe GoToAutomation.DistrictTravelSettleDelay (identische Begründung).
            districtTravelFinishedAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - districtTravelFinishedAt.Value < DistrictTravelSettleDelay)
                return;

            // Zurück auf Idle statt direkt weiterzumachen - TryStartNext wählt dort frisch den
            // nächstgelegenen Punkt (meist, aber nicht zwingend, genau dieser hier), passend zur
            // inzwischen tatsächlichen neuen Position.
            // Der Bezirkswechsel selbst zählt nicht als Versuch für den Punkt.
            districtTravelFinishedAt = null;
            attemptCounts[currentTargetEntry.Id] = Math.Max(0, attemptCounts.GetValueOrDefault(currentTargetEntry.Id, 1) - 1);
            state = State.Idle;
            return;
        }

        districtTravelFinishedAt = null;
        if (DateTime.UtcNow - stateEnteredAt > DistrictTravelTimeout)
        {
            Plugin.Log.Info($"[SightseeingAutomation] UpdateTravelingToDistrict({currentTargetEntry.Name}): Reise dauert zu lange - übersprungen.");
            StopLifestream();
            SkipCurrent(Loc.T("Bezirkswechsel dauert zu lange", "District travel is taking too long"));
        }
    }

    private void BeginPathfind()
    {
        if (!TryBeginPathfindAccepted())
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

    /// <summary>
    /// Zweiter, viel engerer Laufauftrag direkt zur echten geloggten Position (currentTargetEntry.
    /// WorldPosition), NICHT dem u.U. leicht danebenliegenden floorPoint aus BeginPathfind - siehe
    /// FinalApproachTolerance-Kommentar. Gibt false zurück, wenn vnavmesh den Auftrag ablehnt (z.B.
    /// weil schon nah genug dran), dann direkt weiter zu WaitingForUnlock statt hier hängen zu bleiben.
    /// </summary>
    private bool BeginFinalApproach()
    {
        if (currentTargetEntry?.WorldPosition is not { } target)
            return false;

        currentTargetPosition = target;

        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var accepted = false;
        if (mounted && Plugin.CanFly)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(target, true, FinalApproachTolerance);

        if (!accepted)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(target, false, FinalApproachTolerance);

        return accepted;
    }

    /// <summary>
    /// Läuft zum NÄCHSTEN von Hand hinterlegten Zwischenstopp (siehe Plugin.
    /// SightseeingApproachWaypoints) - mit der normalen, großzügigen Toleranz wie BeginPathfind (das
    /// hier auch für Fliegen-Erlaubnis/Abmounten wiederverwendet wird, siehe currentLegAllowsFlying),
    /// da es sich (wie der erste Zwischenstopp) nur um einen groben Etappenpunkt handelt, nicht die
    /// echte Zielposition selbst.
    /// </summary>
    private bool BeginNextApproachWaypoint(Plugin.SightseeingApproachWaypoint waypoint)
    {
        currentTargetPosition = waypoint.Position;
        currentLegAllowsFlying = waypoint.AllowFlying;
        return TryBeginPathfindAccepted();
    }

    /// <summary>Wie BeginPathfind, gibt aber zusätzlich zurück, ob vnavmesh den Laufweg angenommen hat (statt bei Ablehnung SkipCurrent aufzurufen) - für Aufrufer, die bei Ablehnung selbst einen Fallback haben (siehe BeginNextApproachWaypoint).</summary>
    private bool TryBeginPathfindAccepted()
    {
        // Bewusst KEIN erzwungenes Abmounten mehr hier, auch wenn currentLegAllowsFlying false ist -
        // der Charakter soll während des Anflugs (auch für Teilstücke, die nicht fliegend
        // zurückgelegt werden können, z.B. durch eine Tür) beritten bleiben und dort einfach am Boden
        // mit dem Mount laufen, statt komplett abzusteigen. Nur der spätere Rückweg NACH dem
        // Freischalten (siehe TryRequestWalkOutPath) mountet bewusst ab.
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var accepted = false;
        var flyingAccepted = false;
        if (currentLegAllowsFlying && mounted && Plugin.CanFly)
            accepted = flyingAccepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, true, ArrivalTolerance);

        if (!accepted)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, ArrivalTolerance);

        // Teilstücke, die bewusst NICHT geflogen werden (currentLegAllowsFlying = false), nie auf
        // Fliegen umplanen - gilt für FlightPathUpgrade wie ein bereits fliegender Weg.
        if (accepted)
            flightUpgrade.OnPathStarted(flyingAccepted || !currentLegAllowsFlying);

        return accepted;
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
            if (currentPuzzle != null)
            {
                BeginJumpingPuzzle();
                return;
            }

            // Weiterer von Hand hinterlegter Zwischenstopp übrig (siehe Plugin.
            // SightseeingApproachWaypoints)? Dann erst dorthin, bevor der finale enge Schritt
            // (BeginFinalApproach) überhaupt versucht wird.
            if (pendingApproachWaypoints is { } waypoints && pendingApproachWaypointIndex + 1 < waypoints.Count)
            {
                // Nach dem fliegenden ersten Zwischenstopp erst sicherstellen, dass wirklich am Boden
                // gelandet wurde (der lockere ArrivalTolerance beim fliegenden Anflug lässt den
                // Charakter u.U. noch leicht in der Luft stehen) - sonst kann der Bodenlaufweg zum
                // nächsten (ggf. nicht-fliegenden) Zwischenstopp abgelehnt werden, weil kein gültiger
                // Startpunkt auf dem Navmesh gefunden wird.
                if (pendingApproachWaypointIndex == 0 && !hasLandedAtFirstApproachWaypoint)
                {
                    hasLandedAtFirstApproachWaypoint = true;
                    if (pathfindAndMoveCloseTo.InvokeFunc(waypoints[0].Position, false, ApproachWaypointLandingTolerance))
                    {
                        hasSeenPathRunning = false;
                        stateEnteredAt = DateTime.UtcNow;
                        stuckDetector.Reset();
                        StatusText = Loc.T(
                            $"Lande am Zwischenstopp: {currentTargetEntry.Name}...",
                            $"Landing at the waypoint: {currentTargetEntry.Name}...");
                        return;
                    }

                    // Bereits genau genug am Boden - direkt weiter zum nächsten Zwischenstopp.
                }

                // Kurze Pause zwischen den einzelnen Zwischenstopps.
                interWaypointPauseStartedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - interWaypointPauseStartedAt.Value < InterWaypointPauseDuration)
                    return;
                interWaypointPauseStartedAt = null;

                pendingApproachWaypointIndex++;
                if (BeginNextApproachWaypoint(waypoints[pendingApproachWaypointIndex]))
                {
                    hasSeenPathRunning = false;
                    stateEnteredAt = DateTime.UtcNow;
                    stuckDetector.Reset();
                    StatusText = Loc.T(
                        $"Laufe zum nächsten Zwischenstopp: {currentTargetEntry.Name}...",
                        $"Walking to the next waypoint: {currentTargetEntry.Name}...");
                    return;
                }

                // vnavmesh lehnt ab - direkt mit dem finalen Schritt weiter, statt hier hängen zu bleiben.
            }

            if (!didFinalApproach)
            {
                // Kurze Pause nach dem letzten Zwischenstopp, bevor zum eigentlichen Punkt geflogen wird.
                if (pendingApproachWaypoints != null)
                {
                    interWaypointPauseStartedAt ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - interWaypointPauseStartedAt.Value < InterWaypointPauseDuration)
                        return;
                    interWaypointPauseStartedAt = null;
                }

                didFinalApproach = true;
                if (BeginFinalApproach())
                {
                    hasSeenPathRunning = false;
                    stateEnteredAt = DateTime.UtcNow;
                    stuckDetector.Reset();
                    StatusText = Loc.T(
                        $"Laufe genau auf den Punkt: {currentTargetEntry.Name}...",
                        $"Walking precisely onto the point: {currentTargetEntry.Name}...");
                    return;
                }

                // vnavmesh lehnt ab (z.B. weil bereits nah genug dran) - direkt weiter wie bisher.
            }

            state = State.WaitingForUnlock;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Warte auf Freischaltung: {currentTargetEntry.Name}...", $"Waiting to unlock: {currentTargetEntry.Name}...");
            return;
        }

        if (Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
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

        // Erst abmounten (v.a. nach einem fliegenden Anflug) und die Absteige-/Lande-Animation
        // abwarten - AUCH im Simulation-Modus (siehe Configuration.SimulateSightseeingAutomation)
        // und bei bereits aufgezeichneten Punkten, nicht erst danach: sonst bliebe der Charakter beim
        // Weiterfliegen zum nächsten Punkt durchgehend beritten/in der Luft, statt wie beim echten
        // Abschließen auch sichtbar am jeweiligen Punkt zu landen. Gleiches Muster wie
        // AetheryteAutomation.UpdateInteracting (siehe DismountSettleDelay-Kommentar).
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            Plugin.TryDismount();
            dismountedAt = null;
            return;
        }

        dismountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - dismountedAt.Value < DismountSettleDelay)
            return;

        // stateEnteredAt (verwendet unten für PreEmoteDelay/UnlockWaitTimeout/AlreadyCompleteLingerDuration)
        // stammt noch vom Betreten von WaitingForUnlock, also von VOR dem Abmounten - einmalig neu
        // setzen, damit diese Wartezeiten wirklich erst NACH der Lande-/Absteige-Animation zu zählen
        // beginnen.
        if (stateEnteredAt < dismountedAt.Value)
            stateEnteredAt = DateTime.UtcNow;

        // Für manche Punkte reicht die normale Lande-/Ankunftsposition nach dem Abmounten nicht ganz
        // (siehe Plugin.SightseeingExactStandPositions-Kommentar) - dann hier einmalig noch ein
        // letztes kurzes Stück zu Fuß exakt hin, BEVOR überhaupt auf die Freischaltung gewartet oder
        // ein Emote ausgeführt wird, sonst schaltet der Punkt u.U. gar nicht frei.
        if (!hasEnsuredExactPosition)
        {
            hasEnsuredExactPosition = true;
            if (Plugin.TryGetSightseeingExactStandPosition(currentTargetEntry.Id, out var exactPos))
            {
                var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? exactPos;
                if (Vector3.Distance(playerPos, exactPos) > ExactPositionTolerance)
                {
                    currentTargetPosition = exactPos;
                    state = State.EnsuringExactPosition;
                    stateEnteredAt = DateTime.UtcNow;
                    hasSeenPathRunning = false;
                    stuckDetector.Reset();
                    lastPathRetryAt = DateTime.MinValue;
                    StatusText = Loc.T(
                        $"Laufe genau auf den Punkt: {currentTargetEntry.Name}...",
                        $"Walking precisely onto the point: {currentTargetEntry.Name}...");
                    pathfindAndMoveCloseTo.InvokeFunc(exactPos, false, ExactPositionTolerance);
                    lastPathRetryAt = DateTime.UtcNow;
                    return;
                }
            }
        }

        // Bereits aufgezeichnet ODER Simulation-Modus aktiv - dort bewusst NIE ein Emote senden, auch
        // nicht bei einem noch nicht aufgezeichneten Punkt: der Modus dient nur zum Testen von
        // Laufweg/Ankunftsposition, nicht zum tatsächlichen Abschließen - kein Emote senden,
        // stattdessen nur kurz (bereits abgemountet) stehen bleiben und weiter zum nächsten Punkt.
        if (Plugin.IsAdventureComplete(currentTargetEntry.Id) || Plugin.SimulateSightseeingAutomation)
        {
            if (DateTime.UtcNow - stateEnteredAt > AlreadyCompleteLingerDuration)
                TryWalkOutOrFinish(entries);
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
            TryWalkOutOrFinish(entries);
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > UnlockWaitTimeout)
            SkipCurrent(Loc.T("Nicht automatisch freigeschaltet (evtl. falscher Emote oder zu weit weg)", "Not unlocked automatically (maybe the wrong emote or too far away)"));
    }

    /// <summary>
    /// Läuft (siehe UpdateWaitingForUnlock) noch ein letztes kurzes, enges Stück zu Fuß exakt auf
    /// Plugin.SightseeingExactStandPositions, bevor es zurück zu WaitingForUnlock geht, das dann mit
    /// der eigentlichen Freischaltungs-/Emote-Prüfung weitermacht (hasEnsuredExactPosition/
    /// dismountedAt sind zu diesem Zeitpunkt bereits gesetzt). Gibt bei Steckenbleiben/Timeout/nie
    /// gestartetem Laufweg trotzdem auf statt endlos zu warten - dann eben mit der normalen
    /// Landeposition weiter, besser als hier hängen zu bleiben.
    /// </summary>
    private void UpdateEnsuringExactPosition()
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
            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[SightseeingAutomation] UpdateEnsuringExactPosition({currentTargetEntry.Name}): scheinbar steckengeblieben - weiter mit der bisherigen Position.");
                StopPath();
                state = State.WaitingForUnlock;
                stateEnteredAt = DateTime.UtcNow;
                return;
            }

            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
            {
                StopPath();
                state = State.WaitingForUnlock;
                stateEnteredAt = DateTime.UtcNow;
            }

            return;
        }

        if (hasSeenPathRunning)
        {
            state = State.WaitingForUnlock;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        // Noch nie sichtbar losgelaufen - z.B. weil der Charakter gerade erst nach dem Abmounten
        // fällt/landet und vnavmesh den Laufweg deshalb zunächst ablehnt. Innerhalb der Anlaufzeit in
        // kurzen Abständen erneut versuchen, statt sofort aufzugeben.
        if (Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
        {
            state = State.WaitingForUnlock;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - lastPathRetryAt > PathRetryInterval && !Plugin.IsVnavPathfindInProgress())
        {
            lastPathRetryAt = DateTime.UtcNow;
            pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, ExactPositionTolerance);
        }
    }

    /// <summary>
    /// Nach Erledigen eines Punkts MIT von Hand hinterlegten Rückweg-Zwischenstopps (siehe Plugin.
    /// SightseeingPostCompletionWaypoints, z.B. Summerford Farms) erst zu Fuß der Reihe nach dorthin,
    /// statt direkt loszufliegen - der enge Anflugweg (Tür/Wand) muss zu Fuß auch wieder raus. Nur,
    /// wenn danach überhaupt noch ein anderer, aktuell erreichbarer Sightseeing-Punkt übrig ist -
    /// sonst (letzter Punkt der Zone) lohnt sich der Umweg nicht, die Automation stoppt ohnehin gleich.
    /// </summary>
    private void TryWalkOutOrFinish(IReadOnlyList<CollectibleEntry> entries)
    {
        if (currentTargetEntry != null
            && Plugin.TryGetSightseeingPostCompletionWaypoints(currentTargetEntry.Id, out var waypoints)
            && waypoints.Count > 0
            && entries.Any(e => e.Id != currentTargetEntry.Id && !skippedIds.Contains(e.Id)))
        {
            pendingPostCompletionWaypoints = waypoints;
            postCompletionWaypointIndex = 0;
            BeginWalkOutLeg();
            return;
        }

        FinishCurrent();
    }

    private void BeginWalkOutLeg()
    {
        if (pendingPostCompletionWaypoints == null)
        {
            FinishCurrent();
            return;
        }

        currentTargetPosition = pendingPostCompletionWaypoints[postCompletionWaypointIndex];

        state = State.WalkingOut;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
        stuckDetector.Reset();
        lastPathRetryAt = DateTime.MinValue;
        StatusText = Loc.T(
            $"Laufe zurück zum Ausgang: {currentTargetEntry?.Name}...",
            $"Walking back to the exit: {currentTargetEntry?.Name}...");

        // Erster Versuch direkt hier - weitere folgen ggf. über UpdateWalkingOut (siehe dort und
        // PathRetryInterval-Kommentar). Nie fliegend, immer zu Fuß.
        TryRequestWalkOutPath();
    }

    /// <summary>Immer zu Fuß (nie fliegend) - sicherheitshalber vor jedem Versuch abmounten.</summary>
    private void TryRequestWalkOutPath()
    {
        lastPathRetryAt = DateTime.UtcNow;
        Plugin.TryDismount();
        pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, ArrivalTolerance);
    }

    // Zwischen zwei erneuten Pathfind-Versuchen, falls der erste Aufruf noch keinen sichtbaren
    // Laufweg ausgelöst hat (siehe UpdateWalkingOut/UpdateEnsuringExactPosition) - z.B. weil der
    // Charakter gerade erst nach einem Abmounten fällt/landet und vnavmesh deshalb zunächst ablehnt.
    private DateTime lastPathRetryAt = DateTime.MinValue;
    private static readonly TimeSpan PathRetryInterval = TimeSpan.FromSeconds(1);

    private void UpdateWalkingOut()
    {
        if (currentTargetEntry == null || pendingPostCompletionWaypoints == null)
        {
            state = State.Idle;
            return;
        }

        if (pathIsRunning.InvokeFunc())
        {
            hasSeenPathRunning = true;

            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;

            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[SightseeingAutomation] UpdateWalkingOut({currentTargetEntry.Name}): scheinbar steckengeblieben - Rückweg wird abgebrochen.");
                StopPath();
                FinishCurrent();
                return;
            }

            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
            {
                StopPath();
                FinishCurrent();
            }

            return;
        }

        if (hasSeenPathRunning)
        {
            // Wirklich angekommen - weiter zum nächsten Zwischenstopp, oder fertig.
            postCompletionWaypointIndex++;
            if (postCompletionWaypointIndex < pendingPostCompletionWaypoints.Count)
            {
                BeginWalkOutLeg();
                return;
            }

            FinishCurrent();
            return;
        }

        // Noch nie sichtbar losgelaufen - z.B. weil der Charakter nach dem Abmounten am vorherigen
        // (erhöhten) Punkt gerade erst landet/fällt und vnavmesh den Laufweg deshalb zunächst
        // ablehnt. Innerhalb der Anlaufzeit (PathStartGracePeriod) in kurzen Abständen erneut
        // versuchen, statt sofort aufzugeben und (fälschlich) direkt zum nächsten Punkt weiterzuziehen.
        if (Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
        {
            FinishCurrent();
            return;
        }

        if (DateTime.UtcNow - lastPathRetryAt > PathRetryInterval && !Plugin.IsVnavPathfindInProgress())
            TryRequestWalkOutPath();
    }

    private void FinishCurrent()
    {
        RestorePathTolerance();
        Plugin.Log.Info($"[SightseeingAutomation] FinishCurrent({currentTargetEntry?.Name}): freigeschaltet.");
        StatusText = Loc.T($"Erledigt: {currentTargetEntry?.Name}", $"Done: {currentTargetEntry?.Name}");
        attemptCounts.Remove(currentTargetEntry!.Id);

        // Auch bei echtem Erfolg (nicht nur bei SkipCurrent) merken - im Simulation-Modus bleibt der
        // Punkt bewusst dauerhaft in der Zielliste (siehe Configuration.SimulateSightseeingAutomation),
        // sonst würde TryStartNext ihn als nächstgelegenen sofort wieder anlaufen (Distanz ~0, gerade
        // erst erreicht) statt zum nächsten Punkt weiterzugehen.
        skippedIds.Add(currentTargetEntry.Id);

        currentTargetEntry = null;
        state = State.Idle;
    }

    private void SkipCurrent(string reason)
    {
        RestorePathTolerance();
        Plugin.Log.Info($"[SightseeingAutomation] SkipCurrent({currentTargetEntry?.Name}): {reason}");
        if (currentTargetEntry != null)
        {
            skippedIds.Add(currentTargetEntry.Id);
            lastSkipReason = reason;
            StatusText = $"{Loc.T("Übersprungen", "Skipped")} ({reason}): {currentTargetEntry.Name}";
        }

        StopPath();
        currentTargetEntry = null;
        state = State.Idle;
    }

    // ---- Jumping Puzzles (siehe Plugin.SightseeingJumpingPuzzles) ----

    private enum PuzzlePhase
    {
        Dismounting,
        GoingToStart,
        StepSettling,
        StepMoving,
        EvaluatingFailure,
        ReturningToStepStart,
        FinalPrecisePosition,
        FlyingToStart,
    }

    private const float PuzzleFlyToStartTolerance = 0.1f;

    // Letzter Puzzle-Punkt (= Sightseeing-Kugel): mit dieser vnavmesh-Wegpunkt-Toleranz genau draufstellen.
    private const float PuzzleFinalPreciseTolerance = 0.05f;
    private static readonly TimeSpan PuzzleFinalPreciseTimeout = TimeSpan.FromSeconds(4);
    private float? savedPathTolerance;

    private void BeginFinalPrecisePosition(Vector3 target)
    {
        try
        {
            savedPathTolerance ??= pathGetTolerance.InvokeFunc();
            pathSetTolerance.InvokeAction(PuzzleFinalPreciseTolerance);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[SightseeingAutomation] vnavmesh-Toleranz konnte nicht gesetzt werden.");
        }

        moveToPath.InvokeAction(new List<Vector3> { target }, false);
        SetPuzzlePhase(PuzzlePhase.FinalPrecisePosition);
        StatusText = Loc.T(
            $"Jumping Puzzle: stelle mich genau auf den Punkt: {currentTargetEntry?.Name}...",
            $"Jumping puzzle: stepping precisely onto the point: {currentTargetEntry?.Name}...");
    }

    // Für Puzzle-Schritte mit Exact: enge vnavmesh-Wegpunkt-Toleranz, sonst wieder die ursprüngliche.
    private void SetExactPathTolerance(bool exact)
    {
        if (!exact)
        {
            RestorePathTolerance();
            return;
        }

        try
        {
            savedPathTolerance ??= pathGetTolerance.InvokeFunc();
            pathSetTolerance.InvokeAction(PuzzleFinalPreciseTolerance);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[SightseeingAutomation] vnavmesh-Toleranz konnte nicht gesetzt werden.");
        }
    }

    private void RestorePathTolerance()
    {
        if (savedPathTolerance is not { } tolerance)
            return;

        savedPathTolerance = null;
        try
        {
            pathSetTolerance.InvokeAction(tolerance);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[SightseeingAutomation] vnavmesh-Toleranz konnte nicht zurückgesetzt werden.");
        }
    }

    private const int MaxPuzzleAttempts = 10;

    // Misslungener Sprung, aber noch oben am Absprungpunkt (z.B. Apkallu Falls Punkt 2 -> 3, klappt
    // nicht immer): denselben Schritt so oft wiederholen, bis er klappt - nur nach einem Absturz
    // zurück zum Startpunkt.
    private const int MaxPuzzleStepRetries = 50;
    private const float PuzzleStepRetryHeightMargin = 0.5f;
    private const float PuzzleStepRetryRadius = 3f;
    private const float PuzzleStartTolerance = 0.3f;
    private const float PuzzleStartExactTolerance = 0.1f;
    private const float PuzzlePointTolerance = 1.0f;
    private const float PuzzleFallMargin = 1.5f;
    private static readonly TimeSpan PuzzleStepSettleDuration = TimeSpan.FromSeconds(0.4);
    private static readonly TimeSpan PuzzleJumpDelay = TimeSpan.FromSeconds(0.1);
    private static readonly TimeSpan PuzzleStepTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PuzzleGoToStartTimeout = TimeSpan.FromSeconds(60);

    private Plugin.SightseeingJumpingPuzzle? currentPuzzle;
    private PuzzlePhase puzzlePhase;
    private int puzzleStepIndex;
    private int puzzleAttempts;
    private DateTime puzzlePhaseStartedAt;
    private DateTime lastPuzzleDismountAttempt = DateTime.MinValue;
    private bool puzzleJumpSent;
    private bool puzzleGoToStartRequested;
    private float puzzleStepFromY;
    private int puzzleStepRetries;
    private bool puzzleRunUpPending;
    private float puzzleReturnFloorY;
    private bool puzzleSprintUsed;

    // Festlaufen innerhalb eines Schritts (siehe StepMoving): weniger als diese Strecke in dieser Zeit.
    private const float PuzzleProgressMinDistance = 0.15f;
    private static readonly TimeSpan PuzzleStuckDuration = TimeSpan.FromSeconds(0.6);
    private Vector3 puzzleProgressPos;
    private DateTime puzzleProgressAt;

    // Sprung mit Anlauf (SightseeingPuzzleStep.RunUp): so nah (waagerecht) am Absprungpunkt wird
    // abgesprungen, ohne anzuhalten.
    private const float PuzzleRunUpJumpDistance = 0.2f;

    private void BeginJumpingPuzzle()
    {
        StopPath();
        state = State.JumpingPuzzle;
        stateEnteredAt = DateTime.UtcNow;

        // Startpunkt in der Luft: erst beritten genau hinfliegen, dort absteigen.
        if (currentPuzzle?.DismountAtStart == true && Plugin.Condition[ConditionFlag.Mounted])
        {
            SetExactPathTolerance(true);
            var accepted = Plugin.CanFly && pathfindAndMoveCloseTo.InvokeFunc(currentPuzzle.Start, true, PuzzleFlyToStartTolerance);
            if (!accepted)
                moveToPath.InvokeAction(new List<Vector3> { currentPuzzle.Start }, true);
            SetPuzzlePhase(PuzzlePhase.FlyingToStart);
            StatusText = Loc.T($"Fliege genau zum Startpunkt: {currentTargetEntry?.Name}...", $"Flying precisely to the start point: {currentTargetEntry?.Name}...");
            return;
        }

        SetPuzzlePhase(PuzzlePhase.Dismounting);
        StatusText = Loc.T($"Jumping Puzzle: {currentTargetEntry?.Name}...", $"Jumping puzzle: {currentTargetEntry?.Name}...");
    }

    private void SetPuzzlePhase(PuzzlePhase phase)
    {
        puzzlePhase = phase;
        puzzlePhaseStartedAt = DateTime.UtcNow;
        puzzleJumpSent = false;
        puzzleGoToStartRequested = false;
        puzzleRunUpPending = false;
        puzzleProgressPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        puzzleProgressAt = DateTime.UtcNow;
    }

    private void UpdateJumpingPuzzle(IReadOnlyList<CollectibleEntry> entries)
    {
        if (currentTargetEntry == null || currentPuzzle == null)
        {
            state = State.Idle;
            return;
        }

        if (!StillNeeded(entries, currentTargetEntry.Id))
        {
            FinishCurrent();
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        var playerPos = player.Position;
        var sinceStart = DateTime.UtcNow - puzzlePhaseStartedAt;

        switch (puzzlePhase)
        {
            case PuzzlePhase.Dismounting:
                // Gesprungen wird zu Fuß.
                if (Plugin.Condition[ConditionFlag.Mounted])
                {
                    if (DateTime.UtcNow - lastPuzzleDismountAttempt > TimeSpan.FromSeconds(1))
                    {
                        lastPuzzleDismountAttempt = DateTime.UtcNow;
                        Plugin.TryDismount();
                    }

                    puzzlePhaseStartedAt = DateTime.UtcNow;
                    return;
                }

                if (sinceStart < DismountSettleDelay || Plugin.Condition[ConditionFlag.Jumping])
                    return;

                // Startpunkt in der Luft (DismountAtStart): die Landestelle darunter ist der Start.
                if (currentPuzzle.DismountAtStart)
                {
                    BeginPuzzleStep(0);
                    return;
                }

                SetPuzzlePhase(PuzzlePhase.GoingToStart);
                return;

            case PuzzlePhase.FlyingToStart:
                if (Plugin.IsVnavPathfindInProgress() || pathIsRunning.InvokeFunc() || sinceStart < TimeSpan.FromSeconds(0.5))
                {
                    if (sinceStart < PuzzleGoToStartTimeout)
                        return;
                    StopPath();
                }

                // Genau über dem Startpunkt - jetzt absteigen (senkrecht nach unten).
                RestorePathTolerance();
                SetPuzzlePhase(PuzzlePhase.Dismounting);
                return;

            case PuzzlePhase.GoingToStart:
            {
                var start = currentPuzzle.Start;
                if (!puzzleGoToStartRequested)
                {
                    puzzleGoToStartRequested = true;
                    if (IsAtPuzzlePoint(playerPos, start)
                        && Vector2.Distance(new Vector2(playerPos.X, playerPos.Z), new Vector2(start.X, start.Z)) <= PuzzleStartExactTolerance)
                    {
                        BeginPuzzleStep(0);
                        return;
                    }

                    // Normale Wegsuche (z.B. vom Boden unterhalb nach einem Absturz), genau auf den Startpunkt.
                    if (!pathfindAndMoveCloseTo.InvokeFunc(start, false, PuzzleStartTolerance))
                        moveToPath.InvokeAction(new List<Vector3> { start }, false);

                    StatusText = Loc.T(
                        $"Jumping Puzzle: laufe zum Startpunkt ({puzzleAttempts + 1}. Versuch)...",
                        $"Jumping puzzle: walking to the start point (attempt {puzzleAttempts + 1})...");
                    return;
                }

                if (sinceStart > PuzzleGoToStartTimeout)
                {
                    SkipCurrent(Loc.T("Startpunkt des Jumping Puzzles nicht erreichbar", "Jumping puzzle start point not reachable"));
                    return;
                }

                if (Plugin.IsVnavPathfindInProgress() || pathIsRunning.InvokeFunc() || sinceStart < TimeSpan.FromSeconds(0.5))
                    return;

                // Startpunkt ohne Abweichung - sonst das letzte Stück in gerader Linie mit enger
                // Toleranz nachlaufen.
                if (IsAtPuzzlePoint(playerPos, start)
                    && Vector2.Distance(new Vector2(playerPos.X, playerPos.Z), new Vector2(start.X, start.Z)) <= PuzzleStartExactTolerance)
                {
                    BeginPuzzleStep(0);
                    return;
                }

                SetExactPathTolerance(true);
                moveToPath.InvokeAction(new List<Vector3> { start }, false);
                return;
            }

            case PuzzlePhase.StepSettling:
            {
                // Kurz stillstehen, damit jeder Sprung aus dem Stand mit gleichem Anlauf beginnt.
                if (sinceStart < PuzzleStepSettleDuration || Plugin.Condition[ConditionFlag.Jumping])
                    return;

                var step = currentPuzzle.Steps[puzzleStepIndex];

                // Noch aktiven Sprint entfernen (CancelSprintBefore) - warten, bis er wirklich weg ist.
                if (step.CancelSprintBefore && !Plugin.TryCancelSprint())
                {
                    StatusText = Loc.T("Jumping Puzzle: entferne Sprint...", "Jumping puzzle: removing Sprint...");
                    puzzlePhaseStartedAt = DateTime.UtcNow;
                    return;
                }

                // Sprint auf Anweisung (SprintBefore) - bei Abklingzeit hier am Absprungpunkt warten.
                if (step.SprintBefore && !puzzleSprintUsed)
                {
                    if (!Plugin.TryUseSprintNow())
                    {
                        StatusText = Loc.T("Jumping Puzzle: warte auf Sprint...", "Jumping puzzle: waiting for Sprint...");
                        return;
                    }

                    puzzleSprintUsed = true;
                    puzzlePhaseStartedAt = DateTime.UtcNow; // kurz stehen lassen, bis Sprint wirkt
                    return;
                }

                puzzleStepFromY = playerPos.Y;

                // Folgt ein Sprung mit Anlauf: durchgehend über diesen Punkt hinaus zum Sprungziel
                // laufen, abgesprungen wird beim Überqueren (siehe StepMoving).
                // Auch über mehrere Punkte hintereinander (Kette aus RunUp-Schritten).
                var hasRunUpNext = puzzleStepIndex + 1 < currentPuzzle.Steps.Length && currentPuzzle.Steps[puzzleStepIndex + 1].RunUp;
                var path = new List<Vector3> { step.Target };
                var anyExact = step.Exact;
                for (var i = puzzleStepIndex + 1; i < currentPuzzle.Steps.Length && currentPuzzle.Steps[i].RunUp; i++)
                {
                    path.Add(currentPuzzle.Steps[i].Target);
                    anyExact |= currentPuzzle.Steps[i].Exact;
                }

                SetExactPathTolerance(anyExact);
                moveToPath.InvokeAction(path, false);
                SetPuzzlePhase(PuzzlePhase.StepMoving);
                puzzleRunUpPending = hasRunUpNext;
                StatusText = Loc.T(
                    $"Jumping Puzzle: {(step.Jump ? "Sprung" : "Laufe")} {puzzleStepIndex + 1}/{currentPuzzle.Steps.Length}...",
                    $"Jumping puzzle: {(step.Jump ? "jump" : "walk")} {puzzleStepIndex + 1}/{currentPuzzle.Steps.Length}...");
                return;
            }

            case PuzzlePhase.EvaluatingFailure:
                UpdatePuzzleFailure(playerPos, sinceStart);
                return;

            case PuzzlePhase.ReturningToStepStart:
                UpdateReturningToStepStart(playerPos, sinceStart);
                return;

            case PuzzlePhase.StepMoving:
            {
                // Anlauf für den nächsten Sprung: beim Überqueren des Absprungpunkts (Ziel dieses
                // Schritts) im Laufen abspringen - ab dann wird gegen das Sprungziel geprüft.
                // Anlauf-Kette: beim Überqueren des aktuellen Punkts ohne anzuhalten zum nächsten
                // Schritt wechseln - ist der ein Sprung, dabei abspringen. Gesprungen wird nur am
                // Boden (z.B. wenn der Anlauf selbst mit einem Sprung beginnt), auch wenn der Punkt
                // bei der Landung schon knapp überquert wurde.
                while (puzzleRunUpPending)
                {
                    var takeoff = currentPuzzle.Steps[puzzleStepIndex].Target;
                    var next = currentPuzzle.Steps[puzzleStepIndex + 1];
                    var player2 = new Vector2(playerPos.X, playerPos.Z);
                    var takeoff2 = new Vector2(takeoff.X, takeoff.Z);
                    var passedTakeoff = Vector2.Dot(new Vector2(next.Target.X, next.Target.Z) - takeoff2, player2 - takeoff2) > 0f;
                    var jumpDistance = currentPuzzle.Steps[puzzleStepIndex].Exact ? PuzzleFinalPreciseTolerance : PuzzleRunUpJumpDistance;
                    if (Vector2.Distance(player2, takeoff2) > jumpDistance && !passedTakeoff)
                        break;

                    var ownJumpPending = currentPuzzle.Steps[puzzleStepIndex].Jump && !currentPuzzle.Steps[puzzleStepIndex].RunUp && !puzzleJumpSent;
                    if (next.Jump && (ownJumpPending || Plugin.Condition[ConditionFlag.Jumping]))
                        break;

                    puzzleStepIndex++;
                    puzzleStepFromY = MathF.Min(puzzleStepFromY, playerPos.Y);
                    puzzleJumpSent = true;
                    puzzleRunUpPending = puzzleStepIndex + 1 < currentPuzzle.Steps.Length && currentPuzzle.Steps[puzzleStepIndex + 1].RunUp;
                    if (next.Jump)
                    {
                        Plugin.TryJump();
                        StatusText = Loc.T(
                            $"Jumping Puzzle: Sprung mit Anlauf {puzzleStepIndex + 1}/{currentPuzzle.Steps.Length}...",
                            $"Jumping puzzle: running jump {puzzleStepIndex + 1}/{currentPuzzle.Steps.Length}...");
                    }
                }

                var step = currentPuzzle.Steps[puzzleStepIndex];
                if (step.Jump && !step.RunUp && !puzzleJumpSent && sinceStart >= PuzzleJumpDelay)
                {
                    puzzleJumpSent = true;
                    Plugin.TryJump();
                }

                // Heruntergefallen - von vorne.
                if (playerPos.Y < MathF.Min(puzzleStepFromY, step.Target.Y) - PuzzleFallMargin)
                {
                    FailPuzzleAttempt($"Absturz bei Schritt {puzzleStepIndex + 1}");
                    return;
                }

                var inAir = Plugin.Condition[ConditionFlag.Jumping];
                if (pathIsRunning.InvokeFunc() || inAir || sinceStart < TimeSpan.FromSeconds(0.3))
                {
                    // Läuft gegen eine Wand/Kante (kein Vorankommen mehr, obwohl der Laufweg noch
                    // aktiv ist) - sofort als Fehlversuch werten statt bis zum Timeout weiterzulaufen.
                    if (!inAir && Vector3.Distance(playerPos, puzzleProgressPos) > PuzzleProgressMinDistance)
                    {
                        puzzleProgressPos = playerPos;
                        puzzleProgressAt = DateTime.UtcNow;
                    }
                    else if (!inAir && DateTime.UtcNow - puzzleProgressAt > PuzzleStuckDuration)
                    {
                        FailPuzzleAttempt($"Schritt {puzzleStepIndex + 1}: festgelaufen");
                        return;
                    }

                    if (sinceStart > PuzzleStepTimeout)
                        FailPuzzleAttempt($"Zeitüberschreitung bei Schritt {puzzleStepIndex + 1}");
                    return;
                }

                // Laufweg zu Ende, aber der Sprung mit Anlauf wurde nie ausgelöst (z.B. stehen
                // geblieben) - nicht als Erfolg werten, sondern neu versuchen.
                if (puzzleRunUpPending)
                {
                    FailPuzzleAttempt($"Sprung mit Anlauf nach Schritt {puzzleStepIndex + 1} nicht ausgelöst");
                    return;
                }

                if (!IsAtPuzzlePoint(playerPos, step.Target))
                {
                    FailPuzzleAttempt($"Schritt {puzzleStepIndex + 1} nicht erreicht");
                    return;
                }

                puzzleStepRetries = 0;
                if (puzzleStepIndex + 1 < currentPuzzle.Steps.Length)
                {
                    BeginPuzzleStep(puzzleStepIndex + 1);
                    return;
                }

                // Letzter Punkt erreicht - noch exakt draufstellen (sonst steht er u.U. knapp neben
                // der Kugel und der Punkt schaltet nicht frei).
                BeginFinalPrecisePosition(currentPuzzle.ExactStand ?? step.Target);
                return;
            }

            case PuzzlePhase.FinalPrecisePosition:
            {
                var target = currentPuzzle.ExactStand ?? currentPuzzle.Steps[^1].Target;
                if (pathIsRunning.InvokeFunc() || Plugin.Condition[ConditionFlag.Jumping] || sinceStart < TimeSpan.FromSeconds(0.3))
                {
                    if (sinceStart < PuzzleFinalPreciseTimeout)
                        return;
                    StopPath();
                }

                // Abgestürzt (vom kleinen Plateau gerutscht) - Puzzle neu.
                if (playerPos.Y < target.Y - PuzzleFallMargin)
                {
                    RestorePathTolerance();
                    RestartPuzzleFromStart();
                    return;
                }

                RestorePathTolerance();
                Plugin.Log.Info($"[SightseeingAutomation] Jumping Puzzle {currentTargetEntry.Name} geschafft (Abweichung {Vector2.Distance(new Vector2(playerPos.X, playerPos.Z), new Vector2(target.X, target.Z)):F2}).");
                hasEnsuredExactPosition = true;
                didFinalApproach = true;
                dismountedAt = DateTime.UtcNow - DismountSettleDelay;
                state = State.WaitingForUnlock;
                stateEnteredAt = DateTime.UtcNow;
                StatusText = Loc.T($"Warte auf Freischaltung: {currentTargetEntry.Name}...", $"Waiting to unlock: {currentTargetEntry.Name}...");
                return;
            }
        }
    }

    private void BeginPuzzleStep(int index)
    {
        puzzleStepIndex = index;
        puzzleSprintUsed = false;
        SetPuzzlePhase(PuzzlePhase.StepSettling);
    }

    private void FailPuzzleAttempt(string reason)
    {
        StopPath();
        Plugin.Log.Info($"[SightseeingAutomation] Jumping Puzzle {currentTargetEntry?.Name}: {reason}.");

        // Erst landen lassen, dann entscheiden: noch oben am Absprungpunkt -> denselben Schritt
        // wiederholen, sonst (abgestürzt) vom Startpunkt neu.
        SetPuzzlePhase(PuzzlePhase.EvaluatingFailure);
    }

    // Absprungpunkt des aktuellen Schritts: Startpunkt bzw. Ziel des vorherigen Schritts.
    private Vector3 CurrentStepFromPoint() =>
        puzzleStepIndex == 0 ? currentPuzzle!.Start : currentPuzzle!.Steps[puzzleStepIndex - 1].Target;

    private void UpdatePuzzleFailure(Vector3 playerPos, TimeSpan sinceStart)
    {
        if (sinceStart < TimeSpan.FromSeconds(0.3) || Plugin.Condition[ConditionFlag.Jumping])
            return;

        var from = CurrentStepFromPoint();
        var stillOnPlatform = playerPos.Y >= from.Y - PuzzleStepRetryHeightMargin
                              && Vector2.Distance(new Vector2(playerPos.X, playerPos.Z), new Vector2(from.X, from.Z)) <= PuzzleStepRetryRadius;
        if (stillOnPlatform && puzzleStepRetries < MaxPuzzleStepRetries)
        {
            puzzleStepRetries++;
            Plugin.Log.Info($"[SightseeingAutomation] Jumping Puzzle: wiederhole Schritt {puzzleStepIndex + 1} ({puzzleStepRetries}. Wiederholung).");

            // Sprung mit Anlauf: den Anlauf (vorherigen Schritt) komplett wiederholen, nicht vom
            // Absprungpunkt aus dem Stand springen.
            // Bei einer Anlauf-Kette bis zu deren Anfang zurück.
            if (currentPuzzle!.Steps[puzzleStepIndex].RunUp && puzzleStepIndex > 0)
            {
                while (currentPuzzle.Steps[puzzleStepIndex].RunUp && puzzleStepIndex > 0)
                    puzzleStepIndex--;
                from = CurrentStepFromPoint();
            }

            // Tiefer als diese Höhe während des Zurücklaufens = abgestürzt.
            puzzleReturnFloorY = MathF.Min(playerPos.Y, from.Y) - PuzzleStepRetryHeightMargin;
            SetExactPathTolerance(puzzleStepIndex > 0 && currentPuzzle.Steps[puzzleStepIndex - 1].Exact);
            moveToPath.InvokeAction(new List<Vector3> { from }, false);
            SetPuzzlePhase(PuzzlePhase.ReturningToStepStart);
            StatusText = Loc.T(
                $"Jumping Puzzle: wiederhole Schritt {puzzleStepIndex + 1} ({puzzleStepRetries}. Wiederholung)...",
                $"Jumping puzzle: retrying step {puzzleStepIndex + 1} (retry {puzzleStepRetries})...");
            return;
        }

        RestartPuzzleFromStart();
    }

    private void UpdateReturningToStepStart(Vector3 playerPos, TimeSpan sinceStart)
    {
        var from = CurrentStepFromPoint();
        if (playerPos.Y < puzzleReturnFloorY)
        {
            RestartPuzzleFromStart();
            return;
        }

        if (pathIsRunning.InvokeFunc() || sinceStart < TimeSpan.FromSeconds(0.3))
        {
            if (sinceStart > PuzzleStepTimeout)
                RestartPuzzleFromStart();
            return;
        }

        if (IsAtPuzzlePoint(playerPos, from))
            BeginPuzzleStep(puzzleStepIndex);
        else
            RestartPuzzleFromStart();
    }

    private void RestartPuzzleFromStart()
    {
        StopPath();
        RestorePathTolerance();
        puzzleAttempts++;
        puzzleStepRetries = 0;
        Plugin.Log.Info($"[SightseeingAutomation] Jumping Puzzle {currentTargetEntry?.Name}: von vorne - Versuch {puzzleAttempts}/{MaxPuzzleAttempts}.");
        if (puzzleAttempts >= MaxPuzzleAttempts)
        {
            SkipCurrent(Loc.T("Jumping Puzzle zu oft fehlgeschlagen", "Jumping puzzle failed too often"));
            return;
        }

        SetPuzzlePhase(PuzzlePhase.Dismounting);
    }

    private static bool IsAtPuzzlePoint(Vector3 playerPos, Vector3 point) =>
        Vector2.Distance(new Vector2(playerPos.X, playerPos.Z), new Vector2(point.X, point.Z)) <= PuzzlePointTolerance
        && MathF.Abs(playerPos.Y - point.Y) <= PuzzlePointTolerance;
}
