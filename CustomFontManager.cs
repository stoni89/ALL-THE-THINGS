using System.Collections.Generic;
using Dalamud.Interface.ManagedFontAtlas;

namespace AllTheThings;

/// <summary>
/// Lädt beliebige TrueType-/OpenType-Dateien (z.B. installierte Windows-Schriften) dynamisch
/// in Dalamuds Font-Atlas und hält sie zwischengespeichert, damit ein Wechsel zurück zu einer
/// bereits geladenen Schrift keinen erneuten Ladevorgang auslöst.
/// </summary>
public static class CustomFontManager
{
    private static readonly Dictionary<string, IFontHandle> Handles = new();

    public static IFontHandle? GetOrCreate(string fontPath)
    {
        if (string.IsNullOrEmpty(fontPath))
            return null;

        if (Handles.TryGetValue(fontPath, out var existing))
            return existing;

        var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;
        var handle = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var config = new SafeFontConfig { SizePx = Plugin.PluginInterface.UiBuilder.FontDefaultSizePx };
            tk.Font = tk.AddFontFromFile(fontPath, config);
        }));

        Handles[fontPath] = handle;
        return handle;
    }
}
