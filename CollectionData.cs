using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllTheThings;

/// <summary>
/// Ein einzelnes sammelbares Objekt (Mount, Minion, ...) mit Fundort-Info.
/// Diese Klasse ist bewusst simpel gehalten - die eigentliche Datenbasis
/// (welches Item in welcher Zone droppt) musst du separat aufbauen, z.B.
/// aus einem JSON-Export von Community-Datenbanken wie Garland Tools,
/// FFXIV Collect oder Teamcraft.
/// </summary>
public class CollectibleEntry
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public CollectibleType Type { get; init; }
    public string Category { get; init; } = string.Empty; // Beschaffungsart, z.B. "Quest", "Errungenschaft", "Echtgeld-Shop"
    public uint TerritoryTypeId { get; init; } // Zone, in der der Eintrag angezeigt wird (Lumina "TerritoryType" Sheet)
    public uint MapId { get; init; } // Lumina "Map" Sheet, für MapLinkPayload benötigt

    // Für Kartenlinks, deren Flagge auf einer ANDEREN Karte liegt als die Zone, in der der Eintrag
    // angezeigt wird (z.B. ein Aethernetz-Kristall, der laut Spiel auf der Nachbarkarte markiert
    // wird) - null bedeutet "gleiche Zone wie TerritoryTypeId" (Normalfall).
    public uint? FlagTerritoryTypeId { get; init; }
    public string Vendor { get; init; } = string.Empty; // Händler-/NPC-Name, falls per Kauf erhältlich
    public float VendorMapX { get; init; } // Kartenkoordinate des Händlers (0 = unbekannt)
    public float VendorMapY { get; init; }
    public string Currency { get; init; } = string.Empty; // Preis/Währung, falls per Kauf erhältlich
    public uint CurrencyIconId { get; init; } // Icon-ID der Währung (0 = unbekannt)
    public uint CurrencyItemId { get; init; } // Item-ID der Währung, für Inventar-Abfrage (0 = unbekannt)
    public uint CurrencyAmount { get; init; } // benötigte Menge der Währung
    public string Source { get; init; } = string.Empty; // z.B. "Dungeon Drop", "Vendor", "Quest"

    public bool HasVendorLocation => TerritoryTypeId != 0 && MapId != 0 && (VendorMapX != 0 || VendorMapY != 0);
}

public enum CollectibleType
{
    Mount,
    Minion,
    Orchestrion,
    Barding,
    Emote,
    Facewear,
    FashionAccessory,
    TripleTriadCard,
    FrameKit,
    Aetheryte,
    Quest,
}

public static class CollectionData
{
    private static List<CollectibleEntry>? cachedEntries;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] DataFiles =
    {
        "mounts.json", "minions.json", "orchestrions.json", "bardings.json",
        "emotes.json", "facewear.json", "fashions.json", "triadcards.json", "frames.json",
    };

    public static List<CollectibleEntry> GetAllEntries()
    {
        if (cachedEntries != null)
            return cachedEntries;

        var entries = new List<CollectibleEntry>();
        var dataDir = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Data");

        foreach (var fileName in DataFiles)
        {
            var path = Path.Combine(dataDir, fileName);
            if (!File.Exists(path))
                continue;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<CollectibleEntry>>(json, JsonOptions);
            if (loaded != null)
                entries.AddRange(loaded);
        }

        cachedEntries = entries;
        return entries;
    }
}
