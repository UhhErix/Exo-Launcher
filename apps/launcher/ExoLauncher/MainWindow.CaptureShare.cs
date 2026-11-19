using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// A capture on its way out of Captures: its text read off a snip (on the PC, <see cref="Services.CaptureText"/>), or the
/// file for a Discord conversation, cut to fit Discord's upload size first when it must be (<see cref="Services.CaptureShare"/>).
/// The path stays here; the page gets the text, or the bridge gets the bytes for Exo's own Discord client.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>capture.text: the text in a snip, or in the part of it dragged over (its own pixels).</summary>
    internal async Task<object> CaptureTextAsync(string? id, double? x, double? y, double? width, double? height)
    {
        var path = PathOfCapture(id);
        if (path is null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That snip is gone." };
        try
        {
            var (text, error) = await Services.CaptureText.ReadAsync(path, x, y, width, height).ConfigureAwait(false);
            return new { ok = text is not null, text, message = error };
        }
        catch (Exception ex) { return CaptureFailure("Snip text", ex); }
    }

    internal sealed record CaptureUpload(Services.DiscordClient.DiscordRest.Upload? File, string? Error, CaptureItem? Made);

    /// <summary>
    /// The capture as one Discord upload: the file itself, or for a trimmed or too-large clip, Exo Capture's Discord export
    /// of the part kept (a new clip beside it, Made). Error is a line for the player.
    /// </summary>
    internal async Task<CaptureUpload> CaptureUploadAsync(string? id, double? startMs, double? endMs, CancellationToken ct)
    {
        var path = PathOfCapture(id);
        if (path is null || Services.CaptureShare.TypeOf(path) is not { } type) return new(null, "That capture is gone.", null);
        try
        {
            var video = type == "video/mp4";
            var trimmed = Services.CaptureShare.Trimmed(startMs, endMs);
            CaptureItem? made = null;
            switch (Services.CaptureShare.PlanFor(video, new FileInfo(path).Length, trimmed))
            {
                case Services.CaptureShare.Plan.TooBig:
                    return new(null, Services.CaptureShare.TooBigPicture, null);
                case Services.CaptureShare.Plan.DiscordCopy:
                    if (CaptureAddon is not { } addon) return new(null, "Exo Capture is not installed.", null);
                    var parameters = new JsonObject
                    {
                        ["path"] = path,
                        ["startMs"] = Math.Max(0, startMs ?? 0),
                        ["endMs"] = Math.Max(0, endMs ?? 0),
                        ["preset"] = "discord",
                    };
                    var result = await addon.CallAsync("export", parameters, ct).ConfigureAwait(false);
                    if (result?["path"]?.GetValue<string>() is not { } copy || !File.Exists(copy)) return new(null, "Exo Capture didn't make the Discord copy.", null);
                    path = copy;
                    made = ItemFor(copy);
                    if (new FileInfo(path).Length > Services.CaptureShare.DiscordLimit) return new(null, Services.CaptureShare.TooBigCopy, made);
                    break;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return new(new Services.DiscordClient.DiscordRest.Upload(Path.GetFileName(path), type, bytes), null, made);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) { return new(null, ex.Message, null); }
        catch (Exception ex)
        {
            AppLog.Warn("Capture send: " + ex.GetType().Name + ": " + ex.Message);
            return new(null, "Exo could not read that capture. The details are in the log.", null);
        }
    }
}
