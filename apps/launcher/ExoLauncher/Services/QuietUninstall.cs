using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Which installer made a program, told from its Apps &amp; features entry and its uninstaller's own bytes (as Bulk Crap
/// Uninstaller tells them), and the command that removes it without asking anything, for a run of several the player chose
/// and confirmed once (apps.confirm). Only what each installer documents: the vendor's own QuietUninstallString first; Windows
/// Installer with /passive (a progress bar and Windows' prompt, never /qn, under which a per-machine product can't ask for
/// rights and fails); Inno Setup /VERYSILENT; NSIS /S; Squirrel --uninstall -s; a WiX Burn bundle /passive. Anything else
/// (InstallShield's own scripts, a vendor's custom uninstaller) has no switch Exo trusts, so it runs as it always does, its
/// window asking its own questions. Elevation is never changed: an uninstaller that needs it still gets Windows' prompt.
/// </summary>
internal static partial class QuietUninstall
{
    internal enum Kind { Unknown, Msi, Inno, Nsis, Squirrel, Burn, Vendor }

    /// <summary>What a program's entry says about its uninstaller.</summary>
    internal sealed record Entry(string KeyName, string? Uninstall, string? Quiet, string? ProductCode, bool Bundle);

    /// <summary>The installer behind <paramref name="entry"/>; <paramref name="marker"/> reads its uninstaller's maker from the file (<see cref="Marker"/>).</summary>
    internal static Kind Detect(Entry entry, Func<string, Kind> marker)
    {
        if (entry.Quiet is { Length: > 0 } quiet && Usable(quiet)) return Kind.Vendor;
        if (entry.ProductCode is not null || entry.Uninstall is { } msi && IsMsiExec(msi)) return Kind.Msi;
        if (entry.Uninstall is not { Length: > 0 } command) return Kind.Unknown;
        var (file, arguments) = Adapters.OfficialInstalledLibraries.SplitCommand(command);
        var exe = Path.GetFileName(file);
        if (entry.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || InnoName().IsMatch(exe)) return Kind.Inno;
        if (exe.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && arguments.Contains("--uninstall", StringComparison.OrdinalIgnoreCase)) return Kind.Squirrel;
        if (entry.Bundle && arguments.Contains("/uninstall", StringComparison.OrdinalIgnoreCase)) return Kind.Burn;
        return Path.IsPathFullyQualified(file) ? marker(file) : Kind.Unknown;
    }

    /// <summary>
    /// The quiet command for <paramref name="entry"/>, or null when its installer has no switch Exo trusts: then its own window
    /// runs as usual. A switch already in the command isn't added twice.
    /// </summary>
    internal static string? Command(Entry entry, Func<string, Kind> marker)
    {
        var kind = Detect(entry, marker);
        var command = entry.Uninstall?.Trim() ?? "";
        return kind switch
        {
            Kind.Vendor => entry.Quiet!.Trim(),
            // BuildUninstallStartInfo turns any MSI uninstall into /X {code} /passive /norestart.
            Kind.Msi => entry.ProductCode is { } code ? $"MsiExec.exe /X{code} /passive /norestart" : command,
            Kind.Inno => With(command, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"),
            Kind.Nsis => With(command, "/S"),
            Kind.Squirrel => With(command, "-s"),
            Kind.Burn => With(command, "/passive", "/norestart"),
            _ => null,
        };
    }

    // Each switch once, case aside, after what the entry already says.
    private static string With(string command, params string[] switches)
    {
        var (_, arguments) = Adapters.OfficialInstalledLibraries.SplitCommand(command);
        var have = Adapters.Cli.WindowsCommandLine.Parse(arguments).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = switches.Where(item => !have.Contains(item)).ToList();
        return added.Count == 0 ? command : command + " " + string.Join(' ', added);
    }

    private static bool IsMsiExec(string command)
    {
        var file = Path.GetFileName(Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName);
        return file.Equals("msiexec", StringComparison.OrdinalIgnoreCase) || file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
    }

    // A vendor's quiet command Exo would start at all (the same rules as its plain one: an MSI by product code, or a full path to an .exe).
    private static bool Usable(string command) => Adapters.OfficialInstalledLibraries.BuildUninstallStartInfo(command) is not null;

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnoName();

    /// <summary>
    /// The installer that made an uninstaller, by what it writes into its own file: NSIS's "Nullsoft" (its manifest and its
    /// stub's own name), Inno Setup's name, WiX Burn's ".wixburn" section. Unknown for anything else, or a file past 64 MB
    /// or unreadable.
    /// </summary>
    internal static Kind Marker(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 64L << 20) return Kind.Unknown;
            return Marker(File.ReadAllBytes(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Kind.Unknown; }
    }

    internal static Kind Marker(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOf(".wixburn"u8) >= 0) return Kind.Burn;
        if (bytes.IndexOf("Inno Setup"u8) >= 0) return Kind.Inno;
        if (bytes.IndexOf("Nullsoft"u8) >= 0 || bytes.IndexOf("NullsoftInst"u8) >= 0) return Kind.Nsis;
        return Kind.Unknown;
    }

    /// <summary>What the page and the log call it.</summary>
    internal static string Name(Kind kind) => kind switch
    {
        Kind.Msi => "Windows Installer",
        Kind.Inno => "Inno Setup",
        Kind.Nsis => "NSIS",
        Kind.Squirrel => "Squirrel",
        Kind.Burn => "WiX bundle",
        Kind.Vendor => "its own quiet mode",
        _ => "its own window",
    };
}
