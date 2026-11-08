using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services.Microsoft;
using Hearth.Core.Tools;

namespace Hearth.Core.Services.Channels;

/// <summary>
/// The documents a reply made, as files to send to a chat app: workspace files it wrote, and Word versions of the
/// Google Docs and OneDrive documents it created (so the cover letter itself arrives, not just a link).
/// </summary>
public static class ReplyFiles
{
    public const long MaxBytes = 20_000_000;

    public sealed record OutFile(string Name, string MimeType, byte[] Data);

    public static async Task<List<OutFile>> CollectAsync(ChatMessage reply, string workspaceDirectory, CancellationToken ct = default)
    {
        var files = new List<OutFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in reply.ToolCalls.Where(t => t.Status == ToolCallStatus.Done))
        {
            JsonObject? args;
            try { args = JsonNode.Parse(tc.ArgumentsJson) as JsonObject; } catch { continue; }
            if (args is null) continue;
            OutFile? file = null;
            try
            {
                switch (tc.Name)
                {
                    case "write_file":
                        var path = Workspace.Resolve(workspaceDirectory, args.Str("path"));
                        if (File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxBytes)
                            file = new OutFile(Path.GetFileName(path), Mime(path), await File.ReadAllBytesAsync(path, ct));
                        break;
                    case "docs_create":
                        var title = Safe(args.Str("title") ?? "Document");
                        file = new OutFile(title + ".docx", DocxMime, WordDocs.FromMarkdown(args.Str("content") ?? "", title));
                        break;
                    case "onedrive_write":
                        var target = args.Str("path") ?? "file.txt";
                        var name = Safe(Path.GetFileName(target));
                        file = target.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                            ? new OutFile(name, DocxMime, WordDocs.FromMarkdown(args.Str("content") ?? "", Path.GetFileNameWithoutExtension(name)))
                            : new OutFile(name, Mime(name), Encoding.UTF8.GetBytes(args.Str("content") ?? ""));
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* that one isn't sent */ }
            // The last version of each file wins (a bot may write it twice).
            if (file is not null)
            {
                if (!seen.Add(file.Name)) files.RemoveAll(f => f.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
                files.Add(file);
            }
        }
        return files;
    }

    public const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static string Safe(string name)
    {
        var s = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        return s.Length == 0 ? "file" : s.Length > 100 ? s[..100] : s;
    }

    public static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "text/markdown",
        ".txt" or ".log" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".pdf" => "application/pdf",
        ".docx" => DocxMime,
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
}
