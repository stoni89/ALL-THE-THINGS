using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace TheExplorersCodex;

/// <summary>
/// Welches Fremdplugin den Kampf übernimmt (siehe Configuration.CombatPlugin/CombatPluginBridge) -
/// es muss mindestens eines davon installiert sein.
/// </summary>
public enum CombatPluginKind
{
    RotationSolver,
    WrathCombo,
}

/// <summary>
/// Gemeinsame Ansteuerung der unterstützten Kampf-Plugins "RotationSolver Reborn" und "Wrath
/// Combo" - für HuntingLogAutomation (und die Verfügbarkeitsprüfung der Quest-Automation), damit
/// dort nur noch "Kampfmodus an/aus", "läuft er gerade?" und "dieses Monster bevorzugen" steht,
/// unabhängig davon, welches Plugin tatsächlich dahintersteckt. Beide werden in einem Modus
/// betrieben, in dem sie AUSSCHLIESSLICH das aktuelle (von uns gesetzte) Ziel angreifen:
///
/// - RotationSolver: Chat-Befehl "/rotation Manual" bzw. "/rotation Off" (siehe
///   HuntingLogAutomation-Klassenkommentar, warum Chat-Befehl statt IPC), dazu die IPC-Endpunkte
///   AutorotationActive/AddPriorityNameID/RemovePriorityNameID.
/// - Wrath Combo: Lease-basierte IPC ("WrathCombo.*", verifiziert gegen WrathCombo/Services/IPC und
///   WrathCombo.API): RegisterForLease, dann Auto-Rotation an, die aktuelle Klasse für die
///   Auto-Rotation bereit machen und die Zielwahl auf "Manual" (DPS- und Heiler-Modus) stellen - das
///   entspricht RotationSolvers Manual-Modus. Ausschalten gibt die Lease wieder frei, womit Wrath alle
///   so überschriebenen Einstellungen selbst wieder auf die des Nutzers zurücksetzt. Eine
///   Ziel-Priorität gibt es bei Wrath nicht - im Manual-Modus nicht nötig, das Ziel setzen wir selbst.
///
/// Rückgabetypen, die bei Wrath eigene Enums sind (SetResult), werden bewusst als object abgefragt
/// und nur als Zahl ausgewertet - so braucht es keine Assembly-Referenz auf WrathCombo.API.
/// </summary>
public sealed class CombatPluginBridge
{
    public const string RotationSolverInternalName = "RotationSolver";
    public const string WrathComboInternalName = "WrathCombo";

    // Wrath-IPC-Werte (siehe WrathCombo.API/Enum): AutoRotationConfigOption und DPS-/HealerRotationMode.
    private const int WrathOptionInCombatOnly = 0;
    private const int WrathOptionDpsRotationMode = 1;
    private const int WrathOptionHealerRotationMode = 2;
    private const int WrathOptionOnlyAttackInCombat = 13;
    private const int WrathRotationModeManual = 0;

    // SetResult: 0 = Okay, 1 = OkayWorking, 13 = Duplicate (bereits so gesetzt) - alles andere ist ein Fehler.
    private static bool IsWrathSetOk(object? result)
    {
        try
        {
            var code = Convert.ToInt32(result);
            return code is 0 or 1 or 13;
        }
        catch
        {
            return false;
        }
    }

    private readonly ICallGateSubscriber<bool> rsrAutorotationActive;
    private readonly ICallGateSubscriber<uint, object> rsrAddPriorityNameId;
    private readonly ICallGateSubscriber<uint, object> rsrRemovePriorityNameId;

    private readonly ICallGateSubscriber<bool> wrathIpcReady;
    private readonly ICallGateSubscriber<string, string, string?, Guid?> wrathRegisterForLeaseWithCallback;
    private readonly ICallGateSubscriber<Guid, object> wrathReleaseControl;
    private readonly ICallGateSubscriber<bool> wrathGetAutoRotationState;
    private readonly ICallGateSubscriber<Guid, bool, object> wrathSetAutoRotationState;
    private readonly ICallGateSubscriber<Guid, object> wrathSetCurrentJobAutoRotationReady;
    private readonly ICallGateSubscriber<Guid, object, object, object> wrathSetAutoRotationConfigState;

    // Wrath ruft bei Entzug der Lease (z.B. Nutzer übernimmt in Wrath selbst die Kontrolle)
    // "<Präfix>.WrathComboCallback(int reason, string info)" auf - dann ist die Lease ungültig.
    private readonly ICallGateProvider<int, string, object> wrathLeaseCallback;
    private const string WrathCallbackPrefix = "TheExplorersCodex";

    private Guid? wrathLease;

    public CombatPluginBridge()
    {
        var pi = Plugin.PluginInterface;
        rsrAutorotationActive = pi.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
        rsrAddPriorityNameId = pi.GetIpcSubscriber<uint, object>("RotationSolverReborn.AddPriorityNameID");
        rsrRemovePriorityNameId = pi.GetIpcSubscriber<uint, object>("RotationSolverReborn.RemovePriorityNameID");

        wrathIpcReady = pi.GetIpcSubscriber<bool>("WrathCombo.IPCReady");
        wrathRegisterForLeaseWithCallback = pi.GetIpcSubscriber<string, string, string?, Guid?>("WrathCombo.RegisterForLeaseWithCallback");
        wrathReleaseControl = pi.GetIpcSubscriber<Guid, object>("WrathCombo.ReleaseControl");
        wrathGetAutoRotationState = pi.GetIpcSubscriber<bool>("WrathCombo.GetAutoRotationState");
        wrathSetAutoRotationState = pi.GetIpcSubscriber<Guid, bool, object>("WrathCombo.SetAutoRotationState");
        wrathSetCurrentJobAutoRotationReady = pi.GetIpcSubscriber<Guid, object>("WrathCombo.SetCurrentJobAutoRotationReady");
        wrathSetAutoRotationConfigState = pi.GetIpcSubscriber<Guid, object, object, object>("WrathCombo.SetAutoRotationConfigState");

        wrathLeaseCallback = pi.GetIpcProvider<int, string, object>($"{WrathCallbackPrefix}.WrathComboCallback");
        wrathLeaseCallback.RegisterAction((reason, info) =>
        {
            Plugin.Log.Info($"[CombatPlugin] Wrath Combo hat die Lease entzogen (Grund={reason}, {info}).");
            wrathLease = null;
        });
    }

    public static string DisplayName(CombatPluginKind kind) => kind switch
    {
        CombatPluginKind.WrathCombo => "Wrath Combo",
        _ => "RotationSolver Reborn",
    };

    private static string InternalName(CombatPluginKind kind) => kind switch
    {
        CombatPluginKind.WrathCombo => WrathComboInternalName,
        _ => RotationSolverInternalName,
    };

    public static bool IsInstalled(CombatPluginKind kind) =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == InternalName(kind) && p.IsLoaded);

    /// <summary>Alle aktuell installierten/geladenen Kampf-Plugins (Reihenfolge wie im Enum).</summary>
    public static List<CombatPluginKind> GetInstalled() =>
        Enum.GetValues<CombatPluginKind>().Where(IsInstalled).ToList();

    /// <summary>
    /// Das tatsächlich zu nutzende Kampf-Plugin: das in den Einstellungen gewählte, solange es
    /// installiert ist - sonst das (erste) installierte. null, wenn keines installiert ist.
    /// </summary>
    public static CombatPluginKind? GetEffective()
    {
        var installed = GetInstalled();
        if (installed.Count == 0)
            return null;

        var configured = Plugin.ConfiguredCombatPlugin;
        return configured.HasValue && installed.Contains(configured.Value) ? configured.Value : installed[0];
    }

    public static bool IsAnyAvailable() => GetEffective() != null;

    /// <summary>
    /// Kampfmodus ("nur das aktuelle Ziel angreifen") ein- bzw. ausschalten. Ausschalten betrifft
    /// immer BEIDE Plugins (falls z.B. zwischendurch in den Einstellungen gewechselt wurde, soll das
    /// vorher genutzte nicht einfach weiterlaufen).
    /// </summary>
    public void SetCombatMode(bool enabled)
    {
        if (!enabled)
        {
            if (IsInstalled(CombatPluginKind.RotationSolver))
                SendChatCommand("/rotation Off");

            ReleaseWrath();
            return;
        }

        switch (GetEffective())
        {
            case CombatPluginKind.RotationSolver:
                SendChatCommand("/rotation Manual");
                break;
            case CombatPluginKind.WrathCombo:
                EnableWrath();
                break;
            default:
                Plugin.Log.Info("[CombatPlugin] Kein Kampf-Plugin installiert - Kampfmodus kann nicht eingeschaltet werden.");
                break;
        }
    }

    /// <summary>Ob das genutzte Kampf-Plugin gerade tatsächlich aktiv rotiert (für EnsureRotationSolverCombatMode o.ä.).</summary>
    public bool IsCombatModeActive()
    {
        try
        {
            return GetEffective() switch
            {
                CombatPluginKind.RotationSolver => rsrAutorotationActive.HasFunction && rsrAutorotationActive.InvokeFunc(),
                // Ohne eigene Lease zählt Wrath als "nicht von uns eingeschaltet", auch wenn der
                // Nutzer die Auto-Rotation selbst an hat - sonst fehlte die Manual-Zielwahl.
                CombatPluginKind.WrathCombo => wrathLease.HasValue && wrathGetAutoRotationState.HasFunction && wrathGetAutoRotationState.InvokeFunc(),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Monster (BNpcName-RowId) bevorzugt angreifen - nur RotationSolver kennt das, bei Wrath ohne Wirkung (true = gesetzt).</summary>
    public bool AddPriorityNameId(uint bNpcNameId)
    {
        if (GetEffective() != CombatPluginKind.RotationSolver)
            return false;

        try
        {
            if (!rsrAddPriorityNameId.HasAction)
                return false;

            rsrAddPriorityNameId.InvokeAction(bNpcNameId);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Setzen der RotationSolver-Priorität.");
            return false;
        }
    }

    public void RemovePriorityNameId(uint bNpcNameId)
    {
        if (!IsInstalled(CombatPluginKind.RotationSolver))
            return;

        try
        {
            if (rsrRemovePriorityNameId.HasAction)
                rsrRemovePriorityNameId.InvokeAction(bNpcNameId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Entfernen der RotationSolver-Priorität.");
        }
    }

    private void EnableWrath()
    {
        try
        {
            if (wrathIpcReady.HasFunction && !wrathIpcReady.InvokeFunc())
            {
                Plugin.Log.Info("[CombatPlugin] Wrath Combo IPC noch nicht bereit.");
                return;
            }

            wrathLease ??= wrathRegisterForLeaseWithCallback.InvokeFunc(Plugin.PluginInterface.InternalName, "The Explorer's Codex", WrathCallbackPrefix);
            if (wrathLease is not { } lease)
            {
                Plugin.Log.Warning("[CombatPlugin] Wrath Combo hat die Lease-Registrierung abgelehnt.");
                return;
            }

            var autoOk = IsWrathSetOk(wrathSetAutoRotationState.InvokeFunc(lease, true));
            var jobOk = IsWrathSetOk(wrathSetCurrentJobAutoRotationReady.InvokeFunc(lease));

            // Zielwahl wie RotationSolvers Manual-Modus: nur das aktuelle Ziel angreifen, und das
            // auch außerhalb des Kampfes (sonst würde ein noch nicht aggressives Hunting-Monster nie
            // angegriffen).
            var dpsOk = IsWrathSetOk(wrathSetAutoRotationConfigState.InvokeFunc(lease, WrathOptionDpsRotationMode, WrathRotationModeManual));
            var healerOk = IsWrathSetOk(wrathSetAutoRotationConfigState.InvokeFunc(lease, WrathOptionHealerRotationMode, WrathRotationModeManual));
            var inCombatOk = IsWrathSetOk(wrathSetAutoRotationConfigState.InvokeFunc(lease, WrathOptionInCombatOnly, false));
            var attackOk = IsWrathSetOk(wrathSetAutoRotationConfigState.InvokeFunc(lease, WrathOptionOnlyAttackInCombat, false));

            Plugin.Log.Info($"[CombatPlugin] Wrath Combo eingeschaltet: AutoRotation={autoOk}, JobBereit={jobOk}, " +
                            $"DPS-Manual={dpsOk}, Heiler-Manual={healerOk}, NichtNurImKampf={inCombatOk}/{attackOk}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Einschalten von Wrath Combo.");
            wrathLease = null;
        }
    }

    private void ReleaseWrath()
    {
        if (wrathLease is not { } lease)
            return;

        wrathLease = null;
        try
        {
            if (wrathReleaseControl.HasAction)
                wrathReleaseControl.InvokeAction(lease);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fehler beim Freigeben der Wrath-Combo-Lease.");
        }
    }

    private static void SendChatCommand(string command)
    {
        try
        {
            Plugin.CommandManager.ProcessCommand(command);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Fehler beim Senden von '{command}'.");
        }
    }

    public void Dispose()
    {
        ReleaseWrath();
        wrathLeaseCallback.UnregisterAction();
    }
}
