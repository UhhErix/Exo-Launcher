using ExoLauncher.Services;
using static ExoLauncher.Services.MouseCore;
using Device = ExoLauncher.Services.InterruptMode.Device;
using Mode = ExoLauncher.Services.InterruptMode.Mode;

namespace ExoLauncher.Tests;

/// <summary>
/// Spreading the other devices off the mouse's CPU, on a made-up PC: it can't be tried without changing one. The ways it could go wrong silently:
/// - a device lands on core 0, the mouse's core, a second thread, a pinned core or an E-core;
/// - Windows' drive's path, the mouse's or keyboard's USB controller, storage or another tool's device moves;
/// - a network adapter with several queues (RSS) moves, though its driver spreads its own work;
/// - a PC already spread is offered a change, or a PC with more than 64 CPUs one at all;
/// - on two CCDs something stays on the mouse's, or the graphics card moves on a PC with one;
/// - the backup is written after the change, a declined prompt leaves one, or Undo writes a value that wasn't there.
/// </summary>
public sealed class MouseLayoutTests : IDisposable
{
    private const string Net = @"PCI\VEN_8086&DEV_125C&SUBSYS_1&REV_04\A1";
    private const string Audio = @"PCI\VEN_1022&DEV_15E3&SUBSYS_1&REV_00\A2";
    private const string Hdmi = @"PCI\VEN_10DE&DEV_228B&SUBSYS_1&REV_A1\A3";
    private const string Gpu = @"PCI\VEN_10DE&DEV_2484&SUBSYS_1&REV_A1\A4";
    private const string Usb1 = @"PCI\VEN_1022&DEV_15B6&SUBSYS_1&REV_00\A5";
    private const string Usb2 = @"PCI\VEN_1022&DEV_15B7&SUBSYS_1&REV_00\A6";
    private const string Nvme = @"PCI\VEN_144D&DEV_A80C&SUBSYS_1&REV_00\A7";

    private readonly string _folder = Directory.CreateTempSubdirectory("exo-mouselayout-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    /// <summary>Two threads a core, cores 0 to 7: CPUs 0 to 15.</summary>
    private static Topology Cores(int count = 8, ulong[]? l3 = null) => new(
        Enumerable.Range(0, count).Select(i => new Core([i * 2, i * 2 + 1], 0)).ToList(),
        (l3 ?? [(1UL << (count * 2)) - 1]).Select(m => new Cache(m, 32_000_000)).ToList(), count * 2);

    private static Device Pci(string id, byte cls, byte sub, byte progIf, string name, uint messages = 1, ulong? affinity = 0xFFFF, int? policy = null, byte[]? mask = null, bool local = true) =>
        new(id, name, "drv", 0, cls, sub, progIf, 7, messages, 0x00FF2001, Mode.Msi, 1, null, policy, mask, false, affinity, local);

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private static List<Device> All() =>
    [
        Pci(Net, 0x02, 0, 0, "Ethernet"),
        Pci(Audio, 0x04, 0x03, 0, "HD Audio"),
        Pci(Hdmi, 0x04, 0x03, 0, "HDMI audio"),
        Pci(Gpu, 0x03, 0, 0, "RTX"),
        Pci(Usb1, 0x0C, 0x03, 0x30, "xHCI 1"),
        Pci(Usb2, 0x0C, 0x03, 0x30, "xHCI 2"),
    ];

    [Fact]
    public void Each_gets_a_core_of_its_own_and_usb_shares_one()
    {
        var moves = Suggest(All(), Cores(), 4, Nothing, 0);
        Assert.Equal([Net, Audio, Hdmi, Usb1, Usb2], moves.Select(m => m.Id));
        Assert.Equal(moves.First(m => m.Id == Usb1).Cpu, moves.First(m => m.Id == Usb2).Cpu);
        Assert.Equal(4, moves.Select(m => m.Cpu).Distinct().Count());
    }

    [Fact]
    public void No_device_lands_on_core_0_the_mouses_core_a_second_thread_or_a_pinned_core()
    {
        var pinned = 1UL << 6; // CPU 6, core 3
        var moves = Suggest(All(), Cores(), 4, Nothing, pinned);
        Assert.All(moves, m =>
        {
            Assert.True(m.Cpu % 2 == 0, "first thread");
            Assert.DoesNotContain(m.Cpu, new[] { 0, 1, 4, 5, 6, 7 });
        });
    }

    [Fact]
    public void Slower_cores_take_nothing()
    {
        var t = new Topology([new Core([0, 1], 1), new Core([2, 3], 1), new Core([4, 5], 1), new Core([6], 0), new Core([7], 0)], [new Cache(0xFF, 1)], 8);
        Assert.All(Suggest(All(), t, 2, Nothing, 0), m => Assert.Equal(4, m.Cpu));
    }

    [Fact]
    public void What_must_stay_stays()
    {
        var keep = new HashSet<string> { Usb1 };
        var list = All();
        list.Add(Pci(Nvme, 0x01, 0x08, 0x02, "NVMe"));
        Assert.DoesNotContain(Suggest(list, Cores(), 4, keep, 0), m => m.Id == Usb1 || m.Id == Nvme);
    }

    [Fact]
    public void A_network_adapter_with_several_queues_stays()
    {
        var list = new List<Device> { Pci(Net, 0x02, 0, 0, "Ethernet", messages: 8) };
        Assert.Empty(Suggest(list, Cores(), 4, Nothing, 0));
    }

    [Fact]
    public void Another_tools_device_a_docks_and_a_broken_one_stay()
    {
        Assert.Empty(Suggest([Pci(Audio, 0x04, 0x03, 0, "HD Audio", policy: 4, mask: [4, 0, 0, 0, 0, 0, 0, 0])], Cores(), 4, Nothing, 0));
        Assert.Empty(Suggest([Pci(Usb1, 0x0C, 0x03, 0x30, "Thunderbolt xHCI")], Cores(), 4, Nothing, 0));
        Assert.Empty(Suggest([Pci(Usb1, 0x0C, 0x03, 0x30, "xHCI", local: false)], Cores(), 4, Nothing, 0));
        Assert.Empty(Suggest([Pci(Net, 0x02, 0, 0, "Ethernet") with { Problem = 28 }], Cores(), 4, Nothing, 0));
    }

    [Fact]
    public void What_is_already_elsewhere_is_left_alone()
    {
        var list = new List<Device> { Pci(Audio, 0x04, 0x03, 0, "HD Audio", affinity: 1UL << 8), Pci(Usb2, 0x0C, 0x03, 0x30, "xHCI", affinity: 1UL << 10) };
        Assert.Empty(Suggest(list, Cores(), 4, Nothing, 0));
    }

    [Fact]
    public void Past_64_cpus_nothing_is_offered() => Assert.Empty(Suggest(All(), new Topology(Cores().Cores, [], 65), 4, Nothing, 0));

    [Fact]
    public void Out_of_cores_it_moves_what_it_can()
    {
        var moves = Suggest(All(), Cores(3), 2, Nothing, 0); // core 2 alone is free: the first in line takes it
        Assert.Equal(Net, Assert.Single(moves).Id);
    }

    [Fact]
    public void On_two_ccds_everything_leaves_the_mouses_and_the_graphics_card_moves_too()
    {
        // CCD one (with its V-Cache) CPUs 0 to 7 holds the mouse; CCD two CPUs 8 to 15.
        var moves = Suggest(All(), Cores(8, [0xFF, 0xFF00]), 4, Nothing, 0);
        Assert.Contains(moves, m => m.Id == Gpu);
        Assert.All(moves, m => Assert.InRange(m.Cpu, 8, 15));
    }

    [Fact]
    public void On_one_ccd_the_graphics_card_stays() => Assert.DoesNotContain(Suggest(All(), Cores(), 4, Nothing, 0), m => m.Id == Gpu);

    // ---- The change ----

    private MouseCore Make(out Devices devices, out Writer writer)
    {
        devices = new Devices { Items = All() };
        writer = new Writer(devices, Path.Combine(_folder, "b-layout.json"));
        return new MouseCore(devices, writer, Path.Combine(_folder, "b.json"), () => _boot, () => _now);
    }

    private DateTimeOffset _boot = DateTimeOffset.UtcNow.AddHours(-1);
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <summary>A mouse on Usb1 at 1 kHz with real late reports, its CPU 4 read from the controller's aim.</summary>
    private static Sample LateTest(Devices devices)
    {
        devices.Parents["HID\\M"] = Usb1;
        devices.Items[devices.Items.FindIndex(d => d.Id == Usb1)] = Pci(Usb1, 0x0C, 0x03, 0x30, "xHCI 1", affinity: 1UL << 4);
        var reports = new List<Report>();
        var at = 10_000_000L;
        for (var i = 0; i < 9000; i++)
        {
            at += i % 100 == 99 ? 40_000 : 10_000;
            reports.Add(new Report(at, 12, 0));
        }
        return new Sample(reports, "HID\\M", 10_000_000, 0, [], Cores());
    }

    [Fact]
    public void The_backup_comes_first_a_declined_prompt_leaves_none_and_undo_puts_back_what_was_there()
    {
        var core = Make(out var devices, out var writer);
        core.Measure(LateTest(devices));
        var offer = core.ReadLayout().Offer;
        Assert.NotEmpty(offer);

        writer.Decline = true;
        Assert.NotNull(core.ApplyLayout());
        Assert.False(File.Exists(writer.Path));

        writer.Decline = false;
        Assert.Null(core.ApplyLayout());
        Assert.True(writer.BackupWasThere);
        var state = core.ReadLayout();
        Assert.True(state.Restart);
        Assert.True(state.CanUndo);
        Assert.Empty(state.Offer);
        Assert.Equal(4, devices.Items.First(d => d.Id == Audio).Policy);

        Assert.Null(core.UndoLayout());
        var audio = devices.Items.First(d => d.Id == Audio);
        Assert.Null(audio.Policy);
        Assert.Null(audio.Override);
        Assert.False(core.ReadLayout().CanUndo);
    }

    [Fact]
    public void With_no_test_nothing_is_written()
    {
        var core = Make(out _, out var writer);
        Assert.NotNull(core.ApplyLayout());
        Assert.Equal(0, writer.Calls);
    }

    internal sealed class Devices : InterruptMode.IDevices
    {
        public List<Device> Items = [];
        public Dictionary<string, string> Parents = [];
        public IReadOnlyList<Device> List() => Items.ToList();
        public IReadOnlyList<string> BootDisks() => [Nvme];
        public string? Parent(string id) => Parents.GetValueOrDefault(id);
    }

    internal sealed class Writer(Devices devices, string path) : IWriter
    {
        public string Path { get; } = path;
        public bool Decline;
        public bool BackupWasThere;
        public int Calls;

        public string? Write(string id, int? policy, byte[]? mask) => WriteAll([(id, policy, mask)]);

        public string? WriteAll(IReadOnlyList<(string Id, int? Policy, byte[]? Mask)> values)
        {
            Calls++;
            BackupWasThere = File.Exists(Path);
            if (Decline) return "Windows' permission was declined, so nothing changed.";
            foreach (var (id, policy, mask) in values)
            {
                var at = devices.Items.FindIndex(d => d.Id == id);
                devices.Items[at] = devices.Items[at] with { Policy = policy, Override = mask };
            }
            return null;
        }
    }
}
