namespace Hearth.App;

/// <summary>First run (or "Switch server"): connect to the Hearth running on your computer.</summary>
public sealed class ConnectPage : ContentPage
{
    private readonly Entry _url;
    private readonly Label _status;
    private readonly Button _connect;

    public ConnectPage(string? url = null, bool autoConnect = false)
    {
        var dark = Application.Current?.RequestedTheme != AppTheme.Light;
        var text = dark ? Color.FromArgb("#ECECF1") : Color.FromArgb("#17181C");
        var muted = dark ? Color.FromArgb("#9396A0") : Color.FromArgb("#646872");
        var accent = Color.FromArgb("#6366F1");
        BackgroundColor = dark ? Color.FromArgb("#0E0F11") : Colors.White;

        _url = new Entry
        {
            Placeholder = "http://192.168.1.20:5290 or https://my-mac.tailnet.ts.net",
            Keyboard = Keyboard.Url, Text = url ?? ServerConnection.Url ?? "", TextColor = text,
            PlaceholderColor = muted, ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            AutomationId = "ServerUrl",
        };
        _url.Completed += async (_, _) => await ConnectAsync();
        _status = new Label { TextColor = Color.FromArgb("#F2716B"), FontSize = 14, IsVisible = false };
        _connect = new Button { Text = "Connect", BackgroundColor = accent, TextColor = Colors.White, CornerRadius = 10, HeightRequest = 48, AutomationId = "Connect" };
        _connect.Clicked += async (_, _) => await ConnectAsync();
        var local = new Button { Text = "Use Hearth on this phone instead", BackgroundColor = Colors.Transparent, TextColor = muted, FontSize = 14 };
        local.Clicked += (_, _) => { ServerConnection.UseLocal(); ServerConnection.ShowPage(new MainPage()); };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 72, 24, 24), Spacing = 14, MaximumWidthRequest = 520,
                Children =
                {
                    new Border
                    {
                        WidthRequest = 56, HeightRequest = 56, BackgroundColor = accent, StrokeThickness = 0, HorizontalOptions = LayoutOptions.Start,
                        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
                        Content = new Label { Text = "L", FontSize = 28, FontAttributes = FontAttributes.Bold, TextColor = Colors.White,
                            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
                    },
                    new Label { Text = "Connect to your Hearth", FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = text },
                    new Label { Text = "Use the bots, chats and memory on your computer from this phone. On your computer, open Hearth → Settings → Connections → Phone and scan the \"In the Hearth app\" code with your camera, or type the address here.",
                        FontSize = 15, TextColor = muted },
                    new Border
                    {
                        Stroke = muted.WithAlpha(0.4f), StrokeThickness = 1, Padding = new Thickness(10, 2),
                        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 }, Content = _url,
                    },
                    _status,
                    _connect,
                    local,
                },
            },
        };
        if (autoConnect) Loaded += async (_, _) => await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var url = ServerConnection.Normalize(_url.Text);
        if (url is null) { Show("Enter your computer's Hearth address, e.g. http://192.168.1.20:5290."); return; }
        _connect.IsEnabled = false;
        _connect.Text = "Connecting…";
        _status.IsVisible = false;
        var problem = await ServerConnection.CheckAsync(url);
        _connect.IsEnabled = true;
        _connect.Text = "Connect";
        if (problem is not null) { Show(problem); return; }
        ServerConnection.UseRemote(url);
        ServerConnection.ShowPage(new RemotePage(url));
    }

    private void Show(string message)
    {
        _status.Text = message;
        _status.IsVisible = true;
    }
}
