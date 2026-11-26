using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class FrameCapRetireTests
{
    [Fact]
    public void A_cap_with_no_backend_goes_and_a_cap_for_a_game_not_found_stays_for_the_next_start()
    {
        var caps = new Dictionary<string, GameFrameCapPref>(StringComparer.OrdinalIgnoreCase)
        {
            ["steam:1"] = new() { Tier = "off" },
            ["steam:2"] = new() { Tier = "custom", CapFps = 141, Backend = FrameCapRetire.BackendNvidiaDrs, PriorValue = "0" },
        };
        var kept = FrameCapRetire.Run(caps, _ => null);
        Assert.Equal(["steam:2"], kept);
    }

    [Fact]
    public void An_own_interface_cap_in_a_game_without_its_settings_file_counts_as_taken_out()
    {
        var game = new GameEntry { Id = "steam:3", Title = "Test Game", Store = StoreKind.Steam, Installed = true, Path = Path.Combine(Path.GetTempPath(), "exo-no-such-game-" + Guid.NewGuid().ToString("N")) };
        var caps = new Dictionary<string, GameFrameCapPref>
        {
            [game.Id] = new() { Tier = "custom", CapFps = 141, Backend = FrameCapRetire.BackendOwnInterface, PriorValue = "0" },
        };
        Assert.Empty(FrameCapRetire.Run(caps, id => id == game.Id ? game : null));
    }
}
