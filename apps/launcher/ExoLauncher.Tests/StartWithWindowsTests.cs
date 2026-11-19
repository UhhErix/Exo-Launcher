using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Open Exo when Windows starts. Ways it could fail: it is on before the player turns it on, or the Run value names a path
/// Windows splits at its spaces.
/// </summary>
public sealed class StartWithWindowsTests
{
    [Fact]
    public void ItIsOffUntilThePlayerTurnsItOn() => Assert.False(new AppSettings().StartWithWindows);

    [Fact]
    public void TheRunValueQuotesExosPath() =>
        Assert.Equal(@"""C:\Users\A B\AppData\Local\ExoLauncher\app\ExoLauncher.exe"" --startup",
            StartWithWindows.CommandFor(@"C:\Users\A B\AppData\Local\ExoLauncher\app\ExoLauncher.exe"));
}
