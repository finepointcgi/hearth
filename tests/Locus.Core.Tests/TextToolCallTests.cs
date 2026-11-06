using Locus.Core.Llm;

namespace Locus.Core.Tests;

public sealed class TextToolCallTests
{
    private static readonly HashSet<string> Tools = ["web_search", "fetch_url"];

    [Fact]
    public void FunctionCallsBlockBecomesCalls()
    {
        var text = "I'll research Google.\n<function_calls>\n[\n  {\"name\": \"web_search\", \"arguments\": {\"query\": \"Google stock\", \"count\": 5}},\n  {\"name\": \"web_search\", \"arguments\": {\"query\": \"Alphabet news\"}}\n]\n</function_calls>";
        var (calls, rest) = TextToolCalls.Find(text, Tools);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("web_search", c.Name));
        Assert.Contains("Google stock", calls[0].ArgumentsJson);
        Assert.Equal("I'll research Google.", rest);
    }

    [Fact]
    public void QwenToolCallTagsAndCutOffArrays()
    {
        var (calls, _) = TextToolCalls.Find("<tool_call>\n{\"name\": \"fetch_url\", \"arguments\": \"{\\\"url\\\": \\\"https://a.test\\\"}\"}\n</tool_call>", Tools);
        Assert.Equal("{\"url\": \"https://a.test\"}", Assert.Single(calls).ArgumentsJson);

        // Output cut off mid-array: keep the complete call.
        var (cut, _) = TextToolCalls.Find("<function_calls>[{\"name\": \"web_search\", \"arguments\": {\"query\": \"a\"}}, {\"name\": \"web_se", Tools);
        Assert.Single(cut);
    }

    [Fact]
    public void QwenXmlFormat()
    {
        var (calls, rest) = TextToolCalls.Find("Checking.\n<tool_call>\n<function=web_search>\n<parameter=query>\n.NET 10 release\n</parameter>\n<parameter=count>\n5\n</parameter>\n</function>\n</tool_call>", Tools);
        var call = Assert.Single(calls);
        Assert.Equal("web_search", call.Name);
        Assert.Equal("{\"query\":\".NET 10 release\",\"count\":5}", call.ArgumentsJson);
        Assert.Equal("Checking.", rest);
    }

    [Fact]
    public void OrdinaryJsonIsLeftAlone()
    {
        var text = "Here's the config:\n```json\n{\"name\": \"my-app\", \"version\": 2}\n```";
        var (calls, rest) = TextToolCalls.Find(text, Tools);
        Assert.Empty(calls);
        Assert.Equal(text, rest);
    }

    private static readonly System.Text.Json.Nodes.JsonObject Schema = Locus.Core.Tools.Schema.Object(
        ("query", "string", "Search query", true), ("count", "integer", "How many", false));

    [Fact]
    public void ArgumentsAreReadLeniently()
    {
        Assert.Equal("a", Locus.Core.Tools.ToolArguments.Check("{\"query\": \"a\",}", Schema).args["query"]!.ToString()); // trailing comma
        Assert.Equal("b", Locus.Core.Tools.ToolArguments.Check("\"{\\\"query\\\": \\\"b\\\"}\"", Schema).args["query"]!.ToString()); // double-encoded
        Assert.Equal("c", Locus.Core.Tools.ToolArguments.Check("```json\n{\"query\": \"c\"}\n```", Schema).args["query"]!.ToString());
    }

    [Fact]
    public void BadArgumentsExplainWhatToFix()
    {
        var (_, missing) = Locus.Core.Tools.ToolArguments.Check("{\"count\": 3}", Schema);
        Assert.Contains("Missing required argument: query", missing);
        Assert.Contains("\"required\":[\"query\"]", missing);
        var (_, broken) = Locus.Core.Tools.ToolArguments.Check("{\"query\": \"x", Schema);
        Assert.Contains("weren't valid JSON", broken);
        Assert.Contains("You sent: {\"query\": \"x", broken);
        Assert.Null(Locus.Core.Tools.ToolArguments.Check("{\"query\": \"ok\"}", Schema).problem);
    }
}

public sealed class TidyRoundsTests
{
    [Fact]
    public void CollapsesBlankLinesButNotInCode()
    {
        Assert.Equal("I'll look.\n\nFound it.", Locus.Core.Services.ChatService.TidyRounds("I'll look.\n\n\n\n\n\nFound it."));
        var code = "```\na\n\n\n\nb\n```";
        Assert.Equal(code, Locus.Core.Services.ChatService.TidyRounds(code));
    }
}
