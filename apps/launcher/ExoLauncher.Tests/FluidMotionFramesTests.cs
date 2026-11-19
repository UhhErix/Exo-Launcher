using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo's Fluid Motion Frames choice could go wrong without anyone seeing it (each game's Steam tags, requirements
/// and PCGamingWiki fields are the real ones, read in October 2026):
/// - a competitive game gets it (added lag where it costs most): a big one Steam tags poorly (Call of Duty leads with
///   Singleplayer), one off Steam (Fortnite, VALORANT), a fighting game held at 60 fps, a sports game with eSports low
///   in its tags;
/// - a single-player game is taken for competitive because a PvP mode is in its tags (Elden Ring's invasions, an MMO's
///   arenas), or a PvE shooter for a PvP one;
/// - a game with frame generation of its own gets the driver's on top, found from the wiki or only from its files;
/// - a game on Direct3D 9 alone, or a GPU before RX 6000 or the 700M, gets it where it can't run;
/// - the base frame rate is ignored: a PC that meets only a game's Minimum gets it, or a game that names only a Minimum
///   is taken as too heavy;
/// - the choice is the same on every PC (an RX 6600 and an RX 9070 XT differ on Baldur's Gate 3, an APU on most);
/// - a game Exo knows nothing about gets it;
/// - the wiki's fields are misread (another field taken for "framegen", Direct3D's "9.0c" for 9 and 0, an empty
///   Vulkan field for Vulkan).
/// </summary>
public sealed class FluidMotionFramesTests
{
    private const string Rx6600 = "AMD Radeon RX 6600", Rx7800Xt = "AMD Radeon RX 7800 XT", Rx9070Xt = "AMD Radeon RX 9070 XT",
        Rx5700 = "AMD Radeon RX 5700", Apu = "AMD Radeon(TM) 780M Graphics";

    private static readonly string[] Gpus = [Rx6600, Rx7800Xt, Rx9070Xt, Rx5700, Apu];

    /// <summary>A game as Exo gathers it: Steam's top tags, the wiki's fields, Steam's graphics requirements, its frame generation files.</summary>
    private sealed record Known(int[] Tags, string? Wiki = null, string? Minimum = null, string? Recommended = null, bool Files = false);

    /// <summary>The wiki's fields as its pages write them.</summary>
    private static string Page(string modes, string genres, string frameGen, string fps120, string direct3D, string vulkan = "", string openGl = "") =>
        $"{{{{Infobox game/row/taxonomy/modes             | {modes} }}}}\n{{{{Infobox game/row/taxonomy/genres            | {genres} }}}}\n" +
        $"|framegen                   = {frameGen}\n|framegen tech              =\n|framegen notes             =\n|120 fps                    = {fps120}\n" +
        $"|direct3d versions      = {direct3D}\n|opengl versions        = {openGl}\n|vulkan versions        = {vulkan}\n";

    private static readonly Dictionary<string, Known> Games = new()
    {
        ["ELDEN RING"] = new([29482, 1695, 4604, 122, 4026, 4231, 3859, 1697, 1684, 4182, 3843, 19],
            Page("Singleplayer, Multiplayer", "Action, RPG, Open world", "unknown", "hackable", "12"),
            "NVIDIA GEFORCE GTX 1060 3 GB or AMD RADEON RX 580 4 GB", "NVIDIA GEFORCE GTX 1070 8 GB or AMD RADEON RX VEGA 56 8 GB"),
        ["Sekiro™: Shadows Die Twice"] = new([29482, 4026, 19, 4182, 1688, 1687, 21, 1697, 1695, 1742, 4667, 97070],
            Page("Singleplayer", "Action", "unknown", "hackable", "11"),
            "NVIDIA GeForce GTX 760 | AMD Radeon HD 7950", "NVIDIA GeForce GTX 970 | AMD Radeon RX 570"),
        ["Red Dead Redemption 2"] = new([1695, 1742, 1647, 3859, 21, 19, 4175, 4182, 1774, 6041, 5411, 4166],
            Page("Singleplayer, Multiplayer", "Action, FPS, Open world, Shooter, TPS", "unknown", "true", "12", vulkan: "1.1"),
            "Nvidia GeForce GTX 770 2GB / AMD Radeon R9 280 3GB", "Nvidia GeForce GTX 1060 6GB / AMD Radeon RX 480 4GB"),
        ["Baldur's Gate 3"] = new([122, 4747, 6426, 4325, 1742, 12095, 4474, 1684, 21, 4947, 3843, 3859],
            Page("Singleplayer, Multiplayer", "Adventure, RPG, Exploration, Open world", "unknown", "true", "11", vulkan: "1.1"),
            "Nvidia GTX 970 / RX 480 / Intel Arc A380 / Qualcomm Adreno X1 (4GB+ of VRAM)", "Nvidia 2060 Super / RX 5700 XT / Intel Arc A580 (8GB+ of VRAM)"),
        ["Sid Meier's Civilization® VI"] = new([9, 1741, 3859, 3987, 4364, 4182, 1677, 1670, 4328, 1678, 599, 1708],
            Page("Singleplayer, Multiplayer", "4X, TBS", "unknown", "true", "11, 12", openGl: "true"),
            "1 GB & AMD 5570 or nVidia 450 or Intel Integrated Graphics 530", "2GB & AMD 7970 or nVidia 770 or greater"),
        ["FINAL FANTASY XIV Online"] = new([1754, 122, 4747, 128, 1684, 4434, 1742, 1695, 3859, 3843, 4057, 21], null,
            "NVIDIA GeForce GTX 970 or higher, AMD Radeon RX 480 or higher", "NVIDIA GeForce RTX 2060 or higher, AMD Radeon RX 5600 XT or higher"),
        ["Lost Ark"] = new([1754, 113, 4231, 3859, 1646, 4747, 122, 128, 12095, 19, 6730, 21]),
        ["HELLDIVERS™ 2"] = new([3843, 6730, 3814, 3859, 1774, 19, 1685, 3942, 1199779, 4845, 1755, 1697]),
        ["Stardew Valley"] = new([87918, 3964, 3859, 10235, 122, 1654, 599, 22602, 3810, 1702, 492, 1643],
            Page("Singleplayer, Multiplayer", "RPG, Building, Farming, Life sim", "unknown", "false", "9.0c", openGl: "2.0"),
            "256 mb video memory, shader model 3.0+"),
        ["World of Warcraft"] = new([], Page("Multiplayer", "MMORPG", "unknown", "true", "11, 12")),

        ["Cyberpunk 2077"] = new([4115, 1695, 6650, 122, 4182, 1742, 1663, 3942, 4295, 3839, 4166, 3834],
            Page("Singleplayer", "Action, RPG, Open world", "true", "true", "12"),
            "GeForce GTX 1060 6GB or Radeon RX 580 8GB or Arc A380", "GeForce RTX 2060 SUPER or Radeon RX 5700 XT or Arc A770"),
        ["Monster Hunter Wilds"] = new([9564, 19, 3859, 3843, 1685, 4747, 1695, 4046, 1697, 4231, 9551, 21],
            Page("Singleplayer, Multiplayer", "ARPG, Hunting", "true", "true", "12")),
        ["Black Myth: Wukong"] = new([16094, 4231, 19, 29482, 122, 3993, 1742, 4182, 4106, 4604, 4166, 21], Files: true),
        ["Fallout: New Vegas"] = new([], Page("Singleplayer", "ARPG, Open world, Shooter", "unknown", "hackable", "9.0c")),

        ["Counter-Strike 2"] = new([1663, 1774, 3859, 3878, 19, 5711, 5055, 1708, 3839, 1775, 3843, 1685]),
        ["Apex Legends™"] = new([113, 3859, 176981, 1663, 1774, 3839, 1775, 19, 620519, 5711, 1708, 3942]),
        ["Street Fighter™ 6"] = new([4736, 1743, 1773, 4747, 3859, 19, 1775, 3878, 21, 7368, 5055, 4182],
            Page("Singleplayer, Multiplayer", "Fighting", "unknown", "limited", "12")),
        ["EA SPORTS FC™ 25"] = new([701, 1254546, 7481, 3859, 599, 5055, 3878, 7368, 5350, 3841, 3843, 4182]),
        ["Call of Duty®"] = new([1663, 3859, 1774, 4182, 19, 4168, 3839, 1678, 5673, 1708, 4667, 1685]),
        ["Call of Duty®: Black Ops 7"] = new([]),
        ["Fortnite"] = new([]),
        ["VALORANT"] = new([]),
        ["Tom Clancy's Rainbow Six® Siege X"] = new([]),
        ["Rust"] = new([], Page("Multiplayer", "Survival, FPS", "unknown", "true", "11")),

        ["A game nobody has written about"] = new([]),
    };

    // On for each GPU, in the order of Gpus: RX 6600, RX 7800 XT, RX 9070 XT, RX 5700, Radeon 780M.
    public static TheoryData<string, bool[]> Table => new()
    {
        // Held at 60 fps: doubled, on every card that runs it near 60; a 780M is under Elden Ring's and Sekiro's Minimum.
        { "ELDEN RING", [true, true, true, false, false] },
        { "Sekiro™: Shadows Die Twice", [true, true, true, false, false] },
        { "Red Dead Redemption 2", [true, true, true, false, false] },
        // Recommended is an RX 5700 XT: an RX 6600 meets only Minimum, under the base frame rate AFMF needs.
        { "Baldur's Gate 3", [false, true, true, false, false] },
        { "Sid Meier's Civilization® VI", [true, true, true, false, false] },
        { "FINAL FANTASY XIV Online", [true, true, true, false, false] },
        // No requirements to judge: the genre decides, the APU included.
        { "Lost Ark", [true, true, true, false, true] },
        { "HELLDIVERS™ 2", [true, true, true, false, true] },
        { "Stardew Valley", [true, true, true, false, true] },
        { "World of Warcraft", [true, true, true, false, true] },
        // Frame generation of their own, or no Fluid Motion Frames for the API.
        { "Cyberpunk 2077", [false, false, false, false, false] },
        { "Monster Hunter Wilds", [false, false, false, false, false] },
        { "Black Myth: Wukong", [false, false, false, false, false] },
        { "Fallout: New Vegas", [false, false, false, false, false] },
        // Competitive.
        { "Counter-Strike 2", [false, false, false, false, false] },
        { "Apex Legends™", [false, false, false, false, false] },
        { "Street Fighter™ 6", [false, false, false, false, false] },
        { "EA SPORTS FC™ 25", [false, false, false, false, false] },
        { "Call of Duty®", [false, false, false, false, false] },
        { "Call of Duty®: Black Ops 7", [false, false, false, false, false] },
        { "Fortnite", [false, false, false, false, false] },
        { "VALORANT", [false, false, false, false, false] },
        { "Tom Clancy's Rainbow Six® Siege X", [false, false, false, false, false] },
        { "Rust", [false, false, false, false, false] },
        { "A game nobody has written about", [false, false, false, false, false] },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void EachGameOnEachPc(string title, bool[] on)
    {
        for (var i = 0; i < Gpus.Length; i++)
        {
            var decision = FluidMotionFrames.Decide(Gpus[i], Game(title, Gpus[i]));
            Assert.True(decision.On == on[i], $"{title} on {Gpus[i]}: {(decision.On ? "on" : "off")} ({decision.Why})");
        }
    }

    private static FluidMotionFrames.Game Game(string title, string gpu)
    {
        var known = Games[title];
        var pc = new StoreService.ThisPc(null, gpu, 16, 11, null, gpu == Apu ? 2 : 8);
        return new FluidMotionFrames.Game(title)
        {
            Tags = known.Tags,
            Wiki = known.Wiki is null ? null : GameWiki.ReadFacts(known.Wiki),
            FrameGenFiles = known.Files,
            Graphics = known.Minimum is null ? null : FluidMotionFrames.Graphics([new StoreService.Spec("Graphics", known.Minimum, known.Recommended)], pc),
        };
    }

    [Theory]
    [InlineData("AMD Radeon RX 6600", true)]
    [InlineData("AMD Radeon RX 7800 XT", true)]
    [InlineData("AMD Radeon RX 9070 XT", true)]
    [InlineData("AMD Radeon RX 7600M XT", true)]
    [InlineData("AMD Radeon PRO W7900", true)]
    [InlineData("AMD Radeon AI PRO R9700", true)]
    [InlineData("AMD Radeon(TM) 780M Graphics", true)]
    [InlineData("AMD Radeon(TM) 890M Graphics", true)]
    [InlineData("AMD Radeon(TM) 8060S Graphics", true)]
    [InlineData("AMD Radeon(TM) Graphics", true)]   // no generation in the name: the driver decides
    [InlineData(null, true)]
    [InlineData("AMD Radeon RX 5700", false)]
    [InlineData("AMD Radeon RX 5700 XT", false)]
    [InlineData("Radeon RX 580 Series", false)]
    [InlineData("AMD Radeon RX Vega 64", false)]
    [InlineData("AMD Radeon(TM) 680M", false)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", false)]
    public void OnlyTheGpusAmdListsRunIt(string? gpu, bool runs) => Assert.Equal(runs, FluidMotionFrames.Runs(gpu));

    [Fact]
    public void TheWikisFieldsReadAsThePageWritesThem()
    {
        var cyberpunk = GameWiki.ReadFacts("{{Infobox game/row/taxonomy/modes             | Singleplayer }}\n" +
            "{{Infobox game/row/taxonomy/genres            | Action, RPG, Open world }}\n|framegen                   = true\n" +
            "|framegen tech              = DLSS FG, DLSS MFG, fsr fg, xess fg\n|120 fps                    = true\n" +
            "|direct3d versions      = 12\n|opengl versions        = \n|vulkan versions        =\n");
        Assert.Equal(["singleplayer"], cyberpunk.Modes);
        Assert.Equal(["action", "rpg", "open world"], cyberpunk.Genres);
        Assert.Equal("true", cyberpunk.FrameGen);
        Assert.True(cyberpunk.Over60);
        Assert.Equal(["dx12"], cyberpunk.Apis);

        var stardew = GameWiki.ReadFacts(Page("Singleplayer, Multiplayer", "RPG", "unknown", "false", "9.0c", openGl: "2.0"));
        Assert.Equal(["dx9", "opengl"], stardew.Apis);
        Assert.False(stardew.Over60);
        Assert.Equal(["dx11", "vulkan"], GameWiki.ReadFacts(Page("Multiplayer", "FPS", "unknown", "true", "11", vulkan: "true")).Apis);

        var none = GameWiki.ReadFacts(null);
        Assert.Empty(none.Modes);
        Assert.Equal("unknown", none.FrameGen);
        Assert.Null(none.Over60);
        Assert.Empty(none.Apis);
    }
}
