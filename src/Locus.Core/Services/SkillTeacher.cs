using System.Text;
using Locus.Core.Llm;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>Turns a recorded browser session into a reusable skill (show-and-learn).</summary>
public sealed class SkillTeacher(SettingsStore settingsStore, ILlmClientFactory factory, ModelPicker models, SkillStore skills)
{
    public async Task<Skill> CreateAsync(string name, string description, IReadOnlyList<RecordedStep> steps, CancellationToken ct = default)
    {
        var numbered = string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s.Describe()}"));
        var startUrl = steps.FirstOrDefault(s => s.Kind == "navigate")?.Url;
        var instructions = await WriteWithModelAsync(name, description, numbered, ct) ?? Template(description, numbered, startUrl);

        var skill = new Skill
        {
            Name = Skill.Slug(name),
            Icon = "🎬",
            Description = string.IsNullOrWhiteSpace(description) ? $"Repeat the \"{name}\" task shown in the browser" : description,
            Instructions = instructions,
        };
        await skills.SaveAsync(skill);
        return skill;
    }

    private async Task<string?> WriteWithModelAsync(string name, string description, string numbered, CancellationToken ct)
    {
        var model = await models.PickAsync(null);
        if (model is null) return null;
        try
        {
            var settings = await settingsStore.GetAsync();
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System(
                    "You turn a recording of a person doing a task in a web browser into reusable instructions for an AI agent. " +
                    "The agent has browser tools: browser_open(url), browser_snapshot, browser_click(element number), " +
                    "browser_type(element number, text), browser_fill_login(saved login), browser_scroll, browser_screenshot. " +
                    "Write clear numbered steps that describe elements by their visible label (element numbers change every time). " +
                    "Values that would differ next time (search terms, names, dates, amounts) become placeholders like {search term}; " +
                    "keep values that are clearly fixed (site URLs, menu choices). Never include passwords: for sign-in steps say to use " +
                    "browser_fill_login with the saved login for that site. End with how to check the task worked and what to report back. " +
                    "Start with one line listing the inputs the agent should ask for if they weren't given. Output only the instructions."),
                LlmMessage.User($"Task name: {name}\nWhat it's for: {(string.IsNullOrWhiteSpace(description) ? "(not given)" : description)}\n\nRecorded steps:\n{numbered}"),
            };
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            await foreach (var c in factory.CreateForChat(settings).StreamChatAsync(new LlmRequest(model, prompt, 0.2, 1200, 8192, false), cts.Token))
                sb.Append(c.Content);
            var text = sb.ToString().Trim();
            return text.Length > 40 ? text + $"\n\n(Recorded steps, for reference:)\n{numbered}" : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Used when no model is available: the recording itself, with guidance.</summary>
    public static string Template(string description, string numbered, string? startUrl) =>
        (string.IsNullOrWhiteSpace(description) ? "" : description.Trim() + "\n\n") +
        "Repeat this task in the browser, the way the user showed it. Element numbers change between visits, so find each " +
        "element by its label with browser_snapshot before clicking or typing. If a typed value looks like it should be " +
        "different this time (a search term, a name, a date), use the value the user gave you, or ask for it. " +
        "For sign-ins use browser_fill_login with the saved login for the site.\n\n" +
        (startUrl is null ? "" : $"Start at {startUrl}\n\n") + numbered +
        "\n\nWhen done, take a browser_screenshot and say what happened.";
}
