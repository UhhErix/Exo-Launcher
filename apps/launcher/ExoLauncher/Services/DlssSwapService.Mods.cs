using System.Diagnostics;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Upscaler mods. A file a mod put under a vendor's name (HelixSR's FSR stand-in, OptiScaler's FSR loader under FSR
/// 3.1's name, a community FSR 4 build) is the mod's: never part of Update all, refused alone, never restored over,
/// and shown by the mod's name. A file is the mod's when
/// - it sits in a folder only a mod makes (OptiScaler\, HelixSR-fsrfg\, a mod's backup), whatever it is;
/// - it says so itself: HelixSR's network trailer, a mod named in its version information, an AMD library under
///   another library's name, or an FSR 4 upscaler AMD didn't sign; or
/// - a mod's marker is in its folder and it isn't the vendor's own file under this name (signed by the company that
///   owns the name, with that company's identity and the role the name says).
/// A game's own unsigned FSR build with no marker beside it stays the game's and updates as before. The mods Exo puts in
/// itself for frame generation (OptiScaler and DLSSG SM86, DlssSwapService.FrameGen.cs) are read the same way. Footprints from each mod's README, install scripts and build files, October 2026;
/// AMDNR's, dlss-unlocked's and the FSR 4 shader overrides' from their READMEs, November 2026.
///
/// Loaders that are not upscaler mods (UE4SS, Ultimate ASI Loader; <see cref="LoaderModIn"/>) are named only for frame
/// generation, so Exo leaves a game with one alone: they never make an upscaler file a mod's.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>A mod Exo knows by its marker but not by name.</summary>
    internal const string UnnamedMod = "Mod";

    /// <param name="Inside">Paths under the folder that only this mod makes there: dlss-unlocked's Streamline and DLSSG SM86 inside its OptiScaler folder.</param>
    private sealed record UpscalerMod(string Name, string[] Files, string[] Folders, string[]? Inside = null);

    /// <summary>
    /// Marker files and the folders only that mod makes, most specific first: AMDNR and dlss-unlocked are OptiScaler
    /// builds (dlss-unlocked also carries DLSSG SM86 and DLSS Enabler), HelixSR Scaler and DLSS Enabler carry
    /// OptiScaler's files, and OptiScaler carries Nukem's dlssg-to-fsr3.
    /// </summary>
    private static readonly UpscalerMod[] UpscalerMods =
    [
        // AMDNR (3zwr1's OptiScaler build): its neural rendering runtimes, their weights and logs, and the launcher's folders.
        new("AMDNR",
            ["lmxxfnrruntime.dll", "lmxxfnrruntime.pak", "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll",
             "dlssnr_on_amd_weights.bin", "dlssnr_on_amd.ini", "dlssnr_on_amd.log", "danielblnc_attribution.txt", "amd_presr.log",
             "amd_bridge.log", "lmxxf_backend.log", "lmxxf_vk_launch.pending", "amdnr_dlssg_fsr3.dll"],
            ["AMDNR-SR-L", "AMDNR_backup", "DLSS5-AMD"],
            [@"OptiScaler\amdnr_dlssg_fsr3.dll"]),
        // dlss-unlocked (ShyVortex): its DLSS-NR forwarder beside the game, the RTX 30 Smooth Motion file, RE Engine's
        // patch folder, and Streamline and DLSSG SM86 inside its OptiScaler folder (a game's own Streamline folder is the game's).
        new("dlss-unlocked",
            ["nvngx.dll_dlssnr.dll", "nvsmooth30.dll"],
            ["REPatch"],
            [@"OptiScaler\streamline", @"OptiScaler\dlssg_sm86", @"OptiScaler\nvsmooth30.dll"]),
        // zgauthier2000's FSR 4 shader overrides: AMD's upscaler with its GPU check taken out, the game's kept as *.orig.
        new("FSR 4 shader overrides", ["amd_fidelityfx_upscaler_dx12.dll.orig"], []),
        // The FSR drop-in keeps the game's file as *.original.dll (3.x: AMD's FSR 4 as *.amd.dll) and logs beside it;
        // HelixSR Scaler is OptiScaler with HelixSR's folders and an install record.
        new("HelixSR",
            ["helixsr.ini", "helixsr.log", "hlx_fsr.log", "helixsr_loader.log", "helixsr_weights.bin", "helixsr_kernels.pak",
             "helixsr-scaler.files", "helixsr-scaler-remove.sh", "amd_fidelityfx_dx12.original.dll",
             "amd_fidelityfx_upscaler_dx12.original.dll", "amd_fidelityfx_upscaler_dx12.amd.dll"],
            ["HelixSR", "HelixSR-fsrfg", "HelixSR-Scaler-backup", "HLX-FSR4", "helixsr-fg"]),
        // DLSS frame generation on RTX 20 and 30: MFG Unlocker's settings, status and older names, and DLSSG SM86's folder.
        new("MFG Unlocker",
            ["rtxmfg-universal.json", "rtxmfg-universal.status.json", "rtx40mfg-ui.ini", "rtxmfg-ui.ini", "rtx40mfgcore.dll", "rtx40mfg.asi",
             "mfgunlocker.dll", "mfgunlocker.asi", "rtxmfg.dll"],
            []),
        new("DLSSG SM86", ["dlssg_sm86.ini"], ["dlssg_sm86"]),
        new("DLSS Enabler",
            ["dlss-enabler.dll", "dlss-enabler-upscaler.dll", "dlss-enabler.log", "nvngx-wrapper.dll", "nvapi64-proxy.dll", "dlss-finder.exe"],
            []),
        // Its own amd_fidelityfx_dx12.dll, the game's kept as *.amd.dll.
        new("fsr2dlss", ["fsr2dlss.ini", "ffx_bridge.log", "amd_fidelityfx_dx12.amd.dll"], []),
        new("Uniscaler", ["uniscaler.asi", "uniscaler.config.toml", "fsr2fsr3.asi", "fsr2fsr3.config.toml"], ["uniscaler"]),
        new("PureDark's upscaler", ["pdperfplugin.dll"], ["UpscalerBasePlugin"]),
        // 0.9 puts AMD's signed FidelityFX loader in as amd_fidelityfx_dx12.dll; its own folders hold the rest.
        new("OptiScaler",
            ["optiscaler.ini", "optiscaler.log", "optiscaler.asi", "remove_optiscaler.bat", "remove optiscaler.bat", "optiscaler setup.bat"],
            ["OptiScaler", "D3D12_Optiscaler", "DlssOverrides"]),
        new("dlssg-to-fsr3", ["dlssg_to_fsr3_amd_is_better.dll", "dlssg_to_fsr3.log", "dlssg_to_fsr3.ini", "dlssg_to_fsr3.asi"], []),
        // CyberFSR2 and OptiScaler before 0.7 (and DLSS Enabler's copy of OptiScaler's settings).
        new(UnnamedMod, ["nvngx.ini"], []),
    ];

    /// <summary>Words a mod's own build carries in its version information (OptiScaler's company is "nitec").</summary>
    private static readonly (string Word, string Mod)[] ModVersionWords =
    [
        ("amdnr", "AMDNR"), ("dlss-unlocked", "dlss-unlocked"), ("cyboman", "FSR 4 shader overrides"),
        ("helixsr", "HelixSR"), ("hlx fsr", "HelixSR"), ("hlx_fsr", "HelixSR"),
        ("dlss enabler", "DLSS Enabler"), ("dlss-enabler", "DLSS Enabler"),
        ("fsr2dlss", "fsr2dlss"), ("uniscaler", "Uniscaler"), ("optiscaler", "OptiScaler"),
        ("dlssg_to_fsr3", "dlssg-to-fsr3"), ("dlssg-to-fsr3", "dlssg-to-fsr3"), ("mfg unlocker", "MFG Unlocker"),
    ];

    /// <summary>AMD names each FidelityFX library's role in its description; FSR 3.1's own ("DX12 AMD FidelityFX Library") names none.</summary>
    private static readonly (string Word, string Name)[] AmdRoles =
    [
        ("Loader", "_loader_"), ("Upscaler", "_upscaler_"), ("Frame Generation", "_framegeneration_"),
        ("Denoiser", "_denoiser_"), ("Radiance", "_radiancecache_"),
    ];

    /// <summary>Swapped in tests: the file's company, product, description and names from its version information.</summary>
    internal Func<string, string?> VersionText { get; init; } = ReadVersionText;

    /// <summary>Swapped in tests: the vendor's own file under this name (<see cref="IsVendorsOwnFile"/>).</summary>
    internal Func<string, string, bool> VendorsOwn { get; init; } = IsVendorsOwnFile;

    internal static string ModMessage(string mod) =>
        (mod == UnnamedMod ? "A mod" : mod) + " put this file here, so Exo leaves it alone.";

    /// <summary>The mod an upscaler file is, or null when it is the game's or the vendor's.</summary>
    /// <param name="knownRelease">The file's bytes are a vendor release Exo pins, so it is the vendor's own.</param>
    /// <param name="markers">Marker mods per folder, read once per scan.</param>
    private string? ModOf(string root, string full, string name, bool knownRelease, Dictionary<string, List<string>> markers)
    {
        var found = new List<string>();
        var claimed = false;
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var dir = Directory.GetParent(full); dir is not null && IsUnderRoot(top, dir.FullName); dir = dir.Parent)
        {
            if (FolderOwner(dir.Name) is not { } owner) continue;
            claimed = true;
            found.Add(owner);
            // The folder holding it says whose it is (HelixSR keeps HLX FSR 4 in an OptiScaler folder).
            if (dir.Parent is { } parent) found.AddRange(MarkersIn(parent.FullName, markers));
        }
        if (HasHelixTrailer(full))
        {
            claimed = true;
            found.Add("HelixSR");
        }
        var version = VersionText(full);
        if (ModNamedIn(version) is { } named)
        {
            claimed = true;
            found.Add(named);
        }
        else if (!RoleMatches(name, version))
        {
            // AMD's loader under FSR 3.1's name: no game ships that, OptiScaler 0.9 does.
            claimed = true;
            found.Add(UnnamedMod);
        }
        bool? vendors = knownRelease ? true : null;
        bool Vendors() => vendors ??= VendorsOwn(full, name);
        // FSR 4's network is AMD's own, shipped only signed (FSR 3.1 is open source, and games build it themselves):
        // an unsigned one is a community build (FSR 4 INT8) or HelixSR's.
        if (!claimed && name.Equals("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase) && !Vendors())
        {
            claimed = true;
            found.Add(UnnamedMod);
        }
        var beside = MarkersIn(Path.GetDirectoryName(full)!, markers);
        if (beside.Count > 0 && (claimed || !Vendors()))
        {
            claimed = true;
            found.AddRange(beside);
        }
        return claimed ? MostSpecific(found) : null;
    }

    private static string MostSpecific(IEnumerable<string> mods) =>
        mods.OrderBy(mod => Array.FindIndex(UpscalerMods, known => known.Name == mod) is var at and >= 0 ? at : UpscalerMods.Length)
            .FirstOrDefault() ?? UnnamedMod;

    private static string? FolderOwner(string folderName) =>
        UpscalerMods.FirstOrDefault(mod => mod.Folders.Contains(folderName, StringComparer.OrdinalIgnoreCase))?.Name;

    /// <summary>The mods whose marker files or folders are in this folder.</summary>
    private static List<string> MarkersIn(string folder, Dictionary<string, List<string>> markers)
    {
        if (markers.TryGetValue(folder, out var known)) return known;
        var found = new List<string>();
        try
        {
            // Exo's own frame generation (DlssSwapService.FrameGen.cs) marks only its OptiScaler folder: the game's files
            // beside its loader stay the game's.
            // A record of a mod Exo already took out (another mod's file is over its loader) marks nothing as Exo's.
            var exos = File.Exists(Path.Combine(folder, FrameGenManifestName)) && ReadFrameGenManifest(folder) is not { Out: true };
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                var mod = entry is DirectoryInfo
                    ? FolderOwner(entry.Name)
                    : UpscalerMods.FirstOrDefault(item => item.Files.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))?.Name;
                if (exos && mod is "OptiScaler" or "dlssg-to-fsr3" or "MFG Unlocker") continue;
                if (mod is not null && !found.Contains(mod)) found.Add(mod);
            }
            foreach (var mod in UpscalerMods.Where(item => item.Inside?.Any(path => Path.Exists(Path.Combine(folder, path))) == true))
                if (!found.Contains(mod.Name)) found.Add(mod.Name);
        }
        catch { /* an unreadable folder has no markers */ }
        markers[folder] = found;
        return found;
    }

    internal static string? ModNamedIn(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return null;
        // OptiScaler's builds keep its company: the build's own name goes first.
        if (ModVersionWords.Take(3).FirstOrDefault(word => versionText.Contains(word.Word, StringComparison.OrdinalIgnoreCase)).Mod is { } build) return build;
        if (versionText.Split('\n').Any(line => line.Trim().Equals("nitec", StringComparison.OrdinalIgnoreCase))) return "OptiScaler";
        return ModVersionWords.FirstOrDefault(word => versionText.Contains(word.Word, StringComparison.OrdinalIgnoreCase)).Mod;
    }

    /// <summary>Loaders that aren't upscaler mods, by marker files and folders (UE4SS's own files and folder).</summary>
    private static readonly UpscalerMod[] LoaderMods =
    [
        new("UE4SS", ["ue4ss.dll", "ue4ss-settings.ini", "ue4ss.log"], ["ue4ss"]),
    ];

    /// <summary>Words a loader's version information carries (Ultimate ASI Loader's proxy names only itself there).</summary>
    private static readonly (string Word, string Mod)[] LoaderVersionWords =
    [
        ("ultimate asi loader", "Ultimate ASI Loader"), ("ultimateasiloader", "Ultimate ASI Loader"), ("ue4ss", "UE4SS"),
    ];

    /// <summary>The names a proxy loader takes beside a game's program.</summary>
    private static readonly string[] ProxyNames =
    [
        "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "dinput8.dll", "dsound.dll",
        "d3d9.dll", "d3d10.dll", "d3d11.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll", "dwmapi.dll", "msacm32.dll", "msvfw32.dll",
    ];

    /// <summary>
    /// A loader beside the game's program that isn't an upscaler mod, by name, or null: UE4SS by its files or folder,
    /// Ultimate ASI Loader or UE4SS by a proxy's version information. Read only for frame generation.
    /// </summary>
    internal string? LoaderModIn(string folder)
    {
        if (LoaderMarkedIn(folder) is { } marked) return marked;
        foreach (var name in ProxyNames)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && LoaderNamedIn(VersionText(path)) is { } named) return named;
        }
        return null;
    }

    /// <summary>The names each loader mod's own proxy takes (UE4SS: dwmapi.dll by default, or the name a mod manager gives it).</summary>
    private static readonly Dictionary<string, string[]> LoaderProxyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UE4SS"] = ["dwmapi.dll", "version.dll", "xinput1_3.dll"],
    };

    /// <summary>Overlays that also load as a proxy, named only by their own version information: never markers of a mod in the game.</summary>
    private static readonly (string Word, string Mod)[] OverlayVersionWords =
    [
        ("reshade", "ReShade"), ("special k", "Special K"), ("specialk", "Special K"),
    ];

    /// <summary>
    /// The mod whose file is over Exo's loader, by that file alone: its version information names an upscaler mod, a
    /// loader or an overlay (ReShade, Special K). A file with no version information at all is named by a loader mod's
    /// own files beside it only under one of that mod's proxy names. Null when Exo can't tell.
    /// </summary>
    internal string? ReplacedBy(string folder, string file)
    {
        var text = VersionText(Path.Combine(folder, file));
        if (!string.IsNullOrWhiteSpace(text))
            return ModNamedIn(text) ?? LoaderNamedIn(text) ??
                   OverlayVersionWords.FirstOrDefault(word => text.Contains(word.Word, StringComparison.OrdinalIgnoreCase)).Mod;
        return LoaderMarkedIn(folder) is { } marked && LoaderProxyNames.TryGetValue(marked, out var names) &&
               names.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) ? marked : null;
    }

    /// <summary>A loader mod whose own files or folder are in this folder (UE4SS), or null.</summary>
    private static string? LoaderMarkedIn(string folder)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                var mod = LoaderMods.FirstOrDefault(item => entry is DirectoryInfo
                    ? item.Folders.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                    : item.Files.Contains(entry.Name, StringComparer.OrdinalIgnoreCase));
                if (mod is not null) return mod.Name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable folder has no loader Exo can name */ }
        return null;
    }

    /// <summary>The loader a proxy's version information names (Ultimate ASI Loader, UE4SS), or null.</summary>
    private static string? LoaderNamedIn(string? versionText) =>
        string.IsNullOrEmpty(versionText) ? null
            : LoaderVersionWords.FirstOrDefault(word => versionText.Contains(word.Word, StringComparison.OrdinalIgnoreCase)).Mod;

    private static string? ReadVersionText(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Join('\n', info.CompanyName, info.ProductName, info.FileDescription, info.InternalName, info.OriginalFilename);
        }
        catch { return null; }
    }

    /// <summary>HelixSR's DLL with its network built in ends in a 32-byte trailer that starts HXSRNET1.</summary>
    internal static bool HasHelixTrailer(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 32) return false;
            stream.Position = stream.Length - 32;
            Span<byte> head = stackalloc byte[8];
            return stream.Read(head) == 8 && head.SequenceEqual("HXSRNET1"u8);
        }
        catch { return false; }
    }

    /// <summary>
    /// The vendor's own file under this name: signed by the company that owns it (read offline: this decides what a
    /// file is, not whether to run it), with that company's identity and the role the name says.
    /// </summary>
    internal static bool IsVendorsOwnFile(string path, string fileName)
    {
        if (!VendorSigned(path, fileName, AuthenticodeVerifier.SignatureValid)) return false;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return VendorMetadataMatches(fileName, info.OriginalFilename, info.CompanyName) && RoleMatches(fileName, info.FileDescription);
        }
        catch { return false; }
    }

    /// <summary>
    /// False when AMD's description names another library than the file's name: OptiScaler 0.9 ships AMD's signed
    /// loader ("AMD FidelityFX (DirectX) Loader Library") as amd_fidelityfx_dx12.dll, and AMD leaves OriginalFilename empty.
    /// </summary>
    internal static bool RoleMatches(string fileName, string? description)
    {
        if (!fileName.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(description)) return true;
        var said = AmdRoles.FirstOrDefault(role => description.Contains(role.Word, StringComparison.OrdinalIgnoreCase));
        return said.Name is null || fileName.Contains(said.Name, StringComparison.OrdinalIgnoreCase);
    }
}
