using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile. gpu.profile: Exo's profile for this PC (no
/// action), apply (on Intel, with lowLatency) or undo. gpu.game: whether
/// Exo turns Fluid Motion Frames on for a game on a Radeon (Exo's decision, nothing to set), or its XeSS frame
/// generation on Intel (set when "frameGen" is passed).
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>
    /// A gpu.* call answers within <paramref name="limit"/>: a driver call that hangs (NvAPI, ADLX) leaves
    /// its thread waiting, but the page gets a plain line instead of waiting forever.
    /// </summary>
    private static async Task<object> GpuDeadlineAsync(Task<object> work, TimeSpan limit, CancellationToken ct)
    {
        try { return await work.WaitAsync(limit, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Helpers.AppLog.Warn($"GPU: the driver did not answer within {limit.TotalSeconds:0} s.");
            throw new UserFacingException("Your graphics driver didn't answer. Try again.");
        }
    }

    // The System room's header and its profile tab both read the profile as it shows, and each read is a whole driver
    // session (half a second or more): the second waited out the first. One read in flight answers both.
    private readonly Lock _profileReadGate = new();
    private Task<GpuProfileService.State>? _profileRead;

    private Task<GpuProfileService.State> SharedProfileRead()
    {
        lock (_profileReadGate)
        {
            if (_profileRead is { IsCompleted: false } running) return running;
            return _profileRead = Task.Run(GpuProfileService.Read);
        }
    }

    private async Task<object> GpuProfileAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var lowLatency = ReadString(p, hasParams, "lowLatency");
        var vrr = ReadBool(p, hasParams, "vrr");
        var state = action is "apply" or "undo"
            ? await Task.Run(() => action == "apply" ? GpuProfileService.ApplyWith(lowLatency, vrr) : GpuProfileService.Undo(), ct).ConfigureAwait(false)
            : await SharedProfileRead().WaitAsync(ct).ConfigureAwait(false);
        return new
        {
            ok = state.Ok,
            message = state.Message,
            vendor = state.Vendor,
            gpu = state.Gpu,
            series = state.Series,
            screen = new { name = state.Screen.Name, hz = state.Screen.RefreshHz, vrr = state.Screen.Vrr },
            status = state.Status,
            canUndo = state.CanUndo,
            driverUpdated = state.DriverUpdated,
            items = state.Manual.Select(item => new { key = item.Key, label = item.Label, value = item.Value, why = item.Why, current = item.Current }).ToList(),
            lowLatency = state.LowLatency,
            vrrEnabled = state.VrrEnabled,
            rebar = state.Rebar,
            cacheFreeGb = state.CacheFreeGb,
            details = state.Details?.Select(d => new { key = d.Key, label = d.Label, value = d.Value, current = d.Current, state = d.State, note = d.Note }).ToList(),
            summary = state.Summary,
            // Something the profile asks for is not in this driver: no count and no id reach the page, only that.
            skipped = state.Partly,
            features = state.Features?.Select(feature => new { key = feature.Key, label = feature.Label, state = feature.State }).ToList(),
        };
    }

    /// <summary>
    /// Game sheet > driver frame generation: on a Radeon, Fluid Motion Frames (supported, whether Exo turns it on for this
    /// game, whether AMD Software runs); on Intel, XeSS frame generation's override (supported, the game's choice).
    /// </summary>
    private async Task<object> GpuGameAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        if (await Task.Run(() => GraphicsService.Vendor(), ct).ConfigureAwait(false) == "intel")
        {
            var intel = IntelProfileService.Shared;
            if (ReadString(p, hasParams, "frameGen") is { } choice && !await Task.Run(() => intel.SetFrameGen(game.Id, choice), ct).ConfigureAwait(false))
                return new { ok = false, message = "Could not save." };
            var (canGenerate, frameGen) = await Task.Run(() => intel.FrameGen(game.Id), ct).ConfigureAwait(false);
            return new { ok = true, vendor = "intel", supported = canGenerate, frameGen };
        }
        var (supported, gpu, amdSoftware) = await Task.Run(RadeonProfileService.Shared.Afmf, ct).ConfigureAwait(false);
        if (!supported || !Helpers.FluidMotionFrames.Runs(gpu)) return new { ok = true, vendor = "amd", supported = false };
        // What the launch will do, looked up now (the wiki, Steam), so the launch finds it kept.
        var known = await FrameGenFacts.WithinAsync(game, FactsWait, ct).ConfigureAwait(false);
        return new { ok = true, vendor = "amd", supported = true, afmf = Helpers.FluidMotionFrames.Decide(gpu, known).On, amdSoftware };
    }

    /// <summary>
    /// gpu.afmf3: AMD's unreleased Fluid Motion Frames 3 (<see cref="Helpers.Afmf3Preview"/>): where its switch stands, or with on, one press of it. Only a Radeon goes on to ask AMD's pages anything.
    /// </summary>
    private static async Task<object> GpuAfmf3Async(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var (gpu, driver) = await Task.Run(() =>
        {
            if (GraphicsService.Vendor() != "amd") return ((string?)null, (string?)null);
            var (_, name, _) = RadeonProfileService.Shared.Afmf();
            return (name, name is null ? null : GpuDriverUpdate.Installed("amd", name));
        }, ct).ConfigureAwait(false);
        var released = await Helpers.Afmf3Preview.OfficialAsync(gpu, driver, ct).ConfigureAwait(false);
        var preview = Helpers.Afmf3Preview.ThisPc;
        var message = ReadBool(p, hasParams, "on") is { } on ? await Task.Run(() => preview.Set(on, gpu, driver, released), ct).ConfigureAwait(false) : null;
        var state = await Task.Run(() => preview.Read(gpu, driver, released), ct).ConfigureAwait(false);
        return new { ok = message is null, message, offer = state.Offer, on = state.On, note = state.Note };
    }

    /// <summary>How long the game sheet waits for a game's facts before reading Fluid Motion Frames from what is kept.</summary>
    private static readonly TimeSpan FactsWait = TimeSpan.FromSeconds(8);

    // Set once: the first two gpu.driver calls (the header and the driver row) run on the pool together.
    private Action<NvidiaDriverService.Progress>? _driverEvents;

    /// <summary>
    /// System > Graphics > Driver and profile's driver row (<see cref="NvidiaDriverService"/>): "state" checks NVIDIA's newest Game Ready
    /// driver for this card against the installed one; "update" (after the user's consent) starts the
    /// driver-only clean install, its progress arriving as gpu.driver events. A version with a known problem on this PC
    /// (<see cref="DriverAdvisories"/>: a game in the library, the card, Windows' build) is never offered: skipped names it
    /// and why, and when the advisory says to go back, that version is offered instead if it's newer than what runs. When
    /// what runs has a known problem, advised says why, with the version to go back to (back), which "goback" installs
    /// through the same flow. A Radeon or an Intel GPU gets the same row from <see cref="GpuDriverUpdate"/>, for the vendor
    /// whose GPU Exo talks to (else the first one Windows lists).
    /// </summary>
    private static readonly TimeSpan DriverReadBudget = TimeSpan.FromMilliseconds(500);

    private static async Task<T?> WithReadBudgetAsync<T>(Func<CancellationToken, Task<T?>> fetch, bool waitFull, CancellationToken ct) where T : class
    {
        if (waitFull) return await fetch(ct).ConfigureAwait(false);
        try { return await fetch(CancellationToken.None).WaitAsync(DriverReadBudget, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private async Task<object> GpuDriverAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        Action<NvidiaDriverService.Progress> post = progress => PostEvent("gpu.driver", DriverProgress(progress));
        if (Interlocked.CompareExchange(ref _driverEvents, post, null) is null)
        {
            NvidiaDriverService.Changed += post;
            GpuDriverUpdate.Changed += post;
            // An install starts only from this row, so its steps (the page's driver screen) are wired with it.
            DriverInstallSignal.Changed += PostDriverInstall;
        }
        // While an update runs the row stays on its vendor: mid-install, Windows may list no GPU of that vendor.
        var vendor = GpuDriverUpdate.Updating
            ?? (NvidiaDriverService.Current.Stage is "downloading" or "unpacking" or "waiting" or "installing" ? "nvidia" : null)
            ?? GraphicsService.Vendor() ?? GraphicsService.Vendors(GpuCapability.Adapters()).FirstOrDefault();
        var action = ReadString(p, hasParams, "action");
        var update = action == "update";
        var waitForNetwork = update || action == "goback";
        if (waitForNetwork) await DriverAdvisories.Shared.RefreshAsync(ct).ConfigureAwait(false);
        else
        {
            try { await DriverAdvisories.Shared.RefreshAsync(CancellationToken.None).WaitAsync(DriverReadBudget, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        if (vendor is "amd" or "intel") return await VendorDriverAsync(vendor, update, waitForNetwork, ct).ConfigureAwait(false);
        if (vendor != "nvidia") return new { ok = true, vendor = (string?)null };
        var card = NvidiaDriverService.Card();
        var workstation = card is not null && NvidiaDriverService.Workstation(card);
        var studio = ReadString(p, hasParams, "branch") == "studio";
        // The version watch tells of the branch the row shows.
        if (studio != (_services.Settings.Current.NvidiaDriverBranch == "studio")) _services.Settings.UpdateProfile(s => s.NvidiaDriverBranch = studio ? "studio" : null);
        var branch = studio ? "studio" : "game";
        var latest = await WithReadBudgetAsync(token => NvidiaDriverService.LatestAsync(branch, token, fresh: update), waitForNetwork, ct).ConfigureAwait(false);
        var installed = NvidiaDriverService.Installed();
        var laptop = card?.Contains("Laptop", StringComparison.OrdinalIgnoreCase) == true;
        var subject = DriverAdvisories.Gpu("nvidia", card);
        var skipped = latest is null || VersionNumber.Same(latest.Version, installed) ? null : DriverAdvisories.Shared.Against(subject, latest.Version);
        var bad = DriverAdvisories.Shared.Against(subject, installed);
        var back = bad is { Do: DriverAdvisories.Advice.Back, To: { } to } ? to : null;
        // The best version, not the newest: past an advisory that says go back, that version when it is newer than what runs.
        var offer = skipped is null ? latest
            : skipped is { Do: DriverAdvisories.Advice.Back, To: { } good } && VersionNumber.Newer(good, installed)
                ? NvidiaDriverService.At(good, studio ? await NvidiaDriverService.LatestAsync("game", ct).ConfigureAwait(false) : latest, laptop)
            : null;
        string? message = null;
        if (update)
            message = latest is null ? "NVIDIA's driver list couldn't be reached." : offer is null ? DriverAdvisories.Skipped(latest.Version, skipped!) + "." : NvidiaDriverService.Start(offer);
        else if (action == "goback")
            message = back is null ? "Your driver has no known problem here." : NvidiaDriverService.Start(NvidiaDriverService.At(back, studio ? await NvidiaDriverService.LatestAsync("game", ct).ConfigureAwait(false) : latest, laptop));
        return new
        {
            ok = message is null,
            message,
            vendor,
            card,
            workstation,
            installed,
            latest = offer is null ? null : new { version = offer.Version, size = offer.Size, released = offer.Released, kind = offer.Kind },
            update = offer is not null && installed is not null && NvidiaDriverService.Compare(offer.Version, installed) > 0,
            skipped = skipped is null ? null : new { version = latest!.Version, why = skipped.Why },
            advised = bad is null ? null : new { version = installed, why = bad.Why, back },
            software = false,
            blocked = NvidiaDriverService.Blocked(),
            progress = DriverProgress(NvidiaDriverService.Current),
        };
    }

    /// <summary>
    /// The row on a Radeon or an Intel GPU: AMD's or Intel's newest driver for the card (no Game Ready or Studio choice).
    /// Software: AMD Software's minimal view installs with the driver (the card runs Fluid Motion Frames, which needs it).
    /// </summary>
    private static async Task<object> VendorDriverAsync(string vendor, bool update, bool waitForNetwork, CancellationToken ct)
    {
        var card = GpuDriverUpdate.Card(vendor);
        var newest = await WithReadBudgetAsync(token => GpuDriverUpdate.LatestAsync(vendor, token, fresh: update), waitForNetwork, ct).ConfigureAwait(false);
        var installed = card is null ? null : GpuDriverUpdate.Installed(vendor, card);
        var subject = DriverAdvisories.Gpu(vendor, card);
        // AMD's and Intel's lists give only their newest, so one with a known problem here isn't offered at all.
        var skipped = newest is null || VersionNumber.Same(newest.Version, installed) ? null : DriverAdvisories.Shared.Against(subject, newest.Version);
        var bad = DriverAdvisories.Shared.Against(subject, installed);
        var latest = skipped is null ? newest : null;
        string? message = null;
        if (update) message = newest is null ? $"{(vendor == "amd" ? "AMD" : "Intel")}'s newest driver couldn't be found." : latest is null ? DriverAdvisories.Skipped(newest.Version, skipped!) + "." : GpuDriverUpdate.Start(latest);
        return new
        {
            ok = message is null,
            message,
            vendor,
            card,
            workstation = false,
            installed,
            latest = latest is null ? null : new { version = latest.Version, size = latest.Size, released = latest.Released, kind = latest.Kind, page = latest.Page },
            update = latest is not null && GpuDriverUpdate.Newer(latest.Version, installed),
            skipped = skipped is null ? null : new { version = newest!.Version, why = skipped.Why },
            // AMD's and Intel's pages name only their newest, so there's no earlier version to go back to.
            advised = bad is null ? null : new { version = installed, why = bad.Why, back = (string?)null },
            software = vendor == "amd" && Helpers.FluidMotionFrames.Runs(card),
            blocked = GpuDriverUpdate.Blocked(),
            progress = DriverProgress(GpuDriverUpdate.Current),
        };
    }

    private static object DriverProgress(NvidiaDriverService.Progress progress) =>
        new { stage = progress.Stage, percent = progress.Percent, message = progress.Message };

    /// <summary>gpu.driverInstall: a step of a driver install, for the page's driver screen; done lists what was put back and what wasn't.</summary>
    private void PostDriverInstall(DriverInstallSignal.Step step) =>
        PostEvent("gpu.driverInstall", new { phase = step.Phase, version = step.Version, message = step.Message, restored = step.Restored, notRestored = step.NotRestored });
}
