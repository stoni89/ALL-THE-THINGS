using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
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
    // Absichtlich set statt init - Plugin.EnrichEntriesWithZoneFromSource trägt diese beiden Felder
    // nachträglich für Einträge nach, deren JSON-Datei nur den Fundort als Klartext (Source, z.B.
    // ein Dungeon-Name) kennt, aber keine Zone.
    public uint TerritoryTypeId { get; set; } // Zone, in der der Eintrag angezeigt wird (Lumina "TerritoryType" Sheet)
    public uint MapId { get; set; } // Lumina "Map" Sheet, für MapLinkPayload benötigt

    // Für Kartenlinks, deren Flagge auf einer ANDEREN Karte liegt als die Zone, in der der Eintrag
    // angezeigt wird (z.B. ein Aethernetz-Kristall, der laut Spiel auf der Nachbarkarte markiert
    // wird) - null bedeutet "gleiche Zone wie TerritoryTypeId" (Normalfall).
    public uint? FlagTerritoryTypeId { get; init; }

    // Absichtlich set statt init (wie TerritoryTypeId/MapId oben) - Plugin.EnrichFrameKitVendors
    // trägt Händler-Infos für Portrait-Rahmen nach, die als Framer's-Kit-Item bei einem NPC
    // gekauft werden können (siehe GetFrameKitEntries - dort zunächst ohne Fundort angelegt).
    public string Vendor { get; set; } = string.Empty; // Händler-/NPC-Name, falls per Kauf erhältlich
    public float VendorMapX { get; set; } // Kartenkoordinate des Händlers (0 = unbekannt)
    public float VendorMapY { get; set; }
    public string Currency { get; set; } = string.Empty; // Preis/Währung, falls per Kauf erhältlich
    public uint CurrencyIconId { get; set; } // Icon-ID der Währung (0 = unbekannt)
    public uint CurrencyItemId { get; set; } // Item-ID der Währung, für Inventar-Abfrage (0 = unbekannt)
    public uint CurrencyAmount { get; set; } // benötigte Menge der Währung
    public string Source { get; set; } = string.Empty; // z.B. "Dungeon Drop", "Vendor", "Quest"

    // Nur für Hunting-Log-Einträge (siehe Plugin.GetHuntingLogEntries/ManualHuntingLogPositions) -
    // roamende Monster haben keine Kartenkoordinate wie Händler/Aetheryten, sondern (falls bekannt)
    // eine von Hand nachgetragene rohe Weltposition, direkt fürs Laufen mit vnavmesh gedacht.
    public Vector3? WorldPosition { get; init; }

    // Nur für Hunting-Log-Einträge - RowId aus dem Lumina-Sheet "BNpcName", entspricht zur Laufzeit
    // ICharacter.NameId eines lebenden Weltobjekts. Für HuntingLogAutomation, um das tatsächliche
    // Monster in der Objekttabelle zu finden (und RotationSolver mitzuteilen, welches priorisiert
    // angegriffen werden soll).
    public uint? BNpcNameId { get; init; }

    // Nur für Sightseeing-Log-Einträge - manche Aussichtspunkte (Lumina "Adventure".Emote) schalten
    // erst frei, wenn man am Zielort einen bestimmten Emote ausführt, nicht durch reine Nähe. Der
    // Chat-Befehl (z.B. "/sit") kommt direkt aus dem verlinkten Emote/TextCommand-Sheet.
    public string? RequiredEmoteCommand { get; init; }

    // Nur für Portrait-Rahmen (siehe Plugin.GetFrameKitEntries) - ein Rahmen kann über ganz
    // unterschiedliche Wege freigeschaltet werden (Quest, Errungenschaft, Duty, Emote/Minion/
    // Mount/Ornament-Besitz, oder ein separates "Framer's Kit"-Item). Id allein (die BannerFrame-
    // RowId) reicht für den Freischalt-Check nicht - dafür diese beiden zusätzlichen Felder, deren
    // FrameKitUnlockId je nach FrameKitUnlockKind eine andere Sheet-RowId meint.
    public FrameKitUnlockKind? FrameKitUnlockKind { get; init; }
    public uint FrameKitUnlockId { get; init; }

    public bool HasVendorLocation => TerritoryTypeId != 0 && MapId != 0 && (VendorMapX != 0 || VendorMapY != 0);

    // "Hat irgendein Laufziel" - für das "Hinlaufen"-Icon (siehe CompactOverlayWindow.DrawClickableName/
    // DrawGoToIcon und GoToAutomation), das beide Positionsarten gleich behandelt.
    public bool HasGoToTarget => HasVendorLocation || WorldPosition.HasValue;
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
    HuntingLog,
    AetherCurrent,
    Sightseeing,
    Chocobokeep,
}

/// <summary>
/// Auf welchem Weg ein Portrait-Rahmen freigeschaltet wird - siehe Plugin.GetFrameKitEntries für
/// die Herleitung aus dem Lumina-Sheet "BannerCondition".
/// </summary>
public enum FrameKitUnlockKind
{
    Unknown,
    Quest,
    Duty,
    Achievement,
    Emote,
    Minion,
    Mount,
    Ornament,
    FramersKitItem,
}

public static class CollectionData
{
    private static List<CollectibleEntry>? cachedEntries;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new Vector3JsonConverter() },
    };

    // "frames.json" (Portrait-Rahmen) gibt es bewusst nicht mehr - die Freischalt-Wege dafür sind
    // zu unterschiedlich (Quest/Errungenschaft/Duty/Emote/Minion/Mount/Ornament/Kit-Item), um sie
    // von Hand zu pflegen. Stattdessen live aus Lumina aufgelöst, siehe Plugin.GetFrameKitEntries.
    private static readonly string[] DataFiles =
    {
        "mounts.json", "minions.json", "orchestrions.json", "bardings.json",
        "emotes.json", "facewear.json", "fashions.json", "triadcards.json",
        "aethercurrents.json",
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

        // Viele Dungeon-Drops (Truhen-Notenrollen, Triple-Triad-Karten, Minions - siehe Category
        // "Dungeon" in den JSON-Dateien) haben dort nur den Dungeon-Namen als Klartext in Source
        // stehen, aber keine Zone - dadurch tauchten sie im Overlay nie auf, auch nicht, wenn man
        // tatsächlich gerade in genau diesem Dungeon steht. Live aus Lumina nachgetragen, kein
        // Community-Export nötig.
        Plugin.EnrichEntriesWithZoneFromSource(entries);

        // Viele Händlereinträge kennen zwar Vendor+Zone als Klartext, aber keine Kartenkoordinate
        // (VendorMapX/Y = 0) - dadurch fehlten "Auf Karte anzeigen"/"Hinlaufen" (siehe z.B.
        // "Jonathas" in Old Gridania). Nach EnrichEntriesWithZoneFromSource, damit auch Einträge
        // erfasst werden, deren Zone erst DORT nachgetragen wurde.
        Plugin.EnrichEntriesWithVendorPosition(entries);

        entries.AddRange(Plugin.GetFrameKitEntries());
        entries.AddRange(Plugin.GetChocobokeepEntries());

        cachedEntries = entries;
        return entries;
    }
}

/// <summary>
/// Erlaubt "WorldPosition": {"X":.., "Y":.., "Z":..} in JSON-Datendateien (siehe
/// aethercurrents.json) - System.Text.Json kann Vector3 sonst nicht automatisch (de-)serialisieren,
/// da X/Y/Z dort öffentliche FELDER statt Properties sind.
/// </summary>
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        float x = 0, y = 0, z = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var propertyName = reader.GetString();
            reader.Read();
            var value = reader.GetSingle();
            switch (propertyName?.ToUpperInvariant())
            {
                case "X": x = value; break;
                case "Y": y = value; break;
                case "Z": z = value; break;
            }
        }

        return new Vector3(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Z", value.Z);
        writer.WriteEndObject();
    }
}
