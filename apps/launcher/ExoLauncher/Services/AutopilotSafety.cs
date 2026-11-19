namespace ExoLauncher.Services;

/// <summary>
/// What Autopilot and PC › Task Manager never touch, by executable name without ".exe" (case ignored; a trailing * is a prefix),
/// and why. Anti-cheat is <see cref="ProcessTermination.IsAntiCheat"/>'s list, the one answer every place asks. Beyond the names:
/// a process outside the player's own Windows session (services, session 0), the game's own processes and Exo's are left alone
/// by the callers. Ways it could fail, each with a test: a voice call or the sound demoted mid-game; Windows' own shell or its
/// security suspended; a name with ".exe", other casing or a version suffix slipping through.
/// </summary>
internal static class AutopilotSafety
{
    internal const string AntiCheat = "anti-cheat";
    internal const string Windows = "part of Windows";
    internal const string Sound = "sound";
    internal const string Voice = "voice and calls";
    internal const string Capture = "streaming and capture";
    internal const string Input = "mouse, keyboard and controller software";
    internal const string Graphics = "graphics driver";

    private static readonly (string Why, string[] Names)[] Lists =
    [
        (Windows, [
            "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "lsm", "svchost", "fontdrvhost",
            "dwm", "memcompression", "memory compression", "secure system", "sihost", "ctfmon", "explorer", "taskhostw", "taskmgr",
            "runtimebroker", "startmenuexperiencehost", "shellexperiencehost", "shellhost", "searchhost", "searchindexer", "textinputhost",
            "lockapp", "logonui", "userinit", "conhost", "dllhost", "wudfhost", "spoolsv", "dashost", "wlanext", "smartscreen",
            "securityhealthservice", "securityhealthsystray", "msmpeng", "nissrv", "mpdefendercoreservice", "mssense", "sgrmbroker",
            "vmmem", "vmmemws", "vmcompute", "vmwp", "winmgmt", "wmiprvse", "trustedinstaller", "tiworker", "consent", "useroobebroker",
            "applicationframehost", "systemsettings", "widgets", "widgetservice", "gamebarpresencewriter", "dwmcore",
        ]),
        (Sound, [
            "audiodg", "rtkauduservice64", "rtkaudioservice64", "ravbg64", "nahimicsvc64", "nahimicsvc32", "a-volute*", "voicemeeter*",
            "vb-audio*", "steelseriessonar", "steelseriesgg", "nvbroadcast", "nvidia broadcast", "krisp", "fxsound", "dolbydax*", "dtsapo*",
            "waves*", "maxxaudio*", "equalizerapo*", "peace*", "soundswitch", "sonic studio*", "senarytechnology*",
        ]),
        (Voice, [
            "discord", "discordptb", "discordcanary", "discorddevelopment", "teamspeak", "ts3client_win64", "ts3client_win32", "mumble",
            "skype", "teams", "ms-teams", "msteams", "zoom", "slack", "guilded", "element", "telegram", "whatsapp", "signal", "vesktop",
            "webcord", "revolt", "ventrilo",
        ]),
        (Capture, [
            "obs64", "obs32", "obs-browser-page", "streamlabs*", "xsplit*", "prismlivestudio", "meldstudio", "twitchstudio", "vmix*",
            "medal", "medalencoder", "outplayed", "insights-capture", "nvcontainer", "nvsphelper64", "nvidia share", "nvidia overlay",
            "amdrsserv", "amdow", "radeonsoftware", "exocapture", "gamebar", "xboxgamebarwidgets", "elgato*", "streamdeck",
        ]),
        (Input, [
            "lghub", "lghub_agent", "lghub_system_tray", "lghub_updater", "logioptionsplus*", "razer*", "rzsynapse*", "icue", "corsair*",
            "steelseriesengine*", "wootility*", "wooting*", "ds4windows", "dsx", "steam", "steamservice", "x360ce", "joytokey",
            "rewasd*", "glorious*", "pulsar*", "finalmouse*", "lamzu*", "vgtray", "openmouse*",
        ]),
        (Graphics, [
            "nvdisplay.container", "nvidia web helper", "atieclxx", "atiesrxx", "amdfendrsr", "amdfendr", "igfxem", "igfxcui", "igfxhk",
            "igfxtray", "intelcphdcpsvc", "intelgraphicssoftware", "arccontrol", "msiafterburner", "rtss", "rtsshooksloader64",
        ]),
    ];

    /// <summary>The bare name: no folder, no ".exe", trimmed.</summary>
    internal static string Bare(string? name)
    {
        var text = (name ?? "").Trim();
        var slash = text.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) text = text[(slash + 1)..];
        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
    }

    /// <summary>Why a process of this name is never touched (one of the reasons above), or null when it may be.</summary>
    internal static string? WhyLeftAlone(string? name)
    {
        var bare = Bare(name);
        if (bare.Length == 0) return Windows;
        if (ProcessTermination.IsAntiCheat(bare)) return AntiCheat;
        foreach (var (why, names) in Lists)
            foreach (var entry in names)
                if (entry.EndsWith('*') ? bare.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase) : bare.Equals(entry, StringComparison.OrdinalIgnoreCase))
                    return why;
        return null;
    }
}
