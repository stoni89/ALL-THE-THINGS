using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AllTheThings.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private static readonly string VersionText = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
    private string fontFilter = string.Empty;

    public MainWindow(Plugin plugin) : base(
        $"All The Things (v{VersionText})##AllTheThings",
        ImGuiWindowFlags.None)
    {
        this.plugin = plugin;

        // Standardgröße reicht, um alle Optionen ohne Scrollbalken zu zeigen -
        // gilt nur beim allerersten Öffnen, danach darf frei skaliert werden.
        Size = new Vector2(420, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 260),
            MaximumSize = new Vector2(650, 820),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;

        ImGui.TextUnformatted(Loc.T("Kompaktes Overlay", "Compact Overlay"));
        ImGui.TextDisabled(Loc.T("Zeigt fehlende Sammelobjekte der aktuellen Zone an.", "Shows missing collectibles for the current zone."));
        ImGui.Spacing();

        var showOverlay = config.ShowCompactOverlay;
        if (ImGui.Checkbox(Loc.T("Overlay aktivieren", "Enable overlay"), ref showOverlay))
        {
            config.ShowCompactOverlay = showOverlay;
            plugin.CompactOverlayWindow.IsOpen = showOverlay;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted(Loc.T("Anzeige", "Display"));
        ImGui.TextDisabled(Loc.T("Reihenfolge im Overlay anpassen.", "Adjust the order shown in the overlay."));
        ImGui.Spacing();

        for (var i = 0; i < config.TypeOrder.Count; i++)
        {
            var type = config.TypeOrder[i];
            ImGui.PushID(i);

            var enabled = config.ShowType.GetValueOrDefault(type, true);
            if (ImGui.Checkbox($"{Loc.TypeName(type)}##TypeEnabled", ref enabled))
            {
                config.ShowType[type] = enabled;
                config.Save();
            }

            ImGui.SameLine(160);
            ImGui.BeginDisabled(i == 0);
            if (ImGui.ArrowButton("##MoveUp", ImGuiDir.Up))
            {
                (config.TypeOrder[i - 1], config.TypeOrder[i]) = (config.TypeOrder[i], config.TypeOrder[i - 1]);
                config.Save();
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(i == config.TypeOrder.Count - 1);
            if (ImGui.ArrowButton("##MoveDown", ImGuiDir.Down))
            {
                (config.TypeOrder[i + 1], config.TypeOrder[i]) = (config.TypeOrder[i], config.TypeOrder[i + 1]);
                config.Save();
            }
            ImGui.EndDisabled();

            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var transparency = config.CompactTransparency;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat(Loc.T("Transparenz (kompaktes Overlay)", "Transparency (compact overlay)"), ref transparency, 0f, 1f, "%.2f"))
        {
            config.CompactTransparency = transparency;
            config.Save();
        }

        var fontScale = config.CompactFontScale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat(Loc.T("Textgröße (kompaktes Overlay)", "Text size (compact overlay)"), ref fontScale, 0.7f, 2f, "%.2f"))
        {
            config.CompactFontScale = fontScale;
            config.Save();
        }

        var monoLabel = Loc.T("Monospace", "Monospace");
        var standardLabel = Loc.T("Standard", "Standard");
        var currentLabel = config.CompactFontMode switch
        {
            CompactFontMode.Mono => monoLabel,
            CompactFontMode.Custom when !string.IsNullOrEmpty(config.CompactCustomFontName) => config.CompactCustomFontName,
            _ => standardLabel,
        };

        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo(Loc.T("Schriftart (kompaktes Overlay)", "Font (compact overlay)"), currentLabel))
        {
            if (ImGui.Selectable(standardLabel, config.CompactFontMode == CompactFontMode.Standard))
            {
                config.CompactFontMode = CompactFontMode.Standard;
                config.Save();
            }

            if (ImGui.Selectable(monoLabel, config.CompactFontMode == CompactFontMode.Mono))
            {
                config.CompactFontMode = CompactFontMode.Mono;
                config.Save();
            }

            ImGui.Separator();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##FontFilter", Loc.T("Windows-Schriften durchsuchen...", "Search Windows fonts..."), ref fontFilter, 100);

            var installedFonts = WindowsFonts.GetInstalledFonts();
            var filtered = string.IsNullOrWhiteSpace(fontFilter)
                ? installedFonts
                : installedFonts.Where(f => f.Name.Contains(fontFilter, System.StringComparison.OrdinalIgnoreCase)).ToList();

            ImGui.BeginChild("##FontList", new Vector2(0, 150));
            foreach (var font in filtered)
            {
                var isSelected = config.CompactFontMode == CompactFontMode.Custom && config.CompactCustomFontPath == font.Path;
                if (ImGui.Selectable(font.Name, isSelected))
                {
                    config.CompactFontMode = CompactFontMode.Custom;
                    config.CompactCustomFontPath = font.Path;
                    config.CompactCustomFontName = font.Name;
                    config.Save();
                }
            }
            ImGui.EndChild();

            ImGui.EndCombo();
        }

        var showWallet = config.ShowCurrencyWallet;
        if (ImGui.Checkbox(Loc.T("Währungen anzeigen", "Show currencies"), ref showWallet))
        {
            config.ShowCurrencyWallet = showWallet;
            config.Save();
        }
    }
}
