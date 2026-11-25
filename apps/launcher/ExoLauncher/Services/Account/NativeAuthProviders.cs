using System.Reflection;
using System.Text.Json;

namespace ExoLauncher.Services.Account;

internal static class NativeAuthProviders
{
    internal const string ConfigFileName = "oauth-clients.json";

    internal sealed record ProviderSpec(string Id, string DisplayName, string Flow, string EnvVar, bool RequiresTokenProxy);

    internal sealed record ProviderStatus(string Provider, string DisplayName, string Flow, bool Available, string Note);

    private static readonly ProviderSpec[] AllProviders =
    [
        new("spotify", "Spotify", "pkce-system-browser", "EXO_OAUTH_SPOTIFY_CLIENT_ID", false),
        new("discord", "Discord", "pkce-system-browser", "EXO_OAUTH_DISCORD_CLIENT_ID", false),
        new("google", "Google / YouTube Music", "pkce-system-browser", "EXO_OAUTH_GOOGLE_CLIENT_ID", true),
        new("twitch", "Twitch", "device-code", "EXO_OAUTH_TWITCH_CLIENT_ID", false),
        new("battlenet", "Battle.net", "pkce-system-browser", "EXO_OAUTH_BATTLENET_CLIENT_ID", true),
        new("microsoft", "Xbox / Microsoft", "wam", "EXO_OAUTH_MICROSOFT_CLIENT_ID", false),
        new("riot", "Riot Games", "pkce-system-browser", "EXO_OAUTH_RIOT_CLIENT_ID", true),
        new("epic", "Epic Games", "pkce-system-browser", "EXO_OAUTH_EPIC_CLIENT_ID", true),
    ];

    private static readonly Dictionary<string, string> BakedMetadata = ReadAssemblyBakedMetadata();

    private static readonly string[] RejectedFragments =
    [
        "placeholder",
        "changeme",
        "replace_me",
        "replace-me",
        "your_client",
        "your-client",
        "exo_lab",
        "dummy",
        "example",
        "todo",
    ];

    internal static IReadOnlyList<ProviderStatus> LoadStatuses(
        string dataDir,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? getBaked = null)
    {
        var ids = LoadClientIds(dataDir, getEnv, getBaked);
        var list = new List<ProviderStatus>(AllProviders.Length);
        foreach (var spec in AllProviders)
        {
            var available = ids.ContainsKey(spec.Id);
            var note = !available
                ? $"Using classic sign-in for {spec.DisplayName}."
                : spec.RequiresTokenProxy
                    ? $"Client ID configured for {FlowLabel(spec.Flow)}; confidential /token exchange requires cloud token proxy."
                    : $"Ready via {FlowLabel(spec.Flow)}.";
            list.Add(new ProviderStatus(spec.Id, spec.DisplayName, spec.Flow, available, note));
        }
        return list;
    }

    internal static bool UsesClassicFallback(
        string providerId,
        string dataDir,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? getBaked = null)
    {
        var normalized = NormalizeProviderId(providerId);
        if (normalized is null) return true;
        var ids = LoadClientIds(dataDir, getEnv, getBaked);
        return !ids.ContainsKey(normalized);
    }

    internal static Dictionary<string, string> LoadClientIds(
        string dataDir,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? getBaked = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bakedReader = getBaked ?? ReadBakedValue;
        foreach (var spec in AllProviders)
        {
            var baked = bakedReader(spec.EnvVar);
            if (IsValidClientId(baked))
                result[spec.Id] = baked!.Trim();
        }
        var path = Path.Combine(dataDir, ConfigFileName);
        if (File.Exists(path))
        {
            try
            {
                MergeJson(File.ReadAllText(path), result);
            }
            catch
            {
            }
        }
        var envReader = getEnv ?? Environment.GetEnvironmentVariable;
        foreach (var spec in AllProviders)
        {
            var raw = envReader(spec.EnvVar);
            if (raw is null) continue;
            if (IsValidClientId(raw))
                result[spec.Id] = raw.Trim();
            else
                result.Remove(spec.Id);
        }
        return result;
    }

    internal static bool IsValidClientId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var trimmed = candidate.Trim();
        if (trimmed.Length < 8 || trimmed.Length > 256) return false;
        foreach (var ch in trimmed)
            if (char.IsWhiteSpace(ch)) return false;
        var lower = trimmed.ToLowerInvariant();
        foreach (var frag in RejectedFragments)
            if (lower.Contains(frag, StringComparison.Ordinal)) return false;
        return true;
    }

    private static string? ReadBakedValue(string envVar) =>
        BakedMetadata.TryGetValue(envVar, out var val) ? val : null;

    private static Dictionary<string, string> ReadAssemblyBakedMetadata()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in typeof(NativeAuthProviders).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key.StartsWith("EXO_OAUTH_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attr.Value))
                map[attr.Key] = attr.Value.Trim();
        }
        return map;
    }

    private static void MergeJson(string raw, Dictionary<string, string> target)
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        var obj = doc.RootElement.TryGetProperty("providers", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : doc.RootElement;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            var id = NormalizeProviderId(prop.Name);
            if (id is null) continue;
            var value = prop.Value.GetString();
            if (IsValidClientId(value))
                target[id] = value!.Trim();
            else
                target.Remove(id);
        }
    }

    private static string? NormalizeProviderId(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "spotify" => "spotify",
        "discord" => "discord",
        "google" or "youtube" => "google",
        "twitch" => "twitch",
        "battlenet" or "battle.net" or "bnet" => "battlenet",
        "microsoft" or "xbox" => "microsoft",
        "riot" => "riot",
        "epic" => "epic",
        _ => null,
    };

    private static string FlowLabel(string flow) => flow switch
    {
        "pkce-system-browser" => "system browser OAuth + PKCE",
        "wam" => "Windows Web Account Manager",
        "device-code" => "device code sign-in",
        _ => "native sign-in",
    };
}
