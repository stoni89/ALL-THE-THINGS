using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;

namespace TheExplorersCodex.Windows;

public class MainWindow : Window
{
    private enum RailPage
    {
        Settings,
        Blacklist,
        Statistics,
        Dependencies,
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
    private Vector2 expandedSize = new(746f, 960f);

    // Rechter Randabstand für JEDEN Tab-Inhalt (Einstellungen/Statistik/Plugins/Über) - dieselbe
    // Größe wie dividerToSidebarGap unten (der Abstand von der vertikalen Trennlinie zu "Settings"
    // bzw. den Tab-Inhalten), damit Elemente, die die volle Breite ausnutzen (z.B. ProgressBar mit
    // Breite -1 im Statistik-Tab), nicht bis an den Fensterrand reichen. Die Fensterbreite (siehe
    // expandedSize/ExpandedSizeConstraints) ist um denselben Betrag größer, damit die NUTZBARE
    // Breite dadurch nicht kleiner wird als vorher.
    private const float ContentRightMargin = 26f;

    // Kopfzeilen-Bandhöhe und Titel-Skalierung je Zustand (siehe DrawCustomHeader) - eingeklappt
    // bewusst kleiner, damit die Titelleiste dann wirklich kompakt wirkt, statt (wie zuvor) immer
    // gleich hoch zu bleiben und nur das Fenster darunter wegzuschneiden.
    private const float HeaderBandHeightExpanded = 38f;
    private const float HeaderBandHeightCollapsed = 16f;
    private const float TitleScaleExpanded = 1.6f;
    private const float TitleScaleCollapsed = 0.9f;

    // Eigener, vom Titeltext entkoppelter Skalierungsfaktor fürs Icon - entspricht bewusst dem
    // ALTEN TitleScaleExpanded-Wert, damit das Icon exakt gleich groß bleibt, obwohl der Titeltext
    // jetzt kleiner skaliert wird (das Icon war vorher an die gerenderte Texthöhe gekoppelt, siehe
    // DrawCustomHeader-Kommentar dort).
    private const float IconSizeScale = 1.8f;

    private const float WindowPaddingY = 12f;

    // Eingeklappt bewusst ein eigenes, deutlich knapperes oberes/unteres Innenpolster (statt wie im
    // ausgeklappten Zustand WindowPaddingY) - macht die eingeklappte Titelleiste insgesamt so
    // kompakt wie bei anderen Dalamud-Plugins mit nativer Titelleiste. Verursacht keinen
    // "Verschieben"-Effekt (siehe frühere Version dieses Kommentars): Da im eingeklappten Zustand
    // ohnehin NICHTS außer der Kopfzeile selbst gezeichnet wird, gibt es nichts, wozu die Kopfzeile
    // "verrutschen" könnte - sie zentriert sich in beiden Zuständen unabhängig neu innerhalb ihres
    // eigenen Bandes (siehe DrawCustomHeader).
    private const float WindowPaddingYCollapsed = 2f;

    // Nur so hoch wie die (eingeklappte) Kopfzeile selbst - der Rest (Sidebar/Inhalt) wird beim
    // Einklappen komplett ausgeblendet, siehe PreDraw/Draw. Falls der Inhalt durch Rundungsfehler
    // doch mal 1-2px zu hoch wäre, wird er dank NoScrollbar am Fenster einfach knapp abgeschnitten
    // statt eine Scrollbar zu zeigen.
    private const float CollapsedHeight = HeaderBandHeightCollapsed + WindowPaddingYCollapsed * 2f;

    // Mindesthöhe bewusst so hoch gewählt, dass selbst der Tab mit dem meisten Inhalt (Anzeige, mit
    // beiden Karten: Aussehen + Reihenfolge mit 8 Zeilen) ohne Scrollbalken hineinpasst - der
    // Spieler soll das Fenster gar nicht erst so klein ziehen können, dass eine Scrollbar nötig würde.
    private static readonly WindowSizeConstraints ExpandedSizeConstraints = new()
    {
        MinimumSize = new Vector2(520 + ContentRightMargin, 930),
        MaximumSize = new Vector2(1100 + ContentRightMargin, 1050),
    };

    private static readonly WindowSizeConstraints CollapsedSizeConstraints = new()
    {
        MinimumSize = new Vector2(420, CollapsedHeight),
        MaximumSize = new Vector2(1100, CollapsedHeight),
    };

    private readonly (FontAwesomeIcon Icon, string Label, Action Draw)[] navItems;

    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    public MainWindow(Plugin plugin) : base(
        $"The Explorer's Codex (v{VersionText})##TheExplorersCodex",
        BaseFlags)
    {
        this.plugin = plugin;

        navItems = new (FontAwesomeIcon, string, Action)[]
        {
            (FontAwesomeIcon.Cog, Loc.T("Allgemein", "General"), DrawGeneralTab),
            (FontAwesomeIcon.Desktop, Loc.T("Anzeige", "Display"), DrawDisplayTab),
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
    /// Verhindert, dass das Optionsfenster schon am Titelbildschirm (vor dem Einloggen) oder
    /// während des Lade-/Zonenwechsel-Übergangs (BetweenAreas/BetweenAreas51) mit ggf. veralteten
    /// Daten der letzten Sitzung angezeigt wird, falls es beim letzten Schließen des Spiels offen
    /// war (Dalamud stellt den Öffnen-Zustand von Fenstern über Neustarts hinweg wieder her) - wird
    /// von Dalamuds WindowSystem VOR PreDraw/Draw/PostDraw geprüft, das Fenster erscheint also gar
    /// nicht erst.
    /// </summary>
    public override bool DrawConditions() =>
        Plugin.ClientState.IsLoggedIn && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51];

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

        // Eingeklappt bewusst NoResize: Bei einer so knappen Fensterhöhe (siehe CollapsedHeight)
        // überlappt ImGuis eigene Rahmen-Zieh-Trefferzone (für Größenänderung per Maus) praktisch
        // das GESAMTE Fenster - jeder Klick (auch der Doppelklick zum Wieder-Ausklappen) wurde dann
        // von dieser Ziehzone abgefangen, statt unser InvisibleButton in DrawCustomHeader zu
        // erreichen (beobachtet: Einklappen per Doppelklick ging noch, aber nicht mehr zurück).
        Flags = collapsed ? BaseFlags | ImGuiWindowFlags.NoResize : BaseFlags;

        if (!collapsed && collapsedLastFrame)
        {
            Size = expandedSize;
            SizeCondition = ImGuiCond.Always;
        }
        else if (collapsed && !collapsedLastFrame)
        {
            // Genau wie beim Ausklappen oben muss die Größe hier explizit gesetzt werden - allein
            // CollapsedSizeConstraints zu setzen schrumpft ein bereits offenes, größeres Fenster
            // NICHT von selbst (Größenbeschränkungen wirken nur auf künftiges manuelles Ziehen).
            // Ohne das blieb das Fenster beim Einklappen optisch auf seiner vorherigen (ausgeklappten)
            // Höhe stehen, egal wie klein CollapsedHeight gesetzt wurde.
            Size = new Vector2(expandedSize.X, CollapsedHeight);
            SizeCondition = ImGuiCond.Always;
        }
        else
        {
            // Nur für den einen Wiederherstellungs-Frame oben "Always" - sonst würde jeder weitere
            // Frame die Fenstergröße erzwingen und der Spieler könnte nie mehr manuell skalieren.
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        collapsedLastFrame = collapsed;

        // ImGuis Stil-Standard WindowMinSize (32x32) begrenzt JEDE Fenstergröße nach unten,
        // unabhängig davon, was Size/SizeConstraints sagen - ohne diesen Override konnte das Fenster
        // beim Einklappen nie unter ~32px Höhe schrumpfen, egal welchen (kleineren) CollapsedHeight-
        // Wert wir gesetzt haben. Betrifft auch den ausgeklappten Zustand nicht negativ - dessen
        // tatsächliche Mindestgröße kommt weiterhin allein aus ExpandedSizeConstraints.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));

        // Eingeklappt eigenes, knapperes oberes/unteres Innenpolster (siehe WindowPaddingYCollapsed-
        // Kommentar oben) - macht die eingeklappte Titelleiste so kompakt wie bei anderen Dalamud-
        // Plugins mit nativer Titelleiste.
        ModernUi.PushStyle(new Vector2(12f, collapsed ? WindowPaddingYCollapsed : WindowPaddingY));
    }

    public override void PostDraw()
    {
        ModernUi.PopStyle();
        ImGui.PopStyleVar();
    }

    private static IFontHandle? titleFontHandle;

    /// <summary>
    /// Eigene, in nativer Pixelgröße gebaute Schrift (Noto Sans CJK Medium, etwas kräftiger als die
    /// normale UI-Schrift) für den Titeltext im ausgeklappten Zustand - lazy erzeugt, da
    /// Plugin.PluginInterface bei einem statischen Feld-Initializer noch nicht bereitstünde.
    /// </summary>
    private static IFontHandle GetTitleFontHandle()
    {
        titleFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
            tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new SafeFontConfig
            {
                SizePx = Plugin.PluginInterface.UiBuilder.FontDefaultSizePx * TitleScaleExpanded,
            })));
        return titleFontHandle;
    }

    /// <summary>
    /// Liefert das gepushte Titel-Font-Handle, oder null solange es (z.B. kurz nach dem Start,
    /// während der Font-Atlas noch baut) noch nicht verfügbar ist - der Aufrufer fällt in dem Fall
    /// auf das simple Hochskalieren der Standardschrift zurück.
    /// </summary>
    private static System.IDisposable? PushTitleFontIfAvailable()
    {
        var handle = GetTitleFontHandle();
        return handle is { Available: true } ? handle.Push() : null;
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
        // Abstand über und unter sich, wie bei einer echten Titelleiste). Eingeklappt bewusst
        // kleiner (siehe HeaderBandHeightCollapsed-Kommentar oben).
        var headerBandHeight = collapsed ? HeaderBandHeightCollapsed : HeaderBandHeightExpanded;
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

        // Icon + Name vergrößert - ausgeklappt über eine eigene, in nativer Pixelgröße gebaute
        // Schrift (siehe GetTitleFontHandle) statt per SetWindowFontScale hochskaliert: Skalieren
        // vergrößert nur die vorhandene (kleine) Schrifttextur und sah dadurch sichtbar verschwommen
        // aus. Eingeklappt bleibt es beim einfachen Skalieren - der Faktor ist dort <1, und beim
        // Verkleinern entsteht kein Blur.
        var titleFontPush = collapsed ? null : PushTitleFontIfAvailable();
        var usingTitleFont = titleFontPush != null;
        if (!usingTitleFont)
            ImGui.SetWindowFontScale(collapsed ? TitleScaleCollapsed : TitleScaleExpanded);

        // Texthöhe direkt am tatsächlich gerenderten Titeltext gemessen (statt nur an der
        // generischen Zeilenhöhe), damit die Zentrierung stimmt, unabhängig davon, welche Schrift/
        // welcher Skalierungsfaktor gerade aktiv ist. Icon-Größe bewusst NICHT mehr daran gekoppelt
        // (siehe IconSizeScale) - eingeklappt bleibt es dagegen weiterhin an den Text gekoppelt,
        // da dort ohnehin alles gemeinsam einfach skaliert wird.
        const string titleText = "The Explorer's Codex";
        var titleLineHeight = ImGui.CalcTextSize(titleText).Y;
        var iconSize = collapsed ? titleLineHeight : Plugin.PluginInterface.UiBuilder.FontDefaultSizePx * IconSizeScale;
        var rowHeight = MathF.Max(titleLineHeight, iconSize);
        var rowStartY = bandStartY + (headerBandHeight - rowHeight) * 0.5f;
        ImGui.SetCursorPosY(rowStartY + (rowHeight - iconSize) * 0.5f);

        var headerIcon = Plugin.TextureProvider.GetFromFile(IconPath).GetWrapOrEmpty();
        ImGui.Image(headerIcon.Handle, new Vector2(iconSize, iconSize));

        ImGui.SameLine();
        ImGui.SetCursorPosY(rowStartY + (rowHeight - titleLineHeight) * 0.5f);
        ImGui.TextUnformatted(titleText);

        if (!usingTitleFont)
            ImGui.SetWindowFontScale(1f);
        titleFontPush?.Dispose();

        // Warnhinweis mittig in der Titelleiste (horizontal UND vertikal), statt neben dem Titeltext
        // zu kleben - in normaler (nicht der großen Titel-) Schriftgröße, daher erst NACH dem
        // Dispose des Titelschrift-Handles gezeichnet.
        if (!collapsed && HasMissingRequiredDependency())
        {
            var badgeText = Loc.T("Plugin benötigt", "Plugin needed");
            var badgeSize = MeasureDotBadgeSize(badgeText);
            ImGui.SetCursorPos(new Vector2(
                bandStartX + (regionMaxXEarly - bandStartX - badgeSize.X) * 0.5f,
                bandStartY + (headerBandHeight - badgeSize.Y) * 0.5f));
            DrawDotBadge(badgeText, new Vector4(0.95f, 0.35f, 0.55f, 1f), new Vector4(0.95f, 0.35f, 0.55f, 0.15f), new Vector4(0.95f, 0.35f, 0.55f, 0.6f), new Vector4(1f, 0.75f, 0.85f, 1f));
        }

        var regionMaxX = ImGui.GetWindowContentRegionMax().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            // Normale Button() mit fester, quadratischer Größe statt SmallButton: SmallButton
            // erzwingt intern FramePadding.Y=0, wodurch der Hover-/Klick-Hintergrund viel breiter
            // als hoch wirkte (nur an der Zeilenhöhe des Icons orientiert), statt wie ein richtiger
            // Icon-Button quadratisch zu sein.
            var buttonWidth = ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;
            const float buttonHeightScale = 0.9f;
            var buttonHeight = buttonWidth * buttonHeightScale;
            var buttonYOffset = (rowHeight - buttonHeight) * 0.5f;
            var buttonSize = new Vector2(buttonWidth, buttonHeight);

            // Im Ruhezustand transparent (verschmilzt mit dem normalen Fensterhintergrund) - nur
            // beim Hovern/Klicken sichtbar hervorgehoben, statt permanent als eigener grauer Kasten.
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));

            ImGui.SameLine(regionMaxX - buttonWidth);
            ImGui.SetCursorPosY(rowStartY + buttonYOffset);
            if (ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##HeaderClose", buttonSize))
                IsOpen = false;

            var collapseIcon = collapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp;
            ImGui.SameLine(regionMaxX - buttonWidth * 2f - spacing);
            ImGui.SetCursorPosY(rowStartY + buttonYOffset);
            if (ImGui.Button($"{collapseIcon.ToIconString()}##HeaderCollapse", buttonSize))
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
        // Setzt Loc.MenuLanguageOverride NUR für die Dauer dieses einen Draw()-Aufrufs (siehe
        // Loc-Klassenkommentar) - das kompakte Overlay/Automation-Statustexte laufen über eigene,
        // später im selben Frame folgende Draw()-Aufrufe und sehen den Override dadurch nie.
        var config = plugin.Configuration;
        Loc.MenuLanguageOverride = config.MenuLanguage == MenuLanguage.German;

        try
        {
            DrawInner();
        }
        finally
        {
            Loc.MenuLanguageOverride = null;
        }
    }

    private void DrawInner()
    {
        // Nur merken, solange NICHT eingeklappt - sonst würde die (künstlich auf Kopfzeilenhöhe
        // geschrumpfte) Größe während des Einklappens versehentlich als "neue Normalgröße"
        // gespeichert und beim Ausklappen fälschlich wiederhergestellt.
        if (!collapsed)
            expandedSize = ImGui.GetWindowSize();

        DrawCustomHeader();

        if (!collapsed)
        {
            const float railWidth = 48f;
            const float sidebarWidth = 200f;

            ImGui.BeginChild("##OptionsRail", new Vector2(railWidth, 0f), false, ImGuiWindowFlags.NoScrollbar);
            ImGui.Spacing();
            if (ModernUi.RailButton(FontAwesomeIcon.SlidersH, railPage == RailPage.Settings, Loc.T("Einstellungen", "Settings")))
                railPage = RailPage.Settings;
            ImGui.Spacing();
            if (ModernUi.RailButton(FontAwesomeIcon.Ban, railPage == RailPage.Blacklist, Loc.T("Blacklist", "Blacklist")))
                railPage = RailPage.Blacklist;
            ImGui.Spacing();
            if (ModernUi.RailButton(FontAwesomeIcon.ChartBar, railPage == RailPage.Statistics, Loc.T("Statistik", "Statistics")))
                railPage = RailPage.Statistics;
            ImGui.Spacing();
            if (ModernUi.RailButton(FontAwesomeIcon.Plug, railPage == RailPage.Dependencies, Loc.T("Plugins", "Plugins"), HasMissingRequiredDependency()))
                railPage = RailPage.Dependencies;
            ImGui.Spacing();
            if (ModernUi.RailButton(FontAwesomeIcon.InfoCircle, railPage == RailPage.About, Loc.T("Über", "About")))
                railPage = RailPage.About;
            ImGui.EndChild();

            // Vertikale Trennlinie über die volle Höhe der Icon-Leiste (siehe Referenzbild) - von
            // Hand in die Draw-List gezeichnet, statt Separator() zu benutzen: das erkennt "zwischen
            // zwei per SameLine() verbundenen Elementen" nur bei normalen Widgets als vertikal, NICHT
            // zwischen zwei Child-Fenstern (hätte sonst fälschlich eine horizontale Linie oberhalb
            // des nächsten Inhalts gezeichnet, statt neben den Icons zu stehen). Die Icon-Leiste hat
            // Höhe 0 (= "volle verfügbare Höhe") bekommen, ihr Item-Rect reicht deshalb bereits von
            // ganz oben bis ganz unten im Inhaltsbereich.
            // Getrennte Abstände statt eines gemeinsamen Werts - die Linie soll nah an den Icons
            // bleiben, während zwischen ihr und "Settings"/den Tab-Knöpfen deutlich mehr Luft ist.
            const float railToDividerGap = 10f;
            const float dividerToSidebarGap = 26f;
            var railMin = ImGui.GetItemRectMin();
            var railMax = ImGui.GetItemRectMax();
            var dividerX = railMax.X + railToDividerGap;
            ImGui.GetWindowDrawList().AddLine(new Vector2(dividerX, railMin.Y), new Vector2(dividerX, railMax.Y), ImGui.GetColorU32(ImGuiCol.Separator));

            // Bewusst etwas Abstand zur Trennlinie (statt direkt SameLine()) - im Referenzbild
            // beginnt "Settings" sichtbar rechts von der Linie, nicht direkt daran klebend.
            ImGui.SameLine(0f, railToDividerGap + dividerToSidebarGap);

            if (railPage == RailPage.Settings)
            {
                ImGui.BeginChild("##OptionsSidebar", new Vector2(sidebarWidth, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.SetWindowFontScale(1.5f);
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
                ImGui.BeginChild("##OptionsContent", new Vector2(-ContentRightMargin, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.Indent(4f);
                navItems[selectedNavIndex].Draw();
                ImGui.Unindent(4f);
                ImGui.EndChild();
            }
            else if (railPage == RailPage.Blacklist)
            {
                ImGui.BeginChild("##BlacklistContent", new Vector2(-ContentRightMargin, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.Indent(4f);
                DrawBlacklistPage();
                ImGui.Unindent(4f);
                ImGui.EndChild();
            }
            else if (railPage == RailPage.Statistics)
            {
                ImGui.BeginChild("##StatisticsContent", new Vector2(-ContentRightMargin, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.Indent(4f);
                DrawStatisticsPage();
                ImGui.Unindent(4f);
                ImGui.EndChild();
            }
            else if (railPage == RailPage.Dependencies)
            {
                ImGui.BeginChild("##DependenciesContent", new Vector2(-ContentRightMargin, 0f), false, ImGuiWindowFlags.NoScrollbar);
                ImGui.Spacing();
                ImGui.Indent(4f);
                DrawDependenciesPage();
                ImGui.Unindent(4f);
                ImGui.EndChild();
            }
            else
            {
                ImGui.BeginChild("##AboutContent", new Vector2(-ContentRightMargin, 0f), false, ImGuiWindowFlags.NoScrollbar);
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
        // Bewusst OHNE Karte drumherum - nur das Icon selbst, groß und horizontal zentriert.
        const float aboutIconSize = 160f;
        var aboutIcon = Plugin.TextureProvider.GetFromFile(IconPath).GetWrapOrEmpty();
        var availWidth = ImGui.GetContentRegionAvail().X;
        if (availWidth > aboutIconSize)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - aboutIconSize) * 0.5f);
        ImGui.Image(aboutIcon.Handle, new Vector2(aboutIconSize, aboutIconSize));

        ImGui.SetWindowFontScale(1.2f);
        var nameWidth = ImGui.CalcTextSize("The Explorer's Codex").X;
        if (availWidth > nameWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - nameWidth) * 0.5f);
        ImGui.TextUnformatted("The Explorer's Codex");
        ImGui.SetWindowFontScale(1f);

        var versionText = $"{Loc.T("Version", "Version")} {VersionText}";
        var versionWidth = ImGui.CalcTextSize(versionText).X;
        if (availWidth > versionWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - versionWidth) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.TextUnformatted(versionText);
        ImGui.PopStyleColor();

        // Deutlich mehr Luft zur Versionsnummer, statt der Karte direkt darunter kleben zu lassen.
        ImGui.Dummy(new Vector2(0f, 28f));

        DrawSupportCard(availWidth);
        DrawConnectSection();
    }

    /// <summary>
    /// Unterstützungs-Karte: Herz-Icon in einem umrandeten Kreis, zentrierter Titel, links-
    /// bündiger Absatz und ein über die volle Kartenbreite gehender Ko-fi-Knopf, mit eigenfarbigem
    /// (statt dem sonst überall gedämpften grauen) Kartenrand, damit die Karte bewusst heraussticht.
    /// Bewusst schmaler als der restliche Inhaltsbereich, mit demselben Abstand links (zur
    /// Trennlinie der Icon-Leiste) wie rechts (zum Fensterrand) - <paramref name="outerAvailWidth"/>
    /// ist die volle Breite des Inhaltsbereichs, gemessen VOR jedem kartenspezifischen Einzug.
    /// </summary>
    private static void DrawSupportCard(float outerAvailWidth)
    {
        // EndCard() legt am Ende noch einmal denselben CardMargin außen an - daher wird zusätzlich
        // zum eigenen, größeren Rand auch 2x CardMargin abgezogen, damit die Karte am Ende exakt
        // outerMargin von der Trennlinie UND vom Fensterrand entfernt landet (siehe Herleitung in
        // den Commit-Notizen: Indent(outerMargin) VOR BeginCard(), Unindent(outerMargin) NACH
        // EndCard(), symmetrisch).
        const float outerMargin = 40f;
        ImGui.Indent(outerMargin);
        ModernUi.BeginCard();
        var innerAvail = outerAvailWidth - outerMargin * 2f - ModernUi.CardMargin * 2f;
        var drawList = ImGui.GetWindowDrawList();
        var accent = ModernUi.Accent;

        const float iconDiameter = 52f;
        var iconTopLeft = ImGui.GetCursorScreenPos() + new Vector2((innerAvail - iconDiameter) * 0.5f, 0f);
        var iconCenter = iconTopLeft + new Vector2(iconDiameter * 0.5f, iconDiameter * 0.5f);
        drawList.AddCircleFilled(iconCenter, iconDiameter * 0.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.18f)), 32);
        drawList.AddCircle(iconCenter, iconDiameter * 0.5f, ImGui.ColorConvertFloat4ToU32(accent), 32, 1.5f);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var glyph = FontAwesomeIcon.Heart.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(iconCenter - glyphSize * 0.5f, ImGui.ColorConvertFloat4ToU32(accent), glyph);
        }
        ImGui.Dummy(new Vector2(innerAvail, iconDiameter));
        ImGui.Spacing();

        var title = Loc.T("Aus Leidenschaft entwickelt", "Built with passion");
        ImGui.SetWindowFontScale(1.1f);
        var titleWidth = ImGui.CalcTextSize(title).X;
        if (innerAvail > titleWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (innerAvail - titleWidth) * 0.5f);
        ImGui.TextUnformatted(title);
        ImGui.SetWindowFontScale(1f);
        ImGui.Spacing();

        // Eigener Textumbruch statt des gemeinsamen TextDisabledWrapped-Helfers: der Helfer
        // berechnet die Umbruchbreite aus dem LIVE ImGui-Inhaltsbereich, der hier (wegen des
        // zusätzlichen outerMargin-Einzugs) breiter wäre als innerAvail - der Text würde sonst über
        // den sichtbaren Kartenrand hinauslaufen.
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + innerAvail);
        ImGui.TextWrapped(Loc.T(
            "Dieses Plugin entsteht Update für Update in meiner Freizeit. Falls es dir das Spiel etwas " +
            "leichter macht, ist eine kleine Spende auf Ko-fi eine schöne Geste - ganz ohne Verpflichtung. " +
            "Danke, dass du dabei bist!",
            "This plugin is built update by update in my free time. If it's made your playtime a little " +
            "easier, a small Ko-fi donation is a nice gesture - never expected. Thanks for being part of this!"));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ModernUi.AccentHover);
        if (IconTextButton("AboutKofi", FontAwesomeIcon.MugHot, Loc.T("Auf Ko-fi unterstützen", "Support on Ko-fi"), new Vector2(innerAvail, 0f)))
            Util.OpenLink("https://ko-fi.com/horstbrot");
        ImGui.PopStyleColor(2);

        ModernUi.EndCard(borderColor: new Vector4(accent.X, accent.Y, accent.Z, 0.55f));
        ImGui.Unindent(outerMargin);
    }

    /// <summary>
    /// Button, dessen sichtbarer Inhalt (Icon + Text) komplett manuell in die Draw-List gezeichnet
    /// wird, statt einen kombinierten "Icon Text"-String direkt an ImGui.Button zu übergeben - ein
    /// einzelner String kann nur in EINER Schrift gerendert werden, die Icon-Schrift enthält aber
    /// keine normalen Buchstaben (Text wäre unlesbar) und die Standardschrift enthält nicht jedes
    /// Icon-Glyph (führte dazu, dass z.B. MugHot/CodeBranch als leere Fläche statt als Symbol
    /// erschienen). ImGui.Button bekommt daher ein unsichtbares Label ("##id") und übernimmt nur
    /// Größe/Klick/Hover-Optik, der eigentliche Inhalt kommt hinterher on top.
    /// </summary>
    private static bool IconTextButton(string id, FontAwesomeIcon icon, string text, Vector2 size)
    {
        var clicked = ImGui.Button($"##{id}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        // Ohne Text (reiner Icon-Knopf, siehe DrawDependencyCard) kein Abstand, sonst säße das Icon nicht mittig.
        var gap = string.IsNullOrEmpty(text) ? 0f : 8f;
        string iconGlyph;
        Vector2 iconSize;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            iconGlyph = icon.ToIconString();
            iconSize = ImGui.CalcTextSize(iconGlyph);
        }
        var textSize = ImGui.CalcTextSize(text);
        var contentWidth = iconSize.X + gap + textSize.X;
        var contentStartX = min.X + (max.X - min.X - contentWidth) * 0.5f;

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            drawList.AddText(new Vector2(contentStartX, min.Y + (max.Y - min.Y - iconSize.Y) * 0.5f), textColor, iconGlyph);
        drawList.AddText(new Vector2(contentStartX + iconSize.X + gap, min.Y + (max.Y - min.Y - textSize.Y) * 0.5f), textColor, text);

        return clicked;
    }

    /// <summary>Größe, die ein per IconTextButton gezeichneter Button für Icon+Text+Innenabstand braucht.</summary>
    private static Vector2 MeasureIconTextButtonSize(FontAwesomeIcon icon, string text, Vector2 padding)
    {
        float iconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X;
        var textSize = ImGui.CalcTextSize(text);
        var gap = string.IsNullOrEmpty(text) ? 0f : 8f;
        return new Vector2(iconWidth + gap + textSize.X + padding.X * 2f, textSize.Y + padding.Y * 2f);
    }

    /// <summary>
    /// "CONNECT"-Trenner (Linie-Text-Linie, wie im Vorgabe-Screenshot) gefolgt vom GitHub-Knopf.
    /// </summary>
    private static void DrawConnectSection()
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var label = Loc.T("VERBINDEN", "CONNECT");

        string linkGlyph;
        float linkGlyphWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            linkGlyph = FontAwesomeIcon.Link.ToIconString();
            linkGlyphWidth = ImGui.CalcTextSize(linkGlyph).X;
        }
        var labelWidth = ImGui.CalcTextSize(label).X;

        const float iconToLabelGap = 6f;
        const float lineGap = 10f;
        var centerWidth = linkGlyphWidth + iconToLabelGap + labelWidth;
        var lineWidth = MathF.Max(0f, (avail - centerWidth - lineGap * 2f) * 0.5f);

        var drawList = ImGui.GetWindowDrawList();
        var lineY = ImGui.GetCursorScreenPos().Y + ImGui.GetTextLineHeight() * 0.5f;
        var startX = ImGui.GetCursorScreenPos().X;
        var lineColor = ImGui.ColorConvertFloat4ToU32(ModernUi.CardBorder);
        drawList.AddLine(new Vector2(startX, lineY), new Vector2(startX + lineWidth, lineY), lineColor);
        drawList.AddLine(new Vector2(startX + avail - lineWidth, lineY), new Vector2(startX + avail, lineY), lineColor);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + lineWidth + lineGap);
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            ImGui.TextUnformatted(linkGlyph);
        ImGui.SameLine(0f, iconToLabelGap);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Spacing();

        // Dalamuds FontAwesomeIcon-Enum enthält nur die "Solid"-Icons, keine Marken-/Brand-Icons -
        // daher CodeBranch statt eines echten GitHub-Logos als naheliegender Ersatz für einen
        // Quellcode-Link.
        var githubText = Loc.T("GitHub", "GitHub");
        var buttonSize = MeasureIconTextButtonSize(FontAwesomeIcon.CodeBranch, githubText, new Vector2(14f, 7f));
        if (avail > buttonSize.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - buttonSize.X) * 0.5f);

        // Dunkles GitHub-Grau statt des sonst transparent/dezenten Knopf-Stils, damit der Knopf als
        // eigene, erkennbare Marke heraussticht statt mit dem Hintergrund zu verschmelzen.
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.16f, 0.18f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.22f, 0.25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.10f, 0.11f, 0.13f, 1f));
        if (IconTextButton("AboutGitHub", FontAwesomeIcon.CodeBranch, githubText, buttonSize))
            Util.OpenLink("https://github.com/stoni89/explorers-codex");
        ImGui.PopStyleColor(3);
    }

    private void DrawGeneralTab()
    {
        var config = plugin.Configuration;

        ModernUi.SectionHeader(
            Loc.T("Allgemein", "General"),
            Loc.T("Zeigt fehlende Sammelobjekte der aktuellen Zone an.", "Shows missing collectibles for the current zone."));

        ModernUi.GroupLabel(Loc.T("Sprache", "Language"));
        ModernUi.BeginCard();
        var languageLabels = new (MenuLanguage Language, string Label)[]
        {
            (MenuLanguage.German, "Deutsch"),
            (MenuLanguage.English, "English"),
        };

        ModernUi.LabelRow(Loc.T("Menüsprache", "Menu language"), 280f, Loc.T(
            "Gilt nur für dieses Menü - das kompakte Overlay folgt weiterhin der Spielsprache.",
            "Only affects this menu - the compact overlay keeps following the game language."));
        var currentLanguageLabel = languageLabels.First(l => l.Language == config.MenuLanguage).Label;
        if (ImGui.BeginCombo("##MenuLanguage", currentLanguageLabel))
        {
            foreach (var (language, label) in languageLabels)
            {
                if (ImGui.Selectable(label, config.MenuLanguage == language))
                {
                    config.MenuLanguage = language;
                    config.Save();
                }
            }

            ImGui.EndCombo();
        }

        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Overlay", "Overlay"));
        ModernUi.BeginCard();
        var showOverlay = config.ShowCompactOverlay;
        if (ModernUi.ToggleRow(Loc.T("Overlay aktivieren", "Enable overlay"), ref showOverlay))
        {
            config.ShowCompactOverlay = showOverlay;
            plugin.CompactOverlayWindow.IsOpen = showOverlay;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showAutomationButtons = config.ShowAutomationButtons;
        if (ModernUi.ToggleRow(Loc.T("Automation-Knöpfe anzeigen", "Show automation buttons"), ref showAutomationButtons))
        {
            config.ShowAutomationButtons = showAutomationButtons;
            config.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T(
                "Blendet nur die Start-/Stopp-Knöpfe im Overlay aus - laufende Automationen werden dadurch nicht gestoppt.",
                "Only hides the start/stop buttons in the overlay - running automations keep running."));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showWallet = config.ShowCurrencyWallet;
        if (ModernUi.ToggleRow(Loc.T("Währungen anzeigen", "Show currencies"), ref showWallet))
        {
            config.ShowCurrencyWallet = showWallet;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showGoToIcon = config.ShowGoToIcon;
        if (ModernUi.ToggleRow(Loc.T("\"Hinlaufen\"-Icon anzeigen", "Show \"go to\" icon"), ref showGoToIcon))
        {
            config.ShowGoToIcon = showGoToIcon;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var hideOverlayWhenEmpty = config.HideOverlayWhenEmpty;
        if (ModernUi.ToggleRow(Loc.T("Overlay bei leerer Zone ausblenden", "Hide overlay when zone is empty"), ref hideOverlayWhenEmpty, Loc.T(
                "Blendet das Overlay komplett aus, solange es in der aktuellen Zone (nach allen aktiven Filtern) nichts Fehlendes gibt - laufende Automationen laufen davon unbeeinflusst weiter.",
                "Completely hides the overlay while there's nothing missing in the current zone (after all active filters) - running automations keep running unaffected.")))
        {
            config.HideOverlayWhenEmpty = hideOverlayWhenEmpty;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Automatisch abschalten, falls Allagan Tools nachträglich deinstalliert/deaktiviert wurde -
        // gleiches Muster wie EnableAllaganToolsIntegration (siehe QoL-Karte weiter unten).
        var allaganToolsAvailableForRetainerCounts = Plugin.IsAllaganToolsAvailable();
        if (config.ShowRetainerItemCounts && !allaganToolsAvailableForRetainerCounts)
        {
            config.ShowRetainerItemCounts = false;
            config.Save();
        }

        if (!allaganToolsAvailableForRetainerCounts)
            ImGui.BeginDisabled();

        var showRetainerItemCounts = config.ShowRetainerItemCounts;
        if (ModernUi.ToggleRow(Loc.T("Retainer-Bestände bei Währungen anzeigen", "Show retainer stock next to currencies"), ref showRetainerItemCounts, Loc.T(
                "Zeigt hinter jeder Währung unter \"Deine Währungen\" zusätzlich \"(<Anzahl>)\" mit der auf den eigenen Retainern liegenden Menge - Hover zeigt, welcher Retainer wie viel besitzt. Erfordert Allagan Tools.",
                "Shows \"(<count>)\" after each currency under \"Your currencies\" with how much of it sits on your retainers - hover to see which retainer holds how much. Requires Allagan Tools.")))
        {
            config.ShowRetainerItemCounts = showRetainerItemCounts;
            config.Save();
        }

        if (!allaganToolsAvailableForRetainerCounts)
            ImGui.EndDisabled();

        if (!allaganToolsAvailableForRetainerCounts && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("Allagan Tools ist nicht installiert.", "Allagan Tools is not installed."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showAllItems = config.ShowAllItems;
        if (ModernUi.ToggleRow(Loc.T("Alle Gegenstände anzeigen", "Show all items"), ref showAllItems, Loc.T(
                "Zeige alle Items/Daten, auch wenn sie durch ein nicht erreichtes Achievement oder nicht freigeschaltete Ränge (z.B. bei Beast-Tribe-Händlern) aktuell nicht erreichbar sind.",
                "Show all items/data, even if they're currently unreachable due to a not-yet-completed achievement or unlocked rank (e.g. with beast tribe vendors).")))
        {
            config.ShowAllItems = showAllItems;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showOnlyActiveEventItems = config.ShowOnlyActiveEventItems;
        if (ModernUi.ToggleRow(Loc.T("Nur aktive Event-Gegenstände anzeigen", "Show only active event items"), ref showOnlyActiveEventItems, Loc.T(
                "Blendet nur Saisonevent-Gegenstände aus, deren Event gerade nicht läuft - alle anderen Gegenstände bleiben sichtbar.",
                "Only hides seasonal event items whose event isn't currently running - all other items stay visible.")))
        {
            config.ShowOnlyActiveEventItems = showOnlyActiveEventItems;
            config.Save();
        }
        ModernUi.EndCard();

        ModernUi.GroupLabel("QoL");
        ModernUi.BeginCard();
        var showNavigationArrow = config.ShowNavigationArrow;
        if (ModernUi.ToggleRow(Loc.T("Wegweiser-Pfeil anzeigen", "Show navigation arrow"), ref showNavigationArrow))
        {
            config.ShowNavigationArrow = showNavigationArrow;
            config.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T(
                "Zeigt einen verschiebbaren Pfeil zum aktuellen Ziel (Automation, \"Hinlaufen\"-Icon oder Karten-Link) - verschwindet bei Ankunft oder per Rechtsklick.",
                "Shows a movable arrow pointing to the current target (automation, \"go to\" icon or map link) - disappears on arrival or right-click."));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var useSprint = config.UseSprintOnCooldown;
        if (ModernUi.ToggleRow(Loc.T("Sprint auf Cooldown nutzen", "Use Sprint on cooldown"), ref useSprint))
        {
            config.UseSprintOnCooldown = useSprint;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Automatisch abschalten, falls Allagan Tools nachträglich deinstalliert/deaktiviert wurde -
        // sonst bliebe die Option "an", obwohl der Klick-Handler (siehe Plugin.OpenAllaganToolsItemInfo)
        // mangels Plugin ohnehin nichts mehr täte.
        var allaganToolsAvailable = Plugin.IsAllaganToolsAvailable();
        if (config.EnableAllaganToolsIntegration && !allaganToolsAvailable)
        {
            config.EnableAllaganToolsIntegration = false;
            config.Save();
        }

        if (!allaganToolsAvailable)
            ImGui.BeginDisabled();

        var enableAllaganTools = config.EnableAllaganToolsIntegration;
        if (ModernUi.ToggleRow(Loc.T("Allagan-Tools-Integration aktivieren", "Enable Allagan Tools integration"), ref enableAllaganTools, Loc.T(
                "Aktiviert die Möglichkeit, mit SHIFT + Linksklick mehr Informationen zu den Items oder Currencys zu bekommen.",
                "Enables the ability to get more information about items or currencies via SHIFT + left-click.")))
        {
            config.EnableAllaganToolsIntegration = enableAllaganTools;
            config.Save();
        }

        if (!allaganToolsAvailable)
            ImGui.EndDisabled();

        if (!allaganToolsAvailable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("Allagan Tools ist nicht installiert.", "Allagan Tools is not installed."));

        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Automation", "Automation"));
        ModernUi.BeginCard();
        DrawCombatPluginPicker(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawAetheryteMountPicker(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawChocoboCompanionSettings(config);
        ModernUi.EndCard();
    }

    /// <summary>
    /// Auswahl des Kampf-Plugins (siehe Configuration.CombatPlugin/CombatPluginBridge) - nur
    /// tatsächlich installierte Plugins sind wählbar. Ist nur eines installiert, steht es fest
    /// eingetragen (Auswahl ausgegraut), ist keines installiert, verweist der Tooltip auf die
    /// Plugins-Seite.
    /// </summary>
    private static void DrawCombatPluginPicker(Configuration config)
    {
        Plugin.EnsureCombatPluginDefault();
        var installed = CombatPluginBridge.GetInstalled();
        var effective = CombatPluginBridge.GetEffective();

        ModernUi.LabelRow(Loc.T("Kampf-Plugin", "Combat plugin"), 280f, Loc.T(
            "Welches Plugin bei der Hunting-Log-Automation (und kampfpflichtigen Quest-Schritten) den Kampf übernimmt.",
            "Which plugin handles combat during the hunting log automation (and combat-required quest steps)."));

        var pickerEnabled = installed.Count > 1;
        if (!pickerEnabled)
            ImGui.BeginDisabled();

        var currentLabel = effective is { } current
            ? CombatPluginBridge.DisplayName(current)
            : Loc.T("Keines installiert", "None installed");
        if (ImGui.BeginCombo("##CombatPlugin", currentLabel))
        {
            foreach (var kind in Enum.GetValues<CombatPluginKind>())
            {
                var isInstalled = installed.Contains(kind);
                if (!isInstalled)
                    ImGui.BeginDisabled();

                if (ImGui.Selectable(CombatPluginBridge.DisplayName(kind), effective == kind) && isInstalled && config.CombatPlugin != kind)
                {
                    // Das bisher genutzte Plugin nicht einfach weiterlaufen lassen (siehe CombatPluginBridge.SetCombatMode(false)).
                    Plugin.CombatPlugin.SetCombatMode(false);
                    config.CombatPlugin = kind;
                    config.Save();
                }

                if (!isInstalled)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(Loc.T("Nicht installiert - siehe Plugins-Seite.", "Not installed - see the Plugins page."));
                }
            }

            ImGui.EndCombo();
        }

        if (!pickerEnabled)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(installed.Count == 0
                    ? Loc.T(
                        "Kein Kampf-Plugin installiert (RotationSolver Reborn oder Wrath Combo) - siehe Plugins-Seite.",
                        "No combat plugin installed (RotationSolver Reborn or Wrath Combo) - see the Plugins page.")
                    : Loc.T(
                        $"Nur {currentLabel} ist installiert und wird automatisch genutzt.",
                        $"Only {currentLabel} is installed and is used automatically."));
            }
        }
    }

    /// <summary>
    /// Chocobo-Begleiter-Automation (siehe Configuration.UseChocoboCompanion/ChocoboCompanionSupport) -
    /// der Haupt-Toggle ist ausgegraut, bis die Quest "My Feisty Little Chocobo" abgeschlossen ist
    /// (Plugin.IsChocoboCompanionUnlocked, schaltet das System überhaupt erst frei); die Stance-
    /// Combobox zusätzlich, solange der Toggle selbst aus ist. Jede einzelne Stance in der Combobox
    /// ist wiederum erst ab ihrem eigenen Stance-Level wählbar (Plugin.IsChocoboStanceUnlocked) -
    /// außer Free Stance, die keins braucht - auch wenn Toggle/Combobox insgesamt schon aktivierbar sind.
    /// </summary>
    private static void DrawChocoboCompanionSettings(Configuration config)
    {
        var unlocked = Plugin.IsChocoboCompanionUnlocked();

        if (!unlocked)
            ImGui.BeginDisabled();

        var useChocobo = config.UseChocoboCompanion;
        if (ModernUi.ToggleRow(Loc.T("Chocobo-Begleiter nutzen", "Use Chocobo Companion"), ref useChocobo, Loc.T(
                "Lässt die Quest- und Hunting-Log-Automation den Chocobo-Begleiter beschwören und am Leben erhalten (verbraucht dabei Gysahl Greens).",
                "Lets the quest and hunting log automation summon and keep the Chocobo Companion alive (consumes Gysahl Greens).")))
        {
            config.UseChocoboCompanion = useChocobo;
            config.Save();
        }

        if (!unlocked)
            ImGui.EndDisabled();

        if (!unlocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(Loc.T(
                "Erfordert die abgeschlossene Quest \"My Feisty Little Chocobo\".",
                "Requires the completed quest \"My Feisty Little Chocobo\"."));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var stanceComboEnabled = unlocked && config.UseChocoboCompanion;
        if (!stanceComboEnabled)
            ImGui.BeginDisabled();

        var stances = new (ChocoboStance Stance, string Label)[]
        {
            (ChocoboStance.Attacker, Loc.T("Angreifer", "Attacker")),
            (ChocoboStance.Defender, Loc.T("Verteidiger", "Defender")),
            (ChocoboStance.Healer, Loc.T("Heiler", "Healer")),
            (ChocoboStance.FreeStance, Loc.T("Freie Haltung", "Free Stance")),
        };

        ModernUi.LabelRow(Loc.T("Chocobo-Haltung", "Chocobo stance"), 280f);
        var currentStanceLabel = string.Empty;
        foreach (var (stance, label) in stances)
        {
            if (stance == config.ChocoboStance)
                currentStanceLabel = label;
        }

        if (ImGui.BeginCombo("##ChocoboStance", currentStanceLabel))
        {
            foreach (var (stance, label) in stances)
            {
                var stanceUnlocked = Plugin.IsChocoboStanceUnlocked(stance);
                if (!stanceUnlocked)
                    ImGui.BeginDisabled();

                if (ImGui.Selectable(label, config.ChocoboStance == stance) && stanceUnlocked)
                {
                    config.ChocoboStance = stance;
                    config.Save();
                }

                if (!stanceUnlocked)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(Loc.T(
                            "Erfordert mindestens Level 1 in dieser Stance (steigt durch Kampfeinsatz in ihr).",
                            "Requires at least level 1 in this stance (gained by using it in combat)."));
                    }
                }
            }

            ImGui.EndCombo();
        }

        if (!stanceComboEnabled)
            ImGui.EndDisabled();
    }

    private void DrawDisplayTab()
    {
        var config = plugin.Configuration;

        ModernUi.SectionHeader(
            Loc.T("Anzeige", "Display"),
            Loc.T("Reihenfolge und Darstellung im kompakten Overlay anpassen.", "Adjust the order and look of the compact overlay."));

        ModernUi.GroupLabel(Loc.T("Overlay-Aussehen", "Overlay appearance"));
        ModernUi.BeginCard();
        var transparency = config.CompactTransparency;
        ModernUi.LabelRow(Loc.T("Transparenz", "Transparency"), 280f);
        if (ImGui.SliderFloat("##Transparency", ref transparency, 0f, 1f, "%.2f"))
        {
            config.CompactTransparency = transparency;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var fontScale = config.CompactFontScale;
        ModernUi.LabelRow(Loc.T("Textgröße", "Text size"), 280f);
        if (ImGui.SliderFloat("##FontScale", ref fontScale, 0.7f, 2f, "%.2f"))
        {
            config.CompactFontScale = fontScale;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
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
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Pfeil-Aussehen", "Arrow appearance"));
        ModernUi.BeginCard();
        var arrowWidth = config.NavigationArrowWidth;
        ModernUi.LabelRow(Loc.T("Pfeil-Breite", "Arrow width"), 280f);
        if (ImGui.SliderFloat("##NavigationArrowWidth", ref arrowWidth, 40f, 300f, "%.0f"))
        {
            config.NavigationArrowWidth = arrowWidth;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var arrowHeight = config.NavigationArrowHeight;
        ModernUi.LabelRow(Loc.T("Pfeil-Höhe", "Arrow height"), 280f);
        if (ImGui.SliderFloat("##NavigationArrowHeight", ref arrowHeight, 40f, 300f, "%.0f"))
        {
            config.NavigationArrowHeight = arrowHeight;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var arrowColor = config.NavigationArrowColor;
        ModernUi.LabelRow(Loc.T("Pfeil-Farbe", "Arrow color"), 280f);
        if (ImGui.ColorEdit4("##NavigationArrowColor", ref arrowColor, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoInputs))
        {
            config.NavigationArrowColor = arrowColor;
            config.Save();
        }
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Reihenfolge", "Order"));
        ModernUi.BeginCard();
        for (var i = 0; i < config.TypeOrder.Count; i++)
        {
            var type = config.TypeOrder[i];
            ImGui.PushID(i);

            // Bewusst in der ursprünglichen (kleineren) Größe belassen, nicht im per ToggleRow
            // genutzten ToggleHeightScale - hier stehen viele Zeilen dicht untereinander, größere
            // Schalter würden die Liste unnötig aufblähen.
            var enabled = config.ShowType.GetValueOrDefault(type, true);
            if (ModernUi.ToggleSwitch("##TypeEnabled", ref enabled, 0.8f))
            {
                config.ShowType[type] = enabled;
                config.Save();
            }

            ImGui.SameLine();
            if (Plugin.IsTypeCurrentlyPossible(type))
            {
                ImGui.TextUnformatted(Loc.TypeName(type));
            }
            else
            {
                // Noch nicht möglich in der aktuellen Zone (z.B. Sightseeing/Hunting Log ohne
                // freigeschaltetes Fliegen) - Eintrag bleibt in der Liste, nur ausgegraut, siehe
                // Plugin.IsTypeCurrentlyPossible.
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Loc.TypeName(type));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Plugin.GetTypeNotPossibleReason(type));
            }

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

    /// <summary>
    /// Mount-Auswahl für die Aetheryten-Automation: "Kein Mount" (aus, Standard), "Mount Roulette"
    /// (zufällige Auswahl unter den eigenen freigeschalteten Mounts, siehe
    /// Plugin.TryRequestAetheryteMount) oder ein konkretes Mount - ausgegraut, solange gar kein
    /// Mount freigeschaltet ist, da dann keine der Optionen etwas bewirken könnte. "Mount Roulette"
    /// wird erst ab zwei freigeschalteten Mounts angeboten (mit nur einem gäbe es nichts
    /// auszuwürfeln) - siehe Plugin.EnsureAetheryteMountAutoDefault für die dazu passende
    /// einmalige Vorbelegung.
    /// </summary>
    private void DrawAetheryteMountPicker(Configuration config)
    {
        plugin.EnsureAetheryteMountAutoDefault();

        var unlockedMounts = plugin.GetUnlockedMounts();
        var noMountsUnlocked = unlockedMounts.Count == 0;
        var rouletteAvailable = unlockedMounts.Count >= 2;

        var noneLabel = Loc.T("Kein Mount (zu Fuß)", "No mount (on foot)");
        var rouletteLabel = Loc.T("Mount Roulette", "Mount Roulette");
        var currentLabel = config.AetheryteMountId switch
        {
            null => noneLabel,
            0 when rouletteAvailable => rouletteLabel,
            0 => noneLabel,
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

            if (rouletteAvailable && ImGui.Selectable(rouletteLabel, config.AetheryteMountId == 0))
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

        ModernUi.GroupLabel(Loc.T("Allgemein", "General"));
        ModernUi.BeginCard();
        var showDebug = config.ShowDebugInfo;
        if (ModernUi.ToggleRow(Loc.T("Debug-Infos im Overlay anzeigen", "Show debug info in overlay"), ref showDebug))
        {
            config.ShowDebugInfo = showDebug;
            config.Save();
        }
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Simulation", "Simulation"));
        ModernUi.BeginCard();
        TextDisabledWrapped(Loc.T(
            "Lässt die Automation auch bereits freigeschaltete Ziele erneut anlaufen, zum Testen von Laufweg/Interaktion. Wirkt sich nur auf die Automation aus, nicht auf die normale Anzeige im Overlay.",
            "Makes the automation revisit already-unlocked targets too, for testing pathing/interaction. Only affects the automation, not the normal overlay display."));
        var simulateAetheryte = config.SimulateAetheryteAutomation;
        if (ModernUi.ToggleRow(Loc.T("Auto Aetheryte simulieren", "Simulate Auto Aetheryte"), ref simulateAetheryte))
        {
            config.SimulateAetheryteAutomation = simulateAetheryte;
            config.Save();
        }
        var simulateChocobokeep = config.SimulateChocobokeepAutomation;
        if (ModernUi.ToggleRow(Loc.T("Auto Chocobokeep simulieren", "Simulate Auto Chocobokeep"), ref simulateChocobokeep))
        {
            config.SimulateChocobokeepAutomation = simulateChocobokeep;
            config.Save();
        }
        var simulateSightseeing = config.SimulateSightseeingAutomation;
        if (ModernUi.ToggleRow(Loc.T("Auto Sightseeing simulieren", "Simulate Auto Sightseeing"), ref simulateSightseeing))
        {
            config.SimulateSightseeingAutomation = simulateSightseeing;
            config.Save();
        }
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Debug-Dumps (ins Log schreiben)", "Debug dumps (write to log)"));
        ModernUi.BeginCard();
        DrawWrappedButtonRow(new (string Label, Action OnClick)[]
        {
            (Loc.T("Aetheryten", "Aetherytes"), () => plugin.DumpAetheryteDebugInfo()),
            (Loc.T("Hunting Log", "Hunting log"), Plugin.DumpHuntingLogDebugInfo),
            (Loc.T("Sightseeing", "Sightseeing"), () => plugin.DumpSightseeingDebugInfo()),
            (Loc.T("Eigene Position", "My position"), () => plugin.DumpPlayerPositionDebugInfo()),
            (Loc.T("Framer's Kit", "Framer's kit"), Plugin.DumpFrameKitDebugInfo),
            (Loc.T("Moderne Ästhetik", "Modern Aesthetics"), Plugin.DumpHairstyleDebugInfo),
            (Loc.T("Chocobokeep", "Chocobokeep"), Plugin.DumpChocobokeepDebugInfo),
            (Loc.T("Händler-Positionen", "Vendor positions"), Plugin.DumpVendorPositionEnrichmentDebugInfo),
            (Loc.T("GK-Bardinghändler", "GC barding vendors"), Plugin.DumpGrandCompanyBardingVendorDebugInfo),
            (Loc.T("Dungeon-Zonen", "Dungeon zones"), Plugin.DumpZoneEnrichmentDebugInfo),
            (Loc.T("Saisonevent", "Seasonal event"), Plugin.DumpSeasonalEventDebugInfo),
            (Loc.T("Ätherströmungen (aktuelle Zone)", "Aether currents (current zone)"), Plugin.DumpAetherCurrentDebugInfo),
            (Loc.T("Ätherströmungen (alle Zonen)", "Aether currents (all zones)"), Plugin.DumpAetherCurrentDebugInfoAllZones),
        });
        ModernUi.EndCard();

        ModernUi.GroupLabel(Loc.T("Aktueller Status", "Current status"));
        ModernUi.BeginCard();
        var territoryId = Plugin.ClientState.TerritoryType;
        ImGui.TextUnformatted($"{Loc.T("Zone", "Zone")}: {Plugin.GetZoneName(territoryId)} ({territoryId})");

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position;
        var posText = playerPos.HasValue
            ? $"{playerPos.Value.X:F3}, {playerPos.Value.Y:F3}, {playerPos.Value.Z:F3}"
            : Loc.T("nicht verfügbar", "not available");
        ImGui.TextUnformatted($"{Loc.T("Eigene Weltposition", "Own world position")}: {posText}");

        if (playerPos.HasValue && ImGui.Button(Loc.T("In Zwischenablage kopieren", "Copy to clipboard") + "##CopyPlayerPos"))
        {
            ImGui.SetClipboardText($"{playerPos.Value.X.ToString(CultureInfo.InvariantCulture)}f, " +
                                    $"{playerPos.Value.Y.ToString(CultureInfo.InvariantCulture)}f, " +
                                    $"{playerPos.Value.Z.ToString(CultureInfo.InvariantCulture)}f");
        }
        ModernUi.EndCard();
    }

    /// <summary>
    /// Zeichnet eine Reihe gleichartiger Knöpfe, die bei Bedarf in weitere Zeilen umbrechen (statt
    /// wie vorher jeden einzeln mit eigenem Separator untereinander) - für die Debug-Dump-Knöpfe, die
    /// sonst eine sehr lange, unübersichtliche Liste ergäben.
    /// </summary>
    private static void DrawWrappedButtonRow((string Label, Action OnClick)[] buttons)
    {
        var windowVisibleX2 = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        for (var i = 0; i < buttons.Length; i++)
        {
            var (label, onClick) = buttons[i];
            if (ImGui.Button(label))
                onClick();

            if (i + 1 >= buttons.Length)
                continue;

            var lastButtonX2 = ImGui.GetItemRectMax().X;
            var nextButtonWidth = ImGui.CalcTextSize(buttons[i + 1].Label).X + ImGui.GetStyle().FramePadding.X * 2f;
            var nextButtonX2 = lastButtonX2 + ImGui.GetStyle().ItemSpacing.X + nextButtonWidth;
            if (nextButtonX2 < windowVisibleX2)
                ImGui.SameLine();
        }
    }

    // Group: Plugins derselben Gruppe sind gegeneinander austauschbar - "Required" ist dann schon
    // erfüllt, sobald EINES davon installiert ist (siehe IsDependencySatisfied), z.B. die beiden
    // Kampf-Plugins (siehe CombatPluginBridge).
    private const string CombatDependencyGroup = "Combat";

    private static readonly (string InternalName, string DisplayName, string DescriptionDe, string DescriptionEn, bool Required, string? Group)[] Dependencies =
    {
        (CombatPluginBridge.RotationSolverInternalName, "RotationSolver Reborn",
            "Kampf-Plugin: übernimmt den Kampf bei der Hunting-Log-Kill-Automation und bei kampfpflichtigen Schritten während der Quest-Automation. Alternativ zu Wrath Combo - eines der beiden wird benötigt.",
            "Combat plugin: drives combat for the hunting log kill automation and for combat-required steps during the quest automation. Alternative to Wrath Combo - one of the two is required.",
            true, CombatDependencyGroup),
        (CombatPluginBridge.WrathComboInternalName, "Wrath Combo",
            "Kampf-Plugin: übernimmt den Kampf bei der Hunting-Log-Kill-Automation und bei kampfpflichtigen Schritten während der Quest-Automation. Alternativ zu RotationSolver Reborn - eines der beiden wird benötigt.",
            "Combat plugin: drives combat for the hunting log kill automation and for combat-required steps during the quest automation. Alternative to RotationSolver Reborn - one of the two is required.",
            true, CombatDependencyGroup),
        ("vnavmesh", "vnavmesh",
            "Für das Laufen bei allen Automationen (Aetheryte, Quest, Hunting Log, \"Hinlaufen\").",
            "For pathfinding/walking in every automation (aetheryte, quest, hunting log, \"go to\").",
            true, null),
        ("Questionable", "Questionable",
            "Lässt die Quest-Automation Quests automatisch annehmen und abschließen.",
            "Drives the quest automation to accept and complete quests automatically.",
            true, null),
        ("Lifestream", "Lifestream",
            "Für Reisen zwischen Bezirken einer geteilten Hauptstadt während der Automation.",
            "For traveling between districts of a split capital city during automation.",
            true, null),
        ("TextAdvance", "TextAdvance",
            "Klickt automatisch durch Dialoge/Cutscenes während der Quest-Automation.",
            "Automatically clicks through dialogue/cutscenes during the quest automation.",
            true, null),
        ("InventoryTools", "Allagan Tools",
            "Aktiviert die Allagan-Tools-Integration für dieses Plugin.",
            "Enable Allagan Tools Integration for this Plugin.",
            false, null),
    };

    /// <summary>
    /// Ob mindestens ein als "Required" markiertes Plugin aktuell nicht installiert/geladen ist -
    /// wird für den Warn-Badge im Fenstertitel, den roten Punkt am Plugins-Icon in der Seitenleiste
    /// (siehe DrawCustomHeader/Draw) UND zum Ausgrauen sämtlicher Automations-Knöpfe im kompakten
    /// Overlay gebraucht (siehe CompactOverlayWindow) - bewusst pauschal für JEDES fehlende
    /// Required-Plugin, nicht nur das von der jeweiligen Automation tatsächlich genutzte, damit
    /// nicht pro Knopf einzeln nachvollzogen werden muss, welches Plugin wofür gebraucht wird.
    /// </summary>
    internal static bool HasMissingRequiredDependency() =>
        Dependencies.Any(d => d.Required && !IsDependencySatisfied(d.InternalName, d.Group));

    private static bool IsPluginLoaded(string internalName) =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);

    /// <summary>Installiert - oder (bei einer Gruppe, siehe CombatDependencyGroup) ein anderes Plugin derselben Gruppe.</summary>
    private static bool IsDependencySatisfied(string internalName, string? group) =>
        group == null
            ? IsPluginLoaded(internalName)
            : Dependencies.Any(d => d.Group == group && IsPluginLoaded(d.InternalName));

    // Nur die Typen, die als globale (zonenunabhängige) Liste über CollectionData.GetAllEntries
    // verfügbar sind - Quest/Aetheryte/HuntingLog/Sightseeing werden nur pro Zone live berechnet
    // und haben deshalb keine sinnvolle "Gesamt"-Zahl.
    private static readonly CollectibleType[] StatisticsTypes =
    {
        CollectibleType.Mount, CollectibleType.Minion, CollectibleType.Orchestrion, CollectibleType.Barding,
        CollectibleType.Emote, CollectibleType.Facewear, CollectibleType.FashionAccessory, CollectibleType.TripleTriadCard,
        CollectibleType.FrameKit, CollectibleType.AetherCurrent,
    };

    private static void DrawStatRow(string label, int owned, int total, float barHeight = 6f)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - ImGui.CalcTextSize($"{owned}/{total}").X);
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.TextUnformatted($"{owned}/{total}");
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, ModernUi.Accent);
        ImGui.ProgressBar(total == 0 ? 0f : owned / (float)total, new Vector2(-1f, barHeight), string.Empty);
        ImGui.PopStyleColor();
    }

    // Nur für die Blacklist-Seite (siehe DrawBlacklistPage) - Sitzungszustand, nicht gespeichert.
    private string blacklistSearch = string.Empty;
    private readonly HashSet<CollectibleType> blacklistHiddenTypes = new();

    /// <summary>
    /// Verwaltung der Blacklist (siehe Configuration.Blacklist/Plugin.IsBlacklisted): Einträge werden
    /// im Overlay per STRG + SHIFT + Klick hinzugefügt und lassen sich hier jederzeit wieder
    /// entfernen - mit Suchfeld und Typen-Filter (wie im Overlay).
    /// </summary>
    private void DrawBlacklistPage()
    {
        var config = plugin.Configuration;

        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted(Loc.T("Blacklist", "Blacklist"));
        ImGui.SetWindowFontScale(1f);
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.TextWrapped(Loc.T(
            "Mit STRG + SHIFT + Klick auf einen Eintrag im Overlay wird er hier eingetragen und komplett ausgeblendet - er erscheint nicht mehr im Overlay und wird von keiner Automation angelaufen.",
            "CTRL + SHIFT + click an entry in the overlay to add it here and hide it completely - it no longer shows up in the overlay and isn't targeted by any automation."));
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 10f));

        // Suchfeld + Typen-Filter in einer Zeile.
        var filterLabel = Loc.T("Typen filtern", "Filter types");
        var filterButtonWidth = ImGui.CalcTextSize(filterLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - filterButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##BlacklistSearch", Loc.T("Blacklist durchsuchen...", "Search blacklist..."), ref blacklistSearch, 100);
        ImGui.SameLine();
        if (ImGui.Button(filterLabel + "##BlacklistTypeFilter"))
            ImGui.OpenPopup("BlacklistTypeFilterPopup");

        if (ImGui.BeginPopup("BlacklistTypeFilterPopup"))
        {
            foreach (var type in config.TypeOrder)
            {
                var enabled = !blacklistHiddenTypes.Contains(type);
                if (ImGui.Checkbox($"{Loc.TypeName(type)}##BlacklistTypeFilterEntry", ref enabled))
                {
                    if (enabled)
                        blacklistHiddenTypes.Remove(type);
                    else
                        blacklistHiddenTypes.Add(type);
                }
            }

            ImGui.EndPopup();
        }

        var filtered = config.Blacklist
            .Where(b => !blacklistHiddenTypes.Contains(b.Type))
            .Where(b => string.IsNullOrWhiteSpace(blacklistSearch) || b.Name.Contains(blacklistSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => config.TypeOrder.IndexOf(b.Type))
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.TextUnformatted(Loc.T($"{filtered.Count} von {config.Blacklist.Count} Einträgen", $"{filtered.Count} of {config.Blacklist.Count} entries"));
        ImGui.PopStyleColor();
        ImGui.Spacing();

        if (config.Blacklist.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
            ImGui.TextWrapped(Loc.T("Die Blacklist ist leer.", "The blacklist is empty."));
            ImGui.PopStyleColor();
            return;
        }

        BlacklistedEntry? toRemove = null;
        ImGui.BeginChild("##BlacklistList", new Vector2(0f, 0f), false);
        if (ImGui.BeginTable("##BlacklistTable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
        {
            // Aktions-Spalte etwas breiter als der Knopf selbst - bei exakt Knopfbreite schob das
            // Zellen-Padding ihn über den Rand, wodurch er rechts abgeschnitten aussah.
            var removeButtonSize = new Vector2(ImGui.GetFrameHeight() + 6f, ImGui.GetFrameHeight());
            ImGui.TableSetupColumn(Loc.T("Typ", "Type"), ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn(Loc.T("Name", "Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, removeButtonSize.X + ImGui.GetStyle().CellPadding.X * 2f);
            // Eigene, dezente Kopfzeile statt ImGui.TableHeadersRow (dessen farbiger Balken passte nicht
            // zum restlichen Design): kleine Großbuchstaben in TextMuted, darunter eine feine Linie.
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, 0u);
            foreach (var (column, header) in new[] { (0, Loc.T("TYP", "TYPE")), (1, Loc.T("NAME", "NAME")) })
            {
                ImGui.TableSetColumnIndex(column);
                ImGui.Dummy(new Vector2(0f, 2f));
                ImGui.SetWindowFontScale(0.85f);
                ImGui.TextColored(ModernUi.TextMuted, header);
                ImGui.SetWindowFontScale(1f);
            }

            // Linie über die volle Tabellenbreite direkt unter der Kopfzeile.
            ImGui.TableSetColumnIndex(2);
            var headerBottomY = ImGui.GetItemRectMax().Y + 4f;
            var tableMinX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X;
            var tableMaxX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            // Eigenes Clip-Rechteck (ohne Schnitt mit dem aktuellen) - sonst würde die Linie auf die
            // gerade aktive Tabellenspalte beschnitten und nur ganz rechts sichtbar.
            var headerDrawList = ImGui.GetWindowDrawList();
            headerDrawList.PushClipRect(new Vector2(tableMinX, headerBottomY - 1f), new Vector2(tableMaxX, headerBottomY + 1f), false);
            headerDrawList.AddLine(new Vector2(tableMinX, headerBottomY), new Vector2(tableMaxX, headerBottomY), ImGui.GetColorU32(ImGuiCol.Separator));
            headerDrawList.PopClipRect();
            ImGui.Dummy(new Vector2(0f, 6f));

            foreach (var item in filtered)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(CompactOverlayWindow.TypeColors.GetValueOrDefault(item.Type, ModernUi.TextMuted), Loc.TypeName(item.Type));

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(item.Name);

                ImGui.TableNextColumn();
                // Dezenter Knopf: ohne Hintergrund, erst beim Überfahren rot hinterlegt - Icon per
                // IconTextButton exakt mittig (ohne Text kein Abstand, siehe dort).
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.3f, 0.35f, 0.45f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.3f, 0.35f, 0.7f));
                ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
                var clicked = IconTextButton($"BlacklistRemove{item.Type}{item.Id}", FontAwesomeIcon.TrashAlt, string.Empty, removeButtonSize);
                ImGui.PopStyleColor(4);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.T("Von der Blacklist entfernen (wieder anzeigen)", "Remove from the blacklist (show again)"));
                if (clicked)
                    toRemove = item;
            }

            ImGui.EndTable();
        }
        ImGui.EndChild();

        // Erst nach der Schleife entfernen - nicht während über dieselbe Liste iteriert wird.
        if (toRemove != null)
            Plugin.RemoveFromBlacklist(toRemove.Type, toRemove.Id);
    }

    private void DrawStatisticsPage()
    {
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted(Loc.T("Statistik", "Statistics"));
        ImGui.SetWindowFontScale(1f);
        ImGui.PushStyleColor(ImGuiCol.Text, ModernUi.TextMuted);
        ImGui.TextWrapped(Loc.T(
            "Enthält keine Erfolge (Achievements) o.ä., sondern nur die Kategorien, die dieses Plugin selbst verfolgt (siehe Overlay).",
            "Doesn't include achievements etc. - only the categories this plugin itself tracks (see the overlay)."));
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 10f));

        // Nur zonengebundene Einträge (TerritoryTypeId != 0) - Einträge ohne Zone tauchen im
        // kompakten Overlay nie auf (siehe HasGoToTarget/siblingTerritories-Filter dort), zählen
        // hier also absichtlich nicht mit, sonst würde die Statistik Dinge "mitrechnen", die das
        // Plugin selbst gar nirgends anzeigt.
        // Je Sammelobjekt nur einmal zählen - dasselbe Objekt kann mehrfach in der Liste stehen (z.B.
        // bei mehreren Händlern oder den drei Itinerant Moogles, siehe Plugin.GetItinerantMoogleEntries).
        var entries = CollectionData.GetAllEntries()
            .Where(e => e.TerritoryTypeId != 0)
            .GroupBy(e => (e.Type, e.Id))
            .Select(g => g.First())
            .ToList();
        var totalCount = 0;
        var totalOwned = 0;

        foreach (var type in StatisticsTypes)
        {
            var typeEntries = entries.Where(e => e.Type == type).ToList();
            if (typeEntries.Count == 0)
                continue;

            var owned = typeEntries.Count(plugin.IsOwned);
            totalCount += typeEntries.Count;
            totalOwned += owned;

            DrawStatRow(Loc.TypeName(type), owned, typeEntries.Count);
            ImGui.Spacing();
        }

        // Quests laufen separat (siehe Plugin.GetAllTrackedQuestIds) - anders als die Typen oben
        // gibt es dafür keine feste JSON-Liste, sondern eine live aus dem kompletten Lumina-Quest-
        // Sheet berechnete, zonenunabhängige Annehmbarkeits-Prüfung.
        var questIds = plugin.GetAllTrackedQuestIds();
        if (questIds.Count > 0)
        {
            var questsOwned = questIds.Count(id => plugin.IsOwned(new CollectibleEntry { Id = id, Type = CollectibleType.Quest }));
            totalCount += questIds.Count;
            totalOwned += questsOwned;

            DrawStatRow(Loc.TypeName(CollectibleType.Quest), questsOwned, questIds.Count);
            ImGui.Spacing();
        }

        ImGui.Dummy(new Vector2(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 10f));

        ImGui.SetWindowFontScale(1.1f);
        DrawStatRow(Loc.T("Insgesamt", "Total"), totalOwned, totalCount, 8f);
        ImGui.SetWindowFontScale(1f);
    }

    private static void DrawDependenciesPage()
    {
        var installed = Dependencies.Select(d => IsPluginLoaded(d.InternalName)).ToArray();

        // Eine Gruppe (siehe CombatDependencyGroup) zählt als EIN fehlendes Plugin, und nur, wenn
        // keines ihrer Plugins installiert ist.
        var missingRequired = Dependencies
            .Where(d => d.Required && !IsDependencySatisfied(d.InternalName, d.Group))
            .Select(d => d.Group ?? d.InternalName)
            .Distinct()
            .Count();

        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted(Loc.T("Plugins", "Plugins"));
        ImGui.SetWindowFontScale(1f);

        if (missingRequired > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.35f, 0.4f, 1f));
            ImGui.TextUnformatted(missingRequired == 1
                ? Loc.T("1 benötigtes Plugin fehlt.", "1 required plugin is missing.")
                : Loc.T($"{missingRequired} benötigte Plugins fehlen.", $"{missingRequired} required plugins are missing."));
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 10f));

        var drawnGroups = new HashSet<string>();
        for (var i = 0; i < Dependencies.Length; i++)
        {
            var dep = Dependencies[i];
            if (dep.Group == null)
            {
                DrawDependencyCard(dep.InternalName, dep.DisplayName, Loc.T(dep.DescriptionDe, dep.DescriptionEn), dep.Required, installed[i]);
                ImGui.Spacing();
                continue;
            }

            // Eine Gruppe austauschbarer Plugins (siehe CombatDependencyGroup) als EINE Zeile:
            // Überschrift mit "EINES BENÖTIGT"-Badge, darunter alle Plugins der Gruppe als gleich
            // große, einzeilige Karten nebeneinander - beim ersten Plugin der Gruppe komplett gezeichnet, die
            // übrigen werden danach übersprungen.
            if (!drawnGroups.Add(dep.Group))
                continue;

            var groupIndices = Enumerable.Range(0, Dependencies.Length).Where(j => Dependencies[j].Group == dep.Group).ToList();
            var groupSatisfied = groupIndices.Any(j => installed[j]);

            ImGui.Indent(ModernUi.CardMargin);
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, groupSatisfied ? ModernUi.TextMuted : new Vector4(0.95f, 0.35f, 0.4f, 1f));
            ImGui.TextUnformatted(Loc.T("Kampf-Plugin", "Combat plugin"));
            ImGui.PopStyleColor();
            ImGui.SameLine();
            DrawBadge(Loc.T("EINES BENÖTIGT", "ONE REQUIRED"), true);
            ImGui.Unindent(ModernUi.CardMargin);

            // Tabelle nur für die Aufteilung in gleich breite Spalten - Zellabstand 0, den Abstand
            // zwischen den Karten ergibt sich aus tileOuterWidth/DependencyTileGap.
            var rowWidth = ImGui.GetContentRegionAvail().X;
            var tileOuterWidth = (rowWidth - DependencyTileGap * (groupIndices.Count - 1)) / groupIndices.Count;
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(0f, 0f));
            if (ImGui.BeginTable($"##DependencyGroup{dep.Group}", groupIndices.Count, ImGuiTableFlags.SizingStretchSame))
            {
                for (var column = 0; column < groupIndices.Count; column++)
                {
                    ImGui.TableNextColumn();

                    // EndCard zeichnet den Kartenhintergrund CardVerticalPadding über dem Inhalt - in einer
                    // Tabellenzelle würde dieser Streifen sonst an der Zelloberkante abgeschnitten.
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ModernUi.CardVerticalPadding);
                    var j = groupIndices[column];
                    var member = Dependencies[j];
                    var satisfiedByOther = !installed[j] && groupSatisfied;

                    // Gleich breite Karten mit festem Abstand dazwischen, unabhängig davon, wo die
                    // Tabellenspalte selbst beginnt (Spalten sind rowWidth/n breit, die Karten etwas schmaler).
                    var offsetX = column * (tileOuterWidth + DependencyTileGap) - column * rowWidth / groupIndices.Count;
                    // Per Indent statt SetCursorPosX - BeginCard rückt selbst per Indent ein, was eine
                    // vorher gesetzte Cursor-X-Position wieder verwerfen würde.
                    if (offsetX != 0f)
                        ImGui.Indent(offsetX);

                    DrawDependencyCard(member.InternalName, member.DisplayName, Loc.T(member.DescriptionDe, member.DescriptionEn), member.Required, installed[j],
                        tileOuterWidth, showBadge: false, satisfiedByOther);

                    if (offsetX != 0f)
                        ImGui.Unindent(offsetX);
                }

                ImGui.EndTable();
            }
            ImGui.PopStyleVar();
            ImGui.Spacing();
        }
    }

    // Horizontaler Abstand zwischen zwei Karten derselben Zeile (siehe DrawDependenciesPage).
    private const float DependencyTileGap = 12f;

    /// <summary>
    /// Eine einzelne Abhängigkeit als abgerundete, einzeilige Karte: kreisförmiges Status-Icon links,
    /// Name + "BENÖTIGT"/"OPTIONAL"-Badge in der Mitte (Beschreibung als "?"-Tooltip wie in den
    /// Einstellungen), Installiert-Haken bzw. "Installieren"-Knopf rechtsbündig. Alle Karten sind
    /// dadurch exakt gleich hoch - auch die schmaleren, nebeneinander stehenden Karten einer Gruppe
    /// (siehe CombatDependencyGroup/DrawDependenciesPage).
    /// </summary>
    /// <param name="outerWidth">Feste Außenbreite (für mehrere Karten in einer Zeile), sonst die volle verfügbare Breite.</param>
    /// <param name="showBadge">false für Gruppen-Karten - dort steht das Badge bereits an der Gruppenüberschrift.</param>
    /// <param name="satisfiedByOther">Nicht installiert, aber ein anderes Plugin der Gruppe ist es - dann neutral statt rot.</param>
    private static void DrawDependencyCard(string internalName, string displayName, string description, bool required, bool isInstalled,
        float? outerWidth = null, bool showBadge = true, bool satisfiedByOther = false)
    {
        ModernUi.BeginCard();

        const float iconDiameter = 36f;
        const float rowHeight = iconDiameter;
        var rowStart = ImGui.GetCursorScreenPos();
        // CardMargin abziehen, genau wie bei LabelRow/ToggleRow: EndCard() legt außen noch einmal
        // denselben Rand um den Karteninhalt, ohne den Abzug würde die Karte um CardMargin breiter
        // werden als der restliche Inhalt (z.B. die Trennlinie über den Karten). Bei fester
        // Außenbreite beide Ränder (links ist hier bereits per BeginCard eingerückt).
        var availWidth = outerWidth.HasValue
            ? outerWidth.Value - ModernUi.CardMargin * 2f
            : ImGui.GetContentRegionAvail().X - ModernUi.CardMargin;
        var drawList = ImGui.GetWindowDrawList();

        var iconColor = isInstalled
            ? new Vector4(0.3f, 0.75f, 0.45f, 1f)
            : satisfiedByOther
                ? new Vector4(0.45f, 0.45f, 0.5f, 1f)
                : new Vector4(0.85f, 0.3f, 0.35f, 1f);
        var iconCenter = rowStart + new Vector2(iconDiameter * 0.5f, iconDiameter * 0.5f);
        drawList.AddCircleFilled(iconCenter, iconDiameter * 0.5f, ImGui.ColorConvertFloat4ToU32(iconColor), 24);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var glyph = (isInstalled ? FontAwesomeIcon.Check : FontAwesomeIcon.Times).ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(iconCenter - glyphSize * 0.5f, ImGui.ColorConvertFloat4ToU32(Vector4.One), glyph);
        }

        var badgeText = required ? Loc.T("BENÖTIGT", "REQUIRED") : Loc.T("OPTIONAL", "OPTIONAL");
        var badgeWidth = showBadge ? ImGui.CalcTextSize(badgeText).X + BadgePaddingX * 2f + ImGui.GetStyle().ItemSpacing.X : 0f;
        var nameWidth = ImGui.CalcTextSize(displayName).X;

        // Rechtsbündiger Status/Knopf - Größe zuerst berechnen, mit demselben Schriftkontext wie
        // beim tatsächlichen Zeichnen weiter unten (Haken-Icon unter IconFontHandle, der restliche
        // Text/Knopf in der Standardschrift), damit die Ausrichtung exakt an den rechten Rand passt.
        // Reicht die Breite (schmale Gruppen-Karten bei kleinem Fenster) nicht für den vollen Text,
        // nur das Icon zeigen - die Karte bleibt so trotzdem einzeilig und gleich hoch.
        var installedLabel = Loc.T("Installiert", "Installed");
        var installLabel = Loc.T("Installieren", "Install");
        var nameStartOffset = iconDiameter + 12f;
        var helpIconReserve = 26f;

        float MeasureStatus(bool compact)
        {
            if (isInstalled)
            {
                float checkIconWidth;
                using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                    checkIconWidth = ImGui.CalcTextSize(FontAwesomeIcon.Check.ToIconString()).X;
                return compact ? checkIconWidth : checkIconWidth + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(installedLabel).X;
            }

            return MeasureIconTextButtonSize(FontAwesomeIcon.Download, compact ? string.Empty : installLabel, ImGui.GetStyle().FramePadding).X;
        }

        var compactStatus = nameStartOffset + nameWidth + badgeWidth + helpIconReserve + MeasureStatus(false) > availWidth;
        var statusWidth = MeasureStatus(compactStatus);
        var statusHeight = isInstalled ? ImGui.GetTextLineHeight() : ImGui.GetFrameHeight();

        // Name + Badge in einer Zeile, vertikal mittig zum Icon-Kreis. Die Beschreibung steht - wie bei
        // den Einstellungen - nur im "?"-Tooltip dahinter (siehe ModernUi.HelpIconIfHovered).
        var badgeHeight = ImGui.GetTextLineHeight() + BadgePaddingY * 2f;
        var nameRowY = rowStart.Y + (iconDiameter - badgeHeight) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + nameStartOffset, nameRowY + BadgePaddingY));
        ImGui.TextUnformatted(displayName);
        var nameTopY = ImGui.GetItemRectMin().Y;
        var labelEnd = ImGui.GetItemRectMax();
        if (showBadge)
        {
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, nameRowY));
            DrawBadge(badgeText, required);
            labelEnd = ImGui.GetItemRectMax();
        }

        ModernUi.HelpIconIfHovered(rowStart, new Vector2(availWidth, rowHeight), labelEnd, nameTopY, description);

        var statusY = rowStart.Y + (rowHeight - statusHeight) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + availWidth - statusWidth, statusY));
        if (isInstalled)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), FontAwesomeIcon.Check.ToIconString());
            if (!compactStatus)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(installedLabel);
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(installedLabel);
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.9f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.32f, 0.53f, 0.98f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.2f, 0.38f, 0.8f, 1f));
            var installButtonSize = new Vector2(statusWidth, ImGui.GetFrameHeight());
            if (IconTextButton($"install_{internalName}", FontAwesomeIcon.Download, compactStatus ? string.Empty : installLabel, installButtonSize))
                Plugin.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins, displayName);
            ImGui.PopStyleColor(3);
            if (compactStatus && ImGui.IsItemHovered())
                ImGui.SetTooltip(installLabel);
        }

        // Unsichtbarer Punkt ganz rechts, damit die Karte IMMER exakt bis availWidth reicht -
        // ohne das würde die Kartenbreite vom tatsächlich gerenderten Inhalt abhängen (Installiert-
        // Text vs. Installieren-Knopf sind unterschiedlich breit), wodurch die Karten je nach
        // Installationsstatus unterschiedlich breit wirkten. Zugleich volle Zeilenhöhe (der
        // Icon-Kreis ist nur auf die Draw-List gezeichnet und zählt sonst nicht zur Kartengröße).
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + availWidth, rowStart.Y));
        ImGui.Dummy(new Vector2(0f, rowHeight));

        ModernUi.EndCard();
    }

    // Vertikales Innenpolster von DrawBadge - auch für das vertikale Ausrichten daneben stehenden Texts (siehe DrawDependencyCard).
    private const float BadgePaddingY = 3f;
    private const float BadgePaddingX = 8f;

    /// <summary>Kleine abgerundete Pille mit Rahmen für "BENÖTIGT"/"OPTIONAL" neben einem Namen.</summary>
    private static void DrawBadge(string text, bool emphasized)
    {
        var textSize = ImGui.CalcTextSize(text);
        var padding = new Vector2(BadgePaddingX, BadgePaddingY);
        var size = textSize + padding * 2f;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var bg = emphasized ? new Vector4(0.25f, 0.4f, 0.85f, 0.35f) : new Vector4(1f, 1f, 1f, 0.08f);
        var border = emphasized ? new Vector4(0.4f, 0.55f, 0.95f, 0.9f) : new Vector4(1f, 1f, 1f, 0.25f);
        var textColor = emphasized ? new Vector4(0.7f, 0.8f, 1f, 1f) : ModernUi.TextMuted;

        drawList.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(bg), size.Y * 0.5f);
        drawList.AddRect(pos, pos + size, ImGui.ColorConvertFloat4ToU32(border), size.Y * 0.5f);
        drawList.AddText(pos + padding, ImGui.ColorConvertFloat4ToU32(textColor), text);

        ImGui.Dummy(size);
    }

    // Vertikales Innenpolster der Punkt-Pille (siehe DrawDotBadge) - eigene Konstante, damit der
    // Aufrufer (Fenstertitel) dieselbe Höhe schon VOR dem Zeichnen kennt, um die Pille korrekt
    // vertikal zu zentrieren.
    private const float DotBadgePaddingY = 3f;

    /// <summary>
    /// Abgerundete Pille mit farbigem Punkt + Text davor, z.B. "Plugin needed" neben dem Fenster-
    /// titel, wenn ein benötigtes Plugin fehlt (siehe HasMissingRequiredDependency).
    /// </summary>
    private const float DotBadgeDotDiameter = 6f;
    private const float DotBadgeDotToTextGap = 6f;
    private static readonly Vector2 DotBadgePadding = new(10f, DotBadgePaddingY);

    /// <summary>Größe, die DrawDotBadge für den gegebenen Text zeichnen wird - zum Zentrieren VOR dem Zeichnen.</summary>
    private static Vector2 MeasureDotBadgeSize(string text)
    {
        var textSize = ImGui.CalcTextSize(text);
        return new Vector2(DotBadgeDotDiameter + DotBadgeDotToTextGap + textSize.X + DotBadgePadding.X * 2f, textSize.Y + DotBadgePadding.Y * 2f);
    }

    private static void DrawDotBadge(string text, Vector4 dotColor, Vector4 bgColor, Vector4 borderColor, Vector4 textColor)
    {
        const float dotDiameter = DotBadgeDotDiameter;
        const float dotToTextGap = DotBadgeDotToTextGap;
        var padding = DotBadgePadding;
        var textSize = ImGui.CalcTextSize(text);
        var size = MeasureDotBadgeSize(text);
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(bgColor), size.Y * 0.5f);
        drawList.AddRect(pos, pos + size, ImGui.ColorConvertFloat4ToU32(borderColor), size.Y * 0.5f);

        var dotCenter = pos + new Vector2(padding.X + dotDiameter * 0.5f, size.Y * 0.5f);
        drawList.AddCircleFilled(dotCenter, dotDiameter * 0.5f, ImGui.ColorConvertFloat4ToU32(dotColor), 12);

        var textPos = pos + new Vector2(padding.X + dotDiameter + dotToTextGap, padding.Y);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(textColor), text);

        ImGui.Dummy(size);
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

}
