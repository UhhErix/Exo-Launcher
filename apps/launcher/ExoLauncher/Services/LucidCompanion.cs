using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Lucid (an EQ app on Equalizer APO: headphone EQ, auto device and per-game EQ switching, by tekkusai), which ships
/// unmodified inside Exo under <c>addons\lucid</c>, with its author's permission, and comes up to date with Exo's own
/// releases. It is still its own process: Exo starts it as Lucid's Windows sign-in entry does, out of sight
/// (<c>/startup</c>), with no tray icon, and Exo's page is its UI. It closes with Exo, whose installer has to rename the
/// app folder it runs from; what you hear stays, since Equalizer APO keeps playing the files Lucid wrote. Lucid's hidden
/// start doesn't answer the Restart Manager's ask (351 in app.log), so what closes it for sure is <see cref="StopHostedAsync"/>:
/// ask, wait, then end that one process if its exe is inside Exo's own folder (<see cref="LucidStop"/>).
/// Lucid's own files (<c>%APPDATA%\LucidEQ</c>, <c>%ProgramData%\LucidEQ</c>) don't depend on where its exe is, so a copy the
/// user already ran hands over its settings as they are, and that copy's file is never touched.
/// Lucid reads its settings once at start and saves its whole copy on every change (no watcher, no message), so Exo
/// changes them only with Lucid closed: Windows' Restart Manager asks it to close as at sign-out (no kill), Exo writes
/// the file the way Lucid does (<see cref="LucidSettings"/>), then starts it again out of sight. Lucid Voice is kept off
/// (<see cref="KeepVoiceOff"/>): Exo Voice handles the mic in Exo's calls, and a second chain before it adds delay.
/// </summary>
internal static partial class LucidCompanion
{
    private const string Instance = @"Global\LucidToggle.SingleInstance";

    /// <summary>The Lucid that ships in Exo; a Debug build has none until an add-on folder is copied in.</summary>
    internal static string Bundled { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "addons", "lucid", "Lucid.exe");

    /// <summary>Where the Lucid that Exo downloaded lives: outside the app folder, which Exo's own updates replace (<see cref="LucidUpdate"/>).</summary>
    internal static LucidStore Store => new(System.IO.Path.Combine(PathHelper.AppDataDir, "addons", "lucid-current"), System.IO.Path.Combine(PathHelper.AppDataDir, "lucid-update.json"));

    /// <summary>The folders Exo hosts a Lucid from: its app folder (the shipped one) and the store of the downloaded one.</summary>
    internal static string[] HostedFolders => [AppContext.BaseDirectory, Store.Folder];

    internal static bool IsHosted(string? file) => LucidStop.IsUnderAny(file, HostedFolders);

    private static (string File, long Length, long Written, string? Recorded, bool Good)? _checkedDownload;

    /// <summary>
    /// The Lucid Exo downloaded, if it is whole: the file the update recorded (its size and SHA-256, read once per change of
    /// the file), still Lucid. Anything else in the store is not used.
    /// </summary>
    internal static string? Downloaded()
    {
        var store = Store;
        try
        {
            if (!File.Exists(store.Current)) return null;
            var info = new FileInfo(store.Current);
            var state = store.Load();
            if (_checkedDownload is { } known && known.File == store.Current && known.Length == info.Length && known.Written == info.LastWriteTimeUtc.Ticks && known.Recorded == state.Sha256)
                return known.Good ? store.Current : null;
            var good = state.Sha256 is { } want && info.Length == state.Size && IsLucid(store.Current)
                && LucidUpdate.Sha256Of(store.Current).Equals(want, StringComparison.OrdinalIgnoreCase);
            _checkedDownload = (store.Current, info.Length, info.LastWriteTimeUtc.Ticks, state.Sha256, good);
            if (!good) AppLog.Warn("Lucid: the downloaded copy isn't the one the update recorded, so it isn't used.");
            return good ? store.Current : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The Lucid Exo hosts: the newer of the one shipped in Exo and the one it downloaded (the shipped one when they are the same); null when neither exists.</summary>
    internal static string? Hosted() =>
        LucidUpdate.Newest(File.Exists(Bundled) ? Bundled : null, Downloaded(), FileVersion);

    internal sealed record State(string? Path, bool Running, bool VoiceOn);

    internal static State Read(AppSettings settings) => new(Locate(settings), Running(), VoiceOn());

    /// <summary>The exe to use on this PC (<see cref="Choose"/>).</summary>
    internal static string? Locate(AppSettings settings) =>
        Choose(Hosted(), HostedOnly(RunningFile()), settings.LucidPath is { } remembered && IsLucid(remembered) ? remembered : null, settings.StartLucidWithExo, FileVersion);

    /// <summary>
    /// The Lucid in Exo, unless a separate copy is newer: starting an older one would take its settings backwards. A copy
    /// running on its own stays the one Exo talks to until Start with Exo is turned on, as it is the user's until then.
    /// </summary>
    internal static string? Choose(string? bundled, string? running, string? remembered, bool startWithExo, Func<string, Version?> version)
    {
        if (bundled is null) return running ?? remembered;
        if (running is not null && !Same(running, bundled) && (!startWithExo || Newer(running))) return running;
        if (remembered is not null && !Same(remembered, bundled) && Newer(remembered)) return remembered;
        return bundled;

        bool Newer(string other) => version(other) is { } theirs && version(bundled) is { } ours && theirs > ours;
    }

    // A running Lucid from one of Exo's folders is Exo's whichever it is; only one from elsewhere is a separate copy.
    private static string? HostedOnly(string? running) => running is not null && IsHosted(running) ? null : running;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The exe Lucid runs from; null when it isn't running, or runs as administrator (Windows won't say).</summary>
    private static string? RunningFile()
    {
        var running = RunningLucid();
        try { return running.Count > 0 ? running[0].File : null; }
        finally { foreach (var (process, _) in running) process.Dispose(); }
    }

    /// <summary>
    /// Whether Lucid can start without asking anything of Windows. Without Equalizer APO or its own mark that its engine is
    /// set up (ApInstaller.IsInstalled), Lucid downloads and installs at start, even out of sight: that waits for Set up.
    /// </summary>
    internal static bool Ready() =>
        DeviceSelector() is not null && File.Exists(LucidSettings.FilePath)
        && File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(LucidSettings.FilePath)!, "__lucid_ok.flag"));

    /// <summary>Lucid's running processes and their exe; the caller disposes them.</summary>
    private static List<(Process Process, string File)> RunningLucid()
    {
        var lucid = new List<(Process, string)>();
        foreach (var process in Process.GetProcessesByName("Lucid"))
        {
            try
            {
                if (process.MainModule?.FileName is { } file && IsLucid(file)) { lucid.Add((process, file)); continue; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return lucid;
    }

    /// <summary>A path on one of this PC's own drives: a Lucid path from another PC or a sync never reaches a network share.</summary>
    internal static bool IsLocalPath(string file) =>
        LocalPath.IsLocalDrivePath(file);

    private static bool IsLucid(string file)
    {
        if (!IsLocalPath(file)) return false;
        if (!string.Equals(System.IO.Path.GetFileName(file), "Lucid.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return false;
        try { return string.Equals(FileVersionInfo.GetVersionInfo(file).ProductName?.Trim(), "Lucid", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The exe's file version, null when it has none.</summary>
    internal static Version? FileVersion(string exe)
    {
        try { return Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var version) ? version : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"6.0.3" for 6.0.3.0, "7.0" for 7.0.0.0.</summary>
    internal static string Shown(Version v) => v.ToString(v.Revision > 0 ? 4 : v.Build > 0 ? 3 : 2);

    internal static bool Running()
    {
        try
        {
            if (!Mutex.TryOpenExisting(Instance, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Lucid Voice's live switch, which its Voice Core reads as it runs (Lucid's LucidVoiceAudio.DspSettingsPath).</summary>
    internal static string VoiceDspPath { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LucidEQ", "lucid_voice_dsp.json");

    /// <summary>Lucid Voice's live switch turned off, the rest kept; null when it is already off or isn't Lucid's.</summary>
    internal static string? VoiceDspOff(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["enabled"]?.GetValueKind() != JsonValueKind.True) return null;
            root["enabled"] = false;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return null; }
    }

    private static readonly List<FileSystemWatcher> VoiceWatchers = [];
    private static Timer? _voiceRecheck, _voiceLater;

    /// <summary>
    /// Lucid Voice stays off, however it was turned on (Lucid's window, a Repair, an update): its live switch off at once,
    /// as Lucid's own DisableLucidVoiceAsync does (no restart), and its setting off so Lucid doesn't turn it on again when
    /// it starts. Checked now and whenever either file changes while Exo runs.
    /// </summary>
    internal static void KeepVoiceOff(SettingsService settings)
    {
        if (HostMode.IsDisposable) return;
        Check();
        Watch();
        // Lucid set up while Exo runs: its folders appear later, so until both are
        // watched, a slow look every half minute starts watching them and checks once.
        if (!AllWatched()) _voiceLater = new Timer(_ =>
        {
            if (GameRunningSignal.Running) return;
            Watch();
            Check();
            if (AllWatched()) _voiceLater?.Dispose();
        }, null, 30_000, 30_000);

        bool AllWatched() { lock (VoiceWatchers) return VoiceWatchers.Count == 2; }

        void Watch()
        {
            foreach (var file in new[] { VoiceDspPath, LucidSettings.FilePath })
            {
                try
                {
                    var folder = System.IO.Path.GetDirectoryName(file)!;
                    if (!Directory.Exists(folder)) continue;
                    lock (VoiceWatchers) if (VoiceWatchers.Any(w => string.Equals(w.Path, folder, StringComparison.OrdinalIgnoreCase) && w.Filter == System.IO.Path.GetFileName(file))) continue;
                    var watcher = new FileSystemWatcher(folder, System.IO.Path.GetFileName(file)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
                    // Lucid writes through a temporary file and a replace: one check a moment after the last event.
                    FileSystemEventHandler later = (_, _) => (_voiceRecheck ??= new Timer(_ => Check())).Change(800, Timeout.Infinite);
                    watcher.Changed += later;
                    watcher.Created += later;
                    watcher.Renamed += (_, _) => later(null!, null!);
                    watcher.EnableRaisingEvents = true;
                    lock (VoiceWatchers) VoiceWatchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { AppLog.Warn("Lucid: can't watch " + System.IO.Path.GetFileName(file) + ": " + ex.Message); }
            }
        }

        void Check()
        {
            try
            {
                if (File.Exists(VoiceDspPath) && VoiceDspOff(File.ReadAllText(VoiceDspPath)) is { } off)
                {
                    // Lucid's own way: UTF-8 without a mark (its Voice Core rejects a file that starts with one).
                    // Beside it and moved over, as Lucid itself writes: Lucid never reads half a file.
                    var staged = VoiceDspPath + ".exo-tmp";
                    try
                    {
                        File.WriteAllText(staged, off, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        File.Move(staged, VoiceDspPath, overwrite: true);
                    }
                    finally { try { DeletionGate.DeleteFile(staged); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                    AppLog.Info("Lucid: Lucid Voice turned off; Exo Voice handles the mic.");
                    // A Voice Tune made through Lucid Voice set its gate for Lucid's louder sound: without it the gate would hold
                    // back a normal voice, so that mic's tuning is let go of (Exo Voice's general settings until it is tuned again).
                    if (LucidSettings.Load(LucidSettings.FilePath)?["LucidVoiceMicEndpointGuid"] is JsonValue mic && mic.TryGetValue<string>(out var guid) && guid.Length > 0)
                        settings.UpdateProfile(s =>
                        {
                            foreach (var key in s.DiscordVoiceTunes.Keys.Where(key => key.EndsWith(guid, StringComparison.OrdinalIgnoreCase)).ToList())
                                s.DiscordVoiceTunes.Remove(key);
                        });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Lucid: Lucid Voice's switch not changed: " + ex.Message); }
            if (VoiceOn() && Locate(settings.Current) is { } lucid) _ = ChangeAsync(lucid, new LucidSettings.Change { VoiceOff = true }, now: true);
        }
    }

    /// <summary>Lucid Voice as Lucid's own settings have it; off when they can't be read.</summary>
    internal static bool VoiceOn() =>
        LucidSettings.Load(LucidSettings.FilePath)?["LucidVoiceEnabled"]?.GetValueKind() == JsonValueKind.True;

    /// <summary>Starts Lucid out of sight, as at Windows sign-in, and looks for its new tray icon until it has come and gone.</summary>
    internal static bool Start(string path)
    {
        if (Launch(path, window: false) is null) return false;
        _ = Task.Run(async () =>
        {
            foreach (var wait in (int[])[1000, 1000, 1000, 3000])
            {
                await Task.Delay(wait).ConfigureAwait(false);
                RemoveTrayIcon();
            }
        });
        return true;
    }

    /// <summary>Starts Lucid, out of sight or showing its window (a running Lucid restores its own); its process id, or null.</summary>
    private static int? Launch(string path, bool window)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, window ? "" : "/startup")
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
            });
            AppLog.Info($"Lucid: started {(window ? "with its window" : "out of sight")}.");
            return process?.Id;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn("Lucid: could not start: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Lucid's own window, only for the user who pressed Repair in Exo: Repair audio engine lives nowhere else. Null when
    /// it opened, else why not. A Lucid running as administrator can't be reached, and another copy's start would stop at
    /// its "different version" message.
    /// </summary>
    internal static string? Repair(string path)
    {
        if (Running() && RunningFile() is null) return WontClose;
        if (IsHosted(path)) HideTray(true);
        return Open(path) ? null : "Lucid didn't open.";
    }

    /// <summary>
    /// The window itself, with Exo in front as Repair was pressed. The second launch only asks the Lucid already running to
    /// restore its window (Lucid's App.TryAlertExisting posts it LucidToggle.Restore.V1), and Windows doesn't let that
    /// background process take the front: its window came up behind, and where it last was, which can be the other screen
    /// under a game. So Exo, in front as it was pressed, lets that Lucid take the front, and once its window shows, puts
    /// it on Exo's screen and hands it the front. When the user has gone to another app meanwhile, nothing is taken.
    /// </summary>
    private static bool Open(string path)
    {
        var exo = GetForegroundWindow();
        var pressed = exo != 0 && GetWindowThreadProcessId(exo, out var owner) != 0 && owner == (uint)Environment.ProcessId;
        var running = RunningLucid();
        var lucid = running.Select(p => p.Process.Id).ToList();
        foreach (var (process, _) in running) process.Dispose();
        if (pressed) foreach (var id in lucid) AllowSetForegroundWindow((uint)id);
        if (Launch(path, window: true) is not { } started) return false;
        if (lucid.Count == 0) lucid.Add(started);
        _ = Task.Run(() => ShowAsync(path, pressed ? exo : 0, lucid));
        return true;
    }

    // Exo is 0 when the user has gone elsewhere: Lucid's window still has to come, but nothing is taken from them.
    private static async Task ShowAsync(string path, nint exo, List<int> lucid)
    {
        // A Lucid started cold takes about a second to show its window. A Lucid in its tray can miss the ask, which then
        // leaves no trace on either side, so a window still not there is asked for again.
        for (var tries = 1; tries <= 60; tries++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            var window = lucid.Select(MainWindowOf).FirstOrDefault(w => w != 0);
            if (window == 0 && tries is 20 or 40)
            {
                AppLog.Info("Lucid: its window hasn't come; asking again.");
                Launch(path, window: true);
            }
            if (window == 0 || IsIconic(window)) continue;
            if (exo == 0) return;
            var front = GetForegroundWindow();
            var theirs = GetWindowThreadProcessId(front, out var owner) != 0 && lucid.Contains((int)owner);
            if (front != exo && !theirs) return;
            ToScreenOf(window, exo);
            if (front != window) SetForegroundWindow(window);
            return;
        }
        AppLog.Warn("Lucid: its window didn't come after being asked three times.");
    }

    // The process's visible window that stands on its own (no owner, not a tool window such as a toast), or 0.
    private static nint MainWindowOf(int processId)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var owner) == 0 || owner != (uint)processId
                || GetWindow(window, 4 /* GW_OWNER */) != 0 || (GetWindowLongPtr(window, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    // Centred on the screen Exo is on, when it's on another; its size and place in the stack kept, and no activation.
    private static void ToScreenOf(nint window, nint exo)
    {
        var screen = MonitorFromWindow(exo, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (MonitorFromWindow(window, 2) == screen) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(screen, ref info) || !GetWindowRect(window, out var rect)) return;
        var (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        var x = info.Work.Left + Math.Max(0, (info.Work.Right - info.Work.Left - width) / 2);
        var y = info.Work.Top + Math.Max(0, (info.Work.Bottom - info.Work.Top - height) / 2);
        SetWindowPos(window, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 /* SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE */);
    }

    /// <summary>
    /// On Exo's start, and when Start with Exo is turned on: Lucid running out of sight, from the copy in Exo. A separate
    /// older or equal copy, running or set to start at sign-in, gives way to it (<see cref="LucidSettings.Change.TakeOver"/>);
    /// nothing starts on a PC where Lucid would download or install, until Set up is pressed. Null, or why it didn't start.
    /// </summary>
    internal static async Task<string?> StartWithExoAsync(AppSettings settings)
    {
        if (!settings.StartLucidWithExo || HostMode.IsDisposable) return null;
        HideTray(true);
        if (!Ready()) { AppLog.Info("Lucid: waits for Set up."); return null; }
        if (Locate(settings) is not { } path) { AppLog.Info("Lucid: Start with Exo is on, but this build has no Lucid."); return null; }
        var signIn = IsHosted(path) && LucidSettings.Load(LucidSettings.FilePath)?["RunAtStartup"]?.GetValueKind() == JsonValueKind.True;
        if (!Running())
        {
            if (signIn) return await ChangeAsync(path, new LucidSettings.Change { TakeOver = true }).ConfigureAwait(false);
            return Start(path) ? null : "Lucid didn't start. Try again.";
        }
        if (RunningFile() is not { } running) { AppLog.Info("Lucid: runs as administrator, so it's left until the next sign-in."); return null; }
        return Same(running, path) && !signIn ? null : await ChangeAsync(path, new LucidSettings.Change { TakeOver = true }).ConfigureAwait(false);
    }

    /// <summary>Exo closing, or Start with Exo turned off: the Lucid in Exo closes; a separate copy stays, and gets its tray icon back.</summary>
    internal static async Task LetGoAsync()
    {
        if (HostMode.IsDisposable) return;
        // An update's swap in progress finishes first: it would start the new Lucid after this had closed the old one.
        var held = await ChangeGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        try { await StopHostedAsync().ConfigureAwait(false); }
        finally { if (held) ChangeGate.Release(); }
        HideTray(false);
    }

    /// <summary>
    /// Closes the Lucid that Exo hosts, which would hold the folder an update, install, uninstall or swap has to rename or
    /// delete: asked to close first, ended only if it won't (<see cref="LucidStop"/>), and only if its exe is inside Exo's app
    /// folder or the store of the Lucid it downloaded (<see cref="HostedFolders"/>). A copy the user runs from anywhere else
    /// is never asked or ended. File is the exe that was running (null when none ran, or when one is still running). A caller
    /// that closed it for an update starts it again with <see cref="Start"/> if the update doesn't go ahead; a finished update
    /// starts Exo, which starts it (<see cref="StartWithExoAsync"/>).
    /// </summary>
    internal static async Task<(LucidStopped Result, string? File)> StopHostedAsync()
    {
        var os = new SystemLucidProcesses();
        var ran = os.Running().FirstOrDefault(p => IsHosted(p.File))?.File;
        var result = await LucidStop.HostedAsync(os, HostedFolders, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        if (result.Found == 0) return (result, null);
        if (result.Ended > 0) AppLog.Warn($"Lucid: {result.Ended} didn't close when asked and {(result.Ended == 1 ? "was" : "were")} ended (it ran from Exo's own folder).");
        else AppLog.Info("Lucid: closed.");
        if (result.Left == 0) return (result, ran);
        AppLog.Warn("Lucid: still running from Exo's folder.");
        return (result, null);
    }

    /// <summary>Equalizer APO's device picker, which Lucid opens elevated from APO's folder to choose its devices.</summary>
    internal static string? DeviceSelector()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            var dir = key?.GetValue("InstallPath") as string;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            var picker = System.IO.Path.Combine(dir, "DeviceSelector.exe");
            return File.Exists(picker) ? picker : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Opens the device picker as Lucid's DeviceSelectorLauncher does (runas, in APO's folder); null when it opened.</summary>
    internal static string? OpenDeviceSelector()
    {
        if (DeviceSelector() is not { } picker) return "Equalizer APO isn't installed. Set up Lucid first.";
        try
        {
            // Elevated, it opens behind Exo: Exo (in front when it was pressed) lets it take the front, then puts it there.
            AllowSetForegroundWindow(uint.MaxValue);
            using var process = Process.Start(new ProcessStartInfo(picker)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = System.IO.Path.GetDirectoryName(picker) ?? "",
            });
            if (process is null) return "The device picker didn't open.";
            var id = process.Id;
            _ = Task.Run(async () =>
            {
                for (var tries = 0; tries < 75; tries++)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    if (WindowOf(id) is not 0 and var window) { ShowWindow(window, 9); SetForegroundWindow(window); return; } // SW_RESTORE
                }
            });
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Windows asked for permission and it was declined."; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Lucid: device picker didn't open: " + ex.Message);
            return "The device picker didn't open.";
        }
    }

    private static readonly SemaphoreSlim ChangeGate = new(1, 1);
    private static readonly Lock PendingGate = new();
    private static LucidSettings.Change? _pending;
    private static Task<string?>? _pendingRun;

    /// <summary>
    /// Whether a game is playing (Exo's host says). A change waits for the last game to end: Lucid starts again to take
    /// one, and the EQ drops out for about 3 seconds while it does. Lucid Voice going off for Exo Voice doesn't wait.
    /// </summary>
    internal static Func<bool> Playing { get; set; } = () => false;

    private static LucidSettings.Change? _held;

    /// <summary>The change waiting for the game to end, if one is.</summary>
    internal static LucidSettings.Change? Held { get { lock (PendingGate) return _held; } }

    /// <summary>
    /// Changes Lucid's settings. Changes made close together are written in one go after a short pause, so a few
    /// switches cost Lucid one restart. Null when they took, else why not.
    /// </summary>
    internal static Task<string?> ChangeAsync(string lucid, LucidSettings.Change change, bool now = false)
    {
        if (HostMode.IsDisposable) return Task.FromResult<string?>(HostMode.Refusal);
        if (!now && Playing())
        {
            lock (PendingGate) Hold(lucid, _held is null ? change : _held.Then(change));
            AppLog.Info("Lucid: a change waits for the game to end.");
            return Task.FromResult<string?>(null);
        }
        lock (PendingGate)
        {
            _pending = _pending is null ? change : _pending.Then(change);
            return _pendingRun ??= Task.Run(async () =>
            {
                await Task.Delay(700).ConfigureAwait(false);
                LucidSettings.Change next;
                lock (PendingGate) { next = _pending!; _pending = null; _pendingRun = null; }
                await ChangeGate.WaitAsync().ConfigureAwait(false);
                try { return await WriteAsync(lucid, next).ConfigureAwait(false); }
                finally { ChangeGate.Release(); }
            });
        }
    }

    private static readonly object FollowGate = new();
    private static string? _seenOutput;

    /// <summary>
    /// Lucid's headphone follows the headset Windows plays on: on a PC where none was ever picked, and, with
    /// <see cref="AppSettings.LucidFollowHeadset"/> on, each time Windows moves to a headset Lucid has exactly one tuning
    /// for (<see cref="LucidCatalog.Pick"/>). Like every change it waits for a game to end. True when a pick went in.
    /// </summary>
    internal static async Task<bool> FollowAsync(AppSettings settings)
    {
        if (HostMode.IsDisposable || Locate(settings) is not { } lucid || LucidSettings.Load(LucidSettings.FilePath) is not { } root) return false;
        if (Held is { } held) LucidSettings.Apply(root, held, id => HeadphoneName(lucid, id), LucidSettings.WindowsNightLight);
        var output = LucidOutput.Read()?.Playing;
        bool moved;
        lock (FollowGate)
        {
            moved = settings.LucidFollowHeadset && _seenOutput is not null && output is not null &&
                !string.Equals(_seenOutput, output, StringComparison.OrdinalIgnoreCase);
            if (output is not null) _seenOutput = output;
        }
        if (LucidCatalog.Pick(LucidSettings.Read(root), output, moved, LucidCatalog.Read(lucid)) is not { } pick) return false;
        if (await ChangeAsync(lucid, new LucidSettings.Change { Headphone = pick.Id }).ConfigureAwait(false) is { } error)
        {
            AppLog.Warn("Lucid: the headset's tuning didn't go in: " + error);
            return false;
        }
        AppLog.Info(moved ? "Lucid: Windows moved to another headset; its tuning went in." : "Lucid: picked the headset Windows plays on, as nothing was picked yet.");
        return true;
    }

    // Beside Exo's settings, as a change that waits has to outlast Exo closing: the page shows it as already made.
    private static string HeldFile => System.IO.Path.Combine(PathHelper.AppDataDir, "lucid-held.json");
    private static readonly TimeSpan HeldLook = TimeSpan.FromSeconds(5);
    private static Timer? _heldWatch;

    /// <summary>
    /// Keeps the change until no game is playing. It is looked for rather than told: not every game Exo counts as playing
    /// ends through the launcher, and one that did could still count at that moment, which left the change waiting for good.
    /// The caller holds PendingGate.
    /// </summary>
    private static void Hold(string lucid, LucidSettings.Change change)
    {
        _held = change;
        _heldWatch ??= new Timer(_ => ReleaseAfterGames(lucid), null, HeldLook, HeldLook);
        try { File.WriteAllText(HeldFile, LucidSettings.Stored(change)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Lucid: the change that waits wasn't kept: " + ex.Message); }
    }

    private static void ReleaseAfterGames(string lucid)
    {
        // On a timer's thread, where anything thrown would end Exo.
        try { if (!Playing()) ReleaseHeld(lucid); }
        catch (Exception ex) { AppLog.Warn("Lucid: looking for the game's end failed: " + ex.GetType().Name); }
    }

    /// <summary>No game is playing: the change that waited for one goes in now, and its file goes once it has.</summary>
    private static void ReleaseHeld(string lucid)
    {
        LucidSettings.Change? held;
        lock (PendingGate)
        {
            held = _held;
            _held = null;
            _heldWatch?.Dispose();
            _heldWatch = null;
        }
        if (held is null) return;
        AppLog.Info("Lucid: the game ended; the change that waited goes in.");
        _ = Task.Run(async () =>
        {
            if (await ChangeAsync(lucid, held).ConfigureAwait(false) is { } error)
            {
                AppLog.Warn("Lucid: the change that waited didn't go in, so Exo's next start tries again: " + error);
                return;
            }
            lock (PendingGate)
            {
                if (_held is not null) return;
                try { DeletionGate.DeleteFile(HeldFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Lucid: the change that waited wasn't let go of: " + ex.Message); }
            }
        });
    }

    /// <summary>Exo started: a change still waiting from its last run waits on, and goes in when no game is playing.</summary>
    internal static void RestoreHeld(AppSettings settings)
    {
        try
        {
            if (!File.Exists(HeldFile) || LucidSettings.FromStored(File.ReadAllText(HeldFile)) is not { } held) return;
            if (Locate(settings) is not { } lucid) return;
            AppLog.Info("Lucid: a change that waited through Exo closing waits on.");
            lock (PendingGate) Hold(lucid, _held is null ? held : held.Then(_held));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Lucid: the change that waited couldn't be read: " + ex.Message); }
    }

    private static async Task<string?> WriteAsync(string lucid, LucidSettings.Change change)
    {
        var wasRunning = Running();
        if (wasRunning && !await CloseAsync("a settings change").ConfigureAwait(false))
            return WontClose;
        string? error;
        try
        {
            // Read after Lucid closed: it saves its own copy as it goes.
            if (LucidSettings.Load(LucidSettings.FilePath) is not { } root) error = "Set up Lucid first.";
            else if ((error = LucidSettings.Apply(root, change, id => HeadphoneName(lucid, id), LucidSettings.WindowsNightLight)) is null)
            {
                LucidSettings.Save(LucidSettings.FilePath, root);
                AppLog.Info("Lucid: settings changed.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn("Lucid: settings not saved: " + ex.Message);
            error = "Lucid's settings couldn't be saved. Try again.";
        }
        if ((wasRunning || change.TakeOver) && !Start(lucid)) error ??= "Lucid didn't start again. Restart Exo.";
        return error;
    }

    private const string WontClose = "Lucid didn't close. Restart your PC, then try again.";

    internal static string? HeadphoneName(string lucid, string id) =>
        LucidCatalog.Read(lucid)?.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase)) is { } headphone &&
        headphone.Id != LucidSettings.LucidIems ? headphone.Name : null;

    /// <summary>
    /// Closes Lucid for a settings change: the one in Exo's folder as <see cref="StopHostedAsync"/> does; a separate copy
    /// by asking only, through the Restart Manager: Windows sends it the sign-out close (WM_QUERYENDSESSION with
    /// ENDSESSION_CLOSEAPP), which WPF answers by shutting down normally; its window's close button would only hide it
    /// to the tray. Without RmForceShutdown nothing is ever killed: a separate copy that refuses keeps running. One that
    /// runs as administrator can't be asked (Windows keeps Exo's messages from it): Lucid's own updater leaves it that
    /// way until the next sign-in.
    /// </summary>
    private static async Task<bool> CloseAsync(string reason)
    {
        await StopHostedAsync().ConfigureAwait(false);
        var processes = RunningLucid();
        try
        {
            var lucid = processes.Select(p => p.Process).ToArray();
            if (lucid.Length == 0) return !Running();
            var rc = RmShutdown(lucid);
            if (rc != 0) AppLog.Warn($"Lucid: Restart Manager didn't close it ({rc}).");
            for (var i = 0; i < 50 && Running(); i++) await Task.Delay(100).ConfigureAwait(false);
            var closed = !Running();
            if (closed) AppLog.Info($"Lucid: closed for {reason}.");
            return closed;
        }
        finally { foreach (var (process, _) in processes) process.Dispose(); }
    }

    /// <summary>Windows' side of <see cref="LucidStop"/>: the Lucid processes it lists, the Restart Manager's ask, and the last-resort end.</summary>
    private sealed class SystemLucidProcesses : ILucidProcesses
    {
        public IReadOnlyList<LucidProcess> Running()
        {
            var running = RunningLucid();
            try
            {
                var list = new List<LucidProcess>();
                foreach (var (process, file) in running)
                {
                    try { list.Add(new LucidProcess(process.Id, process.StartTime.ToFileTime(), file)); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                return list;
            }
            finally { foreach (var (process, _) in running) process.Dispose(); }
        }

        public bool AskToClose(IReadOnlyList<LucidProcess> which)
        {
            var processes = new List<Process>();
            try
            {
                foreach (var one in which)
                    if (Open(one) is { } process) processes.Add(process);
                if (processes.Count == 0) return true;
                var rc = RmShutdown([.. processes]);
                if (rc != 0) AppLog.Warn($"Lucid: Restart Manager didn't close it ({rc}).");
                return rc == 0;
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        public bool End(LucidProcess which)
        {
            using var process = Open(which);
            if (process is null) return true; // gone, or no longer this process
            try
            {
                // The exe is read again from the process in hand: its id could have gone to another program since the list.
                return process.MainModule?.FileName is { } file && Same(file, which.File) && IsHosted(file)
                    && ProcessTermination.Kill(process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }

        public Task Wait(TimeSpan time) => Task.Delay(time);

        // The process only if it is still the one listed (same id and start time); null when it is gone.
        private static Process? Open(LucidProcess which)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(which.Id);
                if (process.StartTime.ToFileTime() == which.Started) return process;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process?.Dispose();
            return null;
        }
    }

    private static int RmShutdown(Process[] processes)
    {
        var rc = RmStartSession(out var session, 0, new StringBuilder(64));
        if (rc != 0) return rc;
        try
        {
            var unique = processes.Select(p =>
            {
                var time = p.StartTime.ToFileTime();
                return new RmUniqueProcess { ProcessId = p.Id, StartTime = new FILETIME { dwLowDateTime = (int)(time & 0xffffffff), dwHighDateTime = (int)(time >> 32) } };
            }).ToArray();
            rc = RmRegisterResources(session, 0, null, (uint)unique.Length, unique, 0, null);
            return rc != 0 ? rc : RmShutdownSession(session, 0, IntPtr.Zero);
        }
        finally { _ = RmEndSession(session); }
    }

    private static readonly Lock TrayGate = new();
    private static Timer? _trayWatch;
    private static bool _trayLogged;
    // The Lucid whose icon is gone, and the taskbar it was gone from: while both stay, the look costs no process list.
    private static Process[] _trayHiddenFrom = [];
    private static nint _trayHiddenIn;

    /// <summary>
    /// While Start with Exo is on, Lucid runs out of sight like the apps Exo hosts: its tray icon is removed with the
    /// remover Exo uses for store clients' (<see cref="TrayIconRemover"/>), looked at just after each start (<see cref="Start"/>),
    /// since Lucid adds a new one then, and every 30 s for when Explorer restarts (a new taskbar window; Lucid answers its TaskbarCreated). While
    /// the same Lucid runs under the same taskbar a look is two cheap checks, and none is made while a game runs.
    /// Off (or Exo closing) gives the icon back: Lucid's tray library (Hardcodet's TaskbarIcon)
    /// adds it again when its message window gets Windows' TaskbarCreated, the message Explorer sends after a restart.
    /// Started with <c>/startup</c>, Lucid shows no window of its own: only the one Repair asks for from Exo, and its close
    /// button hides it again (MainWindow.OnClosing: MinimizeToTray).
    /// </summary>
    internal static void HideTray(bool on)
    {
        lock (TrayGate)
        {
            if (on) { _trayWatch ??= new Timer(_ => RemoveTrayIcon(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30)); return; }
            if (_trayWatch is null) return;
            _trayWatch.Dispose();
            _trayWatch = null;
            _trayLogged = false;
            ForgetHidden();
        }
        ShowTrayIcon();
    }

    private static void RemoveTrayIcon()
    {
        if (GameRunningSignal.Running) return;
        lock (TrayGate)
        {
            if (_trayWatch is null) return;
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (_trayHiddenFrom.Length > 0 && taskbar == _trayHiddenIn && Array.TrueForAll(_trayHiddenFrom, Alive)) return;
            ForgetHidden();
            // The cheap look first: no Lucid, no process list.
            if (!Running()) return;
            var running = RunningLucid();
            var keep = false;
            try
            {
                if (running.Count == 0) return;
                var removed = TrayIconRemover.Remove(TrayIcons.Read(running[0].File), running.Select(p => p.Process.Id).ToArray());
                if (removed == 0) return;
                if (!_trayLogged) { _trayLogged = true; AppLog.Info("Lucid tray icon removed."); }
                // Each process's own handle, held from now on, answers whether it is still this Lucid (a reused id can't).
                foreach (var (process, _) in running) _ = process.SafeHandle;
                _trayHiddenFrom = running.Select(p => p.Process).ToArray();
                _trayHiddenIn = taskbar;
                keep = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { ForgetHidden(); }
            finally { if (!keep) foreach (var (process, _) in running) process.Dispose(); }
        }

        static bool Alive(Process process)
        {
            try { return !process.HasExited; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    private static void ForgetHidden()
    {
        foreach (var process in _trayHiddenFrom) process.Dispose();
        _trayHiddenFrom = [];
        _trayHiddenIn = 0;
    }

    private static void ShowTrayIcon()
    {
        var running = RunningLucid();
        var pids = running.Select(p => (uint)p.Process.Id).ToHashSet();
        foreach (var (process, _) in running) process.Dispose();
        if (pids.Count == 0) return;
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid)) PostMessage(hwnd, taskbarCreated, 0, 0);
            return true;
        }, 0);
        AppLog.Info("Lucid tray icon given back.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public FILETIME StartTime;
    }

    // The process's first visible top-level window, or 0.
    private static nint WindowOf(int processId)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var owner) == 0 || owner != (uint)processId) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[]? fileNames, uint applications, RmUniqueProcess[] processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll", EntryPoint = "RmShutdown")]
    private static extern int RmShutdownSession(uint session, uint flags, IntPtr progress);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
