using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Best settings' frame generation (DlssSwapService.FrameGenAuto.cs) and the card it reads from the adapter's name
/// (FrameGenCardFor) could go wrong, silently, on someone's PC:
/// - it goes into games on a card that has frame generation of its own (RTX 40 and 50);
/// - it leaves out a card a mod helps: RTX 20 and 30, GTX 16, 10 and 900, Radeon RX 400 and newer and Vega, Radeon
///   graphics in laptops and handhelds (600M to 800M, Strix, Z1, Z2, Ryzen AI), Intel Arc cards and Arc graphics;
/// - a name is read as its neighbour: an RX 580 as an RTX 5080, a GTX 1650 as an RTX, Arc graphics as an Arc A770,
///   Vega graphics inside a processor as an RX Vega 64, a Radeon 780M as an RX 7800;
/// - it takes a card no mod helps (GTX 750, MX, GT, R9, HD, a Radeon with no model, Intel UHD, Iris, HD);
/// - it goes into a game with anti-cheat, one the player turned off, or one that has it, needs nothing or can't run it.
/// The names are the adapters' own, as Windows lists them (Exo strips "(R)" and "(TM)"; both forms are here).
/// </summary>
public sealed class FrameGenEverywhereTests
{
    [Theory]
    // RTX 20 and 30: DLSSG SM86 where Exo offers it, OptiScaler everywhere else.
    [InlineData("NVIDIA GeForce RTX 2060", "nvidia", 20, "rtx")]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", "nvidia", 20, "rtx")]
    [InlineData("NVIDIA GeForce RTX 3070", "nvidia", 30, "rtx")]
    [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU", "nvidia", 30, "rtx")]
    [InlineData("NVIDIA GeForce RTX 3050", "nvidia", 30, "rtx")]
    [InlineData("NVIDIA RTX A4000", "nvidia", 30, "rtx")]
    // GTX 16, 10 and 900: OptiScaler, on the first-session rule.
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", "nvidia", 16, "gtx16")]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", "nvidia", 16, "gtx16")]
    [InlineData("NVIDIA GeForce GTX 1650", "nvidia", 16, "gtx16")]
    [InlineData("NVIDIA GeForce GTX 1650 Ti with Max-Q Design", "nvidia", 16, "gtx16")]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", "nvidia", 10, "gtx10")]
    [InlineData("NVIDIA GeForce GTX 1070", "nvidia", 10, "gtx10")]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", "nvidia", 10, "gtx10")]
    [InlineData("NVIDIA GeForce GTX 1050 Ti", "nvidia", 10, "gtx10")]
    [InlineData("NVIDIA TITAN Xp", "nvidia", 10, "gtx10")]
    [InlineData("NVIDIA GeForce GTX 980 Ti", "nvidia", 9, "gtx900")]
    [InlineData("NVIDIA GeForce GTX 970", "nvidia", 9, "gtx900")]
    [InlineData("NVIDIA GeForce GTX 960", "nvidia", 9, "gtx900")]
    [InlineData("NVIDIA GeForce GTX 950", "nvidia", 9, "gtx900")]
    // Radeon RX 5000 and newer, as today.
    [InlineData("AMD Radeon RX 5700 XT", "amd", 0, "radeon")]
    [InlineData("AMD Radeon RX 5500 XT", "amd", 0, "radeon")]
    [InlineData("AMD Radeon RX 6600", "amd", 0, "radeon")]
    [InlineData("AMD Radeon RX 7900 XTX", "amd", 0, "radeon")]
    [InlineData("AMD Radeon RX 7800 XT", "amd", 0, "radeon")]
    [InlineData("AMD Radeon RX 7600M XT", "amd", 0, "radeon")]
    [InlineData("AMD Radeon RX 9070 XT", "amd", 0, "radeon")]
    [InlineData("AMD Radeon AI PRO R9700", "amd", 0, "radeon")]
    // RX 400 and 500, Vega cards: OptiScaler, on the first-session rule.
    [InlineData("Radeon RX 580 Series", "amd", 0, "polaris")]
    [InlineData("AMD Radeon RX 580 2048SP", "amd", 0, "polaris")]
    [InlineData("AMD Radeon (TM) RX 480 Graphics", "amd", 0, "polaris")]
    [InlineData("AMD Radeon RX 570", "amd", 0, "polaris")]
    [InlineData("AMD Radeon RX 560X", "amd", 0, "polaris")]
    [InlineData("AMD Radeon RX 550", "amd", 0, "polaris")]
    [InlineData("AMD Radeon RX 590 GME", "amd", 0, "polaris")]
    [InlineData("Radeon RX Vega 64", "amd", 0, "vega")]
    [InlineData("AMD Radeon RX Vega 56", "amd", 0, "vega")]
    [InlineData("AMD Radeon VII", "amd", 0, "vega")]
    // Radeon graphics in laptops and handhelds.
    [InlineData("AMD Radeon 610M", "amd", 0, "integrated")]
    [InlineData("AMD Radeon 680M", "amd", 0, "integrated")]
    [InlineData("AMD Radeon 660M", "amd", 0, "integrated")]
    [InlineData("AMD Radeon 780M Graphics", "amd", 0, "integrated")]
    [InlineData("AMD Radeon(TM) 760M Graphics", "amd", 0, "integrated")]
    [InlineData("AMD Radeon 890M Graphics", "amd", 0, "integrated")]
    [InlineData("AMD Radeon 880M", "amd", 0, "integrated")]
    [InlineData("AMD Radeon 8060S Graphics", "amd", 0, "integrated")]
    [InlineData("AMD Radeon 8040S Graphics", "amd", 0, "integrated")]
    // A handheld lists "AMD Radeon Graphics"; Exo reads it with its processor's name (SystemSpecs.FrameGenName).
    [InlineData("AMD Radeon Graphics (AMD Ryzen Z1 Extreme)", "amd", 0, "integrated")]
    [InlineData("AMD Radeon Graphics (AMD Ryzen Z2 Go)", "amd", 0, "integrated")]
    [InlineData("AMD Radeon Graphics (AMD Ryzen AI 9 HX 370 w/ Radeon 890M)", "amd", 0, "integrated")]
    // Intel Arc cards, and Arc graphics inside the processor.
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "intel", 0, "arc")]
    [InlineData("Intel(R) Arc(TM) A380 Graphics", "intel", 0, "arc")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "intel", 0, "arc")]
    [InlineData("Intel Arc B570 Graphics", "intel", 0, "arc")]
    [InlineData("Intel(R) Arc(TM) Graphics", "intel", 0, "arc-integrated")]
    [InlineData("Intel Arc Graphics", "intel", 0, "arc-integrated")]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", "intel", 0, "arc-integrated")]
    [InlineData("Intel(R) Arc(TM) 130V GPU (8GB)", "intel", 0, "arc-integrated")]
    // Laptops' Arc A-series and Arrow Lake H's Arc graphics.
    [InlineData("Intel(R) Arc(TM) A370M Graphics", "intel", 0, "arc")]
    [InlineData("Intel(R) Arc(TM) A550M Graphics", "intel", 0, "arc")]
    [InlineData("Intel Arc A770M Graphics", "intel", 0, "arc")]
    [InlineData("Intel(R) Arc(TM) 140T GPU (16GB)", "intel", 0, "arc-integrated")]
    [InlineData("Intel(R) Arc(TM) 130T GPU (16GB)", "intel", 0, "arc-integrated")]
    public void CardsAModHelpsGetIt(string gpu, string vendor, int series, string kind)
    {
        Assert.Equal(new FrameGenCard(vendor, series, kind), FrameGenCardFor(gpu));
        Assert.Equal(new FrameGenCard(vendor, series, kind), FrameGenEverywhereCard(gpu));
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 750 Ti")]
    [InlineData("NVIDIA GeForce GTX 680")]
    [InlineData("NVIDIA GeForce MX450")]
    [InlineData("NVIDIA GeForce GT 1030")]
    [InlineData("AMD Radeon R9 390 Series")]
    [InlineData("AMD Radeon HD 7970")]
    [InlineData("AMD Radeon R7 Graphics")]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("AMD Radeon Graphics")]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics")]
    [InlineData("AMD Radeon Vega 3 Graphics")]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics")]
    [InlineData("AMD Radeon RX Vega 10 Graphics")]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Intel(R) UHD Graphics 620")]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("Intel(R) HD Graphics 530")]
    [InlineData("Intel(R) Arc(TM) Pro A60 Graphics")]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("")]
    [InlineData(null)]
    public void CardsItLeavesAlone(string? gpu) => Assert.Null(FrameGenEverywhereCard(gpu));

    /// <summary>Neighbours by name are read as what they are, never as each other.</summary>
    [Theory]
    [InlineData("AMD Radeon RX 580", "amd", 0, "polaris")]
    [InlineData("NVIDIA GeForce RTX 5080", "nvidia", 50, "rtx")]
    [InlineData("AMD Radeon RX 5500", "amd", 0, "radeon")]
    [InlineData("NVIDIA GeForce GTX 1650", "nvidia", 16, "gtx16")]
    [InlineData("NVIDIA GeForce RTX 3050 6GB Laptop GPU", "nvidia", 30, "rtx")]
    [InlineData("Intel(R) Arc(TM) Graphics", "intel", 0, "arc-integrated")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "intel", 0, "arc")]
    [InlineData("AMD Radeon RX Vega 64", "amd", 0, "vega")]
    [InlineData("AMD Radeon 780M Graphics", "amd", 0, "integrated")]
    [InlineData("AMD Radeon RX 7800 XT", "amd", 0, "radeon")]
    public void NeighboursAreNotConfused(string gpu, string vendor, int series, string kind) =>
        Assert.Equal(new FrameGenCard(vendor, series, kind), FrameGenCardFor(gpu));

    /// <summary>
    /// A ROG Ally's Z1 Extreme lists only "AMD Radeon(TM) Graphics", as a desktop Ryzen 7000's two-unit Radeon does: the
    /// processor's name tells them apart, and only for that bare name.
    /// </summary>
    [Theory]
    [InlineData("AMD Radeon Graphics", "AMD Ryzen Z1 Extreme", true)]
    [InlineData("AMD Radeon(TM) Graphics", "AMD Ryzen Z1", true)]
    [InlineData("AMD Radeon Graphics", "AMD Ryzen Z2 Extreme", true)]
    [InlineData("AMD Radeon Graphics", "AMD Ryzen AI 7 350 w/ Radeon 860M", true)]
    [InlineData("AMD Radeon Graphics", "AMD Ryzen 7 7700X", false)]
    [InlineData("AMD Radeon Graphics", "AMD Ryzen 5 5600G with Radeon Graphics", false)]
    [InlineData("AMD Radeon Graphics", null, false)]
    [InlineData("NVIDIA GeForce RTX 4070", "AMD Ryzen Z1 Extreme", true)]
    public void AHandheldsRadeonIsKnownByItsProcessor(string gpu, string? cpu, bool helped)
    {
        Assert.Equal(helped, FrameGenEverywhereCard(ExoLauncher.Helpers.SystemSpecs.FrameGenName(gpu, cpu)) is not null);
        if (!gpu.Contains("Radeon", StringComparison.Ordinal) || cpu is null) return;
        Assert.Equal(helped ? "integrated" : null, FrameGenCardFor(ExoLauncher.Helpers.SystemSpecs.FrameGenName(gpu, cpu))?.Class);
    }

    [Fact]
    public void VegaInsideAProcessorIsNotAnRxVega()
    {
        Assert.Null(FrameGenCardFor("AMD Radeon(TM) Vega 8 Graphics"));
        Assert.Null(FrameGenCardFor("AMD Radeon(TM) RX Vega 11 Graphics"));
        Assert.Equal("vega", FrameGenCardFor("AMD Radeon RX Vega 64")?.Class);
        Assert.Equal("vega", FrameGenCardFor("Radeon RX Vega")?.Class);
    }

    [Fact]
    public void OnlyRtx20And30RunDlssgSm86()
    {
        Assert.True(FrameGenCardFor("NVIDIA GeForce RTX 2060")!.Dlssg);
        Assert.True(FrameGenCardFor("NVIDIA GeForce RTX 3090")!.Dlssg);
        Assert.False(FrameGenCardFor("NVIDIA GeForce GTX 1660 SUPER")!.Dlssg);
        Assert.False(FrameGenCardFor("NVIDIA GeForce GTX 1080")!.Dlssg);
        Assert.False(FrameGenCardFor("NVIDIA GeForce RTX 4070")!.Dlssg);
        Assert.False(FrameGenCardFor("AMD Radeon RX 6600")!.Dlssg);
    }

    // Which games it goes into is the state table's last row (FrameGenStateTableTests), read end to end from fixture
    // folders in FrameGenModTests: BestSettingsLeavesABrokenInstallAlone, RemovedFilesGoBackOnceAndTheSecondTimeTheGameStaysOff
    // and AntiCheatIsRefusedOnEveryRouteAndCard.
}
