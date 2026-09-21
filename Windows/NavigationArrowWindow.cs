using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace TheExplorersCodex.Windows;

/// <summary>
/// TomTom-artiger Wegweiser-Pfeil (World of Warcraft) - zeigt relativ zur Blickrichtung des
/// Charakters, wo das aktuelle Navigationsziel liegt (siehe Plugin.NavigationTargetPosition, gesetzt
/// beim Start einer Automation, per "Hinlaufen"-Icon oder beim Öffnen eines Karten-Links - siehe
/// Plugin.OpenVendorMap/OpenEntryMap). Verschwindet automatisch beim Ankommen (ArrivalDistance) oder
/// per Rechtsklick, frei verschiebbar wie jedes andere ImGui-Fenster.
/// </summary>
public class NavigationArrowWindow : Window
{
    private readonly Plugin plugin;

    private const float ArrivalDistance = 3f;

    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoBringToFrontOnFocus;

    public NavigationArrowWindow(Plugin plugin) : base("##TheExplorersCodexNavigationArrow", BaseFlags)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    // Fenster selbst komplett unsichtbar (weder Hintergrund noch Umriss, nur Pfeil+Text sind per
    // Draw-List gezeichnet) - bleibt trotzdem per Ziehen im Client-Bereich verschiebbar, da kein
    // NoMove gesetzt ist.
    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        // Breite/Höhe kommen aus den Optionen (General-Tab, Gruppe "QoL") - jeden Frame erzwungen,
        // damit ein Ändern des Reglers sich sofort auswirkt, nicht erst beim nächsten Öffnen.
        Size = new Vector2(plugin.Configuration.NavigationArrowWidth, plugin.Configuration.NavigationArrowHeight);
        SizeCondition = ImGuiCond.Always;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
    }

    public override bool DrawConditions() =>
        plugin.Configuration.ShowNavigationArrow
        && Plugin.NavigationTargetPosition.HasValue
        && Plugin.ClientState.IsLoggedIn
        && !Plugin.Condition[ConditionFlag.BetweenAreas]
        && !Plugin.Condition[ConditionFlag.BetweenAreas51]
        && Plugin.ClientState.TerritoryType == Plugin.NavigationTargetTerritoryId;

    public override void Draw()
    {
        var target = Plugin.NavigationTargetPosition;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (target == null || player == null)
            return;

        var playerPos = player.Position;
        var distance = Vector2.Distance(new Vector2(playerPos.X, playerPos.Z), new Vector2(target.Value.X, target.Value.Z));
        if (distance <= ArrivalDistance)
        {
            Plugin.ClearNavigationTarget();
            return;
        }

        // Rechtsklick irgendwo im Fenster - Pfeil verwerfen (wie in "TomTom"), ohne die Option im
        // General-Tab dauerhaft abzuschalten.
        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            Plugin.ClearNavigationTarget();
            return;
        }

        // Winkel vom Charakter zum Ziel relativ zur eigenen Blickrichtung (player.Rotation, Radiant) -
        // Atan2(dx, dz) statt des sonst üblichen Atan2(dz, dx), da FFXIVs Rotation=0 nach Norden
        // (also -Z-Richtung mit X als Sinus-Komponente) zeigt, nicht nach Osten wie in der üblichen
        // Mathe-Konvention (verifiziert anhand von "visland" - Helpers.Angle.FromDirectionXZ nutzt
        // exakt dieselbe Formel). "player.Rotation - angleToTarget" (nicht umgekehrt!) - visland
        // verwendet (angleToTarget - player.Rotation) als LINKS/RECHTS-Bewegungseingabe (Sin() davon
        // = "Left"), unsere Bildschirm-X-Achse zeigt aber in die andere Richtung als "links" in
        // Bewegungsrichtung - ohne das Vorzeichen umzudrehen zeigte der Pfeil spiegelverkehrt.
        // 0 = Ziel genau voraus (Pfeil zeigt nach oben).
        var dx = target.Value.X - playerPos.X;
        var dz = target.Value.Z - playerPos.Z;
        var angleToTarget = MathF.Atan2(dx, dz);
        var relativeAngle = player.Rotation - angleToTarget;

        var drawList = ImGui.GetWindowDrawList();

        // Pfeil-Breite/-Höhe UNABHÄNGIG aus der konfigurierten Fenstergröße ableiten (General-Tab,
        // Gruppe "QoL") - bewusst NICHT über MathF.Min(width, height) für beide Achsen (dann hätte
        // z.B. alleiniges Erhöhen von "Pfeil-Breite" nichts bewirkt, solange die Höhe kleiner bleibt
        // - der Pfeil wäre nur verschoben, nie größer geworden). Mitte etwas über der Fenstermitte,
        // damit unten Platz für den Distanztext bleibt.
        var windowSize = ImGui.GetWindowSize();
        var halfWidth = windowSize.X * 0.3f;
        var halfHeight = windowSize.Y * 0.3f;
        var center = ImGui.GetWindowPos() + new Vector2(windowSize.X / 2f, windowSize.Y * 0.4f);

        var cos = MathF.Cos(relativeAngle);
        var sin = MathF.Sin(relativeAngle);
        Vector2 Rotate(Vector2 local) => center + new Vector2(local.X * cos - local.Y * sin, local.X * sin + local.Y * cos);

        var tip = Rotate(new Vector2(0f, -halfHeight));
        var baseLeft = Rotate(new Vector2(-halfWidth * 0.6f, halfHeight * 0.6f));
        var baseRight = Rotate(new Vector2(halfWidth * 0.6f, halfHeight * 0.6f));
        var notch = Rotate(new Vector2(0f, halfHeight * 0.25f));

        var fillColor = ImGui.ColorConvertFloat4ToU32(plugin.Configuration.NavigationArrowColor);

        // EIN Aufruf für die ganze Pfeilform (Fan-Triangulierung ab "tip", die genau die beiden
        // vorherigen Einzel-Dreiecke ergibt), statt zwei separater AddTriangleFilled-Aufrufe - die
        // hatten an der gemeinsamen Kante (tip-notch) jeweils eine EIGENE Kantenglättung erzeugt,
        // was dort wie eine unscharfe/verpixelte Naht aussah. Bleibt scharf, egal wie groß skaliert.
        Span<Vector2> arrowPoints = stackalloc Vector2[] { tip, baseLeft, notch, baseRight };
        drawList.AddConvexPolyFilled(ref arrowPoints[0], arrowPoints.Length, fillColor);

        // Dünner Umriss als EIGENE, einzelne Polylinie (geschlossen) um die ganze Form - nicht pro
        // Dreieck, sonst wieder dieselbe Naht-Unschärfe wie beim Füllen.
        var outlineColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.85f));
        drawList.AddPolyline(ref arrowPoints[0], arrowPoints.Length, outlineColor, ImDrawFlags.Closed, 1f);

        var distanceText = $"{distance:0}y";
        var textSize = ImGui.CalcTextSize(distanceText);
        var textPos = center + new Vector2(-textSize.X / 2f, halfHeight + 6f);
        var textShadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.6f));
        drawList.AddText(textPos + Vector2.One, textShadowColor, distanceText);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(Vector4.One), distanceText);
    }
}
