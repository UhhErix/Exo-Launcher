using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// art.byTitle: wide art for a game known only by its name (Tonight's card for a game friends are in that isn't in
/// the library), from the cover cache, else Steam's or Epic's, kept there by name (CoverArtService.WideArtByTitleAsync).
/// url is null when none is found.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> ArtByTitleAsync(JsonElement p, bool hasParams, CancellationToken cancellationToken)
    {
        var url = ReadString(p, hasParams, "title") is { Length: > 0 } title
            ? await CoverArtService.WideArtByTitleAsync(title, cancellationToken).ConfigureAwait(false)
            : null;
        return new { ok = url is not null, url };
    }
}
