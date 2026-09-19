using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AllTheThings.Windows;

/// <summary>
/// Schlankes, randloses Overlay-Fenster - zeigt nur Sammelobjekte der aktuellen Zone.
/// Gedacht zum permanenten Offenlassen neben der Action-Leiste, daher bewusst kompakt gehalten.
/// </summary>
public class CompactOverlayWindow : Window
{
    private readonly Plugin plugin;

    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing;

    public CompactOverlayWindow(Plugin plugin) : base("##AllTheThingsCompact", BaseFlags)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;

        // Startgröße nur beim allerersten Öffnen - danach darf der Spieler frei skalieren
        // (z.B. wenn ein Mount-Name nicht in die Standardbreite passt).
        Size = new Vector2(260, 200);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(180, 100),
            MaximumSize = new Vector2(800, 2000),
        };
    }

    public void Dispose() { }

    // Bewusst FEST (nicht von CompactTransparency abhängig) - der Resize-Griff unten rechts soll
    // auch bei voller Transparenz sichtbar bleiben, sonst sieht man gar nicht mehr, wo das Fenster
    // endet bzw. wo man es zum Skalieren greifen kann.
    private static readonly Vector4 ResizeGripColor = new(0.62f, 0.38f, 0.85f, 1f);
    private static readonly Vector4 ResizeGripHoveredColor = new(0.74f, 0.48f, 0.98f, 1f);
    private static readonly Vector4 ResizeGripActiveColor = new(0.82f, 0.58f, 1f, 1f);

    public override void PreDraw()
    {
        var config = plugin.Configuration;

        // Gesperrt = weder verschiebbar noch skalierbar - ImGui blendet den Resize-Griff dann
        // automatisch aus (kein zusätzlicher Farb-Trick nötig), macht also gleich beides.
        Flags = config.CompactLocked ? BaseFlags | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize : BaseFlags;

        var alpha = 1f - System.Math.Clamp(config.CompactTransparency, 0f, 1f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        // Dieselbe Grundfarbe wie das Optionsfenster (siehe ModernUi.PushStyle) - bei Transparenz=0
        // (voll undurchsichtig) sehen beide Fenster damit identisch aus.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.055f, 0.063f, 0.098f, alpha));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, ResizeGripColor);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, ResizeGripHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, ResizeGripActiveColor);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        using var fontScope = PushCompactFont(config);
        ImGui.SetWindowFontScale(config.CompactFontScale);

        var currentTerritoryId = Plugin.ClientState.TerritoryType;

        // Manche Zonen gehören zu einer Stadt, haben aber ein eigenes TerritoryType (z.B. "Heart of
        // the Sworn" -> Ul'dah) - dort sollen dieselben Daten wie in der zugeordneten Stadtzone
        // verwendet werden. Nur für Datenabfragen, nicht für die angezeigte Zonenüberschrift unten.
        var effectiveTerritoryId = Plugin.ResolveEffectiveTerritoryId(currentTerritoryId);

        OutlineText("All The Things", TitleColor);
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (ImGui.IsItemClicked())
            plugin.OpenOptions();

        if (DrawCloseButtonTopRight())
        {
            IsOpen = false;
            config.ShowCompactOverlay = false;
            config.Save();
        }

        OutlineText($"{plugin.GetZoneName(currentTerritoryId)} ({currentTerritoryId})", MutedColor);

        // In geteilten Hauptstädten (Ul'dah, Limsa, Gridania, Ishgard) sollen Sammelobjekte aus
        // JEDEM Bezirk angezeigt werden, egal in welchem man gerade steht - jeder Eintrag verlinkt
        // trotzdem auf seinen tatsächlichen Bezirk (siehe FlagTerritoryTypeId/MapId je Eintrag).
        var siblingTerritories = Plugin.GetSplitCityTerritories(effectiveTerritoryId);
        var allForZone = CollectionData.GetAllEntries()
            .Concat(plugin.GetLiveZoneEntries(effectiveTerritoryId))
            .Where(e => siblingTerritories.Contains(e.TerritoryTypeId))
            .ToList();

        var afterTypeFilter = allForZone
            .Where(e => config.ShowType.GetValueOrDefault(e.Type, true))
            .ToList();

        var entries = afterTypeFilter
            .Where(e => !plugin.IsOwned(e))
            .OrderBy(e => config.TypeOrder.IndexOf(e.Type))
            .ThenBy(e => e.Name)
            .ToList();

        // Bewusst aus "allForZone" (nicht "entries") - die Automation soll unabhängig vom
        // Typen-Filter laufen, auch wenn Quests im Overlay z.B. ausgeblendet sind.
        var missingQuests = allForZone
            .Where(e => e.Type == CollectibleType.Quest && !plugin.IsOwned(e))
            .ToList();

        // Unabhängig davon, ob die Automation läuft - damit die rote "Nicht unterstützt"-Markierung
        // schon beim Betreten der Zone erscheint, statt erst nach einem gestarteten Automation-Lauf.
        plugin.QuestAutomation.RefreshSupportStatus(missingQuests);
        plugin.QuestAutomation.Update(missingQuests, effectiveTerritoryId);

        // Bewusst die ganze Stadt (inkl. Kristalle aus Nachbarbezirken einer geteilten Hauptstadt,
        // siehe allForZone) - die Automation reist bei Bedarf selbst mit Lifestream zwischen den
        // Bezirken hin und her (siehe AetheryteAutomation.cs). Im Debug-Simulationsmodus werden
        // bewusst auch schon freigeschaltete Kristalle mitgenommen, um den Laufweg/die Reihenfolge
        // ohne Fortschrittsverlust zu überprüfen.
        var missingAetherytesCity = allForZone
            .Where(e => e.Type == CollectibleType.Aetheryte
                        && (plugin.AetheryteAutomation.SimulateAllCrystals || !plugin.IsOwned(e)))
            .ToList();
        plugin.AetheryteAutomation.Update(missingAetherytesCity);

        // "Unterstützt" heißt hier: noch nicht als von Questionable abgelehnt bekannt (siehe
        // QuestAutomation.IsKnownUnsupported) - erst nach einem Versuch bekannt, siehe dort.
        var hasActionableQuests = missingQuests.Any(q => !plugin.QuestAutomation.IsKnownUnsupported(q.Id));
        var hasActionableAetherytes = missingAetherytesCity.Count > 0;

        DrawQuestAutomationButton(hasActionableQuests, effectiveTerritoryId);
        ImGui.SameLine();
        DrawAetheryteAutomationButton(hasActionableAetherytes);

        // Ganz rechts an den Fensterrand.
        var filterLabel = Loc.T("Typen filtern", "Filter types") + "##CompactTypeFilter";
        var filterButtonWidth = ImGui.CalcTextSize(Loc.T("Typen filtern", "Filter types")).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - filterButtonWidth);
        if (ImGui.Button(filterLabel))
            ImGui.OpenPopup("CompactTypeFilterPopup");

        if (ImGui.BeginPopup("CompactTypeFilterPopup"))
        {
            foreach (var type in config.TypeOrder)
            {
                var enabled = config.ShowType.GetValueOrDefault(type, true);
                if (ImGui.Checkbox($"{Loc.TypeName(type)}##CompactTypeFilterEntry", ref enabled))
                {
                    config.ShowType[type] = enabled;
                    config.Save();
                }
            }

            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (plugin.QuestAutomation.ShouldShowStatusText)
            OutlineText(plugin.QuestAutomation.StatusText, plugin.QuestAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (plugin.AetheryteAutomation.ShouldShowStatusText)
            OutlineText(plugin.AetheryteAutomation.StatusText, plugin.AetheryteAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (config.ShowDebugInfo)
            OutlineText($"debug: zone={allForZone.Count} typefilter={afterTypeFilter.Count} missing={entries.Count}", MutedColor);

        if (entries.Count == 0)
        {
            OutlineText(Loc.T("Nichts Fehlendes in dieser Zone.", "Nothing missing in this zone."), MutedColor);
            return;
        }

        OutlineText($"[{entries.Count}]", TitleColor);

        if (config.ShowCurrencyWallet)
            DrawCurrencyWallet(entries);

        // Nur dieser Teil (die eigentliche Liste) soll scrollen - alles darüber (Titel, Knöpfe,
        // Status, Währungen) bleibt beim Scrollen fest stehen, size.Y=0 füllt dafür einfach den
        // Rest des (frei durch den Spieler skalierbaren) Fensters.
        ImGui.BeginChild("##CompactEntryList", new Vector2(0, 0), false);

        foreach (var entry in entries)
        {
            OutlineText("•", MutedColor);
            ImGui.SameLine();

            var isUnsupportedQuest = entry.Type == CollectibleType.Quest && plugin.QuestAutomation.IsKnownUnsupported(entry.Id);
            var typeColor = isUnsupportedQuest ? UnsupportedColor : TypeColors.GetValueOrDefault(entry.Type, NormalColor);
            OutlineText($"[{Loc.TypeName(entry.Type)}]", typeColor);
            if (isUnsupportedQuest && ImGui.IsItemHovered())
                ImGui.SetTooltip("Not supported with Questionable");

            ImGui.SameLine();
            DrawClickableName(entry);

            if (!string.IsNullOrEmpty(entry.Currency))
            {
                ImGui.SameLine();
                OutlineText("-", MutedColor);
                ImGui.SameLine();

                if (entry.CurrencyIconId != 0)
                {
                    var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(entry.CurrencyIconId)).GetWrapOrEmpty();
                    var size = new Vector2(ImGui.GetTextLineHeight());
                    ImGui.Image(icon.Handle, size);
                    ImGui.SameLine();
                }

                var affordable = plugin.CanAfford(entry);
                var color = affordable ? AffordableColor : NormalColor;
                OutlineText(entry.Currency, color);
            }
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Zeigt, wie viel der Spieler von jeder Währung besitzt, die für die aktuell
    /// angezeigten (gefilterten) Einträge benötigt wird.
    /// </summary>
    private void DrawCurrencyWallet(List<CollectibleEntry> entries)
    {
        var currencies = entries
            .Where(e => e.CurrencyItemId != 0)
            .GroupBy(e => e.CurrencyItemId)
            .Select(g => g.First())
            .ToList();

        if (currencies.Count == 0)
            return;

        OutlineText(Loc.T("Deine Währungen:", "Your currencies:"), MutedColor);

        foreach (var sample in currencies)
        {
            if (sample.CurrencyIconId != 0)
            {
                var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(sample.CurrencyIconId)).GetWrapOrEmpty();
                var size = new Vector2(ImGui.GetTextLineHeight());
                ImGui.Image(icon.Handle, size);
                ImGui.SameLine();
            }

            var owned = plugin.GetCurrencyAmount(sample.CurrencyItemId);
            var label = GetCurrencyLabel(sample.Currency);
            OutlineText($"{owned.ToString("N0", CultureInfo.InvariantCulture)} {label}", NormalColor);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static string GetCurrencyLabel(string currencyText)
    {
        var t = Regex.Replace(currencyText, @"^\s*[\d,]+\s*", "");
        t = Regex.Replace(t, @"\s*\([^)]*\)\s*$", "");
        return t.Trim();
    }

    /// <summary>
    /// Färbt einen Automation-Knopf passend zum Typ-Tag seiner Kategorie (leicht abgedunkelt, damit
    /// der Knopf nicht zu grell wirkt), oder rot, solange die Automation aktiv ist ("zum Stoppen").
    /// Muss von der Aufrufstelle immer mit ImGui.PopStyleColor(2) beendet werden.
    /// </summary>
    private static void PushAutomationButtonColors(bool isActive, Vector4 typeColor)
    {
        if (isActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.25f, 0.25f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(typeColor.X * 0.6f, typeColor.Y * 0.6f, typeColor.Z * 0.6f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(typeColor.X * 0.75f, typeColor.Y * 0.75f, typeColor.Z * 0.75f, 1f));
        }
    }

    /// <summary>
    /// Knopf, der die Questionable-Automation (siehe QuestAutomation.cs) für die aktuell
    /// fehlenden Quests dieser Zone an-/ausschaltet. Questionable ist ein separates Fremdplugin -
    /// ist es nicht installiert/geladen, wird das per Tooltip erklärt statt der Knopf einfach
    /// nichts zu tun.
    /// </summary>
    private void DrawQuestAutomationButton(bool hasActionableQuests, uint effectiveTerritoryId)
    {
        var automation = plugin.QuestAutomation;
        var label = automation.IsActive
            ? Loc.T("Automation stoppen", "Stop automation")
            : Loc.T("Quest-Automation", "Quest automation");

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht.
        var isDisabled = !automation.IsActive && !hasActionableQuests;

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.Quest]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactQuestAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(isDisabled
                ? Loc.T(
                    "Keine von Questionable unterstützten Quests in dieser Zone.",
                    "No quests supported by Questionable in this zone.")
                : automation.IsActive
                    ? Loc.T(
                        "Schiebt keine weiteren Quests mehr nach. Questionable selbst kennt keine Stopp-IPC - " +
                        "eine bereits laufende Quest läuft dort weiter, bis sie fertig ist oder du sie in " +
                        "Questionables eigenem Fenster abbrichst.",
                        "Stops queueing further quests. Questionable itself has no stop IPC - a quest it has " +
                        "already started keeps running there until it finishes, or until you cancel it in " +
                        "Questionable's own window.")
                    : Loc.T(
                        "Lässt Questionable nacheinander alle fehlenden Quests dieser Zone annehmen und abschließen.",
                        "Has Questionable pick up and complete all missing quests in this zone, one by one."));
        }

        if (!clicked)
            return;

        if (automation.IsActive)
        {
            automation.Stop();
        }
        else if (automation.IsQuestionableAvailable())
        {
            automation.Start(effectiveTerritoryId);
        }
        else
        {
            automation.MarkUnavailable();
        }
    }

    /// <summary>
    /// Knopf, der die Aetheryten-Automation (siehe AetheryteAutomation.cs) für die aktuell
    /// fehlenden Aetheryten/Kristalle dieser Zone an-/ausschaltet. Nutzt das Fremdplugin
    /// "vnavmesh" zum Laufen - fehlt es, wird das per Tooltip erklärt statt der Knopf einfach
    /// nichts zu tun.
    /// </summary>
    private void DrawAetheryteAutomationButton(bool hasActionableAetherytes)
    {
        var automation = plugin.AetheryteAutomation;
        var label = automation.IsActive
            ? Loc.T("Automation stoppen", "Stop automation")
            : Loc.T("Aetheryten-Automation", "Aetheryte automation");

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht.
        var isDisabled = !automation.IsActive && !hasActionableAetherytes;

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.Aetheryte]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactAetheryteAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(isDisabled
                ? Loc.T("Keine fehlenden Aetheryten/Kristalle in dieser Zone.", "No missing aetherytes/crystals in this zone.")
                : automation.IsActive
                    ? Loc.T("Bricht die Laufbewegung sofort ab und stoppt die Automation.", "Immediately stops movement and the automation.")
                    : Loc.T(
                        "Läuft mit vnavmesh nacheinander alle fehlenden Aetheryten/Kristalle ab und interagiert mit ihnen.",
                        "Uses vnavmesh to walk to and interact with all missing aetherytes/crystals, one by one."));
        }

        if (!clicked)
            return;

        if (automation.IsActive)
        {
            automation.Stop();
        }
        else if (automation.IsVNavmeshAvailable())
        {
            automation.Start();
        }
        else
        {
            automation.MarkUnavailable();
        }
    }

    private void DrawClickableName(CollectibleEntry entry)
    {
        var affordable = plugin.CanAfford(entry);

        if (!entry.HasVendorLocation)
        {
            OutlineText(entry.Name, affordable ? AffordableColor : NormalColor);
            return;
        }

        OutlineText(entry.Name, affordable ? AffordableColor : VendorLinkColor);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(string.IsNullOrEmpty(entry.Vendor)
                ? Loc.T("Auf Karte anzeigen", "Show on map")
                : Loc.T($"Bei {entry.Vendor} - Auf Karte anzeigen", $"From {entry.Vendor} - show on map"));
        }

        if (ImGui.IsItemClicked())
            Plugin.OpenVendorMap(entry);
    }

    /// <summary>
    /// Aktiviert bei Bedarf eine abweichende Schrift für das Overlay (nur, wenn sie bereits
    /// geladen ist - andernfalls bleibt die reguläre Dalamud/ImGui-Schrift aktiv).
    /// </summary>
    private static System.IDisposable? PushCompactFont(Configuration config)
    {
        Dalamud.Interface.ManagedFontAtlas.IFontHandle? handle = config.CompactFontMode switch
        {
            CompactFontMode.Mono => Plugin.PluginInterface.UiBuilder.MonoFontHandle,
            CompactFontMode.Custom => CustomFontManager.GetOrCreate(config.CompactCustomFontPath),
            _ => null,
        };

        return handle is { Available: true } ? handle.Push() : null;
    }

    private static bool DrawCloseButtonTopRight()
    {
        var buttonWidth = ImGui.CalcTextSize("x").X + ImGui.GetStyle().FramePadding.X * 2f;
        var regionMaxX = ImGui.GetWindowContentRegionMax().X;
        ImGui.SameLine(regionMaxX - buttonWidth);
        return ImGui.SmallButton("x##CloseCompact");
    }

    private static readonly Vector4 TitleColor = new(0.55f, 0.8f, 1f, 1f);
    private static readonly Vector4 MutedColor = new(0.75f, 0.75f, 0.75f, 1f);
    private static readonly Vector4 NormalColor = new(0.92f, 0.92f, 0.92f, 1f);
    private static readonly Vector4 VendorLinkColor = new(0.5f, 0.8f, 1f, 1f);
    private static readonly Vector4 AffordableColor = new(0.55f, 0.95f, 0.55f, 1f);
    private static readonly Vector4 UnsupportedColor = new(1f, 0.3f, 0.3f, 1f);

    private static readonly Dictionary<CollectibleType, Vector4> TypeColors = new()
    {
        [CollectibleType.Mount] = new(0.85f, 0.45f, 0.05f, 1f),
        [CollectibleType.Minion] = new(0.75f, 0.6f, 1f, 1f),
        [CollectibleType.Orchestrion] = new(0.4f, 0.9f, 0.85f, 1f),
        [CollectibleType.Barding] = new(0.85f, 0.65f, 0.45f, 1f),
        [CollectibleType.Emote] = new(1f, 0.55f, 0.75f, 1f),
        [CollectibleType.Facewear] = new(0.55f, 0.75f, 1f, 1f),
        [CollectibleType.FashionAccessory] = new(0.75f, 0.9f, 0.45f, 1f),
        [CollectibleType.TripleTriadCard] = new(1f, 0.5f, 0.5f, 1f),
        [CollectibleType.FrameKit] = new(0.6f, 0.85f, 1f, 1f),
        [CollectibleType.Aetheryte] = new(0.6f, 1f, 0.75f, 1f),
        [CollectibleType.Quest] = new(1f, 0.9f, 0.5f, 1f),
    };
    private static readonly Vector2[] ShadowOffsets =
    {
        new(-1, -1), new(1, -1), new(-1, 1), new(1, 1),
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    /// <summary>
    /// Zeichnet Text mit dunklem Rand, damit er bei voller Transparenz (kein Fensterhintergrund
    /// mehr) auf jedem beliebigen Ingame-Untergrund lesbar bleibt. Verhält sich wie ein normales
    /// Text-Widget (SameLine/IsItemHovered/IsItemClicked funktionieren danach wie gewohnt), da der
    /// letzte Zeichenaufruf an der eigentlichen Cursor-Position passiert.
    /// </summary>
    private static void OutlineText(string text, Vector4 color)
    {
        var origin = ImGui.GetCursorPos();
        var shadow = new Vector4(0f, 0f, 0f, 0.9f);

        foreach (var offset in ShadowOffsets)
        {
            ImGui.SetCursorPos(origin + offset);
            ImGui.TextColored(shadow, text);
        }

        ImGui.SetCursorPos(origin);
        ImGui.TextColored(color, text);
    }
}
