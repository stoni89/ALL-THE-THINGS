using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

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
        Mounting,
        MovingTo,
        Interacting,
        TravelingToDistrict,
        ManualDistrictEntry,
        SimulationWaitingForWindowClose,
    }

    private enum ManualEntryPhase
    {
        Walking,
        Interacting,
        WaitingForZoneChange,
    }

    // Wie lange maximal aufs Aufsteigen gewartet wird (Ruf-Animation), bevor trotzdem
    // weitergemacht wird (dann eben zu Fuß, siehe UpdateMounting) - falls Rufen aus irgendeinem
    // Grund nicht klappt (z.B. gerade nicht erlaubt), soll die Automation nicht ewig hängen bleiben.
    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);

    // Ab dieser Entfernung (Yalms) zum Kristall wird die Bewegung gestoppt und interagiert - für
    // kleine Aethernetz-Kristalle (schlankes Modell, kleiner Sockel). Für den gezielten Nachlauf
    // zum echten Weltobjekt (siehe UpdateInteracting) bewusst NICHT enger: ein Test mit 1.5 führte
    // dazu, dass der Charakter endlos gegen den (kollidierenden) Sockel gelaufen ist, weil vnavmesh
    // diese Distanz gar nicht erst physisch erreichen kann (der Sockel blockiert vorher).
    private const float SmallAetheryteFinalApproachDistance = 3.5f;

    // GROSSE Aetheryten (row.IsAetheryte == true, siehe Plugin.IsBigAetheryte) haben einen deutlich
    // breiteren Sockel/größeres Kollisionsmodell als die kleinen Aethernetz-Kristalle - mit
    // demselben engen Abstand wie oben lief der Charakter gelegentlich dagegen, statt sauber davor
    // stehen zu bleiben (gemeldetes Verhalten). Bewusst großzügiger, nicht nur minimal erhöht.
    private const float BigAetheryteFinalApproachDistance = 6f;

    private static float GetFinalApproachDistance(uint aetheryteId) =>
        Plugin.IsBigAetheryte(aetheryteId) ? BigAetheryteFinalApproachDistance : SmallAetheryteFinalApproachDistance;

    // Deutlich engere Zusatz-Annäherung NUR direkt vor dem eigentlichen Interact-Aufruf (siehe
    // UpdateInteracting) - Verdacht: der tatsächliche Klick-Radius des Spiels für "Menü öffnen"
    // liegt enger als GetFinalApproachDistance (die bewusst großzügig ist, um nicht gegen den
    // Sockel zu laufen). Bewusst mit eigenem, kurzem Timeout statt der vollen MovingTo-
    // Zustandsmaschine - kann vnavmesh diese Distanz wegen Sockel-Kollision gar nicht erreichen
    // (siehe Kommentar bei SmallAetheryteFinalApproachDistance), soll die Automation nach kurzer
    // Zeit trotzdem aus der bestmöglich erreichten Entfernung interagieren, statt endlos gegen den
    // Sockel zu laufen.
    private const float TightInteractApproachDistance = 3f;

    // Kurz gehalten: Empirisch bringt langes Weiterbumpen gegen den Sockel keinen Vorteil - vnavmesh
    // erreicht die physisch nächstmögliche Position bereits nach einem Bruchteil dieser Zeit, der
    // Rest wäre nur sichtbar sinnloses Gegenlaufen. Nach Ablauf wird trotzdem aus der bestmöglich
    // erreichten Entfernung interagiert (siehe oben).
    private static readonly TimeSpan TightInteractApproachTimeout = TimeSpan.FromSeconds(1.2);

    // Wie oft geprüft wird, ob sich der Charakter während des engen Heranlaufens überhaupt noch
    // spürbar bewegt (siehe TightApproachStuckMinProgress) - kürzer als TightInteractApproachTimeout,
    // damit ein Steckenbleiben am Sockel (Kollision) nicht erst nach dem vollen Timeout, sondern
    // deutlich früher erkannt und abgebrochen wird. Sichtbar kürzeres "Gegenlaufen" gegen den Sockel.
    private static readonly TimeSpan TightApproachStuckCheckInterval = TimeSpan.FromMilliseconds(350);

    // Unterhalb dieser Bewegung (Yalms) seit der letzten Prüfung gilt der Charakter als am Sockel
    // steckengeblieben - bewusst klein (deutlich kleiner als NavigationStuckDetector.
    // MinProgressDistance), weil hier viel häufiger/früher geprüft wird als dort.
    private const float TightApproachStuckMinProgress = 0.3f;

    // Innerhalb dieser Entfernung (Yalms) zum Ziel wird kein Sprint mehr benutzt - wird Sprint
    // genau in dem Moment aktiviert, in dem vnavmesh eigentlich anhalten und den Aetheryten
    // anklicken will, verpasst der Klick das Objekt und die Automation bleibt ohne erkennbaren
    // Grund stehen.
    private const float SprintDisableDistance = 8f;

    // Toleranz, die vnavmesh für PathfindAndMoveCloseTo bekommt - bewusst großzügiger als die
    // Final-Approach-Distanzen oben: manche Kristalle stehen in Gebäude-Innenräumen, deren genaue Position
    // (aus dem MapMarker-Sheet) knapp außerhalb der von der Navmesh abgedeckten Fläche liegen kann
    // (z.B. hinter einer Tür) - mit einer zu engen Toleranz lehnt vnavmesh den Laufauftrag dann
    // komplett ab, statt wenigstens bis zur Tür zu laufen.
    private const float PathTolerance = 10f;

    // Absolute Notbremse für einen einzelnen Laufweg - solange Path.IsRunning true bleibt, vertraut
    // die Automation vnavmesh (siehe UpdateMoving), auch auf langen/verwinkelten Wegen (z.B. große
    // Zonen wie Mor Dhona). Nur wenn selbst das viel zu lange dauert, wird abgebrochen.
    private static readonly TimeSpan StepMaxDuration = TimeSpan.FromMinutes(10);

    // Wie lange nach der Interaktion auf den tatsächlichen Freischalt-Abschluss gewartet wird
    // (der "Entdecken"-Cast braucht ein paar Sekunden) - danach gilt der Versuch als gescheitert.
    private static readonly TimeSpan UnlockWaitTimeout = TimeSpan.FromSeconds(15);

    // Kurze Anlaufzeit nach dem Testklick auf einen bereits freigeschalteten Aetheryten
    // (Simulation-Modus), bevor zum ersten Mal geprüft wird, ob das dabei geöffnete native Menü
    // ("SelectString"/"Teleport") schon wieder zu ist - das Fenster braucht ein paar Frames, um
    // überhaupt zu erscheinen. Ohne diese Gnadenfrist würde der allererste Check (Fenster noch gar
    // nicht gerendert) fälschlich sofort als "schon wieder geschlossen" gewertet.
    private static readonly TimeSpan SimulationWindowOpenGracePeriod = TimeSpan.FromMilliseconds(800);

    // Kurze Nachlaufzeit, NACHDEM das native Menü wieder zu ist, bevor der nächste Schritt
    // (insbesondere der Mount-Ruf fürs nächste Ziel, siehe TryRequestAetheryteMount) losgeschickt
    // wird - beobachtet: ein Chat-Befehl (SendGameChatCommand/ProcessChatBoxEntry), der GENAU in dem
    // Moment abgesetzt wird, in dem ein natives Fenster gerade erst zugeht, scheint von der
    // Schließ-Animation/dem UI-Fokuswechsel verschluckt zu werden - der Mount-Ruf wird zwar gesendet
    // (siehe MountDebug-Log), bewirkt aber nichts, und die Automation läuft danach fälschlich zu Fuß
    // statt zu fliegen.
    private static readonly TimeSpan SimulationWindowCloseSettleDelay = TimeSpan.FromMilliseconds(700);

    // Wie lange nach dem Absteigen gewartet wird, bevor überhaupt versucht wird zu interagieren -
    // Condition[Mounted] wird schon VOR dem Ende der sichtbaren Absteige-/Lande-Animation false
    // (reines Prüfen darauf reicht also nicht) - ein Interact-Versuch mitten in dieser Animation
    // greift nicht (identisches Problem/Lösung wie in ChocobokeepAutomation).
    private static readonly TimeSpan DismountSettleDelay = TimeSpan.FromSeconds(1);

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
    private string currentTargetName = string.Empty;
    private Vector3 currentTargetPosition;
    private float currentArrivalTolerance = SmallAetheryteFinalApproachDistance;
    private DateTime stateEnteredAt;
    private bool hasInteractedThisCycle;
    private bool didFinalApproach;
    private bool currentTargetWasUnlockedAtStart;
    private bool didTightInteractApproach;
    private DateTime? tightInteractApproachStartedAt;
    private Vector3? tightApproachStuckCheckPosition;
    private DateTime? tightApproachStuckCheckAt;

    // Verhindert, dass Plugin.TryRemountAfterForcedDismount (gedacht für unfreiwilliges Absteigen
    // beim Schwimmen) den Charakter wieder aufsitzen lässt, nachdem WIR ihn absichtlich zum
    // Interagieren abgestiegen haben (siehe UpdateMoving/UpdateInteracting, wo Plugin.TryDismount()
    // aufgerufen wird) - sonst versucht er auf dem letzten Stück zum Kristall ständig wieder
    // aufzumounten, statt zu Fuß zu interagieren.
    private bool hasIntentionallyDismounted;
    private DateTime? simulationWindowClosedAt;

    // Ab wann Condition[Mounted] erstmals false war, nachdem wir absichtlich abgestiegen sind
    // (siehe DismountSettleDelay) - null, solange noch beritten.
    private DateTime? dismountedAt;

    // Einmal in UpdateInteracting gefundenes Weltobjekt wird für den Rest des Zyklus an dieser
    // Adresse festgehalten, statt jeden Frame neu per Nähe zu suchen - in Aetheryte-Plätzen stehen
    // oft mehrere Aetheryte-Objekte (großer Aetheryte + mehrere Aethernetz-Kristalle) nur wenige
    // Yalm auseinander. Ohne dieses Festhalten kann FindNearestAetheryteObject je nach winziger
    // Positionsänderung frameweise zwischen zwei Objekten hin- und herspringen - dann wird nie 2
    // Frames hintereinang dasselbe Objekt als aktuelles Ziel gesetzt (siehe IsCurrentTarget-Prüfung
    // unten) und der eigentliche Interact-Aufruf wird nie erreicht (Charakter zielt sichtbar, ohne
    // je zu interagieren).
    private nint? currentInteractObjectAddress;
    private bool hasSeenPathRunning;
    private DateTime lastRemountAttempt = DateTime.MinValue;
    private readonly NavigationStuckDetector stuckDetector = new();
    private readonly FlightPathUpgrade flightUpgrade = new(); // siehe Plugin.FlightPathUpgrade (Flugverbots-Bereiche)
    private DateTime? interactObjectNotFoundSince;
    private DateTime? districtTravelFinishedAt;
    private readonly HashSet<uint> skippedIds = new();

    // Manueller Bezirks-Zugang (siehe Plugin.ManualDistrictEntryPoints/TryTravelToDistrict).
    private ManualEntryPhase manualEntryPhase;
    private DateTime manualEntryPhaseStartedAt;
    private uint manualEntryTargetTerritory;
    private Vector3 manualEntryPosition;
    private int manualEntryInteractAttempts;
    private DateTime manualEntryInteractedAt;
    private DateTime? manualEntryDismountedAt;
    private DateTime lastManualEntryMountAttempt = DateTime.MinValue;
    private const float ManualEntryArrivalTolerance = 0.6f;
    private const float ManualEntryObjectSearchRadius = 8f;
    private static readonly TimeSpan ManualEntryWalkTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ManualEntryPathRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ManualEntryDismountSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ManualEntryZoneChangeTimeout = TimeSpan.FromSeconds(30);
    private const int ManualEntryMaxInteractAttempts = 3;

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
        manualEntryBlockedUntil = DateTime.MinValue;
        StatusText = Loc.T("Automation gestartet...", "Automation started...");
    }

    public void Stop()
    {
        IsActive = false;
        state = State.Idle;
        currentTargetId = null;
        StopPath();
        StopLifestream();
        Plugin.ClearNavigationTarget();
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

                case State.Mounting:
                    UpdateMounting();
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

                case State.ManualDistrictEntry:
                    UpdateManualDistrictEntry();
                    break;

                case State.SimulationWaitingForWindowClose:
                    UpdateSimulationWaitingForWindowClose();
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

        TryTravelToDistrict(otherDistrictCandidate, candidates, currentTerritory);
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
        // am Kristall liegt, reicht dafür die enge Final-Approach-Toleranz (siehe
        // GetFinalApproachDistance) statt der großzügigen PathTolerance (die sonst dazu führt, dass
        // vnavmesh schon viel zu weit weg stehen bleibt).
        var isManualPosition = Plugin.HasManualAetheryteWorldPosition(next.Id);
        Vector3? floorPoint = isManualPosition ? targetPosition : null;
        var tolerance = isManualPosition ? GetFinalApproachDistance(next.Id) : PathTolerance;

        // Sonst erst über die Karten-Flagge versuchen (genau das, was "/vnav moveflag" auch macht)
        // - dieser Weg findet manchmal einen begehbaren Punkt (z.B. an einer Ladentür), den die
        // reine Koordinatensuche unten (PointOnFloor auf der rohen MapMarker-Position) nicht
        // findet, weil die Karten-Flagge intern anders/großzügiger auf die Navmesh gerastert wird.
        if (floorPoint == null && next.HasVendorLocation)
        {
            Plugin.OpenVendorMap(next, showMapWindow: false);
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

        currentTargetId = next.Id;
        currentTargetName = next.Name;
        currentTargetPosition = floorPoint.Value;
        currentArrivalTolerance = tolerance;
        didFinalApproach = false;
        didTightInteractApproach = false;
        tightInteractApproachStartedAt = null;
        tightApproachStuckCheckPosition = null;
        tightApproachStuckCheckAt = null;
        hasIntentionallyDismounted = false;
        dismountedAt = null;
        currentInteractObjectAddress = null;

        // Merken, ob dieses Ziel schon VOR diesem Anlauf freigeschaltet war (z.B. im Simulation-
        // Modus, siehe Configuration.SimulateAetheryteAutomation, der bewusst auch schon
        // freigeschaltete Aetheryten zu Testzwecken anlaufen lässt) - sonst würde die
        // "bereits durch Nähe freigeschaltet"-Abkürzung in UpdateInteracting sofort greifen, noch
        // bevor überhaupt sichtbar abgestiegen/interagiert wurde (siehe currentTargetWasUnlockedAtStart).
        currentTargetWasUnlockedAtStart = Plugin.IsAetheryteUnlocked(next.Id);

        // Mount rufen (falls in den QoL-Einstellungen ausgewählt und noch nicht beritten) - der
        // eigentliche Laufauftrag an vnavmesh geht erst raus, nachdem entweder aufgestiegen wurde
        // oder das Aufsteigen aufgegeben wurde (siehe UpdateMounting), sonst würde vnavmesh mitten
        // in der Aufstiegs-Animation losschicken wollen.
        if (Plugin.TryRequestAetheryteMount())
        {
            state = State.Mounting;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Rufe Mount, dann: {next.Name}...", $"Summoning mount, then: {next.Name}...");
            return;
        }

        BeginPathfind();
    }

    /// <summary>
    /// Stößt den eigentlichen Laufauftrag an vnavmesh an - beritten wird zuerst Fliegen versucht
    /// (schneller), aber nur, wenn Plugin.CanFly gerade true ist (Zone freigeschaltet und Fliegen dort
    /// erlaubt) - sonst nimmt vnavmesh einen Flugauftrag teils trotzdem an, obwohl der Charakter gar
    /// nicht abheben kann, und hüpft nur sinnlos am Boden herum statt zu laufen. Lehnt vnavmesh den
    /// Flugauftrag trotzdem ab (oder ist Fliegen gerade nicht erlaubt), stattdessen mit demselben
    /// Mount am Boden geritten. Ohne Mount (aus, oder das Aufsteigen hat nicht geklappt) ganz normal
    /// zu Fuß wie bisher (siehe SprintDisableDistance/TryUseSprint in UpdateMoving).
    /// </summary>
    private void BeginPathfind()
    {
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var accepted = false;
        var flyingAccepted = false;

        if (mounted && Plugin.CanFly)
        {
            accepted = flyingAccepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, true, currentArrivalTolerance);
            Plugin.Log.Info($"[AetheryteAutomation] BeginPathfind({currentTargetName}): pathfindAndMoveCloseTo(fly=true, tolerance={currentArrivalTolerance}) accepted={accepted}");
        }

        if (!accepted)
        {
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, currentArrivalTolerance);
            Plugin.Log.Info($"[AetheryteAutomation] BeginPathfind({currentTargetName}): pathfindAndMoveCloseTo(fly=false, tolerance={currentArrivalTolerance}) accepted={accepted}, mounted={mounted}");
        }

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
        StatusText = Loc.T($"Laufe zu: {currentTargetName}...", $"Walking to: {currentTargetName}...");
    }

    private void UpdateMounting()
    {
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            BeginPathfind();
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > MountWaitTimeout)
        {
            // Aufsteigen hat nicht geklappt (z.B. gerade nicht erlaubt) - nicht endlos warten,
            // sondern stattdessen normal zu Fuß weiter (BeginPathfind erkennt "nicht beritten"
            // selbst und nutzt dann den Fußweg-Pfad).
            Plugin.Log.Info($"[AetheryteAutomation] UpdateMounting(#{currentTargetId}): nach {MountWaitTimeout.TotalSeconds}s nicht aufgestiegen - laufe stattdessen zu Fuß.");
            BeginPathfind();
        }
    }

    /// <summary>
    /// Reist per Lifestream zum bereits freigeschalteten großen Aetheryten des Zielbezirks -
    /// von dort übernimmt beim nächsten Idle-Durchlauf wieder vnavmesh wie gewohnt. Ohne Lifestream
    /// oder ohne einen bereits freigeschalteten Aetheryten dort werden alle Kandidaten dieses
    /// Bezirks übersprungen, statt endlos erneut zu versuchen.
    /// </summary>
    private void TryTravelToDistrict(CollectibleEntry targetCandidate, List<CollectibleEntry> allCandidates, uint currentTerritory)
    {
        var targetTerritory = HomeTerritory(targetCandidate);

        void SkipWholeDistrict()
        {
            foreach (var c in allCandidates.Where(c => HomeTerritory(c) == targetTerritory))
                skippedIds.Add(c.Id);
        }

        // Manueller Bezirks-Zugang (z.B. Gold-Saucer-Fahrstuhl) - für Bezirke, in denen noch KEIN
        // Aetheryte/Aethernetz-Kristall freigeschaltet ist, kann Lifestream ohnehin nicht bootstrappen
        // (siehe Plugin.ManualDistrictEntryPoints-Kommentar). Braucht selbst kein Lifestream.
        bool TryManualEntry()
        {
            if (DateTime.UtcNow < manualEntryBlockedUntil || !Plugin.TryGetManualDistrictEntryPoint(currentTerritory, targetTerritory, out var entryPosition))
                return false;

            Plugin.Log.Info($"[AetheryteAutomation] TryTravelToDistrict({targetTerritory}): nutze manuellen Bezirks-Zugang bei {entryPosition}.");
            BeginManualDistrictEntry(targetTerritory, entryPosition);
            return true;
        }

        // Im Simulation-Modus (siehe Configuration.SimulateAetheryteAutomation) den manuellen Zugang
        // IMMER nutzen, statt der Aethernetz-/Teleport-Abkürzungen unten - der Zielkristall ist dort
        // zu Testzwecken absichtlich schon freigeschaltet, im echten "noch nicht freigeschaltet"-Fall
        // wäre der Zugang aber ohnehin die einzige Möglichkeit (siehe Plugin.SimulateAetheryteAutomation).
        if (Plugin.SimulateAetheryteAutomation && TryManualEntry())
            return;

        if (!IsLifestreamAvailable())
        {
            if (TryManualEntry())
                return;

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
            if (TryManualEntry())
                return;

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

    /// <summary>Siehe Plugin.ManualDistrictEntryPoints - hinlaufen, interagieren, "Ja" bestätigen, auf den Zonenwechsel warten.</summary>
    private void BeginManualDistrictEntry(uint targetTerritory, Vector3 entryPosition)
    {
        manualEntryTargetTerritory = targetTerritory;
        manualEntryPosition = entryPosition;
        manualEntryInteractAttempts = 0;
        manualEntryDismountedAt = null;
        state = State.ManualDistrictEntry;
        stateEnteredAt = DateTime.UtcNow;
        SetManualEntryPhase(ManualEntryPhase.Walking);
    }

    private void SetManualEntryPhase(ManualEntryPhase phase)
    {
        manualEntryPhase = phase;
        manualEntryPhaseStartedAt = DateTime.UtcNow;
    }

    private void UpdateManualDistrictEntry()
    {
        switch (manualEntryPhase)
        {
            case ManualEntryPhase.Walking:
                UpdateManualEntryWalking();
                break;
            case ManualEntryPhase.Interacting:
                UpdateManualEntryInteracting();
                break;
            case ManualEntryPhase.WaitingForZoneChange:
                UpdateManualEntryWaitingForZoneChange();
                break;
        }
    }

    private void UpdateManualEntryWalking()
    {
        StatusText = Loc.T("Laufe zum Zugang des Nachbarbezirks...", "Walking to the neighboring district's entrance...");

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? manualEntryPosition;
        if (Vector3.Distance(playerPos, manualEntryPosition) <= ManualEntryArrivalTolerance)
        {
            StopPath();
            manualEntryDismountedAt = null;
            SetManualEntryPhase(ManualEntryPhase.Interacting);
            return;
        }

        if (DateTime.UtcNow - manualEntryPhaseStartedAt > ManualEntryWalkTimeout)
        {
            Plugin.Log.Info("[AetheryteAutomation] ManualDistrictEntry: Zugang nicht erreicht - gebe auf.");
            FailManualDistrictEntry();
            return;
        }

        var running = false;
        try { running = pathIsRunning.InvokeFunc(); } catch { /* vnavmesh fehlt */ }
        if (running)
            return;

        if (DateTime.UtcNow - lastManualEntryMountAttempt > ManualEntryPathRetryInterval)
        {
            lastManualEntryMountAttempt = DateTime.UtcNow;
            var mounted = Plugin.Condition[ConditionFlag.Mounted];
            var flying = mounted && Plugin.CanFly;
            var accepted = flying && pathfindAndMoveCloseTo.InvokeFunc(manualEntryPosition, true, ManualEntryArrivalTolerance);
            if (!accepted)
                accepted = pathfindAndMoveCloseTo.InvokeFunc(manualEntryPosition, false, ManualEntryArrivalTolerance);
            Plugin.Log.Info($"[AetheryteAutomation] ManualDistrictEntry: Laufe zum Zugang, angenommen={accepted}.");
        }
    }

    private void UpdateManualEntryInteracting()
    {
        StatusText = Loc.T("Betrete den Nachbarbezirk...", "Entering the neighboring district...");

        // Der Fahrstuhlführer redet erst noch (reiner Text-Dialog, siehe Nutzer-Report Gold Saucer) -
        // den jeden Frame weiterklicken, sonst kommt das Ja/Nein-Fenster nie dazu, überhaupt aufzugehen.
        Plugin.TryAdvanceTalkDialogue();

        // Das Ja/Nein-Fenster jeden Frame bestätigen, sobald es da ist.
        if (Plugin.TryConfirmSelectYesno())
        {
            SetManualEntryPhase(ManualEntryPhase.WaitingForZoneChange);
            return;
        }

        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            Plugin.TryDismount();
            manualEntryDismountedAt = null;
            return;
        }

        manualEntryDismountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - manualEntryDismountedAt.Value < ManualEntryDismountSettleDelay)
            return;

        // Nach einem Interact-Versuch kurz auf das Fenster warten.
        if (manualEntryInteractAttempts > 0 && DateTime.UtcNow - manualEntryInteractedAt < TimeSpan.FromSeconds(3))
            return;

        if (manualEntryInteractAttempts >= ManualEntryMaxInteractAttempts)
        {
            Plugin.Log.Info("[AetheryteAutomation] ManualDistrictEntry: Zugang reagiert nicht - gebe auf.");
            FailManualDistrictEntry();
            return;
        }

        var target = FindManualEntryObject();
        if (target == null)
        {
            Plugin.Log.Info("[AetheryteAutomation] ManualDistrictEntry: Zugangs-Objekt nicht gefunden - gebe auf.");
            FailManualDistrictEntry();
            return;
        }

        if (!Plugin.IsCurrentTarget(target))
        {
            Plugin.SetTarget(target);
            return;
        }

        Plugin.Log.Info($"[AetheryteAutomation] ManualDistrictEntry: Interagiere mit '{target.Name}'.");
        Plugin.InteractWithGameObject(target);
        manualEntryInteractAttempts++;
        manualEntryInteractedAt = DateTime.UtcNow;
    }

    private void UpdateManualEntryWaitingForZoneChange()
    {
        // Falls noch weiterer Dialogtext bzw. das Fenster ein zweites Mal kommt.
        Plugin.TryAdvanceTalkDialogue();
        Plugin.TryConfirmSelectYesno();

        if (Plugin.ResolveEffectiveTerritoryId(Plugin.ClientState.TerritoryType) == manualEntryTargetTerritory
            && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51]
            && !Plugin.Condition[ConditionFlag.OccupiedInEvent])
        {
            Plugin.Log.Info("[AetheryteAutomation] ManualDistrictEntry: Nachbarbezirk betreten - Automation läuft weiter.");
            state = State.Idle;
            return;
        }

        if (DateTime.UtcNow - manualEntryPhaseStartedAt > ManualEntryZoneChangeTimeout)
        {
            Plugin.Log.Info("[AetheryteAutomation] ManualDistrictEntry: Zonenwechsel blieb aus - versuche erneut zu interagieren.");
            SetManualEntryPhase(ManualEntryPhase.Interacting);
            manualEntryDismountedAt = DateTime.UtcNow - ManualEntryDismountSettleDelay;
        }
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindManualEntryObject()
    {
        // Der Zugang ist i.d.R. ein ansprechbarer NPC (z.B. der Fahrstuhlführer "Seathrith"), also
        // gezielt nach EventNpc/EventObj suchen statt (wie zuvor) versehentlich alle ICharacter
        // auszuschließen - das hätte genau diesen NPC nie gefunden.
        Dalamud.Game.ClientState.Objects.Types.IGameObject? nearest = null;
        var bestDistance = ManualEntryObjectSearchRadius;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (!obj.IsTargetable || (obj.ObjectKind != ObjectKind.EventNpc && obj.ObjectKind != ObjectKind.EventObj))
                continue;

            var distance = Vector3.Distance(obj.Position, manualEntryPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = obj;
            }
        }

        return nearest;
    }

    // Nach einem Fehlschlag (Zugang nicht erreicht/reagiert nicht) so lange nicht erneut versuchen -
    // ohne diese Sperre würde die Automation bei einem dauerhaften Problem (z.B. Objekt nicht
    // gefunden) jeden Frame von Neuem denselben Fehlschlag produzieren.
    private static readonly TimeSpan ManualEntryFailureCooldown = TimeSpan.FromMinutes(2);
    private DateTime manualEntryBlockedUntil = DateTime.MinValue;

    private void FailManualDistrictEntry()
    {
        StopPath();
        manualEntryBlockedUntil = DateTime.UtcNow + ManualEntryFailureCooldown;
        StatusText = Loc.T("Nachbarbezirk übersprungen (Zugang nicht erreichbar)", "Skipped neighboring district (entrance not reachable)");
        state = State.Idle;
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

            // Falls unterwegs durch Schwimmen zwangsweise abgestiegen wurde - sobald wieder Land
            // erreicht ist, erneut aufsitzen. NICHT, nachdem wir selbst absichtlich zum Interagieren
            // abgestiegen sind (siehe hasIntentionallyDismounted, gesetzt bei Plugin.TryDismount()
            // unten) - sonst versucht der Charakter genau auf dem letzten Stück zum Kristall
            // (Final-/Tight-Approach-Laufwege, beide laufen über UpdateMoving) wieder aufzusitzen,
            // obwohl er absichtlich abgestiegen ist, um interagieren zu können.
            if (!hasIntentionallyDismounted)
                Plugin.TryRemountAfterForcedDismount(ref lastRemountAttempt);

            // Aus einem Flugverbots-Bereich heraus (siehe FlightPathUpgrade) - jetzt fliegend weiter.
            if (flightUpgrade.ShouldReplanFlying(movingPlayerPos, currentTargetPosition))
            {
                StopPath();
                BeginPathfind();
                return;
            }

            // Steckengeblieben (z.B. gegen eine Wand/Kante, vnavmeshs lokale Steuerung kommt nicht
            // weiter) - NICHT dasselbe wie "Distanz zum Ziel nimmt nicht ab" (siehe Kommentar unten,
            // das wäre auf verwinkelten Wegen ein Fehlalarm), sondern die eigene POSITION hat sich
            // seit Sekunden gar nicht mehr verändert. Pfad neu anfordern statt untätig zu warten.
            if (stuckDetector.CheckStuck(movingPlayerPos))
            {
                Plugin.Log.Info($"[AetheryteAutomation] UpdateMoving(#{currentTargetId}): scheinbar steckengeblieben - Laufweg wird neu angefordert.");
                StopPath();
                BeginPathfind();
                return;
            }

            // Bewusst KEIN "kein Fortschritt seit X Sekunden"-Abbruch mehr: Die geradlinige Distanz
            // zum Ziel ist auf verwinkelten/langen Wegen (z.B. große Zonen wie Mor Dhona mit Seen/
            // Bergen dazwischen) kein verlässliches Fortschrittsmaß - vnavmesh kann streckenweise
            // sogar kurz weiter weg vom Ziel laufen müssen, um überhaupt herumzukommen, obwohl es
            // sichtbar weiter aktiv unterwegs ist. Solange Path.IsRunning true bleibt, gilt das als
            // "noch dabei" - einzige Bremse ist die absolute Notbremse (StepMaxDuration) unten.
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
                // Anders als bei ChocobokeepAutomation HIER bewusst schon absteigen: Ätheryten haben
                // (anders als offene NPC-Standorte) eine spielseitige Kein-Flug-Zone direkt um sich
                // herum - ein Versuch, per vnavmesh nah heranzufliegen, bleibt dort einfach hängen
                // (beobachtet: stoppt ~9y entfernt, der Stuck-Detector startet den Flug immer wieder
                // neu, ohne je näherzukommen). Deshalb hier zu Fuß weiter, wie ursprünglich.
                Plugin.TryDismount();
                hasIntentionallyDismounted = true;

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
        else if (Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
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
        // nächstgelegene Aetheryte-Objekt zuverlässig der richtige Kristall. Aber NUR beim ersten
        // Mal in diesem Zyklus neu suchen (siehe currentInteractObjectAddress) - in Aetheryte-
        // Plätzen stehen oft mehrere Aetheryte-Objekte dicht beieinander (großer Aetheryte +
        // Aethernetz-Kristalle), da würde eine jeden Frame neue Nähe-Suche zwischen zwei Objekten
        // hin- und herspringen können, sodass nie zwei Frames hintereinander dasselbe Ziel gesetzt
        // wird und der Interact-Aufruf unten nie erreicht wird (Charakter zielt sichtbar, ohne je zu
        // interagieren).
        var gameObject = currentInteractObjectAddress.HasValue
            ? Plugin.ObjectTable.FirstOrDefault(o => o.ObjectKind == ObjectKind.Aetheryte && o.Address == currentInteractObjectAddress.Value)
            : null;
        gameObject ??= FindNearestAetheryteObject(currentTargetPosition, 15f);
        if (gameObject == null)
        {
            currentInteractObjectAddress = null;
            // Jetzt am Ziel angekommen sollte das Objekt eigentlich sofort geladen sein - eine
            // kurze Gnadenfrist trotzdem, für den seltenen Fall eines Frames Verzögerung.
            interactObjectNotFoundSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - interactObjectNotFoundSince.Value < InteractObjectGracePeriod)
                return;

            SkipCurrent(Loc.T("Objekt trotz Ankunft nicht gefunden", "object not found despite arriving"));
            return;
        }

        currentInteractObjectAddress = gameObject.Address;

        // Manche Feld-Aetheryten scheinen sich schon durch reine Nähe selbst zu entdecken, noch
        // bevor überhaupt interagiert wurde - dann direkt fertig, statt trotzdem noch zu versuchen
        // zu interagieren (und ggf. dabei gegen den Sockel zu laufen, siehe GetFinalApproachDistance).
        // NICHT anwenden, wenn das Ziel schon VOR diesem Anlauf freigeschaltet war (siehe
        // currentTargetWasUnlockedAtStart) - sonst würde dieser Check sofort greifen, ohne dass der
        // Charakter überhaupt sichtbar abgestiegen/interagiert wäre (z.B. im Simulation-Modus).
        if (!currentTargetWasUnlockedAtStart && Plugin.IsAetheryteUnlocked(currentTargetId.Value))
        {
            Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): bereits durch Nähe freigeschaltet, ohne explizite Interaktion.");
            currentTargetId = null;
            state = State.Idle;
            return;
        }

        if (!hasInteractedThisCycle && !didFinalApproach)
        {
            // Die lockere PathTolerance (siehe StartMovingTo) reicht vnavmesh zum "Ankommen", ist
            // aber oft zu großzügig für die tatsächliche Spiel-Interaktion/den Entdecken-Sensor
            // (der einen deutlich engeren Radius braucht, siehe GetFinalApproachDistance) - jetzt,
            // wo das echte Weltobjekt bekannt ist, gezielt näher heranlaufen, statt aus zu großer
            // Entfernung erfolglos zu interagieren und nur auf den Freischalt-Timeout zu warten.
            var finalApproachDistance = GetFinalApproachDistance(currentTargetId.Value);
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            var distanceToObject = Vector3.Distance(playerPos, gameObject.Position);
            if (distanceToObject > finalApproachDistance)
            {
                didFinalApproach = true;
                currentTargetPosition = gameObject.Position;
                currentArrivalTolerance = finalApproachDistance;

                // Bewusst zu Fuß (fly=false), nicht fliegend - anders als bei Chocobokeep-NPCs
                // haben Ätheryten eine spielseitige Kein-Flug-Zone direkt um sich herum, in der
                // vnavmesh beim Fliegen einfach hängen bleibt, ohne näherzukommen (siehe
                // UpdateMoving, wo deshalb schon vorher abgestiegen wird).
                var accepted = pathfindAndMoveCloseTo.InvokeFunc(gameObject.Position, false, finalApproachDistance);
                Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): zu weit vom echten Objekt entfernt (distance={distanceToObject}, BaseId={gameObject.BaseId} @ {gameObject.Position}) - laufe gezielt näher heran, accepted={accepted}.");
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

        // Sicherheitsnetz - eigentlich schon beim Ankommen in UpdateMoving abgestiegen; falls das
        // aus irgendeinem Grund nicht gegriffen hat (z.B. Aktion war in genau dem Frame noch nicht
        // bereit), hier nochmal versuchen, bevor der folgende enge Klick-Annäherungsschritt zu Fuß
        // Bodenkontakt voraussetzt.
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            Plugin.TryDismount();
            hasIntentionallyDismounted = true;
            dismountedAt = null;
            return;
        }

        // Condition[Mounted] wird schon VOR dem Ende der sichtbaren Absteige-/Lande-Animation
        // false - ein Bewegungs-/Interact-Versuch mitten in dieser Animation greift nicht. Deshalb
        // zusätzlich noch kurz warten (siehe DismountSettleDelay).
        dismountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - dismountedAt.Value < DismountSettleDelay)
            return;

        // Zusätzliche, deutlich engere Annäherung NUR direkt vor dem Interact-Versuch (siehe
        // TightInteractApproachDistance) - Verdacht: der tatsächliche Spiel-Klickradius für
        // "Menü öffnen" ist enger als GetFinalApproachDistance oben. Bleibt bewusst in
        // State.Interacting (keine eigene MovingTo-Runde) und pollt selbst, mit kurzem Timeout,
        // falls vnavmesh diese Distanz wegen Sockel-Kollision gar nicht physisch erreichen kann.
        if (!hasInteractedThisCycle && !didTightInteractApproach)
        {
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            var distanceToObject = Vector3.Distance(playerPos, gameObject.Position);
            if (distanceToObject <= TightInteractApproachDistance)
            {
                didTightInteractApproach = true;
            }
            else if (tightInteractApproachStartedAt == null)
            {
                tightInteractApproachStartedAt = DateTime.UtcNow;
                tightApproachStuckCheckPosition = playerPos;
                tightApproachStuckCheckAt = DateTime.UtcNow;
                var accepted = pathfindAndMoveCloseTo.InvokeFunc(gameObject.Position, false, TightInteractApproachDistance);
                Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): versuche noch enger für den Klick heranzulaufen (distance={distanceToObject}, Ziel<= {TightInteractApproachDistance}), accepted={accepted}.");
                if (!accepted)
                    didTightInteractApproach = true;
                else
                    return;
            }
            else if (DateTime.UtcNow - tightInteractApproachStartedAt.Value > TightInteractApproachTimeout)
            {
                Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): enges Heranlaufen nach {TightInteractApproachTimeout.TotalSeconds}s abgebrochen (distance={distanceToObject}) - interagiere trotzdem aus aktueller Entfernung.");
                StopPath();
                didTightInteractApproach = true;
            }
            else if (tightApproachStuckCheckAt.HasValue && DateTime.UtcNow - tightApproachStuckCheckAt.Value >= TightApproachStuckCheckInterval)
            {
                // Deutlich früher als der volle Timeout prüfen, ob überhaupt noch spürbare
                // Bewegung stattfindet - läuft der Charakter gerade gegen den (kollidierenden)
                // Sockel, bewegt er sich seit der letzten Prüfung kaum noch. Dann sofort abbrechen
                // und interagieren, statt sichtbar bis zum vollen Timeout weiter dagegenzulaufen.
                var movedSinceLastCheck = tightApproachStuckCheckPosition.HasValue
                    ? Vector3.Distance(tightApproachStuckCheckPosition.Value, playerPos)
                    : float.MaxValue;
                if (movedSinceLastCheck < TightApproachStuckMinProgress)
                {
                    Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): enges Heranlaufen steckengeblieben (Sockel?, bewegt={movedSinceLastCheck}) - interagiere aus aktueller Entfernung.");
                    StopPath();
                    didTightInteractApproach = true;
                }
                else
                {
                    tightApproachStuckCheckPosition = playerPos;
                    tightApproachStuckCheckAt = DateTime.UtcNow;
                    return;
                }
            }
            else
            {
                return;
            }
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
            Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): interagiert mit BaseId={gameObject.BaseId} @ {gameObject.Position}, warte auf Freischaltung...");
            return;
        }

        // Das Entdecken eines Aetheryten spielt einen kurzen Cast ab, bevor er wirklich
        // freigeschaltet ist - erst danach weitermachen, statt nach einer festen (zu kurzen)
        // Wartezeit einfach anzunehmen, dass es geklappt hat.
        if (Plugin.IsAetheryteUnlocked(currentTargetId.Value))
        {
            // War das Ziel schon VOR diesem Anlauf freigeschaltet (Simulation-Modus), öffnet der
            // gerade erfolgte Klick (siehe oben) das echte Teleport-Menü des Aetheryten (erst ein
            // "SelectString"-Auswahlmenü, danach ggf. die eigentliche "Teleport"-Zielliste) - direkt
            // zum nächsten Ziel weiterzumachen würde durch die dafür nötige Bewegung dieses Menü
            // sofort wieder schließen (Interaktionsfenster schließen sich in FFXIV beim Loslaufen),
            // bevor man es überhaupt sehen könnte. Stattdessen warten, bis der Spieler es selbst
            // wieder wegklickt (siehe UpdateSimulationWaitingForWindowClose), und DANACH von selbst
            // zum nächsten Ziel in der Zone weiterziehen - kein manueller Neustart nötig.
            if (currentTargetWasUnlockedAtStart)
            {
                Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): Simulation - angeklickt, warte auf Schließen des Menüs...");
                StatusText = Loc.T(
                    "Simulation: angeklickt - Menü schließen zum Weitermachen...",
                    "Simulation: clicked - close the window to continue...");
                state = State.SimulationWaitingForWindowClose;
                stateEnteredAt = DateTime.UtcNow;
                return;
            }

            Plugin.Log.Info($"[AetheryteAutomation] UpdateInteracting(#{currentTargetId}): freigeschaltet.");

            // Sofort als erledigt vermerken (nicht erst auf die vom Aufrufer übergebene
            // missingAetherytesInZone-Liste verlassen) - die kommt aus Plugin.IsOwned, das den
            // frisch gesetzten Freischalt-Flag u.U. noch einen Frame lang nicht widerspiegelt. Ohne
            // das würde TryStartNext direkt danach denselben, gerade erst fertigen Aetheryten erneut
            // als "nächstgelegenes" (Distanz 0) Ziel wählen - erneutes Mounten/Absteigen/Interagieren,
            // bevor es endlich zum nächsten weitergeht.
            skippedIds.Add(currentTargetId.Value);
            currentTargetId = null;
            state = State.Idle;
            return;
        }

        if (DateTime.UtcNow - stateEnteredAt > UnlockWaitTimeout)
            SkipCurrent(Loc.T("Freischalten hat nicht geklappt", "unlocking did not go through"));
    }

    /// <summary>
    /// Nur im Simulation-Modus erreicht (siehe currentTargetWasUnlockedAtStart) - wartet, bis der
    /// Spieler das durch den Testklick geöffnete native Menü ("SelectString"/"Teleport") selbst
    /// wieder schließt, und zieht danach automatisch zum nächsten Ziel in der Zone weiter. Öffnet
    /// sich aus irgendeinem Grund gar kein Menü (z.B. der Klick hat nicht gegriffen), wird nach der
    /// kurzen Anlaufzeit trotzdem sofort weitergemacht, statt für immer zu warten.
    /// </summary>
    private void UpdateSimulationWaitingForWindowClose()
    {
        if (DateTime.UtcNow - stateEnteredAt < SimulationWindowOpenGracePeriod)
            return;

        if (Plugin.IsAnyAddonVisible("SelectString", "Teleport"))
        {
            simulationWindowClosedAt = null;
            return;
        }

        // Kurze Nachlaufzeit NACH dem Zugehen des Menüs (siehe SimulationWindowCloseSettleDelay) -
        // ein Chat-Befehl (z.B. der Mount-Ruf fürs nächste Ziel), der direkt im selben Moment
        // abgesetzt wird, scheint sonst von der Schließ-Animation/dem UI-Fokuswechsel verschluckt zu
        // werden (beobachtet: /mount wird laut Log gesendet, aber nie aufgestiegen).
        simulationWindowClosedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - simulationWindowClosedAt.Value < SimulationWindowCloseSettleDelay)
            return;

        simulationWindowClosedAt = null;
        Plugin.Log.Info($"[AetheryteAutomation] UpdateSimulationWaitingForWindowClose(#{currentTargetId}): Menü zu (oder nie geöffnet) - weiter zum nächsten Ziel.");

        // Für diesen Durchlauf als erledigt markieren - sonst würde TryStartNext sofort wieder
        // genau dasselbe, weiterhin "freigeschaltete" Ziel als nächstgelegenes wählen und die
        // Simulation käme nie über den ersten Kristall hinaus.
        if (currentTargetId.HasValue)
            skippedIds.Add(currentTargetId.Value);

        currentTargetId = null;
        state = State.Idle;
        StatusText = Loc.T("Simulation: weiter zum nächsten Ziel...", "Simulation: moving to the next target...");
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
