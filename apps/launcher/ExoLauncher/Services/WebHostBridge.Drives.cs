using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// system.drives: PC › Drives (<see cref="Drives"/>). No action reads the drives, the fix rows and the plan's NVMe power; space adds what fills
/// each drive (<see cref="DriveSpace"/>, kept ten minutes). cache, flush, optimize and all (the three, for Best settings) set what is
/// off with one Windows prompt. Each answer carries the drives as
/// they are after it. Before an install, <see cref="InstallRoomAsync"/> says when it would leave its drive nearly full.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly HashSet<string> DriveActions = ["cache", "flush", "optimize", "trim", "all"];

    private async Task<object> SystemDrivesAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var message = action is null ? null
            : DriveActions.Contains(action) ? await Task.Run(() => Drives.Change(action), ct).ConfigureAwait(false)
            : "Exo doesn't know that change.";
        var (disks, optimize, trim, plan) = await Task.Run(() => (Drives.ReadDisks(), Drives.ReadOptimize(), Drives.ReadTrim(), Drives.ReadPlanPower()), ct).ConfigureAwait(false);
        var fixes = Drives.Fixes(disks, optimize, Drives.LoadBackup(), trim);
        DriveSpace.Report? space = null;
        if (ReadBool(p, hasParams, "space") == true)
        {
            try { space = await DriveSpace.ReadAsync(_services.Library.PeekCachedLibrary(), ct).WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false); }
            catch (TimeoutException) { /* the drives show without it this time; the scan goes on for the next */ }
        }
        return new
        {
            ok = message is null,
            message,
            fixes = fixes.Select(f => new { key = f.Key, label = f.Label, detail = f.Detail, applied = f.Applied, restart = f.Restart }).ToList(),
            drives = disks.Select(d => DriveDto(d, space, plan)).ToList(),
        };
    }

    private static object DriveDto(Drives.Disk d, DriveSpace.Report? space, Drives.PlanPower? plan)
    {
        var letters = d.Volumes.Select(v => v.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = space?.Games.Where(g => letters.Contains(g.Letter)).OrderByDescending(g => g.Bytes).ToList() ?? [];
        var vendor = Drives.Vendor(d.Model);
        long On(IReadOnlyDictionary<string, long> bytes) => bytes.Where(b => letters.Contains(b.Key)).Sum(b => b.Value);
        object? used = null;
        if (space is not null)
        {
            long total = d.Volumes.Sum(v => v.Total), free = d.Volumes.Sum(v => v.Free);
            var caches = space.Caches.Where(c => letters.Contains(DriveSpace.LetterOf(c.Path))).Sum(c => c.Bytes) + On(space.DriverCaches);
            var old = space.Old.Where(c => letters.Contains(DriveSpace.LetterOf(c.Path))).Sum(c => c.Bytes);
            long gameBytes = games.Sum(g => g.Bytes), exo = On(space.Exo), leftovers = On(space.Leftovers);
            used = new { total, free, games = gameBytes, caches, oldCaches = old, exo, leftovers, other = Math.Max(0, total - free - gameBytes - caches - exo - leftovers) };
        }
        return new
        {
            id = d.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            model = d.Model,
            kind = d.Kind,
            link = d.Link is { } l ? new { gen = l.Gen, width = l.Width, maxGen = l.MaxGen, maxWidth = l.MaxWidth } : null,
            firmware = d.Firmware,
            firmwareSlots = d.FirmwareSlots,
            vendor = vendor is { } v ? new { name = v.Name, tool = v.Tool, url = v.Url } : null,
            temperature = d.Temperature is { } t ? new { now = t.NowC, warn = t.WarnC, critical = t.CriticalC } : null,
            health = d.Health is { } h ? new
            {
                lifeUsed = h.LifeUsed, spare = h.Spare, writtenTb = h.WrittenTb is { } tb ? Math.Round(tb, 1) : (double?)null, hours = h.Hours,
                powerCycles = h.PowerCycles, unsafeShutdowns = h.UnsafeShutdowns, mediaErrors = h.MediaErrors, badSectors = h.BadSectors,
            } : null,
            volumes = d.Volumes.Select(vol => new { letter = vol.Letter, totalGb = vol.Total / 1073741824.0, freeGb = vol.Free / 1073741824.0 }).ToList(),
            driver = Drives.DriverLabel(d.Driver),
            writeCache = d.Cache is { On: true } ? true : d.Cache is { Off: true } ? false : (bool?)null,
            build = Drives.Build(d),
            headroom = (int)Math.Round(Drives.LowFreeFor(d) * 100),
            // Full power is the plan's on NVMe under StorNVMe; on battery the laptop's own choice stands and nothing is said.
            power = d.Bus == Drives.Bus.Nvme && (d.Nvme?.PowerState is not null || plan is { Battery: false }) ? new
            {
                state = d.Nvme?.PowerState,
                full = plan is { Battery: false } ? plan.Full : (bool?)null,
                plan = plan is { Battery: false } ? plan.Plan : null,
                exo = plan is { Battery: false, Exo: true },
                apst = d.Nvme?.ApstOn,
            } : null,
            nvme = d.Nvme is { } n ? new { hmbMb = n.HmbBytes > 0 ? n.HmbBytes >> 20 : (long?)null, hmbOn = n.HmbOn, sector = n.SectorBytes, fourK = n.FourK } : null,
            space = used,
            games = games.Take(6).Select(g => new { id = g.Id, title = g.Title, bytes = g.Bytes, cacheBytes = g.CacheBytes }).ToList(),
            verdicts = Drives.Verdicts(d, games, DriveHeat.SlowSeen(d.InstanceId, d.Link)).Select(v => new { key = v.Key, text = v.Text, bad = v.Bad }).ToList(),
        };
    }

    /// <summary>
    /// Before an install: when it would leave its drive under its headroom (<see cref="Drives.LowFreeFor"/>: 25% on QLC,
    /// 15% without DRAM, 10% with it, else 12%) or not fit, the question for Exo's window, naming the roomiest SSD that
    /// stays above its own. Epic, GOG and Amazon install into Exo's install folder; Steam to its own
    /// library, so a Steam game is judged on the drive of its only library (with several, Steam picks, and nothing is
    /// said); the other stores' apps choose for themselves, and nothing is said. The size is the store's, else for Steam the
    /// storage its page asks for (Steam's store, asked for at most 3 s). Null when there's nothing to say or no size.
    /// </summary>
    private async Task<(bool Fits, string Words)?> InstallRoomAsync(GameEntry game, string root)
    {
        // Only stores that install where Exo says (Epic, GOG and Amazon into its install folder) or into a library Exo can
        // read (Steam): the others' own apps choose the place.
        if (game.Store is not (StoreKind.Steam or StoreKind.Epic or StoreKind.Gog or StoreKind.Amazon)) return null;
        try
        {
            var target = root;
            if (game.Store == StoreKind.Steam)
            {
                var libraries = Adapters.SteamInstallation.ResolveRoot() is { } steam ? SteamLeftoverCleanup.CollectLibraries(steam).ToList() : [];
                if (libraries.Count != 1) return null;
                target = libraries[0];
            }
            var size = game.SizeBytes ?? 0;
            if (size <= 0 && game.Store == StoreKind.Steam && game.LaunchTarget is { } appId)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { size = StoreService.StorageBytes(await _services.Store.GameAsync(appId, limit.Token).ConfigureAwait(true)) ?? 0; }
                catch (OperationCanceledException) { /* no size: nothing to say */ }
            }
            var letter = DriveSpace.LetterOf(target);
            if (size <= 0 || letter.Length == 0) return null;
            var disks = await Task.Run(Drives.ReadDisks).ConfigureAwait(true);
            if (disks.SelectMany(d => d.Volumes.Select(v => (Volume: v, Disk: d))).FirstOrDefault(p => p.Volume.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase)) is not { Volume: { } volume, Disk: { } disk }
                || !Drives.LeavesLow(volume.Free, volume.Total, size, Drives.LowFreeFor(disk))) return null;
            var left = volume.Free - size;
            var slows = Drives.Build(disk) switch
            {
                "QLC" => $"a QLC drive like this one writes much slower with under {Math.Round(Drives.LowFreeFor(disk) * 100)}% free",
                "no DRAM" => $"a drive without its own DRAM like this one slows down with under {Math.Round(Drives.LowFreeFor(disk) * 100)}% free",
                _ => "an SSD slows down when it's nearly full",
            };
            var after = left < 0 ? $"There isn't room for it on {letter}: it needs {Gb(size)} and {letter} has {Gb(volume.Free)} free."
                : $"It would leave {letter} with {Math.Max(0, (int)Math.Round(left * 100.0 / volume.Total))}% free ({Gb(left)}), and {slows}. Store updates need room to patch, too.";
            var best = Drives.BestFor(disks, size, letter) is { } other
                ? game.Store == StoreKind.Steam ? $" {other.Volume.Letter} has {Gb(other.Volume.Free)} free: add a Steam library there to put it on {other.Volume.Letter}."
                    : $" {other.Volume.Letter} has {Gb(other.Volume.Free)} free: choose a folder there in Settings › Downloads."
                : "";
            return (left >= 0, after + best);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Helpers.AppLog.Debug("Install room: " + ex.Message);
            return null;
        }
    }

    private static string Gb(long bytes) => bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.0} TB" : $"{Math.Max(1, bytes >> 30)} GB";
}
