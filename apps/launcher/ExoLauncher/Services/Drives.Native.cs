using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

/// <summary>
/// <see cref="Drives"/> on this PC. Each physical disk that holds a fixed volume, asked through \\.\PhysicalDriveN opened
/// with no access (as <see cref="PcAbout"/> does): its descriptor, write cache, TRIM, temperature, and on NVMe the
/// SMART/Health log and Identify Controller through the protocol-specific query; on SATA the SMART data
/// IOCTL_STORAGE_PREDICT_FAILURE returns. The PCIe link and the controller's driver come from Windows' device
/// properties (cfgmgr32), the flushing setting from the disk's Device Parameters key, the optimize schedule from
/// schtasks' XML and Automatic Maintenance's key. None of it needs administrator rights; a read that fails leaves its
/// part null. The NVMe drive's own settings (power state, APST, host memory, sector formats) come from Identify and Get
/// Features through the same query, never a vendor command. The one elevated step is <see cref="Change"/>.
/// </summary>
internal static partial class Drives
{
    private static string BackupPath => Path.Combine(PathHelper.AppDataDir, "exo-drives.json");

    /// <summary>Every physical disk behind a fixed, ready volume, by number, its volumes with it.</summary>
    internal static IReadOnlyList<Disk> ReadDisks()
    {
        var volumes = new SortedDictionary<int, List<Volume>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var letter = drive.Name.TrimEnd('\\');
                if (PcAbout.DiskNumber(letter) is not { } number) continue;
                if (!volumes.TryGetValue(number, out var list)) volumes[number] = list = [];
                list.Add(new Volume(letter, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ready */ }
        }
        var devices = DiskDevices();
        return volumes.Select(v => ReadDisk(v.Key, v.Value, devices.GetValueOrDefault(v.Key))).ToList();
    }

    private static Disk ReadDisk(int number, List<Volume> volumes, string? instance)
    {
        using var disk = PcAbout.Open(@"\\.\PhysicalDrive" + number);
        var descriptor = disk.IsInvalid ? null : PcAbout.Query(disk, 0 /* StorageDeviceProperty */, 1024);
        var (model, busCode) = PcAbout.ParseDevice(descriptor);
        var bus = BusOf(busCode);
        var kind = PcAbout.Kind(busCode, disk.IsInvalid ? null : PcAbout.Seeks(disk));
        if (disk.IsInvalid) return new Disk(number, model, bus, kind, null, null, null, null, null, null, null, volumes, instance);
        var cache = PcAbout.Query(disk, 4 /* StorageDeviceWriteCacheProperty */, 32) is { } c ? ParseWriteCache(c) : null;
        var temperature = PcAbout.Query(disk, 52 /* StorageDeviceTemperatureProperty */, 512) is { } t ? ParseTemperature(t) : null;
        Health? health = null;
        int? slots = null;
        NvmeFacts? nvme = null;
        if (bus == Bus.Nvme)
        {
            health = NvmeQuery(disk, 2 /* log page */, 0x02 /* SMART / Health */, 512) is { } log ? ParseSmartLog(log) : null;
            if (NvmeQuery(disk, 1 /* identify */, 0x01 /* controller */, 4096) is { } id && ParseIdentify(id) is { } identify)
            {
                slots = identify.Slots > 0 ? identify.Slots : null;
                if (temperature is { WarnC: null } && identify.WarnC is not null) temperature = temperature with { WarnC = identify.WarnC, CriticalC = identify.CriticalC };
                var format = NvmeQuery(disk, 1, 0x00 /* namespace */, 4096, subValue: 1 /* NSID 1 */) is { } ns ? ParseNamespace(ns) : null;
                nvme = new NvmeFacts(
                    PowerState: NvmeFeature(disk, 0x02 /* power management */) is { } power ? PowerStateOf(power) : null,
                    PowerStates: identify.PowerStates, Apst: identify.Apst,
                    ApstOn: identify.Apst && NvmeFeature(disk, 0x0C /* APST */) is { } apst ? (apst & 1) != 0 : null,
                    HmbBytes: identify.HmbBytes,
                    HmbOn: identify.HmbBytes > 0 && NvmeFeature(disk, 0x0D /* host memory buffer */) is { } hmb ? (hmb & 1) != 0 : null,
                    VolatileCache: identify.VolatileCache, SectorBytes: format?.SectorBytes, FourK: format?.FourK ?? false);
            }
        }
        else if (bus == Bus.Sata && PredictFailure(disk) is { } predict && ParseAtaSmart(predict.Data, predict.Failing) is { } ata)
        {
            health = ata.Health;
            if (temperature is null && ata.TemperatureC is { } c2) temperature = new Temperature(c2, null, null);
        }
        var (driver, link) = instance is null ? (null, null) : Controller(instance, bus);
        return new Disk(number, model, bus, kind, ParseRevision(descriptor), slots, link, driver, health, temperature, cache, volumes, instance,
            instance is null ? null : DiskValue(instance, "CacheIsPowerProtected"), nvme, instance is null ? null : DiskValue(instance, "UserWriteCacheSetting"));
    }

    /// <summary>
    /// The active plan's NVMe power on AC (<see cref="FullPower"/>: the Disk subgroup's latency tolerances and idle
    /// timeouts, as <c>powercfg /qh</c> lists them), its name, whether it's Exo's plan, and whether the PC is on battery
    /// now (then the laptop's own battery choice applies, and Exo says nothing of it). Null when the plan can't be read.
    /// </summary>
    internal static PlanPower? ReadPlanPower()
    {
        var api = new PcHealth.NativePower();
        if (PowerPlan.Shared.Read() is not { } state) return null;
        uint? Ac(string setting) => api.AcValue(state.Active, PowerProfiles.Disk, new Guid(setting));
        var full = FullPower(Ac("fc95af4d-40e7-4b6d-835a-56d131dbc80e"), Ac("dbc9e238-6de9-49e3-92cd-8c2b4946b472"),
            Ac("d639518a-e56d-4345-8af2-b9f32fb26109"), Ac("d3d55efd-c1ff-424e-9dc3-441be7833010"));
        return full is null ? null : new PlanPower(full.Value, state.Name, state.Exo, state.Battery);
    }

    /// <summary>
    /// A temperature, the SMART log's thermal counters and the link, for one disk while a game runs (<see cref="DriveHeat"/>):
    /// two IOCTLs and two device properties, nothing written.
    /// </summary>
    internal static (Temperature? Temperature, Health? Health, Link? Link) Sample(int number, string? instance)
    {
        using var disk = PcAbout.Open(@"\\.\PhysicalDrive" + number);
        if (disk.IsInvalid) return (null, null, null);
        var temperature = PcAbout.Query(disk, 52, 512) is { } t ? ParseTemperature(t) : null;
        var health = NvmeQuery(disk, 2, 0x02, 512) is { } log ? ParseSmartLog(log) : null;
        var link = instance is null ? null : Controller(instance, Bus.Nvme).Link;
        return (temperature, health, link);
    }

    /// <summary>
    /// STORAGE_PROPERTY_QUERY of StorageDeviceProtocolSpecificProperty for NVMe: an Identify (data type 1) or a log page
    /// (2), its data after the 40-byte STORAGE_PROTOCOL_SPECIFIC_DATA. Null when the driver doesn't answer it.
    /// </summary>
    private static byte[]? NvmeQuery(SafeFileHandle disk, int dataType, int value, int length, int subValue = 0)
    {
        var query = new byte[8 + 40 + length];
        BitConverter.GetBytes(50).CopyTo(query, 0);        // StorageDeviceProtocolSpecificProperty, PropertyStandardQuery
        BitConverter.GetBytes(3).CopyTo(query, 8);         // ProtocolTypeNvme
        BitConverter.GetBytes(dataType).CopyTo(query, 12);
        BitConverter.GetBytes(value).CopyTo(query, 16);
        BitConverter.GetBytes(subValue).CopyTo(query, 20); // ProtocolDataRequestSubValue (a namespace's id)
        BitConverter.GetBytes(40).CopyTo(query, 24);       // ProtocolDataOffset
        BitConverter.GetBytes(length).CopyTo(query, 28);   // ProtocolDataLength
        var answer = new byte[query.Length];
        if (!DeviceIoControl(disk, 0x002D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero)) return null;
        var offset = BitConverter.ToInt32(answer, 8 + 16);
        var size = Math.Min(BitConverter.ToInt32(answer, 8 + 20), length);
        return offset >= 40 && size > 0 && 8 + offset + size <= returned ? answer.AsSpan(8 + offset, size).ToArray() : null;
    }

    /// <summary>
    /// Get Features (NVMeDataTypeFeature, current value) for a feature without a data buffer: its first completion dword
    /// (FixedProtocolReturnData). Offset and length 0, or StorNVMe refuses it. Null when the driver or drive doesn't answer.
    /// </summary>
    private static uint? NvmeFeature(SafeFileHandle disk, int feature)
    {
        var query = new byte[8 + 40];
        BitConverter.GetBytes(50).CopyTo(query, 0);
        BitConverter.GetBytes(3).CopyTo(query, 8);
        BitConverter.GetBytes(3).CopyTo(query, 12);        // NVMeDataTypeFeature
        BitConverter.GetBytes(feature).CopyTo(query, 16);
        var answer = new byte[query.Length];
        return DeviceIoControl(disk, 0x002D1400, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero) && returned >= 8 + 28
            ? BitConverter.ToUInt32(answer, 8 + 24) : null;
    }

    /// <summary>IOCTL_STORAGE_PREDICT_FAILURE (any access): the drive's own failure prediction and, on ATA, its 512 bytes of SMART data.</summary>
    private static (bool Failing, byte[] Data)? PredictFailure(SafeFileHandle disk)
    {
        var answer = new byte[4 + 512];
        return DeviceIoControl(disk, 0x002D1100, null, 0, answer, answer.Length, out var returned, IntPtr.Zero) && returned >= 4 + 364
            ? (BitConverter.ToUInt32(answer, 0) != 0, answer[4..]) : null;
    }

    /// <summary>
    /// The disk's storage controller: its driver service and, on a PCIe NVMe controller of Windows' own or Samsung's driver
    /// (not behind Intel RST/VMD or AMD RAID, whose controller is not the drive), its link as negotiated and its maximum.
    /// </summary>
    private static (string? Driver, Link? Link) Controller(string instance, Bus bus)
    {
        if (CM_Locate_DevNode(out var node, instance, 0) != 0 || CM_Get_Parent(out var parent, node, 0) != 0) return (null, null);
        var service = String(parent, ServiceKey);
        if (bus != Bus.Nvme || service is null || service.StartsWith("iastor", StringComparison.OrdinalIgnoreCase) || service.Equals("rcraid", StringComparison.OrdinalIgnoreCase) || service.Equals("rcbottom", StringComparison.OrdinalIgnoreCase))
            return (service, null);
        return (service, UInt(parent, CurrentSpeedKey) is { } gen and > 0 && UInt(parent, CurrentWidthKey) is { } width and > 0
            && UInt(parent, MaxSpeedKey) is { } maxGen and > 0 && UInt(parent, MaxWidthKey) is { } maxWidth and > 0
            ? new Link((int)gen, (int)width, (int)maxGen, (int)maxWidth) : null);
    }

    /// <summary>Each disk interface's number (IOCTL_STORAGE_GET_DEVICE_NUMBER) and the device instance behind it.</summary>
    private static Dictionary<int, string> DiskDevices()
    {
        var found = new Dictionary<int, string>();
        var guid = DiskInterface;
        if (CM_Get_Device_Interface_List_Size(out var length, ref guid, null, 0) != 0 || length == 0) return found;
        var buffer = new char[length];
        if (CM_Get_Device_Interface_List(ref guid, null, buffer, length, 0) != 0) return found;
        foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            using var handle = PcAbout.Open(path);
            var data = new byte[12]; // STORAGE_DEVICE_NUMBER: type, number, partition
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x002D1080 /* IOCTL_STORAGE_GET_DEVICE_NUMBER */, null, 0, data, data.Length, out _, IntPtr.Zero)) continue;
            var key = InstanceIdKey;
            var id = new byte[800];
            var size = (uint)id.Length;
            if (CM_Get_Device_Interface_Property(path, ref key, out var type, id, ref size, 0) == 0 && type == 0x12)
                found[(int)BitConverter.ToUInt32(data, 4)] = Encoding.Unicode.GetString(id, 0, (int)size).TrimEnd('\0');
        }
        return found;
    }

    /// <summary>
    /// A value under a disk's Device Parameters\Disk key: CacheIsPowerProtected (Windows' "turn off write-cache buffer
    /// flushing") or UserWriteCacheSetting ("enable write caching on the device").
    /// </summary>
    private static int? DiskValue(string instance, string name)
    {
        if (!DiskId().IsMatch(instance)) return null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters\Disk");
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>
    /// Whether Windows' scheduled optimize can run: its task (ScheduledDefrag, run by Automatic Maintenance weekly) on,
    /// Automatic Maintenance not turned off (MaintenanceDisabled, which optimizer tools set) and its service not disabled.
    /// Null when none can be read.
    /// </summary>
    internal static Optimize? ReadOptimize()
    {
        bool? maintenanceOff = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(MaintenanceKey);
            maintenanceOff = key?.GetValue("MaintenanceDisabled") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* unknown */ }
        bool? serviceOff = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + DefragService);
            serviceOff = key?.GetValue("Start") is int start && start == 4;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* unknown */ }
        var taskOff = TaskOff();
        return maintenanceOff is null && serviceOff is null && taskOff is null ? null : new Optimize(taskOff == true, maintenanceOff == true, serviceOff == true);
    }

    internal static TrimState? ReadTrim()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(FileSystemKey);
            if (key is null) return null;
            var ntfsOff = key.GetValue("NtfsDisableDeleteNotify") is int ntfs && ntfs != 0;
            var refsOff = key.GetValue("RefsDisableDeleteNotify") is int refs && refs != 0;
            return new TrimState(ntfsOff, refsOff);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>The optimize task turned off: its XML (schtasks /query /xml, no administrator rights) says Enabled false. Null when it can't be read.</summary>
    private static bool? TaskOff()
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in (string[])["/query", "/tn", DefragTask, "/xml", "ONE"]) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null) return null;
            using var bytes = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(bytes);
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000) || !copy.Wait(1000) || process.ExitCode != 0) return null;
            return TaskXmlOff(bytes.ToArray());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>A task's XML as schtasks writes it (UTF-16 or the console's code page): off when its settings say Enabled false.</summary>
    internal static bool? TaskXmlOff(byte[] bytes)
    {
        if (bytes.Length < 8) return null;
        var text = bytes.Length > 1 && (bytes[1] == 0 || bytes[0] == 0xFF) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        if (!text.Contains("<Task", StringComparison.Ordinal)) return null;
        var settings = text.IndexOf("<Settings>", StringComparison.Ordinal);
        var end = text.IndexOf("</Settings>", StringComparison.Ordinal);
        return settings >= 0 && end > settings && text[settings..end].Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
    }

    internal static Backup? LoadBackup()
    {
        try { return File.Exists(BackupPath) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private static bool SaveBackup(Backup? backup)
    {
        try
        {
            if (backup is null || backup.Flush.Count == 0 && backup.Optimize is null && backup.Cache is not { Count: > 0 } && backup.Trim is null)
            {
                DeletionGate.DeleteFile(BackupPath);
                return true;
            }
            var temp = BackupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(backup));
            File.Move(temp, BackupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Drives: the backup couldn't be saved: " + ex.Message);
            return false;
        }
    }

    private static readonly SemaphoreSlim ChangeGate = new(1, 1);

    /// <summary>
    /// One elevated step (Windows asks once): cache turns write caching on for every drive inside the PC that has it off,
    /// flush turns write-cache flushing back on for every drive that has it off,
    /// optimize lets Windows' weekly optimize run again, all does the three; each -undo puts back what Exo changed. What Exo
    /// replaces is saved first. Null when done or nothing to do, else the line to show.
    /// </summary>
    internal static string? Change(string action)
    {
        if (!ChangeGate.Wait(0)) return "A drive change is already running.";
        try
        {
            var disks = ReadDisks();
            var optimize = ReadOptimize();
            var trim = ReadTrim();
            var before = LoadBackup();
            var flush = new List<FlushChange>(before?.Flush ?? []);
            var cache = new List<CacheChange>(before?.Cache ?? []);
            var optimizeKept = before?.Optimize;
            var trimKept = before?.Trim;
            var writes = new List<(string Id, int Value)>();
            var cacheWrites = new List<(string Id, int? Value)>();
            OptimizeChange? on = null;
            TrimChange? trimOn = null;
            if (action is "cache" or "all")
                foreach (var disk in CachePending(disks))
                {
                    cacheWrites.Add((disk.InstanceId!, 1));
                    if (cache.All(c => c.InstanceId != disk.InstanceId)) cache.Add(new CacheChange(disk.InstanceId!, disk.CacheSetting));
                }
            if (action is "flush" or "all")
                foreach (var disk in FlushPending(disks))
                {
                    writes.Add((disk.InstanceId!, 0));
                    if (flush.All(c => c.InstanceId != disk.InstanceId)) flush.Add(new FlushChange(disk.InstanceId!, disk.FlushSetting ?? 1));
                }
            if (action is "optimize" or "all" && optimize is { Off: true })
            {
                on = new OptimizeChange(optimize.TaskOff, optimize.MaintenanceOff, optimize.ServiceOff);
                optimizeKept ??= on;
            }
            if (action is "trim" or "all" && trim is { Off: true } && disks.Any(d => d.Kind is "NVMe SSD" or "SSD"))
            {
                trimOn = new TrimChange(trim.NtfsOff, trim.RefsOff);
                trimKept ??= trimOn;
            }
            if (writes.Count == 0 && cacheWrites.Count == 0 && on is null && trimOn is null) return null;
            var lines = Commands(Path.Combine(Environment.SystemDirectory, "reg.exe"), Path.Combine(Environment.SystemDirectory, "schtasks.exe"), writes, on, cacheWrites, trimOn);
            if (lines is null) return "Exo didn't recognize a drive, so nothing changed.";
            if (!SaveBackup(new Backup(flush, optimizeKept, cache, trimKept))) return "Your current settings couldn't be saved, so nothing changed.";
            if (ElevatedCommands.Run(lines, "Drives") is { } refused)
            {
                SaveBackup(before);
                return refused;
            }
            AppLog.Info($"Drives: {action}: {writes.Count} flushing value(s), {cacheWrites.Count} write-caching value(s){(on is not null ? ", optimize on" : "")}{(trimOn is not null ? ", TRIM on" : "")}.");
            var after = ReadDisks();
            if (writes.Count > 0 && FlushPending(after).Count > 0) return "Windows didn't take the change.";
            if (cacheWrites.Count > 0 && CachePending(after).Count > 0) return "Windows didn't take the change.";
            if (on is not null && ReadOptimize() is { Off: true }) return "Windows didn't take the change.";
            if (trimOn is not null && ReadTrim() is { Off: true }) return "Windows didn't take the change.";
            return null;
        }
        finally { ChangeGate.Release(); }
    }

    /// <summary>
    /// DirectStorage's fast path for one file: FSCTL_MANAGE_BYPASS_IO's query on a handle opened for reading without
    /// buffering (a cached handle always answers no), shared every way so nothing else is held up. Null when it can bypass
    /// or the file can't be asked.
    /// </summary>
    internal static Blocked? BypassOf(string file)
    {
        using var handle = CreateFile(file, 0x80000000 /* GENERIC_READ */, 0x7 /* share read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x20000000 /* FILE_FLAG_NO_BUFFERING */, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var input = new byte[24]; // FS_BPIO_INPUT: FS_BPIO_OP_QUERY (3), flags, two reserved
        BitConverter.GetBytes(3).CopyTo(input, 0);
        var output = new byte[512];
        return DeviceIoControl(handle, 0x00090448 /* FSCTL_MANAGE_BYPASS_IO */, input, input.Length, output, output.Length, out _, IntPtr.Zero) ? ParseBypass(output) : null;
    }

    private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b"); // GUID_DEVINTERFACE_DISK
    private static readonly Guid PciDevice = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly DevPropKey CurrentSpeedKey = new(PciDevice, 9), CurrentWidthKey = new(PciDevice, 10), MaxSpeedKey = new(PciDevice, 11), MaxWidthKey = new(PciDevice, 12);
    private static readonly DevPropKey ServiceKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 6); // DEVPKEY_Device_Service
    private static readonly DevPropKey InstanceIdKey = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256); // DEVPKEY_Device_InstanceId

    private static byte[]? Raw(uint node, DevPropKey key, uint expected)
    {
        var data = new byte[512];
        var size = (uint)data.Length;
        return CM_Get_DevNode_Property(node, ref key, out var type, data, ref size, 0) == 0 && type == expected ? data[..(int)size] : null;
    }

    private static string? String(uint node, DevPropKey key) =>
        Raw(node, key, 0x12) is { } data && Encoding.Unicode.GetString(data).TrimEnd('\0').Trim() is { Length: > 0 } text ? text : null;

    private static uint? UInt(uint node, DevPropKey key) => Raw(node, key, 0x7) is { Length: >= 4 } data ? BitConverter.ToUInt32(data) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern uint CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    private static extern uint CM_Get_Device_Interface_Property(string path, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
