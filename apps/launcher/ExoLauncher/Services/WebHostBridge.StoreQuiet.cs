using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The store clients stay quiet, always: Exo holds Steam's and Epic's notifications, sounds and pop-ups off. Steam's
/// values go through the one Steam pass (which waits for Steam to close, never closing it), Epic's through
/// <see cref="EpicQuietSettings"/>. Neither writes anything the player did not keep: docs/architecture/store-quiet.md.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>Whether Epic's quiet is wanted: its start with Windows was handed to Exo.</summary>
    internal bool EpicQuietWanted() => _services.Settings.Current.StartupHandedToExo.Contains("epic");
}
