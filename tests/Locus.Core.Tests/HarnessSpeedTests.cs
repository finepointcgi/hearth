using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;

namespace Locus.Core.Tests;

public sealed class HarnessSpeedTests
{
    private static ToolCall Result(int chars) => new() { Name = "fetch_url", Result = new string('x', chars), Status = ToolCallStatus.Done };

    [Fact]
    public void ShortenedResultsStayShortenedSoThePromptOnlyGrowsAtTheEnd()
    {
        var calls = Enumerable.Range(0, 6).Select(_ => Result(6000)).ToList();
        ContextWindow.ShortenOlderResults(calls, tokensToFree: 3000);
        // Oldest first, newest three kept whole, and only as many as needed.
        Assert.Equal(800, calls[0].SentChars);
        Assert.Equal(800, calls[1].SentChars);
        Assert.Null(calls[2].SentChars);
        Assert.All(calls.Skip(3), c => Assert.Null(c.SentChars));

        // Later steps never undo or redo it: what was sent before is sent the same way again.
        calls.Add(Result(6000));
        ContextWindow.ShortenOlderResults(calls, tokensToFree: 1);
        Assert.Equal(800, calls[0].SentChars);
        Assert.Equal(800, calls[1].SentChars);
        Assert.Equal(800, calls[2].SentChars);
        Assert.StartsWith(new string('x', 800) + "\n… [shortened", ContextWindow.ClipResult(calls[0].Result!, 800));
    }

    [Fact]
    public void RepeatedMenuLinesAreDroppedFromPages()
    {
        var page = "Home\nNews\nGoogle shares rose 4% after earnings.\nHome\nNews\n\nShare\nShare\nAnalysts expect more.";
        Assert.Equal("Home\nNews\nGoogle shares rose 4% after earnings.\n\nShare\nAnalysts expect more.", FetchUrlTool.DropRepeats(page));
    }

    [Fact]
    public async Task BackgroundWorkWaitsWhileAChatUsesTheSameLocalServer()
    {
        var local = new ProviderProfile { Name = "oMLX", Kind = ProviderKind.OpenAICompatible };
        var claude = new ProviderProfile { Name = "Claude", Kind = ProviderKind.ClaudeCode };
        ModelQueue.Poll = TimeSpan.FromMilliseconds(10);

        // Chats never wait.
        using (ModelQueue.ChatRunning(local))
            Assert.False(await ModelQueue.WaitForChatsAsync(local, CancellationToken.None));

        var chat = ModelQueue.ChatRunning(local);
        var background = Task.Run(async () =>
        {
            ModelQueue.MarkBackground();
            Assert.False(await ModelQueue.WaitForChatsAsync(claude, CancellationToken.None)); // hosted: no queue
            return await ModelQueue.WaitForChatsAsync(local, CancellationToken.None);
        });
        await Task.Delay(100);
        Assert.False(background.IsCompleted); // held while the chat runs
        chat.Dispose();
        Assert.True(await background.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, ModelQueue.ChatsOn(local.Id));
        Assert.False(ModelQueue.InBackground); // marking stays inside the background run
    }
}
