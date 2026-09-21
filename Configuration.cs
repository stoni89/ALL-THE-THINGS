using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TheExplorersCodex;

public enum CompactFontMode
{
    Standard,
    Mono,
    Custom,
}

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
    public float CompactTransparency { get; set; } = 1f;

    // Kompaktes Overlay (nur aktuelle Zone)
    public bool ShowCompactOverlay { get; set; } = false;
    public bool ShowCurrencyWallet { get; set; } = true;

    // "Hinlaufen"-Icon neben verlinkten Einträgen (siehe CompactOverlayWindow.DrawClickableName) -
    // läuft per vnavmesh/Lifestream automatisch zum Fundort, siehe GoToAutomation.
    public bool ShowGoToIcon { get; set; } = true;

    // Reihe der Automation-Start/Stopp-Knöpfe (Quest/Aetheryte/Hunting Log/Ätherströmung/Sightseeing/
    // Chocobokeep) im Overlay - die Automationen selbst laufen unabhängig davon weiter, nur die
    // Knöpfe zum Starten/Stoppen werden ein-/ausgeblendet.
    public bool ShowAutomationButtons { get; set; } = true;
    public float CompactFontScale { get; set; } = 1.3f;
    public CompactFontMode CompactFontMode { get; set; } = CompactFontMode.Standard;
    public string CompactCustomFontPath { get; set; } = string.Empty;
    public string CompactCustomFontName { get; set; } = string.Empty;
    public bool CompactLocked { get; set; } = false;

    // QoL
    public bool UseSprintOnCooldown { get; set; } = true;

    // Mount für die Aetheryten-Automation: null = aus (zu Fuß mit Sprint), 0 = "Mount Roulette"
    // (bei jedem Ruf wird zufällig eines der bereits freigeschalteten Mounts gewählt - es gibt
    // dafür keine verlässliche, sprachunabhängige Spiel-IPC, daher wird die Zufallsauswahl selbst
    // hier im Plugin gemacht statt über das Spiel-eigene Mount-Roulette-Feature), sonst die
    // Lumina-RowId eines konkreten, bereits freigeschalteten Mounts.
    public int? AetheryteMountId { get; set; } = null;

    // true, sobald Plugin.EnsureAetheryteMountAutoDefault einmal eine Vorbelegung für AetheryteMountId
    // gesetzt hat (siehe dort) - verhindert, dass eine spätere manuelle Rückstellung auf "Kein Mount"
    // bei jedem weiteren Öffnen des Optionsfensters wieder überschrieben wird.
    public bool AetheryteMountAutoDefaultApplied { get; set; } = false;

    // TomTom-artiger Wegweiser-Pfeil (siehe Windows/NavigationArrowWindow.cs) - Default aus, da er
    // eine zusätzliche, ständig sichtbare UI-Fläche wäre, die nicht jeder will.
    public bool ShowNavigationArrow { get; set; } = false;
    public float NavigationArrowWidth { get; set; } = 170f;
    public float NavigationArrowHeight { get; set; } = 170f;

    // #FFC200FF
    public Vector4 NavigationArrowColor { get; set; } = new(1f, 0.7607843f, 0f, 1f);

    // Debug
    public bool ShowDebugInfo { get; set; } = false;

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
