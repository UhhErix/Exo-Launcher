using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.SystemTuning;

namespace ExoLauncher.Tests;

public sealed class OsTweaksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-os-tweaks-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_dir, recursive: true);

    private sealed class FakeTuningSystem : ISystem
    {
        public Dictionary<(string, string), int> Dwords { get; } = new();
        public Dictionary<(string, string), string> StringValues { get; } = new();
        public Dictionary<string, string> BcdFlags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Hz { get; set; } = 10_000_000;
        public bool MemoryCompressionOn { get; set; } = true;
        public long TotalMemKb { get; set; } = 16_777_216L;
        public int OsBuildValue { get; set; } = Win11_24H2Build;
        public bool Laptop { get; set; }
        public DateTimeOffset BootAt { get; set; } = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        public List<IReadOnlyList<string>> Runs { get; } = [];
        public bool Accept { get; set; } = true;
        public bool SupportsDeepPlaybook => true;
        public bool SupportsOsOverhead => true;

        private readonly Dictionary<string, string> _answers = new(StringComparer.OrdinalIgnoreCase);

        public long Frequency() => Hz;
        public IReadOnlyList<string> GameFolders() => [.. Folders.Keys];
        public bool? FolderNotContentIndexed(string folderPath) => Folders.TryGetValue(folderPath, out var v) ? v : null;
        public bool SetFolderNotContentIndexed(string folderPath, bool notIndexed)
        {
            if (!Folders.ContainsKey(folderPath)) return false;
            Folders[folderPath] = notIndexed;
            return true;
        }
        public int? Dword(string key, string name) => Dwords.TryGetValue((key, name), out var v) ? v : null;
        public string[]? Strings(string key, string name) => [WindowsManaged];
        public bool Exists(string key) => false;
        public (int Start, bool Delayed)? Service(string name) => null;
        public bool? IntegrityRunning() => false;
        public DateTimeOffset Boot() => BootAt;
        public bool PointStanding => true;
        public void PointMade(RestorePoint.Outcome outcome) { }
        public Task<LatencyTrace.Answer?> Latency() => Task.FromResult<LatencyTrace.Answer?>(null);
        public string? StringValue(string key, string name) => StringValues.TryGetValue((key, name), out var s) ? s : null;
        public long TotalMemoryKb() => TotalMemKb;
        public int OsBuild() => OsBuildValue;
        public bool IsLaptop() => Laptop;
        public string Answer() => Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.ans");
        public string? ReadAnswer(string answer, bool keep = false) => _answers.TryGetValue(answer, out var v) ? v : null;

        public Task<bool> Admin(IReadOnlyList<string> lines)
        {
            Runs.Add(lines);
            if (!Accept) return Task.FromResult(false);
            foreach (var line in lines) ApplyLine(line);
            return Task.FromResult(true);
        }

        private void ApplyLine(string line)
        {
            if (line.Contains("bcdedit.exe", StringComparison.OrdinalIgnoreCase))
            {
                var del = Regex.Match(line, @"/deletevalue \{current\} ([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase);
                if (del.Success) BcdFlags.Remove(del.Groups[1].Value);
                var set = Regex.Match(line, @"/set \{current\} ([A-Za-z0-9_.-]+) ([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase);
                if (set.Success) BcdFlags[set.Groups[1].Value] = set.Groups[2].Value;
                var enumOut = Regex.Match(line, @"/enum \{current\} > ""([^""]+)""");
                if (enumOut.Success)
                {
                    var body = string.Join("\r\n", BcdFlags.Select(kv => $"{kv.Key,-24}{kv.Value}")) + "\r\n" + ReadOk;
                    _answers[enumOut.Groups[1].Value] = body;
                }
                return;
            }
            if (line.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase))
            {
                var b64 = line[(line.LastIndexOf(' ') + 1)..];
                var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(b64));
                if (decoded.Contains("Enable-MMAgent", StringComparison.OrdinalIgnoreCase))
                    MemoryCompressionOn = true;
                var write = Regex.Match(decoded, @"WriteAllText\('([^']+)'");
                if (write.Success) _answers[write.Groups[1].Value] = MemoryCompressionOn.ToString();
                return;
            }
            var addDword = Regex.Match(line, @"reg\.exe add ""HKLM\\([^""]+)"" /v (?:""([^""]+)""|(\S+)) /t REG_DWORD /d (0x[0-9A-Fa-f]+|\d+)", RegexOptions.IgnoreCase);
            if (addDword.Success)
            {
                var key = addDword.Groups[1].Value;
                var name = addDword.Groups[2].Success ? addDword.Groups[2].Value : addDword.Groups[3].Value;
                var raw = addDword.Groups[4].Value;
                Dwords[(key, name)] = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? unchecked((int)Convert.ToUInt32(raw[2..], 16))
                    : int.Parse(raw);
                return;
            }
            var addSz = Regex.Match(line, @"reg\.exe add ""HKLM\\([^""]+)"" /v (?:""([^""]+)""|(\S+)) /t REG_SZ /d ""([^""]*)""", RegexOptions.IgnoreCase);
            if (addSz.Success)
            {
                var key = addSz.Groups[1].Value;
                var name = addSz.Groups[2].Success ? addSz.Groups[2].Value : addSz.Groups[3].Value;
                StringValues[(key, name)] = addSz.Groups[4].Value;
                return;
            }
            var delReg = Regex.Match(line, @"reg\.exe delete ""HKLM\\([^""]+)"" /v (?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase);
            if (delReg.Success)
            {
                var key = delReg.Groups[1].Value;
                var name = delReg.Groups[2].Success ? delReg.Groups[2].Value : delReg.Groups[3].Value;
                Dwords.Remove((key, name));
                StringValues.Remove((key, name));
            }
        }
    }

    [Fact]
    public async Task One_time_migration_reverts_only_myths_and_harmful_keys_exo_set()
    {
        var sys = new FakeTuningSystem { TotalMemKb = 16_777_216L };
        sys.BcdFlags["disabledynamictick"] = "Yes";
        sys.BcdFlags["tscsyncpolicy"] = "Enhanced";
        sys.BcdFlags["x2apicpolicy"] = "Enable";
        sys.Dwords[(PowerThrottlingKey, PowerThrottlingOffValue)] = 1;
        sys.Dwords[(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode")] = 0;
        sys.Dwords[(MemoryManagement, DisablePagingExecValue)] = 1;
        sys.Dwords[(MmcssKey, "SystemResponsiveness")] = 10;
        sys.StringValues[(MmcssGamesKey, "Latency Sensitive")] = "True";
        sys.Dwords[(KernelSessionKey, GlobalTimerValue)] = 1;
        sys.Dwords[(FthKey, "Enabled")] = 0;
        sys.Dwords[(DwmKey, OverlayTestModeValue)] = 5;
        sys.Dwords[(ControlKey, SvcHostSplitValue)] = 16_777_216;
        sys.MemoryCompressionOn = false;

        var savedPath = Path.Combine(_dir, "tuning.json");
        var legacySaved = new Saved(
            Off: [],
            Kept: new Dictionary<string, Was>
            {
                [BootLatencyKey] = new(Strings: ["disabledynamictick=absent", "tscsyncpolicy=Default", "x2apicpolicy=absent"]),
                [SvcHostSplitKey] = new(Strings: [$"{ControlKey}|{SvcHostSplitValue}=d:3670016"]),
                [OsOverheadKey] = new(Strings:
                [
                    $"{PowerThrottlingKey}|{PowerThrottlingOffValue}=absent",
                    @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization|DODownloadMode=3",
                ]),
                [KernelLatencyKey] = new(Strings:
                [
                    $"{KernelSessionKey}|{GlobalTimerValue}=absent",
                    $"{FthKey}|Enabled=d:1",
                    $"{MemoryManagement}|{DisablePagingExecValue}=d:0",
                    $"{MmcssKey}|SystemResponsiveness=d:20",
                    $"{MmcssGamesKey}|Latency Sensitive=absent",
                ]),
                [MpoOffKey] = new(Strings: [$"{DwmKey}|{OverlayTestModeValue}=absent"]),
                [MemCompressionKey] = new(Strings: ["was=true"]),
            },
            Changed: [],
            Native: [],
            MigrationVersion: 0);
        File.WriteAllText(savedPath, JsonSerializer.Serialize(legacySaved));

        var tuning = new SystemTuning(sys, savedPath, () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var before = tuning.Read();
        Assert.NotNull(before.Migration);
        Assert.True(before.Migration!.Pending);
        Assert.Contains(BootLatencyKey, before.Migration.Keys);
        Assert.Contains(OsOverheadKey, before.Migration.Keys);
        Assert.Contains(KernelLatencyKey, before.Migration.Keys);
        Assert.Contains(MpoOffKey, before.Migration.Keys);
        Assert.Contains(SvcHostSplitKey, before.Migration.Keys);
        Assert.Contains(MemCompressionKey, before.Migration.Keys);

        Assert.Null(await tuning.MigrateAsync());
        Assert.Single(sys.Runs);

        Assert.False(sys.BcdFlags.ContainsKey("disabledynamictick"));
        Assert.Equal("Default", sys.BcdFlags["tscsyncpolicy"]);
        Assert.False(sys.BcdFlags.ContainsKey("x2apicpolicy"));
        Assert.False(sys.Dwords.ContainsKey((PowerThrottlingKey, PowerThrottlingOffValue)));
        Assert.Equal(0, sys.Dwords[(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode")]);
        Assert.Equal(0, sys.Dwords[(MemoryManagement, DisablePagingExecValue)]);
        Assert.Equal(20, sys.Dwords[(MmcssKey, "SystemResponsiveness")]);
        Assert.False(sys.StringValues.ContainsKey((MmcssGamesKey, "Latency Sensitive")));
        Assert.Equal(1, sys.Dwords[(KernelSessionKey, GlobalTimerValue)]);
        Assert.Equal(0, sys.Dwords[(FthKey, "Enabled")]);
        Assert.False(sys.Dwords.ContainsKey((DwmKey, OverlayTestModeValue)));
        Assert.Equal(3_670_016, sys.Dwords[(ControlKey, SvcHostSplitValue)]);
        Assert.True(sys.MemoryCompressionOn);

        var after = tuning.Read();
        Assert.NotNull(after.Migration);
        Assert.False(after.Migration!.Pending);
        Assert.NotEmpty(after.Migration.Reverted);
        Assert.True(after.Restart);

        Assert.Null(await tuning.ApplyAsync([MpoOffKey]));
        var reopted = tuning.Read();
        Assert.False(reopted.Migration!.Pending);
        Assert.True(reopted.Items.Single(i => i.Key == MpoOffKey).On);
    }

    [Fact]
    public void Fresh_pc_never_shows_pending_migration_and_does_not_touch_user_bcd_flags()
    {
        var sys = new FakeTuningSystem();
        sys.BcdFlags["disabledynamictick"] = "Yes";
        var savedPath = Path.Combine(_dir, "fresh.json");
        var tuning = new SystemTuning(sys, savedPath, () => DateTimeOffset.UtcNow);
        var state = tuning.Read();
        Assert.Null(state.Migration);
        Assert.Equal("Yes", sys.BcdFlags["disabledynamictick"]);
    }

    [Fact]
    public async Task Harmful_tweak_cleaners_show_honest_lines_and_undo_to_exact_original_values()
    {
        var sys = new FakeTuningSystem { Hz = HpetHz };
        sys.BcdFlags["useplatformclock"] = "Yes";
        sys.BcdFlags["useplatformtick"] = "Yes";
        sys.Dwords[(MmcssKey, NetworkThrottlingValue)] = -1;
        sys.Dwords[(MemoryManagement, DisablePagingExecValue)] = 1;
        sys.Dwords[(FileSystemKey, DisableDeleteNotifyValue)] = 1;
        var savedPath = Path.Combine(_dir, "cleaners.json");
        var tuning = new SystemTuning(sys, savedPath, () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

        var initial = tuning.Read().Items;
        var timerItem = Assert.Single(initial, i => i.Key == TimerKey);
        Assert.False(timerItem.On);
        Assert.Contains("Another tool", timerItem.Detail, StringComparison.Ordinal);
        var netItem = Assert.Single(initial, i => i.Key == NetworkThrottlingKey);
        Assert.False(netItem.On);
        Assert.Contains("Another tool", netItem.Detail, StringComparison.Ordinal);
        var pageItem = Assert.Single(initial, i => i.Key == PagingExecutiveKey);
        Assert.False(pageItem.On);
        Assert.Contains("Another tool", pageItem.Detail, StringComparison.Ordinal);
        var trimItem = Assert.Single(initial, i => i.Key == TrimEnabledKey);
        Assert.False(trimItem.On);
        Assert.Contains("Another tool", trimItem.Detail, StringComparison.Ordinal);
        Assert.Empty(sys.Runs);

        Assert.Null(await tuning.ApplyAsync([TimerKey, NetworkThrottlingKey, PagingExecutiveKey, TrimEnabledKey]));
        Assert.False(sys.BcdFlags.ContainsKey("useplatformclock"));
        Assert.False(sys.BcdFlags.ContainsKey("useplatformtick"));
        Assert.Equal(10, sys.Dwords[(MmcssKey, NetworkThrottlingValue)]);
        Assert.Equal(0, sys.Dwords[(MemoryManagement, DisablePagingExecValue)]);
        Assert.Equal(0, sys.Dwords[(FileSystemKey, DisableDeleteNotifyValue)]);

        Assert.Null(await tuning.TurnOffAsync(TimerKey));
        Assert.Equal("Yes", sys.BcdFlags["useplatformclock"]);
        Assert.Equal("Yes", sys.BcdFlags["useplatformtick"]);

        Assert.Null(await tuning.TurnOffAsync(NetworkThrottlingKey));
        Assert.Equal(-1, sys.Dwords[(MmcssKey, NetworkThrottlingValue)]);

        Assert.Null(await tuning.TurnOffAsync(PagingExecutiveKey));
        Assert.Equal(1, sys.Dwords[(MemoryManagement, DisablePagingExecValue)]);

        Assert.Null(await tuning.TurnOffAsync(TrimEnabledKey));
        Assert.Equal(1, sys.Dwords[(FileSystemKey, DisableDeleteNotifyValue)]);
    }

    [Fact]
    public async Task Game_folder_not_content_indexed_records_prior_state_and_reverts_cleanly()
    {
        var sys = new FakeTuningSystem();
        sys.Folders[@"D:\Games\CS2"] = false;
        sys.Folders[@"D:\Games\AlreadyExcluded"] = true;
        var savedPath = Path.Combine(_dir, "indexing.json");
        var tuning = new SystemTuning(sys, savedPath, () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

        Assert.False(tuning.Read().Items.Single(i => i.Key == GameIndexingKey).On);
        Assert.Null(await tuning.ApplyAsync([GameIndexingKey]));
        Assert.Empty(sys.Runs);
        Assert.True(sys.Folders[@"D:\Games\CS2"]);
        Assert.True(sys.Folders[@"D:\Games\AlreadyExcluded"]);
        Assert.True(tuning.Read().Items.Single(i => i.Key == GameIndexingKey).CanUndo);

        Assert.Null(await tuning.TurnOffAsync(GameIndexingKey));
        Assert.False(sys.Folders[@"D:\Games\CS2"]);
        Assert.True(sys.Folders[@"D:\Games\AlreadyExcluded"]);
    }

    [Fact]
    public void Cpu_sets_respect_intel_hybrid_x3d_dual_ccd_and_monolithic_topologies()
    {
        var hybrid = new List<CpuSetInfo>
        {
            new(256, 0, 0, 0, 1, 0), new(257, 0, 0, 0, 1, 1),
            new(258, 0, 1, 0, 1, 2), new(259, 0, 1, 0, 1, 3),
            new(260, 0, 2, 0, 0, 4), new(261, 0, 3, 0, 0, 5),
            new(262, 0, 4, 0, 0, 6), new(263, 0, 5, 0, 0, 7),
        };
        Assert.Equal([260u, 261u, 262u, 263u], AutopilotCpuSets.BackgroundSetIds(hybrid, PowerProfiles.CpuClass.IntelHybrid));

        var x3d = Enumerable.Range(0, 16)
            .Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)(i / 2), (byte)(i < 8 ? 0 : 1), 0, (byte)i))
            .ToList();
        var x3dLayout = new CcdLayout(2, 0x00FFUL, 96L << 20);
        var x3dBg = AutopilotCpuSets.BackgroundSetIds(x3d, PowerProfiles.CpuClass.AmdDualCcdX3d, x3dLayout);
        Assert.Equal(Enumerable.Range(8, 8).Select(i => (uint)(256 + i)).ToArray(), x3dBg);

        var mono = Enumerable.Range(0, 12)
            .Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)(i / 2), 0, 0, (byte)i))
            .ToList();
        var monoBg = AutopilotCpuSets.BackgroundSetIds(mono, PowerProfiles.CpuClass.AmdSingleCcd);
        Assert.Equal([265u, 266u, 267u], monoBg);

        var tiny = Enumerable.Range(0, 4)
            .Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)i, 0, 0, (byte)i))
            .ToList();
        Assert.Empty(AutopilotCpuSets.BackgroundSetIds(tiny, PowerProfiles.CpuClass.IntelNonHybrid));
    }

    private sealed class CpuSetFakePc(string journalPath, IReadOnlyList<CpuSetInfo> sets) : IAutopilotSystem
    {
        public readonly List<ProcessRow> Rows = [];
        public readonly Dictionary<int, uint[]> Assigned = [];
        public int FrontPid = 100;
        public bool JournaledBeforeWrite = true;

        public void Add(int pid, string name, uint[]? initialSets = null)
        {
            Rows.Add(new ProcessRow(pid, 1, name, 4, 10, 1, 1000 + pid, 0, 0, 0, 0, 0, 8, 0));
            Assigned[pid] = initialSets ?? [];
        }

        public IReadOnlyList<ProcessRow> Processes() => [.. Rows];
        public int Front() => FrontPid;
        public int? Read(Knob knob, int pid, long created) => knob == Knob.Priority ? AutopilotRules.Normal : null;
        public bool Write(Knob knob, int pid, long created, int value) => false;
        public uint? PowerRead(Guid scheme, Guid subgroup, Guid setting) => 100;
        public bool PowerWrite(Guid scheme, Guid subgroup, Guid setting, uint value) => true;
        public Guid? ActiveScheme() => Guid.Empty;
        public bool OnBattery() => false;
        public IReadOnlyList<CpuSetInfo> CpuSets() => sets;
        public uint[]? ReadCpuSets(int pid, long created) => Assigned.TryGetValue(pid, out var v) ? v : null;

        public bool WriteCpuSets(int pid, long created, uint[]? ids)
        {
            if (ids is { Length: > 0 } && (!File.Exists(journalPath) || !File.ReadAllText(journalPath).Contains("\"kind\":\"CpuSets\"")))
                JournaledBeforeWrite = false;
            Assigned[pid] = ids ?? [];
            return true;
        }
    }

    [Fact]
    public void Autopilot_engine_applies_and_restores_background_cpu_sets_without_touching_game_or_custom_sets()
    {
        var journalPath = Path.Combine(_dir, "autopilot-journal.json");
        var sets = Enumerable.Range(0, 12)
            .Select(i => new CpuSetInfo((uint)(256 + i), 0, (byte)(i / 2), 0, 0, (byte)i))
            .ToList();
        var pc = new CpuSetFakePc(journalPath, sets);
        pc.Add(9000, "ExoLauncher.exe");
        pc.Add(100, "Game.exe");
        pc.Add(200, "chrome.exe");
        pc.Add(201, "custom.exe", initialSets: [256u]);
        var settings = new AutopilotSettings { On = true, AlwaysDemote = ["chrome", "custom"] };
        var engine = new AutopilotEngine(pc, new AutopilotJournal(journalPath), () => settings, () => PowerProfiles.CpuClass.AmdSingleCcd, _ => new DummyTimer(), self: 9000);

        engine.Follow(new AutopilotGame("g1", "Game", [100], MayTouchGame: false, Learned: []));
        Assert.True(pc.JournaledBeforeWrite);
        Assert.Empty(pc.Assigned[100]!);
        Assert.Equal([265u, 266u, 267u], pc.Assigned[200]);
        Assert.Equal([256u], pc.Assigned[201]);

        engine.End();
        Assert.Empty(pc.Assigned[200]!);
        Assert.Equal([256u], pc.Assigned[201]);
    }

    private sealed class DummyTimer : IDisposable { public void Dispose() { } }

    [Fact]
    public void Timer_resolution_picks_micro_adjusted_5070_and_respects_bounds()
    {
        Assert.Equal(5070u, AutopilotNative.ChooseTimerResolution(156250u, 5000u));
        Assert.Equal(5070u, AutopilotNative.ChooseTimerResolution(156250u, 5070u));
        Assert.Equal(10000u, AutopilotNative.ChooseTimerResolution(156250u, 10000u));
    }

    private sealed class FakePauseSystem(string journalPath) : IGameSessionDownloadPauseSystem
    {
        public List<Guid> ActiveJobs { get; } = [Guid.NewGuid(), Guid.NewGuid()];
        public HashSet<Guid> SuspendedJobs { get; } = [];
        public Dictionary<string, ServiceSnapshot> Services { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BITS"] = new("BITS", 3, "running", CanPause: true, CanStop: true),
            ["DoSvc"] = new("DoSvc", 2, "running", CanPause: false, CanStop: true),
            ["wuauserv"] = new("wuauserv", 3, "stopped", CanPause: false, CanStop: true),
            ["WSearch"] = new("WSearch", 4, "running", CanPause: false, CanStop: true),
        };
        public HashSet<string> RefuseControl { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> TouchedServices { get; } = [];
        public bool JournalExistedBeforeEveryMutation { get; private set; } = true;

        private void CheckJournalWritten(string expectedToken)
        {
            if (!File.Exists(journalPath) || !File.ReadAllText(journalPath).Contains(expectedToken, StringComparison.OrdinalIgnoreCase))
                JournalExistedBeforeEveryMutation = false;
        }

        public IReadOnlyList<Guid> ListActiveBitsJobs() => [.. ActiveJobs.Where(j => !SuspendedJobs.Contains(j))];

        public bool SuspendBitsJob(Guid jobId)
        {
            CheckJournalWritten(jobId.ToString());
            return SuspendedJobs.Add(jobId);
        }

        public bool ResumeBitsJob(Guid jobId) => SuspendedJobs.Remove(jobId);

        public ServiceSnapshot? QueryService(string serviceName) => Services.TryGetValue(serviceName, out var s) ? s : null;

        public bool PauseService(string serviceName)
        {
            CheckJournalWritten(serviceName);
            TouchedServices.Add("pause:" + serviceName);
            if (RefuseControl.Contains(serviceName)) return false;
            Services[serviceName] = Services[serviceName] with { State = "paused" };
            return true;
        }

        public bool StopService(string serviceName)
        {
            CheckJournalWritten(serviceName);
            TouchedServices.Add("stop:" + serviceName);
            if (RefuseControl.Contains(serviceName)) return false;
            Services[serviceName] = Services[serviceName] with { State = "stopped" };
            return true;
        }

        public bool ContinueService(string serviceName)
        {
            TouchedServices.Add("continue:" + serviceName);
            Services[serviceName] = Services[serviceName] with { State = "running" };
            return true;
        }

        public bool StartService(string serviceName)
        {
            TouchedServices.Add("start:" + serviceName);
            Services[serviceName] = Services[serviceName] with { State = "running" };
            return true;
        }
    }

    [Fact]
    public void Game_session_download_pause_journals_before_stop_leaves_disabled_alone_recovers_crash_and_enforces_timeout()
    {
        var path = Path.Combine(_dir, "exo-download-pause.json");
        var fake = new FakePauseSystem(path);
        var clock = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var pause = new GameSessionDownloadPause(fake, path, _ => { }, () => clock, TimeSpan.FromHours(6));

        pause.SetGaming(true);
        Assert.True(fake.JournalExistedBeforeEveryMutation);
        Assert.True(File.Exists(path));
        var rawJournal = File.ReadAllText(path);
        Assert.Contains("\"originalStartType\":3", rawJournal, StringComparison.Ordinal);
        Assert.Contains("\"originalState\":\"running\"", rawJournal, StringComparison.Ordinal);
        Assert.DoesNotContain("WSearch", rawJournal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wuauserv", rawJournal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.TouchedServices, t => t.Contains("WSearch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fake.TouchedServices, t => t.Contains("wuauserv", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("paused", fake.Services["BITS"].State);
        Assert.Equal("stopped", fake.Services["DoSvc"].State);
        Assert.Equal(2, fake.SuspendedJobs.Count);

        var afterCrash = new GameSessionDownloadPause(fake, path, _ => { }, () => clock, TimeSpan.FromHours(6));
        Assert.Equal(4, afterCrash.Recover());
        Assert.Equal("running", fake.Services["BITS"].State);
        Assert.Equal("running", fake.Services["DoSvc"].State);
        Assert.Empty(fake.SuspendedJobs);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(fake.TouchedServices, t => t.Contains("WSearch", StringComparison.OrdinalIgnoreCase));

        pause.SetGaming(false);
        pause.SetGaming(true);
        clock = clock.AddHours(5);
        Assert.Equal(0, pause.CheckTimeout());
        Assert.Equal("paused", fake.Services["BITS"].State);
        clock = clock.AddHours(1);
        Assert.Equal(4, pause.CheckTimeout());
        Assert.Equal("running", fake.Services["BITS"].State);
        Assert.Equal("running", fake.Services["DoSvc"].State);
        Assert.False(File.Exists(path));

        pause.SetGaming(true);
        Assert.Equal(4, pause.OnPowerTransition("system resume"));
        Assert.Equal("running", fake.Services["BITS"].State);
        Assert.False(File.Exists(path));
    }

    private sealed class FakeQosSystem : GameQosPolicy.ISystem
    {
        public Dictionary<string, (string AppName, int Dscp)> Policies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Nla { get; set; }
        public bool Accept { get; set; } = true;
        public List<IReadOnlyList<string>> Runs { get; } = [];

        public IReadOnlyDictionary<string, (string AppName, int Dscp)> ReadPolicies() => Policies;
        public string? ReadDoNotUseNla() => Nla;

        public Task<bool> RunElevatedAsync(IReadOnlyList<string> lines, CancellationToken ct)
        {
            Runs.Add(lines);
            if (!Accept) return Task.FromResult(false);
            foreach (var line in lines)
            {
                if (line.Contains(GameQosPolicy.DoNotUseNlaValue, StringComparison.Ordinal))
                {
                    if (line.Contains(" delete ", StringComparison.OrdinalIgnoreCase)) Nla = null;
                    else
                    {
                        var m = Regex.Match(line, @"/d ""([^""]*)""");
                        if (m.Success) Nla = m.Groups[1].Value;
                    }
                }
                var addApp = Regex.Match(line, @"QoS\\(Exo_DSCP_[^""\\]+)"" /v ""Application Name"" /t REG_SZ /d ""([^""]+)""");
                if (addApp.Success) Policies[addApp.Groups[1].Value] = (addApp.Groups[2].Value, 46);
                var delPol = Regex.Match(line, @"reg\.exe delete ""HKLM\\.+\\QoS\\(Exo_DSCP_[^""\\]+)""");
                if (delPol.Success) Policies.Remove(delPol.Groups[1].Value);
            }
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Per_game_qos_dscp_46_enables_disables_restores_nla_and_includes_honest_router_note()
    {
        var fake = new FakeQosSystem { Nla = null };
        var qos = new GameQosPolicy(fake, Path.Combine(_dir, "qos.json"));
        var game = new GameEntry { Id = "steam:730", Title = "Counter-Strike 2", Store = StoreKind.Steam, Installed = true, Path = @"C:\Games\CS2\cs2.exe" };

        var initial = qos.Read([game]);
        Assert.Contains("SQM", initial.Note, StringComparison.Ordinal);
        Assert.Contains("WMM", initial.Note, StringComparison.Ordinal);
        var entry = Assert.Single(initial.Games);
        Assert.False(entry.Enabled);
        Assert.Equal("cs2.exe", entry.ExeName);

        Assert.Null(await qos.SetAsync(game, true, CancellationToken.None));
        Assert.Equal("1", fake.Nla);
        Assert.True(qos.Read([game]).Games.Single().Enabled);

        Assert.Null(await qos.SetAsync(game, false, CancellationToken.None));
        Assert.Null(fake.Nla);
        Assert.False(qos.Read([game]).Games.Single().Enabled);

        Assert.False(GameQosPolicy.IsSafeExeName("evil.exe & calc"));
        Assert.False(GameQosPolicy.IsSafeExeName("notexe.txt"));
    }

    private sealed class FakeOverlaySystem : OverlayAdvisor.ISystem
    {
        public bool NvFiltersOn { get; set; } = true;
        public bool DiscordOn { get; set; } = true;
        public bool SteamOn { get; set; } = true;
        public bool SteamRunning { get; set; }
        public bool GameBarDvrOn { get; set; } = true;
        public bool X3d { get; set; } = true;

        public OverlayAdvisor.NvidiaOverlayReading ReadNvidia() => new(true, NvFiltersOn, true, true, "nv.json");
        public bool WriteNvidiaFilters(string? storagePath, bool enabled) { NvFiltersOn = enabled; return true; }
        public OverlayAdvisor.DiscordOverlayReading ReadDiscord() => new(true, DiscordOn, true, "discord.json");
        public bool WriteDiscordOverlay(string settingsPath, bool enabled) { DiscordOn = enabled; return true; }
        public OverlayAdvisor.SteamOverlayReading ReadSteam() => new(true, SteamRunning, SteamOn, ["localconfig.vdf"]);
        public bool WriteSteamGlobalOverlay(IReadOnlyList<string> paths, bool enabled) { SteamOn = enabled; return true; }
        public OverlayAdvisor.GameBarReading ReadGameBar() => new(GameBarDvrOn, !GameBarDvrOn, X3d);
        public string? SetGameBarDvrOff(bool x3dKeepApp) { GameBarDvrOn = false; return null; }
        public string? RestoreGameBarDvr() { GameBarDvrOn = true; return null; }
    }

    [Fact]
    public void Overlay_advisor_detects_all_four_overlays_toggles_and_keeps_game_bar_note_on_x3d()
    {
        var fake = new FakeOverlaySystem();
        var advisor = new OverlayAdvisor(fake, Path.Combine(_dir, "overlays.json"));

        var initial = advisor.Read().Items;
        Assert.Equal(4, initial.Count);
        Assert.Contains(initial, i => i.Id == OverlayAdvisor.NvidiaFiltersId && i.Enabled && i.Cost.Contains("15%"));
        Assert.Contains(initial, i => i.Id == OverlayAdvisor.DiscordId && i.Enabled);
        Assert.Contains(initial, i => i.Id == OverlayAdvisor.SteamId && i.Enabled);
        var gb = Assert.Single(initial, i => i.Id == OverlayAdvisor.GameBarId);
        Assert.Contains("X3D", gb.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Null(advisor.Disable(OverlayAdvisor.NvidiaFiltersId));
        Assert.False(fake.NvFiltersOn);
        Assert.Null(advisor.Restore(OverlayAdvisor.NvidiaFiltersId));
        Assert.True(fake.NvFiltersOn);

        fake.SteamRunning = true;
        Assert.NotNull(advisor.Disable(OverlayAdvisor.SteamId));
        fake.SteamRunning = false;
        Assert.Null(advisor.Disable(OverlayAdvisor.SteamId));
        Assert.False(fake.SteamOn);

        Assert.Null(advisor.Disable(OverlayAdvisor.GameBarId));
        Assert.False(fake.GameBarDvrOn);
    }

    [Fact]
    public void Network_flow_control_and_pc_health_advisories_cover_top_30_gaps()
    {
        var eth = new NetworkTune.Adapter("0001", "Ethernet", "Intel I226-V", @"PCI\VEN_8086&DEV_125C\1", false, 2_500_000_000,
            new Dictionary<string, NetworkTune.Param>(StringComparer.OrdinalIgnoreCase)
            {
                ["*FlowControl"] = new("3", "3", new Dictionary<string, string> { ["0"] = "Disabled", ["3"] = "Rx & Tx Enabled" }),
            });
        Assert.Contains(NetworkTune.Decide(eth, portable: false), r => r.Keyword == "*FlowControl" && r.To == "0");
        Assert.Empty(NetworkTune.Decide(eth, portable: true));

        var best = new NetworkBest(
            new NetworkTune(new FlowAdapters(eth), new NoOpWriter(), Path.Combine(_dir, "nt.json"), () => DateTimeOffset.UtcNow),
            new EmptyNetSystem(),
            Path.Combine(_dir, "nb.json"),
            () => DateTimeOffset.UtcNow);
        var adapterRow = Assert.Single(best.Read().Items, i => i.Key == NetworkBest.AdapterKey);
        Assert.Contains("flow control", adapterRow.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnect", adapterRow.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(adapterRow.On);

        var ryzenWin10 = PcHealth.Decide(new PcHealth.Reading([], null, "AMD Ryzen 7 9800X3D", null, null, null, OsBuild: 19045));
        Assert.Contains(ryzenWin10, i => i.Key == "os-ryzen-24h2");

        var ryzen24H2 = PcHealth.Decide(new PcHealth.Reading([], null, "AMD Ryzen 7 9800X3D", null, null, null, OsBuild: 26100));
        Assert.DoesNotContain(ryzen24H2, i => i.Key == "os-ryzen-24h2");

        var x3dMissingSvc = PcHealth.Decide(new PcHealth.Reading([], null, "AMD Ryzen 9 7950X3D", null, null, null, OsBuild: 26100, Amd3dVCacheRunning: false));
        Assert.Contains(x3dMissingSvc, i => i.Key == "x3d-service");

        var intelMissingDtt = PcHealth.Decide(new PcHealth.Reading([], null, "Intel(R) Core(TM) i7-14700K", null, null, null, CpuHybridProbe: true, OsBuild: 26100, IntelDttRunning: false));
        Assert.Contains(intelMissingDtt, i => i.Key == "intel-apo");
    }

    private sealed class FlowAdapters(NetworkTune.Adapter adapter) : NetworkTune.IAdapters
    {
        public IReadOnlyList<NetworkTune.Adapter> List() => [adapter];
        public IReadOnlyDictionary<string, NetworkTune.Param>? Params(string key) => adapter.Params;
        public bool Portable() => false;
    }

    private sealed class NoOpWriter : NetworkTune.IWriter
    {
        public string? Write(IReadOnlyList<(NetworkTune.Adapter Adapter, IReadOnlyList<(string Keyword, string? Value)> Values)> writes) => null;
    }

    private sealed class EmptyNetSystem : NetworkBest.ISystem
    {
        public IReadOnlyList<CloudflareDns.Adapter> Adapters() => [];
        public IReadOnlySet<string> Registered() => new HashSet<string>();
        public NetworkBest.Tcp? Tcp() => null;
        public (int? Code, string? Problem) Elevated(string script) => (NetworkBest.Ran, null);
        public bool StillRunning() => false;
        public bool Online() => true;
        public NetworkBest.Sample Measure() => new(null, null, null, null);
    }
}
