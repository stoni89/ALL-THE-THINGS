using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using AllTheThings.Windows;

namespace AllTheThings;

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

    private const string CommandName = "/att";

    // Für den Zugriff aus statischen Methoden (z.B. TryUseSprint), die keine Plugin-Instanz haben -
    // es gibt zur Laufzeit ohnehin immer nur genau eine.
    private static Plugin instance = null!;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("AllTheThings");
    private MainWindow MainWindow { get; init; }
    public CompactOverlayWindow CompactOverlayWindow { get; init; }
    public QuestAutomation QuestAutomation { get; init; }
    public AetheryteAutomation AetheryteAutomation { get; init; }

    public Plugin()
    {
        instance = this;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.SanitizeTypeOrder();

        QuestAutomation = new QuestAutomation();
        AetheryteAutomation = new AetheryteAutomation();

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CompactOverlayWindow = new CompactOverlayWindow(this) { IsOpen = Configuration.ShowCompactOverlay };
        WindowSystem.AddWindow(CompactOverlayWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.T("Öffnet All The Things.", "Opens All The Things.")
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
            CollectibleType.FrameKit => PlayerState.Instance()->IsFramersKitUnlocked(entry.Id),
            CollectibleType.Aetheryte => IsAetheryteUnlocked(entry.Id),
            CollectibleType.Quest => QuestManager.IsQuestComplete((ushort)entry.Id),
            _ => false,
        };
    }

    /// <summary>
    /// Eigenständig aufrufbar (nicht nur über IsOwned) - wird von der Aetheryten-Automation
    /// benutzt, um nach dem Interagieren auf den tatsächlichen Freischalt-Abschluss zu warten
    /// (der Entdecken-Cast braucht ein paar Sekunden, bis er durchläuft).
    /// </summary>
    public static unsafe bool IsAetheryteUnlocked(uint aetheryteId) => UIState.Instance()->IsAetheryteUnlocked(aetheryteId);

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
        [130] = new[] { 130u, 131u },   // Ul'dah (Steps of Nald / Steps of Thal)
        [131] = new[] { 130u, 131u },
        [128] = new[] { 128u, 129u },   // Limsa Lominsa (Upper / Lower Decks)
        [129] = new[] { 128u, 129u },
        [132] = new[] { 132u, 133u },   // Gridania (New / Old)
        [133] = new[] { 132u, 133u },
        [418] = new[] { 418u, 419u },   // Ishgard (Foundation / The Pillars)
        [419] = new[] { 418u, 419u },
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
    /// <summary>
    /// Verwirft den Zonen-Cache für Aetheryten/Quests, damit die nächste Abfrage neu berechnet
    /// wird (z.B. für den "Cache zurücksetzen"-Knopf im Debug-Tab).
    /// </summary>
    public void ResetLiveEntriesCache()
    {
        liveEntriesZoneId = null;
        liveEntriesCache = new List<CollectibleEntry>();
    }

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
                    if (row.IsRepeatable)
                        continue;
                    if (row.BeastTribe.RowId != 0)
                        continue;
                    if (row.JournalGenre.RowId == 0)
                        continue;

                    // Hauptquests (MSQ) gehören nicht in eine Sammelobjekt-Übersicht - erkannt über
                    // die JournalSection (0 = "Main Scenario" ARR-EW, 1 = "Main Scenario" Dawntrail).
                    var journalSection = row.JournalGenre.ValueNullable?.JournalCategory.ValueNullable?.JournalSection.RowId;
                    if (journalSection is 0 or 1)
                        continue;

                    // Saisonale Event-Quests (Winterstern, Valentionstag, etc.) nur zeigen, wenn das
                    // zugehörige Event aktuell auch wirklich läuft - sonst wären sie "annehmbar"
                    // laut Datenbank, aber im Spiel gerade gar nicht verfügbar.
                    if (row.Festival.RowId != 0 && !IsFestivalActive((ushort)row.Festival.RowId))
                        continue;

                    // Klassengebundene Quests bewusst ausklammern - aber nicht nur Kategorie 1 ("All
                    // Classes") akzeptieren, sondern jede Kategorie, deren Name mit "All" beginnt
                    // (z.B. Kategorie 130 "All classes and jobs (excluding limited jobs)"). Das
                    // deckt die meisten normalen Quests ab, die nur Limited Jobs wie Blue Mage ausschließen.
                    // WICHTIG: Der Name muss explizit auf Englisch abgefragt werden - row.ClassJobCategory0
                    // liefert sonst den Namen in der Spielclient-Sprache (z.B. Deutsch "Alle Klassen"),
                    // der nie mit "All" beginnt und dadurch ausnahmslos JEDE Quest ausgeschlossen hätte.
                    var categoryName = GetEnglishClassJobCategoryName(row.ClassJobCategory0.RowId);
                    if (!categoryName.StartsWith("All", StringComparison.Ordinal))
                        continue;
                    if (row.ClassJobLevel[0] > playerLevel)
                        continue;

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
                        continue;

                    var name = row.Name.ToString();
                    if (string.IsNullOrEmpty(name))
                        continue;

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
    /// Öffnet die Ingame-Karte mit einer Flagge auf der Händler-Position des Eintrags.
    /// </summary>
    public static void OpenVendorMap(CollectibleEntry entry)
    {
        if (!entry.HasVendorLocation)
            return;

        var territoryForFlag = entry.FlagTerritoryTypeId ?? entry.TerritoryTypeId;
        var payload = new MapLinkPayload(territoryForFlag, entry.MapId, entry.VendorMapX, entry.VendorMapY);
        GameGui.OpenMapWithMapLink(payload);
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
            return false;

        if (Condition[ConditionFlag.Mounted])
            return false;

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

        var mountName = mount.Singular.ToString();
        if (string.IsNullOrEmpty(mountName))
            return false;

        try
        {
            CommandManager.ProcessCommand($"/mount \"{mountName}\"");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Rufen des Mounts für die Aetheryten-Automation.");
            return false;
        }

        return true;
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
        QuestAutomation.Dispose();

        CommandManager.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUI;
    }
}
