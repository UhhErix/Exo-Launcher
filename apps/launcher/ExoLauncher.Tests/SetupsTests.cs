using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a Setup's restore can fail:
/// - a competitive game's end leaves Exo's plan or the closed apps behind;
/// - a plan the user changed while the game ran is overwritten on the way back;
/// - a plan already on Exo's is "put back" to something else;
/// - an app that wasn't running is opened after, or one the user opened again is opened twice;
/// - anti-cheat, Lucid, Spotify or a voice or stream app (Discord, OBS) is ever closed;
/// - a startup app that is the running game itself (Roblox's player in the tray) is closed as in the way;
/// - the way back saved to disk doesn't read back after a crash or restart;
/// - a startup command's program and arguments are split wrongly, so the app can't open again.
/// </summary>
public sealed class SetupsTests
{
    private static readonly Guid UserPlan = Guid.NewGuid(), Exo = Guid.NewGuid(), Other = Guid.NewGuid();

    private sealed class FakePc : Setups.ISystem
    {
        internal Guid Plan = UserPlan;
        internal readonly HashSet<string> Processes = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Opened = [], Closed = [];

        public Guid? ActivePlan() => Plan;
        public Guid? ExoPlan() => Exo;
        public bool SetPlan(Guid plan) { Plan = plan; return true; }
        public bool Running(string process) => Processes.Contains(process);
        public void Close(string process) { Closed.Add(process); Processes.Remove(process); }
        public bool Open(string command) { Opened.Add(command); return true; }
    }

    private static readonly Setups.App Roblox = new("run:cu:RobloxPlayerBeta", "RobloxPlayerBeta", "\"C:\\Roblox\\RobloxPlayerBeta.exe\" --launch-to-tray");
    private static readonly Setups.App Helper = new("run:cu:Helper", "Helper", "C:\\Helper\\Helper.exe /tray");

    private static Setups.Held Move(Setups.Held? from, string setup, FakePc pc, params Setups.App[] inTheWay) =>
        Setups.Move(from, setup, setup == Setups.Competitive ? "steam:730" : null, Setups.PartsOf(setup, inTheWay), pc);

    [Fact]
    public void TheGamesEndPutsEverythingBack()
    {
        var pc = new FakePc { Processes = { "RobloxPlayerBeta", "Helper" } };
        var held = Move(null, Setups.Competitive, pc, Roblox, Helper);
        Assert.Equal(Exo, pc.Plan);
        Assert.Equal(["RobloxPlayerBeta", "Helper"], pc.Closed);
        Assert.True(held.Holds);

        var back = Move(held, Setups.Everyday, pc);
        Assert.Equal(UserPlan, pc.Plan);
        Assert.Equal([Roblox.Command, Helper.Command], pc.Opened);
        Assert.False(back.Holds);
    }

    [Fact]
    public void WhatTheUserChangedMeanwhileIsTheirs()
    {
        var pc = new FakePc { Processes = { "Helper" } };
        var held = Move(null, Setups.Competitive, pc, Helper);
        pc.Plan = Other;
        pc.Processes.Add("Helper");

        Move(held, Setups.Everyday, pc);
        Assert.Equal(Other, pc.Plan);
        Assert.Empty(pc.Opened);
    }

    [Fact]
    public void NothingToPutBackWhenExosPlanWasAlreadyOn()
    {
        var pc = new FakePc { Plan = Exo };
        var held = Move(null, Setups.Competitive, pc, Helper);
        Assert.Null(held.PlanSet);
        Assert.False(held.Holds);
        Move(held, Setups.Everyday, pc);
        Assert.Equal(Exo, pc.Plan);
    }

    [Fact]
    public void AnAppThatWasntRunningIsNeitherClosedNorOpened()
    {
        var pc = new FakePc();
        var held = Move(null, Setups.Competitive, pc, Helper);
        Move(held, Setups.Everyday, pc);
        Assert.Empty(pc.Closed);
        Assert.Empty(pc.Opened);
    }

    [Theory]
    [InlineData("vgtray")] [InlineData("EasyAntiCheat")] [InlineData("Lucid")] [InlineData("Spotify")]
    [InlineData("Discord")] [InlineData("obs64")] [InlineData("Streamlabs OBS")] [InlineData("NVIDIA Broadcast")]
    public void NeverClosed(string process)
    {
        Assert.True(Setups.NeverClosed(process));
        var pc = new FakePc { Processes = { process } };
        var held = Move(null, Setups.Competitive, pc, new Setups.App("run:cu:" + process, process, process + ".exe"));
        Assert.Empty(pc.Closed);
        Assert.Empty(held.Closed);
    }

    [Theory]
    [InlineData("RobloxPlayerBeta")] [InlineData("steamwebhelper")] [InlineData("Helper")]
    public void OthersCanBeInTheWay(string process) => Assert.False(Setups.NeverClosed(process));

    [Theory]
    [InlineData(@"C:\Program Files\Roblox\Versions\v1\RobloxPlayerBeta.exe", "RobloxPlayerBeta", "RobloxPlayerBeta.exe", null, true)]
    [InlineData(@"C:\Games\Tool\helper.exe", "helper", null, @"C:\Games\Tool\", true)]
    [InlineData(@"C:\Games\ToolBox\helper.exe", "helper", null, @"C:\Games\Tool", false)]
    [InlineData(@"C:\Apps\Roblox.exe", "Roblox", "cs2.exe", @"C:\Steam\cs2", false)]
    public void ARunningGameIsNeverInTheWay(string program, string process, string? exe, string? folder, bool game) =>
        Assert.Equal(game, Setups.PartOfGame(program, process, [(exe, folder)]));

    [Fact]
    public void TheWayBackSurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"setups-held-{Guid.NewGuid():N}.json");
        var before = Setups.HeldPath;
        try
        {
            var pc = new FakePc { Processes = { "Helper" } };
            var held = Move(null, Setups.Competitive, pc, Helper);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(held));
            Setups.HeldPath = path;
            var read = Setups.HeldNow();
            Assert.NotNull(read);
            Assert.Equal("steam:730", read.GameId);

            Move(read, Setups.Everyday, pc);
            Assert.Equal(UserPlan, pc.Plan);
            Assert.Equal([Helper.Command], pc.Opened);
        }
        finally
        {
            Setups.HeldPath = before;
            DeletionGate.DeleteFile(path);
        }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Steam\\steam.exe\" -silent", "C:\\Program Files\\Steam\\steam.exe", "-silent")]
    [InlineData("C:\\Discord\\Update.exe --processStart Discord.exe", "C:\\Discord\\Update.exe", "--processStart Discord.exe")]
    [InlineData("\"C:\\App\\app.exe\"", "C:\\App\\app.exe", "")]
    [InlineData("C:\\Users\\me\\Start Menu\\App.lnk", "C:\\Users\\me\\Start Menu\\App.lnk", "")]
    public void SplitsAStartupCommand(string command, string program, string arguments) =>
        Assert.Equal((program, arguments), Setups.Split(command));
}
