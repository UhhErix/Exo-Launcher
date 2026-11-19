using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The Exo account (plan §2.17): signing in at Exo's account service with Google, Apple or a 6-digit email code, and the
/// recovery code the account keeps so a new PC gets everything back. Google and Apple sign in in the real browser
/// (Google refuses embedded web views); the service sends it back to a one-time listener on 127.0.0.1 with a grant that
/// only this app can swap for a session, since only it holds the PKCE verifier. The session is this PC's, shared by every
/// Exo app on it (<see cref="IdentityFolder.SaveAccount"/>). Each call is the service's API as cloud/account/README.md
/// lists it; <c>service</c> ends with a slash.
/// </summary>
public sealed class AccountClient(Uri service, HttpClient http)
{
    public sealed record Session(string Token, string AccountId, string Provider, string? Email);

    /// <param name="Created">The account is new: this was its first sign-in.</param>
    public sealed record SignedIn(Session Session, bool Created);

    public sealed record Account(string Id, long Created, IReadOnlyList<string> Providers, bool Recovery);

    /// <summary>The service refused (its error code, such as "wrong-code"; cloud/account/README.md lists them), the
    /// browser came back without a grant, or the session ended elsewhere ("signed-out").</summary>
    public sealed class AccountException(string code, string message) : Exception(message)
    {
        public string Code => code;
    }

    /// <summary>How long the browser has to come back from Google or Apple.</summary>
    public static readonly TimeSpan BrowserWait = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(10);

    /// <summary>The ways in the service has turned on: some of "google", "apple" and "email".</summary>
    public async Task<IReadOnlyList<string>> ProvidersAsync(CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, "v1/providers", null, null, ct).ConfigureAwait(false))?["providers"] is JsonArray providers
            ? [.. providers.Select(p => p?.GetValue<string>()).OfType<string>()]
            : [];

    /// <summary>
    /// Google or Apple: opens the browser at the service (<paramref name="openBrowser"/>, the app's way to open a link
    /// outside itself) and waits for it to come back to this PC, then swaps the grant for a session for
    /// <paramref name="deviceId"/>.
    /// </summary>
    public async Task<SignedIn> SignInAsync(string provider, string deviceId, Func<Uri, Task> openBrowser, CancellationToken ct)
    {
        var verifier = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await openBrowser(new Uri(service, $"v1/start?provider={Uri.EscapeDataString(provider)}&port={port}&challenge={challenge}")).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(BrowserWait);
            var (grant, error) = await ReturnAsync(listener, wait.Token).ConfigureAwait(false);
            if (grant is null) throw new AccountException(error ?? "not-finished", Says(error ?? "not-finished"));
            return await SessionAsync(new JsonObject { ["grant"] = grant, ["verifier"] = verifier, ["device"] = deviceId }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AccountException("not-finished", Says("not-finished"));
        }
        finally { listener.Stop(); }
    }

    /// <summary>Sends a 6-digit code to <paramref name="email"/>; answers the ticket <see cref="EmailFinishAsync"/> takes.</summary>
    public async Task<string> EmailStartAsync(string email, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, "v1/email/start", new JsonObject { ["email"] = email.Trim() }, null, ct).ConfigureAwait(false))?["ticket"]?.GetValue<string>()
            ?? throw new AccountException("bad-answer", Says("bad-answer"));

    public Task<SignedIn> EmailFinishAsync(string ticket, string code, string deviceId, CancellationToken ct) =>
        SessionAsync(new JsonObject { ["ticket"] = ticket, ["code"] = code.Trim(), ["device"] = deviceId }, ct);

    /// <summary>The dev servers' own way in (<see cref="DevServers"/>): their key and a subject sign in as that dev
    /// account. A real account service has none and answers "not-found".</summary>
    public Task<SignedIn> DevSignInAsync(string key, string subject, string deviceId, CancellationToken ct) =>
        SessionAsync(new JsonObject { ["dev"] = key, ["subject"] = subject, ["device"] = deviceId }, ct);

    /// <summary>The account, or a "signed-out" <see cref="AccountException"/> when the session has ended.</summary>
    public async Task<Account> AccountAsync(Session session, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "v1/account", null, session, ct).ConfigureAwait(false);
        if (json?["account"] is not JsonObject account || account["id"]?.GetValue<string>() is not { } id) throw new AccountException("bad-answer", Says("bad-answer"));
        var providers = json["providers"] is JsonArray list ? list.Select(p => p?.GetValue<string>()).OfType<string>().ToList() : [];
        return new Account(id, account["created"]?.GetValue<long>() ?? 0, providers, json["recovery"]?.GetValue<bool>() == true);
    }

    /// <summary>Gives the account this identity's recovery code (<see cref="RecoveryCodeText"/>), in place of any it had.</summary>
    public Task PutRecoveryAsync(Session session, string code, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "v1/recovery", new JsonObject { ["code"] = code }, session, ct);

    /// <summary>The recovery code the account keeps, or null when it keeps none (a new account).</summary>
    public async Task<string?> GetRecoveryAsync(Session session, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, "v1/recovery", null, session, ct, notFound: true).ConfigureAwait(false))?["code"]?.GetValue<string>();

    public Task DeleteRecoveryAsync(Session session, CancellationToken ct) => SendAsync(HttpMethod.Delete, "v1/recovery", null, session, ct);

    /// <param name="Created">Unix seconds, as <paramref name="LastUsed"/>.</param>
    /// <param name="Current">This PC's own session.</param>
    public sealed record SessionView(string Device, long Created, long LastUsed, bool Current);

    /// <summary>The account's live sessions, oldest first: how a PC sees that the account signed in somewhere new.</summary>
    public async Task<IReadOnlyList<SessionView>> SessionsAsync(Session session, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, "v1/sessions", null, session, ct).ConfigureAwait(false))?["sessions"] is JsonArray sessions
            ? [.. sessions.OfType<JsonObject>().Select(s => new SessionView(s["device"]?.GetValue<string>() ?? "", s["created"]?.GetValue<long>() ?? 0,
                s["lastUsed"]?.GetValue<long>() ?? 0, s["current"]?.GetValue<bool>() == true))]
            : [];

    /// <summary>Claims the head <paramref name="prev"/> for the account entry's add <paramref name="hash"/> (plan §2.17);
    /// false when another add claimed it first, so this PC takes that one and signs again on top.</summary>
    public async Task<bool> ClaimEntryAsync(Session session, string prev, string hash, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, "v1/entry", new JsonObject { ["prev"] = prev, ["hash"] = hash }, session, ct, taken: true).ConfigureAwait(false) is not { } json ||
        json["error"] is null;

    /// <summary>Ends this PC's session.</summary>
    public Task SignOutAsync(Session session, CancellationToken ct) => SendAsync(HttpMethod.Post, "v1/signout", null, session, ct);

    /// <summary>Ends every session of another PC on this account: the one this PC just removed from the profile.</summary>
    public Task EndDeviceAsync(Session session, string deviceId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, "v1/sessions/" + Uri.EscapeDataString(deviceId), null, session, ct);

    public Task DeleteAccountAsync(Session session, CancellationToken ct) => SendAsync(HttpMethod.Delete, "v1/account", null, session, ct);

    /// <summary>
    /// What the account keeps for an identity: the canonical JSON {"code":"…","exoId":"…"}, so a PC that already has an
    /// identity can tell the account's code is another one's before using it.
    /// </summary>
    public static string RecoveryCodeText(string exoId, string code) =>
        Encoding.UTF8.GetString(CanonicalJson.Serialize(new JsonObject { ["code"] = code, ["exoId"] = exoId }));

    public static (string ExoId, string Code)? ReadRecoveryCodeText(string text) =>
        CanonicalJson.ParseObject(Encoding.UTF8.GetBytes(text)) is { Count: 2 } json && Device.Text(json, "exoId") is { } exoId && Device.Text(json, "code") is { } code
            ? (exoId, code)
            : null;

    /// <summary>The session as <c>account.sealed</c> holds it: canonical JSON, the email left out when there is none.</summary>
    public static JsonObject SessionJson(Session session)
    {
        var json = new JsonObject { ["v"] = 1, ["token"] = session.Token, ["account"] = session.AccountId, ["provider"] = session.Provider };
        if (session.Email is { } email) json["email"] = email;
        return json;
    }

    public static Session? ReadSession(JsonObject json) =>
        Device.Int(json, "v") == 1 && Device.Text(json, "token") is { } token && Device.Text(json, "account") is { } account && Device.Text(json, "provider") is { } provider
            ? new Session(token, account, provider, Device.Text(json, "email"))
            : null;

    private async Task<SignedIn> SessionAsync(JsonObject body, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "v1/session", body, null, ct).ConfigureAwait(false);
        if (json?["token"]?.GetValue<string>() is not { } token || json["account"]?["id"]?.GetValue<string>() is not { } id)
            throw new AccountException("bad-answer", Says("bad-answer"));
        var provider = json["provider"]?.GetValue<string>() ?? "email";
        return new SignedIn(new Session(token, id, provider, json["email"]?.GetValue<string>()), json["created"]?.GetValue<bool>() == true);
    }

    private async Task<JsonObject?> SendAsync(HttpMethod method, string path, JsonObject? body, Session? session, CancellationToken ct, bool notFound = false, bool taken = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(service, path));
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new AccountException("offline", Says("offline")); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonObject? json = null;
            try { json = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null; }
            catch (System.Text.Json.JsonException) { }
            if (response.IsSuccessStatusCode) return json;
            if (notFound && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (taken && response.StatusCode == HttpStatusCode.Conflict && json is not null) return json;
            // A 401 is "signed-out" (the session ended) or "wrong-code" (an email code), so the service's own code decides.
            var code = json?["error"]?.GetValue<string>() ?? (response.StatusCode == HttpStatusCode.Unauthorized ? "signed-out" : "failed");
            throw new AccountException(code, Says(code));
        }
    }

    /// <summary>The browser's way back: the first request for /exo-account answers the grant (or the service's error);
    /// anything else the browser asks this listener for (a favicon, a connection it opened ahead) gets a 404.</summary>
    private static async Task<(string? Grant, string? Error)> ReturnAsync(TcpListener listener, CancellationToken ct)
    {
        var done = new TaskCompletionSource<(string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = ct.Register(() => done.TrySetCanceled(ct));
        _ = Task.Run(async () =>
        {
            while (!done.Task.IsCompleted)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = AnswerAsync(client, done, ct);
            }
        }, CancellationToken.None);
        return await done.Task.ConfigureAwait(false);
    }

    private static async Task AnswerAsync(TcpClient client, TaskCompletionSource<(string?, string?)> done, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(RequestWait);
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, wait.Token).ConfigureAwait(false);
                var target = head?.Split(' ') is [_, var t, ..] ? t : null;
                if (target is null || !target.StartsWith("/exo-account?", StringComparison.Ordinal))
                {
                    await WriteAsync(stream, "404 Not Found", "", wait.Token).ConfigureAwait(false);
                    return;
                }
                var query = Query(target["/exo-account?".Length..]);
                var grant = query.GetValueOrDefault("grant") is { Length: > 0 and <= 128 } g && g.All(IsBase64Url) ? g : null;
                var error = query.GetValueOrDefault("error") is { Length: > 0 and <= 40 } e && e.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') ? e : null;
                await WriteAsync(stream, "200 OK", Page(grant is not null), wait.Token).ConfigureAwait(false);
                done.TrySetResult((grant, grant is null ? error ?? "not-finished" : null));
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
            var end = buffer.AsSpan(0, read).IndexOf("\r\n"u8);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end);
        }
        return null;
    }

    private static async Task WriteAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n" +
                   "Referrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>The tab the browser is left on, in Exo's look: black, white text, nothing else.</summary>
    private static string Page(bool signedIn) =>
        "<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Exo</title>" +
        "<body style=\"margin:0;min-height:100vh;display:grid;place-items:center;background:#000;color:#fff;" +
        "font:500 17px/1.4 Inter,'Segoe UI Variable','Segoe UI',system-ui,sans-serif;text-align:center;padding:0 16px\">" +
        (signedIn ? "<p>You're signed in to Exo. You can close this tab.</p>" : "<p>Signing in didn't finish. Go back to Exo and try again.</p>");

    private static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            if (at > 0) values.TryAdd(Uri.UnescapeDataString(pair[..at]), Uri.UnescapeDataString(pair[(at + 1)..]));
        }
        return values;
    }

    private static bool IsBase64Url(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static string Says(string code) => code switch
    {
        "wrong-code" => "That code isn't right.",
        "code-expired" => "That code has expired. Send a new one.",
        "too-many-codes" => "Too many codes went to that address. Try again later.",
        "email-failed" => "The code couldn't be sent. Try again.",
        "slow-down" => "Too many tries. Wait a minute, then try again.",
        "bad-email" => "That isn't an email address.",
        "provider-off" => "That way of signing in isn't on yet.",
        "email-unverified" => "That account's email isn't verified, so Exo can't use it.",
        "cancelled" => "Signing in was cancelled.",
        "not-finished" or "grant-expired" => "Signing in didn't finish. Try again.",
        "signed-out" => "You were signed out. Sign in again.",
        "offline" => "Exo couldn't reach its account service. Check the connection and try again.",
        _ => "Exo couldn't sign you in. Try again.",
    };
}
