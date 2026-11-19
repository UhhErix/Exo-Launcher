using ExoLauncher.Adapters;

namespace ExoLauncher.Services;

/// <summary>
/// The one place Exo's waiting Steam settings go in before Exo starts Steam. Steam writes its settings as it exits, so a
/// value Exo wants while Steam runs waits in the record instead of closing it; the moment Exo is about to start Steam
/// itself, that value can go into the files (Steam is closed, nothing holds them) before Steam reads them. Runs once at
/// a time: two starts at once wait for the first write, not each other's.
/// </summary>
internal static class SteamPendingSettings
{
    private static SemaphoreSlim? _oneAtATime;
    /// <summary>The bridge that owns the record for this run, set as it is built: the adapter starts Steam without one.</summary>
    private static WebHostBridge? _bridge;

    /// <summary>Called once, when the bridge starts: the one that answers for the waiting values.</summary>
    internal static void Attach(WebHostBridge bridge) => _bridge = bridge;

    /// <summary>
    /// Writes what waited before Exo starts Steam. Steam is not running here (every start made is made with it closed),
    /// so the write lands in the files Steam is about to read. A failure never keeps Steam from starting.
    /// </summary>
    internal static async Task BeforeStartAsync(CancellationToken ct)
    {
        var bridge = _bridge;
        if (bridge is null || HostMode.IsDisposable) return;
        var gate = _oneAtATime ??= new SemaphoreSlim(1, 1);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await bridge.ApplyPendingSteamSettingsAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn($"Steam: what waited could not be written before Steam started: {ex.Message}");
        }
        finally { gate.Release(); }
    }
}
