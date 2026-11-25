using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways frame generation through a mod (DlssSwapService.FrameGen.cs) could go wrong:
/// - the wrong package for the card: MFG Unlocker or a 2x/3x/4x choice where AMD FidelityFX (OptiScaler) goes in
///   (an RTX 20 or 30, including Phantom Blade Zero), a mod on an RTX 40 or 50 (they have NVIDIA's own), or a mod
///   in a game with anti-cheat;
/// - it goes into a game with anti-cheat of any kind, or stays in one a patch changed;
/// - a release that isn't the project's newest, has no SHA-256 digest, is a test build, older than Exo takes, known not
///   to work, or whose files aren't the mod's (a loader that isn't its DLL, an AMD file not signed by AMD);
/// - it lands beside the wrong program or over a file the game has;
/// - Off leaves anything behind (the mod's logs and status), deletes a file Exo can't prove is the mod's, or a tampered
///   record makes it delete the game's files;
/// - an update to the newest release leaves the game half moved, or one that failed (the game closed at once, or no
///   frames came) stays, or comes back;
/// - the upscaler swap updates the mod's files, or takes the game's own files beside the mod for the mod's;
/// - it stays in a game that later shows anti-cheat, or on a card that no longer needs it;
/// - the driver's frame cap isn't moved into OptiScaler and put back after, or is touched for NVIDIA's own pipeline;
/// - another mod over Exo's loader reads as a game update, is written over, or makes Off fail; files that went are put
///   back again and again; a game that stopped shipping DLSS Frame Generation keeps the mod;
/// - an RTX 20 or 30 is left with nothing while DLSSG SM86 isn't offered, flips between DLSSG SM86 and OptiScaler, or
///   loses an earlier DLSSG SM86 install for that alone; a fallback or a new card class reaches a game with anti-cheat;
/// - a mod beside the game (AMDNR, dlss-unlocked, the FSR 4 shader overrides, UE4SS, Ultimate ASI Loader) goes unnamed,
///   or a new marker takes the game's own files.
/// </summary>
public sealed partial class FrameGenModTests : IDisposable
{
    private const string Rtx3070 = "NVIDIA GeForce RTX 3070";
    private const string Radeon = "AMD Radeon RX 6600";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-framegen-" + Guid.NewGuid().ToString("N"));
    private readonly string _source = Path.Combine(Path.GetTempPath(), "exo-framegen-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exo-framegen-data-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorData = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly GameEntry _game;
    /// <summary>Each mod's newest release, as Exo's cache lists it now.</summary>
    private readonly Dictionary<string, FrameGenRelease> _latest = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The loaders the fixture releases carry, by their bytes, and the mod DLL each is.</summary>
    private readonly Dictionary<string, string> _loaders = new(StringComparer.OrdinalIgnoreCase);
    private string _dlssg = "310.9.16.0";
    private string _streamline = "2.14.1.0";

    public FrameGenModTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_data);
        // The upscaler scan keeps its cache in Exo's data folder: keep it out of the real one.
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _data);
        _game = new() { Id = "framegen-" + Guid.NewGuid().ToString("N"), Title = "Frame Gen Test", Store = StoreKind.Local, Installed = true, Path = _root };
        Release(MfgUnlocker, "v1.7.0");
        Release(OptiScaler, "v0.9.4");
        Release(DlssgSm86, "0.4.1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorData);
        foreach (var dir in new[] { _root, _source, _data })
            try { DeletionGate.DeleteDirectory(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A release of the mod in its real layout (each file its own bytes), now its newest.</summary>
    private string Release(FrameGenMod mod, string tag)
    {
        var folder = Path.Combine(_source, mod.Repo, tag);
        foreach (var spec in mod.Files)
        {
            var path = Path.Combine(folder, spec.Source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Pe(400);
            System.Text.Encoding.ASCII.GetBytes(mod.Repo + tag + spec.Source).CopyTo(bytes, 80);
            File.WriteAllBytes(path, bytes);
            if (spec.Target.Length == 0) _loaders[Sha(path)] = mod.Dll;
        }
        if (mod.Payload is { } payload)
        {
            var kernel = Path.Combine(folder, payload, "kernels", "fg_kernels.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(kernel)!);
            File.WriteAllBytes(kernel, Pe(400));
            File.WriteAllText(Path.Combine(folder, mod.Settings), "[General]\nEnabled=1\n[FrameGeneration]\nEngine=DLSS\nMaxGeneratedFrames=3\n[DLSSG]\nPreset=A\n");
        }
        _latest[mod.Repo] = new(tag, mod.Repo + "-" + tag + ".zip", 1000, new string('a', 64));
        return folder;
    }

    /// <param name="dlssgOffered">The DLSSG SM86 switch (its pin): on in these fixtures unless a test turns it off, as it ships today.</param>
    /// <param name="running">The programs running from the game's folder (none unless a test says so).</param>
    private DlssSwapService Service(FakeDriver? driver = null, bool dlssgOffered = true, Func<string, string?>? versionText = null,
        Func<string, IReadOnlyList<string>>? running = null, Func<string, bool, CancellationToken, Task<string?>>? recorder = null,
        Action<GameEntry>? warn = null, Func<bool>? antiCheatRunning = null) => new()
    {
        ProgramsRunningIn = running ?? (_ => []),
        SessionRecorder = recorder ?? ((_, _, _) => Task.FromResult<string?>(null)),
        WarnFilesInUse = warn ?? (_ => { }),
        AntiCheatRunning = antiCheatRunning ?? (() => false),
        FileVersion = path => Path.GetFileName(path).ToLowerInvariant() switch
        {
            "sl.interposer.dll" => _streamline,
            "nvngx_dlssg.dll" => _dlssg,
            _ => "310.1.0.0",
        },
        VendorsOwn = (_, _) => true,
        VersionText = versionText ?? (_ => null),
        OriginalName = path => _loaders.GetValueOrDefault(Sha(path)),
        FrameGenLatest = (mod, _) => Task.FromResult(_latest[mod.Repo]),
        // DLSSG SM86 held back, as a card class Exo stops offering would be.
        UnofferedReason = mod => dlssgOffered || mod != DlssgSm86 ? null : "Not available yet",
        FrameGenSource = (mod, release, _) =>
        {
            var folder = Path.Combine(_source, mod.Repo, release.Tag);
            return Directory.Exists(folder) ? Task.FromResult(folder) : throw new InvalidDataException($"Exo no longer has {mod.Name} {release.Tag}.");
        },
        FrameGenDriver = driver,
        FrameGenIndexPath = Path.Combine(_data, "frame-gen", "games.json"),
    };

    // ---- fixtures

    /// <summary>An x64 image header padded to <paramref name="size"/> bytes.</summary>
    private static byte[] Pe(int size, byte fill = 1)
    {
        var bytes = new byte[Math.Max(size, 256)];
        Array.Fill(bytes, fill, 70, bytes.Length - 70);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(bytes, 64);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 68);
        return bytes;
    }

    /// <summary>
    /// A PE32+ image whose import table names <paramref name="dlls"/>: one section at RVA 0x1000 (file 0x200), the
    /// descriptors there, the names from file 0x300.
    /// </summary>
    internal static byte[] PeImporting(params string[] dlls)
    {
        var bytes = new byte[0x400];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(bytes, 0x40);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x44);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 0x46);
        BitConverter.GetBytes((ushort)0xF0).CopyTo(bytes, 0x54);
        BitConverter.GetBytes((ushort)0x20B).CopyTo(bytes, 0x58);
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, 0xC8 + 8); // import table
        BitConverter.GetBytes((uint)(20 * (dlls.Length + 1))).CopyTo(bytes, 0xC8 + 12);
        var section = 0x58 + 0xF0;
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, section + 8);
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, section + 12);
        BitConverter.GetBytes(0x200u).CopyTo(bytes, section + 16);
        BitConverter.GetBytes(0x200u).CopyTo(bytes, section + 20);
        var name = 0x300;
        for (var i = 0; i < dlls.Length; i++)
        {
            BitConverter.GetBytes((uint)(0x1000 + name - 0x200)).CopyTo(bytes, 0x200 + i * 20 + 12);
            System.Text.Encoding.ASCII.GetBytes(dlls[i] + "\0").CopyTo(bytes, name);
            name += dlls[i].Length + 1;
        }
        return bytes;
    }

    private string Put(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void Marker(string relative, string text = "") => Put(relative, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>An Unreal game: its small launcher at the top, the game in Project\Binaries\Win64, DLSS frame generation and Streamline in the engine's plugins.</summary>
    private const string UnrealProgram = @"Game\Binaries\Win64";

    private void UnrealGame()
    {
        Put("Game.exe", Pe(300));
        Put(@"Game\Binaries\Win64\Game-Win64-Shipping.exe", Pe(4000));
        Put(@"Engine\Binaries\Win64\CrashReportClient.exe", Pe(9000));
        Put(@"Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64\nvngx_dlssg.dll", Pe(500));
        Put(@"Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64\sl.interposer.dll", Pe(500));
        Put(@"Game\Content\Paks\Game.pak", [1, 2, 3]);
    }

    /// <summary>
    /// Phantom Blade Zero's install as Steam has it (build 26266538, read on this PC): the Steam stub, the game under
    /// platform\steam with Direct3D 12's Agility folder beside it, DLSS frame generation 310.9.16 and Streamline 2.14.1
    /// in the engine's StreamlineCore plugin, the FidelityFX SDK's frame generation in the FSR plugin's signedbin (here
    /// already updated by Exo's upscaler swap), and XeSS's.
    /// </summary>
    private void PhantomBladeZero()
    {
        Put(@"steam\PBZ.exe", Pe(1000));
        Put(@"platform\steam\PBZ.exe", Pe(50000));
        Put(@"platform\steam\steam_api64.dll", Pe(300));
        Put(@"platform\steam\D3D12\D3D12Core.dll", Pe(300));
        Put(@"ProjectPBZ\Binaries\Win64\crashpad_handler.exe", Pe(3000));
        Put(@"Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64\nvngx_dlssg.dll", Pe(500));
        Put(@"Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64\sl.interposer.dll", Pe(500));
        var fsr = Put(@"ProjectPBZ\Plugins\FSR\Source\fidelityfx-sdk\Kits\FidelityFX\signedbin\amd_fidelityfx_framegeneration_dx12.dll", Pe(500));
        File.Copy(fsr, fsr + ".dlsss");
        File.WriteAllText(fsr + ".exo-written", Sha(fsr));
        Put(@"ProjectPBZ\Plugins\XeSS\Binaries\ThirdParty\Win64\libxess_fg.dll", Pe(500));
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Every file and folder under the install, with each file's bytes.</summary>
    private SortedDictionary<string, string> Snapshot()
    {
        var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)) all[Path.GetRelativePath(_root, dir) + "\\"] = "dir";
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) all[Path.GetRelativePath(_root, file)] = Sha(file);
        return all;
    }

    private async Task<(bool Ok, string Message)> On(DlssSwapService service, string gpu = Rtx3070) =>
        await service.FrameGenOnAsync(_game, gpu, TestContext.Current.CancellationToken);

    private FrameGenStatus State(DlssSwapService service, string gpu = Rtx3070) =>
        service.FrameGenState(_game, gpu, TestContext.Current.CancellationToken);

    private FrameGenNext Next(DlssSwapService service, string gpu = Rtx3070) =>
        service.FrameGenRead(_game, gpu, TestContext.Current.CancellationToken).Next;

    private string Text(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    private string LoaderBytes(FrameGenMod mod, string tag) => Sha(Path.Combine(_source, mod.Repo, tag, mod.Dll));

    internal sealed class FakeDriver(uint? global, uint? gameOwn = null) : IFrameGenDriver
    {
        public uint? Global { get; set; } = global;
        public uint? Game { get; private set; } = gameOwn;
        public List<uint?> Sets { get; } = [];
        public uint? GlobalCap() => Global;
        public uint? GameCap(GameEntry game, string exe) => Game;

        public bool SetGameCap(GameEntry game, string exe, uint? fps, out uint? before, out string? profile)
        {
            before = Game;
            profile = "Exo: " + game.Title;
            Game = fps;
            Sets.Add(fps);
            return true;
        }
    }

    // ---- the card and the path

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", "nvidia", 30)]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", "nvidia", 20)]
    [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU", "nvidia", 30)]
    [InlineData("NVIDIA RTX A4000", "nvidia", 30)]
    [InlineData("NVIDIA GeForce RTX 4090", "nvidia", 40)]
    [InlineData("NVIDIA RTX 2000 Ada Generation", "nvidia", 40)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", "nvidia", 50)]
    [InlineData("AMD Radeon RX 6600", "amd", 0)]
    [InlineData("AMD Radeon RX 5700 XT", "amd", 0)]
    [InlineData("AMD Radeon 780M Graphics", "amd", 0)]
    [InlineData("AMD Radeon 8060S Graphics", "amd", 0)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "intel", 0)]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", "intel", 0)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", "nvidia", 16)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", "nvidia", 10)]
    [InlineData("NVIDIA GeForce GTX 970", "nvidia", 9)]
    [InlineData("AMD Radeon RX 580 Series", "amd", 0)]
    [InlineData("AMD Radeon RX Vega 64", "amd", 0)]
    public void CardsAModAddsToAreKnown(string gpu, string vendor, int series)
    {
        var card = FrameGenCardFor(gpu);
        Assert.Equal((vendor, series), (card?.Vendor, card?.Series));
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 750 Ti")]
    [InlineData("NVIDIA GeForce MX450")]
    [InlineData("AMD Radeon R9 390 Series")]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics")]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("")]
    [InlineData(null)]
    public void CardsNoModAddsToGetNothing(string? gpu) => Assert.Null(FrameGenCardFor(gpu));

    private static FrameGenGame Game(bool dlssg = true, int? major = 310, bool own = false, int? streamline = 2, bool vulkan = false, bool noDlssg = false, string? antiCheat = null) =>
        new(@"C:\Game", @"C:\Game\Game.exe", dlssg, major, own, streamline, vulkan, null, antiCheat, null, noDlssg);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("{\"dlssg_create_calls\": 2}", "ran")]
    [InlineData("the proxy loaded and waited", "loaded")]
    public void DlssLogSaysWhetherToKeepOrMoveOn(string? log, string? state) =>
        Assert.Equal(state, DlssLogState(log));

    [Fact]
    public void TheProxyStaysVersionDllBecauseItExportsNothingElse() => Assert.Equal(["version.dll"], DlssgSm86.Loaders);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProxyAnEarlierExoRenamedGoesBack(bool bothThere)
    {
        var folder = Path.Combine(_root, "renamed");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "winmm.dll"), "proxy");
        if (bothThere) File.WriteAllText(Path.Combine(folder, "version.dll"), "proxy");
        var install = new FrameGenInstall(DlssgSm86.Name, "0.4.1", "winmm.dll", "dlss", "Game.exe", [new("winmm.dll", 5, null, default), new("dlssg_sm86.ini", 1, null, default)]);

        var repaired = DlssLoaderRepaired(folder, install);

        Assert.False(File.Exists(Path.Combine(folder, "winmm.dll")));
        Assert.Equal("proxy", File.ReadAllText(Path.Combine(folder, "version.dll")));
        Assert.Equal("version.dll", repaired.Loader);
        Assert.Equal(["version.dll", "dlssg_sm86.ini"], repaired.Files.Select(file => file.Path));
    }

    [Fact]
    public void WhatExosFrameGenPutInListsOnItsLineNotAsAnUpscaler()
    {
        DetectedDll Dll(string name, string? mod) => new(name, name, name, "DLSS", "310.9.16.0", null, "game", null, null, null, mod);
        var files = new[] { Dll("nvngx_dlss.dll", null), Dll("sm86\nvngx_dlss.dll", DlssgSm86.Name), Dll("nvngx.dll", "CyberFSR") };
        Assert.Equal(["nvngx_dlss.dll", "nvngx.dll"], WithoutFrameGenMod(files, DlssgSm86.Name).Select(dll => dll.Path));
        Assert.Equal(3, WithoutFrameGenMod(files, null).Count());
    }

    [Fact]
    public void DlssFrameGenStaysInWhateverAScanOfTheGameReads()
    {
        var install = new FrameGenInstall(DlssgSm86.Name, "0.4.1", "version.dll", "dlss", "Game.exe", []);
        var rtx30 = FrameGenCardFor("NVIDIA GeForce RTX 3070");
        foreach (var wanted in new[] { "dlss", "native", "fsr", null })
            Assert.Null(FrameGenTakeOut(rtx30, DlssgSm86, null, _root, install, wanted));
        Assert.NotNull(FrameGenTakeOut(FrameGenCardFor("AMD Radeon RX 6800"), DlssgSm86, null, _root, install, "fsr"));
        Assert.NotNull(FrameGenTakeOut(FrameGenCardFor("NVIDIA GeForce RTX 4070"), DlssgSm86, null, _root, install, null));
    }

    [Fact]
    public void TheRealPathIsAmdFidelityFxOnRtx20And30AndNothingOnRtx40()
    {
        var rtx30 = FrameGenCardFor(Rtx3070);
        Assert.Equal("dlss", FrameGenPath(rtx30, Game()));
        // RTX 20 and 30 still get DLSS where the game also has FSR or XeSS (Phantom Blade Zero): it starts on DLSS's.
        Assert.Equal("dlss", FrameGenPath(rtx30, Game(own: true)));
        Assert.Equal("dlss", FrameGenPath(FrameGenCardFor("NVIDIA GeForce RTX 2070 SUPER"), Game()));
        Assert.Null(FrameGenPath(FrameGenCardFor("NVIDIA GeForce RTX 4070"), Game()));
        // Vulkan, Streamline 1 or none, or a runtime before 310: still FSR, unless the game has frame generation of its own.
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(vulkan: true)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(streamline: 1)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(streamline: null)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(major: 3)));
        // 310 or newer: a newer runtime major stays on DLSS (it moved RTX 20 and 30 to FSR when the rule was 310 exactly).
        Assert.Equal("dlss", FrameGenPath(rtx30, Game(major: 311)));
        Assert.Equal("dlss", FrameGenPath(rtx30, Game(major: 400)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(major: 309)));
        // DLSSG SM86 can't be this game's (not offered, failed here, or its loader's name taken): OptiScaler, as on other cards.
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(noDlssg: true)));
        Assert.Equal("native", FrameGenPath(rtx30, Game(own: true, noDlssg: true)));
        Assert.Equal("native", FrameGenPath(rtx30, Game(vulkan: true, own: true)));
        // Old cards a mod now helps: OptiScaler, never DLSSG SM86.
        foreach (var old in new[] { "NVIDIA GeForce GTX 1660 SUPER", "NVIDIA GeForce GTX 1060 6GB", "NVIDIA GeForce GTX 970", "AMD Radeon RX 580", "AMD Radeon RX Vega 56", "AMD Radeon 780M Graphics", "Intel(R) Arc(TM) Graphics" })
        {
            Assert.Equal("fsr", FrameGenPath(FrameGenCardFor(old), Game()));
            Assert.Equal("native", FrameGenPath(FrameGenCardFor(old), Game(own: true)));
        }
        Assert.Null(FrameGenPath(FrameGenCardFor("NVIDIA GeForce RTX 4070"), Game(vulkan: true)));
        Assert.Null(FrameGenPath(FrameGenCardFor("NVIDIA GeForce RTX 5090"), Game()));
        Assert.Equal("fsr", FrameGenPath(FrameGenCardFor("AMD Radeon RX 6600"), Game()));
        Assert.Equal("native", FrameGenPath(FrameGenCardFor("AMD Radeon RX 6600"), Game(own: true)));
        Assert.Null(FrameGenPath(rtx30, Game(dlssg: false)));
        Assert.Null(FrameGenPath(null, Game()));
    }

    /// <summary>
    /// An RTX 40 or 50 has no mod route, so its line reads the game's own frame generation from the upscaler scan: a
    /// DLSS frame generation game must not read as unsupported, and a mod's copy of the file is not the game's.
    /// </summary>
    [Fact]
    public void AGameShipsFrameGenerationOnlyWithItsOwnFile()
    {
        static DetectedDll Dll(string name, string? mod = null) =>
            new($@"C:\Game\{name}", name, name, "dlss", "310.2.1", null, "game", null, null, null, mod);
        Assert.True(ShipsFrameGen([Dll("nvngx_dlss.dll"), Dll("nvngx_dlssg.dll")]));
        Assert.True(ShipsFrameGen([Dll("amd_fidelityfx_framegeneration_dx12.dll")]));
        Assert.True(ShipsFrameGen([Dll("libxess_fg.dll")]));
        Assert.False(ShipsFrameGen([Dll("nvngx_dlss.dll")]));
        Assert.False(ShipsFrameGen([Dll("nvngx_dlssg.dll", mod: "OptiScaler")]));
        Assert.False(ShipsFrameGen([]));
    }

    // ---- the pinned release: the cache serves bytes only, so which release is newest is pinned in code

    private static FrameGenRelease Pinned(string tag = "v1.7.0", string asset = "MFGUnlocker-v1.7.0.zip", long size = 4842619, string? sha = null) =>
        new(tag, asset, size, sha ?? new string('5', 64));

    [Fact]
    public void TheNewestPinnedReleaseOfAModIsTaken()
    {
        var pins = new[]
        {
            (MfgUnlocker.Repo, Pinned("v1.5.0-hotfix.1", "MFGUnlocker-v1.5.0-hotfix.1.zip")),
            (MfgUnlocker.Repo, Pinned()),
            (OptiScaler.Repo, Pinned("v0.9.4", "Optiscaler_0.9.4-final.20260718._MM.7z", 55016448)),
        };

        Assert.Equal(Pinned(), NewestPinnedRelease(MfgUnlocker, pins));
    }

    /// <summary>Each mod's shipped pin is the one selected for it, inside the mod's own version floor and size limit. Ways it
    /// could fail: a pin filed under the wrong repo, a tag the floor refuses, a size over the mod's limit, or a digest that
    /// isn't a SHA-256.</summary>
    [Theory]
    [InlineData("MFG-Unlocker", "v1.7.2", "MFGUnlocker-v1.7.2.zip", 5334929L, "e002d624e3e7ba5cedab7211043c096fe8f555c30ff43500235a0686c7893f22")]
    [InlineData("OptiScaler", "v0.9.5", "Optiscaler_0.9.5-final.20261116.7z", 70418897L, "77e3ed76bed04e1fb32bacb5a72799ff172f3da22ed5231c0cf3e84fe6761662")]
    public void EachShippedPinIsSelectedForItsMod(string repo, string tag, string asset, long size, string sha)
    {
        var mod = new[] { MfgUnlocker, OptiScaler, DlssgSm86 }.Single(candidate => candidate.Repo == repo);
        var selected = NewestPinnedRelease(mod, PinnedFrameGenReleases);
        Assert.Equal(new FrameGenRelease(tag, asset, size, sha), selected);
        Assert.True(selected.Size <= mod.MaxSize);
        Assert.True(TagVersion(selected.Tag) >= mod.Minimum);
        Assert.Matches("^[0-9a-f]{64}$", selected.Sha256);
    }

    /// <summary>Every mod is offered: its newest release comes from GitHub with the digest GitHub publishes, a pin is only
    /// the cache's fallback. Ways the pin fallback could fail: a pin added later isn't the newest, a pin of another mod
    /// offers it, or a mod with a pin is refused.</summary>
    [Fact]
    public void EveryModIsOfferedAndAPinIsTheFallback()
    {
        Assert.Null(Unoffered(MfgUnlocker));
        Assert.Null(Unoffered(OptiScaler));
        Assert.Null(Unoffered(DlssgSm86));
        var on = PinnedFrameGenReleases.Append((DlssgSm86.Repo, new FrameGenRelease("0.4.1", "dlssg-release-0.4.1-x64.zip", 1000, new string('a', 64)))).ToList();
        Assert.Null(Unoffered(DlssgSm86, on));
        Assert.Equal("0.4.1", NewestPinnedRelease(DlssgSm86, on).Tag);
        Assert.NotNull(Unoffered(DlssgSm86, PinnedFrameGenReleases.Append(("dlssg_for_sm86-fork", on[^1].Item2))));
    }

    [Fact]
    public void WithNoPinnedReleaseExoTakesNoModAtAll()
    {
        // Fail closed: the cache's word alone never names a release.
        Assert.Throws<InvalidDataException>(() => NewestPinnedRelease(MfgUnlocker, []));
    }

    [Theory]
    [InlineData("too old")]
    [InlineData("too large")]
    [InlineData("bad sha")]
    public void APinnedReleaseExoShouldntTakeIsRefused(string why)
    {
        var release = why switch
        {
            "too old" => Pinned("v1.4.2", "MFGUnlocker-v1.4.2.zip"),
            "too large" => Pinned(size: 1L << 40),
            _ => Pinned(sha: "not-a-digest"),
        };

        Assert.Throws<InvalidDataException>(() => NewestPinnedRelease(MfgUnlocker, [(MfgUnlocker.Repo, release)]));
    }

    [Fact]
    public void AReleaseKnownNotToWorkIsRefused()
    {
        RefusedFrameGenReleases.Add("MFG-Unlocker@v9.8.7");
        try { Assert.Throws<InvalidDataException>(() => NewestPinnedRelease(MfgUnlocker, [(MfgUnlocker.Repo, Pinned("v9.8.7", "MFGUnlocker-v9.8.7.zip"))])); }
        finally { RefusedFrameGenReleases.Remove("MFG-Unlocker@v9.8.7"); }
    }

    [Fact]
    public void AReleasesFilesMustBeTheMods()
    {
        var folder = Path.Combine(_source, MfgUnlocker.Repo, "v1.7.0");
        string? Original(string path) => _loaders.GetValueOrDefault(Sha(path));
        Assert.Null(CheckFrameGenFolder(MfgUnlocker, folder, Original, (_, _) => true));
        // Another DLL under the mod's name.
        Assert.Contains("own DLL", CheckFrameGenFolder(MfgUnlocker, folder, _ => "version.dll", (_, _) => true));
        var opti = Path.Combine(_source, OptiScaler.Repo, "v0.9.4");
        Assert.Null(CheckFrameGenFolder(OptiScaler, opti, Original, (_, _) => true));
        // AMD's frame generation without AMD's signature.
        Assert.Contains("signed", CheckFrameGenFolder(OptiScaler, opti, Original, (_, name) => !name.StartsWith("amd_", StringComparison.Ordinal)));
        // A 32-bit file, and a missing one.
        var x86 = Pe(400);
        BitConverter.GetBytes((ushort)0x014c).CopyTo(x86, 68);
        File.WriteAllBytes(Path.Combine(opti, "dlssg_to_fsr3_amd_is_better.dll"), x86);
        Assert.Contains("x64", CheckFrameGenFolder(OptiScaler, opti, Original, (_, _) => true));
        DeletionGate.DeleteFile(Path.Combine(opti, "dlssg_to_fsr3_amd_is_better.dll"));
        Assert.Contains("no dlssg_to_fsr3", CheckFrameGenFolder(OptiScaler, opti, Original, (_, _) => true));
    }

    [Fact]
    public void ExosCopyOfAReleaseIsUsedOnlyWhileEveryFileIsAsRecorded()
    {
        var folder = Path.Combine(_source, MfgUnlocker.Repo, "v1.7.0");
        var dll = Path.Combine(folder, "MFGUnlocker.dll");
        File.WriteAllText(Path.Combine(folder, VerifiedName), JsonSerializer.Serialize(new Dictionary<string, string> { ["MFGUnlocker.dll"] = Sha(dll) }));

        Assert.True(CachedFrameGen(folder));
        File.AppendAllText(dll, "tampered");
        Assert.False(CachedFrameGen(folder));
        File.WriteAllText(Path.Combine(folder, VerifiedName), JsonSerializer.Serialize(new Dictionary<string, string> { [@"..\x.dll"] = new string('a', 64) }));
        Assert.False(CachedFrameGen(folder));
    }

    // ---- AMD FidelityFX through OptiScaler, including where MFG Unlocker used to go

    [Fact]
    public async Task AmdFidelityFxGoesBesideAnUnrealGamesProgramAndOffPutsTheFolderBackExactly()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.Equal(("off", "dlss"), (State(service).State, State(service).Path));

        var on = await On(service);

        Assert.True(on.Ok, on.Message);
        var program = Path.Combine(_root, UnrealProgram);
        Assert.Equal(LoaderBytes(DlssgSm86, "0.4.1"), Sha(Path.Combine(program, "version.dll")));
        Assert.True(File.Exists(Path.Combine(program, @"dlssg_sm86\kernels\fg_kernels.dll")));
        Assert.False(File.Exists(Path.Combine(program, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(program, "RTXMFG-Universal.json")));
        var ini = Text(UnrealProgram + @"\dlssg_sm86.ini");
        Assert.Contains("Engine=DLSS", ini);
        Assert.Contains("MaxGeneratedFrames=1", ini);
        Assert.Contains("Preset=A", ini);
        var status = State(service);
        Assert.Equal(("on", "dlss", "DLSSG SM86 0.4.1"), (status.State, status.Path, status.Version));

        // What the mod writes as it runs goes with its folder.
        Marker(UnrealProgram + @"\dlssg_sm86\logs\dlssg_sm86.log");
        var off = service.FrameGenOff(_game);

        Assert.True(off.Ok, off.Message);
        Assert.Equal(before, Snapshot());
        Assert.Equal("off", State(service).State);
        Assert.Null(service.FrameGenFolderFor(_game.Id));
    }

    [Fact]
    public async Task PhantomBladeZeroOnAnRtx3070GetsAmdFidelityFxNotNvidiasUnlocker()
    {
        PhantomBladeZero();
        var before = Snapshot();
        var service = Service();

        var status = State(service);
        Assert.Equal(("off", "dlss"), (status.State, status.Path));
        Assert.True((await On(service)).Ok);

        // Beside the game, not its Steam stub. DLSS frame generation, not FidelityFX.
        Assert.Equal(LoaderBytes(DlssgSm86, "0.4.1"), Sha(Path.Combine(_root, @"platform\steam\version.dll")));
        Assert.True(File.Exists(Path.Combine(_root, @"platform\steam\dlssg_sm86\kernels\fg_kernels.dll")));
        Assert.False(File.Exists(Path.Combine(_root, @"platform\steam\dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(_root, @"steam\version.dll")));
        Assert.Equal("DLSSG SM86 0.4.1", State(service).Version);
        Assert.True(service.FrameGenOff(_game).Ok);
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData("AMD Radeon RX 6600", "native")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "native")]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER", "off")]
    [InlineData("NVIDIA GeForce RTX 4070", "native")]
    [InlineData("NVIDIA GeForce RTX 5070", "native")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", "native")]
    [InlineData("AMD Radeon RX 580 Series", "native")]
    [InlineData("Intel(R) UHD Graphics 770", "hidden")]
    public void PhantomBladeZeroOnOtherCards(string gpu, string state)
    {
        PhantomBladeZero();

        Assert.Equal(state, State(Service(), gpu).State);
    }

    [Fact]
    public async Task TheRecorderClashesWithDlssFrameGenerationOnlyWhileItRecords()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service)).Ok);
        Assert.False(service.RecorderClashes(_game.Id));
        File.WriteAllText(Path.Combine(_root, UnrealProgram, "scskiller.ini"), "[scskiller]");
        Assert.True(service.RecorderClashes(_game.Id));
        Assert.False(service.RecorderClashes("steam:0"));
    }

    [Fact]
    public async Task AnRtx30GetsDlssAtTwoTimes()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service)).Ok);
        var program = Path.Combine(_root, UnrealProgram);
        Assert.False(File.Exists(Path.Combine(program, "RTXMFG-Universal.json")));
        Assert.True(File.Exists(Path.Combine(program, "version.dll")));
        Assert.False(File.Exists(Path.Combine(program, "dxgi.dll")));
        Assert.Contains("MaxGeneratedFrames=1", Text(UnrealProgram + @"\dlssg_sm86.ini"));
        var status = State(service);
        Assert.Equal(("on", "dlss", "DLSSG SM86 0.4.1"), (status.State, status.Path, status.Version));

        var again = await On(service);
        Assert.True(again.Ok, again.Message);
        Assert.Equal("Already on.", again.Message);
    }

    [Fact]
    public async Task AnRtx40GetsNoMod()
    {
        UnrealGame();
        var before = Snapshot();

        Assert.False((await On(Service(), "NVIDIA GeForce RTX 4070")).Ok);
        Assert.False((await On(Service(), "NVIDIA GeForce RTX 4070")).Ok);
        Assert.Equal(before, Snapshot());
        var line = State(Service(), "NVIDIA GeForce RTX 4070");
        Assert.Equal("native", line.State);
        Assert.Null(line.Path);

        var service = Service();
        Assert.True((await On(service, "NVIDIA GeForce RTX 2080")).Ok);
        var on = State(service, "NVIDIA GeForce RTX 2080");
        Assert.Equal(("on", "dlss", "DLSSG SM86 0.4.1"), (on.State, on.Path, on.Version));
        Assert.Contains("MaxGeneratedFrames=1", Text(UnrealProgram + @"\dlssg_sm86.ini"));
        Assert.True(File.Exists(Path.Combine(_root, UnrealProgram, "version.dll")));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
    }

    // ---- RTX 20 and 30 are never left with nothing

    /// <summary>
    /// The switch as it ships (no DLSSG SM86 pin). Ways it could fail: the old dead end ("Not available for RTX 20 and 30
    /// cards.") with nothing installed; DLSSG SM86 downloaded anyway; OptiScaler taken out again because the card "uses DLSS".
    /// </summary>
    [Fact]
    public async Task WithDlssgUnofferedAnRtx30GetsOptiScalerAndKeepsIt()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service(dlssgOffered: false);
        var status = State(service);
        Assert.Equal(("off", "fsr", null), (status.State, status.Path, status.Note));
        Assert.Equal(FrameGenNext.PutIn, Next(service));

        Assert.Equal(1, await BestSettingsPass(service));

        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "version.dll")));
        Assert.Contains("FGInput=dlssg", Text(UnrealProgram + @"\OptiScaler.ini"));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, @"OptiScaler\fakenvapi.dll")));
        Assert.StartsWith("OptiScaler v", State(service).Version);
        service.FrameGenAtLaunch(_game, Rtx3070);
        Assert.Equal("on", State(service).State);
        Assert.True(service.FrameGenOff(_game).Ok);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void WithDlssgUnofferedPhantomBladeZeroOnAnRtx30UsesItsOwn()
    {
        PhantomBladeZero();
        Assert.Equal("native", State(Service(dlssgOffered: false)).State);
    }

    /// <summary>Exo's earlier DLSSG SM86 installs keep working with the switch off: not taken out, not replaced by OptiScaler.</summary>
    [Fact]
    public async Task AnEarlierDlssgInstallStaysWhileItIsUnoffered()
    {
        UnrealGame();
        Assert.True((await On(Service())).Ok);
        var installed = Snapshot();
        var service = Service(dlssgOffered: false);

        var status = State(service);
        service.FrameGenAtLaunch(_game, Rtx3070);
        Assert.Equal(0, await BestSettingsPass(service));
        await service.FrameGenKeepCurrentAsync([_game], Rtx3070, _ => false, TestContext.Current.CancellationToken);

        Assert.Equal(("on", "dlss", "DLSSG SM86 0.4.1"), (status.State, status.Path, status.Version));
        Assert.Equal(installed, Snapshot());
    }

    /// <summary>The switch on: DLSSG SM86 first, from Best settings.</summary>
    [Fact]
    public async Task WithDlssgOfferedAnRtx30GetsRealDlssFrameGenerationFirst()
    {
        UnrealGame();
        var service = Service(dlssgOffered: true);
        Assert.Equal(1, await BestSettingsPass(service));
        Assert.Equal(LoaderBytes(DlssgSm86, "0.4.1"), Sha(Path.Combine(_root, UnrealProgram, "version.dll")));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.Equal("DLSSG SM86 0.4.1", State(service).Version);
    }

    /// <summary>Every name DLSSG SM86 loads as is taken by a file Exo didn't write: OptiScaler under a free name, the other file untouched.</summary>
    [Fact]
    public async Task WhenDlssgsLoaderNameIsTakenTheGameGetsOptiScaler()
    {
        UnrealGame();
        var theirs = Put(UnrealProgram + @"\version.dll", Pe(700, fill: 5));
        var bytes = Sha(theirs);
        var service = Service(dlssgOffered: true);

        Assert.Equal(("off", "fsr"), (State(service).State, State(service).Path));
        Assert.Equal(1, await BestSettingsPass(service));

        Assert.Equal(bytes, Sha(theirs));
        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.Equal("on", State(service).State);
    }

    /// <summary>
    /// DLSSG SM86's first session fails: the game falls to OptiScaler once; if that fails too the game is kept off, and
    /// it never goes back to DLSSG SM86. Ways it could fail: DLSSG SM86 stays in a game it can't run; the game flips
    /// between the two; OptiScaler is taken out because the card "uses DLSS"; a long session that worked is judged failed.
    /// </summary>
    [Theory]
    [InlineData(5, false)]
    [InlineData(600, true)]
    public async Task AFailedDlssgFirstSessionFallsToOptiScalerOnceAndNeverFlipsBack(int seconds, bool noLog)
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service(dlssgOffered: true);
        Assert.Equal(1, await BestSettingsPass(service));
        if (!noLog) Marker(UnrealProgram + @"\dlssg_sm86\logs\dlssg_sm86.log", "proxy loaded");

        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(seconds), Rtx3070));

        Assert.Equal(before, Snapshot());
        Assert.True(service.FrameGenMemoryFor(_game.Id).DlssFailed);
        var status = State(service);
        Assert.Equal(("off", "fsr", "Taken out: it didn't run in this game"), (status.State, status.Path, status.Note));

        Assert.Equal(1, await BestSettingsPass(service));
        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        // OptiScaler stays: the card no longer "uses DLSS" in this game.
        service.FrameGenAtLaunch(_game, Rtx3070);
        Assert.StartsWith("OptiScaler v", State(service).Version);

        // OptiScaler's first session fails too: out, and kept off (the settings' list, which the caller writes).
        Assert.True(service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(5), Rtx3070));
        Assert.Equal(before, Snapshot());
        Assert.Equal("fsr", State(service).Path);
        Assert.Equal(0, await BestSettingsPass(service, keptOff: id => id == _game.Id));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ADlssgSessionThatRanStays()
    {
        UnrealGame();
        var service = Service(dlssgOffered: true);
        Assert.Equal(1, await BestSettingsPass(service));
        Marker(UnrealProgram + @"\dlssg_sm86\logs\dlssg_sm86.log", "{\"dlssg_create_calls\": 3}");

        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromMinutes(20), Rtx3070));

        var install = ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!;
        Assert.Equal((DlssgSm86.Name, true, "ran"), (install.Mod, install.Proven, install.Session));
        Assert.False(service.FrameGenMemoryFor(_game.Id).DlssFailed);
        // Proven: a later short session doesn't take it out.
        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(5), Rtx3070));
        Assert.Equal(DlssgSm86.Name, ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!.Mod);
    }

    [Theory]
    [InlineData(true, 5, null, true)]
    [InlineData(true, 600, null, true)]
    [InlineData(true, 600, "loaded", false)]
    [InlineData(true, null, null, false)]
    [InlineData(false, 5, null, false)]
    public void DlssgsFirstSessionRule(bool auto, int? seconds, string? log, bool failed)
    {
        var install = new FrameGenInstall(DlssgSm86.Name, "0.4.1", "version.dll", "dlss", "Game.exe", [], Proven: false, Auto: auto);
        Assert.Equal(failed, DlssFirstSessionFailed(install, seconds is { } s ? TimeSpan.FromSeconds(s) : null, log));
        Assert.False(DlssFirstSessionFailed(install with { Proven = true }, TimeSpan.FromSeconds(5), null));
        // A release Exo moved the game to has the one before to go back to: not a first session.
        Assert.False(DlssFirstSessionFailed(install with { Previous = "0.4.0" }, TimeSpan.FromSeconds(5), null));
    }

    /// <summary>
    /// DLSSG SM86 worked in the game, Exo moved it to a newer release, and that one closed at once. Ways it could fail:
    /// DLSSG SM86 comes out and is banned from the game for good, although the release before worked there.
    /// </summary>
    [Fact]
    public async Task ADlssgReleaseThatFailsAfterAnUpdateGoesBackToTheOneBefore()
    {
        UnrealGame();
        var service = Service(dlssgOffered: true);
        Assert.Equal(1, await BestSettingsPass(service));
        Marker(UnrealProgram + @"\dlssg_sm86\logs\dlssg_sm86.log", "{\"dlssg_create_calls\": 3}");
        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromMinutes(20), Rtx3070));
        Release(DlssgSm86, "0.4.2");
        Assert.Equal(1, await service.FrameGenKeepCurrentAsync([_game], Rtx3070, _ => false, TestContext.Current.CancellationToken));

        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(5), Rtx3070));

        var install = ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!;
        Assert.Equal((DlssgSm86.Name, "0.4.1", true), (install.Mod, install.Release, install.Proven));
        Assert.Equal(["0.4.2"], install.Refused!);
        Assert.Equal(LoaderBytes(DlssgSm86, "0.4.1"), Sha(Path.Combine(_root, UnrealProgram, "version.dll")));
        Assert.False(service.FrameGenMemoryFor(_game.Id).DlssFailed);
        Assert.Contains("0.4.2 didn't run", State(service).Note);
    }

    // ---- anti-cheat on every route and card

    /// <summary>Every route and card class this release adds, and the fallback: a game with anti-cheat never gets a mod.</summary>
    [Theory]
    [InlineData(Rtx3070, false)]
    [InlineData(Rtx3070, true)]
    [InlineData("NVIDIA GeForce RTX 2060", false)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", true)]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", true)]
    [InlineData("NVIDIA GeForce GTX 970", true)]
    [InlineData("AMD Radeon RX 580 Series", true)]
    [InlineData("AMD Radeon RX Vega 64", true)]
    [InlineData("AMD Radeon 780M Graphics", true)]
    [InlineData("AMD Radeon 8060S Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", true)]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", true)]
    public async Task AntiCheatIsRefusedOnEveryRouteAndCard(string gpu, bool dlssgOffered)
    {
        UnrealGame();
        Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Pe(300));
        var before = Snapshot();
        var service = Service(dlssgOffered: dlssgOffered);

        Assert.Equal(0, await BestSettingsPass(service, gpu));
        var refused = await On(service, gpu);

        Assert.False(refused.Ok);
        Assert.Contains("Easy Anti-Cheat", refused.Message);
        Assert.Equal(before, Snapshot());
        var status = State(service, gpu);
        Assert.Equal(("note", "Easy Anti-Cheat"), (status.State, status.AntiCheat));
        Assert.Equal(FrameGenNext.Nothing, Next(service, gpu));
    }

    /// <summary>The fallback after DLSSG SM86 failed never sneaks OptiScaler into a game that since shows anti-cheat.</summary>
    [Fact]
    public async Task TheFallbackIsRefusedInAGameThatShowsAntiCheat()
    {
        UnrealGame();
        var service = Service(dlssgOffered: true);
        Assert.Equal(1, await BestSettingsPass(service));
        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(5), Rtx3070));
        Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Pe(300));
        var before = Snapshot();

        Assert.Equal(0, await BestSettingsPass(service));
        Assert.False((await On(service)).Ok);

        Assert.Equal(before, Snapshot());
        Assert.Equal("note", State(service).State);
    }

    // ---- FSR's frame generation where NVIDIA's can't run: OptiScaler

    [Fact]
    public async Task AVulkanGameOnAnRtx30GetsOptiScalerAsWinmmThroughNukems()
    {
        Put(@"x64\Doom.exe", [.. PeImporting("KERNEL32.dll", "vulkan-1.dll"), .. new byte[8000]]);
        Put(@"x64\nvngx_dlssg.dll", Pe(500));
        Put(@"x64\sl.interposer.dll", Pe(500));
        var before = Snapshot();
        var service = Service();
        Assert.Equal(("off", "fsr"), (State(service).State, State(service).Path));

        Assert.True((await On(service)).Ok);

        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(Path.Combine(_root, @"x64\winmm.dll")));
        Assert.True(File.Exists(Path.Combine(_root, @"x64\OptiScaler\amd_fidelityfx_framegeneration_dx12.dll")));
        Assert.True(File.Exists(Path.Combine(_root, @"x64\OptiScaler\Licenses\FidelityFX_v2_LICENSE.md")));
        // A GeForce keeps NVIDIA's own driver library and DLSS upscaling.
        Assert.False(File.Exists(Path.Combine(_root, @"x64\OptiScaler\fakenvapi.dll")));
        var ini = Text(@"x64\OptiScaler.ini");
        Assert.Contains("Enabled=true", ini);
        Assert.Contains("FGInput=nukems", ini);
        Assert.Contains("OptiDllPath=OptiScaler", ini);
        Assert.StartsWith("OptiScaler v", State(service).Version);

        Marker(@"x64\OptiScaler.log");
        Marker(@"x64\OptiScaler\fakenvapi.log");
        Assert.True(service.FrameGenOff(_game).Ok);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task OlderStreamlineOnAnRtx30GetsOptiScalerThroughNukems()
    {
        UnrealGame();
        _streamline = "1.5.0.0";

        Assert.True((await On(Service())).Ok);

        Assert.True(File.Exists(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.Contains("FGInput=nukems", Text(UnrealProgram + @"\OptiScaler.ini"));
    }

    [Fact]
    public async Task ARadeonGetsOptiScalerThroughStreamlineWithFakenvapiAndXess()
    {
        UnrealGame();

        Assert.True((await On(Service(), "AMD Radeon RX 6600")).Ok);

        var ini = Text(UnrealProgram + @"\OptiScaler.ini");
        Assert.Contains("FGInput=dlssg", ini);
        Assert.Contains("FGOutput=fsrfg", ini);
        Assert.True(File.Exists(Path.Combine(_root, UnrealProgram, @"OptiScaler\fakenvapi.dll")));
        Assert.True(File.Exists(Path.Combine(_root, UnrealProgram, @"OptiScaler\libxess.dll")));
        Assert.True(File.Exists(Path.Combine(_root, UnrealProgram, @"OptiScaler\Licenses\XeSS_LICENSE.txt")));
    }

    [Fact]
    public async Task TheUpscalerSwapLeavesTheModsFilesAloneAndTheGamesOwnBesideItTheGames()
    {
        Put(@"bin\x64\Game.exe", Pe(5000));
        Put(@"bin\x64\nvngx_dlssg.dll", Pe(500));
        Put(@"bin\x64\nvngx_dlss.dll", Pe(500));
        Put(@"bin\x64\sl.interposer.dll", Pe(500));
        var service = Service();
        Assert.True((await On(service, "AMD Radeon RX 6600")).Ok);
        // The mod's own files beside its loader, as it writes them while running.
        Marker(@"bin\x64\OptiScaler.log");

        var scan = service.ScanGame(_game, TestContext.Current.CancellationToken);
        var mods = scan.Files.Where(dll => dll.RelativePath.StartsWith(@"bin\x64\OptiScaler\", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(mods);
        Assert.All(mods, dll => Assert.Equal("OptiScaler", dll.Mod));
        Assert.Null(scan.Files.Single(dll => dll.RelativePath.Equals(@"bin\x64\nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)).Mod);
        Assert.Null(scan.Files.Single(dll => dll.RelativePath.Equals(@"bin\x64\nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase)).Mod);
        var plan = Plan(scan.Files, null, null, out var error);
        Assert.Null(error);
        Assert.DoesNotContain(plan, step => step.Dll.Mod is not null);
        Plan(scan.Files, mods[0].RelativePath, "latest", out error);
        Assert.Equal(ModMessage("OptiScaler"), error);
    }

    [Fact]
    public async Task OptiScalersSettingsBesideTheGamesFilesDontMakeThemTheMods()
    {
        Put(@"bin\x64\Game.exe", Pe(5000));
        Put(@"bin\x64\nvngx_dlssg.dll", Pe(500));
        Put(@"bin\x64\amd_fidelityfx_dx12.dll", Pe(500));
        Put(@"bin\x64\sl.interposer.dll", Pe(500));
        var service = new DlssSwapService
        {
            FileVersion = path => Path.GetFileName(path) == "sl.interposer.dll" ? "2.14.1.0" : "310.9.16.0",
            VendorsOwn = (_, _) => false,
            VersionText = _ => null,
            OriginalName = path => _loaders.GetValueOrDefault(Sha(path)),
            FrameGenLatest = (mod, _) => Task.FromResult(_latest[mod.Repo]),
            UnofferedReason = _ => null,
            FrameGenSource = (mod, release, _) => Task.FromResult(Path.Combine(_source, mod.Repo, release.Tag)),
            FrameGenDriver = null,
            FrameGenIndexPath = Path.Combine(_data, "frame-gen", "games.json"),
        };
        Assert.True((await On(service, "AMD Radeon RX 6600")).Ok);

        // The game's own unsigned FSR build stays the game's. OptiScaler's copy, in its folder, is the mod's.
        var scan = service.ScanGame(_game, TestContext.Current.CancellationToken);
        Assert.Null(scan.Files.Single(dll => dll.RelativePath.Equals(@"bin\x64\amd_fidelityfx_dx12.dll", StringComparison.OrdinalIgnoreCase)).Mod);
        Assert.Equal("OptiScaler", scan.Files.Single(dll => dll.RelativePath.Equals(@"bin\x64\OptiScaler\amd_fidelityfx_dx12.dll", StringComparison.OrdinalIgnoreCase)).Mod);
    }

    // ---- refusals

    [Fact]
    public async Task AntiCheatIsAlwaysRefused()
    {
        UnrealGame();
        Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Pe(300));
        var before = Snapshot();
        var service = Service();

        var refused = await On(service);

        Assert.False(refused.Ok);
        Assert.Contains("Easy Anti-Cheat", refused.Message);
        Assert.Equal(before, Snapshot());
        var status = State(service);
        // One plain sentence on the line, from the shared anti-cheat answer.
        Assert.Equal(("note", "Easy Anti-Cheat", "This game uses Easy Anti-Cheat, so Exo puts nothing into it."), (status.State, status.AntiCheat, status.Note));
    }

    [Fact]
    public async Task RiotVanguardIsNeverChanged()
    {
        UnrealGame();
        Marker("vgk.sys");
        var before = Snapshot();
        var service = Service();

        Assert.False((await On(service)).Ok);
        Assert.Equal(before, Snapshot());
        // The line says why, in one sentence, instead of disappearing.
        Assert.Equal(("note", "Exo never changes Riot games or games with Vanguard."), (State(service).State, State(service).Note));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", "native")]
    [InlineData("Intel(R) UHD Graphics 770", "hidden")]
    public async Task CardsWithMultiFrameGenerationOrNoModGetNothing(string gpu, string expectedState)
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();

        Assert.False((await On(service, gpu)).Ok);
        Assert.Equal(before, Snapshot());
        Assert.Equal(expectedState, State(service, gpu).State);
    }

    [Fact]
    public async Task AGameWithoutDlssFrameGenerationHasNoLine()
    {
        Put(@"bin\Game.exe", Pe(5000));
        Put(@"bin\nvngx_dlss.dll", Pe(500));
        var service = Service();

        Assert.Equal("hidden", State(service).State);
        Assert.False((await On(service)).Ok);
    }

    [Theory]
    [InlineData(@"RTXMFG-Universal.json", "MFG Unlocker")]
    [InlineData(@"RTXMFG-Game-Win64-Shipping-0f0f.log", "MFG Unlocker")]
    [InlineData(@"dlssg_sm86.ini", "DLSSG SM86")]
    [InlineData(@"OptiScaler.ini", "OptiScaler")]
    [InlineData(@"LmxxfNrRuntime.dll", "AMDNR")]
    [InlineData(@"dlssnr_on_amd_weights.bin", "AMDNR")]
    [InlineData(@"OptiScaler\amdnr_dlssg_fsr3.dll", "AMDNR")]
    [InlineData(@"nvsmooth30.dll", "dlss-unlocked")]
    [InlineData(@"nvngx.dll_dlssnr.dll", "dlss-unlocked")]
    [InlineData(@"OptiScaler\streamline\sl.common.dll", "dlss-unlocked")]
    [InlineData(@"OptiScaler\dlssg_sm86\dlssg_sm86.ini", "dlss-unlocked")]
    [InlineData(@"dlssg_sm86\dlssg_sm86.dll", "DLSSG SM86")]
    [InlineData(@"amd_fidelityfx_upscaler_dx12.dll.orig", "FSR 4 shader overrides")]
    [InlineData(@"UE4SS-settings.ini", "UE4SS")]
    [InlineData(@"ue4ss\UE4SS.dll", "UE4SS")]
    public async Task AnotherModInTheGameIsLeftAlone(string marker, string mod)
    {
        UnrealGame();
        Marker(UnrealProgram + "\\" + marker);
        var before = Snapshot();
        var service = Service();

        var status = State(service);
        Assert.Equal(("mod", mod), (status.State, status.Mod));
        Assert.False((await On(service)).Ok);
        Assert.Equal(0, await BestSettingsPass(service));
        Assert.Equal(before, Snapshot());
    }

    /// <summary>Ultimate ASI Loader names itself only in its proxy's version information, under any proxy name.</summary>
    [Theory]
    [InlineData("dinput8.dll")]
    [InlineData("version.dll")]
    [InlineData("winmm.dll")]
    public async Task UltimateAsiLoaderIsKnownByItsVersionInformation(string proxy)
    {
        UnrealGame();
        var path = Put(UnrealProgram + "\\" + proxy, Pe(600, fill: 4));
        var before = Snapshot();
        var service = Service(versionText: file => file.Equals(path, StringComparison.OrdinalIgnoreCase) ? "ThirteenAG\nUltimate ASI Loader\nUltimate ASI Loader x64\n\n" : null);

        Assert.Equal(("mod", "Ultimate ASI Loader"), (State(service, Radeon).State, State(service, Radeon).Mod));
        Assert.Equal(0, await BestSettingsPass(service, Radeon));
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// The new markers name mods without taking the game's own files: a game's own Streamline folder stays the game's (its
    /// DLSS frame generation is still found), UE4SS beside the game's DLSS leaves that file the game's, and dlss-unlocked's
    /// copy of NVIDIA's frame generation inside its OptiScaler folder is the mod's.
    /// </summary>
    [Fact]
    public void TheNewMarkersNeverTakeTheGamesOwnFiles()
    {
        Put(@"bin\x64\Game.exe", Pe(5000));
        Put(@"bin\x64\nvngx_dlss.dll", Pe(500));
        Put(@"Engine\Plugins\Runtime\Nvidia\Streamline\Binaries\ThirdParty\Win64\nvngx_dlssg.dll", Pe(500));
        Put(@"Engine\Plugins\Runtime\Nvidia\Streamline\Binaries\ThirdParty\Win64\sl.interposer.dll", Pe(500));
        Marker(@"bin\x64\ue4ss\UE4SS.dll");
        Put(@"bin\x64\OptiScaler\dlssg_sm86\runtime\nvngx_dlssg.dll", Pe(500));
        Marker(@"bin\x64\nvsmooth30.dll");
        var service = Service();

        var scan = service.ScanGame(_game, TestContext.Current.CancellationToken);

        Assert.Null(scan.Files.Single(dll => dll.RelativePath.StartsWith(@"Engine\", StringComparison.OrdinalIgnoreCase)).Mod);
        Assert.Null(scan.Files.Single(dll => dll.RelativePath.Equals(@"bin\x64\nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)).Mod);
        Assert.Equal("dlss-unlocked", scan.Files.Single(dll => dll.RelativePath.Contains("dlssg_sm86", StringComparison.OrdinalIgnoreCase)).Mod);
    }

    [Fact]
    public async Task ADownloadThatFailsChangesNothing()
    {
        UnrealGame();
        var before = Snapshot();
        var service = new DlssSwapService
        {
            FileVersion = path => Path.GetFileName(path) == "sl.interposer.dll" ? "2.14.1.0" : "310.9.16.0",
            VendorsOwn = (_, _) => true,
            VersionText = _ => null,
            UnofferedReason = _ => null,
            FrameGenLatest = (_, _) => throw new InvalidDataException("The release publishes no SHA-256 for OptiScaler v0.9.4, so Exo can't check it."),
            FrameGenDriver = null,
            FrameGenIndexPath = Path.Combine(_data, "frame-gen", "games.json"),
        };

        var on = await On(service);

        Assert.False(on.Ok);
        Assert.Contains("SHA-256", on.Message);
        Assert.Equal(before, Snapshot());
    }

    // ---- after it went in

    // ---- what broke, and what happens next

    private Task<int> BestSettingsPass(DlssSwapService service, string gpu = Rtx3070, Func<string, bool>? keptOff = null) =>
        service.FrameGenEverywhereAsync([_game], gpu, _ => false, keptOff ?? (_ => false), TestContext.Current.CancellationToken);

    /// <summary>
    /// Phantom Blade Zero on the owner's PC: Vortex deployed UE4SS's version.dll over Exo's DLSSG SM86 loader and kept
    /// Exo's as version.dll.vortex_backup. The game did not update. Ways it could fail: the line says a game update broke
    /// it; Exo writes over the other mod, puts its own back, or touches the backup; Best settings tries again; Off fails or
    /// changes a byte of either file.
    /// </summary>
    [Fact]
    public async Task AnotherModOverExosLoaderIsNamedLeftAloneAndKeepsTheGameOff()
    {
        PhantomBladeZero();
        var service = Service();
        Assert.True((await On(service)).Ok);
        var program = Path.Combine(_root, @"platform\steam");
        var loader = Path.Combine(program, "version.dll");
        File.Move(loader, loader + ".vortex_backup");
        File.WriteAllBytes(loader, Pe(2000, fill: 9));
        Directory.CreateDirectory(Path.Combine(program, "ue4ss"));
        Marker(@"platform\steam\ue4ss\UE4SS.dll");
        var foreign = Sha(loader);
        var backup = Sha(loader + ".vortex_backup");

        var status = State(service);

        Assert.Equal(("broken", "replaced", "UE4SS"), (status.State, status.Reason, status.Mod));
        Assert.Equal(FrameGenNext.KeepOff, Next(service));
        Assert.Equal("replaced", service.FrameGenMemoryFor(_game.Id).KeptOff);
        // Best settings and the next launch leave it as it is.
        var untouched = Snapshot();
        Assert.Equal(0, await BestSettingsPass(service));
        service.FrameGenAtLaunch(_game, Rtx3070);
        Assert.Equal(untouched, Snapshot());

        var off = service.FrameGenOff(_game);

        Assert.True(off.Ok, off.Message);
        Assert.Equal(foreign, Sha(loader));
        Assert.Equal(backup, Sha(loader + ".vortex_backup"));
        Assert.False(File.Exists(Path.Combine(program, DlssgSm86.Settings)));
        Assert.False(Directory.Exists(Path.Combine(program, "dlssg_sm86")));
        Assert.True(File.Exists(Path.Combine(program, @"ue4ss\UE4SS.dll")));
        // Exo's record keeps only its proxy, the one file it can't take out yet; the line still says why, and Best
        // settings still puts nothing in.
        Assert.Equal(["version.dll"], ReadFrameGenManifest(program) is { Out: true } left ? left.Files.Select(file => file.Path) : []);
        Assert.Equal(("broken", "replaced", "UE4SS"), (State(service).State, State(service).Reason, State(service).Mod));
        var after = Snapshot();
        Assert.Equal(0, await BestSettingsPass(service));
        Assert.Equal(after, Snapshot());
    }

    /// <summary>A loader Exo can't name: the line says another mod, Off succeeds and keeps it, and the game stays off until that file is gone.</summary>
    [Fact]
    public async Task AnUnnamedLoaderOverExosKeepsTheGameOffUntilItIsGone()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var loader = Path.Combine(_root, UnrealProgram, "dxgi.dll");
        File.WriteAllBytes(loader, Pe(900, fill: 7));
        var foreign = Sha(loader);

        var status = State(service, Radeon);
        Assert.Equal(("broken", "replaced", null), (status.State, status.Reason, status.Mod));

        var off = service.FrameGenOff(_game);

        Assert.True(off.Ok, off.Message);
        Assert.Contains("dxgi.dll", off.Message);
        Assert.Equal(foreign, Sha(loader));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "OptiScaler.ini")));
        Assert.False(Directory.Exists(Path.Combine(_root, UnrealProgram, "OptiScaler")));
        Assert.True(ReadFrameGenManifest(Path.Combine(_root, UnrealProgram)) is { Out: true, Files: [{ Path: "dxgi.dll" }] });
        // Kept off: Best settings would otherwise put OptiScaler in under the next free name beside the other mod.
        Assert.Equal(("broken", "replaced"), (State(service, Radeon).State, State(service, Radeon).Reason));
        var kept = Snapshot();
        Assert.Equal(0, await BestSettingsPass(service, Radeon));
        Assert.Equal(kept, Snapshot());

        // The player took the other mod out: the game is Best settings' again.
        DeletionGate.DeleteFile(loader);
        Assert.Equal("off", State(service, Radeon).State);
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(loader));
    }

    /// <summary>The other mod's manager put Exo's own loader back: Exo's install is whole and on again.</summary>
    [Fact]
    public async Task ExosLoaderPutBackByTheOtherModsManagerIsOnAgain()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var loader = Path.Combine(_root, UnrealProgram, "dxgi.dll");
        File.Move(loader, loader + ".vortex_backup");
        File.WriteAllBytes(loader, Pe(900, fill: 7));
        Assert.Equal("replaced", State(service, Radeon).Reason);

        DeletionGate.DeleteFile(loader);
        File.Move(loader + ".vortex_backup", loader);

        Assert.Equal(("on", null), (State(service, Radeon).State, State(service, Radeon).Reason));
        Assert.Null(service.FrameGenMemoryFor(_game.Id).KeptOff);
    }

    /// <summary>
    /// The player deploys their own OptiScaler build with Vortex over Exo's: Vortex keeps Exo's loader as its backup and
    /// puts its own loader, settings and a file in the OptiScaler folder. Ways it could fail: Off deletes the other build's
    /// settings file, its files in the OptiScaler folder or the folder itself, or touches the backup.
    /// </summary>
    [Fact]
    public async Task AnOptiScalerBuildOverExosKeepsItsOwnFilesWhenOffRuns()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var program = Path.Combine(_root, UnrealProgram);
        var loader = Path.Combine(program, "dxgi.dll");
        File.Move(loader, loader + ".vortex_backup");
        File.WriteAllBytes(loader, Pe(900, fill: 7));
        File.WriteAllText(Path.Combine(program, "OptiScaler.ini"), "[FrameGen]\r\nEnabled=true\r\n; the other build's own settings\r\n");
        File.WriteAllBytes(Path.Combine(program, @"OptiScaler\amd_fidelityfx_dx12.dll"), Pe(800, fill: 6));
        Put(UnrealProgram + @"\OptiScaler\amdnr_dlssg_fsr3.dll", Pe(700, fill: 5));
        var theirs = new[] { "dxgi.dll", "dxgi.dll.vortex_backup", "OptiScaler.ini", @"OptiScaler\amd_fidelityfx_dx12.dll", @"OptiScaler\amdnr_dlssg_fsr3.dll" }
            .ToDictionary(name => name, name => Sha(Path.Combine(program, name)));
        Assert.Equal("replaced", State(service, Radeon).Reason);

        // Best settings' switch goes off: every game's Off.
        service.FrameGenEverywhereOff([_game], _ => false);

        foreach (var (name, sha) in theirs) Assert.Equal(sha, Sha(Path.Combine(program, name)));
        Assert.False(File.Exists(Path.Combine(program, @"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(program, @"OptiScaler\fakenvapi.dll")));
    }

    /// <summary>
    /// dlss-unlocked puts its Streamline inside Exo's OptiScaler folder, and the player drops a newer XeSS there. Ways it
    /// could fail: the line blames another mod for Exo's loader; Off deletes either file, or the folder that holds them.
    /// </summary>
    [Fact]
    public async Task OffKeepsAFileAnotherModPutInExosFolderAndOneThatChanged()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var program = Path.Combine(_root, UnrealProgram);
        var foreign = Put(UnrealProgram + @"\OptiScaler\streamline\sl.common.dll", Pe(600, fill: 4));
        var xess = Path.Combine(program, @"OptiScaler\libxess.dll");
        File.WriteAllBytes(xess, Pe(900, fill: 3));
        var (foreignSha, xessSha) = (Sha(foreign), Sha(xess));

        var status = State(service, Radeon);
        Assert.Equal(("broken", "changed", null), (status.State, status.Reason, status.Mod));
        Assert.Null(service.FrameGenMemoryFor(_game.Id).KeptOff);
        Assert.Equal(0, await BestSettingsPass(service, Radeon));

        var off = service.FrameGenOff(_game);

        Assert.True(off.Ok, off.Message);
        Assert.Equal(foreignSha, Sha(foreign));
        Assert.Equal(xessSha, Sha(xess));
        Assert.False(File.Exists(Path.Combine(program, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(program, "OptiScaler.ini")));
        Assert.False(File.Exists(Path.Combine(program, @"OptiScaler\amd_fidelityfx_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(program, FrameGenManifestName)));
    }

    /// <summary>
    /// Phantom Blade Zero before its first session proved DLSSG SM86: UE4SS's version.dll sits over Exo's proxy, and the
    /// player plays an hour. Ways it could fail: the session that never ran Exo's mod takes it out, bans DLSSG SM86 from
    /// the game for good, and says it didn't run in this game.
    /// </summary>
    [Fact]
    public async Task ASessionWithAnotherModOverExosProxySaysNothingAboutDlssg()
    {
        PhantomBladeZero();
        var service = Service(dlssgOffered: true);
        Assert.Equal(1, await BestSettingsPass(service));
        var program = Path.Combine(_root, @"platform\steam");
        var loader = Path.Combine(program, "version.dll");
        File.Move(loader, loader + ".vortex_backup");
        File.WriteAllBytes(loader, Pe(2000, fill: 9));
        Marker(@"platform\steam\ue4ss\UE4SS.dll");

        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromHours(1), Rtx3070));

        Assert.False(service.FrameGenMemoryFor(_game.Id).DlssFailed);
        Assert.True(File.Exists(Path.Combine(program, DlssgSm86.Settings)));
        Assert.True(Directory.Exists(Path.Combine(program, "dlssg_sm86")));
        var status = State(service);
        Assert.Equal(("broken", "replaced", "UE4SS", null), (status.State, status.Reason, status.Mod, status.Note));
    }

    /// <summary>The same for OptiScaler's first session from Best settings: a short session with another mod's loader over Exo's takes nothing out and keeps nothing off.</summary>
    [Fact]
    public async Task AShortSessionWithAnotherLoaderOverExosTakesNothingOut()
    {
        UnrealGame();
        var service = Service();
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        File.WriteAllBytes(Path.Combine(_root, UnrealProgram, "dxgi.dll"), Pe(900, fill: 7));

        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(5), Radeon));

        Assert.Equal(OptiScaler.Name, ReadFrameGenManifest(Path.Combine(_root, UnrealProgram)) is { Out: false } install ? install.Mod : null);
        Assert.Null(State(service, Radeon).Note);
    }

    /// <summary>
    /// Off with UE4SS's version.dll over Exo's, then the player purges UE4SS and Vortex puts Exo's proxy back. Ways it could
    /// fail: Exo forgets its proxy, which then loads with nothing behind it and is never taken out; the game stays kept
    /// off for good because the file under the name exists again.
    /// </summary>
    [Fact]
    public async Task ExosProxyPutBackAfterOffGoesAndTheGameIsBestSettingsAgain()
    {
        PhantomBladeZero();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service)).Ok);
        var program = Path.Combine(_root, @"platform\steam");
        var loader = Path.Combine(program, "version.dll");
        File.Move(loader, loader + ".vortex_backup");
        File.WriteAllBytes(loader, Pe(2000, fill: 9));
        Marker(@"platform\steam\ue4ss\UE4SS.dll");

        Assert.True(service.FrameGenOff(_game).Ok);

        var left = ReadFrameGenManifest(program)!;
        Assert.True(left.Out);
        Assert.Equal(["version.dll"], left.Files.Select(file => file.Path));
        Assert.Equal(("broken", "replaced", "UE4SS"), (State(service).State, State(service).Reason, State(service).Mod));
        Assert.Equal(0, await BestSettingsPass(service));
        Assert.False(Directory.Exists(Path.Combine(program, "dlssg_sm86")));

        // The player purges UE4SS in Vortex, which puts its backup of Exo's proxy back.
        DeletionGate.DeleteFile(loader);
        DeletionGate.DeleteDirectory(Path.Combine(program, "ue4ss"), true);
        File.Move(loader + ".vortex_backup", loader);

        var status = State(service);

        Assert.Equal(("off", "dlss"), (status.State, status.Path));
        Assert.Equal(before, Snapshot());
        Assert.Null(service.FrameGenMemoryFor(_game.Id).KeptOff);
        Assert.Null(service.FrameGenFolderFor(_game.Id));
    }

    /// <summary>The same proxy back, found as the game starts: it goes before the game loads it.</summary>
    [Fact]
    public async Task ExosProxyPutBackAfterOffGoesAsTheGameStarts()
    {
        PhantomBladeZero();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service)).Ok);
        var loader = Path.Combine(_root, @"platform\steam\version.dll");
        File.Move(loader, loader + ".vortex_backup");
        File.WriteAllBytes(loader, Pe(2000, fill: 9));
        Assert.True(service.FrameGenOff(_game).Ok);
        service.FrameGenAtLaunch(_game, Rtx3070);
        Assert.True(File.Exists(loader + ".vortex_backup"));

        DeletionGate.DeleteFile(loader);
        File.Move(loader + ".vortex_backup", loader);
        service.FrameGenAtLaunch(_game, Rtx3070);

        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// A take-out (here a patch to the game's program) while an unnamed loader is over Exo's. Ways it could fail: the take-out
    /// forgets the other loader, and the next Best settings pass puts OptiScaler in under the next free name beside it.
    /// </summary>
    [Fact]
    public async Task ATakeOutWithAnotherLoaderOverExosKeepsTheGameOff()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var program = Path.Combine(_root, UnrealProgram);
        var loader = Path.Combine(program, "dxgi.dll");
        File.WriteAllBytes(loader, Pe(900, fill: 7));
        var foreign = Sha(loader);
        File.AppendAllText(Path.Combine(program, "Game-Win64-Shipping.exe"), "patch");

        var status = State(service, Radeon);

        Assert.Equal(("broken", "replaced", "Taken out: the game updated"), (status.State, status.Reason, status.Note));
        Assert.Equal("replaced", service.FrameGenMemoryFor(_game.Id).KeptOff);
        Assert.False(Directory.Exists(Path.Combine(program, "OptiScaler")));
        Assert.Equal(0, await BestSettingsPass(service, Radeon));
        Assert.False(File.Exists(Path.Combine(program, "winmm.dll")));
        Assert.Equal(foreign, Sha(loader));
    }

    /// <summary>
    /// The FSR 4 shader overrides are beside the game while an unnamed loader goes over Exo's. Ways it could fail: the line
    /// says the shader overrides replaced Exo's loader; a loader that names itself goes unnamed.
    /// </summary>
    [Fact]
    public async Task TheLineNamesOnlyTheModWhoseFileIsOverExosLoader()
    {
        UnrealGame();
        Assert.True((await On(Service(), Radeon)).Ok);
        Marker(UnrealProgram + @"\amd_fidelityfx_upscaler_dx12.dll.orig");
        var loader = Path.Combine(_root, UnrealProgram, "dxgi.dll");
        File.WriteAllBytes(loader, Pe(900, fill: 7));

        Assert.Equal(("replaced", null), (State(Service(), Radeon).Reason, State(Service(), Radeon).Mod));
        var named = Service(versionText: file => file.Equals(loader, StringComparison.OrdinalIgnoreCase) ? "ThirteenAG\nUltimate ASI Loader\n" : null);
        Assert.Equal(("replaced", "Ultimate ASI Loader"), (State(named, Radeon).Reason, State(named, Radeon).Mod));
    }

    /// <summary>
    /// Vortex deploys a copy with exactly Exo's bytes over Exo's loader and its OptiScaler.ini, keeping Exo's own as
    /// *.vortex_backup. Ways it could fail: Off deletes the manager's copies because their bytes are Exo's, leaving Exo's
    /// files orphaned as the backups; once the manager puts its backups back, Exo's files stay with nothing to take them out.
    /// </summary>
    [Fact]
    public async Task AManagersCopyOfExosOwnFilesStaysWithItsBackupAndGoesOnceItIsPutBack()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var program = Path.Combine(_root, UnrealProgram);
        foreach (var name in new[] { "dxgi.dll", "OptiScaler.ini" })
        {
            var path = Path.Combine(program, name);
            File.Move(path, path + ".vortex_backup");
            File.Copy(path + ".vortex_backup", path);
        }
        var managed = new[] { "dxgi.dll", "dxgi.dll.vortex_backup", "OptiScaler.ini", "OptiScaler.ini.vortex_backup" }
            .ToDictionary(name => name, name => Sha(Path.Combine(program, name)));
        Assert.Equal(("broken", "replaced"), (State(service, Radeon).State, State(service, Radeon).Reason));

        Assert.True(service.FrameGenOff(_game).Ok);

        foreach (var (name, sha) in managed) Assert.Equal(sha, Sha(Path.Combine(program, name)));
        Assert.False(Directory.Exists(Path.Combine(program, "OptiScaler")));
        Assert.Equal(0, await BestSettingsPass(service, Radeon));
        Assert.False(File.Exists(Path.Combine(program, "winmm.dll")));

        // The player purges the mod in Vortex, which puts its backups (Exo's own files) back.
        foreach (var name in new[] { "dxgi.dll", "OptiScaler.ini" })
        {
            var path = Path.Combine(program, name);
            DeletionGate.DeleteFile(path);
            File.Move(path + ".vortex_backup", path);
        }

        Assert.Equal("off", State(service, Radeon).State);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// A take-out (a patch to the game's program) whose Off can't finish: Exo's record stays (here held open, as an
    /// antivirus would). Ways it could fail: the line says on, or "Exo's files were removed" for files Exo took out
    /// itself; Best settings puts them back and counts that against the game; the loop never ends.
    /// </summary>
    [Fact]
    public async Task ATakeOutThatCantFinishSaysItIsComingOutAndCountsNothing()
    {
        UnrealGame();
        var service = Service();
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        var program = Path.Combine(_root, UnrealProgram);
        File.AppendAllText(Path.Combine(program, "Game-Win64-Shipping.exe"), "patch");

        using (new FileStream(Path.Combine(program, FrameGenManifestName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var status = State(service, Radeon);
            Assert.Equal(("note", null, "Coming out: the game updated"), (status.State, status.Reason, status.Note));
            Assert.Equal(0, await BestSettingsPass(service, Radeon));
            Assert.Equal(new FrameGenMemory(), service.FrameGenMemoryFor(_game.Id));
            Assert.False(File.Exists(Path.Combine(program, "dxgi.dll")));
        }

        // Once the record can go, the take-out finishes and Best settings puts the mod in beside the new program.
        var done = State(service, Radeon);
        Assert.Equal(("off", "Taken out: the game updated"), (done.State, done.Note));
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        Assert.Equal(("on", null), (State(service, Radeon).State, State(service, Radeon).Note));
    }

    /// <summary>
    /// The session that ends right after a patch: Exo's mod is still in (the take-out runs at the next read or launch).
    /// Ways it could fail: the short session counts as the mod's failed first session, and Best settings keeps the game
    /// off for good although the patch was the cause.
    /// </summary>
    [Fact]
    public async Task ASessionAfterAPatchIsNotTheModsFailedSession()
    {
        UnrealGame();
        var service = Service();
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        File.AppendAllText(Path.Combine(_root, UnrealProgram, "Game-Win64-Shipping.exe"), "patch");

        Assert.False(service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(5), Radeon));

        Assert.Equal("Taken out: the game updated", State(service, Radeon).Note);
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
    }

    /// <summary>
    /// The take-out happens inside a Best settings pass that puts the mod straight back beside the new program. Ways it
    /// could fail: the line then shows the stale "Taken out: the game updated" over a mod that is on.
    /// </summary>
    [Fact]
    public async Task AModPutBackInTheSamePassShowsItsLineNotTheTakeOut()
    {
        UnrealGame();
        var service = Service();
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        File.AppendAllText(Path.Combine(_root, UnrealProgram, "Game-Win64-Shipping.exe"), "patch");
        service.FrameGenAtLaunch(_game, Radeon);

        Assert.Equal(1, await BestSettingsPass(service, Radeon));

        var status = State(service, Radeon);
        Assert.Equal(("on", null, $"OptiScaler v0.9.4"), (status.State, status.Note, status.Version));
    }

    /// <summary>
    /// UE4SS is in the game under its own proxy name (dwmapi.dll) with its folder, and then ReShade, or a file Exo can't
    /// name, goes over Exo's dxgi.dll. Ways it could fail: the line says UE4SS replaced Exo's loader because UE4SS's folder
    /// is beside the game; ReShade and Special K go unnamed.
    /// </summary>
    [Fact]
    public async Task AUe4ssFolderBesideTheGameDoesntNameADifferentFileOverExosLoader()
    {
        UnrealGame();
        Assert.True((await On(Service(), Radeon)).Ok);
        Directory.CreateDirectory(Path.Combine(_root, UnrealProgram, "ue4ss"));
        Marker(UnrealProgram + @"\ue4ss\UE4SS.dll");
        Put(UnrealProgram + @"\dwmapi.dll", Pe(700, fill: 8));
        var loader = Path.Combine(_root, UnrealProgram, "dxgi.dll");
        File.WriteAllBytes(loader, Pe(900, fill: 7));

        Assert.Equal(("replaced", null), (State(Service(), Radeon).Reason, State(Service(), Radeon).Mod));
        foreach (var (text, mod) in new[] { ("crosire\nReShade\ncrosire's ReShade post-processing suite", "ReShade"), ("The Special K Group\nSpecial K\n", "Special K") })
        {
            var named = Service(versionText: file => file.Equals(loader, StringComparison.OrdinalIgnoreCase) ? text : null);
            Assert.Equal(("replaced", mod), (State(named, Radeon).Reason, State(named, Radeon).Mod));
        }
        // A file with version information that names nothing Exo knows is never put down to UE4SS either.
        var unknown = Service(versionText: file => file.Equals(loader, StringComparison.OrdinalIgnoreCase) ? "Some Company\nSome Tool\n" : null);
        Assert.Null(State(unknown, Radeon).Mod);
    }

    /// <summary>
    /// Only Exo's loader under other bytes is another mod's loader. Ways it could fail: a newer AMD or Intel library in Exo's
    /// folder, or a file an antivirus holds while the page opens, reads as another mod over the loader.
    /// </summary>
    [Fact]
    public void OnlyTheLoaderReadsAsReplacedAndAFileExoCantReadSaysNothing()
    {
        var folder = Path.Combine(_root, "damage");
        var loader = Path.Combine(folder, "dxgi.dll");
        var xess = Path.Combine(folder, @"OptiScaler\libxess.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(xess)!);
        File.WriteAllBytes(loader, Pe(500));
        File.WriteAllBytes(xess, Pe(500, fill: 2));
        FrameGenFile Recorded(string path, string relative) =>
            new(relative, new FileInfo(path).Length, Sha(path).ToLowerInvariant(), File.GetLastWriteTimeUtc(path));
        var install = new FrameGenInstall(OptiScaler.Name, "v0.9.4", "dxgi.dll", "dlssg", "Game.exe", [Recorded(loader, "dxgi.dll"), Recorded(xess, @"OptiScaler\libxess.dll")]);
        Assert.Null(FrameGenDamage(folder, install));

        File.WriteAllBytes(xess, Pe(600, fill: 3));
        Assert.Equal(("changed", @"OptiScaler\libxess.dll"), FrameGenDamage(folder, install));
        using (new FileStream(xess, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(FrameGenDamage(folder, install));

        File.WriteAllBytes(loader, Pe(700, fill: 4));
        Assert.Equal(("replaced", "dxgi.dll"), FrameGenDamage(folder, install));
    }

    /// <summary>
    /// Learned.json can't be written (read-only, or an antivirus holds its temporary file). Ways it could fail: DLSSG SM86's
    /// failed first session isn't remembered, so the game swaps between DLSSG SM86 and OptiScaler every session; files
    /// that went are put back every pass.
    /// </summary>
    [Fact]
    public async Task ADlssgFailureExoCantRememberKeepsTheGameOff()
    {
        UnrealGame();
        var service = Service(dlssgOffered: true);
        Assert.Equal(1, await BestSettingsPass(service));
        Directory.CreateDirectory(Path.Combine(_data, "frame-gen", "learned.json.tmp"));

        Assert.True(service.FrameGenAfterSession(_game, TimeSpan.FromMinutes(10), Rtx3070));

        Assert.False(service.FrameGenMemoryFor(_game.Id).DlssFailed);
        Assert.Null(ReadFrameGenManifest(Path.Combine(_root, UnrealProgram)));
    }

    [Fact]
    public async Task FilesThatWentWhereExoCantCountThemKeepTheGameOff()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        DeletionGate.DeleteFile(Path.Combine(_root, UnrealProgram, "dxgi.dll"));
        Directory.CreateDirectory(Path.Combine(_data, "frame-gen", "learned.json.tmp"));
        var keptOff = new List<string>();

        var added = await service.FrameGenEverywhereAsync([_game], Radeon, _ => false, _ => false, TestContext.Current.CancellationToken, keptOff.Add);

        Assert.Equal(0, added);
        Assert.Equal([_game.Id], keptOff);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>Kept off because Exo's files went twice; then Best settings' switch goes off. Ways it could fail: the line stays red with nothing of Exo's in the game, and the game can never be tried again.</summary>
    [Fact]
    public async Task KeptOffEndsWhenBestSettingsGoesOff()
    {
        UnrealGame();
        var service = Service();
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        var loader = Path.Combine(_root, UnrealProgram, "dxgi.dll");
        DeletionGate.DeleteFile(loader);
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        DeletionGate.DeleteFile(loader);
        Assert.Equal(0, await BestSettingsPass(service, Radeon));
        Assert.Equal(("broken", "removed"), (State(service, Radeon).State, State(service, Radeon).Reason));

        service.FrameGenEverywhereOff([_game], _ => false);

        Assert.Equal("off", State(service, Radeon).State);
        Assert.Equal(new FrameGenMemory(), service.FrameGenMemoryFor(_game.Id));
    }

    /// <summary>
    /// Best settings and a broken install, end to end. Ways it could fail: a changed file or another loader over Exo's is
    /// put back over, or reads as off.
    /// </summary>
    [Theory]
    [InlineData("changed")]
    [InlineData("replaced")]
    public async Task BestSettingsLeavesABrokenInstallAlone(string reason)
    {
        UnrealGame();
        var service = Service();
        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        File.WriteAllBytes(Path.Combine(_root, UnrealProgram, reason == "changed" ? @"OptiScaler\libxess.dll" : "dxgi.dll"), Pe(900, fill: 7));
        var status = State(service, Radeon);
        Assert.Equal(("broken", reason), (status.State, status.Reason));
        var broken = Snapshot();

        Assert.Equal(0, await BestSettingsPass(service, Radeon));

        Assert.Equal(broken, Snapshot());
    }

    /// <summary>
    /// A GTX card gets OptiScaler with NVIDIA's own driver beneath it, as an RTX 20 or 30 does: no fakenvapi (it stands in for
    /// NVIDIA's driver where there is none, and would hide the card's own Reflex) and no XeSS upscaler.
    /// </summary>
    [Theory]
    [InlineData("NVIDIA GeForce GTX 1060 6GB")]
    [InlineData("NVIDIA GeForce GTX 1650")]
    public async Task AGtxGetsOptiScalerOverNvidiasOwnDriver(string gpu)
    {
        UnrealGame();
        var service = Service();

        Assert.Equal(1, await BestSettingsPass(service, gpu));

        var program = Path.Combine(_root, UnrealProgram);
        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(Path.Combine(program, "dxgi.dll")));
        Assert.Contains("FGInput=dlssg", Text(UnrealProgram + @"\OptiScaler.ini"));
        var files = ReadFrameGenManifest(program)!.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dxgi.dll", files);
        Assert.Contains(@"OptiScaler\amd_fidelityfx_dx12.dll", files);
        Assert.Contains(@"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll", files);
        Assert.Contains(@"OptiScaler\dlssg_to_fsr3_amd_is_better.dll", files);
        Assert.Contains("OptiScaler.ini", files);
        Assert.DoesNotContain(@"OptiScaler\fakenvapi.dll", files);
        Assert.DoesNotContain(@"OptiScaler\libxess.dll", files);
    }

    /// <summary>
    /// Exo's files went (antivirus, a store's verify). Ways it could fail: it reads as a game update or another mod; Exo
    /// puts them back again and again; Off leaves anything of Exo's.
    /// </summary>
    [Fact]
    public async Task RemovedFilesGoBackOnceAndTheSecondTimeTheGameStaysOff()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var loader = Path.Combine(_root, UnrealProgram, "dxgi.dll");

        DeletionGate.DeleteFile(loader);
        var status = State(service, Radeon);
        Assert.Equal(("broken", "removed"), (status.State, status.Reason));
        Assert.Null(status.Mod);

        Assert.Equal(1, await BestSettingsPass(service, Radeon));
        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(loader));
        Assert.Equal("on", State(service, Radeon).State);

        DeletionGate.DeleteFile(loader);
        Assert.Equal(0, await BestSettingsPass(service, Radeon));

        Assert.Equal(before, Snapshot());
        Assert.Equal(("broken", "removed"), (State(service, Radeon).State, State(service, Radeon).Reason));
        Assert.Equal(0, await BestSettingsPass(service, Radeon));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task RemovedFilesOffCleansTheRest()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);

        DeletionGate.DeleteFile(Path.Combine(_root, UnrealProgram, @"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll"));

        Assert.Equal("removed", State(service, Radeon).Reason);
        Assert.True(service.FrameGenOff(_game).Ok);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>The game stopped shipping DLSS Frame Generation (an update or a repair took its file): Exo's mod comes out, as for a game update, and says why.</summary>
    [Theory]
    [InlineData(Radeon)]
    [InlineData(Rtx3070)]
    public async Task AGameThatNoLongerHasDlssFrameGenerationLosesTheMod(string gpu)
    {
        UnrealGame();
        const string dlssg = @"Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64\nvngx_dlssg.dll";
        var expected = Snapshot();
        expected.Remove(dlssg);
        var service = Service();
        Assert.True((await On(service, gpu)).Ok);
        DeletionGate.DeleteFile(Path.Combine(_root, dlssg));

        var status = State(service, gpu);

        Assert.Equal(("note", DlssgGoneNote), (status.State, status.Note));
        Assert.Equal(expected, Snapshot());
        Assert.Equal("hidden", State(service, gpu).State);
    }

    [Fact]
    public async Task AsTheGameStartsItComesOutOnceTheGameNoLongerHasDlssFrameGeneration()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        DeletionGate.DeleteFile(Path.Combine(_root, @"Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64\nvngx_dlssg.dll"));

        service.FrameGenAtLaunch(_game, Radeon);

        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, FrameGenManifestName)));
        Assert.Equal(DlssgGoneNote, State(service, Radeon).Note);
    }

    [Fact]
    public void TheDamageIsReadFromExosRecord()
    {
        var folder = Path.Combine(_root, "damage");
        Directory.CreateDirectory(folder);
        var loader = Path.Combine(folder, "dxgi.dll");
        File.WriteAllBytes(loader, Pe(500));
        File.WriteAllText(Path.Combine(folder, "OptiScaler.ini"), "[FrameGen]");
        var info = new FileInfo(loader);
        var install = new FrameGenInstall(OptiScaler.Name, "v0.9.4", "dxgi.dll", "dlssg", "Game.exe",
            [new("dxgi.dll", info.Length, Sha(loader).ToLowerInvariant(), info.LastWriteTimeUtc), new("OptiScaler.ini", 10, null, default), new(@"OptiScaler\x.dll", 1, new string('a', 64), default)]);

        // A file missing is removed; a loader of other bytes is replaced, and that wins.
        Assert.Equal(("removed", @"OptiScaler\x.dll"), FrameGenDamage(folder, install));
        File.WriteAllBytes(loader, Pe(600, fill: 3));
        Assert.Equal(("replaced", "dxgi.dll"), FrameGenDamage(folder, install));
        // The settings file is the mod's to rewrite.
        File.WriteAllText(Path.Combine(folder, "OptiScaler.ini"), "[FrameGen]\nEnabled=false\n");
        Assert.Null(FrameGenDamage(folder, install with { Files = install.Files.Where(file => file.Sha256 is null).ToList() }));
    }

    [Fact]
    public async Task ARecordThatNamesTheGamesFilesIsNotExos()
    {
        UnrealGame();
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var program = Path.Combine(_root, UnrealProgram);
        var manifest = Path.Combine(program, FrameGenManifestName);
        var text = File.ReadAllText(manifest);

        foreach (var bad in new[] { @"..\\..\\..\\Game.exe", "Game-Win64-Shipping.exe", @"C:\\Windows\\win.ini" })
        {
            File.WriteAllText(manifest, text.Replace("OptiScaler.ini", bad, StringComparison.Ordinal));
            Assert.Null(ReadFrameGenManifest(program));
        }
        File.WriteAllText(manifest, text.Replace("\"OptiScaler\"", "\"Something else\"", StringComparison.Ordinal));
        Assert.Null(ReadFrameGenManifest(program));
        File.WriteAllText(manifest, text);
        Assert.NotNull(ReadFrameGenManifest(program));
    }

    [Fact]
    public async Task AsTheGameStartsItComesOutOnceTheGameShowsAntiCheat()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service)).Ok);

        Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Pe(300));
        var withAntiCheat = Snapshot();
        foreach (var added in withAntiCheat.Keys.Where(key => key.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase))) before[added] = withAntiCheat[added];
        service.FrameGenAtLaunch(_game, Rtx3070);

        Assert.Equal(before, Snapshot());
        var status = State(service);
        Assert.Equal("note", status.State);
        Assert.Contains("Easy Anti-Cheat", status.Note);
    }

    /// <summary>
    /// A game whose files show Easy Anti-Cheat, started from Steam's entry without it (NBA 2K26's "without EAC"): frame
    /// generation may go in. Back to "with anti-cheat" it comes out before the next start, whether Properties takes it out
    /// (<see cref="WebHostBridge.AntiCheatBackOn"/>) or, the pick changed some other way, the start itself does.
    /// </summary>
    [Fact]
    public async Task WithoutAntiCheatItGoesInAndBackWithItComesOutBeforeTheStart()
    {
        UnrealGame();
        Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Pe(300));
        var before = Snapshot();
        var without = true;
        var prior = AntiCheatCheck.WithoutAntiCheatLaunch;
        AntiCheatCheck.WithoutAntiCheatLaunch = game => game.Id == _game.Id
            ? without ? new Adapters.SteamAppInfoNames.AntiCheatFreeLaunch("option1", "Frame Gen Test without EAC (offline only)", "Game.exe") : null
            : prior(game);
        try
        {
            var service = Service();
            Assert.True((await On(service)).Ok);
            Assert.Equal("on", State(service).State);
            Assert.NotEqual(before, Snapshot());
            // Still without: the start keeps it.
            service.FrameGenAtLaunch(_game, Rtx3070);
            Assert.Equal("on", State(service).State);

            // Back with anti-cheat through Properties: out at once, and nothing goes back in.
            without = false;
            Assert.Equal((true, true), AntiCheatBackOn(service) is var (ok, removed, _) ? (ok, removed) : default);
            Assert.Equal(before, Snapshot());
            Assert.Equal(0, await BestSettingsPass(service));
            Assert.False((await On(service)).Ok);
            Assert.Equal(before, Snapshot());
            Assert.Equal(("note", "Easy Anti-Cheat"), (State(service).State, State(service).AntiCheat));
            // Nothing in: taking out again is a no-op.
            Assert.Equal((true, false), AntiCheatBackOn(service) is var (ok2, removed2, _) ? (ok2, removed2) : default);

            // In again without, then the pick goes back some other way: the start takes it out first.
            without = true;
            Assert.True((await On(service)).Ok);
            without = false;
            service.FrameGenAtLaunch(_game, Rtx3070);
            Assert.Equal(before, Snapshot());
            Assert.Contains("Easy Anti-Cheat", State(service).Note);
        }
        finally { AntiCheatCheck.WithoutAntiCheatLaunch = prior; }
    }

    private (bool Ok, bool Removed, string Message) AntiCheatBackOn(DlssSwapService service) => WebHostBridge.AntiCheatBackOn(service, _game);

    [Fact]
    public async Task AsTheGameStartsItComesOutOnceAPatchChangedTheGamesProgram()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service)).Ok);

        Put(@"Game\Binaries\Win64\Game-Win64-Shipping.exe", Pe(4100));
        var patched = Snapshot();
        before[@"Game\Binaries\Win64\Game-Win64-Shipping.exe"] = patched[@"Game\Binaries\Win64\Game-Win64-Shipping.exe"];
        service.FrameGenAtLaunch(_game, Rtx3070);

        Assert.Equal(before, Snapshot());
        var status = State(service);
        Assert.Equal("off", status.State);
        Assert.Equal("Taken out: the game updated", status.Note);
    }

    [Fact]
    public async Task AsTheGameStartsItComesOutOnACardWithMultiFrameGeneration()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service)).Ok);

        service.FrameGenAtLaunch(_game, "NVIDIA GeForce RTX 5070");

        Assert.Equal(before, Snapshot());
        var status = State(service, "NVIDIA GeForce RTX 5070");
        Assert.Equal("note", status.State);
        Assert.Contains("Multi Frame Generation", status.Note);
    }

    [Fact]
    public async Task TheDriversFrameCapMovesIntoOptiScalerAndBack()
    {
        Put(@"x64\Doom.exe", [.. PeImporting("vulkan-1.dll"), .. new byte[8000]]);
        Put(@"x64\nvngx_dlssg.dll", Pe(500));
        Put(@"x64\sl.interposer.dll", Pe(500));
        var driver = new FakeDriver(global: 138, gameOwn: null);
        var service = Service(driver);

        Assert.True((await On(service)).Ok);

        Assert.Contains("FramerateLimit=138", Text(@"x64\OptiScaler.ini"));
        Assert.Equal(new uint?[] { 0 }, driver.Sets);
        // The screen changed: the global cap followed it, and OptiScaler follows as the game starts.
        driver.Global = 141;
        service.FrameGenAtLaunch(_game, Rtx3070);
        Assert.Contains("FramerateLimit=141", Text(@"x64\OptiScaler.ini"));
        Assert.True(service.FrameGenOff(_game).Ok);
        Assert.Null(driver.Game);
        Assert.Equal(new uint?[] { 0, null }, driver.Sets);
    }

    [Fact]
    public async Task AnRtx40KeepsTheDriversFrameCap()
    {
        UnrealGame();
        var driver = new FakeDriver(global: 138);
        var service = Service(driver);

        Assert.False((await On(service, "NVIDIA GeForce RTX 4070")).Ok);
        service.FrameGenAtLaunch(_game, "NVIDIA GeForce RTX 4070");

        Assert.Empty(driver.Sets);
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "OptiScaler.ini")));
    }

    [Fact]
    public async Task AsTheGameStartsItComesOutOnACardWithDlssFrameGeneration()
    {
        UnrealGame();
        var before = Snapshot();
        var service = Service();
        Assert.True((await On(service)).Ok);

        service.FrameGenAtLaunch(_game, "NVIDIA GeForce RTX 4070");

        Assert.Equal(before, Snapshot());
        var status = State(service, "NVIDIA GeForce RTX 4070");
        Assert.Equal("note", status.State);
        Assert.Contains("DLSS Frame Generation", status.Note);
    }

    [Fact]
    public void AnExistingMfgUnlockerInstallIsTakenOutForAmdFidelityFx()
    {
        UnrealGame();
        var before = Snapshot();
        var program = Path.Combine(_root, UnrealProgram);
        var loader = Path.Combine(_source, MfgUnlocker.Repo, "v1.7.0", MfgUnlocker.Dll);
        File.Copy(loader, Path.Combine(program, "version.dll"));
        File.WriteAllText(Path.Combine(program, MfgUnlocker.Settings), "{}");
        var loaderInfo = new FileInfo(Path.Combine(program, "version.dll"));
        var settingsInfo = new FileInfo(Path.Combine(program, MfgUnlocker.Settings));
        var install = new FrameGenInstall(MfgUnlocker.Name, "v1.7.0", "version.dll", "dlss", "Game-Win64-Shipping.exe",
        [
            new("version.dll", loaderInfo.Length, Sha(loaderInfo.FullName), loaderInfo.LastWriteTimeUtc),
            new(MfgUnlocker.Settings, settingsInfo.Length, null, settingsInfo.LastWriteTimeUtc),
        ]);
        File.WriteAllText(Path.Combine(program, FrameGenManifestName), JsonSerializer.Serialize(install));

        var status = State(Service());

        Assert.Equal(("off", "dlss"), (status.State, status.Path));
        Assert.Contains("DLSS", status.Note);
        Assert.Equal(before, Snapshot());
    }

    // ---- keeping it on the newest release

    [Fact]
    public async Task ANewerReleaseReplacesTheModsFilesAndOffStillPutsTheFolderBack()
    {
        UnrealGame();
        var before = Snapshot();
        Release(OptiScaler, "v0.9.3");
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        var settings = Text(UnrealProgram + @"\OptiScaler.ini");

        Release(OptiScaler, "v0.9.4");
        var moved = await service.FrameGenKeepCurrentAsync([_game], Radeon, _ => false, TestContext.Current.CancellationToken);

        Assert.Equal(1, moved);
        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.Equal(Sha(Path.Combine(_source, OptiScaler.Repo, "v0.9.4", "amd_fidelityfx_framegeneration_dx12.dll")), Sha(Path.Combine(_root, UnrealProgram, @"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll")));
        Assert.Equal(settings, Text(UnrealProgram + @"\OptiScaler.ini"));
        Assert.False(File.Exists(Path.Combine(_root, UnrealProgram, "version.dll")));
        var install = ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!;
        Assert.Equal(("v0.9.4", "v0.9.3", false), (install.Release, install.Previous, install.Proven));
        Assert.StartsWith("OptiScaler v", State(service, Radeon).Version);
        // Nothing newer: nothing moves; a game being updated by its store waits.
        Assert.Equal(0, await service.FrameGenKeepCurrentAsync([_game], Radeon, _ => false, TestContext.Current.CancellationToken));
        Assert.True(service.FrameGenOff(_game).Ok);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task AGameBusyWithItsStoreIsNotMoved()
    {
        UnrealGame();
        Release(OptiScaler, "v0.9.3");
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        Release(OptiScaler, "v0.9.4");

        Assert.Equal(0, await service.FrameGenKeepCurrentAsync([_game], Radeon, _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(LoaderBytes(OptiScaler, "v0.9.3"), Sha(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
        Assert.StartsWith("OptiScaler v", State(service, Radeon).Version);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(600, true)]
    public async Task ANewReleaseThatClosedAtOnceGoesBackAndARealSessionStays(int seconds, bool stays)
    {
        UnrealGame();
        Release(OptiScaler, "v0.9.3");
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        Release(OptiScaler, "v0.9.4");
        Assert.Equal(1, await service.FrameGenKeepCurrentAsync([_game], Radeon, _ => false, TestContext.Current.CancellationToken));
        // OptiScaler does not read an MFG Unlocker status file.
        Marker(UnrealProgram + @"\RTXMFG-Universal.status.json", """{"frameGenerationCreateObserved":true,"ampereSubmittedBatches":0,"ampereFailure":3}""");

        service.FrameGenAfterSession(_game, TimeSpan.FromSeconds(seconds), Radeon);

        var install = ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!;
        var line = State(service, Radeon);
        Assert.StartsWith("OptiScaler v", line.Version);
        if (stays)
        {
            Assert.Equal(LoaderBytes(OptiScaler, "v0.9.4"), Sha(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
            Assert.Equal(("v0.9.4", true), (install.Release, install.Proven));
            Assert.Null(install.Refused);
            Assert.Null(line.Note);
            Assert.Null(line.Session);
        }
        else
        {
            Assert.Equal(LoaderBytes(OptiScaler, "v0.9.3"), Sha(Path.Combine(_root, UnrealProgram, "dxgi.dll")));
            Assert.Equal(("v0.9.3", true), (install.Release, install.Proven));
            Assert.Equal(new[] { "v0.9.4" }, install.Refused!);
            Assert.Contains("0.9.4 didn't run", line.Note);
            Assert.Equal(0, await service.FrameGenKeepCurrentAsync([_game], Radeon, _ => false, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ARealSessionProvesTheNewReleaseAndTheLineNamesIt()
    {
        UnrealGame();
        Release(OptiScaler, "v0.9.3");
        var service = Service();
        Assert.True((await On(service, Radeon)).Ok);
        Release(OptiScaler, "v0.9.4");
        await service.FrameGenKeepCurrentAsync([_game], Radeon, _ => false, TestContext.Current.CancellationToken);
        Marker(UnrealProgram + @"\RTXMFG-Universal.status.json", """{"frameGenerationCreateObserved":true,"ampereSubmittedBatches":20480}""");

        service.FrameGenAfterSession(_game, TimeSpan.FromMinutes(40), Radeon);

        var install = ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!;
        Assert.Equal(("v0.9.4", true, null), (install.Release, install.Proven, install.Session));
        Assert.StartsWith("OptiScaler v", State(service, Radeon).Version);
        Assert.Null(State(service, Radeon).Session);
        Marker(UnrealProgram + @"\RTXMFG-Universal.status.json", """{"frameGenerationCreateObserved":true,"ampereSubmittedBatches":0}""");
        service.FrameGenAfterSession(_game, TimeSpan.FromMinutes(5), Radeon);
        Assert.Null(State(service, Radeon).Session);
        Assert.Equal("v0.9.4", ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!.Release);
    }

    // ---- pieces

    [Fact]
    public void TheGamesProgramIsTheLargestBesideFrameGenerationOrInTheInstall()
    {
        var stub = Put(@"steam\PBZ.exe", Pe(1000));
        var game = Put(@"platform\steam\PBZ.exe", Pe(50000));
        var bytes = Pe(90000);
        BitConverter.GetBytes((ushort)0x014c).CopyTo(bytes, 68);
        var x86 = Put(@"platform\steam\Big32.exe", bytes);
        var exes = new[] { stub, game, x86 }.Select(path => (path, new FileInfo(path).Length)).ToList();

        Assert.Equal(game, GameProgram(exes, []));
        Assert.Equal(stub, GameProgram(exes, [Path.GetDirectoryName(stub)!]));
    }

    [Fact]
    public void ImportsAreReadFromThePeImage()
    {
        var path = Put("vk.exe", PeImporting("KERNEL32.dll", "vulkan-1.dll"));

        Assert.Equal(new HashSet<string> { "kernel32.dll", "vulkan-1.dll" }, ReadPeImportNames(path)!.ToHashSet());
        Assert.Null(ReadPeImportNames(Put("not.exe", [1, 2, 3])));
    }

    [Fact]
    public void OptiScalersSettingsAreSetInPlaceInItsOwnFile()
    {
        const string own = "; -------\r\n[FrameGen]\r\n; Enables Frame Generation\r\nEnabled=auto\r\n\r\n; dlssg - DLSSG\r\nFGInput=auto\r\n\r\n[Framerate]\r\n; float\r\nFramerateLimit=auto\r\n";

        var text = SetIniValue(own, "FrameGen", "FGInput", "dlssg");
        text = SetIniValue(text, "Framerate", "FramerateLimit", "138");
        text = SetIniValue(text, "FrameGen", "FGOutput", "fsrfg");
        text = SetIniValue(text, "Libraries", "OptiDllPath", "OptiScaler");

        Assert.Equal(
            "; -------\r\n[FrameGen]\r\n; Enables Frame Generation\r\nEnabled=auto\r\n\r\n; dlssg - DLSSG\r\nFGInput=dlssg\r\nFGOutput=fsrfg\r\n\r\n[Framerate]\r\n; float\r\nFramerateLimit=138\r\n\r\n[Libraries]\r\nOptiDllPath=OptiScaler\r\n",
            text);
        Assert.Equal("[FrameGen]\r\nEnabled=true\r\nFGInput=nukems\r\n\r\n[Libraries]\r\nOptiDllPath=OptiScaler\r\n", FrameGenIniText("nukems", null));
    }

    [Fact]
    public void TagsReadAsVersions()
    {
        Assert.Equal(new Version(1, 5, 0), TagVersion("v1.5.0-hotfix.1"));
        Assert.Equal(new Version(0, 9, 4), TagVersion("v0.9.4"));
        Assert.Equal(new Version(2, 0), TagVersion("2"));
        Assert.Null(TagVersion("nightly"));
    }

    [Fact]
    public void AntiCheatGameWithOwnFrameGenReportsNativeOnRtx30InsteadOfRefusingMod()
    {
        var rtx30 = FrameGenCardFor(Rtx3070);
        Assert.Equal("native", FrameGenPath(rtx30, Game(own: true, antiCheat: AntiCheatCheck.Ace)));

        UnrealGame();
        Put(@"AntiCheatExpert\ACE-CORE.sys", Pe(300));
        Put(@"Engine\Plugins\Runtime\Intel\XeSS\Binaries\ThirdParty\Win64\libxess_fg.dll", Pe(500));
        var before = Snapshot();
        var service = Service(dlssgOffered: true);

        var status = State(service, Rtx3070);
        Assert.Equal("native", status.State);
        Assert.Null(status.AntiCheat);
        Assert.Equal(FrameGenNext.Nothing, Next(service, Rtx3070));
        Assert.Equal(before, Snapshot());
    }
}
