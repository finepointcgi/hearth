using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>
/// Check-ins: a morning hello and an evening "how did it go?" from your lead bot, written like a friend who keeps up
/// with your life (your threads, today's calendar), not a report. Kept as two routines on that bot; in chat apps they
/// arrive as ordinary messages in your chat, so replying just carries on the conversation.
/// </summary>
public sealed class CheckIns(SettingsStore settingsStore, BotStore bots)
{
    public const string MorningName = "Morning hello";
    public const string EveningName = "Evening check-in";

    public const string MorningPrompt =
        "Send the user a short good-morning message, the way a friend who keeps up with their life would — not a report or a brief. " +
        "2–4 sentences, no headings or bullet lists. If you can, glance at today's calendar; think about the threads of their life " +
        "and how they've been. Mention the one thing that matters most today, or ask how something is going. At most one question. " +
        "Reply with only the message to the user.";

    public const string EveningPrompt =
        "Check in with the user at the end of their day, like a friend: a short, warm message (2–3 sentences, no lists). Ask how " +
        "something specific went (from today's calendar or the threads of their life), or what they want to remember from today. " +
        "One question. Reply with only the message to the user. When they answer, keep their threads up to date.";

    public async Task<Bot?> BotAsync(CheckInSettings c)
    {
        var team = await bots.ListAsync();
        var settings = await settingsStore.GetAsync();
        return team.FirstOrDefault(b => b.Id == c.BotId) ?? team.FirstOrDefault(b => b.Id == settings.DefaultBotId)
            ?? team.FirstOrDefault(b => b.IsCoordinator) ?? team.FirstOrDefault();
    }

    /// <summary>Saves the settings and makes the bot's two routines match them.</summary>
    public async Task ApplyAsync(CheckInSettings c)
    {
        var target = c.Morning || c.Evening ? await BotAsync(c) : null;
        if ((c.Morning || c.Evening) && target is null) throw new InvalidOperationException("Add a bot first: check-ins come from one of your bots.");
        c.MorningRoutineId = await KeepAsync(target, c.Morning, c.MorningRoutineId, MorningName, MorningPrompt, c.MorningTime, "08:30");
        c.EveningRoutineId = await KeepAsync(target, c.Evening, c.EveningRoutineId, EveningName, EveningPrompt, c.EveningTime, "20:30");
        var settings = await settingsStore.GetAsync();
        settings.CheckIns = c;
        await settingsStore.SaveAsync(settings);
    }

    private async Task<string?> KeepAsync(Bot? target, bool on, string? id, string name, string prompt, string time, string fallback)
    {
        Routine? existing = null;
        foreach (var bot in await bots.ListAsync())
        {
            var mine = bot.Routines.FirstOrDefault(r => r.Id == id);
            if (mine is null) continue;
            if (on && bot.Id == target?.Id) { existing = mine; continue; }
            bot.Routines.Remove(mine); // moved to another bot, or turned off
            await bots.SaveAsync(bot);
        }
        if (!on || target is null) return null;
        var routine = existing ?? new Routine { Name = name };
        routine.Prompt = prompt;
        routine.Trigger = TriggerKind.Schedule;
        routine.Schedule = RoutineSchedule.Daily;
        routine.TimeOfDay = TimeOnly.TryParse(time, out var t) ? t.ToString("HH:mm") : fallback;
        routine.Enabled = true;
        routine.NotifyChannels = true;
        routine.Conversational = true;
        if (existing is null) target.Routines.Add(routine);
        await bots.SaveAsync(target);
        return routine.Id;
    }
}
