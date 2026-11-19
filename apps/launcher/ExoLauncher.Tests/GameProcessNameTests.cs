using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the run-state poll's process names can fail, silently (it reads ToolHelp's image names, the deny-list and the
/// stores' process names are written as Process.ProcessName gives them):
/// - ".exe" left on, so steam.exe passes the deny-list and Steam itself reads as a running game;
/// - ".EXE" in capitals left on;
/// - another extension or a name without one changed.
/// </summary>
public sealed class GameProcessNameTests
{
    [Theory]
    [InlineData("steam.exe", "steam")]
    [InlineData("EasyAntiCheat_EOS.EXE", "EasyAntiCheat_EOS")]
    [InlineData("VALORANT-Win64-Shipping.exe", "VALORANT-Win64-Shipping")]
    [InlineData("Registry", "Registry")]
    [InlineData("screensaver.scr", "screensaver.scr")]
    [InlineData("Riot Client.exe", "Riot Client")]
    public void ImageNamesReadAsProcessNames(string image, string expected) =>
        Assert.Equal(expected, GameProcessRegistry.ProcessNameOf(image));

    [Fact]
    public void StoreClientsStayDenied() =>
        Assert.True(GameProcessRegistry.IsReservedProcessName(GameProcessRegistry.ProcessNameOf("steamwebhelper.exe")));
}
