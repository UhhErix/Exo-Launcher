using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Helpers;

/// <summary>
/// This PC's processor, installed memory, graphics card and Windows, read once (none of it changes
/// while Exo runs), and the free space where a Store game would go, read each time: the Steam library
/// with the most room, else the Windows drive. The Store judges a game's requirements against it.
/// </summary>
internal static partial class SystemSpecs
{
    private static readonly Lazy<StoreService.ThisPc> Once = new(ReadNow, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static StoreService.ThisPc Read() => Once.Value with { FreeGb = FreeGb(), Online = NetworkInterface.GetIsNetworkAvailable() };

    /// <summary>The main graphics card's name, as <see cref="Read"/> has it, without reading the drives.</summary>
    internal static string? Gpu => Once.Value.Gpu;

    /// <summary>The main graphics card's name for frame generation (<see cref="FrameGenName"/>).</summary>
    internal static string? FrameGenGpu => FrameGenName(Gpu, Once.Value.Cpu);

    /// <summary>
    /// The graphics card's name as frame generation reads it (DlssSwapService.FrameGenCardFor): a handheld's Radeon, which
    /// Windows names only "AMD Radeon Graphics" (a ROG Ally's Ryzen Z1 Extreme), carries its processor's name, so a Z1, Z2
    /// or Ryzen AI chip is told from a desktop processor's small Radeon of the same name. Any other name is as it is.
    /// </summary>
    internal static string? FrameGenName(string? gpu, string? cpu) =>
        gpu is not null && cpu is not null && BareRadeon().IsMatch(gpu) && HandheldChip().IsMatch(cpu) ? $"{gpu} ({cpu})" : gpu;

    [GeneratedRegex(@"^AMD\s+Radeon(?:\(TM\))?\s+Graphics$", RegexOptions.IgnoreCase)]
    private static partial Regex BareRadeon();

    [GeneratedRegex(@"\bRyzen\s+(?:Z[12]|AI)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HandheldChip();

    private static StoreService.ThisPc ReadNow()
    {
        string? cpu = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = Cpu(key?.GetValue("ProcessorNameString") as string);
        }
        catch { /* left out */ }
        // The memory installed (what the box says), not what Windows can use after hardware reserves.
        var ramGb = GetPhysicallyInstalledSystemMemory(out var kilobytes) ? (int)Math.Round(kilobytes / 1048576.0) : 0;
        var windows = Environment.OSVersion.Version.Build >= 22000 ? 11 : 10;
        // The adapter names from the driver key and Direct3D alone: never the GPU profile, whose read can write the frame cap.
        var fastest = PcHealth.MainGpu() is { Uma: false } card ? card.Name : null;
        return new StoreService.ThisPc(cpu, MainGpu(GpuCapability.Adapters(), fastest), ramGb, windows, null, GpuCapability.VideoMemoryGb(), DirectXVersion());
    }

    /// <summary>The processor as the game pages write it: "AMD Ryzen 5 5600X 6-Core Processor" is "AMD Ryzen 5 5600X".</summary>
    internal static string? Cpu(string? name) => name is null ? null
        : Regex.Replace(name, @"\s+(\d+-Core|Six-Core|Eight-Core|Quad-Core)?\s*Processor\s*$|\(R\)|\(TM\)|\s+CPU\s+@.*$", "", RegexOptions.IgnoreCase).Trim();

    /// <summary>
    /// The main graphics card, without "(R)" and "(TM)": the one Windows picks for high performance when it has memory of
    /// its own (<paramref name="fastest"/>, from Direct3D, which lists only cards that are there: the driver key still names
    /// one taken out). Else by the adapters' names: the first vendor's (a card of its own first,
    /// <see cref="GraphicsService.Vendors"/>), its card before a processor's graphics (an Arc beside Intel's UHD), and a
    /// name with a model number before "Radeon Graphics".
    /// </summary>
    internal static string? MainGpu(IReadOnlyList<string> adapters, string? fastest = null) =>
        (fastest ?? GraphicsService.Vendors(adapters)
            .Select(vendor => adapters.Where(name => GraphicsService.Vendors([name]).Contains(vendor))
                .OrderByDescending(GraphicsService.Discrete).ThenByDescending(name => name.Any(char.IsAsciiDigit)).First())
            .FirstOrDefault())?.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>The most free space on a Steam library's drive (where Get on Steam installs), else on the Windows drive, in GB.</summary>
    private static double? FreeGb()
    {
        var roots = new List<string>();
        if (SteamInstallation.ResolveRoot() is { } steam)
        {
            roots.Add(steam);
            try
            {
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    roots.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the Steam folder alone */ }
        }
        else roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        double? best = null;
        foreach (var drive in roots.Select(Path.GetPathRoot).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { best = Math.Max(best ?? 0, new DriveInfo(drive).AvailableFreeSpace / 1073741824.0); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* not ready */ }
        }
        return best;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    /// <summary>
    /// The DirectX a game can count on: 12 when the main graphics card runs Direct3D 12 at feature level 12_0 (asked
    /// of the driver without making a device), else 11, which every card Windows 10 and 11 run on has.
    /// </summary>
    private static int DirectXVersion() => Direct3D12(0xc000) ? 12 : 11;

    /// <summary>Whether the main graphics card runs Direct3D 12 at a feature level (0xc000 is 12_0; 0xc200, 12_2, is DirectX 12 Ultimate).</summary>
    internal static bool Direct3D12(int featureLevel)
    {
        try
        {
            var device = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
            return D3D12CreateDevice(IntPtr.Zero, featureLevel, ref device, IntPtr.Zero) >= 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // A null device asks only whether one could be made (S_FALSE when it could).
    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);
}
