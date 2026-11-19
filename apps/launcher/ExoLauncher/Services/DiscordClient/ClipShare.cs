namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Sharing a capture from Exo Capture to a Discord conversation in one press (Play › Clips): the host reads the file,
/// because paths stay in native code, and posts it as an attachment. This is what decides whether a file may go, from its name and size alone.
///
/// How it can fail, and what stops it:
/// - A clip over Discord's 25 MB reaches the upload and fails halfway: <see cref="Check"/> says so first, and the page offers Shrink instead.
/// - A file that is not a capture (anything but mp4 or png) goes out under a capture's id: only those two kinds pass.
/// - A name with a path, control characters or a bidi override reaches the conversation: <see cref="SafeName"/> keeps the file's name only, cleaned, and short.
/// - An empty file posts an empty attachment: refused as gone.
/// </summary>
internal static class ClipShare
{
    /// <summary>The most a conversation takes at once, as the composer's own upload limit.</summary>
    internal const long MostBytes = 25L * 1024 * 1024;
    private const int MostNameChars = 100;

    internal sealed record Plan(string Name, string Type);

    /// <summary>The attachment's name and type, or why this file can't be shared.</summary>
    internal static (Plan? Plan, string? Error) Check(string fileName, long bytes)
    {
        var type = Path.GetExtension(fileName).ToLowerInvariant() switch { ".mp4" => "video/mp4", ".png" => "image/png", _ => null };
        if (type is null || bytes <= 0) return (null, "That capture is gone.");
        if (bytes > MostBytes) return (null, "It's over 25 MB. Shrink it first.");
        return (new Plan(SafeName(fileName), type), null);
    }

    /// <summary>The file's own name, without a folder, controls or bidi overrides, at most 100 characters with its extension kept.</summary>
    internal static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name);
        var stem = new string([.. Path.GetFileNameWithoutExtension(name).Where(c => !char.IsControl(c) && c is not (>= '‪' and <= '‮') and not (>= '⁦' and <= '⁩') && c is not ('/' or '\\' or ':'))]).Trim();
        if (stem.Length == 0) stem = "capture";
        var room = Math.Max(1, MostNameChars - extension.Length);
        if (stem.Length > room) stem = stem[..(char.IsHighSurrogate(stem[room - 1]) ? room - 1 : room)];
        return stem + extension;
    }
}
