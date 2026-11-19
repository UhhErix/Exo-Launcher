using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Windows Game Mode while Autopilot is on. Microsoft documents Game Mode as reallocating the resources of background processes
/// and holding back their notifications, and (since 1809) holding Windows Update's driver installs and restart notices while a
/// game runs; measured on Windows 10 and 11 it is within 0 to 2% either way, sometimes worse in CPU-bound lows. Autopilot does
/// the first job itself, per process and put back after (<see cref="AutopilotEngine"/>), so it turns Game Mode off
/// (<c>HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled</c> = 0, what Settings › Gaming › Game Mode writes) and records what
/// was there. Exo's own keeper (<see cref="WindowsGameMode"/>, which keeps Game Mode on when Autopilot is off) is told first, so
/// it puts back what it replaced and stops writing; turning Autopilot off puts the value back and hands it to the keeper again.
/// Game Mode stays on when the player chose that (Autopilot's override) and on a dual-CCD X3D or Zen 5 Ryzen, whose AMD driver
/// parks the second CCD only in Game Mode (AMD's 7000X3D guidance requires Game Mode and Game Bar). What is lost with it off:
/// Windows Update's hold on driver installs and restart notices during play, which no documented API gives an app without
/// administrator rights (IAutomaticUpdates::Pause does nothing since Windows 10).
/// Ways it could fail, each with a test: a value Exo didn't write put back over the player's; the keeper fighting Autopilot; the
/// record lost between the write and a crash (the journal entry is on disk first); X3D parking lost.
/// </summary>
internal sealed class AutopilotGameMode(IUserRegistry registry, AutopilotJournal journal, Func<GameModeState> keeper, Func<string?> keeperOff,
    Func<string?> keeperOn, Action<string>? logLine = null)
{
    internal const string Kind = "GameMode";
    private readonly Action<string> log = logLine ?? AppLog.Info;

    /// <summary>Why Game Mode stays on with Autopilot on, or null when Autopilot turns it off.</summary>
    internal static string? KeptOnBecause(AutopilotSettings settings, PowerProfiles.CpuClass cpu) =>
        !settings.On ? "off"
        : settings.KeepGameMode ? "you keep it on"
        : cpu is PowerProfiles.CpuClass.AmdDualCcdX3d or PowerProfiles.CpuClass.AmdDualCcdZen5 ? "AMD's driver needs it to park the second CCD in games"
        : null;

    internal JournalEntry? Held => journal.Entries.FirstOrDefault(entry => entry.Kind == Kind);

    /// <summary>Game Mode as Autopilot wants it now: off (recorded) when <paramref name="off"/>, else what was there put back. Null when done, else the sentence.</summary>
    internal string? Hand(bool off)
    {
        var held = Held;
        if (off && held is null) return TurnOff();
        if (!off && held is not null) return PutBack(held);
        return null;
    }

    private string? TurnOff()
    {
        // A keeper that holds Game Mode, or that would take it at its next look (Exo's next start), is told first.
        var before = keeper();
        if (Keeps(before) && keeperOff() is { } refused) return refused;
        RegValue? now;
        try { now = registry.Get(WindowsGameMode.KeyPath, WindowsGameMode.ValueName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "Windows didn't say what Game Mode is set to."; }
        if (now is { Kind: not Microsoft.Win32.RegistryValueKind.DWord }) return "Game Mode holds a value Exo doesn't know, so it's left as it is.";
        var entry = new JournalEntry(Kind, 0, 0, "Windows Game Mode", now?.Number ?? -1, 0, Held: true, Note: before.ToString());
        if (!journal.Add(entry)) return "Exo couldn't save what Game Mode was set to, so nothing changed.";
        if (Put(RegValue.DWord(0)))
        {
            log($"Autopilot: Windows Game Mode off (was {(now is null ? "not set" : now.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))}).");
            return null;
        }
        journal.Remove(entry);
        if (Keeps(before)) keeperOn();
        return "Windows didn't turn Game Mode off.";
    }

    private string? PutBack(JournalEntry held)
    {
        RegValue? now = null;
        try { now = registry.Get(WindowsGameMode.KeyPath, WindowsGameMode.ValueName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        // Only what is still Autopilot's goes back: a Game Mode the player turned on since stays on.
        if (now == RegValue.DWord(0) && !Put(held.Before < 0 ? null : RegValue.DWord(held.Before))) return "Windows didn't put Game Mode back.";
        journal.Remove(held);
        if (Enum.TryParse<GameModeState>(held.Note, out var keeperBefore) && Keeps(keeperBefore)) keeperOn();
        log("Autopilot: Windows Game Mode put back.");
        return null;
    }

    /// <summary>The keeper's states that write Game Mode on: holding it, or not yet (its next look at Exo's start takes it).</summary>
    private static bool Keeps(GameModeState state) => state is GameModeState.NotKept or GameModeState.Held or GameModeState.PutBackOnce;

    private bool Put(RegValue? value)
    {
        try
        {
            if (value is null) registry.Delete(WindowsGameMode.KeyPath, WindowsGameMode.ValueName);
            else registry.Set(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, value);
            return registry.Get(WindowsGameMode.KeyPath, WindowsGameMode.ValueName) == value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
