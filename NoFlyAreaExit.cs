using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace TheExplorersCodex;

/// <summary>
/// Ausnahme für den Flugverbots-Bereich "The Eight Sentinels" in Mor Dhona (Nutzer-Report: von dort
/// aus gab es mit allen Automationen Probleme, auch mit dem Fußweg-Fallback aus Plugin.CanFly/
/// FlightPathUpgrade). Startet/läuft dort eine Automation, wird sie kurz angehalten und stattdessen:
/// 1. zum Crystal Gate gelaufen,
/// 2. mit dem Gate interagiert und das Ja/Nein-Fenster mit "Ja" bestätigt,
/// 3. gewartet, bis man draußen ist - danach wird die angehaltene Automation neu gestartet und fliegt
///    ganz normal zu ihrem nächsten Punkt.
/// Die Quest-Automation ist ausgenommen (Questionable steuert die Bewegung selbst).
/// </summary>
public sealed class NoFlyAreaExit
{
    private enum Phase
    {
        None,
        WalkingToGate,
        Interacting,
        WaitingForExit,
    }

    // Mor Dhona / Unterbereich "The Eight Sentinels" (PlaceName 942, per Spieldaten verifiziert).
    private const uint MorDhonaTerritoryId = 156;
    private const uint EightSentinelsPlaceNameId = 942;

    // Crystal Gate - Position vom Nutzer per /pos im Spiel ermittelt.
    private static readonly Vector3 CrystalGatePosition = new(569.58624f, -1.6762573f, -256.51663f);

    // Genau hier muss man stehen, um mit dem Gate interagieren zu können (vom Nutzer per /pos ermittelt -
    // mit der früheren groben Ankunftstoleranz blieb der Charakter zu weit weg stehen).
    private static readonly Vector3 GateStandPosition = new(569.4891f, -1.6827074f, -256.314f);
    private const float StandPositionTolerance = 0.3f;

    private const float GateArrivalDistance = 0.5f;
    private const float GateObjectSearchRadius = 8f;
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PathRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DismountSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(15);
    private const int MaxInteractAttempts = 3;

    // Siehe UpdateWalking - so lange wird vor dem Losgehen versucht aufzusitzen.
    private static readonly TimeSpan MountPhaseDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MountRetryInterval = TimeSpan.FromSeconds(2);
    private DateTime lastMountAttemptAt = DateTime.MinValue;

    // Nach einem Fehlschlag so lange nicht erneut versuchen (die Automation läuft dann normal weiter).
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    // Nach einem erfolgreichen Verlassen so lange nicht erneut auslösen - liegt das Ziel der
    // Automation selbst im Bereich, würde sie sonst gleich wieder hinausgeschickt.
    private static readonly TimeSpan SuccessCooldown = TimeSpan.FromSeconds(60);

    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;

    private Phase phase = Phase.None;
    private DateTime phaseStartedAt;
    private DateTime lastPathRequestAt = DateTime.MinValue;
    private DateTime? dismountedAt;
    private DateTime interactedAt;
    private int interactAttempts;
    private DateTime blockedUntil = DateTime.MinValue;
    private List<Action> pausedRestarts = new();

    public NoFlyAreaExit()
    {
        pathfindAndMoveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public string StatusText { get; private set; } = string.Empty;

    public bool IsBusy => phase != Phase.None;

    // Fallback, falls der Bereichsname nicht wie erwartet gemeldet wird: Flugverbot an der aktuellen
    // Stelle UND in der Nähe des Gates - dann ist man mit sehr hoher Wahrscheinlichkeit in diesem Bereich.
    private const float NoFlyFallbackRadius = 150f;

    private DateTime lastDiagnosticLogAt = DateTime.MinValue;

    /// <summary>
    /// Ob man in "The Eight Sentinels" steht - der Name kann als Haupt- ODER Unterbereich gemeldet
    /// werden (TerritoryInfo.AreaPlaceNameId/SubAreaPlaceNameId), zusätzlich als Rückfall
    /// "Flugverbot hier + nahe am Gate".
    /// </summary>
    private unsafe bool IsInEightSentinels(bool logDiagnostics)
    {
        if (Plugin.ClientState.TerritoryType != MorDhonaTerritoryId)
            return false;

        var territoryInfo = TerritoryInfo.Instance();
        if (territoryInfo == null)
            return false;

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var nearGate = Vector3.Distance(playerPos, CrystalGatePosition) < NoFlyFallbackRadius;
        var inside = territoryInfo->AreaPlaceNameId == EightSentinelsPlaceNameId
                     || territoryInfo->SubAreaPlaceNameId == EightSentinelsPlaceNameId
                     || (territoryInfo->FlyingDisabled && nearGate);

        if (logDiagnostics && DateTime.UtcNow - lastDiagnosticLogAt > TimeSpan.FromSeconds(10))
        {
            lastDiagnosticLogAt = DateTime.UtcNow;
            Plugin.Log.Info($"[NoFlyAreaExit] Mor Dhona: Area={territoryInfo->AreaPlaceNameId}, SubArea={territoryInfo->SubAreaPlaceNameId}, " +
                            $"FlyingDisabled={territoryInfo->FlyingDisabled}, Abstand zum Gate={Vector3.Distance(playerPos, CrystalGatePosition):F0} -> im Bereich={inside}.");
        }

        return inside;
    }

    /// <summary>
    /// Jeden Frame VOR den Automationen aufrufen. true = der Ausgang läuft gerade, die Automationen
    /// dürfen in diesem Frame NICHT weiterlaufen (siehe CompactOverlayWindow).
    /// </summary>
    /// <param name="automations">Alle vnavmesh-Automationen: läuft sie gerade + wie sie neu gestartet wird.</param>
    public bool Update(IReadOnlyList<(Func<bool> IsActive, Action Restart)> automations)
    {
        var active = automations.Where(a => a.IsActive()).ToList();

        if (phase == Phase.None)
        {
            if (active.Count == 0 || DateTime.UtcNow < blockedUntil || !IsInEightSentinels(logDiagnostics: true))
                return false;

            Plugin.Log.Info("[NoFlyAreaExit] In \"The Eight Sentinels\" - verlasse den Bereich über das Crystal Gate, bevor die Automation weiterläuft.");
            pausedRestarts = active.Select(a => a.Restart).ToList();
            StopPath();
            Plugin.SendGameChatCommand("/vnav stop");
            SetPhase(Phase.WalkingToGate);
            lastPathRequestAt = DateTime.MinValue;
            interactAttempts = 0;
        }

        // Automation(en) inzwischen vom Nutzer gestoppt - Ausgang abbrechen.
        if (active.Count == 0)
        {
            StopPath();
            Reset();
            return false;
        }

        try
        {
            switch (phase)
            {
                case Phase.WalkingToGate:
                    UpdateWalking();
                    break;
                case Phase.Interacting:
                    UpdateInteracting();
                    break;
                case Phase.WaitingForExit:
                    UpdateWaitingForExit();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[NoFlyAreaExit] Fehler - Ausnahme wird übersprungen.");
            Fail();
        }

        return phase != Phase.None;
    }

    private void UpdateWalking()
    {
        StatusText = Loc.T("Verlasse \"The Eight Sentinels\" über das Crystal Gate...", "Leaving \"The Eight Sentinels\" via the Crystal Gate...");

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? GateStandPosition;
        if (Vector3.Distance(playerPos, GateStandPosition) <= GateArrivalDistance)
        {
            StopPath();
            SetPhase(Phase.Interacting);
            dismountedAt = null;
            return;
        }

        if (DateTime.UtcNow - phaseStartedAt > WalkTimeout)
        {
            Plugin.Log.Info("[NoFlyAreaExit] Crystal Gate nicht erreicht.");
            Fail();
            return;
        }

        // Reiten geht hier (nur Fliegen nicht) - vor dem ersten Laufauftrag aufsitzen, dann beritten am
        // Boden zum Gate. Klappt das Aufsitzen nicht innerhalb von MountPhaseDuration, trotzdem losgehen.
        if (!Plugin.Condition[ConditionFlag.Mounted] && DateTime.UtcNow - phaseStartedAt < MountPhaseDuration)
        {
            if (!Plugin.Condition[ConditionFlag.Casting] && !Plugin.IsAnimationLocked()
                && DateTime.UtcNow - lastMountAttemptAt > MountRetryInterval)
            {
                lastMountAttemptAt = DateTime.UtcNow;
                Plugin.TryRequestAetheryteMount();
            }

            return;
        }

        // Am Boden (hier kann ohnehin nicht geflogen werden) - erneut anfordern, falls kein Weg läuft.
        var running = false;
        try { running = pathIsRunning.InvokeFunc() || Plugin.IsVnavPathfindInProgress(); } catch { /* vnavmesh fehlt */ }
        if (!running && DateTime.UtcNow - lastPathRequestAt > PathRetryInterval)
        {
            lastPathRequestAt = DateTime.UtcNow;
            var accepted = pathfindAndMoveCloseTo.InvokeFunc(GateStandPosition, false, StandPositionTolerance);
            Plugin.Log.Info($"[NoFlyAreaExit] Laufe zum Crystal Gate, angenommen={accepted}.");
        }
    }

    private void UpdateInteracting()
    {
        // Das Ja/Nein-Fenster jeden Frame bestätigen, sobald es da ist.
        if (Plugin.TryConfirmSelectYesno())
        {
            SetPhase(Phase.WaitingForExit);
            return;
        }

        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            Plugin.TryDismount();
            dismountedAt = null;
            return;
        }

        dismountedAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - dismountedAt.Value < DismountSettleDelay)
            return;

        // Nach einem Interact-Versuch kurz auf das Fenster warten.
        if (interactAttempts > 0 && DateTime.UtcNow - interactedAt < TimeSpan.FromSeconds(3))
            return;

        if (interactAttempts >= MaxInteractAttempts)
        {
            Plugin.Log.Info("[NoFlyAreaExit] Crystal Gate reagiert nicht.");
            Fail();
            return;
        }

        var gate = FindGateObject();
        if (gate == null)
        {
            Plugin.Log.Info("[NoFlyAreaExit] Crystal-Gate-Objekt nicht gefunden.");
            Fail();
            return;
        }

        if (!Plugin.IsCurrentTarget(gate))
        {
            Plugin.SetTarget(gate);
            return;
        }

        Plugin.Log.Info($"[NoFlyAreaExit] Interagiere mit '{gate.Name}'.");
        Plugin.InteractWithGameObject(gate);
        interactAttempts++;
        interactedAt = DateTime.UtcNow;
    }

    private void UpdateWaitingForExit()
    {
        // Falls das Fenster ein zweites Mal kommt.
        Plugin.TryConfirmSelectYesno();

        if (!IsInEightSentinels(logDiagnostics: false) && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.OccupiedInEvent])
        {
            Plugin.Log.Info("[NoFlyAreaExit] Bereich verlassen - Automation läuft weiter.");
            blockedUntil = DateTime.UtcNow + SuccessCooldown;
            Resume();
            return;
        }

        if (DateTime.UtcNow - phaseStartedAt > ExitWaitTimeout)
        {
            // Nochmal interagieren.
            SetPhase(Phase.Interacting);
            dismountedAt = DateTime.UtcNow - DismountSettleDelay;
        }
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindGateObject()
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? nearest = null;
        var bestDistance = GateObjectSearchRadius;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (!obj.IsTargetable || obj is Dalamud.Game.ClientState.Objects.Types.ICharacter)
                continue;

            var distance = Vector3.Distance(obj.Position, CrystalGatePosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = obj;
            }
        }

        return nearest;
    }

    private void Fail()
    {
        StopPath();
        blockedUntil = DateTime.UtcNow + FailureCooldown;
        Resume();
    }

    private void Resume()
    {
        var restarts = pausedRestarts;
        Reset();
        foreach (var restart in restarts)
        {
            try
            {
                restart();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[NoFlyAreaExit] Automation konnte nicht neu gestartet werden.");
            }
        }
    }

    private void Reset()
    {
        phase = Phase.None;
        pausedRestarts = new();
        StatusText = string.Empty;
    }

    private void SetPhase(Phase next)
    {
        phase = next;
        phaseStartedAt = DateTime.UtcNow;
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
            Plugin.Log.Error(ex, "[NoFlyAreaExit] Fehler beim Stoppen von vnavmesh.");
        }
    }
}
