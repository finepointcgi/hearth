using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Services.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Hearth.Core.Tests;

/// <summary>Running outside agents: ACP (with a fake agent), Claude Code's output, permissions, and looking after their memory and skills.</summary>
public sealed class AgentHubTests : IAsyncDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-agents-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ServiceProvider _sp;
    private readonly string _work;

    public AgentHubTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: true));
        _sp = services.BuildServiceProvider();
        _work = Directory.CreateDirectory(Path.Combine(_paths.DataDirectory, "work")).FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }

    /// <summary>A tiny ACP agent: says hello, asks to edit a file, reports whether it was allowed, then ends the turn.</summary>
    private const string FakeAgent = """
        #!/usr/bin/env python3
        import json, sys
        def send(o): sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()
        sid = "s1"; asks = 0
        for line in sys.stdin:
            m = json.loads(line)
            meth = m.get("method")
            if meth == "initialize":
                send({"jsonrpc": "2.0", "id": m["id"], "result": {"protocolVersion": 1, "agentCapabilities": {"loadSession": False}}})
            elif meth == "session/new":
                send({"jsonrpc": "2.0", "id": m["id"], "result": {"sessionId": sid}})
            elif meth == "session/prompt":
                text = m["params"]["prompt"][0]["text"]
                def upd(u): send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": sid, "update": u}})
                upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "Hello, "}})
                upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "got: " + text}})
                upd({"sessionUpdate": "tool_call", "toolCallId": "t1", "title": "Read notes.txt", "kind": "read", "status": "completed"})
                upd({"sessionUpdate": "plan", "entries": [{"content": "Look", "status": "completed"}, {"content": "Edit", "status": "pending"}]})
                asks += 1
                send({"jsonrpc": "2.0", "id": 1000 + asks, "method": "session/request_permission", "params": {"sessionId": sid,
                      "toolCall": {"toolCallId": "t2", "title": "Edit app.py", "kind": "edit", "rawInput": {"path": "app.py"}},
                      "options": [{"optionId": "yes", "name": "Allow", "kind": "allow_once"}, {"optionId": "no", "name": "Reject", "kind": "reject_once"}]}})
                answer = json.loads(sys.stdin.readline())
                chosen = answer["result"]["outcome"].get("optionId", "cancelled")
                upd({"sessionUpdate": "tool_call_update", "toolCallId": "t2", "status": "completed" if chosen == "yes" else "failed"})
                upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": " Edit was " + chosen + "."}})
                send({"jsonrpc": "2.0", "id": m["id"], "result": {"stopReason": "end_turn"}})
        """;

    private string? FakeAgentPath()
    {
        if (OperatingSystem.IsWindows() || AgentCatalog.Locate("python3") is null) return null;
        var path = Path.Combine(_paths.DataDirectory, "fake-agent");
        File.WriteAllText(path, FakeAgent.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private async Task<AgentHub> HubAsync(AgentMode mode)
    {
        var settings = _sp.GetRequiredService<SettingsStore>();
        var s = await settings.GetAsync();
        s.Agents.ByAgent["hermes"] = new AgentSettings { Mode = mode, MaxMinutes = 2 };
        await settings.SaveAsync(s);
        var hub = _sp.GetRequiredService<AgentHub>();
        var fake = FakeAgentPath();
        hub.Locator = def => def.Id == "hermes" ? fake : null;
        return hub;
    }

    [Fact]
    public async Task AnAcpAgentStreamsItsWorkAndAsksYouBeforeChangingFiles()
    {
        var hub = await HubAsync(AgentMode.Ask);
        if (hub.Locator!(AgentCatalog.Find("hermes")!) is null) return; // needs python3
        var approvals = _sp.GetRequiredService<ApprovalCenter>();
        PendingApproval? asked = null;
        approvals.Requested += p => { asked = p; approvals.Decide(p.Id, true); };

        var run = await hub.StartAsync("hermes", "fix the bug", _work);
        run = await hub.WaitAsync(run.Id, TimeSpan.FromSeconds(30));

        Assert.Equal(AgentRunStatus.Done, run.Status);
        Assert.Equal("Hello, got: fix the bug\n\nEdit was yes.", run.Result);
        Assert.NotNull(asked);
        Assert.Equal("Hermes: Edit app.py", asked!.ToolName);
        Assert.Equal(run.ApprovalKey, asked.ConversationId);
        Assert.Contains(run.Steps, s => s is { Kind: AgentStep.Tool, Content: "Read notes.txt", Status: "completed" });
        Assert.Contains(run.Steps, s => s is { Kind: AgentStep.Tool, Content: "Edit app.py", Status: "completed" });
        Assert.Contains(run.Steps, s => s.Kind == AgentStep.Plan && s.Content == "☑ Look\n☐ Edit");
        Assert.Contains(run.Steps, s => s.Content == "You allowed: Edit app.py");

        // A follow-up goes to the same open session.
        await hub.SendAsync(run.Id, "again");
        run = await hub.WaitAsync(run.Id, TimeSpan.FromSeconds(30));
        Assert.Equal("Hello, got: again\n\nEdit was yes.", run.Result);
        Assert.Equal(2, run.Steps.Count(s => s.Kind == AgentStep.You));
        Assert.Equal("Hello, got: again\n\nEdit was yes.", (await _sp.GetRequiredService<AgentRunStore>().GetAsync(run.Id))!.Result); // saved
    }

    [Fact]
    public async Task WhatTheModeAllowsIsAnsweredWithoutAskingAndNoOneToAskMeansNo()
    {
        var hub = await HubAsync(AgentMode.AcceptEdits);
        if (hub.Locator!(AgentCatalog.Find("hermes")!) is null) return;
        var approvals = _sp.GetRequiredService<ApprovalCenter>();
        var asked = 0;
        approvals.Requested += _ => asked++;

        var run = await hub.WaitAsync((await hub.StartAsync("hermes", "go", _work)).Id, TimeSpan.FromSeconds(30));
        Assert.EndsWith("Edit was yes.", run.Result);
        Assert.Equal(0, asked);

        await HubAsync(AgentMode.Ask);
        run = await hub.WaitAsync((await hub.StartAsync("hermes", "go", _work, unattended: true)).Id, TimeSpan.FromSeconds(30));
        Assert.EndsWith("Edit was no.", run.Result);
        Assert.Contains(run.Steps, s => s.Content == "Refused, no one to ask: Edit app.py");
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData(AgentMode.Ask, "read", true)]
    [InlineData(AgentMode.Ask, "edit", null)]
    [InlineData(AgentMode.Ask, "execute", null)]
    [InlineData(AgentMode.AcceptEdits, "edit", true)]
    [InlineData(AgentMode.AcceptEdits, "execute", null)]
    [InlineData(AgentMode.FullAuto, "execute", true)]
    [InlineData(AgentMode.ReadOnly, "search", true)]
    [InlineData(AgentMode.ReadOnly, "edit", false)]
    public void EachModeAllowsSomeStepsOutright(AgentMode mode, string kind, bool? allowed) => Assert.Equal(allowed, AgentHub.Allowed(mode, kind));

    [Fact]
    public void ClaudeCodesOutputBecomesStepsAndRefusalsBecomeAQuestion()
    {
        var run = new AgentRun { Folder = "/work/app", Mode = AgentMode.Ask };
        var denials = new List<(string, JsonNode?)>();
        string[] lines =
        [
            """{"type":"system","subtype":"init","session_id":"abc"}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u1","name":"Read","input":{"file_path":"/work/app/src/a.cs"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"u1","content":"..."}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u2","name":"Bash","input":{"command":"dotnet test"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"u2","content":"denied","is_error":true}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"I need to run the tests."}]}}""",
            """{"type":"result","subtype":"success","result":"I need to run the tests.","session_id":"abc","permission_denials":[{"tool_name":"Bash","tool_input":{"command":"dotnet test"}}]}""",
        ];
        var done = lines.Select(l => AgentHub.OnClaudeEvent(run, (JsonObject)JsonNode.Parse(l)!, denials)).ToList();

        Assert.Equal([false, false, false, false, false, false, true], done);
        Assert.Equal("abc", run.SessionId);
        Assert.Equal("I need to run the tests.", run.Result);
        Assert.Contains(run.Steps, s => s is { Content: "Read src/a.cs", Status: "completed", ToolKind: "read" });
        Assert.Contains(run.Steps, s => s is { Content: "Run `dotnet test`", Status: "failed", ToolKind: "execute" });
        var (tool, input) = Assert.Single(denials);
        Assert.Equal("Bash(dotnet test)", AgentHub.Rule(tool, input));
        Assert.Equal("Write", AgentHub.Rule("Write", JsonNode.Parse("""{"file_path":"x"}""")));

        var args = AgentHub.ClaudeArgs(run, "carry on", ["Bash(dotnet test)"]);
        Assert.Equal(["-p", "carry on", "--output-format", "stream-json", "--verbose", "--permission-mode", "default",
            "--resume", "abc", "--allowedTools", "Bash(dotnet test)"], args);
    }

    [Fact]
    public async Task ABotHandsOffWorkOnlyInItsCodeFolderOrALinkedFolder()
    {
        var hub = await HubAsync(AgentMode.FullAuto);
        if (hub.Locator!(AgentCatalog.Find("hermes")!) is null) return;
        var settings = await _sp.GetRequiredService<SettingsStore>().GetAsync();
        var tools = await new Hearth.Core.Tools.AgentToolProvider(hub, _sp.GetRequiredService<LinkedFolders>()).GetToolsAsync(settings, default);
        var handOff = tools.Single(t => t.Name == "hand_off_to_agent");
        Assert.True(handOff.RequiresApproval);
        Assert.Contains("hermes (Hermes", handOff.Description);
        var conv = new Conversation();
        Hearth.Core.Tools.ToolContext Ctx(string? code) => new()
        {
            Conversation = conv, Reply = new ChatMessage(), Settings = settings, Client = null!, Model = "m",
            Code = code is null ? null : new CodeSession { Directory = code },
        };

        var outside = await handOff.ExecuteAsync(new JsonObject { ["agent"] = "hermes", ["task"] = "x", ["folder"] = "/tmp" }, Ctx(_work), default);
        Assert.True(outside.IsError);
        Assert.Contains("code folder or a linked folder", outside.Text);

        var done = await handOff.ExecuteAsync(new JsonObject { ["agent"] = "hermes", ["task"] = "look" }, Ctx(_work), default);
        Assert.False(done.IsError, done.Text);
        Assert.StartsWith("Hermes finished", done.Text);
        Assert.Contains("Changes it made: Edit app.py", done.Text);
        var runId = System.Text.RegularExpressions.Regex.Match(done.Text, @"run_id (\w+)").Groups[1].Value;

        var followUp = tools.Single(t => t.Name == "follow_up_agent");
        var again = await followUp.ExecuteAsync(new JsonObject { ["run_id"] = runId, ["message"] = "and now?" }, Ctx(_work), default);
        Assert.Contains("got: and now?", again.Text);
        conv = new Conversation(); // another chat can't steer it
        Assert.True((await followUp.ExecuteAsync(new JsonObject { ["run_id"] = runId, ["message"] = "hi" }, Ctx(_work), default)).IsError);
    }

    /// <summary>
    /// Another computer, played by a local shell: Hearth's remote command line (PATH, cd into the folder, exec the
    /// agent) runs exactly as it would after ssh, and the agent's JSON passes through it.
    /// </summary>
    private async Task<(AgentHub hub, Hearth.Core.Tools.RemoteHost host)?> RemoteAsync(AgentMode mode)
    {
        var hub = await HubAsync(mode);
        var fake = FakeAgentPath();
        if (fake is null) return null;
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        var host = new Hearth.Core.Tools.RemoteHost { Name = "lab", Target = "me@lab", Notes = "Linux box" };
        s.RemoteHosts.Add(host);
        await store.SaveAsync(s);
        hub.NodeRunner = async (_, command, _) =>
        {
            if (command.Contains("@@done")) return (0, $"@@hermes|{fake}|Hermes Agent v9\n@@claude|/usr/bin/claude|2.1.0 (Claude Code)\n@@done\n");
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, output);
        };
        hub.NodeStarter = (_, command) =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh")
                { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command);
            return psi;
        };
        return (hub, host);
    }

    [Fact]
    public async Task ItFindsTheAgentsOnAnotherComputer()
    {
        if (await RemoteAsync(AgentMode.Ask) is not var (hub, host)) return;
        var there = await hub.NodeStatusAsync(host);
        Assert.Equal(["claude-code", "hermes"], there.Where(a => a.Installed).Select(a => a.Def.Id).Order());
        Assert.Equal("Hermes Agent v9", there.Single(a => a.Def.Id == "hermes").Version);
        Assert.Same(there, hub.KnownNodeStatus(host.Id));

        hub.NodeRunner = (_, _, _) => Task.FromResult((255, "ssh: connect to host lab port 22: Connection refused"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hub.NodeStatusAsync(host, refresh: true));
        Assert.Equal("Couldn't reach lab: ssh: connect to host lab port 22: Connection refused", ex.Message);
    }

    [Fact]
    public async Task AJobRunsOnAnotherComputerWithTheSameStepsAndApprovals()
    {
        if (await RemoteAsync(AgentMode.Ask) is not var (hub, host)) return;
        var odd = Directory.CreateDirectory(Path.Combine(_work, "it's $HOME")).FullName; // quoting must hold
        var approvals = _sp.GetRequiredService<ApprovalCenter>();
        PendingApproval? asked = null;
        approvals.Requested += p => { asked = p; approvals.Decide(p.Id, true); };

        var run = await hub.StartAsync("hermes", "fix it", odd, computer: "lab");
        Assert.Equal("lab", run.NodeName);
        Assert.Equal(odd, run.Folder);
        run = await hub.WaitAsync(run.Id, TimeSpan.FromSeconds(30));

        Assert.True(run.Status == AgentRunStatus.Done, run.Error);
        Assert.Equal("Hello, got: fix it\n\nEdit was yes.", run.Result);
        Assert.Equal($"Hermes is working in {odd} on lab.", asked!.Reason);

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.StartAsync("hermes", "x", Path.Combine(_work, "nope"), computer: "lab"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.StartAsync("gemini", "x", _work, computer: "lab")); // not installed there
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.StartAsync("hermes", "x", "relative/path", computer: "lab"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.StartAsync("hermes", "x", _work, computer: "nowhere"));
    }

    [Fact]
    public async Task BotsCanSendAJobToAnotherComputer()
    {
        if (await RemoteAsync(AgentMode.FullAuto) is not var (hub, host)) return;
        await hub.NodeStatusAsync(host);
        var settings = await _sp.GetRequiredService<SettingsStore>().GetAsync();
        var tools = await new Hearth.Core.Tools.AgentToolProvider(hub, _sp.GetRequiredService<LinkedFolders>()).GetToolsAsync(settings, default);
        var handOff = tools.Single(t => t.Name == "hand_off_to_agent");
        Assert.Contains("On the computer \"lab\" (Linux box): claude-code, hermes.", handOff.Description);
        var ctx = new Hearth.Core.Tools.ToolContext { Conversation = new Conversation(), Reply = new ChatMessage(), Settings = settings, Client = null!, Model = "m" };

        var noFolder = await handOff.ExecuteAsync(new JsonObject { ["agent"] = "hermes", ["task"] = "x", ["computer"] = "lab" }, ctx, default);
        Assert.Contains("full path on lab", noFolder.Text);
        var done = await handOff.ExecuteAsync(new JsonObject { ["agent"] = "hermes", ["task"] = "go", ["computer"] = "lab", ["folder"] = _work }, ctx, default);
        Assert.StartsWith("Hermes finished", done.Text);
        Assert.Contains(" on lab (run ", done.Text);
    }

    [Fact]
    public void TheRemoteCommandQuotesEverything()
    {
        var run = new AgentRun { Folder = "~/my project's", NodeName = "lab" };
        var line = AgentHub.RemoteCommand(run, "/home/me/.local/bin/hermes", ["acp"]);
        Assert.StartsWith(AgentHub.RemotePath, line);
        Assert.Contains("cd \"$HOME\"/'my project'\\''s' || {", line);
        Assert.EndsWith("exec '/home/me/.local/bin/hermes' 'acp'", line);
    }

    [Fact]
    public void TheHearthSectionInAnAgentsMemoryIsReplacedNotRepeated()
    {
        var once = AgentCare.WithSection("# Notes\nkeep me\n", "## About\n- a");
        Assert.StartsWith("# Notes\nkeep me\n\n<!-- hearth:start -->", once);
        var twice = AgentCare.WithSection(once, "## About\n- b");
        Assert.Contains("- b", twice);
        Assert.DoesNotContain("- a", twice);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "hearth:start"));
        Assert.Contains("keep me", twice);
    }

    [Fact]
    public async Task AgentsCanBeGivenWhatHearthKnowsAndItsSkills()
    {
        var care = _sp.GetRequiredService<AgentCare>();
        care.Home = Directory.CreateDirectory(Path.Combine(_paths.DataDirectory, "home")).FullName;
        var hermes = AgentCatalog.Find("hermes")!;
        var claude = AgentCatalog.Find("claude-code")!;
        Directory.CreateDirectory(care.HomeOf(hermes));
        Directory.CreateDirectory(care.HomeOf(claude));

        await _sp.GetRequiredService<MemoryStore>().AddAsync("Has a dog named Biscuit");
        Assert.Equal(1, await care.ShareMemoriesAsync(hermes));
        var user = await care.ReadAsync(hermes, "memories/USER.md");
        Assert.Contains("- Has a dog named Biscuit", user);
        await care.SaveAsync(hermes, "memories/USER.md", user + "\nmore");
        Assert.True(File.Exists(Path.Combine(care.HomeOf(hermes), "memories/USER.md.hearth-backup")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => care.SaveAsync(hermes, "../../evil.txt", "x"));

        var skills = Path.Combine(_paths.DataDirectory, "skills");
        Directory.CreateDirectory(Path.Combine(skills, "weekly-report"));
        File.WriteAllText(Path.Combine(skills, "weekly-report", "SKILL.md"), "---\nname: weekly-report\n---\nSteps");

        Assert.Equal(1, await care.ShareSkillsAsync(hermes, true));
        Assert.True(care.SkillsShared(hermes));
        Assert.True(File.Exists(Path.Combine(care.HomeOf(hermes), "skills", "hearth", "weekly-report", "SKILL.md")));
        Assert.Equal(1, await care.ShareSkillsAsync(claude, true));
        Assert.True(File.Exists(Path.Combine(care.HomeOf(claude), "skills", "hearth-weekly-report", "SKILL.md")));
        Assert.True((await _sp.GetRequiredService<SettingsStore>().GetAsync()).Agents.For("claude-code").ShareSkills);

        await care.ShareSkillsAsync(claude, false);
        await care.ShareSkillsAsync(hermes, false);
        Assert.False(care.SkillsShared(claude));
        Assert.False(care.SkillsShared(hermes));
        Assert.True(File.Exists(Path.Combine(skills, "weekly-report", "SKILL.md"))); // only the links went
    }
}
