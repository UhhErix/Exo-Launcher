using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Puts back what a graphics driver install resets (<see cref="AfterInstallAsync"/>, the restoring step
/// <see cref="DriverInstallSignal"/> shows): Exo's GPU profile, each game's NVIDIA profile, the RTX Video choice, NVIDIA
/// color per screen, each screen's refresh rate, color format and NVIDIA scaling as they were just before the install, and
/// interrupts. Screens are matched by their monitor's device path, never DISPLAYn (a clean install renumbers them); a
/// monitor the install gave a new path is found by its model when only one screen has it (<see cref="SameMonitor"/>).
/// After an NVIDIA install, what the user removed in Clean up goes again (<see cref="ReplacedApps.AgainAsync"/>: setup
/// brings the Control Panel back, its container the tray icon). On Exo's start (<see cref="AtStartAsync"/>, deferred):
/// RTX Video once for a driver installed outside Exo, interrupts a driver put back, and that removal for a driver Exo
/// didn't install.
/// </summary>
internal static class DriverRestore
{
    /// <summary>A screen just before the install: its refresh rate, its output color (Auto: the driver's own), and on NVIDIA its scaling and override.</summary>
    internal sealed record Screen(string DevicePath, int Refresh, OutputColor? Color, bool Auto, int? Scaling, bool? Override);

    /// <summary>What the restoring step put back (a few words each) and what it couldn't (a sentence each that says what to do).</summary>
    internal sealed record Outcome(IReadOnlyList<string> Restored, IReadOnlyList<string> NotRestored);

    /// <summary>The restoring step's record: each line goes to app.log as it happens.</summary>
    private sealed class Steps
    {
        internal readonly List<string> Restored = [], NotRestored = [];

        internal void Done(string what)
        {
            Restored.Add(what);
            AppLog.Info($"Driver: put back {what}.");
        }

        internal void Missed(string line)
        {
            NotRestored.Add(line);
            AppLog.Warn("Driver: not put back: " + line);
        }

        /// <summary>One step; a failure of its own is said, and the next step runs.</summary>
        internal void Run(string what, string missed, Action step)
        {
            try { step(); }
            catch (Exception ex)
            {
                AppLog.Warn($"Driver: putting back {what} failed: {ex.Message}");
                NotRestored.Add(missed);
            }
        }
    }

    /// <summary>Exo's start: RTX Video once for a driver installed outside Exo, then interrupts a driver put back (set again at a quiet moment, or offered).</summary>
    internal static async Task AtStartAsync(CancellationToken ct)
    {
        try
        {
            // After the first screen and the start's own work; nothing here is needed before then.
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (DriverInstallSignal.Installing) return;
            // A driver installed outside Exo (Windows Update, the vendor's app, by hand) reset Exo's profiles: they go back.
            DriverKeepWatch.Look("on start", ct);
            RtxVideo(App.Services.Settings, new Steps(), afterInstall: false);
            // Both wait for a quiet moment by themselves.
            await Task.WhenAll(InterruptMode.FollowUpAsync("on start", now: false, ct), ReplacedApps.AfterDriverChangeAsync("on start", ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Driver settings check on start failed: " + ex.Message); }
    }

    /// <summary>Every screen as it is now, read just before an install.</summary>
    internal static List<Screen> Snapshot()
    {
        var list = new List<Screen>();
        try
        {
            foreach (var screen in WindowsHdr.All())
            {
                var refresh = DisplayOutput.RefreshRates(screen.GdiName).Current;
                OutputColor? color = null;
                var auto = false;
                int? scaling = null;
                bool? over = null;
                if (NvidiaDisplay.DisplayId(screen.GdiName) is { } id)
                {
                    (color, auto, _) = NvidiaDisplay.Read(id);
                    scaling = NvidiaScaling.Read(id);
                    over = NvidiaScaling.ReadOverride(id);
                }
                else if (AmdAdlx.Present && AmdAdlx.ReadDisplay(screen.Name) is { } amd) color = amd.Current;
                else if (IntelIgcl.Present && IntelIgcl.ReadDisplay(screen.Target) is { } intel) color = intel.Current;
                list.Add(new Screen(screen.DevicePath, refresh, color, auto, scaling, over));
            }
        }
        catch (Exception ex) { AppLog.Warn("Driver: the screens couldn't be read before the install: " + ex.Message); }
        AppLog.Info($"Driver: {list.Count} screen(s) noted before the install.");
        return list;
    }

    /// <summary>
    /// The restoring step after a driver installed: each part in turn, what went back and what didn't. The record is told which
    /// driver identity it leaves Exo's settings held under (<see cref="DriverKeep.NoteExosOwnRestore"/>), so a look after this
    /// finds them already in place and writes nothing.
    /// </summary>
    internal static async Task<Outcome> AfterInstallAsync(string vendor, IReadOnlyList<Screen> before, bool interruptsChained)
    {
        var outcome = await RestoreAsync(vendor, before, interruptsChained).ConfigureAwait(false);
        try { DriverKeep.Shared.NoteExosOwnRestore(outcome.NotRestored.Count == 0); }
        catch (Exception ex) { AppLog.Warn("Driver: noting the driver in the record failed: " + ex.Message); }
        return outcome;
    }

    /// <summary>Each part of the restoring step; <see cref="AfterInstallAsync"/> runs it and notes the driver afterwards.</summary>
    private static async Task<Outcome> RestoreAsync(string vendor, IReadOnlyList<Screen> before, bool interruptsChained)
    {
        var steps = new Steps();
        // The new driver brings the screens back (often under new DISPLAYn names) before Exo reads them.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        steps.Run("Exo's GPU profile", "Exo's GPU profile wasn't put back. Press Re-apply in PC › Graphics.", () => Profile(steps));
        if (vendor == "nvidia")
        {
            steps.Run("game profiles", "Your games' NVIDIA profiles weren't put back. Optimize them again.", () => Games(steps));
            steps.Run("RTX Video", "RTX Video wasn't put back. Set it in PC › Display.", () => RtxVideo(App.Services.Settings, steps, afterInstall: true));
            steps.Run("NVIDIA color", "NVIDIA color wasn't put back. Set it in PC › Display.", () => Color(steps));
            // Setup provisions the Control Panel again and the container brings its tray icon back: what the user removed in
            // Clean up goes again (Windows' image part ran in the installer's elevated step).
            try
            {
                var (done, missed) = await ReplacedApps.ForThisPc().AgainAsync(NvidiaDriverService.Installed(), admin: false, "after the driver install", CancellationToken.None).ConfigureAwait(false);
                // Said in app.log by ReplacedApps.
                steps.Restored.AddRange(done);
                steps.NotRestored.AddRange(missed);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Driver: removing NVIDIA App and Control Panel again failed: " + ex.Message);
                steps.NotRestored.Add("NVIDIA App and Control Panel weren't removed again. Remove them in Clean up.");
            }
            NvidiaTrayIcon.Soon();
        }
        steps.Run("screens", "Your screens' settings weren't put back. Set them in PC › Display.", () => Displays(before, steps));
        try { Interrupts(await InterruptMode.FollowUpAsync("after the driver install", now: true).ConfigureAwait(false), interruptsChained, steps); }
        catch (Exception ex)
        {
            AppLog.Warn("Driver: putting back interrupts failed: " + ex.Message);
            steps.NotRestored.Add("Interrupts weren't put back. Press Switch back in PC.");
        }
        AppLog.Info($"Driver: restoring done: {steps.Restored.Count} put back, {steps.NotRestored.Count} not.");
        return new(steps.Restored, steps.NotRestored);
    }

    /// <summary>Exo's global profile, when the user applied it (a clean install puts every driver setting back to NVIDIA's).</summary>
    private static void Profile(Steps steps)
    {
        var why = PutProfileBack();
        if (why is null) steps.Done("Exo's GPU profile");
        else
        {
            AppLog.Warn("Driver: Exo's GPU profile: " + why);
            steps.Missed("Exo's GPU profile wasn't put back. Press Re-apply in PC › Graphics.");
        }
    }

    /// <summary>
    /// Writes Exo's global profile again and reads it back (a clean install puts every driver setting back to NVIDIA's);
    /// null when it reads as applied, else why not. Nothing to put back when Exo has no backup: it was never applied here.
    /// <see cref="DriverKeep"/> uses this too, so a driver installed outside Exo gets the same retry.
    /// </summary>
    internal static string? PutProfileBack() => PutProfileBack(GpuProfileService.Read, GpuProfileService.Apply, Thread.Sleep);

    /// <summary><see cref="PutProfileBack()"/> over the profile's read and write and the wait between tries (a test gives fakes).</summary>
    internal static string? PutProfileBack(Func<GpuProfileService.State> read, Func<string?, GpuProfileService.State> apply, Action<TimeSpan> wait)
    {
        var profile = read();
        if (!profile.CanUndo) return null;
        // Already as Exo set it (Exo's own install, or a driver that kept its profiles): what the player changed in NVIDIA's
        // own panel since is not written over.
        if (profile.Ok && profile.Status == "applied") return null;
        var applied = apply(profile.LowLatency);
        // After going back to an older driver the first write can land before the driver settles and read back as
        // changed (seen on 621.01 to 617.42): written again, a few seconds apart, until it reads as applied.
        for (var tries = 0; tries < 2 && applied.Ok && applied.Status == "changed"; tries++)
        {
            AppLog.Info("Driver: Exo's GPU profile read back as changed; writing it again.");
            wait(TimeSpan.FromSeconds(3));
            applied = apply(profile.LowLatency);
        }
        return applied.Ok && applied.Status != "changed" ? null : applied.Message ?? "The NVIDIA driver did not answer.";
    }

    private static void Games(Steps steps)
    {
        var (done, failed) = GameSettings.RestoreNvidiaGames();
        foreach (var title in done) steps.Done($"{title}'s NVIDIA profile");
        foreach (var title in failed) steps.Missed($"{title}'s NVIDIA profile wasn't put back. Optimize the game again.");
    }

    private static void Color(Steps steps)
    {
        var (done, failed) = NvidiaColor.ApplySaved(App.Services.Settings);
        foreach (var screen in done) steps.Done($"NVIDIA color on {screen}");
        foreach (var screen in failed) steps.Missed($"NVIDIA color on {screen} wasn't put back. Set it in PC › Display.");
    }

    /// <summary>
    /// RTX Video back to the choice (<see cref="RtxWrites"/>) after a driver changed: after Exo's own install always, on
    /// start once per driver (<see cref="Models.AppSettings.RtxVideoDriver"/>), so a change made in NVIDIA's own apps
    /// afterwards stays. Nothing on a card without RTX Video.
    /// </summary>
    private static void RtxVideo(SettingsService settings, Steps steps, bool afterInstall)
    {
        var driver = NvidiaDriverService.Installed();
        var kept = settings.Shared;
        if (!afterInstall && (driver is null || kept.RtxVideoDriver == driver)) return;
        if (NvidiaVideo.AnyScreen() is not { } gdi || NvidiaVideo.Read(gdi, NvidiaVideo.SuperResolution) is not { } superResolution) return;
        var writes = RtxWrites(superResolution, NvidiaVideo.Read(gdi, NvidiaVideo.VideoHdr), kept.RtxVideoSuperResolution, kept.RtxVideoHdr);
        string? problem = null;
        foreach (var (component, value) in writes) problem ??= NvidiaVideo.Write(gdi, component, value);
        if (writes.Count > 0)
        {
            AppLog.Info($"Display: RTX Video {(afterInstall ? "after the driver install" : "for driver " + driver)}: Super resolution was {superResolution}, " +
                $"wanted {kept.RtxVideoSuperResolution?.ToString() ?? "Auto (Exo's default)"}{(problem is null ? "" : "; " + problem)}.");
            if (problem is null) steps.Done("RTX Video");
            else steps.Missed("RTX Video wasn't put back. Set it in PC › Display.");
        }
        if (problem is null && driver is not null && kept.RtxVideoDriver != driver)
            settings.UpdateProfile(s => s.RtxVideoDriver = driver);
    }

    /// <summary>
    /// The RTX Video writes that bring the driver to the choice: Super resolution to the user's (null: Auto, Exo's built-in
    /// default on an RTX card), HDR to the user's only when they chose one. Empty when it's so.
    /// </summary>
    internal static List<(int Component, int Value)> RtxWrites(int superResolution, int? hdr, int? chosenSuperResolution, bool? chosenHdr)
    {
        var writes = new List<(int Component, int Value)>();
        var wanted = chosenSuperResolution ?? 5;
        if (superResolution != wanted) writes.Add((NvidiaVideo.SuperResolution, wanted));
        if (chosenHdr is { } on && hdr is { } now && (now != 0) != on) writes.Add((NvidiaVideo.VideoHdr, on ? 5 : 0));
        return writes;
    }

    /// <summary>Each screen from before the install, found again by its monitor: its refresh rate, color format and scaling as they were.</summary>
    private static void Displays(IReadOnlyList<Screen> before, Steps steps)
    {
        if (before.Count == 0) return;
        var screens = WindowsHdr.All();
        var paths = screens.Select(screen => screen.DevicePath).ToList();
        var noted = before.Select(screen => screen.DevicePath).ToList();
        foreach (var saved in before)
        {
            if (SameMonitor(saved.DevicePath, paths, noted) is not { } path)
            {
                AppLog.Info("Driver: a screen from before the install isn't connected now.");
                continue;
            }
            var index = paths.IndexOf(path);
            var screen = screens[index];
            var name = DisplayOutput.ScreenName(screen.Name, screen.DevicePath, index + 1);
            steps.Run($"the refresh rate on {name}", $"{saved.Refresh} Hz on {name} wasn't put back. Set it in PC › Display.", () => Refresh(saved, screen, name, steps));
            steps.Run($"the color format on {name}", $"The color format on {name} wasn't put back. Set it in PC › Display.", () => ColorFormat(saved, screen, name, steps));
            steps.Run($"scaling on {name}", $"Scaling on {name} wasn't put back. Set it in PC › Display.", () => Scaling(saved, screen, name, steps));
        }
    }

    private static void Refresh(Screen saved, WindowsHdr.Screen screen, string name, Steps steps)
    {
        var (now, rates) = DisplayOutput.RefreshRates(screen.GdiName);
        if (saved.Refresh <= 0 || now == saved.Refresh) return;
        var problem = !rates.Contains(saved.Refresh) ? "not offered" : DisplayOutput.SetRefresh(screen.GdiName, saved.Refresh) ?? DisplayOutput.SaveRefresh(screen.GdiName);
        if (problem is not null)
        {
            AppLog.Info($"Driver: {saved.Refresh} Hz on {screen.GdiName}: {problem}");
            steps.Missed($"{saved.Refresh} Hz on {name} wasn't put back. Set it in PC › Display.");
            return;
        }
        steps.Done($"{saved.Refresh} Hz on {name}");
        // The driver answers for the new mode once the screen has settled on it.
        Thread.Sleep(1000);
    }

    private static void ColorFormat(Screen saved, WindowsHdr.Screen screen, string name, Steps steps)
    {
        if (saved.Color is not { } wanted) return;
        string? problem;
        if (NvidiaDisplay.DisplayId(screen.GdiName) is { } id)
        {
            var (color, auto, supported) = NvidiaDisplay.Read(id);
            if (saved.Auto ? auto : color == wanted && !auto) return;
            problem = saved.Auto || supported.Contains(wanted) ? NvidiaDisplay.Write(id, wanted, saved.Auto) : "not offered";
        }
        else if (AmdAdlx.Present && AmdAdlx.ReadDisplay(screen.Name) is { } amd)
        {
            if (amd.Current == wanted) return;
            problem = amd.Supported.Contains(wanted) ? AmdAdlx.WriteDisplay(screen.Name, wanted) : "not offered";
        }
        else if (IntelIgcl.Present && IntelIgcl.ReadDisplay(screen.Target) is { } intel)
        {
            if (intel.Current == wanted) return;
            problem = intel.Supported.Contains(wanted) ? IntelIgcl.WriteDisplay(screen.Target, wanted) : "not offered";
        }
        else return;
        if (problem is null) steps.Done($"{(saved.Auto ? "The driver's color" : DisplayOutput.Label(wanted))} on {name}");
        else
        {
            AppLog.Info($"Driver: {DisplayOutput.Label(wanted)} on {screen.GdiName}: {problem}");
            steps.Missed($"The color format on {name} wasn't put back. Set it in PC › Display.");
        }
    }

    private static void Scaling(Screen saved, WindowsHdr.Screen screen, string name, Steps steps)
    {
        if (NvidiaDisplay.DisplayId(screen.GdiName) is not { } id) return;
        if (saved.Scaling is { } code && NvidiaScaling.Read(id) is { } now && now != code)
        {
            if (NvidiaScaling.Write(id, code) is { } problem)
            {
                AppLog.Info($"Driver: scaling on {screen.GdiName}: {problem}");
                steps.Missed($"Scaling on {name} wasn't put back. Set it in PC › Display.");
            }
            else steps.Done($"Scaling on {name}");
        }
        if (saved.Override is { } on && NvidiaScaling.ReadOverride(id) is { } overNow && overNow != on)
        {
            if (NvidiaScaling.WriteOverride(id, on) is { } problem)
            {
                AppLog.Info($"Driver: scaling override on {screen.GdiName}: {problem}");
                steps.Missed($"The scaling override on {name} wasn't put back. Set it in PC › Display.");
            }
            else steps.Done($"Scaling override on {name}");
        }
    }

    /// <summary>Interrupts after the install: set again in its own elevated step, by itself, or left for Switch back.</summary>
    private static void Interrupts((InterruptMode.FollowUp Next, string? Problem) followUp, bool chained, Steps steps)
    {
        switch (followUp.Next)
        {
            case InterruptMode.FollowUp.None when chained:
            case InterruptMode.FollowUp.Reapply when followUp.Problem is null:
                steps.Done("Interrupts, after a restart");
                break;
            case InterruptMode.FollowUp.Reapply:
                steps.Missed("Interrupts weren't put back. Press Switch back in PC.");
                break;
            case InterruptMode.FollowUp.Ask:
                steps.Missed("Interrupts need your OK to go back. Press Switch back in PC.");
                break;
        }
    }

    /// <summary>
    /// Where a screen noted by its device path is now: the same path, else, when a driver install gave the monitor a new
    /// instance (\\?\DISPLAY#&lt;model&gt;#&lt;instance&gt;#{…}), the one screen of that model whose path nothing noted has, when
    /// only one noted screen of that model is missing. Null when it isn't connected or two screens leave it unclear.
    /// </summary>
    internal static string? SameMonitor(string noted, IReadOnlyList<string> now, IReadOnlyCollection<string> allNoted)
    {
        if (now.FirstOrDefault(path => path.Equals(noted, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
        if (Model(noted) is not { } model) return null;
        bool Known(string path, IEnumerable<string> list) => list.Contains(path, StringComparer.OrdinalIgnoreCase);
        if (allNoted.Count(path => Model(path) == model && !Known(path, now)) != 1) return null;
        var candidates = now.Where(path => Model(path) == model && !Known(path, allNoted)).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string? Model(string devicePath) =>
        devicePath.Split('#') is { Length: >= 3 } parts && parts[1].Length > 0 ? parts[1].ToUpperInvariant() : null;
}
