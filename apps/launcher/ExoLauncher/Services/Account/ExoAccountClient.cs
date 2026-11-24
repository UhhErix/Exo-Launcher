using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.Account;

/// <summary>One answer from the account service. Status 0 is no answer (network failure or timeout).</summary>
internal sealed record AccountReply(int Status, JsonNode? Body, string? Error, int? RetryAfterSeconds)
{
    public bool Ok => Error is null && Status is >= 200 and < 300;
    public bool Unreachable => Status == 0;
}

/// <summary>The sign-in or creation answer: this PC's token and the account, the recovery code on creation or reset, and the device token.</summary>
internal sealed record AccountSignIn(string Token, string AccountId, string? RecoveryCode, string? DeviceToken = null);

/// <summary>
/// The HTTP calls of the account service (cloud/account/CLIENT.md section 3). Returns answers, never a token to React:
/// the caller keeps the token in <see cref="AccountSessionStore"/>. Every request goes through <see cref="SendAsync"/>.
/// </summary>
internal sealed class ExoAccountClient : IDisposable
{
    internal static readonly Uri Service = new("https://accounts.getexo.app/");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    /// <summary>How long busy answers are waited out in all before the busy answer stands (<see cref="BusyWait"/>).</summary>
    internal static readonly TimeSpan BusyBudget = TimeSpan.FromSeconds(5);
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> wait;

    internal ExoAccountClient(HttpMessageHandler? handler = null, Uri? service = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        http = ExoHttp.Create(Timeout, handler ?? NewHandler());
        http.BaseAddress = service ?? Service;
        this.wait = wait ?? Task.Delay;
    }

    /// <summary>
    /// Whether to send a request again, and after how long. Only the service's own 503 busy (its Argon2 slots are full, or a
    /// PUT for the account is in flight; nothing was done, CLIENT.md 4.2) is sent again, after its Retry-After held to 1 to 2
    /// seconds. A password route (<paramref name="throttled"/>) is counted by the service's per-address limit (10 a minute)
    /// before it can answer busy, so each try costs one: it is sent again once at most. Any other route (sync) is sent again
    /// while the waits add up to no more than <see cref="BusyBudget"/>.
    /// Rows: not busy, null; password route, first busy, its wait; password route, busy again, null; other route, busy within
    /// the budget, its wait; other route, busy past it, null.
    /// </summary>
    internal static TimeSpan? BusyWait(AccountReply reply, TimeSpan waited, int retries, bool throttled)
    {
        if (reply.Status != 503 || reply.Error != "busy") return null;
        var next = TimeSpan.FromSeconds(Math.Clamp(reply.RetryAfterSeconds ?? 1, 1, 2));
        if (throttled) return retries == 0 ? next : null;
        return waited + next <= BusyBudget ? next : null;
    }

    /// <summary>The handler the account client uses: a redirect is never followed, so the token never goes to another host.</summary>
    internal static SocketsHttpHandler NewHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public void Dispose() => http.Dispose();

    /// <summary>
    /// Sends one request, and again while the service answers busy (<see cref="BusyWait"/>), so a moment of load is waited
    /// out here rather than shown as an outage. Network failure and timeouts come back as status 0; a non-JSON error body as
    /// error "bad-response".
    /// </summary>
    internal async Task<AccountReply> SendAsync(HttpMethod method, string path, string? token, JsonObject? body, CancellationToken ct = default, bool throttled = false)
    {
        var waited = TimeSpan.Zero;
        for (var retries = 0; ; retries++)
        {
            var reply = await SendOnceAsync(method, path, token, body, ct);
            if (BusyWait(reply, waited, retries, throttled) is not { } next) return reply;
            await wait(next, ct);
            waited += next;
        }
    }

    private async Task<AccountReply> SendOnceAsync(HttpMethod method, string path, string? token, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try { if (text.Length > 0) json = JsonNode.Parse(text); }
            catch (JsonException) { /* no JSON: the error below says so */ }
            string? error = json is JsonObject obj && obj["error"] is JsonValue value && value.TryGetValue(out string? code) ? code : null;
            if (error is null && !response.IsSuccessStatusCode) error = "bad-response";
            var retry = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : (int?)null;
            return new AccountReply((int)response.StatusCode, json, error, retry);
        }
        catch (HttpRequestException)
        {
            return new AccountReply(0, null, "network", null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AccountReply(0, null, "network", null);
        }
    }

    internal Task<AccountReply> CreateAccountAsync(string email, string password, string device) =>
        SendAsync(HttpMethod.Post, "v1/account", null, Body(("email", email), ("password", password), ("device", device)), throttled: true);

    internal Task<AccountReply> SignInAsync(string email, string password, string device, string? deviceToken = null) =>
        SendAsync(HttpMethod.Post, "v1/session", null, WithDeviceToken(Body(("email", email), ("password", password), ("device", device)), deviceToken), throttled: true);

    internal Task<AccountReply> GetAccountAsync(string token) => SendAsync(HttpMethod.Get, "v1/account", token, null);

    /// <summary>Whether the service answers at all: GET /v1/account with no token, which it answers 401 (nothing is sent about this PC).</summary>
    internal async Task<bool> ReachableAsync(CancellationToken ct = default)
    {
        var reply = await SendOnceAsync(HttpMethod.Get, "v1/account", null, null, ct);
        return !reply.Unreachable && reply.Status < 500;
    }

    /// <summary>One PUT of a batch (CLIENT.md 7.4). The body is built by <see cref="AccountSyncEngine.PutBody"/>.</summary>
    internal Task<AccountReply> PutItemsAsync(string token, IEnumerable<SyncItem> batch) =>
        SendAsync(HttpMethod.Put, "v1/sync", token, AccountSyncEngine.PutBody(batch));

    /// <summary>One page of items after a version (CLIENT.md 7.7): the caller keeps going while more is true.</summary>
    internal Task<AccountReply> PullAsync(string token, long after) =>
        SendAsync(HttpMethod.Get, "v1/sync?after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture), token, null);

    /// <summary>One page of the export (CLIENT.md 7.7). Tombstones come back with a null value.</summary>
    internal Task<AccountReply> ExportPageAsync(string token, long after) =>
        SendAsync(HttpMethod.Get, "v1/export?after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture), token, null);

    internal Task<AccountReply> SignOutAsync(string token) => SendAsync(HttpMethod.Post, "v1/signout", token, null);

    internal Task<AccountReply> ChangePasswordAsync(string token, string current, string next, string? deviceToken = null) =>
        SendAsync(HttpMethod.Put, "v1/password", token, WithDeviceToken(Body(("current", current), ("password", next)), deviceToken), throttled: true);

    internal Task<AccountReply> NewRecoveryCodeAsync(string token, string password, string? deviceToken = null) =>
        SendAsync(HttpMethod.Post, "v1/recovery", token, WithDeviceToken(Body(("password", password)), deviceToken), throttled: true);

    internal Task<AccountReply> ResetAsync(string email, string code, string password, string device, string? deviceToken = null) =>
        SendAsync(HttpMethod.Post, "v1/recovery/reset", null, WithDeviceToken(Body(("email", email), ("code", code), ("password", password), ("device", device)), deviceToken), throttled: true);

    internal Task<AccountReply> ListSessionsAsync(string token) => SendAsync(HttpMethod.Get, "v1/sessions", token, null);

    internal Task<AccountReply> RemoveSessionAsync(string token, string device) =>
        SendAsync(HttpMethod.Delete, "v1/sessions/" + device, token, null);

    internal Task<AccountReply> DeleteAccountAsync(string token, string password, string? deviceToken = null) =>
        SendAsync(HttpMethod.Delete, "v1/account", token, WithDeviceToken(Body(("password", password)), deviceToken), throttled: true);

    internal Task<AccountReply> ListPasskeysAsync(string token) =>
        SendAsync(HttpMethod.Get, "v1/passkeys", token, null);

    internal Task<AccountReply> PasskeyRegisterStartAsync(string token, string? password = null, string? deviceToken = null)
    {
        var body = string.IsNullOrEmpty(password) ? new JsonObject() : Body(("password", password));
        return SendAsync(HttpMethod.Post, "v1/passkeys/register/start", token, WithDeviceToken(body, deviceToken), throttled: true);
    }

    internal Task<AccountReply> PasskeyRegisterFinishAsync(string token, string challenge, string clientDataJson, string attestationObject, string label) =>
        SendAsync(HttpMethod.Post, "v1/passkeys/register/finish", token,
            Body(("challenge", challenge), ("clientDataJSON", clientDataJson), ("attestationObject", attestationObject), ("label", label)), throttled: true);

    internal Task<AccountReply> DeletePasskeyAsync(string token, string credentialId) =>
        SendAsync(HttpMethod.Delete, "v1/passkeys/" + Uri.EscapeDataString(credentialId), token, null);

    internal Task<AccountReply> PasskeySignInStartAsync() =>
        SendAsync(HttpMethod.Post, "v1/passkeys/signin/start", null, new JsonObject(), throttled: true);

    internal Task<AccountReply> PasskeySignInFinishAsync(
        string challenge, string device, string credentialId, string clientDataJson, string authenticatorData, string signature) =>
        SendAsync(HttpMethod.Post, "v1/passkeys/signin/finish", null,
            Body(("challenge", challenge), ("device", device), ("credentialId", credentialId),
                ("clientDataJSON", clientDataJson), ("authenticatorData", authenticatorData), ("signature", signature)),
            throttled: true);

    /// <summary>The account id, this PC's token and its device token from an answer that carries them (201 create, 200 sign-in or reset).</summary>
    internal static AccountSignIn? ReadSignIn(AccountReply reply)
    {
        if (!reply.Ok || reply.Body is not JsonObject obj) return null;
        var token = obj["token"]?.GetValue<string>();
        var id = obj["account"]?["id"]?.GetValue<string>();
        if (token is null || id is null) return null;
        return new AccountSignIn(token, id, obj["recoveryCode"]?.GetValue<string>(), obj["deviceToken"]?.GetValue<string>());
    }

    private static JsonObject Body(params (string Key, string Value)[] fields)
    {
        var body = new JsonObject();
        foreach (var (key, value) in fields) body[key] = value;
        return body;
    }

    /// <summary>The stored device token, sent in the body when this PC has one (CLIENT.md 3). Never sent when there is none.</summary>
    private static JsonObject WithDeviceToken(JsonObject body, string? deviceToken)
    {
        if (!string.IsNullOrEmpty(deviceToken)) body["deviceToken"] = deviceToken;
        return body;
    }
}
