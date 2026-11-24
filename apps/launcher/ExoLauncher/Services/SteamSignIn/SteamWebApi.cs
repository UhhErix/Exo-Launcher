using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.SteamSignIn;

/// <summary>A Steam answer that refused the request, with its <c>x-eresult</c> code. Carries no token and no URL.</summary>
internal sealed class SteamAuthException(int eresult, string message) : Exception(message)
{
    internal int EResult { get; } = eresult;

    internal static SteamAuthException FromCode(int eresult) => new(eresult, eresult switch
    {
        5 => "Steam did not accept that password.",
        9 => "This sign-in expired. Start it again.",
        65 or 88 => "Steam did not accept that Steam Guard code.",
        84 => "Steam is limiting sign-ins right now. Try again in a few minutes.",
        _ => $"Steam refused the request (code {eresult}).",
    });
}

/// <summary>
/// Steam's Web API for the keyless sign-in and the account's library: <c>IAuthenticationService</c> (protobuf, sent as
/// <c>input_protobuf_encoded</c>), <c>IPlayerService/GetOwnedGames</c> and <c>IFamilyGroupsService</c> (JSON), each
/// called with the account's own access token and no Web API key. Endpoints and parameters follow Irori's
/// crates/steam (MIT, github.com/countradooku/irori: api, auth.rs, library.rs). No method here logs, and no exception
/// carries a URL or a token.
/// </summary>
internal sealed class SteamWebApi(HttpClient http, string baseUrl = "https://api.steampowered.com")
{
    private const string AuthService = "IAuthenticationService";
    private const int MaxBody = 1 << 20;

    internal static HttpClient CreateDefaultClient()
    {
        var client = ExoHttp.Create(TimeSpan.FromSeconds(15), new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        return client;
    }

    internal Task<SteamAuthMessages.QrStart> BeginQrAsync(string deviceName, byte[] machineId, CancellationToken ct) =>
        Protobuf(HttpMethod.Post, "BeginAuthSessionViaQR", SteamAuthMessages.BeginQrRequest(deviceName, machineId), SteamAuthMessages.ReadQrStart, ct);

    internal Task<SteamAuthMessages.PollAnswer> PollAsync(ulong clientId, byte[] requestId, CancellationToken ct) =>
        Protobuf(HttpMethod.Post, "PollAuthSessionStatus", SteamAuthMessages.PollRequest(clientId, requestId), SteamAuthMessages.ReadPoll, ct);

    internal Task<SteamAuthMessages.RsaKey> RsaKeyAsync(string accountName, CancellationToken ct) =>
        Protobuf(HttpMethod.Get, "GetPasswordRSAPublicKey", SteamAuthMessages.RsaKeyRequest(accountName), SteamAuthMessages.ReadRsaKey, ct);

    internal Task<SteamAuthMessages.CredentialsStart> BeginCredentialsAsync(
        string deviceName, string accountName, string encryptedPasswordBase64, ulong timestamp, byte[] machineId, string guardData, CancellationToken ct) =>
        Protobuf(HttpMethod.Post, "BeginAuthSessionViaCredentials",
            SteamAuthMessages.BeginCredentialsRequest(deviceName, accountName, encryptedPasswordBase64, timestamp, machineId, guardData),
            SteamAuthMessages.ReadCredentialsStart, ct);

    internal Task<bool> SubmitGuardCodeAsync(ulong clientId, ulong steamId, string code, SteamGuardKind kind, CancellationToken ct) =>
        Protobuf(HttpMethod.Post, "UpdateAuthSessionWithSteamGuardCode",
            SteamAuthMessages.GuardCodeRequest(clientId, steamId, code.Trim().ToUpperInvariant(), kind), _ => true, ct);

    internal Task<string> GenerateAccessTokenAsync(string refreshToken, ulong steamId, CancellationToken ct) =>
        Protobuf(HttpMethod.Post, "GenerateAccessTokenForApp", SteamAuthMessages.AccessTokenRequest(refreshToken, steamId), message =>
        {
            var token = SteamAuthMessages.ReadAccessToken(message);
            return token.Length > 0 ? token : throw new SteamAuthException(0, "Steam did not renew the sign-in.");
        }, ct);

    /// <summary>Ends a saved sign-in on Steam's side. The access token travels in the form body, not the address.</summary>
    internal async Task RevokeAsync(string refreshToken, string accessToken, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["input_protobuf_encoded"] = Convert.ToBase64String(SteamAuthMessages.RevokeRequest(refreshToken)),
            ["access_token"] = accessToken,
        };
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, Url(AuthService, "RevokeToken")) { Content = new FormUrlEncodedContent(body) }, ct).ConfigureAwait(false);
        CheckEResult(response);
    }

    /// <summary>The account's own games with playtime. An answer without a <c>games</c> list is unavailable, not an empty library.</summary>
    internal async Task<SteamCommunityGames.OwnedGamesResult> OwnedGamesAsync(string accessToken, ulong steamId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("IPlayerService", "GetOwnedGames", ct,
            ("access_token", accessToken), ("steamid", steamId.ToString(CultureInfo.InvariantCulture)), ("include_appinfo", "1"),
            ("include_played_free_games", "1"), ("format", "json")).ConfigureAwait(false);
        return doc is null ? SteamCommunityGames.OwnedGamesResult.Unavailable : ParseOwned(doc.RootElement);
    }

    /// <summary>The Steam Family the account is in: its id, <c>0</c> when it is in none, null when Steam did not answer.</summary>
    internal async Task<ulong?> FamilyGroupAsync(string accessToken, ulong steamId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("IFamilyGroupsService", "GetFamilyGroupForUser", ct,
            ("access_token", accessToken), ("steamid", steamId.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return doc is null ? null : ParseFamilyGroup(doc.RootElement);
    }

    /// <summary>
    /// The games other family members share with the account (its own left out, and the ones an owner keeps private or
    /// Steam does not let a family share), each with its name and owners; null when Steam did not answer.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, SteamFamilyLibrary.SharedApp>?> FamilySharedAppsAsync(string accessToken, ulong familyGroupId, ulong steamId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("IFamilyGroupsService", "GetSharedLibraryApps", ct,
            ("access_token", accessToken), ("family_groupid", familyGroupId.ToString(CultureInfo.InvariantCulture)),
            ("steamid", steamId.ToString(CultureInfo.InvariantCulture)), ("include_own", "false"), ("include_excluded", "false"),
            ("include_non_games", "false"), ("language", "english")).ConfigureAwait(false);
        return doc is null ? null : ParseSharedApps(doc.RootElement);
    }

    /// <summary>The game's achievement schema, with the account's access token (no Web API key); null when Steam did not answer.</summary>
    internal Task<JsonDocument?> AchievementSchemaAsync(string accessToken, string appId, CancellationToken ct) =>
        GetJsonAsync("ISteamUserStats", "GetSchemaForGame", "v2", ct,
            [("access_token", accessToken), ("appid", appId), ("l", "english")]);

    /// <summary>Which of the game's achievements the account has, and when; null when Steam did not answer.</summary>
    internal Task<JsonDocument?> PlayerAchievementsAsync(string accessToken, ulong steamId, string appId, CancellationToken ct) =>
        GetJsonAsync("ISteamUserStats", "GetPlayerAchievements", ct,
            ("access_token", accessToken), ("steamid", steamId.ToString(CultureInfo.InvariantCulture)), ("appid", appId), ("l", "english"));

    // Parsing is pure and tested with fixture answers.

    internal static SteamCommunityGames.OwnedGamesResult ParseOwned(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return SteamCommunityGames.OwnedGamesResult.Unavailable;
        if (!response.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
            return SteamCommunityGames.OwnedGamesResult.Unavailable;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var minutes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var game in games.EnumerateArray())
        {
            var appId = AppId(game);
            if (appId is null) continue;
            ids.Add(appId);
            if (game.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                names[appId] = name.GetString()!.Trim();
            if (game.TryGetProperty("playtime_forever", out var played) && played.ValueKind == JsonValueKind.Number &&
                played.TryGetInt64(out var mins) && mins > 0)
                minutes[appId] = (int)Math.Min(mins, int.MaxValue);
        }
        return new SteamCommunityGames.OwnedGamesResult(true, ids, minutes, names);
    }

    /// <summary>Null when the answer has no response; <c>0</c> when the account is in no family.</summary>
    internal static ulong? ParseFamilyGroup(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return null;
        if (response.TryGetProperty("is_not_member_of_any_group", out var none) && none.ValueKind == JsonValueKind.True) return 0;
        if (!response.TryGetProperty("family_groupid", out var id)) return 0;
        return id.ValueKind switch
        {
            JsonValueKind.String => ulong.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0,
            JsonValueKind.Number => id.TryGetUInt64(out var n) ? n : 0,
            _ => 0UL,
        };
    }

    /// <summary>
    /// The shared library: a game once, with its name and its owners' account ids. An app Steam marks excluded
    /// (<c>exclude_reason</c> not 0: kept private by its owner, free, or not shareable) or that is not a game
    /// (<c>app_type</c> not 1) is left out, whatever the request asked. A response with nothing in it is an empty
    /// library (Steam leaves out an empty list); no response is null.
    /// </summary>
    internal static IReadOnlyDictionary<string, SteamFamilyLibrary.SharedApp>? ParseSharedApps(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return null;
        var apps = new Dictionary<string, SteamFamilyLibrary.SharedApp>(StringComparer.Ordinal);
        if (!response.TryGetProperty("apps", out var list)) return response.EnumerateObject().Any(p => p.Name != "owner_steamid") ? null : apps;
        if (list.ValueKind != JsonValueKind.Array) return null;
        foreach (var app in list.EnumerateArray())
        {
            var appId = AppId(app);
            if (appId is null) continue;
            if (app.TryGetProperty("exclude_reason", out var excluded) && excluded.ValueKind == JsonValueKind.Number &&
                (!excluded.TryGetInt32(out var reason) || reason != 0))
                continue;
            if (app.TryGetProperty("app_type", out var type) && type.ValueKind == JsonValueKind.Number &&
                (!type.TryGetInt32(out var kind) || kind != 1))
                continue;
            var name = app.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!.Trim()
                : null;
            var owners = new List<string>();
            if (app.TryGetProperty("owner_steamids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var id in ids.EnumerateArray())
                {
                    var steamId = id.ValueKind switch
                    {
                        JsonValueKind.String => ulong.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0UL,
                        JsonValueKind.Number => id.TryGetUInt64(out var u) ? u : 0UL,
                        _ => 0UL,
                    };
                    if (SteamFamilyLibrary.AccountIdOf(steamId) is { } account && !owners.Contains(account)) owners.Add(account);
                }
            apps[appId] = apps.TryGetValue(appId, out var seen)
                ? seen with { Name = seen.Name ?? name, OwnerAccountIds = seen.OwnerAccountIds.Union(owners).ToArray() }
                : new SteamFamilyLibrary.SharedApp(appId, name, owners);
        }
        return apps;
    }

    private static string? AppId(JsonElement item)
    {
        if (!item.TryGetProperty("appid", out var idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetUInt32(out var id)) return null;
        var text = id.ToString(CultureInfo.InvariantCulture);
        return SteamProtocol.IsValidAppId(text) ? text : null;
    }

    // Transport.

    private async Task<T> Protobuf<T>(HttpMethod verb, string method, byte[] request, Func<byte[], T> read, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(request);
        var outgoing = verb == HttpMethod.Get
            ? new HttpRequestMessage(HttpMethod.Get, Url(AuthService, method) + "?input_protobuf_encoded=" + Uri.EscapeDataString(encoded))
            : new HttpRequestMessage(HttpMethod.Post, Url(AuthService, method)) { Content = new FormUrlEncodedContent([new("input_protobuf_encoded", encoded)]) };
        using var response = await SendAsync(outgoing, ct).ConfigureAwait(false);
        CheckEResult(response);
        var stream = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (stream.Length > MaxBody) throw new SteamAuthException(0, "Steam sent an answer Exo could not read.");
        try
        {
            return read(stream);
        }
        catch (FormatException)
        {
            throw new SteamAuthException(0, "Steam sent an answer Exo could not read.");
        }
    }

    private Task<JsonDocument?> GetJsonAsync(string service, string method, CancellationToken ct, params (string Name, string Value)[] query) =>
        GetJsonAsync(service, method, "v1", ct, query);

    private async Task<JsonDocument?> GetJsonAsync(string service, string method, string version, CancellationToken ct, (string Name, string Value)[] query)
    {
        var parts = query.Select(q => Uri.EscapeDataString(q.Name) + "=" + Uri.EscapeDataString(q.Value));
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(service, method, version) + "?" + string.Join('&', parts)), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var text = await ExoHttp.ReadBodyLimitedAsync(response.Content, MaxBody, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    /// A request that fails loses its exception's text: a transport message can carry the URL, and the URL the access token.
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // No inner exception: it is the one that would carry the URL.
            throw new HttpRequestException("Steam could not be reached. Try again.");
        }
    }

    private string Url(string service, string method, string version = "v1") => $"{baseUrl}/{service}/{method}/{version}/";

    private static void CheckEResult(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-eresult", out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code != 1)
            throw SteamAuthException.FromCode(code);
        if (!response.IsSuccessStatusCode)
            throw new SteamAuthException((int)response.StatusCode, "Steam could not be reached. Try again.");
    }
}
