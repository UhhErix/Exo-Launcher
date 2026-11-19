using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>Why Exo never turns a start-up item off: what kind of thing it is, by evidence.</summary>
internal enum LockKind { None, Windows, Security, AntiCheat, Driver, InputAudio, Exo, Updater }

/// <summary>An item as the lock check sees it: what it is called, the command it runs, the program, and the service behind it.</summary>
internal sealed record LockCandidate(string Name, string Command, string? Program, string? Service = null, int ServiceType = 0, string? ServiceImage = null);

/// <summary>
/// The groups Exo never turns off at start-up (9-startup §E, PLAN V4.1), each recognised by what the thing is and not by its
/// name alone: Windows' own programs (under the Windows folder or signed as Microsoft Windows), security software
/// (Windows Security's list of products and its tray), anti-cheat (the markers <see cref="AntiCheatCheck"/> keeps, plus a
/// service whose program sits inside a game's folder), drivers' services and helpers, input and audio, and Exo itself with
/// its add-ons, WebView2's updater and Edge's. A program that only runs other programs (PowerShell, cmd) is never judged by
/// its own folder: what it runs is the item. Riot Vanguard's tray (vgtray) is the one anti-cheat file the player may take out
/// of start-up (AGENTS.md hard stops); its driver and service are locked.
/// </summary>
internal static class StartupLocks
{
    /// <summary>The folders of the games Exo knows, for a service that sits inside one. Set by the app; empty in tests unless they fill it.</summary>
    internal static Func<IReadOnlyList<string>> GameFolders { get; set; } = () => [];

    /// <summary>Programs that only start other programs: never Windows' own item, whatever folder they sit in.</summary>
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe",
        "conhost.exe", "wt.exe", "node.exe", "python.exe", "pythonw.exe", "javaw.exe", "java.exe", "bash.exe", "wsl.exe",
    };

    internal static bool IsHost(string? program) => program is not null && Hosts.Contains(Path.GetFileName(program));

    private static readonly string[] SecurityNames = ["securityhealthsystray.exe", "securityhealthservice.exe", "msmpeng.exe", "msascuil.exe", "mpdefendercoreservice.exe", "nissrv.exe"];

    private static readonly string[] AntiCheatNames =
    [
        "vgc.exe", "vgk.sys", "easyanticheat.exe", "easyanticheat_eos.exe", "easyanticheat_launcher.exe", "beservice.exe", "bedaisy.sys",
        "faceitclient.exe", "faceit.exe", "faceitservice.exe", "eseaclient.exe", "esea.exe", "anticheatexpert.exe", "aceservice.exe", "ace-base.sys",
        "gameguard.des", "npggnt.exe", "ggsvc.exe", "mhyprot2.sys", "mhyprotect.exe", "ricochet.exe", "xigncode.exe", "x3.xem", "punkbuster.exe", "pbsvc.exe",
    ];

    private static readonly string[] DriverNames =
    [
        "nvcontainer.exe", "nvdisplay.container.exe", "nvtelemetrycontainer.exe", "rtkauduservice64.exe", "rtkauduservice.exe", "rtkngui64.exe",
        "wavessyssvc64.exe", "wavessvc64.exe", "wavesaudioservice.exe", "igfxcuiservice.exe",
        "amdrssrcext.exe", "atiesrxx.exe", "atieclxx.exe", "amdfendrsr.exe", "cncmd.exe",
    ];

    private static readonly string[] InputAudioNames =
    [
        "lghub_agent.exe", "lghub.exe", "lghub_updater.exe", "razer synapse 3.exe", "razer synapse.exe", "rzsdkservice.exe", "rzsdkserver.exe",
        "icue.exe", "corsair.service.exe", "audiodg.exe", "equalizerapo.exe", "steelseriesgg.exe", "steelseriesengine3.exe", "hyperx ngenuity.exe",
        "audiosrv", "audioendpointbuilder",
    ];

    private static readonly string[] InputAudioServices = ["audiosrv", "audioendpointbuilder", "hidserv", "bthserv"];

    /// <summary>WebView2's and Edge's updater: Exo runs on WebView2 and needs it kept up to date.</summary>
    private static readonly string[] UpdaterNames = ["microsoftedgeupdate.exe", "microsoftedgeupdatecore.exe", "microsoftedgeupdatebroker.exe", "microsoftedgeupdateondemand.exe", "microsoftedge_x"];

    /// <summary>A name the owner chose to take out on purpose and Exo keeps out: the Windows Security tray (2026-10-31).</summary>
    internal static bool OwnerChoice(string? program, string command) =>
        (program ?? command).Contains("securityhealthsystray.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Riot Vanguard's tray icon: anti-cheat's, left alone by the guard, but the player's to take out of start-up.</summary>
    internal static bool IsVanguardTray(string? program, string command) =>
        (program ?? command).Contains("vgtray.exe", StringComparison.OrdinalIgnoreCase) || command.Contains("vgtray.exe", StringComparison.OrdinalIgnoreCase);

    private static bool Under(string? path, string folder) =>
        path is not null && folder.Length > 0 && path.StartsWith(folder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    internal static LockKind Classify(IStartupHost host, LockCandidate item, StartupCatalog.Item? known)
    {
        var program = item.Program;
        var file = (program is null ? null : Path.GetFileName(program))?.ToLowerInvariant() ?? "";
        var names = StartupApps.ExecutableNames(item.Command).Select(name => name.ToLowerInvariant()).Append(file).Where(name => name.Length > 0).ToList();
        if (item.Service is { } service) names.Add(service.ToLowerInvariant());
        if (item.ServiceImage is { } image) names.Add(Path.GetFileName(image.Trim('"')).ToLowerInvariant());
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Exo and what it carries: its own folder, its add-ons, and the data it keeps.
        if (Under(program, AppContext.BaseDirectory.TrimEnd('\\')) || Under(program, PathHelper.AppDataDir)
            || names.Any(name => name is "exolauncher.exe" or "exoshaders.service.exe") || program is not null && program.Contains(@"\ExoLauncher\addons\", StringComparison.OrdinalIgnoreCase)
            || item.Name.StartsWith("Exo", StringComparison.Ordinal) && item.Name.Contains("Shaders", StringComparison.Ordinal))
            return LockKind.Exo;

        // Anti-cheat, except the Riot Vanguard tray icon.
        if (!IsVanguardTray(program, item.Command))
        {
            if (names.Any(name => AntiCheatNames.Contains(name)) || known?.Verdict == "locked" && known.Match.Any(match => AntiCheatNames.Contains(match, StringComparer.OrdinalIgnoreCase))
                || names.Any(name => AntiCheatCheck.ForFile(name) is not null && !name.StartsWith("riotclient", StringComparison.Ordinal))
                || (item.ServiceImage ?? program) is { } where && GameFolders().Any(folder => Under(where.Trim('"'), folder)))
                return LockKind.AntiCheat;
        }

        // Security software: Windows Security's own list, and its tray and service.
        if (names.Any(name => SecurityNames.Contains(name))) return LockKind.Security;
        if (program is not null && SecurityProgramMatch(host, program)) return LockKind.Security;

        // WebView2's and Edge's updater.
        if (names.Any(name => UpdaterNames.Any(updater => name.StartsWith(updater, StringComparison.Ordinal)))
            || item.Name.StartsWith("MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase)) return LockKind.Updater;

        // Drivers and their helpers.
        if (item.ServiceType is 1 or 2 or 8) return LockKind.Driver;
        if (program is not null && program.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase)
            || item.ServiceImage is { } driverImage && driverImage.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase)
            || names.Any(name => DriverNames.Contains(name))) return LockKind.Driver;

        // Input and audio.
        if (names.Any(name => InputAudioNames.Contains(name)) || item.Service is { } audio && InputAudioServices.Contains(audio, StringComparer.OrdinalIgnoreCase)) return LockKind.InputAudio;

        // Windows' own programs, unless the program only starts another one.
        if (program is not null && !IsHost(program) && (Under(program, windir) || host.Signer(program) == "Microsoft Windows")) return LockKind.Windows;
        if (item.ServiceImage is { } serviceImage && Path.IsPathFullyQualified(serviceImage.Trim('"')) && Under(serviceImage.Trim('"'), windir) && !IsHost(serviceImage.Trim('"'))) return LockKind.Windows;
        // The catalog's own word for what it cannot classify further (FACEIT's client): anti-cheat.
        return known?.Verdict == "locked" ? LockKind.AntiCheat : LockKind.None;
    }

    private static bool SecurityProgramMatch(IStartupHost host, string program)
    {
        var folder = Path.GetDirectoryName(program);
        foreach (var product in host.SecurityPrograms())
        {
            if (string.Equals(product, program, StringComparison.OrdinalIgnoreCase)) return true;
            var productFolder = Path.GetDirectoryName(product);
            if (folder is { Length: > 3 } && string.Equals(folder, productFolder, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(folder, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>What a person reads when Exo leaves one in start-up: one plain sentence.</summary>
    internal static string Sentence(LockKind kind) => kind switch
    {
        LockKind.Windows => "Windows needs this one.",
        LockKind.Security => "Your security software needs this one.",
        LockKind.AntiCheat => "Anti-cheat needs this one.",
        LockKind.Driver => "A driver needs this one.",
        LockKind.InputAudio => "Your mouse, keyboard or sound needs this one.",
        LockKind.Exo => "Exo needs this one.",
        LockKind.Updater => "Exo needs this one to keep WebView2 up to date.",
        _ => "",
    };
}
