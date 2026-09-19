using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    private enum RailPage
    {
        Settings,
        About,
    }

    private readonly Plugin plugin;
    private static readonly string VersionText = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    private static readonly string IconPath =
        Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Data", "icon.png");

    private string fontFilter = string.Empty;
    private string mountFilter = string.Empty;
    private int selectedNavIndex;
    private RailPage railPage = RailPage.Settings;
    private bool collapsed;
    private bool collapsedLastFrame;
    private Vector2 expandedSize = new(720f, 960f);

    // Nur so hoch wie die Kopfzeile selbst - der Rest (Sidebar/Inhalt) wird beim Einklappen komplett
    // ausgeblendet, siehe PreDraw/Draw. Exakt die Kopfzeilen-Bandhöhe (52, siehe DrawCustomHeader)
    // PLUS das (eingeklappt bewusst knappere, siehe PreDraw) obere/untere WindowPadding von 4 je
    // Seite - keine zusätzliche Marge mehr, sonst wirkt die eingeklappte Titelleiste unnötig hoch.
    // Falls der Inhalt durch Rundungsfehler doch mal 1-2px zu hoch wäre, wird er dank NoScrollbar am
    // Fenster einfach knapp abgeschnitten statt eine Scrollbar zu zeigen.
    private const float CollapsedHeight = 52f + 4f + 4f;

    // Mindesthöhe bewusst so hoch gewählt, dass selbst der Tab mit dem meisten Inhalt (Anzeige, mit
    // beiden Karten: Aussehen + Reihenfolge mit 8 Zeilen) ohne Scrollbalken hineinpasst - der
    // Spieler soll das Fenster gar nicht erst so klein ziehen können, dass eine Scrollbar nötig würde.
    private static readonly WindowSizeConstraints ExpandedSizeConstraints = new()
    {
        MinimumSize = new Vector2(520, 930),
        MaximumSize = new Vector2(1100, 1050),
    };

    private static readonly WindowSizeConstraints CollapsedSizeConstraints = new()
    {
        MinimumSize = new Vector2(420, CollapsedHeight),
        MaximumSize = new Vector2(1100, CollapsedHeight),
    };

    private readonly (FontAwesomeIcon Icon, string Label, Action Draw)[] navItems;

    public MainWindow(Plugin plugin) : base(
        $"All The Things (v{VersionText})##AllTheThings",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;

        navItems = new (FontAwesomeIcon, string, Action)[]
        {
            (FontAwesomeIcon.Cog, Loc.T("Allgemein", "General"), DrawGeneralTab),
            (FontAwesomeIcon.Desktop, Loc.T("Anzeige", "Display"), DrawDisplayTab),
            (FontAwesomeIcon.Bolt, "QoL", DrawQoLTab),
            (FontAwesomeIcon.Plug, Loc.T("Abhängigkeiten", "Dependencies"), DrawDependenciesTab),
            (FontAwesomeIcon.Bug, Loc.T("Debug", "Debug"), DrawDebugTab),
        };

        // Standardgröße reicht, um alle Optionen ohne Scrollbalken zu zeigen -
        // gilt nur beim allerersten Öffnen, danach darf frei skaliert werden.
        Size = expandedSize;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedSizeConstraints;
    }

    public void Dispose() { }

    /// <summary>
    /// Zwingt die Fenstergröße beim Ein-/Ausklappen (siehe DrawCustomHeader/collapsed) auf die
    /// Kopfzeilenhöhe bzw. zurück auf die zuletzt bekannte Größe - muss VOR ImGui.Begin() passieren
    /// (SetNextWindowSize wirkt sonst erst einen Frame zu spät), daher hier statt in Draw(). Bewusst
    /// über Size/SizeCondition (nicht direkt ImGui.SetNextWindowSize) - Dalamud ruft nach PreDraw()
    /// selbst noch SetNextWindowSize(Size, SizeCondition) auf und würde einen eigenen direkten Aufruf
    /// hier sonst mit der (seit dem allerersten Öffnen wirkungslosen) FirstUseEver-Bedingung wieder
    /// überschreiben, wodurch die Wiederherstellung nie sichtbar ankäme.
    /// </summary>
    public override void PreDraw()
    {
        SizeConstraints = collapsed ? CollapsedSizeConstraints : ExpandedSizeConstraints;

        if (!collapsed && collapsedLastFrame)
        {
            Size = expandedSize;
            SizeCondition = ImGuiCond.Always;
        }
        else
        {
            // Nur für den einen Wiederherstellungs-Frame oben "Always" - sonst würde jeder weitere
            // Frame die Fenstergröße erzwingen und der Spieler könnte nie mehr manuell skalieren.
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        collapsedLastFrame = collapsed;

        // Eingeklappt bewusst mit deutlich knapperem Innenabstand oben/unten - muss hier (vor
        // Begin()) statt in Draw() passieren, siehe PushStyle-Kommentar, sonst würde die eingeklappte
        // Titelleiste trotzdem den vollen (großzügigeren) Standardabstand behalten und unnötig hoch
        // wirken.
        ModernUi.PushStyle(collapsed ? new Vector2(12f, 4f) : new Vector2(12f, 12f));
    }

    public override void PostDraw()
    {
        ModernUi.PopStyle();
    }

    /// <summary>
    /// Ohne native Fenster-Titelleiste (siehe ImGuiWindowFlags.NoTitleBar oben) gibt es weder ein
    /// eingebautes Verschieben (funktioniert trotzdem - ImGui erlaubt Ziehen am leeren Fensterinhalt,
    /// solange NoMove nicht gesetzt ist, genau wie beim kompakten Overlay) noch einen Schließen-Knopf -
    /// diese Kopfzeile ersetzt beides durch eigene, dezente Icons statt der Standard-Titelleiste.
    /// </summary>
    private void DrawCustomHeader()
    {
        // Eigene Bandhöhe für die Kopfzeile statt nur "so hoch wie der Text" - Icon/Name/Buttons
        // werden weiter unten INNERHALB dieses Bandes vertikal zentriert, statt es einfach an der
        // Textgröße kleben zu lassen (dadurch sitzt der Inhalt jetzt mittig, mit sichtbarem
        // Abstand über und unter sich, wie bei einer echten Titelleiste).
        const float headerBandHeight = 52f;
        var bandStartY = ImGui.GetCursorPosY();
        var bandStartX = ImGui.GetCursorPosX();
        var bandScreenPos = ImGui.GetCursorScreenPos();

        var regionMaxXEarly = ImGui.GetWindowContentRegionMax().X;
        var spacingEarly = ImGui.GetStyle().ItemSpacing.X;
        float headerButtonWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            headerButtonWidth = ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;

        // Nur der linke Teil (Icon+Name) ist zieh-/doppelklickbar - der Bereich der beiden Buttons
        // rechts wird bewusst ausgespart. Ein überlappender unsichtbarer Button dort würde deren
        // Klicks abfangen, BEVOR sie Schließen/Einklappen erreichen (genau das war der Bug: beide
        // Buttons reagierten plötzlich gar nicht mehr, nur noch der Doppelklick selbst).
        var buttonsAreaWidth = headerButtonWidth * 2f + spacingEarly;
        var dragWidth = MathF.Max(0f, regionMaxXEarly - buttonsAreaWidth - spacingEarly - bandStartX);
        ImGui.InvisibleButton("##HeaderDragArea", new Vector2(dragWidth, headerBandHeight));
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            collapsed = !collapsed;
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        ImGui.SetCursorScreenPos(bandScreenPos);

        // Icon + Name vergrößert, beide mit demselben Skalierungsfaktor.
        const float titleScale = 1.8f;
        ImGui.SetWindowFontScale(titleScale);
        var titleLineHeight = ImGui.GetTextLineHeight();
        ImGui.SetWindowFontScale(1f);

        var rowStartY = bandStartY + (headerBandHeight - titleLineHeight) * 0.5f;
        ImGui.SetCursorPosY(rowStartY);

        var headerIcon = Plugin.TextureProvider.GetFromFile(IconPath).GetWrapOrEmpty();
        ImGui.Image(headerIcon.Handle, new Vector2(titleLineHeight, titleLineHeight));

        ImGui.SameLine();
        ImGui.SetWindowFontScale(titleScale);
        ImGui.TextUnformatted("All The Things");
        ImGui.SetWindowFontScale(1f);

        var regionMaxX = ImGui.GetWindowContentRegionMax().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            // SmallButton nutzt (anders als die vergrößerte Titelzeile) die normale, unskalierte
            // Icon-Schriftgröße ohne Innenabstand - ohne diesen Ausgleich würden die Buttons zu
            // weit oben in der (jetzt höheren) Kopfzeile kleben statt mittig zu sitzen.
            var buttonLineHeight = ImGui.GetTextLineHeight();
            var buttonYOffset = (titleLineHeight - buttonLineHeight) * 0.5f;
            var buttonWidth = ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;

            // Im Ruhezustand transparent (verschmilzt mit dem normalen Fensterhintergrund) - nur
            // beim Hovern/Klicken sichtbar hervorgehoben, statt permanent als eigener grauer Kasten.
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));

            ImGui.SameLine(regionMaxX - buttonWidth);
            ImGui.SetCursorPosY(rowStartY + buttonYOffset);
            if (ImGui.SmallButton($"{FontAwesomeIcon.Times.ToIconString()}##HeaderClose"))
                IsOpen = false;

            var collapseIcon = collapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp;
            ImGui.SameLine(regionMaxX - buttonWidth * 2f - spacing);
            ImGui.SetCursorPosY(rowStartY + buttonYOffset);
            if (ImGui.SmallButton($"{collapseIcon.ToIconString()}##HeaderCollapse"))
                collapsed = !collapsed;

            ImGui.PopStyleColor();
        }

        // Nur zeichnen, wenn auch tatsächlich noch etwas darunter folgt - eingeklappt gibt es nichts
        // zu trennen, und die zusätzliche Höhe würde sonst knapp über die (bewusst knapp bemessene)
        // CollapsedHeight hinausragen.
        if (!collapsed)
        {
            // Unabhängig davon, wie hoch die einzelnen Elemente tatsächlich waren, geht es exakt am
            // Ende des reservierten Bandes weiter - sonst würde der Trenner je nach Zentrierung mal
            // knapper, mal weiter darunter sitzen.
            ImGui.SetCursorPosY(bandStartY + headerBandHeight);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }
    }

    public override void Draw()
    {
        // Nur merken, solange NICHT eingeklappt - sonst würde die (künstlich auf Kopfzeilenhöhe
        // geschrumpfte) Größe während des Einklappens versehentlich als "neue Normalgröße"
        // gespeichert und beim Ausklappen fälschlich wiederhergestellt.
        if (!collapsed)
            expandedSize = ImGui.GetWindowSize();

        DrawCustomHeader();

        if (!collapsed)
        {
            const float railWidth = 40f;
            const float sidebarWidth = 160f;

            ImGui.BeginChild("##OptionsRail", new Vector2(railWidth, 0f), false, ImGuiWindowFlags.NoScrollbar);
            ImGui.Spacing();
            if (ModernUi.RailButton(FontAwesomeIcon.SlidersH, railPage == RailPage.Settings, Loc.T("Einstellungen", "Settings")))
                railPage = RailPage.Settings;
            ImGui.Spacing();
            if (ModernUi.RailButton(FontAwesomeIcon.InfoCircle, railPage == RailPage.About, Loc.T("Über", "About")))
                railPage = RailPage.About;
            ImGui.EndChild();

            ImGui.SameLine();

            if (railPage == RailPage.Settings)
            {
                ImGui.BeginChild("##OptionsSidebar", new Vector2(sidebarWidth, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.SetWindowFontScale(1.1f);
                ImGui.TextUnformatted(Loc.T("Einstellungen", "Settings"));
                ImGui.SetWindowFontScale(1f);
                ImGui.Spacing();
                for (var i = 0; i < navItems.Length; i++)
                {
                    if (ModernUi.SidebarItem(navItems[i].Icon, navItems[i].Label, selectedNavIndex == i))
                        selectedNavIndex = i;
                }
                ImGui.EndChild();

                ImGui.SameLine();

                // NoScrollbar: Die Mindestfenstergröße (siehe ExpandedSizeConstraints) ist bewusst so
                // hoch gewählt, dass jeder Tab-Inhalt hineinpasst - eine Scrollbar soll also gar nicht
                // erst nötig sein/erscheinen. Ohne NoScrollbar würde ImGui bei einer versehentlichen
                // Ein-Pixel-Überlänge sonst wieder eine Scrollbar samt der bekannten Pfeil-Kollision
                // rechts einblenden (siehe Anzeige-Tab).
                ImGui.BeginChild("##OptionsContent", new Vector2(0f, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.Indent(4f);
                navItems[selectedNavIndex].Draw();
                ImGui.Unindent(4f);
                ImGui.EndChild();
            }
            else
            {
                ImGui.BeginChild("##AboutContent", new Vector2(0f, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.Indent(4f);
                DrawAboutPage();
                ImGui.Unindent(4f);
                ImGui.EndChild();
            }
        }
    }

    private void DrawAboutPage()
    {
        ModernUi.SectionHeader(
            Loc.T("Über", "About"),
            Loc.T("Sammel-Tracker pro Karte, inspiriert von \"All the Things\".", "Per-map collectible tracker, inspired by \"All the Things\"."));

        // Bewusst OHNE Karte drumherum - nur das Icon selbst, groß und horizontal zentriert.
        const float aboutIconSize = 160f;
        var aboutIcon = Plugin.TextureProvider.GetFromFile(IconPath).GetWrapOrEmpty();
        var availWidth = ImGui.GetContentRegionAvail().X;
        if (availWidth > aboutIconSize)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - aboutIconSize) * 0.5f);
        ImGui.Image(aboutIcon.Handle, new Vector2(aboutIconSize, aboutIconSize));

        ImGui.SetWindowFontScale(1.2f);
        var nameWidth = ImGui.CalcTextSize("All The Things").X;
        if (availWidth > nameWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - nameWidth) * 0.5f);
        ImGui.TextUnformatted("All The Things");
        ImGui.SetWindowFontScale(1f);

        var versionText = $"{Loc.T("Version", "Version")} {VersionText}";
        var versionWidth = ImGui.CalcTextSize(versionText).X;
        if (availWidth > versionWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - versionWidth) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.TextUnformatted(versionText);
        ImGui.PopStyleColor();
        ImGui.Spacing();

        ModernUi.GroupLabel(Loc.T("Beschreibung", "Description"));
        ModernUi.BeginCard();
        TextDisabledWrapped(Loc.T(
            "Zeigt an, welche Mounts, Minions, Orchestrionrollen, Bardings, Emotes, Facewear, Fashion " +
            "Accessories, Triple-Triad-Karten und Portrait-Rahmen in der aktuellen Zone noch fehlen. " +
            "Die Quest- und Aetheryten-Automation können zusätzlich Questionable bzw. vnavmesh/Lifestream " +
            "steuern, um fehlende Quests und Aetheryten/Kristalle automatisch abzuarbeiten.",
            "Shows which mounts, minions, orchestrion rolls, bardings, emotes, facewear, fashion " +
            "accessories, Triple Triad cards, and portrait frames are still missing in the current zone. " +
            "The quest and aetheryte automations can additionally drive Questionable and vnavmesh/" +
            "Lifestream to automatically work through missing quests and aetherytes/crystals."));
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Unterstützung", "Support"));
        ModernUi.BeginCard();
        TextDisabledWrapped(Loc.T(
            "Gefällt dir das Plugin? Über eine kleine Unterstützung auf Ko-fi freue ich mich sehr.",
            "Enjoying the plugin? A small tip on Ko-fi is very appreciated."));
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.42f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.52f, 0.2f, 1f));
        if (ImGui.Button(Loc.T("Auf Ko-fi unterstützen", "Support on Ko-fi") + "##AboutKofi"))
            Util.OpenLink("https://ko-fi.com/horstbrot");
        ImGui.PopStyleColor(2);
        ModernUi.EndCard();
    }

    private void DrawGeneralTab()
    {
        var config = plugin.Configuration;

        ModernUi.SectionHeader(
            Loc.T("Allgemein", "General"),
            Loc.T("Zeigt fehlende Sammelobjekte der aktuellen Zone an.", "Shows missing collectibles for the current zone."));

        ModernUi.BeginCard();
        var showOverlay = config.ShowCompactOverlay;
        if (ModernUi.ToggleRow(Loc.T("Overlay aktivieren", "Enable overlay"), ref showOverlay))
        {
            config.ShowCompactOverlay = showOverlay;
            plugin.CompactOverlayWindow.IsOpen = showOverlay;
            config.Save();
        }
        ModernUi.EndCard();
    }

    private void DrawDisplayTab()
    {
        var config = plugin.Configuration;

        ModernUi.SectionHeader(
            Loc.T("Anzeige", "Display"),
            Loc.T("Reihenfolge und Darstellung im kompakten Overlay anpassen.", "Adjust the order and look of the compact overlay."));

        ModernUi.GroupLabel(Loc.T("Aussehen", "Appearance"));
        ModernUi.BeginCard();
        var transparency = config.CompactTransparency;
        ModernUi.LabelRow(Loc.T("Transparenz", "Transparency"), 280f);
        if (ImGui.SliderFloat("##Transparency", ref transparency, 0f, 1f, "%.2f"))
        {
            config.CompactTransparency = transparency;
            config.Save();
        }

        ImGui.Spacing();
        var fontScale = config.CompactFontScale;
        ModernUi.LabelRow(Loc.T("Textgröße", "Text size"), 280f);
        if (ImGui.SliderFloat("##FontScale", ref fontScale, 0.7f, 2f, "%.2f"))
        {
            config.CompactFontScale = fontScale;
            config.Save();
        }

        ImGui.Spacing();
        var monoLabel = Loc.T("Monospace", "Monospace");
        var standardLabel = Loc.T("Standard", "Standard");
        var currentLabel = config.CompactFontMode switch
        {
            CompactFontMode.Mono => monoLabel,
            CompactFontMode.Custom when !string.IsNullOrEmpty(config.CompactCustomFontName) => config.CompactCustomFontName,
            _ => standardLabel,
        };

        ModernUi.LabelRow(Loc.T("Schriftart", "Font"), 280f);
        if (ImGui.BeginCombo("##CompactFont", currentLabel))
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
        var showWallet = config.ShowCurrencyWallet;
        if (ModernUi.ToggleRow(Loc.T("Währungen anzeigen", "Show currencies"), ref showWallet))
        {
            config.ShowCurrencyWallet = showWallet;
            config.Save();
        }

        ImGui.Spacing();
        var locked = config.CompactLocked;
        if (ModernUi.ToggleRow(Loc.T("Fenster sperren (Position & Größe)", "Lock window (position & size)"), ref locked))
        {
            config.CompactLocked = locked;
            config.Save();
        }
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Reihenfolge", "Order"));
        ModernUi.BeginCard();
        for (var i = 0; i < config.TypeOrder.Count; i++)
        {
            var type = config.TypeOrder[i];
            ImGui.PushID(i);

            var enabled = config.ShowType.GetValueOrDefault(type, true);
            if (ModernUi.ToggleSwitch("##TypeEnabled", ref enabled))
            {
                config.ShowType[type] = enabled;
                config.Save();
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.TypeName(type));

            // Breite dynamisch aus der aktuellen Button-/Abstandsgröße berechnen, statt eines festen
            // Werts - sonst verschieben sich die Pfeile bei jeder Änderung an FramePadding/ItemSpacing
            // (z.B. für größere Regler) wieder aus dem sichtbaren Bereich.
            var arrowButtonSize = ImGui.GetFrameHeight();
            var arrowsWidth = arrowButtonSize * 2f + ImGui.GetStyle().ItemSpacing.X;
            const float arrowsExtraRightGap = 20f;
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - arrowsWidth - ModernUi.CardMargin - arrowsExtraRightGap + ImGui.GetCursorPosX());
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

            if (i < config.TypeOrder.Count - 1)
                ImGui.Spacing();
        }
        ModernUi.EndCard();
    }

    private void DrawQoLTab()
    {
        var config = plugin.Configuration;

        ModernUi.SectionHeader("QoL", Loc.T("Komfortfunktionen für die Automationen.", "Convenience features for the automations."));

        ModernUi.GroupLabel(Loc.T("Quest-Automation", "Quest automation"));
        ModernUi.BeginCard();
        TextDisabledWrapped(Loc.T("Keine Einstellungen.", "No settings."));
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Aetheryten-Automation", "Aetheryte automation"));
        ModernUi.BeginCard();
        var useSprint = config.UseSprintOnCooldown;
        if (ModernUi.ToggleRow(Loc.T("Sprint auf Cooldown nutzen", "Use Sprint on cooldown"), ref useSprint))
        {
            config.UseSprintOnCooldown = useSprint;
            config.Save();
        }

        ImGui.Spacing();
        DrawAetheryteMountPicker(config);
        ModernUi.EndCard();
    }

    /// <summary>
    /// Mount-Auswahl für die Aetheryten-Automation: "Kein Mount" (aus, Standard), "Mount Roulette"
    /// (zufällige Auswahl unter den eigenen freigeschalteten Mounts, siehe
    /// Plugin.TryRequestAetheryteMount) oder ein konkretes Mount - ausgegraut, solange gar kein
    /// Mount freigeschaltet ist, da dann keine der Optionen etwas bewirken könnte.
    /// </summary>
    private void DrawAetheryteMountPicker(Configuration config)
    {
        var unlockedMounts = plugin.GetUnlockedMounts();
        var noMountsUnlocked = unlockedMounts.Count == 0;

        var noneLabel = Loc.T("Kein Mount (zu Fuß)", "No mount (on foot)");
        var rouletteLabel = Loc.T("Mount Roulette", "Mount Roulette");
        var currentLabel = config.AetheryteMountId switch
        {
            null => noneLabel,
            0 => rouletteLabel,
            var id => unlockedMounts.FirstOrDefault(m => m.Id == (uint)id.Value)?.Name ?? noneLabel,
        };

        ModernUi.LabelRow(Loc.T("Mount", "Mount"), 280f);
        if (noMountsUnlocked)
            ImGui.BeginDisabled();

        if (ImGui.BeginCombo("##AetheryteMount", currentLabel))
        {
            if (ImGui.Selectable(noneLabel, config.AetheryteMountId == null))
            {
                config.AetheryteMountId = null;
                config.Save();
            }

            if (ImGui.Selectable(rouletteLabel, config.AetheryteMountId == 0))
            {
                config.AetheryteMountId = 0;
                config.Save();
            }

            ImGui.Separator();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##AetheryteMountFilter", Loc.T("Mounts durchsuchen...", "Search mounts..."), ref mountFilter, 100);

            var filtered = string.IsNullOrWhiteSpace(mountFilter)
                ? unlockedMounts
                : unlockedMounts.Where(m => m.Name.Contains(mountFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            ImGui.BeginChild("##AetheryteMountList", new Vector2(0, 150));
            foreach (var mount in filtered)
            {
                var isSelected = config.AetheryteMountId == (int)mount.Id;
                if (ImGui.Selectable(mount.Name, isSelected))
                {
                    config.AetheryteMountId = (int)mount.Id;
                    config.Save();
                }
            }
            ImGui.EndChild();

            ImGui.EndCombo();
        }

        if (noMountsUnlocked)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(Loc.T(
                    "Noch keine Mounts freigeschaltet.",
                    "No mounts unlocked yet."));
            }
        }
    }

    private void DrawDebugTab()
    {
        var config = plugin.Configuration;

        ModernUi.SectionHeader(
            Loc.T("Debug", "Debug"),
            Loc.T("Nur relevant, wenn im Overlay etwas nicht wie erwartet angezeigt wird.", "Only relevant if something in the overlay doesn't show as expected."));

        ModernUi.BeginCard();
        var showDebug = config.ShowDebugInfo;
        if (ModernUi.ToggleRow(Loc.T("Debug-Infos im Overlay anzeigen", "Show debug info in overlay"), ref showDebug))
        {
            config.ShowDebugInfo = showDebug;
            config.Save();
        }
        TextDisabledWrapped(Loc.T(
            "Zeigt Rohzahlen (Zone/Filter/Fehlend) über der Liste im kompakten Overlay.",
            "Shows raw counts (zone/filter/missing) above the list in the compact overlay."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(Loc.T("Aetheryten-/Quest-Cache zurücksetzen", "Reset aetheryte/quest cache")))
            plugin.ResetLiveEntriesCache();
        TextDisabledWrapped(Loc.T(
            "Aetheryten und Quests werden pro Zone zwischengespeichert. Nötig, falls sich der " +
            "Fortschritt (z.B. Questabschluss) ändert, während das Overlay in derselben Zone offen ist.",
            "Aetherytes and quests are cached per zone. Needed if progress (e.g. completing a quest) " +
            "changes while the overlay stays open in the same zone."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var simulateAll = plugin.AetheryteAutomation.SimulateAllCrystals;
        if (ModernUi.ToggleRow(
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
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Aktueller Status", "Current status"));
        ModernUi.BeginCard();
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
        ModernUi.EndCard();
    }

    private static void DrawDependenciesTab()
    {
        ModernUi.SectionHeader(
            Loc.T("Abhängigkeiten", "Dependencies"),
            Loc.T("Für die Automation-Funktionen benötigte Fremdplugins.", "Third-party plugins needed for the automation features."));

        ModernUi.BeginCard();
        DrawPluginStatus("Questionable", "Questionable");
        TextDisabledWrapped(Loc.T(
            "Für die Quest-Automation. Questionable hat selbst weitere Abhängigkeiten " +
            "(z.B. je nach Quest eigene Kampf-/Bewegungs-Plugins) - siehe dessen eigene Dokumentation.",
            "For quest automation. Questionable itself has further dependencies of its own " +
            "(e.g. combat/movement plugins depending on the quest) - see its own documentation."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawPluginStatus("vnavmesh", "vnavmesh");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawPluginStatus("Lifestream", "Lifestream");
        ModernUi.EndCard();
    }

    /// <summary>
    /// Wie ImGui.TextDisabled, aber bricht lange Texte am Fensterrand um, statt die Fensterbreite
    /// zu überschreiten.
    /// </summary>
    private static void TextDisabledWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ModernUi.CardMargin);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
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
