using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

public sealed class ExoHostPhase2Slice2ParityTests
{
    private static JsonDocument LoadGolden()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "native-ui",
                "crates",
                "exo-host",
                "tests",
                "fixtures",
                "phase2-slice2-golden.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate));
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate native-ui/crates/exo-host/tests/fixtures/phase2-slice2-golden.json");
    }

    private static HashSet<string> LoadContractMethods()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ui", "contracts", "host-v1.json");
            if (File.Exists(candidate))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                return doc.RootElement
                    .GetProperty("methods")
                    .EnumerateObject()
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.Ordinal);
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate ui/contracts/host-v1.json");
    }

    [Fact]
    public void RecordedDotnetGoldenCoversAllFiveReadonlyFamiliesInHostV1Contract()
    {
        using var doc = LoadGolden();
        var methods = LoadContractMethods();
        var families = doc.RootElement.GetProperty("families");

        string[] expectedKeys =
        [
            "sensors",
            "gpuProfile",
            "gpuProcesses",
            "gpuTuningState",
            "systemInfo",
            "displayInfo",
            "hdrState",
            "driveInfo",
        ];

        foreach (var key in expectedKeys)
        {
            var entry = families.GetProperty(key);
            var method = entry.GetProperty("method").GetString()!;
            Assert.Contains(method, methods);
            Assert.Equal(JsonValueKind.Object, entry.GetProperty("recordedDotnet").ValueKind);
        }

        var sensors = families.GetProperty("sensors").GetProperty("recordedDotnet");
        Assert.True(sensors.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(sensors.GetProperty("items").EnumerateArray());

        var gpuProfile = families.GetProperty("gpuProfile").GetProperty("recordedDotnet");
        Assert.True(gpuProfile.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(gpuProfile.GetProperty("vendor").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(gpuProfile.GetProperty("gpu").GetString()));

        var gpuTuning = families.GetProperty("gpuTuningState").GetProperty("recordedDotnet");
        Assert.True(gpuTuning.GetProperty("ok").GetBoolean());
        Assert.Equal("auto", gpuTuning.GetProperty("mode").GetString());
        Assert.True(gpuTuning.GetProperty("live").TryGetProperty("tempC", out _));

        var about = families.GetProperty("systemInfo").GetProperty("recordedDotnet");
        Assert.False(string.IsNullOrWhiteSpace(about.GetProperty("processor").GetString()));
        Assert.True(about.GetProperty("cores").GetInt32() > 0);
        Assert.True(about.GetProperty("threads").GetInt32() >= about.GetProperty("cores").GetInt32());
        Assert.True(about.GetProperty("memoryGb").GetInt32() > 0);

        var display = families.GetProperty("displayInfo").GetProperty("recordedDotnet");
        Assert.True(display.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(display.GetProperty("displays").EnumerateArray());

        var hdr = families.GetProperty("hdrState").GetProperty("recordedDotnet");
        Assert.NotEmpty(hdr.GetProperty("displays").EnumerateArray());

        var drives = families.GetProperty("driveInfo").GetProperty("recordedDotnet");
        Assert.True(drives.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(drives.GetProperty("drives").EnumerateArray());
    }

    [Fact]
    public void RecordedGpuProcessesRespectAntiCheatAndWindowsProtectionPolicy()
    {
        using var doc = LoadGolden();
        var procs = doc.RootElement
            .GetProperty("families")
            .GetProperty("gpuProcesses")
            .GetProperty("recordedDotnet")
            .GetProperty("processes")
            .EnumerateArray()
            .ToArray();

        Assert.NotEmpty(procs);
        foreach (var proc in procs)
        {
            var name = proc.GetProperty("name").GetString()!;
            if (ProcessTermination.IsAntiCheat(name))
                Assert.True(proc.TryGetProperty("protectedReason", out _));
        }

        Assert.Contains(procs, p =>
            string.Equals(p.GetProperty("name").GetString(), "dwm", StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.GetProperty("protectedReason").GetString(), "part of Windows", StringComparison.Ordinal));
    }
}
