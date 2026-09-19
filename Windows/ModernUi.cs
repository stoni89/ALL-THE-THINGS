using System.Numerics;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;

namespace AllTheThings.Windows;

/// <summary>
/// Kleines Set wiederverwendbarer ImGui-Bausteine für einen moderneren Look des Optionsfensters
/// (dunkler Verlaufshintergrund, abgerundete Karten, Toggle-Switches, Icon-Sidebar) - ImGui bietet
/// dafür von sich aus nichts, alles hier wird manuell per Draw-List gezeichnet. Bewusst als
/// eigenständige, zustandslose Helfer (keine Abhängigkeit auf Plugin/Configuration), damit sie sich
/// auch in anderen Fenstern wiederverwenden lassen.
/// </summary>
public static class ModernUi
{
    public static readonly Vector4 CardBg = new(0.12f, 0.14f, 0.20f, 0.92f);
    public static readonly Vector4 CardBorder = new(1f, 1f, 1f, 0.06f);
    public static readonly Vector4 Accent = new(0.32f, 0.56f, 0.95f, 1f);
    public static readonly Vector4 AccentHover = new(0.40f, 0.63f, 0.98f, 1f);
    public static readonly Vector4 TextMuted = new(0.58f, 0.61f, 0.70f, 1f);
    public static readonly Vector4 ToggleOff = new(0.24f, 0.26f, 0.34f, 1f);
    public static readonly Vector4 ToggleOffHover = new(0.30f, 0.32f, 0.41f, 1f);
    public static readonly Vector4 SidebarHover = new(1f, 1f, 1f, 0.06f);
    public static readonly Vector4 SidebarSelected = new(0.32f, 0.56f, 0.95f, 0.16f);

    // Card-Innenabstand links (per ImGui.Indent in BeginCard) UND rechts - rechts gibt es dafür
    // keine ImGui-Bordfunktion, daher müssen alle rechtsbündigen Helfer hier (LabelRow, ToggleRow,
    // TextDisabledWrapped) ihre verfügbare Breite explizit um diesen Wert verkleinern, sonst reicht
    // ihr Inhalt bis an den echten Fensterrand, während links durch Indent() schon 14px Abstand ist.
    public const float CardMargin = 14f;

    /// <summary>
    /// Setzt globale Stil-Werte (Rundungen, Abstände, Grundfarben) für einen moderneren Look - muss
    /// VOR ImGui.Begin() aufgerufen werden (also in PreDraw(), nicht in Draw()!), sonst greift der
    /// Innenabstand (WindowPadding) nicht für das äußere Fenster selbst, da dessen Content-Bereich
    /// schon beim Begin()-Aufruf mit dem bis dahin aktiven Wert berechnet wird. Muss von der
    /// Aufrufstelle immer mit PopStyle() beendet werden (z.B. in PostDraw()).
    /// </summary>
    public static void PushStyle(Vector2? windowPadding = null)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, windowPadding ?? new Vector2(12f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.055f, 0.063f, 0.098f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.93f, 0.94f, 0.97f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.16f, 0.18f, 0.25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.20f, 0.22f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.25f, 0.33f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.22f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.26f, 0.29f, 0.38f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.33f, 0.43f, 1f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.10f, 0.12f, 0.17f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 0.05f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(1f, 1f, 1f, 0.08f));
    }

    public static void PopStyle()
    {
        ImGui.PopStyleColor(13);
        ImGui.PopStyleVar(7);
    }

    /// <summary>
    /// Einzelner, quadratischer Icon-Button für die schmale äußere Navigationsleiste (wie im
    /// Referenzdesign links außen) - horizontal zentriert in der verfügbaren Breite.
    /// </summary>
    public static bool RailButton(FontAwesomeIcon icon, bool selected, string? tooltip = null)
    {
        const float size = 34f;
        var avail = ImGui.GetContentRegionAvail().X;
        var offsetX = (avail - size) * 0.5f;
        if (offsetX > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

        ImGui.PushStyleColor(ImGuiCol.Button, selected ? Accent : new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, selected ? AccentHover : SidebarHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentHover);

        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            clicked = ImGui.Button($"{icon.ToIconString()}##rail_{icon}", new Vector2(size, size));

        ImGui.PopStyleColor(3);

        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    /// <summary>
    /// Startet eine abgerundete "Karte" um die nachfolgend gezeichneten Widgets - der Hintergrund
    /// wird per Draw-List-Channel HINTER den Inhalt gezeichnet (Standard-ImGui-Trick, da die
    /// Draw-List sonst nur in Zeichenreihenfolge - und damit immer OBEN - zeichnen könnte). Jeder
    /// Aufruf muss mit EndCard() beendet werden.
    /// </summary>
    public static void BeginCard()
    {
        ImGui.Indent(CardMargin);
        ImGui.BeginGroup();
        ImGui.GetWindowDrawList().ChannelsSplit(2);
        ImGui.GetWindowDrawList().ChannelsSetCurrent(1);
        ImGui.Dummy(new Vector2(0f, 2f));
    }

    public static void EndCard(float padding = CardMargin)
    {
        ImGui.Dummy(new Vector2(0f, 2f));
        ImGui.EndGroup();

        var min = ImGui.GetItemRectMin() - new Vector2(padding, padding);
        var max = ImGui.GetItemRectMax() + new Vector2(padding, padding);

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(CardBg), 12f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(CardBorder), 12f);
        drawList.ChannelsMerge();

        ImGui.Unindent(CardMargin);
        ImGui.Dummy(new Vector2(0f, padding));
    }

    /// <summary>
    /// Kleine Zwischenüberschrift direkt über einer Karte (z.B. "Match intro" im Referenzdesign) -
    /// um CardMargin eingerückt, damit sie mit dem eingerückten Karteninhalt darunter fluchtet statt
    /// mit dem (weiter links liegenden) Kartenrand.
    /// </summary>
    public static void GroupLabel(string text)
    {
        ImGui.Indent(CardMargin);
        ImGui.TextUnformatted(text);
        ImGui.Unindent(CardMargin);
        ImGui.Spacing();
    }

    /// <summary>
    /// Größere, fette Überschrift + gedämpfter Untertext darunter - für den Titel oben in jedem
    /// Tab-Inhalt (entspricht "In match" / "The social touches..." im Referenzdesign).
    /// </summary>
    public static void SectionHeader(string title, string? subtitle = null)
    {
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted(title);
        ImGui.SetWindowFontScale(1f);

        if (!string.IsNullOrEmpty(subtitle))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
            ImGui.TextWrapped(subtitle);
            ImGui.PopStyleColor();
        }

        ImGui.Dummy(new Vector2(0f, 6f));
    }

    /// <summary>
    /// Positioniert den Cursor für das nächste Widget rechtsbündig am Rand des verfügbaren
    /// Inhaltsbereichs (Karte/Fenster), NACHDEM label links geschrieben wurde - für Zeilen im
    /// Stil "Beschriftung ..................... Regler" wie im Referenzdesign. Ruft selbst kein
    /// Widget auf - direkt danach z.B. ImGui.SliderFloat mit SetNextItemWidth(controlWidth) davor.
    /// </summary>
    public static void LabelRow(string label, float controlWidth)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        var avail = ImGui.GetContentRegionAvail().X - CardMargin;
        if (avail > controlWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - controlWidth);
        ImGui.SetNextItemWidth(controlWidth);
    }

    /// <summary>
    /// Zeile "Beschriftung ..................... Toggle" - Kombination aus LabelRow und
    /// ToggleSwitch für den häufigsten Fall (ein Bool-Setting pro Zeile).
    /// </summary>
    public static bool ToggleRow(string label, ref bool value)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        var toggleWidth = ImGui.GetFrameHeight() * 0.8f * 1.8f;
        var avail = ImGui.GetContentRegionAvail().X - CardMargin;
        if (avail > toggleWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - toggleWidth);
        return ToggleSwitch($"##toggle_{label}", ref value);
    }

    /// <summary>
    /// Ein einzelner "An/Aus"-Schalter statt einer eckigen Checkbox - visuell wie in modernen
    /// Settings-UIs üblich (siehe Referenzbild). Verhält sich wie ImGui.Checkbox: gibt true zurück,
    /// wenn der Wert sich durch einen Klick geändert hat, und schreibt den neuen Wert in value.
    /// </summary>
    public static bool ToggleSwitch(string id, ref bool value)
    {
        var height = ImGui.GetFrameHeight() * 0.8f;
        var width = height * 1.8f;
        var pos = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, new Vector2(width, height));
        var changed = false;
        if (ImGui.IsItemClicked())
        {
            value = !value;
            changed = true;
        }

        var hovered = ImGui.IsItemHovered();
        var trackColor = value ? (hovered ? AccentHover : Accent) : (hovered ? ToggleOffHover : ToggleOff);
        var radius = height * 0.5f;

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(trackColor), radius);

        var knobRadius = radius - 2.5f;
        var knobX = value ? pos.X + width - radius : pos.X + radius;
        drawList.AddCircleFilled(new Vector2(knobX, pos.Y + radius), knobRadius, ImGui.ColorConvertFloat4ToU32(Vector4.One), 32);

        return changed;
    }

    /// <summary>
    /// Eine Zeile in der linken Icon-Sidebar (Nav-Eintrag) - abgerundete Hervorhebung + blauer
    /// Akzentstrich links, wenn ausgewählt, sonst nur dezentes Hover. Gibt true zurück, wenn
    /// angeklickt.
    /// </summary>
    public static bool SidebarItem(FontAwesomeIcon icon, string label, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var height = 38f;
        var startPos = ImGui.GetCursorScreenPos();

        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, SidebarHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, SidebarSelected);
        ImGui.PushStyleColor(ImGuiCol.Header, selected ? SidebarSelected : new Vector4(0f, 0f, 0f, 0f));
        var clicked = ImGui.Selectable($"##sidebar_{label}", selected, ImGuiSelectableFlags.None, new Vector2(width, height));
        ImGui.PopStyleColor(3);

        var drawList = ImGui.GetWindowDrawList();
        if (selected)
        {
            drawList.AddRectFilled(
                startPos,
                startPos + new Vector2(3f, height),
                ImGui.ColorConvertFloat4ToU32(Accent),
                2f);
        }

        var textColor = selected ? Vector4.One : TextMuted;
        var iconPos = startPos + new Vector2(16f, height * 0.5f - ImGui.GetTextLineHeight() * 0.5f);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            drawList.AddText(iconPos, ImGui.ColorConvertFloat4ToU32(textColor), icon.ToIconString());

        var labelPos = startPos + new Vector2(42f, height * 0.5f - ImGui.GetTextLineHeight() * 0.5f);
        drawList.AddText(labelPos, ImGui.ColorConvertFloat4ToU32(textColor), label);

        return clicked;
    }
}
