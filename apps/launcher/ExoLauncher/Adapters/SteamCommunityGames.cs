using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Adapters;

/// <summary>
/// The account's own owned games with their hours, from the public Steam community profile
/// (<c>steamcommunity.com/profiles/&lt;steamid64&gt;/games?xml=1</c>). No key and no sign-in: it answers only for a
/// profile whose game details are public, and it is the one keyless list of what the account owns. The running Steam
/// client is asked first (it licenses family titles too); this list separates the account's own titles from them.
/// A private profile is unavailable, never an empty library. With a sign-in (Exo's own, or the Steam website's token)
/// it also reads the owned list and the Steam Family's shared library from Steam's web API.
/// </summary>
internal static class SteamCommunityGames
{
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object Gate = new();
    private static string _cachedFor = "";
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private static OwnedGamesResult _cached = OwnedGamesResult.Unavailable;
    private static SteamFamilyLibrary.Shares? _cachedFamily;

    internal sealed record OwnedGamesResult(
        bool Authoritative,
        IReadOnlySet<string> AppIds,
        IReadOnlyDictionary<string, int> PlaytimeMinutes,
        IReadOnlyDictionary<string, string> Names)
    {
        public static OwnedGamesResult Unavailable { get; } =
            new(
                false,
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal static void InvalidateOwnedGames()
    {
        lock (Gate)
        {
            _cachedFor = "";
            _cachedAt = DateTimeOffset.MinValue;
            _cached = OwnedGamesResult.Unavailable;
            _cachedFamily = null;
        }
    }

    /// <summary>
    /// The account's owned list and its Steam Family shares, cached for five minutes. Owned is unavailable for a bad id
    /// or when neither a sign-in nor a public profile answers; Family is null when no sign-in could ask Steam's family
    /// service (or it did not answer).
    /// </summary>
    internal static async Task<(OwnedGamesResult Owned, SteamFamilyLibrary.Shares? Family)> LoadOwnedGamesAsync(string steamId64, CancellationToken ct = default)
    {
        if (!IsSteamId64(steamId64)) return (OwnedGamesResult.Unavailable, null);
        lock (Gate)
        {
            if (string.Equals(_cachedFor, steamId64, StringComparison.Ordinal) && DateTimeOffset.UtcNow - _cachedAt < Ttl)
                return (_cached, _cachedFamily);
        }

        var signedInTask = SignedInLibraryAsync(steamId64, ct);
        var publicList = await PublicProfileAsync(steamId64, ct).ConfigureAwait(false);
        var (signedIn, family) = await signedInTask.ConfigureAwait(false);
        var result = Merge(signedIn, publicList);
        lock (Gate)
        {
            _cachedFor = steamId64;
            _cachedAt = DateTimeOffset.UtcNow;
            _cached = result;
            _cachedFamily = family;
        }
        return (result, family);
    }

    /// <summary>
    /// Every source's games as one list: a game once, with the largest playtime any source gives it, and a name from the
    /// first source that has one. Authoritative when any source answered. Pure, so the merge is tested alone.
    /// </summary>
    internal static OwnedGamesResult Merge(params OwnedGamesResult?[] sources)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var minutes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var authoritative = false;
        foreach (var source in sources)
        {
            if (source is null || !source.Authoritative) continue;
            authoritative = true;
            ids.UnionWith(source.AppIds);
            foreach (var pair in source.PlaytimeMinutes)
                minutes[pair.Key] = Math.Max(minutes.GetValueOrDefault(pair.Key), pair.Value);
            foreach (var pair in source.Names)
                if (!string.IsNullOrWhiteSpace(pair.Value)) names.TryAdd(pair.Key, pair.Value);
        }
        return authoritative ? new OwnedGamesResult(true, ids, minutes, names) : OwnedGamesResult.Unavailable;
    }

    /// <summary>
    /// The library with Exo's Steam sign-in; without one, with the Steam website sign-in's token (when it is held:
    /// a missing one is fetched behind this scan, never waited for). A website token Steam no longer answers is dropped.
    /// </summary>
    private static async Task<(OwnedGamesResult? Owned, SteamFamilyLibrary.Shares? Family)> SignedInLibraryAsync(string steamId64, CancellationToken ct)
    {
        if (!ulong.TryParse(steamId64, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var steamId)) return (null, null);
        var via = "Exo's Steam sign-in";
        try
        {
            if (await SteamSignInRuntime.Service.LibraryAsync(steamId, ct).ConfigureAwait(false) is { } library) return library;
            if (SteamWebsiteToken.For(steamId) is not { } token) return (null, null);
            via = "the Steam website sign-in";
            var web = await SteamSignInService.LibraryWithTokenAsync(SteamSignInRuntime.Api, token, steamId, ct).ConfigureAwait(false);
            if (web.Family is null && !web.Owned.Authoritative)
            {
                AppLog.Info("Steam website sign-in: Steam did not answer with its token; it is read again.");
                SteamWebsiteToken.Forget();
            }
            return web;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or SteamAuthException or FormatException)
        {
            AppLog.Debug($"Steam library with {via} unavailable: " + ex.GetType().Name);
            return (null, null);
        }
    }

    private static async Task<OwnedGamesResult> PublicProfileAsync(string steamId64, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://steamcommunity.com/profiles/{steamId64}/games?xml=1",
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return OwnedGamesResult.Unavailable;
            if (response.Content.Headers.ContentLength is > MaxBodyBytes) return OwnedGamesResult.Unavailable;
            var body = await ExoHttp.ReadBodyLimitedAsync(response.Content, MaxBodyBytes, ct).ConfigureAwait(false);
            return Parse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            AppLog.Debug("Steam community games unavailable: " + ex.GetType().Name);
            return OwnedGamesResult.Unavailable;
        }
    }

    /// <summary>The games list of a community profile XML answer. A DTD is refused; no <c>games</c> element (private, error) is unavailable.</summary>
    internal static OwnedGamesResult Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return OwnedGamesResult.Unavailable;
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return OwnedGamesResult.Unavailable;
        }

        if (document.Root is not { Name.LocalName: "gamesList" } root ||
            root.Element("games") is not { } games)
            return OwnedGamesResult.Unavailable;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var minutes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var game in games.Elements("game"))
        {
            var appId = game.Element("appID")?.Value.Trim();
            if (!SteamProtocol.IsValidAppId(appId)) continue;
            ids.Add(appId);
            var name = game.Element("name")?.Value.Trim();
            if (!string.IsNullOrWhiteSpace(name)) names[appId] = name;
            var mins = HoursToMinutes(game.Element("hoursOnRecord")?.Value);
            if (mins > 0) minutes[appId] = mins;
        }

        return new OwnedGamesResult(true, ids, minutes, names);
    }

    /// <summary>Steam writes hours with a thousands comma ("1,234.5"). Anything else reads as no time.</summary>
    internal static int HoursToMinutes(string? hours)
    {
        if (string.IsNullOrWhiteSpace(hours)) return 0;
        if (!double.TryParse(hours.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value <= 0 || value > int.MaxValue / 60.0)
            return 0;
        return (int)Math.Round(value * 60, MidpointRounding.AwayFromZero);
    }

    private static bool IsSteamId64(string? steamId64) =>
        steamId64 is { Length: 17 } && steamId64.All(char.IsAsciiDigit) && steamId64.StartsWith("7656119", StringComparison.Ordinal);

    private static HttpClient CreateHttpClient()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(8), new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        return http;
    }
}
