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
        [CollectibleType.Hairstyle] = true,
    };

    // Welche Währungen (per GetCurrencyLabel-Kurzname, z.B. "MGP", "Allied Seals") komplett
    // ausgeblendet werden sollen - und mit ihnen alle Einträge, die genau diese Währung verlangen
    // (siehe CompactOverlayWindow "Currencys filtern"). Leer = nichts ausgeblendet.
    public HashSet<string> HiddenCurrencies { get; set; } = new();

    // Reihenfolge, in der die Typen im kompakten Overlay aufgelistet werden
    public List<CollectibleType> TypeOrder { get; set; } = new()
    {
        CollectibleType.Mount, CollectibleType.Minion, CollectibleType.Orchestrion, CollectibleType.Barding,
        CollectibleType.Emote, CollectibleType.Facewear, CollectibleType.FashionAccessory, CollectibleType.TripleTriadCard,
        CollectibleType.Hairstyle,
    };

    // 0 = undurchsichtig, 1 = vollständig transparent
    public float CompactTransparency { get; set; } = 1f;

    // Kompaktes Overlay (nur aktuelle Zone)
    public bool ShowCompactOverlay { get; set; } = false;
    public bool ShowCurrencyWallet { get; set; } = true;

    // "Hinlaufen"-Icon neben verlinkten Einträgen (siehe CompactOverlayWindow.DrawClickableName) -
    // läuft per vnavmesh/Lifestream automatisch zum Fundort, siehe GoToAutomation.
    public bool ShowGoToIcon { get; set; } = true;

    // Standardmäßig AN: zeigt auch Sammelobjekte, die aktuell nur durch eine noch nicht erreichte
    // Errungenschaft oder einen noch nicht erreichten Stammes-/Grad-Rang erreichbar sind (siehe
    // Plugin.AchievementOrRankGatedItems, von Hand gepflegte Liste). Deaktiviert blendet genau diese
    // Einträge aus, statt sie als vermeintlich "gleich erreichbar" mit allen anderen zu vermischen.
    public bool ShowAllItems { get; set; } = true;

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

    // Lässt die Aetheryten-/Chocobokeep-Automation auch bereits freigeschaltete Ziele erneut
    // anlaufen (statt nur die tatsächlich fehlenden) - zum Testen von Laufweg/Interaktion, ohne
    // dafür einen unfertigen Account zu brauchen. Wirkt sich NUR auf die Automation-Zielliste aus,
    // nicht auf die normale "fehlt noch"-Anzeige im Overlay.
    public bool SimulateAetheryteAutomation { get; set; } = false;
    public bool SimulateChocobokeepAutomation { get; set; } = false;

    // Wie oben, aber zusätzlich zu bereits aufgezeichneten Punkten auch solche, die gerade durch
    // falsches Wetter/falsche Uhrzeit oder eine noch nicht erfüllte Buch-Freischaltung als "Bedingung
    // nicht erfüllt" markiert sind (siehe Plugin.ComputeGrandCompanyOrTribeGateReason) - zum Testen
    // von Laufweg/Ankunftsposition, ohne auf das passende Wetter/die passende Uhrzeit warten zu
    // müssen. Wirkt sich NUR auf die Automation-Zielliste aus, nicht auf die normale Anzeige im
    // Overlay.
    public bool SimulateSightseeingAutomation { get; set; } = false;

    // Aktiviert SHIFT + Linksklick auf einen Sammelobjekt-Namen oder eine Währungsangabe im
    // kompakten Overlay, um Allagan Tools' "Mehr Informationen"-Fenster für das jeweilige Item zu
    // öffnen (siehe Plugin.OpenAllaganToolsItemInfo/Windows.CompactOverlayWindow). Nur wirksam,
    // solange Allagan Tools (interner Name "InventoryTools") installiert/geladen ist - wird in den
    // Einstellungen automatisch wieder ausgeschaltet, falls das Plugin nachträglich entfernt wird.
    public bool EnableAllaganToolsIntegration { get; set; } = false;

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
