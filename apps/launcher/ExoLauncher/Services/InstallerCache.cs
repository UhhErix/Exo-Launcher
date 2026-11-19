using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Windows Installer's cache (%WINDIR%\Installer): a copy of every installed MSI and patch, which Windows needs to repair,
/// change or remove it. A product removed badly leaves its copy behind, and nothing ever deletes it. A file there is
/// orphaned only when Windows Installer registers it for nothing: not the LocalPackage of any product or patch, read both
/// through Windows Installer's own API (MsiEnumProductsEx, MsiEnumPatchesEx: this user's and the machine's, all a user
/// without elevation may ask) and from its registration under UserData (every user's and the machine's; one unreadable
/// key and nothing is offered). Only the .msi and .msp files right in the folder (never $PatchCache$ or a product's icon
/// folder), none written in the last week (an install may be under way), and nothing at all when either read fails or
/// finds no product, since an empty read would make every copy look orphaned. They go to the Recycle Bin under one Windows
/// prompt, so Put back brings them back.
/// </summary>
internal static class InstallerCache
{
    internal sealed record CachedFile(string Path, long Bytes, DateTime WrittenUtc);

    internal static readonly TimeSpan Young = TimeSpan.FromDays(7);

    /// <summary>The cache's files no product or patch is registered with; none when <paramref name="registered"/> is null or empty (its read failed).</summary>
    internal static List<CachedFile> Orphans(string folder, IEnumerable<CachedFile> files, IReadOnlySet<string>? registered, DateTime nowUtc)
    {
        if (registered is null || registered.Count == 0) return [];
        var top = folder.TrimEnd('\\');
        return files.Where(file =>
                string.Equals(Path.GetDirectoryName(file.Path)?.TrimEnd('\\'), top, StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(file.Path) is var ext && (ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msp", StringComparison.OrdinalIgnoreCase)) &&
                !file.Path.Any(char.IsControl) &&
                nowUtc - file.WrittenUtc > Young &&
                !registered.Contains(file.Path))
            .ToList();
    }

    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer");

    /// <summary>The orphaned copies on this PC; empty when anything needed to be sure couldn't be read.</summary>
    internal static List<CachedFile> Find()
    {
        var folder = Folder;
        if (!Directory.Exists(folder)) return [];
        var api = FromApi();
        var registry = FromRegistry();
        if (api is null || registry is null || api.Count == 0) return [];
        var registered = new HashSet<string>(api, StringComparer.OrdinalIgnoreCase);
        registered.UnionWith(registry);
        var files = new List<CachedFile>();
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                files.Add(new(info.FullName, info.Length, info.LastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return Orphans(folder, files, registered, DateTime.UtcNow);
    }

    /// <summary>What a clear did: how many went and how big they were, its Put back record, how many stayed, and whether Windows' prompt was declined.</summary>
    internal sealed record Cleared(int Files, long Bytes, string? Undo, int Left, bool Declined);

    internal const string RecordName = "Windows Installer files";

    /// <summary>
    /// The orphaned copies, found again now (never what the page says), to the Recycle Bin under one Windows prompt; one the
    /// bin wouldn't keep stays. What went is written down so Put back moves it back.
    /// </summary>
    internal static async Task<Cleared> ClearAsync(Func<IReadOnlyList<string>, Task<bool>> admin, RemovalHistory history)
    {
        if (HostMode.IsDisposable) return new(0, 0, null, 0, false);
        var found = await Task.Run(Find).ConfigureAwait(false);
        var going = found.Where(file => LeftoverScan.RecycleBinRefuses(file.Path) is null).ToList();
        var steps = LeftoverScan.RecycleSteps(going.Select(file => file.Path));
        if (steps.Count == 0) return new(0, 0, null, found.Count, false);
        if (!await admin(steps).ConfigureAwait(false)) return new(0, 0, null, found.Count, true);
        var gone = going.Where(file => !File.Exists(file.Path)).ToList();
        var bytes = gone.Sum(file => file.Bytes);
        var undo = gone.Count == 0 ? null : history.Add(RecordName, new(0, gone.Count, 0, 0, bytes), gone.Select(file => new RemovedPath(file.Path, Machine: true)), [], [], DateTime.UtcNow);
        AppLog.Info($"Installer cache: {gone.Count} orphaned files ({bytes / 1_000_000} MB) to the Recycle Bin; {found.Count - gone.Count} stayed.");
        return new(gone.Count, bytes, undo, found.Count - gone.Count, false);
    }

    // ---- what Windows Installer registers ----

    private const uint AllContexts = 7; // MSIINSTALLCONTEXT_USERMANAGED | USERUNMANAGED | MACHINE
    private const uint AllPatchStates = 15; // applied, superseded, obsoleted, registered
    private const int NoMoreItems = 259, MoreData = 234, Success = 0;

    /// <summary>Every product's and patch's LocalPackage, from Windows Installer itself; null when it couldn't be asked.</summary>
    private static List<string>? FromApi()
    {
        try
        {
            var found = new List<string>();
            for (uint index = 0; ; index++)
            {
                var product = new StringBuilder(39);
                var sid = new StringBuilder(256);
                var sidLength = (uint)sid.Capacity;
                var result = MsiEnumProductsEx(null, null, AllContexts, index, product, out var context, sid, ref sidLength);
                if (result == NoMoreItems) break;
                if (result != Success) return null;
                if (Info(product.ToString(), context, sid.ToString(), patch: null) is { } package) found.Add(package);
            }
            for (uint index = 0; ; index++)
            {
                var patch = new StringBuilder(39);
                var product = new StringBuilder(39);
                var sid = new StringBuilder(256);
                var sidLength = (uint)sid.Capacity;
                var result = MsiEnumPatchesEx(null, null, AllContexts, AllPatchStates, index, patch, product, out var context, sid, ref sidLength);
                if (result == NoMoreItems) break;
                if (result != Success) return null;
                if (Info(product.ToString(), context, sid.ToString(), patch.ToString()) is { } package) found.Add(package);
            }
            return found;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Debug("Installer cache: Windows Installer not asked: " + ex.GetType().Name);
            return null;
        }

        static string? Info(string product, uint context, string sid, string? patch)
        {
            var user = context == 4 ? null : sid.Length > 0 ? sid : null;
            uint length = 512;
            var value = new StringBuilder((int)length);
            var result = patch is null
                ? MsiGetProductInfoEx(product, user, context, "LocalPackage", value, ref length)
                : MsiGetPatchInfoEx(patch, product, user, context, "LocalPackage", value, ref length);
            if (result == MoreData)
            {
                value = new StringBuilder((int)++length);
                result = patch is null
                    ? MsiGetProductInfoEx(product, user, context, "LocalPackage", value, ref length)
                    : MsiGetPatchInfoEx(patch, product, user, context, "LocalPackage", value, ref length);
            }
            return result == Success && value.Length > 0 ? value.ToString() : null;
        }
    }

    /// <summary>Every LocalPackage under Windows Installer's UserData, for each user and the machine; null when it couldn't be read.</summary>
    private static List<string>? FromRegistry()
    {
        try
        {
            using var userData = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
            if (userData is null) return null;
            var found = new List<string>();
            foreach (var sid in userData.GetSubKeyNames())
            {
                using var products = userData.OpenSubKey(sid + @"\Products");
                foreach (var product in products?.GetSubKeyNames() ?? [])
                {
                    using var properties = products!.OpenSubKey(product + @"\InstallProperties");
                    if (properties?.GetValue("LocalPackage") is string package) found.Add(package);
                }
                using var patches = userData.OpenSubKey(sid + @"\Patches");
                foreach (var patch in patches?.GetSubKeyNames() ?? [])
                {
                    using var key = patches!.OpenSubKey(patch);
                    if (key?.GetValue("LocalPackage") is string package) found.Add(package);
                }
            }
            return found;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Installer cache: registration unreadable: " + ex.GetType().Name);
            return null;
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiEnumProductsEx(string? productCode, string? userSid, uint context, uint index, StringBuilder installedProductCode,
        out uint installedContext, StringBuilder sid, ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiEnumPatchesEx(string? productCode, string? userSid, uint context, uint filter, uint index, StringBuilder patchCode,
        StringBuilder targetProductCode, out uint targetContext, StringBuilder targetSid, ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetProductInfoEx(string productCode, string? userSid, uint context, string property, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetPatchInfoEx(string patchCode, string productCode, string? userSid, uint context, string property, StringBuilder value, ref uint length);
}
