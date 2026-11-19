using ExoLauncher.Helpers;
using System.Diagnostics;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.health: System > This PC (<see cref="PcHealth"/>). No action reads the checks; apply
/// switches to Exo's power plan for this PC's processor (<see cref="PowerPlan"/>); chipset starts the AMD or
/// Intel chipset update now (<see cref="ChipsetUpdate"/>); network sets the adapters for games
/// (<see cref="NetworkTune"/>), never during a call or a game; gpusched opens Windows' Graphics settings; gamemode-on and gamemode-off, gamebar-on (Exo keeps Game Bar off) and gamebar-off (Exo puts it back) are the Windows side of Keep (<see cref="WindowsGameMode"/>, <see cref="WindowsGameBar"/>). Each answer
/// carries the checks as they are after it. system.about: PC › Overview's About card (<see cref="PcAbout"/>), read only.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> SystemAboutAsync(CancellationToken ct) => await PcAbout.ReadAsync(ct).ConfigureAwait(false);

    private async Task<object> SystemHealthAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var message = action switch
        {
            "apply" => await Task.Run(() => PowerPlan.Shared.Apply(PcHealth.ThisPcProfile()), ct).ConfigureAwait(false),
            "chipset" => await Task.Run(StartChipsetUpdateAsync, ct).ConfigureAwait(false),
            "gpupref" => await Task.Run(UseFastGpuForGames, ct).ConfigureAwait(false),
            "gpupref-undo" => await Task.Run(UndoFastGpuForGames, ct).ConfigureAwait(false),
            "network" => NetworkBusy() ?? await Task.Run(NetworkTune.Shared.Apply, ct).ConfigureAwait(false),
            "gpusched" => OpenWindowsSettings("ms-settings:display-advancedgraphics", "Graphics"),
            _ when IsWindowsSideAction(action) => await Task.Run(() => WindowsSideAction(action, WindowsGameMode.Shared, WindowsGameBar.Shared), ct).ConfigureAwait(false),
            _ => null,
        };
        var reading = await Task.Run(() => PcHealth.ReadAsync(PowerPlan.Shared, _services.Settings.Current.AutoUpdateChipset, _services.Settings.Current.FastGpuForGames, ct), ct).ConfigureAwait(false);
        var decided = PcHealth.Decide(reading).ToList();
        return new
        {
            ok = message is null,
            message,
            items = decided.Select(item => new { key = item.Key, label = item.Label, detail = item.Detail, url = item.Url, applied = item.Applied, canUndo = item.CanUndo, busy = item.Busy, cpu = item.Cpu, current = item.Current, held = item.Held }).ToList(),
        };
    }

    /// <summary>The actions that reach Game Mode and Game Bar: on and off each.</summary>
    internal static bool IsWindowsSideAction(string? action) => action is "gamemode-on" or "gamemode-off" or "gamebar-on" or "gamebar-off";

    /// <summary>Game Mode on or off, Game Bar kept off or put back, as the page asked: null when done, else the sentence to show.</summary>
    internal static string? WindowsSideAction(string? action, WindowsGameMode mode, WindowsGameBar bar) => action switch
    {
        "gamemode-on" => mode.TurnOn(),
        "gamemode-off" => mode.TurnOff(),
        "gamebar-on" => bar.TurnOn().Message,
        "gamebar-off" => bar.TurnOff().Message,
        _ => null,
    };

    /// <summary>Graphics for games on: the setting, and every installed game's programs asked for the fast chip now (a game started later is set as it starts).</summary>
    private string? UseFastGpuForGames()
    {
        _services.Settings.UpdateProfile(s => s.FastGpuForGames = true);
        var said = GpuPreference.Shared.UseAndSay(_services.Library.PeekCachedLibrary().Where(game => game.Installed).SelectMany(GpuPreference.ProgramsOf));
        // Windows kept nothing: the row does not say it is on, and no game start retries a setting that is not taken.
        if (said is not null) _services.Settings.UpdateProfile(s => s.FastGpuForGames = false);
        return said;
    }

    private string? UndoFastGpuForGames()
    {
        _services.Settings.UpdateProfile(s => s.FastGpuForGames = false);
        return GpuPreference.Shared.UndoAndSay();
    }

    /// <summary>Why not now: the adapter restarts, so the connection drops for a few seconds.</summary>
    private static string? NetworkBusy() =>
        DiscordClient.DiscordSession.Current?.InCall == true ? "You're in a call. Your connection drops for a few seconds while this changes, so try after it."
        : App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0 ? "A game is running. Your connection drops for a few seconds while this changes, so try after it."
        : null;

    private static async Task<string?> StartChipsetUpdateAsync()
    {
        if (ChipsetUpdate.ThisPc is not { } chipset) return "Exo doesn't update this PC's chipset driver.";
        if (chipset.Installed() is not { } installed) return $"{chipset.Maker}'s chipset driver isn't installed on this PC.";
        return await chipset.LatestAsync().ConfigureAwait(false) is { } latest
            ? chipset.Start(installed, latest)
            : $"{chipset.Maker}'s page couldn't be read. Try again later.";
    }

    /// <summary>One of Windows' own Settings pages, by a fixed address (never one the page names); null when it opened.</summary>
    private static string? OpenWindowsSettings(string uri, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Helpers.AppLog.Warn($"PC health: {what} settings didn't open: " + ex.Message);
            return "Windows Settings didn't open.";
        }
    }
}
