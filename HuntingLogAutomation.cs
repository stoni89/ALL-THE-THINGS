using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

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
        SummoningChocobo,
        Idle,
        Mounting,
        MovingTo,
        SearchingMonster,
        ApproachingMonster,
        DismountingForFight,
        Fighting,
        FinishingCombat,
    }

    // Wie lange maximal auf das Beschwören + Setzen der Stance gewartet wird, bevor trotzdem mit der
    // eigentlichen Automation begonnen wird (z.B. falls keine Gysahl Greens vorhanden sind) - siehe
    // UpdateSummoningChocobo.
    private static readonly TimeSpan ChocoboSummonWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(6);

    // Wie lange nach dem Aufsteigen (Condition[Mounted] wird VOR dem Ende der sichtbaren
    // Aufsteige-Animation true) noch gewartet wird, bevor der erste Laufauftrag losgeschickt wird -
    // ohne diese kurze Verzögerung war Plugin.CanFly (siehe BeginPathfind) in genau diesem Moment
    // manchmal noch false, wodurch der Charakter beritten am Boden lief statt zu fliegen (siehe
    // Git-Historie/Nutzer-Report "ist er nur gelaufen mit dem Mount anstatt zu fliegen").
    private static readonly TimeSpan MountSettleDelay = TimeSpan.FromSeconds(1);

    // Wie lange nach dem Erreichen der Angriffsreichweite auf das tatsächliche Abmounten gewartet
    // wird (siehe UpdateDismountingForFight), bevor trotzdem mit dem Kampf begonnen wird - eine
    // Notbremse, damit ein einzelner hartnäckiger Fall (z.B. noch mitten im Landeanflug) die
    // Automation nicht für immer blockiert.
    private static readonly TimeSpan DismountForFightTimeout = TimeSpan.FromSeconds(8);

    // Wie lange nach dem tatsächlichen Abmounten (Condition[Mounted] wird VOR dem Ende der
    // sichtbaren Absteige-/Lande-Animation false) noch gewartet wird, bevor der Kampf beginnt -
    // ohne diese Verzögerung lief RotationSolver/der Kampfbeginn teils noch mitten in der
    // Landeanimation ins Leere.
    private static readonly TimeSpan DismountSettleDelay = TimeSpan.FromSeconds(1);

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
    private DateTime lastRemountAttempt = DateTime.MinValue;
    private readonly NavigationStuckDetector stuckDetector = new();
    private DateTime? mountedAt;
    private DateTime? dismountedAt;

    // Siehe Stop()/ForceStop() - statt MITTEN im Kampf RotationSolver abzuschalten (Charakter bliebe
    // angeschlagen und wehrlos stehen), wird der eigentliche Stopp zurückgehalten, bis der aktuell
    // laufende Kampf zu Ende ist.
    private bool stopRequested;
    private DateTime stopRequestedAt;
    private static readonly TimeSpan StopAfterCombatTimeout = TimeSpan.FromMinutes(2);
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
        currentTargetEntry = null;
        skippedIds.Clear();
        attemptCounts.Clear();
        stopRequested = false;
        mountedAt = null;
        dismountedAt = null;
        Plugin.ChocoboCompanionSupport.Reset();

        // Erst den Chocobo-Begleiter beschwören/die Stance setzen (siehe UpdateSummoningChocobo),
        // BEVOR überhaupt das erste Ziel angelaufen wird - nur, wenn das Feature aktiv und
        // freigeschaltet ist, sonst direkt wie bisher.
        if (Plugin.UseChocoboCompanion && Plugin.IsChocoboCompanionUnlocked())
        {
            state = State.SummoningChocobo;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T("Beschwöre Chocobo-Begleiter...", "Summoning Chocobo Companion...");
        }
        else
        {
            state = State.Idle;
            StatusText = Loc.T("Automation gestartet...", "Automation started...");
        }
    }

    /// <summary>
    /// Wird MITTEN in einem Kampf (state == Fighting, tatsächlich im Kampf laut Spiel) nicht sofort
    /// ausgeführt, sonst bliebe der Charakter angeschlagen und ohne Gegenwehr stehen (RotationSolver
    /// wäre schon abgeschaltet) - stattdessen erst gemerkt (siehe Update/ForceStop) und der aktuelle
    /// Kampf zu Ende gebracht, bevor wirklich gestoppt wird. In jedem anderen Zustand (noch am
    /// Laufen/Suchen, kein echter Kampf) unverändert sofortiger Stopp wie bisher.
    /// </summary>
    public void Stop()
    {
        if ((state == State.Fighting || state == State.FinishingCombat) && Plugin.Condition[ConditionFlag.InCombat])
        {
            if (stopRequested)
                return;

            stopRequested = true;
            stopRequestedAt = DateTime.UtcNow;
            StatusText = Loc.T("Beende aktuellen Kampf, dann Stopp...", "Finishing current fight, then stopping...");
            return;
        }

        ForceStop();
    }

    private void ForceStop()
    {
        IsActive = false;
        state = State.Idle;
        currentTargetEntry = null;
        stopRequested = false;
        StopPath();
        ClearRotationSolverPriority();
        SetRotationSolverAutoMode(false);
        Plugin.ClearNavigationTarget();
    }

    public void MarkUnavailable()
    {
        StatusText = Loc.T("vnavmesh nicht gefunden - bitte installieren.", "vnavmesh not found - please install it.");
    }

    /// <summary>
    /// Blockiert den eigentlichen Automation-Start, bis der Chocobo-Begleiter beschworen und die
    /// gewünschte Stance gesetzt ist (Plugin.ChocoboCompanionSupport.Tick() übernimmt das eigentliche
    /// Beschwören/Stance-Setzen, hier wird nur beobachtet, wann das erledigt ist) - gibt aber
    /// spätestens nach ChocoboSummonWaitTimeout auf (z.B. falls keine Gysahl Greens vorhanden sind),
    /// statt die Hunting-Log-Automation endlos zu blockieren.
    /// </summary>
    private void UpdateSummoningChocobo()
    {
        var settled = !Plugin.UseChocoboCompanion || !Plugin.IsChocoboCompanionUnlocked();
        if (!settled)
        {
            settled = Plugin.IsChocoboCompanionSummoned()
                ? Plugin.ChocoboCompanionSupport.HasAppliedStanceForCurrentSummon || !Plugin.IsChocoboStanceUnlocked(Plugin.ChocoboStance)
                : Plugin.GetGysahlGreensCount() == 0;
        }

        if (settled || DateTime.UtcNow - stateEnteredAt > ChocoboSummonWaitTimeout)
        {
            state = State.Idle;
            StatusText = Loc.T("Automation gestartet...", "Automation started...");
        }
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

        Plugin.ChocoboCompanionSupport.Tick();

        // Zurückgehaltener Stopp (siehe Stop()) - sobald wirklich kein Kampf mehr läuft (oder die
        // Notbremse StopAfterCombatTimeout greift, falls InCombat aus irgendeinem Grund hängen
        // bleibt), jetzt tatsächlich stoppen, bevor der normale Zustandsautomat weiterläuft.
        if (stopRequested && (!Plugin.Condition[ConditionFlag.InCombat] || DateTime.UtcNow - stopRequestedAt > StopAfterCombatTimeout))
        {
            ForceStop();
            return;
        }

        try
        {
            switch (state)
            {
                case State.SummoningChocobo:
                    UpdateSummoningChocobo();
                    break;

                case State.Idle:
                    // Nicht mit dem nächsten Ziel weitermachen, während ein Stopp aussteht - nur
                    // noch abwarten, bis der oben geprüfte Kampf-Zustand den eigentlichen Stopp
                    // auslöst.
                    if (!stopRequested)
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

                case State.DismountingForFight:
                    UpdateDismountingForFight(huntingLogEntriesInZone);
                    break;

                case State.Fighting:
                    UpdateFighting(huntingLogEntriesInZone);
                    break;

                case State.FinishingCombat:
                    UpdateFinishingCombat();
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

        if (Plugin.TryRequestAetheryteMount())
        {
            state = State.Mounting;
            stateEnteredAt = DateTime.UtcNow;
            mountedAt = null;
            StatusText = Loc.T($"Rufe Mount, dann: {entry.Name}...", $"Summoning mount, then: {entry.Name}...");
            return;
        }

        BeginPathfind();
    }

    private void BeginPathfind()
    {
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var canFly = Plugin.CanFly;
        var accepted = false;
        var triedFlying = false;

        // Fliegend nur versuchen, wenn Plugin.CanFly gerade true ist - sonst nimmt vnavmesh einen
        // Flugauftrag teils trotzdem an, obwohl der Charakter gar nicht abheben kann, und hüpft nur
        // sinnlos am Boden herum statt zu laufen.
        if (mounted && canFly)
        {
            triedFlying = true;
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, true, PathTolerance);
        }

        if (!accepted)
            accepted = pathfindAndMoveCloseTo.InvokeFunc(currentTargetPosition, false, PathTolerance);

        // Diagnose für den Nutzer-Report "läuft statt zu fliegen" - zeigt beim nächsten Auftreten
        // genau, ob mounted/CanFly falsch waren oder der Flugversuch von vnavmesh abgelehnt wurde.
        Plugin.Log.Info($"[HuntingLogAutomation] BeginPathfind({currentTargetEntry?.Name}): mounted={mounted}, canFly={canFly}, " +
                         $"triedFlying={triedFlying}, accepted={accepted} ({(triedFlying && !accepted ? "Flugversuch abgelehnt, auf Boden zurückgefallen" : triedFlying ? "fliegend angenommen" : "gar nicht erst fliegend versucht")}).");

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
            // Kurz abwarten, bevor der erste Laufauftrag losgeschickt wird - siehe
            // MountSettleDelay-Kommentar (Plugin.CanFly kann direkt nach Condition[Mounted]==true
            // noch kurz hinterherhinken).
            mountedAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - mountedAt.Value < MountSettleDelay)
                return;

            mountedAt = null;
            BeginPathfind();
            return;
        }

        mountedAt = null;
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

            // Falls unterwegs durch Schwimmen zwangsweise abgestiegen wurde - sobald wieder Land
            // erreicht ist, erneut aufsitzen.
            Plugin.TryRemountAfterForcedDismount(ref lastRemountAttempt);

            // Steckengeblieben (z.B. gegen eine Wand) - Pfad neu anfordern statt untätig zu warten.
            if (stuckDetector.CheckStuck(playerPos))
            {
                Plugin.Log.Info($"[HuntingLogAutomation] UpdateMoving({currentTargetEntry.Name}): scheinbar steckengeblieben - Laufweg wird neu angefordert.");
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

            // Beritten lassen sich die meisten Klassen-Aktionen (und damit RotationSolver) gar nicht
            // ausführen - das Absteigen passiert nicht von allein, nur weil man in Reichweite ist
            // (erst ein tatsächlicher Kampfbeginn würde es erzwingen, aber genau dafür braucht es ja
            // erst die Aktionen). NICHT sofort in den Kampf übergehen - erst in
            // UpdateDismountingForFight wirklich BESTÄTIGEN, dass Condition[Mounted] auch tatsächlich
            // false geworden ist (z.B. nach einem fliegenden Anflug braucht das einen Moment), sonst
            // bleibt RotationSolver wirkungslos, weil der Charakter noch beritten ist.
            Plugin.TryDismount();
            dismountedAt = null;

            state = State.DismountingForFight;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Steige ab: {FreshName(entries)}...", $"Dismounting: {FreshName(entries)}...");
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

    /// <summary>
    /// Wartet NACH dem Anlaufen (siehe UpdateApproachingMonster), bis Condition[Mounted] auch
    /// tatsächlich false geworden ist (plus eine kurze Absteige-/Lande-Settle-Zeit), bevor
    /// RotationSolver-Priorität/Auto-Modus gesetzt und in den eigentlichen Kampf übergegangen wird -
    /// ohne diese Bestätigung blieb RotationSolver nach einem fliegenden Anflug manchmal wirkungslos,
    /// weil der Charakter noch beritten war (siehe Klassenkommentar-Nutzer-Report). Gibt spätestens
    /// nach DismountForFightTimeout auf und kämpft trotzdem, statt für immer hängen zu bleiben.
    /// </summary>
    private void UpdateDismountingForFight(IReadOnlyList<CollectibleEntry> entries)
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        if (!StillNeeded(entries, currentTargetEntry.Id))
        {
            // Schon erledigt (z.B. von jemand anderem mitgetötet), bevor überhaupt richtig gekämpft
            // wurde - trotzdem über State.FinishingCombat, nicht direkt FinishCurrent(): durch das
            // Anlaufen/Anvisieren kann der Charakter längst im Kampf stecken (Aggro), auch ohne dass
            // RotationSolver hier je aktiv war.
            state = State.FinishingCombat;
            stateEnteredAt = DateTime.UtcNow;
            return;
        }

        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            Plugin.TryDismount();
            dismountedAt = null;

            if (DateTime.UtcNow - stateEnteredAt > DismountForFightTimeout)
                BeginFighting(entries);

            return;
        }

        dismountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - dismountedAt.Value < DismountSettleDelay)
            return;

        BeginFighting(entries);
    }

    /// <summary>
    /// Setzt RotationSolver-Priorität/Auto-Modus und wechselt in State.Fighting - aufgerufen erst
    /// NACHDEM das Abmounten bestätigt ist (siehe UpdateDismountingForFight). Sucht das Monster
    /// bewusst noch einmal frisch (statt die Referenz aus UpdateApproachingMonster weiterzureichen) -
    /// zwischen Ankunft und bestätigtem Abmounten kann eine kurze Zeit vergangen sein.
    /// </summary>
    private void BeginFighting(IReadOnlyList<CollectibleEntry> entries)
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
        var monster = Plugin.FindNearestLiveMonster(currentTargetEntry.BNpcNameId!.Value, playerPos, MonsterSearchRadius);
        if (monster == null)
        {
            state = State.SearchingMonster;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Suche Monster: {FreshName(entries)}...", $"Looking for monster: {FreshName(entries)}...");
            return;
        }

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
        Plugin.Log.Info($"[HuntingLogAutomation] BeginFighting({currentTargetEntry.Name}): beritten={Plugin.Condition[ConditionFlag.Mounted]}, " +
                         $"Ziel gesetzt={Plugin.IsCurrentTarget(monster)}, RotationSolver-Priorität gesetzt={rsrPriorityOk}, '/rotation Auto' gesendet.");

        state = State.Fighting;
        stateEnteredAt = DateTime.UtcNow;
        StatusText = Loc.T($"Kämpfe: {FreshName(entries)}...", $"Fighting: {FreshName(entries)}...");
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
            // Benötigte Anzahl erreicht - RotationSolver bewusst NICHT sofort abschalten, siehe
            // UpdateFinishingCombat (erst alle gerade kämpfenden Gegner zu Ende bekämpfen, egal ob
            // Hunting-Log-Ziel oder nicht, dann erst "/rotation Off" und weiter zum nächsten Ziel).
            state = State.FinishingCombat;
            stateEnteredAt = DateTime.UtcNow;
            StatusText = Loc.T($"Beende laufenden Kampf: {currentTargetEntry.Name}...", $"Finishing current fight: {currentTargetEntry.Name}...");
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

        // Sicherheitsnetz - falls der erste Absteige-Versuch beim Eintritt in den Kampf (siehe
        // UpdateApproachingMonster) aus irgendeinem Grund nicht gegriffen hat (z.B. Aktion war in
        // genau dem Frame noch nicht bereit). Wirkungslos/kein Aufruf, wenn schon abgestiegen.
        Plugin.TryDismount();

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
    /// Die benötigte Anzahl ist laut Hunting Log bereits erreicht (siehe UpdateFighting), aber der
    /// Charakter steckt evtl. noch mitten im Kampf (z.B. weitere, nicht zum Hunting-Log-Ziel
    /// zählende Gegner greifen noch an) - RotationSolver bleibt bewusst weiter aktiv, bis
    /// Condition[InCombat] tatsächlich wieder false wird, statt mitten im Gefecht abzuschalten und
    /// wehrlos loszufliegen (explizite Nutzeranforderung). Gibt spätestens nach
    /// StopAfterCombatTimeout auf (gleiche Notbremse/Begründung wie beim zurückgehaltenen Stop()).
    /// </summary>
    private void UpdateFinishingCombat()
    {
        if (currentTargetEntry == null)
        {
            state = State.Idle;
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat] && DateTime.UtcNow - stateEnteredAt < StopAfterCombatTimeout)
            return;

        FinishCurrent();
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
