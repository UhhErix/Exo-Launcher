using ExoLauncher.Helpers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// The achievement banners as an OBS Browser Source, for streamers: http://127.0.0.1:9364/achievements serves the same
/// card as the in-game overlay (Assets/Overlay/banner.html, which sees the path and runs in its "source" mode: no sound
/// unless ?sound=1, unlocks from the event stream), with its font and sounds (<see cref="AssetOf"/>), and
/// /achievements/events streams each unlock as a server-sent event.
/// Always on while Exo runs, bound to the loopback address only, so nothing off this PC reaches it; a request naming any
/// other host is refused (a web page can't rebind a name of its own onto it). The port is fixed so the URL streamers
/// paste stays the same; Exo's other local ports are 9333 (Launcher's debug) and 9339 (E2E).
/// </summary>
internal static class AchievementSource
{
    internal const int Port = 9364;
    internal const string Url = "http://127.0.0.1:9364/achievements";
    private const int MaxClients = 16;

    internal enum Route { Page, Asset, Events, NotFound, Refused }

    /// <summary>The page's files by the address it asks for them: the file in its folder and its type; null for anything else.</summary>
    internal static (string File, string Type)? AssetOf(string path) => path switch
    {
        "/Exoline-Variable.woff2" => ("Exoline-Variable.woff2", "font/woff2"),
        "/sounds/common.wav" or "/sounds/rare.wav" or "/sounds/epic.wav" or "/sounds/legendary.wav" or "/sounds/completion.wav" => (Path.Combine("sounds", path[8..]), "audio/wav"),
        _ => null,
    };

    private static readonly Lock Gate = new();
    private static readonly List<Client> Clients = [];
    private static Timer? _ping;

    private sealed class Client(TcpClient tcp, NetworkStream stream) : IDisposable
    {
        internal readonly Lock Writing = new();
        internal bool Send(byte[] bytes)
        {
            try { lock (Writing) stream.Write(bytes); return true; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return false; }
        }
        public void Dispose() { stream.Dispose(); tcp.Dispose(); }
    }

    internal static void Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            AppLog.Warn($"Achievements: the OBS browser source isn't available, port {Port} is taken ({ex.SocketErrorCode}).");
            return;
        }
        AchievementWatch.Unlocked += unlock =>
        {
            var settings = App.Services.Settings.Current;
            var json = AchievementWatch.Card(unlock, settings.AchievementVolume, settings.ReduceMotion);
            _ = Task.Run(() => Broadcast(Encoding.UTF8.GetBytes($"data: {json}\n\n")));
        };
        // A comment line every 20 s: OBS keeps the stream open, and a source that went away is noticed and dropped.
        _ping = new Timer(_ => Broadcast(": ping\n\n"u8.ToArray()), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        _ = Task.Run(() => AcceptAsync(listener));
        AppLog.Info($"Achievements: OBS browser source at {Url}.");
    }

    private static void Broadcast(byte[] bytes)
    {
        Client[] all;
        lock (Gate) all = [.. Clients];
        foreach (var client in all)
        {
            if (client.Send(bytes)) continue;
            lock (Gate) Clients.Remove(client);
            client.Dispose();
        }
    }

    private static async Task AcceptAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                AppLog.Warn("Achievements: the OBS browser source stopped: " + ex.Message);
                return;
            }
            _ = Task.Run(() => ServeAsync(tcp));
        }
    }

    private static async Task ServeAsync(TcpClient tcp)
    {
        var keep = false;
        try
        {
            var stream = tcp.GetStream();
            stream.WriteTimeout = 5000;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
            switch (head is null ? Route.NotFound : RouteOf(head))
            {
                case Route.Page:
                    await SendFileAsync(stream, "banner.html", "text/html; charset=utf-8").ConfigureAwait(false);
                    break;
                case Route.Asset:
                    if (AssetOf(PathOf(head!)) is var (file, type)) await SendFileAsync(stream, file, type).ConfigureAwait(false);
                    break;
                case Route.Events:
                    lock (Gate)
                    {
                        if (Clients.Count >= MaxClients) break;
                        var client = new Client(tcp, stream);
                        // retry: how soon OBS reconnects when Exo restarts.
                        if (!client.Send("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n\r\nretry: 3000\n\n"u8.ToArray())) break;
                        Clients.Add(client);
                        keep = true;
                    }
                    break;
                case Route.Refused:
                    await stream.WriteAsync("HTTP/1.1 421 Misdirected Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
                    break;
                default:
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException) { }
        finally { if (!keep) tcp.Dispose(); }
    }

    private static async Task SendFileAsync(NetworkStream stream, string file, string type)
    {
        var body = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Overlay", file)).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    private static string PathOf(string head) => head.Split("\r\n")[0].Split(' ')[1].Split('?', 2)[0];

    /// <summary>The request line and headers, up to the blank line; null when there is none within 8 KB.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) return null;
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    /// <summary>
    /// Where a request goes: a GET of /achievements (any query) is the page, of /achievements/events the stream, of one of the page's files an asset; a Host
    /// other than this PC's loopback on <see cref="Port"/> is refused; anything else isn't here.
    /// </summary>
    internal static Route RouteOf(string head)
    {
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return Route.NotFound;
        var host = lines.Skip(1).Select(line => line.Split(':', 2)).Where(pair => pair.Length == 2 && pair[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim()).ToList();
        if (host.Count != 1 || !(host[0].Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) || host[0].Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase)))
            return Route.Refused;
        if (request[0] != "GET") return Route.NotFound;
        var path = request[1].Split('?', 2)[0];
        return path switch
        {
            "/achievements" or "/achievements/" => Route.Page,
            "/achievements/events" => Route.Events,
            _ => AssetOf(path) is null ? Route.NotFound : Route.Asset,
        };
    }
}
