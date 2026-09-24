using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

/// <summary>
/// Läuft nacheinander alle aktuell noch fehlenden Hunting-Log-Ziele der Zone ab (siehe
/// Plugin.GetHuntingLogEntries - vom Overlay bereits auf den aktiven Rang der aktuellen Klasse gefiltert,
/// höhere Stufen siehe CollectibleEntry.HuntingLogRequiredRank) und
/// tötet dort die jeweils benötigte Anzahl. Anders als AetheryteAutomation/QuestAutomation bleibt
/// sie IMMER auf die aktuelle Zone beschränkt (kein Bezirkswechsel per Lifestream) - die übergebene
/// Eintragsliste ist ohnehin schon auf genau diese Zone eingegrenzt.
///
/// Für den eigentlichen Kampf wird das in den Einstellungen gewählte Kampf-Plugin angesteuert
/// ("RotationSolver Reborn" oder "Wrath Combo", siehe CombatPluginBridge/Configuration.CombatPlugin) -
/// beide in einem Modus, in dem sie NUR das von uns gesetzte Ziel angreifen (RotationSolver
/// "/rotation Manual", Wrath Combo per Lease mit Zielwahl "Manual"). Im Auto-Modus hatte
/// RotationSolver sich selbst Ziele gesucht und dabei auch nicht benötigte Gegner in der Nähe
/// gepullt; Beifang, der sich trotzdem selbst anhängt, wird per Plugin.FindNearestAttacker gezielt
/// anvisiert (siehe UpdateFighting/UpdateFinishingCombat/UpdateDefendingSelf). Bei RotationSolver
/// wird das Hunting-Log-Monster zusätzlich per AddPriorityNameID priorisiert.
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
    // UpdateSummoningChocobo. Großzügig, da ggf. erst aus der Luft gelandet werden muss (siehe
    // ChocoboCompanionSupport.TryRequestLanding).
    private static readonly TimeSpan ChocoboSummonWaitTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MountWaitTimeout = TimeSpan.FromSeconds(10);

    // Siehe UpdateMounting - Abstand zwischen zwei "/mount"-Versuchen, solange noch nicht aufgesessen.
    private static readonly TimeSpan MountRetryInterval = TimeSpan.FromSeconds(2);

    // Siehe TryUpgradeToFlyingPath - bei weniger Restweg lohnt das Neuplanen nicht mehr.
    private const float FlyingUpgradeMinDistance = 40f;

    // Ob der aktuelle Laufauftrag unberitten losgeschickt wurde (siehe BeginPathfind/TryUpgradeToFlyingPath) -
    // nur dann lohnt ein fliegendes Neuplanen; bei einem beritten abgelehnten Flugweg würde es sich endlos wiederholen.
    private bool currentPathStartedOnFoot;

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

    // Ziele, bei denen gerade kein lebendes Exemplar gefunden wurde (siehe DeferCurrent) - anders
    // als skippedIds NICHT endgültig: sie werden nur hinten angestellt, bis alle übrigen Ziele
    // erledigt/ebenfalls zurückgestellt sind, und dann erneut abgesucht (Respawn abwarten), statt
    // die Automation zu beenden, solange laut Hunting Log noch Kills fehlen.
    private readonly HashSet<uint> deferredIds = new();

    // Suchradien (Yalm, nacheinander) für den Bodenpunkt-Rückfall in StartMovingTo.
    private static readonly float[] FloorSearchHalfExtents = { 50f, 100f };

    // Siehe TryStartNext - Pause, bevor übersprungene Ziele erneut versucht werden.
    private static readonly TimeSpan SkippedRetryDelay = TimeSpan.FromSeconds(30);
    private DateTime? retrySkippedAt;

    // Siehe TryStartNext - Mindestabstand zwischen zwei blockierenden Beschwören-Schritten.
    private static readonly TimeSpan ChocoboSummonRecheckInterval = TimeSpan.FromSeconds(60);
    private DateTime lastChocoboSummonStepAt = DateTime.MinValue;

    // Siehe UpdateDefendingSelf.
    private bool isDefendingSelf;
    private DateTime lastDefendApproachAt = DateTime.MinValue;
    private static readonly TimeSpan DefendApproachRetryInterval = TimeSpan.FromSeconds(1);

    // Drossel für EnsureCombatMode.
    private static readonly TimeSpan CombatEnsureInterval = TimeSpan.FromSeconds(2);
    private DateTime lastCombatEnsureAt = DateTime.MinValue;

    // Bis zu dieser Entfernung zum gespeicherten Fundort wird beim erneuten Absuchen eines
    // zurückgestellten Ziels direkt vor Ort weitergesucht, statt erst neu hinzureisen/aufzumounten.
    private const float RespawnWaitDirectSearchDistance = 60f;

    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> navmeshIsReady;
    private readonly ICallGateSubscriber<Vector3?> queryFlagToPoint;

    // (Punkt, allowUnlandable, halfExtentXZ) -> Bodenpunkt oder null - Rückfall, falls FlagToPoint
    // nichts findet (siehe StartMovingTo, gleiches Vorgehen wie AetheryteAutomation).
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> queryPointOnFloor;

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
        queryPointOnFloor = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
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

    // Nur vnavmesh ist eine harte Voraussetzung (ohne das kann gar nicht gelaufen werden) - das
    // Kampf-Plugin (siehe CombatPluginBridge) ist über die Plugins-Seite als benötigt markiert.
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

    private void ClearCombatPriority()
    {
        if (currentPriorityNameId == null)
            return;

        Plugin.CombatPlugin.RemovePriorityNameId(currentPriorityNameId.Value);

        currentPriorityNameId = null;
    }

    /// <summary>Manual-Modus statt Auto - siehe Klassenkommentar (sonst werden fremde Gegner gepullt).</summary>
    /// <param name="force">
    /// Nur ForceStop (Automation endet wirklich): ausschalten auch mitten im Kampf. Sonst wird ein
    /// Ausschalten, solange noch gekämpft wird, bewusst ignoriert - Überspringen/Zurückstellen eines
    /// Ziels o.ä. darf den Charakter nicht wehrlos zwischen noch angreifenden Gegnern stehen lassen
    /// (siehe auch UpdateDefendingSelf, das danach die Gegenwehr übernimmt).
    /// </param>
    private void SetCombatMode(bool enabled, bool force = false)
    {
        if (!enabled && !force && Plugin.Condition[ConditionFlag.InCombat])
        {
            Plugin.Log.Info("[HuntingLogAutomation] Ausschalten des Kampfmodus zurückgehalten - noch im Kampf.");
            return;
        }

        Plugin.CombatPlugin.SetCombatMode(enabled);
    }

    /// <summary>
    /// Zentrale Gegenwehr außerhalb des eigentlichen Hunting-Kampfs (Fighting/FinishingCombat/
    /// DismountingForFight kümmern sich selbst darum): wird man unterwegs, beim Suchen oder beim
    /// Beschwören angegriffen, wird der normale Zustandsautomat angehalten, der Laufweg gestoppt, der
    /// Angreifer anvisiert (bei Bedarf hingelaufen) und RotationSolver im Manual-Modus gehalten, bis
    /// kein Kampf mehr läuft - danach geht es im vorherigen Zustand weiter. Gibt true zurück, solange
    /// verteidigt wird (Aufrufer überspringt dann den Zustandsautomaten für diesen Frame).
    /// </summary>
    private bool UpdateDefendingSelf(IReadOnlyList<CollectibleEntry> entries)
    {
        if (state is State.Fighting or State.FinishingCombat or State.DismountingForFight)
        {
            isDefendingSelf = false;
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
            var attacker = Plugin.FindNearestAttacker(playerPos);
            if (attacker != null || (isDefendingSelf && Plugin.HasLiveTarget()))
            {
                if (!isDefendingSelf)
                {
                    Plugin.Log.Info($"[HuntingLogAutomation] Angegriffen im Zustand {state} (von {attacker?.Name}) - wehre mich, bevor es weitergeht.");
                    isDefendingSelf = true;
                    StopPath();
                }

                if (!Plugin.HasLiveTarget() && attacker != null)
                    Plugin.SetTarget(attacker);

                Plugin.TryDismount();
                EnsureCombatMode();

                // RotationSolver bewegt den Charakter nicht selbst - steht das Ziel außer
                // Reichweite (z.B. Fernkämpfer-Gegner, Nahkampf-Klasse), gedrosselt hinlaufen.
                if (Plugin.TargetManager.Target is { } target
                    && Vector3.Distance(playerPos, target.Position) > AttackRange
                    && !pathIsRunning.InvokeFunc() && !Plugin.IsVnavPathfindInProgress()
                    && DateTime.UtcNow - lastDefendApproachAt > DefendApproachRetryInterval)
                {
                    lastDefendApproachAt = DateTime.UtcNow;
                    pathfindAndMoveCloseTo.InvokeFunc(target.Position, false, AttackRange);
                }

                StatusText = Loc.T($"Wehre mich gegen: {Plugin.TargetManager.Target?.Name}...", $"Defending against: {Plugin.TargetManager.Target?.Name}...");
                return true;
            }
        }

        if (!isDefendingSelf)
            return false;

        isDefendingSelf = false;
        StopPath();
        Plugin.Log.Info($"[HuntingLogAutomation] Kampf vorbei - setze Zustand {state} fort ({currentTargetEntry?.Name}).");

        // Unterbrochene Reise zum Ziel neu starten (Laufweg wurde für die Gegenwehr gestoppt) - ohne
        // dass die Unterbrechung als Fehlversuch zählt (siehe MaxAttemptsPerTarget).
        if (state is State.MovingTo or State.Mounting && currentTargetEntry is { } entry && StillNeeded(entries, entry.Id))
        {
            attemptCounts[entry.Id] = Math.Max(0, attemptCounts.GetValueOrDefault(entry.Id, 0) - 1);
            StartMovingTo(entry);
            return false;
        }

        // Anlauf zum Monster: neu anfragen lassen (siehe UpdateApproachingMonster), übrige Zustände
        // bekommen nur ihre Zeitmessung (Suche/Beschwören) frisch gestartet.
        hasSeenPathRunning = true;
        stateEnteredAt = DateTime.UtcNow;
        return false;
    }

    /// <summary>
    /// Solange gekämpft wird, jeden Frame aufrufen - schaltet das Kampf-Plugin (gedrosselt) wieder in
    /// den Manual-Modus, falls es laut IPC gerade NICHT aktiv ist. RotationSolver kann sich
    /// zwischendurch selbst abschalten (z.B. eigene "nach Kampfende ausschalten"-Einstellung,
    /// sobald das erste Ziel tot ist, obwohl noch ein weiterer Gegner angreift) - ein einmaliges
    /// "/rotation Manual" beim Kampfbeginn reicht deshalb nicht.
    /// </summary>
    private void EnsureCombatMode()
    {
        if (DateTime.UtcNow - lastCombatEnsureAt < CombatEnsureInterval)
            return;

        lastCombatEnsureAt = DateTime.UtcNow;
        if (Plugin.CombatPlugin.IsCombatModeActive())
            return;

        Plugin.Log.Info($"[HuntingLogAutomation] {CombatPluginBridge.DisplayName(CombatPluginBridge.GetEffective() ?? CombatPluginKind.RotationSolver)} ist mitten im Kampf nicht aktiv - schalte den Kampfmodus wieder ein.");
        SetCombatMode(true);
    }

    public void Start()
    {
        IsActive = true;
        currentTargetEntry = null;
        skippedIds.Clear();
        deferredIds.Clear();
        isDefendingSelf = false;
        retrySkippedAt = null;
        lastCombatEnsureAt = DateTime.MinValue;
        attemptCounts.Clear();
        stopRequested = false;
        mountedAt = null;
        dismountedAt = null;
        Plugin.ChocoboCompanionSupport.Reset();

        // Erst den Chocobo-Begleiter beschwören/die Stance setzen (siehe UpdateSummoningChocobo),
        // BEVOR überhaupt das erste Ziel angelaufen wird - nur, wenn das Feature aktiv und
        // freigeschaltet ist, sonst direkt wie bisher.
        lastChocoboSummonStepAt = DateTime.MinValue;
        if (Plugin.UseChocoboCompanion && Plugin.IsChocoboCompanionUnlocked())
        {
            EnterSummoningChocobo();
        }
        else
        {
            state = State.Idle;
            StatusText = Loc.T("Automation gestartet...", "Automation started...");
        }
    }

    /// <summary>Blockierender Beschwören-Schritt (siehe UpdateSummoningChocobo) - beim Start und zwischen zwei Zielen (siehe TryStartNext).</summary>
    private void EnterSummoningChocobo()
    {
        lastChocoboSummonStepAt = DateTime.UtcNow;
        state = State.SummoningChocobo;
        stateEnteredAt = DateTime.UtcNow;
        StatusText = Loc.T("Beschwöre Chocobo-Begleiter...", "Summoning Chocobo Companion...");
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
        // In JEDEM Zustand, nicht nur beim eigentlichen Hunting-Kampf - auch unterwegs angreifende
        // Gegner (siehe UpdateDefendingSelf) erst zu Ende bekämpfen.
        if (Plugin.Condition[ConditionFlag.InCombat])
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
        isDefendingSelf = false;
        state = State.Idle;
        currentTargetEntry = null;
        stopRequested = false;
        StopPath();

        // Zusätzlich zum IPC Path.Stop - "/vnav stop" bricht auch eine noch laufende Pfadsuche
        // (SimpleMove) ab, nicht nur das Abfliegen eines bereits fertigen Pfads.
        try
        {
            Plugin.CommandManager.ProcessCommand("/vnav stop");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Senden von /vnav stop.");
        }

        ClearCombatPriority();
        SetCombatMode(false, force: true);
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
            // Erst fertig, wenn nicht (mehr) beschworen werden muss (auch bei weniger als 1 Minute
            // Restzeit, siehe ChocoboCompanionSupport.NeedsSummon) UND die Stance steht.
            settled = !ChocoboCompanionSupport.NeedsSummon
                && (!Plugin.IsChocoboCompanionSummoned()
                    || Plugin.ChocoboCompanionSupport.HasAppliedStanceForCurrentSummon
                    || !Plugin.IsChocoboStanceUnlocked(Plugin.ChocoboStance)
                    // Beritten wird die Stance erst beim nächsten Absteigen gesetzt (siehe ChocoboCompanionSupport.Tick) -
                    // bereits beschworen reicht dann, statt den Start dafür zu blockieren.
                    || Plugin.Condition[ConditionFlag.Mounted]);
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
    /// auf Klasse gefiltert; Ziele noch nicht erreichter Rang-Stufen filtert CompactOverlayWindow heraus).
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> huntingLogEntriesInZone)
    {
        if (!IsActive)
            return;

        Plugin.ChocoboCompanionSupport.Tick(allowDismount: state == State.SummoningChocobo);

        // Zurückgehaltener Stopp (siehe Stop()) - sobald wirklich kein Kampf mehr läuft (oder die
        // Notbremse StopAfterCombatTimeout greift, falls InCombat aus irgendeinem Grund hängen
        // bleibt), jetzt tatsächlich stoppen, bevor der normale Zustandsautomat weiterläuft.
        if (stopRequested && (!Plugin.Condition[ConditionFlag.InCombat] || DateTime.UtcNow - stopRequestedAt > StopAfterCombatTimeout))
        {
            ForceStop();
            return;
        }

        if (UpdateDefendingSelf(huntingLogEntriesInZone))
            return;

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
            StatusText = Loc.T("Fehler bei vnavmesh/Kampf-Plugin - Automation gestoppt.", "Error talking to vnavmesh/the combat plugin - automation stopped.");
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
        var withPosition = entries.Where(e => e.WorldPosition.HasValue).ToList();
        if (withPosition.Count == 0)
        {
            StatusText = Loc.T(
                "Keine Hunting-Log-Ziele mit bekannter Position mehr in dieser Zone.",
                "No hunting log targets with a known position left in this zone.");
            Stop();
            return;
        }

        var candidates = withPosition.Where(e => !skippedIds.Contains(e.Id)).ToList();
        if (candidates.Count == 0)
        {
            // Alle noch fehlenden Ziele wurden in diesem Lauf übersprungen (z.B. vnavmesh-Fehler,
            // zu langer Kampf) - nicht beenden, solange laut Hunting Log noch etwas fehlt, sondern
            // nach einer Pause alle erneut versuchen (die Pause verhindert eine Endlosschleife, falls
            // ein Ziel dauerhaft unerreichbar ist).
            retrySkippedAt ??= DateTime.UtcNow + SkippedRetryDelay;
            var remaining = retrySkippedAt.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                StatusText = Loc.T(
                    $"Alle übrigen Ziele übersprungen - neuer Versuch in {remaining.TotalSeconds:F0}s...",
                    $"All remaining targets skipped - retrying in {remaining.TotalSeconds:F0}s...");
                return;
            }

            Plugin.Log.Info($"[HuntingLogAutomation] TryStartNext: alle {withPosition.Count} übrigen Ziele waren übersprungen - versuche sie erneut.");
            retrySkippedAt = null;
            skippedIds.Clear();
            attemptCounts.Clear();
            candidates = withPosition;
        }

        // Vor jedem neuen Ziel (der vorige Kampf ist hier bereits vorbei, siehe
        // UpdateFinishingCombat) prüfen, ob der Chocobo-Begleiter fehlt oder weniger als 1 Minute
        // Restzeit hat - dann erst (neu) beschwören, bevor weitergereist wird. Die Mindestpause
        // verhindert eine Endlosschleife, falls das Beschwören gerade nicht klappt (siehe
        // UpdateSummoningChocobo/ChocoboSummonWaitTimeout).
        if (ChocoboCompanionSupport.NeedsSummon && DateTime.UtcNow - lastChocoboSummonStepAt > ChocoboSummonRecheckInterval)
        {
            Plugin.Log.Info($"[HuntingLogAutomation] TryStartNext: Chocobo-Begleiter muss (neu) beschworen werden (TimeLeft={Plugin.GetChocoboSummonTimeLeft():F1}s) - erst beschwören, dann weiter.");
            EnterSummoningChocobo();
            return;
        }

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

        // Zurückgestellte Ziele (siehe deferredIds) erst dann wieder, wenn sonst nichts mehr übrig ist.
        var pool = candidates.Where(e => !deferredIds.Contains(e.Id)).ToList();
        if (pool.Count == 0)
        {
            // Alle verbliebenen Ziele warten auf einen Respawn - nicht aufgeben, sondern das
            // nächstgelegene erneut absuchen (direkt vor Ort, falls man ohnehin noch dort steht).
            deferredIds.Clear();
            var nearest = candidates.OrderBy(e => Vector3.Distance(playerPos, e.WorldPosition!.Value)).First();
            if (Vector3.Distance(playerPos, nearest.WorldPosition!.Value) <= RespawnWaitDirectSearchDistance)
            {
                Plugin.Log.Info($"[HuntingLogAutomation] TryStartNext: nur noch zurückgestellte Ziele - warte vor Ort auf Respawn von {nearest.Name}.");
                currentTargetEntry = nearest;
                currentTargetPosition = nearest.WorldPosition.Value;
                state = State.SearchingMonster;
                stateEnteredAt = DateTime.UtcNow;
                return;
            }

            pool = candidates;
        }

        var next = pool.OrderBy(e => Vector3.Distance(playerPos, e.WorldPosition!.Value)).First();
        StartMovingTo(next);
    }

    private void StartMovingTo(CollectibleEntry entry)
    {
        // VOR dem Versuchszähler - sonst zählte jeder Frame, in dem nur auf die Navmesh gewartet
        // wird (State bleibt Idle, TryStartNext läuft sofort erneut), als eigener Fehlversuch, und das
        // Ziel war nach zwei Frames schon "zu oft versucht".
        if (!navmeshIsReady.InvokeFunc())
        {
            StatusText = Loc.T("Warte auf vnavmesh-Navmesh für diese Zone...", "Waiting for vnavmesh's navmesh for this zone...");
            return;
        }

        var attempts = attemptCounts.GetValueOrDefault(entry.Id, 0) + 1;
        attemptCounts[entry.Id] = attempts;
        if (attempts > MaxAttemptsPerTarget)
        {
            Plugin.Log.Info($"[HuntingLogAutomation] StartMovingTo({entry.Name}): übersprungen (zu oft versucht).");
            skippedIds.Add(entry.Id);
            StatusText = Loc.T($"Übersprungen (zu oft versucht): {entry.Name}", $"Skipped (too many attempts): {entry.Name}");
            state = State.Idle;
            return;
        }

        // Genau derselbe Trick wie bei GoToAutomation/AetheryteAutomation: die Karten-Flagge auf
        // die (rohe, per Plugin.OpenEntryMap umgerechnete) Zielposition setzen und vnavmesh nach
        // einem begehbaren Punkt in deren Nähe fragen.
        Plugin.OpenEntryMap(entry, showMapWindow: false);
        var floorPoint = queryFlagToPoint.InvokeFunc();

        // Die Position stammt aus umgerechneten Wiki-Kartenkoordinaten (Y=0, siehe
        // HuntingLogPositions) und liegt daher auch mal knapp neben der Navmesh (Wasser, Fels) -
        // dann schrittweise im Umkreis nach Boden suchen, statt das Ziel gleich zu überspringen. Die
        // eigentliche Monstersuche vor Ort (MonsterSearchRadius) ist ohnehin großzügig.
        foreach (var halfExtent in FloorSearchHalfExtents)
        {
            if (floorPoint != null)
                break;

            floorPoint = queryPointOnFloor.InvokeFunc(entry.WorldPosition!.Value, true, halfExtent);
            Plugin.Log.Info($"[HuntingLogAutomation] StartMovingTo({entry.Name}): FlagToPoint leer, PointOnFloor({entry.WorldPosition}, halfExtentXZ={halfExtent}) = {floorPoint}");
        }

        if (floorPoint == null)
        {
            Plugin.Log.Info($"[HuntingLogAutomation] StartMovingTo({entry.Name}): übersprungen (kein erreichbarer Punkt laut vnavmesh).");
            skippedIds.Add(entry.Id);
            StatusText = Loc.T($"Übersprungen (nicht erreichbar): {entry.Name}", $"Skipped (not reachable): {entry.Name}");
            state = State.Idle;
            return;
        }

        currentTargetEntry = entry;
        currentTargetPosition = floorPoint.Value;

        if (Plugin.TryRequestAetheryteMount())
        {
            lastRemountAttempt = DateTime.UtcNow;
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

        currentPathStartedOnFoot = !mounted;
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
        {
            Plugin.Log.Info($"[HuntingLogAutomation] UpdateMounting({currentTargetEntry?.Name}): nach {MountWaitTimeout.TotalSeconds:F0}s nicht aufgesessen - laufe zu Fuß los (Aufsitzen wird unterwegs weiter versucht).");
            BeginPathfind();
            return;
        }

        // Direkt nach einem Kampf/Beschwören lehnt das Spiel den ersten "/mount" oft ab (Kampf-
        // Nachlauf, Animationssperre des Chocobo-Beschwörens/Stance-Setzens) - dann gedrosselt erneut
        // versuchen, statt nach dem einen Fehlversuch nur noch zu laufen.
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Casting] || Plugin.IsAnimationLocked())
            return;

        if (DateTime.UtcNow - lastRemountAttempt < MountRetryInterval)
            return;

        lastRemountAttempt = DateTime.UtcNow;
        Plugin.TryRequestAetheryteMount();
    }

    /// <summary>
    /// Zu Fuß losgelaufen (Aufsitzen hatte nicht geklappt), aber unterwegs doch aufgesessen (siehe
    /// UpdateMounting/TryRemountAfterForcedDismount) - bei noch weitem Weg einmal fliegend neu planen,
    /// statt beritten den ganzen Bodenweg abzulaufen.
    /// </summary>
    private bool TryUpgradeToFlyingPath(Vector3 playerPos)
    {
        if (!currentPathStartedOnFoot || !Plugin.Condition[ConditionFlag.Mounted] || !Plugin.CanFly)
            return false;

        if (Vector3.Distance(playerPos, currentTargetPosition) < FlyingUpgradeMinDistance)
            return false;

        // Wie in UpdateMounting kurz nach dem Aufsitzen warten, bis CanFly verlässlich ist.
        mountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - mountedAt.Value < MountSettleDelay)
            return false;

        mountedAt = null;
        Plugin.Log.Info($"[HuntingLogAutomation] UpdateMoving({currentTargetEntry?.Name}): unterwegs aufgesessen - plane fliegend neu.");
        StopPath();
        BeginPathfind();
        return true;
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
            if (TryUpgradeToFlyingPath(playerPos))
                return;

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

        if (Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
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
            // Angriffe während des (ggf. langen) Wartens auf einen Respawn übernimmt UpdateDefendingSelf.
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
                DeferCurrent(Loc.T("Monster nicht gefunden, warte auf Respawn", "Monster not found, waiting for respawn"));

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
        if (hasSeenPathRunning || Plugin.HasPathStartGraceElapsed(stateEnteredAt, PathStartGracePeriod))
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
        var priorityOk = Plugin.CombatPlugin.AddPriorityNameId(currentTargetEntry.BNpcNameId!.Value);

        SetCombatMode(true);
        Plugin.Log.Info($"[HuntingLogAutomation] BeginFighting({currentTargetEntry.Name}): beritten={Plugin.Condition[ConditionFlag.Mounted]}, " +
                         $"Ziel gesetzt={Plugin.IsCurrentTarget(monster)}, Priorität gesetzt={priorityOk}, Kampfmodus eingeschaltet ({CombatPluginBridge.DisplayName(CombatPluginBridge.GetEffective() ?? CombatPluginKind.RotationSolver)}).");

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

        // RotationSolver greift im Manual-Modus (siehe SetCombatMode) nur das aktuelle
        // Ziel an - ist das keins (mehr), wieder das Hunting-Log-Monster anvisieren. Ein noch lebendes
        // anderes Ziel (z.B. ein angreifender Beifang-Gegner) bewusst nicht wegnehmen.
        if (!Plugin.HasLiveTarget())
            Plugin.SetTarget(monster);

        EnsureCombatMode();

        if (DateTime.UtcNow - lastDiagnosticLogAt > DiagnosticLogInterval)
        {
            lastDiagnosticLogAt = DateTime.UtcNow;
            var combatActive = Plugin.CombatPlugin.IsCombatModeActive();
            Plugin.Log.Info($"[HuntingLogAutomation] Fighting({currentTargetEntry.Name}): Ziel={monster.Name}, HP={monster.CurrentHp}/{monster.MaxHp}, " +
                             $"aktuelles Spielziel={Plugin.IsCurrentTarget(monster)}, Kampf-Plugin aktiv laut IPC={combatActive}.");
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
        {
            // Manual-Modus: Gegner, die uns noch angreifen, selbst anvisieren - RotationSolver sucht
            // sich (anders als im Auto-Modus) keine neuen Ziele.
            if (!Plugin.HasLiveTarget())
            {
                var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? currentTargetPosition;
                var attacker = Plugin.FindNearestAttacker(playerPos);
                if (attacker != null)
                {
                    Plugin.Log.Info($"[HuntingLogAutomation] FinishingCombat: visiere angreifenden Gegner an: {attacker.Name}.");
                    Plugin.SetTarget(attacker);
                }
            }

            EnsureCombatMode();
            return;
        }

        FinishCurrent();
    }

    /// <summary>
    /// Aktuelles Ziel erfolgreich erledigt (die benötigte Anzahl ist laut Hunting Log erreicht) -
    /// RotationSolver-Priorität/Auto-Modus wieder zurücknehmen und mit dem nächsten Ziel weitermachen.
    /// </summary>
    private void FinishCurrent()
    {
        Plugin.Log.Info($"[HuntingLogAutomation] FinishCurrent({currentTargetEntry?.Name}): laut Hunting Log erledigt.");
        ClearCombatPriority();
        SetCombatMode(false);
        attemptCounts.Remove(currentTargetEntry!.Id);
        currentTargetEntry = null;
        state = State.Idle;
    }

    /// <summary>
    /// Wie SkipCurrent, aber nicht endgültig (siehe deferredIds) - für "gerade kein Exemplar
    /// gespawnt": das Ziel kommt wieder dran, sobald alle anderen erledigt/zurückgestellt sind.
    /// Setzt auch den Versuchszähler zurück, da erneutes Hinreisen zum Respawn kein Fehlversuch ist.
    /// </summary>
    private void DeferCurrent(string reason)
    {
        Plugin.Log.Info($"[HuntingLogAutomation] DeferCurrent({currentTargetEntry?.Name}): {reason}");
        if (currentTargetEntry != null)
        {
            deferredIds.Add(currentTargetEntry.Id);
            attemptCounts.Remove(currentTargetEntry.Id);
            StatusText = $"{reason}: {currentTargetEntry.Name}";
        }

        StopPath();
        ClearCombatPriority();
        SetCombatMode(false);
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
        ClearCombatPriority();
        SetCombatMode(false);
        currentTargetEntry = null;
        state = State.Idle;
    }
}
