using ExoLauncher.Helpers;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _connectedDialogGate = new(1, 1);

    internal async Task<object> PrepareConnectedRoomsAsync()
    {
        if (PackagedSmoke.Enabled || _closingWindow) return new { ok = true };
        if (!App.Services.Settings.Current.OnboardingComplete) return new { ok = true };
        _shellUiInactive = null;
        var hiddenNow = WindowIsHiddenOrMinimized();
        _ = UpdateShellActivityAsync(hiddenNow || !_windowActive, hiddenNow);
        var milestone = !_connectedPreloadLogged;
        _connectedPreloadLogged = true;
        if (milestone) LogStartupMilestone("connected-preload-start");
        var work = new List<Task> { PrewarmCapture(), Task.Run(Services.DiscordClient.DiscordSession.Start) };
        await Task.WhenAll(work);
        if (milestone) LogStartupMilestone("connected-preload-ready");
        return new { ok = true };
    }

    private bool _connectedPreloadLogged;
}
