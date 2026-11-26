using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>How Exo gets a driver or firmware: not at all (current, or nothing newer), it installs it, or it opens the maker's page.</summary>
public enum DriverHow { None, Install, Page }

/// <summary>
/// One device in the Drivers list. Id is "kind:device" ("network:PCI\VEN_8086&amp;...", "windows:{update id}"); Kind is
/// network, windows, bios or ssd. Version and Date (yyyy-MM-dd) are what runs, Latest and LatestDate the newest its source
/// has. How is None while nothing newer is offered. Page: the maker's page (link-only rows, BIOS, SSD firmware).
/// CanRollBack: Exo installed what runs and kept the driver it replaced.
/// </summary>
internal sealed record DriverItem(string Id, string Kind, string Name, string? Maker, string? Version, string? Date, string? Latest, string? LatestDate,
    DriverHow How, string? Page, bool CanRollBack)
{
    /// <summary>Where Latest comes from, for the row: "Intel", "Windows Update", "ASUS".</summary>
    public string? Source { get; init; }

    /// <summary>What the advisories match (<see cref="Subject"/>): the device id and name, the board, the drive's model.</summary>
    public string? Device { get; init; }

    /// <summary>A vendor's app Windows Update installs as a driver (class SoftwareComponent): only on its own Install, never in Update all.</summary>
    public bool App { get; init; }

    /// <summary>Latest is a version with a known problem here, so it isn't offered (<see cref="DriverAdvisories"/>).</summary>
    public DriverAdvisories.Advisory? Skipped { get; init; }

    /// <summary>What runs has a known problem here: the advisory's way out is the row's button.</summary>
    public DriverAdvisories.Advisory? Advised { get; init; }

    /// <summary>"network:PCI\VEN_8086&amp;DEV_125C&amp;… Intel(R) Ethernet Controller I226-V", "bios:ASUS ROG STRIX B550-F GAMING WIFI II".</summary>
    public string Subject => $"{Kind}:{Device ?? Name}";

    /// <summary>Exo installs it (Install), only its maker's page has it (Page), or what runs has a known problem; never an app.</summary>
    public bool NeedsYou => !App && (How != DriverHow.None || Advised is not null);
}

/// <summary>How an install or roll back ended: Message is the row's line. Restart: Windows finishes it at the next restart.</summary>
internal sealed record DriverResult(bool Ok, string Message, bool Restart = false);

/// <summary>A source of rows for the Drivers list that can install what it finds (network, Windows Update).</summary>
internal interface IDriverProvider
{
    Task<IReadOnlyList<DriverItem>> ReadAsync(CancellationToken ct);
    /// <summary>Why nothing can install now (a game, a call, another install), or null.</summary>
    string? Busy();
    /// <summary>The row's line while its install runs.</summary>
    string Working(DriverItem item);
    Task<DriverResult> InstallAsync(DriverItem item, IProgress<int>? progress, CancellationToken ct);
    Task<DriverResult> RollBackAsync(DriverItem item, CancellationToken ct);
}

/// <summary>
/// PC › Drivers: one list of every device with a driver or firmware Exo can see a newer version of. Sources: the network
/// adapters (<see cref="NetworkDrivers"/>), the BIOS (<see cref="BiosUpdate"/>, link only), the SSDs' firmware (a slot until
/// Drives says which firmware is newer), and Windows Update's driver catalog (<see cref="WindowsUpdateDrivers"/>: audio,
/// Bluetooth, USB, card readers, cameras, screens, HID, storage controllers and the rest, every one Microsoft-signed). The
/// graphics and chipset drivers keep their own rows and flows (<see cref="GpuDriverUpdate"/>, <see cref="NvidiaDriverService"/>,
/// <see cref="ChipsetUpdate"/>); the page shows them in the same section, and they win over Windows Update here.
///
/// Merge rules (<see cref="Merge"/>): a maker's own source wins over Windows Update for the same device, so the graphics
/// driver comes from NVIDIA, AMD or Intel and the AMD or Intel chipset's System devices from the chipset package; Windows
/// Update's offer stays only when it's newer than the maker's newest and the maker's row can't install it. Never offered:
/// a hotfix, a driver older than what runs, a version with a known problem on this PC (<see cref="Apply"/>, from
/// <see cref="DriverAdvisories"/>; each provider checks again right before it installs). A vendor's app that Windows
/// Update ships as a driver (class SoftwareComponent) is its own row, installed only by its own Install; one that only
/// collects data or adds sound effects (HP's analytics, A-Volute's Nahimic) isn't offered at all.
///
/// Update all (<see cref="UpdateAllAsync"/>) installs one after another, never during a game, a Discord call, another driver
/// install or an Exo update; Windows Update's are one batch under one Windows prompt, each replaced driver kept first.
/// </summary>
internal sealed class DriverCatalog(IReadOnlyList<IDriverProvider> providers)
{
    internal static DriverCatalog Shared { get; } = new([NetworkDrivers.Shared, WindowsUpdateDrivers.Shared]);

    private int _running;
    private volatile bool _restart;

    /// <summary>Update all runs now.</summary>
    internal bool Running => Volatile.Read(ref _running) == 1;

    /// <summary>An install this session wants a restart to finish.</summary>
    internal bool Restart => _restart;

    // ---- Rules ----

    /// <summary>A hotfix, beta or preview build: never offered (NVIDIA's are off for good reason; Windows Update's titles say so when one is).</summary>
    internal static bool Hotfix(string? title) =>
        title is not null && (title.Contains("hotfix", StringComparison.OrdinalIgnoreCase) || title.Contains("beta", StringComparison.OrdinalIgnoreCase)
            || title.Contains("preview", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every row against the advisories (<paramref name="against"/>: the advisory for a subject's version on this PC, or
    /// null). An offered version with one is left out (How None, Skipped names it); what runs with one is Advised, and when
    /// nothing installs the way out, its maker's page does. Rows that need you come first, then by name.
    /// </summary>
    internal static IReadOnlyList<DriverItem> Apply(IReadOnlyList<DriverItem> items, Func<string, string?, DriverAdvisories.Advisory?> against) =>
        [.. items.Select(item =>
        {
            var advised = against(item.Subject, item.Version);
            var skipped = item.Latest is null || item.Latest == item.Version ? null : against(item.Subject, item.Latest);
            if (advised is null && skipped is null) return item;
            var how = skipped is not null ? DriverHow.None : item.How;
            if (advised is not null && how == DriverHow.None && item.Page is not null) how = DriverHow.Page;
            return item with { How = how, Skipped = skipped, Advised = advised };
        }).OrderByDescending(i => i.NeedsYou).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// A vendor's app Windows Update ships as a driver that only collects data or adds sound effects, never needed by its
    /// device (Microsoft: a software component is "not necessary for basic device functionality"): HP's Touchpoint
    /// Analytics (hpanalyticscomp.inf, borncity.com 2020-01-03) and A-Volute's Nahimic (neowin.net). Not offered.
    /// </summary>
    internal static bool Bloat(WindowsUpdateDrivers.Offer offer) =>
        $"{offer.Title} {offer.Model} {offer.Maker}" is var text
        && (text.Contains("Analytics", StringComparison.OrdinalIgnoreCase) || text.Contains("Telemetry", StringComparison.OrdinalIgnoreCase)
            || text.Contains("A-Volute", StringComparison.OrdinalIgnoreCase) || text.Contains("Nahimic", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether an offered driver is newer than what runs. A device on one of Windows' own generic drivers (an in-box INF, not
    /// oemN.inf) takes the maker's driver whatever its number (Windows' 10.0.26100.1 placeholder outnumbers every real one);
    /// a device on a maker's driver takes only a higher version, never an earlier date.
    /// </summary>
    internal static bool Newer(string? installed, string? installedDate, string? installedInf, string? offered, string? offeredDate)
    {
        if (VersionNumber.Parse(offered) is null) return false;
        if (!Oem(installedInf) || VersionNumber.Parse(installed) is null) return true;
        if (!VersionNumber.Newer(offered, installed)) return false;
        return installedDate is null || offeredDate is null || string.CompareOrdinal(offeredDate, installedDate) >= 0;
    }

    /// <summary>A driver package someone added (oem12.inf), not one of Windows' own INFs.</summary>
    internal static bool Oem(string? inf) =>
        inf is not null && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) && inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One list from the makers' rows and Windows Update's offers. <paramref name="gpuMakers"/>: makers whose graphics card
    /// Exo updates itself ("NVIDIA", "AMD", "Intel"); <paramref name="chipsetMaker"/>: whose chipset package it updates.
    /// Each maker row stays; a Windows Update offer is dropped when it's hidden, a hotfix, not newer than what runs, a
    /// graphics driver of a card Exo updates, a System device of the chipset Exo updates, or for a device a maker's row has
    /// unless it's newer than that row's newest and the row can't install. Rows that need you come first, then by name.
    /// </summary>
    internal static IReadOnlyList<DriverItem> Merge(IReadOnlyList<DriverItem> makers, IReadOnlyList<WindowsUpdateDrivers.Offer> offers,
        IReadOnlyCollection<string> gpuMakers, string? chipsetMaker)
    {
        var list = new List<DriverItem>(makers);
        foreach (var offer in offers.GroupBy(o => o.DeviceId ?? o.Id).Select(g => g.OrderByDescending(o => o.Version ?? "", VersionNumber.Order).First()))
        {
            if (offer.Hidden || Hotfix(offer.Title) || (offer.App && Bloat(offer))) continue;
            if (!Newer(offer.Installed, offer.InstalledDate, offer.Inf, offer.Version, offer.Date)) continue;
            if (offer.Class.Equals("Display", StringComparison.OrdinalIgnoreCase) && gpuMakers.Any(m => SameMaker(m, offer.Maker))) continue;
            if (chipsetMaker is not null && offer.Class.Equals("System", StringComparison.OrdinalIgnoreCase) && SameMaker(chipsetMaker, offer.Maker)) continue;
            var maker = offer.DeviceId is null ? -1 : list.FindIndex(i => i.Kind != "windows" && i.Id.EndsWith(":" + offer.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (maker >= 0)
            {
                var row = list[maker];
                if (row.How == DriverHow.Install || !VersionNumber.Newer(offer.Version, row.Latest ?? row.Version)) continue;
                list.RemoveAt(maker);
            }
            list.Add(offer.Item());
        }
        return [.. list.OrderByDescending(i => i.NeedsYou).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"NVIDIA" is "NVIDIA Corporation", "AMD" is "Advanced Micro Devices, Inc.", "Intel" is "Intel Corporation".</summary>
    internal static bool SameMaker(string maker, string? other) =>
        other is not null && (other.Contains(maker, StringComparison.OrdinalIgnoreCase)
            || (maker.Equals("AMD", StringComparison.OrdinalIgnoreCase) && other.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)));

    // ---- Reading ----

    /// <summary>
    /// Every row: each provider's, the BIOS, the drives, and Windows Update's offers merged in, against the advisories. A
    /// source that fails leaves its rows out.
    /// </summary>
    internal async Task<IReadOnlyList<DriverItem>> ReadAsync(CancellationToken ct)
    {
        var makers = new List<DriverItem>();
        var advisories = DriverAdvisories.Shared.RefreshAsync(ct);
        var wu = (WindowsUpdateDrivers?)providers.FirstOrDefault(p => p is WindowsUpdateDrivers);
        var offers = wu is null ? Task.FromResult<IReadOnlyList<WindowsUpdateDrivers.Offer>>([]) : wu.OffersAsync(ct);
        foreach (var provider in providers.Where(p => p is not WindowsUpdateDrivers))
        {
            try { makers.AddRange(await provider.ReadAsync(ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLog.Warn($"Drivers: {provider.GetType().Name} not read: {ex.Message}");
            }
        }
        if (await BiosAsync(ct).ConfigureAwait(false) is { } bios) makers.Add(bios);
        makers.AddRange(await Task.Run(Ssds, ct).ConfigureAwait(false));
        var gpuMakers = GraphicsService.Vendors(GpuCapability.Adapters()).Select(v => v switch { "nvidia" => "NVIDIA", "amd" => "AMD", _ => "Intel" }).ToList();
        var chipset = ChipsetUpdate.ThisPc?.Maker;
        var merged = Merge(makers, await offers.ConfigureAwait(false), gpuMakers, chipset);
        await advisories.ConfigureAwait(false);
        return Apply(merged, (subject, version) => DriverAdvisories.Shared.Against(subject, version));
    }

    /// <summary>
    /// Exo's start: the list read once in the background, after the first screen and the start's own work, so PC ›
    /// Drivers has it the moment it opens (each source keeps what it read: Windows Update 6 hours, the makers' pages 12).
    /// Not while a game runs: it waits for the next start or the room.
    /// </summary>
    internal async Task WarmAsync(Func<bool> gaming, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            if (gaming() || DriverInstallSignal.Installing) return;
            var read = await ReadAsync(ct).ConfigureAwait(false);
            AppLog.Info($"Drivers: read at start, {read.Count} row(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Drivers: not read at start: " + ex.Message); }
    }

    private static async Task<DriverItem?> BiosAsync(CancellationToken ct)
    {
        try
        {
            if (await BiosUpdate.ReadAsync(ct).ConfigureAwait(false) is not { } b) return null;
            // ASUS's page for the board even when it's current: a known problem with what runs opens it.
            var page = b.Page ?? BiosUpdate.SupportPage(PcAbout.Bios.Maker, PcAbout.Bios.Product);
            return new DriverItem("bios:board", "bios", "BIOS", b.Board, b.Version, b.Date, b.Latest, b.LatestDate,
                b.UpToDate == false ? DriverHow.Page : DriverHow.None, page, false) { Source = PcAbout.Maker(b.Board), Device = b.Board };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            AppLog.Info("Drivers: BIOS not read: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Each drive's firmware as PC › Drives reads it (<see cref="Drives"/>), with its maker's tool page (Exo never flashes
    /// firmware): it needs you when its maker lists newer firmware, or when what it runs has a known problem (<see cref="Apply"/>).
    /// </summary>
    private static List<DriverItem> Ssds()
    {
        try
        {
            return [.. Drives.ReadDisks().Select(d => Ssd(d.Number, d.Model, d.Firmware, Drives.NewerFirmware(d.Model, d.Firmware), Drives.Vendor(d.Model)))
                .OfType<DriverItem>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Info("Drivers: drives not read: " + ex.Message);
            return [];
        }
    }

    /// <summary>A drive's firmware row (null without a model or firmware to match): its maker's tool page when the maker lists newer firmware.</summary>
    internal static DriverItem? Ssd(int number, string? model, string? firmware, string? newer, (string Name, string Tool, string Url)? maker) =>
        model is null || firmware is null ? null
            : new DriverItem("ssd:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture), "ssd", model, maker?.Name, firmware, null, newer,
                null, newer is not null && maker is not null ? DriverHow.Page : DriverHow.None, maker?.Url, false) { Source = maker?.Tool, Device = model };

    // ---- Installing ----

    /// <summary>Why no driver can install now, or null: a game, a Discord call, a graphics driver or Exo update running.</summary>
    internal string? Busy() =>
        Running ? "Drivers are updating."
        : GpuDriverUpdate.Busy() is { } gpu ? gpu
        : DriverInstallSignal.Installing || GpuDriverUpdate.Updating is not null ? "A graphics driver update is running."
        : App.Services.Updater.Installing ? "Exo is updating."
        : providers.Select(p => p.Busy()).FirstOrDefault(b => b is not null);

    /// <summary>The provider that owns a row.</summary>
    internal IDriverProvider? Owner(DriverItem item) => item.Kind switch
    {
        "network" => providers.FirstOrDefault(p => p is NetworkDrivers),
        "windows" => providers.FirstOrDefault(p => p is WindowsUpdateDrivers),
        _ => null,
    };

    /// <summary>
    /// Installs every row Exo can install, one source after another: Windows Update's as one batch (one Windows prompt), then
    /// each maker's. Stops at the first refusal of Windows' prompt or when a game, a call or an update starts. Each result is
    /// reported as it lands; the last says whether a restart finishes them. A vendor's app installs only by its own row's
    /// Install (<paramref name="pressed"/>: the one row pressed), never in Update all.
    /// </summary>
    internal async Task<IReadOnlyList<(DriverItem Item, DriverResult Result)>> UpdateAllAsync(IReadOnlyList<DriverItem> items, Action<DriverItem, DriverResult?> step, CancellationToken ct, bool pressed = false)
    {
        if (Busy() is { } busy) return [(items.FirstOrDefault() ?? Empty, new DriverResult(false, busy))];
        if (Interlocked.Exchange(ref _running, 1) == 1) return [];
        var done = new List<(DriverItem, DriverResult)>();
        try
        {
            var installable = items.Where(i => i.How == DriverHow.Install && (pressed || !i.App)).ToList();
            var windows = installable.Where(i => i.Kind == "windows").ToList();
            if (windows.Count > 0 && providers.OfType<WindowsUpdateDrivers>().FirstOrDefault() is { } wu)
            {
                foreach (var item in windows) step(item, null);
                // Each driver of the batch says so as it lands; the batch's answer then says each again, final.
                foreach (var (item, result) in await wu.InstallBatchAsync(windows, ct, (item, result) => step(item, result)).ConfigureAwait(false))
                {
                    done.Add((item, result));
                    step(item, result);
                }
            }
            foreach (var item in installable.Where(i => i.Kind != "windows"))
            {
                if (Owner(item) is not { } owner) continue;
                if (StopReason() is { } stop) { done.Add((item, new DriverResult(false, stop))); step(item, done[^1].Item2); break; }
                step(item, null);
                var result = await owner.InstallAsync(item, null, ct).ConfigureAwait(false);
                done.Add((item, result));
                step(item, result);
            }
            if (done.Any(d => d.Item2.Restart)) _restart = true;
            return done;
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private static readonly DriverItem Empty = new("", "", "", null, null, null, null, null, DriverHow.None, null, false);

    /// <summary>A game, a call or an update that started mid-run stops the rest.</summary>
    private static string? StopReason() =>
        GpuDriverUpdate.Busy() ?? (App.Services.Updater.Installing ? "Exo is updating." : null);
}
