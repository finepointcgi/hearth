using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Locus.Core;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Services.Phone;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

public sealed class PhoneTests : IAsyncDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-phone-").FullName;
    }

    /// <summary>Stands in for api.twilio.com.</summary>
    private sealed class FakeTwilio : HttpMessageHandler
    {
        public readonly List<(string url, Dictionary<string, string> form)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .GroupBy(p => WebUtility.UrlDecode(p[0])).ToDictionary(g => g.Key, g => WebUtility.UrlDecode(g.Last()[1]));
            Requests.Add((request.RequestUri!.ToString(), form));
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"sid\":\"CA123\"}") };
        }
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly FakeTwilio _twilio = new();
    private readonly ServiceProvider _sp;
    private readonly Bot _bot = new() { Name = "Concierge", Title = "Phone assistant" };
    private const string Token = "test-auth-token";

    public PhoneTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        services.AddHttpClient(LlmClientFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _twilio);
        _sp = services.BuildServiceProvider();
        var settings = _sp.GetRequiredService<SettingsStore>();
        var s = settings.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "main-model" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryEnabled = false;
        s.SkillLearning.Enabled = false;
        settings.SaveAsync(s).GetAwaiter().GetResult();
        _sp.GetRequiredService<BotStore>().SaveAsync(_bot).GetAwaiter().GetResult();
        var phone = _sp.GetRequiredService<PhoneStore>();
        phone.UpdateAsync(c =>
        {
            c.Enabled = true; c.AccountSid = "AC1"; c.Number = "+13135550100"; c.PublicUrl = "https://locus.example.ts.net";
            c.AnswerBotId = _bot.Id; c.AllowedCallers = ["+13135550123"];
        }).GetAwaiter().GetResult();
        phone.SetAuthTokenAsync(Token).GetAwaiter().GetResult();
    }

    private PhoneService Phone => _sp.GetRequiredService<PhoneService>();

    /// <summary>A pretend ConversationRelay connection: what Twilio sends in, what Locus sends back.</summary>
    private sealed class Line
    {
        public readonly Channel<string?> In = Channel.CreateUnbounded<string?>();
        public readonly List<JsonNode> Out = [];
        public Task Run(PhoneService phone) => phone.RunRelayAsync(
            async ct => await In.Reader.ReadAsync(ct),
            (text, _) => { lock (Out) Out.Add(JsonNode.Parse(text)!); return Task.CompletedTask; },
            CancellationToken.None);
        public void Twilio(object message) => In.Writer.TryWrite(System.Text.Json.JsonSerializer.Serialize(message));
        public void HangUp() => In.Writer.TryWrite(null);
        public List<string> Spoken() { lock (Out) return Out.Where(o => o["type"]?.ToString() == "text").Select(o => o["token"]!.ToString()).ToList(); }
        public bool Ended() { lock (Out) return Out.Any(o => o["type"]?.ToString() == "end"); }
    }

    private static async Task Until(Func<bool> done, int seconds = 10)
    {
        for (var i = 0; i < seconds * 20 && !done(); i++) await Task.Delay(50);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public void SignaturesAreCheckedForWebhooksAndTheRelayHandshake()
    {
        var form = new Dictionary<string, string> { ["From"] = "+13135550123", ["CallSid"] = "CA9" };
        var url = "https://locus.example.ts.net/phone/incoming";
        var sig = Twilio.Sign(Token, url, form);
        Assert.True(Twilio.IsValid(Token, url, form, sig));
        Assert.False(Twilio.IsValid(Token, url, new Dictionary<string, string>(form) { ["From"] = "+19995550000" }, sig)); // tampered
        Assert.False(Twilio.IsValid("other-token", url, form, sig));
        Assert.False(Twilio.IsValid(Token, url, form, null));
        // The WebSocket handshake: accepted whether Twilio signed the wss:// or https:// form, with or without a slash.
        var relay = "wss://locus.example.ts.net/phone/relay";
        Assert.True(Twilio.IsValid(Token, relay, null, Twilio.Sign(Token, relay)));
        Assert.True(Twilio.IsValid(Token, relay, null, Twilio.Sign(Token, "https://locus.example.ts.net/phone/relay")));
        Assert.True(Twilio.IsValid(Token, relay, null, Twilio.Sign(Token, relay + "/")));
    }

    [Fact]
    public async Task OnlyAllowedCallersReachTheBot()
    {
        var stranger = await Phone.IncomingAsync(new Dictionary<string, string> { ["From"] = "+12125550199", ["CallSid"] = "CA1" });
        Assert.Contains("this line is private", stranger);
        Assert.Contains("<Hangup/>", stranger);

        var owner = await Phone.IncomingAsync(new Dictionary<string, string> { ["From"] = "(313) 555-0123", ["CallSid"] = "CA2" });
        Assert.Contains("<ConversationRelay url=\"wss://locus.example.ts.net/phone/relay\"", owner);
        Assert.Contains("welcomeGreeting=\"Hi, it&apos;s Concierge. What can I do for you?\"", owner);
        Assert.Contains("<Connect action=\"https://locus.example.ts.net/phone/ended\">", owner);
        Assert.Contains("<Parameter name=\"call\"", owner);
    }

    [Fact]
    public async Task ACallInIsAChatSpokenAloudThatTheBotCanHangUp()
    {
        var twiml = await Phone.IncomingAsync(new Dictionary<string, string> { ["From"] = "+13135550123", ["CallSid"] = "CA2" });
        var callId = System.Text.RegularExpressions.Regex.Match(twiml, "name=\"call\" value=\"(\\w+)\"").Groups[1].Value;
        string? systemPrompt = null;
        _team.Script = (_, messages, req) =>
        {
            systemPrompt = messages[0].Content;
            var said = messages.Last(m => m.Role == "user").Content;
            if (said.Contains("bye")) return messages.Any(m => m.Role == "tool") ? new LlmChunk("Talk soon, bye!") : ScriptedTeam.Call("end_call", new { });
            return new LlmChunk("**Four.** It's [2+2](https://example.com) [1].");
        };

        var line = new Line();
        var running = line.Run(Phone);
        line.Twilio(new { type = "setup", callSid = "CA2", from = "+13135550123", customParameters = new { call = callId } });
        line.Twilio(new { type = "prompt", voicePrompt = "what's two plus two", last = true });
        await Until(() => line.Spoken().Count == 1);
        Assert.Equal("Four. It's 2+2 .", line.Spoken()[0]); // markdown, links and citations aren't read out
        Assert.Contains("YOU'RE ON A PHONE CALL", systemPrompt);

        line.Twilio(new { type = "prompt", voicePrompt = "ok thanks, bye", last = true });
        await Until(line.Ended, 30);
        Assert.Equal("Talk soon, bye!", line.Spoken()[^1]);
        await running;

        var conv = (await _sp.GetRequiredService<ConversationStore>().GetAsync(Phone.Find(callId)!.ConversationId))!;
        Assert.StartsWith("📞 Call from +13135550123", conv.Title);
        Assert.Contains(conv.Messages, m => m.RoutineName == PhoneService.CallerName && m.Content == "what's two plus two");
        Assert.Contains(conv.Messages, m => m.Notice?.StartsWith("📞 Call ended") == true);
    }

    [Fact]
    public async Task BotPlacesACallWithApprovalAndReportsBackToTheChat()
    {
        var tool = new CallPhoneTool(Phone);
        Assert.True(tool.RequiresApproval);
        var origin = new Conversation { BotIds = [_bot.Id] };
        origin.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "book Saturday dinner at Giulia for 2 at 7" });
        await _sp.GetRequiredService<ConversationStore>().SaveAsync(origin);
        var ctx = new ToolContext
        {
            Conversation = origin, Reply = new ChatMessage(), Settings = await _sp.GetRequiredService<SettingsStore>().GetAsync(),
            Client = _team.Create(new ProviderProfile { Name = "main" }), Model = "main-model", Bot = _bot,
        };

        Assert.Contains("emergency", (await tool.ExecuteAsync(new JsonObject { ["number"] = "+1 911", ["goal"] = "x" }, ctx, default)).Text, StringComparison.OrdinalIgnoreCase);
        var placed = await tool.ExecuteAsync(new JsonObject { ["number"] = "313-555-0177", ["goal"] = "Book a table for 2 on Saturday at 7pm under Mitchell" }, ctx, default);
        Assert.False(placed.IsError, placed.Text);
        var (url, form) = Assert.Single(_twilio.Requests);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/AC1/Calls.json", url);
        Assert.Equal(("+13135550177", "+13135550100"), (form["To"], form["From"]));
        Assert.Contains("<ConversationRelay", form["Twiml"]);
        Assert.Equal("https://locus.example.ts.net/phone/status", form["StatusCallback"]);
        var callId = System.Text.RegularExpressions.Regex.Match(form["Twiml"], "name=\"call\" value=\"(\\w+)\"").Groups[1].Value;

        var prompts = new List<string>();
        _team.Script = (_, messages, _) =>
        {
            var last = messages.Last(m => m.Role == "user").Content;
            prompts.Add(messages[0].Content + "\n---\n" + last);
            if (last.Contains("phone call to +13135550177 has ended")) return new LlmChunk("Booked: Saturday 7pm for 2 at Giulia.");
            if (last.Contains("7 works")) return messages.Any(m => m.Role == "tool") ? new LlmChunk("Thank you, goodbye!") : ScriptedTeam.Call("end_call", new { });
            return new LlmChunk("Hi, I'm an AI assistant calling for Mitchell to book a table for two on Saturday at 7.");
        };
        var line = new Line();
        var running = line.Run(Phone);
        line.Twilio(new { type = "setup", callSid = "CA123", direction = "outbound-api", customParameters = new { call = callId } });
        await Until(() => line.Spoken().Count == 1); // the bot speaks first on a call it placed
        Assert.Contains("AI assistant calling for Mitchell", line.Spoken()[0]);
        Assert.Contains("NOT the user", prompts[0]);
        Assert.Contains("[Locus] Call +13135550177 for the user. Goal: Book a table", prompts[0]);

        line.Twilio(new { type = "prompt", voicePrompt = "Sure, 7 works, see you then", last = true });
        await Until(line.Ended, 30);
        Assert.Contains(prompts, p => p.Contains("[On the call]: Sure, 7 works")); // the other party isn't treated as the user
        await running;

        // Back in the chat it was asked from: the transcript went to the bot, which told the user how it went.
        var store = _sp.GetRequiredService<ConversationStore>();
        Conversation saved = origin;
        await Until(() => (saved = store.GetAsync(origin.Id).GetAwaiter().GetResult()!).Messages.LastOrDefault()?.Content == "Booked: Saturday 7pm for 2 at Giulia.", 30);
        var result = saved.Messages.Single(m => m.RoutineName == PhoneService.ResultName);
        Assert.Contains("Them: Sure, 7 works, see you then", result.Content);
        Assert.Contains("Bot: Hi, I'm an AI assistant", result.Content);
    }

    [Fact]
    public async Task UnansweredCallsAreReported()
    {
        var origin = new Conversation { BotIds = [_bot.Id] };
        origin.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "call the dentist" });
        await _sp.GetRequiredService<ConversationStore>().SaveAsync(origin);
        _team.Script = (_, messages, _) => new LlmChunk(messages.Last(m => m.Role == "user").Content.Contains("didn't connect") ? "No one picked up." : "ok");
        await Phone.PlaceCallAsync(_bot, origin, "+13135550188", "Move my cleaning to next week", default);
        await Phone.StatusAsync(new Dictionary<string, string> { ["CallSid"] = "CA123", ["CallStatus"] = "no-answer" });
        var saved = (await _sp.GetRequiredService<ConversationStore>().GetAsync(origin.Id))!;
        Assert.Contains("didn't connect (no answer)", saved.Messages.Single(m => m.RoutineName == PhoneService.ResultName).Content);
        Assert.Equal("No one picked up.", saved.Messages.Last().Content);
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
