using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

public sealed class LosslessScalingConfigTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "exo-ls-test-" + Guid.NewGuid().ToString("N"));

    public LosslessScalingConfigTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) DeletionGate.DeleteDirectory(_tempDir, recursive: true);
    }

    [Fact]
    public void DetectFindsSteamApp993090InSecondaryLibraryFolder()
    {
        var steamRoot = @"C:\Program Files (x86)\Steam";
        var extraLib = @"D:\SteamLibrary";
        var vdf = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """;
        var manifestPath = Path.Combine(extraLib, "steamapps", "appmanifest_993090.acf");
        var exePath = Path.Combine(extraLib, "steamapps", "common", "Lossless Scaling", "LosslessScaling.exe");
        var acf = """
            "AppState"
            {
                "appid" "993090"
                "installdir" "Lossless Scaling"
            }
            """;

        var info = LosslessScalingConfig.Detect(
            steamRoot,
            vdf,
            @"C:\Users\Player\AppData\Local",
            path => path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) || path.Equals(exePath, StringComparison.OrdinalIgnoreCase),
            path => path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) ? acf : null);

        Assert.True(info.Installed);
        Assert.Equal(exePath, info.ExePath);
        Assert.Equal(@"C:\Users\Player\AppData\Local\Lossless Scaling\settings.xml", info.SettingsPath);
    }

    [Fact]
    public void UpsertProfileXmlCreatesAndUpdatesProfileWhilePreservingExistingSettings()
    {
        var initial = """
            <?xml version="1.0" encoding="utf-8"?>
            <Settings>
              <Hotkey>S</Hotkey>
              <GameProfiles>
                <Profile>
                  <Title>Elden Ring</Title>
                  <Path>eldenring.exe</Path>
                  <AutoScale>true</AutoScale>
                  <FrameGeneration>LSFG 3.1</FrameGeneration>
                </Profile>
              </GameProfiles>
            </Settings>
            """;

        var added = LosslessScalingConfig.UpsertProfileXml(
            initial,
            "Wuthering Waves",
            @"D:\Games\Wuthering Waves\Client\Binaries\Win64\Client-Win64-Shipping.exe",
            autoScale: true);

        Assert.Contains("<Hotkey>S</Hotkey>", added, StringComparison.Ordinal);
        Assert.True(LosslessScalingConfig.HasAutoScaleProfile(added, "Elden Ring", "eldenring.exe"));
        Assert.True(LosslessScalingConfig.HasAutoScaleProfile(added, "Wuthering Waves", "Client-Win64-Shipping.exe"));

        var disabled = LosslessScalingConfig.UpsertProfileXml(
            added,
            "Wuthering Waves",
            @"D:\Games\Wuthering Waves\Client\Binaries\Win64\Client-Win64-Shipping.exe",
            autoScale: false);

        Assert.False(LosslessScalingConfig.HasAutoScaleProfile(disabled, "Wuthering Waves", "Client-Win64-Shipping.exe"));
        Assert.True(LosslessScalingConfig.HasAutoScaleProfile(disabled, "Elden Ring", "eldenring.exe"));
    }

    [Fact]
    public void ConfigureProfileCreatesBackupAndRefusesWhileLosslessScalingRuns()
    {
        var settingsPath = Path.Combine(_tempDir, "Lossless Scaling", "settings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "<Settings><GameProfiles /></Settings>");

        var blocked = LosslessScalingConfig.ConfigureProfile(settingsPath, "Wuthering Waves", "Client-Win64-Shipping.exe", true, () => true);
        Assert.False(blocked.Ok);
        Assert.Contains("Close Lossless Scaling", blocked.Error ?? "", StringComparison.Ordinal);

        var applied = LosslessScalingConfig.ConfigureProfile(settingsPath, "Wuthering Waves", "Client-Win64-Shipping.exe", true, () => false);
        Assert.True(applied.Ok);
        Assert.True(File.Exists(settingsPath + ".exo.bak"));
        Assert.True(LosslessScalingConfig.HasAutoScaleProfile(File.ReadAllText(settingsPath), "Wuthering Waves", "Client-Win64-Shipping.exe"));
    }
}
