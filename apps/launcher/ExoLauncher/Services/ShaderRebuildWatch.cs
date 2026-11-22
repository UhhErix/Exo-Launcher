using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The rebuild's watch: one <see cref="ShaderRebuild"/> look a minute (the driver's identity and the games' records), and one
/// 30 seconds after a driver install says it is done. The look runs on a timer thread, never the UI's; the engine is
/// asked through the window's add-on (<see cref="MainWindow.ShadersRebuildStartAsync"/>), with a minute to answer.
/// Ways it could fail: a look that throws (caught and logged, the next one tries again); the timer outliving Exo
/// (<see cref="Stop"/> at shutdown); a look in the installer or first-run host (<see cref="HostMode.IsDisposable"/>:
/// nothing runs there).
/// </summary>
internal static class ShaderRebuildWatch
{
    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan AfterInstall = TimeSpan.FromSeconds(30);

    private static Timer? _tick;
    private static int _started;
    private static ShaderRebuild? _rebuild;

    /// <summary>The status changed: the page reads it again.</summary>
    internal static event Action? Changed;

    /// <summary>What the page is told now; idle before the watch starts or without the add-on.</summary>
    internal static RebuildStatus Status => _rebuild?.Status ?? new RebuildStatus("idle", 0, 0);

    /// <summary>Starts the watch once. Without the shader add-on there is nothing to rebuild and nothing starts.</summary>
    internal static void Start(AppServices services, CancellationToken ct)
    {
        if (HostMode.IsDisposable || !ShadersAddon.Present || Interlocked.Exchange(ref _started, 1) != 0) return;
        var rebuild = new ShaderRebuild(
            DriverIdentity.Current,
            () => DriverInstallSignal.Installing,
            () => PcIdle.Now(() => services.Launcher.AnyGameRunning || GameRunningSignal.Running || LeanSignal.Active),
            () => services.Shaders.ToCompile(services.Library.PeekCachedLibrary()),
            () => Engine(ShadersAddon.Live is { } live ? live.CompilingAsync(ct) : Task.FromResult(false), "asking whether it compiles"),
            ids => Engine(services.ShadersHost.RebuildStartAsync(ids, ct), "starting the compile"),
            () => Engine(Done(services.ShadersHost.RebuildPauseAsync(ct)), "pausing the compile"),
            () => ShaderRebuild.LoadFrom(ShaderRebuild.Path),
            record => ShaderRebuild.SaveTo(ShaderRebuild.Path, record),
            AppLog.Info,
            installed: () => services.Library.PeekCachedLibrary() is { Count: > 0 } library ? ScsKillerStatus.Installed(library) : null,
            scan: () => Engine(services.ShadersHost.ScanAsync(ct), "scanning the games"));
        rebuild.Changed += () => Changed?.Invoke();
        _rebuild = rebuild;
        DriverInstallSignal.Changed += OnStep;
        _tick = new Timer(_ => Look(settled: false, ct), null, Every, Every);
        ct.Register(Stop);
    }

    /// <summary>Stops the watch and lets go of the install signal; safe twice, and from any thread.</summary>
    internal static void Stop()
    {
        DriverInstallSignal.Changed -= OnStep;
        _tick?.Dispose();
        _tick = null;
    }

    /// <summary>
    /// A driver install said it is done: a look 30 seconds on. Exo's own install's final "done" (it carries what the
    /// restoring step put back) is a driver that stands, so the queue is built at once; anyone else's waits its two minutes.
    /// </summary>
    private static void OnStep(DriverInstallSignal.Step step)
    {
        if (step.Phase is not "done") return;
        var settled = step.Restored is not null || step.NotRestored is not null;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(AfterInstall).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Look(settled, CancellationToken.None);
        });
    }

    private static void Look(bool settled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _rebuild is not { } rebuild) return;
        try { rebuild.Tick(settled); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Shaders: the look for a new driver failed: " + ex.Message); }
    }

    private static async Task<bool> Done(Task task)
    {
        await task.ConfigureAwait(false);
        return true;
    }

    /// <summary>One request to the engine from the timer thread, with a minute to answer; false when it failed or took longer.</summary>
    private static bool Engine(Task<bool> call, string what)
    {
        try
        {
            return call.Wait(TimeSpan.FromMinutes(1)) && call.Result;
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException or OperationCanceledException)
        {
            AppLog.Info($"Shaders: {what} for the new driver did not work: {(ex.InnerException ?? ex).Message}");
            return false;
        }
    }
}
