using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Locus.Core;
using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.Extensions.DependencyInjection;

// Live harness eval: runs real prompts through Locus (ChatRuns -> ChatService -> your model server) on a throwaway
// copy of your settings and bots, and reports what tools ran, whether calls leaked as text, timing and the answer.
// Nothing is written to your real data folder. Each model call is logged with its size and time to first word.
//
//   dotnet run --project eval/Locus.Eval -- [case …] [--provider=oMLX] [--model=…] [--think] [--repeat=N] [--timeout=MIN]
//   dotnet run --project eval/Locus.Eval -- probe        raw requests to the server (tool calls, thinking switch, cut-offs)
//
//   dotnet run --project eval/Locus.Eval -- memory       fold sample memories into a profile, then ask a related question
//
// Cases: math datetime no-tools parallel-convert web web+math files bad-url follow-up handoff direct unit-trap
// people plan undo skill check routing contacts (none named = all; contacts only when connected).
// Full answers go to eval/answers/ (git-ignored).
var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Locus");
var data = Directory.CreateTempSubdirectory("locus-eval-").FullName;
foreach (var f in new[] { "settings.json", "vault.key", "bots.json", "skills.json" })
    if (File.Exists(Path.Combine(real, f))) File.Copy(Path.Combine(real, f), Path.Combine(data, f));

var services = new ServiceCollection();
services.AddSingleton<IAppPaths>(new Paths(data));
services.AddLocusCore(new PlatformInfo("eval", false, CanRunProcesses: true));
services.AddSingleton<Locus.Core.Llm.LlmClientFactory>();
services.AddSingleton<Locus.Core.Llm.ILlmClientFactory>(x => new Logging(x.GetRequiredService<Locus.Core.Llm.LlmClientFactory>()));
var sp = services.BuildServiceProvider();
var store = sp.GetRequiredService<SettingsStore>();
var s = await store.GetAsync();
s.AutoTitle = false; s.MemoryAutoLearn = false; s.SkillLearning.Enabled = false;
var think = args.Contains("--think");
var want = args.FirstOrDefault(a => a.StartsWith("--provider="))?.Split('=', 2)[1] ?? "oMLX";
s.ActiveProviderId = s.Providers.First(p => p.Name.StartsWith(want, StringComparison.OrdinalIgnoreCase)).Id;
await store.SaveAsync(s);
var provider = s.ActiveProvider;
var model = args.FirstOrDefault(a => a.StartsWith("--model="))?.Split('=', 2)[1] ?? provider.DefaultModel ?? "";
Console.WriteLine($"provider={provider.Name} model={model} temp={s.Temperature} maxTokens={s.MaxTokens} think={think} data={data}");

if (args.Contains("probe")) { await Probe(); return; }
if (args.Contains("memory")) { await MemoryCheck(); return; }
if (args.Contains("models"))
{
    foreach (var m in await sp.GetRequiredService<Locus.Core.Llm.ILlmClientFactory>().Create(provider).ListModelsAsync()) Console.WriteLine("  " + m.Id);
    return;
}

var cases = new List<Case>
{
    new("math", ["What's 1847 × 392, minus 17% of 5000?"], r => r.Tools.Contains("calculate") && Has(r.Answer, "723,174", "723174")),
    new("datetime", ["What's today's date, and what time is it in Tokyo right now?"], r => r.Tools.Contains("get_current_datetime")),
    new("no-tools", ["Write a haiku about autumn leaves."], r => r.Tools.Count == 0 && r.Answer.Length > 20),
    new("parallel-convert", ["Convert 26.2 miles to km and 180 lb to kg."], r => r.Tools.Count(t => t == "convert_units") >= 2 && Has(r.Answer, "42.1", "42.2") && Has(r.Answer, "81.6", "81.65")),
    new("web", ["What's the latest stable .NET release and when did it come out? Cite your source."], r => r.Tools.Contains("web_search") && r.Answer.Contains("http") || r.Answer.Contains("[1]")),
    new("web+math", ["Look up the population of Detroit and of Michigan, then tell me what percent of Michigan lives in Detroit."], r => r.Tools.Contains("web_search") && r.Answer.Contains('%')),
    new("files", ["Save a file eval/trip.md with exactly three bullet ideas for a weekend trip, then read it back and tell me how many lines it has."], r => r.Tools.Contains("write_file") && r.Tools.Contains("read_file")),
    new("bad-url", ["Fetch https://no-such-host-locus-eval.invalid/page and summarize it."], r => r.Answer.Length > 10 && !r.Answer.Contains("Summary of")),
    new("follow-up", ["What's 15% of 240?", "Now double that and add 7."], r => Has(r.Answer, "79")),
    new("handoff", ["@Chief of Staff can you research Google and its stock for me"], r => r.Handoff && r.Answer.Length > 400),
    // No @mention in a chat with only the coordinator: clear research should go straight to the researcher.
    new("direct", ["research Google and its stock for me"], r => r.Handoff && r.Answer.Length > 400),
    new("unit-trap", ["How many seconds are in a leap year? Use your tools."], r => r.Tools.Contains("calculate") && Has(r.Answer, "31,622,400", "31622400")),
    // Locus's own features.
    new("people", ["Note this with note_about: Dana Whitfield is a recruiter at Shield AI who reached out about a Director role.",
        "Who's Dana Whitfield again, and where does she work?"], r => r.Tools.Contains("note_about") && r.Tools.Contains("who_is") && Has(r.Answer, "Shield AI")),
    new("plan", ["Help me plan applying to a Director job at Anduril: show your plan with update_plan, then do the first step (look up what Anduril does)."],
        r => r.Tools.Contains("update_plan") && r.Conv.Plan.Count >= 3),
    new("undo", ["Save a file eval/undo-me.md with a two-line shopping list.", "Actually, undo that."],
        r => r.Tools.Contains("write_file") && r.Tools.Contains("undo_change") && !File.Exists(Path.Combine(data, "workspace", "eval", "undo-me.md"))),
    new("skill", ["Use the meal-plan skill: three dinners for this week for two people."], r => r.Tools.Contains("use_skill") && r.Answer.Length > 200),
    // Research, then the answer check reads the answer against the sources (fixing it when they disagree).
    new("check", ["Who is the current CEO of Shield AI, and when did they take the role? Cite your source."],
        r => r.Tools.Contains("web_search") && r.Trace.Any(t => t.StartsWith("check: ")), Setup: st => st.CheckAnswers = AnswerCheck.AfterResearch),
    // Automatic model choice: a hard request goes to the strong model and says why.
    new("routing", ["Compare the pros and cons of three ways to structure a hybrid search index, in depth."],
        r => r.Trace.Any(t => t.StartsWith("route:")) && r.Answer.Length > 300, Setup: st =>
        {
            st.ModelRouting = new ModelRoutingSettings { Enabled = true, FastProviderId = st.ActiveProviderId, FastModel = model,
                StrongProviderId = st.ActiveProviderId, StrongModel = model };
        }),
    // The whole team: the coordinator splits the work, specialists build on each other, the reviewer checks, one answer.
    new("team", ["Find out Shield AI's most recent funding round, then write me a three-sentence paragraph about it I could use in a cover letter."],
        r => r.Conv.Messages.Where(m => m.Role == ChatRole.Assistant && m.Content.Length > 0).Select(m => m.BotId).Distinct().Count() >= 3
             && r.Answer.Length > 150),
    new("contacts", ["Find my contact Mitch and give me their email."], r => r.Tools.Contains("find_contact"), Needs: "find_contact"),
};
var only = args.Where(a => !a.StartsWith("--") && a != "run").ToHashSet();
var repeat = args.FirstOrDefault(a => a.StartsWith("--repeat="))?.Split('=')[1] is { } rp ? int.Parse(rp) : 1;
var timeout = args.FirstOrDefault(a => a.StartsWith("--timeout="))?.Split('=')[1] is { } to ? double.Parse(to) : 12;
var answersDir = Path.Combine(AppContext.BaseDirectory, "../../../../answers");
var runs = sp.GetRequiredService<ChatRuns>();
var results = new List<Result>();
var toolNames = (await sp.GetRequiredService<Locus.Core.Tools.ToolRegistry>().GetToolsAsync(s, default)).Select(t => t.Name).ToHashSet();
foreach (var c in cases.Where(c => only.Count == 0 || only.Contains(c.Name)))
for (var i = 0; i < repeat; i++)
{
    if (c.Needs is not null && !toolNames.Contains(c.Needs)) { Console.WriteLine($"\n=== {c.Name}: SKIP ({c.Needs} isn't available here)"); break; }
    // Some cases change a setting for themselves (answer check, model routing); put it back afterwards.
    var before = System.Text.Json.JsonSerializer.Serialize(await store.GetAsync());
    if (c.Setup is not null) { var st = await store.GetAsync(); c.Setup(st); await store.SaveAsync(st); }
    var conv = new Conversation();
    if (c.Name is "handoff" or "direct" or "team")
    {
        var team = await sp.GetRequiredService<BotStore>().ListAsync();
        conv.BotIds = [team.First(b => b.IsCoordinator).Id];
    }
    var sw = Stopwatch.StartNew();
    string? err = null;
    try
    {
        foreach (var prompt in c.Prompts)
        {
            conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = prompt });
            await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeout));
            var run = runs.ReplyAsync(conv, new SendOptions(model, think, false, true));
            // Nobody is here to approve actions (e.g. saving a memory after reading web pages): decline them, as a
            // user who ignores the card would, so the run carries on instead of waiting until the timeout.
            while (!run.IsCompleted)
            {
                foreach (var tc in conv.Messages.ToArray().SelectMany(m => m.ToolCalls.ToArray()).Where(t => t.Status == ToolCallStatus.AwaitingApproval))
                    if (tc.Approval?.TrySetResult(false) == true) Console.WriteLine($"  [declined] {tc.Name} asked for approval");
                await Task.WhenAny(run, Task.Delay(500, cts.Token));
            }
            await run.WaitAsync(cts.Token);
        }
    }
    catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
    var last = conv.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant) ?? new ChatMessage();
    var tools = conv.Messages.Where(m => m.Role == ChatRole.Assistant).SelectMany(m => m.ToolCalls).ToList();
    var r = new Result(c.Name, sw.Elapsed.TotalSeconds, tools.Where(t => t.Status == ToolCallStatus.Done).Select(t => t.Name).ToList(), last.Content ?? "",
        err ?? last.Error, last.Notice,
        tools.Count(t => t.Status != ToolCallStatus.Done), last.Thinking?.Length ?? 0,
        last.Stats?.CompletionTokens, last.Trace.Count(t => t.Kind == "model"))
    {
        Conv = conv,
        Trace = conv.Messages.Where(m => m.Role == ChatRole.Assistant).SelectMany(m => m.Trace).Select(t => t.Kind == "route" ? "route: " + t.Name : t.Name).ToList(),
    };
    if (c.Setup is not null) await store.SaveAsync(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(before)!);
    var said = conv.Messages.Where(m => m.Role == ChatRole.Assistant && m.Content.Length > 0).ToList(); // a reply can carry a notice too
    if (c.Name is "handoff" or "direct" or "team")
    {
        var team = await sp.GetRequiredService<BotStore>().ListAsync();
        var chiefId = team.First(b => b.IsCoordinator).Id;
        var first = said.FirstOrDefault();
        // Handed off by @mention (short message, no tools), asked via message_bot only, or (direct) skipped
        // entirely so a specialist answered first — never did the work itself.
        r = r with { Handoff = first is not null && (first.BotId != chiefId && c.Name == "direct" || first.BotId == chiefId && (
            first.ToolCalls.Count == 0 && first.Content.Contains('@') && first.Content.Length < 800 && said.Any(m => m.BotId != chiefId)
            || first.ToolCalls.Count > 0 && first.ToolCalls.All(t => t.Name == "message_bot"))) };
        foreach (var m in said) Console.WriteLine($"  [{team.FirstOrDefault(b => b.Id == m.BotId)?.Name}] tools={m.ToolCalls.Count} {Short(m.Content, 300)}");
    }
    r = r with { Pass = err is null && c.Check(r) };
    var leak = Leak(r.Answer);
    results.Add(r with { Leak = leak });
    Console.WriteLine($"\n=== {c.Name} #{i + 1}: {(r.Pass ? "PASS" : "FAIL")}{(leak ? " LEAK" : "")} {r.Secs:F0}s rounds={r.Rounds} thinkChars={r.ThinkChars} outTok={r.OutTokens}");
    Console.WriteLine($"tools: {string.Join(", ", tools.Select(t => $"{t.Name}[{t.Status}]{Short(t.ArgumentsJson, 80)}"))}");
    foreach (var t in tools.Where(t => t.Status != ToolCallStatus.Done)) Console.WriteLine($"  !! {t.Name}: {Short(t.Result, 200)}");
    if (r.Error is not null) Console.WriteLine($"error: {r.Error}");
    if (r.Notice is not null) Console.WriteLine($"notice: {r.Notice}");
    if (last.Checked is not null) Console.WriteLine($"check fixed: {Short(last.Checked, 300)}");
    Console.WriteLine($"answer: {Short(r.Answer, 600)}");
    Directory.CreateDirectory(answersDir);
    File.WriteAllText(Path.Combine(answersDir, $"{want}-{c.Name}-{i + 1}.md"), string.Join("\n\n----\n\n", conv.Messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Content + "\n\n[tools] " + string.Join("; ", m.ToolCalls.Select(t => $"{t.Name}({t.ArgumentsJson}) -> {Short(t.Result, 150)}")))));
}
Console.WriteLine($"\nSUMMARY {results.Count(r => r.Pass)}/{results.Count} pass, leaks={results.Count(r => r.Leak)}, avg {results.Average(r => r.Secs):F0}s");
foreach (var g in results.GroupBy(r => r.Name)) Console.WriteLine($"  {g.Key,-18} {g.Count(r => r.Pass)}/{g.Count()}  avg {g.Average(r => r.Secs):F0}s");
await sp.DisposeAsync();
try { Directory.Delete(data, true); } catch { }

static bool Has(string s, params string[] any) => any.Any(a => s.Contains(a));
static bool Leak(string s) => s.Contains("<tool_call") || s.Contains("function_call") || s.Contains("<tool_result") || s.Contains("\"arguments\"");
static string Short(string? s, int n) => s is null ? "" : (s.Length > n ? s[..n] + "…" : s).Replace("\n", " ⏎ ");

async Task MemoryCheck()
{
    // Long-term memory: fold sample memories into the profile, then see what a later question brings back.
    var mem = sp.GetRequiredService<MemoryStore>();
    foreach (var f in new[]
    {
        "Director of Advanced Software at Detroit Defense", "Job hunting; salary floor $230k",
        "Target sectors: defense tech, national security, automotive/mobility AI", "Doesn't eat chicken",
        "Cooks dinner for 2 people and is a good cook; fine with longer recipes", "Prefers short, direct answers",
        "Asked once what the weather was in Detroit", "Has a résumé draft for a Shield AI Director role",
    }) await mem.AddAsync(f);
    var curator = sp.GetRequiredService<MemoryCurator>();
    var sw = Stopwatch.StartNew();
    var (folded, updated) = await curator.ConsolidateAsync();
    Console.WriteLine($"fold: {folded} folded, updated={updated}, {sw.Elapsed.TotalSeconds:F0}s\n--- profile ---\n{(await curator.ProfileAsync()).Text}\n--- still recent ---");
    foreach (var m in (await mem.ListAsync()).Where(m => !m.Archived)) Console.WriteLine("  " + m.Text);
    var conv = new Conversation();
    conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Plan three dinners for this week." });
    await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);
    await sp.GetRequiredService<ChatRuns>().ReplyAsync(conv, new SendOptions(model, think, false, false));
    Console.WriteLine($"--- dinner answer ---\n{Short(conv.Messages.Last().Content, 700)}");
}

async Task Probe()
{
    // Raw requests to the server: native tool calls? does enable_thinking work? finish_reason on cut-offs?
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var url = provider.BaseUrl.TrimEnd('/') + "/chat/completions";
    var tool = JsonNode.Parse("""[{"type":"function","function":{"name":"calculate","description":"Evaluate a math expression","parameters":{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}}}]""");
    async Task Ask(string label, JsonObject extra)
    {
        var body = new JsonObject
        {
            ["model"] = model, ["stream"] = false, ["temperature"] = 0.7,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "What is 1847*392? Use the tool." }),
            ["tools"] = tool!.DeepClone(),
        };
        foreach (var (k, v) in extra) body[k] = v?.DeepClone();
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(provider.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        var sw = Stopwatch.StartNew();
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var n = JsonNode.Parse(text);
        var ch = n?["choices"]?[0];
        var m = ch?["message"];
        Console.WriteLine($"\n--- {label}: {(int)res.StatusCode} {sw.Elapsed.TotalSeconds:F1}s finish={ch?["finish_reason"]} usage={n?["usage"]?.ToJsonString()}");
        Console.WriteLine($"tool_calls: {m?["tool_calls"]?.ToJsonString()}");
        Console.WriteLine($"reasoning: {Short((m?["reasoning_content"] ?? m?["reasoning"])?.ToString(), 200)}");
        Console.WriteLine($"content: {Short(m?["content"]?.ToString(), 300)}");
        if (!res.IsSuccessStatusCode) Console.WriteLine(Short(text, 400));
    }
    await Ask("default", new JsonObject());
    await Ask("thinking off (chat_template_kwargs)", new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } });
    await Ask("thinking on (chat_template_kwargs)", new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true } });
    await Ask("max_tokens 40", new JsonObject { ["max_tokens"] = 40 });
}

sealed record Case(string Name, string[] Prompts, Func<Result, bool> Check, Action<AppSettings>? Setup = null, string? Needs = null);
sealed record Result(string Name, double Secs, List<string> Tools, string Answer, string? Error, string? Notice, int FailedTools, int ThinkChars, int? OutTokens, int Rounds)
{
    public bool Pass { get; init; }
    public bool Leak { get; init; }
    public bool Handoff { get; init; }
    public Conversation Conv { get; init; } = new();
    public List<string> Trace { get; init; } = [];
}
sealed class Paths(string dir) : IAppPaths { public string DataDirectory => dir; }

sealed class Logging(Locus.Core.Llm.ILlmClientFactory inner) : Locus.Core.Llm.ILlmClientFactory
{
    public Locus.Core.Llm.ILlmClient Create(ProviderProfile p) => new C(inner.Create(p));
    sealed class C(Locus.Core.Llm.ILlmClient c) : Locus.Core.Llm.ILlmClient
    {
        public Task<IReadOnlyList<Locus.Core.Models.ModelInfo>> ListModelsAsync(CancellationToken ct = default) => c.ListModelsAsync(ct);
        public Task<float[][]> EmbedAsync(string m, IReadOnlyList<string> i, CancellationToken ct = default) => c.EmbedAsync(m, i, ct);
        public Task<Locus.Core.Models.ModelCapabilities?> GetCapabilitiesAsync(string m, CancellationToken ct = default) => c.GetCapabilitiesAsync(m, ct);
        public async IAsyncEnumerable<Locus.Core.Llm.LlmChunk> StreamChatAsync(Locus.Core.Llm.LlmRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var sys = r.Messages.FirstOrDefault(m => m.Role == "system")?.Content.Length ?? 0;
            var all = r.Messages.Sum(m => m.Content.Length);
            var tools = r.Tools is null ? 0 : System.Text.Json.JsonSerializer.Serialize(r.Tools.Select(t => new { t.Name, t.Description, P = t.Parameters.ToJsonString() })).Length;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double? first = null;
            Locus.Core.Models.LlmStats? st = null;
            await foreach (var ch in c.StreamChatAsync(r, ct))
            {
                first ??= sw.Elapsed.TotalSeconds;
                if (ch.Stats is not null) st = ch.Stats;
                yield return ch;
            }
            Console.WriteLine($"  [call] msgs={r.Messages.Count} sysChars={sys} msgChars={all} tools={r.Tools?.Count ?? 0} toolChars={tools} promptTok={st?.PromptTokens} outTok={st?.CompletionTokens} first={first:F1}s total={sw.Elapsed.TotalSeconds:F1}s");
        }
    }
}
