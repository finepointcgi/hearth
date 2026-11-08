using System.Globalization;
using System.Text.Json.Nodes;

namespace Hearth.Core.Tools;

public sealed class CalculatorTool : ITool
{
    public string Name => "calculate";
    public string DisplayName => "Calculator";
    public string Icon => "🧮";
    public string Description =>
        "Evaluate a math expression exactly. Use this for ANY arithmetic instead of computing in your head. " +
        "Supports + - * / % ^, parentheses, sqrt, abs, round, floor, ceil, log (base 10), ln, exp, sin, cos, tan, " +
        "asin, acos, atan (radians), min, max, pow, and constants pi and e.";
    public JsonObject Parameters => Schema.Object(("expression", "string", "e.g. (12.5 * 3)^2 / sqrt(7)", true));
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var expr = args.Str("expression") ?? "";
        try
        {
            var value = ExpressionEvaluator.Evaluate(expr);
            return Task.FromResult(new ToolResult($"{expr} = {value.ToString("G15", CultureInfo.InvariantCulture)}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Could not evaluate '{expr}': {ex.Message}"));
        }
    }
}

/// <summary>Small recursive-descent evaluator (no code execution, no reflection).</summary>
public static class ExpressionEvaluator
{
    public static double Evaluate(string text)
    {
        var p = new Parser(text.Replace("×", "*").Replace("÷", "/").Replace("**", "^"));
        var v = p.ParseExpression();
        p.SkipWs();
        if (p.Pos < p.Text.Length) throw new FormatException($"Unexpected '{p.Text[p.Pos]}' at {p.Pos}");
        return v;
    }

    private sealed class Parser(string text)
    {
        public readonly string Text = text;
        public int Pos;

        public void SkipWs() { while (Pos < Text.Length && char.IsWhiteSpace(Text[Pos])) Pos++; }
        private bool Eat(char c) { SkipWs(); if (Pos < Text.Length && Text[Pos] == c) { Pos++; return true; } return false; }

        public double ParseExpression()
        {
            var v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        private double ParseTerm()
        {
            var v = ParseUnary();
            while (true)
            {
                if (Eat('*')) v *= ParseUnary();
                else if (Eat('/')) v /= ParseUnary();
                else if (Eat('%')) v %= ParseUnary();
                else return v;
            }
        }

        private double ParseUnary()
        {
            if (Eat('-')) return -ParseUnary();
            if (Eat('+')) return ParseUnary();
            return ParsePower();
        }

        private double ParsePower()
        {
            var b = ParsePrimary();
            if (Eat('^')) return Math.Pow(b, ParseUnary()); // right-associative
            return b;
        }

        // "1,000" is a number; "max(1, 5)" is an argument list.
        private bool IsThousandsComma(int i) =>
            Text[i] == ','
            && Enumerable.Range(i + 1, 3).All(j => j < Text.Length && char.IsDigit(Text[j]))
            && (i + 4 >= Text.Length || !char.IsDigit(Text[i + 4]));

        private double ParsePrimary()
        {
            SkipWs();
            if (Eat('('))
            {
                var v = ParseExpression();
                if (!Eat(')')) throw new FormatException("Missing ')'");
                return v;
            }
            if (Pos < Text.Length && (char.IsDigit(Text[Pos]) || Text[Pos] == '.'))
            {
                var start = Pos;
                while (Pos < Text.Length && (char.IsDigit(Text[Pos]) || Text[Pos] is '.' or '_' || IsThousandsComma(Pos))) Pos++;
                if (Pos < Text.Length && Text[Pos] is 'e' or 'E' && Pos + 1 < Text.Length &&
                    (char.IsDigit(Text[Pos + 1]) || Text[Pos + 1] is '-' or '+'))
                {
                    Pos += 2;
                    while (Pos < Text.Length && char.IsDigit(Text[Pos])) Pos++;
                }
                var num = Text[start..Pos].Replace("_", "").Replace(",", "");
                return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (Pos < Text.Length && char.IsLetter(Text[Pos]))
            {
                var start = Pos;
                while (Pos < Text.Length && char.IsLetterOrDigit(Text[Pos])) Pos++;
                var name = Text[start..Pos].ToLowerInvariant();
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                if (!Eat('(')) throw new FormatException($"Unknown name '{name}'");
                var args = new List<double> { ParseExpression() };
                while (Eat(',')) args.Add(ParseExpression());
                if (!Eat(')')) throw new FormatException("Missing ')'");
                return Call(name, args);
            }
            throw new FormatException(Pos < Text.Length ? $"Unexpected '{Text[Pos]}'" : "Unexpected end of expression");
        }

        private static double Call(string f, List<double> a) => f switch
        {
            "sqrt" => Math.Sqrt(a[0]),
            "abs" => Math.Abs(a[0]),
            "round" => a.Count > 1 ? Math.Round(a[0], (int)a[1]) : Math.Round(a[0]),
            "floor" => Math.Floor(a[0]),
            "ceil" => Math.Ceiling(a[0]),
            "log" => a.Count > 1 ? Math.Log(a[0], a[1]) : Math.Log10(a[0]),
            "ln" => Math.Log(a[0]),
            "exp" => Math.Exp(a[0]),
            "sin" => Math.Sin(a[0]),
            "cos" => Math.Cos(a[0]),
            "tan" => Math.Tan(a[0]),
            "asin" => Math.Asin(a[0]),
            "acos" => Math.Acos(a[0]),
            "atan" => Math.Atan(a[0]),
            "min" => a.Min(),
            "max" => a.Max(),
            "pow" => Math.Pow(a[0], a[1]),
            _ => throw new FormatException($"Unknown function '{f}'"),
        };
    }
}

public sealed class DateTimeTool : ITool
{
    public string Name => "get_current_datetime";
    public string DisplayName => "Date & time";
    public string Icon => "🕒";
    public string Description => "Get the current date and time, optionally in another IANA time zone (e.g. Asia/Tokyo).";
    public JsonObject Parameters => Schema.Object(("timezone", "string", "IANA time zone id; omit for the user's local time", false));
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var tzId = args.Str("timezone");
        var tz = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(tzId) && !TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out tz!))
            return Task.FromResult(ToolResult.Error($"Unknown time zone '{tzId}'."));
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, tz);
        return Task.FromResult(new ToolResult(
            $"{now:dddd, MMMM d, yyyy h:mm:ss tt} ({tz.Id}, UTC{now:zzz}). ISO: {now:O}. Week {ISOWeek.GetWeekOfYear(now.DateTime)}."));
    }
}

public sealed class UnitConvertTool : ITool
{
    public string Name => "convert_units";
    public string DisplayName => "Unit converter";
    public string Icon => "📏";
    public string Description =>
        "Convert between units of length, mass, volume, temperature, speed, area, time, data size and energy. " +
        "Unit examples: m, km, mi, ft, in, kg, lb, oz, l, ml, gal, cup, c, f, k, kmh, mph, m2, acre, s, h, day, kb, mb, gb, tb, j, kcal, kwh.";
    public JsonObject Parameters => Schema.Object(
        ("value", "number", "Amount to convert", true),
        ("from", "string", "Source unit", true),
        ("to", "string", "Target unit", true));
    public bool RequiresApproval => false;

    // factor to the SI base of each dimension
    private static readonly Dictionary<string, (string dim, double factor)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = ("len", 1), ["meter"] = ("len", 1), ["meters"] = ("len", 1), ["km"] = ("len", 1000), ["cm"] = ("len", .01),
        ["mm"] = ("len", .001), ["um"] = ("len", 1e-6), ["nm"] = ("len", 1e-9), ["mi"] = ("len", 1609.344), ["mile"] = ("len", 1609.344),
        ["miles"] = ("len", 1609.344), ["yd"] = ("len", .9144), ["ft"] = ("len", .3048), ["feet"] = ("len", .3048), ["foot"] = ("len", .3048),
        ["in"] = ("len", .0254), ["inch"] = ("len", .0254), ["inches"] = ("len", .0254), ["nmi"] = ("len", 1852), ["ly"] = ("len", 9.4607e15),
        ["kg"] = ("mass", 1), ["g"] = ("mass", .001), ["mg"] = ("mass", 1e-6), ["t"] = ("mass", 1000), ["tonne"] = ("mass", 1000),
        ["lb"] = ("mass", .45359237), ["lbs"] = ("mass", .45359237), ["pound"] = ("mass", .45359237), ["oz"] = ("mass", .028349523125),
        ["st"] = ("mass", 6.35029318), ["ton"] = ("mass", 907.18474),
        ["l"] = ("vol", .001), ["liter"] = ("vol", .001), ["litre"] = ("vol", .001), ["ml"] = ("vol", 1e-6), ["m3"] = ("vol", 1),
        ["gal"] = ("vol", .003785411784), ["gallon"] = ("vol", .003785411784), ["qt"] = ("vol", .000946352946), ["pt"] = ("vol", .000473176473),
        ["cup"] = ("vol", .0002365882365), ["floz"] = ("vol", 2.95735295625e-5), ["tbsp"] = ("vol", 1.478676478125e-5), ["tsp"] = ("vol", 4.92892159375e-6),
        ["ms"] = ("speed", 1), ["m/s"] = ("speed", 1), ["kmh"] = ("speed", 1 / 3.6), ["km/h"] = ("speed", 1 / 3.6), ["kph"] = ("speed", 1 / 3.6),
        ["mph"] = ("speed", .44704), ["kn"] = ("speed", .514444), ["knot"] = ("speed", .514444), ["knots"] = ("speed", .514444),
        ["m2"] = ("area", 1), ["km2"] = ("area", 1e6), ["ft2"] = ("area", .09290304), ["sqft"] = ("area", .09290304), ["acre"] = ("area", 4046.8564224),
        ["acres"] = ("area", 4046.8564224), ["ha"] = ("area", 10000), ["mi2"] = ("area", 2589988.110336),
        ["s"] = ("time", 1), ["sec"] = ("time", 1), ["min"] = ("time", 60), ["h"] = ("time", 3600), ["hr"] = ("time", 3600), ["hour"] = ("time", 3600),
        ["day"] = ("time", 86400), ["days"] = ("time", 86400), ["week"] = ("time", 604800), ["year"] = ("time", 31557600),
        ["b"] = ("data", 1), ["byte"] = ("data", 1), ["kb"] = ("data", 1e3), ["mb"] = ("data", 1e6), ["gb"] = ("data", 1e9), ["tb"] = ("data", 1e12),
        ["kib"] = ("data", 1024), ["mib"] = ("data", 1048576), ["gib"] = ("data", 1073741824), ["tib"] = ("data", 1099511627776), ["bit"] = ("data", .125),
        ["j"] = ("energy", 1), ["kj"] = ("energy", 1000), ["cal"] = ("energy", 4.184), ["kcal"] = ("energy", 4184), ["wh"] = ("energy", 3600),
        ["kwh"] = ("energy", 3.6e6), ["btu"] = ("energy", 1055.05585),
    };

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var value = args.Num("value");
        var from = Norm(args.Str("from"));
        var to = Norm(args.Str("to"));
        if (value is null) return Task.FromResult(ToolResult.Error("value must be a number"));

        double? result = null;
        if (IsTemp(from) && IsTemp(to)) result = FromKelvin(ToKelvin(value.Value, from), to);
        else if (Units.TryGetValue(from, out var a) && Units.TryGetValue(to, out var b))
        {
            if (a.dim != b.dim) return Task.FromResult(ToolResult.Error($"Can't convert {a.dim} to {b.dim}."));
            result = value.Value * a.factor / b.factor;
        }
        return Task.FromResult(result is null
            ? ToolResult.Error($"Unknown unit '{(Units.ContainsKey(from) || IsTemp(from) ? to : from)}'.")
            : new ToolResult($"{value.Value.ToString(CultureInfo.InvariantCulture)} {from} = {result.Value.ToString("G10", CultureInfo.InvariantCulture)} {to}"));
    }

    private static string Norm(string? u) => (u ?? "").Trim().Replace("°", "").Replace(" ", "").Replace("²", "2").Replace("³", "3");
    private static bool IsTemp(string u) => u.ToLowerInvariant() is "c" or "f" or "k" or "celsius" or "fahrenheit" or "kelvin";
    private static double ToKelvin(double v, string u) => u.ToLowerInvariant()[0] switch { 'c' => v + 273.15, 'f' => (v - 32) * 5 / 9 + 273.15, _ => v };
    private static double FromKelvin(double v, string u) => u.ToLowerInvariant()[0] switch { 'c' => v - 273.15, 'f' => (v - 273.15) * 9 / 5 + 32, _ => v };
}
