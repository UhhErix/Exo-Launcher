using System.Buffers.Binary;
using System.Text;

namespace ExoLauncher.Adapters;

/// <summary>
/// Resolves <c>steamclient64.dll</c> IPC vtable slots by decoding x64 RIP-relative
/// <c>LEA reg, [rip + disp32]</c> string references (method name + interface name),
/// using a 24-bit IPC hash prefix to disambiguate overloaded names and falling back
/// to the 24-bit hash prefix if a build lacks matching debug/trace strings.
/// </summary>
internal static class SteamIpcStubResolver
{
    internal const int MaxVtableSlots = 192;
    internal const int StubReadBytes = 0x180;
    private const int MinStubBytes = 0x40;
    private const int MaxAsciiStringBytes = 96;

    internal delegate bool ReadMemorySpan(long address, Span<byte> destination);

    internal readonly record struct StubSpec(
        string InterfaceName,
        string MethodName,
        uint HashPrefix24,
        bool RequireHashPrefix = false);

    private const string AppManager = "IClientAppManager";
    private const string RemoteStorage = "IClientRemoteStorage";

    internal static readonly StubSpec BIsDlcEnabled = new(AppManager, "BIsDlcEnabled", 0x143d4500u);
    internal static readonly StubSpec SetDlcEnabled = new(AppManager, "SetDlcEnabled", 0x0ec3c800u);
    internal static readonly StubSpec IsAppDlcInstalled = new(AppManager, "IsAppDlcInstalled", 0xcdbd8c00u);
    internal static readonly StubSpec GetCurrentLanguage = new(AppManager, "GetCurrentLanguage", 0x600aa900u, RequireHashPrefix: true);
    internal static readonly StubSpec SetCurrentLanguage = new(AppManager, "SetCurrentLanguage", 0x8050d900u);
    internal static readonly StubSpec GetActiveBeta = new(AppManager, "GetActiveBeta", 0x5062a000u);
    internal static readonly StubSpec SetActiveBeta = new(AppManager, "SetActiveBeta", 0x4d0b2000u);
    internal static readonly StubSpec CheckBetaPassword = new(AppManager, "CheckBetaPassword", 0x75217c00u);
    internal static readonly StubSpec GetAutoUpdate = new(AppManager, "GetAppAutoUpdateBehavior", 0x34e96400u);
    internal static readonly StubSpec SetAutoUpdate = new(AppManager, "SetAppAutoUpdateBehavior", 0xc5d6e500u);
    internal static readonly StubSpec GetBackground = new(AppManager, "GetAppAllowDownloadsWhileRunningBehavior", 0x7145c800u);
    internal static readonly StubSpec SetBackground = new(AppManager, "SetAppAllowDownloadsWhileRunningBehavior", 0xfe722100u);
    internal static readonly StubSpec NumLibraryFolders = new(AppManager, "GetNumLibraryFolders", 0xab50eb00u);
    internal static readonly StubSpec LibraryFolderPath = new(AppManager, "GetLibraryFolderPath", 0xf27c5d00u);
    internal static readonly StubSpec AppLibraryFolder = new(AppManager, "GetAppLibraryFolder", 0x166eba00u);
    internal static readonly StubSpec MoveApp = new(AppManager, "MoveApp", 0x47804a00u);
    internal static readonly StubSpec MoveProgress = new(AppManager, "GetMoveAppProgress", 0x55236b00u);

    internal static readonly StubSpec CloudForAccount = new(RemoteStorage, "IsCloudEnabledForAccount", 0x8694e900u);
    internal static readonly StubSpec CloudForApp = new(RemoteStorage, "IsCloudEnabledForApp", 0x88a72700u);
    internal static readonly StubSpec SetCloudForApp = new(RemoteStorage, "SetCloudEnabledForApp", 0x2eaada00u);

    internal static readonly StubSpec[] AllSpecs =
    [
        BIsDlcEnabled, SetDlcEnabled, IsAppDlcInstalled, GetCurrentLanguage, SetCurrentLanguage,
        GetActiveBeta, SetActiveBeta, CheckBetaPassword, GetAutoUpdate, SetAutoUpdate,
        GetBackground, SetBackground, NumLibraryFolders, LibraryFolderPath, AppLibraryFolder,
        MoveApp, MoveProgress, CloudForAccount, CloudForApp, SetCloudForApp,
    ];

    internal static long FindSlot(
        IReadOnlyList<long> vtableSlots,
        long moduleStart,
        long moduleEnd,
        ReadMemorySpan readMemory,
        StubSpec spec) =>
        FindByName(vtableSlots, moduleStart, moduleEnd, readMemory, spec) is var byName and not 0
            ? byName
            : FindByHashPrefix24(vtableSlots, moduleStart, moduleEnd, readMemory, spec.HashPrefix24);

    private delegate bool StubPredicate(ReadOnlySpan<byte> slice, long methodAddress);

    internal static long FindByName(
        IReadOnlyList<long> vtableSlots,
        long moduleStart,
        long moduleEnd,
        ReadMemorySpan readMemory,
        StubSpec spec) =>
        ScanUniqueSlot(vtableSlots, moduleStart, moduleEnd, readMemory,
            (slice, method) => StubMatchesName(slice, method, moduleStart, moduleEnd, readMemory, spec));

    internal static long FindByHashPrefix24(
        IReadOnlyList<long> vtableSlots,
        long moduleStart,
        long moduleEnd,
        ReadMemorySpan readMemory,
        uint hashPrefix24) =>
        ScanUniqueSlot(vtableSlots, moduleStart, moduleEnd, readMemory,
            (slice, _) => StubSendsHashPrefix24(slice[..FunctionScanLimit(slice)], hashPrefix24));

    private static long ScanUniqueSlot(
        IReadOnlyList<long> vtableSlots,
        long moduleStart,
        long moduleEnd,
        ReadMemorySpan readMemory,
        StubPredicate matches)
    {
        if (moduleStart <= 0 || moduleEnd <= moduleStart) return 0;
        long found = 0;
        Span<byte> stub = stackalloc byte[StubReadBytes];
        var limit = Math.Min(MaxVtableSlots, vtableSlots.Count);
        for (var index = 0; index < limit; index++)
        {
            var method = vtableSlots[index];
            if (method < moduleStart || method + MinStubBytes > moduleEnd) break;
            var readLen = (int)Math.Min(StubReadBytes, moduleEnd - method);
            var slice = stub[..readLen];
            if (!readMemory(method, slice)) continue;
            if (!matches(slice, method)) continue;
            if (found != 0 && found != method) return 0;
            found = method;
        }
        return found;
    }

    internal static int FunctionScanLimit(ReadOnlySpan<byte> stub)
    {
        var int3Run = 0;
        for (var i = 0x60; i < stub.Length; i++)
        {
            if (stub[i] == 0xCC)
            {
                int3Run++;
                if (int3Run >= 4) return i - 3;
            }
            else
            {
                int3Run = 0;
            }
        }
        return stub.Length;
    }

    internal static bool StubMatchesName(
        ReadOnlySpan<byte> stub,
        long methodAddress,
        long moduleStart,
        long moduleEnd,
        ReadMemorySpan readMemory,
        StubSpec spec)
    {
        var limit = FunctionScanLimit(stub);
        if (limit < 7) return false;
        var sawMethod = false;
        var sawInterface = false;
        Span<byte> strScratch = stackalloc byte[MaxAsciiStringBytes];
        for (var i = 0; i <= limit - 7; i++)
        {
            // x64 RIP-relative LEA: REX.W (0x48..0x4F), 0x8D, ModR/M (mod=00, rm=101 => & 0xC7 == 0x05), disp32.
            if ((stub[i] & 0xF8) != 0x48 || stub[i + 1] != 0x8D || (stub[i + 2] & 0xC7) != 0x05)
                continue;
            var disp = BinaryPrimitives.ReadInt32LittleEndian(stub.Slice(i + 3, 4));
            var target = methodAddress + i + 7L + disp;
            if (!TryReadAsciiString(target, moduleStart, moduleEnd, readMemory, strScratch, out var text))
                continue;
            if (string.Equals(text, spec.MethodName, StringComparison.Ordinal)) sawMethod = true;
            else if (string.Equals(text, spec.InterfaceName, StringComparison.Ordinal)) sawInterface = true;
            if (sawMethod && sawInterface && !spec.RequireHashPrefix) return true;
        }
        if (!sawMethod || !sawInterface) return false;
        return !spec.RequireHashPrefix || StubSendsHashPrefix24(stub[..limit], spec.HashPrefix24);
    }

    /// <summary>Matches <c>mov dword ptr [rbp/rsp+disp], imm32</c> ignoring the volatile low byte.</summary>
    internal static bool StubSendsHashPrefix24(ReadOnlySpan<byte> stub, uint hashPrefix24)
    {
        var masked = hashPrefix24 & 0xFFFFFF00u;
        Span<byte> needle = stackalloc byte[3];
        needle[0] = (byte)(masked >> 8);
        needle[1] = (byte)(masked >> 16);
        needle[2] = (byte)(masked >> 24);
        var offset = 1;
        while (offset <= stub.Length - 3)
        {
            var hit = stub[offset..].IndexOf(needle);
            if (hit < 0) break;
            var immAt = offset + hit - 1;
            foreach (var back in (ReadOnlySpan<int>)[3, 4, 6, 7])
            {
                if (immAt >= back && stub[immAt - back] == 0xC7) return true;
            }
            offset += hit + 1;
        }
        return false;
    }

    private static bool TryReadAsciiString(
        long address,
        long moduleStart,
        long moduleEnd,
        ReadMemorySpan readMemory,
        Span<byte> scratch,
        out string value)
    {
        value = "";
        if (address < moduleStart || address >= moduleEnd) return false;
        var maxRead = (int)Math.Min(scratch.Length, moduleEnd - address);
        if (maxRead < 4) return false;
        var dest = scratch[..maxRead];
        if (!readMemory(address, dest)) return false;
        var nul = dest.IndexOf((byte)0);
        if (nul < 3) return false;
        for (var i = 0; i < nul; i++)
        {
            if (dest[i] is < 0x20 or > 0x7E) return false;
        }
        value = Encoding.ASCII.GetString(dest[..nul]);
        return true;
    }
}
