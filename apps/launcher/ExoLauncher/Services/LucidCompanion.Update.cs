using System.Diagnostics;
using System.Net.Http.Headers;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Lucid's updates on this PC (<see cref="LucidUpdate"/> has the rules): a look every half hour that costs a few checks
/// while Lucid is off or a game or call is on, and one request to Exo's cache of Lucid's release list a day otherwise. The row says nothing of an update
/// unless it failed (<see cref="UpdateNote"/>); the version it shows is the one running.
/// </summary>
internal static partial class LucidCompanion
{
    /// <summary>
    /// The owner's switch for Lucid's updates, docs/architecture/lucid-inside.md › Decision. False until Lucid's author has agreed
    /// that Exo may fetch his releases: with it off, no timer starts, no request is made to the cache, and the row shows nothing of
    /// an update. Turning it on needs the "Lucid's updates" row back in PRIVACY.md and its four hosts in NetworkDestinations
    /// (<c>LucidUpdateTests.UpdatesAreOffUntilPrivacyNamesThem</c> fails until both are there). It is a readonly field, not a
    /// const, so the compiler keeps the code behind it (a const false made it unreachable, CS0162) and the switch stays one line.
    /// </summary>
    internal static readonly bool UpdatesAvailable = false;

    private static Timer? _updateTimer;
    private static int _looking;
    private static volatile bool _closing;

    /// <summary>Starts looking for a newer Lucid: 2 minutes after Exo starts, then every half hour.</summary>
    internal static void WatchUpdates(SettingsService settings)
    {
        if (!UpdatesAvailable) return;
        lock (TrayGate)
            _updateTimer ??= new Timer(_ => _ = LookForUpdateAsync(settings), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
    }

    /// <summary>Exo is closing: no new look, and none already begun starts Lucid again after it was let go.</summary>
    internal static void StopWatchingUpdates()
    {
        _closing = true;
        lock (TrayGate) { _updateTimer?.Dispose(); _updateTimer = null; }
    }

    /// <summary>What the last update failed at, in words for the Lucid row; null when nothing is wrong.</summary>
    internal static string? UpdateNote() => UpdatesAvailable && Store.Load().Note is { Length: > 0 } note ? note : null;

    private static async Task LookForUpdateAsync(SettingsService settings)
    {
        if (!UpdatesAvailable) return;
        if (Interlocked.Exchange(ref _looking, 1) != 0) return;
        try { await LucidUpdate.RunAsync(Store, UpdateSteps(settings), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("Lucid: the update look failed: " + ex.GetType().Name + ": " + ex.Message); }
        finally { Interlocked.Exchange(ref _looking, 0); }
    }

    private static LucidUpdateSteps UpdateSteps(SettingsService settings) => new(
        () => DateTime.UtcNow,
        () => settings.Current.StartLucidWithExo && Ready() && Locate(settings.Current) is { } path && IsHosted(path),
        () => _closing || Playing() || GameRunningSignal.Running || DiscordClient.DiscordSession.Current?.InCall == true,
        FetchReleaseAsync,
        (offer, destination, ct) => DependencyService.DownloadToAsync(offer.Url, destination, null, ct, Store.Folder, offer.Size, NetworkDestinations.LucidUpdates),
        Inspect,
        Hosted,
        Running,
        async () => (await StopHostedAsync().ConfigureAwait(false)).Result.Left == 0,
        Start,
        StartedAsync,
        () => ChangeGate.WaitAsync(),
        () => ChangeGate.Release())
    {
        Pinned = LucidUpdate.Pins.Count > 0,
    };

    private static LucidFile Inspect(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return new LucidFile(Version.TryParse(info.FileVersion, out var version) ? version : null, info.ProductName, info.CompanyName, AuthenticodeVerifier.Publisher(exe));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new LucidFile(null, null, null, null); }
    }

    // One request, to Exo's cache of the release list of Lucid's own repository: no redirect followed, and only a small JSON answer read.
    private static async Task<string?> FetchReleaseAsync(CancellationToken ct)
    {
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(20), new SocketsHttpHandler { AllowAutoRedirect = false });
        http.MaxResponseContentBufferSize = 256 * 1024;
        using var request = new HttpRequestMessage(HttpMethod.Get, LucidUpdate.ReleaseUrl);
        request.Headers.UserAgent.ParseAdd("ExoLauncher");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            AppLog.Info($"Lucid: the release list answered {(int)response.StatusCode}.");
            return null;
        }
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // Started with /startup, Lucid holds its single-instance mutex within a second; 20 s covers a slow PC.
    private static async Task<bool> StartedAsync()
    {
        for (var tries = 0; tries < 100; tries++)
        {
            if (Running()) return true;
            await Task.Delay(200).ConfigureAwait(false);
        }
        return false;
    }
}
