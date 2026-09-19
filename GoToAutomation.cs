using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace AllTheThings;

/// <summary>
/// Läuft (auf Klick des "Hinlaufen"-Icons neben einem verlinkten Eintrag, siehe
/// CompactOverlayWindow.DrawClickableName) einmalig zum Fundort EINES einzelnen Eintrags
/// (Händler/Quest-Vergabeort/Aetheryte) - anders als AetheryteAutomation/QuestAutomation keine
/// Dauerschleife über mehrere Einträge, sondern nur ein einzelner Laufauftrag, der auch wieder
/// per erneutem Klick auf dasselbe Icon abgebrochen werden kann. Nutzt für die eigentliche
/// Bewegung dieselben Fremdplugins (vnavmesh zum Laufen, Lifestream nur für den Bezirkswechsel in
/// geteilten Hauptstädten) und denselben Fliegen/Reiten/Laufen-Rufmechanismus wie die
/// Aetheryten-Automation (siehe Plugin.TryRequestAetheryteMount).
/// </summary>
public sealed class GoToAutomation
{
    private enum State
    {
        Idle,
        TravelingToDistrict,
        Mounting,
        MovingTo,
    }

    // Wie lange maximal aufs Aufsteigen gewartet wird, bevor trotzdem zu Fuß weitergemacht wird -
    // siehe AetheryteAutomation.MountWaitTimeout (gleiche Begründung).
    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);

    // Ankunftstoleranz für vnavmesh - bewusst großzügig wie AetheryteAutomation.PathTolerance:
    // die aus der Karten-Flagge gewonnene Position kann knapp außerhalb der Navmesh-Abdeckung
    // liegen (z.B. hinter einer Tür), eine zu enge Toleranz würde vnavmesh den Auftrag sonst
    // komplett ablehnen lassen.
    private const float PathTolerance = 10f;

    // Innerhalb dieser Entfernung (Yalms) zum Ziel wird kein Sprint mehr benutzt - siehe
    // AetheryteAutomation.SprintDisableDistance (gleiche Begründung).
    private const float SprintDisableDistance = 8f;

    // Absolute Notbremse für den Laufweg - siehe AetheryteAutomation.StepMaxDuration.
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DistrictTravelTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DistrictTravelSettleDelay = TimeSpan.FromSeconds(2);

    // dest, fly, ankunftstoleranz(Yalms) -> ob der Aufruf angenommen wurde.
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> navmeshIsReady;

    // Kein Parameter -> begehbarer Punkt zur aktuell auf der Ingame-Karte gesetzten Flagge (oder
    // null) - siehe AetheryteAutomation.queryFlagToPoint. Wird für BEIDE Positionsarten genutzt
    // (Kartenkoordinate oder rohe Weltposition, siehe BeginNavigateToEntry/Plugin.OpenEntryMap),
    // da diese Einträge (anders als Aetheryten) keine MapMarker-Weltposition haben, über die
    // vnavmesh sonst direkt per PointOnFloor abgefragt werden könnte.
    private readonly ICallGateSubscriber<Vector3?> queryFlagToPoint;

    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleport;
    private readonly ICallGateSubscriber<uint, bool> lifestreamAethernetTeleportById;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;
    private readonly ICallGateSubscriber<object> lifestreamAbort;

    private State state = State.Idle;
    private CollectibleEntry? pendingEntry;
    private uint? currentEntryId;

    // IDs werden pro Sammelobjekt-Datenquelle unabhängig vergeben (Mount/Minion/Aetheryte/Quest/
    // HuntingLog fangen alle wieder bei 1 an) - ohne den Typ mit zu vergleichen würde z.B. ein
    // Mount-Eintrag #1 fälschlich als "aktiv" gelten, während eigentlich zu Hunting-Log-Ziel #1
    // (RowId aus MonsterNoteTarget) gelaufen wird.
    private CollectibleType? currentEntryType;
    private string currentEntryName = string.Empty;
    private Vector3 currentTargetPosition;
    private DateTime stateEnteredAt;
    private bool hasSeenPathRunning;
    private DateTime? districtTravelFinishedAt;

    public GoToAutomation()
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

    /// <summary>
    /// Beide Fremdplugins werden gebraucht (vnavmesh zum Laufen, Lifestream für den eventuell
    /// nötigen Bezirkswechsel) - fehlt eines, wird das Icon im Overlay ausgegraut statt einen
    /// Klick anzunehmen, der dann doch nicht zuverlässig ans Ziel führen könnte.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            return pathfindAndMoveCloseTo.HasFunction && pathIsRunning.HasFunction && navmeshIsReady.HasFunction
                   && lifestreamTeleport.HasFunction && lifestreamIsBusy.HasFunction;
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

    public bool IsNavigatingTo(CollectibleEntry entry) => currentEntryType == entry.Type && currentEntryId == entry.Id;

    /// <summary>
    /// Startet den Laufauftrag zu diesem Eintrag - bricht dafür zuerst einen eventuell schon
    /// laufenden Auftrag zu einem ANDEREN Eintrag ab (immer nur einer gleichzeitig).
    /// </summary>
    public void GoTo(CollectibleEntry entry)
    {
        if (!entry.HasGoToTarget)
            return;

        Cancel();

        pendingEntry = entry;
        currentEntryId = entry.Id;
        currentEntryType = entry.Type;
        currentEntryName = entry.Name;
        state = State.Idle;
        stateEnteredAt = DateTime.UtcNow;

        try
        {
            var targetTerritory = entry.FlagTerritoryTypeId ?? entry.TerritoryTypeId;
            var currentTerritory = Plugin.ResolveEffectiveTerritoryId(Plugin.ClientState.TerritoryType);

            if (targetTerritory != currentTerritory)
                TryTravelToDistrict(entry, targetTerritory);
            else
                BeginNavigateToEntry(entry);
        }
        catch (Exception ex)
        {
            // Anders als der Aufruf aus Update() (siehe dort) läuft dieser hier direkt aus dem
            // Klick-Handler heraus, ohne dessen try/catch - ohne dieses hier würde ein Fehler an
            // dieser Stelle (z.B. eine abgelehnte vnavmesh-IPC-Anfrage) sonst lautlos den Klick
            // wirkungslos verpuffen lassen, ganz ohne Log-Zeile.
            Plugin.Log.Error(ex, $"[GoToAutomation] GoTo({entry.Name}): Fehler beim Starten - abgebrochen.");
            Cancel();
        }
    }

    /// <summary>Bricht einen laufenden Auftrag (egal in welcher Phase) sofort ab.</summary>
    public void Cancel()
    {
        if (currentEntryId == null)
            return;

        StopPath();
        StopLifestream();
        state = State.Idle;
        pendingEntry = null;
        currentEntryId = null;
        currentEntryType = null;
        districtTravelFinishedAt = null;
    }

    private void Finish()
    {
        state = State.Idle;
        pendingEntry = null;
        currentEntryId = null;
        currentEntryType = null;
        districtTravelFinishedAt = null;
    }

    /// <summary>Muss jeden Frame (während das Overlay offen ist) aufgerufen werden.</summary>
    public void Update()
    {
        if (currentEntryId == null)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    if (pendingEntry != null)
                        BeginNavigateToEntry(pendingEntry);
                    break;

                case State.TravelingToDistrict:
                    UpdateTravelingToDistrict();
                    break;

                case State.Mounting:
                    UpdateMounting();
                    break;

                case State.MovingTo:
                    UpdateMoving();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Hinlaufen zu einem Eintrag - abgebrochen.");
            Cancel();
        }
    }

    private void BeginNavigateToEntry(CollectibleEntry entry)
    {
        var navReady = navmeshIsReady.InvokeFunc();
        if (!navReady)
            // Noch nicht bereit (z.B. Navmesh dieser Zone lädt noch) - beim nächsten Update-Tick
            // (State bleibt Idle) einfach erneut versuchen, statt endgültig aufzugeben.
            return;

        // Einheitlich für BEIDE Positionsarten (Kartenkoordinate wie bei Händlern/Quests/
        // Aetheryten, oder rohe Weltposition wie bei Hunting-Log-Monstern): die Karten-Flagge auf
        // die Zielposition setzen (Plugin.OpenEntryMap rechnet eine Weltposition dafür intern über
        // MapUtil.WorldToMap in eine Kartenkoordinate um) und vnavmesh nach einem begehbaren Punkt
        // in deren Nähe fragen - genau derselbe Trick wie "/vnav moveflag" bzw.
        // AetheryteAutomation.StartMovingTo. Zuverlässiger als eine direkte PointOnFloor-Abfrage
        // auf der rohen Position, weil die Karten-Flagge intern großzügiger auf die Navmesh
        // gerastert wird (u.a. wichtig, da Hunting-Log-Weltpositionen mangels bekannter
        // Geländehöhe immer mit Y=0 gerechnet sind, siehe HuntingLogPositions.cs).
        Plugin.OpenEntryMap(entry);
        var floorPoint = queryFlagToPoint.InvokeFunc();
        if (floorPoint == null)
        {
            Plugin.Log.Info($"[GoToAutomation] BeginNavigateToEntry({entry.Name}): FlagToPoint() liefert keinen begehbaren Punkt - abgebrochen.");
            Finish();
            return;
        }

        currentTargetPosition = floorPoint.Value;

        // Mount rufen (falls in den QoL-Einstellungen ausgewählt und noch nicht beritten) - der
        // eigentliche Laufauftrag geht erst raus, nachdem entweder aufgestiegen wurde oder das
        // Aufsteigen aufgegeben wurde (siehe UpdateMounting), sonst würde vnavmesh mitten in der
        // Aufstiegs-Animation losschicken wollen.
        if (Plugin.TryRequestAetheryteMount())
        {
            state = State.Mounting;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        BeginPathfind();
    }

    /// <summary>
    /// Beritten wird zuerst Fliegen versucht (schneller, sofern Zone/Mount es erlauben), lehnt
    /// vnavmesh das ab, mit demselben Mount stattdessen am Boden geritten - siehe
    /// AetheryteAutomation.BeginPathfind (identische Logik).
    /// </summary>
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
            Plugin.Log.Info($"[GoToAutomation] BeginPathfind({currentEntryName}): vnavmesh lehnt den Laufweg ab - abgebrochen.");
            Finish();
            return;
        }

        state = State.MovingTo;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
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

    /// <summary>
    /// Reist per Lifestream in den Bezirk des Ziels - erst kostenlos per Aethernetz zu irgendeinem
    /// dort schon freigeschalteten Punkt, sonst per bezahlter Teleport-Aktion zum großen
    /// Aetheryten. Klappt keins von beidem, wird abgebrochen statt endlos zu warten - siehe
    /// AetheryteAutomation.TryTravelToDistrict (identische Logik, nur für ein einzelnes Ziel).
    /// </summary>
    private void TryTravelToDistrict(CollectibleEntry entry, uint targetTerritory)
    {
        if (!IsLifestreamAvailable())
        {
            Plugin.Log.Info($"[GoToAutomation] TryTravelToDistrict({entry.Name}): Lifestream nicht gefunden - abgebrochen.");
            Finish();
            return;
        }

        var anyUnlockedId = Plugin.FindAnyUnlockedAetheryteInTerritory(targetTerritory);
        if (anyUnlockedId != null && lifestreamAethernetTeleportById.InvokeFunc(anyUnlockedId.Value))
        {
            state = State.TravelingToDistrict;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        var mainAetheryteId = Plugin.FindUnlockedMainAetheryteId(targetTerritory);
        var accepted = mainAetheryteId.HasValue && lifestreamTeleport.InvokeFunc(mainAetheryteId.Value, (byte)0);
        if (!accepted)
        {
            Plugin.Log.Info($"[GoToAutomation] TryTravelToDistrict({entry.Name}): weder Aethernetz noch Teleport möglich - abgebrochen.");
            Finish();
            return;
        }

        state = State.TravelingToDistrict;
        stateEnteredAt = DateTime.UtcNow;
    }

    private void UpdateTravelingToDistrict()
    {
        if (!lifestreamIsBusy.InvokeFunc())
        {
            // Kurz warten, bis Plugin.ClientState.TerritoryType tatsächlich auf die neue Zone
            // aktualisiert ist - siehe AetheryteAutomation.DistrictTravelSettleDelay.
            districtTravelFinishedAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - districtTravelFinishedAt.Value < DistrictTravelSettleDelay)
                return;

            districtTravelFinishedAt = null;
            state = State.Idle;
            return;
        }

        districtTravelFinishedAt = null;
        if (DateTime.UtcNow - stateEnteredAt > DistrictTravelTimeout)
        {
            Plugin.Log.Info($"[GoToAutomation] UpdateTravelingToDistrict({currentEntryName}): Reise dauert zu lange - abgebrochen.");
            StopLifestream();
            Finish();
        }
    }

    private void UpdateMoving()
    {
        if (pathIsRunning.InvokeFunc())
        {
            hasSeenPathRunning = true;

            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            if (Vector3.Distance(playerPos, currentTargetPosition) > SprintDisableDistance)
                Plugin.TryUseSprint();

            if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
            {
                Plugin.Log.Info($"[GoToAutomation] UpdateMoving({currentEntryName}): dauert zu lange - abgebrochen.");
                StopPath();
                Finish();
            }

            return;
        }

        if (hasSeenPathRunning)
        {
            // Angekommen (oder feststeckend - vnavmesh hat in beiden Fällen selbst gestoppt).
            Finish();
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > PathStartGracePeriod)
        {
            Plugin.Log.Info($"[GoToAutomation] UpdateMoving({currentEntryName}): nie sichtbar losgelaufen - abgebrochen.");
            Finish();
        }
    }
}
