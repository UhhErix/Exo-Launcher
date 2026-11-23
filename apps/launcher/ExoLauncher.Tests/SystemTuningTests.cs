using ExoLauncher.Services;
using Microsoft.Win32;
using static ExoLauncher.Services.SystemTuning;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Best settings' System group could go wrong silently (none of it touches this PC: Windows is a fake here, and the one
/// registry read runs on a test key under HKCU\Software\ExoLauncherTests):
/// - a Windows default that is already in place (0x26 is Programs) is "fixed", or a changed one is missed;
/// - Use best settings sets an opt-in item (services, Memory Integrity);
/// - Memory Integrity goes off on a PC with Vanguard or FACEIT, or under a policy or the firmware lock;
/// - the record of what was there is written after the change, a second press overwrites the first record, or a declined
///   prompt leaves a record of a change that never happened;
/// - a value Windows didn't take keeps a record, so Off "puts back" something that never changed;
/// - the timer's value is removed or recorded when the elevated step didn't find it (Windows' own HPET), a read that failed
///   counts as "nothing to change" for good, a step Exo didn't see finish leaves no Undo, or an Undo Windows refused drops it;
/// - a declined prompt to turn a switch on again forgets the player's Off;
/// - a service Windows kept automatic beside one it set to Manual leaves the switch unable to put that one back;
/// - Off writes anything but the recorded value (a delayed start comes back as plain automatic), or forgets the player's Off;
/// - a service outside the allowlist, or a page file line cmd would read specially, reaches an elevated line;
/// - the restore point isn't the first step, or a PC with System Restore off is refused when Exo's own record is the way back;
/// - a change waiting on a restart says it's done.
/// </summary>
public sealed class SystemTuningTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("exo-tuning-").FullName;
    private string SavedPath => Path.Combine(_folder, "exo-system-tuning.json");
    private readonly FakeSystem _system = new();
    private DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    private SystemTuning Make() => new(_system, SavedPath, () => _now);

    private sealed class FakeSystem : ISystem
    {
        public long Hz { get; set; } = 10_000_000;
        public Dictionary<(string, string), int> Dwords { get; } = new()
        {
            [(PriorityControl, Separation)] = 2,
            [(MemoryManagement, LargeSystemCache)] = 0,
        };
        public Dictionary<(string, string), string[]> Multi { get; } = new() { [(MemoryManagement, PagingFiles)] = [WindowsManaged] };
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (int Start, bool Delayed)> Services { get; } = new() { ["MapsBroker"] = (3, false), ["PcaSvc"] = (3, false) };
        public bool? Running { get; set; } = false;
        public DateTimeOffset BootAt { get; set; } = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
        public bool Accept { get; set; } = true;
        public List<IReadOnlyList<string>> Runs { get; } = [];
        /// <summary>What Windows does when the step runs.</summary>
        public Action<IReadOnlyList<string>>? OnRun { get; set; }
        /// <summary>The platform clock forced in the boot entry (bcdedit's useplatformclock).</summary>
        public bool Clock { get; set; } = true;
        /// <summary>bcdedit can't read the boot entry, or refuses to change it.</summary>
        public bool BcdFails { get; set; }
        public bool BcdRefuses { get; set; }
        /// <summary>Exo closes while the step runs: the step finishes, Exo never reads what it wrote.</summary>
        public bool Closes { get; set; }
        public string PointWord { get; set; } = "made";
        public bool Standing { get; set; }
        public bool SupportsUserHardwareToggles { get; set; }
        public bool SupportsScheduledTasks { get; set; }
        public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool? ScheduledTaskEnabled(string taskPath) => Tasks.TryGetValue(taskPath, out var on) ? on : null;
        public bool SupportsWinFeatures { get; set; }
        public Dictionary<uint, WindowsFeatures.FeatureSnapshot> Features { get; } = new();
        public WindowsFeatures.FeatureSnapshot? WinFeature(uint featureId) => Features.TryGetValue(featureId, out var snap) ? snap : null;
        public string? SelfExe { get; set; } = @"C:\ExoLauncher\ExoLauncher.exe";
        public string? SelfExePath() => SelfExe;
        public bool SupportsOsOverhead { get; set; }
        public bool Laptop { get; set; }
        public bool IsLaptop() => Laptop;
        public string? DirectXUser { get; set; }
        public int[] Mouse { get; set; } = [6, 10, 1];
        public string? UserDirectXSettings() => DirectXUser;
        public bool SetUserDirectXSettings(string? value) { DirectXUser = value; return true; }
        public int[]? MouseParams() => [.. Mouse];
        public bool SetMouseParams(int[] values) { Mouse = [.. values]; return true; }
        public IReadOnlyList<string> HvciGames { get; set; } = [];
        public IReadOnlyList<string> HvciRequiringGames() => HvciGames;
        public List<string> NvidiaKeys { get; } = [];
        public IReadOnlyList<string> NvidiaDisplayClassKeys() => NvidiaKeys;
        public List<string> AmdKeys { get; } = [];
        public IReadOnlyList<string> AmdDisplayClassKeys() => AmdKeys;
        public List<string> UsbHubKeys { get; } = [];
        public IReadOnlyList<string> UsbRootHubKeys() => UsbHubKeys;
        public long TotalMemKb { get; set; }
        public long TotalMemoryKb() => TotalMemKb;
        public int OsBuildValue { get; set; } = Win11_24H2Build;
        public int OsBuild() => OsBuildValue;
        public Dictionary<string, bool> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> GameFolders() => [.. Folders.Keys];
        public bool? FolderNotContentIndexed(string folderPath) => Folders.TryGetValue(folderPath, out var v) ? v : null;
        public bool SetFolderNotContentIndexed(string folderPath, bool notIndexed)
        {
            if (!Folders.ContainsKey(folderPath)) return false;
            Folders[folderPath] = notIndexed;
            return true;
        }
        public bool SupportsDeepPlaybook { get; set; }
        public int FthFlushedCount { get; private set; }
        public int DnsFlushedCount { get; private set; }
        public int StandbyPurgedCount { get; private set; }
        public Dictionary<string, string> BcdFlags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool MemoryCompressionOn { get; set; } = true;
        public bool MmRefuses { get; set; }
        public Dictionary<(string, string), string> StringValues { get; } = new();
        public string? StringValue(string key, string name) => StringValues.TryGetValue((key, name), out var v) ? v : null;
        public Dictionary<(string, string), string> UserStrings { get; } = new();
        public string? UserString(string key, string name) => UserStrings.TryGetValue((key, name), out var v) ? v : null;
        public bool SetUserString(string key, string name, string? value)
        {
            if (value is null) UserStrings.Remove((key, name));
            else UserStrings[(key, name)] = value;
            return true;
        }
        public Dictionary<(string, string), int> UserDwords { get; } = new();
        public int? UserDword(string key, string name) => UserDwords.TryGetValue((key, name), out var v) ? v : null;
        public bool SetUserDword(string key, string name, int? value)
        {
            if (value is null) UserDwords.Remove((key, name));
            else UserDwords[(key, name)] = value.Value;
            return true;
        }

        private readonly List<string> _issued = [];
        private readonly Dictionary<string, string> _answers = [];
        private readonly HashSet<string> _seen = [];

        public long Frequency() => Hz;
        public int? Dword(string key, string name) => Dwords.TryGetValue((key, name), out var v) ? v : null;
        public string[]? Strings(string key, string name) => Multi.TryGetValue((key, name), out var v) ? v : null;
        public bool Exists(string key) => Keys.Contains(key);
        public (int Start, bool Delayed)? Service(string name) => Services.TryGetValue(name, out var s) ? s : null;
        public bool? IntegrityRunning() => Running;
        public DateTimeOffset Boot() => BootAt;

        private static string? DecodeEncodedPs(string line)
        {
            const string marker = " -EncodedCommand ";
            var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            var token = line[(at + marker.Length)..].Trim();
            var space = token.IndexOf(' ');
            if (space > 0) token = token[..space];
            return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(token));
        }

        public Task<bool> Admin(IReadOnlyList<string> lines)
        {
            Runs.Add(lines);
            if (!Accept) return Task.FromResult(false);
            var matchedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var path = _issued.FirstOrDefault(p => line.Contains($"\"{p}\"", StringComparison.Ordinal));
                if (path is not null) matchedPaths.Add(path);
                if (line.Contains("/enum {current}", StringComparison.Ordinal))
                {
                    var sb = new System.Text.StringBuilder();
                    if (!BcdFails)
                    {
                        if (Clock) sb.AppendLine("useplatformclock        Yes");
                        foreach (var kv in BcdFlags)
                            sb.AppendLine($"{kv.Key}        {kv.Value}");
                        sb.AppendLine("identifier              {current}");
                        sb.AppendLine(ReadOk);
                    }
                    _answers[path!] = sb.ToString();
                }
                else if (line.Contains("/deletevalue {current} useplatformclock", StringComparison.Ordinal))
                {
                    // Behind findstr: only when the read before found the value.
                    if (_answers.GetValueOrDefault(path!)?.Contains("useplatformclock", StringComparison.Ordinal) == true && !BcdRefuses) Clock = false;
                }
                else if (line.Contains("/set {current} useplatformclock true", StringComparison.Ordinal)) { if (!BcdRefuses) Clock = true; }
                else if (line.Contains("bcdedit.exe", StringComparison.OrdinalIgnoreCase) && line.Contains("/set {current} ", StringComparison.OrdinalIgnoreCase))
                {
                    var marker = "/set {current} ";
                    var parts = line[(line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && !BcdRefuses) BcdFlags[parts[0]] = parts[1];
                }
                else if (line.Contains("bcdedit.exe", StringComparison.OrdinalIgnoreCase) && line.Contains("/deletevalue {current} ", StringComparison.OrdinalIgnoreCase))
                {
                    var marker = "/deletevalue {current} ";
                    var flag = line[(line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..].Trim().Split(' ', 2)[0];
                    if (!BcdRefuses) BcdFlags.Remove(flag);
                }
                else if (line.Contains("ipconfig.exe", StringComparison.OrdinalIgnoreCase) && line.Contains("/flushdns", StringComparison.OrdinalIgnoreCase))
                {
                    DnsFlushedCount++;
                }
                else if (DecodeEncodedPs(line) is { } ps)
                {
                    if (ps.Contains("Disable-MMAgent", StringComparison.Ordinal))
                    {
                        var psPaths = _issued.Where(p => ps.Contains($"'{p.Replace("'", "''")}'", StringComparison.Ordinal) || ps.Contains(p, StringComparison.Ordinal)).ToList();
                        foreach (var p in psPaths) matchedPaths.Add(p);
                        if (psPaths.Count == 2)
                        {
                            _answers[psPaths[0]] = MemoryCompressionOn ? "True" : "False";
                            if (!MmRefuses) MemoryCompressionOn = false;
                            _answers[psPaths[1]] = MemoryCompressionOn ? "True" : "False";
                        }
                    }
                    else if (ps.Contains("Enable-MMAgent", StringComparison.Ordinal))
                    {
                        var psPaths = _issued.Where(p => ps.Contains($"'{p.Replace("'", "''")}'", StringComparison.Ordinal) || ps.Contains(p, StringComparison.Ordinal)).ToList();
                        foreach (var p in psPaths) matchedPaths.Add(p);
                        if (psPaths.Count == 1)
                        {
                            if (!MmRefuses) MemoryCompressionOn = true;
                            _answers[psPaths[0]] = MemoryCompressionOn ? "True" : "False";
                        }
                    }
                    else if (ps.Contains("MemoryPurgeStandbyList", StringComparison.Ordinal) || ps.Contains("ExoStandbyPurge", StringComparison.Ordinal))
                    {
                        StandbyPurgedCount++;
                    }
                }
            }
            // The restore point's answer: the one fresh path no line names (its step is encoded).
            foreach (var fresh in _issued.Where(p => _seen.Add(p) && !matchedPaths.Contains(p)))
                _answers[fresh] = PointWord;
            OnRun?.Invoke(lines);
            if (Closes) throw new OperationCanceledException("Exo closed");
            return Task.FromResult(true);
        }

        public string Answer()
        {
            var path = $@"C:\Temp\exo-{_issued.Count}.answer";
            _issued.Add(path);
            return path;
        }

        public string? ReadAnswer(string answer, bool keep = false) => keep ? _answers.GetValueOrDefault(answer) : _answers.Remove(answer, out var text) ? text : null;
        public bool PointStanding => Standing;
        public void PointMade(RestorePoint.Outcome outcome) { }
        public Queue<LatencyTrace.Answer?> Checks { get; } = new();
        public Task<LatencyTrace.Answer?> Latency() => Task.FromResult(Checks.Dequeue());

        /// <summary>A PC a tweak tool went over: forced HPET, Background services, no page file, the server cache.</summary>
        public void Tweaked()
        {
            Hz = HpetHz;
            Dwords[(PriorityControl, Separation)] = 0x18;
            Multi[(MemoryManagement, PagingFiles)] = [""];
            Dwords[(MemoryManagement, LargeSystemCache)] = 1;
        }

        private static (string Name, string Tail) ParseRegValueToken(string afterKey)
        {
            var marker = " /v ";
            var at = afterKey.IndexOf(marker, StringComparison.Ordinal);
            var span = afterKey[(at + marker.Length)..];
            if (span.StartsWith('"'))
            {
                var close = span.IndexOf('"', 1);
                return (span[1..close], span[(close + 1)..]);
            }
            var space = span.IndexOf(' ');
            return space < 0 ? (span, "") : (span[..space], span[space..]);
        }

        /// <summary>Windows taking every line it ran (registry and service lines, as reg.exe and sc.exe would).</summary>
        public void Take(IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
            {
                if (line.Contains(" add ", StringComparison.Ordinal) && line.Contains("REG_DWORD", StringComparison.Ordinal))
                {
                    var q1 = line.IndexOf('"');
                    var q2 = line.IndexOf('"', q1 + 1);
                    var key = line[(q1 + 1)..q2][5..];
                    var (name, rest) = ParseRegValueToken(line[(q2 + 1)..]);
                    var rawVal = rest.Split(" /d ")[1].Split(' ')[0];
                    var parsed = rawVal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? unchecked((int)uint.Parse(rawVal[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture))
                        : int.Parse(rawVal, System.Globalization.CultureInfo.InvariantCulture);
                    Dwords[(key, name)] = parsed;
                }
                else if (line.Contains(" add ", StringComparison.Ordinal) && line.Contains("REG_SZ", StringComparison.Ordinal))
                {
                    var q1 = line.IndexOf('"');
                    var q2 = line.IndexOf('"', q1 + 1);
                    var key = line[(q1 + 1)..q2][5..];
                    var (name, rest) = ParseRegValueToken(line[(q2 + 1)..]);
                    var rawData = rest.Split(" /d \"")[1].Split("\" /f")[0];
                    StringValues[(key, name)] = rawData;
                }
                else if (line.Contains("REG_MULTI_SZ", StringComparison.Ordinal))
                {
                    var key = line.Split('"')[1][5..];
                    Multi[(key, PagingFiles)] = line.Split(" /d \"")[1].Split("\" /f")[0].Split("\\0");
                }
                else if (line.Contains(" delete ", StringComparison.Ordinal))
                {
                    var q1 = line.IndexOf('"');
                    var q2 = line.IndexOf('"', q1 + 1);
                    var key = line[(q1 + 1)..q2][5..];
                    var (name, _) = ParseRegValueToken(line[(q2 + 1)..]);
                    Dwords.Remove((key, name));
                    Multi.Remove((key, name));
                    StringValues.Remove((key, name));
                }
                else if (line.Contains("powercfg.exe", StringComparison.OrdinalIgnoreCase) && line.EndsWith("/h off", StringComparison.OrdinalIgnoreCase))
                {
                    Dwords[(PowerControlKey, HibernateEnabledValue)] = 0;
                }
                else if (line.Contains("powercfg.exe", StringComparison.OrdinalIgnoreCase) && line.EndsWith("/h on", StringComparison.OrdinalIgnoreCase))
                {
                    Dwords[(PowerControlKey, HibernateEnabledValue)] = 1;
                }
                else if (line.Contains("FthSysprepSpecialize", StringComparison.Ordinal))
                {
                    FthFlushedCount++;
                }
                else if (line.Contains(" config ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    var name = parts[^3];
                    Services[name] = parts[^1] switch { "demand" => (3, false), "delayed-auto" => (2, true), "auto" => (2, false), _ => (4, false) };
                }
                else if (line.Contains("schtasks.exe", StringComparison.OrdinalIgnoreCase) && line.Contains(" /Change ", StringComparison.OrdinalIgnoreCase))
                {
                    var path = line.Split('"')[1];
                    if (line.EndsWith("/Disable", StringComparison.OrdinalIgnoreCase)) Tasks[path] = false;
                    else if (line.EndsWith("/Enable", StringComparison.OrdinalIgnoreCase)) Tasks[path] = true;
                }
                else if (line.Contains(WindowsFeatures.Flag, StringComparison.OrdinalIgnoreCase))
                {
                    var marker = $"{WindowsFeatures.Flag} set ";
                    var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        var ops = WindowsFeatures.ParseOps(line[(at + marker.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        if (ops is not null)
                        {
                            foreach (var op in ops)
                            {
                                Features.TryGetValue(op.Id, out var prev);
                                if (op.Reset)
                                    Features[op.Id] = new WindowsFeatures.FeatureSnapshot(
                                        op.Id,
                                        prev?.EffectivePriority is not null and not WindowsFeatures.PriorityUser ? prev.EffectiveState : null,
                                        prev?.EffectivePriority is not null and not WindowsFeatures.PriorityUser ? prev.EffectivePriority : null,
                                        null,
                                        null);
                                else
                                    Features[op.Id] = new WindowsFeatures.FeatureSnapshot(
                                        op.Id,
                                        op.EnabledState,
                                        WindowsFeatures.PriorityUser,
                                        (int)op.EnabledState,
                                        (int)op.EnabledStateOptions);
                            }
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(2, true)]       // Programs
    [InlineData(0x26, true)]    // the "gaming" value: the same as Programs on a client
    [InlineData(0x16, true)]    // long, variable, boosted
    [InlineData(0x18, false)]   // Background services: long, fixed, no boost
    [InlineData(0x28, false)]   // short, fixed
    [InlineData(0x24, false)]   // short, variable, no boost
    [InlineData(0, false)]
    public void Foreground_first_is_variable_quanta_with_a_boost(int value, bool first) => Assert.Equal(first, ForegroundFirst(value));

    [Fact]
    public void A_page_file_is_missing_only_when_none_is_listed()
    {
        Assert.True(NoPageFile(null));
        Assert.True(NoPageFile([""]));
        Assert.True(NoPageFile(["  ", ""]));
        Assert.False(NoPageFile([WindowsManaged]));
        Assert.False(NoPageFile([@"D:\pagefile.sys 4096 8192"]));
    }

    [Fact]
    public void A_stock_pc_has_nothing_to_set()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x26;
        Assert.Empty(Make().Read().Items);
        Assert.True(Make().Read().Applied);
    }

    [Fact]
    public void Detection_reads_what_a_tweak_changed_and_the_opt_ins_as_opt_ins()
    {
        _system.Tweaked();
        _system.Services["MapsBroker"] = (2, true);
        _system.Dwords[(Hvci, "Enabled")] = 1;
        _system.Running = true;
        var items = Make().Read().Items;
        Assert.Equal([TimerKey, SchedulingKey, PagefileKey, CacheKey, ServicesKey, IntegrityKey], items.Select(i => i.Key));
        Assert.All(items, i => Assert.False(i.On));
        Assert.Equal([ServicesKey, IntegrityKey], items.Where(i => i.OptIn).Select(i => i.Key));
        Assert.Contains("Downloaded Maps Manager", items.Single(i => i.Key == ServicesKey).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Program Compatibility", items.Single(i => i.Key == ServicesKey).Detail, StringComparison.Ordinal);
        Assert.Equal(IntegrityWarning, items.Single(i => i.Key == IntegrityKey).Detail);
    }

    [Fact]
    public async Task Use_best_settings_sets_the_defaults_never_an_opt_in_behind_a_restore_point_first()
    {
        _system.Tweaked();
        _system.Services["PcaSvc"] = (2, false);
        _system.Dwords[(Hvci, "Enabled")] = 1;
        _system.OnRun = lines =>
        {
            // The record is on disk before Windows changes anything.
            Assert.Contains("\"scheduling\"", File.ReadAllText(SavedPath), StringComparison.Ordinal);
            _system.Take(lines);
        };
        Assert.Null(await Make().ApplyAsync());
        var lines = Assert.Single(_system.Runs);
        Assert.Contains("powershell.exe", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lines, l => l.Contains("sc.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("HypervisorEnforcedCodeIntegrity", StringComparison.Ordinal));
        var state = Make().Read();
        Assert.True(state.Applied);
        Assert.All(state.Items.Where(i => !i.OptIn), i => Assert.True(i.On && i.CanUndo));
        Assert.All(state.Items.Where(i => !i.OptIn && i.NeedsRestart), i => Assert.True(i.Restart));
        var sched = state.Items.Single(i => i.Key == SchedulingKey);
        Assert.False(sched.NeedsRestart);
        Assert.False(sched.Restart);
        Assert.Equal("The game in front gets the longer turn on the processor.", sched.Detail);
        Assert.False(state.Items.Single(i => i.Key == ServicesKey).On);
        Assert.False(state.Items.Single(i => i.Key == IntegrityKey).On);
    }

    [Fact]
    public async Task A_declined_prompt_keeps_no_record()
    {
        _system.Tweaked();
        _system.Accept = false;
        Assert.NotNull(await Make().ApplyAsync());
        var state = Make().Read();
        Assert.All(state.Items, i => Assert.False(i.On || i.CanUndo || i.Restart));
    }

    [Fact]
    public async Task A_value_windows_didnt_take_keeps_no_record()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x18;
        var said = await Make().ApplyAsync();
        Assert.Contains("didn't take", said, StringComparison.Ordinal);
        var item = Make().Read().Items.Single();
        Assert.False(item.CanUndo);
        Assert.False(item.On);
    }

    [Fact]
    public async Task The_timer_is_recorded_only_when_the_step_found_the_forced_clock()
    {
        _system.Hz = HpetHz;
        _system.Clock = false;
        Assert.Contains("by itself", await Make().ApplyAsync(), StringComparison.Ordinal);
        Assert.Empty(Make().Read().Items);
        Assert.Single(_system.Runs);
        // The removal runs only behind findstr's answer, and the same read after says what's left.
        var lines = _system.Runs[0];
        Assert.Contains(lines, l => l.Contains("&& ", StringComparison.Ordinal) && l.Contains("/deletevalue {current} useplatformclock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_forced_clock_goes_and_off_forces_it_again()
    {
        _system.Hz = HpetHz;
        Assert.Null(await Make().ApplyAsync());
        var item = Make().Read().Items.Single();
        Assert.True(item.On && item.CanUndo && item.Restart);
        Assert.False(_system.Clock);
        Assert.Null(await Make().TurnOffAsync(TimerKey));
        Assert.Contains("/set {current} useplatformclock true", _system.Runs[^1][0], StringComparison.Ordinal);
        Assert.True(_system.Clock);
        var after = Make().Read().Items.Single();
        Assert.True(after.Off);
        Assert.Equal("Goes back to how it was after a restart.", after.Detail);
    }

    [Fact]
    public async Task A_failed_timer_read_changes_nothing_and_can_be_tried_again()
    {
        _system.Hz = HpetHz;
        _system.BcdFails = true;
        Assert.Contains("couldn't read", await Make().ApplyAsync(), StringComparison.Ordinal);
        Assert.True(_system.Clock);
        var item = Make().Read().Items.Single();
        Assert.False(item.On || item.CanUndo);
        _system.BcdFails = false;
        Assert.Null(await Make().ApplyAsync());
        Assert.False(_system.Clock);
        Assert.True(Make().Read().Items.Single().CanUndo);
    }

    [Fact]
    public async Task A_timer_step_exo_didnt_see_finish_still_has_its_undo()
    {
        _system.Hz = HpetHz;
        _system.Closes = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Make().ApplyAsync());
        _system.Closes = false;
        Assert.False(_system.Clock);
        // The next read before a restart settles it from what the step wrote and keeps the restart prompt.
        var item = Make().Read().Items.Single();
        Assert.True(item.On && item.CanUndo && item.Restart);
        Assert.Null(await Make().TurnOffAsync(TimerKey));
        Assert.True(_system.Clock);
    }

    [Fact]
    public async Task A_timer_step_recovered_after_restart_clears_its_restart_prompt()
    {
        _system.Hz = HpetHz;
        _system.Closes = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Make().ApplyAsync());
        _system.Closes = false;
        // Windows rebooted after the elevated step ran; settling later uses the step's original timestamp, not recovery time.
        _system.Hz = 10_000_000;
        _system.BootAt = _now.AddMinutes(5);
        _now = _now.AddMinutes(6);
        var item = Make().Read().Items.Single();
        Assert.True(item.On && item.CanUndo);
        Assert.False(item.Restart);
    }

    [Fact]
    public async Task A_timer_undo_windows_refused_keeps_its_record()
    {
        _system.Hz = HpetHz;
        Assert.Null(await Make().ApplyAsync());
        _system.BcdRefuses = true;
        Assert.Contains("didn't put it back", await Make().TurnOffAsync(TimerKey), StringComparison.Ordinal);
        Assert.True(Make().Read().Items.Single().CanUndo);
        _system.BcdRefuses = false;
        Assert.Null(await Make().TurnOffAsync(TimerKey));
        Assert.True(_system.Clock);
    }

    [Fact]
    public async Task A_declined_prompt_to_turn_a_switch_on_again_keeps_the_players_off()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x18;
        _system.OnRun = _system.Take;
        Assert.Null(await Make().ApplyAsync());
        Assert.Null(await Make().TurnOffAsync(SchedulingKey));
        _system.Accept = false;
        Assert.Contains("declined", await Make().ApplyAsync([SchedulingKey]), StringComparison.Ordinal);
        Assert.True(Make().Read().Items.Single().Off);
        _system.Accept = true;
        var runs = _system.Runs.Count;
        Assert.Null(await Make().ApplyAsync());
        Assert.Equal(runs, _system.Runs.Count);
        Assert.Equal(0x18, _system.Dwords[(PriorityControl, Separation)]);
    }

    [Fact]
    public async Task Services_windows_set_only_in_part_can_still_be_put_back()
    {
        _system.Services["MapsBroker"] = (2, false);
        _system.Services["PcaSvc"] = (2, true);
        _system.OnRun = lines => _system.Take([.. lines.Where(l => !l.Contains("PcaSvc", StringComparison.Ordinal))]);
        Assert.Contains("Program Compatibility", await Make().ApplyAsync([ServicesKey]), StringComparison.Ordinal);
        var item = Make().Read().Items.Single();
        Assert.True(item.On && item.CanUndo);
        _system.BootAt = _now.AddMinutes(5);
        Assert.Contains("still starts with Windows", Make().Read().Items.Single().Detail, StringComparison.Ordinal);
        Assert.Null(await Make().TurnOffAsync(ServicesKey));
        Assert.Equal((2, false), _system.Services["MapsBroker"]);
        Assert.Equal((2, true), _system.Services["PcaSvc"]);
    }

    [Fact]
    public async Task Off_puts_back_the_exact_value_and_best_settings_leaves_it_until_its_switch_is_on()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x18;
        _system.OnRun = _system.Take;
        Assert.Null(await Make().ApplyAsync());
        Assert.Null(await Make().TurnOffAsync(SchedulingKey));
        Assert.Equal(0x18, _system.Dwords[(PriorityControl, Separation)]);
        Assert.True(Make().Read().Items.Single().Off);

        var runs = _system.Runs.Count;
        Assert.Null(await Make().ApplyAsync());
        Assert.Equal(runs, _system.Runs.Count);

        Assert.Null(await Make().ApplyAsync([SchedulingKey]));
        Assert.Equal(2, _system.Dwords[(PriorityControl, Separation)]);
        Assert.False(Make().Read().Items.Single().Off);
    }

    [Fact]
    public async Task A_second_press_keeps_the_first_record()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x18;
        _system.OnRun = _system.Take;
        await Make().ApplyAsync();
        // Another tool sets it again; a second press must not record Exo's own value as "what was there".
        _system.Dwords[(PriorityControl, Separation)] = 0x28;
        await Make().ApplyAsync();
        await Make().TurnOffAsync(SchedulingKey);
        Assert.Equal(0x18, _system.Dwords[(PriorityControl, Separation)]);
    }

    [Fact]
    public async Task An_absent_value_is_deleted_again_not_written()
    {
        _system.Multi.Remove((MemoryManagement, PagingFiles));
        _system.OnRun = _system.Take;
        Assert.Null(await Make().ApplyAsync());
        Assert.Equal([WindowsManaged], _system.Multi[(MemoryManagement, PagingFiles)]);
        Assert.Null(await Make().TurnOffAsync(PagefileKey));
        Assert.Contains(_system.Runs[^1], l => l.Contains(" delete ", StringComparison.Ordinal) && l.Contains(PagingFiles, StringComparison.Ordinal));
        Assert.False(_system.Multi.ContainsKey((MemoryManagement, PagingFiles)));
    }

    [Fact]
    public async Task Services_change_only_from_automatic_to_manual_and_come_back_with_their_delayed_start()
    {
        _system.Services["MapsBroker"] = (2, true);
        _system.Services["PcaSvc"] = (4, false); // disabled by someone else: never touched
        _system.OnRun = _system.Take;
        Assert.Null(await Make().ApplyAsync([ServicesKey]));
        var lines = _system.Runs[^1].Where(l => l.Contains("sc.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(["MapsBroker"], lines.Select(l => l.Split(' ')[^3]));
        Assert.EndsWith("start= demand", lines[0], StringComparison.Ordinal);
        Assert.Equal((3, false), _system.Services["MapsBroker"]);
        Assert.Equal((4, false), _system.Services["PcaSvc"]);

        Assert.Null(await Make().TurnOffAsync(ServicesKey));
        Assert.Equal((2, true), _system.Services["MapsBroker"]);
    }

    [Fact]
    public void Only_allowlisted_services_and_plain_page_file_lines_reach_an_elevated_line()
    {
        Assert.Null(Revert(ServicesKey, new Was(Services: [new("WinDefend", 2, false)])));
        Assert.Null(Revert(PagefileKey, new Was(Strings: ["C:\\pagefile.sys\" & calc"])));
        Assert.Null(Revert(PagefileKey, new Was(Strings: ["%TEMP%\\x"])));
        Assert.NotNull(Revert(PagefileKey, new Was(Strings: [@"C:\pagefile.sys 16384 32768", @"D:\pagefile.sys 0 0"])));
        Assert.NotNull(Revert(PagefileKey, new Was(Strings: [""])));
        Assert.All(Trimmed, s => Assert.DoesNotContain(s.Name, new[] { "WinDefend", "Audiosrv", "BFE", "XblAuthManager", "GamingServices", "CaptureService", "GraphicsPerfSvc", "wuauserv", "vgc", "vgk", "BEService", "EasyAntiCheat", "FACEIT", "FACEITService" }));
    }

    [Theory]
    [InlineData("vgk")]
    [InlineData("vgc")]
    [InlineData("FACEIT")]
    public async Task Memory_integrity_stays_on_beside_an_anti_cheat_that_can_require_it(string service)
    {
        _system.Dwords[(Hvci, "Enabled")] = 1;
        _system.Running = true;
        _system.Keys.Add($@"SYSTEM\CurrentControlSet\Services\{service}");
        var item = Make().Read().Items.Single();
        Assert.NotNull(item.Blocked);
        Assert.Equal(item.Blocked, await Make().ApplyAsync([IntegrityKey]));
        Assert.Empty(_system.Runs);
    }

    [Fact]
    public async Task Memory_integrity_stays_on_under_a_policy_or_the_firmware_lock()
    {
        _system.Dwords[(Hvci, "Enabled")] = 1;
        _system.Dwords[(HvciPolicy, "HypervisorEnforcedCodeIntegrity")] = 1;
        Assert.Contains("policy", await Make().ApplyAsync([IntegrityKey]), StringComparison.Ordinal);
        _system.Dwords.Remove((HvciPolicy, "HypervisorEnforcedCodeIntegrity"));
        _system.Dwords[(Hvci, "Locked")] = 1;
        Assert.Contains("firmware", await Make().ApplyAsync([IntegrityKey]), StringComparison.Ordinal);
        Assert.Empty(_system.Runs);
    }

    [Fact]
    public async Task Memory_integrity_off_is_only_its_own_switch_and_waits_on_a_restart()
    {
        _system.Dwords[(Hvci, "Enabled")] = 1;
        _system.Running = true;
        _system.OnRun = _system.Take;
        Assert.Null(await Make().ApplyAsync());
        Assert.Empty(_system.Runs);
        Assert.Null(await Make().ApplyAsync([IntegrityKey]));
        Assert.Equal(0, _system.Dwords[(Hvci, "Enabled")]);
        var item = Make().Read().Items.Single();
        Assert.True(item.On && item.Restart);
        Assert.Null(await Make().TurnOffAsync(IntegrityKey));
        Assert.Equal(1, _system.Dwords[(Hvci, "Enabled")]);
    }

    [Fact]
    public async Task System_restore_off_still_applies_and_says_so()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x18;
        _system.PointWord = "off";
        _system.OnRun = _system.Take;
        Assert.Contains("System Restore is off", await Make().ApplyAsync(), StringComparison.Ordinal);
        Assert.True(Make().Read().Items.Single().On);
    }

    [Fact]
    public async Task A_point_from_the_last_day_stands_and_no_other_is_asked_for()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x18;
        _system.Standing = true;
        await Make().ApplyAsync([SchedulingKey]);
        Assert.DoesNotContain(_system.Runs[0], l => l.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task After_the_restart_a_change_says_its_done()
    {
        _system.Dwords[(PriorityControl, Separation)] = 0x18;
        _system.OnRun = _system.Take;
        await Make().ApplyAsync();
        _system.BootAt = _now.AddMinutes(5);
        var item = Make().Read().Items.Single();
        Assert.False(item.Restart);
        Assert.Equal("The game in front gets the longer turn on the processor.", item.Detail);
    }

    [Fact]
    public async Task An_unknown_item_changes_nothing()
    {
        Assert.Equal("Exo doesn't know that setting.", await Make().ApplyAsync(["defender"]));
        Assert.Equal("Exo doesn't know that setting.", await Make().TurnOffAsync("defender"));
        Assert.Empty(_system.Runs);
    }

    [Fact]
    public void The_native_reader_reads_dwords_lists_and_services_from_its_key()
    {
        const string prefix = @"Software\ExoLauncherTests\SystemTuning";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"{prefix}\{PriorityControl}")) key.SetValue(Separation, 0x26, RegistryValueKind.DWord);
            using (var key = Registry.CurrentUser.CreateSubKey($@"{prefix}\{MemoryManagement}")) key.SetValue(PagingFiles, new[] { "" }, RegistryValueKind.MultiString);
            using (var key = Registry.CurrentUser.CreateSubKey($@"{prefix}\SYSTEM\CurrentControlSet\Services\MapsBroker"))
            {
                key.SetValue("Start", 2, RegistryValueKind.DWord);
                key.SetValue("DelayedAutostart", 1, RegistryValueKind.DWord);
            }
            var native = new NativeSystem(Registry.CurrentUser, prefix);
            Assert.Equal(0x26, native.Dword(PriorityControl, Separation));
            Assert.Null(native.Dword(PriorityControl, "Missing"));
            Assert.Equal([""], native.Strings(MemoryManagement, PagingFiles)!);
            Assert.Null(native.Strings(MemoryManagement, "Missing"));
            Assert.Equal((2, true), native.Service("MapsBroker"));
            Assert.Null(native.Service("PcaSvc"));
            Assert.True(native.Exists(PriorityControl));
            Assert.False(native.Exists(Hvci));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(prefix, throwOnMissingSubKey: false); }
    }

    [Fact]
    public async Task Disabled_capture_service_restores_to_manual_and_is_reversible()
    {
        _system.Services[CaptureServiceName] = (4, false);
        var before = Make().Read();
        var item = Assert.Single(before.Items, i => i.Key == CaptureServiceKey);
        Assert.False(item.On);
        Assert.False(item.OptIn);
        Assert.False(item.NeedsRestart);

        _system.OnRun = lines => _system.Take(lines);
        Assert.Null(await Make().ApplyAsync());
        Assert.Contains(_system.Runs[0], l => l.Contains("sc.exe config CaptureService start= demand", StringComparison.OrdinalIgnoreCase));
        var applied = Make().Read().Items.Single(i => i.Key == CaptureServiceKey);
        Assert.True(applied.On);
        Assert.True(applied.CanUndo);
        Assert.False(applied.Restart);

        Assert.Null(await Make().TurnOffAsync(CaptureServiceKey));
        Assert.Equal(4, _system.Services[CaptureServiceName].Start);
    }

    [Fact]
    public async Task Vrr_and_mouse_accel_are_omitted_on_fresh_pc_and_legacy_vrr_puts_back_cleanly()
    {
        _system.SupportsUserHardwareToggles = true;
        _system.DirectXUser = "SwapEffectUpgradeEnable=1;";
        _system.Mouse = [6, 10, 1];

        var tuning = Make();
        var fresh = tuning.Read().Items;
        Assert.DoesNotContain(fresh, i => i.Key == VrrKey);
        Assert.DoesNotContain(fresh, i => i.Key == MouseAccelKey);

        // Legacy record from an earlier Exo version that had applied VRR: still offered for Put back and restores cleanly without admin.
        File.WriteAllText(SavedPath, """{"Off":[],"Kept":{"vrr":{"Strings":["SwapEffectUpgradeEnable=1;"],"Absent":false}},"Changed":{},"Native":[]}""");
        _system.DirectXUser = "SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;";

        var legacy = Make().Read().Items;
        var vrr = Assert.Single(legacy, i => i.Key == VrrKey);
        Assert.True(vrr.On && vrr.CanUndo && !vrr.Restart && !vrr.NeedsRestart);

        Assert.Null(await Make().TurnOffAsync(VrrKey));
        Assert.Equal("SwapEffectUpgradeEnable=1;", _system.DirectXUser);
        Assert.Empty(_system.Runs);
        Assert.DoesNotContain(Make().Read().Items, i => i.Key == VrrKey);
    }

    [Fact]
    public async Task Installed_vanguard_or_faceit_game_blocks_disabling_memory_integrity_even_before_service_runs()
    {
        _system.Dwords[(Hvci, "Enabled")] = 1;
        _system.Running = true;
        var games = FindHvciRequiringGames([
            new ExoLauncher.Models.GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = ExoLauncher.Models.StoreKind.Riot, Installed = true },
            new ExoLauncher.Models.GameEntry { Id = "riot:2xko", Title = "2XKO", Store = ExoLauncher.Models.StoreKind.Riot, Installed = false },
            new ExoLauncher.Models.GameEntry { Id = "steam:730", Title = "Counter-Strike 2", Store = ExoLauncher.Models.StoreKind.Steam, Installed = true },
        ]);
        Assert.Equal(["VALORANT"], games);

        _system.HvciGames = games;
        var blocked = Make().Read().Items.Single(i => i.Key == IntegrityKey);
        Assert.NotNull(blocked.Blocked);
        Assert.Contains("VALORANT", blocked.Blocked, StringComparison.Ordinal);

        var refusal = await Make().ApplyAsync([IntegrityKey]);
        Assert.NotNull(refusal);
        Assert.Empty(_system.Runs);
    }

    // ---- The latency check ----

    private static LatencyTrace.Result Check(double dpc, string driver, double isr = 20) =>
        new(DateTimeOffset.UtcNow, 10, 5000, 900, dpc, driver, isr, "USBXHCI.SYS", []);

    [Fact]
    public async Task The_latency_check_keeps_the_last_two_and_compares_them()
    {
        _system.Checks.Enqueue(new(Check(1412, "ndis.sys"), null));
        _system.Checks.Enqueue(new(Check(300, "nvlddmkm.sys"), null));
        _system.Checks.Enqueue(new(Check(310, "nvlddmkm.sys"), null));
        Assert.Null(await Make().LatencyAsync());
        var first = Make().Latency();
        Assert.Null(first.Before);
        Assert.Contains("ndis.sys held the processor for 1412 µs", first.Said, StringComparison.Ordinal);
        Assert.Null(await Make().LatencyAsync());
        Assert.Contains("fell from 1412 µs to 300 µs", Make().Latency().Said, StringComparison.Ordinal);
        Assert.Null(await Make().LatencyAsync());
        var third = Make().Latency();
        Assert.Equal(300, third.Before!.DpcMaxUs);
        Assert.Contains("About the same", third.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declined_or_failed_check_keeps_the_last_one()
    {
        _system.Checks.Enqueue(new(Check(200, "nvlddmkm.sys"), null));
        _system.Checks.Enqueue(null);
        _system.Checks.Enqueue(new(null, "Windows' permission is needed to measure."));
        await Make().LatencyAsync();
        Assert.Contains("declined", await Make().LatencyAsync(), StringComparison.Ordinal);
        Assert.Equal("Windows' permission is needed to measure.", await Make().LatencyAsync());
        Assert.Equal(200, Make().Latency().After!.DpcMaxUs);
    }

    [Theory]
    [InlineData(120, "fine for games and audio")]
    [InlineData(700, "close to where audio can crackle")]
    [InlineData(2400, "long enough for audio to crackle")]
    public void The_check_says_what_the_longest_run_means(double us, string words) =>
        Assert.Contains(words, LatencySaid(null, Check(us, "x.sys")), StringComparison.Ordinal);

    [Fact]
    public async Task Nvidia_vrr_lfc_applies_hex_dword_with_restore_point_and_reverts_cleanly()
    {
        var adapter = $@"{DisplayClassRoot}\0000";
        _system.NvidiaKeys.Add(adapter);
        _system.Dwords[(adapter, VrrFrameSplitBehavior)] = unchecked((int)0x80000055u);
        _system.OnRun = lines => _system.Take(lines);

        var initial = Make().Read().Items.Single(i => i.Key == VrrLfcKey);
        Assert.False(initial.On);
        Assert.False(initial.OptIn);
        Assert.True(initial.NeedsRestart);

        Assert.Null(await Make().ApplyAsync());
        Assert.Contains(_system.Runs[0], l => l.Contains("0x80010055", StringComparison.Ordinal) && !l.Contains("-2147", StringComparison.Ordinal));
        Assert.Equal(VrrLfcLowestMultiplier, _system.Dwords[(adapter, VrrFrameSplitBehavior)]);
        var applied = Make().Read().Items.Single(i => i.Key == VrrLfcKey);
        Assert.True(applied.On);
        Assert.True(applied.CanUndo);
        Assert.True(applied.Restart);

        Assert.Null(await Make().TurnOffAsync(VrrLfcKey));
        Assert.Contains(_system.Runs[^1], l => l.Contains("0x80000055", StringComparison.Ordinal));
        Assert.Equal(unchecked((int)0x80000055u), _system.Dwords[(adapter, VrrFrameSplitBehavior)]);

        _system.Dwords.Remove((adapter, VrrFrameSplitBehavior));
        DeletionGate.DeleteFile(SavedPath);
        Assert.Null(await Make().ApplyAsync([VrrLfcKey]));
        Assert.Equal(VrrLfcLowestMultiplier, _system.Dwords[(adapter, VrrFrameSplitBehavior)]);
        Assert.Null(await Make().TurnOffAsync(VrrLfcKey));
        Assert.False(_system.Dwords.ContainsKey((adapter, VrrFrameSplitBehavior)));
    }

    [Fact]
    public void Vrr_lfc_is_omitted_without_nvidia_and_revert_rejects_unlisted_keys()
    {
        Assert.DoesNotContain(Make().Read().Items, i => i.Key == VrrLfcKey);
        Assert.Null(Revert(VrrLfcKey, new Was(Strings: [@"SOFTWARE\Microsoft\Windows Defender=0x00000000"])));
        Assert.NotNull(Revert(VrrLfcKey, new Was(Strings: [$@"{DisplayClassRoot}\0001=absent"])));
    }

    [Fact]
    public async Task Privacy_applies_without_uac_or_restart_and_restores_exact_prior_values()
    {
        _system.SupportsUserHardwareToggles = true;
        _system.UserDwords[(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled")] = 1;
        _system.UserDwords[(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled")] = 1;

        var initial = Make().Read().Items.Single(i => i.Key == PrivacyKey);
        Assert.False(initial.On);
        Assert.False(initial.OptIn);
        Assert.False(initial.NeedsRestart);

        Assert.Null(await Make().ApplyAsync());
        Assert.Empty(_system.Runs);
        foreach (var entry in PrivacySettings)
            Assert.Equal(entry.Target, _system.UserDwords[(entry.Key, entry.Name)]);

        var applied = Make().Read().Items.Single(i => i.Key == PrivacyKey);
        Assert.True(applied.On);
        Assert.True(applied.CanUndo);
        Assert.False(applied.Restart);

        Assert.Null(await Make().TurnOffAsync(PrivacyKey));
        Assert.Empty(_system.Runs);
        Assert.Equal(1, _system.UserDwords[(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled")]);
        Assert.Equal(1, _system.UserDwords[(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled")]);
        Assert.False(_system.UserDwords.ContainsKey((@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled")));
    }

    [Fact]
    public async Task Expanded_services_trim_only_automatic_entries_and_restore_exact_start_types()
    {
        _system.Services["DiagTrack"] = (2, true);
        _system.Services["WerSvc"] = (2, false);
        _system.Services["RemoteRegistry"] = (4, false);
        _system.Services["MapsBroker"] = (3, false);
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyAsync([ServicesKey]));
        Assert.Equal((3, false), _system.Services["DiagTrack"]);
        Assert.Equal((3, false), _system.Services["WerSvc"]);
        Assert.Equal((4, false), _system.Services["RemoteRegistry"]);
        Assert.Equal((3, false), _system.Services["MapsBroker"]);

        Assert.Null(await Make().TurnOffAsync(ServicesKey));
        Assert.Equal((2, true), _system.Services["DiagTrack"]);
        Assert.Equal((2, false), _system.Services["WerSvc"]);
        Assert.Equal((4, false), _system.Services["RemoteRegistry"]);
    }

    [Fact]
    public async Task Scheduled_tasks_disable_and_restore_cleanly_while_rejecting_unlisted_paths()
    {
        _system.SupportsScheduledTasks = true;
        var ceip = TrimmedTasks[0].Path;
        var wer = TrimmedTasks[9].Path;
        var maps = TrimmedTasks[10].Path;
        _system.Tasks[ceip] = true;
        _system.Tasks[wer] = true;
        _system.Tasks[maps] = false;
        _system.OnRun = _system.Take;

        var initial = Make().Read().Items.Single(i => i.Key == TasksKey);
        Assert.False(initial.On);
        Assert.False(initial.NeedsRestart);
        Assert.Contains("2 telemetry", initial.Detail, StringComparison.Ordinal);

        Assert.Null(await Make().ApplyAsync());
        Assert.False(_system.Tasks[ceip]);
        Assert.False(_system.Tasks[wer]);
        Assert.False(_system.Tasks[maps]);

        var applied = Make().Read().Items.Single(i => i.Key == TasksKey);
        Assert.True(applied.On);
        Assert.True(applied.CanUndo);
        Assert.False(applied.Restart);

        Assert.Null(await Make().TurnOffAsync(TasksKey));
        Assert.True(_system.Tasks[ceip]);
        Assert.True(_system.Tasks[wer]);
        Assert.False(_system.Tasks[maps]);

        Assert.Null(Revert(TasksKey, new Was(Strings: [@"\Microsoft\Windows\Defender\Windows Defender Scheduled Scan"])));
        Assert.NotNull(Revert(TasksKey, new Was(Strings: [ceip])));
    }

    [Fact]
    public void Windows_features_abi_obfuscation_and_allowlist_match_windows_11()
    {
        Assert.Equal(12, System.Runtime.InteropServices.Marshal.SizeOf<WindowsFeatures.RtlFeatureConfiguration>());
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.SizeOf<WindowsFeatures.RtlFeatureConfigurationUpdate>());
        Assert.Equal(1853569164u, WindowsFeatures.ObfuscateId(48433719u));

        Assert.Null(WindowsFeatures.ParseOps(["123456=2:0"]));
        Assert.Null(WindowsFeatures.ParseOps(["48433719=9:0"]));
        Assert.Null(WindowsFeatures.ParseOps(["48433719=2:5"]));
        var ops = WindowsFeatures.ParseOps(["48433719=2:0", "60716524=reset"]);
        Assert.NotNull(ops);
        Assert.Equal(2, ops.Count);
        Assert.Equal((48433719u, false, 2u, 0u), (ops[0].Id, ops[0].Reset, ops[0].EnabledState, ops[0].EnabledStateOptions));
        Assert.Equal((60716524u, true, 0u, 0u), (ops[1].Id, ops[1].Reset, ops[1].EnabledState, ops[1].EnabledStateOptions));
    }

    [Fact]
    public async Task Windows_features_skip_os_enabled_and_revert_exact_prior_state()
    {
        _system.SupportsWinFeatures = true;
        _system.Features[48433719u] = new WindowsFeatures.FeatureSnapshot(48433719u, 1u, WindowsFeatures.PriorityUser, 1, 0);
        _system.Features[58989092u] = new WindowsFeatures.FeatureSnapshot(58989092u, 2u, 15u, null, null);
        _system.Features[60716524u] = new WindowsFeatures.FeatureSnapshot(60716524u, null, null, null, null);
        _system.Features[61391826u] = new WindowsFeatures.FeatureSnapshot(61391826u, null, null, null, null);
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyAsync());
        var cmdLine = Assert.Single(_system.Runs[0], l => l.Contains(WindowsFeatures.Flag, StringComparison.Ordinal));
        Assert.StartsWith("call \"", cmdLine, StringComparison.Ordinal);
        Assert.Contains("48433719=2:0", cmdLine, StringComparison.Ordinal);
        Assert.Contains("60716524=2:0", cmdLine, StringComparison.Ordinal);
        Assert.Contains("61391826=2:0", cmdLine, StringComparison.Ordinal);
        Assert.DoesNotContain("58989092", cmdLine, StringComparison.Ordinal);

        var applied = Make().Read().Items.Single(i => i.Key == WinFeaturesKey);
        Assert.True(applied.On && applied.CanUndo && applied.Restart);

        Assert.Null(await Make().TurnOffAsync(WinFeaturesKey));
        var revertLine = Assert.Single(_system.Runs[^1], l => l.Contains(WindowsFeatures.Flag, StringComparison.Ordinal));
        Assert.Contains("48433719=1:0", revertLine, StringComparison.Ordinal);
        Assert.Contains("60716524=reset", revertLine, StringComparison.Ordinal);
        Assert.Contains("61391826=reset", revertLine, StringComparison.Ordinal);
        Assert.Equal(1, _system.Features[48433719u].UserBootState);
        Assert.Null(_system.Features[60716524u].UserBootState);
        Assert.Null(_system.Features[61391826u].UserBootState);
        Assert.Equal(2u, _system.Features[58989092u].EffectiveState);

        Assert.Null(Revert(WinFeaturesKey, new Was(Strings: ["99999999=2:0"]), _system.SelfExe));
    }

    [Fact]
    public async Task Os_overhead_applies_p2p_widgets_and_no_auto_reboot_without_power_throttling_off()
    {
        _system.SupportsOsOverhead = true;
        _system.Dwords[(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode")] = 3;
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyAsync());
        Assert.False(_system.Dwords.ContainsKey((PowerThrottlingKey, PowerThrottlingOffValue)));
        Assert.Equal(0, _system.Dwords[(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode")]);
        Assert.Equal(0, _system.Dwords[(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests")]);
        Assert.Equal(1, _system.Dwords[(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers")]);

        Assert.Null(await Make().TurnOffAsync(OsOverheadKey));
        Assert.Equal(3, _system.Dwords[(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode")]);
        Assert.False(_system.Dwords.ContainsKey((@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests")));
        Assert.False(_system.Dwords.ContainsKey((@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers")));

        DeletionGate.DeleteFile(SavedPath);
        _system.Runs.Clear();
        _system.Laptop = true;
        Assert.Null(await Make().ApplyAsync([OsOverheadKey]));
        Assert.False(_system.Dwords.ContainsKey((PowerThrottlingKey, PowerThrottlingOffValue)));
        Assert.Equal(0, _system.Dwords[(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode")]);

        Assert.Null(Revert(OsOverheadKey, new Was(Strings: [@"SOFTWARE\Microsoft\Windows Defender|DisableAntiSpyware=1"])));
    }

    [Fact]
    public async Task Apply_playbook_batches_defaults_and_services_and_never_disables_hvci_without_explicit_opt_in()
    {
        _system.Tweaked();
        _system.SupportsScheduledTasks = true;
        _system.SupportsWinFeatures = true;
        _system.SupportsOsOverhead = true;
        _system.Tasks[TrimmedTasks[0].Path] = true;
        _system.Services["DiagTrack"] = (2, false);
        _system.Services["SysMain"] = (2, false);
        _system.Keys.Add(@"SYSTEM\CurrentControlSet\Services\vgk");
        _system.Dwords[(Hvci, "Enabled")] = 1;
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyPlaybookAsync());
        Assert.Single(_system.Runs);
        var batch = _system.Runs[0];
        Assert.Contains(batch, l => l.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(batch, l => l.Contains("sc.exe config DiagTrack start= demand", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(batch, l => l.Contains("sc.exe config SysMain start= demand", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(batch, l => l.Contains("schtasks.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(batch, l => l.Contains(WindowsFeatures.Flag, StringComparison.Ordinal));
        Assert.DoesNotContain(batch, l => l.Contains("PowerThrottlingOff", StringComparison.Ordinal));
        Assert.Contains(batch, l => l.Contains("/flushdns", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, _system.DnsFlushedCount);
        Assert.Equal(1, _system.StandbyPurgedCount);
        Assert.DoesNotContain(batch, l => l.Contains("HypervisorEnforcedCodeIntegrity", StringComparison.Ordinal));

        var items = Make().Read().Items;
        Assert.True(items.Single(i => i.Key == ServicesKey).On);
        Assert.False(items.Single(i => i.Key == IntegrityKey).On);
        Assert.NotNull(items.Single(i => i.Key == IntegrityKey).Blocked);

        _system.Keys.Remove(@"SYSTEM\CurrentControlSet\Services\vgk");
        _system.Dwords[(Hvci, "Enabled")] = 1;
        Assert.Null(await Make().ApplyPlaybookAsync());
        Assert.Equal(1, _system.Dwords[(Hvci, "Enabled")]);
        Assert.Null(await Make().ApplyAsync([IntegrityKey]));
        Assert.Equal(0, _system.Dwords[(Hvci, "Enabled")]);
    }

    [Fact]
    public void Native_scheduled_tasks_reader_queries_windows_without_throwing()
    {
        var native = new NativeSystem();
        var states = native.ScheduledTasks(TrimmedTasks.Select(t => t.Path));
        Assert.NotNull(states);
    }

    [Fact]
    public async Task Kernel_latency_applies_global_timer_on_win11_24h2_and_fth_without_myths_and_reverts_cleanly()
    {
        _system.SupportsDeepPlaybook = true;
        _system.Dwords[(MmcssKey, "SystemResponsiveness")] = 20;
        _system.Dwords[(FthKey, "Enabled")] = 1;
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyAsync([KernelLatencyKey]));
        Assert.Equal(1, _system.Dwords[(KernelSessionKey, GlobalTimerValue)]);
        Assert.Equal(20, _system.Dwords[(MmcssKey, "SystemResponsiveness")]);
        Assert.False(_system.Dwords.ContainsKey((MemoryManagement, DisablePagingExecValue)));
        Assert.Equal(0, _system.Dwords[(FthKey, "Enabled")]);
        Assert.Equal(1, _system.FthFlushedCount);

        var applied = Make().Read().Items.Single(i => i.Key == KernelLatencyKey);
        Assert.True(applied.On && applied.CanUndo && applied.Restart);

        Assert.Null(await Make().TurnOffAsync(KernelLatencyKey));
        Assert.False(_system.Dwords.ContainsKey((KernelSessionKey, GlobalTimerValue)));
        Assert.Equal(1, _system.Dwords[(FthKey, "Enabled")]);

        DeletionGate.DeleteFile(SavedPath);
        _system.Laptop = true;
        _system.OsBuildValue = 26100;
        Assert.Null(await Make().ApplyAsync([KernelLatencyKey]));
        Assert.Equal(1, _system.Dwords[(KernelSessionKey, GlobalTimerValue)]);

        DeletionGate.DeleteFile(SavedPath);
        _system.Dwords.Remove((KernelSessionKey, GlobalTimerValue));
        _system.OsBuildValue = 22631;
        Assert.Null(await Make().ApplyAsync([KernelLatencyKey]));
        Assert.False(_system.Dwords.ContainsKey((KernelSessionKey, GlobalTimerValue)));
        Assert.Null(Revert(KernelLatencyKey, new Was(Strings: [@"SOFTWARE\Evil|Key=d:1"])));
    }

    [Fact]
    public async Task Usb_power_disables_root_hub_sleep_on_desktop_omits_on_laptop_and_reverts_exact_prior_values()
    {
        var hub = $@"{UsbEnumRoot}\ROOT_HUB30\5&2bce96aa&0&0\Device Parameters";
        _system.SupportsDeepPlaybook = true;
        _system.UsbHubKeys.Add(hub);
        _system.UsbHubKeys.Add(@"SOFTWARE\Malicious\Injected");
        _system.Dwords[(hub, "EnhancedPowerManagementEnabled")] = 1;
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyAsync([UsbPowerKey]));
        Assert.Equal(0, _system.Dwords[(UsbSurpriseKey, UsbSurpriseValue)]);
        Assert.Equal(0, _system.Dwords[(hub, "EnhancedPowerManagementEnabled")]);
        Assert.Equal(0, _system.Dwords[(hub, "AllowIdleIrpInD3")]);
        Assert.Equal(0, _system.Dwords[(hub, "SelectiveSuspendEnabled")]);
        Assert.False(_system.Dwords.ContainsKey((@"SOFTWARE\Malicious\Injected", "EnhancedPowerManagementEnabled")));

        Assert.Null(await Make().TurnOffAsync(UsbPowerKey));
        Assert.False(_system.Dwords.ContainsKey((UsbSurpriseKey, UsbSurpriseValue)));
        Assert.Equal(1, _system.Dwords[(hub, "EnhancedPowerManagementEnabled")]);
        Assert.False(_system.Dwords.ContainsKey((hub, "AllowIdleIrpInD3")));
        Assert.False(_system.Dwords.ContainsKey((hub, "SelectiveSuspendEnabled")));

        DeletionGate.DeleteFile(SavedPath);
        _system.Laptop = true;
        Assert.DoesNotContain(Make().Read().Items, i => i.Key == UsbPowerKey);
        Assert.Null(Revert(UsbPowerKey, new Was(Strings: [$@"{UsbEnumRoot}\ROOT_HUB30\foo\Device Parameters|EvilValue=d:0"])));
    }

    [Fact]
    public async Task Ai_telemetry_and_storage_fs_apply_and_revert_while_respecting_win11_last_access_bit()
    {
        _system.SupportsDeepPlaybook = true;
        _system.Dwords[(FileSystemKey, LastAccessValue)] = unchecked((int)0x80000001u);
        _system.Dwords[(FileSystemKey, ShortNamesValue)] = 2;
        _system.Dwords[(AiTelemetrySpecs[4].Key, AiTelemetrySpecs[4].Name)] = 3;
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyAsync([AiTelemetryKey, StorageFsKey]));
        foreach (var spec in AiTelemetrySpecs)
            Assert.Equal(spec.Dword!.Value, _system.Dwords[(spec.Key, spec.Name)]);
        Assert.Equal(unchecked((int)0x80000001u), _system.Dwords[(FileSystemKey, LastAccessValue)]);
        Assert.Equal(1, _system.Dwords[(FileSystemKey, ShortNamesValue)]);

        Assert.Null(await Make().TurnOffAsync(AiTelemetryKey));
        Assert.Equal(3, _system.Dwords[(AiTelemetrySpecs[4].Key, AiTelemetrySpecs[4].Name)]);
        Assert.False(_system.Dwords.ContainsKey((AiTelemetrySpecs[0].Key, AiTelemetrySpecs[0].Name)));

        Assert.Null(await Make().TurnOffAsync(StorageFsKey));
        Assert.Equal(unchecked((int)0x80000001u), _system.Dwords[(FileSystemKey, LastAccessValue)]);
        Assert.Equal(2, _system.Dwords[(FileSystemKey, ShortNamesValue)]);

        _system.Dwords[(FileSystemKey, LastAccessValue)] = unchecked((int)0x80000000u);
        DeletionGate.DeleteFile(SavedPath);
        Assert.Null(await Make().ApplyAsync([StorageFsKey]));
        Assert.Equal(1, _system.Dwords[(FileSystemKey, LastAccessValue)]);
        Assert.Null(await Make().TurnOffAsync(StorageFsKey));
        Assert.Equal(unchecked((int)0x80000000u), _system.Dwords[(FileSystemKey, LastAccessValue)]);
    }

    [Fact]
    public async Task Gaming_ux_applies_without_uac_preserves_other_directx_tokens_and_clears_sticky_key_hotkey_bit()
    {
        _system.SupportsDeepPlaybook = true;
        _system.NvidiaKeys.Add($@"{DisplayClassRoot}\0000");
        _system.DirectXUser = "VRROptimizeEnable=1;";
        _system.UserDwords[(@"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference")] = 1;
        _system.UserStrings[(@"Control Panel\Accessibility\StickyKeys", "Flags")] = "510";
        _system.UserStrings[(@"Control Panel\Accessibility\Keyboard Response", "Flags")] = "122";
        _system.UserStrings[(@"Control Panel\Accessibility\ToggleKeys", "Flags")] = "62";

        var before = Make().Read().Items.Single(i => i.Key == GamingUxKey);
        Assert.False(before.On);
        Assert.False(before.NeedsRestart);

        Assert.Null(await Make().ApplyAsync([GamingUxKey]));
        Assert.Empty(_system.Runs);
        Assert.True(HasToken(_system.DirectXUser, "VRROptimizeEnable", "1"));
        Assert.True(HasToken(_system.DirectXUser, SwapEffectToken, "1"));
        Assert.Equal(3, _system.UserDwords[(@"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference")]);
        Assert.Equal(0, _system.UserDwords[(GamingUxNvidiaTelemetrySpec.Key, GamingUxNvidiaTelemetrySpec.Name)]);
        Assert.Equal("506", _system.UserStrings[(@"Control Panel\Accessibility\StickyKeys", "Flags")]);
        Assert.Equal("122", _system.UserStrings[(@"Control Panel\Accessibility\Keyboard Response", "Flags")]);
        Assert.Equal("58", _system.UserStrings[(@"Control Panel\Accessibility\ToggleKeys", "Flags")]);

        var after = Make().Read().Items.Single(i => i.Key == GamingUxKey);
        Assert.True(after.On && after.CanUndo && !after.Restart);

        Assert.Null(await Make().TurnOffAsync(GamingUxKey));
        Assert.True(HasToken(_system.DirectXUser, "VRROptimizeEnable", "1"));
        Assert.False(HasToken(_system.DirectXUser, SwapEffectToken, "1"));
        Assert.Equal(1, _system.UserDwords[(@"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference")]);
        Assert.Equal("510", _system.UserStrings[(@"Control Panel\Accessibility\StickyKeys", "Flags")]);
        Assert.Equal("62", _system.UserStrings[(@"Control Panel\Accessibility\ToggleKeys", "Flags")]);
        Assert.False(_system.UserDwords.ContainsKey((GamingUxNvidiaTelemetrySpec.Key, GamingUxNvidiaTelemetrySpec.Name)));
    }

    [Fact]
    public async Task Amd_gpu_power_and_hags_gate_by_hardware_and_revert_cleanly()
    {
        var amdAdapter = $@"{DisplayClassRoot}\0001";
        _system.SupportsDeepPlaybook = true;
        _system.AmdKeys.Add(amdAdapter);
        _system.Dwords[(amdAdapter, "EnableULPS")] = 1;
        _system.OnRun = _system.Take;

        Assert.DoesNotContain(Make().Read().Items, i => i.Key == HagsKey);
        _system.Dwords[(GraphicsDriversKey, HwSchModeValue)] = 1;
        Assert.Contains(Make().Read().Items, i => i.Key == HagsKey && !i.On);

        Assert.Null(await Make().ApplyAsync([AmdGpuPowerKey, HagsKey]));
        Assert.Equal(0, _system.Dwords[(amdAdapter, "EnableULPS")]);
        Assert.Equal(0, _system.Dwords[(amdAdapter, "EnableAspmL0s")]);
        Assert.Equal(0, _system.Dwords[(amdAdapter, "EnableAspmL1")]);
        Assert.Equal(2, _system.Dwords[(GraphicsDriversKey, HwSchModeValue)]);

        Assert.Null(await Make().TurnOffAsync(AmdGpuPowerKey));
        Assert.Null(await Make().TurnOffAsync(HagsKey));
        Assert.Equal(1, _system.Dwords[(amdAdapter, "EnableULPS")]);
        Assert.False(_system.Dwords.ContainsKey((amdAdapter, "EnableAspmL0s")));
        Assert.Equal(1, _system.Dwords[(GraphicsDriversKey, HwSchModeValue)]);

        DeletionGate.DeleteFile(SavedPath);
        _system.Laptop = true;
        Assert.DoesNotContain(Make().Read().Items, i => i.Key == AmdGpuPowerKey);
    }

    [Fact]
    public async Task Evidence_backed_deep_playbook_runs_without_myths_and_opt_ins_stay_manual()
    {
        var nvidiaAdapter = $@"{DisplayClassRoot}\0000";
        var amdAdapter = $@"{DisplayClassRoot}\0001";
        _system.SupportsDeepPlaybook = true;
        _system.NvidiaKeys.Add(nvidiaAdapter);
        _system.AmdKeys.Add(amdAdapter);
        _system.TotalMemKb = 33_554_432L;
        _system.MemoryCompressionOn = true;
        _system.Dwords[(PowerControlKey, HibernateEnabledValue)] = 1;
        _system.Dwords[(SessionPowerKey, HiberbootEnabledValue)] = 1;
        _system.Dwords[(ControlKey, SvcHostSplitValue)] = 3_670_016;
        _system.Folders[@"D:\Games\CS2"] = false;
        _system.BcdFlags["tscsyncpolicy"] = "Default";
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyPlaybookAsync());
        Assert.Single(_system.Runs);
        Assert.Equal(0, _system.Dwords[(PowerControlKey, HibernateEnabledValue)]);
        Assert.Equal(1, _system.UserDwords[(BackgroundAppsSpecs[0].Key, BackgroundAppsSpecs[0].Name)]);
        Assert.Equal(0, _system.Dwords[(EdgePolicyKey, EdgeStartupBoostValue)]);
        Assert.Equal(0, _system.Dwords[(EdgePolicyKey, EdgeBackgroundModeValue)]);
        Assert.True(_system.Folders[@"D:\Games\CS2"]);
        Assert.Equal(2, _system.Dwords[(FileSystemKey, NtfsMemoryUsageValue)]);
        Assert.Equal("0", _system.UserStrings[(@"Control Panel\Keyboard", "KeyboardDelay")]);

        Assert.False(_system.Dwords.ContainsKey((DwmKey, OverlayTestModeValue)));
        Assert.False(_system.Dwords.ContainsKey((nvidiaAdapter, "DisableDynamicPstate")));
        Assert.False(_system.Dwords.ContainsKey((MemoryManagement, FeatureOverrideValue)));
        Assert.False(_system.Dwords.ContainsKey((MemoryManagement, DisablePagingExecValue)));
        Assert.Equal(3_670_016, _system.Dwords[(ControlKey, SvcHostSplitValue)]);
        Assert.False(_system.BcdFlags.ContainsKey("disabledynamictick"));
        Assert.Equal("Default", _system.BcdFlags["tscsyncpolicy"]);
        Assert.False(_system.BcdFlags.ContainsKey("x2apicpolicy"));
        Assert.True(_system.MemoryCompressionOn);

        string[] defaultDeepKeys = [HibernateOffKey, BackgroundAppsKey, EdgeBackgroundKey, GameIndexingKey];
        foreach (var deepKey in defaultDeepKeys)
        {
            var applied = Make().Read().Items.Single(i => i.Key == deepKey);
            Assert.False(applied.OptIn);
            Assert.True(applied.On && applied.CanUndo);
            Assert.Null(await Make().TurnOffAsync(deepKey));
            var reverted = Make().Read().Items.Single(i => i.Key == deepKey);
            Assert.False(reverted.On);
            Assert.False(reverted.CanUndo);
        }

        string[] optInKeys = [MpoOffKey, GpuPstateKey, CpuMitigationsKey, MemCompressionKey];
        foreach (var optKey in optInKeys)
        {
            var before = Make().Read().Items.Single(i => i.Key == optKey);
            Assert.True(before.OptIn);
            Assert.False(before.On);
            Assert.Null(await Make().ApplyAsync([optKey]));
            var applied = Make().Read().Items.Single(i => i.Key == optKey);
            Assert.True(applied.On && applied.CanUndo);
            Assert.Null(await Make().TurnOffAsync(optKey));
            var reverted = Make().Read().Items.Single(i => i.Key == optKey);
            Assert.False(reverted.On);
        }

        Assert.NotNull(await Make().ApplyAsync([BootLatencyKey]));
        Assert.NotNull(await Make().ApplyAsync([SvcHostSplitKey]));
        Assert.Null(Revert(BootLatencyKey, new Was(Strings: ["badflag=Yes"])));
        Assert.Null(Revert(BootLatencyKey, new Was(Strings: ["disabledynamictick=bad val"])));
        Assert.Null(Revert(GpuPstateKey, new Was(Strings: [@"SOFTWARE\Evil|DisableDynamicPstate=d:1"])));
    }

    [Fact]
    public async Task Mem_compression_that_was_already_off_reverts_without_elevated_prompt()
    {
        _system.SupportsDeepPlaybook = true;
        _system.TotalMemKb = 33_554_432L;
        _system.MemoryCompressionOn = false;
        _system.OnRun = _system.Take;

        Assert.Null(await Make().ApplyAsync([MemCompressionKey]));
        var runsAfterApply = _system.Runs.Count;
        Assert.True(Make().Read().Items.Single(i => i.Key == MemCompressionKey).On);

        Assert.Null(await Make().TurnOffAsync(MemCompressionKey));
        Assert.Equal(runsAfterApply, _system.Runs.Count);
        Assert.False(_system.MemoryCompressionOn);
        Assert.False(Make().Read().Items.Single(i => i.Key == MemCompressionKey).On);
    }
}
