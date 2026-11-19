using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could say more than the driver took:
/// - a rule whose id the driver does not list is dropped before the write and recorded nowhere, so the profile reads
///   applied while a feature the page names (low latency, the DLSS presets) was never written;
/// - a skipped id is compared with the driver's value and read as applied;
/// - a setting the driver refuses at the write is logged and carried nowhere, so nothing remembers it was not written;
/// - the settings one driver skipped are still said of the next one, which may list and take them.
/// Pure, with a fake driver answer: no driver on this PC is touched.
/// </summary>
public sealed class GpuProfileTruthTests
{
    // The ids the RTX 3070's profile needs, as GpuProfileTests lists them, on a driver that lists every one.
    private static readonly uint[] Every =
    [
        390467, 983226, 983227, 983295, 1675263, 2166816, 3066610, 5912412, 6505105, 6600001, 6710836, 6710885, 6714153, 6776373,
        6776937, 8102046, 8703344, 9963649, 9963670, 10011052, 11041231, 11250721, 11250737, 11306135, 12991097, 13510289,
        14019014, 14019015, 14366808, 15151633, 15389065, 28027939, 269553971, 269573257, 270198627, 270426537, 271895433,
        271965065, 272909380, 273507943, 274197361, 274606621, 276089202, 276158834, 276495451, 276652957, 276757595,
        277041150, 277041152, 277041154, 277041157, 277041158, 277041162, 277041199, 278196567, 278196727, 278262127,
        279476652, 279476686, 279476687, 279951208, 280859683, 281106605, 281531554, 282245910, 282364549, 282555346,
        282663500, 283385329, 283385331, 283385333, 283385334, 283385335, 283385345, 283385346, 283385347, 283385348,
        283958146, 283962569, 284962204, 286335539, 286335574, 294973784, 538927519, 539527361, 540508738, 543266006,
        543959236, 544392611, 545898348, 547222078, 549528094, 550932728, 553505273, 1343646814, 2966161525,
        0xB0D384C0, 0x10308298, 0x000BFA21, GpuProfile.ModelPresetProfile, 0x10E41E05, 0x10E41E06,
    ];

    private const uint LowLatency = 0x0005F543, UltraLowLatency = 0x10835000, PreRendered = 0x007BA09E;

    private static HashSet<uint> Listed() => [.. Every];

    private static GpuProfile.Plan Plan(int series, IReadOnlySet<uint>? known = null, long freeGb = 500) =>
        GpuProfile.NvidiaPlan(new GpuProfile.Variant(series), freeGb, known ?? Listed());

    [Fact]
    public void ARuleTheDriverDoesNotListIsSkippedNotApplied()
    {
        var known = Listed();
        known.Remove(LowLatency);
        var plan = Plan(30, known);

        // Never written, and named as skipped: the profile knows what it did not do.
        Assert.False(plan.Writes.ContainsKey(LowLatency));
        Assert.Contains(LowLatency, plan.Skipped);
        // Every write is an id this driver lists; every skipped id is one it does not.
        Assert.All(plan.Writes.Keys, id => Assert.Contains(id, known));
        Assert.All(plan.Skipped, id => Assert.DoesNotContain(id, known));
        // The one write made outside the rules (the shader cache) is split the same way.
        Assert.Contains(GpuProfile.ShaderCacheSize, plan.Writes.Keys);
    }

    [Fact]
    public void NeverWritesAnIdTheDriverDoesNotList()
    {
        // The check GpuProfileTests makes, now on the split plan: it must still hold.
        var known = Listed();
        known.Remove(GpuProfile.FrameRateLimit);
        known.Remove(GpuProfile.ShaderCacheSize);
        known.Remove(0x10E41DF1);
        var plan = Plan(40, known);

        Assert.All(plan.Writes.Keys, id => Assert.Contains(id, known));
        Assert.Contains(GpuProfile.FrameRateLimit, plan.Skipped);
        Assert.Contains(GpuProfile.ShaderCacheSize, plan.Skipped);
        Assert.Contains(0x10E41DF1u, plan.Skipped);
    }

    [Fact]
    public void ASkippedIdIsNeverReportedApplied()
    {
        var known = Listed();
        known.Remove(LowLatency);
        var plan = Plan(30, known);
        var held = new Dictionary<uint, uint?>(plan.Writes) { [LowLatency] = 1u };

        // The skipped id is in no write, so no comparison of the writes can say it holds...
        Assert.False(plan.Writes.ContainsKey(LowLatency));
        Assert.Equal("applied", GpuProfile.Status(plan.Writes, held, applied: true));
        // ...and the page is never told the setting behind it is on.
        Assert.Equal(GpuProfile.FeatureUnavailable,
            GpuProfile.Features(plan.Writes, new Dictionary<uint, uint?>(plan.Writes), plan.Skipped).Single(feature => feature.Key == "lowLatency").State);

        // A setting this driver took and holds another value for is off, whatever it skipped: the feature is not as Exo set it.
        Assert.Equal(GpuProfile.FeatureOff,
            GpuProfile.Features(plan.Writes, new Dictionary<uint, uint?>(plan.Writes) { [PreRendered] = 0u }, plan.Skipped)
                .Single(feature => feature.Key == "lowLatency").State);

        // Nothing skipped, and the driver holds another value: that is off, not unavailable.
        var full = Plan(30);
        Assert.Equal(GpuProfile.FeatureOff,
            GpuProfile.Features(full.Writes, new Dictionary<uint, uint?>(full.Writes) { [LowLatency] = 0u }, full.Skipped)
                .Single(feature => feature.Key == "lowLatency").State);
    }

    [Fact]
    public void LowLatencyIsNotShownOnWhenItsSettingsWereSkipped()
    {
        // On 617.42 and 621.01 the driver lists neither the CPL state nor Ultra: only max pre-rendered frames is written.
        var known = Listed();
        known.Remove(LowLatency);
        known.Remove(UltraLowLatency);
        var plan = Plan(30, known);
        Assert.Contains(LowLatency, plan.Skipped);
        Assert.Contains(UltraLowLatency, plan.Skipped);
        Assert.True(plan.Writes.ContainsKey(PreRendered));

        var lowLatency = GpuProfile.Features(plan.Writes, new Dictionary<uint, uint?>(plan.Writes), plan.Skipped)
            .Single(feature => feature.Key == "lowLatency");
        Assert.Equal("Low latency", lowLatency.Label);
        Assert.Equal(GpuProfile.FeatureUnavailable, lowLatency.State);
        // Not "off" either: the driver holds no value of Exo, so off would be a different, wrong thing to say.
        Assert.NotEqual(GpuProfile.FeatureOff, lowLatency.State);

        // On a driver that lists them, and holds what Exo set, it is on.
        var full = Plan(30);
        Assert.Equal(GpuProfile.FeatureOn, GpuProfile.Features(full.Writes, new Dictionary<uint, uint?>(full.Writes), full.Skipped)
            .Single(feature => feature.Key == "lowLatency").State);
        // A refusal keeps it out too: the id was handed to the driver and not taken.
        Assert.Equal(GpuProfile.FeatureUnavailable, GpuProfile.Features(full.Writes, new Dictionary<uint, uint?>(full.Writes), new uint[] { LowLatency })
            .Single(feature => feature.Key == "lowLatency").State);

        // A GTX card's DLSS is the driver's default everywhere: the feature is not named at all.
        Assert.DoesNotContain(GpuProfile.Features(Plan(16).Writes, new Dictionary<uint, uint?>(Plan(16).Writes), []), feature => feature.Key == "dlssPresets");
        // An RTX card is, and reads on when the driver holds every preset it wrote.
        Assert.Contains(GpuProfile.Features(full.Writes, new Dictionary<uint, uint?>(full.Writes), full.Skipped), feature => feature.Key == "dlssPresets");
    }

    [Fact]
    public void ARefusedIdIsCarriedOutOfTheWrite()
    {
        // The driver's per-id answers, faked: NVAPI_NOT_SUPPORTED (-104) for two ids, ok for the rest. The write names what it
        // refused instead of only logging it, and goes on past the refusals.
        List<KeyValuePair<uint, uint?>> changes = [new(PreRendered, 1u), new(LowLatency, 1u), new(UltraLowLatency, 0u), new(GpuProfile.ShaderCacheSize, null)];
        var called = new List<uint>();
        var (refused, _, failed) = NvidiaProfiles.ApplyChanges(changes, (id, _) =>
        {
            called.Add(id);
            return id is LowLatency or UltraLowLatency ? -104 : 0;
        });
        Assert.Equal([LowLatency, UltraLowLatency], refused);
        Assert.Null(failed);
        Assert.Equal(4, called.Count);

        // Nothing refused carries nothing; any other status stops the write at that id and says which.
        Assert.Empty(NvidiaProfiles.ApplyChanges(changes, (_, _) => 0).Refused);
        var (_, failedId, status) = NvidiaProfiles.ApplyChanges(changes, (id, _) => id == LowLatency ? -5 : 0);
        Assert.Equal(LowLatency, failedId);
        Assert.Equal(-5, status);

        // The state the page gets says "partly", and carries the ids to Keep; no count and no id reach the page.
        var partly = new GpuProfileService.State(true, null, "nvidia", "RTX 3070", 30, new GpuProfile.Screen("VG248", 165, false),
            "applied", true, false, [], Skipped: new uint[] { LowLatency }, Features: []);
        Assert.True(partly.Partly);
        Assert.Equal(new uint[] { LowLatency }, partly.Skipped);
        // Nothing skipped: nothing to say.
        var whole = new GpuProfileService.State(true, null, "nvidia", "RTX 3070", 30, new GpuProfile.Screen("VG248", 165, false),
            "applied", true, false, []);
        Assert.False(whole.Partly);
        Assert.Null(whole.Skipped);
    }

    [Fact]
    public void TheSkippedListIsForgottenWhenTheDriverChanges()
    {
        var pc = new Keep.KeepPc
        {
            Driver = "32.0.15.6094|31.0.101.4502",
            DriverSkipped = ["0x0005F543", "0x10835000"],
            DriverSkippedDriver = "32.0.15.6094|31.0.101.4502",
        };
        Assert.Equal(2, pc.DriverSkipped.Count);

        DriverKeep.ForgetSkipped(pc);

        Assert.Empty(pc.DriverSkipped);
        Assert.Null(pc.DriverSkippedDriver);
        // The identity itself is the look's to record: forgetting only says nothing is known of the new driver's ids.
        Assert.Equal("32.0.15.6094|31.0.101.4502", pc.Driver);
    }

    [Fact]
    public void AProfileWithARefusedIdStillReadsApplied()
    {
        var plan = Plan(30);
        // The driver took every id but one, which it refuses at the write and so never holds as Exo set it.
        var values = new Dictionary<uint, uint?>(plan.Writes) { [LowLatency] = 0u };
        var refused = new uint[] { LowLatency };

        // Counted as a write, the refused id made the profile read "changed" for ever: every put back "failed", and Exo's own
        // install said the profile was not put back.
        Assert.Equal("applied", GpuProfileService.StatusOf(plan.Writes, values, refused, applied: true));
        // A refused id does not hide a real change in what the driver did take.
        var moved = new Dictionary<uint, uint?>(values) { [PreRendered] = 0u };
        Assert.Equal("changed", GpuProfileService.StatusOf(plan.Writes, moved, refused, applied: true));
        Assert.Equal("ready", GpuProfileService.StatusOf(plan.Writes, moved, refused, applied: false));
        // Nothing refused: the read is as it was.
        Assert.Equal("changed", GpuProfileService.StatusOf(plan.Writes, values, [], applied: true));
    }
}
