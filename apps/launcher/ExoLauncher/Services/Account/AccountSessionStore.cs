using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Services.Account;

/// <summary>
/// The signed-in account on this PC: the session token, the device token (CLIENT.md 3), the account id and the email as
/// typed. <see cref="CodePending"/>: a recovery code was issued (sign-up, reset, a new code) and the player has not yet
/// confirmed it; while it is set the account sheet stays up, and after a restart it makes a new code to show.
/// </summary>
internal sealed record AccountSession(string Token, string AccountId, string Email, string? DeviceToken = null, bool CodePending = false);

/// <summary>
/// The session token file (CLIENT.md section 6): DPAPI CurrentUser through <see cref="ExoDpapi"/>, written atomically, and
/// never in settings, logs or a plain backup. A damaged or foreign file reads as signed out and is kept, so local data is
/// never deleted over it. Every write, removal and read-then-change takes one lock, so a sign-in that lands while an old
/// session is being ended is never deleted with it.
/// </summary>
internal static class AccountSessionStore
{
    internal const string FileName = "account-session.bin";
    /// <summary>A plain marker (not secret): the service ended this PC's last session, so the sheet says why it asks.</summary>
    internal const string EndedFileName = "account-session-ended";

    private static readonly object Gate = new();

    internal static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>
    /// The file in <c>dataDir</c> was written or removed: a sign-in, a sign-out, a deleted account, a reset, a confirmed
    /// code, or a session the service ended (<see cref="EndIfCurrent"/>). The bridge tells the page (exoAccount.changed),
    /// so every screen reads one state. Not raised when nothing changed on disk.
    /// </summary>
    internal static event Action<string>? Changed;

    /// <summary>Test seam: runs inside <see cref="EndIfCurrent"/> between its token check and the delete, under the lock.</summary>
    internal static Action? BetweenCheckAndDelete { get; set; }

    internal static void Save(string dataDir, AccountSession session)
    {
        lock (Gate) Write(dataDir, session);
        Changed?.Invoke(dataDir);
    }

    private static void Write(string dataDir, AccountSession session)
    {
        var blob = ExoDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(new Stored(session.Token, session.AccountId, session.Email, session.DeviceToken, session.CodePending)));
        var path = PathIn(dataDir);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, blob);
        File.Move(temp, path, overwrite: true);
        TryDelete(Path.Combine(dataDir, EndedFileName));
    }

    /// <summary>
    /// Changes the saved session in one step under the lock: <paramref name="change"/> gets the session as it is now and
    /// returns the one to write, or null to leave the file as it is. True when it wrote.
    /// </summary>
    internal static bool Update(string dataDir, Func<AccountSession, AccountSession?> change)
    {
        lock (Gate)
        {
            if (Load(dataDir) is not { } current || change(current) is not { } next || next == current) return false;
            Write(dataDir, next);
        }
        Changed?.Invoke(dataDir);
        return true;
    }

    /// <summary>The saved session, or null when there is none or it cannot be read on this PC. Never deletes.</summary>
    internal static AccountSession? Load(string dataDir)
    {
        var path = PathIn(dataDir);
        if (!File.Exists(path)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(ExoDpapi.Unprotect(File.ReadAllBytes(path)));
            if (stored is null || string.IsNullOrEmpty(stored.Token) || string.IsNullOrEmpty(stored.AccountId)) return null;
            return new AccountSession(stored.Token, stored.AccountId, stored.Email ?? "", stored.DeviceToken, stored.CodePending);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether the service ended this PC's last session (cleared by the next sign-in).</summary>
    internal static bool Ended(string dataDir) => File.Exists(Path.Combine(dataDir, EndedFileName));

    /// <summary>Removes the session file. <see cref="Changed"/> is raised only when a file was there and went.</summary>
    internal static void Clear(string dataDir)
    {
        bool gone;
        lock (Gate) gone = Remove(dataDir);
        if (gone) Changed?.Invoke(dataDir);
    }

    private static bool Remove(string dataDir) => File.Exists(PathIn(dataDir)) && TryDelete(PathIn(dataDir));

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            DeletionGate.DeleteFile(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Exo account: a session file could not be removed: " + e.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// The service answered signed-out to <paramref name="token"/> (revoked from another PC, a password changed or reset
    /// elsewhere, 400 days idle): the file goes, so this PC reads as signed out and the account sheet asks again, instead of
    /// keeping a dead session that syncs nothing. A sign-in since (a newer token in the file) is kept. The sync queue and the
    /// changes waiting for an account stay; the next sign-in to the same account sends them. True when the file went.
    /// </summary>
    internal static bool EndIfCurrent(string dataDir, string token) => RemoveIfCurrent(dataDir, token, ended: true);

    /// <summary>Sign out: removes the file only while it still holds <paramref name="token"/>, so a sign-in since is kept.</summary>
    internal static bool ClearIfCurrent(string dataDir, string token) => RemoveIfCurrent(dataDir, token, ended: false);

    private static bool RemoveIfCurrent(string dataDir, string token, bool ended)
    {
        bool gone;
        lock (Gate)
        {
            if (Load(dataDir)?.Token != token) return false;
            BetweenCheckAndDelete?.Invoke();
            gone = Remove(dataDir);
            if (gone && ended)
            {
                try { File.WriteAllText(Path.Combine(dataDir, EndedFileName), ""); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the sheet still asks, without the reason */ }
            }
        }
        if (!gone) return false;
        if (ended) AppLog.Info("Exo account: the service ended this PC's session; sign-in is asked again.");
        Changed?.Invoke(dataDir);
        return true;
    }

    private sealed record Stored(string Token, string AccountId, string? Email, string? DeviceToken, bool CodePending = false);
}
/// <summary>
/// The device id the service keys a session by (CLIENT.md 4.3): 16 random bytes as 32 lowercase hex, kept in a plain file
/// (it is not a secret), made once and reused on every start.
/// </summary>
internal static class AccountDevice
{
    internal const string FileName = "device-id.txt";
    private static readonly byte[] Salt = "Exo.AccountDevice.v1"u8.ToArray();

    internal static Func<(string MachineGuid, string UserSid)?>? IdentityProvider { get; set; }

    internal static string Id(string dataDir)
    {
        if (PcId() is { } stable) return stable;
        if (ReadLegacyId(dataDir) is { } saved) return saved;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(dataDir, FileName), id);
        return id;
    }

    internal static string? PcId()
    {
        var identity = CurrentMachineIdentity();
        return identity is { } pair ? DerivePcId(pair.MachineGuid, pair.UserSid) : null;
    }

    internal static string DerivePcId(string machineGuid, string userSid)
    {
        var payload = Encoding.UTF8.GetBytes($"{machineGuid.Trim()}|{userSid.Trim()}");
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(Salt, payload, mac);
        return Convert.ToHexStringLower(mac[..16]);
    }

    internal static string? ReadLegacyId(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;
            var saved = File.ReadAllText(path).Trim();
            return IsValid(saved) ? saved : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static (string MachineGuid, string UserSid)? CurrentMachineIdentity()
    {
        if (IdentityProvider is { } provider) return provider();
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(sid)) return null;
            return (guid.Trim(), sid.Trim());
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static bool IsValid(string id) =>
        id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
