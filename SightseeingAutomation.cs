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
    }

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);
    private const float ArrivalTolerance = 4f;

    // Sightseeing-Punkte schalten offenbar erst frei, wenn man wirklich GENAU auf der animierten
    // Kugel steht, nicht nur grob in der Nähe (anders als z.B. Aetheryten mit echtem Klick-Radius) -
    // nach dem groben Laufweg (der über den navmesh-genähten "floorPoint" nur die Erreichbarkeit
    // sicherstellt, siehe StartMovingTo) folgt daher ein zweiter, viel engerer Laufauftrag direkt zur
    // echten geloggten Position, siehe BeginFinalApproach.
    private const float FinalApproachTolerance = 0.75f;

    // Ankunftstoleranz beim Zwischenstopp an einem Aethernetz-Kristall (siehe BeginWalkToLocalAethernet)
    // - dieselben Werte wie AetheryteAutomation.SmallAetheryteFinalApproachDistance/
    // BigAetheryteFinalApproachDistance: eng genug, um wirklich in Aethernetz-Reichweite zu stehen
    // (statt "in der Nähe" daran vorbeizulaufen), aber nicht so eng, dass vnavmesh gegen den
    // Kristallsockel selbst läuft.
    private const float SmallAetheryteArrivalTolerance = 3.5f;
    private const float BigAetheryteArrivalTolerance = 6f;

    private const float SprintDisableDistance = 8f;
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
    private bool hasSentEmote;
    private bool didFinalApproach;
    private DateTime? districtTravelFinishedAt;

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

        currentTargetEntry = entry;
        hasSentEmote = false;
        didFinalApproach = false;

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
            StatusText = Loc.T("Warte auf vnavmesh-Navmesh für diese Zone...", "Waiting for vnavmesh's navmesh for this zone...");
            return;
        }

        // Von Hand hinterlegter Zwischenstopp (siehe Plugin.SightseeingApproachWaypoints) hat Vorrang
        // vor dem Karten-Flagge-Umweg - für Punkte, bei denen selbst der darüber gefundene grobe Punkt
        // noch gegen eine Wand/ein Geländer führt. Der eigentliche letzte, enge Schritt zur echten
        // Position passiert unverändert danach über BeginFinalApproach (siehe UpdateMoving).
        if (Plugin.TryGetSightseeingApproachWaypoint(entry.Id, out var waypoint))
        {
            currentTargetPosition = waypoint;
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
            if (Vector3.Distance(playerPos, currentTargetPosition) > SprintDisableDistance)
                Plugin.TryUseSprint();

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
        if (hasSeenPathRunning || DateTime.UtcNow - stateEnteredAt > PathStartGracePeriod)
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
            districtTravelFinishedAt = null;
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
            if (!didFinalApproach)
            {
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

        // Bereits aufgezeichnet ODER Simulation-Modus aktiv (siehe Configuration.
        // SimulateSightseeingAutomation - dort bewusst NIE ein Emote senden, auch nicht bei einem
        // noch nicht aufgezeichneten Punkt: der Modus dient nur zum Testen von Laufweg/Ankunfts-
        // position, nicht zum tatsächlichen Abschließen) - kein Emote senden, stattdessen nur kurz
        // stehen bleiben und weiter zum nächsten Punkt.
        if (Plugin.IsAdventureComplete(currentTargetEntry.Id) || Plugin.SimulateSightseeingAutomation)
        {
            if (DateTime.UtcNow - stateEnteredAt > AlreadyCompleteLingerDuration)
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
