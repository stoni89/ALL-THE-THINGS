using System;
using Dalamud.Game.ClientState.Conditions;

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

    /// <summary>Vor jedem neuen Automation-Lauf (siehe QuestAutomation/HuntingLogAutomation.Start).</summary>
    public void Reset()
    {
        lastStanceAttemptAt = DateTime.MinValue;
        lastSummonAttemptAt = DateTime.MinValue;
        dismountedAt = null;
        summonAttemptInFlight = false;
        summonAttemptStartedAt = DateTime.MinValue;
        wasLockedLastTick = false;
        stanceAppliedForCurrentSummon = false;
    }

    // Nur für die Diagnose-Logzeile direkt unten - verhindert Log-Spam (Tick läuft jeden Frame),
    // meldet aber in vernünftigen Abständen, WARUM (falls doch) gar nichts weiter passiert.
    private DateTime lastGateDiagnosticLogAt = DateTime.MinValue;
    private static readonly TimeSpan GateDiagnosticLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Muss jeden Frame aufgerufen werden, während die haltende Automation aktiv ist (siehe Klassenkommentar).</summary>
    public void Tick()
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
            if (summonAttemptInFlight)
                Plugin.Log.Info($"[ChocoboCompanionSupport] Beschworen bestätigt (TimeLeft={Plugin.GetChocoboSummonTimeLeft():F1}s).");

            summonAttemptInFlight = false;

            // Noch genug Zeit übrig - nicht neu beschwören, sondern (falls noch nicht geschehen)
            // die gewünschte Stance setzen.
            if (Plugin.GetChocoboSummonTimeLeft() >= ResummonThresholdSeconds)
            {
                if (stanceAppliedForCurrentSummon)
                    return;

                if (DateTime.UtcNow - lastStanceAttemptAt < RetryInterval)
                    return;

                lastStanceAttemptAt = DateTime.UtcNow;
                if (Plugin.TrySetChocoboStance(Plugin.ChocoboStance))
                    stanceAppliedForCurrentSummon = true;

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

        // Beschwören schlägt fehl, solange man noch auf einem normalen Mount sitzt - erst abmounten
        // und die Absteige-/Lande-Animation kurz abwarten, dann erst den eigentlichen Versuch starten.
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            Plugin.TryDismount();
            dismountedAt = null;
            return;
        }

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
