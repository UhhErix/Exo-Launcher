using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Autopilot's actions, against a simulated PC (nothing here changes this PC: the system, the registry and the keeper are fakes).
/// Ways it could fail silently:
/// - a change made before its journal entry is on disk, so a crash at that moment leaves no way back;
/// - a change left after the game ends, Exo closes or Exo crashes;
/// - a reused process id given another program's value back, or a value the app or the player changed since overwritten;
/// - realtime priority set, or a game with anti-cheat opened at all;
/// - an anti-cheat, Windows, sound, voice or capture process, another user's process, Exo, or the app in front held back;
/// - the player's apps held back while they are in another window rather than the game;
/// - core parking changed on a dual-CCD X3D or Zen 5 part, on battery, on a plan that doesn't park, or not put back exactly;
/// - Windows Game Mode left off after Autopilot is turned off, a Game Mode the player turned on since turned off again, or
///   Exo's own keeper fighting Autopilot;
/// - the cache layout read wrong: a 5600X taken for two CCDs, or a 7950X3D's V-Cache CCD missed.
/// </summary>
public sealed class AutopilotEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-autopilot-engine-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_dir, AutopilotJournal.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) DeletionGate.DeleteDirectory(_dir, recursive: true);
    }

    private const int Session = 1, Self = 9000, Game = 100, GameChild = 101;
    private static readonly Guid Scheme = Guid.NewGuid();

    /// <summary>A PC in memory: processes with their knobs, the plan's parking value, and every write in order.</summary>
    private sealed class FakePc(string journalPath) : IAutopilotSystem
    {
        internal readonly List<ProcessRow> Rows = [];
        internal readonly Dictionary<(Knob, int), int> Values = [];
        internal readonly List<string> Writes = [];
        internal int FrontPid = Game;
        internal uint? Parking = 50;
        internal Guid ParkingSetting = AutopilotEngine.MinCores;
        internal bool Battery;
        internal bool JournalHadEntryAtEveryWrite = true;
        internal readonly HashSet<int> Refuse = [];

        internal void Add(int pid, string name, long cpu = 0, int session = Session, int priority = AutopilotRules.Normal)
        {
            Rows.Add(new ProcessRow(pid, 1, name, 4, 10, session, 1000 + pid, cpu, 0, 0, 0, 0, 8, 0));
            Values[(Knob.Priority, pid)] = priority;
            Values[(Knob.Eco, pid)] = AutopilotRules.EcoDefault;
            Values[(Knob.Io, pid)] = AutopilotRules.IoNormal;
            Values[(Knob.Memory, pid)] = AutopilotRules.MemoryNormal;
        }

        internal void Burn(int pid, long ticks)
        {
            var at = Rows.FindIndex(row => row.Pid == pid);
            Rows[at] = Rows[at] with { CpuTime = Rows[at].CpuTime + ticks };
        }

        public IReadOnlyList<ProcessRow> Processes() => [.. Rows];
        public int Front() => FrontPid;

        private bool Alive(int pid, long created) => Rows.Any(row => row.Pid == pid && row.CreateTime == created);

        public int? Read(Knob knob, int pid, long created) => Alive(pid, created) && Values.TryGetValue((knob, pid), out var v) ? v : null;

        public bool Write(Knob knob, int pid, long created, int value)
        {
            if (!Alive(pid, created) || Refuse.Contains(pid)) return false;
            if (!File.Exists(journalPath) || !File.ReadAllText(journalPath).Contains($"\"pid\":{pid},")) JournalHadEntryAtEveryWrite = false;
            Assert.NotEqual(AutopilotRules.Realtime, knob == Knob.Priority ? value : 0);
            Values[(knob, pid)] = value;
            Writes.Add($"{knob} {pid} {value}");
            return true;
        }

        public uint? PowerRead(Guid scheme, Guid subgroup, Guid setting) => scheme == Scheme && setting == ParkingSetting ? Parking : null;

        public bool PowerWrite(Guid scheme, Guid subgroup, Guid setting, uint value)
        {
            if (!File.Exists(journalPath) && value == 100) JournalHadEntryAtEveryWrite = false;
            Parking = value;
            Writes.Add($"Parking:{value}");
            return true;
        }

        public Guid? ActiveScheme() => Scheme;
        public bool OnBattery() => Battery;
    }

    private sealed class FakeTimer : IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private FakePc Pc()
    {
        var pc = new FakePc(JournalPath);
        pc.Add(Self, "ExoLauncher.exe");
        pc.Add(Game, "Game.exe");
        pc.Add(GameChild, "GameHelper.exe");
        pc.Add(200, "updater.exe");
        pc.Add(201, "Discord.exe");
        pc.Add(202, "audiodg.exe");
        pc.Add(203, "EasyAntiCheat_EOS.exe");
        pc.Add(204, "svchost.exe");
        pc.Add(205, "indexer.exe", session: 0);
        pc.Add(206, "obs64.exe");
        pc.Add(207, "chrome.exe");
        return pc;
    }

    private (AutopilotEngine Engine, FakeTimer Timer) Engine(FakePc pc, AutopilotSettings? settings = null, PowerProfiles.CpuClass cpu = PowerProfiles.CpuClass.AmdSingleCcd)
    {
        var timer = new FakeTimer();
        var options = settings ?? new AutopilotSettings { On = true };
        var engine = new AutopilotEngine(pc, new AutopilotJournal(JournalPath), () => options, () => cpu, _ => timer, _ => { }, self: Self);
        return (engine, timer);
    }

    private static AutopilotGame TheGame(bool mayTouch = true, params string[] learned) =>
        new("steam:1", "The Game", [Game, GameChild], mayTouch, learned);

    private static bool Untouched(FakePc pc, int pid) => !pc.Writes.Any(write => write.Split(' ') is [_, var at, _] && at == pid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ---------------------------------------------------------------- the game

    [Fact]
    public void TheGameGetsAboveNormalAndItGoesBack()
    {
        var pc = Pc();
        var (engine, timer) = Engine(pc);
        engine.Follow(TheGame());
        Assert.Equal(AutopilotRules.AboveNormal, pc.Values[(Knob.Priority, Game)]);
        Assert.Equal(AutopilotRules.AboveNormal, pc.Values[(Knob.Priority, GameChild)]);
        Assert.True(pc.JournalHadEntryAtEveryWrite);
        Assert.Contains("Gave The Game above normal priority", engine.Did("steam:1"));
        engine.Follow(null);
        Assert.Equal(AutopilotRules.Normal, pc.Values[(Knob.Priority, Game)]);
        Assert.True(timer.Disposed);
        Assert.Empty(new AutopilotJournal(JournalPath).Entries);
        Assert.Contains("Gave The Game above normal priority", engine.Did("steam:1"));
    }

    [Fact]
    public void HighIsTheMostAndAGameWithAntiCheatIsNeverOpened()
    {
        var pc = Pc();
        var (engine, _) = Engine(pc, new AutopilotSettings { On = true, GamePriority = "realtime" }.Copy());
        engine.Follow(TheGame());
        Assert.Equal(AutopilotRules.AboveNormal, pc.Values[(Knob.Priority, Game)]);
        engine.End();

        var (high, _) = Engine(pc, new AutopilotSettings { On = true, GamePriority = "high" });
        high.Follow(TheGame());
        Assert.Equal(AutopilotRules.High, pc.Values[(Knob.Priority, Game)]);
        high.End();

        var guarded = Pc();
        var (anti, _) = Engine(guarded);
        anti.Follow(TheGame(mayTouch: false));
        Assert.True(Untouched(guarded, Game) && Untouched(guarded, GameChild));
        Assert.Contains(anti.Did("steam:1"), line => line.Contains("anti-cheat", StringComparison.Ordinal));
    }

    [Fact]
    public void RealtimeIsNeverAChoice()
    {
        Assert.Equal("above", new AutopilotSettings { GamePriority = "realtime" }.Copy().GamePriority);
        Assert.Equal(AutopilotRules.AboveNormal, AutopilotRules.GamePriority("realtime", true));
        Assert.False(new AutopilotNative().Write(Knob.Priority, Environment.ProcessId, 0, AutopilotRules.Realtime));
    }

    [Fact]
    public void OffMeansNothing()
    {
        var pc = Pc();
        var (engine, _) = Engine(pc, new AutopilotSettings { On = false });
        engine.Follow(TheGame());
        Assert.Empty(pc.Writes);
    }

    // ---------------------------------------------------------------- background apps

    [Fact]
    public void AHogIsHeldBackOnlyWhileTheGameIsInFrontAndTheSafeListNever()
    {
        var pc = Pc();
        var (engine, _) = Engine(pc);
        engine.Follow(TheGame());
        foreach (var pid in new[] { 200, 201, 202, 203, 204, 205, 206, Self }) pc.Burn(pid, 50_000_000); // 5 s of one core each
        Thread.Sleep(20);
        // In another window: nothing is held back.
        pc.FrontPid = 207;
        engine.Look();
        Assert.True(Untouched(pc, 200));
        pc.FrontPid = Game;
        foreach (var pid in new[] { 200, 201, 202, 203, 204, 205, 206, Self }) pc.Burn(pid, 50_000_000);
        Thread.Sleep(20);
        engine.Look();
        Assert.Equal(AutopilotRules.EcoOn, pc.Values[(Knob.Eco, 200)]);
        Assert.Equal(AutopilotRules.BelowNormal, pc.Values[(Knob.Priority, 200)]);
        Assert.Equal(AutopilotRules.IoLow, pc.Values[(Knob.Io, 200)]);
        Assert.Equal(AutopilotRules.MemoryLow, pc.Values[(Knob.Memory, 200)]);
        foreach (var pid in new[] { 201, 202, 203, 204, 205, 206, Self }) Assert.True(Untouched(pc, pid), $"pid {pid} was touched");
        Assert.True(pc.JournalHadEntryAtEveryWrite);
        engine.End();
        Assert.Equal(AutopilotRules.EcoDefault, pc.Values[(Knob.Eco, 200)]);
        Assert.Equal(AutopilotRules.Normal, pc.Values[(Knob.Priority, 200)]);
        Assert.Equal(AutopilotRules.IoNormal, pc.Values[(Knob.Io, 200)]);
        Assert.Equal(AutopilotRules.MemoryNormal, pc.Values[(Knob.Memory, 200)]);
    }

    [Fact]
    public void NamedAndLearnedAppsAreHeldBackFromTheStartAndOnlyLowered()
    {
        var pc = Pc();
        pc.Values[(Knob.Priority, 207)] = AutopilotRules.AboveNormal; // the player raised it themselves
        var (engine, _) = Engine(pc, new AutopilotSettings { On = true, AlwaysDemote = ["chrome.exe", "Discord"] });
        engine.Follow(TheGame(true, "updater"));
        Assert.Equal(AutopilotRules.EcoOn, pc.Values[(Knob.Eco, 200)]);
        Assert.Equal(AutopilotRules.EcoOn, pc.Values[(Knob.Eco, 207)]);
        Assert.Equal(AutopilotRules.AboveNormal, pc.Values[(Knob.Priority, 207)]);
        Assert.True(Untouched(pc, 201)); // named, but a call
        Assert.Contains(engine.Did("steam:1"), line => line.StartsWith("Held back chrome, updater", StringComparison.Ordinal));
    }

    [Fact]
    public void AValueChangedSinceOrAReusedIdIsLeftAlone()
    {
        var pc = Pc();
        var (engine, _) = Engine(pc, new AutopilotSettings { On = true, AlwaysDemote = ["updater", "chrome"] });
        engine.Follow(TheGame());
        pc.Values[(Knob.Priority, 200)] = AutopilotRules.High; // the app changed its own since
        var chrome = pc.Rows.FindIndex(row => row.Pid == 207);
        pc.Rows[chrome] = pc.Rows[chrome] with { CreateTime = 5 }; // chrome closed; another program has its id
        pc.Values[(Knob.Eco, 207)] = 1;
        engine.End();
        Assert.Equal(AutopilotRules.High, pc.Values[(Knob.Priority, 200)]);
        Assert.Equal(1, pc.Values[(Knob.Eco, 207)]);
        Assert.Empty(new AutopilotJournal(JournalPath).Entries);
    }

    [Fact]
    public void AWriteWindowsRefusedLeavesNoEntry()
    {
        var pc = Pc();
        pc.Refuse.Add(200);
        var (engine, _) = Engine(pc, new AutopilotSettings { On = true, AlwaysDemote = ["updater"] });
        engine.Follow(TheGame());
        Assert.DoesNotContain(new AutopilotJournal(JournalPath).Entries, entry => entry.Pid == 200);
    }

    [Fact]
    public void ChosenAppsArePausedAndStartedAgainButNeverASafeOne()
    {
        var pc = Pc();
        var (engine, _) = Engine(pc, new AutopilotSettings { On = true, Suspend = ["chrome", "audiodg", "Discord"] });
        engine.Follow(TheGame());
        Assert.Equal(1, pc.Values[(Knob.Suspended, 207)]);
        Assert.False(pc.Values.ContainsKey((Knob.Suspended, 202)));
        Assert.False(pc.Values.ContainsKey((Knob.Suspended, 201)));
        engine.End();
        Assert.Equal(0, pc.Values[(Knob.Suspended, 207)]);
    }

    // ---------------------------------------------------------------- the journal and a crash

    [Fact]
    public void ACrashLeavesTheWayBackAndTheNextStartTakesIt()
    {
        var pc = Pc();
        var (engine, _) = Engine(pc, new AutopilotSettings { On = true, AlwaysDemote = ["updater"], Suspend = ["chrome"] });
        engine.Follow(TheGame());
        Assert.Equal(100u, pc.Parking);
        // Exo is gone without ending: a new Exo reads the journal.
        var (next, _) = Engine(pc);
        Assert.True(next.Recover() > 0);
        Assert.Equal(AutopilotRules.Normal, pc.Values[(Knob.Priority, Game)]);
        Assert.Equal(AutopilotRules.EcoDefault, pc.Values[(Knob.Eco, 200)]);
        Assert.Equal(0, pc.Values[(Knob.Suspended, 207)]);
        Assert.Equal(50u, pc.Parking);
        Assert.Empty(new AutopilotJournal(JournalPath).Entries);
    }

    [Fact]
    public void ADamagedJournalIsSetAsideNotFatal()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(JournalPath, "{ not json");
        var journal = new AutopilotJournal(JournalPath);
        Assert.Empty(journal.Entries);
        Assert.True(File.Exists(JournalPath + ".bad"));
        Assert.True(journal.Add(new JournalEntry("Priority", 1, 2, "x", AutopilotRules.Normal, AutopilotRules.BelowNormal)));
        Assert.Single(JsonDocument.Parse(File.ReadAllText(JournalPath)).RootElement.GetProperty("entries").EnumerateArray());
    }

    // ---------------------------------------------------------------- core parking

    [Theory]
    [InlineData("AmdSingleCcd", false, 50u, true)]
    [InlineData("AmdDualCcd", false, 10u, true)]
    [InlineData("AmdDualCcdX3d", false, 50u, false)]
    [InlineData("AmdDualCcdZen5", false, 50u, false)]
    [InlineData("AmdSingleCcd", true, 50u, false)]
    [InlineData("AmdSingleCcd", false, 100u, false)]
    public void CoreParkingOnlyWhereThePlanParksAndNoVendorDriverDoes(string cpuName, bool battery, uint parking, bool changes)
    {
        var cpu = Enum.Parse<PowerProfiles.CpuClass>(cpuName);
        var pc = Pc();
        pc.Battery = battery;
        pc.Parking = parking;
        var (engine, _) = Engine(pc, cpu: cpu);
        engine.Follow(TheGame());
        Assert.Equal(changes ? 100u : parking, pc.Parking);
        engine.End();
        Assert.Equal(parking, pc.Parking);
        Assert.Equal(changes, pc.Writes.Contains("Parking:100"));
    }

    [Fact]
    public void AnIntelHybridOnlyKeepsItsPerformanceCoresAwake()
    {
        Assert.Equal(AutopilotEngine.MinCoresPerformant, AutopilotEngine.ParkingSetting(PowerProfiles.CpuClass.IntelHybrid));
        Assert.Null(AutopilotEngine.ParkingSetting(PowerProfiles.CpuClass.AmdDualCcdX3d));
        var pc = Pc();
        pc.ParkingSetting = AutopilotEngine.MinCoresPerformant;
        var (engine, _) = Engine(pc, cpu: PowerProfiles.CpuClass.IntelHybrid);
        engine.Follow(TheGame());
        Assert.Equal(100u, pc.Parking);
        engine.End();
        Assert.Equal(50u, pc.Parking);
    }

    // ---------------------------------------------------------------- the safety list

    [Theory]
    [InlineData("vgc.exe", AutopilotSafety.AntiCheat)]
    [InlineData("EasyAntiCheat_EOS", AutopilotSafety.AntiCheat)]
    [InlineData("FortniteClient-Win64-Shipping_BE.exe", AutopilotSafety.AntiCheat)]
    [InlineData("BEService", AutopilotSafety.AntiCheat)]
    [InlineData("AUDIODG.EXE", AutopilotSafety.Sound)]
    [InlineData("Voicemeeter8x64.exe", AutopilotSafety.Sound)]
    [InlineData("C:\\Users\\x\\AppData\\Local\\Discord\\app-1.0\\Discord.exe", AutopilotSafety.Voice)]
    [InlineData("ts3client_win64", AutopilotSafety.Voice)]
    [InlineData("obs64", AutopilotSafety.Capture)]
    [InlineData("csrss", AutopilotSafety.Windows)]
    [InlineData("MsMpEng.exe", AutopilotSafety.Windows)]
    [InlineData("explorer.exe", AutopilotSafety.Windows)]
    [InlineData("lghub_agent", AutopilotSafety.Input)]
    [InlineData("NVDisplay.Container.exe", AutopilotSafety.Graphics)]
    [InlineData("", AutopilotSafety.Windows)]
    public void TheSafeListHolds(string name, string why) => Assert.Equal(why, AutopilotSafety.WhyLeftAlone(name));

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("OneDrive")]
    [InlineData("steamwebhelper")]
    [InlineData("EpicGamesLauncher")]
    public void OrdinaryAppsMayBeHeldBack(string name) => Assert.Null(AutopilotSafety.WhyLeftAlone(name));

    // ---------------------------------------------------------------- Windows Game Mode

    private (AutopilotGameMode Mode, WindowsKit.FakeRegistry Registry, WindowsGameMode Keeper) GameModeKit(RegValue? start)
    {
        var registry = new WindowsKit.FakeRegistry();
        registry.Put(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, start);
        var record = new WindowsKit.PcRecord();
        var keeper = new WindowsGameMode(registry, new WindowsKit.FakeWatch(), record.Load, record.Save, _ => { });
        keeper.Look();
        var mode = new AutopilotGameMode(registry, new AutopilotJournal(JournalPath), () => keeper.State, keeper.TurnOff, keeper.TurnOn, _ => { });
        return (mode, registry, keeper);
    }

    private static RegValue? Value(WindowsKit.FakeRegistry registry) => registry.Get(WindowsGameMode.KeyPath, WindowsGameMode.ValueName);

    [Fact]
    public void GameModeGoesOffWithAutopilotAndComesBackToTheKeeper()
    {
        var (mode, registry, keeper) = GameModeKit(null);
        Assert.Equal(RegValue.DWord(1), Value(registry)); // Exo's keeper turned it on
        Assert.Null(mode.Hand(off: true));
        Assert.Equal(RegValue.DWord(0), Value(registry));
        Assert.Equal(GameModeState.Off, keeper.State);
        keeper.Look(); // the keeper's next look doesn't fight it
        Assert.Equal(RegValue.DWord(0), Value(registry));
        Assert.Null(mode.Hand(off: false));
        Assert.Equal(RegValue.DWord(1), Value(registry));
        Assert.Equal(GameModeState.Held, keeper.State);
        Assert.Null(mode.Held);
    }

    [Fact]
    public void AKeeperThatHasNotLookedYetDoesNotTurnGameModeOnAtTheNextStart()
    {
        var registry = new WindowsKit.FakeRegistry();
        registry.Put(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, RegValue.DWord(1));
        var record = new WindowsKit.PcRecord();
        var keeper = new WindowsGameMode(registry, new WindowsKit.FakeWatch(), record.Load, record.Save, _ => { });
        Assert.Equal(GameModeState.NotKept, keeper.State);
        var mode = new AutopilotGameMode(registry, new AutopilotJournal(JournalPath), () => keeper.State, keeper.TurnOff, keeper.TurnOn, _ => { });
        Assert.Null(mode.Hand(off: true));
        keeper.Look(); // Exo's next start: the keeper looks before Autopilot
        Assert.Equal(RegValue.DWord(0), Value(registry));
        Assert.Null(mode.Hand(off: false));
        Assert.Equal(RegValue.DWord(1), Value(registry));
        Assert.Equal(GameModeState.Held, keeper.State);
    }

    [Fact]
    public void AGameModeThePlayerTurnedOnSinceStaysOn()
    {
        var (mode, registry, keeper) = GameModeKit(RegValue.DWord(0));
        keeper.TurnOff();
        Assert.Null(mode.Hand(off: true));
        registry.Put(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, RegValue.DWord(1));
        Assert.Null(mode.Hand(off: false));
        Assert.Equal(RegValue.DWord(1), Value(registry));
    }

    [Fact]
    public void GameModeIsKeptForX3dAndWhenThePlayerSaysSo()
    {
        var on = new AutopilotSettings { On = true };
        Assert.Null(AutopilotGameMode.KeptOnBecause(on, PowerProfiles.CpuClass.AmdSingleCcd));
        Assert.Contains("AMD", AutopilotGameMode.KeptOnBecause(on, PowerProfiles.CpuClass.AmdDualCcdX3d));
        Assert.Contains("AMD", AutopilotGameMode.KeptOnBecause(on, PowerProfiles.CpuClass.AmdDualCcdZen5));
        Assert.NotNull(AutopilotGameMode.KeptOnBecause(new AutopilotSettings { On = true, KeepGameMode = true }, PowerProfiles.CpuClass.IntelHybrid));
        Assert.NotNull(AutopilotGameMode.KeptOnBecause(new AutopilotSettings(), PowerProfiles.CpuClass.IntelHybrid));
    }

    [Fact]
    public void GameModeOffSurvivesAnEngineRecovery()
    {
        var (mode, registry, _) = GameModeKit(null);
        mode.Hand(off: true);
        var (engine, _) = Engine(Pc());
        engine.Recover();
        Assert.Equal(RegValue.DWord(0), Value(registry));
        Assert.NotNull(mode.Held);
    }

    // ---------------------------------------------------------------- the cache layout

    private static MouseCore.Topology Topology(params (ulong Mask, long Bytes)[] l3) =>
        new([], [.. l3.Select(c => new MouseCore.Cache(c.Mask, c.Bytes))], 32);

    [Fact]
    public void TheCacheLayoutIsReadRight()
    {
        var r5600x = CcdLayout.Of(Topology((0xFFF, 32L << 20)));
        Assert.Equal(1, r5600x.Caches);
        Assert.False(r5600x.VCache);
        Assert.StartsWith("One CCD", r5600x.Describe());
        var r7950x = CcdLayout.Of(Topology((0xFFFF, 32L << 20), (0xFFFF0000, 32L << 20)));
        Assert.False(r7950x.VCache);
        var x3d = CcdLayout.Of(Topology((0xFFFF0000, 32L << 20), (0xFFFF, 96L << 20)));
        Assert.True(x3d.VCache);
        Assert.Equal(0xFFFFUL, x3d.VCacheMask);
        Assert.Contains("CPUs 0 to 15 (96 MB)", x3d.Describe());
    }

    [Fact]
    public void ThisPcsLayoutParses()
    {
        // Read-only: GetLogicalProcessorInformationEx.
        var layout = CcdLayout.Of(MouseCore.Sampler.ReadTopology());
        Assert.True(layout.Caches >= 1);
    }

    [Fact]
    public void TheNativeReadAnswersForThisProcess()
    {
        // Read-only, on the test's own process: nothing is written.
        var native = new AutopilotNative();
        var self = ProcessSnapshot.Read().Single(row => row.Pid == Environment.ProcessId);
        Assert.NotNull(native.Read(Knob.Priority, self.Pid, self.CreateTime));
        Assert.NotNull(native.Read(Knob.Memory, self.Pid, self.CreateTime));
        Assert.NotNull(native.Read(Knob.Io, self.Pid, self.CreateTime));
        Assert.NotNull(native.Read(Knob.Eco, self.Pid, self.CreateTime));
        Assert.Null(native.Read(Knob.Priority, self.Pid, self.CreateTime + 1)); // another start time: another process
    }
}
