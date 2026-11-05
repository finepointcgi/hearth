using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;

namespace Locus.Core.Tests;

/// <summary>Scripts that call tools: only safe tools, a call limit, and taint carried over.</summary>
public sealed class ScriptToolTests
{
    private sealed class FakeTool(string name, bool approval, string result = "done") : ITool
    {
        public int Calls;
        public string Name => name;
        public string DisplayName => name;
        public string Icon => "";
        public string Description => name + " tool";
        public JsonObject Parameters => new();
        public bool RequiresApproval => approval;
        public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolResult($"{result}:{args.Str("q")}"));
        }
    }

    private static ToolContext Ctx(params ITool[] tools) => new()
    {
        Conversation = new Conversation(), Reply = new ChatMessage(), Settings = new AppSettings(), Client = null!, Model = "m",
        Tools = tools.ToDictionary(t => t.Name),
    };

    [Fact]
    public async Task ScriptsCallOnlySafeToolsAndCarryTaint()
    {
        var fetch = new FakeTool("fetch_url", approval: false, "page");
        var send = new FakeTool("gmail_send", approval: true);
        var bots = new FakeTool("message_bot", approval: false);
        var ctx = Ctx(fetch, send, bots, new CalculatorTool());
        var calls = new List<string>();

        Assert.Equal(["fetch_url", "calculate"], ScriptTool.Callable(ctx).Select(t => t.Name).Order().Reverse());
        var ok = JsonNode.Parse(await ScriptTool.HandleAsync("""{"name":"fetch_url","args":{"q":"x"}}""", ctx, calls, default))!;
        Assert.Equal("page:x", ok["result"]!.ToString());
        Assert.Equal("the web", ctx.TaintedBy); // reading the web from a script taints the run like reading it directly

        var denied = JsonNode.Parse(await ScriptTool.HandleAsync("""{"name":"gmail_send","args":{}}""", ctx, calls, default))!;
        Assert.Contains("can't be called from a script", denied["error"]!.ToString());
        Assert.Equal(0, send.Calls);
        Assert.Contains("can't be called", JsonNode.Parse(await ScriptTool.HandleAsync("""{"name":"message_bot","args":{}}""", ctx, calls, default))!["error"]!.ToString());
        Assert.Contains("No tool", JsonNode.Parse(await ScriptTool.HandleAsync("""{"name":"nope","args":{}}""", ctx, calls, default))!["error"]!.ToString());
        Assert.Contains("fetch_url", JsonNode.Parse(await ScriptTool.HandleAsync("""{"name":"__available__"}""", ctx, calls, default))!["result"]!.ToString());

        for (var i = 0; i < ScriptTool.MaxCalls; i++) await ScriptTool.HandleAsync("""{"name":"fetch_url","args":{}}""", ctx, calls, default);
        Assert.Contains("Too many", JsonNode.Parse(await ScriptTool.HandleAsync("""{"name":"fetch_url","args":{}}""", ctx, calls, default))!["error"]!.ToString());
    }

    /// <summary>Real Docker (LOCUS_DOCKER_TESTS=1): a script loops over tool calls and prints the result.</summary>
    [Fact]
    public async Task RealScriptCallsToolsFromTheSandbox()
    {
        if (Environment.GetEnvironmentVariable("LOCUS_DOCKER_TESTS") != "1") return;
        var data = Directory.CreateTempSubdirectory("locus-script-data-").FullName;
        var computer = new NoBotComputer(new FixedPaths(data));
        var workspace = computer.WorkspaceDirectory;
        var sandbox = new CodeSandbox(new PlatformInfo("test", false, CanRunProcesses: true));
        var ctx = Ctx(new CalculatorTool(), new FakeTool("gmail_send", approval: true));
        try
        {
            var r = await new ScriptTool(sandbox, computer).ExecuteAsync(new JsonObject
            {
                ["code"] = """
                total = 0
                for n in range(1, 6):
                    total += float(tools.calculate(expression=f"{n}^2").split("=")[-1])
                try:
                    tools.gmail_send(to="x")
                except RuntimeError as e:
                    print("blocked:", "can't be called" in str(e))
                print("sum of squares:", int(total))
                """,
            }, ctx, default);
            Assert.False(r.IsError, r.Text);
            Assert.Contains("sum of squares: 55", r.Text);
            Assert.Contains("blocked: True", r.Text);
            Assert.Contains("calculate ×5", r.Text);
        }
        finally { Directory.Delete(data, true); }
    }

    private sealed class FixedPaths(string dir) : IAppPaths
    {
        public string DataDirectory => dir;
    }
}
