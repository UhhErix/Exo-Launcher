using ExoLauncher.Services;
using static ExoLauncher.Tests.WindowsKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the page's calls for Game Mode and Game Bar could go wrong: an action reaches the wrong thing (Game Mode's on
/// for Game Bar's off), a refusal is answered as done, or an action that is not one of these is taken for one.
/// </summary>
public sealed class WindowsSideVerbsTests
{
    private static readonly string Key = WindowsGameMode.KeyPath;
    private static readonly string Name = WindowsGameMode.ValueName;

    [Fact]
    public void TheHostVerbsReachGameModeAndGameBar()
    {
        var registry = new FakeRegistry();
        registry.Put(Key, Name, RegValue.DWord(0));
        var record = new PcRecord();
        var mode = new WindowsGameMode(registry, new FakeWatch(), record.Load, record.Save, _ => { });
        var barRecord = new PcRecord();
        var bar = new WindowsGameBar(registry, barRecord.Load, barRecord.Save, _ => { });

        Assert.Null(WebHostBridge.WindowsSideAction("gamemode-on", mode, bar));
        Assert.Equal(RegValue.DWord(1), registry.Get(Key, Name));
        Assert.Null(WebHostBridge.WindowsSideAction("gamemode-off", mode, bar));
        Assert.Equal(RegValue.DWord(0), registry.Get(Key, Name));

        Assert.Null(WebHostBridge.WindowsSideAction("gamebar-on", mode, bar));
        Assert.Equal(RegValue.DWord(0), registry.Get(WindowsGameBar.Dwords[0].Key, WindowsGameBar.Dwords[0].Name));
        Assert.Equal(GameBarState.Held, barRecord.Pc.GameBar!.State);
        Assert.Null(WebHostBridge.WindowsSideAction("gamebar-off", mode, bar));
        Assert.Equal(GameBarState.Off, barRecord.Pc.GameBar!.State);

        // A refusal comes back as the sentence the page shows.
        registry.Ignored.Add((Key, Name));
        Assert.Equal("Windows didn't turn Game Mode on.", WebHostBridge.WindowsSideAction("gamemode-on", mode, bar));
        // Not one of ours.
        Assert.False(WebHostBridge.IsWindowsSideAction("gpupref"));
        Assert.True(WebHostBridge.IsWindowsSideAction("gamebar-off"));
    }
}
