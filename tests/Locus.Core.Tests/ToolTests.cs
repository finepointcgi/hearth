using System.Text.Json.Nodes;
using Locus.Core.Services;
using Locus.Core.Tools;

namespace Locus.Core.Tests;

public class ExpressionEvaluatorTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("2^3^2", 512)]
    [InlineData("-2^2", -4)]
    [InlineData("sqrt(16) + abs(-3)", 7)]
    [InlineData("max(1, 5, 3) - min(4, 2)", 3)]
    [InlineData("1,000 * 2", 2000)]
    [InlineData("10 % 4", 2)]
    [InlineData("round(pi, 2)", 3.14)]
    [InlineData("1.5e3 / 3", 500)]
    public void Evaluates(string expr, double expected) =>
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expr), 9);

    [Theory]
    [InlineData("2 +")]
    [InlineData("foo(1)")]
    [InlineData("(1 + 2")]
    [InlineData("System.IO.File")]
    public void RejectsBadInput(string expr) =>
        Assert.ThrowsAny<FormatException>(() => ExpressionEvaluator.Evaluate(expr));
}

public class UnitConvertTests
{
    private static async Task<string> Convert(double v, string from, string to)
    {
        var r = await new UnitConvertTool().ExecuteAsync(
            new JsonObject { ["value"] = v, ["from"] = from, ["to"] = to }, null!, CancellationToken.None);
        return r.Text;
    }

    [Fact] public async Task Miles() => Assert.Contains("= 16.09344 km", await Convert(10, "mi", "km"));
    [Fact] public async Task Temperature() => Assert.Contains("= 212 F", await Convert(100, "C", "F"));
    [Fact] public async Task Mismatch() => Assert.Contains("Can't convert", await Convert(1, "kg", "m"));
}

public class ChunkerTests
{
    [Fact]
    public void SplitsLongTextWithOverlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"Paragraph {i} " + new string('x', 100)));
        var chunks = Chunker.Split(text, 500, 80);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(c.Length <= 700));
        Assert.Contains("Paragraph 39", chunks[^1]);
    }
}

public class McpConfigTests
{
    [Fact]
    public void ParsesClaudeDesktopConfig()
    {
        var servers = McpManager.ParseConfig("""
        { "mcpServers": {
            "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/Users/me/My Docs"], "env": { "A": "1" } },
            "remote": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer x" } }
        } }
        """);
        Assert.Equal(2, servers.Count);
        Assert.Equal("npx", servers[0].Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/Users/me/My Docs"], McpManager.SplitArgs(servers[0].Arguments));
        Assert.Equal("1", servers[0].Environment["A"]);
        Assert.Equal(Models.McpTransport.Http, servers[1].Transport);
        Assert.Equal("Bearer x", servers[1].Headers["Authorization"]);
    }
}

public class TextExtractorTests
{
    [Fact]
    public void StripsHtmlChrome()
    {
        var (title, text) = TextExtractor.FromHtml(
            "<html><head><title>T</title><script>evil()</script></head><body><nav>menu</nav><article><h1>Hello</h1><p>World</p></article></body></html>");
        Assert.Equal("T", title);
        Assert.Contains("Hello", text);
        Assert.Contains("World", text);
        Assert.DoesNotContain("evil", text);
        Assert.DoesNotContain("menu", text);
    }
}
