using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;
using TheExplorersCodex.Windows;

namespace TheExplorersCodex;

public sealed class Plugin : IDalamudPlugin
{
    // Von Dalamud per Dependency Injection bereitgestellte Services
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;

    private const string CommandName = "/exc";

    // Für den Zugriff aus statischen Methoden (z.B. TryUseSprint), die keine Plugin-Instanz haben -
    // es gibt zur Laufzeit ohnehin immer nur genau eine.
    private static Plugin instance = null!;

    // Für OpenVendorMap (Wegweiser-Pfeil-Zielposition) - dieselbe IPC, die auch jede Automation für
    // ihre eigenen Laufaufträge nutzt (siehe z.B. AetheryteAutomation.queryFlagToPoint), damit der
    // Pfeil exakt denselben begehbaren Punkt anzeigt, den vnavmesh auch tatsächlich ansteuern würde -
    // nicht eine eigene, möglicherweise leicht abweichende Umrechnung der Kartenkoordinate.
    private static ICallGateSubscriber<Vector3?>? navigationFlagToPointQuery;

    public Configuration Configuration { get; init; }

    /// <summary>
    /// Für SightseeingAutomation (hält sonst bewusst keine Plugin-Instanz, siehe "instance"-Feld
    /// oben) - ob gerade Configuration.SimulateSightseeingAutomation aktiv ist, damit die Automation
    /// im Simulation-Modus NIE einen Emote sendet (nur zum Testen von Laufweg/Position gedacht).
    /// </summary>
    public static bool SimulateSightseeingAutomation => instance.Configuration.SimulateSightseeingAutomation;

    public readonly WindowSystem WindowSystem = new("TheExplorersCodex");
    private MainWindow MainWindow { get; init; }
    public CompactOverlayWindow CompactOverlayWindow { get; init; }
    public NavigationArrowWindow NavigationArrowWindow { get; init; }
    public QuestAutomation QuestAutomation { get; init; }
    public AetheryteAutomation AetheryteAutomation { get; init; }
    public GoToAutomation GoToAutomation { get; init; }
    public HuntingLogAutomation HuntingLogAutomation { get; init; }
    public AetherCurrentAutomation AetherCurrentAutomation { get; init; }
    public SightseeingAutomation SightseeingAutomation { get; init; }
    public ChocobokeepAutomation ChocobokeepAutomation { get; init; }
    public TripleTriadAutomation TripleTriadAutomation { get; init; }
    public NoFlyAreaExit NoFlyAreaExit { get; init; }

    /// <summary>Für Klassen ohne eigene Plugin-Referenz (z.B. TripleTriadAutomation), die IsOwned/GetCurrencyAmount brauchen.</summary>
    public static Plugin Instance => instance;

    // Eine einzige, geteilte Instanz statt je einer pro Automation - es gibt nur EINEN Chocobo-
    // Begleiter/eine Gysahl-Greens-Abklingzeit im Spiel; mit getrennten Instanzen könnten
    // QuestAutomation und HuntingLogAutomation (falls beide gleichzeitig aktiv) unabhängig
    // voneinander gleichzeitig einen Beschwören-Versuch auslösen, ohne voneinander zu wissen.
    public ChocoboCompanionSupport ChocoboCompanionSupportInstance { get; init; }

    /// <summary>Siehe ChocoboCompanionSupportInstance-Kommentar - für QuestAutomation/HuntingLogAutomation, die keine Plugin-Instanz halten.</summary>
    public static ChocoboCompanionSupport ChocoboCompanionSupport => instance.ChocoboCompanionSupportInstance;

    // Siehe CombatPluginBridge - eine geteilte Instanz (hält u.a. die Wrath-Combo-Lease).
    public CombatPluginBridge CombatPluginInstance { get; init; }

    public static CombatPluginBridge CombatPlugin => instance.CombatPluginInstance;

    /// <summary>Siehe Configuration.CombatPlugin.</summary>
    public static CombatPluginKind? ConfiguredCombatPlugin => instance.Configuration.CombatPlugin;

    // Schneller Nachschlage-Index über Configuration.Blacklist (wird im Overlay pro Frame für jeden
    // Eintrag abgefragt) - bei jeder Änderung über AddToBlacklist/RemoveFromBlacklist neu aufgebaut.
    private static HashSet<(CollectibleType Type, uint Id)>? blacklistIndex;

    private static HashSet<(CollectibleType Type, uint Id)> BlacklistIndex =>
        blacklistIndex ??= instance.Configuration.Blacklist.Select(b => (b.Type, b.Id)).ToHashSet();

    /// <summary>
    /// Ob der Eintrag auf der Blacklist steht (siehe Configuration.Blacklist) - dann weder im Overlay
    /// angezeigt noch von einer Automation erfasst (siehe CompactOverlayWindow.DrawContent).
    /// </summary>
    public static bool IsBlacklisted(CollectibleEntry entry) => BlacklistIndex.Contains((entry.Type, entry.Id));

    // "(1/3)"-Fortschritt am Ende von Hunting-Log-Namen - gehört nicht in den gespeicherten Namen.
    private static readonly System.Text.RegularExpressions.Regex ProgressSuffixPattern = new(@"\s*\(\d+/\d+\)$");

    public static void AddToBlacklist(CollectibleEntry entry)
    {
        if (IsBlacklisted(entry))
            return;

        instance.Configuration.Blacklist.Add(new BlacklistedEntry
        {
            Type = entry.Type,
            Id = entry.Id,
            Name = ProgressSuffixPattern.Replace(entry.Name, string.Empty),
        });
        instance.Configuration.Save();
        blacklistIndex = null;
    }

    public static void RemoveFromBlacklist(CollectibleType type, uint id)
    {
        if (instance.Configuration.Blacklist.RemoveAll(b => b.Type == type && b.Id == id) == 0)
            return;

        instance.Configuration.Save();
        blacklistIndex = null;
    }

    /// <summary>
    /// Trägt das installierte Kampf-Plugin als Auswahl ein, solange keines (oder ein nicht mehr
    /// installiertes) gewählt ist - siehe Configuration.CombatPlugin. Speichert nur bei einer
    /// tatsächlichen Änderung, darf also jeden Frame aufgerufen werden (Einstellungsseite).
    /// </summary>
    public static void EnsureCombatPluginDefault()
    {
        var config = instance.Configuration;
        var installed = CombatPluginBridge.GetInstalled();
        if (installed.Count == 0 || (config.CombatPlugin.HasValue && installed.Contains(config.CombatPlugin.Value)))
            return;

        config.CombatPlugin = installed[0];
        config.Save();
    }

    public Plugin()
    {
        instance = this;

        // Welcher Build gerade tatsächlich geladen ist (Dev-Plugin-Reload greift nicht in jedem
        // parallel laufenden Spielclient) - einfacher Abgleich mit dem Zeitstempel der DLL.
        Log.Info($"[TheExplorersCodex] Geladen - Build {System.IO.File.GetLastWriteTime(PluginInterface.AssemblyLocation.FullName):yyyy-MM-dd HH:mm:ss}.");

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.SanitizeTypeOrder();

        navigationFlagToPointQuery = PluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
        vnavPathfindInProgress = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");

        ChocoboCompanionSupportInstance = new ChocoboCompanionSupport();
        CombatPluginInstance = new CombatPluginBridge();
        EnsureCombatPluginDefault();
        QuestAutomation = new QuestAutomation();
        AetheryteAutomation = new AetheryteAutomation();
        GoToAutomation = new GoToAutomation();
        HuntingLogAutomation = new HuntingLogAutomation();
        AetherCurrentAutomation = new AetherCurrentAutomation();
        SightseeingAutomation = new SightseeingAutomation();
        ChocobokeepAutomation = new ChocobokeepAutomation();
        TripleTriadAutomation = new TripleTriadAutomation();
        NoFlyAreaExit = new NoFlyAreaExit();

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CompactOverlayWindow = new CompactOverlayWindow(this) { IsOpen = Configuration.ShowCompactOverlay };
        WindowSystem.AddWindow(CompactOverlayWindow);

        NavigationArrowWindow = new NavigationArrowWindow(this) { IsOpen = true };
        WindowSystem.AddWindow(NavigationArrowWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.T("Öffnet The Explorer's Codex.", "Opens The Explorer's Codex.")
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.IsOpen = !MainWindow.IsOpen;
    }

    private void DrawUI() => WindowSystem.Draw();

    private void ToggleMainUI() => MainWindow.IsOpen = !MainWindow.IsOpen;

    public void OpenOptions() => MainWindow.IsOpen = true;

    /// <summary>
    /// Prüft, ob der Spieler ein bestimmtes Sammelobjekt bereits besitzt.
    /// Nutzt FFXIVClientStructs, um direkt auf die entsprechenden
    /// In-Game-Manager zuzugreifen. Diese Strukturen können sich mit
    /// Spiel-Patches ändern - bei Fehlern nach Updates hier zuerst schauen.
    /// </summary>
    public unsafe bool IsOwned(CollectibleEntry entry)
    {
        return entry.Type switch
        {
            CollectibleType.Mount => PlayerState.Instance()->IsMountUnlocked(entry.Id),
            CollectibleType.Minion => UIState.Instance()->IsCompanionUnlocked(entry.Id),
            CollectibleType.Orchestrion => PlayerState.Instance()->IsOrchestrionRollUnlocked(entry.Id),
            CollectibleType.Barding => UIState.Instance()->Buddy.CompanionInfo.IsBuddyEquipUnlocked(entry.Id),
            CollectibleType.Emote => UIState.Instance()->IsEmoteUnlocked((ushort)entry.Id),
            CollectibleType.Facewear => PlayerState.Instance()->IsGlassesUnlocked((ushort)entry.Id),
            CollectibleType.FashionAccessory => PlayerState.Instance()->IsOrnamentUnlocked(entry.Id),
            CollectibleType.TripleTriadCard => UIState.Instance()->IsTripleTriadCardUnlocked((ushort)entry.Id),
            CollectibleType.FrameKit => IsFrameKitUnlocked(entry),
            CollectibleType.Hairstyle => IsHairstyleUnlocked(entry.Id),
            CollectibleType.Aetheryte => IsAetheryteUnlocked(entry.Id),
            CollectibleType.AetherCurrent => IsAetherCurrentUnlocked(entry.Id),
            CollectibleType.Sightseeing => IsAdventureComplete(entry.Id),
            CollectibleType.Quest => QuestManager.IsQuestComplete((ushort)entry.Id) || IsQuestObsoletedByAchievement(entry.Id) || IsQuestObsoletedByMutualExclusion(entry.Id),
            CollectibleType.Chocobokeep => IsChocoboTaxiStandUnlocked(entry.Id),
            CollectibleType.Achievement => IsAchievementCompleteById(entry.Id),
            _ => false,
        };
    }

    /// <summary>
    /// Schließt ein nach der Chocobokeep-Interaktion aufpoppendes SelectString-Menü ("Hire a
    /// chocobo porter. / Learn about chocobo porters. / Nothing.") automatisch, indem der LETZTE
    /// Eintrag angeklickt wird - die Ablehnen-Option ("Nichts.") steht dort immer als letztes.
    /// Bewusst kein Textabgleich (z.B. gegen Lumina "Addon"-Sheet Row 622, wie ursprünglich
    /// versucht) - Groß-/Kleinschreibung, Zeichensetzung oder unsichtbare Formatierungszeichen in
    /// der rohen SeString machten den exakten Vergleich zu fragil, der Eintrag wurde nie gefunden.
    /// Ohne das bleibt die Automation stehen, weil das offene Menü weitere Eingaben blockiert und
    /// UIState.IsChocoboTaxiStandUnlocked dadurch nie geprüft werden kann. Gibt true zurück, wenn
    /// ein Eintrag angeklickt wurde.
    /// </summary>
    public static unsafe bool TryDismissChocobokeepSelectString()
    {
        var addon = (AddonSelectString*)GameGui.GetAddonByName("SelectString").Address;
        if (addon == null || !addon->IsVisible)
            return false;

        var entryCount = addon->PopupMenu.EntryCount;
        if (entryCount <= 0)
            return false;

        var lastIndex = entryCount - 1;
        var entryText = addon->PopupMenu.EntryNames[lastIndex].ToString();
        addon->AtkUnitBase.FireCallbackInt(lastIndex);
        Log.Info($"[ChocobokeepAutomation] TryDismissChocobokeepSelectString: letzter Eintrag '{entryText}' (#{lastIndex}) automatisch gewählt.");
        return true;
    }

    /// <summary>
    /// Ob eines der genannten nativen UI-Fenster (per Addon-Namen, z.B. "SelectString"/"Teleport")
    /// gerade sichtbar ist - für die Aetheryten-Automation im Simulation-Modus (siehe
    /// AetheryteAutomation.cs), die nach dem Testklick auf einen bereits freigeschalteten
    /// Aetheryten wartet, bis der Spieler das dabei geöffnete Menü selbst wieder schließt, bevor
    /// sie automatisch zum nächsten Ziel weiterzieht.
    /// </summary>
    public static unsafe bool IsAnyAddonVisible(params string[] addonNames)
    {
        foreach (var name in addonNames)
        {
            var addon = (AtkUnitBase*)GameGui.GetAddonByName(name).Address;
            if (addon != null && addon->IsVisible)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Ob ein einzelner Chocobo-Reitstand (Lumina "ChocoboTaxiStand"-RowId, siehe
    /// GetChocobokeepEntries) bereits freigeschaltet ist - doch ein echter Spielstand-Flag
    /// (UIState.IsChocoboTaxiStandUnlocked), keine eigene Merkliste nötig. Eigenständig aufrufbar
    /// (nicht nur über IsOwned), damit ChocobokeepAutomation nach dem Interagieren auf den
    /// tatsächlichen Freischalt-Abschluss warten kann (analog zu IsAetheryteUnlocked).
    /// </summary>
    public static unsafe bool IsChocoboTaxiStandUnlocked(uint chocoboTaxiStandId) => UIState.Instance()->IsChocoboTaxiStandUnlocked(chocoboTaxiStandId);

    /// <summary>
    /// Eigenständig aufrufbar (nicht nur über IsOwned) - wird von der Aetheryten-Automation
    /// benutzt, um nach dem Interagieren auf den tatsächlichen Freischalt-Abschluss zu warten
    /// (der Entdecken-Cast braucht ein paar Sekunden, bis er durchläuft).
    /// </summary>
    public static unsafe bool IsAetheryteUnlocked(uint aetheryteId) => UIState.Instance()->IsAetheryteUnlocked(aetheryteId);

    /// <summary>
    /// Ob gerade tatsächlich geflogen werden darf (Zone freigeschaltet UND Fliegen dort überhaupt
    /// erlaubt, z.B. nicht in Innenräumen) - live vom Spiel gepflegtes Flag. Von allen vnavmesh-
    /// Laufaufträgen (Aetheryte/Chocobokeep/HuntingLog/AetherCurrent/Sightseeing/GoTo) genutzt, bevor
    /// mit fly=true angefragt wird: vnavmesh nimmt einen Flugauftrag sonst teils trotzdem an, obwohl
    /// der Charakter gar nicht abheben kann - das Ergebnis war ein sinnloses Herumhüpfen am Boden
    /// statt eines sauberen Fußwegs.
    /// </summary>
    /// PlayerState.CanFly allein reicht aber nicht: es gilt für die ganze Zone - in Flugverbots-
    /// Bereichen INNERHALB einer Flug-Zone (z.B. "The Eight Sentinels" in Mor Dhona) bleibt es true,
    /// obwohl man dort nicht abheben kann (Nutzer-Report: "komische Bewegungen"). Dafür zusätzlich
    /// TerritoryInfo.FlyingDisabled (gilt für die aktuelle Position) - siehe auch FlightPathUpgrade,
    /// das nach dem Verlassen so eines Bereichs auf einen Flugweg umplant.
    /// <summary>
    /// Nur "Fliegen freigeschaltet" (PlayerState.CanFly), OHNE die Positionsprüfung aus CanFly - für
    /// die Sightseeing-Voraussetzung. In Städten/Flugverbots-Bereichen wäre CanFly sonst false und
    /// alle Punkte dort fälschlich "Bedingung nicht erfüllt" (Automation-Knopf ausgegraut).
    /// </summary>
    public static unsafe bool IsFlyingUnlocked => PlayerState.Instance()->CanFly;

    /// <summary>
    /// Sightseeing-Voraussetzung "Fliegen freigeschaltet" für einen Punkt: PlayerState.CanFly ist
    /// in Städten (TerritoryIntendedUse 0) immer false, weil dort niemand fliegen kann - dort ist
    /// Fliegen auch nicht nötig, die Voraussetzung gilt daher als erfüllt.
    /// </summary>
    public static bool IsSightseeingFlyingRequirementMet(CollectibleEntry entry)
    {
        if (IsFlyingUnlocked)
            return true;

        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        return territorySheet != null
               && territorySheet.TryGetRow(entry.TerritoryTypeId, out var territory)
               && territory.TerritoryIntendedUse.RowId == 0;
    }

    public static unsafe bool CanFly
    {
        get
        {
            if (!PlayerState.Instance()->CanFly)
                return false;

            var territoryInfo = TerritoryInfo.Instance();
            return territoryInfo == null || !territoryInfo->FlyingDisabled;
        }
    }

    /// <summary>
    /// Ob eine Aetheryte-RowId einen GROSSEN Aetheryten (row.IsAetheryte == true - eigener Kristall
    /// mit deutlich größerem Sockel/Kollisionsmodell) statt eines kleinen Aethernetz-Kristalls
    /// bezeichnet. Für AetheryteAutomation, die für große Aetheryten einen größeren Interaktions-
    /// Abstand braucht (siehe dortige FinalApproachDistance-Kommentare) - ein zu enger Abstand ließ
    /// den Charakter gegen den größeren Sockel laufen, statt sauber davor stehen zu bleiben.
    /// </summary>
    public static bool IsBigAetheryte(uint aetheryteId)
    {
        var sheet = DataManager.GetExcelSheet<Aetheryte>();
        return sheet != null && sheet.TryGetRow(aetheryteId, out var row) && row.IsAetheryte;
    }

    /// <summary>
    /// Ob eine einzelne Ätherströmung (Lumina "AetherCurrent"-Zeile) bereits entdeckt wurde -
    /// Gegenstück zu IsAetheryteUnlocked, siehe DumpAetherCurrentDebugInfo für den aktuellen
    /// Kalibrierungsstand dieses Features.
    /// </summary>
    public static unsafe bool IsAetherCurrentUnlocked(uint aetherCurrentId) => PlayerState.Instance()->IsAetherCurrentUnlocked(aetherCurrentId);

    /// <summary>
    /// Ob ein Sightseeing-Log-Eintrag (Lumina "Adventure"-Zeile) bereits abgeschlossen ist - nutzt
    /// Dalamuds eigenen IUnlockState-Service statt direkt FFXIVClientStructs, da IsAdventureComplete
    /// dort schon fertig als offizielle API bereitsteht.
    /// </summary>
    public static bool IsAdventureComplete(uint adventureId)
    {
        var sheet = DataManager.GetExcelSheet<Adventure>();
        return sheet != null && sheet.TryGetRow(adventureId, out var row) && UnlockState.IsAdventureComplete(row);
    }

    /// <summary>
    /// Ob das Sightseeing Log überhaupt schon freigeschaltet ist (unabhängig von einzelnen
    /// Ätherströmungen o.ä. - ein ganz frischer Charakter hat es noch gar nicht). Dalamuds
    /// IUnlockState hat dafür keine eigene Methode, daher direkt das rohe Byte-Feld aus
    /// PlayerState - dessen genaue Bit-Bedeutung (z.B. ob es je Erweiterung mitzählt) ist nicht
    /// dokumentiert, "!= 0" bedeutet aber zuverlässig "noch gar nicht freigeschaltet" vs. "schon".
    /// </summary>
    public static unsafe bool IsSightseeingLogUnlocked() => PlayerState.Instance()->SightseeingLogUnlockState != 0;

    /// <summary>
    /// Ob ein Sammel-Typ in der aktuellen Zone gerade überhaupt machbar ist - aktuell von keinem Typ
    /// mehr genutzt (Sightseeing hing hier früher am Fliegen, das aber das Log selbst gar nicht
    /// freischaltet - siehe ComputeGrandCompanyOrTribeGateReason, das die Log-Freischaltung jetzt
    /// stattdessen über die normale "Bedingung nicht erfüllt"-Markierung abbildet). Bleibt als
    /// generischer Erweiterungspunkt bestehen, falls ein künftiger Typ wieder eine reine
    /// Zonen-Machbarkeits-Ausgrauung braucht.
    /// </summary>
    public static bool IsTypeCurrentlyPossible(CollectibleType type) => true;

    /// <summary>
    /// Erklärtext fürs Ausgrauen (siehe IsTypeCurrentlyPossible) - aktuell ungenutzt, siehe dort.
    /// </summary>
    public static string GetTypeNotPossibleReason(CollectibleType type) => Loc.T("Noch nicht möglich", "Not possible yet");

    private static Dictionary<string, (uint TerritoryId, uint MapId)>? zoneByPlaceNameCache;

    // Zweite Nachschlage-Quelle NEBEN TerritoryType.PlaceName - Raids/Trials/Alliance-Raids heißen
    // im Source-Text meist wie ihr "ContentFinderCondition"-Duty-Name (z.B. "Asphodelos: The Fourth
    // Circle"), der oft NICHT mit der PlaceName-Spalte ihrer Zone übereinstimmt (die ist dort meist
    // generischer/intern benannt) - siehe EnrichEntriesWithZoneFromSource.
    private static Dictionary<string, (uint TerritoryId, uint MapId)>? zoneByContentNameCache;

    /// <summary>
    /// Trägt TerritoryTypeId/MapId für Einträge nach, deren JSON-Datei nur den Fundort als
    /// Klartext in Source kennt (z.B. "The Clyteum" bei einem Dungeon-Truhen-Drop), aber keine
    /// Zone - betrifft vor allem Notenrollen/Minions/Triple-Triad-Karten mit Category "Dungeon"/
    /// "Raid" (siehe CollectionData.GetAllEntries). Gleicht Source gegen zwei Lumina-Quellen ab:
    /// TerritoryType.PlaceName (funktioniert meist für Dungeons) und ContentFinderCondition.Name
    /// (funktioniert meist für Raids/Trials/Alliance-Raids, deren PlaceName oft nicht mit dem
    /// bekannten Duty-Namen übereinstimmt) - jeweils zusätzlich mit/ohne führendem "The " versucht,
    /// da Source und die jeweilige Lumina-Spalte den Artikel nicht immer konsistent führen (z.B.
    /// Source "The Aurum Vale" vs. tatsächlichem PlaceName "Aurum Vale"). Manche Source-Texte
    /// listen mehrere mögliche Fundorte zusammen (z.B. "Asphodelos: The Fourth Circle / Asphodelos:
    /// The Fourth Circle (Savage)", oder ganz unterschiedliche Dungeons für dasselbe Minion) - wird
    /// an "/" aufgeteilt, der erste auflösbare Teil gewinnt. Einträge, für die trotzdem keine
    /// Übereinstimmung gefunden wird, bleiben unverändert (TerritoryTypeId weiterhin 0, kein
    /// Rückschritt gegenüber vorher).
    /// </summary>
    public static void EnrichEntriesWithZoneFromSource(List<CollectibleEntry> entries)
    {
        if (zoneByPlaceNameCache == null)
        {
            zoneByPlaceNameCache = new Dictionary<string, (uint TerritoryId, uint MapId)>(StringComparer.OrdinalIgnoreCase);
            var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
            if (territorySheet != null)
            {
                foreach (var territory in territorySheet)
                {
                    var placeName = territory.PlaceName.ValueNullable?.Name.ToString();
                    if (string.IsNullOrEmpty(placeName) || zoneByPlaceNameCache.ContainsKey(placeName))
                        continue;

                    zoneByPlaceNameCache[placeName] = (territory.RowId, territory.Map.RowId);
                }
            }
        }

        if (zoneByContentNameCache == null)
        {
            zoneByContentNameCache = new Dictionary<string, (uint TerritoryId, uint MapId)>(StringComparer.OrdinalIgnoreCase);
            var cfcSheet = DataManager.GetExcelSheet<ContentFinderCondition>();
            if (cfcSheet != null)
            {
                foreach (var cfc in cfcSheet)
                {
                    var name = cfc.Name.ToString();
                    var territory = cfc.TerritoryType.ValueNullable;
                    if (string.IsNullOrEmpty(name) || territory == null || territory.Value.RowId == 0 || zoneByContentNameCache.ContainsKey(name))
                        continue;

                    zoneByContentNameCache[name] = (territory.Value.RowId, territory.Value.Map.RowId);
                }
            }
        }

        var placeNameCache = zoneByPlaceNameCache;
        var contentNameCache = zoneByContentNameCache;

        bool TryResolveZoneName(string name, out (uint TerritoryId, uint MapId) zone)
        {
            if (placeNameCache.TryGetValue(name, out zone) || contentNameCache.TryGetValue(name, out zone))
                return true;

            var altName = name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : "The " + name;
            return placeNameCache.TryGetValue(altName, out zone) || contentNameCache.TryGetValue(altName, out zone);
        }

        foreach (var entry in entries)
        {
            if (entry.TerritoryTypeId != 0 || string.IsNullOrEmpty(entry.Source))
                continue;

            foreach (var candidate in entry.Source.Split('/'))
            {
                var source = candidate.Trim().Trim('*');
                if (source.Length == 0 || !TryResolveZoneName(source, out var zone))
                    continue;

                entry.TerritoryTypeId = zone.TerritoryId;
                entry.MapId = zone.MapId;
                break;
            }
        }
    }

    /// <summary>
    /// Trägt MapId nach für Einträge, die von Hand nur mit TerritoryTypeId angelegt wurden (z.B. die
    /// Großen-Kompanie-Barding-Händler) - MapId ist für jede Zone eindeutig durch TerritoryType.Map
    /// bestimmt, muss also nie von Hand gepflegt werden. Ohne MapId bliebe HasVendorLocation auch
    /// nach einer erfolgreich aufgelösten Händlerposition (siehe EnrichEntriesWithVendorPosition)
    /// false, und "Auf Karte anzeigen"/"Hinlaufen" würden fehlen.
    /// </summary>
    public static void EnrichEntriesWithMapIdFromTerritory(List<CollectibleEntry> entries)
    {
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null)
            return;

        foreach (var entry in entries)
        {
            if (entry.TerritoryTypeId == 0 || entry.MapId != 0)
                continue;

            if (territorySheet.TryGetRow(entry.TerritoryTypeId, out var territory))
                entry.MapId = territory.Map.RowId;
        }
    }

    /// <summary>
    /// Einmaliger Debug-Dump zur Kalibrierung von EnrichEntriesWithZoneFromSource - listet jeden
    /// "Dungeon"-Eintrag (Category enthält "Dungeon"), dem auch nach der Anreicherung noch eine
    /// Zone fehlt, mitsamt seinem Source-Text, damit sich nicht erkannte Zonennamen gezielt
    /// nachtragen lassen (z.B. Tippfehler oder Zonennamen, die sich zwischen Quelle und Lumina
    /// unterscheiden).
    /// </summary>
    public static void DumpZoneEnrichmentDebugInfo()
    {
        var entries = CollectionData.GetAllEntries();
        var dungeonEntries = entries
            .Where(e => e.Category is "Dungeon" or "Raid" or "Chaos-Raid" or "Variant/Criterion-Dungeon")
            .ToList();
        var stillMissing = dungeonEntries.Where(e => e.TerritoryTypeId == 0).ToList();

        Log.Info($"[ZoneEnrichmentDebug] {dungeonEntries.Count} Dungeon/Raid-Einträge insgesamt, {stillMissing.Count} davon noch ohne Zone:");
        foreach (var entry in stillMissing)
            Log.Info($"[ZoneEnrichmentDebug]   {entry.Type} \"{entry.Name}\": Source=\"{entry.Source}\"");
    }

    /// <summary>
    /// Viele JSON-Einträge (Mounts/Minions/Orchestrionrollen/Triple-Triad-Karten/...) kennen zwar
    /// ihren Händler als Klartext (Vendor) und die richtige Zone (TerritoryTypeId/MapId), aber KEINE
    /// Kartenkoordinate (VendorMapX/Y stehen auf 0/0) - dadurch griff HasVendorLocation nie, und
    /// "Auf Karte anzeigen"/"Hinlaufen" fehlten (siehe z.B. "Jonathas" in Old Gridania, dessen
    /// Achievement-Certificate-Minions alle betroffen waren). Löst das GENERISCH über den Namen auf
    /// (Level-Sheet-NPC-Suche, dieselbe Technik wie schon bei EnrichFrameKitVendors/
    /// GetChocobokeepEntries) - eine Zeile pro (Name, Zone)-Kombination, da NPCs mit demselben Namen
    /// theoretisch in mehreren Zonen stehen könnten.
    /// </summary>
    public static void EnrichEntriesWithVendorPosition(List<CollectibleEntry> entries)
    {
        var candidates = entries
            .Where(e => !string.IsNullOrEmpty(e.Vendor) && e.TerritoryTypeId != 0 && e.VendorMapX == 0 && e.VendorMapY == 0)
            .ToList();
        if (candidates.Count == 0)
            return;

        try
        {
            var npcResidentSheet = DataManager.GetExcelSheet<ENpcResident>();
            var npcBaseSheet = DataManager.GetExcelSheet<ENpcBase>();
            var levelSheet = DataManager.GetExcelSheet<Level>();
            var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            if (npcResidentSheet == null || npcBaseSheet == null || levelSheet == null || mapSheet == null)
                return;

            var neededNames = candidates.Select(e => e.Vendor).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Name -> NPC-RowId(s) - mehrere NPCs können denselben Anzeigenamen tragen (z.B. generische
            // Stadtwachen), deshalb eine Liste statt eines einzelnen Werts pro Name.
            var npcIdsByName = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var npc in npcResidentSheet)
            {
                var name = npc.Singular.ToString();
                if (string.IsNullOrEmpty(name) || !neededNames.Contains(name))
                    continue;

                if (!npcIdsByName.TryGetValue(name, out var list))
                    npcIdsByName[name] = list = new List<uint>();
                list.Add(npc.RowId);
            }

            if (npcIdsByName.Count == 0)
                return;

            var allNeededNpcIds = npcIdsByName.Values.SelectMany(l => l).ToHashSet();

            // (Name, TerritoryTypeId) -> Kartenkoordinate - ein NPC kann an mehreren Stellen platziert
            // sein (siehe GetChocobokeepEntries-Kommentar), hier reicht der ERSTE Treffer je Zone.
            var positionByNameAndTerritory = new Dictionary<(string Name, uint TerritoryId), (float X, float Y)>();
            foreach (var level in levelSheet)
            {
                try
                {
                    if (level.Type != 8 || !allNeededNpcIds.Contains(level.Object.RowId))
                        continue;

                    var territoryId = level.Territory.RowId;
                    var mapId = level.Map.RowId;
                    if (territoryId == 0 || mapId == 0 || !mapSheet.TryGetRow(mapId, out var map))
                        continue;

                    var mapCoords = Dalamud.Utility.MapUtil.WorldToMap(
                        new Vector2(level.X, level.Z), (int)map.OffsetX, (int)map.OffsetY, (uint)map.SizeFactor);

                    foreach (var (name, npcIds) in npcIdsByName)
                    {
                        if (!npcIds.Contains(level.Object.RowId))
                            continue;

                        positionByNameAndTerritory.TryAdd((name, territoryId), (mapCoords.X, mapCoords.Y));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler bei Level-Zeile {level.RowId} (Händler-Positionsauflösung) - übersprungen.");
                }
            }

            foreach (var entry in candidates)
            {
                if (positionByNameAndTerritory.TryGetValue((entry.Vendor, entry.TerritoryTypeId), out var pos))
                {
                    entry.VendorMapX = pos.X;
                    entry.VendorMapY = pos.Y;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Auflösen von Händler-Kartenkoordinaten aus dem Namen.");
        }
    }

    /// <summary>
    /// Einmaliger Debug-Dump zur Kalibrierung von EnrichEntriesWithVendorPosition - listet jeden
    /// Eintrag mit Händlernamen, dem auch danach noch eine Kartenkoordinate fehlt.
    /// </summary>
    public static void DumpVendorPositionEnrichmentDebugInfo()
    {
        var entries = CollectionData.GetAllEntries();
        var withVendor = entries.Where(e => !string.IsNullOrEmpty(e.Vendor)).ToList();
        var stillMissing = withVendor.Where(e => e.VendorMapX == 0 && e.VendorMapY == 0).ToList();

        Log.Info($"[VendorPositionDebug] {withVendor.Count} Einträge mit Händlernamen insgesamt, {stillMissing.Count} davon noch ohne Kartenkoordinate:");
        foreach (var entry in stillMissing)
            Log.Info($"[VendorPositionDebug]   {entry.Type} \"{entry.Name}\": Vendor=\"{entry.Vendor}\", Zone={entry.TerritoryTypeId}");
    }

    /// <summary>
    /// Einmaliger Debug-Dump zur Kalibrierung der Großen-Kompanie-Bardinghändler (siehe
    /// bardings.json "Quartermaster"-Einträge, Zone 534/597/599) - der geratene Vendor-Name
    /// "Quartermaster" hat bei EnrichEntriesWithVendorPosition nicht gegriffen (kein passender NPC
    /// gefunden), daher hier stattdessen ALLE NPCs auflisten, die laut Level-Sheet tatsächlich in
    /// einer der drei Kasernen-Zonen stehen - der richtige Händlername lässt sich daraus ablesen.
    /// </summary>
    public static unsafe void DumpGrandCompanyBardingVendorDebugInfo()
    {
        var territoryIds = new HashSet<uint> { 534, 597, 599 };
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        var npcResidentSheet = DataManager.GetExcelSheet<ENpcResident>();
        var levelSheet = DataManager.GetExcelSheet<Level>();
        if (territorySheet == null || npcResidentSheet == null || levelSheet == null)
        {
            Log.Error("[GCBardingVendorDebug] Benötigtes Lumina-Sheet nicht verfügbar.");
            return;
        }

        Log.Info("[GCBardingVendorDebug] Alle NPCs in den Großen-Kompanie-Kasernen (Zone 534/597/599):");
        foreach (var level in levelSheet)
        {
            try
            {
                if (level.Type != 8 || !territoryIds.Contains(level.Territory.RowId))
                    continue;

                if (!npcResidentSheet.TryGetRow(level.Object.RowId, out var npc))
                    continue;

                var name = npc.Singular.ToString();
                if (string.IsNullOrEmpty(name))
                    continue;

                var zoneName = territorySheet.TryGetRow(level.Territory.RowId, out var territory)
                    ? territory.PlaceName.ValueNullable?.Name.ToString() ?? $"#{level.Territory.RowId}"
                    : $"#{level.Territory.RowId}";
                Log.Info($"[GCBardingVendorDebug]   Zone={level.Territory.RowId} ({zoneName}): \"{name}\" (ENpcResident #{npc.RowId})");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Fehler bei Level-Zeile {level.RowId} (GC-Bardinghändler-Debug) - übersprungen.");
            }
        }
    }

    private static List<CollectibleEntry>? frameKitEntriesCache;

    /// <summary>
    /// Baut die vollständige Liste aller Portrait-Rahmen live aus Lumina auf ("BannerFrame"-Sheet),
    /// mit dem jeweils korrekten Freischalt-Weg (siehe ResolveFrameUnlock) - Rahmen sind über ganz
    /// unterschiedliche Mechaniken freischaltbar (Quest, Errungenschaft, Duty, Emote/Minion/Mount/
    /// Ornament-Besitz oder ein separates "Framer's Kit"-Item), weshalb eine einzige statische
    /// Datendatei (wie bei Mounts/Minions) hier nicht ausreicht. Nach dem Vorbild des Dalamud-
    /// Plugins "Collections" (github.com/Seventhxiv/Collections), dessen Quellcode für die Quest/
    /// Errungenschaft/Kit-Item-Fälle als Referenz diente - Duty/Emote/Minion/Mount/Ornament deckt
    /// Collections selbst nicht ab, das kommt hier zusätzlich aus dem generischen Sheet-Schema.
    /// Rahmen mit einem unbekannten/nicht abgedeckten Freischalt-Typ (z.B. der seltene verkettete
    /// Sonderfall, den auch Collections nur über eine fragile Row-Offset-Heuristik löst) werden mit
    /// FrameKitUnlockKind.Unknown eingetragen - IsFrameKitUnlocked liefert dafür konservativ false,
    /// ganz ohne Fundort/Automation-Bezug (matcht das Verhalten für global unerreichbare Objekte wie
    /// "Legacy Campaign"-Mounts, die ebenfalls TerritoryTypeId=0 haben).
    /// </summary>
    public static List<CollectibleEntry> GetFrameKitEntries()
    {
        if (frameKitEntriesCache != null)
            return frameKitEntriesCache;

        var result = new List<CollectibleEntry>();
        var frameSheet = DataManager.GetExcelSheet<BannerFrame>();
        if (frameSheet == null)
        {
            frameKitEntriesCache = result;
            return result;
        }

        foreach (var frame in frameSheet)
        {
            if (frame.RowId == 0)
                continue;

            var name = frame.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            var condition = frame.UnlockCondition.ValueNullable;
            if (condition == null)
                continue;

            var unlock = ResolveFrameUnlock(condition.Value);
            if (unlock == null)
                continue;

            result.Add(new CollectibleEntry
            {
                Id = frame.RowId,
                Name = name,
                Type = CollectibleType.FrameKit,
                Category = Loc.T("Framer's Kit", "Framer's Kit"),
                TerritoryTypeId = unlock.Value.TerritoryId,
                MapId = unlock.Value.MapId,
                VendorMapX = unlock.Value.X,
                VendorMapY = unlock.Value.Y,
                FrameKitUnlockKind = unlock.Value.Kind,
                FrameKitUnlockId = unlock.Value.UnlockId,
                Source = unlock.Value.Source,
            });
        }

        // Kit-Item-Rahmen (FrameKitUnlockKind.FramersKitItem) haben bis hier IMMER TerritoryTypeId=0
        // (siehe ResolveFrameUnlock) - für die per Händler kaufbaren darunter (z.B. FATE-Belohnungen
        // wie "Sharlayan Stoa Framer's Kit") wird das hier live nachgetragen, sonst blieben sie im
        // zonenbasierten Overlay für immer unsichtbar, obwohl der Eintrag existiert.
        EnrichFrameKitVendors(result);

        frameKitEntriesCache = result;
        return result;
    }

    /// <summary>
    /// Trägt Händler/Fundort für Portrait-Rahmen nach, die als "Framer's Kit"-Item bei einem NPC
    /// gekauft werden können (FrameKitUnlockKind.FramersKitItem, siehe GetFrameKitEntries) - Item →
    /// Shop kommt aus dem rohen Lumina-Sheet "SpecialShop" (Währungs-Tausch-Händler, z.B. FATE-/
    /// Event-Währungen), Shop → NPC direkt aus "ENpcBase.ENpcData" (Shop-RowIds tauchen dort 1:1
    /// wieder auf - das NuGet-Paket "LuminaSupplemental.Excel"s ENpcShop-CSV deckt davon nur eine
    /// Handvoll ab, für die hier relevanten neueren SpecialShops leer). NPCs mit sehr vielen Shops
    /// referenzieren in ENpcData statt der einzelnen SpecialShops nur eine Menüzeile (wegen des
    /// 32-Slot-Limits von ENpcData) - je nach Händlertyp "TopicSelect" (Eureka-/Bozja-/Zadnor-
    /// Quartiermeister), "FateShop" (reine FATE-Belohnungshändler) oder, nochmal eine Ebene tiefer,
    /// "InclusionShop" → "InclusionShopCategory" → "InclusionShopSeries" (Bicolor-Gemstone-/
    /// Achievement-Certificate-/Sammelwährungs-Tauschhändler, z.B. in den Städten) - werden alle
    /// separat aufgelöst und zurückverfolgt. NPC → Weltposition primär
    /// aus dem rohen Lumina-Sheet "Level" (ebenfalls direkt aus SE-Rohdaten, deckt deutlich mehr ab
    /// als die ENpcPlace-CSV desselben NuGet-Pakets, die hier nur noch als Fallback dient). Reine
    /// Gil-Händler (GilShopItem) werden absichtlich NICHT abgedeckt (siehe Kommentar unten) -
    /// betrifft vermutlich nur einen kleinen Teil der Kit-Item-Rahmen.
    /// </summary>
    // RequiredAchievement: nur bei kostenlosen Errungenschafts-Belohnungen (siehe EnrichFrameKitVendors), sonst null.
    private readonly record struct FrameKitShopMatch(uint ShopId, uint CurrencyAmount, uint CurrencyItemId, uint CurrencyIconId, string CurrencyText, string? RequiredAchievement = null);

    private static void EnrichFrameKitVendors(List<CollectibleEntry> entries)
    {
        var candidates = entries
            .Where(e => e.Type == CollectibleType.FrameKit && e.FrameKitUnlockKind == FrameKitUnlockKind.FramersKitItem && e.TerritoryTypeId == 0)
            .ToList();
        if (candidates.Count == 0)
            return;

        try
        {
            var itemSheet = DataManager.GetExcelSheet<Item>();
            var specialShopSheet = DataManager.GetExcelSheet<SpecialShop>();
            var npcResidentSheet = DataManager.GetExcelSheet<ENpcResident>();
            var npcBaseSheet = DataManager.GetExcelSheet<ENpcBase>();
            if (itemSheet == null || specialShopSheet == null || npcResidentSheet == null || npcBaseSheet == null)
                return;

            // additionalData (== die "kitId", siehe PlayerState.IsFramersKitUnlocked-Doku) -> Item -
            // zusätzlich auf den Namen geprüft, da AdditionalData für ganz unterschiedliche Zwecke
            // wiederverwendet wird, nicht nur für Framer's Kits (Kollisionsschutz).
            var kitIdToItemRowId = new Dictionary<uint, uint>();
            foreach (var item in itemSheet)
            {
                if (item.AdditionalData.RowId == 0)
                    continue;
                if (!item.Name.ToString().Contains("Framer's Kit", StringComparison.OrdinalIgnoreCase))
                    continue;

                kitIdToItemRowId[item.AdditionalData.RowId] = item.RowId;
            }

            Log.Info($"[FrameKitDebug] kitIdToItemRowId.Count={kitIdToItemRowId.Count} (candidates={candidates.Count})");
            if (kitIdToItemRowId.Count == 0)
                return;

            // EINMAL komplett durchgehen (nicht pro Kandidat!) und für jedes gefundene Framer's-Kit-
            // Item direkt den Treffer merken - Shop-Zeilen einzeln in try/catch, da Lumina bei
            // manchen (offenbar leeren/reservierten) SpecialShop-Zeilen beim Auslesen einzelner
            // Slots eine NullReferenceException werfen kann (live beobachtet) - eine einzelne
            // kaputte Zeile darf dabei nicht die komplette Anreicherung (und damit das ganze
            // Overlay) mitreißen.
            var targetItemRowIds = kitIdToItemRowId.Values.ToHashSet();
            var itemRowIdToShopMatch = new Dictionary<uint, FrameKitShopMatch>();
            foreach (var shop in specialShopSheet)
            {
                try
                {
                    foreach (var slot in shop.Item)
                    {
                        foreach (var receive in slot.ReceiveItems)
                        {
                            var itemRowId = receive.Item.RowId;
                            if (itemRowId == 0 || !targetItemRowIds.Contains(itemRowId) || itemRowIdToShopMatch.ContainsKey(itemRowId))
                                continue;

                            uint costAmount = 0;
                            uint costItemId = 0;
                            uint costIconId = 0;
                            string costName = "?";
                            foreach (var cost in slot.ItemCosts)
                            {
                                if (cost.CurrencyCost == 0)
                                    continue;

                                costAmount = cost.CurrencyCost;
                                costItemId = cost.ItemCost.RowId;
                                var costItem = cost.ItemCost.ValueNullable;
                                costIconId = costItem?.Icon ?? 0;
                                costName = costItem?.Name.ToString() ?? "?";
                                break;
                            }

                            if (costAmount == 0)
                            {
                                // Kostenlose "Achievement Rewards"-Slots (z.B. Kornago Merchant: "Crucible
                                // Framer's Kit" für "Freeing the Beast") - statt eines Preises zählt dort nur
                                // die Errungenschaft (siehe CollectibleEntry.RequiredAchievement).
                                var achievementName = slot.AchievementUnlock.RowId != 0 ? slot.AchievementUnlock.ValueNullable?.Name.ToString() : null;
                                if (string.IsNullOrEmpty(achievementName))
                                    continue;

                                itemRowIdToShopMatch[itemRowId] = new FrameKitShopMatch(shop.RowId, 0, 0, 0, string.Empty, achievementName);
                                continue;
                            }

                            itemRowIdToShopMatch[itemRowId] = new FrameKitShopMatch(shop.RowId, costAmount, costItemId, costIconId, $"{costAmount:N0} {costName}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler beim Lesen von SpecialShop-Zeile {shop.RowId} - übersprungen.");
                }
            }

            Log.Info($"[FrameKitDebug] itemRowIdToShopMatch.Count={itemRowIdToShopMatch.Count}");
            if (itemRowIdToShopMatch.Count == 0)
                return;

            // LuminaSupplemental.Excel's ENpcShop-CSV deckt nur eine Handvoll (~33) Shops ab - für die
            // hier relevanten (neueren, Fate-/Event-)SpecialShops komplett leer. GilShop/SpecialShop-
            // RowIds liegen aber in einem eigenen, mit ENpcBase.ENpcData geteilten ID-Raum: jeder NPC,
            // der einen Shop betreibt, hat die Shop-RowId direkt (ohne Offset) als einen der 32
            // ENpcData-Einträge - das lässt sich also direkt aus den Rohdaten auflösen, ganz ohne CSV.
            // NPCs mit SEHR vielen Shops (z.B. Eureka-/Bozja-/Zadnor-Quartiermeister, oder der Bicolor-
            // Gemstone-Tauschhändler in den Städten, der FATE-Belohnungen aus ALLEN Zonen der jeweiligen
            // Erweiterung anbietet) referenzieren in ENpcData nicht die einzelnen SpecialShops direkt,
            // sondern nur eine Menü-Zeile - "TopicSelect" (bis zu 10 Unter-Shops) oder "FateShop" (bis
            // zu 3 Unter-Shops, speziell für FATE-Belohnungshändler) - sonst würden diese wegen des
            // 32-Slot-Limits von ENpcData gar nicht mehr hineinpassen. Daher zusätzlich beide auflösen
            // und rückwärts verfolgen (ein Menü kann mehrere unserer Ziel-Shops enthalten, deshalb pro
            // Menü-Zeile eine Liste statt nur des ersten Treffers).
            var targetShopIds = itemRowIdToShopMatch.Values.Select(m => m.ShopId).ToHashSet();
            var menuIdToShopIds = new Dictionary<uint, List<uint>>();

            void AddMenuShop(uint menuRowId, uint shopId)
            {
                if (!menuIdToShopIds.TryGetValue(menuRowId, out var list))
                    menuIdToShopIds[menuRowId] = list = new List<uint>();
                list.Add(shopId);
            }

            var topicSelectSheet = DataManager.GetExcelSheet<TopicSelect>();
            if (topicSelectSheet != null)
            {
                foreach (var topic in topicSelectSheet)
                    foreach (var shopRef in topic.Shop)
                        if (shopRef.RowId != 0 && targetShopIds.Contains(shopRef.RowId))
                            AddMenuShop(topic.RowId, shopRef.RowId);
            }

            var fateShopSheet = DataManager.GetExcelSheet<FateShop>();
            if (fateShopSheet != null)
            {
                foreach (var fateShop in fateShopSheet)
                    foreach (var shopRef in fateShop.SpecialShop)
                        if (shopRef.RowId != 0 && targetShopIds.Contains(shopRef.RowId))
                            AddMenuShop(fateShop.RowId, shopRef.RowId);
            }

            // Bicolor-Gemstone-/Achievement-Certificate-/Sammelwährungs-Tauschhändler (z.B. in den
            // Städten) nutzen eine DRITTE, noch tiefere Menü-Verschachtelung: ENpcData → InclusionShop
            // (bis zu 30 Kategorien) → InclusionShopCategory → InclusionShopSeries (ein Subrow-Sheet -
            // eine Kategorie kann mehrere Serien/Patches mit je einem eigenen SpecialShop enthalten).
            var inclusionShopSheet = DataManager.GetExcelSheet<InclusionShop>();
            var inclusionShopSeriesSheet = DataManager.GetSubrowExcelSheet<InclusionShopSeries>();
            Log.Info($"[FrameKitDebug] InclusionShop-Sheet null={inclusionShopSheet == null}, InclusionShopSeries-Sheet null={inclusionShopSeriesSheet == null}");
            if (inclusionShopSheet != null && inclusionShopSeriesSheet != null)
            {
                var categoryCount = 0;
                var seriesRowFoundCount = 0;
                var seriesItemCount = 0;
                var inclusionMatchCount = 0;
                foreach (var inclusionShop in inclusionShopSheet)
                {
                    foreach (var categoryRef in inclusionShop.Category)
                    {
                        var category = categoryRef.ValueNullable;
                        if (category == null)
                            continue;
                        categoryCount++;

                        var seriesRowId = category.Value.InclusionShopSeries.RowId;
                        if (!inclusionShopSeriesSheet.TryGetRow(seriesRowId, out var seriesRows))
                            continue;
                        seriesRowFoundCount++;

                        foreach (var series in seriesRows)
                        {
                            seriesItemCount++;
                            if (series.SpecialShop.RowId != 0 && targetShopIds.Contains(series.SpecialShop.RowId))
                            {
                                inclusionMatchCount++;
                                AddMenuShop(inclusionShop.RowId, series.SpecialShop.RowId);
                            }
                        }
                    }
                }
                Log.Info($"[FrameKitDebug] InclusionShop: categoryCount={categoryCount}, seriesRowFoundCount={seriesRowFoundCount}, " +
                         $"seriesItemCount={seriesItemCount}, inclusionMatchCount={inclusionMatchCount}");
            }

            // Manche SpecialShops werden nicht direkt (oder über TopicSelect/FateShop/InclusionShop),
            // sondern über ein "CustomTalk"-Skript geöffnet (SpecialShop.CustomTalk) - der NPC hat dann
            // die CustomTalk-RowId statt der SpecialShop-RowId in ENpcData.
            var customTalkIdToShopId = new Dictionary<uint, uint>();
            foreach (var shop in specialShopSheet)
            {
                if (targetShopIds.Contains(shop.RowId) && shop.CustomTalk.RowId != 0)
                    customTalkIdToShopId.TryAdd(shop.CustomTalk.RowId, shop.RowId);
            }
            Log.Info($"[FrameKitDebug] customTalkIdToShopId.Count={customTalkIdToShopId.Count}");

            var shopIdToNpcId = new Dictionary<uint, uint>();
            foreach (var npc in npcBaseSheet)
            {
                foreach (var data in npc.ENpcData)
                {
                    if (data.RowId == 0)
                        continue;

                    if (targetShopIds.Contains(data.RowId))
                        shopIdToNpcId.TryAdd(data.RowId, npc.RowId);
                    else if (menuIdToShopIds.TryGetValue(data.RowId, out var shopIdsViaMenu))
                    {
                        foreach (var shopIdViaMenu in shopIdsViaMenu)
                            shopIdToNpcId.TryAdd(shopIdViaMenu, npc.RowId);
                    }
                    else if (customTalkIdToShopId.TryGetValue(data.RowId, out var shopIdViaCustomTalk))
                        shopIdToNpcId.TryAdd(shopIdViaCustomTalk, npc.RowId);
                }
            }

            // "Gadfrid" (ENpcResident #1037055) öffnet seine Bicolor-Gemstone-Tauschkataloge über eine
            // einzelne Quest-Skript-ID in ENpcData (per Log bestätigt: ENpcData=[721620], eine Quest-
            // RowId - kein Shop/TopicSelect/FateShop/InclusionShop/CustomTalk) - die eigentliche Auswahl
            // zwischen den Katalogen passiert rein im Skript und ist aus Lumina-Rohdaten nicht auflösbar.
            // Der Bicolor-Gemstone-Katalog ROTIERT (ältere Kataloge werden irgendwann wieder entfernt) -
            // deshalb hier NUR der vom Nutzer im Spiel bestätigte, aktuell tatsächlich bei Gadfrid
            // kaufbare Shop hart verdrahtet (Sharlayan Stoa/Agora, #1770470). Die anderen "600 Bicolor
            // Gemstone"-Shops (Exarchic Dome/Tower, Eulmoran Comfort/Glory, Crimson/Golden Dawn, Dark/
            // Bright Solution, Hannish Radiance/Wonders) sind vermutlich aus dem Katalog gerotiert und
            // aktuell bei KEINEM NPC kaufbar - deshalb absichtlich NICHT eingetragen.
            shopIdToNpcId.TryAdd(1770470u, 1037055u);

            // Weltposition primär direkt aus dem rohen Lumina-Sheet "Level" (Type==8 => Object zeigt
            // auf ENpcBase) - LuminaSupplemental.Excel's ENpcPlace-CSV kennt viele der hier relevanten
            // (u.a. PvP-/Sammelwährungs-)Händler-NPCs offenbar gar nicht (live beobachtet), die CSV
            // dient nur noch als Fallback für den seltenen Fall, dass ein NPC in "Level" fehlt.
            var levelSheet = DataManager.GetExcelSheet<Level>();
            var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            var targetNpcIds = shopIdToNpcId.Values.ToHashSet();
            var npcIdToPlace = new Dictionary<uint, (uint TerritoryTypeId, uint MapId, float X, float Y)>();
            if (levelSheet != null && mapSheet != null)
            {
                foreach (var level in levelSheet)
                {
                    if (level.Type != 8)
                        continue;
                    var npcRowId = level.Object.RowId;
                    if (npcRowId == 0 || !targetNpcIds.Contains(npcRowId) || npcIdToPlace.ContainsKey(npcRowId))
                        continue;
                    var mapId = level.Map.RowId;
                    if (mapId == 0 || !mapSheet.TryGetRow(mapId, out var map))
                        continue;

                    var mapCoords = Dalamud.Utility.MapUtil.WorldToMap(
                        new Vector2(level.X, level.Z), (int)map.OffsetX, (int)map.OffsetY, (uint)map.SizeFactor);
                    npcIdToPlace[npcRowId] = (level.Territory.RowId, mapId, mapCoords.X, mapCoords.Y);
                }
            }

            var npcPlaces = CsvLoader.LoadResource<ENpcPlace>(CsvLoader.ENpcPlaceResourceName, true, out _, out _);
            foreach (var place in npcPlaces)
            {
                if (!targetNpcIds.Contains(place.ENpcResidentId))
                    continue;
                npcIdToPlace.TryAdd(place.ENpcResidentId, (place.TerritoryTypeId, place.MapId, place.Position.X, place.Position.Y));
            }

            Log.Info($"[FrameKitDebug] shopIdToNpcId.Count={shopIdToNpcId.Count} (targetShopIds={targetShopIds.Count}, " +
                     $"davon {menuIdToShopIds.Values.SelectMany(l => l).Distinct().Count()} über TopicSelect/FateShop/InclusionShop-Menüs gefunden), " +
                     $"npcIdToPlace.Count={npcIdToPlace.Count} (targetNpcIds={targetNpcIds.Count}, aus Level-Sheet + ENpcPlace-CSV-Fallback)");

            var enrichedCount = 0;
            foreach (var entry in candidates)
            {
                try
                {
                    if (!kitIdToItemRowId.TryGetValue(entry.FrameKitUnlockId, out var itemRowId))
                    {
                        Log.Info($"[FrameKitDebug] {entry.Name}: kein Item mit AdditionalData={entry.FrameKitUnlockId} gefunden.");
                        continue;
                    }
                    if (!itemRowIdToShopMatch.TryGetValue(itemRowId, out var match))
                    {
                        Log.Info($"[FrameKitDebug] {entry.Name}: Item #{itemRowId} in keinem SpecialShop als ReceiveItem gefunden.");
                        continue;
                    }
                    if (!shopIdToNpcId.TryGetValue(match.ShopId, out var npcId))
                    {
                        Log.Info($"[FrameKitDebug] {entry.Name}: SpecialShop #{match.ShopId} (Währung: {match.CurrencyText}) wird von keinem NPC in ENpcBase.ENpcData referenziert.");
                        continue;
                    }
                    if (!npcIdToPlace.TryGetValue(npcId, out var place))
                    {
                        Log.Info($"[FrameKitDebug] {entry.Name}: NPC #{npcId} hat weder einen Platz im Level-Sheet noch in der ENpcPlace-CSV.");
                        continue;
                    }
                    if (!npcResidentSheet.TryGetRow(npcId, out var npc))
                        continue;

                    var vendorName = npc.Singular.ToString();
                    entry.Vendor = vendorName;
                    entry.VendorMapX = place.X;
                    entry.VendorMapY = place.Y;
                    entry.TerritoryTypeId = place.TerritoryTypeId;
                    entry.MapId = place.MapId;
                    entry.Currency = match.CurrencyText;
                    entry.CurrencyIconId = match.CurrencyIconId;
                    entry.CurrencyItemId = match.CurrencyItemId;
                    entry.CurrencyAmount = match.CurrencyAmount;
                    entry.RequiredAchievement ??= match.RequiredAchievement;
                    entry.Source = string.IsNullOrEmpty(match.CurrencyText) ? vendorName : $"{vendorName} - {match.CurrencyText}";
                    enrichedCount++;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler beim Anreichern von Framer's-Kit-Eintrag {entry.Name} - übersprungen.");
                }
            }

            Log.Info($"[FrameKitDebug] EnrichFrameKitVendors fertig: {enrichedCount}/{candidates.Count} Einträge angereichert.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Auflösen von Framer's-Kit-Händlern - Anreicherung übersprungen.");
        }
    }

    /// <summary>
    /// Löst EINE BannerCondition-Zeile in einen konkreten Freischalt-Weg auf. UnlockType1-Werte
    /// 1/4/9 sind direkt vom Dalamud-Plugin "Collections" übernommen (dort live gegen echte
    /// BannerCondition-Daten getestet) - 4 bewusst NICHT wie im generischen Sheet-Schema als
    /// InstanceContent gelesen, sondern wie Collections es tut über das separate "Prerequisite"-
    /// Feld als Errungenschaft (Achievement.Key, NICHT Achievement.RowId!). 3/5/6/7/8 kommen direkt
    /// aus dem generischen RowRef-Schema von UnlockCriteria1 (Duty/Emote/Minion/Mount/Ornament) -
    /// von Collections nicht abgedeckt, hier aber genauso zuverlässig auflösbar.
    /// </summary>
    private static (FrameKitUnlockKind Kind, uint UnlockId, string Source, uint TerritoryId, uint MapId, float X, float Y)? ResolveFrameUnlock(BannerCondition condition)
    {
        switch (condition.UnlockType1)
        {
            case 1: // Quest
            {
                var criteria = condition.UnlockCriteria1.FirstOrDefault(r => r.RowId != 0);
                if (criteria.RowId == 0)
                    return null;

                var questSheet = DataManager.GetExcelSheet<Quest>();
                if (questSheet == null || !questSheet.TryGetRow(criteria.RowId, out var quest))
                    return null;

                var (mapId, issuerTerritoryId, x, y) = ResolveIssuerMapPosition(quest);
                var questName = quest.Name.ToString();
                return (FrameKitUnlockKind.Quest, criteria.RowId, $"{Loc.T("Quest", "Quest")}: {questName}", issuerTerritoryId, mapId, x, y);
            }

            case 3: // Duty (InstanceContent)
            {
                var criteria = condition.UnlockCriteria1.FirstOrDefault(r => r.RowId != 0);
                if (criteria.RowId == 0)
                    return null;

                var instanceSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>();
                var dutyName = instanceSheet != null && instanceSheet.TryGetRow(criteria.RowId, out var duty)
                    ? duty.ContentFinderCondition.ValueNullable?.Name.ToString() ?? $"#{criteria.RowId}"
                    : $"#{criteria.RowId}";
                return (FrameKitUnlockKind.Duty, criteria.RowId, $"{Loc.T("Dungeon/Trial", "Duty")}: {dutyName}", 0, 0, 0, 0);
            }

            case 4: // Achievement - über Prerequisite/Achievement.Key, siehe Methodenkommentar
            {
                var prereqId = condition.Prerequisite.RowId;
                if (prereqId == 0)
                    return null;

                var achievementSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
                if (achievementSheet == null)
                    return null;

                foreach (var achievement in achievementSheet)
                {
                    if (achievement.Key.RowId != prereqId)
                        continue;

                    return (FrameKitUnlockKind.Achievement, achievement.RowId,
                        $"{Loc.T("Errungenschaft", "Achievement")}: {achievement.Name.ToString()}", 0, 0, 0, 0);
                }

                return null;
            }

            case 5: // Emote
            {
                var criteria = condition.UnlockCriteria1.FirstOrDefault(r => r.RowId != 0);
                if (criteria.RowId == 0)
                    return null;

                var emoteSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
                var emoteName = emoteSheet != null && emoteSheet.TryGetRow(criteria.RowId, out var emote) ? emote.Name.ToString() : $"#{criteria.RowId}";
                return (FrameKitUnlockKind.Emote, criteria.RowId, $"{Loc.T("Emote", "Emote")}: {emoteName}", 0, 0, 0, 0);
            }

            case 6: // Minion (Companion)
            {
                var criteria = condition.UnlockCriteria1.FirstOrDefault(r => r.RowId != 0);
                if (criteria.RowId == 0)
                    return null;

                var companionSheet = DataManager.GetExcelSheet<Companion>();
                var minionName = companionSheet != null && companionSheet.TryGetRow(criteria.RowId, out var companion) ? companion.Singular.ToString() : $"#{criteria.RowId}";
                return (FrameKitUnlockKind.Minion, criteria.RowId, $"{Loc.T("Minion", "Minion")}: {minionName}", 0, 0, 0, 0);
            }

            case 7: // Mount
            {
                var criteria = condition.UnlockCriteria1.FirstOrDefault(r => r.RowId != 0);
                if (criteria.RowId == 0)
                    return null;

                var mountSheet = DataManager.GetExcelSheet<Mount>();
                var mountName = mountSheet != null && mountSheet.TryGetRow(criteria.RowId, out var mount) ? mount.Singular.ToString() : $"#{criteria.RowId}";
                return (FrameKitUnlockKind.Mount, criteria.RowId, $"{Loc.T("Mount", "Mount")}: {mountName}", 0, 0, 0, 0);
            }

            case 8: // Ornament (Fashion Accessory)
            {
                var criteria = condition.UnlockCriteria1.FirstOrDefault(r => r.RowId != 0);
                if (criteria.RowId == 0)
                    return null;

                var ornamentSheet = DataManager.GetExcelSheet<Ornament>();
                var ornamentName = ornamentSheet != null && ornamentSheet.TryGetRow(criteria.RowId, out var ornament) ? ornament.Singular.ToString() : $"#{criteria.RowId}";
                return (FrameKitUnlockKind.Ornament, criteria.RowId, $"{Loc.T("Accessoire", "Accessory")}: {ornamentName}", 0, 0, 0, 0);
            }

            case 9: // Framer's Kit-Item - siehe PlayerState.IsFramersKitUnlocked-Doku (kitId steht
                    // an Offset 0 der BannerCondition-Zeile, wenn UnlockType1==9)
            {
                var criteria = condition.UnlockCriteria1.FirstOrDefault(r => r.RowId != 0);
                if (criteria.RowId == 0)
                    return null;

                return (FrameKitUnlockKind.FramersKitItem, criteria.RowId, Loc.T("Framer's Kit (Gegenstand)", "Framer's Kit (item)"), 0, 0, 0, 0);
            }

            default:
                // Z.B. Typ 11 (verkettete Sonderregel, siehe Collections-Quellcode) - lässt sich nur
                // über eine fragile Row-Offset-Heuristik auflösen, die hier bewusst nicht nachgebaut
                // wird. Rahmen wird trotzdem gelistet (Source bleibt generisch), IsFrameKitUnlocked
                // liefert dafür konservativ false.
                return (FrameKitUnlockKind.Unknown, 0, Loc.T("Unbekannter Freischalt-Weg", "Unknown unlock method"), 0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Dispatcht den Freischalt-Check je nach FrameKitUnlockKind auf die jeweils passende API -
    /// siehe ResolveFrameUnlock/GetFrameKitEntries für die Herleitung.
    /// </summary>
    public static unsafe bool IsFrameKitUnlocked(CollectibleEntry entry)
    {
        switch (entry.FrameKitUnlockKind)
        {
            case FrameKitUnlockKind.Quest:
                return QuestManager.IsQuestComplete((ushort)entry.FrameKitUnlockId);

            case FrameKitUnlockKind.Duty:
            {
                var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>();
                return sheet != null && sheet.TryGetRow(entry.FrameKitUnlockId, out var row) && UnlockState.IsInstanceContentUnlocked(row);
            }

            case FrameKitUnlockKind.Achievement:
            {
                var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
                return sheet != null && sheet.TryGetRow(entry.FrameKitUnlockId, out var row) && UnlockState.IsAchievementComplete(row);
            }

            case FrameKitUnlockKind.Emote:
                return UIState.Instance()->IsEmoteUnlocked((ushort)entry.FrameKitUnlockId);

            case FrameKitUnlockKind.Minion:
                return UIState.Instance()->IsCompanionUnlocked(entry.FrameKitUnlockId);

            case FrameKitUnlockKind.Mount:
                return PlayerState.Instance()->IsMountUnlocked(entry.FrameKitUnlockId);

            case FrameKitUnlockKind.Ornament:
                return PlayerState.Instance()->IsOrnamentUnlocked(entry.FrameKitUnlockId);

            case FrameKitUnlockKind.FramersKitItem:
                return PlayerState.Instance()->IsFramersKitUnlocked(entry.FrameKitUnlockId);

            default:
                return false;
        }
    }

    /// <summary>
    /// Einmaliger Debug-Dump aller Portrait-Rahmen mit ihrem aufgelösten Freischalt-Weg - zur
    /// Kalibrierung von GetFrameKitEntries/ResolveFrameUnlock, v.a. um FrameKitUnlockKind.Unknown-
    /// Fälle und offensichtlich falsch aufgelöste Namen/Quellen zu finden.
    /// </summary>
    public static void DumpFrameKitDebugInfo()
    {
        // Cache verwerfen, damit EnrichFrameKitVendors (inkl. seiner Diagnose-Logs) hier garantiert
        // frisch läuft - sonst stehen die Diagnose-Zeilen (kitIdToItemRowId.Count usw.) irgendwo
        // weiter oben im Log, von der allerersten Berechnung beim Öffnen des Overlays.
        frameKitEntriesCache = null;
        var entries = GetFrameKitEntries();
        Log.Info($"[FrameKitDebug] {entries.Count} Portrait-Rahmen mit Freischalt-Bedingung gefunden:");
        foreach (var entry in entries)
        {
            var unlocked = IsFrameKitUnlocked(entry);
            Log.Info($"[FrameKitDebug]   {entry.Name}(#{entry.Id}): Kind={entry.FrameKitUnlockKind}, UnlockId={entry.FrameKitUnlockId}, " +
                     $"Source=\"{entry.Source}\", unlocked={unlocked}");
        }

        var unknownCount = entries.Count(e => e.FrameKitUnlockKind == FrameKitUnlockKind.Unknown);
        Log.Info($"[FrameKitDebug] Davon {unknownCount} mit unbekanntem Freischalt-Weg (FrameKitUnlockKind.Unknown).");
    }

    private static List<CollectibleEntry>? hairstyleEntriesCache;

    /// <summary>
    /// Baut die vollständige Liste aller "Modern Aesthetics"-Frisuren live aus Lumina auf
    /// ("CharaMakeCustomize"-Sheet, dessen HintItem-Feld auf das jeweilige "Modern Aesthetics"-Buch
    /// verweist) - dafür gibt es keine handelsübliche Community-Datenbasis wie bei Mounts/Minions,
    /// und die Freischaltung läuft über eine eigene PlayerState-Bitmaske (siehe IsHairstyleUnlocked),
    /// nicht über ein normales Item/eine Errungenschaft. Gefiltert auf Zeilen, deren HintItem-Name
    /// mit "Modern Aesthetics" beginnt - die übrigen CharaMakeCustomize-Zeilen sind Basis-
    /// Charaktererstellungs-Optionen ohne eigenes Freischalt-Item, die hier nichts verloren haben.
    /// Händler/Preis wird für die per Gil kaufbaren darunter zusätzlich aufgelöst, siehe
    /// EnrichHairstyleVendors - Frisuren ohne auflösbaren Händler (z.B. Raid-/Errungenschafts-
    /// Freischaltungen) bleiben mit TerritoryTypeId=0 (kein Fundort/GoTo-Link), sind aber weiterhin
    /// über IsHairstyleUnlocked live als besessen/nicht besessen erkennbar.
    /// </summary>
    public static List<CollectibleEntry> GetHairstyleEntries()
    {
        if (hairstyleEntriesCache != null)
            return hairstyleEntriesCache;

        var result = new List<CollectibleEntry>();
        var customizeSheet = DataManager.GetExcelSheet<CharaMakeCustomize>();
        if (customizeSheet == null)
        {
            hairstyleEntriesCache = result;
            return result;
        }

        foreach (var row in customizeSheet)
        {
            if (row.RowId == 0)
                continue;

            var hintItem = row.HintItem.ValueNullable;
            if (hintItem == null)
                continue;

            var itemName = hintItem.Value.Name.ToString();
            if (!itemName.StartsWith("Modern Aesthetics", StringComparison.OrdinalIgnoreCase))
                continue;

            // Nur der eigentliche Frisurname ("Loosened Locks" statt "Modern Aesthetics - Loosened
            // Locks") - die Zeile im Overlay wäre sonst zu lang, der Typ steht ohnehin schon dabei.
            var dash = itemName.IndexOf(" - ", StringComparison.Ordinal);
            var hairstyleName = dash >= 0 ? itemName[(dash + 3)..].Trim() : itemName;

            result.Add(new CollectibleEntry
            {
                Id = row.RowId,
                Name = hairstyleName,
                Type = CollectibleType.Hairstyle,
                Category = Loc.T("Moderne Ästhetik", "Modern Aesthetics"),
                Source = Loc.T("Moderne Ästhetik", "Modern Aesthetics"),
            });
        }

        EnrichHairstyleVendors(result);
        EnrichHairstyleSpecialCurrencyVendors(result);

        hairstyleEntriesCache = result;
        return result;
    }

    // Von Hand nachgetragene Händler für "Modern Aesthetics"-Bücher, die NICHT für Gil, sondern über
    // einen Sonderwährungs-Tauschhändler (SpecialShop) verkauft werden - EnrichHairstyleVendors
    // deckt bewusst nur reine Gil-Händler ab (siehe dessen Kommentar), solche Fälle blieben sonst
    // ohne Fundort. Schlüssel ist der Frisurname ohne "Modern Aesthetics - "-Präfix (siehe
    // GetHairstyleEntries, nicht die CharaMakeCustomize-RowId) - dasselbe Buch
    // taucht im Sheet einmal PRO Rasse/Geschlecht auf (mehrere RowIds, ein Name), hier reicht ein
    // einziger Eintrag für alle.
    private static readonly Dictionary<string, (string Vendor, uint TerritoryId, uint MapId, float X, float Y, string CurrencyText, uint CurrencyIconId, uint CurrencyItemId, uint CurrencyAmount, string? RequiredQuest)> HairstyleSpecialVendorOverrides = new()
    {
        // Ose Wyd (Il Mheg) - Pilgrim's-Traverse-Tauschhändler, siehe Plugin.cs-Git-Historie.
        ["Simple and Clean"] = ("Ose Wyd", 816, 494, 29.9f, 5.9f, "99 Luminous Oil", 22654, 47342, 99, null),

        // Kornago Merchant (Central Shroud, Bentbranch Meadows) - Beastmaster-Tauschhändler, laut
        // SpecialShop-Daten erst nach der Quest "Gobsmacked" kaufbar (später als die übrigen
        // Faded-Remnant-Waren, siehe CurrencyRequiredQuest).
        ["Loosened Locks"] = ("Kornago Merchant", 148, 4, 21.9f, 22.7f, "500 Faded Remnants of Resilience", 20217, 51734, 500, "Gobsmacked"),
    };

    private static void EnrichHairstyleSpecialCurrencyVendors(List<CollectibleEntry> entries)
    {
        // Dasselbe Buch steht einmal PRO Rasse/Geschlecht in der Liste (siehe Kommentar an
        // HairstyleSpecialVendorOverrides) - nur der ERSTE Eintrag je Name bekommt den Händler
        // (wie bei EnrichHairstyleVendors), sonst stünde das Buch im Overlay bis zu 10x in der
        // Händlerzone. Freischaltung gilt ohnehin je Buch (gleiche UnlockLink), nicht je Zeile.
        var enrichedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Type != CollectibleType.Hairstyle || entry.TerritoryTypeId != 0)
                continue;

            if (!HairstyleSpecialVendorOverrides.TryGetValue(entry.Name, out var vendor))
                continue;

            if (!enrichedNames.Add(entry.Name))
                continue;

            entry.Vendor = vendor.Vendor;
            entry.TerritoryTypeId = vendor.TerritoryId;
            entry.MapId = vendor.MapId;
            entry.VendorMapX = vendor.X;
            entry.VendorMapY = vendor.Y;
            entry.Currency = vendor.CurrencyText;
            entry.CurrencyIconId = vendor.CurrencyIconId;
            entry.CurrencyItemId = vendor.CurrencyItemId;
            entry.CurrencyAmount = vendor.CurrencyAmount;
            entry.RequiredQuest ??= vendor.RequiredQuest;
            entry.Source = $"{vendor.Vendor} - {vendor.CurrencyText}";
        }
    }

    /// <summary>
    /// Trägt Händler/Preis für "Modern Aesthetics"-Bücher nach, die für Gil bei einem NPC gekauft
    /// werden können (siehe GetHairstyleEntries) - Item → Shop kommt aus dem rohen Lumina-Sheet
    /// "GilShopItem" (einfache Gil-Händler, anders als die Sonderwährungs-Händler bei
    /// EnrichFrameKitVendors), Shop → NPC direkt aus "ENpcBase.ENpcData" (dieselbe direkte
    /// ID-Raum-Auflösung wie dort, siehe Kommentar). NPC → Weltposition ebenfalls nach demselben
    /// Vorbild (Lumina-Sheet "Level", ENpcPlace-CSV nur als Fallback). Bewusst KEINE TopicSelect-/
    /// FateShop-/InclusionShop-Menü-Auflösung wie bei EnrichFrameKitVendors - einfache Gil-Händler
    /// hängen ihre Shops direkt in ENpcData, ohne verschachteltes Menü.
    /// </summary>
    private static void EnrichHairstyleVendors(List<CollectibleEntry> entries)
    {
        var candidates = entries.Where(e => e.Type == CollectibleType.Hairstyle).ToList();
        if (candidates.Count == 0)
            return;

        try
        {
            var customizeSheet = DataManager.GetExcelSheet<CharaMakeCustomize>();
            var gilShopItemSheet = DataManager.GetSubrowExcelSheet<GilShopItem>();
            var npcResidentSheet = DataManager.GetExcelSheet<ENpcResident>();
            var npcBaseSheet = DataManager.GetExcelSheet<ENpcBase>();
            var itemSheet = DataManager.GetExcelSheet<Item>();
            if (customizeSheet == null || gilShopItemSheet == null || npcResidentSheet == null || npcBaseSheet == null || itemSheet == null)
                return;

            // entry.Id ist die CharaMakeCustomize-RowId - HintItem daraus die Ziel-Item-RowId auflösen.
            var itemRowIdToEntry = new Dictionary<uint, CollectibleEntry>();
            foreach (var entry in candidates)
            {
                if (!customizeSheet.TryGetRow(entry.Id, out var row))
                    continue;

                var itemRowId = row.HintItem.RowId;
                if (itemRowId != 0)
                    itemRowIdToEntry.TryAdd(itemRowId, entry);
            }

            Log.Info($"[HairstyleDebug] itemRowIdToEntry.Count={itemRowIdToEntry.Count} (candidates={candidates.Count})");
            if (itemRowIdToEntry.Count == 0)
                return;

            var targetItemRowIds = itemRowIdToEntry.Keys.ToHashSet();
            var itemRowIdToShopId = new Dictionary<uint, uint>();
            try
            {
                foreach (var shopItem in gilShopItemSheet.Flatten())
                {
                    var itemRowId = shopItem.Item.RowId;
                    if (itemRowId == 0 || !targetItemRowIds.Contains(itemRowId) || itemRowIdToShopId.ContainsKey(itemRowId))
                        continue;

                    itemRowIdToShopId[itemRowId] = shopItem.RowId; // RowId == GilShop-RowId bei GilShopItem
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fehler beim Lesen von GilShopItem.");
            }

            Log.Info($"[HairstyleDebug] itemRowIdToShopId.Count={itemRowIdToShopId.Count}");
            if (itemRowIdToShopId.Count == 0)
                return;

            var targetShopIds = itemRowIdToShopId.Values.ToHashSet();
            var shopIdToNpcId = new Dictionary<uint, uint>();
            foreach (var npc in npcBaseSheet)
            {
                foreach (var data in npc.ENpcData)
                {
                    if (data.RowId != 0 && targetShopIds.Contains(data.RowId))
                        shopIdToNpcId.TryAdd(data.RowId, npc.RowId);
                }
            }

            Log.Info($"[HairstyleDebug] shopIdToNpcId.Count={shopIdToNpcId.Count} (targetShopIds={targetShopIds.Count})");

            var levelSheet = DataManager.GetExcelSheet<Level>();
            var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            var targetNpcIds = shopIdToNpcId.Values.ToHashSet();
            var npcIdToPlace = new Dictionary<uint, (uint TerritoryTypeId, uint MapId, float X, float Y)>();
            if (levelSheet != null && mapSheet != null)
            {
                foreach (var level in levelSheet)
                {
                    if (level.Type != 8)
                        continue;
                    var npcRowId = level.Object.RowId;
                    if (npcRowId == 0 || !targetNpcIds.Contains(npcRowId) || npcIdToPlace.ContainsKey(npcRowId))
                        continue;
                    var mapId = level.Map.RowId;
                    if (mapId == 0 || !mapSheet.TryGetRow(mapId, out var map))
                        continue;

                    var mapCoords = Dalamud.Utility.MapUtil.WorldToMap(
                        new Vector2(level.X, level.Z), (int)map.OffsetX, (int)map.OffsetY, (uint)map.SizeFactor);
                    npcIdToPlace[npcRowId] = (level.Territory.RowId, mapId, mapCoords.X, mapCoords.Y);
                }
            }

            var npcPlaces = CsvLoader.LoadResource<ENpcPlace>(CsvLoader.ENpcPlaceResourceName, true, out _, out _);
            foreach (var place in npcPlaces)
            {
                if (!targetNpcIds.Contains(place.ENpcResidentId))
                    continue;
                npcIdToPlace.TryAdd(place.ENpcResidentId, (place.TerritoryTypeId, place.MapId, place.Position.X, place.Position.Y));
            }

            Log.Info($"[HairstyleDebug] npcIdToPlace.Count={npcIdToPlace.Count} (targetNpcIds={targetNpcIds.Count})");

            var enrichedCount = 0;
            foreach (var (itemRowId, entry) in itemRowIdToEntry)
            {
                try
                {
                    if (!itemRowIdToShopId.TryGetValue(itemRowId, out var shopId))
                    {
                        Log.Info($"[HairstyleDebug] {entry.Name}: Item #{itemRowId} in keinem GilShop als Ware gefunden.");
                        continue;
                    }
                    if (!shopIdToNpcId.TryGetValue(shopId, out var npcId))
                    {
                        Log.Info($"[HairstyleDebug] {entry.Name}: GilShop #{shopId} wird von keinem NPC in ENpcBase.ENpcData referenziert.");
                        continue;
                    }
                    if (!npcIdToPlace.TryGetValue(npcId, out var place))
                    {
                        Log.Info($"[HairstyleDebug] {entry.Name}: NPC #{npcId} hat weder einen Platz im Level-Sheet noch in der ENpcPlace-CSV.");
                        continue;
                    }
                    if (!npcResidentSheet.TryGetRow(npcId, out var npc))
                        continue;
                    if (!itemSheet.TryGetRow(itemRowId, out var item))
                        continue;

                    var vendorName = npc.Singular.ToString();
                    var price = item.PriceMid;
                    entry.Vendor = vendorName;
                    entry.VendorMapX = place.X;
                    entry.VendorMapY = place.Y;
                    entry.TerritoryTypeId = place.TerritoryTypeId;
                    entry.MapId = place.MapId;
                    entry.Currency = $"{price:N0} Gil";
                    entry.CurrencyIconId = 65002;
                    entry.CurrencyItemId = 1;
                    entry.CurrencyAmount = price;
                    entry.Source = $"{vendorName} - {price:N0} Gil";
                    enrichedCount++;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler beim Anreichern von Modern-Aesthetics-Eintrag {entry.Name} - übersprungen.");
                }
            }

            Log.Info($"[HairstyleDebug] EnrichHairstyleVendors fertig: {enrichedCount}/{candidates.Count} Einträge angereichert.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Auflösen von Modern-Aesthetics-Händlern - Anreicherung übersprungen.");
        }
    }

    /// <summary>
    /// Ob eine "Modern Aesthetics"-Frisur (CharaMakeCustomize-RowId) bereits freigeschaltet ist -
    /// über Dalamuds IUnlockState (dieselbe generische Bitmasken-Abfrage wie bei Sightseeing/
    /// IsAdventureComplete), da PlayerState/UIState dafür keine eigene Methode anbieten.
    /// </summary>
    public static bool IsHairstyleUnlocked(uint customizeRowId)
    {
        var sheet = DataManager.GetExcelSheet<CharaMakeCustomize>();
        return sheet != null && sheet.TryGetRow(customizeRowId, out var row) && UnlockState.IsCharaMakeCustomizeUnlocked(row);
    }

    /// <summary>
    /// Einmaliger Debug-Dump aller "Modern Aesthetics"-Frisuren mit aufgelöstem Händler/Freischalt-
    /// Status - zur Kalibrierung von GetHairstyleEntries/EnrichHairstyleVendors, v.a. um Einträge
    /// ohne aufgelösten Händler zu finden.
    /// </summary>
    public static void DumpHairstyleDebugInfo()
    {
        hairstyleEntriesCache = null;
        var entries = GetHairstyleEntries();
        Log.Info($"[HairstyleDebug] {entries.Count} Modern-Aesthetics-Frisuren gefunden:");
        foreach (var entry in entries)
        {
            var unlocked = IsHairstyleUnlocked(entry.Id);
            Log.Info($"[HairstyleDebug]   {entry.Name}(#{entry.Id}): Vendor=\"{entry.Vendor}\", Territory={entry.TerritoryTypeId}, " +
                     $"Currency=\"{entry.Currency}\", unlocked={unlocked}");
        }

        var missingVendorCount = entries.Count(e => string.IsNullOrEmpty(e.Vendor));
        Log.Info($"[HairstyleDebug] Davon {missingVendorCount} ohne aufgelösten Händler.");
    }

    // Bits sind Weather-Sheet-RowIds (1u << RowId) - für SightseeingWeatherMask.
    private const uint WeatherClear = 1u << 1;
    private const uint WeatherFair = 1u << 2;
    private const uint WeatherClouds = 1u << 3;
    private const uint WeatherFog = 1u << 4;
    private const uint WeatherGales = 1u << 6;
    private const uint WeatherRain = 1u << 7;
    private const uint WeatherShowers = 1u << 8;
    private const uint WeatherThunder = 1u << 9;
    private const uint WeatherThunderstorms = 1u << 10;
    private const uint WeatherDustStorms = 1u << 11;
    private const uint WeatherHeatWaves = 1u << 14;
    private const uint WeatherSnow = 1u << 15;
    private const uint WeatherBlizzards = 1u << 16;
    private const uint WeatherGloom = 1u << 17;
    private const uint WeatherClearOrFair = WeatherClear | WeatherFair;
    private const uint WeatherRainOrShowers = WeatherRain | WeatherShowers;

    /// <summary>
    /// Wetter-Voraussetzung je A-Realm-Reborn-Sichtungspunkt (Index = laufende Nummer im "Adventure"-
    /// Sheet minus 1, siehe GetSightseeingEntries) - JEDER der 80 A-Realm-Reborn-Punkte verlangt ein
    /// bestimmtes Wetter, KEIN späterer Punkt (Heavensward+) tut das. Nicht aus Lumina auslesbar (das
    /// Adventure-Sheet selbst kennt keine Wetter-Spalte) - übernommen aus dem quelloffenen Dalamud-
    /// Plugin "AutoSightseeingLog" (github.com/XeldarAlz/FFXIV-AutoSightseeingLog,
    /// Core/Vistas/VistaData.cs), das diese Werte selbst wiederum manuell recherchiert/verifiziert hat.
    /// </summary>
    private static readonly uint[] RealmRebornVistaWeathers =
    {
        WeatherClearOrFair, WeatherClearOrFair, WeatherRainOrShowers, WeatherClearOrFair, WeatherClouds, WeatherClearOrFair, WeatherFog, WeatherClearOrFair, WeatherClouds, WeatherClearOrFair,
        WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair, WeatherClouds, WeatherClearOrFair, WeatherFog, WeatherRainOrShowers, WeatherClouds, WeatherClearOrFair,
        WeatherClearOrFair, WeatherClearOrFair, WeatherRainOrShowers, WeatherClearOrFair, WeatherRainOrShowers, WeatherClearOrFair, WeatherGales, WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair,
        WeatherClearOrFair, WeatherThunderstorms, WeatherClearOrFair, WeatherClouds, WeatherClearOrFair, WeatherRainOrShowers, WeatherClearOrFair, WeatherRainOrShowers, WeatherRainOrShowers, WeatherClearOrFair,
        WeatherClearOrFair, WeatherClearOrFair, WeatherThunder, WeatherThunderstorms, WeatherClearOrFair, WeatherFog, WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair, WeatherClouds,
        WeatherClearOrFair, WeatherClearOrFair, WeatherDustStorms, WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair, WeatherShowers, WeatherFog, WeatherClearOrFair, WeatherHeatWaves,
        WeatherClearOrFair, WeatherHeatWaves, WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair, WeatherClouds, WeatherFog, WeatherClearOrFair, WeatherFog, WeatherBlizzards,
        WeatherClearOrFair, WeatherClearOrFair, WeatherBlizzards | WeatherSnow, WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair, WeatherGloom, WeatherClearOrFair, WeatherClearOrFair, WeatherClearOrFair,
    };

    // "A Sight to Behold" (Naoh Gamduhla, New Gridania) - übergibt das Sightseeing Log selbst, siehe
    // auch IsSightseeingLogUnlocked/ComputeGrandCompanyOrTribeGateReason. Erst danach zeichnen sich
    // überhaupt Punkte auf.
    private const uint SightseeingUnlockQuestId = 65698;

    // Die ersten 20 A-Realm-Reborn-Punkte (Nummer 1-20 im Adventure-Sheet) sind sofort verfügbar,
    // sobald das Log freigeschaltet ist - erst wenn ALLE 20 aufgezeichnet sind, schaltet Millith
    // Ironheart (Old Gridania) die restlichen 60 A-Realm-Reborn-Punkte (21-80) frei. KEIN "man muss
    // erst alle A-Realm-Reborn-Punkte machen" wie oft angenommen - nur diese ersten 20.
    private const int SightseeingFirstBookCount = 20;

    // "Sights of the North" - schaltet alle Heavensward-Sichtungspunkte frei. Ab Stormblood übernimmt
    // stattdessen das Lumina-Sheet "AdventureExPhase" (siehe ResolveSightseeingGateQuests) - für
    // Heavensward selbst fehlt dort ein Eintrag, daher hier fest hinterlegt (Quelle: "AutoSightseeingLog").
    private const uint SightseeingHeavenswardQuestId = 67643;

    // Von Hand nachgetragene Ersatz-Zielposition (statt der rohen Adventure.Level-Position) für
    // Punkte, bei denen die Automation gegen eine Wand/ein Geländer läuft, statt anzukommen (Key =
    // Adventure-RowId, siehe DumpPlayerPositionDebugInfo zum Ermitteln passender Werte) - genau der
    // gleiche Ansatz wie "AutoSightseeingLog"s approachPoints-Tabelle, nur ohne dessen aufwendige
    // Navmesh-Raycasting-Suche: hier einfach von Hand ein paar Yards vor die eigentliche Position
    // gesetzt, sobald ein konkreter Punkt als problematisch gemeldet wird.
    private static readonly Dictionary<uint, Vector3> SightseeingApproachOverrides = new()
    {
        [2162688] = new Vector3(-83.241394f, 42.393375f, -170.998f), // Barracuda Piers (Limsa Lominsa Upper Decks)
        [2162691] = new Vector3(-269.60074f, 29.380001f, -206.16368f), // The Skylift (Middle La Noscea)
        [2162690] = new Vector3(-58.95674f, 27.313725f, -118.16382f),  // Seasong Grotto (Middle La Noscea)
        [2162692] = new Vector3(194.44441f, 73.78774f, 302.63824f),    // La Thagran Eastroad (Middle La Noscea)
        [2162708] = new Vector3(-72.16092f, 11.995184f, -416.05194f),  // Woad Whisper Canyon (Middle La Noscea)
        [2162709] = new Vector3(213.05968f, 117.65125f, -222.40886f),  // Summerford Farms (Middle La Noscea)
        [2162695] = new Vector3(425.21655f, 15.025984f, 464.70297f),   // The Brewer's Beacon (Western La Noscea)
        [2162694] = new Vector3(597.14575f, 73.67687f, -112.00588f),   // Red Rooster Stead (Lower La Noscea)
        [2162710] = new Vector3(503.04245f, 106.69299f, -434.7053f),   // The Grey Fleet (Lower La Noscea)
        [2162715] = new Vector3(67.52792f, 1.9575521f, 47.7629f),      // Camp Skull Valley (Western La Noscea)
        [2162719] = new Vector3(381.97714f, 5.188155f, 198.84981f),    // Jijiroon's Trading Post (Upper La Noscea)
        [2162718] = new Vector3(-428.29407f, 69.60198f, 28.178936f),   // Thalaos (Upper La Noscea)
    };

    // Je Zwischenstopp: Position + ob dieses Teilstück fliegend angeflogen werden darf (false =
    // erzwungen zu Fuß/abgemountet, z.B. für einen Durchgang wie eine Tür, durch die man nicht
    // hindurchfliegen kann) - siehe SightseeingApproachWaypoints-Kommentar.
    public readonly record struct SightseeingApproachWaypoint(Vector3 Position, bool AllowFlying = true);

    // Von Hand nachgetragene ZWISCHENSTOPPS (der Reihe nach abzulaufen) vor der eigentlichen
    // Zielposition (Key = Adventure-RowId) - für Punkte, bei denen selbst der über die Karten-
    // Flagge/FlagToPoint gefundene grobe Laufweg (siehe SightseeingAutomation.BeginNavigateToEntry)
    // gegen eine Wand/ein Geländer läuft oder ein enger Durchgang (z.B. eine Tür) einen bestimmten,
    // NICHT fliegenden Anflugweg braucht, statt direkt den geraden/groben Weg zu nehmen. Sind welche
    // hinterlegt, läuft die Automation ZUERST der Reihe nach dorthin (jeweils mit der normalen,
    // großzügigen Toleranz, fliegend nur wenn AllowFlying) und erst vom letzten Zwischenstopp aus den
    // finalen, engen (wieder fliegend erlaubten) Schritt zur echten Position (siehe
    // SightseeingApproachOverrides/BeginFinalApproach) - der Umweg über die Karten-Flagge entfällt
    // dann komplett.
    private static readonly Dictionary<uint, SightseeingApproachWaypoint[]> SightseeingApproachWaypoints = new()
    {
        [2162688] = new[] { new SightseeingApproachWaypoint(new Vector3(-82.96662f, 41.993416f, -170.93227f)) }, // Barracuda Piers (Limsa Lominsa Upper Decks)
        [2162709] = new[] // Summerford Farms (Middle La Noscea) - erst hinfliegen, dann (weiterhin beritten, nur nicht mehr fliegend, siehe SightseeingAutomation.TryBeginPathfindAccepted) durch die Tür, dann fliegend hoch zum eigentlichen Punkt
        {
            new SightseeingApproachWaypoint(new Vector3(210.27715f, 113.26443f, -215.51048f)),
            new SightseeingApproachWaypoint(new Vector3(224.70628f, 113.49955f, -227.0562f), AllowFlying: false),
            // Erst senkrecht hoch (gleiche X/Z wie der Türausgang, nur auf Höhe von Punkt 3) - direkt
            // von der Tür aus horizontal loszufliegen führte über einen Umweg (vermutlich, weil der
            // Türausgang selbst navmesh-technisch noch als "drinnen" gilt).
            new SightseeingApproachWaypoint(new Vector3(224.70628f, 118.22706f, -227.0562f)),
            new SightseeingApproachWaypoint(new Vector3(213.03912f, 118.22706f, -222.41542f)),
        },
    };

    /// <summary>Siehe SightseeingApproachWaypoints-Kommentar.</summary>
    public static bool TryGetSightseeingApproachWaypoints(uint adventureId, out IReadOnlyList<SightseeingApproachWaypoint> waypoints)
    {
        if (SightseeingApproachWaypoints.TryGetValue(adventureId, out var found))
        {
            waypoints = found;
            return true;
        }

        waypoints = Array.Empty<SightseeingApproachWaypoint>();
        return false;
    }

    // Von Hand nachgetragene ZWISCHENSTOPPS, die NACH dem Freischalten eines Punkts der Reihe nach
    // zu Fuß (nie fliegend) abgelaufen werden, bevor es zum nächsten Sightseeing-Punkt weitergeht
    // (Key = Adventure-RowId) - für Punkte, deren Anflug (siehe SightseeingApproachWaypoints) durch
    // einen engen Durchgang wie eine Tür führt: derselbe Weg muss zu Fuß auch wieder raus, bevor
    // erneut losgeflogen werden kann. Nur, wenn nach dem aktuellen Punkt überhaupt noch ein anderer,
    // aktuell erreichbarer Sightseeing-Punkt übrig ist (siehe SightseeingAutomation.TryWalkOutOrFinish).
    private static readonly Dictionary<uint, Vector3[]> SightseeingPostCompletionWaypoints = new()
    {
        [2162709] = new[] // Summerford Farms (Middle La Noscea) - zu Fuß zurück durch die Tür
        {
            new Vector3(219.65729f, 113.499664f, -223.12563f),
            new Vector3(210.64354f, 113.49537f, -215.86862f),
        },
    };

    /// <summary>Siehe SightseeingPostCompletionWaypoints-Kommentar.</summary>
    public static bool TryGetSightseeingPostCompletionWaypoints(uint adventureId, out IReadOnlyList<Vector3> waypoints)
    {
        if (SightseeingPostCompletionWaypoints.TryGetValue(adventureId, out var found))
        {
            waypoints = found;
            return true;
        }

        waypoints = Array.Empty<Vector3>();
        return false;
    }

    // Von Hand nachgetragene, genaue Steh-Position NACH dem Abmounten am Zielpunkt (Key = Adventure-
    // RowId) - für Punkte, bei denen die normale Lande-/Ankunftsposition nach dem Abmounten (siehe
    // SightseeingAutomation.UpdateWaitingForUnlock) noch spürbar neben der tatsächlich zur
    // Freischaltung nötigen Stelle liegt (z.B. weil dort gelandet statt exakt draufgelaufen wird) -
    // dann dort noch ein letztes kurzes Stück zu Fuß hin, bevor überhaupt auf die Freischaltung
    // gewartet/der Emote ausgeführt wird, sonst schaltet der Punkt u.U. gar nicht frei.
    private static readonly Dictionary<uint, Vector3> SightseeingExactStandPositions = new()
    {
        [2162709] = new Vector3(213.07825f, 117.651245f, -222.44019f), // Summerford Farms (Middle La Noscea)
    };

    // Ein Schritt eines Jumping Puzzles: in gerader Linie (vnavmesh Path.MoveTo, ohne Wegsuche) zu
    // Target - bei Jump = true wird dabei direkt am Anfang gesprungen (Absprung = aktuelle Position,
    // also das Ziel des vorherigen Schritts bzw. der Startpunkt). RunUp = Sprung mit Anlauf: am
    // Absprungpunkt (Ziel des vorherigen Schritts) wird NICHT angehalten, sondern vom Punkt davor
    // durchgelaufen und beim Überqueren abgesprungen.
    // SprintBefore = vor diesem Schritt Sprint benutzen (bei Abklingzeit am Absprungpunkt warten).
    // Exact = Target ohne Abweichung treffen (enge vnavmesh-Wegpunkt-Toleranz; als Absprungpunkt eines
    // Anlaufs wird genau beim Überqueren abgesprungen).
    // CancelSprintBefore = vor diesem Schritt einen noch aktiven Sprint entfernen.
    public readonly record struct SightseeingPuzzleStep(Vector3 Target, bool Jump, bool RunUp = false, bool SprintBefore = false, bool Exact = false, bool CancelSprintBefore = false);

    // ExactStand = genaue Position der Sightseeing-Kugel, falls sie nicht exakt der Landepunkt des
    // letzten Schritts ist - dorthin wird nach der Landung noch genau gelaufen.
    // DismountAtStart = Start ist eine Position in der Luft: beritten genau dorthin fliegen, dort
    // absteigen (senkrecht nach unten landen) und von der Landestelle aus die Schritte ablaufen.
    public sealed record SightseeingJumpingPuzzle(Vector3 Start, SightseeingPuzzleStep[] Steps, Vector3? ExactStand = null, bool DismountAtStart = false);

    // Von Hand hinterlegte Jumping Puzzles (Key = Adventure-RowId): Die Automation steuert zuerst
    // normal den Startpunkt an, steigt ab, läuft genau darauf und arbeitet dann die Schritte der
    // Reihe nach ab. Nach dem letzten Schritt folgt das normale Emote/Freischalten an der Stelle, an
    // der der Charakter dann steht. Fällt der Charakter herunter, beginnt das Puzzle vom Startpunkt neu.
    private static readonly Dictionary<uint, SightseeingJumpingPuzzle> SightseeingJumpingPuzzles = new()
    {
        [2162697] = new( // Apkallu Falls (Gridania)
            new Vector3(-42.988815f, 10.863263f, -229.44397f),
            new[]
            {
                new SightseeingPuzzleStep(new Vector3(-41.904015f, 11.792581f, -228.58421f), Jump: true),
                new SightseeingPuzzleStep(new Vector3(-40.541237f, 14.323913f, -226.428f), Jump: true),
                new SightseeingPuzzleStep(new Vector3(-39.444984f, 15.450834f, -225.18867f), Jump: false),
                new SightseeingPuzzleStep(new Vector3(-38.183716f, 17.37831f, -224.43114f), Jump: true),
                new SightseeingPuzzleStep(new Vector3(-31.467901f, 20.728025f, -222.71025f), Jump: false),
                new SightseeingPuzzleStep(new Vector3(-27.137672f, 20.6441f, -231.32637f), Jump: false),
                new SightseeingPuzzleStep(new Vector3(-19.493988f, 21.485334f, -230.3096f), Jump: false),
                new SightseeingPuzzleStep(new Vector3(-19.841375f, 19.849356f, -233.34421f), Jump: false, SprintBefore: true), // auf Punkt 7 Sprint, dann Anlauf
                new SightseeingPuzzleStep(new Vector3(-20.96915f, 16.238455f, -240.18513f), Jump: true, RunUp: true), // mit Anlauf über Punkt 8
            },
            ExactStand: new Vector3(-21.040531f, 16.238455f, -240.50746f)), // Sightseeing-Kugel - nach der Landung genau hierhin
        [2162725] = new( // The Lancers' Guild (Gridania)
            new Vector3(173.8963f, 17.499987f, -268.02325f),
            new[]
            {
                new SightseeingPuzzleStep(new Vector3(171.26968f, 18.623f, -266.38104f), Jump: true),              // Punkt 2
                new SightseeingPuzzleStep(new Vector3(170.97382f, 18.623f, -264.71155f), Jump: false),              // Punkt 3 - Anlauf ab Punkt 2
                new SightseeingPuzzleStep(new Vector3(170.76247f, 18.623f, -264.73038f), Jump: false, RunUp: true), // Punkt 4 - auf Punkt 3 nicht anhalten
                new SightseeingPuzzleStep(new Vector3(166.31177f, 18.1f, -264.707f), Jump: true, RunUp: true),      // Punkt 5 - bei Punkt 4 abspringen
                new SightseeingPuzzleStep(new Vector3(164.9289f, 18.099998f, -264.70456f), Jump: false),            // Punkt 6 - Anlauf ab Punkt 5
                new SightseeingPuzzleStep(new Vector3(160.96414f, 18.1f, -264.70605f), Jump: true, RunUp: true),    // Punkt 7 - bei Punkt 6 abspringen
                new SightseeingPuzzleStep(new Vector3(159.43863f, 18.099998f, -264.7028f), Jump: false, SprintBefore: true), // Punkt 8 - auf Punkt 7 Sprint, dann Anlauf
                new SightseeingPuzzleStep(new Vector3(153.02248f, 17.821384f, -264.7026f), Jump: true, RunUp: true), // Punkt 9 (Sightseeing-Punkt) - bei Punkt 8 abspringen
            }),
        [2162696] = new( // The Leatherworkers' Guild (Gridania)
            new Vector3(82.04379f, 9.999974f, -172.27545f),
            new[]
            {
                new SightseeingPuzzleStep(new Vector3(83.50681f, 10.9113455f, -171.19609f), Jump: true),  // Punkt 2
                new SightseeingPuzzleStep(new Vector3(82.570786f, 10.9f, -169.67464f), Jump: false),     // Punkt 3
                new SightseeingPuzzleStep(new Vector3(81.71872f, 12.7f, -168.0916f), Jump: true),        // Punkt 4
                new SightseeingPuzzleStep(new Vector3(81.43062f, 12.7f, -167.5926f), Jump: false),       // Punkt 5 (Sightseeing-Punkt)
            }),
        [2162704] = new( // The Ruins of Sil'dih (Central Thanalan) - kein Sprung, nur genauer Fußweg
            new Vector3(-283.50278f, -12.651595f, 74.74288f), // in der Luft - dort absteigen
            new[]
            {
                new SightseeingPuzzleStep(new Vector3(-275.7539f, -14.272285f, 74.517914f), Jump: false, Exact: true), // Punkt 2 (Sightseeing-Punkt)
            },
            DismountAtStart: true),
        [2162724] = new( // The Carline Canopy (Gridania)
            new Vector3(144.2908f, -13.261837f, 160.06638f),
            new[]
            {
                new SightseeingPuzzleStep(new Vector3(145.5004f, -11.55687f, 162.28738f), Jump: true),                       // Punkt 2
                new SightseeingPuzzleStep(new Vector3(148.23187f, -8.264013f, 167.01671f), Jump: false, Exact: true),        // Punkt 3 - genau, sonst kein Anlauf möglich
                new SightseeingPuzzleStep(new Vector3(148.50673f, -8.263825f, 166.36598f), Jump: false, Exact: true, CancelSprintBefore: true), // Punkt 4 - auf Punkt 3 Sprint entfernen, Anlauf, genau hier abspringen
                new SightseeingPuzzleStep(new Vector3(150.31108f, -9.416907f, 161.36722f), Jump: true, RunUp: true),        // Punkt 5 - bei Punkt 4 abspringen
                new SightseeingPuzzleStep(new Vector3(150.24185f, -9.401789f, 161.39577f), Jump: false, SprintBefore: true, Exact: true), // Punkt 6 - auf Punkt 5 Sprint, dann genau hierhin
                new SightseeingPuzzleStep(new Vector3(150.25928f, -9.405594f, 160.7533f), Jump: false, Exact: true),        // Punkt 7 - Anlauf ab Punkt 6, genau hier abspringen
                new SightseeingPuzzleStep(new Vector3(150.56349f, -9.47222f, 154.79427f), Jump: true, RunUp: true),         // Punkt 8 (Sightseeing-Punkt) - bei Punkt 7 abspringen
            }),
    };

    /// <summary>Siehe SightseeingJumpingPuzzles-Kommentar.</summary>
    public static bool TryGetSightseeingJumpingPuzzle(uint adventureId, out SightseeingJumpingPuzzle puzzle) =>
        SightseeingJumpingPuzzles.TryGetValue(adventureId, out puzzle!);

    private const uint JumpGeneralActionId = 2;

    /// <summary>Springt (wie die Leertaste) - Allgemeine Aktion "Springen".</summary>
    public static unsafe bool TryJump()
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        return actionManager->UseAction(ActionType.GeneralAction, JumpGeneralActionId);
    }

    /// <summary>Siehe SightseeingExactStandPositions-Kommentar.</summary>
    public static bool TryGetSightseeingExactStandPosition(uint adventureId, out Vector3 position)
    {
        return SightseeingExactStandPositions.TryGetValue(adventureId, out position);
    }

    private static List<CollectibleEntry>? sightseeingEntriesCache;
    private static List<(uint FirstAdventureId, uint LastAdventureId, uint QuestId)>? sightseeingGateQuestsCache;

    /// <summary>
    /// Löst je Buch AB Stormblood die zum Freischalten nötige Quest auf (Lumina-Sheet
    /// "AdventureExPhase": AdventureBegin/AdventureEnd markieren den RowId-Bereich der zu diesem Buch
    /// gehörenden Sichtungspunkte, Quest die dafür nötige Haupt-/Nebenquest) - anders als bei A Realm
    /// Reborn/Heavensward (siehe SightseeingFirstBookCount/SightseeingHeavenswardQuestId) ist das hier
    /// vollständig aus Lumina auslesbar, keine von Hand gepflegten Werte nötig.
    /// </summary>
    private static List<(uint FirstAdventureId, uint LastAdventureId, uint QuestId)> ResolveSightseeingGateQuests()
    {
        if (sightseeingGateQuestsCache != null)
            return sightseeingGateQuestsCache;

        var result = new List<(uint, uint, uint)>();
        var phaseSheet = DataManager.GetExcelSheet<AdventureExPhase>();
        if (phaseSheet != null)
        {
            foreach (var phase in phaseSheet)
            {
                var firstId = phase.AdventureBegin.RowId;
                var lastId = phase.AdventureEnd.RowId;
                var questId = phase.Quest.RowId;
                if (firstId != 0 && lastId != 0 && questId != 0)
                    result.Add((firstId, lastId, questId));
            }
        }

        sightseeingGateQuestsCache = result;
        return result;
    }

    /// <summary>
    /// Baut die vollständige Liste aller Sightseeing-Log-Punkte live aus Lumina auf ("Adventure"-
    /// Sheet) - EINMAL global (nicht pro Zone, anders als früher), weil die Zusatz-Bedingungen
    /// (v.a. SightseeingNeedsFirstTwenty) die laufende Nummer INNERHALB DES GESAMTEN Sheets brauchen.
    /// Positions-Umrechnung: Lumina "Level" speichert X/Y/Z bereits in der normalen FFXIV-Weltkoordinaten-
    /// Konvention (Y = Höhe, siehe Level.cs Feld-Offsets 0/4/8) - FRÜHER wurde hier fälschlich Y und Z
    /// vertauscht (vom Dalamud-Plugin "Tourist" übernommen, das denselben Fehler macht, dort aber nur
    /// eine kosmetische VFX-Markierung betrifft statt echter Lauf-Ziele) - das war vermutlich die
    /// Ursache für "Positionen stimmen nicht". +0.5f auf die Höhe, damit der Zielpunkt nicht im Boden
    /// einsackt (ebenfalls von Tourist übernommen). Wetter-/Zeit-/Buch-Freischalt-Bedingungen siehe
    /// RealmRebornVistaWeathers/SightseeingFirstBookCount/SightseeingHeavenswardQuestId/
    /// ResolveSightseeingGateQuests - alle vier Aspekte (Position, Wetter, Zeit, Buch-Freischaltung)
    /// nach Vorbild des quelloffenen Dalamud-Plugins "AutoSightseeingLog".
    /// </summary>
    public static List<CollectibleEntry> GetSightseeingEntries()
    {
        if (sightseeingEntriesCache != null)
            return sightseeingEntriesCache;

        var result = new List<CollectibleEntry>();
        var adventureSheet = DataManager.GetExcelSheet<Adventure>();
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (adventureSheet == null || territorySheet == null)
        {
            sightseeingEntriesCache = result;
            return result;
        }

        var gateQuests = ResolveSightseeingGateQuests();
        var number = 0;

        foreach (var row in adventureSheet)
        {
            number++;

            try
            {
                var level = row.Level.ValueNullable;
                if (level == null)
                    continue;

                var territoryId = level.Value.Territory.RowId;
                if (territoryId == 0)
                    continue;

                var name = row.Name.ToString();
                if (string.IsNullOrEmpty(name))
                    continue;

                var emoteCommand = row.Emote.ValueNullable?.TextCommand.ValueNullable?.Command.ToString();

                // 0 = A Realm Reborn (siehe TerritoryType.ExVersion), 1 = Heavensward, 2+ = Stormblood
                // und später - dieselbe Reihenfolge wie in "AutoSightseeingLog"s ExpansionKind-Enum.
                var expansion = territorySheet.TryGetRow(territoryId, out var territory) ? territory.ExVersion.RowId : 0;

                uint gateQuestId = 0;
                var needsFirstTwenty = false;
                if (expansion == 0)
                {
                    needsFirstTwenty = number > SightseeingFirstBookCount;
                }
                else if (expansion == 1)
                {
                    gateQuestId = SightseeingHeavenswardQuestId;
                }
                else
                {
                    foreach (var (firstId, lastId, questId) in gateQuests)
                    {
                        if (row.RowId >= firstId && row.RowId <= lastId)
                        {
                            gateQuestId = questId;
                            break;
                        }
                    }
                }

                var weatherMask = expansion == 0 && number <= RealmRebornVistaWeathers.Length
                    ? RealmRebornVistaWeathers[number - 1]
                    : 0u;

                result.Add(new CollectibleEntry
                {
                    Id = row.RowId,
                    Name = name,
                    Type = CollectibleType.Sightseeing,
                    Category = Loc.T("Sightseeing", "Sightseeing"),
                    TerritoryTypeId = territoryId,
                    MapId = level.Value.Map.RowId,
                    WorldPosition = SightseeingApproachOverrides.TryGetValue(row.RowId, out var overridePos)
                        ? overridePos
                        : new Vector3(level.Value.X, level.Value.Y + 0.5f, level.Value.Z),
                    RequiredEmoteCommand = string.IsNullOrEmpty(emoteCommand) ? null : emoteCommand,
                    SightseeingWeatherMask = weatherMask,
                    SightseeingHasTimeWindow = row.MinTime != 0 || row.MaxTime != 0,
                    SightseeingFirstBell = (byte)(row.MinTime / 100),
                    SightseeingLastBell = (byte)(row.MaxTime / 100),
                    SightseeingGateQuestId = gateQuestId,
                    SightseeingNeedsFirstTwenty = needsFirstTwenty,
                    Source = Loc.T("Sightseeing", "Sightseeing"),
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Fehler bei Adventure-Zeile {row.RowId}");
            }
        }

        sightseeingEntriesCache = result;
        return result;
    }

    /// <summary>
    /// Aktuelle Eorzea-Stunde (0-23) - eine "Glocke" dauert 175 Erdsekunden, ein Eorzea-Tag also
    /// 24*175=4200 Erdsekunden. Server-Zeit (Framework.GetServerTime, Unix-Sekunden) statt lokaler
    /// Client-Uhr, damit eine falsch eingestellte PC-Uhrzeit die Prüfung nicht verfälscht - dieselbe
    /// Formel wie im Dalamud-Plugin "AutoSightseeingLog" (Core/Time/EorzeaTime.cs).
    /// </summary>
    private static int GetCurrentEorzeaBell()
    {
        return (int)(GetServerUnixSeconds() / 175 % 24);
    }

    /// <summary>
    /// Unix-Sekunden für die Eorzeazeit-/Wetterberechnung - bewusst die lokale Uhr (DateTime.UtcNow),
    /// NICHT Framework.GetServerTime(): Framework.GetServerTime() spiegelt offenbar nur den zuletzt
    /// vom Server empfangenen Zeitstempel wider (aktualisiert sich nicht jeden Frame live), wodurch
    /// er ein paar Sekunden hinter der tatsächlichen Zeit zurückliegen kann - das führte zu einer
    /// spürbaren (~2s) Abweichung gegenüber dem Dalamud-Plugin "Tourist", das (wie die meisten
    /// Wetter-Tools) durchgängig die lokale Uhr nutzt. Bei korrekt eingestellter PC-Uhr (Standard bei
    /// automatischer Zeitsynchronisation) ist die lokale Uhr hier daher tatsächlich genauer.
    /// </summary>
    private static long GetServerUnixSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Ob die aktuelle Eorzea-Zeit im (ggf. über Mitternacht laufenden) Zeitfenster des Punkts liegt.
    /// Nur für Einträge mit SightseeingHasTimeWindow relevant.
    /// </summary>
    private static bool IsSightseeingTimeOk(CollectibleEntry entry)
    {
        var bell = GetCurrentEorzeaBell();
        return entry.SightseeingFirstBell <= entry.SightseeingLastBell
            ? bell >= entry.SightseeingFirstBell && bell <= entry.SightseeingLastBell
            : bell >= entry.SightseeingFirstBell || bell <= entry.SightseeingLastBell;
    }

    /// <summary>
    /// Ob das aktuelle Wetter der Zone des Punkts zu seiner SightseeingWeatherMask passt - über
    /// GetWeatherIdAtUnixSeconds (funktioniert für JEDE Zone, nicht nur die aktuell geladene, da
    /// FFXIV-Wetter deterministisch aus Zone + Zeit berechnet wird). Nur für Einträge mit
    /// SightseeingWeatherMask != 0 relevant (ausschließlich A-Realm-Reborn-Punkte).
    /// </summary>
    private static bool IsSightseeingWeatherOk(CollectibleEntry entry)
    {
        var weatherId = GetWeatherIdAtUnixSeconds(entry.TerritoryTypeId, GetServerUnixSeconds());
        return weatherId < 32 && (entry.SightseeingWeatherMask & (1u << (int)weatherId)) != 0;
    }

    // Exakter FFXIV-Wetteralgorithmus (SaintCoinach/community-verifiziert, siehe
    // github.com/karashiiro/FFXIVWeather, FFXIVWeatherService.CalculateTarget/GetCurrentWeather) -
    // direkt gegen unsere eigenen Lumina-Daten (TerritoryType.WeatherRate -> WeatherRate-Sheet)
    // nachgebaut, statt sich auf FFXIVClientStructs' WeatherManager.GetWeatherForHour(hourOffset) zu
    // verlassen: dessen Ergebnisse für hourOffset != 0 wichen spürbar von etablierten Referenz-Tools
    // wie dem Dalamud-Plugin "Tourist" ab (das denselben SaintCoinach-Algorithmus nutzt). Wetter-
    // Perioden sind auf FESTE 1400-Sekunden-Blöcke (23min20s = 8 Eorzea-Stunden) seit der Unix-Epoche
    // ausgerichtet, nicht relativ zu "jetzt" - diese Variante berechnet den exakten Perioden-Beginn
    // direkt, keine Rundung auf ganze Eorzea-Stunden nötig.
    private const long WeatherPeriodSeconds = 1400;

    private static int CalculateWeatherTarget(long periodStartUnixSeconds)
    {
        var bell = periodStartUnixSeconds / 175;
        // "Magic" aus SaintCoinach: für die Berechnung ist 16:00 = 0, 00:00 = 8, 08:00 = 16.
        var increment = (uint)(bell + 8 - (bell % 8)) % 24;
        var totalDays = (uint)(periodStartUnixSeconds / 4200);

        var calcBase = totalDays * 100 + increment;
        var step1 = (calcBase << 11) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    /// <summary>
    /// Wetter-Sheet-RowId für eine Zone zu einem beliebigen (auch zukünftigen) Zeitpunkt - 0, wenn
    /// TerritoryType/WeatherRate nicht auflösbar sind.
    /// </summary>
    private static uint GetWeatherIdAtUnixSeconds(uint territoryId, long unixSeconds)
    {
        var periodStart = unixSeconds - (((unixSeconds % WeatherPeriodSeconds) + WeatherPeriodSeconds) % WeatherPeriodSeconds);
        var target = CalculateWeatherTarget(periodStart);

        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(territoryId, out var territory))
            return 0;

        var weatherRateSheet = DataManager.GetExcelSheet<WeatherRate>();
        if (weatherRateSheet == null || !weatherRateSheet.TryGetRow(territory.WeatherRate.RowId, out var weatherRate))
            return 0;

        var cumulative = 0;
        for (var i = 0; i < weatherRate.Rate.Count; i++)
        {
            cumulative += weatherRate.Rate[i];
            if (target < cumulative)
                return weatherRate.Weather[i].RowId;
        }

        return 0;
    }

    // Wetter ist KEIN festes Rotationsmuster - deterministisch aus Zone + absoluter Eorzea-Zeit
    // berechnet (siehe GetWeatherIdAtUnixSeconds/CalculateWeatherTarget), ein seltenes Wetter kann
    // daher mehrere Eorzea-Tage auf sich warten lassen. 120 Wetter-Perioden (168000 Sekunden, ca. 7
    // Erdentage) als Obergrenze reichen praktisch immer, ohne den Scan unbegrenzt laufen zu lassen.
    private const int SightseeingAvailabilityLookaheadPeriods = 120;
    private static readonly TimeSpan SightseeingAvailabilityCacheTtl = TimeSpan.FromSeconds(30);

    // Absolute Zeitpunkte statt fertiger Restdauern gecacht, damit die Anzeige (siehe
    // GetSightseeingAvailabilityLabel/GetSightseeingActiveUntilLabel) bei jedem Aufruf frisch
    // "verfügbar in X" nachrechnen und so wie ein echter Countdown runterzählen kann, ohne bei
    // jedem UI-Frame den teuren Perioden-Scan erneut laufen zu lassen (der nur alle
    // SightseeingAvailabilityCacheTtl neu passiert).
    private static readonly Dictionary<uint, (DateTime ComputedAt, DateTime? AvailableAtUtc)> sightseeingAvailableAtCache = new();
    private static readonly Dictionary<uint, (DateTime ComputedAt, DateTime? UnavailableAtUtc)> sightseeingUnavailableAtCache = new();

    /// <summary>
    /// Ob die Eorzea-Bell "bell" im (ggf. über Mitternacht laufenden) Zeitfenster des Punkts liegt.
    /// </summary>
    private static bool IsBellInSightseeingWindow(CollectibleEntry entry, int bell) =>
        entry.SightseeingFirstBell <= entry.SightseeingLastBell
            ? bell >= entry.SightseeingFirstBell && bell <= entry.SightseeingLastBell
            : bell >= entry.SightseeingFirstBell || bell <= entry.SightseeingLastBell;

    /// <summary>Ob das Wetter zum gegebenen Unix-Zeitpunkt zur SightseeingWeatherMask passt (true, wenn der Punkt gar keine Wetter-Bedingung hat).</summary>
    private static bool IsWeatherOkAtUnixSeconds(CollectibleEntry entry, long unixSeconds)
    {
        if (entry.SightseeingWeatherMask == 0)
            return true;

        var weatherId = GetWeatherIdAtUnixSeconds(entry.TerritoryTypeId, unixSeconds);
        return weatherId < 32 && (entry.SightseeingWeatherMask & (1u << (int)weatherId)) != 0;
    }

    /// <summary>
    /// Absoluter Zeitpunkt (UTC), ab dem Wetter UND Uhrzeit gleichzeitig zum Punkt passen - null,
    /// wenn der Punkt keine solche Bedingung hat, oder wenn dafür (innerhalb von
    /// SightseeingAvailabilityLookaheadPeriods) partout keine passende Kombination gefunden wird.
    /// Scannt dafür in EXAKTEN Wetter-Perioden (1400 Sekunden, absolut seit Unix-Epoche ausgerichtet
    /// - siehe GetWeatherIdAtUnixSeconds/WeatherPeriodSeconds) vorwärts, nicht in 175-Sekunden-
    /// Schritten relativ zu "jetzt": Wetter ist innerhalb einer Periode konstant, ein an "jetzt"
    /// ausgerichtetes Raster würde die echte (an der Epoche ausgerichtete) Wechselgrenze verfehlen
    /// und bis zu eine ganze Periode zu spät melden - genau das führte zu einer spürbaren Abweichung
    /// gegenüber dem Dalamud-Plugin "Tourist". Innerhalb einer wetterlich passenden Periode wird bei
    /// zusätzlichem Zeitfenster die exakte Bell-Grenze gesucht (175-Sekunden-Raster, 8 Bells je
    /// Periode).
    /// </summary>
    private static DateTime? GetSightseeingAvailableAtUtc(CollectibleEntry entry)
    {
        if (entry.SightseeingWeatherMask == 0 && !entry.SightseeingHasTimeWindow)
            return null;

        if (sightseeingAvailableAtCache.TryGetValue(entry.Id, out var cached) && DateTime.UtcNow - cached.ComputedAt < SightseeingAvailabilityCacheTtl)
            return cached.AvailableAtUtc;

        var now = DateTime.UtcNow;
        var nowUnix = GetServerUnixSeconds();
        var currentPeriodStart = nowUnix - (nowUnix % WeatherPeriodSeconds);
        DateTime? result = null;

        for (var p = 0; p <= SightseeingAvailabilityLookaheadPeriods && result == null; p++)
        {
            var candidatePeriodStart = currentPeriodStart + p * WeatherPeriodSeconds;

            if (!IsWeatherOkAtUnixSeconds(entry, candidatePeriodStart))
                continue;

            if (!entry.SightseeingHasTimeWindow)
            {
                var candidateUnix = Math.Max(candidatePeriodStart, nowUnix);
                result = now.AddSeconds(candidateUnix - nowUnix);
                break;
            }

            for (var bellOffset = 0; bellOffset < 8; bellOffset++)
            {
                var candidateUnix = candidatePeriodStart + bellOffset * 175L;
                if (candidateUnix < nowUnix)
                    continue;

                var bell = (int)(candidateUnix / 175 % 24);
                if (IsBellInSightseeingWindow(entry, bell))
                {
                    result = now.AddSeconds(candidateUnix - nowUnix);
                    break;
                }
            }
        }

        sightseeingAvailableAtCache[entry.Id] = (now, result);
        return result;
    }

    /// <summary>
    /// Gegenstück zu GetSightseeingAvailableAtUtc - absoluter Zeitpunkt (UTC), ab dem Wetter ODER
    /// Uhrzeit NICHT mehr passen (erste exakte Wetter-Perioden-/Bell-Grenze, an der mindestens eine
    /// der beiden Bedingungen kippt, siehe dortigen Kommentar zur Perioden-Ausrichtung). Nur
    /// sinnvoll, wenn der Punkt gerade aktiv ist - sonst (siehe GetSightseeingActiveUntilLabel) wird
    /// diese Funktion gar nicht erst aufgerufen.
    /// </summary>
    private static DateTime? GetSightseeingUnavailableAtUtc(CollectibleEntry entry)
    {
        if (entry.SightseeingWeatherMask == 0 && !entry.SightseeingHasTimeWindow)
            return null;

        if (sightseeingUnavailableAtCache.TryGetValue(entry.Id, out var cached) && DateTime.UtcNow - cached.ComputedAt < SightseeingAvailabilityCacheTtl)
            return cached.UnavailableAtUtc;

        var now = DateTime.UtcNow;
        var nowUnix = GetServerUnixSeconds();
        var currentPeriodStart = nowUnix - (nowUnix % WeatherPeriodSeconds);
        DateTime? result = null;

        for (var p = 0; p <= SightseeingAvailabilityLookaheadPeriods && result == null; p++)
        {
            var candidatePeriodStart = currentPeriodStart + p * WeatherPeriodSeconds;

            if (!IsWeatherOkAtUnixSeconds(entry, candidatePeriodStart))
            {
                var candidateUnix = Math.Max(candidatePeriodStart, nowUnix);
                result = now.AddSeconds(candidateUnix - nowUnix);
                break;
            }

            if (!entry.SightseeingHasTimeWindow)
                continue;

            for (var bellOffset = 0; bellOffset < 8; bellOffset++)
            {
                var candidateUnix = candidatePeriodStart + bellOffset * 175L;
                if (candidateUnix < nowUnix)
                    continue;

                var bell = (int)(candidateUnix / 175 % 24);
                if (!IsBellInSightseeingWindow(entry, bell))
                {
                    result = now.AddSeconds(candidateUnix - nowUnix);
                    break;
                }
            }
        }

        sightseeingUnavailableAtCache[entry.Id] = (now, result);
        return result;
    }

    /// <summary>
    /// Für die Sightseeing-Automation (AFK-Modus): ob ein Punkt NUR wegen Wetter/Uhrzeit gerade nicht
    /// erledigbar ist - alle dauerhaften Voraussetzungen (Log/Buch freigeschaltet, Fliegen, kein
    /// Jumping Puzzle) sind erfüllt, er wird also irgendwann von selbst verfügbar. Punkte mit
    /// dauerhaften Blockern ändern sich beim Warten nicht und zählen daher nicht als "noch zu tun".
    /// </summary>
    public static bool IsSightseeingOnlyTemporarilyUnavailable(CollectibleEntry entry)
    {
        if (entry.Type != CollectibleType.Sightseeing)
            return false;

        if (!IsSightseeingLogUnlocked() || !IsSightseeingFlyingRequirementMet(entry) || IsSightseeingUnsupportedByAutomation(entry.Id))
            return false;
        if (entry.SightseeingNeedsFirstTwenty && !AreFirstSightseeingBookEntriesComplete())
            return false;
        if (entry.SightseeingGateQuestId != 0 && !QuestManager.IsQuestComplete((ushort)entry.SightseeingGateQuestId))
            return false;

        return (entry.SightseeingWeatherMask != 0 && !IsSightseeingWeatherOk(entry))
               || (entry.SightseeingHasTimeWindow && !IsSightseeingTimeOk(entry));
    }

    /// <summary>Wann ein nur wegen Wetter/Uhrzeit gesperrter Punkt wieder verfügbar wird, als Text ("in 12 Min.") - leer, wenn unbekannt.</summary>
    public static string GetSightseeingAvailableInText(CollectibleEntry entry) =>
        GetRemaining(GetSightseeingAvailableAtUtc(entry)) is { } remaining ? FormatSightseeingAvailableIn(remaining) : string.Empty;

    /// <summary>Restzeit bis ein Punkt verfügbar wird (für die Sortierung "bald verfügbar zuerst").</summary>
    public static TimeSpan? GetSightseeingAvailableIn(CollectibleEntry entry) => GetRemaining(GetSightseeingAvailableAtUtc(entry));

    private static TimeSpan? GetRemaining(DateTime? targetUtc)
    {
        if (targetUtc == null)
            return null;

        var remaining = targetUtc.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Dauer als echter HH:MM:SS-Countdown (z.B. "02:15:30") - wird bei jedem Aufruf frisch aus dem
    /// gecachten Zielzeitpunkt neu berechnet (siehe GetRemaining), zählt dadurch bis auf die Sekunde
    /// genau runter, nicht nur in groben Stunden-/Minutenschritten.
    /// </summary>
    private static string FormatSightseeingAvailableIn(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>
    /// Ob bei diesem Sightseeing-Punkt alle Voraussetzungen MIT HÖHERER PRIORITÄT als Jumping-
    /// Puzzle-Status/Wetter/Uhrzeit bereits erfüllt sind - Log freigeschaltet, Fliegen freigeschaltet,
    /// ggf. erste 20 A-Realm-Reborn-Punkte erledigt und das Buch per Quest freigeschaltet (siehe
    /// ComputeGrandCompanyOrTribeGateReason für die genaue Reihenfolge). Erst wenn das hier true ist,
    /// sind Jumping-Puzzle-Status oder Wetter/Uhrzeit überhaupt der tatsächlich aktuelle Blockierer.
    /// Ohne diese Prüfung würde z.B. bei noch gesperrtem Log/Fliegen ein irreführender Wetter/Zeit-
    /// Timer angezeigt, obwohl der Punkt aus einem ganz anderen Grund nicht erreichbar ist.
    /// </summary>
    public static bool IsSightseeingBookAccessible(CollectibleEntry entry)
    {
        if (!IsSightseeingLogUnlocked())
            return false;
        if (!IsSightseeingFlyingRequirementMet(entry))
            return false;
        if (entry.SightseeingNeedsFirstTwenty && !AreFirstSightseeingBookEntriesComplete())
            return false;
        if (entry.SightseeingGateQuestId != 0 && !QuestManager.IsQuestComplete((ushort)entry.SightseeingGateQuestId))
            return false;

        return true;
    }

    /// <summary>
    /// Ob AKTUELL wirklich fehlendes Fliegen der Blockierer eines Sightseeing-Punkts ist (Log ist
    /// schon freigeschaltet, sonst wäre DAS die eigentliche Ursache) - für den spezifischen inline
    /// "(Bedingung nicht erfüllt (Fliegen nicht freigeschaltet))"-Hinweis im Overlay (siehe
    /// CompactOverlayWindow), statt nur des generischen Labels.
    /// </summary>
    public static bool IsSightseeingBlockedByFlying(CollectibleEntry entry) =>
        entry.Type == CollectibleType.Sightseeing && IsSightseeingLogUnlocked() && !IsSightseeingFlyingRequirementMet(entry);

    /// <summary>
    /// " - <Dauer>"-Zusatz fürs inline "(Bedingung nicht erfüllt)"-Label im Overlay (siehe
    /// CompactOverlayWindow) - NUR für Sightseeing-Punkte, die AKTUELL wirklich durch Wetter/Uhrzeit
    /// gesperrt sind (siehe IsSightseeingBookAccessible). Leer, wenn nicht anwendbar (anderer Typ/
    /// anderer Sperrgrund) oder innerhalb der Vorschau partout kein Zeitpunkt gefunden wurde. Rechnet
    /// die Restdauer bei jedem Aufruf frisch aus dem gecachten Zielzeitpunkt - zählt dadurch wie ein
    /// echter Countdown runter, ohne den Scan selbst jedes Mal zu wiederholen.
    /// </summary>
    public static string GetSightseeingAvailabilityLabel(CollectibleEntry entry)
    {
        if (entry.Type != CollectibleType.Sightseeing || !IsSightseeingBookAccessible(entry))
            return string.Empty;

        var remaining = GetRemaining(GetSightseeingAvailableAtUtc(entry));
        return remaining.HasValue ? $" - {FormatSightseeingAvailableIn(remaining.Value)}" : string.Empty;
    }

    /// <summary>
    /// " - noch <Dauer>"-Zusatz für einen grünen "(aktiv)"-Hinweis im Overlay (siehe
    /// CompactOverlayWindow) - NUR für Sightseeing-Punkte mit Wetter-/Zeitfenster-Bedingung, die
    /// GERADE aktiv (also nicht gesperrt) sind, mit Restdauer bis diese Bedingung wieder kippt.
    /// Leer für Punkte ohne eine solche Bedingung (die sind ja nie "gesperrt", ein Aktiv-Hinweis
    /// wäre dort bedeutungslos) oder wenn der Punkt gerade tatsächlich gesperrt ist.
    /// </summary>
    public static string GetSightseeingActiveUntilLabel(CollectibleEntry entry)
    {
        if (entry.Type != CollectibleType.Sightseeing)
            return string.Empty;
        if (entry.SightseeingWeatherMask == 0 && !entry.SightseeingHasTimeWindow)
            return string.Empty;
        if (!IsSightseeingBookAccessible(entry))
            return string.Empty;
        if (entry.SightseeingWeatherMask != 0 && !IsSightseeingWeatherOk(entry))
            return string.Empty;
        if (entry.SightseeingHasTimeWindow && !IsSightseeingTimeOk(entry))
            return string.Empty;

        var remaining = GetRemaining(GetSightseeingUnavailableAtUtc(entry));
        return remaining.HasValue
            ? $" - {FormatSightseeingAvailableIn(remaining.Value)}"
            : string.Empty;
    }

    /// <summary>
    /// Ob alle ersten SightseeingFirstBookCount A-Realm-Reborn-Sichtungspunkte bereits aufgezeichnet
    /// sind - live über PlayerState.IsAdventureComplete, für SightseeingNeedsFirstTwenty. Es gibt
    /// kein eigenes "ist A Realm Reborn"-Feld auf CollectibleEntry - SightseeingWeatherMask != 0
    /// identifiziert dieselbe Menge zuverlässig (siehe RealmRebornVistaWeathers-Kommentar: JEDER
    /// A-Realm-Reborn-Punkt hat eine Wetter-Bedingung, KEIN späterer), kombiniert mit
    /// "!SightseeingNeedsFirstTwenty" (nur bei A-Realm-Reborn-Nummer 1-20 gesetzt) ergibt das genau
    /// die ersten 20.
    /// </summary>
    private static unsafe bool AreFirstSightseeingBookEntriesComplete()
    {
        var firstBook = GetSightseeingEntries().Where(e => !e.SightseeingNeedsFirstTwenty && e.SightseeingWeatherMask != 0);
        return firstBook.All(e => PlayerState.Instance()->IsAdventureComplete(e.Id));
    }

    private static Dictionary<uint, string>? questNameByIdCache;

    /// <summary>
    /// Umgekehrte Richtung zu ResolveQuestIdByName - für Hinweistexte, die nur eine Quest-RowId
    /// kennen (siehe SightseeingGateQuestId), aber den Anzeigenamen brauchen.
    /// </summary>
    private static string? ResolveQuestNameById(uint questId)
    {
        if (questNameByIdCache == null)
        {
            questNameByIdCache = new Dictionary<uint, string>();
            var questSheet = DataManager.GetExcelSheet<Quest>();
            if (questSheet != null)
            {
                foreach (var row in questSheet)
                {
                    var name = row.Name.ToString();
                    if (!string.IsNullOrEmpty(name))
                        questNameByIdCache[row.RowId] = name;
                }
            }
        }

        return questNameByIdCache.TryGetValue(questId, out var questName) ? questName : null;
    }

    private readonly record struct ChocobokeepLocation(uint ChocoboTaxiStandId, uint TerritoryId, Vector3 Position);

    /// <summary>
    /// Von Hand erfasste Chocobokeep-Standorte (Reitstand-RowId + Zone + rohe Weltposition) - anders
    /// als z.B. Aetheryten/Sightseeing gibt es keine Lumina-Sheet-Spalte, die einen Chocobokeep-NPC
    /// direkt mit seiner "ChocoboTaxiStand"-RowId (siehe UIState.IsChocoboTaxiStandUnlocked) UND
    /// seiner Weltposition verknüpft - das Sheet selbst kennt nur Name + erreichbare Nachbarstände
    /// (TargetLocations), keine Zone/Position. Übernommen aus dem etablierten, quelloffen im
    /// installierten Dalamud-Plugin "Henchman" enthaltenen Datensatz (Data/ChocoboTaxiStands.json,
    /// per ilspycmd/Dateibetrachtung geprüft) - dieselbe Herangehensweise wie schon bei den
    /// Aetheryte-Pin-Kommentaren oben (ManualAetherytePositions), nur diesmal die GESAMTE Datenbasis,
    /// nicht nur ein paar Korrekturen.
    /// </summary>
    private static readonly ChocobokeepLocation[] ChocobokeepLocations =
    {
        new(1179669, 129, new(45.82275f, 19.97406f, -8.097595f)),
        new(1179658, 130, new(55.35967f, 4.124078f, -143.8992f)),
        new(1179650, 132, new(32.32222f, -0.05002153f, 70.31564f)),
        new(1179670, 134, new(187.9367f, 98.52471f, -193.1733f)),
        new(1179676, 135, new(503.1389f, 79.17908f, -74.80962f)),
        new(1179675, 135, new(49.29806f, 29.3155f, 605.2917f)),
        new(1179673, 137, new(12.93436f, 69.64355f, 21.32637f)),
        new(1179677, 137, new(423.7469f, 18.52183f, 448.5948f)),
        new(1179671, 138, new(667.7173f, 9.882255f, 487.3286f)),
        new(1179672, 138, new(298.6616f, -24.99786f, 233.1584f)),
        new(1179674, 139, new(413.3674f, 4.109592f, 88.74062f)),
        new(1179659, 140, new(63.65807f, 45.20808f, -193.5759f)),
        new(1179660, 140, new(-415.7931f, 23.08685f, -335.7137f)),
        new(1179661, 140, new(-246.0823f, 32.44361f, 383.8406f)),
        new(1179662, 141, new(-2.704952f, -2.055626f, -158.4958f)),
        new(1179663, 145, new(-423.2201f, -39.06165f, 112.2323f)),
        new(1179667, 145, new(-532.0974f, -0.1068726f, -199.8474f)),
        new(1179664, 146, new(-176.196f, 26.90161f, -411.6122f)),
        new(1179665, 146, new(-309.2189f, 7.375896f, 417.8488f)),
        new(1179666, 147, new(54.94769f, 3.999763f, 443.2287f)),
        new(1179668, 147, new(-41.30378f, 48f, -52.91935f)),
        new(1179657, 148, new(22.962f, -8.000056f, 84.98597f)),
        new(1179651, 152, new(-194.2932f, 1.00334f, 278.0651f)),
        new(1179653, 153, new(172.4772f, 8.347722f, -47.46599f)),
        new(1179654, 153, new(-207.4933f, 20.79546f, 346.9381f)),
        new(1179652, 153, new(-203.4791f, 8.960648f, -57.11456f)),
        new(1179656, 154, new(1.233066f, -46.5248f, 238.5829f)),
        new(1179655, 154, new(320.5818f, -6.272354f, -72.78946f)),
        new(1179680, 155, new(195.8394f, 302.3493f, -167.8459f)),
        new(1179679, 155, new(231.2523f, 222.1874f, 316.5895f)),
        new(1179681, 155, new(-473.8978f, 211f, -221.7772f)),
        new(1179683, 156, new(427.5927f, -5.293147f, -462.4678f)),
        new(1179682, 156, new(59.3728f, 20.69333f, -659.968f)),
        new(1179685, 397, new(483.1342f, 217.9514f, 751.0815f)),
        new(1179686, 397, new(-266.2535f, 127.1339f, 16.17947f)),
        new(1179687, 398, new(549.8383f, -51.27571f, 68.96717f)),
        new(1179688, 398, new(-209.3486f, -35.4085f, 162.9337f)),
        new(1179689, 399, new(-50.2f, 100.7f, -203f)),
        new(1179690, 400, new(265.156f, -42.55743f, 565.6061f)),
        new(1179691, 400, new(-50.4167f, -8.866f, 146.5618f)),
        new(1179692, 400, new(-521.2197f, 50f, 362.0813f)),
        new(1179694, 401, new(-630.5486f, -119.6461f, 484.3669f)),
        new(1179693, 401, new(-621.5f, -58.5f, -319.4f)),
        new(1179684, 418, new(-163.428f, 2.171433f, -5.487687f)),
        new(1179696, 612, new(-603.357f, 130.1747f, -470.634f)),
        new(1179697, 612, new(461.9973f, 114.211f, 230.1517f)),
        new(1179703, 613, new(44.16141f, 0.7360184f, -563.2119f)),
        new(1179704, 613, new(318.4404f, -119.3103f, -207.9042f)),
        new(1179706, 614, new(276.1819f, 8.117543f, -404.079f)),
        new(1179705, 614, new(468.5731f, 68.22892f, -76.64232f)),
        new(1179698, 620, new(49.2458f, 118.3919f, -728.944f)),
        new(1179699, 620, new(-258.75f, 257.7096f, 721.2443f)),
        new(1179700, 621, new(-510.0633f, 8.682312f, 20.98108f)),
        new(1179701, 621, new(635.8892f, 80f, 668.8181f)),
        new(1179707, 622, new(569.0254f, -19.23943f, 269.9413f)),
        new(1179708, 622, new(501.4787f, 39.57227f, -464.447f)),
        new(1179709, 622, new(86.35071f, 116.043f, -37.39996f)),
        new(1179702, 628, new(-108.0097f, -7f, -65.83353f)),
        new(1179695, 635, new(42.6823f, -1.192093e-07f, 24.52322f)),
        new(1179723, 813, new(663.6119f, 45.41374f, -60.42229f)),
        new(1179722, 813, new(-589.0135f, 67.15491f, -173.8156f)),
        new(1179712, 814, new(692.3943f, 28.11711f, 298.0428f)),
        new(1179710, 814, new(-412.2312f, 417.1398f, -599.4796f)),
        new(1179711, 814, new(-236.9573f, 21.46942f, 346.8223f)),
        new(1179720, 815, new(281.8935f, 1.468582f, -265.3657f)),
        new(1179724, 815, new(386.1906f, -26.84075f, 275.5016f)),
        new(1179719, 815, new(-492.6681f, 45.12946f, -284.9623f)),
        new(1179716, 816, new(-432.0865f, 64.2068f, 549.9009f)),
        new(1179714, 816, new(50.3217f, 101.7473f, -850.6052f)),
        new(1179715, 816, new(351.7007f, 84.1652f, -647.0517f)),
        new(1179717, 817, new(506.543f, -6.594435f, -267.6097f)),
        new(1179718, 817, new(-105.9113f, -18.24823f, 271.1359f)),
        new(1179721, 819, new(57.9945f, 36.24769f, -177.0935f)),
        new(1179713, 820, new(-106.7369f, -9.999162f, -54.8562f)),
        new(1179732, 956, new(394.0336f, 166.2036f, -499.9312f)),
        new(1179733, 956, new(-29.46515f, -31.53013f, 17.10529f)),
        new(1179734, 956, new(-696.531f, -31.53043f, 272.3326f)),
        new(1179726, 957, new(132.4606f, 5.387928f, 605.0671f)),
        new(1179727, 957, new(-469.8183f, 5.53548f, 32.07308f)),
        new(1179728, 957, new(432.5914f, 3.148673f, -209.653f)),
        new(1179729, 958, new(-333.5149f, 22.37715f, 474.225f)),
        new(1179730, 958, new(509.4448f, 10.87966f, -413.2814f)),
        new(1179731, 962, new(-43.02722f, 18f, -322.7847f)),
        new(1179725, 963, new(79.23245f, -31.97063f, 221.0616f)),
        new(1179735, 1185, new(-282.6906f, -0.01531982f, 69.05442f)),
        new(1179736, 1187, new(295.3087f, -170.8743f, -466.0935f)),
        new(1179737, 1187, new(486.188f, 114.935f, 654.1801f)),
        new(1179738, 1188, new(-201.6319f, 2.47811f, -404.8669f)),
        new(1179739, 1188, new(491.2382f, 112.9984f, 192.9706f)),
        new(1179740, 1188, new(-450.8124f, 121.6325f, 323.1461f)),
    };

    /// <summary>
    /// Ermittelt den lokalen Gebietsnamen (z.B. "Hyrstmill") am nächsten zur übergebenen Weltposition
    /// in einer Zone - für die Anzeige "Chocobokeep (Gebiet)" in GetChocobokeepEntries, da
    /// ChocobokeepLocations selbst keinen Namen kennt (nur Position). Vergleicht gegen jeden
    /// Aetheryten/Aethernetz-Kristall der Zone (deren Weltposition ohnehin schon über
    /// ResolveAetheryteWorldPosition auflösbar ist), nicht nur die kleinen Kristalle - ein
    /// Chocobokeep kann auch näher an einem großen Aetheryten stehen als an einem Aethernetz-Punkt.
    /// </summary>
    private static string? ResolveNearestAetherytePlaceName(uint territoryId, Vector3 position)
    {
        var aetheryteSheet = DataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return null;

        string? bestName = null;
        var bestDistance = float.MaxValue;
        var positionXZ = new Vector2(position.X, position.Z);

        foreach (var row in aetheryteSheet)
        {
            if (row.Territory.RowId != territoryId)
                continue;

            var worldPos = ResolveAetheryteWorldPosition(row.RowId);
            if (worldPos == null)
                continue;

            var distance = Vector2.Distance(new Vector2(worldPos.Value.X, worldPos.Value.Z), positionXZ);
            if (distance >= bestDistance)
                continue;

            var placeName = row.PlaceName.ValueNullable?.Name.ToString();
            if (string.IsNullOrEmpty(placeName))
                continue;

            bestDistance = distance;
            bestName = placeName;
        }

        return bestName;
    }

    private static List<CollectibleEntry>? chocobokeepEntriesCache;

    // Kategorie der live berechneten NPC-Gegner-Karten (siehe GetTripleTriadNpcEntries) - dieselbe
    // wie bei den alten, zonenlosen Einträgen in triadcards.json, damit Filter/Gate-Texte einheitlich greifen.
    internal const string TripleTriadNpcCategory = "Gegner (NPC)";
    private const uint ItemActionTripleTriadCardId = 3357; // Data[0] = TripleTriadCard-RowId

    private static List<CollectibleEntry>? tripleTriadNpcEntriesCache;

    /// <summary>
    /// Triple-Triad-Karten, die man durch einen Sieg gegen einen NPC-Gegner erhalten kann - live aus
    /// den Spieldaten: Lumina "TripleTriad" (je Gegner die möglichen Belohnungskarten + Voraussetzungs-
    /// Quest), der NPC dazu über ENpcBase.ENpcData, dessen Standort über das Sheet "Level". Je Gegner
    /// und Karte ein Eintrag am Standort des NPCs, als "Preis" einfach "NPC Fight". Voraussetzung für
    /// alle: die freigeschaltete Battle Hall (siehe ComputeGrandCompanyOrTribeGateReason).
    /// </summary>
    public static List<CollectibleEntry> GetTripleTriadNpcEntries()
    {
        if (tripleTriadNpcEntriesCache != null)
            return tripleTriadNpcEntriesCache;

        var result = new List<CollectibleEntry>();
        try
        {
            var tripleTriadSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TripleTriad>();
            var npcBaseSheet = DataManager.GetExcelSheet<ENpcBase>();
            var npcResidentSheet = DataManager.GetExcelSheet<ENpcResident>();
            var levelSheet = DataManager.GetExcelSheet<Level>();
            var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            var cardSheet = DataManager.GetExcelSheet<TripleTriadCard>();
            if (tripleTriadSheet == null || npcBaseSheet == null || npcResidentSheet == null || levelSheet == null || mapSheet == null || cardSheet == null)
                return tripleTriadNpcEntriesCache = result;

            // Gegner-Zeile -> NPC (ENpcBase.ENpcData enthält die TripleTriad-RowId direkt).
            var opponentIds = tripleTriadSheet.Select(t => t.RowId).ToHashSet();
            var npcByOpponent = new Dictionary<uint, uint>();
            foreach (var npc in npcBaseSheet)
            {
                foreach (var data in npc.ENpcData)
                {
                    if (data.RowId != 0 && opponentIds.Contains(data.RowId))
                        npcByOpponent.TryAdd(data.RowId, npc.RowId);
                }
            }

            // NPC -> Standort (Level.Type 8 = Objekt ist ein ENpc).
            var levelByNpc = new Dictionary<uint, Level>();
            foreach (var level in levelSheet)
            {
                if (level.Type == 8 && level.Object.RowId != 0)
                    levelByNpc.TryAdd(level.Object.RowId, level);
            }

            foreach (var opponent in tripleTriadSheet)
            {
                if (!npcByOpponent.TryGetValue(opponent.RowId, out var npcId)
                    || !levelByNpc.TryGetValue(npcId, out var level)
                    || !npcResidentSheet.TryGetRow(npcId, out var resident)
                    || !mapSheet.TryGetRow(level.Map.RowId, out var map))
                    continue;

                var npcName = resident.Singular.ToString();
                if (string.IsNullOrEmpty(npcName))
                    continue;

                // Lumina führt manche Namen klein ("Triple Triad master") - für die Anzeige groß beginnen.
                var displayName = char.ToUpperInvariant(npcName[0]) + npcName[1..];

                var mapCoords = Dalamud.Utility.MapUtil.WorldToMap(new Vector2(level.X, level.Z), (int)map.OffsetX, (int)map.OffsetY, (uint)map.SizeFactor);
                // Voraussetzungs-Quests des Gegners - PreviousQuestJoin 2 = EINE davon genügt (z.B. die
                // drei Stadt-Varianten derselben Hauptquest), sonst müssen alle erledigt sein.
                var requiredQuests = opponent.PreviousQuest
                    .Where(q => q.RowId != 0)
                    .Select(q => q.ValueNullable?.Name.ToString().Trim())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList();
                var requiredQuestsAny = opponent.PreviousQuestJoin == 2;

                foreach (var reward in opponent.ItemPossibleReward)
                {
                    if (reward.RowId == 0 || reward.ValueNullable is not { } rewardItem || rewardItem.ItemAction.ValueNullable is not { } action
                        || action.Action.RowId != ItemActionTripleTriadCardId)
                        continue;

                    var cardId = (uint)action.Data[0];
                    if (!cardSheet.TryGetRow(cardId, out var card))
                        continue;

                    result.Add(new CollectibleEntry
                    {
                        Id = cardId,
                        Name = card.Name.ToString(),
                        Type = CollectibleType.TripleTriadCard,
                        Category = TripleTriadNpcCategory,
                        TerritoryTypeId = level.Territory.RowId,
                        MapId = map.RowId,
                        Vendor = displayName,
                        VendorMapX = mapCoords.X,
                        VendorMapY = mapCoords.Y,
                        // Statt eines Preises der Gegner-Name (ausdrücklicher Nutzerwunsch) - im Currency-Filter
                        // fasst CompactOverlayWindow.GetAllCurrencies alle NPC-Kämpfe zu EINEM Eintrag zusammen.
                        Currency = displayName,
                        Source = Loc.T($"Sieg gegen {displayName}", $"Win against {displayName}"),
                        RequiredQuests = requiredQuests.Count > 0 ? requiredQuests : null,
                        RequiredQuestsAny = requiredQuestsAny,
                        // Für TripleTriadAutomation: NPC in der Welt finden bzw. genau dorthin navigieren.
                        EventNpcId = npcId,
                        WorldPosition = new Vector3(level.X, level.Y, level.Z),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Aufbau der Triple-Triad-NPC-Karten.");
        }

        Log.Info($"[TripleTriad] {result.Count} NPC-Gegner-Karten ({result.Select(e => e.Vendor).Distinct().Count()} Gegner).");
        return tripleTriadNpcEntriesCache = result;
    }

    // Lumina "Achievement".Type für die zonengebundenen Errungenschaften (per Auswertung der
    // Spieldaten verifiziert): 8 = "Mapping the Realm" (Key = Map-RowId), 20 = "Freebird" (Key =
    // AetherCurrentCompFlgSet-RowId). "Free Market Friend" (geteilter FATE-Rang) hat keinen
    // Zonen-Key und wird über den Zonennamen zugeordnet.
    private const byte AchievementTypeMapExploration = 8;
    private const byte AchievementTypeAetherCurrents = 20;
    private const string FreeMarketFriendPrefix = "Free Market Friend: ";

    // 14 = eine bestimmte Instanz abschließen (Key = InstanceContent-RowId), z.B. "You Call That a
    // Labyrinth" / "Life Is a Syrcus". Die Zähler-Typen 1/9 werden nur über den Instanz-Namen in der
    // Beschreibung zugeordnet - bewusst NICHT Typ 6 (Quest abschließen: gleichnamige Instanzen wie
    // "Dragon Sound" führten zu falschen Treffern) und nicht Typ 0 (Legacy, nicht mehr erhältlich).
    private const byte AchievementTypeDutyCompletion = 14;
    private static readonly HashSet<byte> AchievementTypesMatchedByDutyName = new() { 1, 9 };

    private static List<CollectibleEntry>? achievementEntriesCache;

    /// <summary>
    /// Errungenschaften, die fest EINER Zone zugeordnet werden können (zonenunabhängige Gesamtliste,
    /// wie GetChocobokeepEntries - das Overlay filtert nach TerritoryTypeId):
    /// - "Mapping the Realm: X" - Karte der Zone/Instanz vollständig aufdecken. Auch Dungeons, Raids,
    ///   Eureka, Bozja usw. - die erscheinen, solange man sich IN der jeweiligen Instanz befindet.
    /// - "Freebird: X" - alle Ätherströmungen der Zone.
    /// - "Free Market Friend: X" - geteilter FATE-Rang der Zone (Shadowbringers bis Dawntrail).
    /// Erledigt-Status über das Spiel selbst (siehe IsAchievementCompleteById).
    /// </summary>
    public static List<CollectibleEntry> GetAchievementEntries()
    {
        if (achievementEntriesCache != null)
            return achievementEntriesCache;

        var result = new List<CollectibleEntry>();
        try
        {
            var achievementSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
            var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            var aetherCurrentSetSheet = DataManager.GetExcelSheet<AetherCurrentCompFlgSet>();
            var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
            if (achievementSheet == null || mapSheet == null || aetherCurrentSetSheet == null || territorySheet == null)
                return achievementEntriesCache = result;

            // Zonenname -> Zone, nur Feldzonen (TerritoryIntendedUse 1) - für "Free Market Friend".
            // Führendes "The " ignorieren ("The Tempest" vs. "Tempest" o.ä.).
            static string NormalizeZoneName(string name) =>
                name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
            var fieldZoneByName = new Dictionary<string, TerritoryType>(StringComparer.OrdinalIgnoreCase);
            foreach (var territory in territorySheet)
            {
                var placeName = territory.PlaceName.ValueNullable?.Name.ToString();
                if (territory.TerritoryIntendedUse.RowId == 1 && territory.Map.RowId != 0 && !string.IsNullOrEmpty(placeName))
                    fieldZoneByName.TryAdd(NormalizeZoneName(placeName), territory);
            }

            // Instanzen (Dungeons, Raids, Trials, Schatzkarten-Dungeons, Eureka, ...) aus dem Duty Finder -
            // für Typ 14 über InstanceContent (ContentLinkType 1), für die Zähler über den Namen.
            var dutySheet = DataManager.GetExcelSheet<ContentFinderCondition>();
            var dutyByInstanceContent = new Dictionary<uint, ContentFinderCondition>();
            var dutiesByName = new List<(string Name, ContentFinderCondition Duty)>();
            var seenDutyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (dutySheet != null)
            {
                foreach (var duty in dutySheet)
                {
                    if (duty.TerritoryType.RowId == 0)
                        continue;

                    if (duty.ContentLinkType == 1 && duty.Content.RowId != 0)
                        dutyByInstanceContent.TryAdd(duty.Content.RowId, duty);

                    var dutyName = duty.Name.ToString();
                    if (dutyName.Length > 4 && seenDutyNames.Add(dutyName))
                        dutiesByName.Add((dutyName, duty));
                }
            }

            // Längste Namen zuerst - "the Hidden Canals of Uznair" soll nicht zusätzlich als kürzerer
            // Teilname zählen.
            dutiesByName.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length));

            // Name als ganzes Wort/Wortgruppe im Text? Bewusst einfache Textsuche statt Regex - pro
            // Errungenschaft wird gegen ~900 Instanznamen geprüft, eine je Name neu gebaute Regex
            // machte das Plugin-Laden spürbar langsam.
            static bool ContainsAsWholeWords(string text, string name)
            {
                var index = 0;
                while ((index = text.IndexOf(name, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var end = index + name.Length;
                    var boundaryBefore = index == 0 || !char.IsAsciiLetter(text[index - 1]);
                    var boundaryAfter = end >= text.Length || !char.IsAsciiLetter(text[end]);
                    if (boundaryBefore && boundaryAfter)
                        return true;
                    index++;
                }

                return false;
            }

            // Genau EINE Instanz im Text (als ganzes Wort/Wortgruppe) - sonst null (mehrdeutig/keine).
            ContentFinderCondition? FindSingleDutyInText(string text)
            {
                var found = dutiesByName
                    .Where(d => ContainsAsWholeWords(text, d.Name))
                    .ToList();
                found = found.Where(f => !found.Any(o => o.Name.Length > f.Name.Length && o.Name.Contains(f.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                return found.Count == 1 ? found[0].Duty : null;
            }

            foreach (var achievement in achievementSheet)
            {
                var name = achievement.Name.ToString();
                if (string.IsNullOrEmpty(name))
                    continue;

                uint territoryId = 0;
                uint mapId = 0;
                if (achievement.Type == AchievementTypeMapExploration && mapSheet.TryGetRow(achievement.Key.RowId, out var map))
                {
                    territoryId = map.TerritoryType.RowId;
                    mapId = map.RowId;
                }
                else if (achievement.Type == AchievementTypeAetherCurrents && aetherCurrentSetSheet.TryGetRow(achievement.Key.RowId, out var currentSet))
                {
                    territoryId = currentSet.Territory.RowId;
                    mapId = currentSet.Territory.ValueNullable?.Map.RowId ?? 0;
                }
                else if (name.StartsWith(FreeMarketFriendPrefix, StringComparison.Ordinal)
                         && fieldZoneByName.TryGetValue(NormalizeZoneName(name[FreeMarketFriendPrefix.Length..]), out var zone))
                {
                    territoryId = zone.RowId;
                    mapId = zone.Map.RowId;
                }
                else if (achievement.Type == AchievementTypeDutyCompletion && dutyByInstanceContent.TryGetValue(achievement.Key.RowId, out var duty))
                {
                    // "Complete the Labyrinth of the Ancients" usw. - Key = InstanceContent-RowId, exakt.
                    territoryId = duty.TerritoryType.RowId;
                    mapId = duty.TerritoryType.ValueNullable?.Map.RowId ?? 0;
                }
                else if (AchievementTypesMatchedByDutyName.Contains(achievement.Type) && FindSingleDutyInText(achievement.Description.ToString()) is { } namedDuty)
                {
                    // Zähler, die nur in genau einer Instanz vorankommen ("Raid the Lost Canals of Uznair
                    // 5 times", Diadem, Eureka, Blaumagier-Solo-Läufe, ...) - über den Instanz-Namen in
                    // der Beschreibung zugeordnet.
                    territoryId = namedDuty.TerritoryType.RowId;
                    mapId = namedDuty.TerritoryType.ValueNullable?.Map.RowId ?? 0;
                }

                if (territoryId == 0)
                    continue;

                result.Add(new CollectibleEntry
                {
                    Id = achievement.RowId,
                    Name = name,
                    Type = CollectibleType.Achievement,
                    Category = Loc.T("Errungenschaft", "Achievement"),
                    TerritoryTypeId = territoryId,
                    MapId = mapId,
                    Source = achievement.Description.ToString(),
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Aufbau der zonengebundenen Errungenschaften.");
        }

        Log.Info($"[Achievements] {result.Count} zonengebundene Errungenschaften.");
        return achievementEntriesCache = result;
    }

    // Siehe EnsureAchievementsLoaded - Drossel für die Server-Anfrage.
    private static DateTime lastAchievementRequestAt = DateTime.MinValue;
    private static readonly TimeSpan AchievementRequestInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Der Erledigt-Status aller Errungenschaften liegt im Client erst vor, nachdem er beim Server
    /// angefragt wurde (normalerweise beim ersten Öffnen des Errungenschaften-Fensters) - das
    /// übernimmt hier das Plugin selbst (gedrosselt), damit man das Fenster nicht erst öffnen muss.
    /// </summary>
    public static unsafe bool EnsureAchievementsLoaded()
    {
        var achievements = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.Instance();
        if (achievements == null)
            return false;

        if (achievements->IsLoaded())
            return true;

        if (achievements->State == FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.AchievementState.Invalid
            && DateTime.UtcNow - lastAchievementRequestAt > AchievementRequestInterval)
        {
            lastAchievementRequestAt = DateTime.UtcNow;
            achievements->RequestCompletedAchievements();
        }

        return false;
    }

    private static unsafe bool IsAchievementCompleteById(uint achievementId) =>
        EnsureAchievementsLoaded() && FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.Instance()->IsComplete((int)achievementId);

    /// <summary>
    /// Alle Chocobo-Reitstände im gesamten Spiel (zonenunabhängige Gesamtliste, wie GetFrameKitEntries),
    /// aus ChocobokeepLocations - Entry.Id ist bewusst die "ChocoboTaxiStand"-RowId (nicht irgendeine
    /// NPC-/Level-RowId), damit IsOwned/ChocobokeepAutomation direkt IsChocoboTaxiStandUnlocked
    /// aufrufen können, exakt wie bei Aetheryten (siehe IsAetheryteUnlocked).
    /// </summary>
    public static List<CollectibleEntry> GetChocobokeepEntries()
    {
        if (chocobokeepEntriesCache != null)
            return chocobokeepEntriesCache;

        var result = new List<CollectibleEntry>();
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        if (territorySheet == null || mapSheet == null)
            return result;

        foreach (var loc in ChocobokeepLocations)
        {
            try
            {
                if (!territorySheet.TryGetRow(loc.TerritoryId, out var territory))
                    continue;

                var mapId = territory.Map.RowId;
                if (mapId == 0 || !mapSheet.TryGetRow(mapId, out var map))
                    continue;

                var mapCoords = Dalamud.Utility.MapUtil.WorldToMap(
                    new Vector2(loc.Position.X, loc.Position.Z), (int)map.OffsetX, (int)map.OffsetY, (uint)map.SizeFactor);

                var areaName = ResolveNearestAetherytePlaceName(loc.TerritoryId, loc.Position);
                var name = string.IsNullOrEmpty(areaName) ? "Chocobokeep" : $"Chocobokeep ({areaName})";

                result.Add(new CollectibleEntry
                {
                    Id = loc.ChocoboTaxiStandId,
                    Name = name,
                    Type = CollectibleType.Chocobokeep,
                    Category = Loc.T("Chocobokeep", "Chocobokeep"),
                    TerritoryTypeId = loc.TerritoryId,
                    MapId = mapId,
                    VendorMapX = mapCoords.X,
                    VendorMapY = mapCoords.Y,
                    // Für ChocobokeepAutomation (läuft direkt zur rohen Weltposition, statt wie
                    // OpenEntryMap den Umweg über die Kartenkoordinate zu gehen).
                    WorldPosition = loc.Position,
                    Source = Loc.T("Chocobokeep", "Chocobokeep"),
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Fehler bei Chocobokeep-Standort (TaxiStandId={loc.ChocoboTaxiStandId}) - übersprungen.");
            }
        }

        chocobokeepEntriesCache = result;
        return result;
    }

    /// <summary>
    /// Einmaliger Debug-Dump zur Kalibrierung von GetChocobokeepEntries - listet jeden Standort
    /// mitsamt aufgelöster Zone/Karte und aktuellem Freischalt-Status.
    /// </summary>
    public static void DumpChocobokeepDebugInfo()
    {
        chocobokeepEntriesCache = null;
        var entries = GetChocobokeepEntries();
        Log.Info($"[ChocobokeepDebug] {entries.Count} Chocobokeep-Standorte gefunden:");
        foreach (var entry in entries)
        {
            Log.Info($"[ChocobokeepDebug]   TaxiStandId={entry.Id}: Zone={entry.TerritoryTypeId}, MapId={entry.MapId}, " +
                     $"Pos=({entry.VendorMapX:F1}, {entry.VendorMapY:F1}), unlocked={IsChocoboTaxiStandUnlocked(entry.Id)}");
        }
    }

    /// <summary>
    /// Prüft, ob ein saisonales Event (Winterstern, Valentionstag, ...) aktuell läuft - für den
    /// Ausschluss von Event-Quests, die laut Datenbank zwar existieren, aber gerade nicht
    /// annehmbar sind, weil das zugehörige Event nicht aktiv ist.
    /// </summary>
    private static unsafe bool IsFestivalActive(ushort festivalId)
    {
        var gameMain = GameMain.Instance();
        if (gameMain == null)
            return false;

        foreach (var festival in gameMain->ActiveFestivals)
        {
            if (festival.Id == festivalId)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Namen aller aktuell laufenden saisonalen Events (Lumina "Festival"-Sheet), gefiltert auf
    /// nicht-leere Namen - für IsSeasonalEventEntryCurrentlyActive, da statische JSON-Einträge
    /// (Mounts/Minions/... mit Category "Saisonevent") anders als Quests keine Festival-RowId
    /// speichern, sondern nur einen Klartext-Namen in Name/Source.
    /// </summary>
    private static unsafe List<string> GetActiveFestivalNames()
    {
        var result = new List<string>();
        var gameMain = GameMain.Instance();
        var festivalSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Festival>();
        if (gameMain == null || festivalSheet == null)
            return result;

        foreach (var festival in gameMain->ActiveFestivals)
        {
            if (festival.Id == 0 || !festivalSheet.TryGetRow(festival.Id, out var row))
                continue;

            var name = row.Name.ToString();
            if (!string.IsNullOrEmpty(name))
                result.Add(name);
        }

        return result;
    }

    /// <summary>
    /// Ob ein Sammelobjekt mit Category "Saisonevent" (Mounts/Minions/... ohne eigene Festival-
    /// RowId, siehe GetActiveFestivalNames-Kommentar) gerade tatsächlich erhältlich ist - per
    /// (grobem, textbasiertem) Abgleich von Name/Source gegen die Namen aller aktuell laufenden
    /// Events. Läuft gerade GAR KEIN Event, ist so ein Eintrag sicher nicht erhältlich (kein
    /// Fehlalarm möglich); läuft eins, aber der Name matcht nicht (z.B. wegen abweichender
    /// Formulierung), wird der Eintrag trotzdem ausgeblendet - siehe Nutzerentscheidung dazu.
    /// Alle anderen Categories sind von diesem Filter unberührt (liefert dafür immer true).
    /// </summary>
    // Von Hand nachgetragene, offiziell verifizierte Zeitfenster einzelner Cross-Game-
    // Kollaborationen (siehe IsSeasonalEventEntryCurrentlyActive-Kommentar) - Schlüssel ist ein
    // Teilstring, der gegen entry.Source geprüft wird. Ergänzt bei Bedarf für jede weitere
    // Kollaboration, sobald ihr offizielles Zeitfenster bekannt ist (z.B. aus der SQUARE ENIX-/
    // Lodestone-Ankündigung) - noch NICHT hier eingetragene Kollaborationen fallen weiterhin auf
    // "generell erfüllt" zurück (siehe unten), nicht auf "nie erfüllt".
    private static readonly (string SourceContains, DateTime StartUtc, DateTime EndUtc)[] KnownCollaborationWindows =
    {
        // "A Nocturne for Heroes" (FINAL FANTASY XV-Kollaboration, Regalia Type-G etc.) - offiziell
        // Do. 24.09.2026 01:00 bis Di. 13.10.2026 07:59 (jeweils PDT/UTC-7), Quelle: offizielle
        // SQUARE ENIX-/Lodestone-Ankündigung.
        ("Final Fantasy XV Collaboration",
            new DateTime(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 13, 14, 59, 0, DateTimeKind.Utc)),
    };

    // Offizielle Zeitfenster von Events, die (wie Kollaborationen) nicht über GameMain.ActiveFestivals
    // laufen - Schlüssel ist CollectibleEntry.EventName. Bei jedem neuen Durchlauf ergänzen (Quelle:
    // offizielle Lodestone-Ankündigung). Ohne passendes Fenster gilt das Event als NICHT laufend.
    private const string MoogleTreasureTroveEventName = "Moogle Treasure Trove";

    private static readonly (string EventName, DateTime StartUtc, DateTime EndUtc)[] KnownEventWindows =
    {
        // "Moogle Treasure Trove: The First Hunt for Astronomy" - Mi. 09.09.2026 01:00 bis Mo.
        // 19.10.2026 07:59 (PDT/UTC-7), Quelle: Lodestone (mogmog-collection/202609).
        (MoogleTreasureTroveEventName,
            new DateTime(2026, 9, 9, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 19, 14, 59, 0, DateTimeKind.Utc)),
    };

    public static bool IsSeasonalEventEntryCurrentlyActive(CollectibleEntry entry)
    {
        if (entry.Category != "Saisonevent")
            return true;

        // Events ohne auslesbaren Festival-Status (siehe CollectibleEntry.EventName) - über das von
        // Hand hinterlegte, offizielle Zeitfenster (KnownEventWindows).
        if (!string.IsNullOrEmpty(entry.EventName))
        {
            var nowUtc = DateTime.UtcNow;
            return KnownEventWindows.Any(w => string.Equals(w.EventName, entry.EventName, StringComparison.OrdinalIgnoreCase)
                                              && nowUtc >= w.StartUtc && nowUtc <= w.EndUtc);
        }

        // Cross-Game-Kollaborationen (Yo-kai Watch, Final Fantasy XV/XI/XVI, Dragon Quest X,
        // Fall Guys/MGF, ...) laufen NICHT über das normale Festival-System
        // (GameMain->ActiveFestivals) - das deckt nur echte wiederkehrende saisonale Events
        // (Starlight, Hatching-tide, ...) ab. GetActiveFestivalNames() liefert für Kollaborationen
        // deshalb IMMER eine leere Liste, auch während die Kollaboration tatsächlich live ist
        // (per Debug-Dump bestätigt: beim laufenden Yo-kai-Watch-Event war die Liste leer, der
        // Eintrag wurde fälschlich dauerhaft als "Bedingung nicht erfüllt" markiert).
        if (entry.Source.Contains("Collaboration", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (sourceContains, startUtc, endUtc) in KnownCollaborationWindows)
            {
                if (entry.Source.Contains(sourceContains, StringComparison.OrdinalIgnoreCase))
                {
                    var nowUtc = DateTime.UtcNow;
                    return nowUtc >= startUtc && nowUtc <= endUtc;
                }
            }

            // Kein bekanntes Zeitfenster hinterlegt (siehe KnownCollaborationWindows) - es gibt
            // dafür keine bekannte, live auslesbare Quelle, daher generell als erfüllt werten
            // (nicht gated), statt permanent falsch negativ zu sein.
            return true;
        }

        var activeNames = GetActiveFestivalNames();
        if (activeNames.Count == 0)
            return false;

        foreach (var name in activeNames)
        {
            if (entry.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || entry.Source.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Einmaliger Debug-Dump zur Kalibrierung von IsSeasonalEventEntryCurrentlyActive - zeigt die
    /// Namen aller aktuell laufenden Events (leer, wenn keins läuft) sowie für jeden "Saisonevent"-
    /// Eintrag, ob er gerade als aktiv erkannt wird. Praktisch v.a. WÄHREND eines laufenden Events,
    /// um Namensabweichungen zwischen Festival.Name und Item-Source/Name zu finden.
    /// </summary>
    public static void DumpSeasonalEventDebugInfo()
    {
        var activeNames = GetActiveFestivalNames();
        Log.Info($"[SeasonalEventDebug] Aktuell laufende Events: [{string.Join(", ", activeNames)}]");

        var entries = CollectionData.GetAllEntries().Where(e => e.Category == "Saisonevent").ToList();
        var activeCount = entries.Count(IsSeasonalEventEntryCurrentlyActive);
        Log.Info($"[SeasonalEventDebug] {entries.Count} Saisonevent-Einträge insgesamt, {activeCount} davon aktuell als aktiv erkannt.");

        foreach (var entry in entries)
        {
            var active = IsSeasonalEventEntryCurrentlyActive(entry);
            if (active)
                Log.Info($"[SeasonalEventDebug]   AKTIV: {entry.Type} \"{entry.Name}\" (Source=\"{entry.Source}\")");
        }
    }

    private uint? liveEntriesZoneId;
    private List<CollectibleEntry> liveEntriesCache = new();

    // Immer auf Englisch geladen (unabhängig von der Spielclient-Sprache) - wird nur für den
    // sprachunabhängigen "startet mit All"-Check bei der Quest-Klassenfilterung gebraucht,
    // siehe GetEnglishClassJobCategoryName.
    private Lumina.Excel.ExcelSheet<ClassJobCategory>? classJobCategorySheetEnglish;

    private string GetEnglishClassJobCategoryName(uint categoryId)
    {
        classJobCategorySheetEnglish ??= DataManager.GetExcelSheet<ClassJobCategory>(Dalamud.Game.ClientLanguage.English);
        return classJobCategorySheetEnglish?.GetRowOrDefault(categoryId)?.Name.ToString() ?? string.Empty;
    }

    /// <summary>
    /// TerritoryTypeId -> zusätzlich akzeptierte generische Stadt-PlaceName-IDs für Quests
    /// (siehe Kommentar in ComputeLiveZoneEntries). Deckt die großen "geteilten" Städte ab.
    /// </summary>
    private static readonly Dictionary<uint, uint[]> GenericCityPlaceNames = new()
    {
        [130] = new[] { 51u, 504u },   // Ul'dah - Steps of Nald
        [131] = new[] { 51u, 504u },   // Ul'dah - Steps of Thal
        [178] = new[] { 51u, 504u },   // Flame Barracks (Große Kompanie Ul'dah, "Hinterraum")
        [128] = new[] { 27u, 500u },   // Limsa Lominsa Upper Decks
        [129] = new[] { 27u, 500u },   // Limsa Lominsa Lower Decks
        [177] = new[] { 27u, 500u },   // Maelstrom Barracks (Große Kompanie Limsa, "Hinterraum")
        [132] = new[] { 39u, 506u },   // New Gridania
        [133] = new[] { 39u, 506u },   // Old Gridania
        [534] = new[] { 39u, 506u },   // Twin Adder Barracks (Große Kompanie Gridania, "Hinterraum")
        [598] = new[] { 39u, 506u },   // Serpent Barracks (Große Kompanie Gridania, "Hinterraum")
        [418] = new[] { 62u, 512u },   // Foundation (Ishgard)
        [419] = new[] { 62u, 512u },   // The Pillars (Ishgard)
    };

    /// <summary>
    /// TerritoryTypeId -> "gehört eigentlich zu"-TerritoryTypeId, für Zonen, die zu einer Stadt
    /// gehören, aber ein eigenes TerritoryType haben (z.B. "Heart of the Sworn", die Innenräume
    /// der Ul'dah-Stadtwache, TerritoryType 210). Dort sollen dieselben Sammelobjekte/Quests/
    /// Aetheryten wie in der zugeordneten Stadtzone angezeigt werden. Wird ganz am Anfang der
    /// Zonen-Auswertung aufgelöst (siehe ResolveEffectiveTerritoryId) - alles Weitere (inkl. der
    /// "geteilte Stadt"-Logik oben) greift danach automatisch, ohne diese Zone extra zu kennen.
    /// </summary>
    private static readonly Dictionary<uint, uint> TerritoryAliases = new()
    {
        [210] = 131, // Heart of the Sworn -> Ul'dah, Steps of Thal
    };

    /// <summary>
    /// Löst eine Zone auf die Zone auf, deren Daten dafür tatsächlich verwendet werden sollen
    /// (siehe TerritoryAliases) - für alle anderen Zonen unverändert dieselbe ID.
    /// </summary>
    public static uint ResolveEffectiveTerritoryId(uint territoryId) =>
        TerritoryAliases.GetValueOrDefault(territoryId, territoryId);

    /// <summary>
    /// TerritoryTypeId -> alle TerritoryTypeIds derselben "geteilten" Hauptstadt (inkl. sich selbst).
    /// Anders als bei Quests (GenericCityPlaceNames) sollen Aetheryten/Aethernetz-Kristalle aus JEDEM
    /// Stadtbezirk angezeigt werden, egal in welchem Bezirk man gerade steht - man kann schließlich
    /// überall im Aethernetz-Menü der Stadt sehen/anwählen, was schon freigeschaltet ist.
    /// </summary>
    private static readonly Dictionary<uint, uint[]> SplitCityTerritories = new()
    {
        [130] = new[] { 130u, 131u, 599u, 178u },   // Ul'dah (Steps of Nald / Steps of Thal / Flame Barracks / The Hourglass)
        [131] = new[] { 130u, 131u, 599u, 178u },
        [599] = new[] { 130u, 131u, 599u, 178u },
        [178] = new[] { 130u, 131u, 599u, 178u },
        [128] = new[] { 128u, 129u, 597u, 177u },   // Limsa Lominsa (Upper / Lower Decks / Maelstrom Barracks / Mizzenmast Inn)
        [129] = new[] { 128u, 129u, 597u, 177u },
        [597] = new[] { 128u, 129u, 597u, 177u },
        [177] = new[] { 128u, 129u, 597u, 177u },
        [132] = new[] { 132u, 133u, 534u, 598u, 179u },   // Gridania (New / Old / Twin Adder Barracks / Serpent Barracks / The Roost)
        [133] = new[] { 132u, 133u, 534u, 598u, 179u },
        [534] = new[] { 132u, 133u, 534u, 598u, 179u },
        [598] = new[] { 132u, 133u, 534u, 598u, 179u },
        [179] = new[] { 132u, 133u, 534u, 598u, 179u },
        [418] = new[] { 418u, 419u, 886u, 433u, 429u },   // Ishgard (Foundation / The Pillars / Firmament / Fortemps Manor / Cloud Nine)
        [419] = new[] { 418u, 419u, 886u, 433u, 429u },
        [886] = new[] { 418u, 419u, 886u, 433u, 429u },
        [433] = new[] { 418u, 419u, 886u, 433u, 429u },
        [429] = new[] { 418u, 419u, 886u, 433u, 429u },
        [144] = new[] { 144u, 388u },   // The Gold Saucer / Chocobo Square
        [388] = new[] { 144u, 388u },
        [628] = new[] { 628u, 629u },   // Kugane / Bokairo Inn
        [629] = new[] { 628u, 629u },
        [819] = new[] { 819u, 843u, 844u },   // The Crystarium / The Pendants Personal Suite / The Ocular
        [843] = new[] { 819u, 843u, 844u },
        [844] = new[] { 819u, 843u, 844u },
        [962] = new[] { 962u, 990u, 1337u },   // Old Sharlayan / Andron / The Maiden's Home
        [990] = new[] { 962u, 990u, 1337u },
        [1337] = new[] { 962u, 990u, 1337u },
        [1185] = new[] { 1185u, 1205u },   // Tuliyollal / The For'ard Cabins
        [1205] = new[] { 1185u, 1205u },
        [1186] = new[] { 1186u, 1207u, 1223u, 1224u },   // Solution Nine / The Backroom / Tritalis Training / Greenroom
        [1207] = new[] { 1186u, 1207u, 1223u, 1224u },
        [1223] = new[] { 1186u, 1207u, 1223u, 1224u },
        [1224] = new[] { 1186u, 1207u, 1223u, 1224u },
        [156] = new[] { 156u, 351u },   // Mor Dhona / The Rising Stones
        [351] = new[] { 156u, 351u },
    };

    /// <summary>
    /// Manueller Kartenlink-Override für einen einzelnen Aethernetz-Kristall. MapId/TerritoryId
    /// sind optional - null bedeutet "wie die Zone, in der der Kristall angezeigt wird" (Normalfall,
    /// die Flagge landet also auf der Karte der eigenen Zone). Manche Kristalle werden vom Spiel
    /// aber auf der Nachbarkarte markiert (z.B. Miners' Guild trotz Anzeige in "Thal" auf der
    /// Nald-Karte) - dafür beide explizit setzen.
    /// </summary>
    private readonly record struct ManualAetherytePosition(float X, float Y, uint? TerritoryId = null, uint? MapId = null);

    /// <summary>
    /// Manuell nachgepflegte Kartenkoordinaten für einzelne Aethernetz-Kristalle (Aetheryte-RowId
    /// -> Koordinate), für die der automatische Karten-Pin (ResolveZoneAetherytePinPosition, ein
    /// Pin pro Zone) zu ungenau ist. Von Hand recherchiert/nachgetragen.
    /// </summary>
    private static readonly Dictionary<uint, ManualAetherytePosition> ManualAetherytePositions = new()
    {
        [50] = new(10.8f, 12.7f),                          // Ul'dah - Steps of Thal: Goldsmiths' Guild
        [36] = new(11.9f, 13.4f), // Ul'dah - Steps of Thal: Miners' Guild
        [37] = new(9.2f, 13.0f, MapId: 73),                 // Alchemists' Guild - Flagge auf Unterkarte "Hustings Strip"
        [51] = new(11.3f, 10.6f, MapId: 73),                // The Chamber of Rule - Flagge auf Unterkarte "Hustings Strip"
        [34] = new(8.1f, 12.6f),                            // Ul'dah - Steps of Nald: Thaumaturges' Guild
    };

    /// <summary>
    /// Berechnet Aetheryten und aktuell annehmbare (nicht wiederholbare, nicht abgeschlossene)
    /// Quests der übergebenen Zone live aus den Lumina-Spieldaten - im Gegensatz zu den übrigen
    /// Kategorien gibt es dafür keine statische Datendatei. Wird pro Zone zwischengespeichert,
    /// da ein voller Durchlauf durchs Quest-Sheet nicht jeden Frame passieren soll.
    /// Die "annehmbar"-Prüfung bei Quests ist ein Best-Effort (Level, Vorquest, keine
    /// Klassen-Einschränkung, keine Stammes-/wiederholbaren Quests) - es gibt dafür keine
    /// fertige Prüfung im Spielclient, siehe Kommentare unten.
    /// </summary>
    public List<CollectibleEntry> GetLiveZoneEntries(uint territoryId)
    {
        if (liveEntriesZoneId == territoryId)
            return liveEntriesCache;

        // Direkt nach einem Zonenwechsel/Login ist der lokale Spieler (insbesondere Level) manchmal
        // noch nicht vollständig initialisiert. Ohne diese Prüfung würde ComputeLiveZoneEntries in
        // genau diesem einen Frame fälschlich eine leere Questliste berechnen - und die würde dann
        // dauerhaft für diese Zone gecacht bleiben, bis die Zone erneut gewechselt wird. Das erklärt
        // das "mal geht's, mal nicht" je Zone: einfach nochmal versuchen, bis Spielerdaten da sind,
        // statt ein schlechtes Ergebnis zu cachen.
        if (ObjectTable.LocalPlayer is not { Level: > 0 })
            return liveEntriesCache;

        List<CollectibleEntry> result;
        try
        {
            result = ComputeLiveZoneEntries(territoryId);
        }
        catch (Exception ex)
        {
            // Absicherung: ein Fehler hier (z.B. durch ein unerwartetes Datenfeld bei einer
            // einzelnen Quest) darf niemals die restliche Anzeige (Mounts etc.) mit runterreißen.
            Log.Error(ex, "Fehler beim Berechnen der Live-Einträge (Aetheryten/Quests)");
            result = new List<CollectibleEntry>();
        }

        liveEntriesZoneId = territoryId;
        liveEntriesCache = result;
        return result;
    }

    /// <summary>
    /// Manuell erfasste Zusatz-Voraussetzungen für Quests, die NICHT über die üblichen
    /// PreviousQuest/QuestLock-Mechanismen abgebildet sind (Recherche ergab: für Achievement-/
    /// Besitz-Voraussetzungen gibt es KEINE auslesbare Verknüpfung in den Lumina-Spieldaten, weder
    /// im Quest- noch im Achievement-Sheet) - daher von Hand gepflegt, Quest-Anzeigename (Englisch,
    /// exakt wie im Spiel) -> benötigte Mount-Namen. Wird nur ergänzt, wenn der Nutzer eine konkrete
    /// Quest + Voraussetzung nennt, siehe ComputeGrandCompanyOrTribeGateReason.
    /// </summary>
    private static readonly Dictionary<string, string[]> QuestRequiredMounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fiery Wings, Fiery Hearts"] = new[]
        {
            "Rose Lanner", "White Lanner", "Round Lanner", "Warring Lanner", "Dark Lanner", "Sophic Lanner", "Demonic Lanner",
        },
    };

    /// <summary>
    /// Umgekehrte Richtung - Mount-Name -> Name der Quest, die zuerst abgeschlossen sein muss (z.B.
    /// "Firebird" braucht "Fiery Wings, Fiery Hearts"). Ebenfalls von Hand gepflegt, siehe
    /// QuestRequiredMounts-Kommentar.
    /// </summary>
    private static readonly Dictionary<string, string> MountRequiredQuest = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Firebird"] = "Fiery Wings, Fiery Hearts",
        ["Magicked Card"] = "The Adventurer with All the Cards",
    };

    // "Simply to Dye For" (schaltet Färben frei) braucht die abgeschlossene Artefakt-Rüstungsquest
    // EINES der ursprünglichen A-Realm-Reborn-Jobs (egal welcher) - in Lumina nur indirekt über
    // "Color Your World" verknüpft, das selbst KEINE auslesbare PreviousQuest-Voraussetzung trägt
    // (dieselbe Lücke wie bei QuestRequiredMounts) - daher von Hand gepflegt. Key = ClassJob-RowId
    // (für die job-abhängige Anzeige im Gate-Text, siehe ComputeGrandCompanyOrTribeGateReason),
    // Wert = Name DIESER Job-Quest. Für die eigentliche Freischalt-Prüfung zählt jede der Werte hier
    // (OR, nicht nur die des aktuellen Jobs) - siehe DyeUnlockQuestName.
    private static readonly Dictionary<uint, string> DyeUnlockJobQuestByClassJobId = new()
    {
        [19] = "Keeping the Oath",        // Paladin
        [20] = "How to Quit You",         // Monk
        [21] = "The Beast Within",        // Warrior
        [22] = "Into the Dragon's Maw",   // Dragoon
        [23] = "Five Easy Pieces",        // Bard
        [24] = "Requiem for the Fallen",  // White Mage
        [25] = "Always Bet on Black",     // Black Mage
        [27] = "Primal Burdens",          // Summoner
        [28] = "Heart of the Forest",     // Scholar
        [30] = "Master and Student",      // Ninja
    };

    private const string DyeUnlockQuestName = "Simply to Dye For";

    /// <summary>
    /// Ob ein Mount (per Anzeigename, siehe QuestRequiredMounts) bereits freigeschaltet ist - löst
    /// den Namen einmalig über die gecachte CollectionData.GetAllEntries() in seine Id auf, dann
    /// live über PlayerState geprüft (wie IsOwned), damit die Markierung sofort verschwindet,
    /// sobald das Mount freigeschaltet wird.
    /// </summary>
    private static unsafe bool IsMountUnlockedByName(string mountName)
    {
        var mount = CollectionData.GetAllEntries()
            .FirstOrDefault(e => e.Type == CollectibleType.Mount && string.Equals(e.Name, mountName, StringComparison.OrdinalIgnoreCase));
        return mount != null && PlayerState.Instance()->IsMountUnlocked(mount.Id);
    }

    /// <summary>
    /// Sammelobjekte, die zwar grundsätzlich existieren, aber nur durch eine noch nicht erreichte
    /// Errungenschaft oder einen noch nicht freigeschalteten Stammes-/Grad-Rang (z.B. bei Beast-
    /// Tribe-Händlern) tatsächlich erreichbar sind - bei deaktiviertem "Alle Gegenstände anzeigen"
    /// (Configuration.ShowAllItems, siehe CompactOverlayWindow.DrawContent) werden genau diese
    /// ausgeblendet, bei aktiviertem Schalter stattdessen mit einem Hinweis markiert (siehe
    /// GetAchievementOrRankGateReason). Von Hand gepflegt (Typ + Anzeigename), da es dafür keine
    /// zuverlässig auslesbare Verknüpfung in den Lumina-Spieldaten gibt.
    ///
    /// Alle Beast-Tribe-Einträge (Category "Stammes-Quest" in den Datendateien) mit einem "(Rank N)"-
    /// Hinweis im Currency-Feld - genau diese Angabe wertet GetAchievementOrRankGateReason zur
    /// Laufzeit aus, hier muss also nur die Liste selbst gepflegt werden, keine Rangzahl je Eintrag.
    /// </summary>
    private static readonly HashSet<(CollectibleType Type, string Name)> AchievementOrRankGatedItems = new()
    {
        // Amalj'aa
        (CollectibleType.Minion, "Wind-up Amalj'aa"),
        (CollectibleType.Minion, "Wind-up Founder"),
        (CollectibleType.Mount, "Cavalry Drake"),
        (CollectibleType.Orchestrion, "Smoulder"),

        // Sylph
        (CollectibleType.Minion, "Wind-up Sylph"),
        (CollectibleType.Minion, "Wind-up Violet"),
        (CollectibleType.Mount, "Laurel Goobbue"),
        (CollectibleType.Orchestrion, "Flibbertigibbet"),

        // Kobold
        (CollectibleType.Minion, "Wind-up Kobold"),
        (CollectibleType.Minion, "Wind-up Kobolder"),
        (CollectibleType.Mount, "Bomb Palanquin"),

        // Sahagin
        (CollectibleType.Minion, "Wind-up Sahagin"),
        (CollectibleType.Minion, "Wind-up Sea Devil"),
        (CollectibleType.Mount, "Cavalry Elbst"),

        // Ixal
        (CollectibleType.Minion, "Wind-up Ixal"),
        (CollectibleType.Minion, "Wind-up Dezul Qualan"),
        (CollectibleType.Mount, "Direwolf"),

        // Vanu Vanu
        (CollectibleType.Mount, "Sanuwa"),
        (CollectibleType.Orchestrion, "Coming Home"),

        // Vath
        (CollectibleType.Minion, "Wind-up Vath"),
        (CollectibleType.Minion, "Wind-up Gnath"),
        (CollectibleType.Mount, "Kongamato"),
        (CollectibleType.Orchestrion, "Piece of Mind"),

        // Moogles
        (CollectibleType.Minion, "Wind-up Zundu Warrior"),
        (CollectibleType.Minion, "Wind-up Gundu Warrior"),
        (CollectibleType.Minion, "Wind-up Ohl Deeh"),
        (CollectibleType.Mount, "Cloud Mallow"),

        // Kojin
        (CollectibleType.Minion, "Wind-up Kojin"),
        (CollectibleType.Minion, "Wind-up Redback"),
        (CollectibleType.Minion, "Zephyrous Zabuton"),
        (CollectibleType.Mount, "Striped Ray"),
        (CollectibleType.Emote, "Ritual Prayer"),
        (CollectibleType.Orchestrion, "Indomitable"),

        // Ananta
        (CollectibleType.Minion, "Wind-up Ananta"),
        (CollectibleType.Minion, "Wind-up Qalyana"),
        (CollectibleType.Mount, "Marid"),
        (CollectibleType.Mount, "True Griffin"),
        (CollectibleType.Emote, "Charmed"),
        (CollectibleType.Orchestrion, "Keepers of the Lock"),

        // Namazu
        (CollectibleType.Minion, "Attendee #777"),
        (CollectibleType.Mount, "Mikoshi"),
        (CollectibleType.Emote, "Yol Dance"),
        (CollectibleType.Orchestrion, "Seven Hundred Seventy-Seven Whiskers"),

        // Pixies
        (CollectibleType.Minion, "Wind-up Pixie"),
        (CollectibleType.Mount, "Portly Porxie"),
        (CollectibleType.Orchestrion, "The Garden's Gates"),

        // Qitari
        (CollectibleType.Minion, "The Behatted Serpent of Ronka"),
        (CollectibleType.Minion, "The Behelmeted Serpent of Ronka"),
        (CollectibleType.Mount, "Great Vessel of Ronka"),
        (CollectibleType.Orchestrion, "Hopl's Dropple"),

        // Dwarves
        (CollectibleType.Minion, "Wind-up Dragonet"),
        (CollectibleType.Minion, "Lalinator 5.H0"),
        (CollectibleType.Mount, "Rolling Tankard"),
        (CollectibleType.Emote, "Lali Hop"),
        (CollectibleType.Orchestrion, "Watts's Anvil"),

        // Loporrits
        (CollectibleType.Minion, "Findingway"),
        (CollectibleType.Mount, "Moon-hopper"),
        (CollectibleType.Orchestrion, "Dreamwalker"),
        (CollectibleType.Orchestrion, "Battle 1 from FINAL FANTASY IV"),

        // Pelupelu
        (CollectibleType.Minion, "Wind-up Pelupelu"),
        (CollectibleType.Mount, "Punutiy"),
        (CollectibleType.FashionAccessory, "Pelupack"),
        (CollectibleType.Orchestrion, "The Travel Agency (Dawntrail)"),

        // Arkasodara
        (CollectibleType.Minion, "Wind-up Arkasodara"),
        (CollectibleType.Mount, "Hippo Cart"),
        (CollectibleType.Orchestrion, "Hippo Ridin'"),
        (CollectibleType.TripleTriadCard, "Gajasura"),

        // Omicrons
        (CollectibleType.Minion, "Lumini"),
        (CollectibleType.Mount, "Miw Miisv"),
        (CollectibleType.Orchestrion, "Cradle of Hope"),
        (CollectibleType.TripleTriadCard, "N-7000"),

        // Yok Huy
        (CollectibleType.Minion, "Wind-up Yok Huy"),
        (CollectibleType.Mount, "Vurgar the Loyal"),
        (CollectibleType.Orchestrion, "Snowline Rollick"),
        (CollectibleType.TripleTriadCard, "Fahrafahr"),

        // Mamool Ja
        (CollectibleType.Minion, "Ja Tiika Leafkin"),
        (CollectibleType.Mount, "Branchbearer"),
        (CollectibleType.Orchestrion, "Home at Heart"),
        (CollectibleType.TripleTriadCard, "Blue Leafkin"),

        // Große-Kompanie-Quartiermeister (Flame/Storm/Serpent) - siehe GrandCompanyRequiredItems
        // unten, dort auch als GC-gebunden markiert statt nur hier gelistet.
        (CollectibleType.Barding, "Ul'dahn Barding"),
        (CollectibleType.Barding, "Ul'dahn Crested Barding"),
        (CollectibleType.Barding, "Ul'dahn Half Barding"),
        (CollectibleType.Barding, "Lominsan Barding"),
        (CollectibleType.Barding, "Lominsan Crested Barding"),
        (CollectibleType.Barding, "Lominsan Half Barding"),
        (CollectibleType.Barding, "Gridanian Barding"),
        (CollectibleType.Barding, "Gridanian Crested Barding"),
        (CollectibleType.Barding, "Gridanian Half Barding"),
        (CollectibleType.Minion, "Flame Hatchling"),
        (CollectibleType.Minion, "Storm Hatchling"),
        (CollectibleType.Minion, "Serpent Hatchling"),
        (CollectibleType.Orchestrion, "The Hall of Flames"),
        (CollectibleType.Orchestrion, "The Sands' Secrets"),
        (CollectibleType.Orchestrion, "Maelstrom Command"),
        (CollectibleType.Orchestrion, "Ripples in the Sea"),
        (CollectibleType.Orchestrion, "Into the Adder's Den"),
        (CollectibleType.Orchestrion, "Dewdrops & Moonbeams"),
    };

    /// <summary>
    /// Teilmenge von AchievementOrRankGatedItems, die zusätzlich (bzw. in erster Linie) eine
    /// Mitgliedschaft in EINER Großen Kompanie voraussetzt - die fünf "elementaren" ARR-Händler
    /// (Amalj'aa/Sylphic/Kobold/Sahagin/Ixali Vendor, GC-Rang-gebunden) und die Flame-/Storm-/
    /// Serpent-Quartiermeister. Ist der Charakter gar keiner Kompanie beigetreten, ist das die
    /// eigentliche Ursache (nicht ein zu niedriger Rang) - siehe GetAchievementOrRankGateReason.
    /// </summary>
    private static readonly HashSet<(CollectibleType Type, string Name)> GrandCompanyRequiredItems = new()
    {
        (CollectibleType.Minion, "Wind-up Amalj'aa"),
        (CollectibleType.Minion, "Wind-up Founder"),
        (CollectibleType.Mount, "Cavalry Drake"),
        (CollectibleType.Orchestrion, "Smoulder"),
        (CollectibleType.Minion, "Wind-up Sylph"),
        (CollectibleType.Minion, "Wind-up Violet"),
        (CollectibleType.Mount, "Laurel Goobbue"),
        (CollectibleType.Orchestrion, "Flibbertigibbet"),
        (CollectibleType.Minion, "Wind-up Kobold"),
        (CollectibleType.Minion, "Wind-up Kobolder"),
        (CollectibleType.Mount, "Bomb Palanquin"),
        (CollectibleType.Minion, "Wind-up Sahagin"),
        (CollectibleType.Minion, "Wind-up Sea Devil"),
        (CollectibleType.Mount, "Cavalry Elbst"),
        (CollectibleType.Minion, "Wind-up Ixal"),
        (CollectibleType.Minion, "Wind-up Dezul Qualan"),
        (CollectibleType.Mount, "Direwolf"),
        (CollectibleType.Barding, "Ul'dahn Barding"),
        (CollectibleType.Barding, "Ul'dahn Crested Barding"),
        (CollectibleType.Barding, "Ul'dahn Half Barding"),
        (CollectibleType.Barding, "Lominsan Barding"),
        (CollectibleType.Barding, "Lominsan Crested Barding"),
        (CollectibleType.Barding, "Lominsan Half Barding"),
        (CollectibleType.Barding, "Gridanian Barding"),
        (CollectibleType.Barding, "Gridanian Crested Barding"),
        (CollectibleType.Barding, "Gridanian Half Barding"),
        (CollectibleType.Minion, "Flame Hatchling"),
        (CollectibleType.Minion, "Storm Hatchling"),
        (CollectibleType.Minion, "Serpent Hatchling"),
        (CollectibleType.Orchestrion, "The Hall of Flames"),
        (CollectibleType.Orchestrion, "The Sands' Secrets"),
        (CollectibleType.Orchestrion, "Maelstrom Command"),
        (CollectibleType.Orchestrion, "Ripples in the Sea"),
        (CollectibleType.Orchestrion, "Into the Adder's Den"),
        (CollectibleType.Orchestrion, "Dewdrops & Moonbeams"),
    };

    /// <summary>
    /// Aktuelle GC-Rangstufe (0 = kein Rang/nicht beigetreten, 1 = Private Third Class, ...,
    /// 11 = Captain, siehe SergeantSecondClassRank) - für IsInAnyGrandCompany und den echten
    /// Rang-Vergleich in ComputeGrandCompanyOrTribeGateReason.
    /// </summary>
    private static unsafe byte CurrentGrandCompanyRank => PlayerState.Instance()->GetGrandCompanyRank();

    /// <summary>
    /// true, solange der Charakter noch KEINER der drei Großen Kompanien beigetreten ist. Über den
    /// GC-RANG geprüft (0 = kein Rang/nicht beigetreten) - genauso zuverlässig wie über
    /// PlayerState.GrandCompany direkt (0 dort = ebenfalls "keine", siehe CurrentGrandCompanyId),
    /// aber semantisch klarer an dieser Stelle.
    /// </summary>
    public static bool IsInAnyGrandCompany() => CurrentGrandCompanyRank != 0;

    /// <summary>
    /// Rohe Kompanie-Kennung aus PlayerState.GrandCompany, 1-BASIERT wie die Lumina-"GrandCompany"-
    /// Sheet-RowId (0 = keine, 1 = Sturmgarde/Maelstrom, 2 = Zweiter Adler/Twin Adder, 3 =
    /// Unsterbliche Flammen/Immortal Flames) - siehe die bereits bestehende, identisch kodierte
    /// Verwendung in IsQuestCurrentlyAcceptable ("row.GrandCompany.RowId != PlayerState.Instance()->
    /// GrandCompany"), die nur funktioniert, wenn beide Seiten dieselbe 1-basierte Kodierung nutzen.
    /// (Ein früherer Versuch, das auf 0-basiert "umzustellen", beruhte auf einem missverstandenen
    /// FFXIVClientStructs-Kommentar, der eigentlich zum NACHBARFELD _GCRanks gehört - nicht zu
    /// GrandCompany selbst - und wurde wieder zurückgenommen.)
    /// </summary>
    private static unsafe byte CurrentGrandCompanyId => PlayerState.Instance()->GrandCompany;

    /// <summary>
    /// Anders als die fünf "elementaren" ARR-Händler (GC-Rang-gebunden, aber unabhängig davon,
    /// WELCHER der drei Kompanien man angehört) sind die Flame-/Storm-/Serpent-Quartiermeister-Waren
    /// (Bardings/Hatchling-Minions/Orchestrion-Rollen) an die jeweils EIGENE Kompanie gebunden - ihre
    /// Seelen-Währung (Flame/Storm/Serpent Seals) lässt sich nur sammeln/ausgeben, solange man genau
    /// dieser Kompanie angehört. Von Hand gepflegt (Typ + Anzeigename -> benötigte Kompanie-Kennung,
    /// siehe CurrentGrandCompanyId), für GetAchievementOrRankGateReason.
    /// </summary>
    private static readonly Dictionary<(CollectibleType Type, string Name), byte> GrandCompanySpecificItems = new()
    {
        // Sturmgarde / Maelstrom (Storm Quartermaster)
        [(CollectibleType.Barding, "Lominsan Barding")] = 1,
        [(CollectibleType.Barding, "Lominsan Crested Barding")] = 1,
        [(CollectibleType.Barding, "Lominsan Half Barding")] = 1,
        [(CollectibleType.Minion, "Storm Hatchling")] = 1,
        [(CollectibleType.Orchestrion, "Maelstrom Command")] = 1,
        [(CollectibleType.Orchestrion, "Ripples in the Sea")] = 1,

        // Zweiter Adler / Twin Adder (Serpent Quartermaster)
        [(CollectibleType.Barding, "Gridanian Barding")] = 2,
        [(CollectibleType.Barding, "Gridanian Crested Barding")] = 2,
        [(CollectibleType.Barding, "Gridanian Half Barding")] = 2,
        [(CollectibleType.Minion, "Serpent Hatchling")] = 2,
        [(CollectibleType.Orchestrion, "Into the Adder's Den")] = 2,
        [(CollectibleType.Orchestrion, "Dewdrops & Moonbeams")] = 2,

        // Unsterbliche Flammen / Immortal Flames (Flame Quartermaster)
        [(CollectibleType.Barding, "Ul'dahn Barding")] = 3,
        [(CollectibleType.Barding, "Ul'dahn Crested Barding")] = 3,
        [(CollectibleType.Barding, "Ul'dahn Half Barding")] = 3,
        [(CollectibleType.Minion, "Flame Hatchling")] = 3,
        [(CollectibleType.Orchestrion, "The Hall of Flames")] = 3,
        [(CollectibleType.Orchestrion, "The Sands' Secrets")] = 3,
    };

    private static string GetGrandCompanyDisplayName(byte companyId) => companyId switch
    {
        1 => Loc.T("Sturmgarde", "the Maelstrom"),
        2 => Loc.T("Zweiter Adler", "the Order of the Twin Adder"),
        3 => Loc.T("Unsterbliche Flammen", "the Immortal Flames"),
        _ => Loc.T("einer Großen Kompanie", "a Grand Company"),
    };

    // Erkennt einen in Currency mitgeführten Rang-Hinweis wie "(Rank 4)" (siehe z.B. "25,000 Gil
    // (Rank 4)" bei Stammes-/GC-Händlern) - für ComputeGrandCompanyOrTribeGateReason unten.
    private static readonly Regex CurrencyRankPattern = new(@"\(Rank\s*(\d+)\)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Numerische GC-Rangstufe von "Sergeant Second Class" (1 = Private Third Class ... 11 =
    /// Captain, siehe GetGrandCompanyRank) - der für Quartiermeister-Waren (Bardings/Hatchling-
    /// Minions/Orchestrion-Rollen) nötige Rang, per Wiki verifiziert (Quelle in der Konversation).
    /// Für alle drei Kompanien dieselbe Stufe, auch wenn der genaue Rangname je Kompanie
    /// unterschiedlich lautet.
    /// </summary>
    private const byte SergeantSecondClassRank = 6;

    /// <summary>
    /// Live geprüft (nicht nur "steht in der Liste") - liefert null, wenn die Voraussetzung
    /// inzwischen TATSÄCHLICH erfüllt ist (Kompanie stimmt UND Rang reicht), sonst den Grund als
    /// lokalisierten Text. Für echte Stammes-Objekte (Vanu Vanu, Vath, ...) gibt es keine bekannte,
    /// live auslesbare Ruf-Rang-API - die gelten daher weiterhin als "erfüllt unbekannt/wahrscheinlich
    /// noch nicht" und bleiben statisch markiert, solange sie in AchievementOrRankGatedItems stehen.
    /// </summary>
    /// <summary>
    /// Währungen, die nur bei einem Händler ausgegeben werden können, dessen Sortiment erst nach
    /// einer bestimmten Quest kaufbar ist - gilt damit für JEDEN Eintrag mit dieser Währung (auch
    /// künftig ergänzte), ohne jeden einzeln pflegen zu müssen. Einzelne Einträge mit einer anderen
    /// (späteren) Quest überschreiben das per CollectibleEntry.RequiredQuest. Item-RowIds/Quests per
    /// Auswertung der SpecialShop-Daten des "Kornago Merchant" (Bentbranch Meadows) verifiziert.
    /// </summary>
    private static readonly Dictionary<uint, string> CurrencyRequiredQuest = new()
    {
        [51734] = "Into the Crucible",     // Faded Remnant of Resilience
        [51735] = "A Beastmaster's Path",  // Bright Remnant of Resilience
    };

    // Schaltet das Triple-Triad-Kartenspiel frei (Quest 65973, per Spieldaten verifiziert) - siehe
    // ComputeGrandCompanyOrTribeGateReason.
    private const string TripleTriadUnlockQuestName = "Triple Triad Trial";

    private static string? GetCurrencyRequiredQuest(CollectibleEntry entry)
    {
        if (CurrencyRequiredQuest.TryGetValue(entry.CurrencyItemId, out var quest))
            return quest;

        if (entry.AdditionalCurrencies != null)
        {
            foreach (var additional in entry.AdditionalCurrencies)
            {
                if (CurrencyRequiredQuest.TryGetValue(additional.CurrencyItemId, out quest))
                    return quest;
            }
        }

        return null;
    }

    private static bool IsAchievementCompleteByName(string achievementName)
    {
        var achievementSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
        var achievementId = ResolveAchievementIdByName(achievementName);
        return achievementId != null && achievementSheet != null
            && achievementSheet.TryGetRow(achievementId.Value, out var row) && UnlockState.IsAchievementComplete(row);
    }

    private static string? ComputeGrandCompanyOrTribeGateReason(CollectibleEntry entry)
    {
        // Errungenschafts-Fortschritt noch nicht vom Server geladen (siehe EnsureAchievementsLoaded) -
        // sonst stünden kurz ALLE Errungenschaften fälschlich als "fehlt" da.
        if (entry.Type == CollectibleType.Achievement && !EnsureAchievementsLoaded())
        {
            return Loc.T(
                "Errungenschafts-Fortschritt wird noch vom Server geladen...",
                "Achievement progress is still being loaded from the server...");
        }

        // Triple Triad selbst wird erst mit der Quest "Triple Triad Trial" freigeschaltet - vorher ist
        // KEINE Karte nutzbar, egal wo/wie erhältlich. Hat daher Vorrang vor allen anderen
        // Voraussetzungen der Karte.
        if (entry.Type == CollectibleType.TripleTriadCard)
        {
            var tripleTriadQuestId = ResolveQuestIdByName(TripleTriadUnlockQuestName);
            if (tripleTriadQuestId == null || !QuestManager.IsQuestComplete((ushort)tripleTriadQuestId.Value))
            {
                // NPC-Gegner-Karten: die Battle Hall (und damit Partien gegen andere NPCs) öffnet sich
                // erst nach dem Tutorial-Sieg gegen den Triple Triad Master - der schließt genau diese
                // Quest ab (der Master bietet danach "Enter the Battlehall" an, per Spieldaten verifiziert).
                if (entry.Category == TripleTriadNpcCategory)
                {
                    return Loc.T(
                        $"Battle Hall noch nicht freigeschaltet - benötigt einen Sieg gegen den Triple Triad Master im Gold Saucer (Quest \"{TripleTriadUnlockQuestName}\").",
                        $"Battle Hall not unlocked yet - requires a win against the Triple Triad Master in the Gold Saucer (quest \"{TripleTriadUnlockQuestName}\").");
                }

                return Loc.T(
                    $"Triple Triad ist noch nicht freigeschaltet - benötigt die abgeschlossene Quest \"{TripleTriadUnlockQuestName}\".",
                    $"Triple Triad isn't unlocked yet - requires the completed quest \"{TripleTriadUnlockQuestName}\".");
            }
        }

        // Händler-Voraussetzungen (siehe CollectibleEntry.RequiredQuest/RequiredAchievement und
        // CurrencyRequiredQuest) - live geprüft, damit die Markierung sofort verschwindet.
        var requiredVendorQuest = entry.RequiredQuest ?? GetCurrencyRequiredQuest(entry);
        if (!string.IsNullOrEmpty(requiredVendorQuest))
        {
            var vendorQuestId = ResolveQuestIdByName(requiredVendorQuest);
            if (vendorQuestId == null || !QuestManager.IsQuestComplete((ushort)vendorQuestId.Value))
            {
                return Loc.T(
                    $"Benötigt die abgeschlossene Quest \"{requiredVendorQuest}\".",
                    $"Requires the completed quest \"{requiredVendorQuest}\".");
            }
        }

        // Mehrere Voraussetzungs-Quests (siehe CollectibleEntry.RequiredQuests).
        if (entry.RequiredQuests is { Count: > 0 } requiredQuestList)
        {
            bool IsDone(string questName) =>
                ResolveQuestIdByName(questName) is { } id && QuestManager.IsQuestComplete((ushort)id);

            if (entry.RequiredQuestsAny)
            {
                if (!requiredQuestList.Any(IsDone))
                {
                    var options = string.Join("\" / \"", requiredQuestList);
                    return Loc.T(
                        $"Benötigt eine der abgeschlossenen Quests \"{options}\".",
                        $"Requires one of the completed quests \"{options}\".");
                }
            }
            else if (requiredQuestList.FirstOrDefault(q => !IsDone(q)) is { } missingQuest)
            {
                return Loc.T(
                    $"Benötigt die abgeschlossene Quest \"{missingQuest}\".",
                    $"Requires the completed quest \"{missingQuest}\".");
            }
        }

        if (!string.IsNullOrEmpty(entry.RequiredAchievement) && !IsAchievementCompleteByName(entry.RequiredAchievement))
        {
            return Loc.T(
                $"Benötigt die Errungenschaft \"{entry.RequiredAchievement}\".",
                $"Requires the achievement \"{entry.RequiredAchievement}\".");
        }

        // Hunting-Log-Ziel einer höheren, noch nicht erreichten Rang-Stufe (siehe GetHuntingLogEntries).
        if (entry.Type == CollectibleType.HuntingLog && entry.HuntingLogRequiredRank is { } requiredHuntingRank)
        {
            return Loc.T(
                $"Bedingung nicht erfüllt (Hunting-Log-Rang {requiredHuntingRank} noch nicht erreicht).",
                $"Condition not met (hunting log rank {requiredHuntingRank} not reached yet).");
        }

        // Das Sightseeing Log selbst wird über die Quest "A Sight to Behold" freigeschaltet, NICHT
        // durchs Fliegen (das war vorher fälschlich über IsTypeCurrentlyPossible/CanFly verknüpft) -
        // ohne freigeschaltetes Log lässt sich kein einziger Sightseeing-Eintrag abschließen. Live
        // geprüft, damit die Markierung sofort verschwindet, sobald die Quest erledigt ist.
        if (entry.Type == CollectibleType.Sightseeing)
        {
            if (!IsSightseeingLogUnlocked())
            {
                return Loc.T(
                    "Sightseeing Log noch nicht freigeschaltet (Quest \"A Sight to Behold\").",
                    "Sightseeing Log not unlocked yet (quest \"A Sight to Behold\").");
            }

            // Explizite Nutzeranforderung: die Automation/das Feature soll NUR mit freigeschaltetem
            // Fliegen funktionieren - direkt nach der Log-Prüfung, noch vor allem Weiteren (auch vor
            // Jumping-Puzzle-Unterstützung, siehe Priorität 1 unten). CanFly berücksichtigt dabei auch
            // Zonen, in denen Fliegen erst durch genug gesammelte Ätherströmungen freigeschaltet wird.
            if (!IsSightseeingFlyingRequirementMet(entry))
            {
                return Loc.T(
                    "Fliegen nicht freigeschaltet.",
                    "Flying not unlocked.");
            }

            // Priorität 1 (siehe Nutzeranfrage): erst die ersten 20 A-Realm-Reborn-Punkte, DANN erst
            // Jumping-Puzzle-Unterstützung, DANN Wetter/Uhrzeit prüfen - ein noch nicht freigeschaltetes
            // Buch ist der fundamentalste Blocker und soll daher vor allem anderen angezeigt werden,
            // auch wenn der jeweilige Punkt zusätzlich noch ein Jumping Puzzle wäre oder gerade
            // falsches Wetter hätte.
            //
            // Die ersten 20 A-Realm-Reborn-Punkte sind mit dem Log selbst verfügbar - die restlichen
            // 60 (SightseeingNeedsFirstTwenty) erst, wenn ALLE ersten 20 aufgezeichnet sind (danach
            // schaltet Millith Ironheart in Old Gridania das Buch frei) - siehe SightseeingFirstBookCount.
            if (entry.SightseeingNeedsFirstTwenty && !AreFirstSightseeingBookEntriesComplete())
            {
                return Loc.T(
                    $"Benötigt zuerst alle ersten {SightseeingFirstBookCount} A-Realm-Reborn-Sichtungspunkte (schaltet die restlichen bei Millith Ironheart, Old Gridania, frei).",
                    $"Requires the first {SightseeingFirstBookCount} A Realm Reborn sightseeing points first (unlocks the rest with Millith Ironheart in Old Gridania).");
            }

            // Heavensward (feste Quest) bzw. Stormblood und später (siehe ResolveSightseeingGateQuests,
            // aus Lumina "AdventureExPhase" aufgelöst) - jedes spätere Buch braucht seine eigene Quest.
            // Gleiche Kategorie wie SightseeingNeedsFirstTwenty oben (Buch-Freischaltung), daher direkt
            // danach geprüft.
            if (entry.SightseeingGateQuestId != 0 && !QuestManager.IsQuestComplete((ushort)entry.SightseeingGateQuestId))
            {
                var gateQuestName = ResolveQuestNameById(entry.SightseeingGateQuestId);
                return string.IsNullOrEmpty(gateQuestName)
                    ? Loc.T("Dieses Sightseeing-Log-Buch ist noch nicht freigeschaltet.", "This sightseeing log book isn't unlocked yet.")
                    : Loc.T(
                        $"Benötigt die abgeschlossene Quest \"{gateQuestName}\", um dieses Sightseeing-Log-Buch freizuschalten.",
                        $"Requires the completed quest \"{gateQuestName}\" to unlock this sightseeing log book.");
            }

            // Priorität 2: Jumping-Puzzle-Unterstützung (siehe SightseeingUnsupportedByAutomation).
            if (SightseeingUnsupportedByAutomation.TryGetValue(entry.Id, out var unsupportedReason))
                return Loc.T(unsupportedReason.De, unsupportedReason.En);

            // Priorität 3: Wetter/Uhrzeit. Nur A-Realm-Reborn-Punkte verlangen ein bestimmtes Wetter
            // (SightseeingWeatherMask != 0),
            // siehe RealmRebornVistaWeathers. Hängt bei beiden Meldungen unten zusätzlich einen
            // "verfügbar in..."-Timer an (siehe GetSightseeingAvailableAtUtc), der - falls der Punkt
            // ZUSÄTZLICH auch ein Zeitfenster hat - beide Bedingungen gemeinsam berücksichtigt, nicht
            // nur die hier gerade geprüfte.
            if (entry.SightseeingWeatherMask != 0 && !IsSightseeingWeatherOk(entry))
            {
                var availableIn = GetRemaining(GetSightseeingAvailableAtUtc(entry));
                var suffix = availableIn.HasValue
                    ? Loc.T($" (verfügbar in {FormatSightseeingAvailableIn(availableIn.Value)})", $" (available in {FormatSightseeingAvailableIn(availableIn.Value)})")
                    : "";
                return Loc.T(
                    $"Das Wetter in dieser Zone passt gerade nicht (nur bei bestimmtem Wetter sichtbar/abschließbar).{suffix}",
                    $"The weather in this zone isn't right currently (only visible/completable with specific weather).{suffix}");
            }

            if (entry.SightseeingHasTimeWindow && !IsSightseeingTimeOk(entry))
            {
                var availableIn = GetRemaining(GetSightseeingAvailableAtUtc(entry));
                var suffix = availableIn.HasValue
                    ? Loc.T($" (verfügbar in {FormatSightseeingAvailableIn(availableIn.Value)})", $" (available in {FormatSightseeingAvailableIn(availableIn.Value)})")
                    : "";
                return Loc.T(
                    $"Nur zwischen {entry.SightseeingFirstBell:00}:00 und {entry.SightseeingLastBell:00}:59 Eorzeazeit sichtbar/abschließbar.{suffix}",
                    $"Only visible/completable between {entry.SightseeingFirstBell:00}:00 and {entry.SightseeingLastBell:00}:59 Eorzea time.{suffix}");
            }
        }

        // Mounts mit einer manuell erfassten Zusatz-Voraussetzung (siehe MountRequiredQuest-
        // Kommentar) - live gegen den tatsächlichen Quest-Abschluss geprüft.
        if (entry.Type == CollectibleType.Mount && MountRequiredQuest.TryGetValue(entry.Name, out var requiredQuestForMount))
        {
            var requiredQuestId = ResolveQuestIdByName(requiredQuestForMount);
            if (requiredQuestId == null || !QuestManager.IsQuestComplete((ushort)requiredQuestId.Value))
            {
                return Loc.T(
                    $"Benötigt die abgeschlossene Quest \"{requiredQuestForMount}\".",
                    $"Requires the completed quest \"{requiredQuestForMount}\".");
            }
        }

        // Quests mit einer manuell erfassten Zusatz-Voraussetzung (siehe QuestRequiredMounts-
        // Kommentar) - live gegen den tatsächlichen Mount-Besitz geprüft.
        if (entry.Type == CollectibleType.Quest && QuestRequiredMounts.TryGetValue(entry.Name, out var requiredMounts))
        {
            var missingMount = requiredMounts.FirstOrDefault(mountName => !IsMountUnlockedByName(mountName));
            if (missingMount != null)
            {
                return Loc.T(
                    $"Benötigt das freigeschaltete Mount \"{missingMount}\".",
                    $"Requires the unlocked mount \"{missingMount}\".");
            }
        }

        // "Simply to Dye For" (Färben-Freischaltung) braucht die abgeschlossene Artefakt-Rüstungs-
        // quest EINES beliebigen ursprünglichen A-Realm-Reborn-Jobs (siehe
        // DyeUnlockJobQuestByClassJobId-Kommentar) - JEDE davon zählt (OR), nicht nur die des
        // aktuell gespielten Jobs. Zeigt bevorzugt den Namen DER Job-Quest, die zum aktuell
        // gespielten Job passt (am ehesten relevant), sonst den generischen Platzhalter.
        if (entry.Type == CollectibleType.Quest && string.Equals(entry.Name, DyeUnlockQuestName, StringComparison.OrdinalIgnoreCase))
        {
            var anyDyeUnlockQuestComplete = DyeUnlockJobQuestByClassJobId.Values
                .SelectMany(ResolveAllQuestIdsByName)
                .Any(id => QuestManager.IsQuestComplete((ushort)id));

            if (!anyDyeUnlockQuestComplete)
            {
                var currentClassJobId = ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
                var jobQuestName = DyeUnlockJobQuestByClassJobId.TryGetValue(currentClassJobId, out var name)
                    ? name
                    : "Artefact Armor Quest";

                return Loc.T(
                    $"Benötigt die abgeschlossene Quest \"{jobQuestName}\" (oder die eines anderen A-Realm-Reborn-Jobs).",
                    $"Requires the completed quest \"{jobQuestName}\" (or that of another A Realm Reborn job).");
            }
        }

        // Nur während eines saisonalen Events kaufbare Einträge (Category "Saisonevent", siehe
        // IsSeasonalEventEntryCurrentlyActive) - früher komplett aus der Liste gefiltert, statt
        // dessen (wie jede andere Bedingung) live geprüft und als "Bedingung nicht erfüllt"
        // markiert, solange das zugehörige Event gerade NICHT läuft. Der Eventname steht bei diesen
        // Einträgen bereits als Klartext im Source-Feld (z.B. "The Rising (2026)").
        if (entry.Category == "Saisonevent" && !IsSeasonalEventEntryCurrentlyActive(entry))
        {
            if (!string.IsNullOrEmpty(entry.EventName))
            {
                return Loc.T(
                    $"Nur während des Events {entry.EventName} erhältlich, das gerade nicht läuft.",
                    $"Only available during the event {entry.EventName}, which isn't currently running.");
            }

            return Loc.T(
                $"Nur während eines Events erhältlich ({entry.Source}), das gerade nicht läuft.",
                $"Only available during an event ({entry.Source}), which isn't currently running.");
        }

        // Die folgenden Prüfungen (Große Kompanie, Stammesrang) sind von Hand pro Typ + NAME gepflegt
        // und gelten nur beim jeweiligen Kompanie-/Stammeshändler - dieselbe Ware beim Itinerant
        // Moogle (EventName gesetzt, siehe GetItinerantMoogleEntries) ist dort ohne Rang kaufbar
        // (Nutzer-Report: z.B. Stammes-Waren im "Previous"-Reiter fälschlich als "Rang fehlt"
        // markiert). Deren eigene Voraussetzungen (Event-Zeitfenster, Quest) sind oben schon geprüft.
        if (!string.IsNullOrEmpty(entry.EventName))
            return null;

        // Quartiermeister-Waren (Bardings/Hatchling-Minions/Orchestrion-Rollen) sind an die JEWEILS
        // EIGENE Kompanie gebunden (siehe GrandCompanySpecificItems-Kommentar).
        if (GrandCompanySpecificItems.TryGetValue((entry.Type, entry.Name), out var requiredCompanyId))
        {
            // WICHTIG: erst auf "überhaupt beigetreten" prüfen (über den Rang, siehe
            // IsInAnyGrandCompany-Kommentar), bevor CurrentGrandCompanyId verglichen wird - dessen
            // "0" bedeutet Sturmgarde, nicht "keine Kompanie", ein nicht beigetretener Charakter
            // würde sonst fälschlich als Sturmgarde-Mitglied durchgehen.
            if (!IsInAnyGrandCompany())
            {
                var requiredCompanyNameForNone = GetGrandCompanyDisplayName(requiredCompanyId);
                return Loc.T(
                    $"Du bist noch keiner Großen Kompanie beigetreten (benötigt: {requiredCompanyNameForNone}).",
                    $"You have not joined a Grand Company yet (requires {requiredCompanyNameForNone}).");
            }

            if (CurrentGrandCompanyId != requiredCompanyId)
            {
                var requiredCompanyName = GetGrandCompanyDisplayName(requiredCompanyId);
                return Loc.T(
                    $"Du bist nicht {requiredCompanyName} beigetreten.",
                    $"You have not joined {requiredCompanyName}.");
            }

            // Richtige Kompanie - jetzt den TATSÄCHLICHEN Rang gegen die nötige Stufe prüfen, statt
            // die Meldung blind anzuzeigen, nur weil der Eintrag in der Liste steht (das führte dazu,
            // dass Spieler mit längst ausreichendem Rang trotzdem "Rang fehlt" zu sehen bekamen).
            if (CurrentGrandCompanyRank >= SergeantSecondClassRank)
                return null;

            var companyNameForRank = GetGrandCompanyDisplayName(requiredCompanyId);
            return Loc.T(
                $"Benötigt bei {companyNameForRank} den GC-Rang \"Sergeant Second Class\" (bzw. die entsprechende Stufe).",
                $"Requires the GC rank \"Sergeant Second Class\" (or the equivalent tier) with {companyNameForRank}.");
        }

        // Die fünf "elementaren" ARR-Händler (Amalj'aa/Sylphic/Kobold/Sahagin/Ixali Vendor) sind
        // an den GC-RANG gebunden, aber NICHT an eine bestimmte der drei Kompanien.
        if (GrandCompanyRequiredItems.Contains((entry.Type, entry.Name)))
        {
            if (!IsInAnyGrandCompany())
            {
                return Loc.T(
                    "Du bist noch keiner Großen Kompanie beigetreten.",
                    "You have not joined a Grand Company yet.");
            }

            var gcRankMatch = CurrencyRankPattern.Match(entry.Currency ?? string.Empty);
            if (gcRankMatch.Success && byte.TryParse(gcRankMatch.Groups[1].Value, out var requiredGcRank))
            {
                if (CurrentGrandCompanyRank >= requiredGcRank)
                    return null;

                // Zwar tatsächlich ein GC-Rang (siehe Kommentar oben, nicht Stammes-Ruf), aber zur
                // Wiedererkennung mit dem Namen aus dem Vendor-Feld beschriftet (z.B. "Amalj'aa" aus
                // "Amalj'aa Vendor") - "GC-Rang X" allein war zu unklar, WELCHER der fünf Händler
                // gemeint ist.
                var vendorTribeName = (entry.Vendor ?? string.Empty).Replace("Vendor", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                return string.IsNullOrEmpty(vendorTribeName)
                    ? Loc.T($"Benötigt GC-Rang {requiredGcRank}.", $"Requires GC rank {requiredGcRank}.")
                    : Loc.T($"Benötigt {vendorTribeName}-Rang {requiredGcRank}.", $"Requires {vendorTribeName} rank {requiredGcRank}.");
            }

            return Loc.T(
                "Voraussetzung (Errungenschaft/Rang) noch nicht erfüllt.",
                "Requirement (achievement/rank) not yet met.");
        }

        // Übrig bleiben die echten Stammes-Objekte (Vanu Vanu, Vath, Moogles, Kojin, ...) - live gegen
        // den Ruf-Rang DES STAMMES geprüft (PlayerState.GetBeastTribeRank, siehe ResolveBeastTribe),
        // nicht gegen den Händler: früher stand dort der Händlername (z.B. "Shikitahe" statt "Kojin")
        // und die Markierung blieb auch bei längst erreichtem Rang dauerhaft stehen (Nutzer-Report).
        if (AchievementOrRankGatedItems.Contains((entry.Type, entry.Name)))
        {
            var tribeRankMatch = CurrencyRankPattern.Match(entry.Currency ?? string.Empty);
            if (tribeRankMatch.Success && ResolveBeastTribe(entry) is { } tribe && byte.TryParse(tribeRankMatch.Groups[1].Value, out var requiredTribeRank))
            {
                // Manche Datendatei-Angaben liegen über dem Höchstrang des Stammes - dann zählt der Höchstrang.
                var effectiveRequiredRank = Math.Min(requiredTribeRank, tribe.MaxRank);
                if (GetBeastTribeRank(tribe.TribeId) >= effectiveRequiredRank)
                    return null;

                return Loc.T(
                    $"Benötigt {tribe.Name}-Stammesrang {requiredTribeRank}.",
                    $"Requires {tribe.Name} tribe rank {requiredTribeRank}.");
            }

            if (tribeRankMatch.Success)
            {
                var rank = tribeRankMatch.Groups[1].Value;
                var tribeName = (entry.Vendor ?? string.Empty).Replace("Vendor", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                return string.IsNullOrEmpty(tribeName)
                    ? Loc.T($"Benötigt Stammesrang {rank}.", $"Requires tribe rank {rank}.")
                    : Loc.T($"Benötigt {tribeName}-Stammesrang {rank}.", $"Requires {tribeName} tribe rank {rank}.");
            }

            return Loc.T(
                "Voraussetzung (Errungenschaft/Rang) noch nicht erfüllt.",
                "Requirement (achievement/rank) not yet met.");
        }

        return null;
    }

    public static bool IsAchievementOrRankGated(CollectibleEntry entry) =>
        ComputeGrandCompanyOrTribeGateReason(entry) != null;

    // Stammeshändler, die mit GIL statt der stammeseigenen Währung verkaufen - dort lässt sich der
    // Stamm nicht über die Währung (siehe ResolveBeastTribe) bestimmen. Wert = Lumina-BeastTribe-RowId.
    private static readonly Dictionary<string, uint> BeastTribeByGilVendor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Luna Vanu"] = 6,         // Vanu Vanu
        ["Vath Stickpeddler"] = 7, // Vath
        ["Mogmul Mogbelly"] = 8,   // Moogles
    };

    private static Dictionary<uint, (uint TribeId, string Name, byte MaxRank)>? beastTribeByCurrencyCache;
    private static Dictionary<uint, (uint TribeId, string Name, byte MaxRank)>? beastTribeByIdCache;

    /// <summary>
    /// Zu welchem Stamm ein Stammeshändler-Eintrag gehört - primär über die Währung (Lumina
    /// "BeastTribe".CurrencyItem, z.B. "Kojin Sango" -> Kojin), für die Gil-Händler über
    /// BeastTribeByGilVendor. null, wenn nicht zuordenbar.
    /// </summary>
    private static (uint TribeId, string Name, byte MaxRank)? ResolveBeastTribe(CollectibleEntry entry)
    {
        if (beastTribeByCurrencyCache == null)
        {
            beastTribeByCurrencyCache = new();
            beastTribeByIdCache = new();
            var sheet = DataManager.GetExcelSheet<BeastTribe>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    var name = row.Name.ToString();
                    if (row.RowId == 0 || string.IsNullOrEmpty(name))
                        continue;

                    // Lumina führt einige Namen klein ("moogles", "pixies", ...) - für die Anzeige groß.
                    var info = (row.RowId, char.ToUpperInvariant(name[0]) + name[1..], row.MaxRank);
                    beastTribeByIdCache[row.RowId] = info;
                    if (row.CurrencyItem.RowId != 0)
                        beastTribeByCurrencyCache.TryAdd(row.CurrencyItem.RowId, info);
                }
            }
        }

        if (beastTribeByCurrencyCache.TryGetValue(entry.CurrencyItemId, out var byCurrency))
            return byCurrency;

        if (BeastTribeByGilVendor.TryGetValue(entry.Vendor ?? string.Empty, out var tribeId) && beastTribeByIdCache!.TryGetValue(tribeId, out var byVendor))
            return byVendor;

        return null;
    }

    private static unsafe byte GetBeastTribeRank(uint tribeId) => PlayerState.Instance()->GetBeastTribeRank((byte)tribeId);

    /// <summary>
    /// Erklärt, WAS genau bei einem als gated erkannten Eintrag fehlt (z.B. "Benötigt Kobold-Rang
    /// 4") - für den Hover-Tooltip neben dem "Bedingung nicht erfüllt"-Hinweis im Overlay. Sollte
    /// nur aufgerufen werden, wenn IsAchievementOrRankGated bereits true zurückgegeben hat.
    /// </summary>
    public static string GetAchievementOrRankGateReason(CollectibleEntry entry) =>
        ComputeGrandCompanyOrTribeGateReason(entry) ?? string.Empty;

    /// <summary>
    /// Große-Kompanie-gebundene Quests, die durch eine übergeordnete Errungenschaft ersetzt werden -
    /// z.B. gibt es "My Little Chocobo" für jede der drei Großen Kompanien als eigene Quest, aber
    /// abschließbar ist immer nur die der aktuellen Kompanie; wechselt man später die Kompanie,
    /// stünde die (nie abschließbare) Quest der neuen Kompanie sonst dauerhaft als "fehlend" da,
    /// obwohl der Chocobo längst freigeschaltet ist (siehe Errungenschaft "My Little Chocobo",
    /// Kategorie "Grand Company - General", die kompanieunabhängig bereits abgeschlossen ist). Wie
    /// QuestRequiredMounts von Hand gepflegt, da auch hier keine auslesbare Verknüpfung existiert.
    /// </summary>
    private static readonly Dictionary<string, string> QuestObsoletedByAchievement = new(StringComparer.OrdinalIgnoreCase)
    {
        ["My Little Chocobo (Twin Adder)"] = "My Little Chocobo",
        ["My Little Chocobo (Maelstrom)"] = "My Little Chocobo",
        ["My Little Chocobo (Immortal Flames)"] = "My Little Chocobo",
    };

    private static Dictionary<uint, string>? questObsoletedByAchievementIdCache;

    private static Dictionary<string, uint>? achievementIdsByNameCache;

    /// <summary>
    /// Löst einen Errungenschafts-Anzeigenamen (Englisch, exakt wie im Spiel) auf seine RowId auf -
    /// für UnlockState.IsAchievementComplete, das eine Achievement-Zeile statt eines Namens braucht.
    /// Analog zu ResolveQuestIdByName, einmalig aufgebaut und gecacht.
    /// </summary>
    private static uint? ResolveAchievementIdByName(string achievementName)
    {
        if (achievementIdsByNameCache == null)
        {
            achievementIdsByNameCache = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var achievementSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
            if (achievementSheet != null)
            {
                foreach (var row in achievementSheet)
                {
                    var rowName = row.Name.ToString();
                    if (!string.IsNullOrEmpty(rowName))
                        achievementIdsByNameCache.TryAdd(rowName, row.RowId);
                }
            }
        }

        return achievementIdsByNameCache.TryGetValue(achievementName, out var id) ? id : null;
    }

    /// <summary>
    /// Siehe QuestObsoletedByAchievement - true, wenn diese Quest zwar laut QuestManager nicht
    /// abgeschlossen ist, aber die zugehörige Errungenschaft bereits vorliegt und die Quest damit
    /// funktional erledigt/nicht mehr annehmbar ist. Für Quests ohne Eintrag immer false. Bewusst
    /// über die RowId statt den Namen geprüft (QuestObsoletedByAchievement einmalig dorthin
    /// aufgelöst) - IsOwned wird an manchen Stellen (siehe MainWindow.DrawStatisticsPage) mit einem
    /// nur-Id CollectibleEntry ohne gesetzten Name aufgerufen, ein namensbasierter Abgleich würde
    /// dort immer ins Leere laufen.
    /// </summary>
    private static unsafe bool IsQuestObsoletedByAchievement(uint questId)
    {
        if (questObsoletedByAchievementIdCache == null)
        {
            questObsoletedByAchievementIdCache = new Dictionary<uint, string>();
            foreach (var (questName, achievementName) in QuestObsoletedByAchievement)
            {
                var resolvedId = ResolveQuestIdByName(questName);
                if (resolvedId != null)
                    questObsoletedByAchievementIdCache[resolvedId.Value] = achievementName;
            }
        }

        if (!questObsoletedByAchievementIdCache.TryGetValue(questId, out var requiredAchievement))
            return false;

        var achievementSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
        var achievementId = ResolveAchievementIdByName(requiredAchievement);
        return achievementId != null && achievementSheet != null
            && achievementSheet.TryGetRow(achievementId.Value, out var row) && UnlockState.IsAchievementComplete(row);
    }

    /// <summary>
    /// Quest-Gruppen, von denen ein Charakter aus Spielsystem-Gründen (Startstadt/Große Kompanie)
    /// nur EINE jemals annehmen/abschließen kann - z.B. "Coming to Limsa Lominsa"/"Coming to
    /// Ul'dah"/"Coming to Gridania" (an die Startstadt gebunden) oder "An Ill-conceived Venture"
    /// (an die aktuelle Große Kompanie gebunden, alle drei GC-Varianten tragen dabei sogar denselben
    /// Anzeigenamen - siehe ResolveAllQuestIdsByName) - die jeweils anderen bleiben für diesen
    /// Charakter dauerhaft nicht abschließbar und stünden sonst fälschlich als "fehlend" da. Ist
    /// irgendeine Quest einer Gruppe abgeschlossen, gelten alle anderen Quests derselben Gruppe als
    /// erledigt (siehe IsQuestObsoletedByMutualExclusion). Von Hand gepflegt wie
    /// QuestObsoletedByAchievement, wird nur ergänzt, wenn konkrete Quests genannt werden.
    /// </summary>
    private static readonly string[][] MutuallyExclusiveQuestGroups =
    {
        new[] { "Coming to Limsa Lominsa", "Coming to Ul'dah", "Coming to Gridania" },
        new[] { "An Ill-conceived Venture" },
    };

    private static Dictionary<uint, uint[]>? mutuallyExclusiveQuestIdsCache;

    private static Dictionary<string, List<uint>>? questIdsByNameMultiCache;

    /// <summary>
    /// Wie ResolveQuestIdByName, aber liefert ALLE Zeilen mit diesem Anzeigenamen statt nur der
    /// ersten - nötig für MutuallyExclusiveQuestGroups: manche Quest-Gruppen (z.B. die drei Große-
    /// Kompanie-Varianten von "An Ill-conceived Venture") tragen im Gegensatz zu den "Coming to
    /// X"-Quests exakt denselben Namen für alle Varianten, ein Name -> EINE RowId-Cache (siehe
    /// questIdsByNameCache) würde hier zwei der drei Zeilen verschlucken.
    /// </summary>
    private static IReadOnlyList<uint> ResolveAllQuestIdsByName(string questName)
    {
        if (questIdsByNameMultiCache == null)
        {
            questIdsByNameMultiCache = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            var questSheet = DataManager.GetExcelSheet<Quest>();
            if (questSheet != null)
            {
                foreach (var row in questSheet)
                {
                    var rowName = row.Name.ToString();
                    if (string.IsNullOrEmpty(rowName))
                        continue;

                    if (!questIdsByNameMultiCache.TryGetValue(rowName, out var ids))
                        questIdsByNameMultiCache[rowName] = ids = new List<uint>();

                    ids.Add(row.RowId);
                }
            }
        }

        return questIdsByNameMultiCache.TryGetValue(questName, out var result) ? result : Array.Empty<uint>();
    }

    /// <summary>
    /// Siehe MutuallyExclusiveQuestGroups - true, wenn diese Quest zwar laut QuestManager nicht
    /// abgeschlossen ist, aber eine ANDERE Quest aus derselben (sich gegenseitig ausschließenden)
    /// Gruppe bereits abgeschlossen wurde (der Charakter also nachweislich in einer anderen
    /// Startstadt/Großen Kompanie begonnen hat bzw. steckt). Für Quests ohne Gruppenzugehörigkeit
    /// immer false.
    /// </summary>
    private static bool IsQuestObsoletedByMutualExclusion(uint questId)
    {
        if (mutuallyExclusiveQuestIdsCache == null)
        {
            mutuallyExclusiveQuestIdsCache = new Dictionary<uint, uint[]>();
            foreach (var group in MutuallyExclusiveQuestGroups)
            {
                var resolvedIds = group.SelectMany(ResolveAllQuestIdsByName).Distinct().ToArray();

                foreach (var id in resolvedIds)
                    mutuallyExclusiveQuestIdsCache[id] = resolvedIds.Where(other => other != id).ToArray();
            }
        }

        if (!mutuallyExclusiveQuestIdsCache.TryGetValue(questId, out var siblingQuestIds))
            return false;

        return siblingQuestIds.Any(id => QuestManager.IsQuestComplete((ushort)id));
    }

    private static Dictionary<string, uint>? questIdsByNameCache;

    /// <summary>
    /// Löst einen Quest-Anzeigenamen (Englisch, exakt wie im Spiel) auf seine RowId auf - für
    /// QuestManager.IsQuestComplete, das nur RowIds akzeptiert. Einmalig über das ganze Quest-Sheet
    /// aufgebaut und danach gecacht (Namen ändern sich nicht zur Laufzeit).
    /// </summary>
    private static uint? ResolveQuestIdByName(string questName)
    {
        if (questIdsByNameCache == null)
        {
            questIdsByNameCache = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var questSheet = DataManager.GetExcelSheet<Quest>();
            if (questSheet != null)
            {
                foreach (var row in questSheet)
                {
                    var rowName = row.Name.ToString();
                    if (!string.IsNullOrEmpty(rowName))
                        questIdsByNameCache.TryAdd(rowName, row.RowId);
                }
            }
        }

        return questIdsByNameCache.TryGetValue(questName, out var id) ? id : null;
    }

    /// <summary>
    /// Prüft alle Freischalt-/Annehmbarkeits-Bedingungen einer Quest AUSSER dem Vergabeort (den
    /// prüft ComputeLiveZoneEntries zusätzlich selbst gegen acceptablePlaceNameIds der jeweiligen
    /// Zone) - ausgelagert, damit dieselbe Logik auch zonenunabhängig für die globale Statistik
    /// (siehe GetAllTrackedQuestIds/MainWindow.DrawStatisticsPage) genutzt werden kann, ohne sie
    /// doppelt zu pflegen.
    /// </summary>
    /// <summary>
    /// Quests, die laut Lumina-Sheet zwar noch existieren (und ohne diese Sperre fälschlich als
    /// "annehmbar" durchgehen würden), im Spiel selbst aber nicht mehr vergeben werden (z.B. durch
    /// einen späteren Patch entfernter NPC/entfernte Questkette) - von Hand gepflegt, wird nur
    /// ergänzt, wenn konkrete Quests genannt werden.
    /// </summary>
    private static readonly HashSet<string> RemovedQuestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "He's Got a Ticket to Ride",
    };

    private unsafe bool IsQuestCurrentlyAcceptable(Quest row, byte playerLevel)
    {
        if (RemovedQuestNames.Contains(row.Name.ToString()))
            return false;
        if (row.IsRepeatable)
            return false;
        if (row.BeastTribe.RowId != 0)
            return false;
        // Ohne Journal-Genre UND ohne Vergabe-NPC ist es keine annehmbare Quest (interne Zeilen).
        // Freischalt-Quests ("blaues Plus", z.B. "Triple Triad Trial", "Hitting the Cactpot", "So You
        // Want to Be a ...") haben zwar KEIN Genre, aber einen Vergabe-NPC - die gehören dazu.
        if (row.JournalGenre.RowId == 0 && row.IssuerLocation.RowId == 0)
            return false;

        // Hauptquests (MSQ) gehören nicht in eine Sammelobjekt-Übersicht - erkannt über
        // die JournalSection (0 = "Main Scenario" ARR-EW, 1 = "Main Scenario" Dawntrail).
        var journalSection = row.JournalGenre.ValueNullable?.JournalCategory.ValueNullable?.JournalSection.RowId;
        if (journalSection is 0 or 1)
            return false;

        // Saisonale Event-Quests (Winterstern, Valentionstag, etc.) nur zeigen, wenn das
        // zugehörige Event aktuell auch wirklich läuft - sonst wären sie "annehmbar"
        // laut Datenbank, aber im Spiel gerade gar nicht verfügbar.
        if (row.Festival.RowId != 0 && !IsFestivalActive((ushort)row.Festival.RowId))
            return false;

        // An eine bestimmte Große Kompanie (Sturmgarde/Zweiter Adler/Unsterbliche
        // Flammen) gebundene Quests (z.B. "My Little Chocobo (Maelstrom)") nur zeigen,
        // wenn der Charakter tatsächlich dieser Kompanie angehört - sonst stünde die
        // Quest laut Datenbank als "annehmbar" da, obwohl man einer anderen/gar keiner
        // Kompanie beigetreten ist und sie im Spiel gar nicht annehmen kann.
        if (row.GrandCompany.RowId != 0 && row.GrandCompany.RowId != PlayerState.Instance()->GrandCompany)
            return false;

        // Klassengebundene Quests bewusst ausklammern - aber nicht nur Kategorie 1 ("All
        // Classes") akzeptieren, sondern jede Kategorie, deren Name mit "All" beginnt
        // (z.B. Kategorie 130 "All classes and jobs (excluding limited jobs)"). Das
        // deckt die meisten normalen Quests ab, die nur Limited Jobs wie Blue Mage ausschließen.
        // WICHTIG: Der Name muss explizit auf Englisch abgefragt werden - row.ClassJobCategory0
        // liefert sonst den Namen in der Spielclient-Sprache (z.B. Deutsch "Alle Klassen"),
        // der nie mit "All" beginnt und dadurch ausnahmslos JEDE Quest ausgeschlossen hätte.
        var categoryName = GetEnglishClassJobCategoryName(row.ClassJobCategory0.RowId);
        if (!categoryName.StartsWith("All", StringComparison.Ordinal))
            return false;
        if (row.ClassJobLevel[0] > playerLevel)
            return false;

        var hasPrev = false;
        var prevOk = false;
        foreach (var prev in row.PreviousQuest)
        {
            if (prev.RowId == 0)
                continue;

            hasPrev = true;
            if (QuestManager.IsQuestComplete((ushort)prev.RowId))
            {
                prevOk = true;
                break;
            }
        }

        if (hasPrev && !prevOk)
            return false;

        // Separate Sperre für Quests, die zwar keinen direkten Vorgänger in derselben
        // Questreihe haben (PreviousQuest bleibt dafür leer), aber trotzdem erst nach
        // Erreichen eines bestimmten Story-/Erweiterungsfortschritts angeboten werden
        // (z.B. viele Nebenquests, die "irgendwann in Endwalker" freischalten) - ohne
        // diesen Check standen solche Quests fälschlich als "annehmbar" da, obwohl sie
        // auf der Karte noch gar kein Icon hatten (siehe "Wings of Hope").
        var hasLock = false;
        var lockOk = false;
        foreach (var lockRef in row.QuestLock)
        {
            if (lockRef.RowId == 0)
                continue;

            hasLock = true;
            if (QuestManager.IsQuestComplete((ushort)lockRef.RowId))
            {
                lockOk = true;
                break;
            }
        }

        if (hasLock && !lockOk)
            return false;

        // Manche Quests setzen zusätzlich (oder statt QuestLock) einen abgeschlossenen
        // Dungeon/Trial voraus.
        var hasInstanceLock = false;
        var instanceLockOk = false;
        foreach (var instanceRef in row.InstanceContent)
        {
            if (instanceRef.RowId == 0)
                continue;

            hasInstanceLock = true;
            if (instanceRef.ValueNullable is { } instanceRow && UnlockState.IsInstanceContentUnlocked(instanceRow))
            {
                instanceLockOk = true;
                break;
            }
        }

        if (hasInstanceLock && !instanceLockOk)
            return false;

        return !string.IsNullOrEmpty(row.Name.ToString());
    }

    private static List<uint>? globalQuestIdsCache;

    /// <summary>
    /// Alle Quests, die der Charakter aktuell (unabhängig von der Zone) annehmen könnte oder
    /// bereits abgeschlossen hat - für die globale Statistik (siehe MainWindow.DrawStatisticsPage).
    /// Anders als ComputeLiveZoneEntries wird hier NICHT nach Vergabeort gefiltert, sondern einmal
    /// über das komplette Quest-Sheet gegangen. Wird wie frameKitEntriesCache nur einmal pro
    /// Plugin-Sitzung berechnet (nicht jeden Frame) - neu erreichte Story-/Level-Fortschritte, die
    /// weitere Quests freischalten, tauchen erst nach einem Plugin-Neuladen in der Statistik auf.
    /// </summary>
    public unsafe List<uint> GetAllTrackedQuestIds()
    {
        if (globalQuestIdsCache != null)
            return globalQuestIdsCache;

        var result = new List<uint>();
        var questSheet = DataManager.GetExcelSheet<Quest>();
        var playerLevel = ObjectTable.LocalPlayer?.Level ?? 0;
        if (questSheet != null && playerLevel > 0)
        {
            foreach (var row in questSheet)
            {
                try
                {
                    if (IsQuestCurrentlyAcceptable(row, playerLevel))
                        result.Add(row.RowId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler bei Quest-Zeile {row.RowId} (globale Statistik)");
                }
            }
        }

        globalQuestIdsCache = result;
        return result;
    }

    private unsafe List<CollectibleEntry> ComputeLiveZoneEntries(uint territoryId)
    {
        var result = new List<CollectibleEntry>();

        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(territoryId, out var territoryRow))
            return result;

        // Manche Quests tragen als Vergabeort nur den generischen Stadtnamen (z.B. "Ul'dah",
        // PlaceName-ID 51) statt der spezifischen Unterzone ("Ul'dah - Steps of Thal"). Für die
        // großen "geteilten" Städte akzeptieren wir daher zusätzlich deren generische PlaceName-IDs.
        var acceptablePlaceNameIds = new HashSet<uint> { territoryRow.PlaceName.RowId };
        if (GenericCityPlaceNames.TryGetValue(territoryId, out var genericIds))
            acceptablePlaceNameIds.UnionWith(genericIds);

        var aetheryteSheet = DataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet != null)
        {
            // In geteilten Hauptstädten (Ul'dah, Limsa, Gridania, Ishgard) sollen Aetheryten aus
            // JEDEM Bezirk angezeigt werden, unabhängig davon, in welchem man gerade steht.
            var acceptableAetheryteTerritoryIds = SplitCityTerritories.TryGetValue(territoryId, out var siblingIds)
                ? siblingIds
                : new[] { territoryId };

            // Karten-Pin pro EINZELNEM Kristall (nicht pro Heimat-Zone!) - gecacht per (Zone,
            // DataType, DataKey), da derselbe Kristall in mehreren Bezirken einer geteilten
            // Hauptstadt auftauchen kann. Wichtig: NICHT (mehr) nur ein Pin pro Zone - Zonen mit
            // mehreren eigenständigen großen Aetheryten (z.B. Northern Thanalan: Camp Bluefog UND
            // Ceruleum Processing Plant) hätten sonst für BEIDE denselben (nur für den ersten
            // gefundenen Marker zutreffenden) Punkt bekommen, wodurch die Aetheryten-Automation
            // beim zweiten Kristall die Kartenflagge auf den ERSTEN setzt. MapId kommt bewusst NICHT
            // aus dem Pin-Lookup (der z.B. ohne passenden MapMarker-Eintrag scheitern kann), sondern
            // direkt von der jeweiligen Zone - sonst wäre ein Eintrag mit manuell hinterlegter
            // Koordinate (ManualAetherytePositions) trotzdem nicht klickbar, weil MapId 0 geblieben wäre.
            var pinCache = new Dictionary<(uint TerritoryId, byte DataType, uint DataKey), (uint MapId, float X, float Y)>();
            (uint MapId, float X, float Y) GetPinForAetheryte(uint homeTerritoryId, byte expectedDataType, uint expectedDataKey)
            {
                var cacheKey = (homeTerritoryId, expectedDataType, expectedDataKey);
                if (pinCache.TryGetValue(cacheKey, out var cached))
                    return cached;

                (uint MapId, float X, float Y) pin = (0, 0, 0);
                if (territorySheet.TryGetRow(homeTerritoryId, out var homeTerritory))
                {
                    var (pinX, pinY) = ResolveZoneAetherytePinPosition(homeTerritory, expectedDataType, expectedDataKey);
                    pin = (homeTerritory.Map.RowId, pinX, pinY);
                }

                pinCache[cacheKey] = pin;
                return pin;
            }

            foreach (var row in aetheryteSheet)
            {
                try
                {
                    if (!acceptableAetheryteTerritoryIds.Contains(row.Territory.RowId))
                        continue;

                    // "Unsichtbare" Einträge (z.B. "Gate of Nald"/"Gate of Thal" in Central
                    // Thanalan) sind reine Aethernetz-Menüpunkte für die Anreise von einer
                    // Nachbarzone aus - keine physisch auffindbaren Kristalle vor Ort und daher
                    // kein eigenständig freischaltbares Sammelobjekt.
                    if (row.Invisible)
                        continue;

                    // Große Aetheryten tragen ihren Namen in PlaceName, die kleinen Aethernetz-
                    // Kristalle (IsAetheryte == false) dagegen in AethernetName.
                    var name = row.IsAetheryte
                        ? row.PlaceName.ValueNullable?.Name.ToString()
                        : row.AethernetName.ValueNullable?.Name.ToString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var hasManual = ManualAetherytePositions.TryGetValue(row.RowId, out var manual);
                    byte expectedDataType = row.IsAetheryte ? (byte)3 : (byte)4;
                    var expectedDataKey = row.IsAetheryte ? row.RowId : row.AethernetName.RowId;
                    var homePin = GetPinForAetheryte(row.Territory.RowId, expectedDataType, expectedDataKey);
                    var mapX = hasManual ? manual.X : homePin.X;
                    var mapY = hasManual ? manual.Y : homePin.Y;
                    var mapId = hasManual ? manual.MapId ?? homePin.MapId : homePin.MapId;
                    // Die Flagge muss immer auf die tatsächliche Heimat-Zone des Kristalls zeigen,
                    // nicht auf die Zone, in der er gerade in der Liste steht (kann bei geteilten
                    // Städten voneinander abweichen) - außer ein manueller Override sagt was anderes.
                    var flagTerritoryId = hasManual ? manual.TerritoryId ?? row.Territory.RowId : row.Territory.RowId;

                    result.Add(new CollectibleEntry
                    {
                        Id = row.RowId,
                        Name = name,
                        Type = CollectibleType.Aetheryte,
                        Category = Loc.T("Aetheryte", "Aetheryte"),
                        TerritoryTypeId = territoryId,
                        MapId = mapId,
                        FlagTerritoryTypeId = flagTerritoryId,
                        VendorMapX = mapX,
                        VendorMapY = mapY,
                        Source = Loc.T("Aetheryte", "Aetheryte"),
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler bei Aetheryte-Zeile {row.RowId}");
                }
            }
        }

        var playerLevel = ObjectTable.LocalPlayer?.Level ?? 0;
        var questSheet = DataManager.GetExcelSheet<Quest>();

        // Für Quests ohne PlaceName (siehe unten) - wie bei den generischen Stadtnamen zählt in
        // geteilten Hauptstädten jeder Bezirk.
        var acceptableQuestIssuerTerritoryIds = SplitCityTerritories.TryGetValue(territoryId, out var questSiblingIds)
            ? questSiblingIds.ToHashSet()
            : new HashSet<uint> { territoryId };

        if (questSheet != null && playerLevel > 0)
        {
            foreach (var row in questSheet)
            {
                try
                {
                    // Freischalt-Quests (siehe IsQuestCurrentlyAcceptable) tragen oft KEINEN
                    // PlaceName (leer) - dann über die Zone des Vergabe-NPCs zuordnen.
                    var matchesPlace = acceptablePlaceNameIds.Contains(row.PlaceName.RowId)
                        || (string.IsNullOrEmpty(row.PlaceName.ValueNullable?.Name.ToString())
                            && acceptableQuestIssuerTerritoryIds.Contains(row.IssuerLocation.ValueNullable?.Territory.RowId ?? 0));
                    if (!matchesPlace)
                        continue;
                    if (!IsQuestCurrentlyAcceptable(row, playerLevel))
                        continue;

                    var name = row.Name.ToString();
                    var (mapId, issuerTerritoryId, mapX, mapY) = ResolveIssuerMapPosition(row);

                    result.Add(new CollectibleEntry
                    {
                        Id = row.RowId,
                        Name = name,
                        Type = CollectibleType.Quest,
                        Category = Loc.T("Quest", "Quest"),
                        TerritoryTypeId = territoryId,
                        MapId = mapId,
                        // Die Flagge muss immer auf die tatsächliche Vergabe-Zone zeigen, auch wenn
                        // der NPC in einem ANDEREN Bezirk derselben geteilten Hauptstadt steht als
                        // dem, in dem die Quest gerade in der Liste angezeigt wird.
                        FlagTerritoryTypeId = issuerTerritoryId != 0 ? issuerTerritoryId : null,
                        VendorMapX = mapX,
                        VendorMapY = mapY,
                        Source = Loc.T("Quest", "Quest"),
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler bei Quest-Zeile {row.RowId}");
                }
            }
        }

        // Sightseeing-Log-Einträge - global EINMAL aus dem ganzen "Adventure"-Sheet aufgebaut (siehe
        // GetSightseeingEntries), da die Zusatz-Bedingungen (v.a. "erste 20 A-Realm-Reborn-Punkte
        // nötig") die Position INNERHALB des GESAMTEN Sheets brauchen, nicht nur der aktuellen Zone -
        // hier nur nach Zone gefiltert. Genau wie bei Aetheryten oben sollen in geteilten Hauptstädten
        // (z.B. Limsa: "Barracuda Piers" liegt in den Upper, andere Punkte evtl. in den Lower Decks)
        // Punkte aus JEDEM Bezirk auftauchen, egal in welchem man gerade steht - sonst fehlt beim
        // Betreten des "falschen" Bezirks ein Teil der eigentlich schon erreichbaren Punkte.
        var acceptableSightseeingTerritoryIds = SplitCityTerritories.TryGetValue(territoryId, out var sightseeingSiblingIds)
            ? sightseeingSiblingIds
            : new[] { territoryId };
        result.AddRange(GetSightseeingEntries().Where(e => acceptableSightseeingTerritoryIds.Contains(e.TerritoryTypeId)));

        return result;
    }

    /// <summary>
    /// Löst NUR die X/Y-Position des Karten-Pins EINES BESTIMMTEN Aetheryten/Kristalls auf (nicht
    /// die MapId - die kommt separat direkt von der Zone, siehe Aufrufstelle). Zonen mit mehreren
    /// eigenständigen großen Aetheryten (z.B. Northern Thanalan: Camp Bluefog UND Ceruleum
    /// Processing Plant) haben auch mehrere DataType==3-Marker - früher wurde hier einfach der
    /// ERSTE gefundene für die ganze Zone übernommen, wodurch alle Aetheryten derselben Zone
    /// fälschlich denselben Pin (und damit dieselbe Kartenflagge) bekamen. Jetzt wird wie bei
    /// ResolveAetheryteWorldPosition exakt nach DataType+DataKey gefiltert (3/eigene RowId für
    /// große Aetheryten, 4/AethernetName-RowId für kleine Kristalle). Pixelkoordinaten werden über
    /// die dokumentierte Formel für Kartentextur-Pixel (nicht die Weltkoordinaten-Formel!) in
    /// Kartenkoordinaten umgerechnet: coord = pixel / sizeFactor * 2 + 1.
    /// </summary>
    private (float X, float Y) ResolveZoneAetherytePinPosition(TerritoryType territory, byte expectedDataType, uint expectedDataKey)
    {
        var map = territory.Map.ValueNullable;
        if (map == null || map.Value.RowId == 0)
            return (0, 0);

        var markerSheet = DataManager.GetSubrowExcelSheet<MapMarker>();
        if (markerSheet == null || !markerSheet.TryGetRow(map.Value.MapMarkerRange, out var markers))
            return (0, 0);

        foreach (var marker in markers)
        {
            if (marker.DataType != expectedDataType || marker.DataKey.RowId != expectedDataKey)
                continue;

            var x = marker.X / (float)map.Value.SizeFactor * 2f + 1f;
            var y = marker.Y / (float)map.Value.SizeFactor * 2f + 1f;
            return (x, y);
        }

        return (0, 0);
    }

    /// <summary>
    /// Von Hand nachgetragene Weltkoordinaten für die Aetheryten-Automation (vnavmesh) - für
    /// Kristalle, bei denen weder die aus dem MapMarker-Sheet berechnete Position noch die
    /// Karten-Flagge ("/vnav moveflag"-Mechanismus) einen begehbaren Punkt liefern (typischerweise
    /// Ladeninnenräume, deren Navmesh-Abdeckung lückenhaft ist). Am besten mit "/vnav moveto x y z"
    /// bzw. "/pos" im Spiel einen Punkt suchen, an dem der Charakter wirklich losläuft, und dessen
    /// Koordinaten hier eintragen.
    /// </summary>
    private static readonly Dictionary<uint, Vector3> ManualAetheryteWorldPositions = new()
    {
        [9] = new Vector3(-141.85756f, -3.1548882f, -166.5635f), // Ul'dah - Steps of Nald: Ul'dah Aetheryte Plaza (großer Aetheryte)
        [33] = new Vector3(63.403244f, 4.1000032f, -116.75167f), // Ul'dah - Steps of Nald: Adventurers' Guild
        [34] = new Vector3(-154.96025f, 14.004999f, 71.609276f), // Ul'dah - Steps of Nald: Thaumaturges' Guild
        [36] = new Vector3(31.53158f, 12.056557f, 112.36633f),   // Ul'dah - Steps of Thal: Miners' Guild
        [50] = new Vector3(-19.538807f, 14.075012f, 73.22114f),  // Ul'dah - Steps of Thal: Goldsmiths' Guild
        [35] = new Vector3(-53.17933f, 10f, 11.130668f),         // Ul'dah - Steps of Thal: Gladiators' Guild
        [37] = new Vector3(-98.96937f, 41f, 89.3127f),           // Ul'dah - Steps of Thal (Hustings Strip): Alchemists' Guild
        [47] = new Vector3(91.03113f, 12f, 59.573563f),          // Ul'dah - Steps of Thal: Weavers' Guild
        [51] = new Vector3(6.3589315f, 30f, -22.86465f),         // Ul'dah - Steps of Thal (Hustings Strip): The Chamber of Rule
        [125] = new Vector3(133.29056f, 4f, -31.510649f),        // Ul'dah - Steps of Thal: Sapphire Avenue Exchange
        [44] = new Vector3(-180.11044f, 4f, 181.18927f),         // Limsa Lominsa Lower Decks: Fishermen's Guild
        [43] = new Vector3(-335.9449f, 11.999161f, 54.79267f),   // Limsa Lominsa Lower Decks: Arcanists' Guild
        [49] = new Vector3(-212.68405f, 15.99901f, 50.275417f),  // Limsa Lominsa Lower Decks: Hawkers' Alley
        [8] = new Vector3(-88.223946f, 18.900326f, 1.9207064f),  // Limsa Lominsa Lower Decks: Aetheryte Plaza (großer Aetheryte)
        [42] = new Vector3(-58.275673f, 42f, -131.06003f),       // Limsa Lominsa Upper Decks: Culinarians' Guild
        [48] = new Vector3(-3.3218017f, 43.999992f, -217.23248f), // Limsa Lominsa Upper Decks: Marauders' Guild
        [41] = new Vector3(14.184732f, 40f, 70.58942f),          // Limsa Lominsa Upper Decks: The Aftcastle
        [62] = new Vector3(0.024659282f, 1.0425026f, -4.0411267f),   // The Gold Saucer: Gold Saucer Aetheryte Plaza (großer Aetheryte)
        [63] = new Vector3(-63.253986f, 0.044274688f, 52.463417f),   // The Gold Saucer: Entrance & Card Squares
        [64] = new Vector3(58.626476f, 20.99973f, 61.28264f),        // The Gold Saucer: Wonder Square East
        [65] = new Vector3(0.7764596f, 20.999727f, 59.995113f),      // The Gold Saucer: Wonder Square West
        [66] = new Vector3(93.61656f, -5.0000005f, -70.9733f),       // The Gold Saucer: Event Square
        [67] = new Vector3(113.93691f, 12.999999f, -39.78969f),      // The Gold Saucer: Cactpot Board
        [68] = new Vector3(-24.051819f, 3.273533f, -83.88178f),      // The Gold Saucer: Round Square
        [69] = new Vector3(-14.04731f, -5.9604645e-07f, -34.106762f), // Chocobo Square: Chocobo Square
        [89] = new Vector3(50.673454f, -2.026558e-06f, 21.841373f),   // Chocobo Square: Minion Square
        [2] = new Vector3(28.862434f, 2.010144f, 30.561651f),         // New Gridania: Gridania Aetheryte Plaza (großer Aetheryte)
        [25] = new Vector3(164.4172f, -2.3711808f, 85.57306f),        // New Gridania: Archers' Guild
        [26] = new Vector3(102.793236f, 8.593217f, -110.11618f),      // New Gridania: Leatherworkers' Guild & Shaded Bower
        [27] = new Vector3(119.54223f, 11.556623f, -230.58084f),      // Old Gridania: Lancers' Guild
        [28] = new Vector3(-146.36765f, 4f, -13.672467f),             // New Gridania: Conjurers' Guild
        [29] = new Vector3(-309.5592f, 7.0605545f, -176.70325f),      // Old Gridania: Botanists' Guild
        [30] = new Vector3(-71.352844f, 7.267789f, -139.29424f),      // Old Gridania: Mih Khetto's Amphitheatre
        [70] = new Vector3(-66.24398f, 8.113304f, 49.884026f),        // Foundation: Ishgard Aetheryte Plaza (großer Aetheryte)
        [80] = new Vector3(47.78436f, 23.979128f, -0.61288136f),      // Foundation: The Forgotten Knight
        [81] = new Vector3(-106.0554f, 15.140585f, -31.97764f),       // Foundation: Skysteel Manufactory
        [82] = new Vector3(48.699932f, -12.020877f, 68.83059f),       // Foundation: The Brume
        [83] = new Vector3(135.3319f, -9.234926f, -63.408485f),       // The Pillars: Athenaeum Astrologicum
        [84] = new Vector3(-136.28429f, -12.634914f, -17.067507f),    // The Pillars: The Jeweled Crozier
        [85] = new Vector3(-79.4322f, 10.054904f, -124.68877f),       // The Pillars: Saint Reymanaud's Cathedral
        [86] = new Vector3(79.41425f, 10.054904f, -124.84091f),       // The Pillars: The Tribunal
        [87] = new Vector3(-0.48842534f, 15.965048f, -34.27581f),     // The Pillars: The Last Vigil
        [75] = new Vector3(71.294464f, 209.25f, -15.635567f),         // Idyllshire: Idyllshire Aetheryte Plaza (großer Aetheryte)
        [90] = new Vector3(-74.0923f, 209.4413f, -22.290205f),        // Idyllshire: West Idyllshire
        [111] = new Vector3(45.558308f, 4.199996f, -39.884335f),      // Kugane: Kugane Aetheryte Plaza (großer Aetheryte)
        [112] = new Vector3(-75.36073f, -6.999999f, -77.26171f),      // Kugane: Shiokaze Hostelry
        [113] = new Vector3(-113.89092f, -5.005731f, 153.13283f),     // Kugane: Pier #1
        [114] = new Vector3(28.635242f, 8f, 143.1382f),               // Kugane: Thavnairian Consulate
        [115] = new Vector3(26.535408f, 4.000001f, 71.60172f),        // Kugane: Kogane Dori Markets
        [116] = new Vector3(-76.326256f, 18f, -163.22801f),           // Kugane: Bokairo Inn
        [117] = new Vector3(130.46959f, 12.000001f, 83.20998f),       // Kugane: The Ruby Bazaar
        [118] = new Vector3(119.33526f, 11.999337f, -90.61815f),      // Kugane: Sekiseigumi Barracks
        [119] = new Vector3(26.013487f, 5.9916945f, -151.63597f),     // Kugane: Rakuza District
        [104] = new Vector3(82.44814f, 0.029867768f, 98.40538f),      // Rhalgr's Reach: Rhalgr's Reach Aetheryte Plaza (großer Aetheryte)
        [121] = new Vector3(-82.69922f, 0f, 8.892529f),                // Rhalgr's Reach: Western Rhalgr's Reach
        [122] = new Vector3(101.2953f, 2.90765f, -112.726654f),        // Rhalgr's Reach: Northeastern Rhalgr's Reach
        [127] = new Vector3(40.730907f, 1.3328607f, -12.541619f),      // The Doman Enclave: Doman Enclave Aetheryte Plaza (großer Aetheryte)
        [129] = new Vector3(11.176858f, -1.1920929e-07f, -104.29073f), // The Doman Enclave: The Northern Enclave
        [130] = new Vector3(-60.81499f, 0f, 88.50076f),                // The Doman Enclave: The Southern Enclave
        [162] = new Vector3(98.28721f, -4.1787133f, 79.60301f),        // The Doman Enclave: Ferry Docks
        [133] = new Vector3(-62.953644f, 2.921092f, -4.317079f),       // The Crystarium: The Crystarium Aetheryte Plaza (großer Aetheryte)
        [149] = new Vector3(-6.015611f, -7.7036285f, 146.8549f),       // The Crystarium: Musica Universalis Markets
        [150] = new Vector3(-108.76381f, 0f, -59.562263f),             // The Crystarium: Temenos Rookery
        [151] = new Vector3(63.53169f, -2.3841858e-07f, -17.810028f),  // The Crystarium: The Dossal Gate
        [152] = new Vector3(35.869473f, 0f, 220.61555f),               // The Crystarium: The Pendants
        [153] = new Vector3(67.03844f, 35.999683f, -132.53917f),       // The Crystarium: The Amaro Launch
        [154] = new Vector3(-50.98299f, 19.999794f, -172.28f),         // The Crystarium: The Crystalline Mean
        [155] = new Vector3(-55.08784f, -37.7f, -239.60663f),          // The Crystarium: The Cabinet of Curiosity
        [134] = new Vector3(-0.29844308f, 82f, -3.4165506f),           // Eulmore: Eulmore Aetheryte Plaza (großer Aetheryte)
        [135] = new Vector3(70.43707f, -10.349164f, 65.53914f),        // Eulmore: Southeast Derelicts
        [157] = new Vector3(11.119778f, 36f, -5.7763677f),             // Eulmore: The Mainstay
        [158] = new Vector3(-55.931984f, -0.82081413f, 52.275536f),    // Eulmore: Nightsoil Pots
        [159] = new Vector3(4.954765f, 5.9452057f, -57.317192f),       // Eulmore: The Glory Gate
        [182] = new Vector3(-0.828484f, 3.2749999f, -3.4330347f),      // Old Sharlayan: Old Sharlayan Aetheryte Plaza (großer Aetheryte)
        [184] = new Vector3(-289.31375f, 20.013304f, -74.640045f),     // Old Sharlayan: The Studium
        [185] = new Vector3(-90.862885f, 2.1191945f, 31.47671f),       // Old Sharlayan: The Baldesion Annex
        [186] = new Vector3(-38.64221f, 41.375996f, -158.23409f),      // Old Sharlayan: The Rostra
        [187] = new Vector3(206.5285f, 21.828178f, -119.5187f),        // Old Sharlayan: The Leveilleur Estate
        [188] = new Vector3(207.76955f, 1.8613925f, 15.380046f),       // Old Sharlayan: Journey's End
        [189] = new Vector3(14.47839f, -16.247f, 127.66104f),          // Old Sharlayan: Scholar's Harbor
        [183] = new Vector3(29.529707f, 0.8999984f, -27.122984f),      // Radz-at-Han: Radz-at-Han Aetheryte Plaza (großer Aetheryte)
        [191] = new Vector3(-366.71188f, 45.001728f, -29.534893f),     // Radz-at-Han: Meghaduta
        [192] = new Vector3(-158.65683f, 36f, 28.179693f),             // Radz-at-Han: Ruveydah Fibers
        [193] = new Vector3(-144.93687f, 27.999994f, 200.03326f),      // Radz-at-Han: Airship Landing
        [194] = new Vector3(8.2280855f, -2.0000007f, 109.46653f),      // Radz-at-Han: Alzadaal's Peace
        [195] = new Vector3(-139.59232f, 3.9997995f, -96.95679f),      // Radz-at-Han: Hall of the Radiant Host
        [196] = new Vector3(-44.230145f, 4.7683716e-07f, -197.55643f), // Radz-at-Han: Mehryde's Meyhane
        [198] = new Vector3(131.40958f, 26.99999f, 14.134306f),        // Radz-at-Han: Kama
        [199] = new Vector3(58.984554f, -24.693443f, -211.90494f),     // Radz-at-Han: The High Crucible of Al-Kimiya
        [216] = new Vector3(-26.05755f, 0.5f, 2.3995228f),             // Tuliyollal: Tuliyollal Aetheryte Plaza (großer Aetheryte)
        [218] = new Vector3(-415.8316f, 3.0000002f, -46.42874f),       // Tuliyollal: Dirigible Landing
        [219] = new Vector3(-185.3738f, 39.94251f, 4.928123f),         // Tuliyollal: The Resplendent Quarter
        [220] = new Vector3(-148.82771f, -14.999287f, 196.75035f),     // Tuliyollal: The For'ard Cabins
        [221] = new Vector3(-14.250872f, -10.00001f, 137.96169f),      // Tuliyollal: Bayside Bevy Marketplace
        [222] = new Vector3(-97.63969f, 100.75f, -220.9536f),          // Tuliyollal: Vollok Shoonsa
        [223] = new Vector3(165.9286f, -17.9643f, 37.00588f),          // Tuliyollal: Wachumeqimeqi
        [224] = new Vector3(70.14987f, 46.999996f, -331.98785f),       // Tuliyollal: Brightploom Post
        [217] = new Vector3(0.030465692f, 8.442986f, -8.074915f),      // Solution Nine: Solution Nine Aetheryte Plaza (großer Aetheryte)
        [230] = new Vector3(-30.557829f, -6.050003f, 211.05759f),      // Solution Nine: Information Center
        [231] = new Vector3(382.00623f, 60f, 74.703575f),              // Solution Nine: True Vue
        [232] = new Vector3(259.9351f, 50.75f, 147.13205f),            // Solution Nine: Neon Stein
        [233] = new Vector3(372.3516f, 60.124996f, 326.0636f),         // Solution Nine: The Arcadion
        [234] = new Vector3(-30.803701f, 38.0566f, -343.41434f),       // Solution Nine: Resolution
        [235] = new Vector3(-159.09541f, 6.4373016e-06f, 23.499605f),  // Solution Nine: Nexus Arcade
        [236] = new Vector3(-376.37665f, 14.030001f, 137.58334f),      // Solution Nine: Residential Sector
    };

    /// <summary>
    /// Ob für diesen Aetheryten eine von Hand geprüfte Weltposition hinterlegt ist - wenn ja, soll
    /// die Automation ihr direkt vertrauen (kein FlagToPoint-/PointOnFloor-Umweg mehr nötig, siehe
    /// AetheryteAutomation.StartMovingTo).
    /// </summary>
    public static bool HasManualAetheryteWorldPosition(uint aetheryteId) => ManualAetheryteWorldPositions.ContainsKey(aetheryteId);

    /// <summary>
    /// Löst die reale WELT-Position (X/Z, keine Kartenkoordinate) eines Aetheryten/Aethernetz-
    /// Kristalls auf - für die Aetheryten-Automation (vnavmesh braucht Weltkoordinaten zum Laufen).
    /// Bewusst NICHT über die Objekttabelle: kleine Kristalle werden dort erst innerhalb von ca.
    /// 15 Yalm geladen, man braucht die Position aber VOR der Ankunft, um überhaupt dorthin laufen
    /// zu können (siehe AetheryteAutomation.cs). Quelle ist wieder das "MapMarker"-Sheet, das jeder
    /// Aetheryte-Zeile eigene Marker zuordnet: DataType 3 = großer Aetheryte (DataKey = eigene
    /// RowId), DataType 4 = Aethernetz-Kristall (DataKey = AethernetName-PlaceName-RowId). Die
    /// Pixelkoordinaten werden hier über die WELTKOORDINATEN-Formel zurückgerechnet (anders als bei
    /// ResolveZoneAetherytePinPosition, das die Kartenanzeige-Formel für den Klick-Pin nutzt):
    /// world = (pixel - 1024) * 100 / sizeFactor - offset. Etabliert durch das Fremdplugin
    /// "Lifestream", das genau so vorgeht.
    /// </summary>
    public static unsafe Vector3? ResolveAetheryteWorldPosition(uint aetheryteId)
    {
        if (ManualAetheryteWorldPositions.TryGetValue(aetheryteId, out var manual))
            return manual;

        var aetheryteSheet = DataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null || !aetheryteSheet.TryGetRow(aetheryteId, out var aetheryte))
            return null;

        var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        var markerSheet = DataManager.GetSubrowExcelSheet<MapMarker>();
        if (mapSheet == null || markerSheet == null)
            return null;

        var expectedDataType = aetheryte.IsAetheryte ? 3 : 4;
        var expectedDataKey = aetheryte.IsAetheryte ? aetheryte.RowId : aetheryte.AethernetName.RowId;

        // Nicht nur die "Haupt"-Karte der Zone durchsuchen, sondern JEDE Karte, die zu dieser
        // TerritoryType gehört - manche Kristalle (z.B. in Ul'dah - Steps of Thal: Alchemists'
        // Guild, The Chamber of Rule) haben ihren Marker nur auf einer Unterkarte wie "Hustings
        // Strip", nicht auf der Hauptkarte selbst.
        foreach (var map in mapSheet)
        {
            if (map.TerritoryType.RowId != aetheryte.Territory.RowId)
                continue;

            if (!markerSheet.TryGetRow(map.MapMarkerRange, out var markers))
                continue;

            foreach (var marker in markers)
            {
                if (marker.DataType != expectedDataType || marker.DataKey.RowId != expectedDataKey)
                    continue;

                var worldX = (marker.X - 1024f) * 100f / map.SizeFactor - map.OffsetX;
                var worldZ = (marker.Y - 1024f) * 100f / map.SizeFactor - map.OffsetY;
                return new Vector3(worldX, 0, worldZ);
            }
        }

        return null;
    }

    /// <summary>
    /// Findet den bereits freigeschalteten "großen" Aetheryten einer Zone (IsAetheryte == true) -
    /// für die Aetheryten-Automation, um per Lifestream (Teleport-Aktion) in einen anderen Bezirk
    /// derselben geteilten Hauptstadt zu reisen (siehe AetheryteAutomation.cs). Null, wenn keiner
    /// existiert/noch nicht freigeschaltet ist. Manche geteilten Hauptstädte (z.B. Ul'dah) haben
    /// aber gar keinen eigenen großen Aetheryten pro Bezirk (nur einen für die ganze Stadt,
    /// physisch in nur einem Bezirk) - dafür siehe FindAnyUnlockedAetheryteInTerritory.
    /// </summary>
    public static unsafe uint? FindUnlockedMainAetheryteId(uint territoryId)
    {
        var aetheryteSheet = DataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return null;

        foreach (var row in aetheryteSheet)
        {
            if (row.Territory.RowId == territoryId && row.IsAetheryte && IsAetheryteUnlocked(row.RowId))
                return row.RowId;
        }

        return null;
    }

    /// <summary>
    /// Findet IRGENDEINEN bereits freigeschalteten Aetheryten/Aethernetz-Kristall einer Zone (groß
    /// oder klein, Hauptsache schon freigeschaltet) - für die Aetheryten-Automation, um per
    /// Lifestreams Aethernetz-Sprung (nicht die Teleport-Aktion) in einen anderen Bezirk derselben
    /// geteilten Hauptstadt zu reisen. Das funktioniert auch dort, wo es (wie in Ul'dah) gar keinen
    /// eigenen großen Aetheryten pro Bezirk gibt, solange man von einem Aethernetz-Punkt in
    /// Reichweite aus springt und im Ziel-Bezirk IRGENDEIN Punkt schon bekannt ist.
    /// </summary>
    public static unsafe uint? FindAnyUnlockedAetheryteInTerritory(uint territoryId)
    {
        var aetheryteSheet = DataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return null;

        foreach (var row in aetheryteSheet)
        {
            if (row.Territory.RowId == territoryId && IsAetheryteUnlocked(row.RowId))
                return row.RowId;
        }

        return null;
    }

    /// <summary>
    /// Nächstgelegener bereits freigeschalteter Aetheryte/Aethernetz-Kristall EINER Zone (nicht der
    /// ganzen geteilten Hauptstadt, siehe FlagTerritoryTypeId) zu einer rohen Weltposition IN
    /// DERSELBEN Zone - über Kartenkoordinaten-Distanz als Näherung (funktioniert daher auch für
    /// eine Zone, in der man sich gerade gar nicht befindet, anders als eine vnavmesh-Abfrage, die
    /// nur die aktuell geladene Zone kennt). Für SightseeingAutomation: vor einem Lifestream-
    /// Aethernetz-Sprung zum nächsten Kristall im AKTUELLEN Bezirk laufen (Lifestream springt nur
    /// aus der Reichweite eines Aethernetz-Punkts heraus, nicht von einer beliebigen Position) UND
    /// im ZIEL-Bezirk den zum eigentlichen Sightseeing-Punkt nächstgelegenen Kristall als
    /// Sprungziel wählen (statt "irgendeinen", der unnötig weit weg liegen kann).
    /// </summary>
    public static CollectibleEntry? FindNearestUnlockedAetheryteInZone(uint territoryId, Vector3 referenceWorldPosition)
    {
        var candidates = instance.GetLiveZoneEntries(ResolveEffectiveTerritoryId(territoryId))
            .Where(e => e.Type == CollectibleType.Aetheryte
                        && (e.FlagTerritoryTypeId ?? e.TerritoryTypeId) == territoryId
                        && IsAetheryteUnlocked(e.Id))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        var mapId = territorySheet != null && territorySheet.TryGetRow(territoryId, out var territory) ? territory.Map.RowId : 0u;
        var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        if (mapId == 0 || mapSheet == null || !mapSheet.TryGetRow(mapId, out var map))
            return candidates[0];

        var referenceMapCoords = Dalamud.Utility.MapUtil.WorldToMap(
            new Vector2(referenceWorldPosition.X, referenceWorldPosition.Z), (int)map.OffsetX, (int)map.OffsetY, (uint)map.SizeFactor);

        return candidates
            .OrderBy(e => Vector2.DistanceSquared(new Vector2(e.VendorMapX, e.VendorMapY), new Vector2(referenceMapCoords.X, referenceMapCoords.Y)))
            .First();
    }

    /// <summary>
    /// Öffentlicher Zugriff auf die geteilten-Hauptstadt-Bezirksgruppe einer Zone (siehe
    /// SplitCityTerritories) - für die Aetheryten-Automation.
    /// </summary>
    public static IReadOnlyList<uint> GetSplitCityTerritories(uint territoryId) =>
        SplitCityTerritories.TryGetValue(territoryId, out var siblingIds) ? siblingIds : new[] { territoryId };

    /// <summary>
    /// Löst die Kartenposition des Quest-Vergabe-NPCs live auf: Quest → "Level"-Sheet (rohe
    /// Weltkoordinaten des NPCs) → über "Map" (Skalierung/Offset) per Dalamuds MapUtil in
    /// "schöne" Kartenkoordinaten umgerechnet, exakt wie der eingebaute Quest-Tracker es tut.
    /// Liefert außerdem die TerritoryTypeId der tatsächlichen Vergabe-Zone (FlagTerritoryTypeId) -
    /// wichtig bei geteilten Hauptstädten, wo der NPC in einem ANDEREN Bezirk stehen kann als dem,
    /// in dem die Quest gerade in der Liste angezeigt wird (siehe ComputeLiveZoneEntries).
    /// </summary>
    private static (uint MapId, uint IssuerTerritoryId, float X, float Y) ResolveIssuerMapPosition(Quest quest)
    {
        var level = quest.IssuerLocation.ValueNullable;
        if (level == null)
            return (0, 0, 0, 0);

        var map = level.Value.Map.ValueNullable;
        if (map == null || map.Value.RowId == 0)
            return (0, 0, 0, 0);

        var mapCoords = MapUtil.WorldToMap(
            new Vector3(level.Value.X, level.Value.Y, level.Value.Z),
            map.Value.OffsetX,
            map.Value.OffsetY,
            0,
            (uint)map.Value.SizeFactor);

        return (map.Value.RowId, map.Value.TerritoryType.RowId, mapCoords.X, mapCoords.Y);
    }

    /// <summary>
    /// Liest den menschenlesbaren Zonennamen aus dem Lumina "TerritoryType"-Sheet.
    /// </summary>
    public static string GetZoneName(uint territoryTypeId)
    {
        var sheet = DataManager.GetExcelSheet<TerritoryType>();
        if (sheet != null && sheet.TryGetRow(territoryTypeId, out var row))
        {
            var name = row.PlaceName.Value.Name.ToString();
            if (!string.IsNullOrEmpty(name))
                return name;
        }

        return "Unbekannt";
    }

    /// <summary>
    /// Liefert für jedes sichtbare, echte native Spielfenster (z.B. Währungs-, Inventar- oder
    /// Charakterfenster - erkannt über AtkUnitBase.WindowNode != null, das nur bei tatsächlich
    /// beweglichen Fenstern mit Titelleiste gesetzt ist, nicht bei fest verankerten HUD-Elementen
    /// wie Aktionsleisten), das den übergebenen Bildschirmbereich überlappt, das jeweilige
    /// Überlappungsrechteck (auf min/max dieses Bereichs begrenzt). Dalamud/ImGui zeichnet
    /// grundsätzlich IMMER nach (also über) dem nativen Spiel-UI in einem einzigen Rendering-
    /// Durchgang - es gibt keine echte Z-Order zwischen beiden. CompactOverlayWindow nutzt die
    /// zurückgegebenen Rechtecke deshalb, um dort gezielt (a) per ImGuiP.SetWindowHitTestHole
    /// Mausklicks ans native Fenster durchzureichen und (b) nur die betroffenen Inhaltszeilen
    /// unsichtbar zu machen, statt (wie früher) das gesamte Overlay bei jeder noch so kleinen
    /// Überlappung komplett auszublenden.
    /// </summary>
    public static unsafe List<(Vector2 Min, Vector2 Max)> GetOverlappingNativeWindowRects(Vector2 min, Vector2 max)
    {
        var result = new List<(Vector2 Min, Vector2 Max)>();
        var unitManager = RaptureAtkUnitManager.Instance();
        if (unitManager == null)
            return result;

        var list = unitManager->AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null || !unit->IsVisible || unit->WindowNode == null)
                continue;

            var unitMin = new Vector2(unit->X, unit->Y);
            var unitMax = unitMin + new Vector2(unit->GetScaledWidth(true), unit->GetScaledHeight(true));

            var overlapMin = new Vector2(System.Math.Max(unitMin.X, min.X), System.Math.Max(unitMin.Y, min.Y));
            var overlapMax = new Vector2(System.Math.Min(unitMax.X, max.X), System.Math.Min(unitMax.Y, max.Y));
            if (overlapMin.X < overlapMax.X && overlapMin.Y < overlapMax.Y)
                result.Add((overlapMin, overlapMax));
        }

        return result;
    }

    /// <summary>
    /// Blendet sofort das native Karten-Fenster ("AreaMap") aus, das GameGui.OpenMapWithMapLink als
    /// Nebeneffekt immer mit öffnet - für Automationen, die die Karten-Flagge nur intern setzen, um
    /// über vnavmesh FlagToPoint einen begehbaren Punkt zu bekommen (siehe OpenVendorMap/
    /// OpenEntryMap mit showMapWindow=false), und die Karte dabei NICHT sichtbar öffnen sollen.
    /// Setzt nur die Sichtbarkeits-Flag direkt (kein Close/Hide mit Callback), damit die zuvor
    /// gesetzte Flaggen-Position selbst unangetastet bleibt - dieselbe Technik wie bei
    /// SuppressQuestionableWindow, nur für ein natives ATK-Addon statt ein ImGui-Fenster.
    /// </summary>
    private static unsafe void SuppressMapWindow()
    {
        var addon = (AtkUnitBase*)GameGui.GetAddonByName("AreaMap").Address;
        if (addon != null)
            addon->IsVisible = false;
    }

    /// <summary>
    /// Rechnet eine Kartenkoordinate (wie bei CollectibleEntry.VendorMapX/Y) in eine grobe rohe
    /// Weltposition um (Umkehrung von MapUtil.WorldToMap, Y unbekannt/immer 0) - für Fälle ohne
    /// vnavmesh-Abfrage verfügbar (andere Zone als aktuell geladen, o.ä.), z.B. um die Entfernung
    /// eines Eintrags zum Spieler grob abzuschätzen (siehe QuestAutomation.TryStartNext).
    /// </summary>
    public static Vector3? ResolveWorldPositionFromMapCoords(uint mapId, float mapX, float mapY)
    {
        var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        if (mapSheet == null || !mapSheet.TryGetRow(mapId, out var map) || map.SizeFactor == 0)
            return null;

        var worldX = (mapX - 1f) * 50f - 102400f / map.SizeFactor - map.OffsetX;
        var worldZ = (mapY - 1f) * 50f - 102400f / map.SizeFactor - map.OffsetY;
        return new Vector3(worldX, 0, worldZ);
    }

    /// <summary>
    /// Öffnet die Ingame-Karte mit einer Flagge auf der Händler-Position des Eintrags. Mit
    /// showMapWindow=false wird die Flagge zwar gesetzt (z.B. für vnavmesh FlagToPoint), das dabei
    /// von GameGui.OpenMapWithMapLink automatisch mit geöffnete Karten-Fenster aber sofort wieder
    /// unterdrückt (siehe SuppressMapWindow) - für Automationen, die die Karte nur intern als
    /// Positions-Trick brauchen, nicht weil der Spieler wirklich einen Kartenlink angeklickt hat.
    /// </summary>
    public static void OpenVendorMap(CollectibleEntry entry, bool showMapWindow = true)
    {
        if (!entry.HasVendorLocation)
            return;

        var territoryForFlag = entry.FlagTerritoryTypeId ?? entry.TerritoryTypeId;
        var payload = new MapLinkPayload(territoryForFlag, entry.MapId, entry.VendorMapX, entry.VendorMapY);
        GameGui.OpenMapWithMapLink(payload);
        if (!showMapWindow)
            SuppressMapWindow();

        // Für den Wegweiser-Pfeil (siehe NavigationTargetPosition) - bevorzugt über dieselbe vnavmesh-
        // IPC aufgelöst, die die Automationen auch fürs tatsächliche Laufen benutzen (steht nur zur
        // Verfügung, wenn man gerade in genau dieser Zone steht, da vnavmesh die Flagge gegen das
        // aktuell geladene Navmesh auflöst) - das zeigt garantiert exakt denselben Punkt, den die
        // Automation ansteuern würde. Sonst Fallback auf ResolveWorldPositionFromMapCoords.
        Vector3? navigationWorldPosition = null;
        if (ClientState.TerritoryType == territoryForFlag && navigationFlagToPointQuery is { HasFunction: true } query)
        {
            try { navigationWorldPosition = query.InvokeFunc(); }
            catch { /* vnavmesh nicht bereit - siehe Fallback unten */ }
        }

        navigationWorldPosition ??= ResolveWorldPositionFromMapCoords(entry.MapId, entry.VendorMapX, entry.VendorMapY);

        if (navigationWorldPosition.HasValue)
            SetNavigationTarget(navigationWorldPosition.Value, entry.Name, territoryForFlag);
    }

    /// <summary>
    /// Wie OpenVendorMap, aber auch für Einträge mit roher Weltposition statt Kartenkoordinate
    /// (aktuell nur Hunting-Log-Monster, siehe WorldPosition) - dafür wird die Weltposition mit
    /// Dalamuds MapUtil.WorldToMap live in eine Kartenkoordinate umgerechnet (dieselbe Formel, mit
    /// der auch das Spiel selbst Weltkoordinaten auf der Karte anzeigt), statt sie separat
    /// vorzuberechnen und zu speichern. Ohne bekannte Position (weder Kartenkoordinate noch
    /// Weltposition) passiert nichts - das Icon dafür wird dann ohnehin nicht angezeigt.
    /// </summary>
    public static void OpenEntryMap(CollectibleEntry entry, bool showMapWindow = true)
    {
        if (entry.HasVendorLocation)
        {
            OpenVendorMap(entry, showMapWindow);
            return;
        }

        if (entry.WorldPosition is not { } worldPosition || entry.MapId == 0)
            return;

        var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        if (mapSheet == null || !mapSheet.TryGetRow(entry.MapId, out var map))
            return;

        var mapCoords = Dalamud.Utility.MapUtil.WorldToMap(
            new Vector2(worldPosition.X, worldPosition.Z), (int)map.OffsetX, (int)map.OffsetY, (uint)map.SizeFactor);

        var territoryForFlag = entry.FlagTerritoryTypeId ?? entry.TerritoryTypeId;
        var payload = new MapLinkPayload(territoryForFlag, entry.MapId, mapCoords.X, mapCoords.Y);
        GameGui.OpenMapWithMapLink(payload);
        if (!showMapWindow)
            SuppressMapWindow();

        SetNavigationTarget(worldPosition, entry.Name, territoryForFlag);
    }

    // Aktuelles Ziel des Wegweiser-Pfeils (siehe Windows/NavigationArrowWindow.cs) - gesetzt beim
    // Start einer Automation, beim Klick aufs "Hinlaufen"-Icon (GoToAutomation) oder beim Öffnen
    // eines Karten-Links (siehe OpenVendorMap/OpenEntryMap), zurückgesetzt beim Ankommen (siehe
    // NavigationArrowWindow) oder per Rechtsklick auf den Pfeil.
    public static Vector3? NavigationTargetPosition { get; private set; }
    public static string NavigationTargetName { get; private set; } = string.Empty;
    public static uint NavigationTargetTerritoryId { get; private set; }

    public static void SetNavigationTarget(Vector3 worldPosition, string name, uint territoryId)
    {
        NavigationTargetPosition = worldPosition;
        NavigationTargetName = name;
        NavigationTargetTerritoryId = territoryId;
    }

    public static void ClearNavigationTarget()
    {
        NavigationTargetPosition = null;
        NavigationTargetName = string.Empty;
    }

    /// <summary>
    /// Setzt ein Weltobjekt als aktuelles Ziel - Voraussetzung für InteractWithGameObject unten.
    /// Muss (mindestens) einen Frame VOR dem eigentlichen Interact-Aufruf passiert sein, damit das
    /// Ziel im Spiel tatsächlich angewendet wurde.
    /// </summary>
    public static void SetTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
    {
        TargetManager.Target = gameObject;
    }

    public static bool IsCurrentTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
    {
        return TargetManager.Target?.Address == gameObject.Address;
    }

    /// <summary>
    /// Interagiert mit einem Weltobjekt (z.B. einem Aetheryten/Aethernetz-Kristall), so wie ein
    /// Spieler-Klick es auch tun würde - für die Aetheryten-Automation (siehe AetheryteAutomation.cs).
    /// Muss bereits als aktuelles Ziel gesetzt sein (siehe SetTarget). checkLineOfSight bewusst
    /// false - sonst blockiert oft schon das Kristall-Sockel-Mesh selbst die Sichtlinienprüfung und
    /// die Interaktion tut lautlos gar nichts.
    /// </summary>
    public static unsafe void InteractWithGameObject(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
    {
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address;
        if (native == null)
            return;

        TargetSystem.Instance()->InteractWithObject(native, false);
    }

    /// <summary>
    /// Sucht das nächstgelegene lebende Weltobjekt mit dieser BNpcName-RowId (siehe
    /// CollectibleEntry.BNpcNameId) - für die Hunting-Log-Kill-Automation, um das tatsächliche
    /// Monster zum Anvisieren zu finden. Anders als bei Aetheryten/Kristallen (die immer geladen
    /// sind, sobald man in Reichweite steht) können mehrere Exemplare gleichzeitig existieren oder
    /// gerade keins - deshalb wird hier IMMER live gesucht, nichts gecacht.
    /// </summary>
    public static Dalamud.Game.ClientState.Objects.Types.IBattleNpc? FindNearestLiveMonster(uint bNpcNameId, Vector3 nearPosition, float maxDistance)
    {
        Dalamud.Game.ClientState.Objects.Types.IBattleNpc? nearest = null;
        var bestDistance = maxDistance;

        foreach (var obj in ObjectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc battleNpc)
                continue;

            if (battleNpc.NameId != bNpcNameId || !battleNpc.IsTargetable || battleNpc.CurrentHp == 0)
                continue;

            var distance = Vector3.Distance(battleNpc.Position, nearPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = battleNpc;
            }
        }

        return nearest;
    }

    // Siehe HasPathStartGraceElapsed.
    private static ICallGateSubscriber<bool>? vnavPathfindInProgress;
    private static DateTime lastVnavPathfindInProgressAt = DateTime.MinValue;
    private static readonly TimeSpan VnavPathfindMaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ob vnavmesh einen per SimpleMove.PathfindAndMoveCloseTo angenommenen Auftrag gerade noch
    /// berechnet - währenddessen ist Path.IsRunning noch false. Merkt sich den Zeitpunkt für
    /// HasPathStartGraceElapsed.
    /// </summary>
    public static bool IsVnavPathfindInProgress()
    {
        try
        {
            var computing = vnavPathfindInProgress is { HasFunction: true } && vnavPathfindInProgress.InvokeFunc();
            if (computing)
                lastVnavPathfindInProgressAt = DateTime.UtcNow;

            return computing;
        }
        catch
        {
            return false; // vnavmesh fehlt oder eine Version ohne diesen Endpunkt
        }
    }

    /// <summary>
    /// Für die "Laufweg nie gestartet"-Prüfung aller Automationen (jeweils PathStartGracePeriod):
    /// true, sobald seit startedAt (Betreten des Lauf-Zustands) die Gnadenfrist verstrichen ist,
    /// OHNE die Zeit mitzuzählen, in der vnavmesh den Weg noch berechnet hat - lange (v.a.
    /// fliegende) Wege brauchen dafür deutlich länger als die üblichen 5s und wurden sonst
    /// fälschlich übersprungen, obwohl vnavmesh sie angenommen hatte. Spätestens nach
    /// VnavPathfindMaxDuration zählt auch eine noch laufende Berechnung als "nie gestartet".
    /// </summary>
    public static bool HasPathStartGraceElapsed(DateTime startedAt, TimeSpan gracePeriod)
    {
        var now = DateTime.UtcNow;
        if (IsVnavPathfindInProgress() && now - startedAt < VnavPathfindMaxDuration)
            return false;

        var graceStart = lastVnavPathfindInProgressAt > startedAt ? lastVnavPathfindInProgressAt : startedAt;
        return now - graceStart > gracePeriod;
    }

    private const byte CombatantBattleNpcSubKind = 5;

    /// <summary>
    /// Sucht den nächstgelegenen lebenden Gegner, der gerade den eigenen Charakter anvisiert (also
    /// angreift) - für HuntingLogAutomation: RotationSolver läuft dort im Manual-Modus und greift
    /// nur das aktuelle Ziel an, Beifang-Gegner, die sich selbst angehängt haben, müssen deshalb
    /// selbst anvisiert werden, statt wehrlos stehen zu bleiben.
    /// </summary>
    public static Dalamud.Game.ClientState.Objects.Types.IBattleNpc? FindNearestAttacker(Vector3 nearPosition)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        Dalamud.Game.ClientState.Objects.Types.IBattleNpc? nearest = null;
        var bestDistance = float.MaxValue;

        foreach (var obj in ObjectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc battleNpc)
                continue;

            // SubKind 5 = kämpfender Gegner (BattleNpcSubKind "Enemy"/"Combatant" - der Name unterscheidet
            // sich je nach Dalamud-Version, der Zahlenwert nicht).
            if (battleNpc.SubKind != CombatantBattleNpcSubKind || !battleNpc.IsTargetable || battleNpc.CurrentHp == 0)
                continue;

            // Greift entweder uns selbst an oder etwas, das uns gehört (z.B. den Chocobo-Begleiter,
            // siehe ChocoboCompanionSupport) - beides hält uns im Kampf.
            var target = battleNpc.TargetObject;
            if (target == null || (target.GameObjectId != player.GameObjectId && target.OwnerId != player.EntityId))
                continue;

            var distance = Vector3.Distance(battleNpc.Position, nearPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = battleNpc;
            }
        }

        return nearest;
    }

    /// <summary>Ob das aktuelle Spielziel noch ein lebender Gegner ist (false auch ohne Ziel).</summary>
    public static bool HasLiveTarget() =>
        TargetManager.Target is Dalamud.Game.ClientState.Objects.Types.IBattleChara { CurrentHp: > 0 };

    // General Action "Sprint" - feste Spiel-ID, kein Excel-Sheet-Lookup nötig (ändert sich nicht
    // zwischen Spiel-Patches).
    private const uint SprintGeneralActionId = 4;

    /// <summary>
    /// Aktiviert Sprint, falls es nicht mehr auf Cooldown ist - für die Aetheryten-Automation
    /// (siehe AetheryteAutomation.UpdateMoving), damit lange Laufwege innerhalb einer Zone
    /// schneller gehen. Nur, wenn in den Optionen aktiviert (Configuration.UseSprintOnCooldown)
    /// und man nicht schon auf einem Mount sitzt (das ist ohnehin schneller als Sprint und ein
    /// Sprint-Versuch dabei wäre sinnlos). Lautlos wirkungslos, falls Sprint gerade erst benutzt
    /// wurde/noch nicht erlernt ist (UseAction lehnt in dem Fall einfach ab).
    /// </summary>
    public static unsafe void TryUseSprint()
    {
        if (!instance.Configuration.UseSprintOnCooldown)
            return;

        if (Condition[ConditionFlag.Mounted])
            return;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return;

        if (!actionManager->IsActionOffCooldown(ActionType.GeneralAction, SprintGeneralActionId))
            return;

        actionManager->UseAction(ActionType.GeneralAction, SprintGeneralActionId);
    }

    private const uint SprintStatusId = 50;

    /// <summary>
    /// Sprint auf ausdrückliche Anweisung (z.B. Jumping-Puzzle-Schritt mit SprintBefore) -
    /// unabhängig von Configuration.UseSprintOnCooldown. true, sobald Sprint benutzt wurde oder schon
    /// aktiv ist; false, solange er noch abklingt (Aufrufer wartet dann).
    /// </summary>
    public static unsafe bool TryUseSprintNow()
    {
        if (ObjectTable.LocalPlayer?.StatusList.Any(s => s.StatusId == SprintStatusId) == true)
            return true;

        var actionManager = ActionManager.Instance();
        if (actionManager == null || !actionManager->IsActionOffCooldown(ActionType.GeneralAction, SprintGeneralActionId))
            return false;

        return actionManager->UseAction(ActionType.GeneralAction, SprintGeneralActionId);
    }

    /// <summary>Entfernt einen noch aktiven Sprint (wie Rechtsklick auf den Buff). true, wenn Sprint (jetzt) nicht mehr aktiv ist.</summary>
    public static unsafe bool TryCancelSprint()
    {
        if (ObjectTable.LocalPlayer?.StatusList.Any(s => s.StatusId == SprintStatusId) != true)
            return true;

        // Nicht jeden Frame erneut anfragen, bis der Server das Entfernen bestätigt hat.
        if (DateTime.UtcNow - lastSprintCancelAt > TimeSpan.FromSeconds(0.5))
        {
            lastSprintCancelAt = DateTime.UtcNow;
            StatusManager.ExecuteStatusOff(SprintStatusId);
        }

        return false;
    }

    private static DateTime lastSprintCancelAt = DateTime.MinValue;

    // General Action "Dismount" - feste Spiel-ID (verifiziert per GeneralAction-Sheet-Dump beim
    // Mount-Roulette-Feature), kein Excel-Sheet-Lookup nötig.
    private const uint DismountGeneralActionId = 23;

    /// <summary>
    /// Steigt sofort ab, falls gerade beritten - für Automationen, die am Ziel kämpfen müssen (z.B.
    /// HuntingLogAutomation): die meisten Klassen-Aktionen (und damit auch RotationSolver) lassen
    /// sich beritten gar nicht ausführen, ein automatisches Absteigen beim Ankommen passiert aber
    /// nicht von selbst, wenn man nicht auch tatsächlich in einen Kampf verwickelt wird (bloßes
    /// Anvisieren reicht dafür nicht).
    /// </summary>
    /// <returns>Ob das Spiel den Absteige-Aufruf angenommen hat (false auch, wenn gar nicht beritten).</returns>
    public static unsafe bool TryDismount()
    {
        if (!Condition[ConditionFlag.Mounted])
            return false;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        return actionManager->UseAction(ActionType.GeneralAction, DismountGeneralActionId);
    }

    /// <summary>Siehe Configuration.UseChocoboCompanion/ChocoboCompanionSupport.</summary>
    public static bool UseChocoboCompanion => instance.Configuration.UseChocoboCompanion;

    /// <summary>Siehe Configuration.ChocoboStance/ChocoboCompanionSupport.</summary>
    public static ChocoboStance ChocoboStance => instance.Configuration.ChocoboStance;

    // Quest "My Feisty Little Chocobo" - schaltet das komplette Chocobo-Begleiter-System frei
    // (Beschwören per Gysahl Greens, Rang/Stances), unabhängig von Großkompanie o.ä. Bewusst kein
    // "const" (siehe (ushort)-Cast in IsChocoboCompanionUnlocked): die volle Lumina-RowId liegt über
    // ushort.MaxValue, ein "const" würde dort einen Compile-Fehler (CS0221) auslösen statt der
    // gewollten Laufzeit-Trunkierung auf die 16-Bit-Quest-ID des Spielclients.
    private static readonly uint ChocoboCompanionUnlockQuestId = 66698;

    // Item "Gysahl Greens" - wird beim Beschwören automatisch vom Spiel verbraucht (wie ein
    // Verzehr-Item), kein eigenständiges "Item benutzen" nötig - siehe TrySummonChocoboCompanion.
    private const uint GysahlGreensItemId = 4868;

    // Lumina-Sheet "BuddyAction"-RowIds für die vier Chocobo-Stances - über ActionManager.UseAction
    // mit ActionType.BuddyAction ausgelöst (siehe TrySetChocoboStance), wie ein "Companion Order"-Klick
    // im Buddy-Fenster.
    private static readonly Dictionary<ChocoboStance, uint> ChocoboStanceBuddyActionIds = new()
    {
        [ChocoboStance.Attacker] = 6,
        [ChocoboStance.Defender] = 5,
        [ChocoboStance.Healer] = 7,
        [ChocoboStance.FreeStance] = 4,
    };

    // Index in UIState.Buddy.CompanionInfo.Levels (FixedSizeArray3<byte>, siehe FFXIVClientStructs-
    // Quelltext) - JEDE dieser drei Stances hat ein eigenes, per Kampfeinsatz in dieser Stance
    // steigendes Level (unabhängig vom Gesamt-Rang des Begleiters, siehe GetChocoboCompanionRank)
    // und ist erst ab Level 1 überhaupt nutzbar. Free Stance hat KEIN eigenes Level und ist von
    // Anfang an nutzbar (siehe IsChocoboStanceUnlocked).
    private static readonly Dictionary<ChocoboStance, int> ChocoboStanceLevelIndex = new()
    {
        [ChocoboStance.Defender] = 0,
        [ChocoboStance.Attacker] = 1,
        [ChocoboStance.Healer] = 2,
    };

    public static bool IsChocoboCompanionUnlocked() => QuestManager.IsQuestComplete((ushort)ChocoboCompanionUnlockQuestId);

    public static unsafe int GetChocoboCompanionRank() => UIState.Instance()->Buddy.CompanionInfo.Rank;

    public static unsafe bool IsChocoboStanceUnlocked(ChocoboStance stance)
    {
        if (stance == ChocoboStance.FreeStance)
            return true;

        return UIState.Instance()->Buddy.CompanionInfo.Levels[ChocoboStanceLevelIndex[stance]] >= 1;
    }

    /// <summary>Verbleibende Beschwörungsdauer in Sekunden - 0 (oder kleiner), solange gerade nicht beschworen.</summary>
    public static unsafe float GetChocoboSummonTimeLeft() => UIState.Instance()->Buddy.CompanionInfo.TimeLeft;

    /// <summary>
    /// Ob gerade eine Aktion/ein Item-Einsatz "sperrt" (echte Zauberzeit ODER die kurze
    /// Animationssperre eines instant genutzten Items wie Gysahl Greens) - für
    /// ChocoboCompanionSupport, damit nicht mitten in einem laufenden Beschwören-Versuch ein
    /// zweiter losgeschickt wird (würde den ersten abbrechen/neu starten).
    /// </summary>
    public static unsafe bool IsAnimationLocked()
    {
        var actionManager = ActionManager.Instance();
        return actionManager != null && actionManager->AnimationLock > 0f;
    }

    /// <summary>
    /// Ob man sich gerade in einem Instanz-Inhalt (Dungeon, Trial, Raid, ...) befindet - für die
    /// im Overlay ausgegrauten "Auto ..."-Knöpfe (siehe CompactOverlayWindow), da vnavmesh/die
    /// hier gesteuerten Fremdplugins dort ohnehin nicht sinnvoll funktionieren. Die üblichen drei
    /// ConditionFlags decken zusammen alle Varianten ab (BoundByDuty = normale Duty, BoundByDuty56
    /// = z.B. Deep Dungeons/Eureka, BoundByDuty95 = z.B. Bozja/Zadnor-Gebiete).
    /// </summary>
    public static bool IsInInstancedContent() =>
        Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.BoundByDuty56] || Condition[ConditionFlag.BoundByDuty95];

    public static bool IsChocoboCompanionSummoned() => GetChocoboSummonTimeLeft() > 0f;

    public static uint GetGysahlGreensCount() => instance.GetCurrencyAmount(GysahlGreensItemId);

    // In welchen der vier normalen Inventar-Bags nach Gysahl Greens gesucht wird (siehe
    // TrySummonChocoboCompanion) - dieselbe Reihenfolge wie im echten Inventar-Fenster.
    private static readonly InventoryType[] ChocoboSummonSearchBags =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    /// <summary>
    /// Beschwört den Chocobo-Begleiter (verbraucht dabei automatisch eine Gysahl Greens aus dem
    /// Inventar) - für ChocoboCompanionSupport. Gibt false zurück (ohne Nebenwirkung), falls das
    /// System noch nicht freigeschaltet ist oder keine Gysahl Greens gefunden werden. Bewusst NICHT
    /// über ActionManager.UseAction(ActionType.Item, ...) (das hat sich für dieses Item als
    /// zuverlässig wirkungslos herausgestellt - Gysahl Greens hat laut Lumina ItemAction-Sheet einen
    /// besonderen Typ (5), keinen normalen "Verzehr"-Typ), sondern über AgentInventoryContext.
    /// UseItem - exakt derselbe Aufruf, den ein Rechtsklick -> "Benutzen" im Inventar-Fenster selbst
    /// auslöst, und damit unabhängig vom jeweiligen ItemAction-Typ zuverlässig.
    /// </summary>
    public static bool TrySummonChocoboCompanion() =>
        IsChocoboCompanionUnlocked() && TryUseInventoryItem(GysahlGreensItemId);

    /// <summary>
    /// Benutzt ein Item aus dem normalen Inventar (die vier Taschen) - exakt wie Rechtsklick ->
    /// "Benutzen" im Inventar-Fenster (AgentInventoryContext.UseItem, siehe TrySummonChocoboCompanion-
    /// Kommentar, warum nicht ActionManager). Für Gysahl Greens und das Lernen gewonnener Triple-
    /// Triad-Karten (siehe TripleTriadAutomation). false, wenn das Item nicht im Inventar liegt.
    /// </summary>
    public static unsafe bool TryUseInventoryItem(uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null || itemId == 0)
            return false;

        foreach (var bag in ChocoboSummonSearchBags)
        {
            var container = inventoryManager->GetInventoryContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId || slot->Quantity <= 0)
                    continue;

                var agent = AgentInventoryContext.Instance();
                if (agent == null)
                    return false;

                agent->UseItem(itemId, bag, (uint)slot->Slot, 0);
                return true;
            }
        }

        return false;
    }

    /// <summary>Freischalt-Item eines Sammelobjekts (siehe ResolveUnlockItemId) - z.B. das Karten-Item einer Triple-Triad-Karte.</summary>
    public static uint GetUnlockItemId(CollectibleEntry entry) => ResolveUnlockItemId(entry);

    /// <summary>
    /// Ob gerade ein Triple-Triad-Fenster offen ist (Anfrage, Deckwahl, Partie, Ergebnis) - für
    /// TripleTriadAutomation, um "Saucy spielt noch" von "fertig" zu unterscheiden.
    /// </summary>
    public static bool IsTripleTriadUiOpen() =>
        IsAnyAddonVisible("TripleTriadRequest", "TripleTriadSelDeck", "TripleTriad", "TripleTriadResult", "TripleTriadRoundResult");

    /// <summary>
    /// Bestätigt ein offenes Ja/Nein-Fenster (SelectYesno) mit "Ja" - z.B. beim Crystal Gate der
    /// "Eight Sentinels" (siehe NoFlyAreaExit). true, wenn geklickt wurde.
    /// </summary>
    public static unsafe bool TryConfirmSelectYesno()
    {
        var addon = (AtkUnitBase*)GameGui.GetAddonByName("SelectYesno").Address;
        if (addon == null || !addon->IsVisible)
            return false;

        addon->FireCallbackInt(0); // 0 = "Ja"/"Yes"
        Log.Info("[NoFlyAreaExit] SelectYesno mit \"Ja\" bestätigt.");
        return true;
    }

    /// <summary>
    /// Wählt im offenen SelectString-Menü den ersten Eintrag, der den Text enthält (Groß-/Klein-
    /// schreibung egal) - z.B. "Triple Triad Challenge" bei NPCs, die auch Quests/Gespräche anbieten.
    /// </summary>
    public static unsafe bool TrySelectStringContaining(string text)
    {
        var addon = (AddonSelectString*)GameGui.GetAddonByName("SelectString").Address;
        if (addon == null || !addon->IsVisible)
            return false;

        for (var i = 0; i < addon->PopupMenu.EntryCount; i++)
        {
            var entryText = addon->PopupMenu.EntryNames[i].ToString();
            if (!entryText.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;

            addon->AtkUnitBase.FireCallbackInt(i);
            Log.Info($"[TripleTriadAutomation] SelectString: '{entryText}' (#{i}) gewählt.");
            return true;
        }

        // NPCs mit mehreren Funktionen (Quest + Triple Triad) zeigen stattdessen ein Menü mit Icons.
        var iconAddon = (AddonSelectIconString*)GameGui.GetAddonByName("SelectIconString").Address;
        if (iconAddon == null || !iconAddon->AtkUnitBase.IsVisible)
            return false;

        for (var i = 0; i < iconAddon->PopupMenu.PopupMenu.EntryCount; i++)
        {
            var entryText = iconAddon->PopupMenu.PopupMenu.EntryNames[i].ToString();
            if (!entryText.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;

            iconAddon->AtkUnitBase.FireCallbackInt(i);
            Log.Info($"[TripleTriadAutomation] SelectIconString: '{entryText}' (#{i}) gewählt.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ob gerade ein Quest-Annahme-Fenster (JournalAccept) offen ist.
    /// </summary>
    public static unsafe bool IsJournalAcceptOpen()
    {
        var addon = (AtkUnitBase*)GameGui.GetAddonByName("JournalAccept").Address;
        return addon != null && addon->IsVisible;
    }

    /// <summary>
    /// Klickt im offenen Quest-Annahme-Fenster auf "Annehmen" (Button-Node 44, wie ECommons'
    /// AddonMaster.JournalAccept) - z.B. für Triple-Triad-Gegner, die erst nach Annahme ihrer
    /// Quest herausgefordert werden können (Mimidoa).
    /// </summary>
    public static unsafe bool TryAcceptJournalQuest()
    {
        var addon = (AtkUnitBase*)GameGui.GetAddonByName("JournalAccept").Address;
        if (addon == null || !addon->IsVisible)
            return false;

        var button = addon->GetComponentButtonById(44);
        if (button == null || !button->IsEnabled || !button->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
            return false;

        var resNode = button->AtkComponentBase.OwnerNode->AtkResNode;
        var evt = resNode.AtkEventManager.Event;
        if (evt == null)
            return false;

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        Log.Info("[TripleTriadAutomation] JournalAccept: Quest angenommen.");
        return true;
    }

    /// <summary>
    /// Stellt die gewünschte Chocobo-Stance ein (wie ein Klick im Buddy-Fenster) - für
    /// ChocoboCompanionSupport. Gibt false zurück (ohne Nebenwirkung), falls der Begleiter gerade
    /// nicht beschworen ist oder der nötige Rang für diese Stance fehlt.
    /// </summary>
    public static unsafe bool TrySetChocoboStance(ChocoboStance stance)
    {
        if (!IsChocoboCompanionSummoned() || !IsChocoboStanceUnlocked(stance))
            return false;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        return actionManager->UseAction(ActionType.BuddyAction, ChocoboStanceBuddyActionIds[stance]);
    }

    private const string AllaganToolsInternalName = "InventoryTools"; // Anzeigename im Spiel ist "Allagan Tools"

    /// <summary>
    /// Ob das optionale Fremdplugin "Allagan Tools" aktuell installiert und geladen ist - für die
    /// Freischaltung von Configuration.EnableAllaganToolsIntegration (siehe MainWindow QoL-
    /// Einstellungen), die dort automatisch wieder ausgeschaltet wird, falls das Plugin nachträglich
    /// entfernt wird.
    /// </summary>
    public static bool IsAllaganToolsAvailable() =>
        PluginInterface.InstalledPlugins.Any(p => p.InternalName == AllaganToolsInternalName && p.IsLoaded);

    /// <summary>
    /// Öffnet Allagan Tools' "Mehr Informationen"-Fenster für ein Item - per SHIFT + Linksklick auf
    /// einen Sammelobjekt-Namen oder eine Währungsangabe im kompakten Overlay (siehe
    /// CompactOverlayWindow.DrawClickableName/DrawCurrencyRequirement), nur wenn Configuration.
    /// EnableAllaganToolsIntegration aktiv UND das Plugin geladen ist. Ruft dessen selbst
    /// registrierten Chat-Befehl "/moreinfo" auf (akzeptiert sowohl Item-Namen als auch Item-ID) -
    /// Allagan Tools' eigene IPC bietet aktuell keine Methode, um das Item-Fenster zu öffnen (nur
    /// Bestands-/Filter-bezogene Funktionen, siehe dessen IPC/IPCService.cs).
    /// </summary>
    public static void OpenAllaganToolsItemInfo(string itemNameOrId)
    {
        if (!instance.Configuration.EnableAllaganToolsIntegration || !IsAllaganToolsAvailable())
            return;

        try
        {
            CommandManager.ProcessCommand($"/moreinfo {itemNameOrId}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Öffnen von Allagan Tools' Item-Fenster.");
        }
    }

    /// <summary>Wie OpenAllaganToolsItemInfo(string), für eine bekannte Item-RowId (z.B. eine Währung).</summary>
    public static void OpenAllaganToolsItemInfo(uint itemId)
    {
        if (itemId != 0)
            OpenAllaganToolsItemInfo(itemId.ToString());
    }

    /// <summary>
    /// Für ein Sammelobjekt (SHIFT + Linksklick im Overlay) - Allagan Tools kennt nur ITEMS, der
    /// Anzeigename eines Sammelobjekts ist aber oft nicht der Name des freischaltenden Items
    /// ("Maelstrom Command" statt "Maelstrom Command Orchestrion Roll", Mount "Uolon" statt "Uolon
    /// Horn"). Daher wird - wo möglich - die Item-ID des freischaltenden Items übergeben (siehe
    /// ResolveUnlockItemId), sonst wie bisher der Name.
    /// </summary>
    public static void OpenAllaganToolsItemInfo(CollectibleEntry entry)
    {
        var itemId = ResolveUnlockItemId(entry);
        if (itemId != 0)
            OpenAllaganToolsItemInfo(itemId);
        else
            OpenAllaganToolsItemInfo(entry.Name);
    }

    // Die beiden Reiter "Newest"/"Previous" beim Itinerant Moogle (Moogle Treasure Trove) - im Spiel
    // eigene SpecialShops, deren Inhalt Square Enix mit jedem Event-Patch austauscht. Bewusst per
    // Name statt fester RowId gesucht (es gibt z.B. mehrere "Newest"-Zeilen, siehe
    // GetItinerantMoogleEntries). "Past"/"Seasonal" werden nicht angezeigt (ausdrücklicher Nutzerwunsch).
    private static readonly string[] ItinerantMoogleShopNames =
    {
        "Newest Irregular Tomestone Exchange",
        "Previous Irregular Tomestone Exchange",
    };

    private const string ItinerantMoogleVendorName = "Itinerant Moogle";

    // Die drei Itinerant Moogles in den Hauptstädten (ENpcResident 1009434/1009433/1009435), Position
    // aus dem Lumina-Sheet "Level" umgerechnet.
    private static readonly (uint TerritoryId, uint MapId, float X, float Y)[] ItinerantMoogleLocations =
    {
        (129, 12, 9.4f, 11.7f),  // Limsa Lominsa Lower Decks
        (132, 2, 12.4f, 12.2f),  // New Gridania
        (130, 13, 9.6f, 9.1f),   // Ul'dah - Steps of Nald
    };

    /// <summary>
    /// Baut die aktuell beim Itinerant Moogle unter "Newest" und "Previous" erhältlichen
    /// Sammelobjekte LIVE aus den Spieldaten (SpecialShop) - ändert sich damit automatisch mit jedem
    /// Moogle-Treasure-Trove-Event, ohne dass eine Liste gepflegt werden muss. Jede Ware wird über
    /// ihr Freischalt-Item (siehe ResolveUnlockItemId) dem passenden Sammelobjekt aus baseEntries
    /// zugeordnet und je Stadt einmal (Händlerposition) mit exaktem Preis angelegt. Nur während des
    /// Events erhältlich (siehe CollectibleEntry.EventName/KnownEventWindows);
    /// Die Slot-Quests der Shop-Daten (v.a. bei Triple-Triad-Karten) werden bewusst NICHT übernommen -
    /// beim Moogle gelten die Waren als kaufbar (ausdrücklicher Nutzerwunsch), nur die generelle
    /// Triple-Triad-Freischaltung (siehe TripleTriadUnlockQuestName) wird weiter geprüft.
    /// </summary>
    public static List<CollectibleEntry> GetItinerantMoogleEntries(IReadOnlyList<CollectibleEntry> baseEntries)
    {
        var result = new List<CollectibleEntry>();
        try
        {
            var specialShopSheet = DataManager.GetExcelSheet<SpecialShop>();
            if (specialShopSheet == null)
                return result;

            // Freischalt-Item -> Sammelobjekt (erster Treffer gewinnt, z.B. Frisuren gibt es je
            // Rasse/Geschlecht mehrfach mit demselben Buch).
            var entryByUnlockItem = new Dictionary<uint, CollectibleEntry>();
            foreach (var entry in baseEntries)
            {
                var itemId = ResolveUnlockItemId(entry);
                if (itemId != 0)
                    entryByUnlockItem.TryAdd(itemId, entry);
            }

            var moogleShops = specialShopSheet.Where(s => ItinerantMoogleShopNames.Contains(s.Name.ToString())).ToList();
            var seen = new HashSet<(CollectibleType, uint)>();
            foreach (var shop in moogleShops)
            {
                var shopName = shop.Name.ToString();

                foreach (var slot in shop.Item)
                {
                    try
                    {
                        var receivedItemId = slot.ReceiveItems.FirstOrDefault(r => r.Item.RowId != 0).Item.RowId;
                        if (receivedItemId == 0 || !entryByUnlockItem.TryGetValue(receivedItemId, out var baseEntry))
                            continue;

                        // Dieselbe Ware kann in mehreren Shops/Währungen auftauchen (z.B. Uolon Horn
                        // für Astronomy I UND II) - einmal reicht.
                        if (!seen.Add((baseEntry.Type, baseEntry.Id)))
                            continue;

                        var costs = new List<CollectibleCurrency>();
                        foreach (var cost in slot.ItemCosts)
                        {
                            if (cost.ItemCost.RowId == 0 || cost.CurrencyCost == 0 || cost.ItemCost.ValueNullable is not { } costItem)
                                continue;

                            costs.Add(new CollectibleCurrency
                            {
                                Currency = $"{cost.CurrencyCost:N0} {costItem.Name}",
                                CurrencyIconId = costItem.Icon,
                                CurrencyItemId = cost.ItemCost.RowId,
                                CurrencyAmount = cost.CurrencyCost,
                            });
                        }

                        if (costs.Count == 0)
                            continue;

                        foreach (var (territoryId, mapId, x, y) in ItinerantMoogleLocations)
                        {
                            result.Add(new CollectibleEntry
                            {
                                Id = baseEntry.Id,
                                Name = baseEntry.Name,
                                Type = baseEntry.Type,
                                Category = "Saisonevent",
                                TerritoryTypeId = territoryId,
                                MapId = mapId,
                                Vendor = ItinerantMoogleVendorName,
                                VendorMapX = x,
                                VendorMapY = y,
                                Currency = costs[0].Currency,
                                CurrencyIconId = costs[0].CurrencyIconId,
                                CurrencyItemId = costs[0].CurrencyItemId,
                                CurrencyAmount = costs[0].CurrencyAmount,
                                AdditionalCurrencies = costs.Count > 1 ? costs.Skip(1).ToList() : null,
                                Source = $"{ItinerantMoogleVendorName} - Moogle Treasure Trove ({shopName})",
                                FrameKitUnlockKind = baseEntry.FrameKitUnlockKind,
                                FrameKitUnlockId = baseEntry.FrameKitUnlockId,
                                EventName = MoogleTreasureTroveEventName,
                            });
                        }
                    }
                    catch (Exception)
                    {
                        // Einzelne leere/reservierte Slots können beim Auslesen werfen (siehe
                        // EnrichFrameKitVendors) - nur diesen Slot überspringen. Bewusst ohne Log (über
                        // 1.500 Stacktraces pro Laden haben das Log geflutet).
                    }
                }
            }

            Log.Info($"[MoogleTrove] {result.Count / ItinerantMoogleLocations.Length} Sammelobjekte beim Itinerant Moogle (Newest/Previous).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Aufbau der Itinerant-Moogle-Waren.");
        }

        return result;
    }

    /// <summary>Ob für dieses Sammelobjekt ein freischaltendes Item bekannt ist (siehe ResolveUnlockItemId).</summary>
    public static bool HasUnlockItem(CollectibleEntry entry) => ResolveUnlockItemId(entry) != 0;

    // Lumina "ItemAction"-Typen (ItemAction.Action.RowId) der Items, die ein Sammelobjekt
    // freischalten - per Auswertung der Spieldaten verifiziert (z.B. "Uolon Horn" = 1322 mit
    // Data[0] = Mount-RowId 414, "Wind-up Cursor" = 853 mit Data[0] = Companion-RowId 51,
    // "Maelstrom Command Orchestrion Roll" = 25183 mit AdditionalData = Orchestrion-RowId 549).
    private const uint ItemActionCompanion = 853;
    private const uint ItemActionBuddyEquip = 1013;
    private const uint ItemActionMount = 1322;
    private const uint ItemActionUnlockLink = 2633; // u.a. Emotes: Data[0] = Emote.UnlockLink
    private const uint ItemActionTripleTriadCard = 3357;
    private const uint ItemActionOrnament = 20086;
    private const uint ItemActionOrchestrion = 25183; // Orchestrion-RowId steht in Item.AdditionalData, nicht in Data
    private const uint ItemActionGlasses = 37312;
    private const uint ItemActionFramersKit = 29459; // Kit-ID (== CollectibleEntry.FrameKitUnlockId) steht in Item.AdditionalData

    private static Dictionary<(CollectibleType Type, uint Id), uint>? unlockItemIdCache;
    private static Dictionary<string, uint>? glassesItemIdByNameCache;

    /// <summary>
    /// Item-RowId des Items, das dieses Sammelobjekt freischaltet - 0, wenn keins bekannt ist (dann
    /// sucht Allagan Tools per Name). Einmalig aus dem Item-Sheet aufgebaut. Gibt es mehrere Items
    /// für dasselbe Sammelobjekt, gewinnt das mit der kleinsten RowId (in der Regel das
    /// ursprüngliche, nicht eine spätere Neuauflage).
    /// </summary>
    private static uint ResolveUnlockItemId(CollectibleEntry entry)
    {
        if (unlockItemIdCache == null)
            BuildUnlockItemCaches();

        // Frisuren: das "Modern Aesthetics"-Buch steht direkt in CharaMakeCustomize.HintItem (entry.Id
        // ist die CharaMakeCustomize-RowId, siehe GetHairstyleEntries).
        if (entry.Type == CollectibleType.Hairstyle)
        {
            var customizeSheet = DataManager.GetExcelSheet<CharaMakeCustomize>();
            return customizeSheet != null && customizeSheet.TryGetRow(entry.Id, out var customize) ? customize.HintItem.RowId : 0u;
        }

        // Framer's Kits: nur Rahmen, die über ein Kit-Item freigeschaltet werden (andere Arten, z.B.
        // per Errungenschaft, haben kein Item - dann Namenssuche wie bisher).
        if (entry.Type == CollectibleType.FrameKit)
        {
            return entry.FrameKitUnlockKind == FrameKitUnlockKind.FramersKitItem
                ? unlockItemIdCache!.GetValueOrDefault((CollectibleType.FrameKit, entry.FrameKitUnlockId), 0u)
                : 0u;
        }

        // Facewear: die Einträge nutzen eine andere ID als die Items (Glasses vs. GlassesStyle ließen
        // sich nicht eindeutig zuordnen) - daher über den Namen des Items ("The Faces We Wear - <Name>").
        if (entry.Type == CollectibleType.Facewear)
            return glassesItemIdByNameCache!.GetValueOrDefault(entry.Name.Trim(), 0u);

        var key = entry.Type == CollectibleType.Emote
            ? (CollectibleType.Emote, ResolveEmoteUnlockLink(entry.Id))
            : (entry.Type, entry.Id);
        return unlockItemIdCache!.GetValueOrDefault(key, 0u);
    }

    private static uint ResolveEmoteUnlockLink(uint emoteId)
    {
        var emoteSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        return emoteSheet != null && emoteSheet.TryGetRow(emoteId, out var emote) ? emote.UnlockLink : 0u;
    }

    private static void BuildUnlockItemCaches()
    {
        var byKey = new Dictionary<(CollectibleType Type, uint Id), uint>();
        var glassesByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var itemSheet = DataManager.GetExcelSheet<Item>();
            if (itemSheet != null)
            {
                foreach (var item in itemSheet)
                {
                    if (item.ItemAction.RowId == 0 || item.ItemAction.ValueNullable is not { } action)
                        continue;

                    var data0 = (uint)action.Data[0];
                    (CollectibleType Type, uint Id)? key = action.Action.RowId switch
                    {
                        ItemActionCompanion => (CollectibleType.Minion, data0),
                        ItemActionBuddyEquip => (CollectibleType.Barding, data0),
                        ItemActionMount => (CollectibleType.Mount, data0),
                        ItemActionUnlockLink => (CollectibleType.Emote, data0),
                        ItemActionTripleTriadCard => (CollectibleType.TripleTriadCard, data0),
                        ItemActionOrnament => (CollectibleType.FashionAccessory, data0),
                        ItemActionOrchestrion => (CollectibleType.Orchestrion, item.AdditionalData.RowId),
                        ItemActionFramersKit => (CollectibleType.FrameKit, item.AdditionalData.RowId),
                        _ => null,
                    };

                    if (key is { } k && k.Id != 0)
                        byKey.TryAdd(k, item.RowId);

                    if (action.Action.RowId == ItemActionGlasses)
                    {
                        // "The Faces We Wear - Oval Spectacles" -> "Oval Spectacles"
                        var itemName = item.Name.ToString();
                        var dash = itemName.LastIndexOf(" - ", StringComparison.Ordinal);
                        var glassesName = dash >= 0 ? itemName[(dash + 3)..] : itemName;
                        glassesByName.TryAdd(glassesName.Trim(), item.RowId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Aufbau der Freischalt-Item-Zuordnung für Allagan Tools.");
        }

        unlockItemIdCache = byKey;
        glassesItemIdByNameCache = glassesByName;
    }

    // Anders als OpenAllaganToolsItemInfo (Chat-Befehl) hier bewusst echte IPC - "AllaganTools.
    // GetItemCountsByCharacter(itemId, currentCharacterOnly, inventoryCategories, includeSharedStorage)"
    // liefert je Charakter-/Retainer-ID (ulong) die besessene Menge, unabhängig davon, ob dessen
    // Inventar in DIESER Sitzung schon einmal geöffnet wurde (Allagan Tools hält eine eigene,
    // sitzungsübergreifende Datenbank) - name/RowId und Registrierung per Dekompilieren von
    // InventoryTools/IPC/IPCService.cs verifiziert (dort registriert unter genau diesem Namen).
    private static ICallGateSubscriber<uint, bool, uint[], bool, Dictionary<ulong, uint>>? allaganToolsGetItemCountsByCharacter;

    /// <summary>
    /// Wie viel eines Items auf den eigenen Retainern liegt, aufgeschlüsselt je Retainer-Name - für
    /// den "(<Anzahl>)"-Zusatz hinter "Deine Währungen" (siehe CompactOverlayWindow.
    /// DrawCurrencyWallet/Configuration.ShowRetainerItemCounts). Leer (kein Fehler), solange Allagan
    /// Tools nicht installiert/geladen ist oder der IPC-Aufruf aus irgendeinem Grund fehlschlägt.
    /// Retainer-IDs/-Namen kommen bewusst NICHT aus Allagan Tools selbst (das liefert nur IDs, keine
    /// Namen) - stattdessen aus RetainerManager (immer verfügbar für alle eigenen Retainer, auch
    /// ohne sie in dieser Sitzung geöffnet zu haben), gegen das die von Allagan Tools gelieferten
    /// IDs abgeglichen werden (schließt dabei automatisch Werte für die eigene Spielfigur/FC-Truhen
    /// aus, auch ohne die genaue InventoryCategory-Aufschlüsselung von Allagan Tools zu kennen).
    /// </summary>
    // Siehe GetAllaganToolsCharacterNames.
    private static Dictionary<ulong, string> allaganToolsCharacterNames = new();
    private static DateTime allaganToolsConfigLastWrite = DateTime.MinValue;
    private static DateTime allaganToolsConfigLastCheck = DateTime.MinValue;
    private static readonly TimeSpan AllaganToolsConfigCheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Namen der Allagan Tools bekannten Charaktere/Retainer/FCs je ID - Allagan Tools bietet dafür
    /// KEINE IPC (nur IDs, siehe GetRetainerItemCounts), speichert sie aber in seiner eigenen
    /// Konfigurationsdatei ("SavedCharacters": ID -> { CharacterId, Name, OwnerId, ... }). Nur
    /// lesend geöffnet (FileShare.ReadWrite, Allagan Tools darf jederzeit weiterschreiben) und nur
    /// neu eingelesen, wenn sich die Datei geändert hat (höchstens alle
    /// AllaganToolsConfigCheckInterval geprüft). Bei jedem Fehler bleibt der letzte Stand erhalten.
    /// </summary>
    private static Dictionary<ulong, string> GetAllaganToolsCharacterNames()
    {
        if (DateTime.UtcNow - allaganToolsConfigLastCheck < AllaganToolsConfigCheckInterval)
            return allaganToolsCharacterNames;

        allaganToolsConfigLastCheck = DateTime.UtcNow;
        try
        {
            var configDirectory = PluginInterface.ConfigFile.DirectoryName;
            if (configDirectory == null)
                return allaganToolsCharacterNames;

            var path = System.IO.Path.Combine(configDirectory, AllaganToolsInternalName + ".json");
            if (!System.IO.File.Exists(path))
                return allaganToolsCharacterNames;

            var lastWrite = System.IO.File.GetLastWriteTimeUtc(path);
            if (lastWrite == allaganToolsConfigLastWrite)
                return allaganToolsCharacterNames;

            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            var names = new Dictionary<ulong, string>();
            if (document.RootElement.TryGetProperty("SavedCharacters", out var savedCharacters)
                && savedCharacters.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var character in savedCharacters.EnumerateObject())
                {
                    if (character.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                        continue;

                    // CharacterId kann (z.B. bei FCs) größer als long.MaxValue sein - daher ulong.
                    if (!character.Value.TryGetProperty("CharacterId", out var idElement) || !idElement.TryGetUInt64(out var id))
                        continue;

                    if (character.Value.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == System.Text.Json.JsonValueKind.String)
                        names[id] = nameElement.GetString() ?? string.Empty;
                }
            }

            allaganToolsCharacterNames = names;
            allaganToolsConfigLastWrite = lastWrite;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte Retainer-Namen nicht aus der Allagan-Tools-Konfiguration lesen.");
        }

        return allaganToolsCharacterNames;
    }

    public static unsafe Dictionary<string, uint> GetRetainerItemCounts(uint itemId)
    {
        var result = new Dictionary<string, uint>();
        if (itemId == 0 || !instance.Configuration.ShowRetainerItemCounts || !IsAllaganToolsAvailable())
            return result;

        allaganToolsGetItemCountsByCharacter ??=
            PluginInterface.GetIpcSubscriber<uint, bool, uint[], bool, Dictionary<ulong, uint>>("AllaganTools.GetItemCountsByCharacter");

        Dictionary<ulong, uint> byCharacter;
        try
        {
            if (!allaganToolsGetItemCountsByCharacter.HasFunction)
                return result;

            byCharacter = allaganToolsGetItemCountsByCharacter.InvokeFunc(itemId, true, Array.Empty<uint>(), false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Abfragen der Retainer-Bestände über Allagan Tools.");
            return result;
        }

        // Namen nur, soweit RetainerManager sie kennt - dessen Liste ist erst gefüllt, nachdem in
        // DIESER Sitzung eine Glocke benutzt wurde. Bisher wurden deshalb ausschließlich dort
        // bekannte IDs übernommen und alle Allagan-Tools-Bestände verworfen, solange die Liste leer
        // war (Nutzer-Report: Retainer mit 80+ Stück, angezeigt wurde gar nichts). Jetzt zählt jede
        // von Allagan Tools gelieferte ID außer der eigenen Spielfigur (currentCharacterOnly=true
        // beschränkt bereits auf eigene Retainer/FC-/Haus-Lager); unbekannte bekommen einen
        // Platzhalternamen.
        var retainerNames = new Dictionary<ulong, string>();
        var retainerManager = RetainerManager.Instance();
        if (retainerManager != null)
        {
            foreach (var retainer in retainerManager->Retainers)
            {
                if (retainer.RetainerId != 0)
                    retainerNames[retainer.RetainerId] = retainer.NameString;
            }
        }

        var ownCharacterId = PlayerState.Instance()->ContentId;
        var allaganToolsNames = GetAllaganToolsCharacterNames();
        var unknownIndex = 0;
        foreach (var (ownerId, count) in byCharacter)
        {
            if (ownerId == ownCharacterId || count == 0)
                continue;

            string name;
            if (retainerNames.TryGetValue(ownerId, out var knownName) && !string.IsNullOrEmpty(knownName))
            {
                name = knownName;
            }
            else if (allaganToolsNames.TryGetValue(ownerId, out var savedName) && !string.IsNullOrEmpty(savedName))
            {
                name = savedName;
            }
            else
            {
                unknownIndex++;
                name = Loc.T($"Retainer/Lager {unknownIndex}", $"Retainer/storage {unknownIndex}");
            }


            result[name] = result.GetValueOrDefault(name) + count;
        }

        return result;
    }

    // Von Hand als "nicht von der Automation unterstützt" markierte Sightseeing-Punkte (Key =
    // Adventure-RowId, Wert = Anzeigetext) - erscheinen dadurch weiterhin in der Liste, aber mit
    // "Bedingung nicht erfüllt" statt von der Automation (erfolglos) angelaufen zu werden. Alles
    // echte Jumping Puzzles - ein Versuch, das per simuliertem Tastendruck (IKeyState) zu
    // automatisieren, ist an einer bewussten Dalamud-Einschränkung gescheitert ("Dalamud does not
    // support pressing keys, only preventing them"); vnavmesh selbst kann so eine echte Sprung-Lücke
    // ohnehin nicht queren (kein Navmesh dort). Inzwischen per Sprung-Aktion + geradem vnavmesh-
    // Path.MoveTo lösbar, sobald die Koordinaten von Hand hinterlegt sind (siehe
    // SightseeingJumpingPuzzles) - solche Punkte dann hier austragen.
    private static readonly Dictionary<uint, (string De, string En)> SightseeingUnsupportedByAutomation = new()
    {
    };

    /// <summary>
    /// Für SightseeingAutomations Zielliste (siehe CompactOverlayWindow) - bewusst ZUSÄTZLICH zu
    /// IsAchievementOrRankGated/ComputeGrandCompanyOrTribeGateReason geprüft, statt sich allein
    /// darauf zu verlassen: Configuration.SimulateSightseeingAutomation umgeht genau diese
    /// Gate-Prüfung absichtlich (siehe dessen Kommentar), würde einen als "nicht unterstützt"
    /// markierten Punkt im Simulation-Modus sonst trotzdem als Automations-Ziel durchlassen.
    /// </summary>
    public static bool IsSightseeingUnsupportedByAutomation(uint adventureId) =>
        SightseeingUnsupportedByAutomation.ContainsKey(adventureId);

    /// <summary>
    /// Liefert die aktuell freigeschalteten Mounts (Id + Name) - für die Mount-Auswahl der
    /// Aetheryten-Automation (siehe MainWindow QoL-Tab). Bewusst live berechnet statt gecacht -
    /// die Liste ändert sich nur, wenn man ein neues Mount freischaltet, und das Optionsfenster
    /// ist ohnehin nicht die ganze Zeit offen.
    /// </summary>
    public IReadOnlyList<CollectibleEntry> GetUnlockedMounts() =>
        CollectionData.GetAllEntries()
            .Where(e => e.Type == CollectibleType.Mount && IsOwned(e))
            .OrderBy(e => e.Name)
            .ToList();

    /// <summary>
    /// Setzt einmalig (siehe Configuration.AetheryteMountAutoDefaultApplied) eine sinnvolle
    /// Vorbelegung für die Mount-Auswahl der Aetheryten-Automation, sobald der Spieler mindestens
    /// ein Mount besitzt - sonst stünde die Automation dauerhaft auf "Kein Mount", obwohl sie längst
    /// einsatzbereit wäre. "Mount Roulette" (AetheryteMountId == 0, siehe DrawAetheryteMountPicker
    /// und TryRequestAetheryteMount) ergibt erst ab zwei Mounts Sinn - vorher gäbe es nichts
    /// auszuwürfeln, daher wird bis dahin direkt das erste freigeschaltete Mount vorbelegt. Läuft
    /// nur einmal; eine spätere manuelle Änderung (auch zurück auf "Kein Mount") bleibt danach
    /// unangetastet.
    /// </summary>
    public void EnsureAetheryteMountAutoDefault()
    {
        if (Configuration.AetheryteMountAutoDefaultApplied)
            return;

        var unlockedMounts = GetUnlockedMounts();
        if (unlockedMounts.Count == 0)
            return;

        Configuration.AetheryteMountId = unlockedMounts.Count >= 2 ? 0 : (int)unlockedMounts[0].Id;
        Configuration.AetheryteMountAutoDefaultApplied = true;
        Configuration.Save();
    }

    private static readonly TimeSpan RemountAfterForcedDismountRetryInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Für alle Automationen mit vnavmesh-Laufwegen (Aetheryte/Chocobokeep/HuntingLog/AetherCurrent/
    /// Sightseeing/GoTo) - viele Mounts steigen beim Schwimmen im tiefen Wasser zwangsweise ab, ohne
    /// dass die Automation das selbst anstößt. Läuft der Weg über Land weiter, blieb man bisher bis
    /// zum Ziel unberitten. Wird pro Update-Tick aus UpdateMoving aufgerufen (solange der Laufweg
    /// noch aktiv ist) und versucht - gedrosselt über lastAttempt, damit nicht bei jedem Frame ein
    /// neuer "/mount"-Ruf rausgeht, während der vorherige noch in der Aufstiegs-Animation steckt -
    /// erneut aufzusitzen, sobald wieder festen Boden unter den Füßen ist.
    /// </summary>
    public static void TryRemountAfterForcedDismount(ref DateTime lastAttempt)
    {
        if (Condition[ConditionFlag.Mounted] || Condition[ConditionFlag.Swimming] || Condition[ConditionFlag.Diving])
            return;

        if (DateTime.UtcNow - lastAttempt < RemountAfterForcedDismountRetryInterval)
            return;

        lastAttempt = DateTime.UtcNow;
        TryRequestAetheryteMount();
    }

    /// <summary>
    /// Stößt (falls in den Optionen ein Mount für die Aetheryten-Automation ausgewählt und man
    /// nicht schon beritten ist) den Ruf des konfigurierten Mounts an - über den normalen "/mount"-
    /// Chat-Befehl (nicht ActionManager direkt), da der Befehl genau das tut, was ein Spieler-Klick
    /// im Mount-Menü auch tun würde (inkl. aller Sonderfälle wie "gerade nicht möglich"), und der
    /// Mount-Name aus Lumina automatisch in der aktuellen Client-Sprache aufgelöst wird. "Mount
    /// Roulette" (AetheryteMountId == 0) wird bewusst selbst simuliert (zufällige Auswahl aus den
    /// eigenen freigeschalteten Mounts) statt über ein Spiel-eigenes Feature, da es dafür keine
    /// verlässliche, sprachunabhängige Ansteuerung gibt.
    /// Gibt true zurück, wenn ein Ruf losgeschickt wurde (Aufrufer sollte dann kurz aufs Aufsteigen
    /// warten, bevor der eigentliche Laufauftrag an vnavmesh geht) - false, wenn nichts zu tun war
    /// (Funktion aus, schon beritten, oder kein Mount auflösbar).
    /// </summary>
    public static unsafe bool TryRequestAetheryteMount()
    {
        var mountId = instance.Configuration.AetheryteMountId;
        if (!mountId.HasValue)
        {
            Log.Info("[MountDebug] Abbruch: kein Mount konfiguriert (AetheryteMountId == null).");
            return false;
        }

        if (Condition[ConditionFlag.Mounted])
        {
            Log.Info("[MountDebug] Abbruch: bereits beritten.");
            return false;
        }

        // In Städten (und anderen Mount-gesperrten Zonen, z.B. manchen Innenräumen) lässt sich gar
        // nicht aufsitzen - ohne diese Prüfung würde hier trotzdem "/mount" gesendet (bewirkt dort
        // nichts) und die Automation wartet danach noch MountWaitTimeout lang sinnlos auf ein
        // Aufsteigen, das nie passiert, bevor sie auf Fußweg umschaltet.
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(ClientState.TerritoryType, out var territory) || !territory.Mount)
        {
            Log.Info($"[MountDebug] Abbruch: Zone={ClientState.TerritoryType} erlaubt kein Aufsitzen (TerritoryType.Mount=false oder Sheet-Zeile nicht gefunden).");
            return false;
        }

        var mountSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Mount>();
        if (mountSheet == null)
            return false;

        uint resolvedMountId;
        if (mountId.Value == 0)
        {
            // Zufällige Auswahl per RowId (nicht per Name aus der eigenen mounts.json) - so kommt
            // der Name für den "/mount"-Befehl unten in jedem Fall aus Lumina und passt garantiert
            // zur aktuellen Client-Sprache, auch wenn die eigene Datendatei z.B. nur englische
            // Namen enthält.
            var unlocked = instance.GetUnlockedMounts();
            if (unlocked.Count == 0)
                return false;

            resolvedMountId = unlocked[Random.Shared.Next(unlocked.Count)].Id;
        }
        else
        {
            resolvedMountId = (uint)mountId.Value;
        }

        if (!mountSheet.TryGetRow(resolvedMountId, out var mount))
            return false;

        // Lumina liefert "Singular" komplett kleingeschrieben (roher Grammatik-Baustein für
        // Satzkonstruktion, z.B. "du hast ein company chocobo erhalten") - für den "/mount"-Text-
        // befehl muss aber der tatsächliche Anzeigename (Title Case) übergeben werden, sonst
        // ignoriert das Spiel den Befehl kommentarlos (siehe MountDebug-Log: Befehl wurde gesendet,
        // aber nie aufgestiegen).
        var mountName = CapitalizeChatCommandName(mount.Singular.ToString());
        if (string.IsNullOrEmpty(mountName))
            return false;

        Log.Info($"[MountDebug] Sende '/mount \"{mountName}\"' über SendGameChatCommand (mountId={resolvedMountId}).");

        try
        {
            SendGameChatCommand($"/mount \"{mountName}\"");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Rufen des Mounts für die Aetheryten-Automation.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Schickt einen echten, vom Spiel selbst verarbeiteten Chat-/Text-Befehl (z.B. "/mount ...",
    /// ein Emote wie "/sit") ab - NICHT über Dalamuds ICommandManager.ProcessCommand, das nur an
    /// von Plugins selbst registrierte Befehle weiterleitet (funktioniert z.B. für "/rotation" oder
    /// "/qst", weil das andere Plugins sind, aber niemals für native Spielbefehle - der Aufruf
    /// schlägt dabei nicht mal fehl, er bewirkt einfach nichts, siehe MountDebug-Log). Stattdessen
    /// wie ein Tastatur-Enter in der Chatbox direkt über UIModule.ProcessChatBoxEntry simuliert
    /// (Standardtechnik vieler Automations-Plugins, z.B. auch in "ECommons" so implementiert).
    /// </summary>
    public static unsafe void SendGameChatCommand(string command)
    {
        var message = Utf8String.FromString(command);
        try
        {
            Framework.Instance()->GetUIModule()->ProcessChatBoxEntry(message, IntPtr.Zero, false);
        }
        finally
        {
            message->Dtor(true);
        }
    }

    /// <summary>
    /// Klickt ein offenes NPC-Gespräch ("Talk"-Fenster, z.B. beim Chocobokeep) einen Schritt weiter -
    /// exakt dieselbe Technik, die auch die Fremdplugins "TextAdvance"/"ECommons"
    /// (AddonMaster.Talk.Click) für automatisches Wegklicken von Dialogtext nutzen: ein simulierter
    /// Maus-Klick (MouseDown/MouseClick/MouseUp) auf das Fenster selbst, worauf das Spiel wie bei
    /// einem echten Klick zur nächsten Zeile springt bzw. das Fenster schließt. War zunächst über
    /// TextAdvances eigene IPC (EnableExternalControl) versucht - dessen TalkSkip griff aber nicht
    /// zuverlässig (vermutlich Serialisierungsproblem beim komplexen Parametertyp über die
    /// Plugin-Grenze), daher stattdessen direkt selbst nachgebaut.
    /// </summary>
    private static DateTime lastTalkDialogueDebugLog = DateTime.MinValue;

    public static unsafe bool TryAdvanceTalkDialogue()
    {
        var addon = (AddonTalk*)GameGui.GetAddonByName("Talk").Address;

        // Temporäre Diagnose (max. 1x/Sekunde) - zeigt, ob "Talk" überhaupt gefunden/sichtbar ist,
        // wenn der Dialog beim Chocobokeep offen ist.
        if (DateTime.UtcNow - lastTalkDialogueDebugLog > TimeSpan.FromSeconds(1))
        {
            lastTalkDialogueDebugLog = DateTime.UtcNow;
            Log.Info($"[ChocobokeepAutomation] TryAdvanceTalkDialogue: addon={(nint)addon:X}, IsVisible={(addon != null ? addon->IsVisible : (bool?)null)}.");
        }

        if (addon == null || !addon->IsVisible)
            return false;

        Log.Info("[ChocobokeepAutomation] TryAdvanceTalkDialogue: Talk-Fenster sichtbar - klicke weiter.");

        var atkEvent = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &AtkStage.Instance()->AtkEventTarget,
            State = new AtkEventState { StateFlags = (AtkEventStateFlags)132 },
        };
        var atkEventData = default(AtkEventData);

        addon->AtkUnitBase.ReceiveEvent(AtkEventType.MouseDown, 0, &atkEvent, &atkEventData);
        addon->AtkUnitBase.ReceiveEvent(AtkEventType.MouseClick, 0, &atkEvent, &atkEventData);
        addon->AtkUnitBase.ReceiveEvent(AtkEventType.MouseUp, 0, &atkEvent, &atkEventData);
        return true;
    }

    /// <summary>
    /// Ob gerade ein "Talk"- oder "SelectString"-Fenster offen ist - für ChocobokeepAutomation, die
    /// sonst (weil UIState.IsChocoboTaxiStandUnlocked schon beim Interagieren selbst true wird, noch
    /// bevor der NPC überhaupt zu reden anfängt, und ConditionFlag.OccupiedInEvent/Occupied dabei gar
    /// nicht gesetzt werden) den Erfolg sofort verbucht und danach nie mehr TryAdvanceTalkDialogue/
    /// TryDismissChocobokeepSelectString aufruft - das offene Gespräch bliebe dann für immer stehen,
    /// weil niemand mehr weiterklickt.
    /// </summary>
    public static unsafe bool IsChocobokeepDialogueOpen()
    {
        var talk = (AddonTalk*)GameGui.GetAddonByName("Talk").Address;
        if (talk != null && talk->IsVisible)
            return true;

        var selectString = (AddonSelectString*)GameGui.GetAddonByName("SelectString").Address;
        return selectString != null && selectString->IsVisible;
    }

    /// <summary>
    /// Großschreibung jedes Worts (nach Leerzeichen/Bindestrich) - macht aus Luminas rohem,
    /// kleingeschriebenem "Singular"-Grammatikfeld (siehe TryRequestAetheryteMount) wieder den
    /// tatsächlichen Anzeigenamen für Text-Befehle wie "/mount".
    /// </summary>
    private static string CapitalizeChatCommandName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        var chars = raw.ToCharArray();
        var capitalizeNext = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i]) || chars[i] == '-')
            {
                capitalizeNext = true;
                continue;
            }

            if (capitalizeNext)
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                capitalizeNext = false;
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Einmaliger Debug-Dump der rohen Hunting-Log-Fortschrittsdaten (FFXIVClientStructs
    /// MonsterNoteManager) zusammen mit der aktuellen Klasse - für den Hunting-Log-Zonenfilter
    /// (siehe CollectionData/GetLiveZoneEntries). Weder welcher der 12 Slots zu welcher Klasse
    /// gehört, noch die genaue Bedeutung von Rank/Flags ist offiziell dokumentiert - das muss
    /// einmalig live abgeglichen werden: pro Klasse hier klicken, die resultierenden Log-Zeilen
    /// (siehe /xllog) mit der jeweils aktiven Klasse und dem im Spiel offenen Hunting Log
    /// vergleichen (u.a. welcher Rang dort gerade als nächstes/unvollständig markiert ist).
    /// </summary>
    public static unsafe void DumpHuntingLogDebugInfo()
    {
        var player = ObjectTable.LocalPlayer;
        Log.Info($"[HuntingLogDebug] Aktuelle Klasse: RowId={player?.ClassJob.RowId}, Name={player?.ClassJob.ValueNullable?.Name}, Level={player?.Level}");

        var manager = MonsterNoteManager.Instance();
        if (manager == null)
        {
            Log.Info("[HuntingLogDebug] MonsterNoteManager.Instance() ist null.");
            return;
        }

        for (var slot = 0; slot < 12; slot++)
        {
            var slotInfo = manager->RankData[slot];
            var perRank = new List<string>();
            for (var rankIdx = 0; rankIdx < 10; rankIdx++)
            {
                var rankData = slotInfo.RankData[rankIdx];
                perRank.Add($"{rankData[0]}/{rankData[1]}/{rankData[2]}/{rankData[3]}");
            }

            Log.Info($"[HuntingLogDebug] Slot {slot}: Index={slotInfo.Index}, Rank={slotInfo.Rank}, Flags={slotInfo.Flags}, Counts je Rang (0..9)=[{string.Join(", ", perRank)}]");
        }

        // Zusätzlich: alle 10 Teil-Ränge der aktuellen Zehner-Stufe (siehe GetHuntingLogEntries-
        // Kommentar) mit ihren bis zu 4 Zielen, Fortschritt und PlaceNameZone-RowIds, plus der
        // PlaceName-RowId der aktuellen Zone zum Abgleich.
        var classId = ResolveHuntingLogClassId(player?.ClassJob.RowId ?? 0);
        Log.Info($"[HuntingLogDebug] Für Hunting Log verwendete Basisklasse-RowId={classId} (Job-RowId={player?.ClassJob.RowId}).");
        var tier = manager->RankData[0].Rank;
        var noteSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.MonsterNote>();
        if (noteSheet == null)
        {
            Log.Info("[HuntingLogDebug] MonsterNote-Sheet nicht gefunden.");
            return;
        }

        var currentTerritoryId = ClientState.TerritoryType;
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        var currentZonePlaceNameId = territorySheet != null && territorySheet.TryGetRow(currentTerritoryId, out var territoryRow)
            ? territoryRow.PlaceName.RowId
            : 0u;
        Log.Info($"[HuntingLogDebug] Stufe (Rank)={tier}, aktuelle Zone={currentTerritoryId}, PlaceName-RowId der Zone={currentZonePlaceNameId}");

        for (var subRank = 0; subRank < 10; subRank++)
        {
            var monsterNoteRowId = (uint)(classId * 10000 + tier * 10 + subRank + 1);
            if (!noteSheet.TryGetRow(monsterNoteRowId, out var note))
            {
                Log.Info($"[HuntingLogDebug] Teil-Rang {subRank} (Zeile {monsterNoteRowId}): nicht gefunden.");
                continue;
            }

            var rankCounts = manager->RankData[0].RankData[subRank];
            for (var i = 0; i < 4; i++)
            {
                var targetRef = note.MonsterNoteTarget[i];
                if (targetRef.RowId == 0)
                    continue;

                var target = targetRef.ValueNullable;
                if (target == null)
                {
                    Log.Info($"[HuntingLogDebug] Teil-Rang {subRank}, Ziel {i}: RowId={targetRef.RowId}, aber ValueNullable ist null.");
                    continue;
                }

                var zoneIds = string.Join(", ", target.Value.PlaceNameZone.Select(p => p.RowId));
                Log.Info($"[HuntingLogDebug] Teil-Rang {subRank} (Zeile {monsterNoteRowId}), Ziel {i}: RowId={target.Value.RowId}, " +
                         $"Name={target.Value.BNpcName.ValueNullable?.Singular}, Fortschritt={rankCounts[i]}/{note.Count[i]}, " +
                         $"PlaceNameZone(RowIds)=[{zoneIds}]");
            }
        }
    }

    /// <summary>
    /// Einmaliger Debug-Dump aller Sightseeing-Log-Punkte der aktuellen Zone (Name, RowId, Welt-
    /// position, Radius, benötigter Emote, Freischalt-Status) - zur Kalibrierung von
    /// SightseeingAutomation (v.a. ob die Achsen-Umrechnung aus Level.X/Y/Z stimmt und ob der
    /// hinterlegte Emote tatsächlich zur Freischaltung reicht).
    /// </summary>
    public void DumpSightseeingDebugInfo()
    {
        var territoryId = ClientState.TerritoryType;
        var sightseeing = GetLiveZoneEntries(territoryId)
            .Where(e => e.Type == CollectibleType.Sightseeing)
            .OrderBy(e => e.Name)
            .ToList();

        Log.Info($"[SightseeingDebug] Zone {territoryId}: {sightseeing.Count} Sightseeing-Punkte:");
        foreach (var entry in sightseeing)
        {
            var emote = string.IsNullOrEmpty(entry.RequiredEmoteCommand) ? "keiner" : entry.RequiredEmoteCommand;
            var gateReason = GetAchievementOrRankGateReason(entry);
            var conditionText = string.IsNullOrEmpty(gateReason) ? "erfüllt" : gateReason;
            Log.Info($"[SightseeingDebug]   {entry.Name}(#{entry.Id}): Position={entry.WorldPosition}, benötigter Emote={emote}, " +
                     $"unlocked={IsAdventureComplete(entry.Id)}, WeatherMask={entry.SightseeingWeatherMask}, " +
                     $"Zeitfenster={(entry.SightseeingHasTimeWindow ? $"{entry.SightseeingFirstBell:00}-{entry.SightseeingLastBell:00}" : "keins")}, " +
                     $"GateQuestId={entry.SightseeingGateQuestId}, NeedsFirstTwenty={entry.SightseeingNeedsFirstTwenty}, Bedingung={conditionText}");
        }
    }

    /// <summary>
    /// Loggt die eigene aktuelle Weltposition + Zone - zum Ermitteln von Ersatz-Koordinaten für
    /// SightseeingApproachOverrides (oder ManualAetheryteWorldPositions): einfach an die gewünschte
    /// Stelle (z.B. 1-2 Yards vor einem Sightseeing-Punkt, der gegen eine Wand läuft) laufen und
    /// diesen Dump auslösen, statt die Koordinaten mit einem Fremd-Tool suchen zu müssen.
    /// </summary>
    public void DumpPlayerPositionDebugInfo()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            Log.Info("[PositionDebug] Kein lokaler Spieler gefunden.");
            return;
        }

        Log.Info($"[PositionDebug] Zone {ClientState.TerritoryType}: Position=({player.Position.X}, {player.Position.Y}, {player.Position.Z})");
    }

    /// <summary>
    /// Einmaliger Debug-Dump aller Aetheryten/Aethernetz-Kristalle der aktuellen Zone (inkl.
    /// Nachbarbezirke einer geteilten Hauptstadt, siehe GetSplitCityTerritories) mit Name + RowId -
    /// erspart das Mitschreiben der "-> nächstes Ziel: Name(#ID)"-Logzeile pro Kristall, wenn für
    /// ManualAetheryteWorldPositions mehrere IDs auf einmal gebraucht werden (z.B. beim
    /// systematischen Nachtragen einer ganzen Stadt).
    /// </summary>
    public void DumpAetheryteDebugInfo()
    {
        var effectiveTerritoryId = ResolveEffectiveTerritoryId(ClientState.TerritoryType);
        var aetherytes = GetLiveZoneEntries(effectiveTerritoryId)
            .Where(e => e.Type == CollectibleType.Aetheryte)
            .OrderBy(e => e.Name)
            .ToList();

        Log.Info($"[AetheryteDebug] Zone {ClientState.TerritoryType} (effektiv {effectiveTerritoryId}): {aetherytes.Count} Aetheryten/Kristalle:");
        foreach (var entry in aetherytes)
        {
            var manual = HasManualAetheryteWorldPosition(entry.Id) ? ", hat bereits manuelle Position" : "";
            Log.Info($"[AetheryteDebug]   {entry.Name}(#{entry.Id}): unlocked={IsAetheryteUnlocked(entry.Id)}{manual}");
        }

        // Temporäre Diagnose - direkt aus dem Lumina-Sheet, ungefiltert (auch Invisible/namenlose
        // Zeilen), für die gerade in SplitCityTerritories gemergten Zonen. Zeigt, ob "fehlende"
        // Kristalle wirklich unter dieser TerritoryId im Sheet stehen oder ob Firmament/Fortemps
        // Manor ihre Aetheryten unter einer ANDEREN TerritoryId führen.
        var rawAetheryteSheet = DataManager.GetExcelSheet<Aetheryte>();
        if (rawAetheryteSheet != null)
        {
            var siblingIds = GetSplitCityTerritories(effectiveTerritoryId);
            Log.Info($"[AetheryteDebug] Ungefilterter Sheet-Dump für Zonen [{string.Join(", ", siblingIds)}]:");
            foreach (var row in rawAetheryteSheet)
            {
                if (!siblingIds.Contains(row.Territory.RowId))
                    continue;

                var rawName = row.IsAetheryte ? row.PlaceName.ValueNullable?.Name.ToString() : row.AethernetName.ValueNullable?.Name.ToString();
                Log.Info($"[AetheryteDebug]   RowId={row.RowId} Territory={row.Territory.RowId} IsAetheryte={row.IsAetheryte} Invisible={row.Invisible} Name=\"{rawName}\"");
            }
        }
    }

    /// <summary>
    /// Einmaliger Debug-Dump zum Befüllen von aethercurrents.json. Der ursprüngliche Plan, die
    /// Position ähnlich wie bei Aetheryten über einen MapMarker-DataType aufzulösen, hat sich
    /// empirisch als Sackgasse erwiesen (über alle 47 Zonen hinweg kein einziger Treffer beim
    /// Abgleich MapMarker.DataKey == AetherCurrent-RowId, siehe Git-Historie dieser Methode) -
    /// Ätherströmungen bekommen anders als Aetheryten offenbar keinen dauerhaften Kartenpin. Der
    /// Dump liefert stattdessen die für die Community-Recherche nötigen Ankerdaten direkt aus
    /// Lumina: Zonenname, die echten AetherCurrent-RowIds (für IsAetherCurrentUnlocked) und - falls
    /// vorhanden - den Namen der Quest, die die jeweilige Strömung freischaltet (Quest-Strömungen
    /// haben keine begehbare Position, nur die "Feld"-Strömungen ohne Quest-Verknüpfung brauchen
    /// Koordinaten aus einem Community-Guide).
    /// </summary>
    public static unsafe void DumpAetherCurrentDebugInfo()
    {
        var territoryId = ClientState.TerritoryType;
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(territoryId, out var territory))
        {
            Log.Info($"[AetherCurrentDebug] TerritoryType-Zeile {territoryId} nicht gefunden.");
            return;
        }

        DumpAetherCurrentDebugInfoForZone(territory, verbose: true);
    }

    /// <summary>
    /// Wie DumpAetherCurrentDebugInfo, aber für ALLE Zonen im Spiel auf einmal, unabhängig davon,
    /// ob der Charakter dort schon war/die Erweiterung freigeschaltet hat - TerritoryType,
    /// AetherCurrentCompFlgSet und MapMarker sind statische, mit dem Spiel ausgelieferte Excel-
    /// Sheets, kein Live-Spielstand, lassen sich also auch mit einem reinen ARR-Charakter komplett
    /// auslesen. Nur IsAetherCurrentUnlocked (echter Spielstand) liefert dann überall "false"; für
    /// die eigentlich gesuchte Positions-/DataType-Zuordnung spielt das keine Rolle.
    /// </summary>
    public static unsafe void DumpAetherCurrentDebugInfoAllZones()
    {
        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null)
        {
            Log.Info("[AetherCurrentDebug] TerritoryType-Sheet nicht gefunden.");
            return;
        }

        Log.Info("[AetherCurrentDebug] Durchsuche ALLE Zonen (auch nicht besuchte) nach Ätherströmungen...");
        var zoneCount = 0;
        foreach (var territory in territorySheet)
        {
            if (territory.AetherCurrentCompFlgSet.RowId == 0)
                continue;
            zoneCount++;
            DumpAetherCurrentDebugInfoForZone(territory, verbose: false);
        }
        Log.Info($"[AetherCurrentDebug] Fertig - {zoneCount} Zonen mit Ätherströmungen durchsucht.");
    }

    private static unsafe void DumpAetherCurrentDebugInfoForZone(TerritoryType territory, bool verbose)
    {
        var territoryId = territory.RowId;
        var compFlgSet = territory.AetherCurrentCompFlgSet.ValueNullable;
        if (compFlgSet == null)
        {
            if (verbose)
                Log.Info($"[AetherCurrentDebug] Zone {territoryId} hat kein AetherCurrentCompFlgSet (keine Ätherströmungen in dieser Zone).");
            return;
        }

        var currentIds = compFlgSet.Value.AetherCurrents
            .Select(c => c.RowId)
            .Where(id => id != 0)
            .ToList();
        if (currentIds.Count == 0)
            return;

        var zoneName = territory.PlaceName.ValueNullable?.Name.ToString() ?? "?";
        var mapId = territory.Map.RowId;
        var currentSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.AetherCurrent>();

        // Ätherströmungen mit einer verknüpften Quest sind reine Quest-Belohnungen (keine begehbare
        // Position, schalten sich automatisch beim Questabschluss frei) - nur die ohne Quest-
        // Verknüpfung ("Feld"-Strömungen) müssen tatsächlich abgelaufen werden und brauchen daher
        // Koordinaten aus einem Community-Guide.
        var fieldIds = new List<uint>();
        var questIds = new List<(uint Id, string QuestName)>();
        foreach (var id in currentIds)
        {
            var questName = currentSheet != null && currentSheet.TryGetRow(id, out var row) && row.Quest.RowId != 0
                ? row.Quest.ValueNullable?.Name.ToString() ?? $"Quest#{row.Quest.RowId}"
                : null;

            if (questName != null)
                questIds.Add((id, questName));
            else
                fieldIds.Add(id);
        }

        Log.Info($"[AetherCurrentDebug] Zone {territoryId} \"{zoneName}\" (MapId={mapId}): {currentIds.Count} Ätherströmungen gesamt, " +
                 $"{fieldIds.Count} Feld-Strömungen (RowIds: [{string.Join(", ", fieldIds)}]), {questIds.Count} Quest-Strömungen.");

        if (!verbose)
            return;

        foreach (var id in fieldIds)
            Log.Info($"[AetherCurrentDebug]   Feld-Strömung #{id}: unlocked={IsAetherCurrentUnlocked(id)}");
        foreach (var (id, questName) in questIds)
            Log.Info($"[AetherCurrentDebug]   Quest-Strömung #{id}: Quest=\"{questName}\", unlocked={IsAetherCurrentUnlocked(id)}");
    }

    /// <summary>
    /// Von Hand nachgetragene Weltpositionen für Hunting-Log-Monster (Schlüssel = RowId aus dem
    /// Lumina-Sheet "MonsterNoteTarget") - anders als Aetheryten/Händler haben roamende Monster
    /// keine feste Kartenkoordinate, es gibt also keine automatische Auflösung wie über
    /// ResolveAetheryteWorldPosition. Siehe HuntingLogPositions.cs für Quelle/Umrechnung; deckt
    /// aktuell alle 9 ARR-Basisklassen ab. Fehlt ein Monster (z.B. spätere Job-eigene Logs), einfach
    /// mit "/vnav moveto x y z" bzw. "/pos" im Spiel einen Punkt suchen und dort ergänzen.
    /// </summary>
    private static readonly Dictionary<uint, Vector3> ManualHuntingLogPositions = HuntingLogPositions.Positions;

    /// <summary>
    /// Löst eine ClassJob-RowId auf die BASISKLASSE auf, unter der das Hunting Log tatsächlich
    /// geführt wird (z.B. Paladin #19 -> Gladiator #1) - per Debug-Dump bestätigt: Sobald ein
    /// Charakter zum Job aufgestiegen ist, liefert player.ClassJob.RowId die JOB-RowId (z.B. 19 für
    /// Paladin), die Hunting-Log-Zeilen im Lumina-Sheet "MonsterNote" sind aber weiterhin unter der
    /// BASISKLASSEN-RowId (1) einsortiert - mit der Job-RowId direkt in der ClassId*10000-Formel
    /// gerechnet, wurden dadurch nie existierende Zeilen (z.B. 190011) gesucht, alle Einträge des
    /// Jobs blieben unsichtbar. Lumina.ClassJob.ClassJobParent zeigt für Jobs auf ihre Basisklasse
    /// (0 = keine, d.h. selbst schon eine Basisklasse/ein Handwerks-/Sammelberuf).
    /// </summary>
    private static uint ResolveHuntingLogClassId(uint classJobRowId)
    {
        var classJobSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (classJobSheet != null && classJobSheet.TryGetRow(classJobRowId, out var row) && row.ClassJobParent.RowId != 0)
            return row.ClassJobParent.RowId;

        return classJobRowId;
    }

    // Höchstens so viele Zehner-Stufen je Klasse (ARR-Basisklassen haben 5) - nicht vorhandene
    // MonsterNote-Zeilen werden in GetHuntingLogEntries ohnehin übersprungen.
    private const int MaxHuntingLogTiers = 10;

    /// <summary>
    /// Berechnet die Hunting-Log-Einträge des AKTUELL AKTIVEN Rangs der AKTUELLEN Klasse, die zur
    /// übergebenen Zone gehören - live pro Frame berechnet (nicht gecacht wie GetLiveZoneEntries,
    /// da sich der Kill-Fortschritt laufend ändert). Quelle ist Slot 0 von FFXIVClientStructs'
    /// MonsterNoteManager, der (empirisch per Debug-Dump bestätigt, siehe DumpHuntingLogDebugInfo)
    /// immer die Daten der GERADE AKTIVEN Klasse enthält, nicht einen festen Klassen-Slot.
    ///
    /// WICHTIG (live per Screenshot korrigiert): MonsterNoteRankInfo.Rank ist NICHT der eine
    /// gerade aktive Einzel-Rang, sondern die aktuelle ZEHNER-STUFE ("Rank" == 0 entspricht der im
    /// Spiel links angezeigten Gruppe "RANK 1", die 10 Teil-Ränge "Klasse 01".."Klasse 10" bündelt,
    /// alle gleichzeitig sichtbar/offen). Innerhalb dieser Stufe zählt RankData[0..9] den
    /// Fortschritt JEDES der 10 Teil-Ränge parallel (Index 3 z.B. "Klasse 04"). Jede Lumina-
    /// "MonsterNote"-Zeile ist EIN Teil-Rang (mit bis zu 4 Zielen + paralleler Count-Anforderung);
    /// die RowId dafür kommt über dieselbe Formel wie AgentMonsterNote.GetMonsterNoteIdForIndex
    /// (ClassId * 10000 + Stufe*10 + Teil-Rang-Index + 1) - ClassId ist dabei bewusst die BASISKLASSE,
    /// nicht der aktuelle Job (siehe ResolveHuntingLogClassId, per Debug-Dump bestätigt: als Paladin
    /// wird trotzdem unter Gladiator #1 gesucht). Bereits abgeschlossene Teil-Ränge (alle Ziele
    /// erreicht) werden nicht mehr angezeigt - das Spiel selbst hakt sie dann ab, statt sie weiter
    /// als "zu tun" zu listen.
    /// </summary>
    public unsafe List<CollectibleEntry> GetHuntingLogEntries(uint territoryId)
    {
        var result = new List<CollectibleEntry>();

        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return result;

        var classId = ResolveHuntingLogClassId(player.ClassJob.RowId);
        var className = player.ClassJob.ValueNullable?.Name.ToString();
        if (classId == 0 || string.IsNullOrEmpty(className))
            return result;

        var manager = MonsterNoteManager.Instance();
        if (manager == null)
            return result;

        var slot = manager->RankData[0];
        var tier = slot.Rank;

        var noteSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.MonsterNote>();
        if (noteSheet == null)
            return result;

        var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(territoryId, out var territoryRow))
            return result;

        var zonePlaceNameId = territoryRow.PlaceName.RowId;

        // Neben der aktiven Stufe auch alle HÖHEREN (noch nicht erreichten) Stufen anzeigen -
        // markiert als "Bedingung nicht erfüllt" (siehe CollectibleEntry.HuntingLogRequiredRank),
        // da Kills dort noch nicht zählen. Niedrigere Stufen sind per Definition bereits komplett
        // (das Spiel schaltet erst nach allen 10 Teil-Rängen weiter). Dasselbe Monster kann in
        // mehreren Stufen vorkommen - dann nur einmal (die niedrigste Stufe gewinnt), sonst gäbe es
        // doppelte Einträge mit derselben Id (siehe z.B. ImGui-IDs im Overlay, StillNeeded).
        var addedTargetIds = new HashSet<uint>();
        for (var noteTier = tier; noteTier < MaxHuntingLogTiers; noteTier++)
        {
            var isLockedTier = noteTier > tier;
            for (var subRank = 0; subRank < 10; subRank++)
            {
                var monsterNoteRowId = (uint)(classId * 10000 + noteTier * 10 + subRank + 1);
                if (!noteSheet.TryGetRow(monsterNoteRowId, out var note))
                    continue;

                // Fortschritt gibt es nur für die aktive Stufe - höhere starten bei 0.
                var rankCounts = new int[4];
                if (!isLockedTier)
                {
                    var liveCounts = slot.RankData[subRank];
                    for (var i = 0; i < 4; i++)
                        rankCounts[i] = liveCounts[i];
                }

                // Teil-Rang schon komplett (alle seine Ziele erreicht)? Dann überspringen - siehe
                // Klassenkommentar oben.
                var isComplete = !isLockedTier;
                for (var i = 0; i < 4 && isComplete; i++)
                {
                    if (note.MonsterNoteTarget[i].RowId != 0 && rankCounts[i] < note.Count[i])
                        isComplete = false;
                }

                if (isComplete)
                    continue;

                AddHuntingLogTargets(note, rankCounts, isLockedTier, noteTier, subRank);
            }
        }

        return result;

        void AddHuntingLogTargets(Lumina.Excel.Sheets.MonsterNote note, int[] rankCounts, bool isLockedTier, int noteTier, int subRank)
        {
            for (var i = 0; i < 4; i++)
            {
                var targetRef = note.MonsterNoteTarget[i];
                if (targetRef.RowId == 0)
                    continue;

                var target = targetRef.ValueNullable;
                if (target == null)
                    continue;

                var requiredCount = note.Count[i];
                var currentCount = rankCounts[i];
                if (currentCount >= requiredCount)
                    continue;

                // Nur zeigen, wenn dieses Monster laut Sheet auch tatsächlich in der übergebenen
                // Zone vorkommt - MonsterNoteTarget listet dafür bis zu 3 mögliche Zonen
                // (PlaceNameZone).
                var isInZone = false;
                foreach (var placeNameZone in target.Value.PlaceNameZone)
                {
                    if (placeNameZone.RowId != 0 && placeNameZone.RowId == zonePlaceNameId)
                    {
                        isInZone = true;
                        break;
                    }
                }

                if (!isInZone)
                    continue;

                var monsterName = target.Value.BNpcName.ValueNullable?.Singular.ToString();
                if (string.IsNullOrEmpty(monsterName))
                    continue;

                if (!addedTargetIds.Add(target.Value.RowId))
                    continue;

                ManualHuntingLogPositions.TryGetValue(target.Value.RowId, out var manualPosition);

                result.Add(new CollectibleEntry
                {
                    Id = target.Value.RowId,
                    Name = $"{monsterName} ({currentCount}/{requiredCount})",
                    Type = CollectibleType.HuntingLog,
                    Category = Loc.T("Hunting Log", "Hunting Log"),
                    TerritoryTypeId = territoryId,
                    MapId = territoryRow.Map.RowId,
                    WorldPosition = manualPosition == default ? null : manualPosition,
                    BNpcNameId = target.Value.BNpcName.RowId,
                    Source = $"{className} {noteTier * 10 + subRank + 1:00}",
                    HuntingLogRequiredRank = isLockedTier ? noteTier + 1 : null,
                });
            }
        }
    }

    /// <summary>
    /// Prüft, ob der Spieler aktuell genug von JEDER benötigten Währung besitzt, um den Eintrag zu
    /// kaufen - bei mehreren gleichzeitig benötigten Währungen (siehe
    /// CollectibleEntry.AdditionalCurrencies, z.B. Triple-Triad-Karte "G-Warrior") müssen ALLE
    /// davon erfüllt sein, nicht nur die erste.
    /// </summary>
    public bool CanAfford(CollectibleEntry entry)
    {
        if (entry.CurrencyItemId == 0 || entry.CurrencyAmount == 0)
            return false;

        if (GetCurrencyAmount(entry.CurrencyItemId) < entry.CurrencyAmount)
            return false;

        if (entry.AdditionalCurrencies != null)
        {
            foreach (var additional in entry.AdditionalCurrencies)
            {
                if (additional.CurrencyItemId == 0 || additional.CurrencyAmount == 0)
                    return false;
                if (GetCurrencyAmount(additional.CurrencyItemId) < additional.CurrencyAmount)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Liefert die aktuell besessene Menge einer Währung/eines Items.
    /// Gil (Item-ID 1) liegt nicht im normalen Inventar und braucht daher GetGil() statt GetInventoryItemCount().
    /// </summary>
    public unsafe uint GetCurrencyAmount(uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return 0;

        return itemId == 1
            ? inventoryManager->GetGil()
            : (uint)inventoryManager->GetInventoryItemCount(itemId);
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        CompactOverlayWindow.Dispose();
        NavigationArrowWindow.Dispose();
        QuestAutomation.Dispose();
        CombatPluginInstance.Dispose();

        // Beim Entladen (z.B. Dev-Neuladen) mitten in der Triple-Triad-Automation: Saucy stoppen und
        // dessen "Fenster automatisch öffnen"-Option wiederherstellen (siehe TripleTriadAutomation.Stop).
        if (TripleTriadAutomation.IsActive)
            TripleTriadAutomation.Stop();

        CommandManager.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUI;
    }
}

/// <summary>
/// Erkennt "gegen eine Wand/Geometriekante festhängen" während eines aktiven vnavmesh-Laufwegs -
/// die lokale Steuerung von vnavmesh (SimpleMove) kann an Ecken/schmalen Durchgängen steckenbleiben,
/// obwohl Path.IsRunning weiterhin true bleibt und der Weg an sich gültig wäre. Jede Automation mit
/// eigenem Laufweg hält eine eigene Instanz (Reset() beim Start eines neuen Wegabschnitts,
/// CheckStuck() pro Tick während pathIsRunning true ist) - siehe z.B. AetheryteAutomation.BeginPathfind/
/// UpdateMoving für die Verdrahtung.
/// </summary>
/// <summary>
/// Beritten zu Fuß losgelaufen, weil gerade nicht geflogen werden konnte (Flugverbots-Bereich wie
/// "The Eight Sentinels", siehe Plugin.CanFly) - sobald der Bereich verlassen ist und noch ein
/// weiter Weg bleibt, soll die Automation einmal fliegend neu planen, statt den ganzen Weg am Boden
/// zurückzulegen. Jede Automation mit eigenem Laufweg hält eine Instanz: OnPathStarted(...) nach
/// jedem angenommenen Laufauftrag, ShouldReplanFlying(...) pro Tick während der Weg läuft.
/// </summary>
public sealed class FlightPathUpgrade
{
    // Darunter lohnt das Neuplanen (Abheben/Aufsteigen) nicht mehr.
    private const float MinRemainingDistance = 40f;

    // Kurz warten, nachdem Fliegen möglich wurde (CanFly kann am Bereichsrand kurz flackern).
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    // Höchstens so oft umplanen - lehnt vnavmesh den Flugweg ab (z.B. Ziel selbst liegt im
    // Flugverbots-Bereich), würde sonst jede Sekunde neu geplant.
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromSeconds(30);

    private bool pathIsFlying = true;
    private DateTime? canFlySince;
    private DateTime lastAttemptAt = DateTime.MinValue;

    /// <summary>Nach jedem angenommenen Laufauftrag - flying = ob er fliegend angenommen wurde.</summary>
    public void OnPathStarted(bool flying)
    {
        pathIsFlying = flying;
        canFlySince = null;
    }

    public bool ShouldReplanFlying(Vector3 playerPosition, Vector3 destination)
    {
        if (pathIsFlying || !Plugin.Condition[ConditionFlag.Mounted] || !Plugin.CanFly)
        {
            canFlySince = null;
            return false;
        }

        if (Vector3.Distance(playerPosition, destination) < MinRemainingDistance || DateTime.UtcNow - lastAttemptAt < AttemptCooldown)
            return false;

        canFlySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - canFlySince.Value < SettleDelay)
            return false;

        lastAttemptAt = DateTime.UtcNow;
        canFlySince = null;
        Plugin.Log.Info("[FlightPathUpgrade] Flugverbots-Bereich verlassen - plane fliegend neu.");
        return true;
    }
}

public sealed class NavigationStuckDetector
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(4);
    private const float MinProgressDistance = 1.5f;

    private Vector3? lastPosition;
    private DateTime lastCheckAt = DateTime.MinValue;

    public void Reset()
    {
        lastPosition = null;
        lastCheckAt = DateTime.MinValue;
    }

    /// <summary>
    /// true, wenn sich der Charakter seit der letzten Prüfung (CheckInterval) um weniger als
    /// MinProgressDistance bewegt hat. Startet nach jedem Reset()/true-Ergebnis wieder bei null, prüft
    /// also nur alle paar Sekunden statt jeden Frame.
    /// </summary>
    public bool CheckStuck(Vector3 currentPosition)
    {
        if (lastCheckAt == DateTime.MinValue)
        {
            lastPosition = currentPosition;
            lastCheckAt = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow - lastCheckAt < CheckInterval)
            return false;

        var stuck = lastPosition.HasValue && Vector3.Distance(lastPosition.Value, currentPosition) < MinProgressDistance;
        lastPosition = currentPosition;
        lastCheckAt = DateTime.UtcNow;
        return stuck;
    }
}
