using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TheExplorersCodex;

/// <summary>
/// Liest die auf dem System installierten TrueType-/OpenType-Schriften aus der Registry aus,
/// damit sie als Auswahl für das kompakte Overlay angeboten werden können.
/// </summary>
public static class WindowsFonts
{
    private static List<(string Name, string Path)>? cached;

    public static List<(string Name, string Path)> GetInstalledFonts()
    {
        if (cached != null)
            return cached;

        var result = new List<(string Name, string Path)>();
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        ReadRegistryFonts(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", fontsDir, result);
        ReadRegistryFonts(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", fontsDir, result);

        cached = result
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cached;
    }

    private static void ReadRegistryFonts(RegistryKey root, string keyPath, string fontsDir, List<(string Name, string Path)> result)
    {
        using var key = root.OpenSubKey(keyPath);
        if (key == null)
            return;

        foreach (var valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is not string fileValue || fileValue.Length == 0)
                continue;

            var extension = Path.GetExtension(fileValue);
            if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                continue;

            var fullPath = Path.IsPathRooted(fileValue) ? fileValue : Path.Combine(fontsDir, fileValue);
            if (!File.Exists(fullPath))
                continue;

            var displayName = valueName
                .Replace(" (TrueType)", string.Empty)
                .Replace(" (OpenType)", string.Empty)
                .Trim();

            result.Add((displayName, fullPath));
        }
    }
}
