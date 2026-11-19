using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// exolauncher:// links, so Windows and other programs can open Exo at a place:
/// exolauncher://pc (PC), exolauncher://pc/best (Best settings), exolauncher://pc/updates, exolauncher://pc/shaders,
/// exolauncher://apps (the Apps sheet), exolauncher://profile (the Exo Profile). Registered for this user only, pointing at this copy of Exo (written again when
/// Exo moved); a link Exo doesn't know opens Exo where it was.
/// </summary>
internal static class ExoLinks
{
    internal const string Scheme = "exolauncher";

    /// <summary>A place a link opens: the room, and the part of it (a PC section's link, as the page names them).</summary>
    internal sealed record Place(string Room, string? Part);

    private static readonly Dictionary<string, Place> Places = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pc"] = new("pc", "overview"),
        ["pc/best"] = new("pc", "performance"),
        ["pc/updates"] = new("pc", "drivers"),
        ["pc/shaders"] = new("pc", "shaders"),
        ["pc/display"] = new("pc", "display"),
        ["pc/graphics"] = new("pc", "graphics"),
        ["apps"] = new("pc", "apps"),
        ["library"] = new("library", null),
    };

    /// <summary>The place a command line names (its exolauncher:// argument), or null.</summary>
    internal static Place? Find(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)) continue;
            var path = arg[(Scheme.Length + 3)..].Split('?', '#')[0].Trim('/');
            return Places.GetValueOrDefault(path) ?? new Place("library", null);
        }
        return null;
    }

    /// <summary>Registers the scheme for this user, pointing at this Exo; nothing when it already does.</summary>
    internal static void Register()
    {
        if (HostMode.IsDisposable) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var command = $"\"{exe}\" \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            using var open = key.CreateSubKey(@"shell\open\command");
            if (Equals(open.GetValue(null), command)) return;
            key.SetValue(null, "URL:Exo Launcher");
            key.SetValue("URL Protocol", "");
            open.SetValue(null, command);
            AppLog.Info("Links: exolauncher:// opens this Exo.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppLog.Warn("Links: exolauncher:// not registered: " + ex.GetType().Name);
        }
    }
}
