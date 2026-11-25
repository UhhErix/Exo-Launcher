using System.Text.RegularExpressions;

namespace ExoLauncher.Tests;

public sealed partial class TempFolderCleanupTests
{
    private static readonly HashSet<string> Allowlisted = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptimizeStaysOnTests.cs",
        "TempFolderCleanupTests.cs",
        "TestDataDirectory.cs",
        "TestDataIsolationTests.cs",
        "VoiceCutoutFixtureTests.cs",
    };

    [GeneratedRegex(@"Directory\.CreateDirectory|File\.WriteAll|File\.Create", RegexOptions.CultureInvariant)]
    private static partial Regex CreatesDiskArtifacts();

    [GeneratedRegex(@"DeleteDirectory|Directory\.Delete|File\.Delete|DeleteFile|TryDelete", RegexOptions.CultureInvariant)]
    private static partial Regex DeletesDiskArtifacts();

    [GeneratedRegex(@"IDisposable|IAsyncDisposable|public\s+void\s+Dispose|finally\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex HasCleanupBlock();

    private static string FindTestsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ExoLauncher.Tests");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "ExoLauncher.Tests.csproj")))
                return candidate;
            var nested = Path.Combine(dir.FullName, "apps", "launcher", "ExoLauncher.Tests");
            if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, "ExoLauncher.Tests.csproj")))
                return nested;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate ExoLauncher.Tests source directory.");
    }

    [Fact]
    public void EveryTestCreatingTempArtifactsCleansThemUp()
    {
        var testsDir = FindTestsDirectory();
        var files = Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (Allowlisted.Contains(name)) continue;

            var text = File.ReadAllText(file);
            if (!text.Contains("GetTempPath", StringComparison.Ordinal)) continue;
            if (!CreatesDiskArtifacts().IsMatch(text)) continue;

            if (!DeletesDiskArtifacts().IsMatch(text) || !HasCleanupBlock().IsMatch(text))
                offenders.Add(name);
        }

        Assert.True(
            offenders.Count == 0,
            "Test files creating temp artifacts without Dispose/finally cleanup: " + string.Join(", ", offenders));
    }
}
