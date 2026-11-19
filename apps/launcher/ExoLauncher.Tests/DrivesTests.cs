using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways PC › Drives could go wrong silently:
/// - the storage driver's answers are misread (a field off by a byte, Kelvin left as Kelvin, a 128-bit counter's high
///   half read, an unset temperature limit shown as 0 °C), so health, wear or temperature are wrong;
/// - flushing is called off on a drive whose cache has power-loss protection, or on with Windows' switch set;
/// - a DirectStorage answer is misread (an OK read as blocked, a reason cut short or run past its length);
/// - a SATA drive's SMART attributes are misread (the 6-byte raw count, the wear value, the temperature byte);
/// - a link at rest (fewer GT/s to save power) is called slow, or a lane-short link isn't;
/// - known-bad firmware is missed, or a fixed version, another line (the 4 TB 990 PRO) or a newer version is flagged;
///   WD's host-memory crash is said on Windows before 24H2, on another capacity, or on the fixed firmware;
/// - Identify or Identify Namespace is misread (HMB in pages not bytes, the 4 KB format with metadata taken as plain 4K);
/// - write caching is called off on a drive without a cache or one that can't change it, or offered on a USB drive;
/// - a drive's headroom ignores what it's built from (QLC, no DRAM), or Full power is claimed when the plan lets it idle;
/// - a fix row nags after Exo fixed it, loses its Undo, or forgets that a restart finishes it;
/// - a device id that cmd could read specially reaches reg.exe;
/// - an install is warned about when it leaves room, not warned when it doesn't fit, or pointed at a hard drive;
/// - a session is said to be hot when it wasn't, or a drive that slowed itself isn't said;
/// - a shader cache is called old while its program is there, while its drive is unplugged, or a second copy in use
///   this month is, or Exo Shaders' compile copy's is (its fold decides); a cache is put on the wrong game (a folder name
///   that only starts the same).
/// </summary>
public sealed class DrivesTests
{
    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace("-", ""));

    // ---- Parsers, with this PC's own answers (a Samsung 990 PRO with Heatsink 2TB) ----

    [Fact]
    public void Smart_log_reads_wear_counts_and_thermal_counters()
    {
        var log = new byte[512];
        log[0] = 0; log[3] = 100; log[4] = 10; log[5] = 5;
        BitConverter.GetBytes(246167856UL).CopyTo(log, 48);
        BitConverter.GetBytes(527UL).CopyTo(log, 112);
        BitConverter.GetBytes(4109UL).CopyTo(log, 128);
        BitConverter.GetBytes(132UL).CopyTo(log, 144);
        BitConverter.GetBytes(2U).CopyTo(log, 216);
        BitConverter.GetBytes(1U).CopyTo(log, 220);
        BitConverter.GetBytes(30U).CopyTo(log, 224);
        BitConverter.GetBytes(10U).CopyTo(log, 228);
        var h = Drives.ParseSmartLog(log)!;
        Assert.Equal(5, h.LifeUsed);
        Assert.Equal(100, h.Spare);
        Assert.Equal(126.0, h.WrittenTb!.Value, 0);
        Assert.Equal(4109, h.Hours);
        Assert.Equal(527, h.PowerCycles);
        Assert.Equal(132, h.UnsafeShutdowns);
        Assert.Equal(0, h.MediaErrors);
        Assert.Equal(3, h.ThrottleEvents);
        Assert.Equal(40, h.ThrottleSeconds);
        Assert.Null(Drives.ParseSmartLog(new byte[100]));
    }

    [Fact]
    public void Identify_reads_slots_and_limits_in_celsius()
    {
        var id = new byte[4096];
        id[260] = 22; // 3 slots, activation without reset
        BitConverter.GetBytes((ushort)355).CopyTo(id, 266);
        BitConverter.GetBytes((ushort)358).CopyTo(id, 268);
        Assert.Equal(new Drives.Identity(3, 82, 85, PowerStates: 1), Drives.ParseIdentify(id));
        Assert.Equal(new Drives.Identity(0, null, null, PowerStates: 1), Drives.ParseIdentify(new byte[4096]));
        Assert.Null(Drives.ParseIdentify(new byte[300]));
        // A DRAM-less drive: five power states, APST, 64 MB of host memory preferred (16,384 pages of 4 KiB), a volatile cache.
        id[263] = 4; id[265] = 1; id[525] = 1;
        BitConverter.GetBytes(16384U).CopyTo(id, 272);
        var dramless = Drives.ParseIdentify(id)!;
        Assert.Equal(5, dramless.PowerStates);
        Assert.True(dramless.Apst && dramless.VolatileCache);
        Assert.Equal(64L << 20, dramless.HmbBytes);
    }

    [Fact]
    public void Namespace_reads_the_sector_size_and_whether_4k_exists()
    {
        var ns = new byte[4096];
        ns[25] = 1; // two formats
        ns[26] = 0; // the first in use
        BitConverter.GetBytes(9U << 16).CopyTo(ns, 128);         // 512 bytes
        BitConverter.GetBytes((12U << 16) | (1U << 24)).CopyTo(ns, 132); // 4096 bytes, no metadata
        Assert.Equal((512, true), Drives.ParseNamespace(ns)!.Value);
        ns[26] = 1;
        Assert.Equal((4096, true), Drives.ParseNamespace(ns)!.Value);
        // 4 KB with 8 bytes of metadata per sector isn't a plain 4K format.
        BitConverter.GetBytes((12U << 16) | 8U).CopyTo(ns, 132);
        ns[26] = 0;
        Assert.Equal((512, false), Drives.ParseNamespace(ns)!.Value);
        ns[26] = 5; // past the formats it has
        Assert.Null(Drives.ParseNamespace(ns));
        Assert.Equal(3, Drives.PowerStateOf(0x23)); // workload hint bits above the state
    }

    [Fact]
    public void Temperature_reads_the_composite_against_the_drive_limits()
    {
        var t = Drives.ParseTemperature(Hex("28-00-00-00-48-00-00-00-55-00-52-00-03-00-00-00-00-00-00-00-00-00-00-00-00-00-23-00-52-00-00-80-01-01-01-00-00-00-00-00-01-00-23-00-EE-FE-00-80-01-01-01-00-00-00-00-00-02-00-28-00-EE-FE-00-80-01-01-01-00-00-00-00-00"))!;
        Assert.Equal(new Drives.Temperature(35, 82, 85), t);
        var none = new byte[40];
        Assert.Null(Drives.ParseTemperature(none));
    }

    [Fact]
    public void Flushing_off_only_on_a_cache_without_power_protection()
    {
        var mine = Hex("1C-00-00-00-1C-00-00-00-02-00-00-00-02-00-00-00-00-00-00-00-02-00-00-00-01-01-00-00");
        Assert.Equal(new Drives.WriteCache(true, true), Drives.ParseWriteCache(mine));
        var protectedCache = (byte[])mine.Clone();
        protectedCache[26] = 1; // NVCacheEnabled: power-loss protection
        Assert.Equal(new Drives.WriteCache(true, false), Drives.ParseWriteCache(protectedCache));
        var stock = (byte[])mine.Clone();
        stock[25] = 0;
        Assert.Equal(new Drives.WriteCache(true, false), Drives.ParseWriteCache(stock));
        // Caching disabled (1): off, unless the drive has no cache (type None) or says it can't change (changeable 1).
        var off = (byte[])stock.Clone();
        off[12] = 1;
        Assert.Equal(new Drives.WriteCache(false, false, true), Drives.ParseWriteCache(off));
        var none = (byte[])off.Clone();
        none[8] = 1;
        Assert.False(Drives.ParseWriteCache(none)!.Off);
        var fixedCache = (byte[])off.Clone();
        fixedCache[16] = 1;
        Assert.False(Drives.ParseWriteCache(fixedCache)!.Off);
    }

    private static byte[] Bypass(uint status, string driver, string reason)
    {
        var o = new byte[352];
        BitConverter.GetBytes(3).CopyTo(o, 0);
        BitConverter.GetBytes(status).CopyTo(o, 24);
        BitConverter.GetBytes((ushort)driver.Length).CopyTo(o, 28);
        Encoding.Unicode.GetBytes(driver).CopyTo(o, 30);
        BitConverter.GetBytes((ushort)reason.Length).CopyTo(o, 94);
        Encoding.Unicode.GetBytes(reason).CopyTo(o, 96);
        return o;
    }

    [Fact]
    public void Bypass_reads_the_blocking_driver_and_reason()
    {
        Assert.Null(Drives.ParseBypass(Bypass(0, "", "")));
        Assert.Equal(new Drives.Blocked("ntfs.sys", "Sparse file not supported"), Drives.ParseBypass(Bypass(0xC00004C4, "ntfs.sys", "Sparse file not supported")));
        Assert.Equal("its files are sparse", Drives.Words(new("ntfs.sys", "Sparse file not supported")));
        Assert.Equal("EasyAntiCheat's driver doesn't support it", Drives.Words(new("EOSSys.sys", "Not supported")));
        Assert.Equal("acme.sys says “Volume not supported”", Drives.Words(new("acme.sys", "Volume not supported.")));
        Assert.Null(Drives.ParseBypass(new byte[100]));
    }

    private static byte[] Ata(params (byte Id, byte Value, long Raw)[] attributes)
    {
        var d = new byte[512];
        for (var i = 0; i < attributes.Length; i++)
        {
            var at = 2 + i * 12;
            d[at] = attributes[i].Id;
            d[at + 3] = attributes[i].Value;
            BitConverter.GetBytes(attributes[i].Raw).AsSpan(0, 6).CopyTo(d.AsSpan(at + 5));
        }
        return d;
    }

    [Fact]
    public void Ata_smart_reads_hours_wear_bad_sectors_and_temperature()
    {
        var (h, temperature) = Drives.ParseAtaSmart(Ata((9, 99, 12345), (12, 99, 800), (5, 100, 8), (197, 100, 2), (231, 93, 0), (194, 64, 0x2A0012002E), (174, 99, 41)), failing: false)!.Value;
        Assert.Equal(12345, h.Hours);
        Assert.Equal(800, h.PowerCycles);
        Assert.Equal(10, h.BadSectors);
        Assert.Equal(7, h.LifeUsed);
        Assert.Equal(41, h.UnsafeShutdowns);
        Assert.Equal(46, temperature);
        var hdd = Drives.ParseAtaSmart(Ata((9, 99, 100), (192, 100, 7)), failing: true)!.Value.Health;
        Assert.Null(hdd.LifeUsed);
        Assert.Equal(7, hdd.UnsafeShutdowns);
        Assert.True(hdd.Failing);
        Assert.Null(hdd.BadSectors);
        Assert.Null(Drives.ParseAtaSmart(new byte[512], false));
    }

    [Fact]
    public void Revision_is_the_firmware_on_every_bus()
    {
        var d = new byte[64];
        BitConverter.GetBytes(40).CopyTo(d, 20);
        Encoding.ASCII.GetBytes("8B2QJXD7").CopyTo(d, 40);
        Assert.Equal("8B2QJXD7", Drives.ParseRevision(d));
        Assert.Null(Drives.ParseRevision(new byte[64]));
    }

    // ---- Verdicts ----

    private static Drives.Disk Nvme(Drives.Link? link = null, Drives.Health? health = null, Drives.WriteCache? cache = null, string firmware = "8B2QJXD7",
        string model = "Samsung SSD 990 PRO with Heatsink 2TB", string? driver = "stornvme", Drives.Temperature? temperature = null) =>
        new(0, model, Drives.Bus.Nvme, "NVMe SSD", firmware, 3, link ?? new(4, 4, 4, 4), driver, health ?? new(LifeUsed: 5, Spare: 100, SpareThreshold: 10),
            temperature ?? new(36, 82, 85), cache ?? new(true, false), [new("C:", 2_000_000_000_000, 300_000_000_000)], @"SCSI\Disk&Ven_NVMe&Prod_Samsung_SSD_990\5&33c33320&0&000000", 0);

    private static IEnumerable<string> Keys(Drives.Disk d, IReadOnlyList<Drives.Game>? games = null, bool slowSeen = false) =>
        Drives.Verdicts(d, games ?? [], slowSeen).Select(v => v.Key);

    [Fact]
    public void A_healthy_drive_says_nothing()
    {
        Assert.Empty(Keys(Nvme()));
    }

    [Fact]
    public void Fewer_lanes_always_speak_a_slower_speed_only_once_seen_under_play()
    {
        Assert.Contains("lanes", Keys(Nvme(link: new(4, 2, 4, 4))));
        Assert.DoesNotContain("speed", Keys(Nvme(link: new(3, 4, 4, 4))));
        var slow = Drives.Verdicts(Nvme(link: new(3, 4, 4, 4)), [], slowSeen: true).Single();
        Assert.Equal("Its M.2 slot or a BIOS setting holds it back.", slow.Text);
    }

    [Fact]
    public void Flushing_off_names_the_unsafe_shutdowns()
    {
        var v = Drives.Verdicts(Nvme(cache: new(true, true), health: new(LifeUsed: 5, Spare: 100, SpareThreshold: 10, UnsafeShutdowns: 132)), [], false).Single();
        Assert.Equal("A power cut can damage files: write-cache flushing is off.", v.Text);
    }

    [Fact]
    public void Faults_and_wear_are_bad()
    {
        Assert.Contains("failing", Keys(Nvme(health: new(CriticalWarning: 0x04))));
        Assert.Contains("readonly", Keys(Nvme(health: new(CriticalWarning: 0x08))));
        Assert.Contains("spare", Keys(Nvme(health: new(Spare: 5, SpareThreshold: 10))));
        Assert.Contains("media", Keys(Nvme(health: new(MediaErrors: 3))));
        Assert.Contains("worn", Keys(Nvme(health: new(LifeUsed: 92))));
        Assert.DoesNotContain("worn", Keys(Nvme(health: new(LifeUsed: 89))));
        Assert.Contains("hot", Keys(Nvme(temperature: new(83, 82, 85))));
    }

    [Fact]
    public void Known_bad_firmware_and_newer_firmware()
    {
        Assert.Contains("firmware", Keys(Nvme(firmware: "0B2QJXD7")));
        Assert.Contains("1B2QJXD7", Drives.KnownBad("Samsung SSD 990 PRO 2TB", "0B2QJXD7"));
        Assert.Contains("5B2QGXA7", Drives.KnownBad("Samsung SSD 980 PRO 2TB", "3B2QGXA7"));
        Assert.Null(Drives.KnownBad("Samsung SSD 980 PRO 2TB", "5B2QGXA7"));
        Assert.Equal("8B2QJXD7", Drives.NewerFirmware("Samsung SSD 990 PRO 1TB", "4B2QJXD7"));
        Assert.Null(Drives.NewerFirmware("Samsung SSD 990 PRO 1TB", "8B2QJXD7"));
        Assert.Null(Drives.NewerFirmware("Samsung SSD 990 PRO 1TB", "9B2QJXD7"));
        // The 4 TB runs its own line.
        Assert.Null(Drives.NewerFirmware("Samsung SSD 990 PRO 4TB", "0B2QJXG7"));
        Assert.Null(Drives.KnownBad("Samsung SSD 990 PRO 4TB", "0B2QJXD7"));
        // 990 PRO: any version of the line before 1B2QJXD7 is the bad one, and 1B2QJXD7 onward isn't.
        Assert.Null(Drives.KnownBad("Samsung SSD 990 PRO with Heatsink 2TB", "8B2QJXD7"));
        Assert.Equal("Samsung Magician", Drives.Vendor("Samsung SSD 990 PRO 2TB")!.Value.Tool);
        Assert.Equal("SanDisk", Drives.Vendor("WD_BLACK SN850X 2000GB")!.Value.Name);
        Assert.Null(Drives.Vendor("Generic NVMe"));
    }

    [Fact]
    public void Hmb_firmware_only_on_the_2tb_models_older_than_the_fix_on_24h2_and_later()
    {
        const int h24 = 26100, h23 = 22631;
        var line = Drives.KnownBad("WD_BLACK SN770 2TB", "731120WD", h24)!;
        Assert.Contains("731130WD", line);
        Assert.Contains("SanDisk Dashboard", line);
        Assert.NotNull(Drives.KnownBad("WD_BLACK SN770M 2TB", "731030WD", 26300));
        Assert.NotNull(Drives.KnownBad("WD Blue SN580 2TB", "281040WD", h24));
        Assert.NotNull(Drives.KnownBad("WD Blue SN5000 2TB", "291010WD", h24));
        // The fixed version, a newer one, Windows before 24H2, another capacity, another line: nothing.
        Assert.Null(Drives.KnownBad("WD_BLACK SN770 2TB", "731130WD", h24));
        Assert.Null(Drives.KnownBad("WD_BLACK SN770 2TB", "731150WD", h24));
        Assert.Null(Drives.KnownBad("WD_BLACK SN770 2TB", "731120WD", h23));
        Assert.Null(Drives.KnownBad("WD_BLACK SN770 1TB", "731120WD", h24));
        Assert.Null(Drives.KnownBad("WD Blue SN580 2TB", "731120WD", h24));
        Assert.Null(Drives.KnownBad("WD_BLACK SN850X 2000GB", "620311WD", h24));
    }

    [Fact]
    public void Headroom_by_what_the_drive_is_built_from()
    {
        Assert.True(Drives.Qlc("Samsung SSD 870 QVO 1TB"));
        Assert.True(Drives.Qlc("INTEL SSDPEKNW010T8"));
        Assert.True(Drives.Qlc("SOLIDIGM SSDPFKNU010TZ"));
        Assert.True(Drives.Qlc("CT1000P1SSD8"));
        Assert.False(Drives.Qlc("Samsung SSD 870 EVO 1TB"));
        Assert.False(Drives.Qlc("Samsung SSD 990 PRO 2TB"));
        var qvo = new Drives.Disk(1, "Samsung SSD 870 QVO 1TB", Drives.Bus.Sata, "SSD", null, null, null, "storahci", null, null, null, [new("D:", 1000, 200)], null);
        Assert.Equal(0.25, Drives.LowFreeFor(qvo));
        Assert.Equal("QLC", Drives.Build(qvo));
        Assert.Equal(0.15, Drives.LowFreeFor(Nvme() with { Nvme = new(HmbBytes: 64L << 20) }));
        Assert.Equal(0.10, Drives.LowFreeFor(Nvme() with { Nvme = new() }));
        Assert.Equal(Drives.LowFree, Drives.LowFreeFor(Nvme()));
        // 20% free: fine on a TLC drive with DRAM, too little on QLC.
        Assert.DoesNotContain("full", Keys(Nvme() with { Nvme = new(), Volumes = [new("C:", 1000, 200)] }));
        Assert.Equal("D: is nearly full, which slows it down.", Drives.Verdicts(qvo, [], false).Single().Text);
        Assert.False(Drives.LeavesLow(300, 1000, 100, 0.10));
        Assert.True(Drives.LeavesLow(300, 1000, 100, 0.25));
    }

    [Fact]
    public void Full_power_when_the_plan_keeps_nvme_out_of_idle_states()
    {
        Assert.True(Drives.FullPower(0, 0, 200, 2000));     // Exo's plan and High performance: tolerances 0
        Assert.True(Drives.FullPower(100, 200, 0, 0));      // no idle timeout: never leaves PS0
        Assert.False(Drives.FullPower(100, 200, 200, 2000)); // Balanced
        Assert.False(Drives.FullPower(0, 200, 200, 2000));
        Assert.Null(Drives.FullPower(null, null, null, null));
    }

    [Fact]
    public void Driver_label_names_windows_own_too()
    {
        Assert.Equal("Microsoft NVMe (StorNVMe)", Drives.DriverLabel("stornvme"));
        Assert.Equal("Microsoft native NVMe", Drives.DriverLabel("nvmedisk"));
        Assert.Equal("Intel RST", Drives.DriverLabel("iaStorVD"));
        Assert.Null(Drives.DriverLabel(null));
    }

    [Fact]
    public void Driver_speaks_only_when_it_is_not_windows_own()
    {
        Assert.Null(Drives.DriverName("stornvme"));
        Assert.Null(Drives.DriverName("nvmedisk"));
        Assert.Equal("Intel RST", Drives.DriverName("iaStorVD"));
        Assert.Equal("Samsung's NVMe driver", Drives.DriverName("secnvme"));
        Assert.Contains("driver", Keys(Nvme(driver: "iaStorAC")));
    }

    [Fact]
    public void Games_on_a_hard_drive_and_off_the_fast_path()
    {
        var hdd = new Drives.Disk(1, "WDC WD20EZRZ", Drives.Bus.Sata, "Hard drive", "80.00A80", null, null, "storahci", null, null, null, [new("D:", 2_000_000_000_000, 400_000_000_000)], null);
        var games = new Drives.Game[] { new("a", "Elden Ring", "D:", 50), new("b", "Hades", "D:", 10) };
        Assert.Equal("2 games load slowly from this hard drive.", Drives.Verdicts(hdd, games, false).Single().Text);
        var blocked = new Drives.Game("ff", "FINAL FANTASY XVI", "C:", 100, DirectStorage: true, Blocked: new("ntfs.sys", "Sparse file not supported"), BlockedFiles: 4);
        Assert.Equal("FINAL FANTASY XVI can't load through DirectStorage's fast path: its files are sparse.", Drives.Verdicts(Nvme(), [blocked], false).Single().Text);
        // Sparse files in a game that doesn't load through DirectStorage cost nothing: not said.
        Assert.Empty(Keys(Nvme(), [blocked with { DirectStorage = false }]));
    }

    // ---- Fixes ----

    [Fact]
    public void Flush_row_asks_then_waits_for_a_restart_then_keeps_its_undo()
    {
        var off = Nvme(cache: new(true, true), health: new(UnsafeShutdowns: 132)) with { FlushSetting = 1 };
        var asked = Drives.Fixes([off], new(false, false), null).Single();
        Assert.False(asked.Applied);
        Assert.Contains("132 times", asked.Detail);
        var backup = new Drives.Backup([new(off.InstanceId!, 1)], null);
        var waiting = Drives.Fixes([off with { FlushSetting = 0 }], new(false, false), backup).Single();
        Assert.True(waiting.Applied && waiting.Restart);
        var done = Drives.Fixes([off with { FlushSetting = 0, Cache = new(true, false) }], new(false, false), backup).Single();
        Assert.True(done.Applied && !done.Restart);
        Assert.Empty(Drives.Fixes([Nvme()], new(false, false), null));
    }

    [Fact]
    public void Write_caching_row_inside_the_pc_only_then_waits_for_a_restart_then_keeps_its_undo()
    {
        var off = Nvme(cache: new(false, false, true));
        Assert.Contains("cache", Keys(off));
        var asked = Drives.Fixes([off], new(false, false), null).Single();
        Assert.Equal(("cache", false), (asked.Key, asked.Applied));
        var backup = new Drives.Backup([], null, [new(off.InstanceId!, null)]);
        var waiting = Drives.Fixes([off with { CacheSetting = 1 }], new(false, false), backup).Single();
        Assert.True(waiting.Applied && waiting.Restart);
        var done = Drives.Fixes([off with { CacheSetting = 1, Cache = new(true, false) }], new(false, false), backup).Single();
        Assert.True(done.Applied && !done.Restart);
        // A USB drive's write caching off is Windows' Quick removal: left alone.
        var usb = off with { Bus = Drives.Bus.Usb, Kind = "USB drive" };
        Assert.Empty(Drives.Fixes([usb], new(false, false), null));
        Assert.DoesNotContain("cache", Keys(usb));
        var lines = Drives.Commands("reg.exe", "schtasks.exe", [], null, [(off.InstanceId!, 1), (off.InstanceId!, null)])!;
        Assert.Contains("/v UserWriteCacheSetting /t REG_DWORD /d 1", lines[0]);
        Assert.Contains("delete", lines[1]);
        Assert.Null(Drives.Commands("reg.exe", "schtasks.exe", [], null, [(@"SCSI\Disk"" & calc & ""\x", 1)]));
    }

    [Fact]
    public void Optimize_row_for_a_task_off_or_maintenance_off()
    {
        Assert.Contains("Automatic Maintenance", Drives.Fixes([], new(false, true), null).Single().Detail);
        Assert.Contains("scheduled optimize", Drives.Fixes([], new(true, false), null).Single().Detail);
        Assert.Contains("optimize service", Drives.Fixes([], new(false, false, true), null).Single().Detail);
        Assert.True(Drives.Fixes([], new(false, false), new([], new(false, true))).Single().Applied);
        Assert.False(Drives.Fixes([], new(false, false, true), new([], new(false, true))).Single().Applied);
        Assert.Empty(Drives.Fixes([], new(false, false), null));
    }

    [Fact]
    public void Commands_only_for_ids_windows_names()
    {
        var lines = Drives.Commands("reg.exe", "schtasks.exe", [(@"SCSI\Disk&Ven_NVMe&Prod_Samsung_SSD_990\5&33c33320&0&000000", 0)], new(true, true))!;
        Assert.Equal(3, lines.Count);
        Assert.Contains("/v CacheIsPowerProtected /t REG_DWORD /d 0", lines[0]);
        Assert.Contains("/ENABLE", lines[1]);
        Assert.Contains("delete", lines[2]);
        var service = Drives.Commands("reg.exe", "schtasks.exe", [], new(false, false, true))!;
        Assert.Equal("\"sc.exe\" config defragsvc start= demand", Assert.Single(service));
        Assert.Null(Drives.Commands("reg.exe", "schtasks.exe", [(@"SCSI\Disk"" & calc & ""\x", 0)], null));
        Assert.Null(Drives.Commands("reg.exe", "schtasks.exe", [(@"SCSI\Disk\1", 5)], null));
    }

    [Fact]
    public void Task_xml_off_only_when_its_settings_say_so()
    {
        Assert.False(Drives.TaskXmlOff(Encoding.Unicode.GetBytes("<?xml?><Task><Settings><Enabled>true</Enabled></Settings></Task>")));
        Assert.True(Drives.TaskXmlOff(Encoding.UTF8.GetBytes("<Task><Settings><Enabled>false</Enabled></Settings></Task>")));
        Assert.False(Drives.TaskXmlOff(Encoding.UTF8.GetBytes("<Task><Settings><UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine></Settings><Triggers><Enabled>false</Enabled></Triggers></Task>")));
        Assert.Null(Drives.TaskXmlOff(Encoding.UTF8.GetBytes("ERROR: The system cannot find the file specified.")));
    }

    // ---- Install-time advice ----

    [Fact]
    public void Install_advice_when_under_twelve_percent_and_the_best_ssd()
    {
        const long tb = 1_000_000_000_000, gb = 1_000_000_000;
        Assert.False(Drives.LeavesLow(500 * gb, tb, 100 * gb));
        Assert.True(Drives.LeavesLow(200 * gb, tb, 100 * gb));
        Assert.True(Drives.LeavesLow(50 * gb, tb, 100 * gb));
        var c = Nvme() with { Volumes = [new("C:", tb, 150 * gb)] };
        var d = new Drives.Disk(1, "Crucial MX500", Drives.Bus.Sata, "SSD", null, null, null, "storahci", null, null, null, [new("D:", 2 * tb, 1200 * gb)], null);
        var e = new Drives.Disk(2, "WDC WD40", Drives.Bus.Sata, "Hard drive", null, null, null, "storahci", null, null, null, [new("E:", 4 * tb, 3500 * gb)], null);
        Assert.Equal("D:", Drives.BestFor([c, d, e], 100 * gb, "C:")!.Value.Volume.Letter);
        Assert.Null(Drives.BestFor([c, e], 100 * gb, "C:"));
    }

    // ---- A game's session ----

    [Fact]
    public void Heat_is_said_near_the_warning_point_or_when_the_drive_slowed()
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.Null(Drives.HeatWorthSaying(60, 82, 0, 0, at));
        Assert.NotNull(Drives.HeatWorthSaying(78, 82, 0, 0, at));
        Assert.Equal(40, Drives.HeatWorthSaying(70, 82, 40, 1, at)!.SlowedSeconds);
        Assert.Null(Drives.HeatWorthSaying(78, null, 0, 0, at));
    }

    // ---- Shader caches ----

    private static readonly DateTime Now = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Old_caches_are_gone_programs_uninstalled_apps_and_unused_older_copies()
    {
        var caches = new DriveSpace.CacheFolder[]
        {
            new("a", @"c:\games\Wuthering Waves\Client.exe", null, 20, Now.AddDays(-2)),
            new("b", @"C:\Games\Lords\LOTF2.exe", null, 3, Now.AddDays(-2)),
            new("c", @"c:\games\lords\lotf2.exe", null, 3, Now.AddDays(-1)),
            new("d", @"C:\Games\Old\Game.exe", null, 5, Now.AddDays(-90)),
            new("e", @"C:\Games\Old\Game.exe", null, 5, Now.AddDays(-1)),
            new("f", @"E:\Unplugged\Game.exe", null, 5, Now.AddDays(-200)),
            new("g", null, "570", 1, Now),
            new("h", null, "730", 1, Now),
            new("i", @"\\?\C:\Temp\selftest.exe", null, 0, Now),
        };
        bool? There(string exe) => exe.StartsWith(@"E:", StringComparison.Ordinal) ? null
            : !exe.Contains("Wuthering", StringComparison.OrdinalIgnoreCase) && !exe.Contains("selftest", StringComparison.Ordinal);
        var old = DriveSpace.Old(caches, There, app => app == "730", Now, null).Select(c => c.Path).Order();
        Assert.Equal(["a", "d", "g", "i"], old);
    }

    /// <summary>Exo Shaders' compile copies run from its data folder and are gone once the compile ends: their caches are its
    /// own to move into the game's or delete, so Clean up never calls them old while that folder is there.</summary>
    [Fact]
    public void Exo_Shaders_compile_copies_caches_are_left_to_Exo_Shaders()
    {
        const string Shaders = @"C:\Users\Player\AppData\Local\SCSKiller";
        var caches = new DriveSpace.CacheFolder[]
        {
            new("staged", @"C:\Users\Player\AppData\Local\SCSKiller\games\steam_2001760\work\stage-13200-1\Beast\Binaries\Win64\Beast-Win64-Shipping.exe", null, 9, Now.AddDays(-4)),
            new("prefixed", @"\\?\c:\users\player\appdata\local\scskiller\games\steam_1\work-1\stage-1-1\Game.exe", null, 1, Now.AddDays(-90)),
            new("sibling", @"C:\Users\Player\AppData\Local\SCSKiller.App\current\selftest.exe", null, 1, Now),   // another folder whose name starts the same
            new("game", @"C:\Games\Gone\Game.exe", null, 1, Now),
        };
        static bool? There(string exe) => false;   // every program here is gone
        Assert.Equal(["game", "sibling"], DriveSpace.Old(caches, There, _ => true, Now, Shaders).Select(c => c.Path).Order());
        Assert.Equal(["game", "prefixed", "sibling", "staged"], DriveSpace.Old(caches, There, _ => true, Now, null).Select(c => c.Path).Order());   // Exo Shaders gone: Clean up's
    }

    [Fact]
    public void A_cache_goes_to_the_game_whose_folder_holds_its_program()
    {
        var games = new (string Id, string? Folder, string? SteamApp)[]
        {
            ("lotf", @"C:\Steam\steamapps\common\Lords of the Fallen", "1501750"), ("lotf2", @"C:\Steam\steamapps\common\Lords of the Fallen 2", "2"),
        };
        Assert.Equal("lotf2", DriveSpace.GameOf(new("x", @"c:\steam\steamapps\common\lords of the fallen 2\LOTF2.exe", null, 0, Now), games));
        Assert.Equal("lotf", DriveSpace.GameOf(new("x", @"\\?\C:\Steam\steamapps\common\Lords of the Fallen\LOTF.exe", null, 0, Now), games));
        Assert.Equal("lotf", DriveSpace.GameOf(new("x", null, "1501750", 0, Now), games));
        Assert.Null(DriveSpace.GameOf(new("x", @"C:\Other\a.exe", null, 0, Now), games));
        Assert.Equal("C:", DriveSpace.LetterOf(@"c:\x"));
        Assert.Equal("", DriveSpace.LetterOf(@"\\server\x"));
    }
}
