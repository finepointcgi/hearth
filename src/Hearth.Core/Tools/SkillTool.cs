using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Tools;

/// <summary>Lets the model load a saved skill's instructions when it decides the skill fits the task.</summary>
public sealed class UseSkillTool(SkillStore skills) : ITool
{
    public string Name => "use_skill";
    public string DisplayName => "Use skill";
    public string Icon => "⚡";
    public string Description =>
        "Load the instructions of one of the saved skills (listed in the system prompt) and follow them for this task.";
    public JsonObject Parameters => Schema.Object(("name", "string", "Skill name, without the slash", true),
        ("file", "string", "Optional: read one of the skill's bundled files (listed when you load it)", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var all = await skills.ActiveAsync();
        var name = Skill.Slug((args.Str("name") ?? "").Trim().TrimStart('/'));
        var skill = all.FirstOrDefault(s => s.Name == name);
        if (skill is null) return ToolResult.Error($"No skill '{name}'. Skills: {string.Join(", ", all.Select(s => s.Name))}");
        if (args.Str("file") is { Length: > 0 } file)
            return skill.Files.TryGetValue(file.TrimStart('/', '.'), out var content)
                ? new ToolResult($"{skill.Name}/{file}:\n{(content.Length > 30000 ? content[..30000] + "\n…(truncated)" : content)}")
                : ToolResult.Error($"No file '{file}'. Files: {string.Join(", ", skill.Files.Keys)}");
        await skills.RecordUseAsync(skill.Id);
        var files = skill.Files.Count == 0 ? "" : $"\n\nBundled files (read with use_skill name={skill.Name} file=…): {string.Join(", ", skill.Files.Keys)}";
        return new ToolResult($"Skill \"{skill.Name}\" instructions — follow them now:\n{skill.Instructions}{files}" +
            "\n\n(If these steps turn out wrong or outdated, fix the skill with improve_skill once you've found what works.)");
    }
}

/// <summary>A bot writes down a method that worked, so it (and other bots) can repeat it.</summary>
public sealed class SaveSkillTool(SkillStore skills, SettingsStore settings) : ITool
{
    public string Name => "save_skill";
    public string DisplayName => "Save skill";
    public string Icon => "📒";
    public string Description =>
        "Save a reusable method as a skill after you've successfully finished a multi-step task you're likely to repeat " +
        "(e.g. how to pull a report from a site, the steps of a weekly routine). Write general, step-by-step instructions " +
        "with the exact sites, tools, gotchas and output format — not this one conversation's details. Don't save trivial things.";
    public JsonObject Parameters => Schema.Object(
        ("name", "string", "Short slash-command name, e.g. weekly-sales-report", true),
        ("description", "string", "One line: when to use it", true),
        ("instructions", "string", "Numbered steps that worked, with tools, sites, pitfalls and the output format", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var name = Skill.Slug(args.Str("name") ?? "");
        var instructions = args.Str("instructions") ?? "";
        if (name.Length < 3) return ToolResult.Error("Give the skill a short name (letters, digits, dashes).");
        if (instructions.Trim().Length < 40) return ToolResult.Error("Write real step-by-step instructions (at least a few lines).");
        if (await skills.FindAsync(name) is { } existing)
            return ToolResult.Error($"A skill /{existing.Name} already exists. Use improve_skill to update it, or pick another name.");
        var review = (await settings.GetAsync()).SkillLearning.NeedsReview;
        var skill = new Skill
        {
            Name = name, Icon = "📒", Description = (args.Str("description") ?? "").Trim(), Instructions = instructions.Trim(),
            CreatedBy = ctx.Bot?.Id ?? Bot.PlainId, Pending = review,
        };
        await skills.SaveAsync(skill);
        ctx.Reply.SkillNotes.Add($"saved /{skill.Name}");
        lock (ctx.Reply.LearnedItems) ctx.Reply.LearnedItems.Add(new LearnedItem { Kind = LearnedItem.NewSkill, Text = "/" + skill.Name, Ref = skill.Id });
        return new ToolResult(review
            ? $"Saved /{skill.Name}. The user reviews new skills before bots can use them."
            : $"Saved /{skill.Name}. You and the other bots can use it from now on.");
    }
}

/// <summary>A bot fixes a skill whose steps were wrong, incomplete or outdated.</summary>
public sealed class ImproveSkillTool(SkillStore skills, SettingsStore settings) : ITool
{
    public string Name => "improve_skill";
    public string DisplayName => "Improve skill";
    public string Icon => "🛠️";
    public string Description =>
        "Rewrite an existing skill when you found its steps were wrong, incomplete or outdated, or you found a better way. " +
        "Send the complete new instructions (not just the change). The previous version is kept so the user can undo.";
    public JsonObject Parameters => Schema.Object(
        ("name", "string", "The skill to update, without the slash", true),
        ("instructions", "string", "The complete improved instructions", true),
        ("change", "string", "One line on what changed and why", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var skill = await skills.FindAsync(args.Str("name") ?? "");
        if (skill is null) return ToolResult.Error("No skill with that name. Use save_skill to create it.");
        var instructions = args.Str("instructions") ?? "";
        if (instructions.Trim().Length < 40) return ToolResult.Error("Send the complete instructions, not just the change.");
        var review = (await settings.GetAsync()).SkillLearning.NeedsReview;
        var changed = skill.Instructions.Trim() != instructions.Trim();
        await skills.ReviseAsync(skill, instructions, null, args.Str("change"), ctx.Bot?.Name ?? "Hearth", review);
        ctx.Reply.SkillNotes.Add($"improved /{skill.Name}");
        if (changed)
            lock (ctx.Reply.LearnedItems)
                ctx.Reply.LearnedItems.Add(new LearnedItem { Kind = LearnedItem.ImprovedSkill, Text = "/" + skill.Name, Ref = skill.Id, Extra = skill.UpdatedAt.ToString("O") });
        return new ToolResult($"Updated /{skill.Name}. The previous version is kept in its history.");
    }
}
