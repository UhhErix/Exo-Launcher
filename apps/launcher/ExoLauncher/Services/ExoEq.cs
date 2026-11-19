using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Exo EQ: the player's headphone correction and sound, per output and per game, written as Equalizer APO's exo.txt
/// (<see cref="ExoEqConfig"/>), which APO reads again the moment it changes: no program restarts, no gap in the sound.
/// Every output's block is in the file at once and APO picks each by its Device line, so a headset Windows moves to plays
/// its own EQ at once, with nothing of Exo's running. What changes the file: a change on the page, a game with its own
/// sound starting or ending, Night Shift's hours, Compare. config.txt gets one Include line, the first time the player
/// sets something; nothing else of the player's is touched. Uninstall takes both back out (<see cref="Remove"/>).
/// </summary>
internal static class ExoEq
{
    private static readonly Lock Gate = new();
    private static EqStore? _store;
    private static bool _compare;
    private static string? _written, _problem;
    private static (string Id, string Title)? _game;
    private static Timer? _night;

    internal static string StoreFile => Path.Combine(PathHelper.AppDataDir, "exo-eq.json");

    /// <summary>The library game running now that has a sound of its own, as the host sees it (set at start).</summary>
    internal static Func<(string Id, string Title)?> RunningGame { get; set; } = () => null;

    /// <summary>The player's EQ as it is now (a copy is not made: change it only through <see cref="Change"/>).</summary>
    internal static EqStore Current { get { lock (Gate) return _store ??= Load(); } }

    /// <summary>Why the last write didn't take (null when it did, or nothing was written).</summary>
    internal static string? Problem { get { lock (Gate) return _problem; } }

    internal static bool Comparing { get { lock (Gate) return _compare; } }

    internal static (string Id, string Title)? Game { get { lock (Gate) return _game; } }

    private static EqStore Load()
    {
        try { return File.Exists(StoreFile) ? EqStore.Read(File.ReadAllText(StoreFile)) : new EqStore(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            AppLog.Warn("Exo EQ: exo-eq.json couldn't be read: " + ex.GetType().Name);
            return new EqStore();
        }
    }

    private static void Save(EqStore store)
    {
        if (HostMode.IsDisposable) return;
        var tmp = StoreFile + ".tmp";
        File.WriteAllText(tmp, store.Text(), new UTF8Encoding(false));
        File.Move(tmp, StoreFile, overwrite: true);
    }

    /// <summary>Changes the player's EQ, keeps it and writes exo.txt. Returns what to tell the player, or null.</summary>
    internal static string? Change(Action<EqStore> change)
    {
        lock (Gate)
        {
            _store ??= Load();
            change(_store);
            try { Save(_store); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Exo EQ: exo-eq.json couldn't be saved: " + ex.GetType().Name);
                return "Exo couldn't keep that change. Try again.";
            }
            WatchNight(_store);
            return WriteLocked(force: true);
        }
    }

    /// <summary>Compare: the EQ off for as long as it is held, then back (never kept, so Exo closing mid-compare loses nothing).</summary>
    internal static string? Compare(bool on)
    {
        lock (Gate) { _compare = on; return WriteLocked(force: false); }
    }

    /// <summary>Writes exo.txt again if what it should say changed (a game, the hour, Windows' Night light).</summary>
    internal static string? Refresh()
    {
        lock (Gate) return WriteLocked(force: false);
    }

    /// <summary>Exo's start: Lucid's settings once, exo.txt as it should be now, and the watches that change it.</summary>
    internal static void Start()
    {
        try { LucidRetire.Once(); }
        catch (Exception ex) { AppLog.Warn("Exo EQ: taking over from Lucid failed: " + ex.GetType().Name); }
        Refresh();
        lock (Gate) WatchNight(_store ??= Load());
    }

    /// <summary>A game started or ended: its own sound goes on, or the output's comes back.</summary>
    internal static void GamesChanged()
    {
        (string, string)? game = null;
        try { game = RunningGame(); }
        catch (Exception ex) { AppLog.Debug("Exo EQ: reading the running game failed: " + ex.GetType().Name); }
        lock (Gate)
        {
            if (_game == game) return;
            _game = game;
            WriteLocked(force: false);
        }
    }

    private static void WatchNight(EqStore store)
    {
        if (store.Night.On) _night ??= new Timer(_ => Refresh(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        else { _night?.Dispose(); _night = null; }
    }

    /// <summary>Whether Night Shift plays now: on, and inside its hours (Windows' Night light schedule when it follows it).</summary>
    internal static bool NightNow(EqStore store, DateTime now)
    {
        if (!store.Night.On) return false;
        var hours = store.Night.Sync ? WindowsNightLight() : null;
        var start = hours?.Start ?? ExoEqSounds.Clock(store.Night.Start) ?? new TimeSpan(21, 0, 0);
        var end = hours?.End ?? ExoEqSounds.Clock(store.Night.End) ?? new TimeSpan(6, 0, 0);
        return ExoEqSounds.InHours(now.TimeOfDay, start, end);
    }

    /// <summary>What exo.txt says for <paramref name="store"/> now.</summary>
    internal static string TextFor(EqStore store, (string Id, string Title)? game, bool night, bool compare)
    {
        if (!store.On) return ExoEqConfig.Text([], "Off: Exo EQ is switched off in Exo.");
        if (compare) return ExoEqConfig.Text([], "Compare: Exo EQ is off while you listen without it.");
        var chains = store.Outputs.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(o => ExoEqSounds.Chain(o.Key.ToLowerInvariant(), o.Value, store, game, night)).ToList();
        return ExoEqConfig.Text(chains);
    }

    /// <summary>The chain an output plays now, for the page (its preamp and what makes it up).</summary>
    internal static EqChain ChainFor(string guid)
    {
        lock (Gate)
        {
            var store = _store ??= Load();
            var output = store.Outputs.TryGetValue(guid, out var known) ? known : new EqOutput();
            return ExoEqSounds.Chain(guid, output, store, _game, NightNow(store, DateTime.Now));
        }
    }

    private static string? WriteLocked(bool force)
    {
        var store = _store ??= Load();
        if (HostMode.IsDisposable) return null;
        // Nothing set yet: config.txt is left as the player has it.
        if (store.Outputs.Count == 0 && store.Games.Count == 0 && _written is null) return null;
        if (EqualizerApo.ConfigFolder() is not { } folder) return null;
        var text = TextFor(store, _game, NightNow(store, DateTime.Now), _compare);
        if (text == _written && !force) return _problem;
        try
        {
            var own = Path.Combine(folder, ExoEqConfig.FileName);
            if (!File.Exists(own) || File.ReadAllText(own) != text) WriteWhole(own, text, Encoding.ASCII);
            var config = Path.Combine(folder, "config.txt");
            var before = File.Exists(config) ? File.ReadAllText(config, Encoding.Latin1) : "";
            var after = ExoEqConfig.Prepared(before, lucidOff: false);
            if (after != before) WriteWhole(config, after, Encoding.Latin1);
            _written = text;
            _problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Exo EQ: Equalizer APO's config couldn't be written: " + ex.GetType().Name);
            _problem = "Exo couldn't write to Equalizer APO's config folder. Install Equalizer APO again, then try again.";
        }
        return _problem;
    }

    /// <summary>A file written whole through a temporary one beside it, so APO never reads half of it.</summary>
    private static void WriteWhole(string path, string text, Encoding encoding)
    {
        var tmp = path + ".exo-tmp";
        File.WriteAllText(tmp, text, encoding);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Whether Lucid's EQ block in config.txt still plays (a Lucid the player runs on their own writes it).</summary>
    internal static bool LucidPlays()
    {
        try
        {
            return EqualizerApo.ConfigFolder() is { } folder && Path.Combine(folder, "config.txt") is var config && File.Exists(config)
                && ExoEqConfig.LucidActive(File.ReadAllText(config, Encoding.Latin1));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Turns Lucid's EQ block in config.txt into comments, so the sound isn't corrected twice. Null when done.</summary>
    internal static string? LucidOff()
    {
        if (HostMode.IsDisposable || EqualizerApo.ConfigFolder() is not { } folder) return null;
        try
        {
            var config = Path.Combine(folder, "config.txt");
            if (!File.Exists(config)) return null;
            var before = File.ReadAllText(config, Encoding.Latin1);
            var after = ExoEqConfig.LucidOff(before);
            if (after != before) WriteWhole(config, after, Encoding.Latin1);
            AppLog.Info("Exo EQ: Lucid's EQ block turned off in config.txt.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Exo EQ: Lucid's block couldn't be turned off: " + ex.GetType().Name);
            return "Exo couldn't change Equalizer APO's config. Try again.";
        }
    }

    /// <summary>Exo's uninstall: exo.txt and its Include line go; the player's own config stays as it is.</summary>
    internal static void Remove()
    {
        if (HostMode.IsDisposable || EqualizerApo.ConfigFolder() is not { } folder) return;
        try
        {
            var config = Path.Combine(folder, "config.txt");
            if (File.Exists(config))
            {
                var before = File.ReadAllText(config, Encoding.Latin1);
                var after = ExoEqConfig.WithoutInclude(before);
                if (after != before) WriteWhole(config, after, Encoding.Latin1);
            }
            DeletionGate.DeleteFile(Path.Combine(folder, ExoEqConfig.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Uninstall: Exo EQ's file stayed: " + ex.GetType().Name); }
    }

    /// <summary>Windows' Night light schedule read from CloudStore protobuf tags (<c>CA 14 0E</c> start, <c>CA 1E 0E</c> end); null when it has none.</summary>
    internal static (TimeSpan Start, TimeSpan End)? WindowsNightLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.settings\windows.data.bluelightreduction.settings");
            return key?.GetValue("Data") is byte[] data ? NightLightSchedule(data) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    internal static (TimeSpan Start, TimeSpan End)? NightLightSchedule(byte[] data)
    {
        if (data.Length < 10) return null;
        var from = FindLastMarker(data, [0x43, 0x42, 0x01, 0x00]);
        if (from < 0) from = 0;
        if (TryReadTagTime(data, from, [0xCA, 0x14, 0x0E], out var start) &&
            TryReadTagTime(data, from, [0xCA, 0x1E, 0x0E], out var end))
            return (start, end);
        return null;
    }

    private static int FindLastMarker(byte[] data, ReadOnlySpan<byte> marker)
    {
        for (var i = data.Length - marker.Length; i >= 0; i--)
            if (data.AsSpan(i, marker.Length).SequenceEqual(marker)) return i;
        return -1;
    }

    private static bool TryReadTagTime(byte[] data, int from, ReadOnlySpan<byte> tag, out TimeSpan time)
    {
        time = default;
        for (var i = from; i <= data.Length - tag.Length - 1; i++)
        {
            if (!data.AsSpan(i, tag.Length).SequenceEqual(tag)) continue;
            var pos = i + tag.Length;
            var hour = data[pos];
            if (hour >= 24) continue;
            var min = pos + 1 < data.Length && data[pos + 1] < 60 ? data[pos + 1] : 0;
            time = new TimeSpan(hour, min, 0);
            return true;
        }
        return false;
    }
}
