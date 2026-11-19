namespace ExoLauncher.Services;

/// <summary>
/// What happens to a capture after it is taken or picked: whether a new snip opens in the editor, and how a capture goes
/// to a Discord conversation (as it is, as a copy cut to fit Discord's upload size, or not at all). The file never leaves
/// this PC except into the conversation the player picked in Exo's own Discord client; no upload host is involved.
/// </summary>
internal static class CaptureShare
{
    /// <summary>Discord's upload size without Nitro (10 MiB): what every account can send.</summary>
    internal const long DiscordLimit = 10L * 1024 * 1024;

    /// <summary>A capture sent to at most this many conversations at once, as Discord's Forward.</summary>
    internal const int MaxConversations = 5;

    internal enum Plan { Original, DiscordCopy, TooBig }

    /// <summary>
    /// A video that is trimmed, or bigger than Discord takes, goes as the Discord export of the part kept (the add-on fits
    /// it under the limit); a picture can't be cut down, so one past the limit is refused rather than sent blurry.
    /// </summary>
    internal static Plan PlanFor(bool video, long bytes, bool trimmed, long limit = DiscordLimit) =>
        video ? (trimmed || bytes > limit ? Plan.DiscordCopy : Plan.Original)
        : bytes <= limit ? Plan.Original : Plan.TooBig;

    /// <summary>A trim the page asked for: anything but the whole clip (0 to 0).</summary>
    internal static bool Trimmed(double? startMs, double? endMs) => startMs > 0 || endMs > 0;

    internal static string? TypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".mp4" => "video/mp4",
        _ => null,
    };

    /// <summary>
    /// After a snip: <c>edit</c> (the default) opens it in the editor, Exo in front for it; <c>copy</c> leaves it copied and
    /// saved, nothing opening. One taken over a game never opens the editor, so the game stays in front.
    /// </summary>
    internal static bool OpensEditor(string? kind, bool inGame, string? afterSnip) =>
        kind == "snip" && !inGame && afterSnip != "copy";

    internal static string NormalizeAfterSnip(string? value) => value == "copy" ? "copy" : "edit";

    internal const string TooBigPicture = "That picture is over Discord's 10 MB. Crop it in Edit, then send it.";
    internal const string TooBigCopy = "Even the Discord copy is over 10 MB. Trim it shorter, then send it.";
}
