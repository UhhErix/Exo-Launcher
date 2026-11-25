using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed partial class SettingsJsonSourceGenTests : IDisposable
{
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "exo-settings-aot-" + Guid.NewGuid().ToString("N"));

    public SettingsJsonSourceGenTests()
    {
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_tempDir, true); } catch { }
    }

    [GeneratedRegex(@"JsonSerializer\.(Serialize|Deserialize)\b", RegexOptions.CultureInvariant)]
    private static partial Regex JsonSerializerCall();

    [Fact]
    public void SettingsJsonContextUsesSourceGenOnlyWithoutReflectionFallback()
    {
        var typeInfo = SettingsJsonContext.Default.AppSettings;
        Assert.Same(SettingsJsonContext.Default, typeInfo.OriginatingResolver);
        Assert.DoesNotContain(
            SettingsJsonContext.Default.Options.TypeInfoResolverChain,
            resolver => resolver is DefaultJsonTypeInfoResolver);

        var reflectionFreeOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = SettingsJsonContext.Default,
        };
        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Serialize(new UnregisteredDummy(1), reflectionFreeOptions));

        var settings = new AppSettings
        {
            AppVersion = "4.0.0-test",
            RtxVideoSuperResolution = 5,
            RtxVideoHdr = true,
            DownloadHoursStart = 2,
            DownloadHoursEnd = 6,
            NvidiaGsync = true,
            FrameCap = new Dictionary<string, GameFrameCapPref>(StringComparer.OrdinalIgnoreCase)
            {
                ["steam:730"] = new GameFrameCapPref { Tier = "max", CapFps = 240, Backend = "nvidia_drs" },
            },
            Autopilot = new AutopilotSettings { On = true, GamePriority = "high", AlwaysDemote = ["OneDrive"] },
            ExoAi = new ExoAiSettings { FanPreset = "quiet" },
            DiscordKeybinds = [new DiscordKeybind { Action = "mute", Keys = [162, 77] }],
            LibraryFolders = [new LibraryFolder { Id = "f1", Name = "FPS", Games = ["steam:730"] }],
            UiPrefs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["voiceChat"] = JsonDocument.Parse("true").RootElement.Clone(),
            },
        };

        var json = JsonSerializer.Serialize(settings, reflectionFreeOptions);
        var roundTripped = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
        Assert.NotNull(roundTripped);
        Assert.Equal("4.0.0-test", roundTripped!.AppVersion);
        Assert.Equal(5, roundTripped.RtxVideoSuperResolution);
        Assert.True(roundTripped.RtxVideoHdr);
        Assert.Equal(2, roundTripped.DownloadHoursStart);
        Assert.Equal(6, roundTripped.DownloadHoursEnd);
        Assert.Equal(240, roundTripped.FrameCap["steam:730"].CapFps);
        Assert.Equal("high", roundTripped.Autopilot.GamePriority);
        Assert.Equal("quiet", roundTripped.ExoAi.FanPreset);
        Assert.Single(roundTripped.DiscordKeybinds);
        Assert.Single(roundTripped.LibraryFolders);
        Assert.Equal(JsonValueKind.True, roundTripped.UiPrefs["voiceChat"].ValueKind);
    }

    [Fact]
    public void SettingsServiceSyncAppVersionAndSalvageWorkThroughSourceGen()
    {
        var service = new SettingsService();
        service.Load();
        service.UpdateProfile(s =>
        {
            s.RtxVideoSuperResolution = 4;
            s.DownloadHoursStart = 1;
            s.DownloadHoursEnd = 7;
        });
        service.SyncAppVersion("4.0.0-test");

        var reloaded = new SettingsService();
        reloaded.Load();
        Assert.Equal("4.0.0-test", reloaded.Current.AppVersion);
        Assert.Equal(4, reloaded.Current.RtxVideoSuperResolution);
        Assert.Equal(1, reloaded.Current.DownloadHoursStart);
        Assert.Equal(7, reloaded.Current.DownloadHoursEnd);

        File.WriteAllText(
            PathHelper.SettingsPath,
            """{"appVersion":"4.0.0-test","rtxVideoSuperResolution":"not-an-int","downloadHoursStart":3,"downloadHoursEnd":8}""");
        var salvaged = new SettingsService();
        salvaged.Load();
        Assert.Null(salvaged.Current.RtxVideoSuperResolution);
        Assert.Equal(3, salvaged.Current.DownloadHoursStart);
        Assert.Equal(8, salvaged.Current.DownloadHoursEnd);
    }

    [Fact]
    public void SettingsServiceSourceCannotRegressToReflectionJson()
    {
        var sourcePath = Repo.PathOf("ExoLauncher", "Services", "SettingsService.cs");
        var lines = File.ReadAllLines(sourcePath);

        Assert.DoesNotContain(lines, line => line.Contains("new JsonSerializerOptions", StringComparison.Ordinal));

        var jsonLines = lines
            .Select((text, index) => (LineNumber: index + 1, Text: text))
            .Where(entry => JsonSerializerCall().IsMatch(entry.Text))
            .ToArray();

        Assert.NotEmpty(jsonLines);
        foreach (var (lineNumber, text) in jsonLines)
        {
            Assert.True(
                text.Contains("SettingsJsonContext.Default.", StringComparison.Ordinal),
                $"SettingsService.cs:{lineNumber} must use SettingsJsonContext.Default.* instead of reflection-based JsonSerializer: {text.Trim()}");
        }
    }

    [Fact]
    public void HeadlessHostProjectGuardsAgainstUnsupportedPublishAotAndReleaseNeverPublishesHostAsAot()
    {
        var hostProject = File.ReadAllText(Repo.PathOf("ExoLauncher.Host", "ExoLauncher.Host.csproj"));
        Assert.Contains("RejectUnsupportedPublishAot", hostProject, StringComparison.Ordinal);
        Assert.Contains("Condition=\"'$(PublishAot)' == 'true'\"", hostProject, StringComparison.Ordinal);

        var buildRelease = File.ReadAllText(Repo.PathOf("tools", "Build-Release.ps1"));
        Assert.DoesNotContain("ExoLauncher.Host", buildRelease, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PublishAot", buildRelease, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record UnregisteredDummy(int Value);
}
