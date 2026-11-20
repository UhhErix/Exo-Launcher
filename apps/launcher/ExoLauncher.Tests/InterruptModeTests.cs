using ExoLauncher.Services;
using static ExoLauncher.Services.InterruptMode;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Device interrupts (MSI mode) could go wrong silently:
/// - a device without MSI (only the line bit, or a maximum of 0 messages) is switched, or one already on MSI is written again;
/// - Windows' drive's controller, or any bridge or USB controller between the drive and the processor, is switched
///   (no boot); a storage controller elsewhere is switched;
/// - a device with no driver, a driver with a problem, an unsigned or unknown driver, a Sound Blaster, a bridge, an old
///   EHCI controller or a USB4 router is switched;
/// - Exo applies while it can't tell which drive Windows starts from;
/// - the backup is written after the change (a crash loses the old value), or a declined prompt leaves a backup for a
///   change that never happened, or a second apply overwrites the first backup's original value;
/// - Undo writes 1 where the value was absent instead of deleting it, or 0 where it was 0, or one device's Revert puts back the others;
/// - "Restart to finish" doesn't show after a change or stays after the restart;
/// - after the restart a device still on a shared line, or one Windows or a driver update put back, is called switched;
/// - the sentence miscounts ("1 USB controllers") or the instance id (it can carry a serial) reaches React;
/// - an id cmd would read specially (a quote, % or ^) reaches reg.exe;
/// - high priority goes to anything but the graphics card and USB controllers, lowers or rewrites a priority already
///   High, isn't backed up, or Undo leaves it (or deletes the MSI value of a device whose MSI Exo never wrote).
/// - after a driver update put Exo's values back, the standing choice isn't set again (or is after Undo all ended it), Switch
///   back is offered when Windows wouldn't ask (or not when it would), or setting it again loses what the device held before
///   Exo first wrote it or doesn't show Restart to finish;
/// - an Exo driver install doesn't write the standing values in its own elevated step, writes them before setup (which then
///   rewrites the keys), loses setup's exit code, or leaves a backup for a step that never ran.
/// </summary>
public sealed class InterruptModeTests : IDisposable
{
    private const string Gpu = @"PCI\VEN_10DE&DEV_2484&SUBSYS_00001043&REV_A1\4&1&0&0019";
    private const string Usb1 = @"PCI\VEN_1022&DEV_149C&SUBSYS_87C01043&REV_00\4&2&0&0341";
    private const string Usb2 = @"PCI\VEN_1022&DEV_149C&SUBSYS_87C01043&REV_00\4&3&0&0342";
    private const string Usb3 = @"PCI\VEN_1022&DEV_43EE&SUBSYS_11421B21&REV_00\4&4&0&000A";
    private const string Nvme = @"PCI\VEN_144D&DEV_A80C&SUBSYS_A801144D&REV_00\4&5&0&0009";
    private const string RootPort = @"PCI\VEN_1022&DEV_1483&SUBSYS_88081043&REV_00\3&1&0&09";
    private const string Disk = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\5&1&0&000000";

    private readonly string _folder = Directory.CreateTempSubdirectory("exo-msi-").FullName;
    private string BackupPath => Path.Combine(_folder, "msi-mode-backup.json");

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    private static Device Dev(string id, byte cls, byte sub, byte progIf = 0, uint support = 3, uint max = 1, Mode mode = Mode.Line, int? msi = null,
        string? service = "drv", uint problem = 0, uint? rank = 0x00FF2001) =>
        new(id, id[..21], service, problem, cls, sub, progIf, support, max, rank, mode, msi);

    private static Device GpuOnLine(int? msi = null) => Dev(Gpu, 0x03, 0x00, msi: msi);
    private static Device Xhci(string id, Mode mode = Mode.Line) => Dev(id, 0x0C, 0x03, 0x30, support: 7, max: 8, mode: mode);

    private sealed class FakeDevices : IDevices
    {
        public List<Device> Devices = [];
        public List<string> Boot = [Disk];
        public Dictionary<string, string> Parents = new(StringComparer.OrdinalIgnoreCase) { [Disk] = Nvme, [Nvme] = RootPort, [RootPort] = @"ACPI\PNP0A08\0" };
        public IReadOnlyList<Device> List() => Devices.ToList();
        public IReadOnlyList<string> BootDisks() => Boot;
        public string? Parent(string id) => Parents.GetValueOrDefault(id);
    }

    /// <summary>The registry: a write sets each device's MSISupported, unless declined or Windows ignores it.</summary>
    private sealed class FakeWriter(FakeDevices devices, string backupPath) : IWriter
    {
        public List<IReadOnlyList<(string Id, string Name, int? Value)>> Calls = [];
        public bool Decline;
        public HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase);
        public string? BackupAtWrite;

        public string? Write(IReadOnlyList<(string Id, string Name, int? Value)> values)
        {
            BackupAtWrite = File.Exists(backupPath) ? File.ReadAllText(backupPath) : null;
            Calls.Add(values);
            if (Decline) return "Windows' permission was declined, so nothing changed.";
            foreach (var (id, name, value) in values)
            {
                if (Ignore.Contains(id)) continue;
                var at = devices.Devices.FindIndex(d => d.Id == id);
                devices.Devices[at] = name == PriorityName ? devices.Devices[at] with { Priority = value } : devices.Devices[at] with { MsiValue = value };
            }
            return null;
        }
    }

    private DateTimeOffset _boot = DateTimeOffset.UtcNow.AddHours(-1);
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    private (InterruptMode Mode, FakeDevices Devices, FakeWriter Writer) Make(params Device[] list)
    {
        var devices = new FakeDevices { Devices = [.. list] };
        var writer = new FakeWriter(devices, BackupPath);
        return (new InterruptMode(devices, writer, BackupPath, () => _boot, () => _now), devices, writer);
    }

    /// <summary>Windows restarts: devices with MSISupported=1 come up on MSI unless <paramref name="stay"/> keeps them on a line.</summary>
    private void Restart(FakeDevices devices, params string[] stay)
    {
        _boot = _now.AddMinutes(1);
        _now = _boot.AddMinutes(1);
        devices.Devices = devices.Devices.Select(d => d.MsiValue == 1 && !stay.Contains(d.Id) ? d with { Mode = Mode.Msi } : d with { Mode = d.MsiValue == 1 ? d.Mode : Mode.Line }).ToList();
    }

    private static Plan PlanOf(InterruptMode mode, string id) => mode.Read().Devices.Single(r => r.Token == Token(id)).Plan;

    // ---- Support ----

    [Fact]
    public void OnlyADeviceWithMsiOnALineIsChanged()
    {
        var none = new HashSet<string>();
        Assert.Equal(Plan.Change, Decide(GpuOnLine(), none).Plan);
        Assert.Equal(Plan.Keep, Decide(Dev(Gpu, 0x03, 0, support: 1), none).Plan);
        Assert.Equal(Plan.Keep, Decide(Dev(Gpu, 0x03, 0, max: 0), none).Plan);
        Assert.Equal(Plan.Done, Decide(Dev(Gpu, 0x03, 0, mode: Mode.Msi) with { Priority = 3 }, none).Plan);
        Assert.Equal(Plan.Skip, Decide(Dev(Gpu, 0x03, 0, mode: Mode.None), none).Plan);
        // Asked for MSI already and the driver still chose a line: writing again changes nothing.
        Assert.Equal(Plan.Keep, Decide(GpuOnLine(msi: 1), none).Plan);
        // A driver that wrote 0 on a capable device is switched.
        Assert.Equal(Plan.Change, Decide(GpuOnLine(msi: 0), none).Plan);
        Assert.Equal(Plan.Change, Decide(Xhci(Usb1), none).Plan);
        Assert.Equal(Plan.Change, Decide(Dev(@"PCI\VEN_8086&DEV_125C&SUBSYS_00000000&REV_04\4&9&0&00E0", 0x02, 0x00), none).Plan);
        Assert.Equal(Plan.Change, Decide(Dev(@"PCI\VEN_1022&DEV_1487&SUBSYS_00000000&REV_00\4&9&0&0441", 0x04, 0x03), none).Plan);
    }

    // ---- Boot device ----

    [Fact]
    public void TheDrivesChainIsNeverTouched()
    {
        var (mode, devices, writer) = Make();
        Assert.Equal([Disk, Nvme, RootPort, @"ACPI\PNP0A08\0"], mode.BootChain());
        // Windows on a USB drive: the xHCI controller above it is left alone, the other one isn't.
        devices.Parents[Disk] = Usb1;
        devices.Parents[Usb1] = RootPort;
        devices.Devices = [Xhci(Usb1), Xhci(Usb2)];
        Assert.Equal(Plan.Skip, PlanOf(mode, Usb1));
        Assert.Equal(Plan.Change, PlanOf(mode, Usb2));
        Assert.Null(mode.Apply());
        Assert.Equal([(Usb2, MsiName, (int?)1), (Usb2, PriorityName, (int?)3)], Assert.Single(writer.Calls));
    }

    [Fact]
    public void NothingIsWrittenWhenWindowsDriveIsUnknown()
    {
        var (mode, devices, writer) = Make(GpuOnLine());
        devices.Boot = [];
        Assert.NotNull(mode.Apply());
        Assert.Empty(writer.Calls);
        Assert.False(File.Exists(BackupPath));
    }

    // ---- Deny list ----

    [Fact]
    public void RiskyDevicesAreLeftAlone()
    {
        var none = new HashSet<string>();
        Assert.Equal(Plan.Skip, Decide(Dev(@"PCI\VEN_1B21&DEV_0612&SUBSYS_00000000&REV_01\4&9&0&00E8", 0x01, 0x06), none).Plan); // SATA, not the boot one
        Assert.Equal(Plan.Skip, Decide(Dev(@"PCI\VEN_1102&DEV_0012&SUBSYS_00000000&REV_01\4&9&0&00E9", 0x04, 0x03), none).Plan); // Sound Blaster
        Assert.Equal(Plan.Skip, Decide(GpuOnLine() with { Service = null }, none).Plan);
        Assert.Equal(Plan.Skip, Decide(GpuOnLine() with { Problem = 28 }, none).Plan);
        Assert.Equal(Plan.Skip, Decide(GpuOnLine() with { DriverRank = null }, none).Plan);
        Assert.Equal(Plan.Skip, Decide(GpuOnLine() with { DriverRank = 0x80000000 }, none).Plan);
        Assert.Equal(Plan.Skip, Decide(Dev(RootPort, 0x06, 0x04), none).Plan); // bridge
        Assert.Equal(Plan.Skip, Decide(Dev(Usb3, 0x0C, 0x03, 0x20), none).Plan); // EHCI
        Assert.Equal(Plan.Skip, Decide(Dev(Usb3, 0x0C, 0x03, 0x40), none).Plan); // USB4
        Assert.Equal(Plan.Skip, Decide(Dev(Usb3, 0x04, 0x00), none).Plan); // a capture card
        Assert.Equal(Plan.Skip, Decide(Dev(Usb3, 0x10, 0x80), none).Plan); // AMD PSP
    }

    [Fact]
    public void OnlyIdsWindowsGivesReachRegExe()
    {
        Assert.Matches(PciId(), Gpu);
        Assert.DoesNotMatch(PciId(), Gpu + "\" & calc");
        Assert.DoesNotMatch(PciId(), @"PCI\VEN_10DE%PATH%\4&1");
        Assert.DoesNotMatch(PciId(), @"PCI\VEN_10DE^&DEV\4&1");
        Assert.DoesNotMatch(PciId(), @"USB\VID_046D&PID_C547\5&1");
        var (mode, _, _) = Make(GpuOnLine() with { Id = Gpu + "\"x" });
        Assert.Empty(mode.Read().Devices);
    }

    // ---- Backup and undo ----

    [Fact]
    public void ApplyBacksUpFirstAndUndoPutsBackExactly()
    {
        var (mode, devices, writer) = Make(GpuOnLine(msi: 0), Xhci(Usb1), Dev(Usb3, 0x0C, 0x03, 0x30, support: 7, max: 8, mode: Mode.Msi, msi: 1) with { Priority = 3 });
        Assert.Null(mode.Apply());
        var call = Assert.Single(writer.Calls);
        Assert.Equal([(Gpu, MsiName, (int?)1), (Gpu, PriorityName, (int?)3), (Usb1, MsiName, (int?)1), (Usb1, PriorityName, (int?)3)], call);
        Assert.Contains("\"Was\":0", writer.BackupAtWrite);
        Assert.Contains("\"Was\":null", writer.BackupAtWrite);

        var state = mode.Read();
        Assert.True(state.Restart);
        Assert.True(state.CanUndo);
        Assert.Null(state.Result);
        Assert.Equal(Plan.Restart, PlanOf(mode, Gpu));

        // A second apply finds nothing and keeps the first backup's values.
        Assert.Null(mode.Apply());
        Assert.Single(writer.Calls);

        Assert.Null(mode.Undo(null));
        Assert.Equal([(Gpu, MsiName, (int?)0), (Gpu, PriorityName, (int?)null), (Usb1, MsiName, (int?)null), (Usb1, PriorityName, (int?)null)], writer.Calls[1]);
        Assert.Equal(0, devices.Devices[0].MsiValue);
        Assert.Null(devices.Devices[1].MsiValue);
        Assert.False(mode.Read().CanUndo);
        Assert.True(mode.Read().Restart);
        Assert.Equal("There's nothing to undo.", mode.Undo(null));

        Restart(devices);
        var after = mode.Read();
        Assert.False(after.Restart);
        Assert.Null(after.Result);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void RevertPutsBackOnlyThatDevice()
    {
        var (mode, devices, writer) = Make(GpuOnLine(), Xhci(Usb1));
        Assert.Null(mode.Apply());
        Assert.Null(mode.Undo(Token(Usb1)));
        Assert.Equal([(Usb1, MsiName, (int?)null), (Usb1, PriorityName, (int?)null)], writer.Calls[1]);
        Assert.Equal(1, devices.Devices[0].MsiValue);
        var rows = mode.Read().Devices;
        Assert.True(rows.Single(r => r.Token == Token(Gpu)).Changed);
        Assert.False(rows.Single(r => r.Token == Token(Usb1)).Changed);
        Assert.Equal(Plan.Restart, rows.Single(r => r.Token == Token(Usb1)).Plan);
        Assert.True(mode.Read().CanUndo);
    }

    [Fact]
    public void ADeclinedPromptLeavesNoBackup()
    {
        var (mode, _, writer) = Make(GpuOnLine());
        writer.Decline = true;
        Assert.Equal("Windows' permission was declined, so nothing changed.", mode.Apply());
        Assert.False(File.Exists(BackupPath));
        Assert.False(mode.Read().Restart);
        Assert.Equal(Plan.Change, PlanOf(mode, Gpu));
    }

    [Fact]
    public void AValueWindowsIgnoredIsNotBackedUp()
    {
        var (mode, _, writer) = Make(GpuOnLine(), Xhci(Usb1));
        writer.Ignore.Add(Usb1);
        Assert.NotNull(mode.Apply());
        Assert.Null(mode.Undo(null));
        Assert.Equal([(Gpu, MsiName, (int?)null), (Gpu, PriorityName, (int?)null)], writer.Calls[1]);
    }

    // ---- After the restart ----

    [Fact]
    public void AfterTheRestartEachDeviceIsChecked()
    {
        var (mode, devices, _) = Make(GpuOnLine(), Xhci(Usb1), Xhci(Usb2), Xhci(Usb3), Dev(@"PCI\VEN_8086&DEV_125C&SUBSYS_00000000&REV_04\4&9&0&00E0", 0x02, 0x00));
        Assert.Null(mode.Apply());
        Restart(devices, stay: @"PCI\VEN_8086&DEV_125C&SUBSYS_00000000&REV_04\4&9&0&00E0");
        var state = mode.Read();
        Assert.False(state.Restart);
        Assert.Equal("Your network adapter didn't switch: its driver keeps a shared line.", state.Result);   // what worked says nothing
        Assert.Equal(Plan.Done, PlanOf(mode, Gpu));
        Assert.True(state.CanUndo);

        // A driver update writes 0 again and the GPU is back on a line.
        devices.Devices[0] = devices.Devices[0] with { MsiValue = 0, Mode = Mode.Line };
        Assert.Contains("Windows or a driver update put your graphics card back.", mode.Read().Result);
        Assert.Equal(Plan.Change, PlanOf(mode, Gpu));
    }

    [Fact]
    public void TheSentenceCountsRight()
    {
        Assert.Equal("graphics card", Phrase(["graphics card"]));
        Assert.Equal("2 USB controllers", Phrase(["USB controller", "USB controller"]));
        Assert.Equal("graphics card, network adapter and 2 audio controllers", Phrase(["graphics card", "network adapter", "audio controller", "audio controller"]));
        var (mode, devices, _) = Make(GpuOnLine());
        Assert.Null(mode.Apply());
        Restart(devices);
        Assert.Null(mode.Read().Result);   // it took: nothing to say
    }

    // ---- Priority ----

    [Fact]
    public void OnlyTheGraphicsCardAndUsbTakeHighPriority()
    {
        var none = new HashSet<string>();
        Assert.Equal(Plan.Change, Decide(Dev(Gpu, 0x03, 0, mode: Mode.Msi), none).Plan);
        Assert.Equal(Plan.Change, Decide(Xhci(Usb1, Mode.Msi), none).Plan);
        Assert.Equal(Plan.Done, Decide(Xhci(Usb1, Mode.Msi) with { Priority = 3 }, none).Plan);
        // Network and audio on MSI are done whatever their priority; Exo never lowers one.
        Assert.Equal(Plan.Done, Decide(Dev(@"PCI\VEN_8086&DEV_125C&SUBSYS_00000000&REV_04\4&9&0&00E0", 0x02, 0x00, mode: Mode.Msi), none).Plan);
        Assert.Equal(Plan.Done, Decide(Dev(@"PCI\VEN_1022&DEV_1487&SUBSYS_00000000&REV_00\4&9&0&0441", 0x04, 0x03, mode: Mode.Msi, msi: 1) with { Priority = 3 }, none).Plan);
    }

    [Fact]
    public void PriorityAloneIsWrittenBackedUpAndPutBack()
    {
        // The GPU already runs MSI (its driver wrote the value): only its priority is written, its MSI value left as it was.
        var (mode, devices, writer) = Make(Dev(Gpu, 0x03, 0, mode: Mode.Msi, msi: 1) with { Priority = 2 }, Xhci(Usb1, Mode.Msi) with { Priority = 3 });
        Assert.Null(mode.Apply());
        Assert.Equal([(Gpu, PriorityName, (int?)3)], Assert.Single(writer.Calls));
        Assert.Contains("\"WasPriority\":2", writer.BackupAtWrite);
        Assert.Equal(1, devices.Devices[0].MsiValue);
        Assert.Equal(Plan.Restart, PlanOf(mode, Gpu));
        Assert.Equal(Plan.Done, PlanOf(mode, Usb1));

        Restart(devices);
        Assert.Null(mode.Read().Result);   // it took: nothing to say
        Assert.Equal(Plan.Done, PlanOf(mode, Gpu));

        Assert.Null(mode.Undo(null));
        Assert.Equal([(Gpu, PriorityName, (int?)2)], writer.Calls[1]);
        Assert.Equal(2, devices.Devices[0].Priority);
        Assert.Equal(1, devices.Devices[0].MsiValue);
    }

    [Fact]
    public void ReactNeverSeesTheInstanceId()
    {
        var (mode, _, _) = Make(GpuOnLine());
        var row = Assert.Single(mode.Read().Devices);
        Assert.Matches("^[0-9a-f]{12}$", row.Token);
        Assert.Equal(Token(Gpu.ToLowerInvariant()), row.Token);
    }

    // ---- The standing choice after a driver update ----

    private const string Net = @"PCI\VEN_8086&DEV_125C&SUBSYS_00000000&REV_04\4&9&0&00E0";

    /// <summary>A driver install rewrites a device's keys from its INF: MSISupported and DevicePriority gone, the device on a line.</summary>
    private static void DriverReset(FakeDevices devices, string id) =>
        devices.Devices = devices.Devices.Select(d => d.Id == id ? d with { MsiValue = null, Priority = null, Mode = Mode.Line } : d).ToList();

    [Fact]
    public void AppliedThenResetIsSetAgainByItselfOrOffered()
    {
        var (mode, devices, writer) = Make(GpuOnLine(), Xhci(Usb1));
        Assert.Null(mode.Apply());
        Restart(devices);
        Assert.Empty(mode.Lapsed());
        Assert.False(mode.Read().Lapsed);
        Assert.Equal(FollowUp.None, Next(0, windowsAsks: true));

        DriverReset(devices, Gpu);
        var lapsed = mode.Lapsed();
        Assert.Equal([(Gpu, MsiName, (int?)1), (Gpu, PriorityName, (int?)3)], lapsed);
        Assert.True(mode.Read().Lapsed);
        Assert.Equal(FollowUp.Reapply, Next(lapsed.Count, windowsAsks: false));
        Assert.Equal(FollowUp.Ask, Next(lapsed.Count, windowsAsks: true));

        // Switch back (or the follow-up by itself): only what lapsed, then Restart to finish.
        Assert.Null(mode.Reapply());
        Assert.Equal([(Gpu, MsiName, (int?)1), (Gpu, PriorityName, (int?)3)], writer.Calls[^1]);
        Assert.Empty(mode.Lapsed());
        Assert.True(mode.Read().Restart);
        Assert.Equal(Plan.Restart, PlanOf(mode, Gpu));

        // Undo still puts back what the card held before Exo first wrote it.
        Assert.Null(mode.Undo(null));
        Assert.Contains((Gpu, MsiName, (int?)null), writer.Calls[^1]);
        Assert.Contains((Gpu, PriorityName, (int?)null), writer.Calls[^1]);
    }

    [Fact]
    public void UndoAllEndsTheStandingChoice()
    {
        var (mode, devices, writer) = Make(GpuOnLine());
        Assert.Null(mode.Apply());
        Restart(devices);
        Assert.Null(mode.Undo(null));
        // A driver update afterwards writes its own value: nothing of Exo's comes back.
        devices.Devices[0] = devices.Devices[0] with { MsiValue = 0 };
        Assert.Empty(mode.Lapsed());
        Assert.Equal(FollowUp.None, Next(mode.Lapsed().Count, windowsAsks: false));
        var calls = writer.Calls.Count;
        Assert.Null(mode.Reapply());
        Assert.Empty(mode.ForDriverInstall().Lines);
        Assert.Equal(calls, writer.Calls.Count);
        Restart(devices);
        Assert.False(mode.Read().Lapsed);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void AnExoDriverInstallChainsTheStandingValuesAfterSetup()
    {
        var (mode, devices, writer) = Make(GpuOnLine(), Xhci(Usb1));
        Assert.Empty(mode.ForDriverInstall().Lines);   // never applied: nothing to chain
        Assert.Null(mode.Apply());
        Restart(devices);
        var calls = writer.Calls.Count;

        var chain = mode.ForDriverInstall();
        Assert.Equal(4, chain.Lines.Count);
        Assert.Contains(chain.Lines, line => line.Contains(Gpu + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties", StringComparison.Ordinal)
            && line.Contains("/v MSISupported /t REG_DWORD /d 1 /f", StringComparison.Ordinal));
        Assert.Contains(chain.Lines, line => line.Contains(Gpu + @"\Device Parameters\Interrupt Management\Affinity Policy", StringComparison.Ordinal)
            && line.Contains("/v DevicePriority /t REG_DWORD /d 3 /f", StringComparison.Ordinal));
        Assert.Equal(calls, writer.Calls.Count);   // nothing written now: the installer's own step writes them

        // Setup first (start /wait, in its folder), its exit code kept, then each line, then cmd exits with setup's code.
        const string folder = @"C:\Users\A\AppData\Local\ExoLauncher\drivers\621.12\unpacked";
        var args = NvidiaDriverService.ChainedArguments(folder + @"\setup.exe", folder, "-s -clean", chain.Lines)!;
        Assert.StartsWith($"/d /v:on /s /c \"start \"\" /d \"{folder}\" /wait \"{folder}\\setup.exe\" -s -clean & set \"EXO_RC=!ERRORLEVEL!\" & ", args);
        Assert.True(args.IndexOf(chain.Lines[0], StringComparison.Ordinal) > args.IndexOf("EXO_RC=", StringComparison.Ordinal));
        Assert.EndsWith(chain.Lines[^1] + " & exit !EXO_RC!\"", args);
        // A path cmd would read (delayed expansion, a variable): setup runs alone, as before.
        Assert.Null(NvidiaDriverService.ChainedArguments(@"C:\Users\A!b\setup.exe", @"C:\Users\A!b", "-s", chain.Lines));
        Assert.Null(NvidiaDriverService.ChainedArguments(@"C:\Users\100%\setup.exe", @"C:\Users\100%", "-s", chain.Lines));
        Assert.Null(NvidiaDriverService.ChainedArguments(folder + @"\setup.exe", folder, "-s", []));
    }

    [Fact]
    public void ADeviceTheInstallWouldSwitchIsBackedUpFirstAndForgottenIfTheStepNeverRan()
    {
        var (mode, devices, _) = Make(GpuOnLine());
        Assert.Null(mode.Apply());
        Restart(devices);
        devices.Devices.Add(Dev(Net, 0x02, 0x00));
        Assert.False(mode.Read().Devices.Single(r => r.Token == Token(Net)).Changed);

        var chain = mode.ForDriverInstall();
        Assert.Contains(chain.Lines, line => line.Contains(Net, StringComparison.Ordinal));
        Assert.True(mode.Read().Devices.Single(r => r.Token == Token(Net)).Changed);

        // Declined: the backup is as it was before.
        mode.Rollback(chain);
        Assert.False(mode.Read().Devices.Single(r => r.Token == Token(Net)).Changed);
        Assert.True(mode.Read().CanUndo);
    }

    // ---- A device that didn't start ----

    [Fact]
    public void ADeviceThatDidntStartInMsiModeIsSaidAndPutBack()
    {
        var (mode, devices, writer) = Make(GpuOnLine(), Xhci(Usb1));
        Assert.Null(mode.Apply());
        // Before the restart nothing is put back: Windows hasn't tried yet.
        Assert.Null(mode.PutBackFailed());
        Restart(devices);
        devices.Devices[0] = devices.Devices[0] with { Problem = 10 }; // CM_PROB_FAILED_START
        var said = mode.Read().Result;
        Assert.NotNull(said);
        Assert.Contains("graphics card didn't start in MSI mode", said, StringComparison.Ordinal);
        Assert.DoesNotContain("didn't switch", said, StringComparison.Ordinal);

        var calls = writer.Calls.Count;
        Assert.Null(mode.PutBackFailed());
        var back = writer.Calls[calls];
        Assert.All(back, w => Assert.Equal(Gpu, w.Id));
        Assert.Contains(back, w => w.Name == MsiName && w.Value is null);
        // The USB controller that started stays switched.
        Assert.Equal(1, devices.Devices.Single(d => d.Id == Usb1).MsiValue);
    }

    [Fact]
    public void ADeviceTheUserDisabledIsNotAFailure()
    {
        var (mode, devices, _) = Make(GpuOnLine());
        Assert.Null(mode.Apply());
        Restart(devices);
        devices.Devices[0] = devices.Devices[0] with { Problem = 22 };
        var backup = System.Text.Json.JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath))!;
        Assert.Empty(Failed(devices.Devices, backup));
        Assert.Null(mode.PutBackFailed());
    }
}
