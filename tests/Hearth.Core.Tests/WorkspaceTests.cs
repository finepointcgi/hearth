using Hearth.Core.Tools;

namespace Hearth.Core.Tests;

public class WorkspaceTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "hearth-ws-test");

    [Theory]
    [InlineData("notes.md")]
    [InlineData("reports/weekly.md")]
    [InlineData("/reports/weekly.md")]
    [InlineData("a/../b.txt")]
    public void AllowsPathsInside(string rel) =>
        Assert.StartsWith(Path.GetFullPath(Root), Workspace.Resolve(Root, rel));

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("a/../../secret.txt")]
    [InlineData("../hearth-ws-test-evil/x")]
    public void BlocksEscapes(string rel) =>
        Assert.Throws<UnauthorizedAccessException>(() => Workspace.Resolve(Root, rel));
}
