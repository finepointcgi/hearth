namespace Locus.App;

/// <summary>
/// Where this app's Locus lives: your computer's Locus (remote, the default) or a self-contained copy on this device.
/// </summary>
public static class ServerConnection
{
    private const string ModeKey = "locus.mode", UrlKey = "locus.serverUrl";

    public static string? Mode => Preferences.Default.Get<string?>(ModeKey, null);
    public static string? Url => Preferences.Default.Get<string?>(UrlKey, null);

    public static void UseRemote(string url)
    {
        Preferences.Default.Set(ModeKey, "remote");
        Preferences.Default.Set(UrlKey, url);
    }

    public static void UseLocal() => Preferences.Default.Set(ModeKey, "local");

    public static void Forget() => Preferences.Default.Remove(ModeKey);

    /// <summary>Normalises what the user typed: adds http:// and drops a trailing slash.</summary>
    public static string? Normalize(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.Contains("://")) s = "http://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.GetLeftPart(UriPartial.Path).TrimEnd('/') : null;
    }

    /// <summary>Checks that a Locus answers at this address (its /health endpoint).</summary>
    public static async Task<string?> CheckAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var res = await http.GetAsync(url + "/health");
            if (!res.IsSuccessStatusCode) return $"That address answered, but not like Locus ({(int)res.StatusCode}).";
            var body = await res.Content.ReadAsStringAsync();
            return body.Contains("\"status\"") ? null : "That address answered, but it doesn't look like Locus.";
        }
        catch (TaskCanceledException) { return "No answer. Is Locus running, and is this phone on the same network (or Tailscale)?"; }
        catch (Exception ex) { return "Couldn't connect: " + ex.Message; }
    }

    /// <summary>Handles locus://connect?url=… (from the QR code on your computer's Phone card).</summary>
    public static bool TryHandleLink(string? link)
    {
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var u) || u.Scheme != "locus") return false;
        var query = System.Web.HttpUtility.ParseQueryString(u.Query);
        if (Normalize(query["url"]) is not { } url) return false;
        MainThread.BeginInvokeOnMainThread(() => ShowPage(new ConnectPage(url, autoConnect: true)));
        return true;
    }

    public static Page StartPage() => Mode switch
    {
        "remote" when Url is { } url => new RemotePage(url),
        "local" => new MainPage(),
        _ => new ConnectPage(),
    };

    public static void ShowPage(Page page)
    {
        if (Application.Current?.Windows.FirstOrDefault() is { } window) window.Page = page;
    }
}
