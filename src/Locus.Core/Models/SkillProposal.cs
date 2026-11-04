namespace Locus.Core.Models;

/// <summary>The model's answer when asked whether a finished task is worth keeping as a skill.</summary>
public sealed record SkillProposal(string Name, string Description, string Instructions, string? Improve)
{
    public static SkillProposal? Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        string? action = null, name = null, description = null;
        var instructions = new List<string>();
        var inInstructions = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var t = line.TrimStart('*', ' ', '#');
            if (!inInstructions && t.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase)) action = t[7..].Trim().Trim('*').Trim();
            else if (!inInstructions && t.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase)) name = t[5..].Trim().Trim('*', '`', '/').Trim();
            else if (!inInstructions && t.StartsWith("DESCRIPTION:", StringComparison.OrdinalIgnoreCase)) description = t[12..].Trim();
            else if (t.StartsWith("INSTRUCTIONS:", StringComparison.OrdinalIgnoreCase))
            {
                inInstructions = true;
                if (t[13..].Trim() is { Length: > 0 } rest) instructions.Add(rest);
            }
            else if (inInstructions) instructions.Add(line);
        }
        if (action is null || action.StartsWith("NONE", StringComparison.OrdinalIgnoreCase)) return null;
        var body = string.Join("\n", instructions).Trim().Trim('`').Trim();
        if (body.Length < 40) return null;
        if (action.StartsWith("IMPROVE", StringComparison.OrdinalIgnoreCase))
        {
            var target = action[7..].Trim().Trim('<', '>', '/', '`', ' ');
            if (target.Length == 0) target = name ?? "";
            return target.Length == 0 ? null : new SkillProposal(target, description ?? "", body, target);
        }
        if (!action.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name)) return null;
        return new SkillProposal(name, description ?? "", body, null);
    }
}
