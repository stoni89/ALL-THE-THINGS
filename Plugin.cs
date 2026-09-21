using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
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

    public Plugin()
    {
        instance = this;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.SanitizeTypeOrder();

        navigationFlagToPointQuery = PluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");

        QuestAutomation = new QuestAutomation();
        AetheryteAutomation = new AetheryteAutomation();
        GoToAutomation = new GoToAutomation();
        HuntingLogAutomation = new HuntingLogAutomation();
        AetherCurrentAutomation = new AetherCurrentAutomation();
        SightseeingAutomation = new SightseeingAutomation();
        ChocobokeepAutomation = new ChocobokeepAutomation();

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
            CollectibleType.Aetheryte => IsAetheryteUnlocked(entry.Id),
            CollectibleType.AetherCurrent => IsAetherCurrentUnlocked(entry.Id),
            CollectibleType.Sightseeing => IsAdventureComplete(entry.Id),
            CollectibleType.Quest => QuestManager.IsQuestComplete((ushort)entry.Id),
            CollectibleType.Chocobokeep => IsChocoboTaxiStandUnlocked(entry.Id),
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
    public static unsafe bool CanFly => PlayerState.Instance()->CanFly;

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
    /// Ob ein Sammel-Typ in der aktuellen Zone gerade überhaupt machbar ist - für Sightseeing
    /// braucht man dafür Fliegen (viele Punkte sind sonst gar nicht erreichbar) und das
    /// freigeschaltete Log selbst (siehe IsSightseeingLogUnlocked). Bewusst NICHT für AetherCurrent -
    /// genau umgekehrtes Henne-Ei-Problem: Ätherströmungen muss man erst einsammeln, UM Fliegen in
    /// der Zone überhaupt erst freizuschalten (siehe AetherCurrentAutomation.BeginPathfind) - sie
    /// hinter CanFly zu verstecken würde das Feature genau dann unbrauchbar machen, wenn man es am
    /// meisten braucht. Auch NICHT für HuntingLog (auf expliziten Wunsch wieder entfernt). Wird
    /// nicht zum Ausfiltern benutzt (Einträge bleiben sichtbar), sondern nur zum Ausgrauen in
    /// Liste/Filter/Reihenfolge.
    /// </summary>
    public static bool IsTypeCurrentlyPossible(CollectibleType type) => type switch
    {
        CollectibleType.Sightseeing => IsSightseeingLogUnlocked() && CanFly,
        _ => true,
    };

    /// <summary>
    /// Erklärtext fürs Ausgrauen (siehe IsTypeCurrentlyPossible). Für Sightseeing bewusst immer
    /// derselbe Text (nicht mehr zwischen "Log nicht freigeschaltet" und "Fliegen nicht
    /// freigeschaltet" unterschieden) - CanFly kann in Zonen ohne jede Flug-Freischaltung leicht
    /// fälschlich als Grund erscheinen, obwohl eigentlich das Log selbst fehlt.
    /// </summary>
    public static string GetTypeNotPossibleReason(CollectibleType type)
    {
        if (type == CollectibleType.Sightseeing)
            return Loc.T("Sightseeing Log noch nicht freigeschaltet", "Sightseeing Log not unlocked yet");

        return Loc.T("Noch nicht möglich", "Not possible yet");
    }

    private static Dictionary<string, (uint TerritoryId, uint MapId)>? zoneByPlaceNameCache;

    /// <summary>
    /// Trägt TerritoryTypeId/MapId für Einträge nach, deren JSON-Datei nur den Fundort als
    /// Klartext in Source kennt (z.B. "The Clyteum" bei einem Dungeon-Truhen-Drop), aber keine
    /// Zone - betrifft vor allem Notenrollen/Minions/Triple-Triad-Karten mit Category "Dungeon"
    /// (siehe CollectionData.GetAllEntries). Gleicht Source gegen die PlaceName-Spalte des Lumina-
    /// Sheets "TerritoryType" ab (case-insensitive, führende/nachgestellte "*" wie bei "*The
    /// Merchant's Tale*" entfernt) - Einträge, für die keine Übereinstimmung gefunden wird, bleiben
    /// unverändert (TerritoryTypeId weiterhin 0, kein Rückschritt gegenüber vorher).
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

        foreach (var entry in entries)
        {
            if (entry.TerritoryTypeId != 0 || string.IsNullOrEmpty(entry.Source))
                continue;

            var source = entry.Source.Trim().Trim('*');
            if (!zoneByPlaceNameCache.TryGetValue(source, out var zone))
                continue;

            entry.TerritoryTypeId = zone.TerritoryId;
            entry.MapId = zone.MapId;
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
        var dungeonEntries = entries.Where(e => e.Category.Contains("Dungeon", StringComparison.OrdinalIgnoreCase)).ToList();
        var stillMissing = dungeonEntries.Where(e => e.TerritoryTypeId == 0).ToList();

        Log.Info($"[ZoneEnrichmentDebug] {dungeonEntries.Count} Dungeon-Einträge insgesamt, {stillMissing.Count} davon noch ohne Zone:");
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
    private readonly record struct FrameKitShopMatch(uint ShopId, uint CurrencyAmount, uint CurrencyItemId, uint CurrencyIconId, string CurrencyText);

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
                                continue;

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
                    entry.Source = $"{vendorName} - {match.CurrencyText}";
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
    public static bool IsSeasonalEventEntryCurrentlyActive(CollectibleEntry entry)
    {
        if (entry.Category != "Saisonevent")
            return true;

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
        [128] = new[] { 27u, 500u },   // Limsa Lominsa Upper Decks
        [129] = new[] { 27u, 500u },   // Limsa Lominsa Lower Decks
        [132] = new[] { 39u, 506u },   // New Gridania
        [133] = new[] { 39u, 506u },   // Old Gridania
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
    /// Quest + Voraussetzung nennt, siehe GetRequirementInfo.
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
    };

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
    /// Hinweistext + Wiki-Link für Einträge mit manuell erfasster Zusatz-Voraussetzung (siehe
    /// QuestRequiredMounts/MountRequiredQuest) - bewusst NICHT im CollectibleEntry selbst gespeichert
    /// (die Mount-Liste kommt aus der einmal geladenen/für die ganze Sitzung gecachten
    /// CollectionData.GetAllEntries() und würde sonst nach Erfüllen der Voraussetzung nicht mehr
    /// aktualisiert), sondern jeden Frame frisch geprüft, genau wie IsOwned/CanAfford. Gibt (null,
    /// null) zurück, wenn keine Voraussetzung bekannt ist oder sie bereits erfüllt ist.
    /// </summary>
    public unsafe (string? Note, string? WikiUrl) GetRequirementInfo(CollectibleEntry entry)
    {
        if (entry.Type == CollectibleType.Quest && QuestRequiredMounts.TryGetValue(entry.Name, out var requiredMounts))
        {
            var mountIdsByName = CollectionData.GetAllEntries()
                .Where(e => e.Type == CollectibleType.Mount)
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            foreach (var mountName in requiredMounts)
            {
                if (!mountIdsByName.TryGetValue(mountName, out var mountId) || !PlayerState.Instance()->IsMountUnlocked(mountId))
                    return (Loc.T("(Voraussetzung nicht erfüllt)", "(requirement not met)"), GetWikiUrl(entry.Name));
            }

            return (null, null);
        }

        if (entry.Type == CollectibleType.Mount && MountRequiredQuest.TryGetValue(entry.Name, out var requiredQuest))
        {
            var questId = ResolveQuestIdByName(requiredQuest);
            if (questId == null || !QuestManager.IsQuestComplete((ushort)questId.Value))
                return ($"(Quest: {requiredQuest})", GetWikiUrl(requiredQuest));

            return (null, null);
        }

        return (null, null);
    }

    /// <summary>
    /// Wiki-Seite für eine Quest mit Zusatz-Voraussetzung (siehe QuestRequiredMounts/
    /// MountRequiredQuest) - das Consolegameswiki verwendet als URL einfach den Quest-Anzeigenamen
    /// mit Leerzeichen durch Unterstriche ersetzt (Satzzeichen wie Kommas bleiben unverändert
    /// stehen), siehe z.B. https://ffxiv.consolegameswiki.com/wiki/Fiery_Wings,_Fiery_Hearts.
    /// </summary>
    private static string GetWikiUrl(string questName) =>
        "https://ffxiv.consolegameswiki.com/wiki/" + questName.Replace(' ', '_');

    /// <summary>
    /// Prüft alle Freischalt-/Annehmbarkeits-Bedingungen einer Quest AUSSER dem Vergabeort (den
    /// prüft ComputeLiveZoneEntries zusätzlich selbst gegen acceptablePlaceNameIds der jeweiligen
    /// Zone) - ausgelagert, damit dieselbe Logik auch zonenunabhängig für die globale Statistik
    /// (siehe GetAllTrackedQuestIds/MainWindow.DrawStatisticsPage) genutzt werden kann, ohne sie
    /// doppelt zu pflegen.
    /// </summary>
    private unsafe bool IsQuestCurrentlyAcceptable(Quest row, byte playerLevel)
    {
        if (row.IsRepeatable)
            return false;
        if (row.BeastTribe.RowId != 0)
            return false;
        if (row.JournalGenre.RowId == 0)
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
        if (questSheet != null && playerLevel > 0)
        {
            foreach (var row in questSheet)
            {
                try
                {
                    if (!acceptablePlaceNameIds.Contains(row.PlaceName.RowId))
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

        // Sightseeing-Log-Einträge ("Adventure" im Lumina-Sheet) - anders als Ätherströmungen haben
        // diese eine echte Weltposition direkt im Sheet (über die verlinkte "Level"-Zeile), kein
        // Community-Export nötig. Achsen-Umrechnung (Level.X/Z/Y -> Welt X/Y/Z) und der +0.5f
        // Höhenversatz sind vom Dalamud-Plugin "Tourist" übernommen (dessen MarkerService setzt
        // exakt dieselbe VFX-Markierung an dieser Position).
        var adventureSheet = DataManager.GetExcelSheet<Adventure>();
        if (adventureSheet != null)
        {
            foreach (var row in adventureSheet)
            {
                try
                {
                    var level = row.Level.ValueNullable;
                    if (level == null || level.Value.Territory.RowId != territoryId)
                        continue;

                    var name = row.Name.ToString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var emoteCommand = row.Emote.ValueNullable?.TextCommand.ValueNullable?.Command.ToString();

                    result.Add(new CollectibleEntry
                    {
                        Id = row.RowId,
                        Name = name,
                        Type = CollectibleType.Sightseeing,
                        Category = Loc.T("Sightseeing", "Sightseeing"),
                        TerritoryTypeId = territoryId,
                        MapId = level.Value.Map.RowId,
                        WorldPosition = new Vector3(level.Value.X, level.Value.Z, level.Value.Y),
                        RequiredEmoteCommand = string.IsNullOrEmpty(emoteCommand) ? null : emoteCommand,
                        Source = Loc.T("Sightseeing", "Sightseeing"),
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Fehler bei Adventure-Zeile {row.RowId}");
                }
            }
        }

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
    public string GetZoneName(uint territoryTypeId)
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
    /// Öffnet die Ingame-Karte mit einer Flagge auf der Händler-Position des Eintrags.
    /// </summary>
    public static void OpenVendorMap(CollectibleEntry entry)
    {
        if (!entry.HasVendorLocation)
            return;

        var territoryForFlag = entry.FlagTerritoryTypeId ?? entry.TerritoryTypeId;
        var payload = new MapLinkPayload(territoryForFlag, entry.MapId, entry.VendorMapX, entry.VendorMapY);
        GameGui.OpenMapWithMapLink(payload);

        // Für den Wegweiser-Pfeil (siehe NavigationTargetPosition) - bevorzugt über dieselbe vnavmesh-
        // IPC aufgelöst, die die Automationen auch fürs tatsächliche Laufen benutzen (steht nur zur
        // Verfügung, wenn man gerade in genau dieser Zone steht, da vnavmesh die Flagge gegen das
        // aktuell geladene Navmesh auflöst) - das zeigt garantiert exakt denselben Punkt, den die
        // Automation ansteuern würde. Sonst Fallback auf die eigene Umrechnung der Kartenkoordinate
        // zurück in eine rohe Weltposition (Umkehrung von MapUtil.WorldToMap, dieselbe Formel wie in
        // HuntingLogPositions.cs dokumentiert/ResolveAetheryteWorldPosition benutzt).
        Vector3? navigationWorldPosition = null;
        if (ClientState.TerritoryType == territoryForFlag && navigationFlagToPointQuery is { HasFunction: true } query)
        {
            try { navigationWorldPosition = query.InvokeFunc(); }
            catch { /* vnavmesh nicht bereit - siehe Fallback unten */ }
        }

        if (navigationWorldPosition == null)
        {
            var mapSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            if (mapSheet != null && mapSheet.TryGetRow(entry.MapId, out var map) && map.SizeFactor != 0)
            {
                var worldX = (entry.VendorMapX - 1f) * 50f - 102400f / map.SizeFactor - map.OffsetX;
                var worldZ = (entry.VendorMapY - 1f) * 50f - 102400f / map.SizeFactor - map.OffsetY;
                navigationWorldPosition = new Vector3(worldX, 0, worldZ);
            }
        }

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
    public static void OpenEntryMap(CollectibleEntry entry)
    {
        if (entry.HasVendorLocation)
        {
            OpenVendorMap(entry);
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
    public static unsafe void TryDismount()
    {
        if (!Condition[ConditionFlag.Mounted])
            return;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return;

        actionManager->UseAction(ActionType.GeneralAction, DismountGeneralActionId);
    }

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
        var classId = player?.ClassJob.RowId ?? 0;
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
            Log.Info($"[SightseeingDebug]   {entry.Name}(#{entry.Id}): Position={entry.WorldPosition}, benötigter Emote={emote}, " +
                     $"unlocked={IsAdventureComplete(entry.Id)}");
        }
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
    /// (ClassId * 10000 + Stufe*10 + Teil-Rang-Index + 1) - ClassId wird dabei mit der Lumina-
    /// ClassJob-RowId gleichgesetzt (beim Gladiator sind beide 1, für andere Klassen noch nicht
    /// querverifiziert). Bereits abgeschlossene Teil-Ränge (alle Ziele erreicht) werden nicht mehr
    /// angezeigt - das Spiel selbst hakt sie dann ab, statt sie weiter als "zu tun" zu listen.
    /// </summary>
    public unsafe List<CollectibleEntry> GetHuntingLogEntries(uint territoryId)
    {
        var result = new List<CollectibleEntry>();

        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return result;

        var classId = player.ClassJob.RowId;
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

        for (var subRank = 0; subRank < 10; subRank++)
        {
            var monsterNoteRowId = (uint)(classId * 10000 + tier * 10 + subRank + 1);
            if (!noteSheet.TryGetRow(monsterNoteRowId, out var note))
                continue;

            var rankCounts = slot.RankData[subRank];

            // Teil-Rang schon komplett (alle seine Ziele erreicht)? Dann überspringen - siehe
            // Klassenkommentar oben.
            var isComplete = true;
            for (var i = 0; i < 4; i++)
            {
                if (note.MonsterNoteTarget[i].RowId != 0 && rankCounts[i] < note.Count[i])
                {
                    isComplete = false;
                    break;
                }
            }

            if (isComplete)
                continue;

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
                    Source = $"{className} {tier * 10 + subRank + 1:00}",
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Prüft, ob der Spieler aktuell genug von der benötigten Währung besitzt, um den Eintrag zu kaufen.
    /// </summary>
    public bool CanAfford(CollectibleEntry entry)
    {
        if (entry.CurrencyItemId == 0 || entry.CurrencyAmount == 0)
            return false;

        return GetCurrencyAmount(entry.CurrencyItemId) >= entry.CurrencyAmount;
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
