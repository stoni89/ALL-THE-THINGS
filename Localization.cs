using Dalamud.Game;

namespace AllTheThings;

/// <summary>
/// Sehr einfache Übersetzungshilfe: Deutsch und Englisch, ausgewählt anhand der
/// im Spielclient eingestellten Sprache (alle anderen Sprachen fallen auf Englisch zurück).
/// </summary>
public static class Loc
{
    private static bool IsGerman => Plugin.ClientState.ClientLanguage == ClientLanguage.German;

    public static string T(string de, string en) => IsGerman ? de : en;

    public static string TypeName(CollectibleType type) => type switch
    {
        CollectibleType.Mount => T("Mount", "Mount"),
        CollectibleType.Minion => T("Minion", "Minion"),
        CollectibleType.Orchestrion => T("Orchestrionrolle", "Orchestrion Roll"),
        CollectibleType.Barding => T("Bardierung", "Barding"),
        CollectibleType.Emote => T("Emote", "Emote"),
        CollectibleType.Facewear => T("Brille", "Facewear"),
        CollectibleType.FashionAccessory => T("Accessoire", "Accessory"),
        CollectibleType.TripleTriadCard => T("Triple-Triad-Karte", "Triple Triad Card"),
        CollectibleType.FrameKit => T("Portrait-Rahmen", "Portrait Frame"),
        CollectibleType.Aetheryte => T("Aetheryte", "Aetheryte"),
        CollectibleType.Quest => T("Quest", "Quest"),
        CollectibleType.HuntingLog => T("Hunting Log", "Hunting Log"),
        CollectibleType.AetherCurrent => T("Ätherströmung", "Aether Current"),
        _ => type.ToString(),
    };
}
