using Dalamud.Game;

namespace TheExplorersCodex;

/// <summary>
/// Sehr einfache Übersetzungshilfe: Deutsch und Englisch, ausgewählt anhand der
/// im Spielclient eingestellten Sprache (alle anderen Sprachen fallen auf Englisch zurück) -
/// AUSSER innerhalb eines aktiven Menü-Sprachüberschreibens (siehe MenuLanguageOverride), das NUR
/// von Windows.MainWindow.Draw() gesetzt wird (dort auf dessen komplette Laufzeit begrenzt, siehe
/// try/finally dort) und deshalb weder das kompakte Overlay noch Automation-Statustexte
/// beeinflusst - beide setzen diese Umgebungsvariable nie.
/// </summary>
public static class Loc
{
    // null = kein Überschreiben aktiv (Standardfall - Spielsprache gilt überall, wie bisher).
    // Absichtlich ambient/statisch statt als Parameter durch jeden T()-Aufruf gereicht - Windows.
    // MainWindow.cs hat hunderte bestehende Loc.T-Aufrufe, die so unverändert bleiben können.
    public static bool? MenuLanguageOverride { get; set; }

    private static bool IsGerman => MenuLanguageOverride ?? Plugin.ClientState.ClientLanguage == ClientLanguage.German;

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
        CollectibleType.FrameKit => T("Framer's Kit", "Framer's Kit"),
        CollectibleType.Hairstyle => T("Moderne Ästhetik", "Modern Aesthetics"),
        CollectibleType.Aetheryte => T("Aetheryte", "Aetheryte"),
        CollectibleType.Quest => T("Quest", "Quest"),
        CollectibleType.HuntingLog => T("Hunting Log", "Hunting Log"),
        CollectibleType.AetherCurrent => T("Ätherströmung", "Aether Current"),
        CollectibleType.Sightseeing => T("Sightseeing", "Sightseeing"),
        CollectibleType.Chocobokeep => T("Chocobokeep", "Chocobokeep"),
        CollectibleType.Achievement => T("Errungenschaft", "Achievement"),
        _ => type.ToString(),
    };
}
