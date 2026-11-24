using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// A game's achievements for the signed-in Steam account, read with that account's own sign-in access token (no Web API key):
/// the schema (ISteamUserStats/GetSchemaForGame) names every achievement, and GetPlayerAchievements says which the account has.
/// Signed out, or when Steam refuses or sends nothing usable, the caller falls back to the local cache, and no exception carries
/// a URL or a token. Steam's answers are parsed here and tested with fixture answers.
/// </summary>
internal static class SteamAccountAchievements
{
    /// <summary>The SteamID64 of the individual account with account id 0 (the base a userdata folder's id is counted from).</summary>
    internal const ulong SteamIdBase = 76561197960265728;

    /// <summary>One achievement as the schema names it; Icon and IconGray are Steam's image URLs.</summary>
    internal sealed record Row(string ApiName, string Name, string Description, bool Hidden, string? Icon, string? IconGray);

    /// <summary>The 32-bit account id a userdata folder names, for a SteamID64 (the coverage key is built from it, as the local cache's is).</summary>
    internal static uint AccountIdOf(ulong steamId) => (uint)(steamId - SteamIdBase);

    /// <summary>The game's achievements from its schema; null when the answer has none (an unavailable catalog, not an empty one).</summary>
    internal static IReadOnlyList<Row>? ParseSchema(JsonElement root)
    {
        if (!root.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.Object ||
            !game.TryGetProperty("availableGameStats", out var stats) || stats.ValueKind != JsonValueKind.Object ||
            !stats.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        var rows = new List<Row>();
        foreach (var item in list.EnumerateArray())
        {
            var api = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(api)) continue;
            rows.Add(new Row(api, ReadString(item, "displayName") ?? api, ReadString(item, "description") ?? "",
                ReadFlag(item, "hidden"), ReadString(item, "icon"), ReadString(item, "icon_gray")));
        }
        return rows.Count == 0 ? null : rows;
    }

    /// <summary>The account's unlocks by api name; null when Steam did not answer with stats for the game.</summary>
    internal static IReadOnlyDictionary<string, (bool Achieved, DateTimeOffset? At)>? ParseUnlocks(JsonElement root)
    {
        if (!root.TryGetProperty("playerstats", out var stats) || stats.ValueKind != JsonValueKind.Object) return null;
        if (!stats.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return null;
        if (!stats.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var unlocks = new Dictionary<string, (bool Achieved, DateTimeOffset? At)>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            var api = ReadString(item, "apiname");
            if (string.IsNullOrWhiteSpace(api)) continue;
            var achieved = ReadFlag(item, "achieved");
            var time = item.TryGetProperty("unlocktime", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : (DateTimeOffset?)null;
            unlocks[api] = (achieved, achieved ? time : null);
        }
        return unlocks;
    }

    /// <summary>
    /// The full catalog with the account's state. Every schema row is an entry, locked or not; an unlock with no schema row adds
    /// nothing. Null when the schema has no rows.
    /// </summary>
    internal static AchievementSnapshot? Build(string sourceGameId, string coverageKey, IReadOnlyList<Row> schema,
        IReadOnlyDictionary<string, (bool Achieved, DateTimeOffset? At)> unlocks, DateTimeOffset observedAtUtc)
    {
        if (schema.Count == 0) return null;
        var entries = schema.Select(row =>
        {
            var unlocked = unlocks.TryGetValue(row.ApiName, out var state) && state.Achieved;
            return new AchievementEntry
            {
                Definition = new AchievementDefinition
                {
                    ProviderId = "steam",
                    SourceGameId = sourceGameId,
                    ExternalId = row.ApiName,
                    Name = row.Name,
                    Description = row.Description,
                    Hidden = row.Hidden,
                    IconUnlockedUrl = AchievementIconCache.SanitizeProviderImageUrl(row.Icon),
                    IconLockedUrl = AchievementIconCache.SanitizeProviderImageUrl(row.IconGray),
                },
                State = new AchievementState
                {
                    ExternalId = row.ApiName,
                    Unlocked = unlocked,
                    UnlockedAtUtc = unlocked ? state.At : null,
                    ObservedAtUtc = observedAtUtc,
                },
            };
        }).ToList();
        return new AchievementSnapshot
        {
            ProviderId = "steam",
            SourceGameId = sourceGameId,
            CoverageKey = coverageKey,
            Coverage = AchievementCoverageStatus.Complete,
            Capabilities = AchievementProviderCapabilities.Snapshot | AchievementProviderCapabilities.Progress | AchievementProviderCapabilities.CompleteCatalog,
            ReportedTotal = entries.Count,
            ReportedUnlocked = entries.Count(entry => entry.State.Unlocked),
            ObservedAtUtc = observedAtUtc,
            Entries = entries,
        };
    }

    /// <summary>
    /// The signed-in account's snapshot of <paramref name="appId"/>, or null when there is no sign-in, Steam refuses, times out, or
    /// sends nothing usable: the caller then reads the local cache. Only a cancelled request throws.
    /// </summary>
    internal static async Task<AchievementSnapshot?> ReadAsync(SteamSignInService signIn, SteamWebApi api, string appId, CancellationToken ct)
    {
        try
        {
            var tokens = await signIn.FreshTokensAsync(ct).ConfigureAwait(false);
            if (tokens is null) return null;
            using var schemaDoc = await api.AchievementSchemaAsync(tokens.AccessToken, appId, ct).ConfigureAwait(false);
            using var unlockDoc = await api.PlayerAchievementsAsync(tokens.AccessToken, tokens.SteamId, appId, ct).ConfigureAwait(false);
            if (schemaDoc is null || unlockDoc is null) return null;
            var schema = ParseSchema(schemaDoc.RootElement);
            var unlocks = ParseUnlocks(unlockDoc.RootElement);
            if (schema is null || unlocks is null) return null;
            var coverage = AchievementCoverageKeys.FromAccount("steam", AccountIdOf(tokens.SteamId).ToString(CultureInfo.InvariantCulture));
            return Build(appId, coverage, schema, unlocks, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or SteamAuthException or JsonException ||
                                   ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            var reason = ex is SteamAuthException ? "sign-in required" : ex.GetType().Name;
            AppLog.Warn("Steam achievements: the signed-in read was not used (" + reason + "); the local cache answers.");
            return null;
        }
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Steam sends these flags as 0/1 numbers or as booleans.</summary>
    private static bool ReadFlag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n == 1);
}
