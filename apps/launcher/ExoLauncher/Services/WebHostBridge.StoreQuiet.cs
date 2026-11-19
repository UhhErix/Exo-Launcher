using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The one switch for the store clients' quiet (storeClientsQuiet in the page's settings, on by default). Switching it
/// off puts Steam's and Epic's values back as they were; switching it on applies them again. Steam's change goes through the one Steam pass (which waits for Steam to close, never closing it), Epic's through
/// <see cref="EpicQuietSettings"/>. Neither writes anything the player did not keep: docs/architecture/store-quiet.md.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>Whether Epic's quiet is wanted: the switch is on and its start with Windows was handed to Exo.</summary>
    internal bool EpicQuietWanted()
    {
        var settings = _services.Settings.Current;
        return settings.StoreClientsQuiet && settings.StartupHandedToExo.Contains("epic");
    }

    /// <summary>Settings: storeClientsQuiet; a change goes to both clients at once.</summary>
    private void ApplyStoreQuietSetting(JsonElement p)
    {
        if (!p.TryGetProperty("storeClientsQuiet", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        var quiet = value.GetBoolean();
        if (quiet == _services.Settings.Current.StoreClientsQuiet) return;
        _services.Settings.ApplyStoreQuiet(quiet);
        if (HostMode.IsDisposable) return;
        _ = Task.Run(() => EpicQuietSettings.HandToExo(EpicQuietWanted()));
        _ = Task.Run(async () =>
        {
            try { await ApplySteamOverlayAsync(_bridgeLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Warn($"Steam: quiet not changed: {ex.Message}");
            }
        });
    }
}
