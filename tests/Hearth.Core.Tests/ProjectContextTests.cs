using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>SOUL.md and project instruction files (AGENTS.md / CLAUDE.md) reach the model.</summary>
public sealed class ProjectContextTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-soul-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly string _project = LinkedFolders.Normalize(Directory.CreateTempSubdirectory("hearth-agents-").FullName);
    private readonly ScriptedTeam _team = new();
    private ServiceProvider? _sp;
    private LlmRequest? _seen;

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "m" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
        _team.Script = (_, _, r) => { _seen = r; return new LlmChunk("ok"); };
        return _sp;
    }

    private async Task<string> SystemPromptAsync(IServiceProvider sp)
    {
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "hi" });
        await sp.GetRequiredService<ChatService>().RunAgentAsync(conv, null, new SendOptions("m", false, false, true), () => Task.CompletedTask, default);
        return _seen!.Messages[0].Content;
    }

    [Fact]
    public async Task SoulAndProjectInstructionsAreFollowed()
    {
        var sp = Services();
        var ctx = sp.GetRequiredService<ProjectContext>();
        Assert.DoesNotContain("SOUL.md", await SystemPromptAsync(sp));

        await ctx.SaveSoulAsync("Talk like a calm ship's captain.");
        Assert.True(File.Exists(Path.Combine(_paths.DataDirectory, "SOUL.md")));
        Assert.Contains("calm ship's captain", await SystemPromptAsync(sp));

        File.WriteAllText(Path.Combine(_project, "AGENTS.md"), "Build with `dotnet build Hearth.sln`. Use file-scoped namespaces.");
        var folders = sp.GetRequiredService<LinkedFolders>();
        Assert.Null(await folders.AddAsync(_project));
        Assert.DoesNotContain("file-scoped namespaces", await SystemPromptAsync(sp)); // read-only folder: not loaded

        await folders.SetEditableAsync((await folders.ListAsync()).Single().Id, true);
        var prompt = await SystemPromptAsync(sp);
        Assert.Contains("file-scoped namespaces", prompt);
        Assert.Contains("its AGENTS.md", prompt);

        await ctx.SaveSoulAsync("");
        Assert.False(File.Exists(Path.Combine(_paths.DataDirectory, "SOUL.md")));
    }

    [Fact]
    public async Task LongInstructionFilesAreCut()
    {
        var sp = Services();
        File.WriteAllText(Path.Combine(_project, "CLAUDE.md"), new string('x', 20_000));
        var folders = sp.GetRequiredService<LinkedFolders>();
        Assert.Null(await folders.AddAsync(_project));
        await folders.SetEditableAsync((await folders.ListAsync()).Single().Id, true);
        var (_, file, text) = Assert.Single(await sp.GetRequiredService<ProjectContext>().ProjectInstructionsAsync());
        Assert.Equal("CLAUDE.md", file);
        Assert.True(text.Length < ProjectContext.MaxCharsPerFile + 100);
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var d in new[] { _paths.DataDirectory, _project })
            try { Directory.Delete(d, true); } catch { }
    }
}
