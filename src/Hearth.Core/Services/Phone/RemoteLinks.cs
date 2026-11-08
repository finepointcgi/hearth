using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearth.Core.Models;
using Hearth.Core.Services.Agents;

namespace Hearth.Core.Services.Phone;

/// <summary>Ways to reach Hearth from a phone away from home (Settings → Phone access).</summary>
public sealed class RemoteAccessSettings
{
    /// <summary>Keep a Cloudflare quick tunnel running (started again with Hearth).</summary>
    public bool CloudflareTunnel { get; set; }
    /// <summary>Send the new link through your notifications whenever it changes (quick tunnel links change on every start).</summary>
    public bool SendNewLink { get; set; } = true;
}

/// <summary>
/// A Cloudflare quick tunnel: a free https://…trycloudflare.com link to Hearth from anywhere, with no account, no
/// router changes and nothing to install on the phone. The link is public and changes every time the tunnel starts,
/// so it only starts while Hearth asks for a sign-in, and the new link can be sent to you.
/// </summary>
public sealed partial class CloudflareTunnel(SettingsStore settingsStore, PlatformInfo platform, PushNotifier notifier, IAccessInfo? access = null) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private bool _wanted, _disposed;
    private int _failures;

    /// <summary>The public link while the tunnel is up.</summary>
    public string? Url { get; private set; }
    /// <summary>Why it isn't running (couldn't start, stopped by itself…).</summary>
    public string? Problem { get; private set; }
    public bool Running => _process is { HasExited: false };
    public bool Starting => Running && Url is null;
    public event Action? Changed;

    /// <summary>For tests: the program to run instead of cloudflared.</summary>
    public Func<string?>? Locator { get; set; }
    /// <summary>For tests: how long to wait before starting again after the tunnel stops by itself.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    public string? Program => Locator is { } l ? l() : Locate();

    public static string? Locate() =>
        AgentCatalog.Locate("cloudflared")
        ?? new[] { @"C:\Program Files (x86)\cloudflared\cloudflared.exe", @"C:\Program Files\cloudflared\cloudflared.exe" }.FirstOrDefault(File.Exists);

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com")] private static partial Regex LinkPattern();

    /// <summary>The quick-tunnel link in a line cloudflared printed, if there is one.</summary>
    public static string? FindUrl(string line) => LinkPattern().Match(line) is { Success: true } m && !m.Value.StartsWith("https://api.") ? m.Value : null;

    /// <summary>Where the tunnel sends visitors: this computer's own address for Hearth's port.</summary>
    public static string? Target(IEnumerable<string> listenUrls)
    {
        foreach (var raw in listenUrls)
        {
            if (!Uri.TryCreate(raw.Replace("+", "0.0.0.0").Replace("*", "0.0.0.0").Replace("[::]", "0.0.0.0"), UriKind.Absolute, out var u)) continue;
            var host = u.Host is "0.0.0.0" or "localhost" or "[::1]" or "::1" ? "127.0.0.1" : u.Host;
            return $"http://{host}:{u.Port}";
        }
        return null;
    }

    /// <summary>Why the tunnel can't start here, or null.</summary>
    public string? CannotStart()
    {
        if (access is null) return "The public link works with the web version of Hearth (the background service or Docker).";
        if (!platform.CanUseComputer) return platform.ComputerProblem + ".";
        if (!access.SignInRequired) return "Hearth has no password, so a public link would let anyone use it. Set a password (HEARTH_PASSWORD) or turn on separate accounts first.";
        if (Program is null) return "cloudflared isn't installed. On a Mac: brew install cloudflared (or download it from Cloudflare).";
        if (Target(access.ListenUrls) is null) return "Hearth isn't listening on a port yet.";
        return null;
    }

    /// <summary>Starts the tunnel and waits for its link. Returns a problem, or null once the link is up.</summary>
    public async Task<string?> StartAsync(bool remember = true, CancellationToken ct = default)
    {
        if (CannotStart() is { } problem) { Problem = problem; Changed?.Invoke(); return problem; }
        if (remember) await RememberAsync(true);
        _wanted = true;
        await _lock.WaitAsync(ct);
        try
        {
            if (!Running) Launch();
        }
        finally { _lock.Release(); }
        // The link shows up within a few seconds.
        for (var i = 0; i < 60 && Url is null && Running; i++) await Task.Delay(500, ct);
        return Url is not null ? null : Problem ??= "Cloudflare didn't give a link. Try again in a minute.";
    }

    /// <summary>Starts it if it was left on (when Hearth starts).</summary>
    public void EnsureStarted()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if ((await settingsStore.GetAsync()).RemoteAccess.CloudflareTunnel) await StartAsync(remember: false);
            }
            catch { /* shown on the Phone access card */ }
        });
    }

    public async Task StopAsync(bool remember = true)
    {
        _wanted = false;
        if (remember) await RememberAsync(false);
        await _lock.WaitAsync();
        try { Kill(); }
        finally { _lock.Release(); }
        Url = null;
        Problem = null;
        Changed?.Invoke();
    }

    private async Task RememberAsync(bool on)
    {
        var s = await settingsStore.GetAsync();
        if (s.RemoteAccess.CloudflareTunnel == on) return;
        s.RemoteAccess.CloudflareTunnel = on;
        await settingsStore.SaveAsync(s);
    }

    private void Launch()
    {
        var psi = new ProcessStartInfo(Program!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AgentCatalog.HomeFolder,
        };
        foreach (var a in new[] { "tunnel", "--no-autoupdate", "--url", Target(access!.ListenUrls)! }) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = AgentCatalog.ProcessPath();
        Url = null;
        Problem = null;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => OnExited(p);
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
        Changed?.Invoke();
    }

    private void OnLine(string? line)
    {
        if (line is null || Url is not null || FindUrl(line) is not { } url) return;
        Url = url;
        _failures = 0;
        Changed?.Invoke();
        _ = SendLinkAsync(url);
    }

    private async Task SendLinkAsync(string url)
    {
        try
        {
            var s = await settingsStore.GetAsync();
            if (!s.RemoteAccess.SendNewLink || !s.Notifications.Enabled) return;
            var n = s.Notifications;
            // Tapping it opens the new link.
            var to = new NotificationSettings { Server = n.Server, Topic = n.Topic, Token = n.Token, OpenUrl = url };
            await notifier.SendAsync(to, "Hearth has a new link", n.IncludeDetails ? $"Open Hearth from anywhere: {url}" : "Tap to open Hearth from anywhere.", tag: "link");
        }
        catch { /* the link is on the Phone access card too */ }
    }

    private void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _process)) return;
        var code = 0;
        try { code = p.ExitCode; } catch { }
        Url = null;
        Problem = $"The tunnel stopped (cloudflared exited with code {code}).";
        Changed?.Invoke();
        if (!_wanted || _disposed) return;
        // Started again on its own (with a new link), waiting longer each time it keeps failing.
        var wait = TimeSpan.FromTicks(RetryDelay.Ticks * Math.Min(20, 1 << Math.Min(_failures++, 5)));
        _ = Task.Run(async () =>
        {
            await Task.Delay(wait);
            if (!_wanted || _disposed) return;
            await _lock.WaitAsync();
            try { if (!Running && CannotStart() is null) Launch(); }
            catch (Exception ex) { Problem = ex.Message; Changed?.Invoke(); }
            finally { _lock.Release(); }
        });
    }

    private void Kill()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { }
        p.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Kill();
    }
}

/// <summary>NetBird (open source, free plan or self-hosted): this computer's name and address on your NetBird network.</summary>
public static class NetBird
{
    public static string? Locate() =>
        AgentCatalog.Locate("netbird")
        ?? new[] { "/Applications/NetBird.app/Contents/MacOS/netbird", "/usr/bin/netbird", @"C:\Program Files\Netbird\netbird.exe" }.FirstOrDefault(File.Exists);

    /// <summary>Name (or address) from `netbird status --json`; null when it isn't connected.</summary>
    public static string? Parse(string json)
    {
        try
        {
            var o = JsonNode.Parse(json);
            var management = o?["management"]?["connected"]?.GetValue<bool>();
            if (management == false) return null;
            var fqdn = o?["fqdn"]?.ToString().TrimEnd('.');
            if (!string.IsNullOrEmpty(fqdn)) return fqdn;
            var ip = o?["netbirdIp"]?.ToString();
            return string.IsNullOrEmpty(ip) ? null : ip.Split('/')[0];
        }
        catch { return null; }
    }

    public static async Task<string?> NameAsync(PlatformInfo platform)
    {
        if (!platform.CanUseComputer || Locate() is not { } exe) return null;
        try
        {
            var (exit, output) = await AgentCatalog.RunAsync(exe, ["status", "--json"], TimeSpan.FromSeconds(10));
            return exit == 0 ? Parse(output) : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Slows down password guessing on the sign-in page: after too many wrong passwords from one address, sign-in is
/// refused from there for a while. Matters most once Hearth has a public link.
/// </summary>
public sealed class LoginGuard(int maxFailures = 10, TimeSpan? window = null, Func<DateTimeOffset>? clock = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(15);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    /// <summary>Whether this address may try now.</summary>
    public bool Allowed(string who)
    {
        if (!_failures.TryGetValue(who, out var list)) return true;
        lock (list)
        {
            list.RemoveAll(t => _now() - t > _window);
            return list.Count < maxFailures;
        }
    }

    public void Failed(string who)
    {
        var list = _failures.GetOrAdd(who, _ => []);
        lock (list) list.Add(_now());
        if (_failures.Count > 10_000) _failures.Clear(); // never grows without bound
    }

    public void Succeeded(string who) => _failures.TryRemove(who, out _);
}
