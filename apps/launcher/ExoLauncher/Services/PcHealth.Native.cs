using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// PC health's reads of the hardware: SMBIOS through GetSystemFirmwareTable (no WMI), Direct3D 12's
/// OPTIONS16 on the high-performance adapter, the processor's vendor and hybrid flag (CPUID), and Windows' power plans
/// and power mode (powrprof).
/// </summary>
internal static partial class PcHealth
{
    /// <summary>
    /// This PC's hardware, as a short one-way id: the SHA-256 of the board's SMBIOS system UUID (type 1). It outlives a
    /// Windows reinstall, which is what tells a backup of this PC from another PC's. Null when the firmware gives no usable UUID.
    /// </summary>
    internal static string? MachineId() => MachineIdOf(SmbiosTable());

    internal static string? MachineIdOf(ReadOnlySpan<byte> raw)
    {
        string? id = null;
        Walk(raw, (type, structure, _) =>
        {
            if (type != 1 || structure.Length < 24 || id is not null) return;
            var uuid = structure.Slice(8, 16);
            if (IsPlaceholderUuid(uuid)) return;
            var data = new byte[12 + 16];
            "exo-machine|"u8.CopyTo(data);
            uuid.CopyTo(data.AsSpan(12));
            id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data))[..16];
        });
        return id;
    }

    /// <summary>
    /// UUIDs a board maker left as a placeholder: the same on every board of that kind, so they mark no one PC. All one byte
    /// (zeros, FFs), and the counting patterns firmware kits ship ("03000200-0400-0500-0006-000700080009" and its kin).
    /// </summary>
    internal static bool IsPlaceholderUuid(ReadOnlySpan<byte> uuid)
    {
        if (uuid.Length != 16 || !uuid.ContainsAnyExcept(uuid[0])) return true;
        var text = new Guid(uuid).ToString();
        return text is "03000200-0400-0500-0006-000700080009" or "00020003-0004-0005-0006-000700080009"
            or "12345678-1234-5678-90ab-cddeefaabbcc" or "01234567-8910-1112-1314-151617181920" or "fefefefe-fefe-fefe-fefe-fefefefefefe";
    }

    private static byte[] SmbiosTable()
    {
        const uint rsmb = 0x52534D42; // 'RSMB'
        try
        {
            var size = GetSystemFirmwareTable(rsmb, 0, null, 0);
            if (size == 0) return [];
            var buffer = new byte[size];
            return GetSystemFirmwareTable(rsmb, 0, buffer, size) == size ? buffer : [];
        }
        catch (EntryPointNotFoundException) { return []; }
    }

    /// <summary>
    /// The adapter Windows picks for high performance (a laptop's discrete GPU), skipping Microsoft's software
    /// renderer: its name, whether it is integrated (UMA), and GPU upload heaps, which the driver reports only when
    /// the CPU can see all of the card's memory (Resizable BAR on). Read once: it changes only with a reboot.
    /// </summary>
    private static unsafe GpuBar? ReadGpu()
    {
        try
        {
            var factoryIid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"); // IDXGIFactory6
            if (CreateDXGIFactory2(0, in factoryIid, out var factory) < 0) return null;
            try
            {
                var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
                var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[29]; // EnumAdapterByGpuPreference
                for (uint i = 0; i < 8; i++)
                {
                    IntPtr adapter;
                    if (byPreference(factory, i, 2 /* DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE */, &adapterIid, &adapter) < 0) return null;
                    try
                    {
                        AdapterDesc1 desc;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                        if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        var deviceIid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
                        if (D3D12CreateDevice(adapter, 0xb000, in deviceIid, out var device) < 0) return new GpuBar(name, false, null);
                        try
                        {
                            var check = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(IntPtr**)device)[13]; // CheckFeatureSupport
                            var architecture = stackalloc uint[4]; // NodeIndex, TileBasedRenderer, UMA, CacheCoherentUMA
                            architecture[0] = 0;
                            var uma = check(device, 1 /* D3D12_FEATURE_ARCHITECTURE */, architecture, 16) >= 0 && architecture[2] != 0;
                            var options16 = stackalloc int[2]; // DynamicDepthBiasSupported, GPUUploadHeapSupported
                            bool? upload = check(device, 45 /* D3D12_FEATURE_D3D12_OPTIONS16 */, options16, 8) >= 0 ? options16[1] != 0 : null;
                            return new GpuBar(name, uma, upload);
                        }
                        finally { Marshal.Release(device); }
                    }
                    finally { Marshal.Release(adapter); }
                }
                return null;
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("PC health: Direct3D 12 couldn't be asked about Resizable BAR: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A PC with two graphics chips, as Windows itself tells them apart: the adapter it picks to save power and the one it picks for
    /// performance are different chips (a laptop's built-in graphics and its GeForce or Radeon). Null with one chip, or when
    /// Direct3D can't be asked. Read once: it changes only with hardware.
    /// </summary>
    private static unsafe HybridGpu? ReadHybrid()
    {
        try
        {
            var factoryIid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"); // IDXGIFactory6
            if (CreateDXGIFactory2(0, in factoryIid, out var factory) < 0) return null;
            try
            {
                var saving = FirstAdapter(factory, 1 /* DXGI_GPU_PREFERENCE_MINIMUM_POWER */);
                var fast = FirstAdapter(factory, 2 /* DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE */);
                return saving is { } low && fast is { } high && low.Luid != high.Luid ? new HybridGpu(low.Name, high.Name) : null;
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("PC health: Direct3D couldn't be asked for the graphics chips: " + ex.Message);
            return null;
        }
    }

    /// <summary>PCI DeviceId of the active high-performance NVIDIA adapter from DXGI (VendorId 0x10DE), else null.</summary>
    internal static unsafe uint? ActiveNvidiaDeviceId()
    {
        try
        {
            var factoryIid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"); // IDXGIFactory6
            if (CreateDXGIFactory2(0, in factoryIid, out var factory) < 0) return null;
            try
            {
                var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
                var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[29];
                for (uint i = 0; i < 8; i++)
                {
                    IntPtr adapter;
                    if (byPreference(factory, i, 2 /* DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE */, &adapterIid, &adapter) < 0) return null;
                    try
                    {
                        AdapterDesc1 desc;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                        if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0) continue;
                        if (desc.VendorId == 0x10DE) return desc.DeviceId;
                    }
                    finally { Marshal.Release(adapter); }
                }
                return null;
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return null; }
    }

    /// <summary>The adapter Windows picks for <paramref name="preference"/>, skipping Microsoft's software renderer: its name and LUID.</summary>
    private static unsafe (string Name, long Luid)? FirstAdapter(IntPtr factory, int preference)
    {
        var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
        var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[29]; // EnumAdapterByGpuPreference
        for (uint i = 0; i < 8; i++)
        {
            IntPtr adapter;
            if (byPreference(factory, i, preference, &adapterIid, &adapter) < 0) return null;
            try
            {
                AdapterDesc1 desc;
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                return (new string(desc.Description).TrimEnd('\0').Trim(), desc.AdapterLuid);
            }
            finally { Marshal.Release(adapter); }
        }
        return null;
    }

    /// <summary>True when a dual-GPU PC has the primary display attached to the power-saving GPU and not the high-performance GPU.</summary>
    internal static unsafe bool ReadPrimaryOnIntegrated()
    {
        try
        {
            var factoryIid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"); // IDXGIFactory6
            if (CreateDXGIFactory2(0, in factoryIid, out var factory) < 0) return false;
            try
            {
                var low = FirstAdapterHandle(factory, 1 /* DXGI_GPU_PREFERENCE_MINIMUM_POWER */);
                var high = FirstAdapterHandle(factory, 2 /* DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE */);
                try
                {
                    if (low.Handle == IntPtr.Zero || high.Handle == IntPtr.Zero || low.Luid == high.Luid) return false;
                    return HasPrimaryMonitorOutput(low.Handle) && !HasPrimaryMonitorOutput(high.Handle);
                }
                finally
                {
                    if (low.Handle != IntPtr.Zero) Marshal.Release(low.Handle);
                    if (high.Handle != IntPtr.Zero) Marshal.Release(high.Handle);
                }
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return false; }
    }

    private static unsafe (IntPtr Handle, long Luid) FirstAdapterHandle(IntPtr factory, int preference)
    {
        var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
        var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[29];
        for (uint i = 0; i < 8; i++)
        {
            IntPtr adapter;
            if (byPreference(factory, i, preference, &adapterIid, &adapter) < 0) return default;
            AdapterDesc1 desc;
            var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
            if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0)
            {
                Marshal.Release(adapter);
                continue;
            }
            return (adapter, desc.AdapterLuid);
        }
        return default;
    }

    private static unsafe bool HasPrimaryMonitorOutput(IntPtr adapter)
    {
        var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)adapter)[7]; // IDXGIAdapter::EnumOutputs
        for (uint i = 0; i < 16; i++)
        {
            IntPtr output;
            if (enumOutputs(adapter, i, &output) < 0) break;
            try
            {
                DxgiOutputDesc desc;
                var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DxgiOutputDesc*, int>)(*(IntPtr**)output)[7]; // IDXGIOutput::GetDesc
                if (getDesc(output, &desc) < 0 || desc.AttachedToDesktop == 0 || desc.Monitor == IntPtr.Zero) continue;
                var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfoW(desc.Monitor, ref info) && (info.dwFlags & 1 /* MONITORINFOF_PRIMARY */) != 0)
                    return true;
            }
            finally { Marshal.Release(output); }
        }
        return false;
    }

    /// <summary>Reads current and maximum PCIe link widths of the active discrete GPU via cfgmgr32, or null when unreadable.</summary>
    internal static PcieLink? ReadGpuPcie(string? mainGpuName)
    {
        try
        {
            var displayClass = new Guid("1ca05180-a699-450a-9a0c-de4fbe3ddd89"); // GUID_DISPLAY_DEVICE_ARRIVAL
            if (CM_Get_Device_Interface_List_SizeW(out var chars, in displayClass, IntPtr.Zero, 0) != 0 || chars < 2)
                return null;
            var buf = new char[chars];
            if (CM_Get_Device_Interface_ListW(in displayClass, IntPtr.Zero, buf, chars, 0) != 0)
                return null;

            PcieLink? fallback = null;
            foreach (var iface in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var inst = ReadInterfaceString(iface, in DevPkeyDeviceInstanceId);
                if (string.IsNullOrWhiteSpace(inst) || !inst.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
                if (CM_Locate_DevNodeW(out var devInst, inst, 0) != 0) continue;
                var name = ReadDevNodeString(devInst, in DevPkeyDeviceFriendlyName)
                    ?? ReadDevNodeString(devInst, in DevPkeyDeviceDesc);
                if (string.IsNullOrWhiteSpace(name) || !GraphicsService.Discrete(name)) continue;
                if (ReadDevNodeU32(devInst, in DevPkeyPciCurrentLinkWidth) is not { } cur
                    || ReadDevNodeU32(devInst, in DevPkeyPciMaxLinkWidth) is not { } max
                    || cur == 0 || max == 0) continue;
                var link = new PcieLink(name, cur, max);
                if (!string.IsNullOrWhiteSpace(mainGpuName)
                    && (name.Contains(mainGpuName, StringComparison.OrdinalIgnoreCase)
                        || mainGpuName.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    return link;
                fallback ??= link;
            }
            return fallback;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("PC health: PCIe link width couldn't be asked: " + ex.Message);
            return null;
        }
    }

    private static string? ReadInterfaceString(string iface, in DevPropKey key)
    {
        uint size = 0;
        var cr = CM_Get_Device_Interface_PropertyW(iface, in key, out var type, null, ref size, 0);
        if (cr != 0x1A /* CR_BUFFER_SMALL */ || size is < 2 or > 4096) return null;
        var bytes = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(iface, in key, out type, bytes, ref size, 0) != 0 || type != 18 /* DEVPROP_TYPE_STRING */)
            return null;
        return System.Text.Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0').Trim();
    }

    private static string? ReadDevNodeString(uint devInst, in DevPropKey key)
    {
        uint size = 0;
        var cr = CM_Get_DevNode_PropertyW(devInst, in key, out var type, null, ref size, 0);
        if (cr != 0x1A /* CR_BUFFER_SMALL */ || size is < 2 or > 4096) return null;
        var bytes = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, in key, out type, bytes, ref size, 0) != 0 || type != 18 /* DEVPROP_TYPE_STRING */)
            return null;
        return System.Text.Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0').Trim();
    }

    private static uint? ReadDevNodeU32(uint devInst, in DevPropKey key)
    {
        var bytes = new byte[4];
        var size = 4u;
        if (CM_Get_DevNode_PropertyW(devInst, in key, out var type, bytes, ref size, 0) != 0 || type != 7 /* DEVPROP_TYPE_UINT32 */ || size != 4)
            return null;
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static readonly DevPropKey DevPkeyDeviceInstanceId = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    private static readonly DevPropKey DevPkeyDeviceDesc = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly DevPropKey DevPkeyDeviceFriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey DevPkeyPciMaxLinkWidth = new(new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62"), 9);
    private static readonly DevPropKey DevPkeyPciCurrentLinkWidth = new(new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62"), 10);

    /// <summary>CPUID leaf 7's hybrid flag (EDX bit 15: P- and E-cores); null when CPUID can't be asked.</summary>
    private static bool? CpuHybrid()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return null;
        if (System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0).Eax < 7) return false;
        return (System.Runtime.Intrinsics.X86.X86Base.CpuId(7, 0).Edx & (1 << 15)) != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    /// <summary>The power plan and power mode Windows has on, and the two switches Settings > Power mode itself flips.</summary>
    internal interface IPowerApi
    {
        Guid? Active();
        /// <summary>The power mode (overlay) in effect: <see cref="Guid.Empty"/> is Balanced.</summary>
        Guid Overlay();
        /// <summary>On battery or with battery saver on: the power mode is left alone then.</summary>
        bool Battery();
        string? Name(Guid scheme);
        bool SetActive(Guid scheme);
        bool SetOverlay(Guid overlay);
        /// <summary>A setting's AC value in <paramref name="scheme"/> (GUIDs as <c>powercfg /qh</c> lists them); null when the plan doesn't have it.</summary>
        uint? AcValue(Guid scheme, Guid subgroup, Guid setting);
        /// <summary>A setting's battery (DC) value in <paramref name="scheme"/>; null when the plan doesn't have it.</summary>
        uint? DcValue(Guid scheme, Guid subgroup, Guid setting);
        /// <summary>A new plan copied from <paramref name="source"/> in <paramref name="made"/>; Windows' error code, 0 when it made one.</summary>
        uint Duplicate(Guid source, out Guid made);
        bool Rename(Guid scheme, string name, string description);
        /// <summary>Writes a setting's AC (or, with <paramref name="dc"/>, battery) value.</summary>
        bool Write(Guid scheme, Guid subgroup, Guid setting, uint value, bool dc);
        /// <summary>Shows a setting in Control Panel's advanced power settings (Attributes 2); needs an administrator.</summary>
        bool Show(Guid subgroup, Guid setting);
        /// <summary>Every plan Windows lists.</summary>
        IReadOnlyList<Guid> Schemes();
        string? Description(Guid scheme);
        /// <summary>Deletes a plan; Windows refuses the one that's on.</summary>
        bool Delete(Guid scheme);
    }

    /// <summary>
    /// powrprof: PowerSetActiveScheme is documented; the power mode calls (PowerGetEffectiveOverlayScheme,
    /// PowerSetActiveOverlayScheme) are not, but they are what Settings > Power mode uses, and Lenovo Legion
    /// Toolkit and G-Helper call them the same way (the GUID by value, only while Balanced is on, never on battery saver).
    /// </summary>
    internal sealed class NativePower : IPowerApi
    {
        public Guid? Active()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
        }

        public Guid Overlay()
        {
            try { return PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? overlay : Guid.Empty; }
            catch (EntryPointNotFoundException) { return Guid.Empty; }
        }

        public bool Battery() => GetSystemPowerStatus(out var status) && (status.AcLineStatus == 0 || status.SystemStatusFlag == 1);

        /// <summary>The PC has a system battery (BatteryFlag 128: none; 255: unknown).</summary>
        internal static bool HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);

        public string? Name(Guid scheme)
        {
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size is 0 or > 4096) return null;
            var buffer = new byte[size];
            return PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
                : null;
        }

        public bool SetActive(Guid scheme) => !HostMode.IsDisposable && PowerSetActiveScheme(IntPtr.Zero, in scheme) == 0;

        public uint? AcValue(Guid scheme, Guid subgroup, Guid setting) =>
            PowerReadACValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, out var value) == 0 ? value : null;

        public uint? DcValue(Guid scheme, Guid subgroup, Guid setting) =>
            PowerReadDCValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, out var value) == 0 ? value : null;

        public uint Duplicate(Guid source, out Guid made)
        {
            made = Guid.Empty;
            if (HostMode.IsDisposable) return 5u;
            var pointer = IntPtr.Zero;
            var code = PowerDuplicateScheme(IntPtr.Zero, in source, ref pointer);
            if (pointer == IntPtr.Zero) return code == 0 ? 13u /* ERROR_INVALID_DATA: no plan came back */ : code;
            try { made = Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
            return code;
        }

        public bool Rename(Guid scheme, string name, string description)
        {
            if (HostMode.IsDisposable) return false;
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(name + "\0");
            var descriptionBytes = System.Text.Encoding.Unicode.GetBytes(description + "\0");
            return PowerWriteFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, nameBytes, (uint)nameBytes.Length) == 0
                & PowerWriteDescription(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, descriptionBytes, (uint)descriptionBytes.Length) == 0;
        }

        public bool Write(Guid scheme, Guid subgroup, Guid setting, uint value, bool dc) =>
            !HostMode.IsDisposable && (dc ? PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, value) : PowerWriteACValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, value)) == 0;

        public bool Show(Guid subgroup, Guid setting) => !HostMode.IsDisposable && PowerWriteSettingAttributes(in subgroup, in setting, 2) == 0;

        public bool SetOverlay(Guid overlay)
        {
            if (HostMode.IsDisposable) return false;
            try { return PowerSetActiveOverlayScheme(overlay) == 0; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public IReadOnlyList<Guid> Schemes()
        {
            const uint AccessScheme = 16;
            var schemes = new List<Guid>();
            var buffer = new byte[16];
            for (uint index = 0; index < 256; index++)
            {
                var size = (uint)buffer.Length;
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size) != 0) break;
                schemes.Add(new Guid(buffer));
            }
            return schemes;
        }

        public string? Description(Guid scheme)
        {
            uint size = 0;
            if (PowerReadDescription(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size is 0 or > 4096) return null;
            var buffer = new byte[size];
            return PowerReadDescription(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
                : null;
        }

        public bool Delete(Guid scheme) => PowerDeleteScheme(IntPtr.Zero, in scheme) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory2(uint flags, in Guid riid, out IntPtr factory);

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, in Guid riid, out IntPtr device);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, in Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr root, in Guid source, ref IntPtr destination);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteFriendlyName(IntPtr root, in Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDescription(IntPtr root, in Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteSettingAttributes(in Guid subgroup, in Guid setting, uint attributes);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr root, in Guid scheme, IntPtr subgroup, IntPtr setting, byte[]? buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDescription(IntPtr root, in Guid scheme, IntPtr subgroup, IntPtr setting, byte[]? buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr root, IntPtr schemeGuid, IntPtr subgroupGuid, uint accessFlags, uint index, byte[] buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeleteScheme(IntPtr root, in Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DxgiOutputDesc
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public MonitorRect rcMonitor;
        public MonitorRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint pulLen, in Guid interfaceClassGuid, IntPtr pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(in Guid interfaceClassGuid, IntPtr pDeviceID, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, in DevPropKey propertyKey, out uint propertyType, [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, in DevPropKey propertyKey, out uint propertyType, [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}
