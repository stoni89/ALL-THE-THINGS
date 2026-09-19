using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Ipc;

namespace AllTheThings;

/// <summary>
/// Läuft nacheinander alle aktuell fehlenden Aetheryten/Aethernetz-Kristalle ab und interagiert mit
/// ihnen, um sie freizuschalten - mit Hilfe des Fremdplugins "vnavmesh" für die Laufweg-Findung
/// innerhalb einer Zone. In geteilten Hauptstädten (Ul'dah, Limsa, Gridania, Ishgard) wechselt sie bei
/// Bedarf per "Lifestream" auch zwischen den Bezirken (siehe TryTravelToDistrict) - außerhalb davon
/// bleibt sie auf die aktuelle Zone begrenzt, kein beliebiger Zonenwechsel im ganzen Spiel.
/// </summary>
public sealed class AetheryteAutomation
{
    private enum State
    {
        Idle,
        MovingTo,
        Interacting,
        TravelingToDistrict,
    }

    // Ab dieser Entfernung (Yalms) zum Kristall wird die Bewegung gestoppt und interagiert.
    private const float InteractDistance = 3.5f;

    // Innerhalb dieser Entfernung (Yalms) zum Ziel wird kein Sprint mehr benutzt - wird Sprint
    // genau in dem Moment aktiviert, in dem vnavmesh eigentlich anhalten und den Aetheryten
    // anklicken will, verpasst der Klick das Objekt und die Automation bleibt ohne erkennbaren
    // Grund stehen.
    private const float SprintDisableDistance = 8f;

    // Toleranz, die vnavmesh für PathfindAndMoveCloseTo bekommt - bewusst großzügiger als
    // InteractDistance: manche Kristalle stehen in Gebäude-Innenräumen, deren genaue Position
    // (aus dem MapMarker-Sheet) knapp außerhalb der von der Navmesh abgedeckten Fläche liegen kann
    // (z.B. hinter einer Tür) - mit einer zu engen Toleranz lehnt vnavmesh den Laufauftrag dann
    // komplett ab, statt wenigstens bis zur Tür zu laufen.
    private const float PathTolerance = 10f;

    // Wie lange ohne messbaren Fortschritt (Distanz zum Ziel wird nicht kleiner) gewartet wird,
    // bevor der aktuelle Kristall als "festgefahren" übersprungen wird. Bewusst NICHT als feste
    // Gesamt-Laufzeit gedacht - manche Ziele liegen 200+ Yalm entfernt und brauchen dafür allein
    // schon deutlich mehr als 30s, obwohl vnavmesh die ganze Zeit sichtbar vorwärtskommt.
    private static readonly TimeSpan StepStallTimeout = TimeSpan.FromSeconds(15);

    // Absolute Notbremse für einen einzelnen Laufweg, falls Fortschritt zwar (minimal) gemessen
    // wird, das Ziel aber trotzdem nie in vertretbarer Zeit erreicht wird (z.B. vnavmesh pendelt
    // um ein Hindernis).
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(3);

    // Wie lange nach der Interaktion auf den tatsächlichen Freischalt-Abschluss gewartet wird
    // (der "Entdecken"-Cast braucht ein paar Sekunden) - danach gilt der Versuch als gescheitert.
    private static readonly TimeSpan UnlockWaitTimeout = TimeSpan.FromSeconds(15);

    // Wie lange maximal auf eine Lifestream-Reise in einen Nachbarbezirk gewartet wird (Ladebildschirm
    // + eventuelles eigenes Laufen von Lifestream zum Ziel-Aetheryten).
    private static readonly TimeSpan DistrictTravelTimeout = TimeSpan.FromSeconds(60);

    // Kurze Gnadenfrist beim Interagieren, falls das Weltobjekt trotz Ankunft am Ziel noch nicht
    // in der Objekttabelle auftaucht (normalerweise höchstens ein paar Frames Verzögerung).
    private static readonly TimeSpan InteractObjectGracePeriod = TimeSpan.FromSeconds(5);

    // Verhindert eine Endlosschleife, falls ein Kristall auch nach erfolgreicher Ankunft +
    // Interaktion nicht als freigeschaltet erkannt wird (z.B. Interaktion hat aus irgendeinem
    // Grund nicht gegriffen) - nach so vielen Versuchen wird derselbe Kristall überspringen.
    private const int MaxAttemptsPerAetheryte = 2;
    private readonly Dictionary<uint, int> attemptCounts = new();

    // dest, fly, ankunftstoleranz(Yalms) -> ob der Aufruf angenommen wurde (false z.B. wenn schon
    // eine Bewegung läuft).
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> navmeshIsReady;

    // point, allowUnlandable, halfExtentXZ -> Punkt auf dem Boden (oder null). Die aus dem
    // MapMarker-Sheet berechnete Zielposition hat keine echte Höhe (Y wird auf 0 gesetzt) - ohne
    // diese Korrektur findet vnavmesh oft gar keinen Pfad (Y liegt z.B. mitten in der Wand/Luft)
    // und bleibt für immer bei "noch nicht gestartet" stehen, ohne Fehlermeldung.
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> queryPointOnFloor;

    // Kein Parameter -> begehbarer Punkt zur aktuell auf der Ingame-Karte gesetzten Flagge (oder
    // null). Genau das, was auch "/vnav moveflag" nutzt - manchmal findet dieser Weg (der über die
    // Karten-Flagge geht) einen begehbaren Punkt, den die reine Koordinatensuche (PointOnFloor)
    // nicht findet, z.B. bei Kristallen in Gebäude-Innenräumen.
    private readonly ICallGateSubscriber<Vector3?> queryFlagToPoint;

    // aetheryteId, subIndex(0 = großer Aetheryte) -> angenommen? Volle Teleport-Aktion (Ladebildschirm).
    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleport;

    // Aetheryte-RowId -> angenommen? Aethernetz-Sprung (kein Ladebildschirm, aber nur innerhalb
    // derselben Stadt/desselben Netzwerks und nur zu bereits bekannten Zielen) - braucht man in
    // Städten wie Ul'dah, die gar keinen eigenen großen Aetheryten pro Bezirk haben (siehe
    // TryTravelToDistrict).
    private readonly ICallGateSubscriber<uint, bool> lifestreamAethernetTeleportById;

    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;
    private readonly ICallGateSubscriber<object> lifestreamAbort;

    private State state = State.Idle;
    private uint? currentTargetId;
    private Vector3 currentTargetPosition;
    private float currentArrivalTolerance = InteractDistance;
    private DateTime stateEnteredAt;
    private bool hasInteractedThisCycle;
    private bool hasSeenPathRunning;
    private float lastProgressDistance;
    private DateTime lastProgressAtUtc;
    private DateTime? interactObjectNotFoundSince;
    private DateTime? districtTravelFinishedAt;
    private readonly HashSet<uint> skippedIds = new();

    // Nach "Lifestream.IsBusy() == false" kann es noch einen Moment dauern, bis Plugin.ClientState.
    // TerritoryType (und andere Zonen-Metadaten) tatsächlich auf die neue Zone aktualisiert sind -
    // ohne diese kurze Verzögerung hält TryStartNext die Zone noch für die alte und schickt die
    // Automation im Kreis (immer wieder zum selben, längst erreichten Aethernetz-Punkt).
    private static readonly TimeSpan DistrictTravelSettleDelay = TimeSpan.FromSeconds(2);

    // Wie lange nach dem Auftrag gewartet wird, bis vnavmesh die Bewegung tatsächlich SICHTBAR
    // startet (Path.IsRunning == true) - lange Strecken brauchen erst einen Moment für die
    // Pfadberechnung, bevor die Bewegung überhaupt beginnt. Ohne das würde "noch nicht gestartet"
    // sofort fälschlich als "schon fertig/steckt fest" gewertet.
    private static readonly TimeSpan PathStartGracePeriod = TimeSpan.FromSeconds(5);

    // Jede Statusänderung bleibt danach noch eine Weile sichtbar (auch nachdem IsActive schon
    // false ist) - sonst verschwindet der eigentliche Grund für ein Stoppen/Überspringen sofort
    // wieder, bevor man ihn lesen kann ("er macht nix", ohne zu sehen warum).
    private static readonly TimeSpan StatusLingerDuration = TimeSpan.FromSeconds(8);
    private string statusText = string.Empty;
    private DateTime statusSetAt = DateTime.MinValue;

    public bool IsActive { get; private set; }

    /// <summary>
    /// Debug/Test-Schalter (nicht gespeichert, immer false nach Neustart): läuft auch bereits
    /// freigeschaltete Kristalle mit ab, um den Laufweg/die Reihenfolge zu überprüfen, ohne den
    /// eigenen Fortschritt zurücksetzen zu müssen. Siehe MainWindow-Debug-Tab.
    /// </summary>
    public bool SimulateAllCrystals { get; set; }

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

    public AetheryteAutomation()
    {
        pathfindAndMoveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        navmeshIsReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        queryPointOnFloor = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
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

    /// <summary>
    /// Nur für den Bezirkswechsel in geteilten Hauptstädten nötig, nicht für die Grundfunktion -
    /// fehlt Lifestream, wird einfach auf den aktuellen Bezirk beschränkt (siehe TryTravelToDistrict).
    /// </summary>
    public bool IsLifestreamAvailable()
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
            // "Path.Stop" ist eine reine Action ohne Rückgabewert - dafür muss HasAction geprüft
            // werden, nicht HasFunction (das gilt nur für Endpunkte MIT Rückgabewert). Mit
            // HasFunction hier wäre der Stopp-Aufruf immer lautlos übersprungen worden.
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
        currentTargetId = null;
        skippedIds.Clear();
        attemptCounts.Clear();
        interactObjectNotFoundSince = null;
        districtTravelFinishedAt = null;
        StatusText = Loc.T("Automation gestartet...", "Automation started...");
    }

    public void Stop()
    {
        IsActive = false;
        state = State.Idle;
        currentTargetId = null;
        StopPath();
        StopLifestream();
    }

    public void MarkUnavailable()
    {
        StatusText = Loc.T("vnavmesh nicht gefunden - bitte installieren.", "vnavmesh not found - please install it.");
    }

    /// <summary>
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell fehlenden Aetheryten/
    /// Kristallen der Zone aufgerufen werden.
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> missingAetherytesInZone)
    {
        if (!IsActive)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(missingAetherytesInZone);
                    break;

                case State.MovingTo:
                    UpdateMoving();
                    break;

                case State.Interacting:
                    UpdateInteracting();
                    break;

                case State.TravelingToDistrict:
                    UpdateTravelingToDistrict();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler bei der Aetheryten-Automation - wird gestoppt.");
            StatusText = Loc.T("Fehler bei vnavmesh/Lifestream - Automation gestoppt.", "Error talking to vnavmesh/Lifestream - automation stopped.");
            Stop();
        }
    }

    private static uint HomeTerritory(CollectibleEntry entry) => entry.FlagTerritoryTypeId ?? entry.TerritoryTypeId;

    private void TryStartNext(IReadOnlyList<CollectibleEntry> missingAetherytesInZone)
    {
        var candidates = missingAetherytesInZone.Where(a => !skippedIds.Contains(a.Id)).ToList();
        Plugin.Log.Info($"[AetheryteAutomation] TryStartNext: {missingAetherytesInZone.Count} insgesamt, {candidates.Count} nicht übersprungen: " +
                         string.Join(", ", candidates.Select(c => $"{c.Name}(#{c.Id})")));
        if (candidates.Count == 0)
        {
            StatusText = Loc.T("Keine Aetheryten mehr übrig.", "No aetherytes left.");
            Stop();
            return;
        }

        // Aufgelöst (siehe Plugin.ResolveEffectiveTerritoryId), damit "Alias"-Zonen wie "Heart of
        // the Sworn" (-> Ul'dah) hier genauso wie ihre zugeordnete Stadtzone behandelt werden.
        var currentTerritory = Plugin.ResolveEffectiveTerritoryId(Plugin.ClientState.TerritoryType);
        Plugin.Log.Info($"[AetheryteAutomation] currentTerritory={currentTerritory} (real={Plugin.ClientState.TerritoryType}), playerPos={Plugin.ObjectTable.LocalPlayer?.Position}");

        // Immer den nächstgelegenen fehlenden Kristall IM AKTUELLEN Bezirk nehmen (Luftlinie zur
        // Spielerposition). Die Position kommt bewusst aus dem MapMarker-Sheet (Plugin.
        // ResolveAetheryteWorldPosition), NICHT aus der Objekttabelle - kleine Kristalle werden dort
        // erst innerhalb von ca. 15 Yalm geladen, man braucht die Position aber VOR der Ankunft,
        // um überhaupt hinlaufen zu können ("Waiting for crystals to load..." hat nie geendet).
        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        CollectibleEntry? next = null;
        Vector3 targetPosition = default;
        var bestDistance = float.MaxValue;
        var anyPositionUnresolved = false;

        foreach (var candidate in candidates)
        {
            var candidateHome = HomeTerritory(candidate);
            if (candidateHome != currentTerritory)
            {
                Plugin.Log.Info($"[AetheryteAutomation]   {candidate.Name}(#{candidate.Id}): anderer Bezirk (home={candidateHome}) - übersprungen für diesen Durchlauf.");
                continue;
            }

            var position = Plugin.ResolveAetheryteWorldPosition(candidate.Id);
            if (position == null)
            {
                Plugin.Log.Info($"[AetheryteAutomation]   {candidate.Name}(#{candidate.Id}): Position NICHT auflösbar (ResolveAetheryteWorldPosition == null).");
                anyPositionUnresolved = true;
                continue;
            }

            var distance = Vector3.Distance(playerPos, position.Value);
            Plugin.Log.Info($"[AetheryteAutomation]   {candidate.Name}(#{candidate.Id}): Position={position.Value}, Distanz={distance}");
            if (distance < bestDistance)
            {
                bestDistance = distance;
                next = candidate;
                targetPosition = position.Value;
            }
        }

        if (next != null)
        {
            Plugin.Log.Info($"[AetheryteAutomation] -> nächstes Ziel: {next.Name}(#{next.Id}) @ {targetPosition}, Distanz={bestDistance}");
            StartMovingTo(next, targetPosition);
            return;
        }

        // Nichts (mehr) im aktuellen Bezirk - liegt noch etwas in einem Nachbarbezirk derselben
        // geteilten Hauptstadt? Dann per Lifestream dorthin reisen, statt aufzugeben.
        var sameDistrictCandidateExists = candidates.Any(c => HomeTerritory(c) == currentTerritory);
        var otherDistrictCandidate = candidates.FirstOrDefault(c => HomeTerritory(c) != currentTerritory);

        if (sameDistrictCandidateExists)
        {
            // Es GIBT Kandidaten hier, nur ihre Position ließ sich nicht auflösen (z.B. fehlender
            // MapMarker-Eintrag) - dauerhaft überspringen, ein erneuter Versuch würde am selben
            // fehlenden Datensatz scheitern.
            foreach (var candidate in candidates.Where(c => HomeTerritory(c) == currentTerritory))
                skippedIds.Add(candidate.Id);

            StatusText = anyPositionUnresolved
                ? Loc.T("Übersprungen (Position nicht auflösbar)", "Skipped (could not resolve position)")
                : Loc.T("Übersprungen (keine Kristalle auffindbar)", "Skipped (no crystals found in world)");
            return;
        }

        if (otherDistrictCandidate == null)
        {
            // Weder hier noch anderswo etwas übrig (sollte durch die obige Prüfung eigentlich
            // nicht mehr vorkommen) - zur Sicherheit trotzdem beenden statt endlos zu drehen.
            foreach (var candidate in candidates)
                skippedIds.Add(candidate.Id);

            StatusText = Loc.T("Übersprungen (keine Kristalle auffindbar)", "Skipped (no crystals found in world)");
            return;
        }

        TryTravelToDistrict(otherDistrictCandidate, candidates);
    }

    private void StartMovingTo(CollectibleEntry next, Vector3 targetPosition)
    {
        var attempts = attemptCounts.GetValueOrDefault(next.Id, 0) + 1;
        attemptCounts[next.Id] = attempts;
        if (attempts > MaxAttemptsPerAetheryte)
        {
            skippedIds.Add(next.Id);
            StatusText = Loc.T($"Übersprungen (zu oft versucht): {next.Name}", $"Skipped (too many attempts): {next.Name}");
            return;
        }

        var navReady = navmeshIsReady.InvokeFunc();
        Plugin.Log.Info($"[AetheryteAutomation] StartMovingTo({next.Name}): navmeshIsReady={navReady}");
        if (!navReady)
        {
            StatusText = Loc.T("Warte auf vnavmesh-Navmesh für diese Zone...", "Waiting for vnavmesh's navmesh for this zone...");
            return;
        }

        // Eine von Hand geprüfte Position (Plugin.ManualAetheryteWorldPositions) ist bereits als
        // begehbar bekannt - direkt übernehmen, kein FlagToPoint-/PointOnFloor-Umweg nötig (und
        // vor allem keine Karte, die dafür aufspringen würde). Da diese Position schon fast genau
        // am Kristall liegt, reicht dafür die enge InteractDistance-Toleranz statt der großzügigen
        // PathTolerance (die sonst dazu führt, dass vnavmesh schon viel zu weit weg stehen bleibt).
        var isManualPosition = Plugin.HasManualAetheryteWorldPosition(next.Id);
        Vector3? floorPoint = isManualPosition ? targetPosition : null;
        var tolerance = isManualPosition ? InteractDistance : PathTolerance;

        // Sonst erst über die Karten-Flagge versuchen (genau das, was "/vnav moveflag" auch macht)
        // - dieser Weg findet manchmal einen begehbaren Punkt (z.B. an einer Ladentür), den die
        // reine Koordinatensuche unten (PointOnFloor auf der rohen MapMarker-Position) nicht
        // findet, weil die Karten-Flagge intern anders/großzügiger auf die Navmesh gerastert wird.
        if (floorPoint == null && next.HasVendorLocation)
        {
            Plugin.OpenVendorMap(next);
            floorPoint = queryFlagToPoint.InvokeFunc();
            Plugin.Log.Info($"[AetheryteAutomation] StartMovingTo({next.Name}): FlagToPoint() = {floorPoint}");
        }

        // Die aus dem MapMarker-Sheet berechnete Position hat keine echte Höhe (Y=0) - auf den
        // tatsächlichen Boden einschnappen, sonst findet vnavmesh oft gar keinen Pfad und bleibt
        // stumm bei "nicht gestartet" stehen.
        floorPoint ??= queryPointOnFloor.InvokeFunc(targetPosition, true, 50f);
        Plugin.Log.Info($"[AetheryteAutomation] StartMovingTo({next.Name}): PointOnFloor({targetPosition}, halfExtentXZ=50) = {floorPoint}");
        if (floorPoint == null)
        {
            // Kein Bodenpunkt in 50 Yalm Umkreis - die Navmesh deckt diese Stelle offenbar gar
            // nicht ab (z.B. Ladeninnenraum) statt nur knapp daneben zu liegen. Direkt aufgeben,
            // statt einen Laufauftrag ins Leere zu schicken, der ohnehin nie starten würde.
            skippedIds.Add(next.Id);
            StatusText = Loc.T(
                $"Übersprungen (von vnavmesh nicht erreichbar): {next.Name}",
                $"Skipped (not reachable by vnavmesh): {next.Name}");
            return;
        }

        var accepted = pathfindAndMoveCloseTo.InvokeFunc(floorPoint.Value, false, tolerance);
        Plugin.Log.Info($"[AetheryteAutomation] StartMovingTo({next.Name}): pathfindAndMoveCloseTo({floorPoint.Value}, tolerance={tolerance}) accepted={accepted}");
        if (!accepted)
        {
            skippedIds.Add(next.Id);
            StatusText = Loc.T($"Übersprungen (vnavmesh lehnt Laufweg ab): {next.Name}", $"Skipped (vnavmesh rejected the path): {next.Name}");
            return;
        }

        currentTargetId = next.Id;
        currentTargetPosition = floorPoint.Value;
        currentArrivalTolerance = tolerance;
        state = State.MovingTo;
        stateEnteredAt = DateTime.UtcNow;
        hasSeenPathRunning = false;
        lastProgressDistance = Vector3.Distance(Plugin.ObjectTable.LocalPlayer?.Position ?? floorPoint.Value, floorPoint.Value);
        lastProgressAtUtc = DateTime.UtcNow;
        StatusText = Loc.T($"Laufe zu: {next.Name}...", $"Walking to: {next.Name}...");
    }

    /// <summary>
    /// Reist per Lifestream zum bereits freigeschalteten großen Aetheryten des Zielbezirks -
    /// von dort übernimmt beim nächsten Idle-Durchlauf wieder vnavmesh wie gewohnt. Ohne Lifestream
    /// oder ohne einen bereits freigeschalteten Aetheryten dort werden alle Kandidaten dieses
    /// Bezirks übersprungen, statt endlos erneut zu versuchen.
    /// </summary>
    private void TryTravelToDistrict(CollectibleEntry targetCandidate, List<CollectibleEntry> allCandidates)
    {
        var targetTerritory = HomeTerritory(targetCandidate);

        void SkipWholeDistrict()
        {
            foreach (var c in allCandidates.Where(c => HomeTerritory(c) == targetTerritory))
                skippedIds.Add(c.Id);
        }

        if (!IsLifestreamAvailable())
        {
            SkipWholeDistrict();
            StatusText = Loc.T(
                "Nachbarbezirk übersprungen (Lifestream nicht gefunden)",
                "Skipped neighboring district (Lifestream not found)");
            return;
        }

        // Zuerst per Aethernetz-Sprung zu IRGENDEINEM schon freigeschalteten Punkt im Zielbezirk -
        // funktioniert auch in Städten wie Ul'dah, die gar keinen eigenen großen Aetheryten pro
        // Bezirk haben (nur einen für die ganze Stadt, physisch nur in einem Bezirk). Das braucht
        // keinen Ladebildschirm und funktioniert von jedem Aethernetz-Punkt in Reichweite aus.
        var anyUnlockedId = Plugin.FindAnyUnlockedAetheryteInTerritory(targetTerritory);
        Plugin.Log.Info($"[AetheryteAutomation] TryTravelToDistrict({targetTerritory}): FindAnyUnlockedAetheryteInTerritory = {anyUnlockedId}");
        if (anyUnlockedId != null)
        {
            var aethernetAccepted = lifestreamAethernetTeleportById.InvokeFunc(anyUnlockedId.Value);
            Plugin.Log.Info($"[AetheryteAutomation] TryTravelToDistrict: AethernetTeleportById({anyUnlockedId.Value}) accepted={aethernetAccepted}");
            if (aethernetAccepted)
            {
                state = State.TravelingToDistrict;
                stateEnteredAt = DateTime.UtcNow;
                StatusText = Loc.T("Reise in Nachbarbezirk (Aethernetz)...", "Traveling to neighboring district (aethernet)...");
                return;
            }
        }

        // Fallback: volle Teleport-Aktion zum großen Aetheryten des Zielbezirks - nur relevant für
        // geteilte Städte, die (anders als Ul'dah) tatsächlich einen eigenen großen Aetheryten pro
        // Bezirk haben.
        var mainAetheryteId = Plugin.FindUnlockedMainAetheryteId(targetTerritory);
        if (mainAetheryteId == null)
        {
            SkipWholeDistrict();
            StatusText = Loc.T(
                "Nachbarbezirk übersprungen (dort noch kein Aetheryte freigeschaltet)",
                "Skipped neighboring district (no unlocked aetheryte there yet)");
            return;
        }

        var accepted = lifestreamTeleport.InvokeFunc(mainAetheryteId.Value, (byte)0);
        if (!accepted)
        {
            SkipWholeDistrict();
            StatusText = Loc.T(
                "Nachbarbezirk übersprungen (Lifestream lehnt die Reise ab)",
                "Skipped neighboring district (Lifestream rejected the trip)");
            return;
        }

        state = State.TravelingToDistrict;
        stateEnteredAt = DateTime.UtcNow;
        StatusText = Loc.T("Reise in Nachbarbezirk...", "Traveling to neighboring district...");
    }

    private void UpdateTravelingToDistrict()
    {
        if (!lifestreamIsBusy.InvokeFunc())
        {
            // Kurz warten, bis sich Plugin.ClientState.TerritoryType tatsächlich auf die neue Zone
            // aktualisiert hat (siehe DistrictTravelSettleDelay) - sonst hält der nächste
            // TryStartNext-Durchlauf die Zone noch für die alte.
            districtTravelFinishedAt ??= DateTime.UtcNow;
            Plugin.Log.Info($"[AetheryteAutomation] UpdateTravelingToDistrict: Lifestream fertig, playerPos={Plugin.ObjectTable.LocalPlayer?.Position}, realTerritory={Plugin.ClientState.TerritoryType}, warte auf Settle...");
            if (DateTime.UtcNow - districtTravelFinishedAt.Value < DistrictTravelSettleDelay)
                return;

            Plugin.Log.Info($"[AetheryteAutomation] UpdateTravelingToDistrict: Settle-Zeit vorbei, realTerritory={Plugin.ClientState.TerritoryType}");
            districtTravelFinishedAt = null;
            state = State.Idle;
            return;
        }

        districtTravelFinishedAt = null;
        if (DateTime.UtcNow - stateEnteredAt > DistrictTravelTimeout)
        {
            StopLifestream();
            state = State.Idle;
            StatusText = Loc.T("Reise dauert zu lange - abgebrochen", "Travel took too long - aborted");
        }
    }

    private void UpdateMoving()
    {
        if (currentTargetId == null)
        {
            state = State.Idle;
            return;
        }

        // vnavmesh stoppt selbst, sobald die bei PathfindAndMoveCloseTo angegebene Toleranz
        // erreicht ist (oder es feststeckt/abgebrochen wurde) - IsRunning wird dann false. Die
        // Zielposition kommt aus dem MapMarker-Sheet (siehe TryStartNext), nicht aus einem
        // Weltobjekt - das wird erst beim Interagieren gebraucht, wenn es geladen sein sollte.
        if (pathIsRunning.InvokeFunc())
        {
            hasSeenPathRunning = true;

            var movingPlayerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            var movingDistance = Vector3.Distance(movingPlayerPos, currentTargetPosition);

            // Sprint nutzen, sobald es nicht mehr auf Cooldown ist - beschleunigt vor allem die
            // langen Laufwege zwischen weiter entfernten Kristallen merklich. Kurz vor dem Ziel
            // aber nicht mehr (siehe SprintDisableDistance).
            if (movingDistance > SprintDisableDistance)
                Plugin.TryUseSprint();

            // Läuft noch - aber kommt es tatsächlich voran? Ein fester Gesamt-Timeout wäre bei
            // weit entfernten Zielen (200+ Yalm) falsch: der reine Fußweg dahin kann allein schon
            // deutlich länger als 30s dauern, obwohl vnavmesh die ganze Zeit sichtbar läuft. Statt
            // die Gesamtzeit zu begrenzen, wird daher nur geprüft, ob die Distanz zum Ziel
            // überhaupt noch kleiner wird - bleibt sie zu lange gleich (feststeckend/Hindernis),
            // wird abgebrochen.
            if (movingDistance <= lastProgressDistance - 1f)
            {
                lastProgressDistance = movingDistance;
                lastProgressAtUtc = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastProgressAtUtc > StepStallTimeout)
            {
                Plugin.Log.Info($"[AetheryteAutomation] UpdateMoving(#{currentTargetId}): kein Fortschritt seit {StepStallTimeout.TotalSeconds}s (distance={movingDistance}, bisher bester={lastProgressDistance}).");
                SkipCurrent(Loc.T("Laufweg abgebrochen (kein Fortschritt)", "movement stopped (no progress)"));
                return;
            }
        }
        else if (hasSeenPathRunning)
        {
            // War schon mal am Laufen und ist jetzt fertig - entweder angekommen oder feststeckend.
            // Schwelle an die beim Start verwendete Toleranz gekoppelt (mit etwas Spielraum) -
            // vnavmesh selbst darf ja bis zu dieser Toleranz entfernt "angekommen" melden, das darf
            // hier nicht fälschlich als "feststeckend" gewertet werden.
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            var distance = Vector3.Distance(playerPos, currentTargetPosition);
            Plugin.Log.Info($"[AetheryteAutomation] UpdateMoving(#{currentTargetId}): Path.IsRunning wurde false. playerPos={playerPos}, target={currentTargetPosition}, distance={distance}, tolerance={currentArrivalTolerance + 2f}");
            if (distance <= currentArrivalTolerance + 2f)
            {
                state = State.Interacting;
                stateEnteredAt = DateTime.UtcNow;
                hasInteractedThisCycle = false;
                interactObjectNotFoundSince = null;
                StatusText = Loc.T("Interagiere...", "Interacting...");
            }
            else
            {
                SkipCurrent(Loc.T("Laufweg abgebrochen (feststeckend?)", "movement stopped (stuck?)"));
            }

            return;
        }
        else if (DateTime.UtcNow - stateEnteredAt > PathStartGracePeriod)
        {
            // Auch nach der Gnadenfrist nie sichtbar losgelaufen - vnavmesh hat den Auftrag zwar
            // angenommen, aber offenbar doch nie wirklich ausgeführt.
            SkipCurrent(Loc.T("vnavmesh hat nie losgelegt", "vnavmesh never started moving"));
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > StepMaxDuration)
            SkipCurrent(Loc.T("Laufweg dauert zu lange", "took too long to walk there"));
    }

    private void UpdateInteracting()
    {
        if (currentTargetId == null)
        {
            state = State.Idle;
            return;
        }

        // Bewusst per Nähe zur (aus MapMarker aufgelösten) Zielposition, nicht per BaseId-Abgleich:
        // BaseId ist bei kleinen Aethernetz-Kristallen offenbar kein verlässlicher 1:1-Schlüssel
        // zur Aetheryte-RowId (auch Lifestream verlässt sich dafür nicht darauf, sondern auf
        // Positionsnähe) - da wir gerade exakt an dieser Position angekommen sind, ist das
        // nächstgelegene Aetheryte-Objekt zuverlässig der richtige Kristall.
        var gameObject = FindNearestAetheryteObject(currentTargetPosition, 15f);
        Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): target={currentTargetPosition}, gefundenes Objekt: BaseId={gameObject?.BaseId}, Position={gameObject?.Position}, hasInteractedThisCycle={hasInteractedThisCycle}");
        if (gameObject == null)
        {
            // Jetzt am Ziel angekommen sollte das Objekt eigentlich sofort geladen sein - eine
            // kurze Gnadenfrist trotzdem, für den seltenen Fall eines Frames Verzögerung.
            interactObjectNotFoundSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - interactObjectNotFoundSince.Value < InteractObjectGracePeriod)
                return;

            SkipCurrent(Loc.T("Objekt trotz Ankunft nicht gefunden", "object not found despite arriving"));
            return;
        }

        if (!hasInteractedThisCycle)
        {
            // Interact braucht das Objekt als aktuelles Ziel - das muss erst einen Frame lang
            // angewendet worden sein, bevor der eigentliche Interact-Aufruf greift.
            if (!Plugin.IsCurrentTarget(gameObject))
            {
                Plugin.SetTarget(gameObject);
                return;
            }

            Plugin.InteractWithGameObject(gameObject);
            hasInteractedThisCycle = true;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        // Das Entdecken eines Aetheryten spielt einen kurzen Cast ab, bevor er wirklich
        // freigeschaltet ist - erst danach weitermachen, statt nach einer festen (zu kurzen)
        // Wartezeit einfach anzunehmen, dass es geklappt hat.
        if (Plugin.IsAetheryteUnlocked(currentTargetId.Value))
        {
            currentTargetId = null;
            state = State.Idle;
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > UnlockWaitTimeout)
            SkipCurrent(Loc.T("Freischalten hat nicht geklappt", "unlocking did not go through"));
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearestAetheryteObject(Vector3 nearPosition, float maxDistance)
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? nearest = null;
        var bestDistance = maxDistance;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.Aetheryte)
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
        Plugin.Log.Info($"[AetheryteAutomation] SkipCurrent(#{currentTargetId}): {reason}");

        if (currentTargetId.HasValue)
            skippedIds.Add(currentTargetId.Value);

        StatusText = Loc.T($"Übersprungen ({reason})", $"Skipped ({reason})");

        StopPath();

        state = State.Idle;
        currentTargetId = null;
    }
}
