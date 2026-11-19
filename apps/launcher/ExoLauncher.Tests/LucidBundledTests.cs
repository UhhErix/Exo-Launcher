using System.Text.Json.Nodes;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Which Lucid Exo uses now that one ships inside it, and how that one takes over from a separate copy.
/// Ways it could fail: an older separate copy keeps running beside the copy in Exo, or a newer one is replaced by it and
/// its settings go backwards; a copy the user runs on their own is taken over before they turn Start with Exo on; the
/// same file reached with other capitals counts as a separate copy; a copy with no readable version wins; a build with
/// no Lucid inside loses the copy it could use; taking over leaves Lucid starting at sign-in from the old copy, or
/// changes anything else in its settings; a take-over waiting for a game is lost when Exo closes; Apps > Startup tells
/// the user to keep a sign-in entry for the copy inside Exo (it would start Lucid, tray icon and all, before Exo is open),
/// or to remove a separate copy's, which is the user's own.
/// </summary>
public sealed class LucidBundledTests
{
    private const string InExo = @"C:\Exo\app\addons\lucid\Lucid.exe";
    private const string Theirs = @"C:\Users\me\Documents\Lucid.exe";

    private static Version? Versions(string exe, string theirs) => exe == InExo ? new Version(6, 0, 4, 0) : theirs.Length == 0 ? null : Version.Parse(theirs);

    [Theory]
    // Nothing else here: the copy in Exo.
    [InlineData(null, null, true, "6.0.4.0", InExo)]
    [InlineData(null, null, false, "6.0.4.0", InExo)]
    // Running, with Start with Exo on: it gives way unless it's newer.
    [InlineData(Theirs, null, true, "6.0.3.0", InExo)]
    [InlineData(Theirs, null, true, "6.0.4.0", InExo)]
    [InlineData(Theirs, null, true, "6.0.5.0", Theirs)]
    // Running on its own, with Start with Exo off: it stays the user's.
    [InlineData(Theirs, null, false, "6.0.3.0", Theirs)]
    // Not running: only a newer one is worth starting.
    [InlineData(null, Theirs, true, "6.0.3.0", InExo)]
    [InlineData(null, Theirs, true, "6.0.5.0", Theirs)]
    // A version that can't be read never beats a known one.
    [InlineData(Theirs, null, true, "", InExo)]
    // The copy in Exo itself, however it's spelled.
    [InlineData(@"c:\exo\APP\addons\lucid\lucid.exe", InExo, false, "6.0.4.0", InExo)]
    public void The_copy_in_Exo_is_used_unless_a_separate_one_has_a_claim(string? running, string? remembered, bool startWithExo, string theirs, string expected) =>
        Assert.Equal(expected, LucidCompanion.Choose(InExo, running, remembered, startWithExo, exe => Versions(exe, theirs)));

    [Fact]
    public void A_build_without_Lucid_inside_uses_what_it_can_find()
    {
        Assert.Equal(Theirs, LucidCompanion.Choose(null, Theirs, null, true, _ => null));
        Assert.Equal(Theirs, LucidCompanion.Choose(null, null, Theirs, true, _ => null));
        Assert.Null(LucidCompanion.Choose(null, null, null, true, _ => null));
    }

    [Fact]
    public void Taking_over_ends_only_the_start_at_sign_in()
    {
        var root = new JsonObject { ["RunAtStartup"] = true, ["SelectedHeadphoneId"] = "ziigaat_lush", ["GlobalGameEqStrength"] = 80, ["LucidVoiceEnabled"] = true };
        var before = root.DeepClone().AsObject();

        Assert.Null(LucidSettings.Apply(root, new LucidSettings.Change { TakeOver = true }, _ => null, () => null));

        Assert.False(root["RunAtStartup"]!.GetValue<bool>());
        before["RunAtStartup"] = false;
        Assert.True(JsonNode.DeepEquals(before, root));
    }

    [Fact]
    public void A_change_without_it_leaves_the_start_at_sign_in_alone()
    {
        var root = new JsonObject { ["RunAtStartup"] = true };
        Assert.Null(LucidSettings.Apply(root, new LucidSettings.Change { Notifications = false }, _ => null, () => null));
        Assert.True(root["RunAtStartup"]!.GetValue<bool>());
    }

    [Fact]
    public void A_take_over_waiting_for_a_game_outlasts_Exo_and_other_changes()
    {
        var waiting = new LucidSettings.Change { TakeOver = true }.Then(new LucidSettings.Change { Notifications = false });
        Assert.True(waiting.TakeOver);
        Assert.True(LucidSettings.FromStored(LucidSettings.Stored(waiting))!.TakeOver);
        Assert.False(LucidSettings.FromStored(LucidSettings.Stored(new LucidSettings.Change { Notifications = false }))!.TakeOver);
    }

    [Theory]
    [InlineData(@"c:\users\me\appdata\local\exolauncher\app\addons\lucid\lucid.exe", "remove")]
    [InlineData(@"c:\users\me\documents\lucid.exe", "keep")]
    [InlineData(@"c:\users\me\appdata\local\exolauncher\app\addons\lucid2\lucid.exe", "keep")]
    public void A_sign_in_entry_is_removed_for_the_copy_inside_Exo_and_kept_for_the_users_own(string path, string verdict) =>
        Assert.Equal(verdict, StartupCatalog.Find("Lucid", ["lucid.exe"], path)?.Verdict);
}
