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

    public CompactOverlayWindow(Plugin plugin) : base(
        "##AllTheThingsCompact",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)
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

    public override void PreDraw()
    {
        var alpha = 1f - System.Math.Clamp(plugin.Configuration.CompactTransparency, 0f, 1f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.09f, 0.09f, 0.11f, alpha));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var currentTerritoryId = Plugin.ClientState.TerritoryType;

        OutlineText("All The Things", TitleColor);
        if (DrawCloseButtonTopRight())
        {
            config.ShowCompactOverlay = false;
            config.Save();
        }

        OutlineText($"{plugin.GetZoneName(currentTerritoryId)} ({currentTerritoryId})", MutedColor);

        if (ImGui.Button(Loc.T("Typen filtern", "Filter types") + "##CompactTypeFilter"))
            ImGui.OpenPopup("CompactTypeFilterPopup");

        ImGui.SameLine();

        var onlyAffordable = config.CompactOnlyAffordable;
        if (ImGui.Checkbox(Loc.T("Nur leistbare Käufe", "Only affordable purchases") + "##Compact", ref onlyAffordable))
        {
            config.CompactOnlyAffordable = onlyAffordable;
            config.Save();
        }

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

        var entries = CollectionData.GetAllEntries()
            .Where(e => e.TerritoryTypeId == currentTerritoryId)
            .Where(e => config.ShowType.GetValueOrDefault(e.Type, true))
            .Where(e => !plugin.IsOwned(e))
            .Where(e => !onlyAffordable || plugin.CanAfford(e))
            .OrderBy(e => config.TypeOrder.IndexOf(e.Type))
            .ThenBy(e => e.Name)
            .ToList();

        if (entries.Count == 0)
        {
            OutlineText(onlyAffordable
                ? Loc.T("Nichts Leistbares in dieser Zone.", "Nothing affordable in this zone.")
                : Loc.T("Nichts Fehlendes in dieser Zone.", "Nothing missing in this zone."), MutedColor);
            return;
        }

        if (config.ShowCurrencyWallet)
            DrawCurrencyWallet(entries);

        foreach (var entry in entries)
        {
            OutlineText("•", MutedColor);
            ImGui.SameLine();
            OutlineText($"[{Loc.TypeName(entry.Type)}]", TypeColors.GetValueOrDefault(entry.Type, NormalColor));
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

    private void DrawClickableName(CollectibleEntry entry)
    {
        if (!entry.HasVendorLocation)
        {
            OutlineText(entry.Name, NormalColor);
            return;
        }

        OutlineText(entry.Name, VendorLinkColor);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(string.IsNullOrEmpty(entry.Vendor)
                ? Loc.T("Auf Karte anzeigen", "Show on map")
                : Loc.T($"Bei {entry.Vendor} - Auf Karte anzeigen", $"From {entry.Vendor} - show on map"));
        }

        if (ImGui.IsItemClicked())
            plugin.OpenVendorMap(entry);
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

    private static readonly Dictionary<CollectibleType, Vector4> TypeColors = new()
    {
        [CollectibleType.Mount] = new(1f, 0.75f, 0.35f, 1f),
        [CollectibleType.Minion] = new(0.75f, 0.6f, 1f, 1f),
        [CollectibleType.Orchestrion] = new(0.4f, 0.9f, 0.85f, 1f),
        [CollectibleType.Barding] = new(0.85f, 0.65f, 0.45f, 1f),
        [CollectibleType.Emote] = new(1f, 0.55f, 0.75f, 1f),
        [CollectibleType.Facewear] = new(0.55f, 0.75f, 1f, 1f),
        [CollectibleType.FashionAccessory] = new(0.75f, 0.9f, 0.45f, 1f),
        [CollectibleType.TripleTriadCard] = new(1f, 0.5f, 0.5f, 1f),
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
