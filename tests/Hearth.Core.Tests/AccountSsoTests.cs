using Hearth.Core.Services;

namespace Hearth.Core.Tests;

/// <summary>Single sign-on: provider identities mapped to Hearth accounts.</summary>
public sealed class AccountSsoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hearth-sso-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Only_the_owner_turns_accounts_on_then_everyone_gets_a_linked_account()
    {
        var store = new AccountStore(_dir);

        // Before accounts are on, only the configured owner may sign in.
        var (none, problem) = await store.SignInExternalAsync("idp|bob", "bob", ownerUser: "alice", autoCreate: true);
        Assert.Null(none);
        Assert.Contains("owner", problem);
        Assert.False(store.Enabled);

        var (alice, _) = await store.SignInExternalAsync("idp|alice", "Alice", ownerUser: "alice", autoCreate: true);
        Assert.NotNull(alice);
        Assert.True(alice!.IsOwner);
        Assert.True(store.Enabled);
        Assert.Equal("idp|alice", alice.ExternalId);

        // Same identity, same account (even after a rename at the provider).
        var (again, _) = await store.SignInExternalAsync("idp|alice", "alice.smith", "alice", true);
        Assert.Equal(alice.Id, again!.Id);

        // Someone new gets their own account, without computer access and with no known password.
        var (bob, _) = await store.SignInExternalAsync("idp|bob", "bob", "alice", true);
        Assert.False(bob!.IsOwner);
        Assert.False(bob.CanUseComputer);
        Assert.Null(store.Verify("bob", ""));
        Assert.NotEqual(store.DataDirectoryFor(alice), store.DataDirectoryFor(bob));
    }

    [Fact]
    public async Task Existing_password_accounts_are_linked_by_name_and_names_never_collide()
    {
        var store = new AccountStore(_dir);
        await store.EnableAsync("owner", "long password 1");
        var (carol, _) = await store.AddAsync("carol", "long password 2");

        var (linked, _) = await store.SignInExternalAsync("idp|carol", "Carol", null, true);
        Assert.Equal(carol!.Id, linked!.Id);

        // A different person with the same user name at another provider doesn't take Carol's account.
        var (other, _) = await store.SignInExternalAsync("other-idp|carol", "carol", null, true);
        Assert.NotEqual(carol.Id, other!.Id);
        Assert.Equal("carol 2", other.Name);

        // With automatic accounts off, unknown people are refused.
        var (refused, why) = await store.SignInExternalAsync("idp|dave", "dave", null, autoCreate: false);
        Assert.Null(refused);
        Assert.Contains("no Hearth account", why);

        // The link survives a restart.
        var reloaded = new AccountStore(_dir);
        Assert.Equal(carol.Id, (await reloaded.SignInExternalAsync("idp|carol", "carol", null, false)).account!.Id);
    }
}
