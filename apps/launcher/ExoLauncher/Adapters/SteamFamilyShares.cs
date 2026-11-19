using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using SharedApp = ExoLauncher.Adapters.SteamFamilyLibrary.SharedApp;

namespace ExoLauncher.Adapters;

/// <summary>
/// The games a Steam account's family shares with it, as Steam's family service or the running client last listed
/// them, kept per account (<c>steam-family.json</c>: each game's id, name and owners' account ids). Exo closes an idle
/// Steam and the family service needs a sign-in: without this, every family game left the library until Steam ran again
/// (107 of 217 games). Written only from an answer that can see the whole family (<see cref="SteamFamilyLibrary.Resolve"/>);
/// read while none can. A share someone stops is dropped at the next answer, and a game bought since counts as the
/// account's own. The file written before 3.26.0 (ids only) still reads.
/// </summary>
internal static class SteamFamilyShares
{
    internal static string FilePath { get; set; } = Path.Combine(PathHelper.AppDataDir, "steam-family.json");

    private static readonly object Gate = new();

    private sealed record Kept(string? Name, string[]? Owners);

    /// <summary>
    /// The current family list for <paramref name="scope"/> (empty when the account left its family), the account that
    /// started the scan. <paramref name="activeScope"/> is Steam's signed-in account as it is now, read just before the
    /// write: when someone else signed in meanwhile, the list (the first account's) is not kept, so it can never be
    /// read back as the second account's family. False when nothing was written for that reason or no scope is known.
    /// </summary>
    internal static bool Remember(string? scope, IReadOnlyDictionary<string, SharedApp> apps, Func<string?> activeScope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return false;
        lock (Gate)
        {
            string? now;
            try { now = activeScope(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { now = null; }
            if (!string.Equals(now, scope, StringComparison.Ordinal))
            {
                AppLog.Info("Steam family shares not saved: the Steam account changed during the scan.");
                return false;
            }
            var all = Read();
            if (all.TryGetValue(scope, out var kept) && Same(kept, apps)) return true;
            all[scope] = apps.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                var json = all.ToDictionary(
                    p => p.Key,
                    p => p.Value.OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(
                        a => a.Key, a => new Kept(a.Value.Name, a.Value.OwnerAccountIds.Count == 0 ? null : a.Value.OwnerAccountIds.ToArray())));
                File.WriteAllText(temp, JsonSerializer.Serialize(json, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("Steam family shares not saved: " + ex.GetType().Name);
            }
            return true;
        }
    }

    /// <summary>The family list last kept for <paramref name="scope"/>, or null when none ever was.</summary>
    internal static IReadOnlyDictionary<string, SharedApp>? Recall(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        lock (Gate) return Read().TryGetValue(scope, out var kept) ? kept : null;
    }

    private static bool Same(Dictionary<string, SharedApp> kept, IReadOnlyDictionary<string, SharedApp> apps) =>
        kept.Count == apps.Count && apps.All(p => kept.TryGetValue(p.Key, out var k) &&
            k.Name == p.Value.Name && k.OwnerAccountIds.SequenceEqual(p.Value.OwnerAccountIds));

    private static Dictionary<string, Dictionary<string, SharedApp>> Read()
    {
        var all = new Dictionary<string, Dictionary<string, SharedApp>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath)) return all;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return all;
            foreach (var scope in doc.RootElement.EnumerateObject())
            {
                var apps = new Dictionary<string, SharedApp>(StringComparer.Ordinal);
                if (scope.Value.ValueKind == JsonValueKind.Array)
                {
                    // Before 3.26.0: the ids alone.
                    foreach (var id in scope.Value.EnumerateArray())
                        if (id.ValueKind == JsonValueKind.String && id.GetString() is { } text && SteamProtocol.IsValidAppId(text))
                            apps[text] = new SharedApp(text, null, []);
                }
                else if (scope.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var app in scope.Value.EnumerateObject())
                    {
                        if (!SteamProtocol.IsValidAppId(app.Name) || app.Value.ValueKind != JsonValueKind.Object) continue;
                        var name = app.Value.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                            ? n.GetString()
                            : null;
                        var owners = app.Value.TryGetProperty("Owners", out var o) && o.ValueKind == JsonValueKind.Array
                            ? o.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
                                .Where(id => id.Length is > 0 and <= 10 && id.All(char.IsAsciiDigit)).ToArray()
                            : [];
                        apps[app.Name] = new SharedApp(app.Name, name, owners);
                    }
                }
                else continue;
                all[scope.Name] = apps;
            }
            return all;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
