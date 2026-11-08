using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Services.Phone;
using Microsoft.Extensions.DependencyInjection;

namespace Hearth.Core.Tests;

/// <summary>Ways in from a phone away from home: Cloudflare quick tunnel, NetBird, and the sign-in guard.</summary>
public sealed class RemoteLinksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hearth-links-").FullName;

    private sealed class Paths(string dir) : IAppPaths { public string DataDirectory => dir; }

    private sealed class Access(bool signIn, params string[] urls) : IAccessInfo
    {
        public IReadOnlyList<string> ListenUrls => urls;
        public bool PasswordSet => false;
        public bool SignInRequired => signIn;
    }

    [Fact]
    public void FindsTheQuickTunnelLinkAndWhereItPoints()
    {
        Assert.Equal("https://lucky-otter-ab12.trycloudflare.com",
            CloudflareTunnel.FindUrl("2026-11-08T21:00:01Z INF |  https://lucky-otter-ab12.trycloudflare.com                            |"));
        Assert.Null(CloudflareTunnel.FindUrl("INF Requesting new quick Tunnel on trycloudflare.com..."));
        Assert.Null(CloudflareTunnel.FindUrl("ERR failed to reach https://api.trycloudflare.com/tunnel"));

        Assert.Equal("http://127.0.0.1:5290", CloudflareTunnel.Target(["http://0.0.0.0:5290"]));
        Assert.Equal("http://127.0.0.1:5292", CloudflareTunnel.Target(["http://localhost:5292"]));
        Assert.Equal("http://127.0.0.1:80", CloudflareTunnel.Target(["http://[::]:80"]));
        Assert.Equal("http://127.0.0.1:8080", CloudflareTunnel.Target(["http://+:8080"]));
        Assert.Equal("http://10.0.0.112:5290", CloudflareTunnel.Target(["http://10.0.0.112:5290"])); // only listening there
        Assert.Null(CloudflareTunnel.Target([]));
    }

    [Fact]
    public void ReadsNetBirdsNameOrAddress()
    {
        Assert.Equal("mac.netbird.cloud", NetBird.Parse("""{"management":{"connected":true},"netbirdIp":"100.92.1.5/16","fqdn":"mac.netbird.cloud."}"""));
        Assert.Equal("100.92.1.5", NetBird.Parse("""{"management":{"connected":true},"netbirdIp":"100.92.1.5/16","fqdn":""}"""));
        Assert.Null(NetBird.Parse("""{"management":{"connected":false},"netbirdIp":"100.92.1.5/16","fqdn":"mac.netbird.cloud"}"""));
        Assert.Null(NetBird.Parse("Daemon status: NeedsLogin"));
    }

    [Fact]
    public void TooManyWrongPasswordsAreRefusedForAWhile()
    {
        var now = DateTimeOffset.UtcNow;
        var guard = new LoginGuard(maxFailures: 3, window: TimeSpan.FromMinutes(15), clock: () => now);
        for (var i = 0; i < 3; i++) { Assert.True(guard.Allowed("1.2.3.4")); guard.Failed("1.2.3.4"); }
        Assert.False(guard.Allowed("1.2.3.4"));
        Assert.True(guard.Allowed("5.6.7.8")); // others aren't affected
        now += TimeSpan.FromMinutes(16);
        Assert.True(guard.Allowed("1.2.3.4")); // the wait is over
        guard.Failed("1.2.3.4");
        guard.Succeeded("1.2.3.4");
        guard.Failed("1.2.3.4"); guard.Failed("1.2.3.4");
        Assert.True(guard.Allowed("1.2.3.4")); // signing in clears the count
    }

    private (ServiceProvider sp, CloudflareTunnel tunnel) Build(IAccessInfo? access, Func<bool>? computer = null)
    {
        var s = new ServiceCollection();
        s.AddSingleton<IAppPaths>(new Paths(Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName));
        s.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: true) { ComputerAccess = computer });
        if (access is not null) s.AddSingleton(access);
        var sp = s.BuildServiceProvider();
        return (sp, sp.GetRequiredService<CloudflareTunnel>());
    }

    /// <summary>A pretend cloudflared: prints a new quick-tunnel link each start, then keeps running (or stops when told).</summary>
    private string FakeCloudflared()
    {
        var path = Path.Combine(_dir, "cloudflared");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            cd '{{_dir}}'
            n=$(cat count 2>/dev/null || echo 0); n=$((n+1)); echo $n > count
            echo "$@" > args
            echo "INF Requesting new quick Tunnel on trycloudflare.com..." >&2
            echo "INF |  https://test-link-$n.trycloudflare.com  |" >&2
            if [ -f die ]; then rm die; sleep 1; exit 1; fi
            sleep 60
            """.Replace("\r", ""));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task OnlyStartsWhenHearthAsksForASignIn()
    {
        var (sp, tunnel) = Build(new Access(signIn: false, "http://0.0.0.0:5290"));
        await using var _ = sp;
        tunnel.Locator = () => "/bin/true";
        Assert.Contains("no password", await tunnel.StartAsync());
        Assert.False(tunnel.Running);

        var (sp2, desktop) = Build(access: null); // the desktop app has no web address
        await using var __ = sp2;
        Assert.Contains("web version", desktop.CannotStart());

        var (sp3, blocked) = Build(new Access(true, "http://0.0.0.0:5290"), computer: () => false);
        await using var ___ = sp3;
        blocked.Locator = () => "/bin/true";
        Assert.Contains("isn't allowed", blocked.CannotStart()); // an account that may not use this computer

        var (sp4, missing) = Build(new Access(true, "http://0.0.0.0:5290"));
        await using var ____ = sp4;
        missing.Locator = () => null;
        Assert.Contains("brew install cloudflared", missing.CannotStart());
    }

    [Fact]
    public async Task StartsGetsALinkStartsAgainAndStops()
    {
        if (OperatingSystem.IsWindows()) return; // the pretend cloudflared is a shell script
        var (sp, tunnel) = Build(new Access(true, "http://0.0.0.0:5290"));
        await using var _ = sp;
        tunnel.Locator = FakeCloudflared;
        tunnel.RetryDelay = TimeSpan.FromMilliseconds(200);

        Assert.Null(await tunnel.StartAsync());
        Assert.Equal("https://test-link-1.trycloudflare.com", tunnel.Url);
        Assert.True(tunnel.Running);
        Assert.Equal("tunnel --no-autoupdate --url http://127.0.0.1:5290", File.ReadAllText(Path.Combine(_dir, "args")).Trim());
        Assert.True((await sp.GetRequiredService<SettingsStore>().GetAsync()).RemoteAccess.CloudflareTunnel); // started again with Hearth

        // It stops by itself: started again, with a new link.
        await tunnel.StopAsync(remember: false);
        File.WriteAllText(Path.Combine(_dir, "die"), "");
        Assert.Null(await tunnel.StartAsync(remember: false));
        Assert.Equal("https://test-link-2.trycloudflare.com", tunnel.Url);
        for (var i = 0; i < 100 && tunnel.Url != "https://test-link-3.trycloudflare.com"; i++) await Task.Delay(100);
        Assert.Equal("https://test-link-3.trycloudflare.com", tunnel.Url);

        await tunnel.StopAsync();
        Assert.False(tunnel.Running);
        Assert.Null(tunnel.Url);
        Assert.False((await sp.GetRequiredService<SettingsStore>().GetAsync()).RemoteAccess.CloudflareTunnel);
        await Task.Delay(500);
        Assert.Equal("3", File.ReadAllText(Path.Combine(_dir, "count")).Trim()); // stopping means it stays stopped
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
