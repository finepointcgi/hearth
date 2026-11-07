using System.Text;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// The morning brief: Settings → Morning brief picks the time, the bot and what's in it; Locus keeps one matching
/// routine on that bot (results land in its chat, and in Telegram/phone notifications like any routine).
/// </summary>
public sealed class Briefing(SettingsStore settingsStore, BotStore bots)
{
    public const string RoutineName = "Morning brief";

    /// <summary>What the bot is asked each morning.</summary>
    public static string Prompt(BriefingSettings b)
    {
        var sb = new StringBuilder("Write the user's morning brief. Keep it short and scannable: a heading per part, bullets, most " +
            "important first, no filler. Use your tools to check each part; if something isn't connected or a tool fails, say so " +
            "in one line and carry on. Cover:");
        if (b.Calendar) sb.Append("\n- Today's calendar: meetings and events with times and who's involved, and anything to prepare.");
        if (b.Email) sb.Append("\n- Email: unread messages that need attention or a reply (skip newsletters and promotions); offer to draft replies.");
        if (b.Tasks) sb.Append("\n- Tasks: open to-dos due today or overdue, and follow-ups the user is waiting on.");
        if (b.Jobs)
            sb.Append("\n- New job openings posted in the last day or so")
              .Append(string.IsNullOrWhiteSpace(b.JobCriteria) ? " that fit what you know about the user's job search" : $" matching: {b.JobCriteria.Trim()}")
              .Append(". Check company career pages and job boards; list only ones not in earlier briefs, with title, company, location, " +
                      "salary if listed and the link. If nothing new, say so.");
        if (b.News && !string.IsNullOrWhiteSpace(b.NewsTopics))
            sb.Append($"\n- News: the most important new developments on {b.NewsTopics.Trim()}, two or three items each, with sources.");
        if (b.Weather) sb.Append($"\n- Weather today{(string.IsNullOrWhiteSpace(b.Location) ? "" : $" in {b.Location.Trim()}")}: high/low, rain, anything notable.");
        if (b.Learned) sb.Append("\n- " + LearningRecap.LearnedToken);
        if (!string.IsNullOrWhiteSpace(b.Extra)) sb.Append($"\n- {b.Extra.Trim()}");
        return sb.ToString();
    }

    /// <summary>The bot that writes the brief: the chosen one, else the coordinator, else the first bot.</summary>
    public async Task<Bot?> BotAsync(BriefingSettings b)
    {
        var team = await bots.ListAsync();
        return team.FirstOrDefault(x => x.Id == b.BotId) ?? team.FirstOrDefault(x => x.IsCoordinator) ?? team.FirstOrDefault();
    }

    /// <summary>At startup: brings an existing brief's routine up to date with the current wording.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var b = (await settingsStore.GetAsync()).Briefing;
            if (b.Enabled) await ApplyAsync(b);
        }
        catch { /* the routine keeps its old wording */ }
    }

    /// <summary>Saves the settings and makes the bot's routine match them (added, updated, moved or removed).</summary>
    public async Task ApplyAsync(BriefingSettings b)
    {
        var target = b.Enabled ? await BotAsync(b) : null;
        if (b.Enabled && target is null) throw new InvalidOperationException("Add a bot first: the brief is written by one of your bots.");
        Routine? existing = null;
        foreach (var bot in await bots.ListAsync())
        {
            var mine = bot.Routines.FirstOrDefault(r => r.Id == b.RoutineId);
            if (mine is null) continue;
            if (bot.Id == target?.Id) { existing = mine; continue; }
            bot.Routines.Remove(mine); // moved to another bot, or turned off
            await bots.SaveAsync(bot);
        }
        if (target is not null)
        {
            var routine = existing ?? new Routine { Name = RoutineName };
            routine.Prompt = Prompt(b);
            routine.Trigger = TriggerKind.Schedule;
            routine.Schedule = b.WeekdaysOnly ? RoutineSchedule.Weekdays : RoutineSchedule.Daily;
            routine.TimeOfDay = TimeOnly.TryParse(b.Time, out var t) ? t.ToString("HH:mm") : "07:00";
            routine.Enabled = true;
            routine.NotifyChannels = true; // the brief is meant to reach you
            if (existing is null) target.Routines.Add(routine);
            await bots.SaveAsync(target);
            b.RoutineId = routine.Id;
            b.BotId ??= target.Id;
        }
        var settings = await settingsStore.GetAsync();
        settings.Briefing = b;
        await settingsStore.SaveAsync(settings);
    }
}
