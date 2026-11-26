using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Keys = ExoLauncher.Helpers.AmdAdlx.Keys;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Radeon profile could go wrong silently, run against a fake ADLX driver:
/// - a setting the driver lacks (an older ADLX without IADLX3DSettingsServices3's FSR override or
///   …1's Fluid Motion Frames, or a GPU that doesn't support it) is written, so the whole apply fails;
/// - Chill is turned off after Anti-Lag (Chill turns Anti-Lag off), Image Sharpening before Super Resolution
///   (which holds it on some GPUs), V-Sync's mode before Enhanced Sync (which turns V-Sync off), or the frame rate
///   target is enabled before its fps is set;
/// - Image Sharpening is left on, or V-Sync is forced on or off for every game instead of left to each game (Fluid
///   Motion Frames needs it off in the game);
/// - the frame cap is not NVIDIA's G-SYNC cap with FreeSync, stays on without FreeSync, or falls
///   outside the driver's range; FreeSync is written for any screen;
/// - FSR 4 for FSR 3.1 games goes on for a GPU the driver can't run it on (RX 5000, RX 6000 before
///   26.10.1, an APU), or stays off on RX 9000/7000;
/// - Fluid Motion Frames is written by the profile (it follows each game Exo launches);
/// - Undo can't get back to the start: the backup is taken after writing, a re-apply overwrites it,
///   a write that fails part way loses it, or it holds a launch's temporary switch;
/// - "Applied" shows while the driver holds something else, or a running game's switch reads as changed;
/// - a game's Fluid Motion Frames is not set to Exo's decision before launch (on, or off when the player left it on
///   in AMD Software), not put back once no game runs, put back while another game still runs, put back to the
///   wrong value when two games overlap, or never put back after Exo stops mid-game; the list of games switched on
///   by hand before 3.17.2 outlives the update;
/// - Anti-Lag is left off, switched at a launch, or a profile saved with the old "while you play" or "off"
///   choice keeps it after the update (the user chose always on);
/// - a PC without a Radeon, or with ADLX not starting, gets a write.
/// </summary>
public sealed class RadeonProfileTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestDataDirectory.Root, "radeon-" + Guid.NewGuid().ToString("N"));
    private static readonly GpuProfile.Screen Screen = new("LG ULTRAGEAR", 144, false);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>An AMD driver as ADLX shows it: the settings it has, what each Set did, and Sets that fail.</summary>
    private sealed class FakeDriver : IGpuDriver
    {
        internal Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);
        internal string Gpu { get; set; } = "AMD Radeon RX 7800 XT";
        internal string? Software { get; set; } = "26.10.1";
        internal bool Down { get; set; }
        internal HashSet<string> Refuses { get; } = [];
        internal List<KeyValuePair<string, int>> Written { get; } = [];

        public GpuDriverState? Read() => Down ? null : new GpuDriverState(Gpu, Software, new Dictionary<string, int>(Values), 30, 300);

        public string? Write(IReadOnlyList<KeyValuePair<string, int>> writes)
        {
            if (Down) return "AMD's driver did not answer.";
            foreach (var write in writes)
            {
                if (!Values.ContainsKey(write.Key) || Refuses.Contains(write.Key)) return $"AMD's driver did not take {write.Key}.";
                Values[write.Key] = write.Value;
                Written.Add(write);
            }
            return null;
        }

        /// <summary>A Radeon on a current driver with every setting Exo touches, set the way a player might have them.</summary>
        internal static FakeDriver Current() => new()
        {
            Values =
            {
                [Keys.AntiLag] = 1, [Keys.Chill] = 1, [Keys.Boost] = 1, [Keys.Rsr] = 1, [Keys.EnhancedSync] = 1,
                [Keys.Sharpening] = 1, [Keys.VSync] = 3,
                [Keys.Frtc] = 0, [Keys.FrtcFps] = 300, [Keys.Fsr] = 0, [Keys.Afmf] = 0,
                [Keys.FreeSync + "LG ULTRAGEAR"] = 0, [Keys.FreeSync + "DELL P2419H"] = 1,
            },
        };
    }

    private sealed class Pc(bool amd = true)
    {
        internal bool Amd { get; set; } = amd;
        internal bool Playing { get; set; }
        internal bool AmdSoftware { get; set; } = true;
    }

    private RadeonProfileService Service(FakeDriver driver, Pc? pc = null)
    {
        pc ??= new Pc();
        return new RadeonProfileService(driver, _folder, () => pc.Amd, () => pc.Playing, () => pc.AmdSoftware);
    }

    [Fact]
    public void ProfileWritesInTheDriversOrderAndOnlyWhatItHas()
    {
        var writes = GpuProfile.Radeon(FakeDriver.Current().Read()!);
        var keys = writes.Select(write => write.Key).ToList();
        Assert.True(keys.IndexOf(Keys.Chill) < keys.IndexOf(Keys.AntiLag));
        Assert.DoesNotContain(Keys.FrtcFps, keys);   // no cap from Exo
        Assert.DoesNotContain(Keys.Frtc, keys);
        Assert.True(keys.IndexOf(Keys.Rsr) < keys.IndexOf(Keys.Sharpening));
        Assert.True(keys.IndexOf(Keys.EnhancedSync) < keys.IndexOf(Keys.VSync));
        Assert.DoesNotContain(Keys.Afmf, keys);
        var values = writes.ToDictionary();
        Assert.Equal(0, values[Keys.Chill]);
        Assert.Equal(0, values[Keys.Boost]);
        Assert.Equal(0, values[Keys.Rsr]);
        Assert.Equal(0, values[Keys.EnhancedSync]);
        Assert.Equal(0, values[Keys.Sharpening]);
        Assert.Equal(GpuProfile.VSyncGameDecides, values[Keys.VSync]);   // WFVR_OFF_UNLESS_APP_SPECIFIES
        Assert.Equal(1, values[Keys.AntiLag]);   // always on: the user's choice
        Assert.DoesNotContain(keys, key => key.StartsWith(Keys.FreeSync, StringComparison.Ordinal));
        Assert.Equal(1, values[Keys.Fsr]);

        // An old driver with the base 3D settings only: nothing else is written.
        var old = new GpuDriverState("AMD Radeon RX 7800 XT", null, new Dictionary<string, int> { [Keys.Chill] = 1, [Keys.AntiLag] = 0 });
        Assert.Equal(new[] { Keys.Chill, Keys.AntiLag }, GpuProfile.Radeon(old).Select(write => write.Key));
        Assert.Equal(1, GpuProfile.Radeon(old).ToDictionary()[Keys.AntiLag]);
    }

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT", "26.3.1", true)]
    [InlineData("AMD Radeon RX 7800 XT", null, true)]
    [InlineData("AMD Radeon PRO W7900", "26.6.2", true)]
    [InlineData("AMD Radeon RX 6800 XT", "26.9.2", false)]
    [InlineData("AMD Radeon RX 6800 XT", "26.10.1", true)]
    [InlineData("AMD Radeon RX 6600", "27.1.1", true)]
    [InlineData("AMD Radeon RX 6600", null, false)]
    [InlineData("AMD Radeon RX 5700 XT", "26.10.1", false)]
    [InlineData("AMD Radeon(TM) 780M Graphics", "26.10.1", false)]
    public void FsrUpgradeOnlyWhereTheDriverRunsIt(string gpu, string? software, bool on) =>
        Assert.Equal(on, GpuProfile.FsrUpgrade(gpu, software));

    // GPUs whose names carry no family (APUs, Radeon AI PRO): from 26.10.1 the driver offering the setting decides; RX 5000 never.
    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics", "26.10.1", true, true)]
    [InlineData("AMD Radeon(TM) 890M Graphics", "26.10.1", false, false)]
    [InlineData("AMD Radeon AI PRO R9700", "26.10.1", true, true)]
    [InlineData("AMD Radeon AI PRO R9700", "26.9.2", true, false)]
    [InlineData("AMD Radeon RX 5700 XT", "26.10.1", true, false)]
    public void FsrUpgradeAsksTheDriverWhereTheNameCantSay(string gpu, string software, bool supported, bool on) =>
        Assert.Equal(on, GpuProfile.FsrUpgrade(gpu, software, supported));

    [Fact]
    public void ApplyBacksUpFirstAndUndoPutsBackEveryValue()
    {
        var driver = FakeDriver.Current();
        var start = new Dictionary<string, int>(driver.Values);
        var radeon = Service(driver);
        Assert.Equal("ready", radeon.Read(Screen)!.Status);

        var applied = radeon.Apply(Screen)!;
        Assert.True(applied.Ok);
        Assert.Equal("applied", applied.Status);
        Assert.True(applied.CanUndo);
        Assert.True(File.Exists(Path.Combine(_folder, "amd-profile.json")));

        // A re-apply keeps the first backup.
        driver.Values[Keys.Boost] = 1;
        Assert.Equal("applied", radeon.Apply(Screen)!.Status);

        var undone = radeon.Undo(Screen)!;
        Assert.True(undone.Ok);
        Assert.False(undone.CanUndo);
        Assert.Equal(start, driver.Values);
        Assert.False(File.Exists(Path.Combine(_folder, "amd-profile.json")));
        Assert.Equal("Nothing to undo.", radeon.Undo(Screen)!.Message);
    }

    [Fact]
    public void AWriteThatFailsPartWayKeepsTheBackup()
    {
        var driver = FakeDriver.Current();
        var start = new Dictionary<string, int>(driver.Values);
        driver.Refuses.Add(Keys.Fsr);
        var radeon = Service(driver);
        var applied = radeon.Apply(Screen)!;
        Assert.False(applied.Ok);
        Assert.NotNull(applied.Message);
        Assert.True(applied.CanUndo);
        Assert.Equal(0, driver.Values[Keys.Chill]);   // written before the failure

        driver.Refuses.Clear();
        Assert.True(radeon.Undo(Screen)!.Ok);
        Assert.Equal(start, driver.Values);
    }

    [Fact]
    public void StatusComparesWithTheDriverAndNamesADriverUpdate()
    {
        var driver = FakeDriver.Current();
        var radeon = Service(driver);
        radeon.Apply(Screen);
        driver.Values[Keys.Boost] = 1;
        var moved = radeon.Read(Screen)!;
        Assert.Equal("changed", moved.Status);
        Assert.False(moved.DriverUpdated);
        driver.Software = "26.11.1";
        Assert.True(radeon.Read(Screen)!.DriverUpdated);
    }

    [Fact]
    public void OlderAdlxWithoutTheNewerInterfacesStillApplies()
    {
        // IADLX3DSettingsServices3 (FSR override) and …1 (Fluid Motion Frames) missing: their keys never appear.
        var driver = FakeDriver.Current();
        driver.Values.Remove(Keys.Fsr);
        driver.Values.Remove(Keys.Afmf);
        var radeon = Service(driver);
        Assert.Equal("applied", radeon.Apply(Screen)!.Status);
        Assert.DoesNotContain(driver.Written, write => write.Key is Keys.Fsr or Keys.Afmf);
        Assert.DoesNotContain("FSR", radeon.Read(Screen)!.Summary);

        Assert.False(radeon.Afmf().Supported);
        driver.Written.Clear();
        radeon.GameLaunching("Game", _ => On);
        Assert.DoesNotContain(driver.Written, write => write.Key == Keys.Afmf);
    }

    private static readonly FluidMotionFrames.Decision On = new(true, "test"), Off = new(false, "test");

    [Fact]
    public void FluidMotionFramesFollowsExosDecisionAtLaunchAndGoesBackOnceNoGameRuns()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc();
        var radeon = Service(driver, pc);
        Assert.True(radeon.Afmf() is { Supported: true, Gpu: "AMD Radeon RX 7800 XT", AmdSoftware: true });

        pc.Playing = true;
        string? asked = null;
        radeon.GameLaunching("Fortnite", gpu => { asked = gpu; return Off; });
        Assert.Equal("AMD Radeon RX 7800 XT", asked);
        Assert.Empty(driver.Written);   // already off
        radeon.GameLaunching("Elden Ring", _ => On);
        Assert.Equal(1, driver.Values[Keys.Afmf]);

        // Another game still runs: nothing goes back yet.
        radeon.GameEnded();
        Assert.Equal(1, driver.Values[Keys.Afmf]);
        pc.Playing = false;
        radeon.GameEnded();
        Assert.Equal(0, driver.Values[Keys.Afmf]);

        // The player left it on in AMD Software: a competitive game still starts with it off, and gets it back after.
        driver.Values[Keys.Afmf] = 1;
        driver.Written.Clear();
        radeon.GameLaunching("Counter-Strike 2", _ => Off);
        Assert.Equal(0, driver.Values[Keys.Afmf]);
        radeon.GameEnded();
        Assert.Equal(1, driver.Values[Keys.Afmf]);
        // A game it suits finds it on already: nothing is switched or put back.
        driver.Written.Clear();
        radeon.GameLaunching("Elden Ring", _ => On);
        radeon.GameEnded();
        Assert.Empty(driver.Written);
    }

    [Fact]
    public void TheListOfGamesSwitchedOnByHandGoesAtStart()
    {
        var driver = FakeDriver.Current();
        Directory.CreateDirectory(_folder);
        var games = Path.Combine(_folder, "amd-games.json");
        File.WriteAllText(games, "{\"afmf\":[\"steam:1\",\"epic:fortnite\"],\"restore\":{}}");
        Service(driver).AtStart(() => Screen);
        Assert.DoesNotContain("steam:1", File.ReadAllText(games));
        Assert.Contains("restore", File.ReadAllText(games));
    }

    [Fact]
    public void ALaunchSwitchIsPutBackAfterExoStopsMidGame()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc { Playing = true };
        var radeon = Service(driver, pc);
        radeon.GameLaunching("Game", _ => On);
        Assert.Equal(1, driver.Values[Keys.Afmf]);

        // Exo stopped mid-game; the next start (no game running) puts it back.
        pc.Playing = false;
        Service(driver, pc).GameEnded();
        Assert.Equal(0, driver.Values[Keys.Afmf]);
    }

    [Fact]
    public void AntiLagIsOnAlwaysAndNoLaunchSwitchesIt()
    {
        var driver = FakeDriver.Current();
        driver.Values[Keys.AntiLag] = 0;
        var pc = new Pc();
        var radeon = Service(driver, pc);
        radeon.Apply(Screen);
        Assert.Equal(1, driver.Values[Keys.AntiLag]);

        driver.Written.Clear();
        pc.Playing = true;
        radeon.GameLaunching("Game", _ => Off);
        pc.Playing = false;
        radeon.GameEnded();
        Assert.Empty(driver.Written);
        Assert.Equal(1, driver.Values[Keys.AntiLag]);
        // Undo still gives the player's own Anti-Lag back.
        Assert.True(radeon.Undo(Screen)!.Ok);
        Assert.Equal(0, driver.Values[Keys.AntiLag]);
    }

    [Theory]
    [InlineData("games")]
    [InlineData("off")]
    public void AProfileSavedWithTheOldAntiLagChoiceBecomesAlwaysOnAtStart(string choice)
    {
        // Saved by 3.17.1: Anti-Lag was on before Exo (the backup's 1), and the profile kept it off; a game was running
        // when Exo stopped, so a launch's switch waits to be put back too.
        var driver = FakeDriver.Current();
        driver.Values[Keys.AntiLag] = 0;
        var pc = new Pc();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "amd-profile.json"), "{\"software\":\"26.10.1\",\"antiLag\":\"" + choice + "\",\"before\":{\"antiLag\":1,\"chill\":1}}");
        File.WriteAllText(Path.Combine(_folder, "amd-games.json"), "{\"afmf\":[],\"restore\":{\"antiLag\":0}}");

        Service(driver, pc).AtStart(() => Screen);
        Assert.Equal(1, driver.Values[Keys.AntiLag]);
        Assert.DoesNotContain("\"antiLag\":\"", File.ReadAllText(Path.Combine(_folder, "amd-profile.json")));
        Assert.Equal("applied", Service(driver, pc).Read(Screen)!.Status);

        // Once is enough: the next start writes nothing.
        driver.Written.Clear();
        Service(driver, pc).AtStart(() => Screen);
        Assert.Empty(driver.Written);
        // Undo goes back to the player's own values from before Exo's first apply.
        Assert.True(Service(driver, pc).Undo(Screen)!.Ok);
        Assert.Equal(1, driver.Values[Keys.AntiLag]);
        Assert.Equal(1, driver.Values[Keys.Chill]);
    }

    [Fact]
    public void NoRadeonOrNoAdlxMeansNoWrite()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc(amd: false);
        var radeon = Service(driver, pc);
        radeon.GameLaunching("Game", _ => On);
        Assert.Empty(driver.Written);
        Assert.False(radeon.Afmf().Supported);

        driver.Down = true;
        pc.Amd = true;
        Assert.Null(radeon.Read(Screen));
        Assert.Null(radeon.Apply(Screen));
        Assert.False(File.Exists(Path.Combine(_folder, "amd-profile.json")));
    }

    [Fact]
    public void AFrameRateTargetExoSetGoesBackAtStartAndIsNotLeftInTheBackup()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc();
        var radeon = Service(driver, pc);
        radeon.Apply(Screen);
        var backup = Path.Combine(_folder, "amd-profile.json");
        // As an earlier version left it: its cap in the driver, what the target held before in the backup.
        var before = File.ReadAllText(backup).Replace("\"before\":{", "\"before\":{\"frtc\":0,\"frtcFps\":300,", StringComparison.Ordinal);
        File.WriteAllText(backup, before);
        driver.Values[Keys.Frtc] = 1;
        driver.Values[Keys.FrtcFps] = 157;

        radeon.AtStart(() => Screen);
        Assert.Equal((0, 300), (driver.Values[Keys.Frtc], driver.Values[Keys.FrtcFps]));
        Assert.DoesNotContain("frtc", File.ReadAllText(backup), StringComparison.Ordinal);

        // The player's own target from now on is theirs: another start and an apply leave it.
        driver.Values[Keys.Frtc] = 1;
        driver.Values[Keys.FrtcFps] = 120;
        radeon.AtStart(() => Screen);
        radeon.Apply(Screen);
        Assert.Equal((1, 120), (driver.Values[Keys.Frtc], driver.Values[Keys.FrtcFps]));
    }

    [Fact]
    public void AnApplyAlsoTakesBackAFrameRateTargetExoSet()
    {
        var driver = FakeDriver.Current();
        var radeon = Service(driver, new Pc());
        radeon.Apply(Screen);
        var backup = Path.Combine(_folder, "amd-profile.json");
        File.WriteAllText(backup, File.ReadAllText(backup).Replace("\"before\":{", "\"before\":{\"frtc\":0,\"frtcFps\":300,", StringComparison.Ordinal));
        driver.Values[Keys.Frtc] = 1;
        driver.Values[Keys.FrtcFps] = 157;
        radeon.Apply(Screen);
        Assert.Equal((0, 300), (driver.Values[Keys.Frtc], driver.Values[Keys.FrtcFps]));
    }

    [Fact]
    public void SummarySaysWhatTheProfileDoes()
    {
        var writes = GpuProfile.Radeon(FakeDriver.Current().Read()!);
        Assert.Equal("FSR 4 for FSR 3.1 games, Anti-Lag on, V-Sync as each game sets it; "
            + "Chill, Boost, Super Resolution, Image Sharpening and Enhanced Sync off",
            GpuProfile.RadeonSummary(writes));
    }

    [Theory]
    [InlineData(0, "Always off")]
    [InlineData(1, "Off, unless application specifies")]
    [InlineData(2, "On, unless application specifies")]
    [InlineData(3, "Always on")]
    [InlineData(4, null)]
    public void VSyncModesReadAsAmdSoftwareNamesThem(int mode, string? words) => Assert.Equal(words, GpuProfile.VSyncMode(mode));
}
