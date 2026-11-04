using Locus.Core.Services;

namespace Locus.Core.Tests;

public class LoginVaultTests
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-vault-").FullName;
    }

    [Fact]
    public async Task EncryptsAtRestAndRoundTrips()
    {
        var paths = new TempPaths();
        var vault = new LoginVault(paths);
        await vault.SaveAsync(new SavedLogin { Name = "github", Site = "github.com", Username = "me" }, "s3cret-pässword");

        var json = await File.ReadAllTextAsync(Path.Combine(paths.DataDirectory, "logins.json"));
        Assert.DoesNotContain("s3cret", json);

        // A fresh vault (new process) decrypts with the stored key.
        var reopened = new LoginVault(paths);
        var login = await reopened.FindAsync("https://github.com/login");
        Assert.NotNull(login);
        Assert.Equal("s3cret-pässword", reopened.RevealPassword(login));

        // Saving without a new password keeps the old one.
        login.Username = "me2";
        await reopened.SaveAsync(login, null);
        Assert.Equal("s3cret-pässword", reopened.RevealPassword((await reopened.FindAsync("github"))!));

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(paths.DataDirectory, "vault.key")));
        Directory.Delete(paths.DataDirectory, true);
    }
}
