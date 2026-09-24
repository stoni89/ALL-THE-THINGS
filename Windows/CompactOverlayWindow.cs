using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;

namespace TheExplorersCodex.Windows;

/// <summary>
/// Schlankes, randloses Overlay-Fenster - zeigt nur Sammelobjekte der aktuellen Zone.
/// Gedacht zum permanenten Offenlassen neben der Action-Leiste, daher bewusst kompakt gehalten.
/// </summary>
public class CompactOverlayWindow : Window
{
    private readonly Plugin plugin;

    // NoBringToFrontOnFocus ist hier der entscheidende Teil: ohne das rutscht das Overlay bei
    // JEDER Interaktion (auch nur Hovern/Scrollen) wieder an die Spitze des ImGui-Z-Stapels - lag
    // ein anderes Fenster (Spiel-eigenes UI oder ein anderes Plugin) optisch DARÜBER, fing das
    // Overlay dessen Klicks trotzdem ab (es galt für die Eingabe-Ermittlung weiterhin als "vorn"),
    // wodurch das andere Fenster an dieser Stelle nicht mehr klickbar war. Da dieses Fenster ohnehin
    // dauerhaft offen bleiben soll (kein Grund, es je "nach vorne" zu holen), kostet das nichts.
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoBringToFrontOnFocus;

    public CompactOverlayWindow(Plugin plugin) : base("##TheExplorersCodexCompact", BaseFlags)
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

    // Position/Größe vom LETZTEN Frame (siehe Draw, ganz unten aktualisiert) - für die
    // Überlappungsprüfung in PreDraw. Erst NACH Begin() (also in Draw) wüsste man die aktuelle
    // Position zwar noch genauer, aber Fenster-Flags wie NoMouseInputs (siehe PreDraw) wirken nur,
    // wenn sie VOR Begin() gesetzt werden - eine Frame Verzögerung ist dafür unmerklich, da sich
    // die Fensterposition normalerweise nicht jeden Frame ändert.
    private Vector2? lastWindowMin;
    private Vector2? lastWindowMax;

    // Vom aktuellen Frame - einmal in PreDraw ermittelt (auf Basis der Fensterposition vom LETZTEN
    // Frame, siehe lastWindowMin/Max), dann in Draw benutzt, um dort per ImGuiP.SetWindowHitTestHole
    // gezielt Mausklicks ans darunterliegende native Fenster durchzureichen, und über IsOccluded in
    // praktisch jedem Zeichenaufruf in DrawContent, um NUR die davon betroffenen Zeilen/Knöpfe/Icons
    // unsichtbar zu machen - der Rest des Overlays bleibt normal sichtbar/klickbar, auch wenn
    // irgendwo ein natives Fenster überlappt.
    private List<(Vector2 Min, Vector2 Max)> nativeOverlapRects = new();

    // Eingeklappt = nur die Kopfzeile (Titel + Schloss-/Einklapp-/Schließen-Knopf) sichtbar, der
    // Rest (Zonenname, Währungen, Sammelobjekt-Liste) wird ausgeblendet - identisches Prinzip wie
    // beim Optionsfenster (siehe MainWindow.collapsed), hier aber die Zielhöhe NICHT fest verdrahtet,
    // sondern jeden Frame aus der tatsächlich gezeichneten Kopfzeile gemessen (siehe DrawContent) -
    // die Schriftgröße dieses Fensters ist über config.CompactFontScale frei einstellbar, eine feste
    // Höhe wie bei MainWindow würde dort also nicht zu jeder Skalierung passen.
    private bool collapsed;
    private bool collapsedLastFrame;
    private Vector2 expandedSize = new(260, 200);

    // Wie collapsedLastFrame, aber für Configuration.HideOverlayWhenEmpty (siehe DrawContent, ganz
    // am Anfang gesetzt/gelesen) - ohne diese Wiederherstellung in PreDraw würde das Fenster nach
    // dem Schrumpfen auf 0x0 dauerhaft winzig bleiben, auch nachdem wieder etwas fehlt.
    private bool hiddenDueToEmptyLastFrame;

    // Umschaltet zwischen "Deine Währungen:" (besessene Menge) und "Benötigte Währung:" (Summe der
    // noch fehlenden Menge über alle aktuell angezeigten, noch nicht besessenen Einträge hinweg) -
    // siehe DrawCurrencyWallet. Bewusst kein Configuration-Feld, da es sich nur um eine
    // Sitzungs-Ansicht handelt, kein dauerhaft zu speichernder Zustand.
    private bool showCurrencyCostMode;

    // Wiederverwendetes leeres Dictionary statt bei jeder Währung im "Benötigte Währung"-Modus neu
    // zu allozieren (siehe DrawCurrencyWallet) - dort wird gar nicht erst nach Retainer-Beständen
    // gefragt.
    private static readonly Dictionary<string, uint> EmptyRetainerCounts = new();

    /// <summary>
    /// Ob das Element, das man an der AKTUELLEN Cursor-Position mit der übergebenen Größe zeichnen
    /// würde, unter einem nativen Fenster liegen würde (siehe nativeOverlapRects) - jede
    /// zeichnende Methode in dieser Klasse ruft das VOR dem eigentlichen Zeichnen auf und
    /// zeichnet bei true stattdessen einen gleich großen ImGui.Dummy (siehe z.B. OutlineText),
    /// damit Layout/SameLine-Reihenfolge unverändert bleiben, aber nichts Sichtbares/Klickbares
    /// an dieser Stelle entsteht.
    /// </summary>
    private bool IsOccluded(Vector2 size)
    {
        if (nativeOverlapRects.Count == 0)
            return false;

        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        return nativeOverlapRects.Any(r => r.Min.X < max.X && r.Max.X > min.X && r.Min.Y < max.Y && r.Max.Y > min.Y);
    }

    /// <summary>
    /// Verhindert, dass das Overlay schon am Titelbildschirm (vor dem Einloggen) oder während des
    /// Lade-/Zonenwechsel-Übergangs (BetweenAreas/BetweenAreas51 - Ladebildschirm, Zone noch nicht
    /// fertig geladen) mit ggf. veralteten Daten der letzten Sitzung angezeigt wird - wird von
    /// Dalamuds WindowSystem VOR PreDraw/Draw/PostDraw geprüft, das Fenster erscheint also gar
    /// nicht erst statt nur mit falschem Inhalt.
    /// </summary>
    public override bool DrawConditions() =>
        Plugin.ClientState.IsLoggedIn && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51];

    // Dezentes Weiß-Grau statt der vorherigen lila Farbe - klein und unauffällig, zeigt aber
    // weiterhin an, wo sich das Fenster zum Skalieren greifen lässt.
    private static readonly Vector4 ResizeGripColor = new(1f, 1f, 1f, 0.25f);
    private static readonly Vector4 ResizeGripHoveredColor = new(1f, 1f, 1f, 0.5f);
    private static readonly Vector4 ResizeGripActiveColor = new(1f, 1f, 1f, 0.7f);
    private static readonly Vector4 ResizeGripHiddenColor = new(0f, 0f, 0f, 0f);

    // Ungefähre sichtbare Kantenlänge (Yalm... äh, Pixel) des von ImGui selbst in die untere rechte
    // Fensterecke gezeichneten Greifdreiecks - ImGui legt die genaue Größe intern fest (u.a. an der
    // Schriftgröße orientiert), ein fester, großzügig bemessener Wert reicht hier aber, da es nur um
    // die grobe Überlappungsprüfung geht, nicht um pixelgenaues Clipping.
    private const float ResizeGripVisualSize = 20f;

    public override void PreDraw()
    {
        var config = plugin.Configuration;

        // Auf Basis der Fensterposition vom LETZTEN Frame (siehe Draw, ganz unten aktualisiert) -
        // erst NACH Begin() (also in Draw) wüsste man die aktuelle Position zwar noch genauer, eine
        // Frame Verzögerung ist dafür unmerklich, da sich die Fensterposition normalerweise nicht
        // jeden Frame ändert.
        nativeOverlapRects = lastWindowMin.HasValue && lastWindowMax.HasValue
            ? Plugin.GetOverlappingNativeWindowRects(lastWindowMin.Value, lastWindowMax.Value)
            : new List<(Vector2 Min, Vector2 Max)>();

        // Gesperrt = nur die Position fixiert, nicht die Größe - das Fenster bleibt also auch im
        // gesperrten Zustand an der Ecke skalierbar (z.B. wenn ein Mount-Name nicht mehr in die
        // aktuelle Breite passt), nur das versehentliche Verschieben wird verhindert. Eingeklappt
        // zusätzlich NoResize - bei der dann sehr knappen Höhe würde ImGuis eigene Rahmen-Zieh-
        // Trefferzone sonst praktisch das ganze Fenster überlappen und Klicks (auch den Klick auf
        // den Ausklapp-Knopf) abfangen, statt sie durchzulassen (identisches Problem/Lösung wie im
        // Optionsfenster, siehe MainWindow.PreDraw).
        var flags = BaseFlags;
        if (config.CompactLocked)
            flags |= ImGuiWindowFlags.NoMove;
        if (collapsed)
            flags |= ImGuiWindowFlags.NoResize;
        Flags = flags;

        // Siehe MainWindow.PreDraw (identisches Problem/Lösung): ImGuis Stil-Standard WindowMinSize
        // (32x32) würde ein Schrumpfen auf die (deutlich kleinere) eingeklappte Kopfzeilenhöhe sonst
        // verhindern, egal was wir per Größe vorgeben.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));

        // Zurück zur zuletzt bekannten ausgeklappten Größe - muss VOR Begin() passieren (siehe
        // MainWindow.PreDraw), sonst kommt die Wiederherstellung erst einen Frame zu spät sichtbar
        // an. Das Schrumpfen beim EINklappen passiert dagegen bewusst NICHT hier, sondern erst in
        // DrawContent (nach Begin()) - dort ist die tatsächlich benötigte Höhe der Kopfzeile bekannt
        // (abhängig von config.CompactFontScale), hier vorher noch nicht.
        if ((!collapsed && collapsedLastFrame) || hiddenDueToEmptyLastFrame)
        {
            Size = expandedSize;
            SizeCondition = ImGuiCond.Always;
        }
        else
        {
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        collapsedLastFrame = collapsed;

        var alpha = 1f - System.Math.Clamp(config.CompactTransparency, 0f, 1f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        // Dieselbe Grundfarbe wie das Optionsfenster (siehe ModernUi.PushStyle) - bei Transparenz=0
        // (voll undurchsichtig) sehen beide Fenster damit identisch aus.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.055f, 0.063f, 0.098f, alpha));

        // Genau wie bei der Scrollbar (siehe DrawContent) wird auch das Greifdreieck unten rechts
        // von ImGui selbst gezeichnet, nicht über die zeilenweise IsOccluded-Prüfung - läge ein
        // natives Fenster genau unter dieser Ecke, würde es trotzdem weiter sichtbar darüber
        // gezeichnet. Deshalb hier komplett durchsichtig machen, statt in den normalen Farben, wenn
        // die (grob abgeschätzte) Greif-Ecke ein natives Fenster überlappt.
        var gripOverlapped = false;
        if (lastWindowMax.HasValue)
        {
            var gripMin = lastWindowMax.Value - new Vector2(ResizeGripVisualSize, ResizeGripVisualSize);
            var gripMax = lastWindowMax.Value;
            gripOverlapped = nativeOverlapRects.Any(r =>
                r.Min.X < gripMax.X && r.Max.X > gripMin.X && r.Min.Y < gripMax.Y && r.Max.Y > gripMin.Y);
        }

        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, gripOverlapped ? ResizeGripHiddenColor : ResizeGripColor);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, gripOverlapped ? ResizeGripHiddenColor : ResizeGripHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, gripOverlapped ? ResizeGripHiddenColor : ResizeGripActiveColor);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(4);
    }

    public override void Draw()
    {
        var window = ImGuiP.GetCurrentWindow();

        // Erzwingt JEDEN Frame aufs Neue, dass dieses Fenster ganz hinten im Anzeige-Stapel sitzt -
        // NoBringToFrontOnFocus (siehe BaseFlags) verhindert nur, dass es bei eigener Interaktion
        // wieder nach vorn rutscht, garantiert aber nicht, dass es ÜBERHAUPT hinten bleibt (z.B.
        // wenn ein anderes Fenster geschlossen und neu geöffnet wird). BringWindowToDisplayBack ist
        // intern in ImGui, aber über ImGuiP öffentlich zugänglich - genau dafür gedacht.
        ImGuiP.BringWindowToDisplayBack(window);

        // Für die Überlappungsprüfung im NÄCHSTEN Frame merken (siehe PreDraw/nativeOverlapRects).
        lastWindowMin = ImGui.GetWindowPos();
        lastWindowMax = lastWindowMin + ImGui.GetWindowSize();

        // Nur merken, solange NICHT eingeklappt - sonst würde die (künstlich auf Kopfzeilenhöhe
        // geschrumpfte) Größe versehentlich als "neue ausgeklappte Normalgröße" gespeichert und beim
        // Ausklappen fälschlich wiederhergestellt (identisches Problem/Lösung wie in MainWindow.Draw).
        if (!collapsed)
            expandedSize = ImGui.GetWindowSize();

        // Dalamud/ImGui zeichnet grundsätzlich IMMER über dem nativen Spiel-UI (keine echte Z-Order
        // zwischen beiden möglich, siehe Plugin.GetOverlappingNativeWindowRects-Kommentar) - echte
        // Mausklicks würden ein darunter liegendes natives Fenster (Währung, Inventar, ...) sonst
        // nie erreichen, obwohl es dort sichtbar ist. SetWindowHitTestHole "durchlöchert" unser
        // Fenster gezielt an dieser Stelle, statt (wie früher) für den GESAMTEN Frame sämtliche
        // Mauseingaben zu deaktivieren - der Rest des Overlays bleibt normal klickbar. ImGui
        // unterstützt intern nur EIN Loch pro Fenster und Frame - bei mehreren gleichzeitig
        // überlappenden nativen Fenstern (seltener Fall) wird deshalb die umschließende Hülle aller
        // Überlappungen als ein einziges Loch benutzt, statt nur die letzte zu berücksichtigen.
        if (nativeOverlapRects.Count > 0)
        {
            var holeMin = nativeOverlapRects[0].Min;
            var holeMax = nativeOverlapRects[0].Max;
            foreach (var (min, max) in nativeOverlapRects.Skip(1))
            {
                holeMin = Vector2.Min(holeMin, min);
                holeMax = Vector2.Max(holeMax, max);
            }

            ImGuiP.SetWindowHitTestHole(window, holeMin, holeMax - holeMin);
        }

        DrawContent();
    }

    private void DrawContent()
    {
        var config = plugin.Configuration;
        using var fontScope = PushCompactFont(config);
        ImGui.SetWindowFontScale(config.CompactFontScale);

        var currentTerritoryId = Plugin.ClientState.TerritoryType;

        // Manche Zonen gehören zu einer Stadt, haben aber ein eigenes TerritoryType (z.B. "Heart of
        // the Sworn" -> Ul'dah) - dort sollen dieselben Daten wie in der zugeordneten Stadtzone
        // verwendet werden. Nur für Datenabfragen, nicht für die angezeigte Zonenüberschrift unten.
        var effectiveTerritoryId = Plugin.ResolveEffectiveTerritoryId(currentTerritoryId);

        // In geteilten Hauptstädten (Ul'dah, Limsa, Gridania, Ishgard) sollen Sammelobjekte aus
        // JEDEM Bezirk angezeigt werden, egal in welchem man gerade steht - jeder Eintrag verlinkt
        // trotzdem auf seinen tatsächlichen Bezirk (siehe FlagTerritoryTypeId/MapId je Eintrag).
        var siblingTerritories = Plugin.GetSplitCityTerritories(effectiveTerritoryId);
        var allForZone = CollectionData.GetAllEntries()
            .Concat(plugin.GetLiveZoneEntries(effectiveTerritoryId))
            // Hunting-Log-Einträge sind bewusst an die TATSÄCHLICHE Zone (nicht die für geteilte
            // Hauptstädte "aufgelöste" effectiveTerritoryId) gebunden - roamende Monster gibt es
            // nur in genau dieser einen Zone, nicht stadtweit wie Aetheryten/Quest-NPCs.
            .Concat(plugin.GetHuntingLogEntries(currentTerritoryId))
            .Where(e => siblingTerritories.Contains(e.TerritoryTypeId))
            // Bei deaktiviertem "Alle Gegenstände anzeigen" (Configuration.ShowAllItems, siehe
            // MainWindow-Einstellungen) Einträge ausblenden, die nur durch eine noch nicht erreichte
            // Errungenschaft/einen noch nicht freigeschalteten Rang ODER (siehe
            // ComputeGrandCompanyOrTribeGateReason) ein gerade nicht laufendes Saisonevent erreichbar
            // sind (siehe Plugin.AchievementOrRankGatedItems) - bei aktiviertem Schalter bleiben sie
            // sichtbar, aber mit der "Bedingung nicht erfüllt"-Markierung (siehe weiter unten).
            .Where(e => config.ShowAllItems || !Plugin.IsAchievementOrRankGated(e))
            .ToList();

        var afterTypeFilter = allForZone
            .Where(e => config.ShowType.GetValueOrDefault(e.Type, true))
            .ToList();

        // Siehe "Currencys filtern" weiter unten - blendet ALLE Einträge einer vom Nutzer
        // ausgewählten Währung aus, unabhängig vom Typ. Prüft auch AdditionalCurrencies (siehe
        // GetAllCurrencyLabels) - ein Eintrag mit mehreren Währungen (z.B. Triple-Triad-Karte
        // "G-Warrior") verschwindet also auch dann, wenn nur EINE seiner mehreren Währungen
        // ausgeblendet wurde, nicht nur bei der ersten.
        var afterCurrencyFilter = afterTypeFilter
            .Where(e => !GetAllCurrencyLabels(e).Any(config.HiddenCurrencies.Contains))
            .ToList();

        var entries = afterCurrencyFilter
            .Where(e => !plugin.IsOwned(e))
            // Siehe Configuration.ShowOnlyActiveEventItems-Kommentar - blendet bei aktiviertem
            // Schalter NUR die Saisonevent-Einträge aus, deren Event gerade NICHT läuft; alle
            // anderen Einträge (auch alle normalen, nicht event-gebundenen) bleiben unverändert.
            .Where(e => !config.ShowOnlyActiveEventItems || e.Category != "Saisonevent" || Plugin.IsSeasonalEventEntryCurrentlyActive(e))
            .OrderBy(e => config.TypeOrder.IndexOf(e.Type))
            .ThenBy(e => e.Vendor)
            .ThenBy(e => e.Name)
            .ToList();

        // Bewusst aus "allForZone" (nicht "entries") - die Automation soll unabhängig vom
        // Typen-Filter laufen, auch wenn Quests im Overlay z.B. ausgeblendet sind. IsAchievementOrRankGated
        // aber IMMER zusätzlich ausgeschlossen (nicht nur wenn "Alle Gegenstände anzeigen" aus ist,
        // siehe allForZone) - sonst würde die Automation bei aktiviertem Schalter auch Quests
        // anlaufen, die als "Bedingung nicht erfüllt" markiert sind (z.B. "Simply to Dye For" ohne
        // abgeschlossene Artefakt-Rüstungsquest).
        var missingQuests = allForZone
            .Where(e => e.Type == CollectibleType.Quest && !plugin.IsOwned(e) && !Plugin.IsAchievementOrRankGated(e))
            .ToList();

        // Unabhängig davon, ob die Automation läuft - damit die rote "Nicht unterstützt"-Markierung
        // schon beim Betreten der Zone erscheint, statt erst nach einem gestarteten Automation-Lauf.
        plugin.QuestAutomation.RefreshSupportStatus(missingQuests);
        plugin.QuestAutomation.Update(missingQuests, effectiveTerritoryId);

        // Bewusst die ganze Stadt (inkl. Kristalle aus Nachbarbezirken einer geteilten Hauptstadt,
        // siehe allForZone) - die Automation reist bei Bedarf selbst mit Lifestream zwischen den
        // Bezirken hin und her (siehe AetheryteAutomation.cs).
        var missingAetherytesCity = allForZone
            .Where(e => e.Type == CollectibleType.Aetheryte && (config.SimulateAetheryteAutomation || !plugin.IsOwned(e)))
            .ToList();
        plugin.AetheryteAutomation.Update(missingAetherytesCity);

        // Bewusst NICHT stadtweit wie Aetheryten/Quests - Hunting-Log-Monster gibt es nur in genau
        // dieser einen Zone (siehe Plugin.GetHuntingLogEntries), kein Bezirkswechsel nötig/möglich.
        var missingHuntingLogInZone = allForZone
            .Where(e => e.Type == CollectibleType.HuntingLog)
            .ToList();
        plugin.HuntingLogAutomation.Update(missingHuntingLogInZone);

        // Wie Hunting Log bewusst NICHT stadtweit - Ätherströmungen kommen aus aethercurrents.json
        // mit exakter Zonen-Zuordnung, kein Bezirkswechsel nötig.
        var missingAetherCurrentsInZone = allForZone
            .Where(e => e.Type == CollectibleType.AetherCurrent && !plugin.IsOwned(e))
            .ToList();
        plugin.AetherCurrentAutomation.Update(missingAetherCurrentsInZone);

        // Ebenfalls nicht stadtweit - Sightseeing-Punkte kommen aus GetLiveZoneEntries mit exakter
        // Zonen-Zuordnung (siehe Plugin.ComputeLiveZoneEntries). Bewusst NICHT aus "allForZone" (das
        // würde bei deaktiviertem "Alle Gegenstände anzeigen" gerade durch Wetter/Uhrzeit/Buch-
        // Freischaltung gesperrte Punkte schon vor diesem Filter hier verlieren) - stattdessen direkt
        // aus GetLiveZoneEntries, damit SimulateSightseeingAutomation (siehe Configuration) unabhängig
        // von diesem Anzeige-Schalter zum Testen auch gesperrte Punkte anlaufen kann. Bewusst inkl.
        // siblingTerritories (geteilte Hauptstädte, z.B. "Barracuda Piers" in den Limsa Upper Decks,
        // während man selbst in den Lower Decks steht) - SightseeingAutomation reist bei Bedarf selbst
        // per Lifestream über den nächsten freigeschalteten Aetheryten in den Zielbezirk, genau wie
        // AetheryteAutomation/GoToAutomation (siehe SightseeingAutomation.TryTravelToDistrict).
        // IsSightseeingUnsupportedByAutomation und "kein Fliegen freigeschaltet" IMMER ausgeschlossen
        // (auch im Simulation-Modus, der die Gate-Prüfung darunter sonst bewusst umgeht) - echte
        // Jumping Puzzles ("The Carline Canopy", "The Leatherworkers' Guild"), die dauerhaft nur
        // manuell aufsuchbar sind (siehe Plugin.SightseeingUnsupportedByAutomation-Kommentar), bzw.
        // Fliegen als harte Voraussetzung fürs gesamte Feature (explizite Nutzeranforderung) - ohne
        // Fliegen kann vnavmesh die Punkte ohnehin nicht zuverlässig erreichen.
        var missingSightseeingInZone = plugin.GetLiveZoneEntries(effectiveTerritoryId)
            .Where(e => e.Type == CollectibleType.Sightseeing && siblingTerritories.Contains(e.TerritoryTypeId))
            .Where(e => !Plugin.IsSightseeingUnsupportedByAutomation(e.Id) && !Plugin.IsSightseeingBlockedByFlying(e))
            .Where(e => config.SimulateSightseeingAutomation || (!plugin.IsOwned(e) && !Plugin.IsAchievementOrRankGated(e)))
            .ToList();
        plugin.SightseeingAutomation.Update(missingSightseeingInZone);

        // Was tatsächlich im Overlay auftaucht (siehe "allForZone", inkl. dessen "Alle Gegenstände
        // anzeigen"-Schalter) - bewusst getrennt von missingSightseeingInZone oben, das für die
        // Automation extra ungefiltert ist. Nur wenn hier NICHTS mehr übrig ist, soll der Knopf ganz
        // verschwinden (siehe hasVisibleSightseeing unten); sind noch mit "Bedingung nicht erfüllt"
        // markierte Punkte sichtbar, bleibt er stehen und wird nur ausgegraut.
        var visibleSightseeingInZone = allForZone
            .Where(e => e.Type == CollectibleType.Sightseeing && !plugin.IsOwned(e))
            .ToList();

        // Ebenfalls nicht stadtweit - Chocobokeep-Standorte kommen aus GetChocobokeepEntries mit
        // exakter Zonen-Zuordnung, kein Bezirkswechsel nötig.
        var missingChocobokeepsInZone = allForZone
            .Where(e => e.Type == CollectibleType.Chocobokeep && (config.SimulateChocobokeepAutomation || !plugin.IsOwned(e)))
            .ToList();
        plugin.ChocobokeepAutomation.Update(missingChocobokeepsInZone);

        // Unabhängig von den Automationen oben - das "Hinlaufen"-Icon (siehe DrawClickableName)
        // betrifft immer nur einen einzelnen Eintrag, egal ob gerade eine Automation läuft.
        plugin.GoToAutomation.Update();

        // "Unterstützt" heißt hier: noch nicht als von Questionable abgelehnt bekannt (siehe
        // QuestAutomation.IsKnownUnsupported) - erst nach einem Versuch bekannt, siehe dort.
        var hasActionableQuests = missingQuests.Any(q => !plugin.QuestAutomation.IsKnownUnsupported(q.Id));
        var hasActionableAetherytes = missingAetherytesCity.Count > 0;
        var hasActionableHuntingLog = missingHuntingLogInZone.Any(e => e.WorldPosition.HasValue);
        var hasActionableAetherCurrents = missingAetherCurrentsInZone.Any(e => e.HasGoToTarget);
        // hasVisibleSightseeing entscheidet nur, ob der Knopf überhaupt gezeichnet wird (siehe
        // DrawAutomationButtonIfNeeded) - hasActionableSightseeing (gerade durch Wetter/Uhrzeit/
        // Buch-Freischaltung eingeschränkt) entscheidet zusätzlich, ob er dabei ausgegraut ist.
        var hasVisibleSightseeing = visibleSightseeingInZone.Any(e => e.HasGoToTarget) && Plugin.IsSightseeingLogUnlocked();
        var hasActionableSightseeing = missingSightseeingInZone.Any(e => e.HasGoToTarget) && Plugin.IsSightseeingLogUnlocked();
        var hasActionableChocobokeeps = missingChocobokeepsInZone.Any(e => e.HasGoToTarget);

        // Siehe Configuration.HideOverlayWhenEmpty-Kommentar - erst NACH allen Automation.Update()-
        // Aufrufen oben geprüft (die laufen immer weiter, unabhängig von der Sichtbarkeit), aber
        // VOR jeglichem Zeichnen (auch vor dem Kopfbereich) - schrumpft das Fenster auf 0x0 und
        // überspringt den Rest von DrawContent komplett, für echte Unsichtbarkeit statt nur einer
        // leeren Kopfzeile wie bei "collapsed".
        if (config.HideOverlayWhenEmpty && entries.Count == 0)
        {
            hiddenDueToEmptyLastFrame = true;
            ImGui.SetWindowSize(Vector2.Zero, ImGuiCond.Always);
            return;
        }

        hiddenDueToEmptyLastFrame = false;

        // Titel + Schloss-/Einklapp-/Schließen-Knopf in einer Gruppe - so lässt sich ihre
        // tatsächliche Höhe direkt danach per ImGui.GetItemRectSize() messen (siehe collapsed unten),
        // ohne sie an eine feste, skalierungsabhängige Pixelzahl zu koppeln.
        ImGui.BeginGroup();

        OutlineText("The Explorer's Codex", TitleColor);
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (ImGui.IsItemClicked())
            plugin.OpenOptions();

        // Alle drei Knöpfe gleich groß und direkt nebeneinander ganz am rechten Rand - die reine
        // Frame-Höhe war schmaler als die tatsächlichen Icon-Glyphen (Schloss/Times), wodurch beide
        // in ihrem eigenen Knopf beschnitten wirkten. Größe daher an der breiteren der Icon-Glyphen
        // ausgerichtet, plus ein kleiner rechter Rand, damit "x" nicht am Fensterrand klebt.
        float topRightIconWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            topRightIconWidth = MathF.Max(
                ImGui.CalcTextSize(FontAwesomeIcon.Lock.ToIconString()).X,
                MathF.Max(
                    ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X,
                    ImGui.CalcTextSize(FontAwesomeIcon.ChevronUp.ToIconString()).X));
        }

        var topRightButtonSize = MathF.Max(ImGui.GetFrameHeight(), topRightIconWidth + ImGui.GetStyle().FramePadding.X * 2f);
        const float TopRightMargin = 4f;

        if (DrawLockButtonTopRight(config.CompactLocked, topRightButtonSize, TopRightMargin))
        {
            config.CompactLocked = !config.CompactLocked;
            config.Save();
        }

        if (DrawCollapseButtonTopRight(collapsed, topRightButtonSize, TopRightMargin))
            collapsed = !collapsed;

        if (DrawCloseButtonTopRight(topRightButtonSize, TopRightMargin))
        {
            IsOpen = false;
            config.ShowCompactOverlay = false;
            config.Save();
        }

        ImGui.EndGroup();

        if (collapsed)
        {
            // Fenster auf genau die Höhe der eben gezeichneten Kopfzeile (plus das obere/untere
            // Innenpolster, siehe PreDraw) schrumpfen - erst jetzt (nach dem Zeichnen) bekannt, siehe
            // Kommentar bei DrawContent-Aufruf/PreDraw. ImGuiCond.Always wirkt hier sofort, auch
            // innerhalb desselben Begin()/End(), nicht erst nächsten Frame.
            var windowPaddingY = ImGui.GetStyle().WindowPadding.Y;
            var neededHeight = ImGui.GetItemRectSize().Y + windowPaddingY * 2f;
            ImGui.SetWindowSize(new Vector2(ImGui.GetWindowSize().X, neededHeight), ImGuiCond.Always);
            return;
        }

        OutlineText($"{Plugin.GetZoneName(currentTerritoryId)} ({currentTerritoryId})", MutedColor);

        // Reihe der Automations-Knöpfe bricht bei Bedarf selbst in eine zweite Zeile um (statt über
        // den Fensterrand hinauszulaufen), wenn das kompakte Fenster nicht breit genug gezogen
        // wurde - jeder Knopf entscheidet VOR dem eigentlichen Zeichnen anhand seiner (aus dem
        // Label vorab berechneten) Breite, ob er noch auf die aktuelle Zeile passt. Die Automationen
        // selbst laufen unabhängig von config.ShowAutomationButtons weiter (siehe .Update-Aufrufe
        // oben) - nur diese Knopfreihe wird ein-/ausgeblendet.
        if (config.ShowAutomationButtons)
        {
            var automationRowContentMaxX = ImGui.GetWindowContentRegionMax().X;
            var automationStopLabel = Loc.T("Automation stoppen", "Stop automation");
            var isFirstAutomationButtonOnRow = true;

            // Ein Knopf wird komplett ausgeblendet (statt nur ausgegraut), sobald es in dieser Zone
            // nichts (mehr) für ihn zu tun gibt - UND er nicht gerade selbst läuft (läuft er schon,
            // muss "Automation stoppen" klickbar sichtbar bleiben, auch falls die Liste inzwischen
            // leer aussieht). Ein fehlendes Fremdplugin blendet bewusst NICHT aus - das bleibt
            // ausgegraut mit erklärendem Tooltip sichtbar, siehe MissingPluginTooltip in den
            // einzelnen DrawXAutomationButton-Methoden.
            void DrawAutomationButtonIfNeeded(bool isActive, bool hasActionable, string startLabel, Action draw)
            {
                if (!isActive && !hasActionable)
                    return;

                if (!isFirstAutomationButtonOnRow)
                {
                    var label = isActive ? automationStopLabel : startLabel;
                    var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
                    ImGui.SameLine();
                    if (ImGui.GetCursorPosX() + width > automationRowContentMaxX)
                        ImGui.NewLine();
                }
                isFirstAutomationButtonOnRow = false;
                draw();
            }

            DrawAutomationButtonIfNeeded(plugin.QuestAutomation.IsActive, hasActionableQuests,
                Loc.T("Auto Quest", "Auto Quest"), () => DrawQuestAutomationButton(hasActionableQuests, effectiveTerritoryId));
            DrawAutomationButtonIfNeeded(plugin.AetheryteAutomation.IsActive, hasActionableAetherytes,
                Loc.T("Auto Aetheryte", "Auto Aetheryte"), () => DrawAetheryteAutomationButton(hasActionableAetherytes));
            DrawAutomationButtonIfNeeded(plugin.HuntingLogAutomation.IsActive, hasActionableHuntingLog,
                Loc.T("Auto Hunting Log", "Auto Hunting Log"), () => DrawHuntingLogAutomationButton(hasActionableHuntingLog));
            DrawAutomationButtonIfNeeded(plugin.AetherCurrentAutomation.IsActive, hasActionableAetherCurrents,
                Loc.T("Auto Ätherströmung", "Auto Aether Current"), () => DrawAetherCurrentAutomationButton(hasActionableAetherCurrents));
            DrawAutomationButtonIfNeeded(plugin.SightseeingAutomation.IsActive, hasVisibleSightseeing,
                Loc.T("Auto Sightseeing", "Auto Sightseeing"), () => DrawSightseeingAutomationButton(hasActionableSightseeing));
            DrawAutomationButtonIfNeeded(plugin.ChocobokeepAutomation.IsActive, hasActionableChocobokeeps,
                Loc.T("Auto Chocobokeep", "Auto Chocobokeep"), () => DrawChocobokeepAutomationButton(hasActionableChocobokeeps));

            if (!isFirstAutomationButtonOnRow)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
        }

        if (plugin.QuestAutomation.ShouldShowStatusText)
            OutlineText(plugin.QuestAutomation.StatusText, plugin.QuestAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (plugin.AetheryteAutomation.ShouldShowStatusText)
            OutlineText(plugin.AetheryteAutomation.StatusText, plugin.AetheryteAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (plugin.HuntingLogAutomation.ShouldShowStatusText)
            OutlineText(plugin.HuntingLogAutomation.StatusText, plugin.HuntingLogAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (plugin.AetherCurrentAutomation.ShouldShowStatusText)
            OutlineText(plugin.AetherCurrentAutomation.StatusText, plugin.AetherCurrentAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (plugin.SightseeingAutomation.ShouldShowStatusText)
            OutlineText(plugin.SightseeingAutomation.StatusText, plugin.SightseeingAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (plugin.ChocobokeepAutomation.ShouldShowStatusText)
            OutlineText(plugin.ChocobokeepAutomation.StatusText, plugin.ChocobokeepAutomation.IsActive ? AffordableColor : VendorLinkColor);

        if (config.ShowDebugInfo)
            OutlineText($"debug: zone={allForZone.Count} typefilter={afterTypeFilter.Count} missing={entries.Count}", MutedColor);

        // Zähler links, "Typen filtern" weiterhin ganz rechts an den Fensterrand - jetzt zusammen
        // auf derselben Zeile statt oben bei den Automation-Knöpfen, da sich der Filter direkt auf
        // diese Anzahl auswirkt.
        OutlineText($"[{entries.Count}]", TitleColor);

        var filterLabel = Loc.T("Typen filtern", "Filter types") + "##CompactTypeFilter";
        var filterButtonWidth = ImGui.CalcTextSize(Loc.T("Typen filtern", "Filter types")).X + ImGui.GetStyle().FramePadding.X * 2f;
        var currencyFilterLabel = Loc.T("Currencys filtern", "Filter currencies") + "##CompactCurrencyFilter";
        var currencyFilterButtonWidth = ImGui.CalcTextSize(Loc.T("Currencys filtern", "Filter currencies")).X + ImGui.GetStyle().FramePadding.X * 2f;

        // Beide Filter-Knöpfe ganz rechts an den Fensterrand, "Currencys filtern" links davon.
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - filterButtonWidth - currencyFilterButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        var currencyFilterButtonSize = new Vector2(currencyFilterButtonWidth, ImGui.GetFrameHeight());
        if (IsOccluded(currencyFilterButtonSize))
            ImGui.Dummy(currencyFilterButtonSize);
        else if (ImGui.Button(currencyFilterLabel))
            ImGui.OpenPopup("CompactCurrencyFilterPopup");

        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - filterButtonWidth);
        var filterButtonSize = new Vector2(filterButtonWidth, ImGui.GetFrameHeight());
        if (IsOccluded(filterButtonSize))
            ImGui.Dummy(filterButtonSize);
        else if (ImGui.Button(filterLabel))
            ImGui.OpenPopup("CompactTypeFilterPopup");

        // Derselbe Hintergrundton wie im Optionsfenster (siehe ModernUi.PushStyle/PopupBg) - ohne
        // diesen expliziten Push würde die Popup hier stattdessen mit dem ImGui-Standardgrau statt
        // dem Rest des Plugin-Looks erscheinen.
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.10f, 0.12f, 0.17f, 0.98f));
        if (ImGui.BeginPopup("CompactCurrencyFilterPopup"))
        {
            DrawCurrencyFilterPopupContent();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("CompactTypeFilterPopup"))
        {
            foreach (var type in config.TypeOrder)
            {
                var enabled = config.ShowType.GetValueOrDefault(type, true);
                var isNotYetPossible = !Plugin.IsTypeCurrentlyPossible(type);
                if (isNotYetPossible)
                    ImGui.PushStyleColor(ImGuiCol.Text, NotYetPossibleColor);

                if (ImGui.Checkbox($"{Loc.TypeName(type)}##CompactTypeFilterEntry", ref enabled))
                {
                    config.ShowType[type] = enabled;
                    config.Save();
                }

                if (isNotYetPossible)
                {
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Plugin.GetTypeNotPossibleReason(type));
                }
            }

            ImGui.EndPopup();
        }
        ImGui.PopStyleColor();

        if (entries.Count == 0)
        {
            OutlineText(Loc.T("Nichts Fehlendes in dieser Zone.", "Nothing missing in this zone."), MutedColor);
            return;
        }

        if (config.ShowCurrencyWallet)
            DrawCurrencyWallet(entries);

        // Die Scrollbar dieser Liste ist ein von ImGui selbst gezeichnetes Chrome-Element, nicht
        // Teil der einzelnen Zeilen oben (IsOccluded) - läge ein natives Fenster genau unter ihr,
        // würde sie trotzdem weiter sichtbar darüber gezeichnet (Dalamud/ImGui zeichnet immer über
        // dem nativen UI, siehe Plugin.GetOverlappingNativeWindowRects-Kommentar), und die
        // Überdeckungs-Illusion wäre an dieser schmalen Stelle kaputt. Deshalb vorab prüfen, ob die
        // (aus der bekannten Fenstergröße vorausberechnete) Scrollbar-Spalte überhaupt ein natives
        // Fenster überlappt, und die Scrollbar in dem Fall für diesen Frame komplett ausblenden
        // (ScrollbarSize=0) - scrollen per Mausrad bleibt dabei weiterhin möglich.
        var listMin = ImGui.GetCursorScreenPos();
        var listSize = ImGui.GetContentRegionAvail();
        var scrollbarSize = ImGui.GetStyle().ScrollbarSize;
        var scrollbarMin = new Vector2(listMin.X + listSize.X - scrollbarSize, listMin.Y);
        var scrollbarMax = listMin + listSize;
        var scrollbarOccluded = nativeOverlapRects.Any(r =>
            r.Min.X < scrollbarMax.X && r.Max.X > scrollbarMin.X && r.Min.Y < scrollbarMax.Y && r.Max.Y > scrollbarMin.Y);
        if (scrollbarOccluded)
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 0f);

        // Nur dieser Teil (die eigentliche Liste) soll scrollen - alles darüber (Titel, Knöpfe,
        // Status, Währungen) bleibt beim Scrollen fest stehen, size.Y=0 füllt dafür einfach den
        // Rest des (frei durch den Spieler skalierbaren) Fensters.
        ImGui.BeginChild("##CompactEntryList", new Vector2(0, 0), false);

        foreach (var entry in entries)
        {
            // Liegt genau DIESE Zeile gerade unter einem nativen Fenster (siehe Draw/
            // nativeOverlapRects/IsOccluded), wird nur sie durch eine leere, gleich hohe Dummy-Zeile
            // ersetzt - der Rest der Liste bleibt normal sichtbar/klickbar, statt (wie früher) beim
            // geringsten Kontakt mit einem nativen Fenster komplett zu verschwinden.
            var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeightWithSpacing());
            if (IsOccluded(rowSize))
            {
                ImGui.Dummy(rowSize);
                continue;
            }

            DrawGoToColumn(entry);

            var isUnsupportedQuest = entry.Type == CollectibleType.Quest && plugin.QuestAutomation.IsKnownUnsupported(entry.Id);
            // Noch nicht möglich (z.B. Sightseeing/Hunting Log ohne freigeschaltetes Fliegen in
            // dieser Zone) - Eintrag bleibt bewusst sichtbar (nicht rausgefiltert), nur ausgegraut,
            // siehe Plugin.IsTypeCurrentlyPossible.
            var isNotYetPossible = !isUnsupportedQuest && !Plugin.IsTypeCurrentlyPossible(entry.Type);
            var typeColor = isUnsupportedQuest ? UnsupportedColor : isNotYetPossible ? NotYetPossibleColor : TypeColors.GetValueOrDefault(entry.Type, NormalColor);
            OutlineText($"[{Loc.TypeName(entry.Type)}]", typeColor);
            if (isUnsupportedQuest && ImGui.IsItemHovered())
                ImGui.SetTooltip("Not supported with Questionable");
            else if (isNotYetPossible && ImGui.IsItemHovered())
                ImGui.SetTooltip(Plugin.GetTypeNotPossibleReason(entry.Type));

            ImGui.SameLine();
            DrawClickableName(entry, isNotYetPossible);

            if (!string.IsNullOrEmpty(entry.Currency))
            {
                ImGui.SameLine();
                OutlineText("-", MutedColor);

                var currencyAllaganToolsEligible = AllaganToolsEligibleTypes.Contains(entry.Type);
                DrawCurrencyRequirement(entry.Currency, entry.CurrencyIconId, entry.CurrencyItemId, entry.CurrencyAmount, currencyAllaganToolsEligible);

                // Für die wenigen Einträge, die MEHRERE Währungen gleichzeitig verlangen (z.B.
                // Triple-Triad-Karte "G-Warrior": 1x Ruby Totem + 1x Emerald Totem + 1x Diamond
                // Totem) - jede weitere genauso wie die erste anzeigen, siehe
                // CollectibleEntry.AdditionalCurrencies.
                if (entry.AdditionalCurrencies != null)
                {
                    foreach (var additional in entry.AdditionalCurrencies)
                        DrawCurrencyRequirement(additional.Currency, additional.CurrencyIconId, additional.CurrencyItemId, additional.CurrencyAmount, currencyAllaganToolsEligible);
                }
            }

            // Nur sichtbar, wenn "Alle Gegenstände anzeigen" aktiviert ist (siehe Filter weiter oben
            // in DrawContent) - bei deaktiviertem Schalter tauchen diese Einträge gar nicht erst in
            // der Liste auf, dieser Hinweis wäre dann redundant.
            if (Plugin.IsAchievementOrRankGated(entry))
            {
                ImGui.SameLine();

                if (Plugin.IsSightseeingBlockedByFlying(entry))
                {
                    // Explizite Nutzeranforderung: das Sightseeing-Feature soll nur mit
                    // freigeschaltetem Fliegen funktionieren - eigener, spezifischer Hinweistext
                    // statt des generischen Labels, hat Vorrang vor der Jumping-Puzzle-Sonderbehandlung
                    // unten (siehe Plugin.ComputeGrandCompanyOrTribeGateReason-Reihenfolge).
                    OutlineText(Loc.T("(Bedingung nicht erfüllt (Fliegen nicht freigeschaltet))", "(condition not met (flying not unlocked))"), UnsupportedColor);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Plugin.GetAchievementOrRankGateReason(entry));
                }
                else if (entry.Type == CollectibleType.Sightseeing && Plugin.IsSightseeingUnsupportedByAutomation(entry.Id) && Plugin.IsSightseeingBookAccessible(entry))
                {
                    // Trotz "von der Automation nicht unterstützt" (echtes Jumping Puzzle) weiterhin
                    // den tatsächlichen Wetter-/Zeit-Status zeigen - grün mit Restdauer, solange
                    // gerade aktiv (man kann so einen Punkt ja manuell erreichen), sonst wie gewohnt
                    // mit Countdown bis zur Verfügbarkeit. Der Text selbst bleibt IMMER
                    // "(Bedingung nicht erfüllt)", unabhängig vom Wetter/Zeit-Status (der Hinweis auf
                    // das Jumping Puzzle steht bereits im Hover-Tooltip, siehe unten).
                    var activeLabel = Plugin.GetSightseeingActiveUntilLabel(entry);
                    var isActive = !string.IsNullOrEmpty(activeLabel);
                    var timerSuffix = isActive ? activeLabel : Plugin.GetSightseeingAvailabilityLabel(entry);
                    var color = isActive ? AffordableColor : UnsupportedColor;

                    OutlineText(
                        Loc.T($"(Bedingung nicht erfüllt{timerSuffix})", $"(condition not met{timerSuffix})"),
                        color);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Plugin.GetAchievementOrRankGateReason(entry));
                }
                else
                {
                    // Für Sightseeing-Punkte, die gerade durch Wetter/Uhrzeit gesperrt sind, direkt im
                    // Label sichtbar (nicht erst im Hover-Tooltip) - siehe GetSightseeingAvailabilityLabel.
                    var availabilityLabel = Plugin.GetSightseeingAvailabilityLabel(entry);
                    OutlineText(Loc.T($"(Bedingung nicht erfüllt{availabilityLabel})", $"(condition not met{availabilityLabel})"), UnsupportedColor);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Plugin.GetAchievementOrRankGateReason(entry));
                }
            }
            else
            {
                // Sightseeing-Punkte mit Wetter-/Zeitfenster-Bedingung, die GERADE aktiv sind - grün
                // mit Restdauer, bis diese Bedingung wieder kippt (siehe GetSightseeingActiveUntilLabel).
                var activeUntilLabel = Plugin.GetSightseeingActiveUntilLabel(entry);
                if (!string.IsNullOrEmpty(activeUntilLabel))
                {
                    ImGui.SameLine();
                    OutlineText($"({Loc.T("aktiv", "active")}{activeUntilLabel})", AffordableColor);
                }
            }
        }

        ImGui.EndChild();

        if (scrollbarOccluded)
            ImGui.PopStyleVar();
    }

    /// <summary>
    /// Zeichnet EINE Preisangabe (Icon + Menge) direkt neben dem zuletzt gezeichneten Element (per
    /// ImGui.SameLine) - für Einträge mit mehreren gleichzeitig benötigten Währungen (siehe
    /// CollectibleEntry.AdditionalCurrencies) mehrfach hintereinander aufgerufen, für den
    /// Normalfall (nur eine Währung) genau einmal.
    /// </summary>
    private void DrawCurrencyRequirement(string currencyText, uint currencyIconId, uint currencyItemId, uint currencyAmount, bool allaganToolsEligible)
    {
        ImGui.SameLine();

        // Icon + Menge zusammen in einer Gruppe, damit EIN Hover-Bereich beide abdeckt - der Name
        // der Währung (z.B. "Allied Seals") steht nur noch im Tooltip, nicht mehr permanent
        // ausgeschrieben daneben, um die Zeile kompakt zu halten.
        ImGui.BeginGroup();

        if (currencyIconId != 0)
        {
            var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(currencyIconId)).GetWrapOrEmpty();
            var size = new Vector2(ImGui.GetTextLineHeight());
            ImGui.Image(icon.Handle, size);
        }

        // CurrencyAmount 0 heißt "wird durch das Öffnen eines Packs/einer Zufallsziehung erhalten,
        // nicht in fester Anzahl gekauft" (z.B. Platinum/Dream/Imperial/...-Triad-Card-Packs) -
        // dort nur das Icon zeigen, da man vorher nicht weiß, wie viele Packs man dafür öffnen muss.
        if (currencyAmount != 0)
        {
            if (currencyIconId != 0)
                ImGui.SameLine();

            var affordable = currencyItemId != 0 && plugin.GetCurrencyAmount(currencyItemId) >= currencyAmount;
            var color = affordable ? AffordableColor : NormalColor;
            OutlineText(currencyAmount.ToString("N0"), color);
        }

        ImGui.EndGroup();

        // SHIFT + Linksklick: Allagan Tools' "Mehr Informationen"-Fenster für DIESE Währung öffnen
        // (siehe Plugin.OpenAllaganToolsItemInfo), falls aktiviert, eine Item-ID bekannt ist und der
        // BESITZENDE Eintrag zu den Item-Typen gehört (siehe AllaganToolsEligibleTypes) - Quest/
        // Sightseeing/etc. zeigen aktuell zwar ohnehin nie eine Währung, aus Konsistenzgründen aber
        // trotzdem mitgeprüft.
        var allaganToolsEnabled = allaganToolsEligible && currencyItemId != 0
                                   && plugin.Configuration.EnableAllaganToolsIntegration && Plugin.IsAllaganToolsAvailable();

        if (ImGui.IsItemHovered())
        {
            var label = GetCurrencyLabel(currencyText);
            ImGui.SetTooltip(allaganToolsEnabled
                ? $"{label}\n{Loc.T("SHIFT + Klick: Mehr Informationen (Allagan Tools)", "SHIFT + click: more information (Allagan Tools)")}"
                : label);
        }

        if (allaganToolsEnabled && ImGui.IsItemClicked() && ImGui.GetIO().KeyShift)
            Plugin.OpenAllaganToolsItemInfo(currencyItemId);
    }

    /// <summary>
    /// Zeigt entweder, wie viel der Spieler von jeder Währung besitzt, die für die aktuell
    /// angezeigten (gefilterten, bereits auf "noch nicht besessen" gefilterten - siehe
    /// DrawContent/entries) Einträge benötigt wird, oder (per showCurrencyCostMode umgeschaltet)
    /// wie viel davon INSGESAMT nötig wäre, um alle diese Einträge zu kaufen.
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

        var toggleSize = new Vector2(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight());
        if (IsOccluded(toggleSize))
        {
            ImGui.Dummy(toggleSize);
        }
        else
        {
            bool toggled;
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                toggled = ImGui.Button($"{FontAwesomeIcon.ExchangeAlt.ToIconString()}##CompactCurrencyModeToggle", toggleSize);
            if (toggled)
                showCurrencyCostMode = !showCurrencyCostMode;
        }
        ImGui.SameLine();

        // X-Position, an der das Label beginnt (direkt nach dem Umschalt-Knopf) - die Währungsliste
        // darunter wird auf genau diese Spalte eingerückt (siehe SetCursorPosX unten), statt am
        // linken Fensterrand (unter dem Knopf) zu beginnen.
        var currencyIndentX = ImGui.GetCursorPosX();

        OutlineText(showCurrencyCostMode
            ? Loc.T("Benötigte Währung:", "Currency needed:")
            : Loc.T("Deine Währungen:", "Your currencies:"), MutedColor);

        // Mehrere Währungen pro Zeile statt jeweils einer eigenen - bricht (wie die Automations-
        // Knopfreihe weiter oben) selbst in eine weitere Zeile um, sobald das kompakte Fenster
        // nicht breit genug gezogen wurde. Jede Währung entscheidet VOR dem Zeichnen anhand ihrer
        // (aus Icon+Text vorab berechneten) Breite, ob sie noch auf die aktuelle Zeile passt.
        var contentMaxX = ImGui.GetWindowContentRegionMax().X;
        var iconSize = ImGui.GetTextLineHeight();
        var itemSpacing = ImGui.GetStyle().ItemSpacing.X;
        var isFirst = true;

        ImGui.SetCursorPosX(currencyIndentX);
        foreach (var sample in currencies)
        {
            var amount = showCurrencyCostMode
                ? (uint)entries.Where(e => e.CurrencyItemId == sample.CurrencyItemId).Sum(e => (long)e.CurrencyAmount)
                : plugin.GetCurrencyAmount(sample.CurrencyItemId);
            var label = GetCurrencyLabel(sample.Currency);
            var text = $"{amount.ToString("N0", CultureInfo.InvariantCulture)} {label}";
            var hasIcon = sample.CurrencyIconId != 0;

            // Nur im "Deine Währungen"-Modus (nicht "Benötigte Währung") - siehe Configuration.
            // ShowRetainerItemCounts-Kommentar. Leeres Dictionary (nicht null), solange Allagan
            // Tools fehlt/der Schalter aus ist - GetRetainerItemCounts prüft das selbst.
            var retainerCounts = showCurrencyCostMode
                ? EmptyRetainerCounts
                : Plugin.GetRetainerItemCounts(sample.CurrencyItemId);
            var retainerTotal = retainerCounts.Count == 0 ? 0u : (uint)retainerCounts.Values.Sum(v => (long)v);
            var retainerSuffix = retainerTotal > 0 ? $" ({retainerTotal})" : string.Empty;

            var itemWidth = ImGui.CalcTextSize(text + retainerSuffix).X + (hasIcon ? iconSize + itemSpacing : 0f);

            if (!isFirst)
            {
                ImGui.SameLine();
                if (ImGui.GetCursorPosX() + itemWidth > contentMaxX)
                {
                    ImGui.NewLine();
                    ImGui.SetCursorPosX(currencyIndentX);
                }
            }
            isFirst = false;

            if (IsOccluded(new Vector2(itemWidth, iconSize)))
            {
                ImGui.Dummy(new Vector2(itemWidth, iconSize));
                continue;
            }

            if (hasIcon)
            {
                var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(sample.CurrencyIconId)).GetWrapOrEmpty();
                ImGui.Image(icon.Handle, new Vector2(iconSize));
                ImGui.SameLine();
            }

            OutlineText(text, NormalColor);

            if (!string.IsNullOrEmpty(retainerSuffix))
            {
                ImGui.SameLine(0f, 0f);
                OutlineText(retainerSuffix, MutedColor);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(string.Join("\n", retainerCounts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{kv.Key}: {kv.Value.ToString("N0", CultureInfo.InvariantCulture)}")));
                }
            }
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

    // Manche Roh-Quelldaten schreiben dieselbe Währung uneinheitlich mal im Singular, mal im Plural
    // (z.B. "Bicolor Gemstone" vs. "Bicolor Gemstones") - ohne Abgleich taucht sie im "Currencys
    // filtern"-Popup fälschlich zweimal auf UND ein Ausblenden über die eine Schreibweise würde
    // Einträge mit der jeweils anderen gar nicht erfassen (siehe CanonicalizeCurrencyLabel). Reiner
    // Vergleichsschlüssel (nicht die Anzeige) - entfernt ein einzelnes anhängendes "s" (aber nicht
    // "ss", z.B. bei "Skybuilders' Scrips" oder generell Wörtern, die schon auf "ss" enden).
    private static string NormalizeCurrencyLabelKey(string label)
    {
        var lower = label.ToLowerInvariant();
        return lower.Length > 1 && lower.EndsWith('s') && !lower.EndsWith("ss") ? lower[..^1] : lower;
    }

    // Je Vergleichsschlüssel (siehe NormalizeCurrencyLabelKey) DIE Schreibweise, die unter allen
    // bekannten Einträgen am häufigsten vorkommt (bei Gleichstand die kürzere, meist die
    // Singular-Form) - einmalig aus der kompletten Sammlung aufgebaut, da Spielinhalte sich zur
    // Laufzeit nicht ändern.
    private static Dictionary<string, string>? currencyLabelCanonicalCache;

    private static string CanonicalizeCurrencyLabel(string rawLabel)
    {
        if (string.IsNullOrEmpty(rawLabel))
            return rawLabel;

        currencyLabelCanonicalCache ??= CollectionData.GetAllEntries()
            .SelectMany(GetRawCurrencyLabels)
            .Where(l => !string.IsNullOrEmpty(l))
            .GroupBy(NormalizeCurrencyLabelKey)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(l => l, StringComparer.Ordinal)
                    .OrderByDescending(gg => gg.Count())
                    .ThenBy(gg => gg.Key.Length)
                    .First().Key);

        var key = NormalizeCurrencyLabelKey(rawLabel);
        return currencyLabelCanonicalCache.TryGetValue(key, out var canonical) ? canonical : rawLabel;
    }

    private static IEnumerable<string> GetRawCurrencyLabels(CollectibleEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Currency))
            yield return GetCurrencyLabel(entry.Currency);

        if (entry.AdditionalCurrencies != null)
        {
            foreach (var additional in entry.AdditionalCurrencies)
            {
                if (!string.IsNullOrEmpty(additional.Currency))
                    yield return GetCurrencyLabel(additional.Currency);
            }
        }
    }

    /// <summary>
    /// Alle Währungen (Kurzname + Icon-Id) eines Eintrags - normalerweise nur eine (die primäre,
    /// Currency/CurrencyIconId), bei mehreren gleichzeitig benötigten (siehe
    /// CollectibleEntry.AdditionalCurrencies, z.B. Triple-Triad-Karte "G-Warrior") auch die
    /// weiteren. Für den Currency-Filter (siehe DrawContent/DrawCurrencyFilterPopupContent), damit
    /// ein Eintrag bei JEDER seiner Währungen gefunden/ausgeblendet werden kann, nicht nur der ersten.
    /// Label ist bereits kanonisiert (siehe CanonicalizeCurrencyLabel), damit Singular-/Plural-
    /// Schreibvarianten derselben Währung als EINE zählen.
    /// </summary>
    private static IEnumerable<(string Label, uint IconId)> GetAllCurrencies(CollectibleEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Currency))
            yield return (CanonicalizeCurrencyLabel(GetCurrencyLabel(entry.Currency)), entry.CurrencyIconId);

        if (entry.AdditionalCurrencies != null)
        {
            foreach (var additional in entry.AdditionalCurrencies)
            {
                if (!string.IsNullOrEmpty(additional.Currency))
                    yield return (CanonicalizeCurrencyLabel(GetCurrencyLabel(additional.Currency)), additional.CurrencyIconId);
            }
        }
    }

    private static IEnumerable<string> GetAllCurrencyLabels(CollectibleEntry entry) => GetAllCurrencies(entry).Select(c => c.Label);

    // Suchtext im "Currencys filtern"-Popup - bleibt über mehrere Frames erhalten (Popup öffnen,
    // tippen, wieder schließen), wird beim erneuten Öffnen bewusst NICHT zurückgesetzt.
    private string currencyFilterSearch = string.Empty;

    /// <summary>
    /// Inhalt des "Currencys filtern"-Popups - Suchfeld + Checkbox-Liste ALLER im Plugin bekannten
    /// Währungen (per GetCurrencyLabel-Kurzname, z.B. "MGP" statt "150.000 MGP"), unabhängig von der
    /// aktuellen Zone (CollectionData.GetAllEntries() ist die vollständige, einmal geladene
    /// Gesamtliste). Eine abgewählte Währung blendet ALLE Einträge mit genau dieser Währung aus dem
    /// Overlay aus (siehe afterCurrencyFilter in DrawContent), unabhängig vom Typ.
    /// </summary>
    private void DrawCurrencyFilterPopupContent()
    {
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##CompactCurrencyFilterSearch", Loc.T("Suchen...", "Search..."), ref currencyFilterSearch, 64);

        var allCurrencies = CollectionData.GetAllEntries()
            .SelectMany(GetAllCurrencies)
            .Where(c => !string.IsNullOrEmpty(c.Label))
            .GroupBy(c => c.Label)
            // Pro Label bevorzugt einen Vertreter MIT bekanntem Icon (mehrere Einträge derselben
            // Währung können unterschiedlich vollständige Icon-Daten haben) - sonst irgendeinen.
            .Select(g => (Label: g.Key, IconId: g.Select(c => c.IconId).FirstOrDefault(id => id != 0)))
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Where(c => string.IsNullOrEmpty(currencyFilterSearch) || c.Label.Contains(currencyFilterSearch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var iconSize = ImGui.GetTextLineHeight();
        ImGui.BeginChild("CompactCurrencyFilterList", new Vector2(220f, 260f));
        foreach (var (label, iconId) in allCurrencies)
        {
            if (iconId != 0)
            {
                var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
                ImGui.Image(icon.Handle, new Vector2(iconSize));
                ImGui.SameLine();
            }

            var enabled = !plugin.Configuration.HiddenCurrencies.Contains(label);
            if (ImGui.Checkbox($"{label}##CompactCurrencyFilterEntry", ref enabled))
            {
                if (enabled)
                    plugin.Configuration.HiddenCurrencies.Remove(label);
                else
                    plugin.Configuration.HiddenCurrencies.Add(label);
                plugin.Configuration.Save();
            }
        }

        if (allCurrencies.Count == 0)
            OutlineText(Loc.T("Keine Treffer.", "No matches."), MutedColor);

        ImGui.EndChild();
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
    /// Gemeinsamer Tooltip-Text für JEDEN Automations-Knopf, sobald irgendein als "Required"
    /// markiertes Plugin fehlt (siehe MainWindow.HasMissingRequiredDependency) - bewusst pauschal
    /// statt pro Knopf ein anderes konkretes Plugin zu nennen: welches Plugin eine Automation
    /// tatsächlich braucht, ist ohnehin auf der Plugins-Seite ersichtlich.
    /// </summary>
    private static string MissingPluginTooltip => Loc.T(
        "Es fehlt mindestens ein benötigtes Plugin - siehe Plugins-Seite.",
        "At least one required plugin is missing - see the Plugins page.");

    /// <summary>
    /// Gemeinsamer Tooltip-Text für JEDEN Automations-Knopf, solange man sich in einem
    /// Instanz-Inhalt befindet (siehe Plugin.IsInInstancedContent) - dort funktionieren vnavmesh/
    /// die angesteuerten Fremdplugins ohnehin nicht sinnvoll.
    /// </summary>
    private static string InstancedContentTooltip => Loc.T(
        "In Instanz-Inhalten (Dungeon, Trial, Raid, ...) nicht verfügbar.",
        "Not available in instanced content (dungeon, trial, raid, ...).");

    /// <summary>
    /// Knopf, der die Questionable-Automation (siehe QuestAutomation.cs) für die aktuell
    /// fehlenden Quests dieser Zone an-/ausschaltet. Ausgegraut, sobald irgendein als "Required"
    /// markiertes Plugin fehlt (nicht nur Questionable selbst) - siehe MissingPluginTooltip.
    /// </summary>
    private void DrawQuestAutomationButton(bool hasActionableQuests, uint effectiveTerritoryId)
    {
        var automation = plugin.QuestAutomation;
        var label = automation.IsActive
            ? Loc.T("Automation stoppen", "Stop automation")
            : Loc.T("Auto Quest", "Auto Quest");

        var buttonSize = new Vector2(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f, ImGui.GetFrameHeight());
        if (IsOccluded(buttonSize))
        {
            ImGui.Dummy(buttonSize);
            return;
        }

        var hasMissingPlugin = MainWindow.HasMissingRequiredDependency();

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht. Fehlt ein Plugin, gibt es
        // aber unabhängig davon nichts sinnvoll zu starten, also trotzdem ausgrauen.
        var inInstancedContent = Plugin.IsInInstancedContent();
        var isDisabled = !automation.IsActive && (!hasActionableQuests || hasMissingPlugin || inInstancedContent);

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.Quest]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactQuestAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hasMissingPlugin
                ? MissingPluginTooltip
                : inInstancedContent
                    ? InstancedContentTooltip
                    : isDisabled
                        ? Loc.T("Keine von Questionable unterstützten Quests in dieser Zone.", "No quests supported by Questionable in this zone.")
                        : automation.IsActive
                        ? Loc.T("Bricht die aktuelle Quest sofort ab und stoppt die Automation.", "Immediately cancels the current quest and stops the automation.")
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
        else if (!hasMissingPlugin)
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
            : Loc.T("Auto Aetheryte", "Auto Aetheryte");

        var buttonSize = new Vector2(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f, ImGui.GetFrameHeight());
        if (IsOccluded(buttonSize))
        {
            ImGui.Dummy(buttonSize);
            return;
        }

        var hasMissingPlugin = MainWindow.HasMissingRequiredDependency();

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht. Fehlt ein Plugin, gibt es
        // aber unabhängig davon nichts zu starten, also trotzdem ausgrauen.
        var inInstancedContent = Plugin.IsInInstancedContent();
        var isDisabled = !automation.IsActive && (!hasActionableAetherytes || hasMissingPlugin || inInstancedContent);

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.Aetheryte]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactAetheryteAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hasMissingPlugin
                ? MissingPluginTooltip
                : inInstancedContent
                    ? InstancedContentTooltip
                    : isDisabled
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
        else if (!hasMissingPlugin)
        {
            automation.Start();
        }
        else
        {
            automation.MarkUnavailable();
        }
    }

    /// <summary>
    /// Knopf, der die Hunting-Log-Kill-Automation (siehe HuntingLogAutomation.cs) für die aktuell
    /// fehlenden Ziele (aktive Klasse/aktiver Rang) dieser Zone an-/ausschaltet. Braucht zum Laufen
    /// zwingend vnavmesh - fehlt es, wird das per Tooltip erklärt statt der Knopf einfach nichts zu
    /// tun. RotationSolver Reborn wird zum Kämpfen nur per Chat-Befehl/Best-Effort-IPC angesteuert
    /// (siehe HuntingLogAutomation.IsRotationSolverAvailable), ist also kein hartes Gate mehr.
    /// </summary>
    private void DrawHuntingLogAutomationButton(bool hasActionableHuntingLog)
    {
        var automation = plugin.HuntingLogAutomation;
        var label = automation.IsActive
            ? Loc.T("Automation stoppen", "Stop automation")
            : Loc.T("Auto Hunting Log", "Auto Hunting Log");

        var buttonSize = new Vector2(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f, ImGui.GetFrameHeight());
        if (IsOccluded(buttonSize))
        {
            ImGui.Dummy(buttonSize);
            return;
        }

        var hasMissingPlugin = MainWindow.HasMissingRequiredDependency();

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht. Fehlt ein Plugin, gibt es
        // aber unabhängig davon nichts zu starten, also trotzdem ausgrauen.
        var inInstancedContent = Plugin.IsInInstancedContent();
        var isDisabled = !automation.IsActive && (!hasActionableHuntingLog || hasMissingPlugin || inInstancedContent);

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.HuntingLog]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactHuntingLogAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hasMissingPlugin
                ? MissingPluginTooltip
                : inInstancedContent
                    ? InstancedContentTooltip
                    : isDisabled
                        ? Loc.T(
                            "Keine Hunting-Log-Ziele mit bekannter Position in dieser Zone.",
                            "No hunting log targets with a known position in this zone.")
                        : automation.IsActive
                        ? Loc.T(
                            "Stoppt die Automation - ein laufender Kampf wird noch zu Ende gebracht, statt den Charakter wehrlos stehen zu lassen.",
                            "Stops the automation - an ongoing fight is finished first instead of leaving the character defenseless.")
                        : Loc.T(
                            "Läuft mit vnavmesh nacheinander alle fehlenden Hunting-Log-Ziele ab und tötet sie mit RotationSolver Reborn.",
                            "Uses vnavmesh to walk to all missing hunting log targets, one by one, and kills them with RotationSolver Reborn."));
        }

        if (!clicked)
            return;

        if (automation.IsActive)
        {
            automation.Stop();
        }
        else if (!hasMissingPlugin)
        {
            automation.Start();
        }
        else
        {
            automation.MarkUnavailable();
        }
    }

    /// <summary>
    /// Knopf, der die Ätherströmungs-Automation (siehe AetherCurrentAutomation.cs) für die aktuell
    /// fehlenden Strömungen dieser Zone an-/ausschaltet. Braucht zum Laufen zwingend vnavmesh -
    /// fehlt es, wird das per Tooltip erklärt statt der Knopf einfach nichts zu tun.
    /// </summary>
    private void DrawAetherCurrentAutomationButton(bool hasActionableAetherCurrents)
    {
        var automation = plugin.AetherCurrentAutomation;
        var label = automation.IsActive
            ? Loc.T("Automation stoppen", "Stop automation")
            : Loc.T("Auto Ätherströmung", "Auto Aether Current");

        var buttonSize = new Vector2(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f, ImGui.GetFrameHeight());
        if (IsOccluded(buttonSize))
        {
            ImGui.Dummy(buttonSize);
            return;
        }

        var hasMissingPlugin = MainWindow.HasMissingRequiredDependency();

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht. Fehlt ein Plugin, gibt es
        // aber unabhängig davon nichts zu starten, also trotzdem ausgrauen.
        var inInstancedContent = Plugin.IsInInstancedContent();
        var isDisabled = !automation.IsActive && (!hasActionableAetherCurrents || hasMissingPlugin || inInstancedContent);

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.AetherCurrent]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactAetherCurrentAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hasMissingPlugin
                ? MissingPluginTooltip
                : inInstancedContent
                    ? InstancedContentTooltip
                    : isDisabled
                        ? Loc.T(
                            "Keine Ätherströmungen mit bekannter Position in dieser Zone.",
                            "No aether currents with a known position in this zone.")
                    : automation.IsActive
                        ? Loc.T("Bricht die Laufbewegung sofort ab und stoppt die Automation.", "Immediately stops movement and the automation.")
                        : Loc.T(
                            "Läuft mit vnavmesh nacheinander alle fehlenden Ätherströmungen ab und wartet auf die automatische Freischaltung.",
                            "Uses vnavmesh to walk to all missing aether currents, one by one, and waits for them to unlock automatically."));
        }

        if (!clicked)
            return;

        if (automation.IsActive)
        {
            automation.Stop();
        }
        else if (!hasMissingPlugin)
        {
            automation.Start();
        }
        else
        {
            automation.MarkUnavailable();
        }
    }

    /// <summary>
    /// Knopf, der die Sightseeing-Automation (siehe SightseeingAutomation.cs) für die aktuell
    /// fehlenden Punkte dieser Zone an-/ausschaltet. Prüfreihenfolge bewusst: erst ob das
    /// Sightseeing Log selbst überhaupt freigeschaltet ist (kein Plugin-Thema, betrifft nur ganz
    /// frische Charaktere), erst DANACH die Plugin-Verfügbarkeit (wie bei den anderen Knöpfen) -
    /// siehe Tooltip-Reihenfolge unten.
    /// </summary>
    private void DrawSightseeingAutomationButton(bool hasActionableSightseeing)
    {
        var automation = plugin.SightseeingAutomation;
        var label = automation.IsActive
            ? Loc.T("Automation stoppen", "Stop automation")
            : Loc.T("Auto Sightseeing", "Auto Sightseeing");

        var buttonSize = new Vector2(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f, ImGui.GetFrameHeight());
        if (IsOccluded(buttonSize))
        {
            ImGui.Dummy(buttonSize);
            return;
        }

        // Kein Plugin-Thema - eigenständig VOR hasMissingPlugin geprüft (siehe Tooltip unten).
        var logUnlocked = Plugin.IsSightseeingLogUnlocked();
        var hasMissingPlugin = MainWindow.HasMissingRequiredDependency();

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht. Fehlt ein Plugin, gibt es
        // aber unabhängig davon nichts zu starten, also trotzdem ausgrauen.
        var inInstancedContent = Plugin.IsInInstancedContent();
        var isDisabled = !automation.IsActive && (!logUnlocked || !hasActionableSightseeing || hasMissingPlugin || inInstancedContent);

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.Sightseeing]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactSightseeingAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(!logUnlocked
                ? Loc.T("Sightseeing Log noch nicht freigeschaltet.", "Sightseeing Log not unlocked yet.")
                : hasMissingPlugin
                    ? MissingPluginTooltip
                    : inInstancedContent
                        ? InstancedContentTooltip
                        : isDisabled
                            ? Loc.T(
                                "Aktuell kein Sightseeing-Punkt in dieser Zone erreichbar (keine bekannte Position, oder Wetter/Uhrzeit passt gerade nicht).",
                                "No sightseeing point currently reachable in this zone (no known position, or the weather/time doesn't match right now).")
                            : automation.IsActive
                            ? Loc.T("Bricht die Laufbewegung sofort ab und stoppt die Automation.", "Immediately stops movement and the automation.")
                            : Loc.T(
                                "Läuft mit vnavmesh nacheinander alle fehlenden Sightseeing-Punkte ab und wartet auf die automatische Freischaltung.",
                                "Uses vnavmesh to walk to all missing sightseeing points, one by one, and waits for them to unlock automatically."));
        }

        if (!clicked)
            return;

        if (automation.IsActive)
        {
            automation.Stop();
        }
        else if (!hasMissingPlugin)
        {
            automation.Start();
        }
        else
        {
            automation.MarkUnavailable();
        }
    }

    /// <summary>
    /// Knopf, der die Chocobokeep-Automation (siehe ChocobokeepAutomation.cs) für die aktuell noch
    /// nicht besuchten Chocobokeep-Standorte dieser Zone an-/ausschaltet. Braucht zum Laufen
    /// zwingend vnavmesh - fehlt es, wird das per Tooltip erklärt statt der Knopf einfach nichts zu tun.
    /// </summary>
    private void DrawChocobokeepAutomationButton(bool hasActionableChocobokeeps)
    {
        var automation = plugin.ChocobokeepAutomation;
        var label = automation.IsActive
            ? Loc.T("Automation stoppen", "Stop automation")
            : Loc.T("Auto Chocobokeep", "Auto Chocobokeep");

        var buttonSize = new Vector2(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f, ImGui.GetFrameHeight());
        if (IsOccluded(buttonSize))
        {
            ImGui.Dummy(buttonSize);
            return;
        }

        var hasMissingPlugin = MainWindow.HasMissingRequiredDependency();

        // Nur ausgrauen, wenn NICHT aktiv - läuft sie schon, muss der Knopf zum Stoppen klickbar
        // bleiben, auch falls die Liste inzwischen (kurz) leer aussieht. Fehlt ein Plugin, gibt es
        // aber unabhängig davon nichts zu starten, also trotzdem ausgrauen.
        var inInstancedContent = Plugin.IsInInstancedContent();
        var isDisabled = !automation.IsActive && (!hasActionableChocobokeeps || hasMissingPlugin || inInstancedContent);

        PushAutomationButtonColors(automation.IsActive, TypeColors[CollectibleType.Chocobokeep]);
        if (isDisabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label + "##CompactChocobokeepAutomation");
        if (isDisabled)
            ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hasMissingPlugin
                ? MissingPluginTooltip
                : inInstancedContent
                    ? InstancedContentTooltip
                    : isDisabled
                        ? Loc.T(
                            "Keine noch nicht besuchten Chocobokeeps in dieser Zone.",
                            "No unvisited chocobokeeps in this zone.")
                        : automation.IsActive
                        ? Loc.T("Bricht die Laufbewegung sofort ab und stoppt die Automation.", "Immediately stops movement and the automation.")
                        : Loc.T(
                            "Läuft mit vnavmesh nacheinander alle noch nicht besuchten Chocobokeeps ab und interagiert mit ihnen.",
                            "Uses vnavmesh to walk to all not-yet-visited chocobokeeps, one by one, and interacts with them."));
        }

        if (!clicked)
            return;

        if (automation.IsActive)
        {
            automation.Stop();
        }
        else if (!hasMissingPlugin)
        {
            automation.Start();
        }
        else
        {
            automation.MarkUnavailable();
        }
    }

    private void DrawClickableName(CollectibleEntry entry, bool isNotYetPossible = false)
    {
        var affordable = plugin.CanAfford(entry);
        var allaganToolsEnabled = plugin.Configuration.EnableAllaganToolsIntegration
                                   && Plugin.IsAllaganToolsAvailable()
                                   && AllaganToolsEligibleTypes.Contains(entry.Type);

        // Sowohl Kartenkoordinaten-Einträge (Händler/Aetheryten/Quest-NPCs) als auch Hunting-Log-
        // Monster mit bekannter Weltposition (siehe WorldPosition) bekommen denselben klickbaren
        // "Auf Karte anzeigen"-Namen - siehe Plugin.OpenEntryMap, das beide Positionsarten
        // einheitlich behandelt. Das "Hinlaufen"-Icon selbst sitzt nicht mehr hier, sondern ganz
        // vorne in der Zeile (siehe DrawGoToColumn).
        if (!entry.HasGoToTarget)
        {
            OutlineText(entry.Name, isNotYetPossible ? NotYetPossibleColor : affordable ? AffordableColor : NormalColor);

            // Ohne Kartenziel normalerweise gar nicht interaktiv - außer für SHIFT + Linksklick
            // (Allagan Tools, siehe Plugin.OpenAllaganToolsItemInfo), falls aktiviert.
            if (allaganToolsEnabled)
            {
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(Loc.T("SHIFT + Klick: Mehr Informationen (Allagan Tools)", "SHIFT + click: more information (Allagan Tools)"));
                }

                if (ImGui.IsItemClicked() && ImGui.GetIO().KeyShift)
                    Plugin.OpenAllaganToolsItemInfo(entry.Name);
            }

            return;
        }

        OutlineText(entry.Name, isNotYetPossible ? NotYetPossibleColor : affordable ? AffordableColor : VendorLinkColor);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var mapTooltip = string.IsNullOrEmpty(entry.Vendor)
                ? Loc.T("Auf Karte anzeigen", "Show on map")
                : Loc.T($"Bei {entry.Vendor} - Auf Karte anzeigen", $"From {entry.Vendor} - show on map");
            ImGui.SetTooltip(allaganToolsEnabled
                ? $"{mapTooltip}\n{Loc.T("SHIFT + Klick: Mehr Informationen (Allagan Tools)", "SHIFT + click: more information (Allagan Tools)")}"
                : mapTooltip);
        }

        if (ImGui.IsItemClicked())
        {
            if (allaganToolsEnabled && ImGui.GetIO().KeyShift)
                Plugin.OpenAllaganToolsItemInfo(entry.Name);
            else
                Plugin.OpenEntryMap(entry);
        }
    }

    /// <summary>
    /// Ganz vorne in jeder Zeile (vor dem [Typ]-Tag) statt des früheren Aufzählungspunkts - zeigt
    /// das "Hinlaufen"-Icon (siehe DrawGoToIcon), oder wenn keins gezeigt wird, weil DIESER Eintrag
    /// kein Laufziel hat (Einstellung aber an), einen gleich breiten Platzhalter, damit der [Typ]-Tag
    /// in jeder Zeile an derselben X-Position beginnt. Ist die Einstellung GLOBAL aus, wird gar keine
    /// Spalte reserviert - dann rutscht der [Typ]-Tag ganz an den Zeilenanfang, statt eine für immer
    /// leere Lücke stehen zu lassen.
    /// </summary>
    private void DrawGoToColumn(CollectibleEntry entry)
    {
        if (!plugin.Configuration.ShowGoToIcon)
            return;

        if (entry.HasGoToTarget)
        {
            DrawGoToIcon(entry);
        }
        else
        {
            float width;
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                width = ImGui.CalcTextSize(FontAwesomeIcon.Running.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;
            ImGui.Dummy(new Vector2(width, 1f));
        }

        ImGui.SameLine();
    }

    /// <summary>
    /// "Hinlaufen"-Icon - startet bzw. bricht per Klick GoToAutomation für GENAU DIESEN Eintrag ab
    /// (immer nur einer gleichzeitig, siehe GoToAutomation.GoTo). Ausgegraut, solange vnavmesh/
    /// Lifestream nicht beide verfügbar sind - ohne beide könnte der Klick ohnehin nicht
    /// zuverlässig ans Ziel führen.
    /// </summary>
    private void DrawGoToIcon(CollectibleEntry entry)
    {
        var automation = plugin.GoToAutomation;
        var isThisEntryActive = automation.IsNavigatingTo(entry);
        var available = automation.IsAvailable();

        bool clicked;
        bool hovered;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var icon = isThisEntryActive ? FontAwesomeIcon.StopCircle : FontAwesomeIcon.Running;
            var color = !available ? MutedColor : isThisEntryActive ? GoToActiveColor : AffordableColor;

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            if (!available)
                ImGui.BeginDisabled();

            // Typ mit in die ImGui-ID einbezogen, nicht nur die entry.Id - verschiedene
            // Sammelobjekt-Datenquellen (Mount/Minion/Aetheryte/Quest/HuntingLog) vergeben ihre IDs
            // unabhängig voneinander, fangen also alle wieder bei 1 an. Ohne den Typ hier hätten
            // z.B. Mount-Eintrag #1 und Hunting-Log-Ziel #1 (RowId aus MonsterNoteTarget) dieselbe
            // ImGui-ID gehabt - dadurch reagierte der Klick auf keinem der beiden mehr zuverlässig.
            clicked = ImGui.SmallButton($"{icon.ToIconString()}##GoTo{entry.Type}{entry.Id}");

            if (!available)
                ImGui.EndDisabled();
            ImGui.PopStyleColor();

            // Hover NUR hier feststellen, das eigentliche SetTooltip (siehe unten) muss außerhalb
            // dieses using-Blocks passieren: Solange die Icon-Schriftart (FontAwesome) noch aktiv
            // ist, würde der normale Tooltip-Text als Icon-Glyphen (also "komische Zeichen") statt
            // als lesbarer Text gerendert.
            hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        }

        if (hovered)
        {
            ImGui.SetTooltip(!available
                ? Loc.T("vnavmesh/Lifestream nicht gefunden - bitte installieren.", "vnavmesh/Lifestream not found - please install them.")
                : isThisEntryActive
                    ? Loc.T("Hinlaufen abbrechen", "Cancel walking there")
                    : Loc.T("Automatisch hinlaufen", "Automatically walk there"));
        }

        if (clicked && available)
        {
            if (isThisEntryActive)
                automation.Cancel();
            else
                automation.GoTo(entry);
        }
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

    /// <summary>
    /// Schloss-Icon links neben dem Einklapp-Knopf - sperrt/entsperrt dieselbe Einstellung wie
    /// "Fenster sperren" im Optionsfenster (config.CompactLocked), nur direkt im Overlay erreichbar,
    /// ohne dafür extra die Optionen öffnen zu müssen. Drittes (linkestes) von drei Knöpfen ganz
    /// rechts - siehe DrawCollapseButtonTopRight/DrawCloseButtonTopRight.
    /// </summary>
    private bool DrawLockButtonTopRight(bool locked, float buttonSize, float rightMargin)
    {
        var icon = locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
        var regionMaxX = ImGui.GetWindowContentRegionMax().X - rightMargin;
        ImGui.SameLine(regionMaxX - buttonSize * 3f - ImGui.GetStyle().ItemSpacing.X * 2f);

        var size = new Vector2(buttonSize, buttonSize);
        if (IsOccluded(size))
        {
            ImGui.Dummy(size);
            return false;
        }

        bool clicked;
        ImGui.PushStyleColor(ImGuiCol.Text, locked ? GoToActiveColor : AffordableColor);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            clicked = ImGui.Button($"{icon.ToIconString()}##LockCompact", size);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(locked
                ? Loc.T("Fenster entsperren", "Unlock window")
                : Loc.T("Fenster sperren (Position fixieren)", "Lock window (fix position)"));
        }

        return clicked;
    }

    /// <summary>
    /// Ein-/Ausklapp-Knopf zwischen Schloss und Schließen-Knopf - blendet beim Einklappen alles
    /// außer der Kopfzeile aus (siehe DrawContent/collapsed), analog zum Optionsfenster.
    /// </summary>
    private bool DrawCollapseButtonTopRight(bool isCollapsed, float buttonSize, float rightMargin)
    {
        var icon = isCollapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp;
        var regionMaxX = ImGui.GetWindowContentRegionMax().X - rightMargin;
        ImGui.SameLine(regionMaxX - buttonSize * 2f - ImGui.GetStyle().ItemSpacing.X);

        var size = new Vector2(buttonSize, buttonSize);
        if (IsOccluded(size))
        {
            ImGui.Dummy(size);
            return false;
        }

        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            clicked = ImGui.Button($"{icon.ToIconString()}##CollapseCompact", size);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(isCollapsed
                ? Loc.T("Ausklappen", "Expand")
                : Loc.T("Einklappen", "Collapse"));
        }

        return clicked;
    }

    private bool DrawCloseButtonTopRight(float buttonSize, float rightMargin)
    {
        var regionMaxX = ImGui.GetWindowContentRegionMax().X - rightMargin;
        ImGui.SameLine(regionMaxX - buttonSize);

        var size = new Vector2(buttonSize, buttonSize);
        if (IsOccluded(size))
        {
            ImGui.Dummy(size);
            return false;
        }

        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            clicked = ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##CloseCompact", size);

        return clicked;
    }

    private static readonly Vector4 TitleColor = new(0.55f, 0.8f, 1f, 1f);
    private static readonly Vector4 MutedColor = new(0.75f, 0.75f, 0.75f, 1f);
    private static readonly Vector4 NormalColor = new(0.92f, 0.92f, 0.92f, 1f);
    private static readonly Vector4 VendorLinkColor = new(0.5f, 0.8f, 1f, 1f);
    private static readonly Vector4 AffordableColor = new(0.55f, 0.95f, 0.55f, 1f);
    private static readonly Vector4 UnsupportedColor = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 NotYetPossibleColor = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 GoToActiveColor = new(1f, 0.65f, 0.2f, 1f);

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
        [CollectibleType.FrameKit] = new(0.55f, 0.55f, 0.95f, 1f),
        [CollectibleType.Hairstyle] = new(0.9f, 0.7f, 0.9f, 1f),
        [CollectibleType.Aetheryte] = new(0.6f, 1f, 0.75f, 1f),
        [CollectibleType.Quest] = new(1f, 0.9f, 0.5f, 1f),
        [CollectibleType.HuntingLog] = new(0.68f, 0.45f, 0.95f, 1f),
        [CollectibleType.AetherCurrent] = new(0.65f, 0.95f, 1f, 1f),
        [CollectibleType.Sightseeing] = new(1f, 0.8f, 0.4f, 1f),
        [CollectibleType.Chocobokeep] = new(0.95f, 0.85f, 0.2f, 1f),
    };

    // Typen, deren Name tatsächlich einem echten Item-Sheet-Eintrag entspricht, den Allagan Tools'
    // "/moreinfo"-Befehl (siehe Plugin.OpenAllaganToolsItemInfo) per Namenssuche finden kann - für
    // SHIFT + Linksklick (siehe DrawClickableName/DrawCurrencyRequirement). Quest/Sightseeing/
    // Aetheryte/HuntingLog/AetherCurrent/Chocobokeep sind keine Items, und FrameKit/Hairstyle tragen
    // nur den Namen des Rahmens/der Frisur, nicht des freischaltenden Items - für all diese würde
    // die Namenssuche ohnehin nur "nicht gefunden" liefern.
    private static readonly HashSet<CollectibleType> AllaganToolsEligibleTypes = new()
    {
        CollectibleType.Mount,
        CollectibleType.Minion,
        CollectibleType.Orchestrion,
        CollectibleType.Barding,
        CollectibleType.Emote,
        CollectibleType.Facewear,
        CollectibleType.FashionAccessory,
        CollectibleType.TripleTriadCard,
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
    /// letzte Zeichenaufruf an der eigentlichen Cursor-Position passiert. Liegt die Stelle gerade
    /// unter einem nativen Fenster (siehe IsOccluded), wird stattdessen ein gleich großer Dummy
    /// gezeichnet - IsItemHovered/IsItemClicked danach liefern dann automatisch immer false, ein
    /// verdeckter Text kann also nie mehr fälschlich als "angeklickt" gelten.
    /// </summary>
    private void OutlineText(string text, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        if (IsOccluded(size))
        {
            ImGui.Dummy(size);
            return;
        }

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
