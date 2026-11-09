using Hearth.Core.Models;

namespace Hearth.UI.State;

/// <summary>A built-in colour theme: CSS custom property values for dark and light mode. Empty sets mean
/// "use the defaults in hearth.css".</summary>
public sealed record ThemePalette(string Id, string Name, string Description,
    IReadOnlyDictionary<string, string> Dark, IReadOnlyDictionary<string, string> Light)
{
    /// <summary>Colours for the picker's preview: background, panel, accent (dark first, then light).</summary>
    public (string bg, string panel, string accent) Swatch(bool light)
    {
        var vars = light ? Light : Dark;
        var defaults = light ? ThemeCatalog.LightDefaults : ThemeCatalog.DarkDefaults;
        string Get(string name) => vars.TryGetValue(name, out var v) ? v : defaults[name];
        return (Get("--bg"), Get("--bg-2"), Get("--accent"));
    }
}

/// <summary>The themes people can pick in Settings → Appearance. Applied by hearth.js, which sets these
/// properties on the page's root element.</summary>
public static class ThemeCatalog
{
    public const string DefaultId = "hearth";

    // Mirrors the defaults in hearth.css (used for previews only).
    internal static readonly Dictionary<string, string> DarkDefaults = new()
    {
        ["--bg"] = "#0e0f11", ["--bg-2"] = "#15161a", ["--accent"] = "#6366f1",
    };
    internal static readonly Dictionary<string, string> LightDefaults = new()
    {
        ["--bg"] = "#ffffff", ["--bg-2"] = "#f6f6f8", ["--accent"] = "#4f46e5",
    };

    private static Dictionary<string, string> Surfaces(string bg, string bg2, string bg3, string rail, string line,
        string lineStrong, string text, string muted) => new()
    {
        ["--bg"] = bg, ["--bg-2"] = bg2, ["--bg-3"] = bg3, ["--bg-rail"] = rail,
        ["--line"] = line, ["--line-strong"] = lineStrong, ["--text"] = text, ["--muted"] = muted,
    };

    private static Dictionary<string, string> With(this Dictionary<string, string> vars, string brand, string accent,
        string accentText = "#ffffff")
    {
        vars["--brand"] = brand; vars["--accent"] = accent; vars["--accent-text"] = accentText;
        return vars;
    }

    private static Dictionary<string, string> Accent(string brand, string accent, string accentText = "#ffffff")
        => new Dictionary<string, string>().With(brand, accent, accentText);

    public static readonly IReadOnlyList<ThemePalette> All =
    [
        new(DefaultId, "Hearth", "Indigo on neutral greys", new Dictionary<string, string>(), new Dictionary<string, string>()),
        new("ocean", "Ocean", "Cool blues",
            Surfaces("#0b1118", "#111a24", "#1a2633", "#081018", "#1f2c3a", "#2c3e52", "#e6eef7", "#8fa1b5").With("#5aa9ff", "#2f80ed"),
            Surfaces("#ffffff", "#f3f7fb", "#e6eef6", "#eaf1f8", "#d9e3ee", "#c3d1e0", "#122030", "#5a6b7e").With("#1f6fd6", "#1e6fe0")),
        new("forest", "Forest", "Calm greens",
            Surfaces("#0c110e", "#121915", "#1b241e", "#090d0b", "#223028", "#304237", "#e7efe9", "#91a297").With("#4fd18b", "#1f9d61"),
            Surfaces("#ffffff", "#f4f8f5", "#e7efe9", "#ebf2ed", "#dae5dd", "#c3d3c8", "#14201a", "#5b6b61").With("#18804f", "#178a53")),
        new("ember", "Ember", "Warm oranges",
            Surfaces("#120e0c", "#1a1512", "#251e1a", "#0e0b09", "#2e2621", "#40352e", "#f3ebe6", "#a6988f").With("#ff9a5c", "#ea6a1f"),
            Surfaces("#fffdfb", "#faf5f1", "#f1e9e3", "#f5eee8", "#e8ddd4", "#d7c8bc", "#24180f", "#6f6157").With("#c4520f", "#d9580f")),
        new("rose", "Rose", "Soft pinks",
            Surfaces("#120d10", "#1a1317", "#251b21", "#0e0a0c", "#30232b", "#43313c", "#f4e9ee", "#a8939d").With("#ff7aa8", "#e0457b"),
            Surfaces("#fffcfd", "#fbf4f7", "#f3e7ed", "#f7eef2", "#ead9e1", "#dcc4cf", "#26131c", "#735d68").With("#c42f66", "#d13a70")),
        new("grape", "Grape", "Rich purples",
            Surfaces("#100d14", "#17131d", "#211b29", "#0c0a10", "#2a2234", "#3b3049", "#efe9f6", "#9d93ab").With("#b78cff", "#8b5cf6"),
            Surfaces("#fdfcff", "#f7f4fc", "#ede7f7", "#f1ecf9", "#e0d7ee", "#cdc0e2", "#1d1428", "#655a74").With("#7340d9", "#7c3aed")),
        new("graphite", "Graphite", "Monochrome, no colour",
            Surfaces("#0f0f0f", "#171717", "#212121", "#0a0a0a", "#292929", "#3a3a3a", "#ededed", "#9a9a9a").With("#e0e0e0", "#e6e6e6", "#111111"),
            Surfaces("#ffffff", "#f6f6f6", "#ececec", "#f0f0f0", "#e2e2e2", "#cdcdcd", "#141414", "#666666").With("#262626", "#1f1f1f")),
        new("paper", "Paper", "Warm cream and ink",
            Surfaces("#16130f", "#1e1a15", "#29241d", "#12100c", "#332d24", "#463e33", "#efe6d8", "#a5998a").With("#e0b264", "#c08a2e", "#1a1408"),
            Surfaces("#fbf7ef", "#f4eee2", "#ebe3d3", "#f1e9da", "#e2d8c4", "#cfc2aa", "#2a241a", "#6e6455").With("#8a5a12", "#96631a")),
        new("contrast", "High contrast", "Maximum readability",
            Surfaces("#000000", "#0a0a0a", "#1a1a1a", "#000000", "#5c5c5c", "#8a8a8a", "#ffffff", "#d4d4d4").With("#ffd60a", "#ffd60a", "#000000"),
            Surfaces("#ffffff", "#ffffff", "#eeeeee", "#f5f5f5", "#6b6b6b", "#3d3d3d", "#000000", "#2e2e2e").With("#0033cc", "#0033cc")),
    ];

    public static ThemePalette Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    /// <summary>What hearth.applyTheme needs: the mode, both property sets and the custom accent.</summary>
    public static object ForJs(ThemeSettings t)
    {
        var p = Find(t.Palette);
        return new
        {
            mode = t.Mode is "light" or "dark" ? t.Mode : "system",
            palette = p.Id,
            accent = IsHex(t.Accent) ? t.Accent : null,
            dark = p.Dark,
            light = p.Light,
        };
    }

    /// <summary>A stable string for "has the theme changed?" checks.</summary>
    public static string Key(ThemeSettings t) => $"{t.Mode}|{t.Palette}|{t.Accent}";

    public static bool IsHex(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
}
