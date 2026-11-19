using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Exo's clock for <see cref="VersionWatch"/>: a few minutes after Exo starts, then every half hour, while nothing plays
/// (no game, no Discord call, no shader compile), it asks for new versions and posts each one found for the first time
/// (versions.new), which the page shows as a notice that opens where its update is. Exo itself is asked once, as it opens
/// (AppUpdateService.OpenCheck): a release that comes out while it runs waits for the next open. Nothing installs without
/// you.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly TimeSpan VersionWatchEvery = TimeSpan.FromMinutes(30);
    private System.Threading.Timer? _versionWatch;
    private int _versionWatching;

    private void WatchVersions() =>
        _versionWatch = new System.Threading.Timer(_ => _ = VersionWatchTickAsync(), null, TimeSpan.FromMinutes(3), VersionWatchEvery);

    private async Task VersionWatchTickAsync()
    {
        if (_detached)
        {
            Interlocked.Exchange(ref _versionWatch, null)?.Dispose();
            return;
        }
        if (Interlocked.Exchange(ref _versionWatching, 1) == 1) return;
        try
        {
            if (GpuDriverUpdate.Busy() is not null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            foreach (var found in await VersionWatch.FindAsync(_services.Settings.Current, timeout.Token).ConfigureAwait(false))
            {
                if (!VersionWatch.FirstTime(found)) continue;
                AppLog.Info($"Versions: {found.Name} {found.Version} is out (installed {found.Installed ?? "unknown"}).");
                PostEvent("versions.new", new { id = found.Id, name = found.Name, version = found.Version, installed = found.Installed, page = found.Page });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
        {
            AppLog.Info("Versions: the check didn't finish: " + ex.Message);
        }
        finally { Volatile.Write(ref _versionWatching, 0); }
    }
}
