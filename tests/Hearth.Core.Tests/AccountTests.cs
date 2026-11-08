using Hearth.Core.Services;

namespace Hearth.Core.Tests;

public sealed class AccountTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hearth-accounts-").FullName;

    [Fact]
    public async Task AccountsSignInSeparatelyAndStayOffUntilTurnedOn()
    {
        var store = new AccountStore(_dir);
        Assert.False(store.Enabled);
        Assert.NotNull(await store.EnableAsync("Mitch", "short")); // too short
        Assert.Null(await store.EnableAsync("Mitch", "mitch-password-1"));
        Assert.True(store.Enabled);
        var (wife, problem) = await store.AddAsync("Sam", "sam-password-1");
        Assert.Null(problem);
        Assert.NotNull((await store.AddAsync("sam", "another-pass")).problem); // names are unique, any case

        var owner = store.Verify("mitch", "mitch-password-1")!;
        Assert.True(owner.IsOwner);
        Assert.Equal(_dir, store.DataDirectoryFor(owner)); // the owner keeps the original data
        Assert.Equal(Path.Combine(_dir, "accounts", wife!.Id), store.DataDirectoryFor(wife));
        Assert.Null(store.Verify("Sam", "mitch-password-1"));
        Assert.Null(store.Verify("Nobody", "sam-password-1"));
        Assert.Equal(wife.Id, store.Verify("Sam", "sam-password-1")!.Id);

        // Cookies can't be forged or moved to another account, and stop working when the password changes.
        var cookie = store.CookieFor(wife);
        Assert.Equal(wife.Id, store.FromCookie(cookie)!.Id);
        Assert.Null(store.FromCookie(owner.Id + "." + cookie.Split('.')[1]));
        Assert.Null(store.FromCookie("garbage"));
        await store.ChangePasswordAsync(wife.Id, "new-sam-password");
        Assert.Null(store.FromCookie(cookie));

        // Saved, and nothing secret in plain text; reloads the same.
        var file = await File.ReadAllTextAsync(Path.Combine(_dir, "accounts.json"));
        Assert.DoesNotContain("new-sam-password", file);
        var again = new AccountStore(_dir);
        Assert.Equal(2, again.Get().Accounts.Count);
        Assert.NotNull(again.Verify("Sam", "new-sam-password"));

        // The owner can't be removed; turning accounts off keeps everyone for later.
        await store.RemoveAsync(owner.Id);
        Assert.NotNull(store.Owner);
        await store.DisableAsync();
        Assert.False(store.Enabled);
        Assert.Equal(2, store.Get().Accounts.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
