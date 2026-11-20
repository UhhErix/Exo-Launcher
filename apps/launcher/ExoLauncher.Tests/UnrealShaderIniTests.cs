using System.Text;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class UnrealShaderIniTests
{
    [Fact]
    public void ApplyWritesMissingVarsAndRestoresOriginalBytes()
    {
        var root = Directory.CreateTempSubdirectory("exo-ue-ini-").FullName;
        try
        {
            var ini = Path.Combine(root, "Engine.ini");
            var ledger = Path.Combine(root, "unreal-shader-ini.json");
            var original = "[Core.System]\r\nPaths=../../../Engine/Content\r\n";
            File.WriteAllText(ini, original, new UTF8Encoding(false));

            Assert.True(UnrealShaderIni.ApplyToFile("steam:1", ini, ledger));
            var text = File.ReadAllText(ini, new UTF8Encoding(false));
            Assert.Contains("[SystemSettings]", text);
            Assert.Contains("r.ShaderPipelineCache.Enabled=1", text);
            Assert.Contains("r.ShaderPipelineCache.SaveUserCache=1", text);
            Assert.Contains("r.ShaderPipelineCache.StartupMode=3", text);

            // Second apply is idempotent.
            Assert.False(UnrealShaderIni.ApplyToFile("steam:1", ini, ledger));

            // Restore brings back exact original file contents.
            Assert.Equal(1, UnrealShaderIni.Restore("steam:1", ledger));
            Assert.Equal(original, File.ReadAllText(ini, new UTF8Encoding(false)));
        }
        finally
        {
            DeletionGate.DeleteDirectory(root, recursive: true);
        }
    }

    [Fact]
    public void PreservesExistingUserSettingsWithoutOverwriting()
    {
        var root = Directory.CreateTempSubdirectory("exo-ue-keep-").FullName;
        try
        {
            var ini = Path.Combine(root, "Engine.ini");
            var ledger = Path.Combine(root, "ledger.json");
            var original = "[SystemSettings]\r\nr.ShaderPipelineCache.Enabled=0\r\nr.Fog=0\r\n";
            File.WriteAllText(ini, original, new UTF8Encoding(false));

            Assert.True(UnrealShaderIni.ApplyToFile("steam:2", ini, ledger));
            var text = File.ReadAllText(ini, new UTF8Encoding(false));
            Assert.Contains("r.ShaderPipelineCache.Enabled=0", text);
            Assert.DoesNotContain("r.ShaderPipelineCache.Enabled=1", text);
            Assert.Contains("r.ShaderPipelineCache.SaveUserCache=1", text);
            Assert.Contains("r.ShaderPipelineCache.StartupMode=3", text);

            Assert.Equal(1, UnrealShaderIni.Restore("steam:2", ledger));
            Assert.Equal(original, File.ReadAllText(ini, new UTF8Encoding(false)));
        }
        finally
        {
            DeletionGate.DeleteDirectory(root, recursive: true);
        }
    }

    [Fact]
    public void SkipsReadOnlyAndAntiCheatGames()
    {
        var root = Directory.CreateTempSubdirectory("exo-ue-skip-").FullName;
        try
        {
            var project = Path.Combine(root, "MyGame");
            Directory.CreateDirectory(Path.Combine(root, "Engine"));
            Directory.CreateDirectory(Path.Combine(project, "Binaries", "Win64"));
            var cfg = Path.Combine(project, "Saved", "Config", "Windows");
            Directory.CreateDirectory(cfg);
            var ini = Path.Combine(cfg, "Engine.ini");
            File.WriteAllText(ini, "[Core.System]\r\n");

            var riot = new GameEntry { Id = "riot:val", Title = "VALORANT", Store = StoreKind.Riot, Installed = true, Path = root };
            UnrealShaderIni.Apply(riot, new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, "Vanguard", "store"));
            Assert.Equal("[Core.System]\r\n", File.ReadAllText(ini));

            File.SetAttributes(ini, FileAttributes.ReadOnly);
            try
            {
                Assert.False(UnrealShaderIni.ApplyToFile("steam:3", ini, Path.Combine(root, "ledger.json")));
            }
            finally
            {
                File.SetAttributes(ini, FileAttributes.Normal);
            }
        }
        finally
        {
            DeletionGate.DeleteDirectory(root, recursive: true);
        }
    }
}
