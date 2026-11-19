using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.SteamSignIn;

/// <summary>
/// The access token of the Steam website sign-in (Settings › Game stores › Steam gifts), for the library's Steam Family
/// list when Exo's own Steam sign-in is not used. Steam's store hands its signed-in page this token
/// (<c>store.steampowered.com/pointssummary/ajaxgetasyncconfig</c>, <c>webapi_token</c>); Exo asks for it from the
/// hidden gifts page (<see cref="Fetch"/>, set by the bridge), keeps it in memory only until shortly before it ends,
/// and never logs, saves or sends it anywhere but Steam's web API. A scan never waits on it: without a token one is
/// fetched behind the scan (at most every ten minutes), and <see cref="Arrived"/> asks for a Steam rescan.
/// </summary>
internal static class SteamWebsiteToken
{
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FetchWait = TimeSpan.FromSeconds(60);
    private static readonly object Gate = new();
    private static string? _token;
    private static SteamTokenClaims _claims;
    private static DateTimeOffset _nextTry = DateTimeOffset.MinValue;
    private static int _fetching;

    /// <summary>Reads the token from the website sign-in's page; null when that page is not signed in. Set by the bridge.</summary>
    internal static Func<CancellationToken, Task<string?>>? Fetch { get; set; }

    /// <summary>A token arrived: the Steam library can read its family now.</summary>
    internal static event Action? Arrived;

    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The token for <paramref name="steamId"/> while it is good for ten more minutes; otherwise null, and one is fetched behind.</summary>
    internal static string? For(ulong steamId)
    {
        lock (Gate)
        {
            if (_token is not null && _claims.ExpiresAt - Clock().ToUnixTimeSeconds() > SteamSignInService.RenewWithin.TotalSeconds)
                return _claims.SteamId == steamId ? _token : null;
            _token = null;
            if (Fetch is null || Clock() < _nextTry) return null;
            _nextTry = Clock() + Retry;
        }
        if (Interlocked.Exchange(ref _fetching, 1) == 0) _ = Task.Run(FetchAsync);
        return null;
    }

    /// <summary>Drops the token: the website was signed out of or in again, or Steam refused it.</summary>
    internal static void Forget(bool retrySoon = false)
    {
        lock (Gate)
        {
            _token = null;
            _claims = default;
            if (retrySoon) _nextTry = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Keeps a token that reads as one; true when it did.</summary>
    internal static bool Accept(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        SteamTokenClaims claims;
        try { claims = SteamTokenClaims.Parse(token); }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            return false;
        }
        if (claims.SteamId == 0 || claims.ExpiresAt <= Clock().ToUnixTimeSeconds()) return false;
        lock (Gate)
        {
            _token = token;
            _claims = claims;
        }
        return true;
    }

    /// <summary><c>webapi_token</c> from the store's async config answer (<c>{"success":1,"data":{"webapi_token":"…"}}</c>); null otherwise.</summary>
    internal static string? ParseAsyncConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                   data.TryGetProperty("webapi_token", out var token) && token.ValueKind == JsonValueKind.String &&
                   token.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task FetchAsync()
    {
        try
        {
            if (Fetch is not { } fetch) return;
            using var wait = new CancellationTokenSource(FetchWait);
            var got = Accept(await fetch(wait.Token).ConfigureAwait(false));
            AppLog.Info(got ? "Steam website sign-in: a token for the family library." : "Steam website sign-in: no token (not signed in there).");
            if (got) Arrived?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam website token not read: " + ex.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }
}
