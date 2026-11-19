using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The player's public Riot ID (Name#Tag), used to fetch tracker.gg hours and to key the Riot ledger. It is public, so it is
/// a plain text file (riot-id.txt) that a backup carries and a new Windows install can read, not a DPAPI blob. An ID an older
/// Exo sealed with DPAPI is read once and written back as plain text.
/// </summary>
internal static class RiotIdStore
{
    internal const string FileName = "riot-id.txt";
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(PathHelper.AppDataDir, FileName);

    internal static string? TryRead()
    {
        var riotId = ReadFile();
        // A test run never reads the Riot client on this PC: the owner's own ID is not an input a test may depend on.
        riotId ??= HostMode.TestRun ? null : RiotIdLocator.TryReadLocal();
        // An ID known here, however it was learned, claims the minutes played before it (RiotLedger.ClaimUnassigned); a repeat claim moves nothing.
        if (Normalize(riotId) is { } known && RiotLedger.AccountKey(known) is { } account) RiotLedger.ClaimUnassigned(account);
        return riotId;
    }

    /// <summary>The ID in the file: plain text, or a DPAPI blob from an older Exo, which is rewritten as plain text.</summary>
    private static string? ReadFile()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath + ".clear-pending")) { Clear(); return null; }
                if (!File.Exists(FilePath)) return null;
                var bytes = File.ReadAllBytes(FilePath);
                if (bytes.Length > 4096) return null;
                if (Normalize(Encoding.UTF8.GetString(bytes)) is { } plain) return plain;
                string? sealedId;
                try { sealedId = Normalize(Encoding.UTF8.GetString(ExoDpapi.Unprotect(bytes))); }
                catch (CryptographicException) { return null; }
                if (sealedId is not null) Write(sealedId);
                return sealedId;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("Riot ID could not be read: " + ex.GetType().Name);
                return null;
            }
        }
    }

    internal static bool Save(string? raw)
    {
        bool saved;
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) { Clear(); return true; }
                if (Normalize(raw) is not { } riotId) return false;
                Write(riotId);
                saved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("Riot ID could not be saved: " + ex.GetType().Name);
                return false;
            }
        }
        // The ID set claims the minutes played before any ID was set (RiotLedger.ClaimUnassigned); a repeat claim moves nothing.
        if (saved && Normalize(raw) is { } id)
        {
            if (RiotLedger.AccountKey(id) is { } account) RiotLedger.ClaimUnassigned(account);
            Account.AccountSyncHooks.RiotIdChosen(id);
        }
        return saved;
    }

    /// <summary>A Riot ID that came from the player's other PC: written here as it is, with no sync back.</summary>
    internal static void ApplySynced(string? raw)
    {
        if (Normalize(raw) is not { } riotId) return;
        lock (Gate)
        {
            if (ReadFile() == riotId) return;
            Write(riotId);
        }
        if (RiotLedger.AccountKey(riotId) is { } account) RiotLedger.ClaimUnassigned(account);
    }

    private static void Write(string riotId)
    {
        Directory.CreateDirectory(PathHelper.AppDataDir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, riotId, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static void Clear()
    {
        if (File.Exists(FilePath)) DeletionGate.DeleteFile(FilePath);
        if (File.Exists(FilePath + ".clear-pending")) DeletionGate.DeleteFile(FilePath + ".clear-pending");
    }

    internal static string? Normalize(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        var hash = trimmed.IndexOf('#');
        if (hash <= 0 || hash >= trimmed.Length - 2) return null;
        var name = trimmed[..hash].Trim();
        var tag = trimmed[(hash + 1)..].Trim();
        if (name.Length is < 1 or > 22 || tag.Length is < 2 or > 8) return null;
        if (name.Contains('#') || tag.Contains('#')) return null;
        return name + "#" + tag;
    }
}
