using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

/// <summary>
/// Frame generation's one state table (<see cref="FrameGenDecide"/>), row by row. Ways it could fail:
/// - another mod's file over Exo's loader reads as on, as a game update, or lets Best settings put Exo's in beside it;
/// - a take-out reason is shown while Exo's files stay, or a broken install reads as on;
/// - a take-out Off couldn't finish reads as on, or as "removed" for files Exo took out itself, and puts them back;
/// - files that went are never put back, or a changed file is put back over;
/// - a game with anti-cheat, another mod, or no route gets PutIn;
/// - a game kept off, or one whose mod Exo can't offer, gets PutIn;
/// - a row is shadowed by an earlier one, so it can never be reached.
/// The words each state and reason show are frameGenWord.ts's, walked by ui/test/frameGenWord.test.ts.
/// </summary>
public sealed class FrameGenStateTableTests
{
    private const string Opti = "OptiScaler";
    private const string Dlssg = "DLSSG SM86";
    private static readonly FrameGenFacts Read = new(Readable: true, Wanted: "fsr");

    private static readonly (int Row, FrameGenFacts Facts, string State, string? Reason, string? Note, FrameGenNext Next)[] Table =
    [
        (1, new(Readable: false), "hidden", null, null, FrameGenNext.Nothing),
        (1, new(Readable: false, Unread: "unread"), "hidden", "unread", null, FrameGenNext.Nothing),
        (1, new(Readable: false, Unread: "card"), "hidden", "card", null, FrameGenNext.Nothing),
        (1, new(Readable: false, Note: "Taken out: the game updated"), "note", null, "Taken out: the game updated", FrameGenNext.Nothing),
        (2, Read with { Record = Opti, TakeOut = "Taken out: the game updated", Damage = "removed", Pending = true }, "note", null, "Coming out: the game updated", FrameGenNext.Nothing),
        (2, Read with { Record = Opti, TakeOut = DlssgGoneNote, Pending = true }, "note", null, DlssgGoneNote, FrameGenNext.Nothing),
        (2, Read with { Record = Dlssg, Out = true, Pending = true }, "note", null, "Coming out: what is left of Exo's files", FrameGenNext.Nothing),
        (3, Read with { Record = Dlssg, Out = true, Damage = "replaced" }, "broken", "replaced", null, FrameGenNext.Nothing),
        (4, Read with { Record = Dlssg, Out = true }, "broken", "replaced", null, FrameGenNext.FinishOff),
        (5, Read with { Record = Opti, TakeOut = "Taken out: the game updated", Damage = "replaced" }, "on", null, null, FrameGenNext.TakeOut),
        (6, Read with { Record = Opti, Damage = "replaced" }, "broken", "replaced", null, FrameGenNext.KeepOff),
        (7, Read with { Record = Opti, Damage = "changed" }, "broken", "changed", null, FrameGenNext.Nothing),
        (8, Read with { Record = Opti, Damage = "removed" }, "broken", "removed", null, FrameGenNext.PutBack),
        (9, Read with { Record = Opti, DlssgGone = true }, "broken", "gone", null, FrameGenNext.Nothing),
        (10, Read with { Record = Opti }, "on", null, null, FrameGenNext.Forget),
        (10, Read with { Record = Opti, Note = "Back on OptiScaler 0.9.4: 0.9.5 didn't run" }, "on", null, "Back on OptiScaler 0.9.4: 0.9.5 didn't run", FrameGenNext.Forget),
        (11, Read with { Wanted = null }, "hidden", null, null, FrameGenNext.Nothing),
        (11, Read with { Wanted = null, Note = DlssgGoneNote }, "note", null, DlssgGoneNote, FrameGenNext.Nothing),
        // An RTX 40 or 50 with a game that ships frame generation: the game's own, never "not supported".
        (11, Read with { Wanted = null, Native = true }, "native", null, null, FrameGenNext.Nothing),
        (11, Read with { Wanted = null, Native = true, Note = "Taken out: this card has DLSS Frame Generation" }, "note", null, "Taken out: this card has DLSS Frame Generation", FrameGenNext.Nothing),
        (12, Read with { Wanted = "native", OtherMod = "UE4SS", AntiCheat = "BattlEye" }, "native", null, null, FrameGenNext.Nothing),
        (13, Read with { Folder = false, OtherMod = "UE4SS" }, "hidden", null, null, FrameGenNext.Nothing),
        (14, Read with { OtherMod = "UE4SS", AntiCheat = "BattlEye" }, "mod", null, null, FrameGenNext.Nothing),
        (15, Read with { AntiCheat = "Riot Vanguard", NeverAllowed = true, RiskWarning = "Game runs Riot Vanguard. A mod loading into it can get the account banned." }, "off", null, null, FrameGenNext.Nothing),
        (16, Read with { AntiCheat = "BattlEye", KeptOff = "removed", RiskWarning = "Game runs BattlEye. A mod loading into it can get the account banned." }, "off", null, null, FrameGenNext.Nothing),
        // Steam can't say, and an install Exo couldn't read, keep auto-apply out the same way.
        (16, Read with { AntiCheat = "possibly Valve Anti-Cheat", RiskWarning = "Game runs possibly Valve Anti-Cheat. A mod loading into it can get the account banned." }, "off", null, null, FrameGenNext.Nothing),
        (16, Read with { AntiCheat = "unreadable", NeverAllowed = true, RiskWarning = "Game runs anti-cheat. A mod loading into it can get the account banned." }, "off", null, null, FrameGenNext.Nothing),
        (17, Read with { KeptOff = "replaced", Unoffered = "Not available yet" }, "broken", "replaced", null, FrameGenNext.Nothing),
        (17, Read with { KeptOff = "removed" }, "broken", "removed", null, FrameGenNext.Nothing),
        (18, Read with { Wanted = "dlss", Unoffered = "Not available yet" }, "note", null, "Not available yet", FrameGenNext.Nothing),
        (19, Read with { Wanted = "dlss" }, "off", null, null, FrameGenNext.PutIn),
        (19, Read, "off", null, null, FrameGenNext.PutIn),
    ];

    public static TheoryData<int> Lines() => [.. Enumerable.Range(0, Table.Length)];

    [Theory]
    [MemberData(nameof(Lines))]
    public void EachRowGivesItsStateAndWhatHappensNext(int line)
    {
        var (row, facts, state, reason, note, next) = Table[line];
        var verdict = FrameGenDecide(facts);
        Assert.True((state, reason, note, next) == (verdict.Status.State, verdict.Status.Reason, verdict.Status.Note, verdict.Next),
            $"row {row}: expected {(state, reason, note, next)}, got {(verdict.Status.State, verdict.Status.Reason, verdict.Status.Note, verdict.Next)}");
    }

    [Fact]
    public void EveryRowIsWalked() =>
        Assert.Equal(Enumerable.Range(1, 19), Table.Select(line => line.Row).Distinct().Order());

    /// <summary>Only the last row puts a mod in: never with anti-cheat, another mod, a record, a kept-off game, or nothing to offer.</summary>
    [Fact]
    public void OnlyTheLastRowPutsAModIn()
    {
        foreach (var line in Table)
            Assert.Equal(line.Row == 19, line.Next == FrameGenNext.PutIn);
    }

    [Fact]
    public void ARecordShowsItsRoute()
    {
        Assert.Equal("fsr", FrameGenDecide(Read with { Record = Opti }).Status.Path);
        Assert.Equal("dlss", FrameGenDecide(Read with { Record = Dlssg, Wanted = "fsr" }).Status.Path);
        Assert.Equal("dlss", FrameGenDecide(Read with { Record = Dlssg, Out = true }).Status.Path);
    }

    [Fact]
    public void AnotherModIsNamedAndAnUnnamedOneIsAMod()
    {
        Assert.Equal("UE4SS", FrameGenDecide(Read with { OtherMod = "UE4SS" }).Status.Mod);
        Assert.Equal("A mod", FrameGenDecide(Read with { OtherMod = UnnamedMod }).Status.Mod);
    }
}
