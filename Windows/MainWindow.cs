using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
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
        Size = new Vector2(440, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 280),
            MaximumSize = new Vector2(700, 850),
        };

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Coffee,
            IconColor = new Vector4(1f, 0.8f, 0.4f, 1f),
            ShowTooltip = () => ImGui.SetTooltip(Loc.T("Auf Ko-fi unterstützen", "Support on Ko-fi")),
            Click = _ => Util.OpenLink("https://ko-fi.com/horstbrot"),
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("OptionsTabs"))
        {
            if (ImGui.BeginTabItem(Loc.T("Allgemein", "General")))
            {
                DrawGeneralTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Loc.T("Anzeige", "Display")))
            {
                DrawDisplayTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("QoL"))
            {
                DrawQoLTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Loc.T("Abhängigkeiten", "Dependencies")))
            {
                DrawDependenciesTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Loc.T("Debug", "Debug")))
            {
                DrawDebugTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawGeneralTab()
    {
        var config = plugin.Configuration;

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Kompaktes Overlay", "Compact Overlay"));
        TextDisabledWrapped(Loc.T("Zeigt fehlende Sammelobjekte der aktuellen Zone an.", "Shows missing collectibles for the current zone."));
        ImGui.Spacing();

        var showOverlay = config.ShowCompactOverlay;
        if (ImGui.Checkbox(Loc.T("Overlay aktivieren", "Enable overlay"), ref showOverlay))
        {
            config.ShowCompactOverlay = showOverlay;
            plugin.CompactOverlayWindow.IsOpen = showOverlay;
            config.Save();
        }
    }

    private void DrawDisplayTab()
    {
        var config = plugin.Configuration;

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Anzeige", "Display"));
        TextDisabledWrapped(Loc.T("Reihenfolge im Overlay anpassen.", "Adjust the order shown in the overlay."));
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
                : installedFonts.Where(f => f.Name.Contains(fontFilter, StringComparison.OrdinalIgnoreCase)).ToList();

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showWallet = config.ShowCurrencyWallet;
        if (ImGui.Checkbox(Loc.T("Währungen anzeigen", "Show currencies"), ref showWallet))
        {
            config.ShowCurrencyWallet = showWallet;
            config.Save();
        }

        var locked = config.CompactLocked;
        if (ImGui.Checkbox(Loc.T("Fenster sperren (Position & Größe)", "Lock window (position & size)"), ref locked))
        {
            config.CompactLocked = locked;
            config.Save();
        }
    }

    private void DrawQoLTab()
    {
        var config = plugin.Configuration;

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Quest-Automation", "Quest automation"));
        ImGui.Spacing();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted(Loc.T("Aetheryten-Automation", "Aetheryte automation"));
        ImGui.Spacing();

        var useSprint = config.UseSprintOnCooldown;
        if (ImGui.Checkbox(Loc.T("Sprint auf Cooldown nutzen", "Use Sprint on cooldown"), ref useSprint))
        {
            config.UseSprintOnCooldown = useSprint;
            config.Save();
        }
    }

    private void DrawDebugTab()
    {
        var config = plugin.Configuration;

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Fehlersuche", "Troubleshooting"));
        TextDisabledWrapped(Loc.T(
            "Nur relevant, wenn im Overlay etwas nicht wie erwartet angezeigt wird.",
            "Only relevant if something in the overlay doesn't show as expected."));
        ImGui.Spacing();

        var showDebug = config.ShowDebugInfo;
        if (ImGui.Checkbox(Loc.T("Debug-Infos im Overlay anzeigen", "Show debug info in overlay"), ref showDebug))
        {
            config.ShowDebugInfo = showDebug;
            config.Save();
        }
        TextDisabledWrapped(Loc.T(
            "Zeigt Rohzahlen (Zone/Filter/Fehlend) über der Liste im kompakten Overlay.",
            "Shows raw counts (zone/filter/missing) above the list in the compact overlay."));

        ImGui.Spacing();

        if (ImGui.Button(Loc.T("Aetheryten-/Quest-Cache zurücksetzen", "Reset aetheryte/quest cache")))
            plugin.ResetLiveEntriesCache();
        TextDisabledWrapped(Loc.T(
            "Aetheryten und Quests werden pro Zone zwischengespeichert. Nötig, falls sich der " +
            "Fortschritt (z.B. Questabschluss) ändert, während das Overlay in derselben Zone offen ist.",
            "Aetherytes and quests are cached per zone. Needed if progress (e.g. completing a quest) " +
            "changes while the overlay stays open in the same zone."));

        ImGui.Spacing();

        var simulateAll = plugin.AetheryteAutomation.SimulateAllCrystals;
        if (ImGui.Checkbox(
            Loc.T("Aetheryten-Automation: alle Kristalle simulieren", "Aetheryte automation: simulate all crystals"),
            ref simulateAll))
        {
            plugin.AetheryteAutomation.SimulateAllCrystals = simulateAll;
        }
        TextDisabledWrapped(Loc.T(
            "Läuft auch bereits freigeschaltete Kristalle mit ab (ohne echten Fortschrittsverlust) - " +
            "zum Überprüfen, ob Laufweg und Reihenfolge über alle Bezirke einer Stadt hinweg korrekt " +
            "funktionieren, ohne den eigenen Fortschritt zurücksetzen zu müssen. Nicht gespeichert - " +
            "steht nach einem Neustart wieder aus.",
            "Also walks through already-unlocked crystals (without losing real progress) - to check " +
            "whether the walking order works correctly across a city's districts, without having to " +
            "reset your own progress. Not saved - resets to off after a restart."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted(Loc.T("Aktueller Status", "Current status"));
        var territoryId = Plugin.ClientState.TerritoryType;
        ImGui.TextUnformatted($"{Loc.T("Zone", "Zone")}: {plugin.GetZoneName(territoryId)} ({territoryId})");

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position;
        var posText = playerPos.HasValue
            ? $"{playerPos.Value.X:F3}, {playerPos.Value.Y:F3}, {playerPos.Value.Z:F3}"
            : Loc.T("nicht verfügbar", "not available");
        ImGui.TextUnformatted($"{Loc.T("Eigene Weltposition", "Own world position")}: {posText}");
        TextDisabledWrapped(Loc.T(
            "Zum Herausfinden begehbarer Koordinaten für die manuelle Aetheryten-Tabelle " +
            "(ManualAetheryteWorldPositions in Plugin.cs) - einfach zum Kristall hinlaufen und hier ablesen.",
            "To find walkable coordinates for the manual aetheryte table " +
            "(ManualAetheryteWorldPositions in Plugin.cs) - just walk to the crystal and read it off here."));

        if (playerPos.HasValue && ImGui.Button(Loc.T("In Zwischenablage kopieren", "Copy to clipboard") + "##CopyPlayerPos"))
        {
            ImGui.SetClipboardText($"{playerPos.Value.X.ToString(CultureInfo.InvariantCulture)}f, " +
                                    $"{playerPos.Value.Y.ToString(CultureInfo.InvariantCulture)}f, " +
                                    $"{playerPos.Value.Z.ToString(CultureInfo.InvariantCulture)}f");
        }
    }

    private static void DrawDependenciesTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Für die Automation-Funktionen benötigte Fremdplugins:", "Third-party plugins needed for the automation features:"));
        ImGui.Spacing();

        DrawPluginStatus("Questionable", "Questionable");
        TextDisabledWrapped(Loc.T(
            "Für die Quest-Automation. Questionable hat selbst weitere Abhängigkeiten " +
            "(z.B. je nach Quest eigene Kampf-/Bewegungs-Plugins) - siehe dessen eigene Dokumentation.",
            "For quest automation. Questionable itself has further dependencies of its own " +
            "(e.g. combat/movement plugins depending on the quest) - see its own documentation."));
        ImGui.Spacing();

        DrawPluginStatus("vnavmesh", "vnavmesh");
        ImGui.Spacing();

        DrawPluginStatus("Lifestream", "Lifestream");
    }

    /// <summary>
    /// Wie ImGui.TextDisabled, aber bricht lange Texte am Fensterrand um, statt die Fensterbreite
    /// zu überschreiten.
    /// </summary>
    private static void TextDisabledWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Zeigt Name + grüner Haken/rotes X, ob das Fremdplugin installiert UND geladen ist. Nutzt
    /// Dalamuds eigene Plugin-Liste (InternalName), nicht IPC-Verfügbarkeit - so ist die Anzeige
    /// unabhängig davon, ob das jeweilige Plugin überhaupt eine IPC anbietet.
    /// </summary>
    private static void DrawPluginStatus(string internalName, string displayName)
    {
        var isInstalled = Plugin.PluginInterface.InstalledPlugins
            .Any(p => p.InternalName == internalName && p.IsLoaded);

        // Unicode-Haken/Kreuz (✓/✗) fehlen in Dalamuds Standardschrift und werden als "?"
        // dargestellt - stattdessen FontAwesome-Icons nutzen, die garantiert geladen sind.
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            if (isInstalled)
                ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), FontAwesomeIcon.Check.ToIconString());
            else
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.35f, 1f), FontAwesomeIcon.Times.ToIconString());
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(displayName);
    }
}
