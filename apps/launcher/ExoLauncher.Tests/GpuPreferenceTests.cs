using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways asking Windows for the fast graphics chip could harm or mislead:
/// - another setting kept in the same value (SwapEffectUpgradeEnable=1;) is lost when GpuPreference is written;
/// - Undo deletes a value that held something before, or leaves the one that held nothing;
/// - Undo overwrites a program the user set themselves after Exo, or one Exo never touched;
/// - a Riot game, an uninstaller, a crash reporter or an anti-cheat's program gets the setting;
/// - a program outside the game's folder (a launch path that wanders) gets it;
/// - the program that draws the game is missed where only a starter sits at the top: an Unreal Engine 4 or 5 game's
///   {Project}\Binaries\Win64, Phantom Blade Zero's platform\{store}; or Unreal's own Engine\Binaries helpers are taken;
/// - the record is lost with the process, so Undo has nothing to go back to.
/// </summary>
public sealed class GpuPreferenceTests : IDisposable
{
    private sealed class FakeStore : GpuPreference.IStore
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Writes of these programs are accepted and not kept: the value reads back as it was.</summary>
        public readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Writes of these programs are refused, as when the key is locked.</summary>
        public readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase);
        public int Writes { get; private set; }
        public string? Get(string program) => Values.GetValueOrDefault(program);

        public void Set(string program, string data)
        {
            Writes++;
            if (Refused.Contains(program)) throw new IOException("refused");
            if (!Ignored.Contains(program)) Values[program] = data;
        }

        public void Delete(string program)
        {
            Writes++;
            if (Refused.Contains(program)) throw new IOException("refused");
            if (!Ignored.Contains(program)) Values.Remove(program);
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-gpupref-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStore _store = new();
    private readonly WindowsKit.PcRecord _record = new();
    private readonly GpuPreference _pref;

    public GpuPreferenceTests()
    {
        Directory.CreateDirectory(_dir);
        _pref = new GpuPreference(_store, Path.Combine(_dir, "gpu-preference.json"), _record.Load, _record.Save);
    }

    public void Dispose() { try { DeletionGate.DeleteDirectory(_dir, true); } catch (IOException) { } }

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Theory]
    [InlineData(null, "GpuPreference=2;")]
    [InlineData("", "GpuPreference=2;")]
    [InlineData("GpuPreference=1;", "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;", "GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=0;", "GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    public void TheValueTakesTheChoiceAndKeepsTheRest(string? before, string after) => Assert.Equal(after, GpuPreference.With(before, 2));

    [Theory]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", 1)]
    [InlineData("SwapEffectUpgradeEnable=1;", null)]
    [InlineData(null, null)]
    public void TheChoiceIsReadFromTheValue(string? data, int? choice) => Assert.Equal(choice, GpuPreference.Preference(data));

    [Fact]
    public void UndoPutsEachProgramBackAsItWas()
    {
        var plain = Touch(@"game\a.exe");
        var tuned = Touch(@"game\b.exe");
        _store.Values[tuned] = "SwapEffectUpgradeEnable=1;GpuPreference=1;";

        Assert.Equal(2, _pref.Use([plain, tuned]));
        Assert.Equal("GpuPreference=2;", _store.Values[plain]);
        Assert.Equal("GpuPreference=2;SwapEffectUpgradeEnable=1;", _store.Values[tuned]);

        Assert.Equal(2, _pref.Undo());
        Assert.False(_store.Values.ContainsKey(plain));                                               // it had nothing: nothing now
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;", _store.Values[tuned]);               // it had a value: that value
    }

    [Fact]
    public void AProgramAlreadyOnTheFastChipIsNeitherChangedNorTakenOnByUndo()
    {
        var mine = Touch(@"game\a.exe");
        _store.Values[mine] = "GpuPreference=2;";
        Assert.Equal(0, _pref.Use([mine]));
        Assert.Equal(0, _pref.Undo());
        Assert.Equal("GpuPreference=2;", _store.Values[mine]);
    }

    [Fact]
    public void AProgramTheUserChangedAfterExoIsLeftAsTheyHaveIt()
    {
        var program = Touch(@"game\a.exe");
        _pref.Use([program]);
        _store.Values[program] = "GpuPreference=1;";
        Assert.Equal(0, _pref.Undo());
        Assert.Equal("GpuPreference=1;", _store.Values[program]);
    }

    [Fact]
    public void TheRecordSurvivesANewInstance()
    {
        var program = Touch(@"game\a.exe");
        _store.Values[program] = "GpuPreference=0;";
        _pref.Use([program]);
        var later = new GpuPreference(_store, Path.Combine(_dir, "gpu-preference.json"));
        Assert.Equal(1, later.Undo());
        Assert.Equal("GpuPreference=0;", _store.Values[program]);
    }

    [Fact]
    public void OnlyTheGamesOwnProgramsAreChosen()
    {
        var game = new GameEntry { Id = "steam:1", Title = "A Game", Store = StoreKind.Steam, Installed = true, Path = Path.Combine(_dir, "game"), LaunchTarget = "1" };
        var main = Touch(@"game\Game.exe");
        var shipping = Touch(@"game\Binaries\Win64\Game-Win64-Shipping.exe");
        Touch(@"game\unins000.exe");
        Touch(@"game\CrashReportClient.exe");
        Touch(@"game\vcredist_x64.exe");
        Touch(@"game\EasyAntiCheat\EasyAntiCheat_Setup.exe");
        Touch(@"game\easyanticheat_eos_setup.exe");
        Touch(@"game\start_protected_game.exe");
        Touch(@"game\Data\deep\Other.exe");   // not in a usual place

        var chosen = GpuPreference.ProgramsOf(game);
        Assert.Equal([main, shipping], chosen.OrderBy(path => path.Length));
    }

    [Fact]
    public void AnUnrealGamesOwnProgramIsChosenBesideItsStarter()
    {
        var game = new GameEntry { Id = "steam:2", Title = "An Unreal Game", Store = StoreKind.Steam, Installed = true, Path = Path.Combine(_dir, "ue5") };
        var starter = Touch(@"ue5\MyGame.exe");
        var shipping = Touch(@"ue5\MyGame\Binaries\Win64\MyGame-Win64-Shipping.exe");
        Touch(@"ue5\Engine\Binaries\Win64\EpicWebHelper.exe");
        Touch(@"ue5\MyGame\Binaries\Win64\CrashReportClient.exe");
        Touch(@"ue5\MyGame\Content\Movies\Other.exe");
        Assert.Equal([starter, shipping], GpuPreference.ProgramsOf(game).OrderBy(path => path.Length));
    }

    [Fact]
    public void PhantomBladeZerosGameIsChosenNotOnlyItsStarter()
    {
        var starter = Touch(@"PBZ\steam\PBZ.exe");
        var drawing = Touch(@"PBZ\platform\steam\PBZ.exe");
        var game = new GameEntry
        {
            Id = "steam:4115450", Title = "Phantom Blade Zero", Store = StoreKind.Steam, Installed = true, Path = Path.Combine(_dir, "PBZ"), LaunchTarget = starter,
        };
        Touch(@"PBZ\ProjectPBZ\Binaries\Win64\crashpad_handler.exe");
        Assert.Equal([starter, drawing], GpuPreference.ProgramsOf(game));
    }

    [Fact]
    public void ARiotGameIsNeverTouched()
    {
        var game = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot, Installed = true, Path = Path.Combine(_dir, "riot") };
        Touch(@"riot\VALORANT.exe");
        Assert.Empty(GpuPreference.ProgramsOf(game));
    }

    [Fact]
    public void ALaunchPathOutsideTheGamesFolderIsIgnored()
    {
        var outside = Touch(@"elsewhere\Evil.exe");
        var game = new GameEntry { Id = "local:x", Title = "X", Store = StoreKind.Local, Installed = true, Path = Path.Combine(_dir, "game"), LaunchTarget = outside };
        Directory.CreateDirectory(game.Path!);
        Assert.Empty(GpuPreference.ProgramsOf(game));
    }

    // ---- Read back: a write that did not take is not a success ----

    [Fact]
    public void AGpuPreferenceThatDidNotTakeIsNotCounted()
    {
        var takes = Touch(@"game\a.exe");
        var refuses = Touch(@"game\b.exe");
        _store.Ignored.Add(refuses); // Windows accepts the write and keeps the old value

        var used = _pref.UseReport([takes, refuses]);

        Assert.Equal(new GpuPreference.Used(Changed: 1, Failed: 1), used);
        Assert.Equal("GpuPreference=2;", _store.Values[takes]);
        Assert.False(_store.Values.ContainsKey(refuses));
        // The old count is the same count: only what took.
        _store.Values.Clear();
        Assert.Equal(1, _pref.Use([takes, refuses]));
    }

    [Fact]
    public void AWriteThatThrowsIsAFailureToo()
    {
        var locked = Touch(@"game\a.exe");
        _store.Refused.Add(locked);
        Assert.Equal(new GpuPreference.Used(0, 1), _pref.UseReport([locked]));
    }

    [Fact]
    public void TheBridgeSaysSoWhenNothingTook()
    {
        var first = Touch(@"game\a.exe");
        var second = Touch(@"game\b.exe");
        _store.Ignored.Add(first);
        Assert.Equal("Windows didn't take the setting.", _pref.UseAndSay([first]));

        // Nothing to write is not a failure; one that took is not either, whatever the others did.
        Assert.Null(_pref.UseAndSay([]));
        Assert.Null(_pref.UseAndSay([first, second]));
        Assert.Equal("GpuPreference=2;", _store.Values[second]);

        // Putting back: nothing taken back is said the same way, and the record stays so a later Undo can try again.
        _store.Ignored.Add(second);
        Assert.Equal("Windows didn't take the setting.", _pref.UndoAndSay());
        _store.Ignored.Clear();
        Assert.Null(_pref.UndoAndSay());
        Assert.False(_store.Values.ContainsKey(second));
        // Nothing left to put back is not a failure.
        Assert.Null(_pref.UndoAndSay());
    }

    // ---- "Optimizations for windowed games" ----

    private const string Global = "DirectXUserGlobalSettings";

    private (GpuPreference Pref, WindowsKit.PcRecord Record) WithRecord()
    {
        var record = new WindowsKit.PcRecord();
        return (new GpuPreference(_store, Path.Combine(_dir, "gpu-preference.json"), record.Load, record.Save), record);
    }

    [Theory]
    [InlineData(null)]                                 // never set: Undo deletes the value
    [InlineData("VRROptimizeEnable=0;")]               // other tokens a person set
    [InlineData("VRROptimizeEnable=1;AutoHDREnable=2097;")]
    public void TheWindowedGamesValueIsPutBackExactlyByUndo(string? before)
    {
        var (pref, record) = WithRecord();
        if (before is not null) _store.Values[Global] = before;

        pref.UseWindowedGames();

        Assert.Contains("SwapEffectUpgradeEnable=1", _store.Values[Global], StringComparison.Ordinal);
        Assert.NotNull(record.Pc.WindowedGames);
        Assert.Equal(before is null ? null : RegValue.Str(before), record.Pc.WindowedGames!.Before);

        Assert.True(pref.UndoWindowedGames(remember: false));

        Assert.Equal(before, _store.Values.GetValueOrDefault(Global));
        Assert.Null(record.Pc.WindowedGames); // forgotten
    }

    [Fact]
    public void ATokenAPersonSetToZeroIsNeitherWrittenNorUndone()
    {
        var (pref, record) = WithRecord();
        _store.Values[Global] = "SwapEffectUpgradeEnable=0;";

        pref.UseWindowedGames();

        Assert.Equal("SwapEffectUpgradeEnable=0;", _store.Values[Global]);
        Assert.Null(record.Pc.WindowedGames);
        Assert.True(pref.UndoWindowedGames(remember: false));
        Assert.Equal("SwapEffectUpgradeEnable=0;", _store.Values[Global]);
    }

    [Fact]
    public void WhatAPersonChangedInTheValueSinceIsLeftWhenWindowedGamesIsUndone()
    {
        var (pref, _) = WithRecord();
        _store.Values[Global] = "VRROptimizeEnable=0;";
        pref.UseWindowedGames();
        // The person turns Auto HDR on in Windows afterwards.
        _store.Values[Global] += "AutoHDREnable=2097;";

        Assert.True(pref.UndoWindowedGames(remember: false));

        Assert.Equal("VRROptimizeEnable=0;AutoHDREnable=2097;", _store.Values[Global]);
    }

    [Fact]
    public void UndoPutsTheWindowedGamesValueBackToo()
    {
        var (pref, _) = WithRecord();
        var program = Touch(@"game\a.exe");
        _store.Values[Global] = "VRROptimizeEnable=0;";
        pref.UseWindowedGames();
        pref.Use([program]);

        Assert.Equal(1, pref.Undo());

        Assert.Equal("VRROptimizeEnable=0;", _store.Values[Global]);
        Assert.False(_store.Values.ContainsKey(program));
    }

    [Fact]
    public void WindowedGamesUndoneByThePlayerIsNotWrittenAgainAtStart()
    {
        var (pref, record) = WithRecord();
        pref.UseWindowedGames();
        Assert.True(pref.UndoWindowedGames());
        Assert.False(_store.Values.ContainsKey(Global));

        pref.UseWindowedGames(); // Exo starts again

        Assert.False(_store.Values.ContainsKey(Global));
        Assert.True(record.Pc.WindowedGames!.Declined);
        // The uninstaller forgets the choice, so a reinstall starts over.
        Assert.True(pref.UndoWindowedGames(remember: false));
        Assert.Null(record.Pc.WindowedGames);
        pref.UseWindowedGames();
        Assert.Contains("SwapEffectUpgradeEnable=1", _store.Values[Global], StringComparison.Ordinal);
    }

    [Fact]
    public void TurningTheFastCardOnAgainStartsWindowedGamesOver()
    {
        var (pref, record) = WithRecord();
        var program = Touch(@"gamea.exe");
        pref.UseWindowedGames();
        Assert.True(pref.UndoWindowedGames());
        Assert.False(_store.Values.ContainsKey(Global));

        Assert.Null(pref.UseAndSay([program]));

        Assert.Contains("SwapEffectUpgradeEnable=1", _store.Values[Global], StringComparison.Ordinal);
        Assert.False(record.Pc.WindowedGames!.Declined);
    }

    [Fact]
    public void WindowedGamesWrittenTwiceKeepsTheFirstValueItReplaced()
    {
        var (pref, record) = WithRecord();
        _store.Values[Global] = "VRROptimizeEnable=0;";
        pref.UseWindowedGames();
        // Something resets just the token; Exo writes it again and the record still holds what was there first.
        _store.Values[Global] = "VRROptimizeEnable=0;";
        pref.UseWindowedGames();
        Assert.Equal(RegValue.Str("VRROptimizeEnable=0;"), record.Pc.WindowedGames!.Before);
        Assert.True(pref.UndoWindowedGames(remember: false));
        Assert.Equal("VRROptimizeEnable=0;", _store.Values[Global]);
    }

    // ---- After a Windows update ----

    [Fact]
    public void AProgramWindowsResetIsPutBackAndOneNobodySetIsNotTouched()
    {
        var mine = Touch(@"game\a.exe");
        var gone = Touch(@"game\b.exe");
        var never = Touch(@"game\c.exe");
        _pref.Use([mine, gone]);
        _store.Values.Remove(mine);        // the update reset it
        DeletionGate.DeleteFile(gone);               // an uninstalled game is not written for
        _store.Values.Remove(gone);

        Assert.Equal(1, _pref.Recheck());

        Assert.Equal("GpuPreference=2;", _store.Values[mine]);
        Assert.False(_store.Values.ContainsKey(gone));
        Assert.False(_store.Values.ContainsKey(never));
        // What it held before Exo's first write is still what Undo puts back.
        Assert.Equal(1, _pref.Undo());
        Assert.False(_store.Values.ContainsKey(mine));
    }
}
