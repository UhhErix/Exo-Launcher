using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The move from Lucid (tekkusai's EQ, which Exo hosted from <c>addons\lucid</c> until 3.26) to Exo EQ, once, at the
/// first start of an Exo without it: the Lucid Exo hosted is closed (<see cref="LucidStop"/>: asked, then ended only if it
/// is that exact exe in Exo's folders), a sign-in entry for it goes, Lucid's settings are read into Exo EQ
/// (<see cref="LucidImport"/>), Lucid's EQ block in config.txt is turned off when it was Exo's Lucid that played it (once its
/// headphone's correction is in Exo EQ), and
/// what Exo kept for Lucid is deleted. A Lucid the player runs on their own, from anywhere else, is never closed, and its
/// files (<c>%APPDATA%\LucidEQ</c>) are only read.
/// </summary>
internal static class LucidRetire
{
    internal static string SettingsFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LucidEQ", "settings.json");

    /// <summary>The folders Exo hosted Lucid from: its app folder and the store of a Lucid it downloaded.</summary>
    internal static string[] HostedFolders =>
        [Path.Combine(AppContext.BaseDirectory, "addons", "lucid"), Path.Combine(PathHelper.AppDataDir, "addons", "lucid-current")];

    /// <summary>Whether Exo hosted Lucid (its Start with Exo was on), read from settings an earlier Exo wrote.</summary>
    internal static Func<bool> Hosted { get; set; } = () => false;

    /// <summary>The library's games (id, title), for Lucid's per-game EQ.</summary>
    internal static Func<IReadOnlyList<(string Id, string Title)>> Games { get; set; } = () => [];

    internal static void Once()
    {
        if (ExoEq.Current.LucidImported || HostMode.IsDisposable) return;
        var hosted = Hosted();
        var stopped = StopHosted();
        RemoveSignIn();
        JsonObject? lucid = null;
        try { lucid = File.Exists(SettingsFile) ? JsonNode.Parse(File.ReadAllText(SettingsFile)) as JsonObject : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { AppLog.Warn("Exo EQ: Lucid's settings couldn't be read: " + ex.GetType().Name); }
        string? config = null;
        try { if (EqualizerApo.ConfigFolder() is { } folder && Path.Combine(folder, "config.txt") is var path && File.Exists(path)) config = File.ReadAllText(path, Encoding.Latin1); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var games = Games();
        var playing = EqualizerApo.Playing();
        // Exo's Lucid played that block: once Exo EQ corrects the same headphone, the block goes off, or the sound would be
        // corrected twice. Until then (offline, or a headphone AutoEq doesn't have) it keeps playing what Lucid last wrote.
        var blockOff = lucid is not null && (hosted || stopped) && !SeparateLucidRuns();
        ExoEq.Change(store =>
        {
            if (lucid is not null) LucidImport.Into(store, lucid, config, games, playing);
            else store.LucidImported = true;
            store.LucidBlockOff = blockOff && store.LucidHeadphone is not null;
        });
        foreach (var name in new[] { "lucid-update.json", "lucid-held.json" })
            try { DeletionGate.DeleteFile(Path.Combine(PathHelper.AppDataDir, name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { DeletionGate.DeleteDirectory(Path.Combine(PathHelper.AppDataDir, "addons", "lucid-current"), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        AppLog.Info($"Exo EQ: took over from Lucid (settings {(lucid is null ? "none" : "read")}, Exo's Lucid {(stopped ? "closed" : "not running")}).");
        if (ExoEq.Current.LucidHeadphone is not null) _ = Task.Run(ResolveAsync);
    }

    /// <summary>Lucid's headphone, matched in AutoEq's list and its correction fetched; tried again at the next start if offline.</summary>
    internal static async Task ResolveAsync()
    {
        if (ExoEq.Current.LucidHeadphone is null) return;
        try
        {
            if (await AutoEqCatalog.ListAsync().ConfigureAwait(false) is not { } list) return;
            if (AutoEqCatalog.Named(ExoEq.Current.LucidHeadphone, list) is not { } found)
            {
                ExoEq.Change(store => { LucidImport.Resolve(store, list, _ => null); store.LucidBlockOff = false; });
                AppLog.Info("Exo EQ: Lucid's headphone isn't in AutoEq's list.");
                return;
            }
            if (await AutoEqCatalog.FiltersAsync(found).ConfigureAwait(false) is not { } filters) return;
            var blockOff = false;
            ExoEq.Change(store =>
            {
                if (LucidImport.Resolve(store, list, h => WebHostBridge.CorrectionOf(h, filters))) blockOff = store.LucidBlockOff;
                store.LucidBlockOff = false;
            });
            if (blockOff) ExoEq.LucidOff();
        }
        catch (Exception ex) { AppLog.Warn("Exo EQ: Lucid's headphone wasn't matched: " + ex.GetType().Name); }
    }

    /// <summary>Closes the Lucid that ran from Exo's folders; true when one ran.</summary>
    private static bool StopHosted()
    {
        try
        {
            var result = LucidStop.HostedAsync(new SystemLucidProcesses(), HostedFolders, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (result.Found == 0) return false;
            if (result.Left > 0) AppLog.Warn("Exo EQ: the Lucid from Exo's folder is still running.");
            else AppLog.Info($"Exo EQ: closed the Lucid Exo hosted ({result.Ended} ended).");
            return true;
        }
        catch (Exception ex) { AppLog.Warn("Exo EQ: closing Lucid failed: " + ex.GetType().Name); return false; }
    }

    private static bool SeparateLucidRuns() => new SystemLucidProcesses().Running().Any(p => !LucidStop.IsUnderAny(p.File, HostedFolders));

    /// <summary>A sign-in entry (HKCU Run) that starts a Lucid from Exo's folders; the player's own Lucid's stays.</summary>
    private static void RemoveSignIn()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run is null) return;
            foreach (var name in run.GetValueNames())
            {
                if (run.GetValue(name) is not string command || !LucidStop.IsUnderAny(ExeOf(command), HostedFolders)) continue;
                run.DeleteValue(name, throwOnMissingValue: false);
                using var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
                approved?.DeleteValue(name, throwOnMissingValue: false);
                AppLog.Info("Exo EQ: removed the sign-in entry of the Lucid Exo hosted.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { AppLog.Warn("Exo EQ: Lucid's sign-in entry stayed: " + ex.GetType().Name); }
    }

    /// <summary>The program a Run command starts: its quoted path, or the text up to ".exe".</summary>
    internal static string? ExeOf(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"')) return text.IndexOf('"', 1) is var end and > 1 ? text[1..end] : null;
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }

    /// <summary>Windows' side of <see cref="LucidStop"/>: the Lucid processes it lists, the Restart Manager's ask, and the last-resort end.</summary>
    private sealed class SystemLucidProcesses : ILucidProcesses
    {
        public IReadOnlyList<LucidProcess> Running()
        {
            var list = new List<LucidProcess>();
            foreach (var process in Process.GetProcessesByName("Lucid"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } file) list.Add(new LucidProcess(process.Id, process.StartTime.ToFileTime(), file));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            return list;
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
                if (rc != 0) AppLog.Info($"Exo EQ: the Restart Manager didn't close Lucid ({rc}).");
                return rc == 0;
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        public bool End(LucidProcess which)
        {
            using var process = Open(which);
            if (process is null) return true;
            try
            {
                // The exe is read again from the process in hand: its id could have gone to another program since the list.
                return process.MainModule?.FileName is { } file && string.Equals(file, which.File, StringComparison.OrdinalIgnoreCase)
                    && LucidStop.IsUnderAny(file, HostedFolders) && ProcessTermination.Kill(process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }

        public Task Wait(TimeSpan time) => Task.Delay(time);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public FILETIME StartTime;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[]? fileNames, uint applications, RmUniqueProcess[] processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll", EntryPoint = "RmShutdown")]
    private static extern int RmShutdownSession(uint session, uint flags, IntPtr progress);
}
