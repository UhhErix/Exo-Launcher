using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Xbox hours read from Xbox before its sign-in was retired (Microsoft has not approved Exo's sign-in, and no key is asked
/// for any more). Once, <see cref="PlaytimeService"/> moves these titles into the playtime store as the Xbox seed (kept in
/// playtime.json with its other data), then <see cref="Remove"/> deletes this file. Only titles and minutes are kept.
/// </summary>
internal static class ReportedHours
{
    internal sealed record Document(
        DateTimeOffset? XboxAt,
        IReadOnlyList<TitleHours> Xbox);

    /// <summary>One Xbox title and its minutes, as it was read from Xbox.</summary>
    internal sealed record TitleHours(string TitleId, string Name, string? Pfn, int Minutes, DateTimeOffset? LastPlayedUtc);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    internal static string FilePath => Path.Combine(PathHelper.AppDataDir, "reported-hours.json");

    /// <summary>The file's Xbox titles with minutes; null when there is no file or it cannot be read (it then stays for another try).</summary>
    internal static Document? TryRead()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 8 * 1024 * 1024) return null;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Json);
            if (document is null) return null;
            return new Document(
                document.XboxAt,
                (document.Xbox ?? [])
                    .Where(title => title is { Minutes: > 0 } && !string.IsNullOrWhiteSpace(title.Name) && !string.IsNullOrWhiteSpace(title.TitleId))
                    .ToList());
        }
        catch (Exception ex)
        {
            AppLog.Debug("Reported hours unreadable: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Deletes the file once its titles are in the playtime store.</summary>
    internal static void Remove()
    {
        try
        {
            if (File.Exists(FilePath)) DeletionGate.DeleteFile(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Reported hours file not removed: " + ex.GetType().Name);
        }
    }
}
