namespace Hearth.Core.Models;

/// <summary>A built-in avatar picture: a small, original SVG character.</summary>
public sealed record BotPicture(string Id, string Label, string Svg);

/// <summary>
/// The built-in avatar pictures. Each one is drawn on a 100×100 canvas with its eyes in a <c>class="eyes"</c> group, so
/// the app can make them blink and look around. They carry no ids or scripts, so they can be drawn inline many times.
/// </summary>
public static class BotPictures
{
    /// <summary>Prefix for a built-in picture in <see cref="Bot.AvatarImage"/>, e.g. "builtin:fox".</summary>
    public const string Prefix = "builtin:";

    private const string Ink = "#2b2233";

    private static string I(FormattableString s) => FormattableString.Invariant(s);

    private static string Eyes(double lx, double rx, double y, double w = 5, double h = 6, string color = Ink) =>
        I($"""<g class="eyes"><ellipse cx="{lx}" cy="{y}" rx="{w}" ry="{h}" fill="{color}"/><ellipse cx="{rx}" cy="{y}" rx="{w}" ry="{h}" fill="{color}"/><circle cx="{lx + w * .38}" cy="{y - h * .38}" r="{w * .36}" fill="#fff"/><circle cx="{rx + w * .38}" cy="{y - h * .38}" r="{w * .36}" fill="#fff"/></g>""");

    private static string Blush(double lx, double rx, double y) =>
        I($"""<ellipse cx="{lx}" cy="{y}" rx="5.5" ry="3.2" fill="#ff8fa3" opacity=".6"/><ellipse cx="{rx}" cy="{y}" rx="5.5" ry="3.2" fill="#ff8fa3" opacity=".6"/>""");

    private static string Smile(double cx, double y, double w = 4) =>
        I($"""<path d="M{cx - w} {y} q{w} {w} {w * 2} 0" stroke="{Ink}" stroke-width="2.2" fill="none" stroke-linecap="round"/>""");

    /// <summary>A little "w" mouth.</summary>
    private static string Cat(double cx, double y) =>
        I($"""<path d="M{cx - 5} {y} q2.5 3 5 0 q2.5 3 5 0" stroke="{Ink}" stroke-width="2" fill="none" stroke-linecap="round"/>""");

    private static string Svg(string bg, string body) =>
        $"""<svg viewBox="0 0 100 100" xmlns="http://www.w3.org/2000/svg" aria-hidden="true"><rect width="100" height="100" fill="{bg}"/>{body}</svg>""";

    public static readonly IReadOnlyList<BotPicture> All =
    [
        new("owl", "Owl", Svg("#FFF1D6",
            """<path d="M22 40 L27 14 L42 30 Z M78 40 L73 14 L58 30 Z" fill="#B9875A"/>""" +
            """<ellipse cx="50" cy="62" rx="32" ry="34" fill="#B9875A"/><ellipse cx="50" cy="84" rx="17" ry="13" fill="#E8CBA3"/>""" +
            """<circle cx="37" cy="52" r="12.5" fill="#F6E3C6"/><circle cx="63" cy="52" r="12.5" fill="#F6E3C6"/>""" +
            Eyes(37, 63, 52, 5.5, 6.5) + """<path d="M46 61 L54 61 L50 67 Z" fill="#F5A623"/>""" + Blush(27, 73, 66) +
            """<circle cx="72" cy="84" r="5" fill="#F5B942"/><path d="M72 81 l1 2 2.2.3-1.6 1.5.4 2.2-2-1-2 1 .4-2.2-1.6-1.5 2.2-.3z" fill="#fff"/>""")),

        new("fox", "Fox", Svg("#E3F6E8",
            """<path d="M20 44 L27 12 L46 32 Z M80 44 L73 12 L54 32 Z" fill="#F08A3C"/><path d="M26 36 L29 20 L39 31 Z M74 36 L71 20 L61 31 Z" fill="#FFD9C0"/>""" +
            """<ellipse cx="50" cy="58" rx="31" ry="27" fill="#F08A3C"/><path d="M24 62 Q50 50 76 62 Q70 86 50 86 Q30 86 24 62 Z" fill="#FFF6EE"/>""" +
            Eyes(38, 62, 53) + """<ellipse cx="50" cy="64" rx="3.6" ry="2.6" fill="#2b2233"/>""" + Cat(50, 68) + Blush(29, 71, 66) +
            """<circle cx="76" cy="80" r="8.5" fill="#cfeff9" fill-opacity=".75" stroke="#5b6b7a" stroke-width="3"/><path d="M82 86 L89 93" stroke="#5b6b7a" stroke-width="4" stroke-linecap="round"/>""")),

        new("cat", "Builder cat", Svg("#E4EAFF",
            """<path d="M22 50 L25 22 L44 36 Z M78 50 L75 22 L56 36 Z" fill="#9AA5B8"/>""" +
            """<ellipse cx="50" cy="62" rx="31" ry="26" fill="#9AA5B8"/><ellipse cx="50" cy="71" rx="11" ry="7.5" fill="#E9EDF3"/>""" +
            Eyes(38, 62, 59) + """<path d="M48 66 h4 l-2 2.4 z" fill="#ff8fa3"/>""" + Cat(50, 70) + Blush(28, 72, 69) +
            """<path d="M18 66 h12 M18 72 l12 -2 M82 66 h-12 M82 72 l-12 -2" stroke="#6f7a8c" stroke-width="1.4" stroke-linecap="round"/>""" +
            """<path d="M24 41 Q50 6 76 41 Z" fill="#F7C948"/><rect x="18" y="38" width="64" height="7" rx="3.5" fill="#E9B22F"/><rect x="47" y="16" width="6" height="22" rx="3" fill="#E9B22F"/>""")),

        new("bunny", "Bunny", Svg("#FFE4EF",
            """<ellipse cx="38" cy="24" rx="8" ry="21" fill="#fff"/><ellipse cx="62" cy="24" rx="8" ry="21" fill="#fff"/>""" +
            """<ellipse cx="38" cy="25" rx="3.8" ry="15" fill="#FFC2D6"/><ellipse cx="62" cy="25" rx="3.8" ry="15" fill="#FFC2D6"/>""" +
            """<circle cx="50" cy="62" r="29" fill="#fff"/>""" + Eyes(39, 61, 59) +
            """<ellipse cx="50" cy="67" rx="3" ry="2.2" fill="#FF8FA3"/>""" + Cat(50, 70) + Blush(30, 70, 69) +
            """<g transform="rotate(-38 80 70)"><rect x="76" y="56" width="7" height="30" rx="1.5" fill="#F7C948"/><path d="M76 86 L79.5 93 L83 86 Z" fill="#F3D2A2"/><path d="M78.6 90.5 L79.5 93 L80.4 90.5 Z" fill="#2b2233"/><rect x="76" y="53" width="7" height="5" rx="1.5" fill="#FF8FA3"/></g>""")),

        new("raccoon", "Raccoon", Svg("#FFE3E3",
            """<circle cx="27" cy="33" r="10" fill="#8C8C99"/><circle cx="73" cy="33" r="10" fill="#8C8C99"/><circle cx="27" cy="33" r="5" fill="#E7C6C6"/><circle cx="73" cy="33" r="5" fill="#E7C6C6"/>""" +
            """<ellipse cx="50" cy="59" rx="32" ry="28" fill="#B3B3BF"/><path d="M20 55 Q35 44 50 54 Q65 44 80 55 Q70 64 50 60 Q30 64 20 55 Z" fill="#5E5E6B"/>""" +
            """<ellipse cx="50" cy="72" rx="14" ry="10" fill="#EFEFF4"/>""" + Eyes(37, 63, 54, 4.5, 5.5) +
            """<circle cx="37" cy="54" r="9.5" fill="none" stroke="#2b2233" stroke-width="2"/><circle cx="63" cy="54" r="9.5" fill="none" stroke="#2b2233" stroke-width="2"/><path d="M46.5 54 h7" stroke="#2b2233" stroke-width="2"/>""" +
            """<path d="M56 40 q7 -4 14 0" stroke="#2b2233" stroke-width="2" fill="none" stroke-linecap="round"/>""" +
            """<ellipse cx="50" cy="68" rx="3.4" ry="2.4" fill="#2b2233"/><path d="M45 75 q5 -2 10 0" stroke="#2b2233" stroke-width="2" fill="none" stroke-linecap="round"/>""" + Blush(26, 74, 68))),

        new("penguin", "Penguin", Svg("#DDF3FF",
            """<ellipse cx="50" cy="62" rx="31" ry="35" fill="#2F3A4F"/><path d="M50 36 Q30 36 28 58 Q28 88 50 90 Q72 88 72 58 Q70 36 50 36 Z" fill="#fff"/>""" +
            Eyes(40, 60, 54, 4.5, 5.5) + """<path d="M45.5 60 L54.5 60 L50 65.5 Z" fill="#F5A623"/>""" + Blush(32, 68, 64) +
            """<rect x="63" y="70" width="20" height="24" rx="3" fill="#C98B5A"/><rect x="66" y="75" width="14" height="16" rx="1.5" fill="#fff"/><rect x="69" y="68" width="8" height="5" rx="1.5" fill="#8C8C99"/>""" +
            """<path d="M68 80 l2 2 3 -4 M68 87 l2 2 3 -4" stroke="#4ade80" stroke-width="1.6" fill="none" stroke-linecap="round"/><path d="M75 80 h3 M75 87 h3" stroke="#b9b9c6" stroke-width="1.4"/>""")),

        new("frog", "Frog", Svg("#F0E4FF",
            """<circle cx="33" cy="42" r="12" fill="#7ED68A"/><circle cx="67" cy="42" r="12" fill="#7ED68A"/><ellipse cx="50" cy="64" rx="34" ry="25" fill="#7ED68A"/>""" +
            """<circle cx="33" cy="42" r="8" fill="#fff"/><circle cx="67" cy="42" r="8" fill="#fff"/>""" + Eyes(33, 67, 43, 4.5, 5) +
            """<path d="M34 66 Q50 80 66 66" stroke="#2b2233" stroke-width="2.4" fill="#ff8fa3" stroke-linecap="round" stroke-linejoin="round"/>""" + Blush(24, 76, 64) +
            """<path d="M50 9 L41 35 L59 35 Z" fill="#FF8FA3"/><path d="M47 18 L53 18 M44.5 26 L55.5 26" stroke="#fff" stroke-width="2.6"/><circle cx="50" cy="9" r="4" fill="#F7C948"/>""")),

        new("bear", "Bear", Svg("#FFEBD6",
            """<circle cx="27" cy="34" r="11" fill="#B07A4F"/><circle cx="73" cy="34" r="11" fill="#B07A4F"/><circle cx="27" cy="34" r="5.5" fill="#E8B48C"/><circle cx="73" cy="34" r="5.5" fill="#E8B48C"/>""" +
            """<circle cx="50" cy="60" r="31" fill="#B07A4F"/><ellipse cx="50" cy="70" rx="13" ry="10" fill="#EBCFAE"/>""" +
            Eyes(38, 62, 56) + """<ellipse cx="50" cy="66" rx="4.2" ry="3" fill="#2b2233"/>""" + Smile(50, 71, 3.5) + Blush(28, 72, 66))),

        new("panda", "Panda", Svg("#E3F5E1",
            """<circle cx="27" cy="34" r="11" fill="#33333d"/><circle cx="73" cy="34" r="11" fill="#33333d"/><circle cx="50" cy="60" r="31" fill="#fff"/>""" +
            """<ellipse cx="38" cy="57" rx="8.5" ry="10" transform="rotate(25 38 57)" fill="#44444f"/><ellipse cx="62" cy="57" rx="8.5" ry="10" transform="rotate(-25 62 57)" fill="#44444f"/>""" +
            Eyes(38.5, 61.5, 57, 3.6, 4.4, "#111") + """<ellipse cx="50" cy="67" rx="3.6" ry="2.6" fill="#2b2233"/>""" + Cat(50, 70) + Blush(27, 73, 69) +
            """<path d="M62 28 q6 -10 14 -6 q-4 8 -14 6 Z" fill="#7ED68A"/><path d="M62 28 q6 -5 14 -6" stroke="#4fae5b" stroke-width="1.2" fill="none"/>""")),

        new("chick", "Chick", Svg("#FFFBD1",
            """<path d="M46 26 q2 -10 6 -2 q4 -8 4 4" stroke="#F2B705" stroke-width="3" fill="none" stroke-linecap="round"/>""" +
            """<circle cx="50" cy="60" r="32" fill="#FFD84D"/><ellipse cx="20" cy="66" rx="7" ry="11" transform="rotate(20 20 66)" fill="#F7C531"/><ellipse cx="80" cy="66" rx="7" ry="11" transform="rotate(-20 80 66)" fill="#F7C531"/>""" +
            Eyes(39, 61, 55) + """<path d="M45 62 L55 62 L50 69 Z" fill="#F5862F"/>""" + Blush(29, 71, 65))),

        new("ghost", "Ghost", Svg("#E9E4FF",
            """<path d="M22 92 V52 Q22 20 50 20 Q78 20 78 52 V92 q-6 -7 -11.2 0 q-5.6 -7 -11.2 0 q-5.6 -7 -11.2 0 q-5.6 -7 -11.2 0 q-5.6 -7 -11.2 0 Z" fill="#fff"/>""" +
            Eyes(40, 60, 50, 4.5, 6) + """<ellipse cx="50" cy="62" rx="3.5" ry="4" fill="#2b2233"/>""" + Blush(31, 69, 58) +
            """<path d="M78 70 q8 -2 9 -10" stroke="#fff" stroke-width="6" fill="none" stroke-linecap="round"/>""")),

        new("axolotl", "Axolotl", Svg("#FFE6F2",
            """<path d="M24 46 q-12 -6 -14 -14 M22 54 q-14 -2 -16 -8 M24 62 q-12 2 -15 -2 M76 46 q12 -6 14 -14 M78 54 q14 -2 16 -8 M76 62 q12 2 15 -2" stroke="#FF7FAF" stroke-width="5" fill="none" stroke-linecap="round"/>""" +
            """<ellipse cx="50" cy="58" rx="30" ry="27" fill="#FFB3CF"/>""" + Eyes(37, 63, 55) + Smile(50, 64, 6) + Blush(28, 72, 64) +
            """<circle cx="44" cy="42" r="1.6" fill="#ff8fb8"/><circle cx="56" cy="40" r="1.3" fill="#ff8fb8"/><circle cx="50" cy="37" r="1.1" fill="#ff8fb8"/>""")),

        new("robot", "Robot", Svg("#E0F4FF",
            """<path d="M50 24 V14" stroke="#8a97a8" stroke-width="3" stroke-linecap="round"/><circle cx="50" cy="12" r="5" fill="#FF8FA3"/>""" +
            """<rect x="16" y="50" width="8" height="18" rx="4" fill="#8a97a8"/><rect x="76" y="50" width="8" height="18" rx="4" fill="#8a97a8"/>""" +
            """<rect x="21" y="24" width="58" height="56" rx="18" fill="#CFD8E3"/><rect x="29" y="36" width="42" height="30" rx="11" fill="#2b3446"/>""" +
            Eyes(41, 59, 49, 4.5, 5.5, "#6EE7F9") + """<path d="M45 57 q5 4 10 0" stroke="#6EE7F9" stroke-width="2.2" fill="none" stroke-linecap="round"/>""" +
            Blush(27, 73, 72))),

        new("hamster", "Hamster", Svg("#FFF0E0",
            """<circle cx="29" cy="32" r="8" fill="#E9A66B"/><circle cx="71" cy="32" r="8" fill="#E9A66B"/><circle cx="29" cy="32" r="4" fill="#FFC9B0"/><circle cx="71" cy="32" r="4" fill="#FFC9B0"/>""" +
            """<ellipse cx="50" cy="60" rx="34" ry="30" fill="#E9A66B"/><path d="M50 38 Q24 44 22 66 Q30 88 50 88 Q70 88 78 66 Q76 44 50 38 Z" fill="#FFF3E6"/>""" +
            Eyes(39, 61, 56, 4.5, 5.5) + """<ellipse cx="50" cy="63" rx="2.8" ry="2" fill="#ff8fa3"/>""" + Cat(50, 66) + Blush(29, 71, 66) +
            """<path d="M47 70 h6 v5 q-3 1.6 -6 0 Z" fill="#fff" stroke="#e6d6c6" stroke-width=".8"/>""")),
    ];

    /// <summary>The picture each starter bot gets, by template name.</summary>
    private static readonly Dictionary<string, string> ForTemplate = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chief of Staff"] = "owl", ["Researcher"] = "fox", ["Engineer"] = "cat", ["Writer"] = "bunny",
        ["Critic"] = "raccoon", ["Planner"] = "penguin", ["Comedian"] = "frog",
    };

    public static BotPicture? Find(string? id) => id is null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The built-in picture a bot shows: the one it picked, or — when it never picked a picture or an emoji — its
    /// starter picture, or one chosen from its id so every bot has its own cute face. Null when it uses an upload or
    /// an emoji.
    /// </summary>
    public static BotPicture? For(Bot bot)
    {
        if (bot.AvatarImage is { } chosen)
            return chosen.StartsWith(Prefix, StringComparison.Ordinal) ? Find(chosen[Prefix.Length..]) : null;
        if (ForTemplate.TryGetValue(bot.Name, out var id)) return Find(id);
        var hash = 0;
        foreach (var c in bot.Id) hash = unchecked(hash * 31 + c);
        return All[(int)((uint)hash % (uint)All.Count)];
    }
}
