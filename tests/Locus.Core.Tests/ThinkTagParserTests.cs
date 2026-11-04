using Locus.Core.Llm;

namespace Locus.Core.Tests;

public class ThinkTagParserTests
{
    private static (string content, string thinking) Run(params string[] chunks)
    {
        var p = new ThinkTagParser();
        string c = "", t = "";
        foreach (var chunk in chunks)
        {
            var (cc, tt) = p.Push(chunk);
            c += cc; t += tt;
        }
        var (fc, ft) = p.Flush();
        return (c + fc, t + ft);
    }

    [Fact]
    public void PlainTextPassesThrough() =>
        Assert.Equal(("hello world", ""), Run("hello ", "world"));

    [Fact]
    public void SplitsThinkBlock() =>
        Assert.Equal(("Answer", "reasoning"), Run("<think>reasoning</think>Answer"));

    [Fact]
    public void HandlesTagsSplitAcrossChunks() =>
        Assert.Equal(("42", "hmm"), Run("<th", "ink>h", "mm</thi", "nk>4", "2"));

    [Fact]
    public void UnterminatedThinkIsThinking() =>
        Assert.Equal(("", "still going"), Run("<think>still ", "going"));

    [Fact]
    public void LessThanInContentIsKept() =>
        Assert.Equal(("a < b <t", ""), Run("a < b <t"));
}
