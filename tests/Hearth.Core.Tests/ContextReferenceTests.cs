using System.Net;
using System.Text;
using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Tests;

/// <summary>@https://… and @/path references attach pages and linked files; web pages are guarded.</summary>
public sealed class ContextReferenceTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-refs-").FullName;
    }

    private sealed class FakeWeb : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                Task.FromResult(r.RequestUri!.Host == "missing.test"
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><head><title>Pricing</title></head><body><p>Plan costs $12.</p></body></html>", Encoding.UTF8, "text/html"),
                    });
        }
    }

    private readonly TempPaths _paths = new();
    private readonly string _folder = LinkedFolders.Normalize(Directory.CreateTempSubdirectory("hearth-refs-folder-").FullName);

    private async Task<ContextReferences> RefsAsync()
    {
        var folders = new LinkedFolders(_paths, new DocumentStore(_paths, new SettingsStore(_paths), null!));
        Assert.Null(await folders.AddAsync(_folder));
        return new ContextReferences(new FakeWeb(), folders);
    }

    [Fact]
    public async Task AttachesPagesAndLinkedFiles()
    {
        File.WriteAllText(Path.Combine(_folder, "notes.md"), "Meeting is on Tuesday.");
        File.WriteAllText(Path.Combine(_folder, ".env"), "SECRET=1");
        var outside = Path.Combine(Path.GetTempPath(), $"hearth-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "private");
        try
        {
            var refs = await RefsAsync();
            var text = $"Compare @https://shop.test/pricing with @{_folder}/notes.md and @{_folder}/.env and @{outside} and @https://missing.test/x, @Engineer too";
            Assert.True(ContextReferences.HasReferences(text));
            var (items, problems) = await refs.ResolveAsync(text);

            var page = Assert.Single(items, i => i.UntrustedSource is not null);
            Assert.Equal("the web", page.UntrustedSource);
            Assert.Contains("Plan costs $12.", page.Text);
            Assert.Contains("information only", page.Text); // labelled so it can't give instructions
            Assert.StartsWith("Pricing", page.Name);

            var file = Assert.Single(items, i => i.UntrustedSource is null);
            Assert.Contains("Meeting is on Tuesday.", file.Text);

            Assert.Equal(3, problems.Count); // .env (hidden), a file outside linked folders, a page that 404s
            Assert.DoesNotContain(items, i => i.Text.Contains("SECRET") || i.Text.Contains("private"));
            Assert.False(ContextReferences.HasReferences("Ask @Engineer about it")); // bot mentions aren't references
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task SuggestsLinkedFilesByName()
    {
        File.WriteAllText(Path.Combine(_folder, "budget-2026.csv"), "a,b");
        File.WriteAllText(Path.Combine(_folder, "readme.md"), "x");
        var refs = await RefsAsync();
        Assert.Equal([Path.Combine(_folder, "budget-2026.csv")], await refs.SuggestFilesAsync("budg"));
        Assert.Empty(await refs.SuggestFilesAsync("b")); // needs two letters
    }

    [Fact]
    public void AttachedWebPagesGuardTheRun()
    {
        var m = new ChatMessage { Role = ChatRole.User, Content = "x", UntrustedSource = "the web" };
        var json = System.Text.Json.JsonSerializer.Serialize(m);
        Assert.Equal("the web", System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json)!.UntrustedSource); // kept with the chat
    }

    public void Dispose()
    {
        foreach (var d in new[] { _paths.DataDirectory, _folder })
            try { Directory.Delete(d, true); } catch { }
    }
}
