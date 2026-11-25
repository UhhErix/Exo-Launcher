using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The Discord sign-in of Exo's own Discord client, kept on this PC DPAPI-wrapped
/// (<see cref="ExoDpapi"/>) in a file only this Windows user can read. It is sent
/// only to Discord's own hosts (<see cref="DiscordRest"/>, <see cref="DiscordSession"/>),
/// never to the UI, never written to settings.json or a log.
/// </summary>
internal static class DiscordToken
{
    private const string FileName = "discord-client.bin";
    private static readonly Lock Gate = new();
    private static bool? _hasToken;
    private static string? _hasTokenPath;

    private static string StorePath => Path.Combine(PathHelper.AppDataDir, FileName);

    internal static bool HasToken()
    {
        lock (Gate)
        {
            var path = StorePath;
            if (_hasToken is { } cached && string.Equals(_hasTokenPath, path, StringComparison.OrdinalIgnoreCase))
                return cached;
            var has = ReadUnlocked(path) is not null;
            _hasToken = has;
            _hasTokenPath = path;
            return has;
        }
    }

    internal static string? Read()
    {
        lock (Gate)
        {
            var path = StorePath;
            var token = ReadUnlocked(path);
            _hasToken = token is not null;
            _hasTokenPath = path;
            return token;
        }
    }

    private static string? ReadUnlocked(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = ExoDpapi.Unprotect(File.ReadAllBytes(path));
            var token = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return token.Length is > 20 and < 200 ? token : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Discord sign-in could not be read: " + ex.GetType().Name);
            return null;
        }
    }

    internal static bool Save(string token)
    {
        lock (Gate)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var blob = ExoDpapi.Protect(bytes);
                var tmp = StorePath + ".tmp";
                File.WriteAllBytes(tmp, blob);
                try { SecretFileAcl.RestrictToCurrentUser(tmp); }
                catch (Exception aclEx) { AppLog.Debug("Discord sign-in ACL: " + aclEx.GetType().Name); }
                File.Move(tmp, StorePath, overwrite: true);
                _hasToken = token.Length is > 20 and < 200;
                _hasTokenPath = StorePath;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Discord sign-in could not be saved: " + ex.GetType().Name);
                return false;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            _hasToken = false;
            _hasTokenPath = StorePath;
            try { DeletionGate.DeleteFile(StorePath); }
            catch (Exception ex) { AppLog.Warn("Discord sign-in could not be removed: " + ex.GetType().Name); }
        }
    }

    internal static void ResetCache()
    {
        lock (Gate)
        {
            _hasToken = null;
            _hasTokenPath = null;
        }
    }
}
