using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tools;

/// <summary>Lets bots look inside folders you linked on the Documents page (read-only).</summary>
public sealed class LinkedFolderToolProvider(LinkedFolders folders) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) =>
        (await folders.ListAsync()).Any(f => f.Enabled)
            ? [new ListLinkedFilesTool(folders), new ReadLinkedFileTool(folders)]
            : [];
}

public sealed class ListLinkedFilesTool(LinkedFolders folders) : ITool, IPermissionInfo
{
    public string Name => "list_linked_files";
    public string DisplayName => "List my folder";
    public string Icon => "🗂️";
    public string Description =>
        "List files in the folders the user linked from their computer (all linked folders, or one sub-folder). " +
        "Use search_documents to find content; use this to see what's there or find a file by name.";
    public JsonObject Parameters => Schema.Object(
        ("folder", "string", "A linked folder or a folder inside one (default: all linked folders)", false),
        ("name_contains", "string", "Only files whose name contains this", false));
    public bool RequiresApproval => false;
    public string Area => "documents";
    public string AreaTitle => "Documents";
    public bool IsWrite => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var linked = (await folders.ListAsync()).Where(f => f.Enabled).ToList();
        var roots = linked.Select(f => f.Path).ToList();
        if (args.Str("folder") is { Length: > 0 } sub)
        {
            var full = LinkedFolders.Normalize(sub);
            if (await folders.FolderForAsync(full) is null || !Directory.Exists(full))
                return ToolResult.Error($"{sub} isn't inside a linked folder. Linked: {string.Join(", ", roots)}");
            roots = [full];
        }
        var filter = args.Str("name_contains");
        var sb = new StringBuilder();
        var n = 0;
        foreach (var root in roots)
        {
            sb.AppendLine($"{root}/");
            foreach (var f in LinkedFolders.Scan(root))
            {
                if (filter is { Length: > 0 } && !f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (++n > 300) { sb.AppendLine("… (more; narrow it with folder or name_contains)"); return new ToolResult(sb.ToString()); }
                sb.AppendLine($"  {Path.GetRelativePath(root, f.FullName)}  ({f.Length / 1024.0:0.#} KB, {f.LastWriteTime:MMM d yyyy})");
            }
        }
        return new ToolResult(n == 0 ? "No matching files." : sb.ToString());
    }
}

public sealed class ReadLinkedFileTool(LinkedFolders folders) : ITool, IPermissionInfo
{
    private const int MaxChars = 40000;

    public string Name => "read_linked_file";
    public string DisplayName => "Read a file from my folder";
    public string Icon => "📄";
    public string Description =>
        "Read a file from a folder the user linked from their computer (full path, as shown by search_documents or " +
        "list_linked_files). Text, Markdown, code and PDF. Long files: use start_line.";
    public JsonObject Parameters => Schema.Object(
        ("path", "string", "Full path of the file", true),
        ("start_line", "integer", "Line to start from (default 1)", false));
    public bool RequiresApproval => false;
    public string Area => "documents";
    public string AreaTitle => "Documents";
    public bool IsWrite => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = args.Str("path") ?? "";
        var full = LinkedFolders.Normalize(path);
        if (full.Length == 0 || await folders.FolderForAsync(full) is null)
            return ToolResult.Error("That file isn't in a linked folder (hidden files and links aren't shared).");
        if (!File.Exists(full)) return ToolResult.Error($"No file at {full}.");
        var info = new FileInfo(full);
        if (info.Length > LinkedFolders.MaxFileBytes) return ToolResult.Error("That file is too large to read here.");
        if (!TextExtractor.IsSupported(info.Name)) return ToolResult.Error("That kind of file can't be read as text.");
        var text = TextExtractor.Extract(info.Name, await File.ReadAllBytesAsync(full, ct));
        var start = (int)Math.Max(1, args.Num("start_line") ?? 1);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var body = string.Join('\n', lines.Skip(start - 1));
        var more = body.Length > MaxChars;
        if (more) body = body[..MaxChars];
        return new ToolResult($"{full} (lines {start}–{Math.Min(lines.Length, start + body.Count(c => c == '\n'))} of {lines.Length})\n\n{body}" +
            (more ? "\n\n… (truncated; continue with start_line)" : ""));
    }
}
