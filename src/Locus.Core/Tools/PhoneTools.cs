using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services.Phone;
using Microsoft.Extensions.DependencyInjection;

namespace Locus.Core.Tools;

/// <summary>Offers call_phone once phone calls are set up (Connections → Phone).</summary>
public sealed class PhoneToolProvider(IServiceProvider services) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        var store = services.GetRequiredService<PhoneStore>();
        return await store.MissingAsync() is null ? [new CallPhoneTool(services.GetRequiredService<PhoneService>())] : [];
    }
}

/// <summary>
/// A bot phones someone for the user (book a table, ask about an order). Always asks the user first, says it's an AI
/// assistant, and reports the outcome and transcript back to this chat.
/// </summary>
public sealed class CallPhoneTool(PhoneService phone) : ITool, IPermissionInfo
{
    public string Name => "call_phone";
    public string DisplayName => "Phone call";
    public string Icon => "📞";
    public string Description =>
        "Phone a business or person for the user and talk with them (reservations, appointments, questions). The user " +
        "approves each call. You'll talk on the call yourself, then get the transcript here to report back.";
    public JsonObject Parameters => Schema.Object(
        ("number", "string", "Phone number with country code, e.g. +13135550100", true),
        ("goal", "string", "What to accomplish, with every detail you'll need (names, dates, times, party size, limits)", true));
    public bool RequiresApproval => true;
    public string Area => "phone";
    public string AreaTitle => "Phone calls";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var number = args.Str("number");
        var goal = args.Str("goal")?.Trim();
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(goal)) return ToolResult.Error("Give the number and the goal.");
        if (PhoneService.IsPhoneTurn(ctx.Conversation.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.RoutineName))
            return ToolResult.Error("You're already on a call.");
        try { return new ToolResult(await phone.PlaceCallAsync(ctx.Bot, ctx.Conversation, number, goal, ct)); }
        catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
    }
}
