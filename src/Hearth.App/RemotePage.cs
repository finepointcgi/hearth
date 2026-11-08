namespace Hearth.App;

/// <summary>Shows the Hearth running on your computer (its web interface), full-screen.</summary>
public sealed class RemotePage : ContentPage
{
    private readonly WebView _web;
    private readonly string _url;

    public RemotePage(string url)
    {
        _url = url;
        BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Light ? Colors.White : Color.FromArgb("#0E0F11");
        _web = new WebView { Source = url, AutomationId = "Remote" };
        // Lets the web interface know it's inside the app (shows "Switch server" in Settings).
        _web.UserAgent = (DeviceInfo.Platform == DevicePlatform.Android
            ? "Mozilla/5.0 (Linux; Android) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Mobile Safari/537.36"
            : "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148") + " HearthApp/1";
        _web.Navigating += OnNavigating;
        _web.Navigated += OnNavigated;
        Content = _web;
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith("hearth-app://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            if (e.Url.Contains("switch-server")) ServerConnection.ShowPage(new ConnectPage(_url));
            return;
        }
        // Links to other sites open in the browser, not inside the app.
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var target) && Uri.TryCreate(_url, UriKind.Absolute, out var home)
            && target.Scheme is "http" or "https" && target.Authority != home.Authority)
        {
            e.Cancel = true;
            _ = Launcher.Default.OpenAsync(target);
        }
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result is WebNavigationResult.Success) return;
        Content = new VerticalStackLayout
        {
            Padding = new Thickness(24, 96, 24, 24), Spacing = 14,
            Children =
            {
                new Label { Text = "Can't reach your Hearth", FontSize = 24, FontAttributes = FontAttributes.Bold,
                    TextColor = Application.Current?.RequestedTheme == AppTheme.Light ? Colors.Black : Colors.White },
                new Label { Text = $"{_url} isn't answering. Check that your computer is on and Hearth is running, and that this phone is on the same network (or Tailscale).",
                    FontSize = 15, TextColor = Colors.Gray },
                Button("Try again", () => { Content = _web; _web.Reload(); }),
                Button("Change server", () => ServerConnection.ShowPage(new ConnectPage(_url))),
            },
        };
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Text = text, CornerRadius = 10, HeightRequest = 46 };
        b.Clicked += (_, _) => onClick();
        return b;
    }
}
