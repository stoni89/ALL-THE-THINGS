using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AllTheThings;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Welche Sammelobjekt-Typen angezeigt werden
    public Dictionary<CollectibleType, bool> ShowType { get; set; } = new()
    {
        [CollectibleType.Mount] = true,
        [CollectibleType.Minion] = true,
        [CollectibleType.Orchestrion] = true,
        [CollectibleType.Barding] = true,
        [CollectibleType.Emote] = true,
        [CollectibleType.Facewear] = true,
        [CollectibleType.FashionAccessory] = true,
        [CollectibleType.TripleTriadCard] = true,
    };

    // Reihenfolge, in der die Typen im kompakten Overlay aufgelistet werden
    public List<CollectibleType> TypeOrder { get; set; } = new()
    {
        CollectibleType.Mount, CollectibleType.Minion, CollectibleType.Orchestrion, CollectibleType.Barding,
        CollectibleType.Emote, CollectibleType.Facewear, CollectibleType.FashionAccessory, CollectibleType.TripleTriadCard,
    };

    // 0 = undurchsichtig, 1 = vollständig transparent
    public float CompactTransparency { get; set; } = 0.1f;

    // Kompaktes Overlay (nur aktuelle Zone)
    public bool ShowCompactOverlay { get; set; } = false;
    public bool CompactOnlyAffordable { get; set; } = false;
    public bool ShowCurrencyWallet { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    /// <summary>
    /// Behebt Duplikate in TypeOrder, die durch Dalamuds JSON-Deserialisierung entstehen können
    /// (gespeicherte Listeneinträge werden an die per Property-Initializer vorbelegte Liste
    /// angehängt statt sie zu ersetzen) und ergänzt neu hinzugekommene Typen am Ende.
    /// </summary>
    public void SanitizeTypeOrder()
    {
        var cleaned = TypeOrder.Distinct().ToList();

        foreach (var type in Enum.GetValues<CollectibleType>())
        {
            if (!cleaned.Contains(type))
                cleaned.Add(type);
        }

        var changed = cleaned.Count != TypeOrder.Count;
        TypeOrder = cleaned;

        if (changed)
            Save();
    }
}
