using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExoLauncher.Helpers;

internal readonly record struct TrayIconRegistration(Guid? Guid, uint? Uid);

/// Windows records every notification-area icon an executable registers under
/// HKCU\Control Panel\NotifyIconSettings. GUID-identified icons (Discord)
/// carry IconGuid; plain hwnd+uID icons carry UID. A program under a known folder is recorded by the folder's id
/// ({7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Steam\steam.exe is Program Files (x86)), so every path is expanded before it
/// is compared; read as written it matched nothing, and Steam's icon and those of the other stores there stayed. The
/// program asked about is expanded the same way: NVIDIA's container is asked about by its entry's own recorded path.
internal static class TrayIcons
{
    /// The program an entry names as a full path, or null when it can't be read.
    private static string? RecordedProgram(string recorded, Func<Guid, string?> knownFolder)
    {
        var path = recorded;
        if (recorded.StartsWith('{') && recorded.IndexOf('}') is var close and > 0)
        {
            if (!Guid.TryParse(recorded[..(close + 1)], out var id) || knownFolder(id) is not { } folder) return null;
            path = folder + recorded[(close + 1)..];
        }
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static string? KnownFolder(Guid id)
    {
        try { return SHGetKnownFolderPath(id, 0, nint.Zero, out var path) == 0 ? path : null; }
        catch { return null; }
    }

    internal static IReadOnlyList<TrayIconRegistration> Select(
        IEnumerable<(string? Executable, string? Guid, int? Uid)> entries, string executable, Func<Guid, string?>? knownFolder = null)
    {
        if (RecordedProgram(executable, knownFolder ?? KnownFolder) is not { } target) return [];

        var icons = new List<TrayIconRegistration>();
        var seen = new HashSet<TrayIconRegistration>();
        foreach (var (candidate, guidText, uid) in entries)
        {
            if (candidate is null) continue;
            if (!string.Equals(RecordedProgram(candidate, knownFolder ?? KnownFolder), target, StringComparison.OrdinalIgnoreCase)) continue;
            TrayIconRegistration icon;
            if (Guid.TryParse(guidText, out var guid)) icon = new TrayIconRegistration(guid, null);
            else if (uid is >= 0) icon = new TrayIconRegistration(null, (uint)uid.Value);
            else continue;
            if (seen.Add(icon)) icons.Add(icon);
        }
        return icons;
    }

    private static readonly NotifyIconEntries KeptEntries = new(ReadAllFromRegistry);

    internal static IReadOnlyList<TrayIconRegistration> Read(string executable)
    {
        // The registry is read once and again only when Windows says the key changed (RegNotifyChangeKeyValue), not on
        // every call: store clients and the GPU container ask about every few seconds.
        if (RecordedProgram(executable, KnownFolder) is not { } target) return [];
        EnsureWatch();
        return Select(KeptEntries.Get(), executable);
    }

    private static List<(string? Executable, string? Guid, int? Uid)>? ReadAllFromRegistry()
    {
        var entries = new List<(string? Executable, string? Guid, int? Uid)>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NotifyKey);
            if (key is null) return null;
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("ExecutablePath") is not string path) continue;
                entries.Add((path, sub.GetValue("IconGuid") as string, sub.GetValue("UID") as int?));
            }
        }
        catch { return null; }
        return entries;
    }

    private const string NotifyKey = @"Control Panel\NotifyIconSettings";
    private static readonly object WatchGate = new();
    private static RegistryKey? s_watchedKey;
    private static ManualResetEvent? s_changed;
    private static bool s_watchFailed;

    private static void EnsureWatch()
    {
        lock (WatchGate)
        {
            if (s_watchedKey is not null || s_watchFailed) return;
            try
            {
                s_watchedKey = Registry.CurrentUser.OpenSubKey(NotifyKey);
                if (s_watchedKey is null) return; // not there yet: nothing cached, the next call tries again
                s_changed = new ManualResetEvent(false);
                Arm();
            }
            catch
            {
                s_watchFailed = true; // no signal: the cache falls back to its age limit
                s_watchedKey?.Dispose();
                s_watchedKey = null;
            }
        }
    }

    private static void Arm()
    {
        const uint ChangeName = 0x1, ChangeLastSet = 0x4;
        s_changed!.Reset();
        if (RegNotifyChangeKeyValue(s_watchedKey!.Handle, true, ChangeName | ChangeLastSet, s_changed.SafeWaitHandle, true) != 0)
        {
            s_watchFailed = true;
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(s_changed, (_, _) =>
        {
            KeptEntries.Changed();
            lock (WatchGate) { if (s_watchedKey is not null) Arm(); }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree, uint filter,
        Microsoft.Win32.SafeHandles.SafeWaitHandle eventHandle, bool asynchronous);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
