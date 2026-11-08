using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Tools;

/// <summary>Built-in tools, filtered by settings and platform.</summary>
public sealed class BuiltInToolProvider(
    PlatformInfo platform,
    WebSearchService search,
    IHttpClientFactory http,
    MemoryStore memory,
    DocumentStore documents,
    ImageGenService images,
    SkillStore skills,
    SettingsStore settingsStore,
    ChatHistoryIndex history,
    ConversationStore conversations,
    IBotComputer computer,
    CodeSandbox sandbox) : IToolProvider
{
    /// <summary>Every built-in tool with whether it can run right now (for the Settings page).</summary>
    public async Task<IReadOnlyList<(ITool tool, string? unavailableReason)>> CatalogAsync(AppSettings s)
    {
        var hasDocs = (await documents.ListAsync()).Count > 0;
        var hasSkills = (await skills.ActiveAsync()).Count > 0;
        return
        [
            (new CalculatorTool(), null),
            (new DateTimeTool(), null),
            (new UnitConvertTool(), null),
            (new WebSearchTool(search), string.IsNullOrWhiteSpace(s.SearxngUrl) ? "Set a SearXNG URL" : null),
            (new FetchUrlTool(http), null),
            (new DocumentSearchTool(documents), hasDocs ? null : "Add documents to your library"),
            (new UseSkillTool(skills), hasSkills ? null : "Create a skill first"),
            (new SaveSkillTool(skills, settingsStore), s.SkillLearning.Enabled ? null : "Self-written skills are off"),
            (new ImproveSkillTool(skills, settingsStore), !s.SkillLearning.Enabled ? "Self-written skills are off" : hasSkills ? null : "No skills yet"),
            (new SaveMemoryTool(memory), s.MemoryEnabled ? null : "Memory is off"),
            (new DeleteMemoryTool(memory), s.MemoryEnabled ? null : "Memory is off"),
            (new RecallMemoryTool(memory), s.MemoryEnabled ? null : "Memory is off"),
            (new SearchPastChatsTool(history), s.MemoryEnabled ? null : "Memory is off"),
            (new ReadPastChatTool(conversations), s.MemoryEnabled ? null : "Memory is off"),
            (new ImageGenTool(images), s.ImageGen.Backend == ImageBackend.None ? "Choose an image backend" : null),
            (new ListFilesTool(computer), s.Computer.Enabled ? null : "Computer is off"),
            (new ReadFileTool(computer), s.Computer.Enabled ? null : "Computer is off"),
            (new WriteFileTool(computer), s.Computer.Enabled ? null : "Computer is off"),
            (new RunCommandTool(computer, sandbox),
                !platform.CanRunProcesses ? "Not available on this platform"
                : !s.Computer.AllowCommands ? "Turned off" : null),
            (new RunRemoteTool(s.RemoteHosts),
                !platform.CanRunProcesses ? "Not available on this platform"
                : s.RemoteHosts.Count == 0 ? "Add a computer on the Computer page" : null),
            (new PythonTool(sandbox, computer),
                !platform.CanRunProcesses ? "Not available on this platform"
                : !s.Tools.PythonEnabled ? "Turned off"
                : !sandbox.Active(s) && PythonTool.FindPython(s.Tools.PythonPath) is null ? "Python not found" : null),
        ];
    }

    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) =>
        (await CatalogAsync(settings)).Where(t => t.unavailableReason is null).Select(t => t.tool).ToList();
}

/// <summary>All tools: built-ins, extra providers registered by the host (e.g. the browser), and MCP servers.</summary>
public sealed class ToolRegistry(BuiltInToolProvider builtIn, McpManager mcp, IEnumerable<IToolProvider> extras)
{
    public IEnumerable<IToolProvider> Extras => extras;

    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        var all = new List<ITool>(await builtIn.GetToolsAsync(settings, ct));
        foreach (var p in extras) all.AddRange(await p.GetToolsAsync(settings, ct));
        try { all.AddRange(await mcp.GetToolsAsync(settings, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* surfaced on the Settings page */ }
        return all
            .Where(t => !settings.Tools.DisabledTools.Contains(t.Name))
            .GroupBy(t => t.Name).Select(g => g.First())
            .ToList();
    }
}
