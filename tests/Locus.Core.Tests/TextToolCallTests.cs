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
    public void OrdinaryJsonIsLeftAlone()
    {
        var text = "Here's the config:\n```json\n{\"name\": \"my-app\", \"version\": 2}\n```";
        var (calls, rest) = TextToolCalls.Find(text, Tools);
        Assert.Empty(calls);
        Assert.Equal(text, rest);
    }
}
