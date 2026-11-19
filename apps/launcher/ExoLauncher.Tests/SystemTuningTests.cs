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

        public Task<bool> Admin(IReadOnlyList<string> lines)
        {
            Runs.Add(lines);
            if (!Accept) return Task.FromResult(false);
            foreach (var line in lines)
            {
                var path = _issued.FirstOrDefault(p => line.Contains($"\"{p}\"", StringComparison.Ordinal));
                if (line.Contains("/enum {current}", StringComparison.Ordinal))
                    _answers[path!] = BcdFails ? "" : (Clock ? "useplatformclock        Yes\r\n" : "") + "identifier              {current}\r\n" + ReadOk + "\r\n";
                else if (line.Contains("/deletevalue {current} useplatformclock", StringComparison.Ordinal))
                {
                    // Behind findstr: only when the read before found the value.
                    if (_answers.GetValueOrDefault(path!)?.Contains("useplatformclock", StringComparison.Ordinal) == true && !BcdRefuses) Clock = false;
                }
                else if (line.Contains("/set {current} useplatformclock true", StringComparison.Ordinal)) { if (!BcdRefuses) Clock = true; }
            }
            // The restore point's answer: the one fresh path no line names (its step is encoded).
            foreach (var fresh in _issued.Where(p => _seen.Add(p) && !lines.Any(l => l.Contains($"\"{p}\"", StringComparison.Ordinal))))
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

        /// <summary>A PC a tweak tool went over: forced HPET, Background services, no page file, the server cache.</summary>
        public void Tweaked()
        {
            Hz = HpetHz;
            Dwords[(PriorityControl, Separation)] = 0x18;
            Multi[(MemoryManagement, PagingFiles)] = [""];
            Dwords[(MemoryManagement, LargeSystemCache)] = 1;
        }

        /// <summary>Windows taking every line it ran (registry and service lines, as reg.exe and sc.exe would).</summary>
        public void Take(IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
            {
                if (line.Contains(" add ", StringComparison.Ordinal) && line.Contains("REG_DWORD", StringComparison.Ordinal))
                {
                    var key = line.Split('"')[1][5..];
                    var name = line.Split(" /v ")[1].Split(' ')[0];
                    Dwords[(key, name)] = int.Parse(line.Split(" /d ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (line.Contains("REG_MULTI_SZ", StringComparison.Ordinal))
                {
                    var key = line.Split('"')[1][5..];
                    Multi[(key, PagingFiles)] = line.Split(" /d \"")[1].Split("\" /f")[0].Split("\\0");
                }
                else if (line.Contains(" delete ", StringComparison.Ordinal))
                {
                    var key = line.Split('"')[1][5..];
                    var name = line.Split(" /v ")[1].Split(' ')[0];
                    Dwords.Remove((key, name));
                    Multi.Remove((key, name));
                }
                else if (line.Contains(" config ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    var name = parts[^3];
                    Services[name] = parts[^1] switch { "demand" => (3, false), "delayed-auto" => (2, true), "auto" => (2, false), _ => (4, false) };
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
        Assert.All(state.Items.Where(i => !i.OptIn), i => Assert.True(i.On && i.CanUndo && i.Restart));
        Assert.Equal("Restart to finish.", state.Items.Single(i => i.Key == SchedulingKey).Detail);
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
        Assert.All(Trimmed, s => Assert.DoesNotContain(s.Name, new[] { "WinDefend", "Audiosrv", "BFE", "XblAuthManager", "CaptureService", "GraphicsPerfSvc", "wuauserv", "WSearch", "vgc", "BEService", "EasyAntiCheat" }));
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
}
