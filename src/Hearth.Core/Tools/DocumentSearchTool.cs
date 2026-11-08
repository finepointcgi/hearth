using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Services;

namespace Hearth.Core.Tools;

public sealed class DocumentSearchTool(DocumentStore docs) : ITool
{
    public string Name => "search_documents";
    public string DisplayName => "Search my documents";
    public string Icon => "📚";
    public string Description =>
        "Search the user's personal document library (PDFs, notes, code) and the folders they linked from their " +
        "computer. Use whenever the question might be answered by their own files. Results from linked folders show " +
        "the file's path (open the whole file with read_linked_file). Cite the document name in your answer.";
    public JsonObject Parameters => Schema.Object(
        ("query", "string", "What to look for, phrased as a question or keywords", true),
        ("top_k", "integer", "Number of passages (default from settings)", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var query = args.Str("query");
        if (string.IsNullOrWhiteSpace(query)) return ToolResult.Error("query is required");
        var k = (int)Math.Clamp(args.Num("top_k") ?? ctx.Settings.Documents.TopK, 1, 20);
        var hits = await docs.SearchAsync(query, k, ct);
        if (hits.Count == 0) return new ToolResult("No matching passages in the document library.");
        var sb = new StringBuilder();
        foreach (var h in hits)
            sb.AppendLine($"--- {h.DocumentName} (relevance {h.Score:0.00}) ---\n{h.Text}\n");
        return new ToolResult(sb.ToString());
    }
}
