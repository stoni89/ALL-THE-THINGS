using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

/// <summary>
/// Von QuestAutomation/HuntingLogAutomation gemeinsam gehaltener Helfer (siehe Configuration.
/// UseChocoboCompanion) - beschwört den Chocobo-Begleiter in der gewählten Stance (Configuration.
/// ChocoboStance), sobald eine Automation läuft, und beschwört automatisch neu, kurz bevor die
/// Beschwörungszeit abläuft. Läuft rein additiv NEBENHER (Tick() macht selbst nichts, das die
/// eigentliche Automation blockieren würde) - das Beschwören selbst hat nur eine kurze
/// Animationssperre, ein eigener wartender Zwischenzustand in jeder Automation wäre unnötige
/// Komplexität für denselben Effekt.
/// </summary>
public sealed class ChocoboCompanionSupport
{
    // Wie lange zwischen zwei Versuchen (Beschwören oder Stance setzen) mindestens gewartet wird -
    // verhindert Spam, falls ein Versuch aus irgendeinem Grund abgelehnt wird (z.B. noch auf
    // Abklingzeit, oder der Begleiter ist unmittelbar nach dem Beschwören noch nicht "angekommen").
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    // Ab wie viel verbleibender Beschwörungszeit automatisch neu beschworen wird, damit der
    // Begleiter nie ganz verschwindet (siehe Plugin.GetChocoboSummonTimeLeft).
    private const float ResummonThresholdSeconds = 60f;

    // Wie lange nach dem Abmounten (siehe Tick) noch gewartet wird, bevor der erste Beschwören-
    // Versuch startet - Condition[Mounted] wird schon VOR dem Ende der sichtbaren Absteige-/
    // Lande-Animation false, ein sofortiger Versuch mitten in dieser Animation würde sonst
    // fehlschlagen (gleiches Muster wie SightseeingAutomation.DismountSettleDelay).
    private static readonly TimeSpan DismountSettleDelay = TimeSpan.FromSeconds(1);

    // Wie lange nach einem Lande-Auftrag an vnavmesh (siehe TryRequestLanding) mindestens gewartet
    // wird, bevor ein neuer erteilt wird - der Pfad startet u.U. erst ein paar Frames später.
    private static readonly TimeSpan LandingRetryInterval = TimeSpan.FromSeconds(5);

    // Horizontaler Suchradius für den Bodenpunkt unter dem Charakter (vnavmesh PointOnFloor).
    private const float LandingSearchHalfExtentXZ = 10f;

    // Wie genau der Bodenpunkt erreicht werden muss - eng, damit wirklich gelandet (nicht nur
    // knapp darüber geschwebt) wird (gleicher Wert wie SightseeingAutomation.ApproachWaypointLandingTolerance).
    private const float LandingTolerance = 0.5f;

    // vnavmesh-IPC fürs Landen, falls beim blockierenden Start-Schritt noch in der Luft (siehe
    // Tick) - ein Absteige-Befehl allein wird vom Spiel in größerer Höhe schlicht ignoriert.
    // (Punkt, allowUnlandable, halfExtentXZ) -> Bodenpunkt oder null.
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> queryPointOnFloor;
    // (Ziel, fly, Toleranz) -> angenommen? Mit fly=false landet vnavmesh zuerst (wie bei SightseeingAutomation).
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;

    private DateTime lastLandingRequestAt = DateTime.MinValue;

    public ChocoboCompanionSupport()
    {
        queryPointOnFloor = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        pathfindAndMoveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
    }

    // Wie lange nach einem angenommenen Beschwören-Versuch gewartet wird, bis er tatsächlich als
    // beschworen zählt (Plugin.IsChocoboCompanionSummoned), bevor er als gescheitert gilt und ein
    // neuer Versuch erlaubt wird - großzügig, da UseAction bereits true zurückgeben kann, bevor
    // TimeLeft im Spielzustand tatsächlich aktualisiert ist.
    private static readonly TimeSpan SummonAttemptTimeout = TimeSpan.FromSeconds(6);

    private DateTime lastStanceAttemptAt = DateTime.MinValue;
    private DateTime lastSummonAttemptAt = DateTime.MinValue;
    private DateTime? dismountedAt;

    // Ob gerade ein Beschwören-Versuch "unterwegs" ist (UseAction wurde bereits ausgelöst, siehe
    // Tick) - verhindert, dass jeden RetryInterval-Tick erneut Gysahl Greens verbraucht werden,
    // während noch auf die tatsächliche Beschwörung gewartet wird. Wird erst wieder freigegeben,
    // sobald entweder wirklich beschworen ist ODER SummonAttemptTimeout ohne Erfolg verstrichen ist.
    private bool summonAttemptInFlight;
    private DateTime summonAttemptStartedAt = DateTime.MinValue;

    // Nur für die Diagnose-Logzeile (siehe Tick) - verhindert Log-Spam, solange durchgehend
    // gewartet wird, meldet aber jeden neuen Beginn/Ende einer Wartephase.
    private bool wasLockedLastTick;

    // Wie wasLockedLastTick, nur für das Warten auf ein natürliches Absteigen (siehe Tick).
    private bool wasWaitingForDismountLastTick;

    private DateTime lastDismountAttemptAt = DateTime.MinValue;

    // Verhindert, dass TrySetChocoboStance nach einem bereits erfolgreichen Setzen weiter alle
    // RetryInterval erneut aufgerufen wird - es gibt keinen zuverlässig auslesbaren "aktuelle
    // Stance"-Wert, an dem sich das sonst festmachen ließe. Wird bei jedem frischen Beschwören
    // zurückgesetzt (ein Wechsel der gewünschten Stance während der Begleiter schon draußen ist,
    // wird dadurch erst beim nächsten Beschwören übernommen).
    private bool stanceAppliedForCurrentSummon;

    /// <summary>
    /// Für den blockierenden Start-Schritt der Automationen (siehe QuestAutomation/
    /// HuntingLogAutomation State.SummoningChocobo) - true, sobald die gewünschte Stance seit dem
    /// letzten (frischen) Beschwören erfolgreich gesetzt wurde.
    /// </summary>
    public bool HasAppliedStanceForCurrentSummon => stanceAppliedForCurrentSummon;

    /// <summary>
    /// Ob (neu) beschworen werden müsste - Feature aktiv/freigeschaltet, Gysahl Greens vorhanden und
    /// der Begleiter gar nicht draußen oder mit weniger als ResummonThresholdSeconds Restzeit. Für
    /// die blockierenden Beschwören-Schritte der Automationen (Start und zwischen zwei Hunting-Log-
    /// Zielen, siehe HuntingLogAutomation.TryStartNext).
    /// </summary>
    public static bool NeedsSummon =>
        Plugin.UseChocoboCompanion
        && Plugin.IsChocoboCompanionUnlocked()
        && Plugin.GetGysahlGreensCount() > 0
        && (!Plugin.IsChocoboCompanionSummoned() || Plugin.GetChocoboSummonTimeLeft() < ResummonThresholdSeconds);

    /// <summary>Vor jedem neuen Automation-Lauf (siehe QuestAutomation/HuntingLogAutomation.Start).</summary>
    public void Reset()
    {
        lastStanceAttemptAt = DateTime.MinValue;
        lastSummonAttemptAt = DateTime.MinValue;
        dismountedAt = null;
        summonAttemptInFlight = false;
        summonAttemptStartedAt = DateTime.MinValue;
        wasLockedLastTick = false;
        wasWaitingForDismountLastTick = false;
        lastDismountAttemptAt = DateTime.MinValue;
        lastLandingRequestAt = DateTime.MinValue;
        stanceAppliedForCurrentSummon = false;
    }

    private bool IsLandingPathActive()
    {
        try
        {
            return pathIsRunning.InvokeFunc() || pathfindInProgress.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lässt vnavmesh zum Bodenpunkt direkt unter dem Charakter fliegen und dort landen - gibt
    /// false zurück, falls vnavmesh fehlt, kein Bodenpunkt gefunden wurde oder der Auftrag abgelehnt wurde.
    /// </summary>
    private bool TryRequestLanding()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        try
        {
            var floor = queryPointOnFloor.InvokeFunc(player.Position, false, LandingSearchHalfExtentXZ);
            if (floor == null)
            {
                Plugin.Log.Info($"[ChocoboCompanionSupport] Kein Bodenpunkt unter {player.Position} gefunden.");
                return false;
            }

            var accepted = pathfindAndMoveCloseTo.InvokeFunc(floor.Value, false, LandingTolerance);
            Plugin.Log.Info($"[ChocoboCompanionSupport] Lande bei {floor.Value} (von {player.Position}), angenommen={accepted}.");
            return accepted;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[ChocoboCompanionSupport] Landen per vnavmesh fehlgeschlagen.");
            return false;
        }
    }

    // Nur für die Diagnose-Logzeile direkt unten - verhindert Log-Spam (Tick läuft jeden Frame),
    // meldet aber in vernünftigen Abständen, WARUM (falls doch) gar nichts weiter passiert.
    private DateTime lastGateDiagnosticLogAt = DateTime.MinValue;
    private static readonly TimeSpan GateDiagnosticLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Muss jeden Frame aufgerufen werden, während die haltende Automation aktiv ist (siehe Klassenkommentar).</summary>
    /// <param name="allowDismount">
    /// Ob Tick() selbst abmounten darf, um beschwören zu können - nur im blockierenden Start-Schritt
    /// der Automationen (State.SummoningChocobo), nie während einer laufenden Reise.
    /// </param>
    public void Tick(bool allowDismount)
    {
        var useChocobo = Plugin.UseChocoboCompanion;
        var unlocked = Plugin.IsChocoboCompanionUnlocked();
        if (!useChocobo || !unlocked)
        {
            if (DateTime.UtcNow - lastGateDiagnosticLogAt > GateDiagnosticLogInterval)
            {
                lastGateDiagnosticLogAt = DateTime.UtcNow;
                Plugin.Log.Info($"[ChocoboCompanionSupport] Tick() bricht ab: UseChocoboCompanion={useChocobo}, IsChocoboCompanionUnlocked={unlocked}.");
            }

            return;
        }

        // Läuft gerade eine Zauberzeit oder die kurze Animationssperre eines Item-Einsatzes (z.B.
        // das Beschwören selbst) - abwarten, bis sie vorbei ist, statt mittendrin erneut das Item
        // zu benutzen (würde den laufenden Versuch abbrechen/neu starten).
        var casting = Plugin.Condition[ConditionFlag.Casting];
        var animationLocked = Plugin.IsAnimationLocked();
        if (casting || animationLocked)
        {
            if (!wasLockedLastTick)
                Plugin.Log.Info($"[ChocoboCompanionSupport] Warte (Casting={casting}, AnimationLocked={animationLocked}).");

            wasLockedLastTick = true;
            return;
        }

        wasLockedLastTick = false;

        if (Plugin.IsChocoboCompanionSummoned())
        {
            // Erst bestätigt, wenn die Restzeit tatsächlich über der Schwelle liegt - beim Neu-
            // Beschwören eines noch draußen stehenden Begleiters (Restzeit < 1 Minute) ist
            // "beschworen" schon vorher true, ein sofortiges Freigeben würde nach RetryInterval
            // gleich die nächsten Gysahl Greens verbrauchen.
            if (summonAttemptInFlight && Plugin.GetChocoboSummonTimeLeft() >= ResummonThresholdSeconds)
            {
                Plugin.Log.Info($"[ChocoboCompanionSupport] Beschworen bestätigt (TimeLeft={Plugin.GetChocoboSummonTimeLeft():F1}s).");
                summonAttemptInFlight = false;
            }

            // Noch genug Zeit übrig - nicht neu beschwören, sondern (falls noch nicht geschehen)
            // die gewünschte Stance setzen.
            if (Plugin.GetChocoboSummonTimeLeft() >= ResummonThresholdSeconds)
            {
                if (stanceAppliedForCurrentSummon)
                    return;

                // Beritten lehnt das Spiel Begleiter-Befehle ab ("Cannot execute at this time") -
                // die Stance wird einfach beim nächsten Absteigen nachgeholt, statt dafür extra
                // abzumounten (siehe auch UpdateSummoningChocobo der Automationen).
                if (Plugin.Condition[ConditionFlag.Mounted])
                    return;

                if (DateTime.UtcNow - lastStanceAttemptAt < RetryInterval)
                    return;

                lastStanceAttemptAt = DateTime.UtcNow;
                stanceAppliedForCurrentSummon = Plugin.TrySetChocoboStance(Plugin.ChocoboStance);
                Plugin.Log.Info($"[ChocoboCompanionSupport] Stance {Plugin.ChocoboStance} setzen, angenommen={stanceAppliedForCurrentSummon} (TimeLeft={Plugin.GetChocoboSummonTimeLeft():F1}s).");

                return;
            }
        }

        // Ab hier: nicht (mehr) beschworen, oder die Zeit läuft bald ab - neu beschwören nötig.
        // Erst abwarten, ob ein vorheriger Versuch noch durchkommen könnte, statt sofort erneut
        // Gysahl Greens zu verbrauchen.
        if (summonAttemptInFlight)
        {
            if (DateTime.UtcNow - summonAttemptStartedAt < SummonAttemptTimeout)
                return;

            Plugin.Log.Info("[ChocoboCompanionSupport] Voriger Beschwören-Versuch nach Timeout ohne Bestätigung aufgegeben - neuer Versuch.");
            summonAttemptInFlight = false;
        }

        // Beschwören schlägt fehl, solange man noch auf einem normalen Mount sitzt. Nur im
        // blockierenden Start-Schritt (allowDismount) selbst abmounten - mitten in einer Reise
        // (vnavmesh/Questionable fliegen/reiten gerade) kollidiert ein Abmount-Versuch nur mit
        // deren Steuerung (in der Luft bleibt der Charakter dann einfach hängen); stattdessen
        // abwarten, bis die Automation ohnehin absteigt (Kampf, NPC-Interaktion, ...).
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            dismountedAt = null;
            if (!allowDismount)
            {
                if (!wasWaitingForDismountLastTick)
                    Plugin.Log.Info("[ChocoboCompanionSupport] Beritten unterwegs - Beschwören wartet, bis abgestiegen.");

                wasWaitingForDismountLastTick = true;
                return;
            }

            wasWaitingForDismountLastTick = false;

            // Landeflug läuft noch - abwarten, erst danach absteigen.
            if (IsLandingPathActive())
                return;

            // Noch in der Luft - ein Absteige-Befehl wird in größerer Höhe vom Spiel ignoriert,
            // daher erst per vnavmesh zum Boden direkt darunter fliegen und landen lassen.
            var inFlight = Plugin.Condition[ConditionFlag.InFlight];
            if (inFlight && DateTime.UtcNow - lastLandingRequestAt >= LandingRetryInterval)
            {
                lastLandingRequestAt = DateTime.UtcNow;
                if (TryRequestLanding())
                {
                    // Dem Pfad Zeit zum Anlaufen geben, bevor unten doch schon abgestiegen wird.
                    lastDismountAttemptAt = DateTime.UtcNow;
                    return;
                }
            }

            // Gedrosselt statt jeden Frame - ein erneuter Dismount-Aufruf mitten im Sinkflug/in der
            // Absteige-Animation kann den laufenden Vorgang wieder abbrechen.
            if (DateTime.UtcNow - lastDismountAttemptAt < RetryInterval)
                return;

            lastDismountAttemptAt = DateTime.UtcNow;
            var accepted = Plugin.TryDismount();
            Plugin.Log.Info($"[ChocoboCompanionSupport] Beritten (InFlight={inFlight}) - Absteigen versucht, angenommen={accepted}.");
            return;
        }

        wasWaitingForDismountLastTick = false;

        dismountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - dismountedAt.Value < DismountSettleDelay)
            return;

        // Unbedingte Mindestpause vor JEDEM Versuch (egal ob der vorherige angenommen oder
        // abgelehnt wurde) - reine Zeit-Drossel, hängt bewusst an KEINEM Spielzustand (z.B.
        // IsActionOffCooldown), der sich für dieses Item als unzuverlässig herausgestellt hat.
        if (DateTime.UtcNow - lastSummonAttemptAt < RetryInterval)
            return;

        lastSummonAttemptAt = DateTime.UtcNow;

        Plugin.Log.Info("[ChocoboCompanionSupport] Versuche zu beschwören (Gysahl Greens)...");
        if (Plugin.TrySummonChocoboCompanion())
        {
            Plugin.Log.Info("[ChocoboCompanionSupport] Beschwören-Versuch angenommen - warte auf Bestätigung.");
            summonAttemptInFlight = true;
            summonAttemptStartedAt = DateTime.UtcNow;
            stanceAppliedForCurrentSummon = false;
        }
        else
        {
            Plugin.Log.Info("[ChocoboCompanionSupport] Beschwören-Versuch abgelehnt (Cooldown/keine Greens/nicht freigeschaltet).");
        }
    }
}
