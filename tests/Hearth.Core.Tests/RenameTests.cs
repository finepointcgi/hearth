using Hearth.Core.Services;

namespace Hearth.Core.Tests;

/// <summary>An install from when Hearth was called Locus keeps its data.</summary>
public sealed class RenameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hearth-rename-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void TheOldDataFolderIsMovedToTheNewName()
    {
        var old = Directory.CreateDirectory(Path.Combine(_root, "Locus")).FullName;
        File.WriteAllText(Path.Combine(old, "settings.json"), "{}");

        var dir = LegacyNames.DataFolder(_root);

        Assert.Equal(Path.Combine(_root, "Hearth"), dir);
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
        Assert.False(Directory.Exists(old));
        Assert.Equal(dir, LegacyNames.DataFolder(_root)); // and it stays put afterwards
    }

    [Fact]
    public void ANewInstallJustUsesTheNewName()
    {
        Assert.Equal(Path.Combine(_root, "Hearth"), LegacyNames.DataFolder(_root));
        Assert.Equal(Path.Combine(_root, "hearth.db"), LegacyNames.Database(_root));
    }

    [Fact]
    public void TheOldDatabaseIsRenamedWithItsJournal()
    {
        File.WriteAllText(Path.Combine(_root, "locus.db"), "db");
        File.WriteAllText(Path.Combine(_root, "locus.db-wal"), "wal");

        var path = LegacyNames.Database(_root);

        Assert.Equal(Path.Combine(_root, "hearth.db"), path);
        Assert.Equal("db", File.ReadAllText(path));
        Assert.Equal("wal", File.ReadAllText(path + "-wal"));
        Assert.False(File.Exists(Path.Combine(_root, "locus.db")));
    }
}
