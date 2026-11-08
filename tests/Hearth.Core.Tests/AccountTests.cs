using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Services.Agents;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task OnlyTheOwnerAndPeopleTheyAllowCanUseThisComputer()
    {
        var store = new AccountStore(_dir);
        await store.EnableAsync("Mitch", "mitch-password-1");
        var sam = (await store.AddAsync("Sam", "sam-password-1")).account!;
        var owner = store.Owner!;
        Assert.True(store.MayUseComputer(owner.Id));
        Assert.False(store.MayUseComputer(sam.Id)); // off for anyone added

        await store.SetComputerAccessAsync(sam.Id, true);
        Assert.True(store.MayUseComputer(sam.Id));
        Assert.True(new AccountStore(_dir).MayUseComputer(sam.Id)); // saved
        await store.SetComputerAccessAsync(owner.Id, false); // the owner always can
        Assert.True(store.MayUseComputer(owner.Id));
        await store.SetComputerAccessAsync(sam.Id, false);
        Assert.False(store.MayUseComputer(sam.Id));
        await store.SetComputerAccessAsync(sam.Id, true);
        await store.RemoveAsync(sam.Id);
        Assert.False(store.MayUseComputer(sam.Id)); // gone: no access

        // The check is asked each time, so a change applies to services already running.
        var allowed = false;
        var platform = new PlatformInfo("test", false, CanRunProcesses: true) { ComputerAccess = () => allowed };
        Assert.False(platform.CanUseComputer);
        Assert.Contains("isn't allowed", platform.ComputerProblem);
        allowed = true;
        Assert.True(platform.CanUseComputer);
        Assert.Null(platform.ComputerProblem);
        Assert.False(new PlatformInfo("phone", true, CanRunProcesses: false) { ComputerAccess = () => true }.CanUseComputer);
        Assert.True(new PlatformInfo("desktop", false).CanUseComputer); // one person: always
    }

    private sealed class Paths(string dir) : IAppPaths { public string DataDirectory => dir; }

    [Fact]
    public async Task AnAccountThatIsNotAllowedCannotReachThisComputer()
    {
        var allowed = false;
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "sam")).FullName;
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new Paths(dir));
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: true) { ComputerAccess = () => allowed });
        await using var sp = services.BuildServiceProvider();

        // Other agents (they'd act with the owner's logins and files), and their memory and skills.
        Assert.False(sp.GetRequiredService<AgentHub>().Available);
        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => sp.GetRequiredService<AgentHub>().StartAsync("hermes", "hi", dir));
        Assert.Contains("isn't allowed", start.Message);
        var care = sp.GetRequiredService<AgentCare>();
        var hermes = new AgentPlace(AgentCatalog.Find("hermes")!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => care.MemoryFilesAsync(hermes));
        await Assert.ThrowsAsync<InvalidOperationException>(() => care.SaveAsync(hermes, "SOUL.md", "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => care.ShareSkillsAsync(hermes, true));

        // Code, commands, Python and SSH to other computers.
        Assert.False(sp.GetRequiredService<CodeTools>().Available);
        var settings = new AppSettings();
        settings.Computer.Enabled = true;
        settings.Computer.AllowCommands = true;
        settings.Tools.PythonEnabled = true;
        settings.RemoteHosts.Add(new RemoteHost { Name = "homelab", Target = "me@homelab" });
        var catalog = await sp.GetRequiredService<BuiltInToolProvider>().CatalogAsync(settings);
        foreach (var t in catalog.Where(t => t.tool is RunCommandTool or RunRemoteTool or PythonTool))
            Assert.Contains("isn't allowed", t.unavailableReason);
        Assert.Equal(3, catalog.Count(t => t.tool is RunCommandTool or RunRemoteTool or PythonTool));

        // Folders on this computer.
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "notes")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.md"), "hello");
        var folders = sp.GetRequiredService<LinkedFolders>();
        Assert.Contains("isn't allowed", await folders.AddAsync(folder));
        Assert.Empty(await folders.ListAsync());

        var hub = sp.GetRequiredService<AgentHub>();
        hub.Locator = def => "/bin/" + def.Command; // pretend every agent is installed
        Assert.All(await hub.StatusAsync(), a => Assert.False(a.Installed)); // nothing is looked for

        // Once allowed, the same running services can.
        allowed = true;
        Assert.True(hub.Available);
        Assert.All(await hub.StatusAsync(), a => Assert.True(a.Installed)); // looked for again, no "look again" needed
        Assert.True(sp.GetRequiredService<CodeTools>().Available);
        Assert.Null(await folders.AddAsync(folder));
        Assert.NotNull(await folders.FolderForAsync(Path.Combine(folder, "a.md")));
        allowed = false; // taken away again: linked folders can't be read through any more
        Assert.Null(await folders.FolderForAsync(Path.Combine(folder, "a.md")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
