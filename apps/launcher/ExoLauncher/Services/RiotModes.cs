using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Riot;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The mode a Riot game session is played in, read from Riot's own local clients and never from tracker.gg: League from
/// the League client's gameflow session (its queue id), Valorant from the Riot Client's own presence for this player
/// (its queue id). Both clients and tracker.gg's playlists map to one name per mode, so a seed's per-mode hours and the
/// tagged sessions add up. Anything the clients do not say is <see cref="Unknown"/>; a queue no name is known for is
/// <see cref="Other"/>.
/// </summary>
internal static class RiotModes
{
    internal const string Unknown = "unknown";
    internal const string Other = "other";

    private static readonly Dictionary<int, string> LeagueQueues = new()
    {
        [420] = "ranked solo",
        [440] = "ranked flex",
        [400] = "normal draft",
        [430] = "normal blind",
        [490] = "quickplay",
        [450] = "aram",
        [900] = "urf",
        [1700] = "arena",
    };

    private static readonly Dictionary<string, string> ValorantQueues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["competitive"] = "competitive",
        ["unrated"] = "unrated",
        ["spikerush"] = "spike rush",
        ["deathmatch"] = "deathmatch",
        ["ggteam"] = "team deathmatch",
        ["swiftplay"] = "swiftplay",
        ["escalation"] = "escalation",
        ["replication"] = "replication",
    };

    // tracker.gg's playlist names, letters and digits only, lower case, to the same mode names.
    private static readonly Dictionary<string, string> LeaguePlaylists = new(StringComparer.Ordinal)
    {
        ["rankedsoloduo"] = "ranked solo",
        ["rankedsolo"] = "ranked solo",
        ["rankedflex"] = "ranked flex",
        ["normaldraft"] = "normal draft",
        ["draftpick"] = "normal draft",
        ["normalblind"] = "normal blind",
        ["blindpick"] = "normal blind",
        ["quickplay"] = "quickplay",
        ["aram"] = "aram",
        ["urf"] = "urf",
        ["arena"] = "arena",
    };

    private static readonly Dictionary<string, string> ValorantPlaylists = new(StringComparer.Ordinal)
    {
        ["competitive"] = "competitive",
        ["ranked"] = "competitive",
        ["unrated"] = "unrated",
        ["spikerush"] = "spike rush",
        ["deathmatch"] = "deathmatch",
        ["teamdeathmatch"] = "team deathmatch",
        ["swiftplay"] = "swiftplay",
        ["escalation"] = "escalation",
        ["replication"] = "replication",
    };

    /// <summary>The mode name for a League queue id from the client's gameflow session.</summary>
    internal static string FromLeagueQueue(int queueId) =>
        queueId <= 0 ? Unknown : LeagueQueues.GetValueOrDefault(queueId, Other);

    /// <summary>The mode name for a Valorant queue id from the Riot Client's private presence.</summary>
    internal static string FromValorantQueue(string? queueId) =>
        string.IsNullOrWhiteSpace(queueId) ? Unknown : ValorantQueues.GetValueOrDefault(queueId.Trim(), Other);

    /// <summary>The mode name for one of tracker.gg's playlist names of a game.</summary>
    internal static string FromTrackerPlaylist(string game, string? playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return Other;
        var key = new string(playlistName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var table = game.Equals("riot:league_of_legends", StringComparison.OrdinalIgnoreCase) ? LeaguePlaylists
            : game.Equals("riot:valorant", StringComparison.OrdinalIgnoreCase) ? ValorantPlaylists
            : null;
        return table is not null && table.TryGetValue(key, out var mode) ? mode : Other;
    }

    /// <summary>The League mode from the client's <c>/lol-gameflow/v1/session</c> answer (its queue id), or Unknown.</summary>
    internal static string ParseLeagueSession(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Unknown;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("gameData", out var data) &&
                data.TryGetProperty("queue", out var queue) &&
                queue.TryGetProperty("id", out var id) &&
                id.TryGetInt32(out var queueId))
                return FromLeagueQueue(queueId);
            return Unknown;
        }
        catch (JsonException)
        {
            return Unknown;
        }
    }

    /// <summary>
    /// The Valorant mode from the Riot Client's <c>/chat/v1/session</c> (this player's puuid) and <c>/chat/v4/presences</c>
    /// answers: this player's Valorant presence carries a base64 JSON <c>private</c> field with its <c>queueId</c>.
    /// </summary>
    internal static string ParseValorantPresence(string? sessionJson, string? presencesJson)
    {
        if (string.IsNullOrWhiteSpace(sessionJson) || string.IsNullOrWhiteSpace(presencesJson)) return Unknown;
        try
        {
            using var session = JsonDocument.Parse(sessionJson);
            if (!session.RootElement.TryGetProperty("puuid", out var self) || self.ValueKind != JsonValueKind.String) return Unknown;
            using var presences = JsonDocument.Parse(presencesJson);
            if (!presences.RootElement.TryGetProperty("presences", out var list) || list.ValueKind != JsonValueKind.Array) return Unknown;
            foreach (var presence in list.EnumerateArray())
            {
                if (!Text(presence, "puuid").Equals(self.GetString(), StringComparison.Ordinal)) continue;
                if (!Text(presence, "product").Equals("valorant", StringComparison.OrdinalIgnoreCase)) continue;
                var privateB64 = Text(presence, "private");
                if (privateB64.Length == 0) return Unknown;
                using var private_ = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(privateB64)));
                return FromValorantQueue(Text(private_.RootElement, "queueId"));
            }
            return Unknown;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Unknown;
        }
    }

    /// <summary>The mode a game's session is in right now, from the client that runs it; Unknown when no client answers.</summary>
    internal static async Task<string> ReadAsync(string game, CancellationToken ct)
    {
        try
        {
            if (game.Equals("riot:valorant", StringComparison.OrdinalIgnoreCase)) return await ReadValorantAsync(ct).ConfigureAwait(false);
            if (game.Equals("riot:league_of_legends", StringComparison.OrdinalIgnoreCase)) return await ReadLeagueAsync(ct).ConfigureAwait(false);
            return Unknown;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot mode unavailable: " + ex.GetType().Name);
            return Unknown;
        }
    }

    /// <summary>Reads a session's mode in the background and sets it on the open session (no wait on the caller).</summary>
    internal static void Refresh(string game)
    {
        _ = Task.Run(async () => RiotLedger.SetMode(game, await ReadAsync(game, CancellationToken.None).ConfigureAwait(false)));
    }

    private static async Task<string> ReadValorantAsync(CancellationToken ct)
    {
        using var api = RiotClientApi.TryConnect();
        if (api is null) return Unknown;
        var session = await api.GetJsonAsync("/chat/v1/session", ct).ConfigureAwait(false);
        var presences = await api.GetJsonAsync("/chat/v4/presences", ct).ConfigureAwait(false);
        return ParseValorantPresence(session, presences);
    }

    private static async Task<string> ReadLeagueAsync(CancellationToken ct)
    {
        // The League client writes its own lockfile in the League install folder while it runs.
        var install = RiotInstallProbe.FindInstalledProduct("league_of_legends");
        if (install is null) return Unknown;
        using var api = RiotClientApi.TryConnect(Path.Combine(install, "lockfile"));
        if (api is null) return Unknown;
        return ParseLeagueSession(await api.GetJsonAsync("/lol-gameflow/v1/session", ct).ConfigureAwait(false));
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
