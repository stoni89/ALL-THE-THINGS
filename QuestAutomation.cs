using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Ipc;

namespace AllTheThings;

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
        Idle,
        WaitingForPickup,
        Running,
    }

    // Wie lange nach dem Start einer Quest gewartet wird, bis Questionable sie tatsächlich
    // übernimmt (IsRunning == true) - reagiert es nicht, gilt die Quest als nicht unterstützt.
    private static readonly TimeSpan PickupTimeout = TimeSpan.FromSeconds(6);

    // Verhindert eine Endlosschleife, falls Questionable eine Quest zwar annimmt, aber nie
    // abschließt (z.B. weil ein manueller Schritt nötig ist) - nach so vielen Versuchen wird
    // dieselbe Quest überspringen.
    private const int MaxAttemptsPerQuest = 2;

    private readonly ICallGateSubscriber<string, bool> startSingleQuest;
    private readonly ICallGateSubscriber<bool> isRunning;

    private State state = State.Idle;
    private uint? currentQuestId;
    private DateTime stateEnteredAt;
    private readonly HashSet<uint> skippedQuestIds = new();
    private readonly Dictionary<uint, int> attemptCounts = new();

    // Bleibt (anders als skippedQuestIds/attemptCounts) über Start()/Stop()-Zyklen hinweg
    // bestehen - das ist eine dauerhaft gültige Erkenntnis ("Questionable kennt diese Quest
    // nicht"), keine Buchhaltung für den aktuellen Automation-Durchlauf. Für die rote
    // "Nicht unterstützt"-Markierung im Overlay, auch außerhalb einer laufenden Automation.
    private readonly HashSet<uint> notSupportedQuestIds = new();

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

    public void Start()
    {
        IsActive = true;
        state = State.Idle;
        currentQuestId = null;
        skippedQuestIds.Clear();
        attemptCounts.Clear();
        StatusText = Loc.T("Automation gestartet...", "Automation started...");
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

        try
        {
            Plugin.CommandManager.ProcessCommand("/qst stop");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim harten Stoppen von Questionable über /qst stop.");
        }
    }

    public void MarkUnavailable()
    {
        StatusText = Loc.T("Questionable nicht gefunden - bitte installieren.", "Questionable not found - please install it.");
    }

    /// <summary>
    /// Muss jeden Frame (während das Overlay offen ist) mit den aktuell fehlenden Quests der
    /// Zone aufgerufen werden. Startet nach und nach jede Quest per Questionable-IPC und wartet
    /// jeweils, bis Questionable sie abgeschlossen hat (oder nicht unterstützt), bevor die
    /// nächste angestoßen wird.
    /// </summary>
    public void Update(IReadOnlyList<CollectibleEntry> missingQuestsInZone)
    {
        if (!IsActive)
            return;

        try
        {
            switch (state)
            {
                case State.Idle:
                    TryStartNext(missingQuestsInZone);
                    break;

                case State.WaitingForPickup:
                    if (isRunning.InvokeFunc())
                    {
                        state = State.Running;
                        StatusText = Loc.T("Questionable arbeitet an der Quest...", "Questionable is working on the quest...");
                    }
                    else if (DateTime.UtcNow - stateEnteredAt > PickupTimeout)
                    {
                        SkipCurrentQuest(Loc.T("keine Reaktion von Questionable", "no response from Questionable"));
                    }
                    break;

                case State.Running:
                    if (!isRunning.InvokeFunc())
                    {
                        // Fertig (erledigt, abgebrochen oder Questionable ist von selbst gestoppt) -
                        // ob die Quest jetzt tatsächlich abgeschlossen ist, entscheidet die Liste
                        // beim nächsten Update: taucht sie noch auf, wird es (bis MaxAttemptsPerQuest) erneut versucht.
                        state = State.Idle;
                        currentQuestId = null;
                    }
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

    private void TryStartNext(IReadOnlyList<CollectibleEntry> missingQuestsInZone)
    {
        var next = missingQuestsInZone.FirstOrDefault(q => !skippedQuestIds.Contains(q.Id));
        if (next == null)
        {
            StatusText = Loc.T("Keine Quests mehr übrig.", "No quests left.");
            Stop();
            return;
        }

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
        state = State.WaitingForPickup;
        stateEnteredAt = DateTime.UtcNow;
        StatusText = Loc.T($"Starte: {next.Name}...", $"Starting: {next.Name}...");
    }

    private void SkipCurrentQuest(string reason)
    {
        if (currentQuestId.HasValue)
            skippedQuestIds.Add(currentQuestId.Value);

        StatusText = Loc.T($"Übersprungen ({reason})", $"Skipped ({reason})");
        state = State.Idle;
        currentQuestId = null;
    }
}
