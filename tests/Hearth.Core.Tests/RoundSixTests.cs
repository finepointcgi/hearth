using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Services.Channels;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Hearth.Core.Tests;

/// <summary>Undo, files back to chat apps, voice replies, always-allow from anywhere, and reply timing.</summary>
public sealed class RoundSixTests : IAsyncDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-r6-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ServiceProvider _sp;

    public RoundSixTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: true));
        _sp = services.BuildServiceProvider();
    }

    private IBotComputer Computer => _sp.GetRequiredService<IBotComputer>();
    private string Workspace => Computer.WorkspaceDirectory;

    private async Task<List<UndoStep>> WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Workspace);
        var tool = new WriteFileTool(Computer);
        List<UndoStep> steps;
        using (UndoScope.Begin(out steps))
            await tool.ExecuteAsync(new JsonObject { ["path"] = path, ["content"] = content }, null!, default);
        return steps;
    }

    [Fact]
    public async Task WorkspaceFilesCanBeUndone()
    {
        var undo = _sp.GetRequiredService<UndoLog>();
        await undo.AddAsync("new", "write_file", null, "c", await WriteAsync("notes/a.md", "first"));
        Assert.True(File.Exists(Path.Combine(Workspace, "notes/a.md")));
        await undo.AddAsync("edit", "write_file", null, "c", await WriteAsync("notes/a.md", "second"));

        Assert.Contains("Put back the earlier a.md", await undo.UndoAsync("edit"));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(Workspace, "notes/a.md")));
        Assert.Contains("Deleted a.md", await undo.UndoAsync("new"));
        Assert.False(File.Exists(Path.Combine(Workspace, "notes/a.md")));

        // A file changed by someone else since isn't overwritten.
        await undo.AddAsync("third", "write_file", null, "c", await WriteAsync("b.md", "bot"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "the user's own edit");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => undo.UndoAsync("third"));
        Assert.Contains("changed since", ex.Message);
        Assert.Equal("the user's own edit", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md")));

        // Outside a tool call nothing is collected.
        await new WriteFileTool(Computer).ExecuteAsync(new JsonObject { ["path"] = "c.md", ["content"] = "x" }, null!, default);
        Assert.False(UndoScope.Active);
    }

    [Fact]
    public async Task UndoToolOnlyReachesThisChat()
    {
        var undo = _sp.GetRequiredService<UndoLog>();
        await undo.AddAsync("mine", "write_file", null, "chat-a", await WriteAsync("x.md", "x"));
        await undo.AddAsync("other", "write_file", null, "chat-b", await WriteAsync("y.md", "y"));
        var tool = new UndoChangeTool(undo);
        var ctx = new ToolContext { Conversation = new Conversation { Id = "chat-a" }, Reply = new(), Settings = new(), Client = null!, Model = "m" };
        var list = (await tool.ExecuteAsync(new JsonObject(), ctx, default)).Text;
        Assert.Contains("id mine", list);
        Assert.DoesNotContain("other", list);
        Assert.True((await tool.ExecuteAsync(new JsonObject { ["id"] = "other" }, ctx, default)).IsError);
        Assert.Contains("Deleted x.md", (await tool.ExecuteAsync(new JsonObject { ["id"] = "mine" }, ctx, default)).Text);
        Assert.True(File.Exists(Path.Combine(Workspace, "y.md")));
    }

    [Fact]
    public void A1Blocks()
    {
        Assert.Equal("Sales!B2:C4", A1.Block("Sales!B2", 3, 2));
        Assert.Equal("'My tab'!Y9:AB9", A1.Block("'My tab'!Y9", 1, 4));
        Assert.Equal("A1:A1", A1.Block("A1:Z99", 1, 1));
        Assert.Null(A1.Block("Sales", 2, 2)); // a whole tab: no fixed block
        Assert.Equal(27, A1.ColumnNumber("AA"));
        Assert.Equal("AZ", A1.ColumnName(52));
        Assert.Equal([["a", ""], ["", ""]], A1.Pad([["a"]], 2, 2));
    }

    [Fact]
    public async Task RepliesBringTheirDocumentsAlong()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "letters"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "letters/draft.md"), "old");
        await File.WriteAllTextAsync(Path.Combine(Workspace, "letters/draft.md"), "# Dear Shield AI");
        var reply = new ChatMessage { Role = ChatRole.Assistant };
        reply.ToolCalls.Add(new ToolCall { Name = "write_file", ArgumentsJson = "{\"path\":\"letters/draft.md\",\"content\":\"x\"}", Status = ToolCallStatus.Done });
        reply.ToolCalls.Add(new ToolCall { Name = "docs_create", ArgumentsJson = "{\"title\":\"Cover letter - Shield AI\",\"content\":\"# Cover letter\\n- 15 years\"}", Status = ToolCallStatus.Done });
        reply.ToolCalls.Add(new ToolCall { Name = "write_file", ArgumentsJson = "{\"path\":\"../../etc/passwd\"}", Status = ToolCallStatus.Done });
        reply.ToolCalls.Add(new ToolCall { Name = "docs_create", ArgumentsJson = "{\"title\":\"Denied\",\"content\":\"x\"}", Status = ToolCallStatus.Denied });

        var files = await ReplyFiles.CollectAsync(reply, Workspace);
        Assert.Equal(["draft.md", "Cover letter - Shield AI.docx"], files.Select(f => f.Name));
        Assert.Equal("# Dear Shield AI", System.Text.Encoding.UTF8.GetString(files[0].Data));
        Assert.Contains("15 years", Hearth.Core.Services.Microsoft.WordDocs.ReadText(files[1].Data));
        Assert.Equal(ReplyFiles.DocxMime, files[1].MimeType);
    }

    [Fact]
    public async Task AlwaysAllowWorksFromAnyApprovalCard()
    {
        var approvals = _sp.GetRequiredService<ApprovalCenter>();
        var remembered = false;
        var call = new ToolCall { Name = "write_file", AlwaysAllowFor = "Scout", AlwaysAllow = () => { remembered = true; return Task.CompletedTask; } };
        var decision = new TaskCompletionSource<bool>();
        var p = approvals.Register(new Conversation(), null, call, decision);
        Assert.Equal("Scout", p.AlwaysAllowFor);
        Assert.True(await approvals.AllowAlwaysAsync(p.Id));
        Assert.True(remembered);
        Assert.True(await decision.Task);
        Assert.False(await approvals.AllowAlwaysAsync(p.Id)); // already answered

        // Calls that can't be always-allowed (phone calls) don't offer it.
        var plain = approvals.Register(new Conversation(), null, new ToolCall { Name = "call_phone" }, new TaskCompletionSource<bool>());
        Assert.Null(plain.AlwaysAllowFor);
    }

    [Fact]
    public async Task ReplyTimesAreKept()
    {
        var usage = _sp.GetRequiredService<UsageStore>();
        await usage.RecordReplyAsync(new ReplyRecord(DateTimeOffset.Now, "oMLX", "qwen", "b1", "c1", "Chat", 12000, 3000, 3, 2, true, true, false));
        await usage.RecordReplyAsync(new ReplyRecord(DateTimeOffset.Now, "Claude", "opus", null, "c2", "Chat", 8000, null, 1, 0, false, false, false));
        var rows = await usage.RepliesSinceAsync(DateTimeOffset.Now.AddMinutes(-1));
        Assert.Equal(2, rows.Count);
        Assert.Equal((12000L, 3000L, true, true), (rows[0].TotalMs, rows[0].FirstTokenMs!.Value, rows[0].CheckRan, rows[0].CheckFixed));
        Assert.Null(rows[1].FirstTokenMs);
    }

    [Fact]
    public async Task VoiceRepliesUseTheMacVoiceWhenNoServerIsSet()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/say")) return;
        var spoken = _sp.GetRequiredService<SpokenReply>();
        Assert.True(await spoken.AvailableAsync());
        var audio = await spoken.SpeakAsync("**Three** dinners: tacos, pasta and *salmon*.");
        Assert.NotNull(audio);
        var hasFfmpeg = File.Exists("/opt/homebrew/bin/ffmpeg") || File.Exists("/usr/local/bin/ffmpeg");
        Assert.Equal(hasFfmpeg ? "audio/ogg" : "audio/aiff", audio.Value.mime);
        if (hasFfmpeg) Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(audio.Value.audio, 0, 4)); // a voice note Telegram can play
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
