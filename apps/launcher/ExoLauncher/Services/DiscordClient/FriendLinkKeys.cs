using System.Security.Cryptography;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Each Discord account's friend link key on this PC: P-256, made the first time it is wanted, DPAPI-wrapped
/// (<see cref="ExoDpapi"/>) in a file only this Windows user can read, in native code only; its public half is all that
/// ever leaves. One that can't be read (restored from another PC, damaged) is replaced: friends' Exo apps pick up the
/// new public half from the presence and derive a new topic. Losing it costs nothing, since it protects nothing stored.
/// </summary>
internal static class FriendLinkKeys
{
    private static readonly Lock Gate = new();

    private static string PathOf(string accountId) => Path.Combine(PathHelper.AppDataDir, "friend-link-" + accountId + ".bin");

    /// <summary>This account's key, or null when it could neither be read nor saved: no key, no link.</summary>
    internal static ECDiffieHellman? ForAccount(string accountId)
    {
        if (!accountId.All(char.IsAsciiDigit)) return null;
        lock (Gate)
        {
            var path = PathOf(accountId);
            if (Read(path) is { } kept) return kept;
            var fresh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            if (Save(path, fresh)) return fresh;
            fresh.Dispose();
            return null;
        }
    }

    /// <summary>Sign-out: the key goes with the account.</summary>
    internal static void Forget(string accountId)
    {
        if (!accountId.All(char.IsAsciiDigit)) return;
        lock (Gate)
        {
            try { DeletionGate.DeleteFile(PathOf(accountId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Info("Friend link: key not removed (" + ex.GetType().Name + ")."); }
        }
    }

    private static ECDiffieHellman? Read(string path)
    {
        byte[]? plain = null;
        try
        {
            if (!File.Exists(path)) return null;
            plain = ExoDpapi.Unprotect(File.ReadAllBytes(path));
            var key = ECDiffieHellman.Create();
            key.ImportPkcs8PrivateKey(plain, out _);
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            AppLog.Info("Friend link: key not read (" + ex.GetType().Name + "); making a new one.");
            return null;
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private static bool Save(string path, ECDiffieHellman key)
    {
        var plain = key.ExportPkcs8PrivateKey();
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, ExoDpapi.Protect(plain));
            try { SecretFileAcl.RestrictToCurrentUser(tmp); }
            catch (Exception aclEx) { AppLog.Debug("Friend link key ACL: " + aclEx.GetType().Name); }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            AppLog.Warn("Friend link: key not saved (" + ex.GetType().Name + ").");
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
