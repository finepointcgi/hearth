using System.Net;
using System.Net.Sockets;

namespace Hearth.Core.Services;

/// <summary>
/// A small HTTP listener on 127.0.0.1 (the Google sign-in callback; fake servers in tests). Picking a free port and
/// then binding it can lose a race with anything else starting at the same moment, so it tries another port when
/// the one it picked is taken. Shutting down goes through <see cref="Close"/>: on macOS and Linux, .NET's
/// HttpListener.Close() after Stop() binds the port again and can throw "Address already in use".
/// </summary>
public static class LoopbackHttp
{
    /// <summary>Starts a listener on a free port. Returns it with its base address (no trailing slash).</summary>
    public static (HttpListener listener, string url) Listen()
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var url = $"http://127.0.0.1:{FreePort()}";
            var listener = new HttpListener();
            listener.Prefixes.Add(url + "/");
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException ex)
            {
                last = ex;
                Close(listener);
            }
        }
        throw new InvalidOperationException("Couldn't find a free local port: " + last?.Message, last);
    }

    /// <summary>Stops and releases a listener without ever throwing.</summary>
    public static void Close(HttpListener? listener)
    {
        if (listener is null) return;
        try { listener.Close(); }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
