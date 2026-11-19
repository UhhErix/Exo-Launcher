using System.ComponentModel;
using System.Diagnostics;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic's own installer of its new per-user launcher (the link launcher.epicgames.com serves; it redirects to Epic's
/// download host). Exo downloads it, keeps it only when the final address is Epic's and it is signed by Epic Games Inc.
/// (<see cref="EpicPublisher"/>), and opens it for its own steps. The installer moves the games over and takes out
/// the old machine-wide launcher itself.
/// </summary>
internal static class EpicNewLauncherInstaller
{
    internal const string SetupUrl = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherSetup-x86_64.exe";

    // Win32 ERROR_CANCELLED: the user declined the elevation prompt.
    private const int ErrorCancelled = 1223;

    /// <summary>The verified installer (its path) or why there is none; FinalAddress is where the download finally came from.</summary>
    internal sealed record Download(string? Path, string? Failure, Uri? FinalAddress = null);

    /// <summary>HTTPS to a host of Epic's own (epicgames.com and its subdomains), with no user info and no other port.</summary>
    internal static bool FromEpic(Uri? uri) =>
        uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } && IsEpicHost(uri.Host);

    private static bool IsEpicHost(string host) =>
        host.Equals("epicgames.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The download is kept only when it came from Epic's host and its signature names Epic Games.</summary>
    internal static bool Trusted(Uri? finalAddress, Func<bool> signedByEpic) => FromEpic(finalAddress) && signedByEpic();

    /// <summary>
    /// The simple name of Epic's signing certificate (CN=Epic Games Inc., O=Epic Games Inc., ...), as Epic's own
    /// EpicGamesLauncherSetup.exe and EpicGamesLauncher.exe are signed.
    /// </summary>
    internal const string EpicPublisher = "Epic Games Inc.";

    /// <summary>A download's signature must carry exactly Epic's publisher name (a valid signature only, no publisher match on a substring).</summary>
    internal static bool SignedByEpic(string setup) => AuthenticodeVerifier.HasPublisher(setup, EpicPublisher);

    /// <summary>Downloads the installer to a temp file and checks it; the file is deleted on every path that does not keep it.</summary>
    internal static async Task<Download> DownloadAsync(CancellationToken ct)
    {
        var setup = Path.Combine(Path.GetTempPath(), "exo-epic-setup-" + Guid.NewGuid().ToString("N") + ".exe");
        var keep = false;
        try
        {
            using var http = ExoHttp.Create(TimeSpan.FromMinutes(10));
            using var response = await http.GetAsync(SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var finalAddress = response.RequestMessage?.RequestUri;
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(setup))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            if (!Trusted(finalAddress, () => SignedByEpic(setup)))
            {
                AppLog.Warn("Epic: the new launcher's installer isn't from Epic or signed by Epic Games; deleted.");
                return new Download(null, "That download wasn't signed by Epic Games, so Exo deleted it.");
            }
            // Its signature must also check against revocation online: when the CA cannot be reached, the installer is not run now.
            if (!AuthenticodeVerifier.RevocationChecked(setup))
            {
                AppLog.Warn("Epic: the new launcher's installer's revocation could not be checked; deleted.");
                return new Download(null, "Couldn't verify Epic's installer right now. Try again.");
            }
            keep = true;
            return new Download(setup, null, finalAddress);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or Win32Exception or TaskCanceledException or UnauthorizedAccessException)
        {
            AppLog.Warn("Epic: its new launcher couldn't be fetched: " + ex.GetType().Name);
            return new Download(null, "Epic's new launcher couldn't be downloaded. Check your connection.");
        }
        finally
        {
            if (!keep) DeleteQuietly(setup);
        }
    }

    /// <summary>True while the installer Exo opened runs, so the Game clients card waits on it and then checks again.</summary>
    internal static volatile bool Running;

    /// <summary>
    /// Opens a verified installer for its own steps; it is deleted once the installer exits. Returns the reason it did not
    /// open (null when it did). The file is deleted on every path that does not hand it to a running installer.
    /// </summary>
    internal static string? Open(Download download, Func<ProcessStartInfo, Process?>? start = null)
    {
        var setup = download.Path!;
        // Revocation is checked again as well as the trust: an installer whose revocation cannot be checked now is not run.
        if (!AuthenticodeVerifier.RevocationChecked(setup))
        {
            AppLog.Warn("Epic: the new launcher's installer's revocation could not be checked before it ran; deleted.");
            DeleteQuietly(setup);
            return "Couldn't verify Epic's installer right now. Try again.";
        }
        return Open(setup, () => Trusted(download.FinalAddress, () => SignedByEpic(setup)), start);
    }

    /// <summary>The trust is checked again here, before anything runs: the file could have changed since its download was checked.</summary>
    internal static string? Open(string setup, Func<bool> trusted, Func<ProcessStartInfo, Process?>? start = null)
    {
        var handedOver = false;
        try
        {
            if (!trusted())
            {
                AppLog.Warn("Epic: the new launcher's installer isn't from Epic or signed by Epic Games anymore; deleted.");
                return "That download wasn't signed by Epic Games, so Exo deleted it.";
            }
            var installer = (start ?? Process.Start)(new ProcessStartInfo(setup) { UseShellExecute = true });
            AppLog.Info("Epic: its new launcher's installer is open.");
            Running = installer is not null;
            if (installer is null) return null;
            handedOver = true;
            _ = Task.Run(() => WaitThenDelete(installer, setup));
            return null;
        }
        catch (Win32Exception ex)
        {
            AppLog.Warn("Epic: its installer didn't open: " + ex.NativeErrorCode);
            return ex.NativeErrorCode == ErrorCancelled ? "The installer was cancelled." : "Epic's installer couldn't open. Try again.";
        }
        finally
        {
            if (!handedOver) DeleteQuietly(setup);
        }
    }

    private static async Task WaitThenDelete(Process installer, string setup)
    {
        try { await installer.WaitForExitAsync().ConfigureAwait(false); }
        finally
        {
            Running = false;
            installer.Dispose();
            DeleteQuietly(setup);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) DeletionGate.DeleteFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
