using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

/// <summary>
/// Steuert das Fremdplugin "Questionable" (https://github.com/WigglyMuffin/Questionable) über
/// dessen Dalamud-IPC, um die aktuell fehlenden Quests einer Zone nacheinander automatisch
/// abzuarbeiten. Questionable selbst stellt dafür keine offizielle/dokumentierte IPC-Version
/// bereit - alle Aufrufe sind daher defensiv (try/catch, HasFunction-Prüfung), da das Plugin
/// jederzeit fehlen oder sich in einer neueren Version anders verhalten kann.
/// </summary>
public sealed class QuestAutomation
{
    private enum State
    {
        SummoningChocobo,
        Idle,
        WaitingForPickup,
        Running,
        TravelingHome,
    }

    // Wie lange maximal auf das Beschwören + Setzen der Stance gewartet wird, bevor trotzdem mit der
    // eigentlichen Automation begonnen wird (z.B. falls keine Gysahl Greens vorhanden sind) - siehe
    // UpdateSummoningChocobo.
    private static readonly TimeSpan ChocoboSummonWaitTimeout = TimeSpan.FromSeconds(10);

    // Wie lange nach dem Start einer Quest gewartet wird, bis Questionable sie tatsächlich
    // übernimmt (IsRunning == true) - reagiert es nicht, gilt die Quest als nicht unterstützt.
    private static readonly TimeSpan PickupTimeout = TimeSpan.FromSeconds(6);

    // Verhindert eine Endlosschleife, falls Questionable eine Quest zwar annimmt, aber nie
    // abschließt (z.B. weil ein manueller Schritt nötig ist) - nach so vielen Versuchen wird
    // dieselbe Quest überspringen.
    private const int MaxAttemptsPerQuest = 2;

    // Wie lange IsRunning DURCHGEHEND false sein muss, bevor eine laufende Quest als "fertig" gilt
    // (siehe State.Running in Update) - kurze, einzelne false-Meldungen (z.B. während eines quest-
    // internen Ladebildschirms bei einem Zonenwechsel mitten in der Quest) sollen nicht sofort als
    // Abschluss gewertet werden.
    private static readonly TimeSpan RunningFalseGracePeriod = TimeSpan.FromSeconds(6);

    // Wie lange maximal auf die Rückreise per Lifestream zur Startzone gewartet wird (siehe
    // TryTravelHome), bevor die Automation aufgibt statt endlos zu warten.
    private static readonly TimeSpan TravelHomeTimeout = TimeSpan.FromSeconds(60);

    // Nach "Lifestream.IsBusy() == false" kann es noch einen Moment dauern, bis Plugin.ClientState.
    // TerritoryType tatsächlich auf die neue Zone aktualisiert ist - ohne diese kurze Verzögerung
    // hält der nächste Update-Aufruf die Zone noch für die alte und stößt eine erneute (unnötige)
    // Rückreise an (siehe auch AetheryteAutomation.DistrictTravelSettleDelay).
    private static readonly TimeSpan TravelHomeSettleDelay = TimeSpan.FromSeconds(2);

    // Lifestream.IsBusy() wird bei der bezahlten Teleport-Aktion beobachtet schon lange VOR dem
    // tatsächlichen Abschluss wieder false (die echte Zauberzeit von ~5s plus Ladebildschirm laufen
    // noch, siehe UpdateTravelingHome) - ohne diese Mindestdauer (Zauberzeit + typischer
    // Ladebildschirm + kleiner Puffer) würde die Ankunftsprüfung viel zu früh laufen, fälschlich
    // "nicht angekommen" melden und sofort einen neuen Versuch starten, der dann an der
    // Teleport-Abklingzeit scheitert (accepted=False).
    private static readonly TimeSpan TeleportMinimumTravelDuration = TimeSpan.FromSeconds(10);

    // Verhindert eine Endlosschleife, falls Lifestream eine Rückreise wiederholt asynchron ablehnt
    // (siehe UpdateTravelingHome) - nach so vielen gescheiterten Versuchen wird die aktuelle Zone
    // stattdessen zur neuen Startzone, statt es immer wieder mit demselben Ergebnis zu versuchen.
    private const int MaxTravelHomeAttempts = 2;
    private int travelHomeFailureCount;

    private readonly ICallGateSubscriber<string, bool> startSingleQuest;
    private readonly ICallGateSubscriber<bool> isRunning;

    // Nur für die proaktive Ausgrau-Prüfung im Overlay (siehe IsRotationSolverAvailable) - Quest-
    // Automation steuert RotationSolver selbst NICHT an (anders als HuntingLogAutomation), aber
    // während Questionable läuft können durchaus kampfpflichtige Quest-Schritte auftreten, die ohne
    // eine laufende Kampf-Rotation ins Stocken geraten würden.
    private readonly ICallGateSubscriber<bool> rsrAutorotationActive;

    // questId -> "gesperrt"? Prüft nur (ohne Nebenwirkung), ob Questionable eine Quest aktuell
    // bearbeiten könnte - für die proaktive Support-Markierung im Overlay direkt beim Betreten
    // einer Zone (siehe RefreshSupportStatus), statt erst nach einem echten Start-Versuch.
    private readonly ICallGateSubscriber<string, bool> isQuestLocked;

    // aetheryteId, subIndex(0 = großer Aetheryte) -> angenommen? Volle (kostenpflichtige) Teleport-
    // Aktion für die Rückreise zur Startzone (siehe TryTravelHome). Der kostenlose Aethernetz-Sprung
    // (Lifestream.AethernetTeleportById) wird bewusst NICHT mehr versucht: TryTravelHome läuft laut
    // Update/State.Idle NUR, wenn die aktuelle Zone NICHT zur Startstadt gehört (isHome bereits
    // false) - ein Aethernetz-Sprung setzt aber voraus, dass man sich BEREITS in Reichweite des
    // Ziel-Netzwerks befindet, was dadurch nie zutrifft. Er wurde früher trotzdem versucht, meldete
    // "accepted=true" (Lifestream validiert das selbst nicht vorher) und scheiterte danach immer
    // asynchron mit "[Lifestream] Destination could not be found" im Chat, bevor endlich auf diesen
    // zuverlässigen bezahlten Teleport zurückgefallen wurde.
    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleport;

    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;
    private readonly ICallGateSubscriber<object> lifestreamAbort;

    private State state = State.Idle;
    private uint? currentQuestId;
    private string currentQuestName = string.Empty;
    private uint? homeTerritoryId;
    private DateTime stateEnteredAt;
    private DateTime? travelHomeFinishedAt;
    private DateTime? runningWentFalseAt;

    private readonly HashSet<uint> skippedQuestIds = new();
    private readonly Dictionary<uint, int> attemptCounts = new();

    // Bleibt (anders als skippedQuestIds/attemptCounts) über Start()/Stop()-Zyklen hinweg
    // bestehen - das ist eine dauerhaft gültige Erkenntnis ("Questionable kennt diese Quest
    // nicht"), keine Buchhaltung für den aktuellen Automation-Durchlauf. Für die rote
    // "Nicht unterstützt"-Markierung im Overlay, auch außerhalb einer laufenden Automation.
    private readonly HashSet<uint> notSupportedQuestIds = new();

    // Welche Quest-IDs bereits per IsQuestLocked geprüft wurden (unabhängig vom Ergebnis) - auch
    // das bleibt dauerhaft bestehen, damit nicht jeden Frame erneut per IPC nachgefragt wird.
    private readonly HashSet<uint> checkedSupportQuestIds = new();

    public bool IsKnownUnsupported(uint questId) => notSupportedQuestIds.Contains(questId);

    // Jede Statusänderung bleibt danach noch eine Weile sichtbar (auch nachdem IsActive schon
    // false ist) - sonst verschwindet der eigentliche Grund für ein Stoppen/Überspringen sofort
    // wieder, bevor man ihn lesen kann.
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

    public QuestAutomation()
    {
        startSingleQuest = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartSingleQuest");
        isRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        isQuestLocked = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestLocked");
        rsrAutorotationActive = Plugin.PluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");

        lifestreamTeleport = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        lifestreamIsBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lifestreamAbort = Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");

        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
    }

    /// <summary>
    /// Questionable meldet Fehler (z.B. "Failed to start task ...", feststeckende Bewegung) nicht
    /// über IsRunning, sondern nur als Chatzeile mit dem festen Präfix "[Questionable] " im
    /// XivChatType.Urgent-Kanal (per PrintError - der Sender bleibt dabei immer leer, daher wird
    /// hier auf den Text geprüft, nicht auf den Absender). Läuft gerade eine Quest, wird sie beim
    /// ersten Auftreten einer solchen Meldung übersprungen, statt endlos auf ein IsRunning==false
    /// zu warten, das so nie kommt.
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!IsActive || state == State.Idle)
            return;

        if (message.LogKind != XivChatType.Urgent)
            return;

        if (!message.Message.TextValue.StartsWith("[Questionable] ", StringComparison.Ordinal))
            return;

        SkipCurrentQuest(Loc.T("Questionable meldet einen Fehler im Chat", "Questionable reported an error in chat"));
    }

    /// <summary>
    /// Prüft, ob Questionable aktuell installiert/geladen ist (registrierte IPC-Provider).
    /// Nur eine Momentaufnahme - Questionable kann jederzeit nachgeladen/entladen werden.
    /// </summary>
    public bool IsQuestionableAvailable()
    {
        try
        {
            return startSingleQuest.HasFunction && isRunning.HasFunction;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prüft, ob RotationSolver Reborn aktuell installiert/geladen ist - siehe Feldkommentar an
    /// rsrAutorotationActive, warum das auch für die Quest-Automation relevant ist, obwohl sie
    /// selbst keine RotationSolver-IPC aufruft.
    /// </summary>
    public bool IsRotationSolverAvailable()
    {
        try
        {
            return rsrAutorotationActive.HasFunction;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prüft für alle noch nicht geprüften Quests der Zone (unabhängig davon, ob die Automation
    /// gerade läuft), ob Questionable sie unterstützt - über IsQuestLocked, das (anders als
    /// StartSingleQuest) rein lesend ist und nichts anstößt. So kann die rote "Nicht unterstützt"-
    /// Markierung im Overlay schon beim Betreten der Zone erscheinen, statt erst nachdem man die
    /// Automation einmal gestartet und einen echten Start-Versuch pro Quest abgewartet hat. Muss
    /// jeden Frame aufgerufen werden (wie Update) - geprüfte Quests werden übersprungen, es wird
    /// also nicht wiederholt nachgefragt.
    /// </summary>
    public void RefreshSupportStatus(IReadOnlyList<CollectibleEntry> missingQuestsInZone)
    {
        if (!IsQuestionableAvailable())
            return;

        try
        {
            if (!isQuestLocked.HasFunction)
                return;
        }
        catch
        {
            return;
        }

        foreach (var quest in missingQuestsInZone)
        {
            if (checkedSupportQuestIds.Contains(quest.Id))
                continue;

            try
            {
                // Gleiche ID-Umrechnung wie beim echten Start (siehe TryStartNext) - Questionable
                // erwartet überall die 16-Bit-Quest-ID des Spielclients, nicht die volle RowId.
                var locked = isQuestLocked.InvokeFunc(((ushort)quest.Id).ToString());
                checkedSupportQuestIds.Add(quest.Id);
                if (locked)
                    notSupportedQuestIds.Add(quest.Id);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Fehler beim Prüfen des Support-Status von Quest {quest.Id} ({quest.Name}).");
            }
        }
    }

    /// <summary>
    /// homeTerritoryId ist die (aufgelöste, siehe Plugin.ResolveEffectiveTerritoryId) Zone, in der
    /// die Automation gestartet wurde - führt eine Quest den Charakter anderswohin und endet dort
    /// auch (siehe TryTravelHome), reist die Automation danach automatisch per Lifestream wieder
    /// hierher zurück, statt einfach dort weiterzumachen, wo die Quest zufällig endete.
    /// </summary>
    public void Start(uint homeTerritoryId)
    {
        IsActive = true;
        currentQuestId = null;
        this.homeTerritoryId = homeTerritoryId;
        travelHomeFinishedAt = null;
        travelHomeFailureCount = 0;
        runningWentFalseAt = null;
        skippedQuestIds.Clear();
        attemptCounts.Clear();
        Plugin.ChocoboCompanionSupport.Reset();

        // Erst den Chocobo-Begleiter beschwören/die Stance setzen (siehe UpdateSummoningChocobo),
        // BEVOR überhaupt die erste Quest gestartet wird - nur, wenn das Feature aktiv und
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
    /// Questionable hat zwar keine Stopp-IPC (siehe Klassenkommentar), aber denselben harten Stopp
    /// wie sein eigener "Stop all actions now"-Knopf über den Chat-Befehl "/qst stop" - der bricht
    /// Bewegung/Quest/Sammeln bei Questionable sofort ab, nicht nur unser eigenes Nachschieben.
    /// </summary>
    public void Stop()
    {
        IsActive = false;
        state = State.Idle;
        currentQuestId = null;
        homeTerritoryId = null;
        StopLifestream();
        StopQuestionable();
    }

    /// <summary>
    /// Nur Questionable selbst hart stoppen (siehe Stop-Klassenkommentar zu "/qst stop"), OHNE
    /// unseren eigenen Automation-Zustand anzufassen - für den Moment vor einer selbst gesteuerten
    /// Lifestream-Rückreise (siehe Update/State.Idle), damit Questionable währenddessen nicht von
    /// sich aus weiterläuft und mit unserer Teleport-Aktion kollidiert.
    /// </summary>
    private void StopQuestionable()
    {
        try
        {
            Plugin.CommandManager.ProcessCommand("/qst stop");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim harten Stoppen von Questionable über /qst stop.");
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

    public void MarkUnavailable()
    {
        StatusText = Loc.T("Questionable nicht gefunden - bitte installieren.", "Questionable not found - please install it.");
    }

    /// <summary>
    /// Blockiert den eigentlichen Automation-Start, bis der Chocobo-Begleiter beschworen und die
    /// gewünschte Stance gesetzt ist (Plugin.ChocoboCompanionSupport.Tick() übernimmt das eigentliche
    /// Beschwören/Stance-Setzen, hier wird nur beobachtet, wann das erledigt ist) - gibt aber
    /// spätestens nach ChocoboSummonWaitTimeout auf (z.B. falls keine Gysahl Greens vorhanden sind),
    /// statt die Quest-Automation endlos zu blockieren.
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
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell fehlenden Quests DER
    /// STARTZONE (siehe Start) und der aktuell aufgelösten Zone des Spielers aufgerufen werden.
    /// Startet nach und nach jede Quest per Questionable-IPC und wartet jeweils, bis Questionable
    /// sie abgeschlossen hat (oder nicht unterstützt), bevor die nächste angestoßen wird. Landet
    /// der Charakter zwischendurch (durch eine Quest) in einer anderen Zone, wird zwischen zwei
    /// Quests automatisch zur Startzone zurückgereist (siehe TryTravelHome), bevor es weitergeht.
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> missingQuestsInZone, uint currentEffectiveTerritoryId)
    {
        if (!IsActive)
            return;

        Plugin.ChocoboCompanionSupport.Tick();

        try
        {
            switch (state)
            {
                case State.SummoningChocobo:
                    UpdateSummoningChocobo();
                    break;

                case State.Idle:
                    // Split-Hauptstädte (Ul'dah etc.) zählen als EIN Zuhause, egal in welchem
                    // Bezirk man gerade steht (siehe Plugin.GetSplitCityTerritories) - konsistent
                    // damit, wie missingQuestsInZone selbst zonenübergreifend zusammengestellt wird.
                    var isHome = !homeTerritoryId.HasValue
                                 || Plugin.GetSplitCityTerritories(homeTerritoryId.Value).Contains(currentEffectiveTerritoryId);
                    if (isHome)
                        TryStartNext(missingQuestsInZone);
                    else
                    {
                        Plugin.Log.Info($"[QuestAutomation] Idle: nicht daheim (homeTerritoryId={homeTerritoryId}, currentEffectiveTerritoryId={currentEffectiveTerritoryId}) - starte TryTravelHome.");
                        // Questionable erst hart stoppen, bevor wir selbst per Lifestream reisen -
                        // sonst kann es (z.B. weil es schon von sich aus zur nächsten erkannten
                        // Quest weiterlaufen will) mit unserer eigenen Teleport-Aktion kollidieren,
                        // was den Teleport-Erfolg/die Ankunftsprüfung verfälscht.
                        StopQuestionable();
                        TryTravelHome(currentEffectiveTerritoryId);
                    }
                    break;

                case State.WaitingForPickup:
                    if (isRunning.InvokeFunc())
                    {
                        state = State.Running;
                        StatusText = Loc.T($"Bearbeite: {currentQuestName}...", $"Processing: {currentQuestName}...");
                    }
                    else if (DateTime.UtcNow - stateEnteredAt > PickupTimeout)
                    {
                        SkipCurrentQuest(Loc.T("keine Reaktion von Questionable", "no response from Questionable"));
                    }
                    break;

                case State.Running:
                    if (isRunning.InvokeFunc())
                    {
                        runningWentFalseAt = null;
                    }
                    else
                    {
                        // Erst nach ein paar Sekunden DURCHGEHEND false als "fertig" werten, nicht
                        // schon beim ersten Mal: Bei quest-internen Zonenwechseln (z.B. "Tougher
                        // Than Leather" führt von Ul'dah nach Central Thanalan) meldet Questionable
                        // während des Ladebildschirms teils kurz IsRunning==false, obwohl die Quest
                        // gleich danach ganz normal weiterläuft. Ohne diese Gnadenfrist hätte das
                        // hier fälschlich "fertig" ausgelöst - und im nächsten Idle-Durchlauf, weil
                        // die aktuelle Zone dann nicht mehr die Startzone ist, eine Rückreise per
                        // Lifestream, die die laufende Quest komplett durcheinanderbringt (Charakter
                        // wird mitten aus der Quest herausteleportiert, während Questionable selbst
                        // munter weitermacht) und im schlimmsten Fall die ganze Automation stoppt.
                        runningWentFalseAt ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - runningWentFalseAt.Value > RunningFalseGracePeriod)
                        {
                            // Fertig (erledigt, abgebrochen oder Questionable ist von selbst gestoppt) -
                            // ob die Quest jetzt tatsächlich abgeschlossen ist, entscheidet die Liste
                            // beim nächsten Update: taucht sie noch auf, wird es (bis MaxAttemptsPerQuest) erneut versucht.
                            // Die Zonen-Prüfung (siehe oben) passiert erst im NÄCHSTEN Update-Aufruf im
                            // Idle-Zweig - nicht hier mitten in der Quest, sonst würde eine gerade von
                            // Questionable selbst durchgeführte Reise unterbrochen.
                            Plugin.Log.Info($"[QuestAutomation] Running->Idle: Quest '{currentQuestName}' ({currentQuestId}) fertig, currentEffectiveTerritoryId={currentEffectiveTerritoryId}.");
                            runningWentFalseAt = null;
                            state = State.Idle;
                            currentQuestId = null;
                        }
                    }
                    break;

                case State.TravelingHome:
                    UpdateTravelingHome(currentEffectiveTerritoryId);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler bei der Questionable-Automation - wird gestoppt.");
            StatusText = Loc.T("Fehler bei Questionable - Automation gestoppt.", "Error talking to Questionable - automation stopped.");
            Stop();
        }
    }

    /// <summary>
    /// Reist zurück zur Zone, in der die Automation gestartet wurde - per kostenpflichtiger
    /// Lifestream-Teleport-Aktion. Bewusst KEIN vorheriger Versuch über den kostenlosen Aethernetz-
    /// Sprung (siehe lifestreamTeleport-Feldkommentar) - der wäre an dieser Stelle immer aussichtslos,
    /// da TryTravelHome laut Update/State.Idle nur läuft, wenn die aktuelle Zone NICHT zur Startstadt
    /// gehört, ein Aethernetz-Sprung aber voraussetzt, bereits in deren Netzwerk-Reichweite zu sein.
    /// Schlägt auch der bezahlte Teleport ab (z.B. "Insufficient gil"), wird NICHT gewartet und die
    /// Rückreise nicht wiederholt: die aktuelle Zone wird stattdessen einfach zur neuen "Startzone",
    /// und es geht direkt mit den dortigen fehlenden Quests weiter (falls keine mehr da sind, endet
    /// die Automation dann ganz regulär über "Keine Quests mehr übrig"). Nur ohne Lifestream selbst
    /// wird sofort gestoppt, da dann gar kein Teleport möglich wäre.
    /// </summary>
    private void TryTravelHome(uint currentEffectiveTerritoryId)
    {
        if (!homeTerritoryId.HasValue)
        {
            state = State.Idle;
            return;
        }

        if (!IsLifestreamAvailable())
        {
            Plugin.Log.Info("[QuestAutomation] TryTravelHome: Lifestream nicht verfügbar - Automation wird gestoppt.");
            StatusText = Loc.T(
                "Kann nicht zur Startzone zurückreisen (Lifestream nicht gefunden) - Automation gestoppt.",
                "Can't travel back to the starting zone (Lifestream not found) - automation stopped.");
            Stop();
            return;
        }

        var homeDistrictIds = Plugin.GetSplitCityTerritories(homeTerritoryId.Value);
        Plugin.Log.Info($"[QuestAutomation] TryTravelHome: homeTerritoryId={homeTerritoryId}, homeDistrictIds=[{string.Join(",", homeDistrictIds)}], currentEffectiveTerritoryId={currentEffectiveTerritoryId}.");

        // Manche geteilte Hauptstädte (z.B. Ul'dah) haben nur EINEN großen Aetheryten für die ganze
        // Stadt, physisch in nur einem Bezirk - daher über alle Bezirke der Startzone suchen, nicht
        // nur exakt den, in dem gestartet wurde.
        uint? mainAetheryteId = null;
        foreach (var territory in homeDistrictIds)
        {
            mainAetheryteId = Plugin.FindUnlockedMainAetheryteId(territory);
            if (mainAetheryteId != null)
                break;
        }
        Plugin.Log.Info($"[QuestAutomation] TryTravelHome: mainAetheryteId={mainAetheryteId}.");

        // Kein Teleport möglich (kein Aetheryte dort freigeschaltet, oder der Teleport wurde
        // abgelehnt, z.B. "Insufficient gil") - statt endlos zu warten oder ganz zu stoppen, wird die
        // aktuelle Zone einfach zur neuen "Startzone": die Automation macht direkt hier mit den
        // dortigen fehlenden Quests weiter (gibt es dort keine mehr, beendet sie sich gleich danach
        // ganz regulär über "Keine Quests mehr übrig").
        var accepted = mainAetheryteId.HasValue && lifestreamTeleport.InvokeFunc(mainAetheryteId.Value, (byte)0);
        Plugin.Log.Info($"[QuestAutomation] TryTravelHome: bezahlter Teleport zu {mainAetheryteId} -> accepted={accepted}.");
        if (!accepted)
        {
            homeTerritoryId = currentEffectiveTerritoryId;
            state = State.Idle;
            StatusText = Loc.T(
                "Rückreise nicht möglich (z.B. zu wenig Gil) - mache stattdessen hier weiter.",
                "Trip back not possible (e.g. not enough gil) - continuing from here instead.");
            return;
        }

        state = State.TravelingHome;
        stateEnteredAt = DateTime.UtcNow;
        travelHomeFinishedAt = null;
        var teleportHomeZoneName = Plugin.GetZoneName(homeTerritoryId.Value);
        StatusText = Loc.T($"Teleportiere nach {teleportHomeZoneName}...", $"Teleporting to {teleportHomeZoneName}...");
    }

    private void UpdateTravelingHome(uint currentEffectiveTerritoryId)
    {
        // Siehe TeleportMinimumTravelDuration-Kommentar - Lifestream.IsBusy() allein wird der
        // tatsächlichen Reisedauer (echte Zauberzeit + Ladebildschirm) nicht zuverlässig gerecht,
        // daher zusätzlich eine Mindestdauer abwarten, bevor "nicht mehr beschäftigt" überhaupt als
        // "fertig" gewertet wird.
        var stillTraveling = lifestreamIsBusy.InvokeFunc() || DateTime.UtcNow - stateEnteredAt < TeleportMinimumTravelDuration;

        if (!stillTraveling)
        {
            // Kurz warten, bis sich Plugin.ClientState.TerritoryType tatsächlich auf die neue Zone
            // aktualisiert hat (siehe TravelHomeSettleDelay) - sonst hält der nächste Update-Aufruf
            // die Zone noch für die alte und reist prompt wieder los.
            travelHomeFinishedAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - travelHomeFinishedAt.Value < TravelHomeSettleDelay)
                return;

            travelHomeFinishedAt = null;

            // Lifestream kann eine Reise auch NACH dem angenommenen Auftrag noch asynchron
            // ablehnen (z.B. ein Ziel, das laut IsAetheryteUnlocked zwar freigeschaltet ist, aber von
            // Lifestream selbst nicht gefunden wird) - dabei wird IsBusy() genauso false wie bei
            // einer echten, erfolgreichen Ankunft. Ohne diese Prüfung würde TryStartNext im nächsten
            // Idle-Durchlauf die (unveränderte) Zone weiter als "nicht daheim" erkennen und denselben,
            // deterministisch wieder scheiternden Reiseversuch endlos wiederholen, statt jemals
            // weiterzumachen.
            var arrivedHome = Plugin.GetSplitCityTerritories(homeTerritoryId!.Value).Contains(currentEffectiveTerritoryId);
            Plugin.Log.Info($"[QuestAutomation] UpdateTravelingHome: Lifestream fertig, homeTerritoryId={homeTerritoryId}, currentEffectiveTerritoryId={currentEffectiveTerritoryId}, arrivedHome={arrivedHome}, travelHomeFailureCount={travelHomeFailureCount}.");
            if (!arrivedHome && ++travelHomeFailureCount <= MaxTravelHomeAttempts)
            {
                // Noch Versuche übrig - im nächsten Idle-Durchlauf erneut versuchen.
                state = State.Idle;
                return;
            }

            if (!arrivedHome)
            {
                homeTerritoryId = currentEffectiveTerritoryId;
                StatusText = Loc.T(
                    "Rückreise wiederholt gescheitert - mache stattdessen hier weiter.",
                    "Trip back repeatedly failed - continuing from here instead.");
            }

            travelHomeFailureCount = 0;
            state = State.Idle;
            return;
        }

        travelHomeFinishedAt = null;
        if (DateTime.UtcNow - stateEnteredAt > TravelHomeTimeout)
        {
            StopLifestream();
            state = State.Idle;
            StatusText = Loc.T("Rückreise dauert zu lange - abgebrochen", "Trip back took too long - aborted");
        }
    }

    private void TryStartNext(IReadOnlyList<CollectibleEntry> missingQuestsInZone)
    {
        var candidates = missingQuestsInZone.Where(q => !skippedQuestIds.Contains(q.Id)).ToList();
        if (candidates.Count == 0)
        {
            StatusText = Loc.T("Keine Quests mehr übrig.", "No quests left.");
            Stop();
            return;
        }

        // Die räumlich nächstgelegene annehmbare Quest zuerst, statt stur der Zonen-Listenreihenfolge
        // zu folgen - sonst läuft der Charakter quer durch die Zone, obwohl die nächste Quest direkt
        // neben der gerade abgeschlossenen liegt (die Vergabe-Position wird aus der Kartenkoordinate
        // zurückgerechnet, siehe Plugin.ResolveWorldPositionFromMapCoords). Quests ohne auflösbare
        // Position fallen ans Ende, statt die Sortierung ganz abzubrechen.
        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var next = candidates
            .OrderBy(q => q.HasVendorLocation
                ? Plugin.ResolveWorldPositionFromMapCoords(q.MapId, q.VendorMapX, q.VendorMapY) is { } questPos
                    ? Vector3.Distance(playerPos, questPos)
                    : float.MaxValue
                : float.MaxValue)
            .First();

        var attempts = attemptCounts.GetValueOrDefault(next.Id, 0) + 1;
        attemptCounts[next.Id] = attempts;
        if (attempts > MaxAttemptsPerQuest)
        {
            skippedQuestIds.Add(next.Id);
            StatusText = Loc.T($"Übersprungen (zu oft versucht): {next.Name}", $"Skipped (too many attempts): {next.Name}");
            return;
        }

        // Questionable erwartet die "echte" Quest-ID des Spielclients (16 Bit, wie sie z.B. auch
        // QuestManager.IsQuestComplete nutzt), nicht die volle Lumina-Excel-RowId (die ab 0x10000
        // zählt) - sonst wird jede einzelne Quest fälschlich als "unbekannt" abgelehnt.
        var accepted = startSingleQuest.InvokeFunc(((ushort)next.Id).ToString());
        if (!accepted)
        {
            skippedQuestIds.Add(next.Id);
            notSupportedQuestIds.Add(next.Id);
            StatusText = Loc.T($"Nicht unterstützt: {next.Name}", $"Not supported: {next.Name}");
            return;
        }

        currentQuestId = next.Id;
        currentQuestName = next.Name;
        state = State.WaitingForPickup;
        stateEnteredAt = DateTime.UtcNow;
        runningWentFalseAt = null;
        StatusText = Loc.T($"Starte: {next.Name}...", $"Starting: {next.Name}...");
    }

    private void SkipCurrentQuest(string reason)
    {
        if (currentQuestId.HasValue)
            skippedQuestIds.Add(currentQuestId.Value);

        StatusText = Loc.T($"Übersprungen ({reason})", $"Skipped ({reason})");
        state = State.Idle;
        currentQuestId = null;
        runningWentFalseAt = null;
    }
}
