using System.Text.Json.Nodes;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Code sessions (a chat bound to a folder): scoped file tools, permission modes, task list, and chat topics.</summary>
public sealed class CodeSessionTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-code-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("hearth-repo-").FullName);
    private readonly ServiceProvider _sp;
    private readonly ScriptedTeam _llm = new();

    public CodeSessionTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: true));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_llm));
        _sp = services.BuildServiceProvider();
        Directory.CreateDirectory(Path.Combine(_root, "src", "Deep"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "src", "App.cs"), "class App\n{\n    int Count = 1;\n}\n");
        File.WriteAllText(Path.Combine(_root, "src", "Deep", "Util.ts"), "export const count = 2;\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "index.js"), "count\n");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Demo\n");
    }

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }

    private ToolContext Ctx(CodePermissionMode mode = CodePermissionMode.Ask, AppSettings? settings = null)
    {
        var code = new CodeSession { Directory = _root, Mode = mode };
        return new()
        {
            Conversation = new Conversation { Code = code }, Code = code,
            Reply = new ChatMessage { Role = ChatRole.Assistant },
            Settings = settings ?? new AppSettings(), Client = null!, Model = "m",
        };
    }

    [Fact]
    public void ToolsDependOnModeAndPlatform()
    {
        var tools = _sp.GetRequiredService<CodeTools>();
        var names = tools.For(Ctx().Code).Select(t => t.Name).ToList();
        Assert.Equal(["glob", "grep", "read", "todo_write", "edit", "write", "bash", "undo_edit"], names);
        Assert.DoesNotContain(tools.For(Ctx(CodePermissionMode.Plan).Code), t => t.Name is "edit" or "write" or "bash");
        Assert.Empty(tools.For(null));
        Assert.Empty(new CodeTools(_sp.GetRequiredService<ProjectHistory>(), _sp.GetRequiredService<CodeSandbox>(),
            new PlatformInfo("phone", true, CanRunProcesses: false)).For(Ctx().Code));
    }

    [Fact]
    public void PathsStayInsideTheFolder()
    {
        var conv = Ctx().Code;
        Assert.Null(CodeTools.Resolve(conv, "src/App.cs").error);
        Assert.Null(CodeTools.Resolve(conv, Path.Combine(_root, "README.md")).error);
        Assert.NotNull(CodeTools.Resolve(conv, "../outside.txt").error);
        Assert.NotNull(CodeTools.Resolve(conv, "/etc/passwd").error);
        Assert.NotNull(CodeTools.Resolve(conv, _root + "-evil/x").error); // a sibling with the same prefix

        var outside = Directory.CreateTempSubdirectory("hearth-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside);
            Assert.NotNull(CodeTools.Resolve(conv, "escape/secret.txt").error);
        }
        finally { Directory.Delete(outside, true); }
    }

    [Theory]
    [InlineData("**/*.cs", "src/App.cs", true)]
    [InlineData("*.cs", "src/App.cs", true)]
    [InlineData("src/*.cs", "src/App.cs", true)]
    [InlineData("src/*.cs", "src/Deep/X.cs", false)]
    [InlineData("src/**/*.{ts,tsx}", "src/Deep/Util.ts", true)]
    [InlineData("*.md", "README.md", true)]
    [InlineData("*.md", "README.mdx", false)]
    public void GlobsMatchLikeOtherTools(string pattern, string path, bool match) =>
        Assert.Equal(match, CodeTools.Glob(pattern).IsMatch(path));

    [Fact]
    public async Task GlobAndGrepSkipDependencies()
    {
        var ctx = Ctx();
        var glob = await new CodeGlobTool().ExecuteAsync(new JsonObject { ["pattern"] = "**/*" }, ctx, default);
        Assert.Contains("src/App.cs", glob.Text);
        Assert.DoesNotContain("node_modules", glob.Text);

        var grep = await new CodeGrepTool().ExecuteAsync(new JsonObject { ["pattern"] = "count", ["ignore_case"] = true }, ctx, default);
        Assert.Contains("src/App.cs:3:", grep.Text);
        Assert.Contains("src/Deep/Util.ts:1:", grep.Text);
        Assert.DoesNotContain("node_modules", grep.Text);

        var only = await new CodeGrepTool().ExecuteAsync(new JsonObject { ["pattern"] = "count", ["include"] = "*.ts" }, ctx, default);
        Assert.Equal("src/Deep/Util.ts:1: export const count = 2;", only.Text);
    }

    [Fact]
    public async Task ReadNumbersLinesAndPages()
    {
        var read = await new CodeReadTool().ExecuteAsync(new JsonObject { ["path"] = "src/App.cs", ["offset"] = 2, ["limit"] = 2 }, Ctx(), default);
        Assert.Equal("     2\t{\n     3\t    int Count = 1;\n… 1 more lines (read on with offset=4)", read.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task EditsAreExactRecordedAndUndoable()
    {
        var ctx = Ctx();
        var history = _sp.GetRequiredService<ProjectHistory>();
        var edit = new CodeEditTool(history);
        var file = Path.Combine(_root, "src", "App.cs");

        var missing = await edit.ExecuteAsync(new JsonObject { ["path"] = "src/App.cs", ["old_text"] = "nope", ["new_text"] = "x" }, ctx, default);
        Assert.True(missing.IsError);

        var ok = await edit.ExecuteAsync(new JsonObject { ["path"] = "src/App.cs", ["old_text"] = "Count = 1", ["new_text"] = "Count = 5" }, ctx, default);
        Assert.False(ok.IsError, ok.Text);
        Assert.Contains("Count = 5", File.ReadAllText(file));

        var id = (await history.ListAsync()).Single().Id;
        var undo = await new CodeUndoTool(history).ExecuteAsync(new JsonObject { ["id"] = id }, ctx, default);
        Assert.False(undo.IsError, undo.Text);
        Assert.Contains("Count = 1", File.ReadAllText(file));

        var outside = await new CodeWriteTool(history).ExecuteAsync(new JsonObject { ["path"] = "../x.txt", ["content"] = "hi" }, ctx, default);
        Assert.True(outside.IsError);
    }

    [Fact]
    public void EditsAskUnlessTheSessionAcceptsThem()
    {
        var edit = new CodeEditTool(_sp.GetRequiredService<ProjectHistory>());
        var settings = new AppSettings();
        Assert.True(PermissionCatalog.NeedsApproval(edit, settings, Ctx()));
        Assert.False(PermissionCatalog.NeedsApproval(edit, settings, Ctx(CodePermissionMode.AcceptEdits)));
        settings.Tools.Sandbox.Enabled = false;
        var bash = new CodeBashTool(_sp.GetRequiredService<CodeSandbox>());
        Assert.True(PermissionCatalog.NeedsApproval(bash, settings, Ctx(CodePermissionMode.AcceptEdits, settings)));
    }

    [Fact]
    public async Task TodoWriteReplacesTheList()
    {
        var ctx = Ctx();
        var todos = new JsonArray(
            new JsonObject { ["content"] = "Read code", ["status"] = "completed" },
            new JsonObject { ["content"] = "Fix bug", ["status"] = "in_progress" },
            new JsonObject { ["content"] = "Run tests", ["status"] = "pending" });
        var r = await new TodoWriteTool().ExecuteAsync(new JsonObject { ["todos"] = todos }, ctx, default);
        Assert.Equal("Task list updated (1/3 done).", r.Text);
        Assert.Equal([CodeTodoStatus.Completed, CodeTodoStatus.InProgress, CodeTodoStatus.Pending], ctx.Conversation.Code!.Todos.Select(t => t.Status));

        // Some models send the array as a JSON string.
        await new TodoWriteTool().ExecuteAsync(new JsonObject { ["todos"] = "[{\"content\":\"Only\",\"status\":\"pending\"}]" }, ctx, default);
        Assert.Equal("Only", ctx.Conversation.Code!.Todos.Single().Content);
    }

    [Fact]
    public async Task GuideIncludesProjectInstructionsAndPlanMode()
    {
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "Run `make test` before finishing.");
        var guide = await ChatService.CodeSessionGuideAsync(new CodeSession { Directory = _root, Mode = CodePermissionMode.Plan });
        Assert.Contains("make test", guide);
        Assert.Contains("PLAN MODE", guide);
    }

    [Theory]
    [InlineData("Job Search", "Job Search")]
    [InlineData("topic: cooking", "Cooking")] // matches the existing spelling
    [InlineData("<think>hmm</think>\nHome Server.", "Home Server")]
    [InlineData("A very long topic that is clearly a sentence", null)]
    public void TopicsAreCleaned(string raw, string? expected) =>
        Assert.Equal(expected, ChatService.Topic(raw, ["Cooking"]));

    [Fact]
    public async Task SummariesCarryTopicAndProject()
    {
        var store = _sp.GetRequiredService<ConversationStore>();
        await store.SaveAsync(new Conversation { Topic = "Coding", Code = new CodeSession { Directory = _root } });
        var s = (await store.ListAsync()).Single();
        Assert.Equal("Coding", s.Topic);
        Assert.Equal(Path.GetFileName(_root), s.Project);
    }

    [Fact]
    public void BotsWithTheCodeAbilityWorkInTheirOwnFolder()
    {
        var engineer = new Bot { Name = "Engineer", Abilities = new() { Code = true, ProjectFolder = _root, CodeMode = CodePermissionMode.AcceptEdits } };
        var plain = new Conversation { BotIds = [engineer.Id] };

        var own = CodeTools.SessionFor(plain, engineer);
        Assert.Equal(_root, own!.Directory);
        Assert.Equal(CodePermissionMode.AcceptEdits, own.Mode);

        // A code session's folder wins over the bot's default.
        var session = new Conversation { Code = new CodeSession { Directory = "/elsewhere" } };
        Assert.Equal("/elsewhere", CodeTools.SessionFor(session, engineer)!.Directory);

        Assert.Null(CodeTools.SessionFor(plain, new Bot { Abilities = new() { Code = false, ProjectFolder = _root } }));
        Assert.Null(CodeTools.SessionFor(plain, new Bot { Abilities = new() { Code = true } }));

        // The code tools belong to the Code switch.
        foreach (var t in new[] { "glob", "grep", "read", "edit", "write", "bash", "todo_write", "undo_edit" })
            Assert.Equal(nameof(BotAbilities.Code), BotAbilities.GroupOf(t));
    }

    [Fact]
    public async Task ABotsMainChatIsNeverOneOfItsCodeSessions()
    {
        var store = _sp.GetRequiredService<ConversationStore>();
        var bot = new Bot { Name = "Engineer" };
        var session = new Conversation { BotIds = [bot.Id], Code = new CodeSession { Directory = _root } };
        await store.SaveAsync(session);
        var direct = await store.GetOrCreateDirectAsync(bot);
        Assert.NotEqual(session.Id, direct.Id);
        Assert.Null(direct.Code);
    }

    [Fact]
    public async Task ACodingBotGetsTheToolsInItsOwnChatButOthersDont()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var engineer = new Bot { Name = "Engineer", Abilities = new() { Code = true, ProjectFolder = _root } };
        var writer = new Bot { Name = "Writer" };
        await bots.SaveAsync(engineer);
        await bots.SaveAsync(writer);
        var offered = new Dictionary<string, List<string>>();
        _llm.Script = (bot, _, req) =>
        {
            lock (offered) offered[bot] = req.Tools?.Select(t => t.Name).ToList() ?? [];
            return new LlmChunk("ok");
        };
        var chat = _sp.GetRequiredService<ChatService>();
        foreach (var b in new[] { engineer, writer })
        {
            var conv = new Conversation { BotIds = [b.Id], Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }] };
            await chat.RunAgentAsync(conv, b, new SendOptions("model", false, false, true), () => Task.CompletedTask, default, autoTitle: false, learn: false);
        }
        Assert.Contains("edit", offered["Engineer"]);
        Assert.Contains("todo_write", offered["Engineer"]);
        Assert.DoesNotContain("edit", offered["Writer"]);
    }
}
