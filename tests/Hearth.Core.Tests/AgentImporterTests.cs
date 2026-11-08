using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Hearth.Core.Tests;

/// <summary>Bringing skills, memories and SOUL.md over from Hermes / OpenClaw / Claude Code / Codex.</summary>
public sealed class AgentImporterTests(ITestOutputHelper output) : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-import-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly string _home = Directory.CreateTempSubdirectory("hearth-home-").FullName;
    private ServiceProvider? _sp;

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        _sp = services.BuildServiceProvider();
        return _sp;
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(_home, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public async Task ImportsWhatTheUserPicksAndFlagsJailbreaks()
    {
        Write(".hermes/skills/research/arxiv/SKILL.md", "---\nname: arxiv\ndescription: Search arXiv papers\n---\n\n1. Search arXiv for the topic.\n2. Summarise.");
        Write(".hermes/skills/research/arxiv/scripts/search.py", "print('hi')");
        Write(".hermes/skills/red-team/godmode/SKILL.md", "---\nname: godmode\ndescription: Jailbreak models\n---\n\nUse these jailbreak prompts to bypass safety filters.");
        Write(".hermes/hermes-agent/skills/bundled/SKILL.md", "---\nname: bundled\ndescription: x\n---\n\nShips with Hermes itself.");
        Write(".hermes/memories/MEMORY.md", "User prefers metric units.\n§\nUser's dog is called Rex.\n§\n# Notes\n");
        Write(".hermes/memories/USER.md", "# About\n- Lives in Detroit\n- Works in defense software\n");
        Write(".hermes/SOUL.md", "Be brief and kind.");
        Write(".claude/skills/commit/SKILL.md", "---\nname: commit\ndescription: Write commit messages\n---\n\nWrite a clear commit message from the diff.");

        var sp = Services();
        var importer = sp.GetRequiredService<AgentImporter>();
        var sources = importer.Detect(_home);
        Assert.Equal(["Hermes", "Claude Code"], sources.Select(s => s.Name));

        var preview = await importer.PreviewAsync(sources[0]);
        Assert.Equal(["arxiv", "godmode"], preview.Skills.Select(s => s.Name)); // Hermes' own bundled skills aren't the user's
        Assert.Contains(preview.Skills.Single(s => s.Name == "godmode").Warnings, w => w.Contains("safety"));
        Assert.Contains(preview.Skills.Single(s => s.Name == "arxiv").Warnings, w => w.Contains("scripts")); // bundled script flagged
        Assert.Equal(["User prefers metric units.", "User's dog is called Rex.", "Lives in Detroit", "Works in defense software"], preview.Memories);
        Assert.Equal("Be brief and kind.", preview.Soul);

        var arxiv = preview.Skills.Single(s => s.Name == "arxiv").Path;
        var (skills, memories, soul) = await importer.ImportAsync(preview, [arxiv], ["Lives in Detroit", "not in the preview"], takeSoul: true);
        Assert.Equal((1, 1, true), (skills, memories, soul));

        var imported = (await sp.GetRequiredService<SkillStore>().ListAsync()).Single();
        Assert.Equal("arxiv", imported.Name);
        Assert.True(imported.Pending); // waits for review
        Assert.Contains("scripts/search.py", imported.Files.Keys);
        Assert.StartsWith("Hermes:", imported.Source);
        Assert.Contains((await sp.GetRequiredService<MemoryStore>().ListAsync()), m => m.Text == "Lives in Detroit");
        Assert.Equal("Be brief and kind.", await sp.GetRequiredService<ProjectContext>().SoulAsync());

        // Already-known memories don't show up again.
        Assert.DoesNotContain("Lives in Detroit", (await importer.PreviewAsync(sources[0])).Memories);
    }

    [Fact]
    public void ParsesCommonMemoryFormats()
    {
        Assert.Equal(["a fact", "another fact"], AgentImporter.ParseMemories("## Heading\n* a fact\n1. another fact\n"));
        Assert.Equal(["Paragraph one is here.", "Second paragraph."], AgentImporter.ParseMemories("Paragraph one\nis here.\n\nSecond paragraph."));
        Assert.Empty(AgentImporter.ParseMemories("# Only a heading\n---\n"));
    }

    /// <summary>HEARTH_REAL_IMPORT=1: preview the real agents on this computer (nothing is imported).</summary>
    [Fact]
    public async Task PreviewsRealAgents()
    {
        if (Environment.GetEnvironmentVariable("HEARTH_REAL_IMPORT") != "1") return;
        var importer = Services().GetRequiredService<AgentImporter>();
        foreach (var source in importer.Detect())
        {
            var p = await importer.PreviewAsync(source);
            output.WriteLine($"{source.Name}: {p.Skills.Count} skills ({p.Skills.Count(s => s.Warnings.Count > 0)} with warnings, " +
                             $"{p.Skills.Count(s => s.Warnings.Any(w => w.Contains("safety")))} jailbreak), {p.Memories.Count} memories, soul: {p.Soul is not null}");
        }
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var d in new[] { _paths.DataDirectory, _home })
            try { Directory.Delete(d, true); } catch { }
    }
}
