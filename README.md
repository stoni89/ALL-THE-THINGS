# All The Things

Dalamud-Plugin für Final Fantasy XIV, das anzeigt, welche Sammelobjekte in der
aktuellen Zone noch fehlen – inspiriert von "All the Things" aus World of Warcraft.

## Features

- **Unterstützte Kategorien:** Mounts, Minions, Orchestrionrollen, Bardings,
  Emotes, Facewear (Brillen), Fashion Accessories und Triple-Triad-Karten.
- **Kompaktes Overlay** – schlankes, randloses Zusatzfenster, das nur fehlende
  Sammelobjekte der aktuellen Zone zeigt. Frei positionierbar, skalierbar und
  in der Transparenz einstellbar.
- **Händler-Kartenlinks** – klickbare Händlernamen öffnen die Ingame-Karte mit
  einer Flagge an der recherchierten NPC-Position.
- **Preis- & Währungsanzeige** – Preis inkl. Icon je Eintrag, plus eine
  Übersicht der aktuell besessenen Menge der benötigten Währungen im Overlay.
- **Leistbarkeits-Filter** – zeigt optional nur Sammelobjekte, für die man
  aktuell genug von der jeweiligen Währung besitzt.
- **Typ-Filter & Sortierung** – jede Kategorie einzeln ein-/ausblendbar, mit
  frei einstellbarer Reihenfolge im Overlay.
- **Deutsch/Englisch** – Oberfläche folgt automatisch der im Spielclient
  eingestellten Sprache (Deutsch, sonst Englisch als Fallback).

## Setup (Entwicklung)

1. **XIVLauncher + Dalamud Dev-Umgebung**
   - Dalamud-Testing/Plugin-Entwicklung in XIVLauncher aktivieren.
   - Standardpfad der Dalamud-Dev-DLLs: `%AppData%\XIVLauncher\addon\Hooks\dev\`

2. **Voraussetzungen**
   - .NET 10 SDK
   - `Dalamud.NET.Sdk` (wird automatisch über NuGet aufgelöst, `nuget.org` muss
     als Paketquelle konfiguriert sein: `dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org`)

3. **Bauen**
   ```
   dotnet build
   ```

4. **Im Spiel laden**
   - `/xlsettings` → "Dev Plugin Locations" → Pfad zur gebauten
     `bin\Debug\AllTheThings.dll` eintragen.
   - `/xlplugins` → "Dev Plugins" → Plugin laden.
   - `/att` öffnet das Optionen-Fenster.

## Projektstruktur

- `Plugin.cs` – Einstiegspunkt, Service-Injection, Besitz-Status-Prüfung pro Typ
- `Configuration.cs` – gespeicherte Plugin-Einstellungen
- `CollectionData.cs` – Datenmodell + Laden der JSON-Datendateien
- `Localization.cs` – einfache Deutsch/Englisch-Übersetzungshilfe
- `Windows/MainWindow.cs` – Optionen-Fenster
- `Windows/CompactOverlayWindow.cs` – kompaktes Overlay-Fenster
- `Data/*.json` – Sammelobjekt-Daten pro Kategorie (Name, Zone, Händler,
  Kartenkoordinaten, Preis/Währung), aufgebaut aus öffentlichen Community-Quellen
  (u. a. FFXIV Collect) und Wiki-Recherchen für Händlerstandorte.

## Bekannte Lücken

- **Framer's Kits (Portrait-Rahmen)** werden nicht unterstützt: FFXIV Collect
  liefert dafür keine Fundort-Daten, und die spielinterne ID für die
  Besitz-Status-Prüfung ist nicht direkt aus der FFXIV-Collect-ID ableitbar.
- Nicht jedes Sammelobjekt hat eine Zonen-/Händler-/Preis-Zuordnung – vieles
  ist an Achievements, Echtgeld-Shop, PvP o. Ä. gebunden und daher nicht
  zonengebunden.
