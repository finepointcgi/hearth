namespace Locus.Core.Services;

/// <summary>
/// Instruction files bots read automatically, like other agents do: SOUL.md (Locus's personality and voice, in the
/// data folder; edit it in Settings) and AGENTS.md / CLAUDE.md at the top of editable project folders (how to work
/// in that project: build commands, conventions).
/// </summary>
public sealed class ProjectContext(IAppPaths paths, LinkedFolders folders)
{
    public static readonly string[] ProjectFiles = ["AGENTS.md", "CLAUDE.md", "GEMINI.md", "CONVENTIONS.md"];
    public const int MaxCharsPerFile = 6000;
    public const int MaxChars = 12000;

    public string SoulPath => Path.Combine(paths.DataDirectory, "SOUL.md");

    public async Task<string?> SoulAsync() =>
        File.Exists(SoulPath) && (await File.ReadAllTextAsync(SoulPath)).Trim() is { Length: > 0 } t
            ? (t.Length > MaxCharsPerFile ? t[..MaxCharsPerFile] : t) : null;

    public async Task SaveSoulAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { if (File.Exists(SoulPath)) File.Delete(SoulPath); return; }
        await Json.WriteTextAtomicAsync(SoulPath, text.Trim() + "\n");
    }

    /// <summary>The instruction files of editable project folders, as (folder, file, text).</summary>
    public async Task<List<(string folder, string file, string text)>> ProjectInstructionsAsync()
    {
        var found = new List<(string, string, string)>();
        var total = 0;
        foreach (var f in (await folders.ListAsync()).Where(f => f.Enabled && f.Editable && Directory.Exists(f.Path)))
            foreach (var name in ProjectFiles)
            {
                var path = Path.Combine(f.Path, name);
                if (!File.Exists(path) || new FileInfo(path).Length > 200_000) continue;
                var text = (await File.ReadAllTextAsync(path)).Trim();
                if (text.Length == 0) continue;
                if (text.Length > MaxCharsPerFile) text = text[..MaxCharsPerFile] + "\n… (cut; read the whole file with read_linked_file)";
                if (total + text.Length > MaxChars) return found;
                total += text.Length;
                found.Add((f.Path, name, text));
                break; // one instruction file per project (the first that exists)
            }
        return found;
    }
}
