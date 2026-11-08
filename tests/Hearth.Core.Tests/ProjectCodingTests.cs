using System.Text.Json.Nodes;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Coding in editable project folders (approve, diff, undo) and the code sandbox (no approval inside it).</summary>
public sealed class ProjectCodingTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-coding-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly string _project = LinkedFolders.Normalize(Directory.CreateTempSubdirectory("hearth-project-").FullName);
    private ServiceProvider? _sp;

    private ServiceProvider Services(CodeSandbox? sandbox = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: true));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(new ScriptedTeam()));
        if (sandbox is not null) services.Replace(ServiceDescriptor.Singleton(sandbox));
        _sp = services.BuildServiceProvider();
        return _sp;
    }

    private ToolContext Ctx(IServiceProvider sp, AppSettings? s = null) => new()
    {
        Conversation = new Conversation(), Reply = new ChatMessage { Role = ChatRole.Assistant },
        Settings = s ?? sp.GetRequiredService<SettingsStore>().GetAsync().GetAwaiter().GetResult(), Client = null!, Model = "m",
    };

    private async Task<LinkedFolder> LinkAsync(IServiceProvider sp, bool editable)
    {
        var folders = sp.GetRequiredService<LinkedFolders>();
        Assert.Null(await folders.AddAsync(_project));
        var f = (await folders.ListAsync()).Single();
        await folders.SetEditableAsync(f.Id, editable);
        return (await folders.ListAsync()).Single();
    }

    [Fact]
    public async Task EditsNeedAnEditableFolderAreExactAndCanBeUndone()
    {
        var sp = Services();
        var file = Path.Combine(_project, "Program.cs");
        File.WriteAllText(file, "int a = 1;\nint b = 1;\nConsole.WriteLine(a + b);\n");
        await LinkAsync(sp, editable: false);
        var folders = sp.GetRequiredService<LinkedFolders>();
        var history = sp.GetRequiredService<ProjectHistory>();
        var edit = new EditProjectFileTool(folders, history);
        var ctx = Ctx(sp);

        var readOnly = await edit.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "int a = 1;", ["new_text"] = "int a = 2;" }, ctx, default);
        Assert.True(readOnly.IsError);
        Assert.Contains("read-only", readOnly.Text);

        var id = (await folders.ListAsync()).Single().Id;
        await folders.SetEditableAsync(id, true);
        Assert.True((await edit.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = " = 1;", ["new_text"] = " = 5;" }, ctx, default)).IsError); // ambiguous
        Assert.True((await edit.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "nope", ["new_text"] = "x" }, ctx, default)).IsError);
        var ok = await edit.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "int a = 1;", ["new_text"] = "int a = 2;" }, ctx, default);
        Assert.False(ok.IsError, ok.Text);
        Assert.StartsWith("int a = 2;\nint b = 1;", File.ReadAllText(file));

        // A new file, then undo both (newest first per file).
        var write = new WriteProjectFileTool(folders, history);
        var created = Path.Combine(_project, "src", "Util.cs");
        Assert.False((await write.ExecuteAsync(new JsonObject { ["path"] = created, ["content"] = "class Util {}" }, ctx, default)).IsError);
        Assert.True(File.Exists(created));
        var changes = await history.ListAsync();
        Assert.Equal(2, changes.Count);

        var undo = new UndoProjectChangeTool(folders, history);
        foreach (var c in changes) Assert.False((await undo.ExecuteAsync(new JsonObject { ["id"] = c.Id }, ctx, default)).IsError);
        Assert.Equal("int a = 1;\nint b = 1;\nConsole.WriteLine(a + b);\n", File.ReadAllText(file));
        Assert.False(File.Exists(created)); // created by the bot: undo removes it
        Assert.Empty(await history.ListAsync());
    }

    [Fact]
    public async Task UndoGoesNewestFirstAndHiddenFilesAreOffLimits()
    {
        var sp = Services();
        var file = Path.Combine(_project, "a.txt");
        File.WriteAllText(file, "one");
        await LinkAsync(sp, editable: true);
        var folders = sp.GetRequiredService<LinkedFolders>();
        var history = sp.GetRequiredService<ProjectHistory>();
        var edit = new EditProjectFileTool(folders, history);
        var ctx = Ctx(sp);
        await edit.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "one", ["new_text"] = "two" }, ctx, default);
        await Task.Delay(5);
        await edit.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "two", ["new_text"] = "three" }, ctx, default);
        var first = (await history.ListAsync()).OrderBy(c => c.At).First();
        Assert.NotNull(await history.UndoAsync(first.Id)); // the newer change must go first
        Assert.Equal("three", File.ReadAllText(file));

        var write = new WriteProjectFileTool(folders, history);
        foreach (var bad in new[] { Path.Combine(_project, ".env"), Path.Combine(_project, ".git", "config"), Path.Combine(Path.GetTempPath(), "elsewhere.txt") })
            Assert.True((await write.ExecuteAsync(new JsonObject { ["path"] = bad, ["content"] = "x" }, ctx, default)).IsError, bad);
    }

    [Fact]
    public async Task FileChangesAlwaysAskAndAreToolsOnlyForEditableFolders()
    {
        var sp = Services();
        var settings = await sp.GetRequiredService<SettingsStore>().GetAsync();
        var registry = sp.GetRequiredService<ToolRegistry>();
        Assert.DoesNotContain(await registry.GetToolsAsync(settings, default), t => t.Name == "edit_project_file");
        await LinkAsync(sp, editable: false);
        Assert.DoesNotContain(await registry.GetToolsAsync(settings, default), t => t.Name == "edit_project_file");
        await sp.GetRequiredService<LinkedFolders>().SetEditableAsync((await sp.GetRequiredService<LinkedFolders>().ListAsync()).Single().Id, true);
        var tools = await registry.GetToolsAsync(settings, default);
        var edit = tools.Single(t => t.Name == "edit_project_file");
        Assert.True(PermissionCatalog.NeedsApproval(edit, settings, Ctx(sp)));
        Assert.Equal(("projects", true), PermissionCatalog.Classify(edit));
        Assert.Contains(tools, t => t.Name == "run_in_project");
        var bot = new Bot { Abilities = new BotAbilities { Files = true, Code = false } };
        Assert.True(bot.Abilities.Allows("edit_project_file", bot.Memory));
        Assert.False(bot.Abilities.Allows("run_in_project", bot.Memory));
    }

    [Fact]
    public async Task CodeInTheSandboxDoesNotAskButOnThisComputerItDoes()
    {
        var withDocker = new CodeSandbox(new PlatformInfo("test", false, CanRunProcesses: true), "/usr/local/bin/docker");
        var sp = Services(withDocker);
        var settings = await sp.GetRequiredService<SettingsStore>().GetAsync();
        var computer = sp.GetRequiredService<IBotComputer>();
        var python = new PythonTool(withDocker, computer);
        var command = new RunCommandTool(computer, withDocker);
        var ctx = Ctx(sp, settings);

        Assert.False(PermissionCatalog.NeedsApproval(python, settings, ctx));
        Assert.False(PermissionCatalog.NeedsApproval(command, settings, ctx));
        ctx.TaintedBy = "the web"; // even after reading a web page: nothing on this computer can change
        Assert.False(ContentTrust.ShouldHold(python, ctx));

        settings.Tools.Sandbox.Enabled = false;
        Assert.True(PermissionCatalog.NeedsApproval(python, settings, ctx));
        Assert.True(PermissionCatalog.NeedsApproval(command, settings, ctx));

        var noDocker = new CodeSandbox(new PlatformInfo("test", false, CanRunProcesses: true), null);
        settings.Tools.Sandbox.Enabled = true;
        Assert.True(PermissionCatalog.NeedsApproval(new PythonTool(noDocker, computer), settings, ctx)); // no Docker: asks
    }

    [Fact]
    public async Task ProjectCommandsRunInTheFolderOnThisComputerWhenNoSandbox()
    {
        if (OperatingSystem.IsWindows()) return;
        var noDocker = new CodeSandbox(new PlatformInfo("test", false, CanRunProcesses: true), null);
        var sp = Services(noDocker);
        File.WriteAllText(Path.Combine(_project, "marker.txt"), "hello");
        await LinkAsync(sp, editable: true);
        var run = new RunInProjectTool(sp.GetRequiredService<LinkedFolders>(), noDocker);
        var ctx = Ctx(sp);
        Assert.True(run.RequiresApprovalFor(ctx));
        var r = await run.ExecuteAsync(new JsonObject { ["folder"] = _project, ["command"] = "cat marker.txt" }, ctx, default);
        Assert.False(r.IsError, r.Text);
        Assert.Contains("hello", r.Text);
        Assert.Contains("on this computer", r.Text);
        Assert.True((await run.ExecuteAsync(new JsonObject { ["folder"] = Path.GetTempPath(), ["command"] = "ls" }, ctx, default)).IsError);
    }

    /// <summary>Real Docker (set HEARTH_DOCKER_TESTS=1): code runs isolated and sees only what it's given.</summary>
    [Fact]
    public async Task RealSandboxIsIsolated()
    {
        if (Environment.GetEnvironmentVariable("HEARTH_DOCKER_TESTS") != "1") return;
        var sandbox = new CodeSandbox(new PlatformInfo("test", false, CanRunProcesses: true));
        var s = new SandboxSettings();
        var workspace = Directory.CreateTempSubdirectory("hearth-ws-").FullName;
        File.WriteAllText(Path.Combine(workspace, "data.txt"), "from the workspace");
        try
        {
            var py = await sandbox.RunAsync(s, workspace, "python3 -", "import os\nprint(open('data.txt').read())\nprint(os.getuid())\nopen('out.txt','w').write('made in sandbox')");
            Assert.Equal(0, py.ExitCode);
            Assert.Contains("from the workspace", py.Stdout);
            Assert.Equal("made in sandbox", File.ReadAllText(Path.Combine(workspace, "out.txt"))); // workspace is shared

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var peek = await sandbox.RunAsync(s, workspace, $"ls '{home}' 2>&1; cat /proc/self/status | grep CapEff");
            Assert.Contains("No such file", peek.Stdout); // the host's files aren't there
            Assert.Contains("0000000000000000", peek.Stdout); // no capabilities

            var slow = await sandbox.RunAsync(s, workspace, "sleep 30", timeout: TimeSpan.FromSeconds(3));
            Assert.True(slow.TimedOut);

            File.WriteAllText(Path.Combine(_project, "proj.txt"), "project file");
            var proj = await sandbox.RunAsync(s, workspace, "cat proj.txt && ls /workspace", projectPath: _project);
            Assert.Contains("project file", proj.Stdout);
        }
        finally { Directory.Delete(workspace, true); }
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var d in new[] { _paths.DataDirectory, _project })
            try { Directory.Delete(d, true); } catch { }
    }
}
