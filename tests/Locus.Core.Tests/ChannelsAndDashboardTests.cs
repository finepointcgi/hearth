using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Services.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Locus.Core.Tests;

public class ChannelFormattingTests
{
    [Fact]
    public void TelegramHtmlEscapesAndFormats()
    {
        var html = TelegramAdapter.ToHtml("## Plan\n**Bold** & <tag> `x<y` [site](https://a.test)\n```cs\nif (a < b) {}\n```");
        Assert.Contains("<b>Plan</b>", html);
        Assert.Contains("<b>Bold</b> &amp; &lt;tag&gt;", html);
        Assert.Contains("<code>x&lt;y</code>", html);
        Assert.Contains("<a href=\"https://a.test\">site</a>", html);
        Assert.Contains("<pre>if (a &lt; b) {}</pre>", html);
    }
}

public class ApprovalAndActivityTests
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-act-").FullName;
    }

    [Fact]
    public async Task ApprovalFirstAnswerWins()
    {
        var center = new ApprovalCenter();
        var resolved = new TaskCompletionSource<bool>();
        center.Resolved += (_, allow) => resolved.TrySetResult(allow);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = center.Register(new Conversation { Title = "t" }, null, new ToolCall { Name = "run_python" }, tcs);
        Assert.Single(center.Pending);
        Assert.True(center.Decide(p.Id, true));
        Assert.False(center.Decide(p.Id, false));
        Assert.True(await tcs.Task);
        Assert.True(await resolved.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(center.Pending);
    }

    [Fact]
    public async Task ActivityLogPersistsAndFilters()
    {
        var paths = new TempPaths();
        var log = new ActivityLog(paths);
        await log.AddAsync(new Activity(DateTimeOffset.Now.AddDays(-2), ActivityKind.Routine, "old"));
        await log.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Error, "new", Ok: false));
        var today = await new ActivityLog(paths).SinceAsync(new DateTimeOffset(DateTime.Today));
        Assert.Equal("new", Assert.Single(today).Title);
        Directory.Delete(paths.DataDirectory, true);
    }

    [Fact]
    public void NextRunForSchedules()
    {
        var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero); // Friday
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 18, 0, 0, TimeSpan.Zero),
            new Routine { Schedule = RoutineSchedule.Daily, TimeOfDay = "18:00" }.NextRun(now));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero), // Monday
            new Routine { Schedule = RoutineSchedule.Weekdays, TimeOfDay = "08:00" }.NextRun(now));
        Assert.Null(new Routine { Trigger = TriggerKind.Webhook }.NextRun(now));
    }

    [Fact]
    public void RecordedStepsReadNaturally()
    {
        Assert.Equal("Type \"laptop\" into the field \"Search\"", new RecordedStep(default, "type", "the field \"Search\"", "laptop", "u").Describe());
        Assert.Equal("Type the password into the field \"Password\"", new RecordedStep(default, "type", "the field \"Password\"", null, "u", true).Describe());
        var t = SkillTeacher.Template("Find prices", "1. Go to https://shop.test", "https://shop.test");
        Assert.Contains("Start at https://shop.test", t);
        Assert.Contains("browser_fill_login", t);
    }
}

/// <summary>Telegram end to end: fake Telegram Bot API + fake Ollama, real hub, chat service and approvals.</summary>
public sealed class TelegramHubTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-tg-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly FakeServer _server = new();
    private readonly ServiceProvider _sp;

    public TelegramHubTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "fake", Kind = ProviderKind.Ollama, BaseUrl = _server.Url + "/ollama", DefaultModel = "fake:1b" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.Tools.PythonEnabled = false;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task PairsRepliesSwitchesBotsAndAnswersApprovals()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        await bots.SaveAsync(new Bot { Name = "Researcher", Memory = new() { AutoLearn = false } });
        var channels = _sp.GetRequiredService<ChannelStore>();
        await channels.UpdateAsync(c => c.TelegramApiBase = _server.Url + "/tg");
        await channels.SetTokenAsync("telegram", "123:ABC");
        var hub = _sp.GetRequiredService<ChannelHub>();
        await hub.EnsureStartedAsync();
        Assert.Equal("@locus_test_bot", hub.Adapter("telegram").Identity);

        // A stranger is told to pair; nothing else happens.
        await _server.UserSays("hello");
        Assert.Contains("isn't linked", await _server.NextReply());

        var code = hub.NewPairingCode();
        await _server.UserSays(code);
        Assert.Contains("Linked to Locus", await _server.NextReply());

        await _server.UserSays("ping");
        Assert.Equal("pong from Locus", await _server.NextReply());

        await _server.UserSays("/bot researcher");
        Assert.Contains("Now talking to", await _server.NextReply());
        await _server.UserSays("ping");
        Assert.Equal("pong from Researcher", await _server.NextReply());

        // Quick follow-up buttons under a reply.
        await _server.UserTaps("q:short");
        Assert.Equal("short from Researcher", await _server.NextReply());

        // A photo reaches the model as an image.
        await _server.UserSendsPhoto("what is this?");
        Assert.Equal("I see an image, Researcher", await _server.NextReply());

        // A group chat: both bots answer, each labelled.
        var bots2 = _sp.GetRequiredService<BotStore>();
        await bots2.SaveAsync(new Bot { Name = "Writer", Memory = new() { AutoLearn = false } });
        var all = await bots2.ListAsync();
        await _sp.GetRequiredService<ConversationStore>().SaveAsync(new Conversation { Title = "Team", BotIds = all.Select(b => b.Id).ToList() });
        await _server.UserSays("/group team");
        Assert.Contains("Now in group", await _server.NextReply());
        await _server.UserSays("hello team");
        var first = await _server.NextReply();
        var second = await _server.NextReply();
        Assert.Contains("pong from", first);
        Assert.Contains("pong from", second);
        Assert.Contains(new[] { first, second }, r => r.Contains("Researcher") && r.Contains("pong from Researcher"));
        Assert.Contains(new[] { first, second }, r => r.Contains("Writer") && r.Contains("pong from Writer"));

        // An approval request from anywhere shows up with buttons; tapping Allow resolves it.
        var center = _sp.GetRequiredService<ApprovalCenter>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = center.Register(new Conversation { Title = "desk chat" }, null, new ToolCall { Name = "run_command", ArgumentsJson = """{"command":"ls"}""" }, tcs);
        var prompt = await _server.NextApproval();
        Assert.Contains("run_command", prompt.text);
        await _server.UserTaps($"a:{pending.Id}");
        Assert.True(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task NotificationsCanBeNarrowedPerChatAndPerRoutine()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var brief = new Routine { Name = "Morning brief", Prompt = "ping" };
        var noisy = new Routine { Name = "Price watch", Prompt = "ping", NotifyChannels = false };
        var bot = new Bot { Name = "Researcher", Memory = new() { AutoLearn = false }, Routines = [brief, noisy] };
        await bots.SaveAsync(bot);
        var channels = _sp.GetRequiredService<ChannelStore>();
        await channels.UpdateAsync(c => c.TelegramApiBase = _server.Url + "/tg");
        await channels.SetTokenAsync("telegram", "123:ABC");
        var hub = _sp.GetRequiredService<ChannelHub>();
        await hub.EnsureStartedAsync();
        await _server.UserSays(hub.NewPairingCode());
        Assert.Contains("Linked to Locus", await _server.NextReply());
        var runner = _sp.GetRequiredService<RoutineRunner>();

        await _server.UserSays("/notify approvals");
        Assert.Contains("Only approval requests", await _server.NextReply());
        await runner.RunAsync(bot, brief, null, default);           // not sent: routines are off for this chat
        await _server.UserSays("/notify");
        var summary = await _server.NextReply();
        Assert.Contains("✅ approval requests", summary);
        Assert.Contains("▫️ routine results", summary);

        await _server.UserSays("/notify on");
        Assert.Contains("You'll get routine results", await _server.NextReply());
        await runner.RunAsync(bot, noisy, null, default);           // not sent: this routine keeps its results in Locus
        await runner.RunAsync(bot, brief, null, default);
        Assert.Contains("Morning brief", await _server.NextReply()); // the first thing sent is the brief

        await _server.UserSays("/quiet 00:00-23:59");
        Assert.Contains("Quiet from 00:00 to 23:59", await _server.NextReply());
        await runner.RunAsync(bot, brief, null, default);           // held: quiet hours
        await _server.UserSays("/quiet off");
        Assert.Equal("Quiet hours off.", await _server.NextReply());
    }

    public void Dispose()
    {
        _server.Dispose();
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch (IOException) { }
    }

    /// <summary>Fake Telegram Bot API (/tg/bot{token}/…) and fake Ollama (/ollama/api/…).</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _l = new();
        private readonly ConcurrentQueue<JsonObject> _updates = new();
        private readonly System.Threading.Channels.Channel<string> _replies = System.Threading.Channels.Channel.CreateUnbounded<string>();
        private readonly System.Threading.Channels.Channel<(string text, string id)> _approvals = System.Threading.Channels.Channel.CreateUnbounded<(string, string)>();
        private long _updateId = 1;
        public string Url { get; }

        public FakeServer()
        {
            var port = new Random().Next(40001, 60000);
            Url = $"http://127.0.0.1:{port}";
            _l.Prefixes.Add(Url + "/");
            _l.Start();
            _ = Task.Run(Loop);
        }

        public Task UserSays(string text)
        {
            _updates.Enqueue(new JsonObject
            {
                ["update_id"] = _updateId++,
                ["message"] = new JsonObject
                {
                    ["text"] = text,
                    ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private" },
                    ["from"] = new JsonObject { ["id"] = 7, ["first_name"] = "Ann" },
                },
            });
            return Task.CompletedTask;
        }

        public Task UserSendsPhoto(string caption)
        {
            _updates.Enqueue(new JsonObject
            {
                ["update_id"] = _updateId++,
                ["message"] = new JsonObject
                {
                    ["caption"] = caption,
                    ["photo"] = new JsonArray(new JsonObject { ["file_id"] = "small" }, new JsonObject { ["file_id"] = "big" }),
                    ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private" },
                    ["from"] = new JsonObject { ["id"] = 7, ["first_name"] = "Ann" },
                },
            });
            return Task.CompletedTask;
        }

        public Task UserTaps(string data)
        {
            _updates.Enqueue(new JsonObject
            {
                ["update_id"] = _updateId++,
                ["callback_query"] = new JsonObject
                {
                    ["id"] = "cb1", ["data"] = data,
                    ["message"] = new JsonObject { ["message_id"] = 5, ["chat"] = new JsonObject { ["id"] = 42 } },
                },
            });
            return Task.CompletedTask;
        }

        public async Task<string> NextReply() => await _replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        public async Task<(string text, string id)> NextApproval() => await _approvals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        private async Task Loop()
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        }

        private async Task Handle(HttpListenerContext c)
        {
            var path = c.Request.Url!.AbsolutePath;
            var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            if (path.StartsWith("/ollama/api/tags")) { await Send(c, """{"models":[{"name":"fake:1b"}]}"""); return; }
            if (path.StartsWith("/ollama/api/chat"))
            {
                var req = JsonNode.Parse(body)!;
                var msgs = req["messages"]!.AsArray();
                var system = msgs[0]!["content"]!.ToString();
                var last = msgs[^1]!;
                var who = system.StartsWith("You are ") ? system[8..system.IndexOf(',')] : "Locus";
                var answer = system.StartsWith("You route messages") ? "Researcher, Writer"
                    : last["images"] is JsonArray { Count: > 0 } ? $"I see an image, {who}"
                    : last["content"]!.ToString() == "Make that shorter." ? $"short from {who}"
                    : $"pong from {who}";
                await Send(c, $$"""{"message":{"role":"assistant","content":"{{answer}}"},"done":false}""" + "\n" +
                              """{"message":{"role":"assistant","content":""},"done":true,"eval_count":3,"eval_duration":1000}""" + "\n");
                return;
            }
            if (path.Contains("/file/bot")) { var bytes = Encoding.UTF8.GetBytes("JPEGDATA"); await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close(); return; }
            if (path.StartsWith("/ollama/")) { await Send(c, "{}"); return; }

            var method = path.Split('/').Last();
            var json = body.Length > 0 && body.TrimStart().StartsWith('{') ? JsonNode.Parse(body) : null;
            switch (method)
            {
                case "getFile":
                    await Send(c, """{"ok":true,"result":{"file_path":"photos/big.jpg"}}""");
                    return;
                case "getMe":
                    await Send(c, """{"ok":true,"result":{"id":1,"username":"locus_test_bot"}}""");
                    return;
                case "getUpdates":
                    var offset = json?["offset"]?.GetValue<long>() ?? 0;
                    for (var i = 0; i < 40 && _updates.IsEmpty; i++) await Task.Delay(50);
                    var batch = new JsonArray();
                    while (_updates.TryDequeue(out var u)) if (u["update_id"]!.GetValue<long>() >= offset) batch.Add(u);
                    await Send(c, new JsonObject { ["ok"] = true, ["result"] = batch }.ToJsonString());
                    return;
                case "sendMessage":
                    var text = json?["text"]?.ToString() ?? "";
                    if (json?["reply_markup"]?["inline_keyboard"] is JsonArray { Count: > 0 } kb && kb[0]![0]!["callback_data"]!.ToString().StartsWith("a:"))
                        await _approvals.Writer.WriteAsync((text, kb[0]![0]!["callback_data"]!.ToString()));
                    else if (json?["reply_to_message_id"] is null)
                        await _replies.Writer.WriteAsync(WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", "")));
                    await Send(c, """{"ok":true,"result":{"message_id":9}}""");
                    return;
                default:
                    await Send(c, """{"ok":true,"result":true}""");
                    return;
            }
        }

        private static async Task Send(HttpListenerContext c, string text)
        {
            var b = Encoding.UTF8.GetBytes(text);
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(b);
            c.Response.Close();
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }
}
