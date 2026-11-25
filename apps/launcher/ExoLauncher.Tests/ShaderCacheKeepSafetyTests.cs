using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class ShaderCacheKeepSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("exo-shader-keep-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private DeletionGate.Policy TestPolicy() => new(
        KeptRoots: [Path.Combine(_root, "Documents", "Exo", "Backups")],
        ExoData: Path.Combine(_root, "Exo"),
        ShaderRoots:
        [
            Path.Combine(_root, "Local", "D3DSCache"),
            Path.Combine(_root, "Local", "ExoShaders"),
            Path.Combine(_root, "Common", "NVIDIA Corporation", "NV_Cache"),
        ],
        Running: () => [],
        ShaderBusy: () => false);

    [Fact]
    public void JunkAutoCleanNeverIncludesShaderCaches()
    {
        Assert.DoesNotContain("shaders", JunkAutoClean.Kinds, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("shaders", JunkAutoClean.WeeklyKinds, StringComparer.OrdinalIgnoreCase);
        Assert.False(JunkAutoClean.Auto("shaders"));
        Assert.False(JunkAutoClean.Auto("shader-cache"));
        Assert.False(JunkAutoClean.Auto("d3dscache"));
    }

    [Fact]
    public void JunkFilesNeverTargetsDriverOrGameShaderCaches()
    {
        var local = Path.Combine(_root, "Local");
        var roaming = Path.Combine(_root, "Roaming");
        var pf86 = Path.Combine(_root, "PF86");
        var common = Path.Combine(_root, "Common");

        var protectedPaths = new[]
        {
            Touch("Local", "D3DSCache", "abc123", "cache.dxcache"),
            Touch("Local", "ExoShaders", "libraries", "steam_1.exoplib"),
            Touch("Local", "NVIDIA", "DXCache", "0002a9db.nvph"),
            Touch("Local", "NVIDIA", "GLCache", "abc.bin"),
            Touch("Common", "NVIDIA Corporation", "NV_Cache", "abc.bin"),
            Touch("Local", "AMD", "DxCache", "a.parc"),
            Touch("Local", "Intel", "ShaderCache", "i.bin"),
            Touch("PF86", "Steam", "steamapps", "shadercache", "1620730", "fozpipelinesv6", "steamapp_pipeline_cache.foz"),
            Touch("Local", "MyUeGame", "Saved", "MyUeGame_PCD3D_SM6.upipelinecache"),
            Touch("PF86", "Steam", "steamapps", "common", "Call of Duty HQ", "cod26", "shadercache", "techsets.psolib"),
        };

        var folders = JunkFiles.BrowserCacheFolders(local, roaming)
            .Concat(JunkFiles.LauncherCacheFolders(local, roaming, pf86, common))
            .ToList();

        foreach (var protectedFile in protectedPaths)
        {
            Assert.DoesNotContain(folders, folder =>
                protectedFile.StartsWith(folder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void DeletionGateRefusesCleanersForEngineDriverAndParentShaderFolders()
    {
        var policy = TestPolicy();
        var protectedTargets = new[]
        {
            Touch("Local", "ExoShaders", "recorded", "steam_1.exopso"),
            Touch("Local", "ExoShaders", "libraries", "steam_1.exoplib"),
            Touch("Common", "NVIDIA Corporation", "NV_Cache", "global.bin"),
            Touch("Games", "Steam", "steamapps", "shadercache", "570", "fozpipelinesv6", "steamapp_shader_cache.foz"),
            Touch("Games", "UE5Title", "Saved", "PipelineCaches", "Game_PCD3D_SM6.upipelinecache"),
            Touch("Games", "UE5Title", "Saved", "ShaderCache", "cache.ushaderprecache"),
            Touch("Games", "CoD", "cod26", "shadercache", "techsets.psolib"),
            Touch("Games", "GodotApp", "Godot", "shader_cache", "pipelines.bin"),
        };

        foreach (var file in protectedTargets)
        {
            Assert.False(DeletionGate.Clean(file, null, out var why, policy));
            Assert.False(string.IsNullOrEmpty(why));
            Assert.True(File.Exists(file));
        }

        var ueParent = Path.Combine(_root, "Games", "UE5Title");
        Assert.False(DeletionGate.Clean(ueParent, null, out var parentWhy, policy));
        Assert.False(string.IsNullOrEmpty(parentWhy));
        Assert.True(Directory.Exists(ueParent));

        var localAncestor = Path.Combine(_root, "Local");
        Assert.False(DeletionGate.Clean(localAncestor, null, out var ancestorWhy, policy));
        Assert.False(string.IsNullOrEmpty(ancestorWhy));
        Assert.True(Directory.Exists(localAncestor));
    }

    [Fact]
    public void DeletionProtectionsIncludesExoShadersAndProgramDataNvCache()
    {
        Assert.Contains(DeletionProtections.ShaderRoots, r => r.EndsWith(@"\ExoShaders", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(DeletionProtections.ShaderRoots, r => r.EndsWith(@"\NVIDIA Corporation\NV_Cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DriveSpaceOldKeepsValidInstalledGameUnpluggedDriveAndExoShadersCaches()
    {
        var now = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        var exoShaders = Path.Combine(_root, "Local", "ExoShaders");
        var caches = new List<DriveSpace.CacheFolder>
        {
            new("steam-installed", null, "1620730", 500_000, now.AddDays(-90)),
            new("d3d-installed-newest", @"C:\Games\UEGame\Binaries\Win64\Game.exe", null, 2_000_000, now.AddDays(-60)),
            new("d3d-unplugged-drive", @"E:\Games\Offline\Game.exe", null, 1_500_000, now.AddDays(-120)),
            new("d3d-exo-shaders-helper", Path.Combine(exoShaders, "ExoShaders.Service.exe"), null, 1_000_000, now.AddDays(-90)),
        };

        bool? ProgramThere(string exe) =>
            exe.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase) ? null
            : exe.StartsWith(exoShaders, StringComparison.OrdinalIgnoreCase) ? false
            : true;

        var old = DriveSpace.OldExcludingRoots(caches, ProgramThere, app => app == "1620730", now, [exoShaders]);
        Assert.Empty(old);
    }

    [Fact]
    public void ShaderRebuildAndShaderCacheWatchNeverDeleteValidGameCaches()
    {
        var gameCache = Touch("Games", "MyGame", "Saved", "PipelineCaches", "MyGame_PCD3D_SM6.upipelinecache");
        var record = new ShaderRebuild.Record { Driver = "590.10" };
        var started = new List<string>();
        var t = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var rebuild = new ShaderRebuild(
            identity: () => "595.79",
            installing: () => false,
            idle: () => true,
            stale: () => [new RebuildGame("steam:1", "MyGame", 100, DateTimeOffset.UtcNow)],
            compiling: () => false,
            start: ids => { started.AddRange(ids); return true; },
            pause: () => { },
            load: () => record,
            save: _ => true,
            log: _ => { },
            now: () => t);

        rebuild.Tick(settled: true);
        Assert.Contains("steam:1", started);
        Assert.True(File.Exists(gameCache));

        var prev = new ShaderCacheWatch.Snapshot(128L << 20, true, 100, t.AddMinutes(-15));
        var wipedNow = new ShaderCacheWatch.Snapshot(4L << 20, true, 100, t);
        Assert.True(ShaderCacheWatch.Wiped(prev, wipedNow, t.AddMinutes(-30)));
        Assert.True(File.Exists(gameCache));
    }

    [Fact]
    public void AutoTuneClearMarkersOnlyTouchesAutoTuneFolderNotShaderCaches()
    {
        var tuneDir = Path.Combine(_root, "auto-tune");
        var shaderFile = Touch("D3DSCache", "bucket", "valid.dxcache");
        var store = new TuneStore(tuneDir);
        store.WriteMarker("gpu", 1);
        store.WriteAppliedAt(DateTimeOffset.UtcNow);
        Touch("auto-tune", TuneStore.HeldFile);
        Touch("auto-tune", TuneStore.ApplyingFile);

        store.ClearHeld();
        store.ClearMarker();
        store.ClearAppliedAt();

        Assert.True(File.Exists(shaderFile));
        Assert.False(File.Exists(Path.Combine(tuneDir, TuneStore.HeldFile)));
        Assert.False(File.Exists(Path.Combine(tuneDir, TuneStore.ApplyingFile)));
    }
}
