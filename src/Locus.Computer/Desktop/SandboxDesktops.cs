using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Computer.Desktop;

/// <summary>
/// One private Linux desktop per bot, each in its own Docker container with a persistent home folder.
/// Its noVNC live view is published on 127.0.0.1 only, behind a random VNC password.
/// </summary>
public sealed partial class SandboxDesktops : ISandboxDesktops, IDisposable
{
    public const string Image = "locus-desktop:2";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, DesktopController> _controllers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUse = new();
    private readonly SettingsStore _settings;
    private readonly Timer _idle;
    private string? _docker;

    public SandboxDesktops(SettingsStore settings)
    {
        _settings = settings;
        _idle = new Timer(_ => _ = StopIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public event Action? Changed;

    public static string ContainerName(string botId) => "locus-desk-" + Safe().Replace(botId.ToLowerInvariant(), "");
    private static string VolumeName(string botId) => ContainerName(botId) + "-home";

    private string? Docker()
    {
        if (_docker is not null) return _docker;
        string[] dirs = [.. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator),
            "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/Applications/Docker.app/Contents/Resources/bin",
            @"C:\Program Files\Docker\Docker\resources\bin"];
        var exe = OperatingSystem.IsWindows() ? "docker.exe" : "docker";
        return _docker = dirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    private async Task<(int code, string output)> D(params string[] args) =>
        await DesktopBackends.RunAsync(Docker() ?? throw new InvalidOperationException("Docker isn't installed."), args);

    public async Task<string?> ProblemAsync()
    {
        if (Docker() is null) return "Install Docker Desktop (docker.com) to give bots their own sandboxed computer.";
        var (code, _) = await D("info", "--format", "{{.ServerVersion}}");
        return code == 0 ? null : "Docker is installed but not running. Start Docker Desktop.";
    }

    public async Task<SandboxInfo> InfoAsync(string botId)
    {
        if (await ProblemAsync() is { } problem) return new(false, null, problem);
        var (code, state) = await D("inspect", "-f", "{{.State.Running}}", ContainerName(botId));
        if (code != 0) return new(false, null, "Not created yet — it starts the first time the bot uses it.");
        if (state.Trim() != "true") return new(false, null, "Stopped — it starts again when the bot needs it.");
        return new(true, await ViewUrlAsync(botId), "Running");
    }

    private async Task<string?> ViewUrlAsync(string botId)
    {
        var name = ContainerName(botId);
        var (pc, port) = await D("port", name, "6080/tcp");
        var (_, env) = await D("inspect", "-f", "{{range .Config.Env}}{{println .}}{{end}}", name);
        var pw = env.Split('\n').FirstOrDefault(l => l.StartsWith("VNC_PASSWORD="))?["VNC_PASSWORD=".Length..].Trim();
        var hostPort = port.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("127.0.0.1:"))?.Split(':')[^1];
        if (pc != 0 || hostPort is null) return null;
        // The password goes in the #fragment, which the browser never sends to a server.
        return $"http://127.0.0.1:{hostPort}/vnc.html#autoconnect=1&resize=scale&reconnect=1&password={pw}";
    }

    private async Task EnsureImageAsync(CancellationToken ct)
    {
        if ((await D("image", "inspect", Image)).code == 0) return;
        var dir = Directory.CreateTempSubdirectory("locus-desktop-");
        try
        {
            var asm = typeof(SandboxDesktops).Assembly;
            foreach (var (res, file) in new[] { ("Sandbox.Dockerfile", "Dockerfile"), ("Sandbox.start.sh", "start.sh") })
            {
                await using var src = asm.GetManifestResourceStream(res)!;
                await using var dst = File.Create(Path.Combine(dir.FullName, file));
                await src.CopyToAsync(dst, ct);
            }
            var (code, output) = await D("build", "-t", Image, dir.FullName);
            if (code != 0) throw new InvalidOperationException("Building the sandbox image failed: " + Tail(output));
        }
        finally { dir.Delete(true); }
    }

    public async Task<SandboxInfo> StartAsync(string botId, CancellationToken ct = default)
    {
        if (await ProblemAsync() is { } problem) throw new InvalidOperationException(problem);
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureImageAsync(ct);
            var name = ContainerName(botId);
            var (exists, state) = await D("inspect", "-f", "{{.State.Running}}", name);
            if (exists != 0)
            {
                var pw = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(); // VNC uses 8 chars
                var (code, output) = await D("run", "-d", "--name", name, "--label", "locus.bot=" + botId,
                    "--shm-size", "1g", "--memory", "3g", "-p", "127.0.0.1::6080",
                    "-e", "VNC_PASSWORD=" + pw, "-e", "TZ=" + LocalTimeZone(), "-v", VolumeName(botId) + ":/home/bot", Image);
                if (code != 0) throw new InvalidOperationException("Couldn't start the sandbox: " + Tail(output));
            }
            else if (state.Trim() != "true")
            {
                var (code, output) = await D("start", name);
                if (code != 0) throw new InvalidOperationException("Couldn't start the sandbox: " + Tail(output));
            }
            // Wait for the X server.
            for (var i = 0; i < 40; i++)
            {
                if ((await D("exec", "-u", "bot", "-e", "DISPLAY=:1", name, "xdotool", "getdisplaygeometry")).code == 0) break;
                await Task.Delay(250, ct);
            }
            _lastUse[botId] = DateTimeOffset.Now;
            return new(true, await ViewUrlAsync(botId), "Running");
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>The controller the desktop_* tools use for this bot, starting its sandbox if needed.</summary>
    internal async Task<DesktopController> ControllerAsync(string botId, CancellationToken ct)
    {
        var info = await InfoAsync(botId);
        if (!info.Running) await StartAsync(botId, ct);
        _lastUse[botId] = DateTimeOffset.Now;
        return _controllers.GetOrAdd(botId, id => new DesktopController(new ContainerDesktop(Docker()!, ContainerName(id)), failSafe: false));
    }

    public async Task<ImageAttachment> ScreenshotAsync(string botId) =>
        await (await ControllerAsync(botId, CancellationToken.None)).ScreenshotAsync();

    public async Task StopAsync(string botId)
    {
        await D("stop", "-t", "3", ContainerName(botId));
        _lastUse.TryRemove(botId, out _);
        Changed?.Invoke();
    }

    public async Task ResetAsync(string botId)
    {
        await D("rm", "-f", ContainerName(botId));
        await D("volume", "rm", "-f", VolumeName(botId));
        _controllers.TryRemove(botId, out _);
        _lastUse.TryRemove(botId, out _);
        Changed?.Invoke();
    }

    private async Task StopIdleAsync()
    {
        try
        {
            var minutes = (await _settings.GetAsync()).Computer.IdleMinutes;
            if (minutes <= 0) return;
            foreach (var (bot, last) in _lastUse.ToArray())
                if (DateTimeOffset.Now - last > TimeSpan.FromMinutes(Math.Max(minutes, 10))) await StopAsync(bot);
        }
        catch { /* best effort */ }
    }

    private static string LocalTimeZone() =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : TimeZoneInfo.Local.Id;

    private static string Tail(string s) => s.Length > 400 ? "…" + s[^400..] : s.Trim();

    [GeneratedRegex("[^a-z0-9_.-]")] private static partial Regex Safe();

    public void Dispose() => _idle.Dispose();
}
