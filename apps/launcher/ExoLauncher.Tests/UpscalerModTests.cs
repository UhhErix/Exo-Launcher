using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the upscaler swap could undo a player's upscaler mod (DlssSwapService.Mods.cs), or stop updating a game's own file:
/// - a mod's stand-in (HelixSR's FSR drop-in, OptiScaler's files) reads as outdated and Update all writes the vendor's
///   file over it;
/// - a single-file update of a mod's file goes through;
/// - Restore puts the game's saved original back over the mod's file;
/// - the status offers Update or Restore on a mod's file;
/// - a mod is named wrong: HelixSR Scaler or DLSS Enabler read as plain OptiScaler, or the unnamed marker wins;
/// - AMD's loader under FSR 3.1's name (OptiScaler 0.9), or an FSR 4 upscaler AMD didn't sign (FSR 4 INT8), passes
///   for AMD's;
/// - a game's own unsigned FSR build with no marker, a vendor's signed file beside a marker, or a file in another folder
///   than the marker is taken for the mod's and never updated again.
/// </summary>
public sealed class UpscalerModTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-mods-" + Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exo-mods-data-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorData = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public UpscalerModTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_data);
        // Restore and Apply store their scan in Exo's cache: keep it out of the real one.
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _data);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorData);
        foreach (var dir in new[] { _root, _data })
            try { DeletionGate.DeleteDirectory(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Old enough that every file reads as outdated; nothing in the fixtures is signed or carries version information.</summary>
    private static DlssSwapService Service(Func<string, string, bool>? vendorsOwn = null, Func<string, string?>? versionText = null) => new()
    {
        FileVersion = path => Path.GetFileName(path).StartsWith("nvngx", StringComparison.OrdinalIgnoreCase) ? "310.1.0.0" : "1.0.0.1",
        VendorsOwn = vendorsOwn ?? ((_, _) => false),
        VersionText = versionText ?? (_ => null),
    };

    private GameEntry Game => new() { Id = "mod-test", Title = "Mod Test", Store = StoreKind.Local, Installed = true, Path = _root };

    /// <summary>An x64 image header, then the given tail (HelixSR's trailer goes last).</summary>
    private string Dll(string relative, byte fill = 1, byte[]? tail = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[256];
        Array.Fill(bytes, fill, 70, 150);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(bytes, 64);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 68);
        File.WriteAllBytes(path, tail is null ? bytes : [.. bytes, .. tail]);
        return path;
    }

    private void Marker(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private DlssSwapService.DetectedDll Scanned(DlssSwapService service, string relative) =>
        service.ScanGame(Game).Files.Single(dll => dll.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));

    private static byte[] HelixTrailer() => [.. "HXSRNET1"u8.ToArray(), .. new byte[24]];

    // ---- detection, one fixture per mod

    [Fact]
    public void HelixSRsFsrDropInIsHelixSRs()
    {
        // 1.x-3.x drop-in: HelixSR under the game's FSR 3.1 name, the game's kept as *.original.dll, its log beside it.
        Dll(@"Game\amd_fidelityfx_dx12.dll");
        Dll(@"Game\amd_fidelityfx_dx12.original.dll");
        Marker(@"Game\HLX_FSR.log");

        Assert.Equal("HelixSR", Scanned(Service(), @"Game\amd_fidelityfx_dx12.dll").Mod);
    }

    [Fact]
    public void HelixSRsBuiltDllIsKnownByItsTrailerAlone()
    {
        Dll(@"Game\amd_fidelityfx_dx12.dll", tail: HelixTrailer());

        Assert.Equal("HelixSR", Scanned(Service(), @"Game\amd_fidelityfx_dx12.dll").Mod);
    }

    [Fact]
    public void HelixSRScalersFoldersAreHelixSRsEvenWithVendorFilesInThem()
    {
        // 3.x: OptiScaler (renamed dxgi.dll) with HLX FSR 4 in its OptiScaler folder, and HelixSR's own folder beside it.
        Marker(@"Game\dxgi.dll");
        Marker(@"Game\OptiScaler.ini");
        Marker(@"Game\HelixSR\helixsr.ini");
        Dll(@"Game\OptiScaler\amd_fidelityfx_upscaler_dx12.dll");
        Dll(@"Game\OptiScaler\libxess.dll");
        // 2.0: HelixSR's FidelityFX DLL and NVIDIA's frame generation it was built against.
        Dll(@"Game\HelixSR-fsrfg\helixsr-fg\nvngx_dlssg.dll");

        var service = Service(vendorsOwn: (_, _) => true);
        Assert.Equal("HelixSR", Scanned(service, @"Game\OptiScaler\amd_fidelityfx_upscaler_dx12.dll").Mod);
        Assert.Equal("HelixSR", Scanned(service, @"Game\OptiScaler\libxess.dll").Mod);
        Assert.Equal("HelixSR", Scanned(service, @"Game\HelixSR-fsrfg\helixsr-fg\nvngx_dlssg.dll").Mod);
    }

    [Fact]
    public void OptiScalersOwnBuildIsOptiScalersAndTheVendorsFileBesideItStaysUpdatable()
    {
        Marker(@"Game\OptiScaler.ini");
        Marker(@"Game\OptiScaler.log");
        Dll(@"Game\amd_fidelityfx_dx12.dll");
        Dll(@"Game\libxess.dll");

        var service = Service(vendorsOwn: (path, _) => Path.GetFileName(path) == "libxess.dll");
        Assert.Equal("OptiScaler", Scanned(service, @"Game\amd_fidelityfx_dx12.dll").Mod);
        Assert.Null(Scanned(service, @"Game\libxess.dll").Mod);
    }

    [Fact]
    public void AmdsLoaderUnderFsr31sNameIsAModsWithOrWithoutItsMarker()
    {
        // OptiScaler 0.9 copies AMD's signed loader in as amd_fidelityfx_dx12.dll; AMD leaves OriginalFilename empty.
        Dll(@"Game\amd_fidelityfx_dx12.dll");
        Func<string, string?> loader = _ => "Advanced Micro Devices, Inc.\nAMD FidelityFX\nAMD FidelityFX (DirectX) Loader Library\n\n";

        Assert.Equal(DlssSwapService.UnnamedMod, Scanned(Service(vendorsOwn: (_, _) => true, versionText: loader), @"Game\amd_fidelityfx_dx12.dll").Mod);
        Marker(@"Game\OptiScaler.ini");
        Assert.Equal("OptiScaler", Scanned(Service(vendorsOwn: (_, _) => true, versionText: loader), @"Game\amd_fidelityfx_dx12.dll").Mod);
    }

    [Fact]
    public void DlssEnablerIsNamedOverTheOptiScalerSettingsItCarries()
    {
        Marker(@"Game\version.dll");
        Marker(@"Game\dlss-enabler-upscaler.dll");
        Marker(@"Game\nvngx.ini");
        Dll(@"Game\amd_fidelityfx_dx12.dll");

        Assert.Equal("DLSS Enabler", Scanned(Service(), @"Game\amd_fidelityfx_dx12.dll").Mod);
    }

    [Fact]
    public void DlssgToFsr3sStandInIsNukems()
    {
        Marker(@"Game\dlssg_to_fsr3_amd_is_better.dll");
        Dll(@"Game\nvngx_dlssg.dll");

        Assert.Equal("dlssg-to-fsr3", Scanned(Service(), @"Game\nvngx_dlssg.dll").Mod);
    }

    [Fact]
    public void UniscalersFilesAreUniscalers()
    {
        Marker(@"Game\winmm.dll");
        Marker(@"Game\Uniscaler.asi");
        Marker(@"Game\uniscaler.config.toml");
        Dll(@"Game\libxess.dll");

        Assert.Equal("Uniscaler", Scanned(Service(), @"Game\libxess.dll").Mod);
    }

    [Fact]
    public void Fsr2DlssStandInIsFsr2Dlss()
    {
        Marker(@"Game\fsr2dlss.ini");
        Dll(@"Game\amd_fidelityfx_dx12.amd.dll");
        Dll(@"Game\amd_fidelityfx_dx12.dll");

        Assert.Equal("fsr2dlss", Scanned(Service(), @"Game\amd_fidelityfx_dx12.dll").Mod);
    }

    [Fact]
    public void PureDarksPluginFolderIsHis()
    {
        Dll(@"Game\mods\UpscalerBasePlugin\nvngx_dlss.dll");

        Assert.Equal("PureDark's upscaler", Scanned(Service(vendorsOwn: (_, _) => true), @"Game\mods\UpscalerBasePlugin\nvngx_dlss.dll").Mod);
    }

    [Fact]
    public void AModsBuildNamedInItsVersionInformationIsThatMods()
    {
        Dll(@"Game\nvngx_dlss.dll");

        Assert.Equal("OptiScaler", Scanned(Service(versionText: _ => "nitec\nOptiScaler\nOptiScaler\nOptiScaler\nOptiScaler.dll"), @"Game\nvngx_dlss.dll").Mod);
    }

    [Fact]
    public void AnFsr4UpscalerAmdDidntSignIsAModsEvenAlone()
    {
        // FSR 4 is AMD's closed network, shipped signed; the FSR 4 INT8 build and HLX FSR 4 are not.
        Dll(@"Game\amd_fidelityfx_upscaler_dx12.dll");

        Assert.Equal(DlssSwapService.UnnamedMod, Scanned(Service(), @"Game\amd_fidelityfx_upscaler_dx12.dll").Mod);
        Assert.Null(Scanned(Service(vendorsOwn: (_, _) => true), @"Game\amd_fidelityfx_upscaler_dx12.dll").Mod);
    }

    // ---- the game's own files stay the game's

    [Fact]
    public void AGamesOwnUnsignedFsrBuildWithNoMarkerStillUpdates()
    {
        Dll(@"Game\amd_fidelityfx_dx12.dll");
        var service = Service();
        var dll = Scanned(service, @"Game\amd_fidelityfx_dx12.dll");

        Assert.Null(dll.Mod);
        var row = DlssSwapService.BuildRow(dll, blocked: false);
        Assert.Equal("outdated", row.State);
        Assert.True(row.CanUpdate);
        Assert.Single(DlssSwapService.Plan(service.ScanGame(Game, TestContext.Current.CancellationToken).Files, null, null, out _));
    }

    [Fact]
    public void AMarkerInAnotherFolderClaimsNothingThere()
    {
        // OptiScaler beside the Unreal game's exe; the engine's own FSR build elsewhere is the game's.
        Marker(@"Game\Binaries\Win64\OptiScaler.ini");
        Dll(@"Game\Binaries\Win64\amd_fidelityfx_dx12.dll");
        Dll(@"Engine\Plugins\FSR\Binaries\amd_fidelityfx_dx12.dll", fill: 2);
        var service = Service();

        Assert.Null(Scanned(service, @"Engine\Plugins\FSR\Binaries\amd_fidelityfx_dx12.dll").Mod);
        Assert.Equal("OptiScaler", Scanned(service, @"Game\Binaries\Win64\amd_fidelityfx_dx12.dll").Mod);
        var plan = DlssSwapService.Plan(service.ScanGame(Game, TestContext.Current.CancellationToken).Files, null, null, out _);
        Assert.Equal(@"Engine\Plugins\FSR\Binaries\amd_fidelityfx_dx12.dll", Assert.Single(plan).Dll.RelativePath);
    }

    [Theory]
    [InlineData("amd_fidelityfx_dx12.dll", "DX12 AMD FidelityFX Library", true)]
    [InlineData("amd_fidelityfx_dx12.dll", "AMD FidelityFX (DirectX) Loader Library", false)]
    [InlineData("amd_fidelityfx_loader_dx12.dll", "AMD FidelityFX (DirectX) Loader Library", true)]
    [InlineData("amd_fidelityfx_upscaler_dx12.dll", "AMD FidelityFX (DirectX) Upscaler Library", true)]
    [InlineData("amd_fidelityfx_upscaler_dx12.dll", "AMD FidelityFX (DirectX) Loader Library", false)]
    [InlineData("amd_fidelityfx_framegeneration_dx12.dll", "AMD FidelityFX (DirectX) Frame Generation Library", true)]
    [InlineData("libxess.dll", "XeSS SDK", true)]
    [InlineData("amd_fidelityfx_dx12.dll", null, true)]
    public void AnAmdLibraryMustBeTheOneItsNameSays(string fileName, string? description, bool matches) =>
        Assert.Equal(matches, DlssSwapService.RoleMatches(fileName, description));

    // ---- the plan, the status and the refusal

    [Fact]
    public void UpdateAllPassesAModsFileByAndOneFileAloneIsRefusedByName()
    {
        Dll(@"Game\amd_fidelityfx_dx12.dll", tail: HelixTrailer());
        Dll(@"Game\nvngx_dlss.dll", fill: 3);
        var service = Service();
        var files = service.ScanGame(Game, TestContext.Current.CancellationToken).Files;

        var all = DlssSwapService.Plan(files, null, null, out var allError);
        Assert.Null(allError);
        Assert.Equal("nvngx_dlss.dll", Assert.Single(all).Dll.FileName);

        Assert.Empty(DlssSwapService.Plan(files, @"Game\amd_fidelityfx_dx12.dll", "latest", out var error));
        Assert.Equal("HelixSR put this file here, so Exo leaves it alone.", error);

        var row = DlssSwapService.BuildRow(files.Single(dll => dll.Mod is not null), blocked: false);
        Assert.Equal("mod", row.State);
        Assert.False(row.CanUpdate);
        Assert.False(row.CanRestore);
    }

    [Fact]
    public void AnUnnamedModIsCalledAMod() =>
        Assert.Equal("A mod put this file here, so Exo leaves it alone.", DlssSwapService.ModMessage(DlssSwapService.UnnamedMod));

    [Fact]
    public async Task ApplyRefusesAModsFileWithoutDownloadingAnything()
    {
        Dll(@"Game\amd_fidelityfx_dx12.dll", tail: HelixTrailer());
        var service = Service();

        var one = await service.ApplyAsync(Game, @"Game\amd_fidelityfx_dx12.dll", "latest", () => null, CancellationToken.None);
        Assert.False(one.Ok);
        Assert.Equal("HelixSR put this file here, so Exo leaves it alone.", one.Message);

        // Update all with only a mod's file: nothing Exo can update, not "Already the newest".
        var all = await service.ApplyAsync(Game, null, null, () => null, CancellationToken.None);
        Assert.False(all.Ok);
        Assert.Equal("This game has no upscaler files Exo can update.", all.Message);
        Assert.Equal("HXSRNET1"u8.ToArray(), File.ReadAllBytes(Path.Combine(_root, @"Game\amd_fidelityfx_dx12.dll"))[^32..^24]);
    }

    [Fact]
    public void RestoreNeverPutsTheGamesOriginalOverAModsFile()
    {
        // Exo saved the game's FSR before an update; HelixSR's drop-in has since replaced the live file.
        var live = Dll(@"Game\amd_fidelityfx_dx12.dll", fill: 9, tail: HelixTrailer());
        var helix = File.ReadAllBytes(live);
        Dll(@"Game\amd_fidelityfx_dx12.dll" + DlssSwapService.SwapperBackupSuffix, fill: 4);
        Dll(@"Game\amd_fidelityfx_dx12.original.dll", fill: 5);
        // A game file elsewhere with Exo's saved original still comes back.
        var other = Dll(@"Other\libxess.dll", fill: 6);
        var original = File.ReadAllBytes(Dll(@"Other\libxess.dll" + DlssSwapService.SwapperBackupSuffix, fill: 7));
        var service = Service();

        var one = service.RestoreGame(Game, @"Game\amd_fidelityfx_dx12.dll");
        Assert.False(one.Ok);
        Assert.Equal("HelixSR put this file here, so Exo leaves it alone.", one.Message);
        Assert.Equal(helix, File.ReadAllBytes(live));

        var all = service.RestoreGame(Game);
        Assert.Equal(helix, File.ReadAllBytes(live));
        Assert.Equal(original, File.ReadAllBytes(other));
        Assert.Contains(all.Files!, file => file.FileName == "amd_fidelityfx_dx12.dll" && file.State == "skipped");
        Assert.Contains(all.Files!, file => file.FileName == "libxess.dll" && file.State == "restored");
    }
}
