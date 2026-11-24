using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Best settings › System: Windows' own settings below the power plan, each with its own switch, an exact record of what
/// was there, and detection read from Windows without administrator rights. Every write is one of the fixed lines below
/// (an allowlist in code: these keys, these values, these services), run in one elevated step Windows asks for once,
/// behind a System Restore point made in that same step. Research, sources and what was left out: docs/architecture/system-tuning.md.
/// <list type="bullet">
/// <item><c>timer</c> (Best settings): Windows' own timer back where a tweak forced the platform clock (bcdedit
/// useplatformclock, seen as a 14.318 MHz performance counter): every clock read a game makes then goes to the HPET, which
/// takes far longer than the processor's own counter. Only the value is removed, and only when the elevated step finds it.</item>
/// <item><c>scheduling</c> (Best settings): Win32PrioritySeparation back to Windows' "Programs" (2) where it was set to
/// fixed quanta or no foreground boost (Background services' 0x18 among them), so the game in front gets the longer turn.
/// 0x26 and the other variable, boosted values already are Programs and are left alone.</item>
/// <item><c>pagefile</c> (Best settings): a page file again, managed by Windows, where a tweak removed it: without one the
/// commit limit is the RAM, and a game that commits past it crashes out of memory. A size the user set stays theirs.</item>
/// <item><c>cache</c> (Best settings): LargeSystemCache back to 0 where it was 1, the server setting that favors the file
/// cache over programs' working sets, games included.</item>
/// <item><c>services</c> (opt-in): two services Windows starts at boot that a gaming PC has no use for, set to Manual (never
/// Disabled): Downloaded Maps Manager (Maps starts it when it needs it) and Program Compatibility Assistant (the "this program
/// might not have installed correctly" prompts for old installers), both "OK to disable" in Microsoft's own services guidance.
/// Only those that are Automatic now; each start type and delayed flag recorded first.</item>
/// <item><c>integrity</c> (opt-in): Memory Integrity (HVCI) off, Microsoft's own advice for gamers who want the last frames,
/// with a plain warning and a restart. Never in Best settings or a first run. Refused while an anti-cheat that can require it is
/// installed (Riot Vanguard, FACEIT), and where a policy or the firmware lock holds it.</item>
/// </list>
/// Turning a switch off puts back exactly what was recorded and keeps it out of Best settings until it's on again. Nothing
/// here needs TrustedInstaller: every key is writable by Administrators, and services change through the service manager
/// (some service keys grant Administrators only read).
/// </summary>
internal sealed partial class SystemTuning(SystemTuning.ISystem system, string path, Func<DateTimeOffset> now)
{
    internal const string TimerKey = "timer", SchedulingKey = "scheduling", PagefileKey = "pagefile", CacheKey = "cache",
        CaptureServiceKey = "captureService", VrrLfcKey = "vrrLfc", PrivacyKey = "privacy",
        TasksKey = "tasks", WinFeaturesKey = "winFeatures", OsOverheadKey = "osOverhead",
        ServicesKey = "services", IntegrityKey = "integrity", VrrKey = "vrr", MouseAccelKey = "mouseAccel";

    /// <summary>The items Use best settings sets: each only puts back a Windows default a tweak changed.</summary>
    internal static readonly string[] Defaults =
    [
        TimerKey, SchedulingKey, PagefileKey, CacheKey, CaptureServiceKey, VrrLfcKey, PrivacyKey,
        TasksKey, WinFeaturesKey, OsOverheadKey,
        KernelLatencyKey, UsbPowerKey, AiTelemetryKey, StorageFsKey, GamingUxKey, AmdGpuPowerKey, HagsKey,
        MpoOffKey, HibernateOffKey, BackgroundAppsKey, SvcHostSplitKey, GpuPstateKey, CpuMitigationsKey,
        BootLatencyKey, MemCompressionKey,
    ];

    internal static readonly (string Path, string Label)[] TrimmedTasks =
    [
        (@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", "Customer Experience Consolidator"),
        (@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", "USB CEIP Telemetry"),
        (@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", "Compatibility Appraiser"),
        (@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp", "Compatibility Appraiser Exp"),
        (@"\Microsoft\Windows\Application Experience\ProgramDataUpdater", "Program Data Updater"),
        (@"\Microsoft\Windows\Autochk\Proxy", "Autochk Proxy"),
        (@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", "Disk Diagnostic Data Collector"),
        (@"\Microsoft\Windows\Feedback\Siuf\DmClient", "Feedback SIUF Client"),
        (@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload", "Feedback SIUF Scenario Download"),
        (@"\Microsoft\Windows\Windows Error Reporting\QueueReporting", "Error Reporting Queue"),
        (@"\Microsoft\Windows\Maps\MapsToastTask", "Maps Toast Task"),
        (@"\Microsoft\Windows\Maps\MapsUpdateTask", "Maps Update Task"),
        (@"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem", "Power Efficiency Diagnostics"),
        (@"\Microsoft\Windows\Diagnosis\Scheduled", "Scheduled Diagnostics"),
        (@"\Microsoft\Windows\Maintenance\WinSAT", "Windows System Assessment"),
    ];

    internal sealed record OsOverheadEntry(string Key, string Name, int Target, bool DesktopOnly);

    internal const string LegacyPowerThrottlingKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
    internal const string LegacyPowerThrottlingValue = "PowerThrottlingOff";

    internal static readonly OsOverheadEntry[] OsOverheadSettings =
    [
        new(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, DesktopOnly: false),
        new(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, DesktopOnly: false),
    ];

    internal const string DisplayClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    internal const string VrrFrameSplitBehavior = "vrrFrameSplitBehavior";
    internal const uint VrrLfcLowestMultiplierUnsigned = 0x80010055u;
    internal const int VrrLfcLowestMultiplier = unchecked((int)VrrLfcLowestMultiplierUnsigned);

    internal static readonly (string Key, string Name, int Target)[] PrivacySettings =
    [
        (@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353696Enabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
    ];

    /// <summary>The items only their own switch sets.</summary>
    internal static readonly string[] OptIn =
    [
        ServicesKey, IntegrityKey, VrrKey, MouseAccelKey,
    ];

    internal static readonly string[] Keys = [.. Defaults, .. OptIn];

    internal const string DirectXUserPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";
    internal const string DirectXGlobalSettings = "DirectXUserGlobalSettings";
    internal const string VrrToken = "VRROptimizeEnable";
    internal const string CaptureServiceName = "CaptureService";

    internal const string PriorityControl = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    internal const string Separation = "Win32PrioritySeparation";
    /// <summary>Windows' "Programs": short, variable quanta with the foreground's boost (what every client Windows ships).</summary>
    internal const int Programs = 2;

    internal const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    internal const string PagingFiles = "PagingFiles", LargeSystemCache = "LargeSystemCache";
    /// <summary>What Windows writes for "Automatically manage paging file size for all drives".</summary>
    internal const string WindowsManaged = @"?:\pagefile.sys";

    internal const string Hvci = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    internal const string HvciPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";

    /// <summary>The performance counter's rate when Windows reads the HPET (14.31818 MHz); the processor's own counter reads 10 MHz.</summary>
    internal const long HpetHz = 14_318_180;

    /// <summary>The only services this group changes, by name, with Windows' own name for them.</summary>
    internal static readonly (string Name, string Label)[] Trimmed =
    [
        ("MapsBroker", "Downloaded Maps Manager"),
        ("PcaSvc", "Program Compatibility Assistant Service"),
        ("DiagTrack", "Connected User Experiences and Telemetry"),
        ("dmwappushservice", "WAP Push Message Routing"),
        ("WerSvc", "Windows Error Reporting"),
        ("RemoteRegistry", "Remote Registry"),
        ("RetailDemo", "Retail Demo"),
        ("Fax", "Fax"),
        ("Wisvc", "Windows Insider Service"),
        ("WMPNetworkSvc", "Windows Media Player Network Sharing"),
        ("diagnosticshub.standardcollector.service", "Diagnostics Hub Standard Collector"),
        ("WdiServiceHost", "Diagnostic Service Host"),
        ("WdiSystemHost", "Diagnostic System Host"),
        ("SysMain", "SysMain (Superfetch)"),
        ("WSearch", "Windows Search Indexer"),
        ("DPS", "Diagnostic Policy Service"),
        ("DusmSvc", "Data Usage"),
    ];

    /// <summary>Anti-cheats that can require Memory Integrity on, by their service, and who they are.</summary>
    internal static readonly (string Service, string Who)[] NeedIntegrity =
    [
        ("vgk", "Riot Vanguard (VALORANT, League of Legends)"),
        ("vgc", "Riot Vanguard (VALORANT, League of Legends)"),
        ("FACEIT", "FACEIT Anti-Cheat"),
        ("FACEITService", "FACEIT Anti-Cheat"),
    ];

    internal const string IntegrityWarning = "Less secure: Windows stops checking that every driver is signed and safe, which is what keeps malicious drivers out. Only for a PC you keep clean. Up to about 5-10% more frames in some games. Takes a restart.";

    internal interface ISystem
    {
        /// <summary>The performance counter's rate (QueryPerformanceFrequency).</summary>
        long Frequency();
        /// <summary>A DWORD under HKLM, or null when it isn't there (or isn't a DWORD).</summary>
        int? Dword(string key, string name);
        /// <summary>A REG_MULTI_SZ under HKLM, or null when the value isn't there.</summary>
        string[]? Strings(string key, string name);
        bool Exists(string key);
        /// <summary>A service's start type (2 automatic, 3 manual, 4 disabled) and delayed flag; null when there's no such service.</summary>
        (int Start, bool Delayed)? Service(string name);
        /// <summary>Memory Integrity runs now (Windows' code integrity options); null when Windows doesn't say.</summary>
        bool? IntegrityRunning();
        DateTimeOffset Boot();
        /// <summary>Runs the lines as administrator, Windows asking once; false when it was declined or didn't run.</summary>
        Task<bool> Admin(IReadOnlyList<string> lines);
        /// <summary>A fresh file path the elevated step writes an answer to (no quotes in it).</summary>
        string Answer();
        /// <summary>What the elevated step wrote there (null: nothing), and the file goes unless <paramref name="keep"/>.</summary>
        string? ReadAnswer(string answer, bool keep = false);
        /// <summary>A restore point made in the last day stands for a new one (Windows keeps one a day).</summary>
        bool PointStanding { get; }
        void PointMade(RestorePoint.Outcome outcome);
        /// <summary>True when non-admin user-scope Windows toggles (VRR, mouse acceleration) can be read and written.</summary>
        bool SupportsUserHardwareToggles => false;
        string? UserDirectXSettings() => null;
        bool SetUserDirectXSettings(string? value) => false;
        int[]? MouseParams() => null;
        bool SetMouseParams(int[] values) => false;
        IReadOnlyList<string> NvidiaDisplayClassKeys() => [];
        int? UserDword(string key, string name) => null;
        bool SetUserDword(string key, string name, int? value) => false;
        /// <summary>Installed library games that require Memory Integrity (HVCI), empty in isolated tests unless set.</summary>
        IReadOnlyList<string> HvciRequiringGames() => [];
        /// <summary>The latency check, run elevated (Windows asks): its answer, or null when the prompt was declined or it didn't run.</summary>
        Task<LatencyTrace.Answer?> Latency();
        bool SupportsScheduledTasks => false;
        bool? ScheduledTaskEnabled(string taskPath) => null;
        IReadOnlyDictionary<string, bool> ScheduledTasks(IEnumerable<string> taskPaths)
        {
            var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in taskPaths)
                if (ScheduledTaskEnabled(path) is { } on) found[path] = on;
            return found;
        }
        bool SupportsWinFeatures => false;
        WindowsFeatures.FeatureSnapshot? WinFeature(uint featureId) => null;
        string? SelfExePath() => null;
        bool SupportsOsOverhead => false;
        bool IsLaptop() => false;
        bool WindowsAsks() => ElevatedCommands.WindowsAsks();
        bool SupportsDeepPlaybook => false;
        string? StringValue(string key, string name) => null;
        string? UserString(string key, string name) => null;
        bool SetUserString(string key, string name, string? value) => false;
        IReadOnlyList<string> UsbRootHubKeys() => [];
        IReadOnlyList<string> AmdDisplayClassKeys() => [];
        long TotalMemoryKb() => 0;
    }

    private static readonly HashSet<string> HvciGameKeys = new(StringComparer.Ordinal)
    {
        "valorant", "leagueoflegends", "teamfighttactics", "2xko",
    };

    /// <summary>Installed library games that rely on Riot Vanguard or FACEIT and can require Memory Integrity on.</summary>
    internal static IReadOnlyList<string> FindHvciRequiringGames(IEnumerable<GameEntry> library) =>
        [.. library
            .Where(g => g.Installed && (
                g.Store == StoreKind.Riot
                || AntiCheatCheck.IsRiot(g)
                || HvciGameKeys.Contains(StoreService.TitleKey(g.Title))
                || (AntiCheatCheck.KeptOrQuick(g) is { Kind: AntiCheatCheck.Kind.Found } ac
                    && ac.Names.Any(n => n.Equals(DlssSwapService.RiotVanguard, StringComparison.OrdinalIgnoreCase)
                        || n.Contains("FACEIT", StringComparison.OrdinalIgnoreCase)))))
            .Select(g => g.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>What a service was before Exo set it to Manual.</summary>
    internal sealed record ServiceWas(string Name, int Start, bool Delayed);

    /// <summary>
    /// What one item held before Exo changed it: a DWORD (<paramref name="Value"/>, null with <paramref name="Absent"/>: not
    /// there), a list of strings (the page file), each service's start, or the platform clock forced on. <paramref name="Probe"/>
    /// is the timer's change under way: the file its elevated step writes Windows' boot entry to before it removes anything,
    /// saved before the step, so a step Exo didn't see finish is settled from it the next time Exo reads (<see cref="Settle"/>).
    /// </summary>
    internal sealed record Was(int? Value = null, string[]? Strings = null, bool Absent = false, List<ServiceWas>? Services = null, bool Clock = false, string? Probe = null, DateTimeOffset? ProbeAt = null);

    /// <summary>
    /// What Exo keeps: the items the player turned off, what each changed item held, when each was last changed (a restart
    /// finishes it), and items a press found nothing to change for (the HPET Windows picks itself on this PC).
    /// </summary>
    internal sealed record Saved(List<string> Off, Dictionary<string, Was> Kept, Dictionary<string, DateTimeOffset> Changed, List<string> Native, List<LatencyTrace.Result>? Latency = null);

    /// <summary>
    /// One item: <paramref name="On"/> Exo's setting is in place; <paramref name="Restart"/> it was changed (or put back) since
    /// Windows started, so a restart finishes it; <paramref name="Blocked"/> why Exo won't change it here;
    /// <paramref name="NeedsRestart"/> whether applying this item requires a Windows restart.
    /// </summary>
    internal sealed record Item(string Key, string Label, string Detail, bool On, bool CanUndo, bool Off, bool OptIn, bool Restart, string? Blocked = null, bool NeedsRestart = true);

    internal sealed record State(IReadOnlyList<Item> Items)
    {
        /// <summary>Every item Best settings sets is set or turned off by the player.</summary>
        internal bool Applied => Items.Where(i => !i.OptIn).All(i => i.On || i.Off || i.Blocked is not null);
        internal bool Restart => Items.Any(i => i.Restart);
    }

    /// <summary>The items as they are, a timer change Exo didn't see finish settled first (unless a change is running now).</summary>
    internal State Read()
    {
        if (!_gate.Wait(0)) return StateOf(Load());
        try { return StateOf(Settled()); }
        finally { _gate.Release(); }
    }

    private State StateOf(Saved saved)
    {
        var boot = system.Boot();
        bool Pending(string key) => saved.Changed.TryGetValue(key, out var at) && at > boot;
        var items = new List<Item>();
        var off = saved.Off;

        // A change waits on a restart: said first, whichever way it went.
        string Tell(string key, string normal) => !Pending(key) ? normal : saved.Kept.ContainsKey(key) ? "Restart to finish." : "Goes back to how it was after a restart.";

        // The timer: the performance counter says which clock Windows reads.
        var hpet = system.Frequency() == HpetHz;
        var timerKept = saved.Kept.TryGetValue(TimerKey, out var timerWas) && timerWas.Clock;
        if (timerKept || Pending(TimerKey))
            items.Add(new(TimerKey, "Windows' own timer", Tell(TimerKey, hpet ? "Windows still reads the platform clock (HPET)." : "Games read the processor's fast clock, not the platform clock (HPET)."),
                timerKept && (!hpet || Pending(TimerKey)), timerKept, off.Contains(TimerKey), false, Pending(TimerKey)));
        else if (hpet && !saved.Native.Contains(TimerKey))
            items.Add(new(TimerKey, "Windows' own timer", "A tweak forced the platform clock (HPET), so every clock read a game makes waits on it. Takes a restart.", false, false, off.Contains(TimerKey), false, false));

        // Scheduling: Programs, or anything that gives the foreground a longer, boosted turn (takes effect immediately, no restart).
        var separation = system.Dword(PriorityControl, Separation);
        var programs = separation is not { } v || ForegroundFirst(v);
        if ((!system.SupportsDeepPlaybook && !programs) || saved.Kept.ContainsKey(SchedulingKey))
            items.Add(new(SchedulingKey, "Windows' scheduling for programs", programs ? "The game in front gets the longer turn on the processor." : "Set to favor background work, so the game in front gets no longer turn than anything behind it.",
                programs, saved.Kept.ContainsKey(SchedulingKey), off.Contains(SchedulingKey), false, false, null, NeedsRestart: false));

        // The page file.
        var none = NoPageFile(system.Strings(MemoryManagement, PagingFiles));
        if (none || saved.Kept.ContainsKey(PagefileKey) || Pending(PagefileKey))
            items.Add(new(PagefileKey, "A page file, managed by Windows", Tell(PagefileKey, none ? "There's no page file, so a game that needs more memory than your RAM crashes instead of slowing down." : "Windows sizes it for you."),
                !none, saved.Kept.ContainsKey(PagefileKey), off.Contains(PagefileKey), false, Pending(PagefileKey)));

        // The file cache.
        var large = system.Dword(MemoryManagement, LargeSystemCache) == 1;
        if (large || saved.Kept.ContainsKey(CacheKey) || Pending(CacheKey))
            items.Add(new(CacheKey, "Memory for programs first", Tell(CacheKey, large ? "Set to the server setting, which gives Windows' file cache memory before your games." : "Games keep their memory before Windows' file cache."),
                !large, saved.Kept.ContainsKey(CacheKey), off.Contains(CacheKey), false, Pending(CacheKey)));

        // Services: only those Windows starts at boot now. On while Exo holds a record of any it set to Manual, so its switch
        // always puts those back, even when Windows kept another starting with it (turning it on again tries that one again).
        var automatic = Trimmed.Where(s => system.Service(s.Name) is { Start: 2 }).ToList();
        var servicesKept = saved.Kept.ContainsKey(ServicesKey);
        if (automatic.Count > 0 || servicesKept || Pending(ServicesKey))
        {
            var manual = Trimmed.Where(s => saved.Kept.GetValueOrDefault(ServicesKey)?.Services?.Any(w => w.Name == s.Name) == true && automatic.All(a => a.Name != s.Name)).ToList();
            var detail = !servicesKept || manual.Count == 0
                ? $"{Join(automatic.Select(s => s.Label))} start{S(automatic)} with Windows; Manual starts {Them(automatic)} only when something needs {Them(automatic)}. Takes a restart."
                : automatic.Count == 0
                    ? $"{Join(manual.Select(s => s.Label))} start{S(manual)} only when something needs {Them(manual)}."
                    : $"{Join(manual.Select(s => s.Label))} start{S(manual)} only when something needs {Them(manual)}; {Join(automatic.Select(s => s.Label))} still start{S(automatic)} with Windows.";
            items.Add(new(ServicesKey, "Services set to Manual", Tell(ServicesKey, detail), servicesKept, servicesKept, false, true, Pending(ServicesKey)));
        }

        // CaptureService: restore from Disabled (4) back to Manual (3) when a tweak disabled Windows screen capture.
        var captureSvc = system.Service(CaptureServiceName);
        var captureDisabled = captureSvc is { Start: 4 };
        var captureKept = saved.Kept.ContainsKey(CaptureServiceKey);
        if (captureDisabled || captureKept)
            items.Add(new(CaptureServiceKey, "Screen Capture Service",
                captureDisabled ? "Disabled by a tweak, which breaks Windows screen capture and Game Bar. Sets it back to Manual." : "Set to Manual so Windows starts it when needed.",
                !captureDisabled, captureKept, off.Contains(CaptureServiceKey), false, false, null, NeedsRestart: false));

        var nvidiaAdapters = system.NvidiaDisplayClassKeys().Where(k => NvidiaClassKeyPattern().IsMatch(k)).ToList();
        var vrrLfcKept = saved.Kept.ContainsKey(VrrLfcKey);
        if (nvidiaAdapters.Count > 0 || vrrLfcKept || Pending(VrrLfcKey))
        {
            var vrrLfcOn = nvidiaAdapters.Count > 0 && nvidiaAdapters.All(k => system.Dword(k, VrrFrameSplitBehavior) == VrrLfcLowestMultiplier);
            var detail = Tell(VrrLfcKey, vrrLfcOn
                ? "Uses the lowest refresh-rate multiplier during G-SYNC/VRR LFC to reduce flicker and stutter."
                : "Sets NVIDIA G-SYNC/VRR LFC to the lowest refresh-rate multiplier to reduce flicker and micro-stutter. Takes a restart.");
            items.Add(new(VrrLfcKey, "NVIDIA VRR LFC lowest multiplier", detail,
                vrrLfcOn || (Pending(VrrLfcKey) && vrrLfcKept), vrrLfcKept, off.Contains(VrrLfcKey), false, Pending(VrrLfcKey)));
        }

        if (system.SupportsScheduledTasks)
        {
            var taskStates = system.ScheduledTasks(TrimmedTasks.Select(t => t.Path));
            var enabledCount = TrimmedTasks.Count(t => taskStates.TryGetValue(t.Path, out var isOn) && isOn);
            var tasksKept = saved.Kept.ContainsKey(TasksKey);
            if (enabledCount > 0 || tasksKept)
            {
                var detail = enabledCount == 0
                    ? "CEIP, Compatibility Appraiser, Feedback, Error Reporting, and Maps tasks are disabled (no restart needed)."
                    : $"{enabledCount} telemetry and background task{(enabledCount == 1 ? "" : "s")} enabled (CEIP, Appraiser, Feedback, WER, Maps); disabling reduces background wakeups.";
                items.Add(new(TasksKey, "Telemetry & background scheduled tasks", detail,
                    enabledCount == 0, tasksKept, off.Contains(TasksKey), false, false, null, NeedsRestart: false));
            }
        }

        if (system.SupportsWinFeatures)
        {
            var snapshots = WindowsFeatures.LowLatencyProfileIds.Select(id => system.WinFeature(id) ?? new WindowsFeatures.FeatureSnapshot(id, null, null, null, null)).ToList();
            var pendingFeatures = snapshots.Count(s => !WindowsFeatures.IsEnabledByOsOrUser(s));
            var winFeatKept = saved.Kept.ContainsKey(WinFeaturesKey);
            if (pendingFeatures > 0 || winFeatKept || Pending(WinFeaturesKey))
            {
                var allEnabled = pendingFeatures == 0;
                var detail = Tell(WinFeaturesKey, allEnabled
                    ? "Low Latency Profile feature flags are enabled."
                    : "Enables Windows 11 Low Latency Profile feature flags via ntdll feature control. Takes a restart.");
                items.Add(new(WinFeaturesKey, "Windows 11 Low Latency Profile", detail,
                    allEnabled || (Pending(WinFeaturesKey) && winFeatKept), winFeatKept, off.Contains(WinFeaturesKey), false, Pending(WinFeaturesKey)));
            }
        }

        if (system.SupportsOsOverhead)
        {
            var needsPtMigration = NeedsPowerThrottlingMigration(saved, out _);
            var overheadOn = !needsPtMigration && OsOverheadSettings.All(e => system.Dword(e.Key, e.Name) == e.Target);
            var overheadKept = saved.Kept.ContainsKey(OsOverheadKey);
            if (!overheadOn || overheadKept || Pending(OsOverheadKey))
            {
                const string label = "P2P updates & Widgets off";
                var detail = Tell(OsOverheadKey, overheadOn
                    ? "Delivery Optimization P2P and background Widgets feeds are off."
                    : needsPtMigration
                        ? "Removes legacy PowerThrottlingOff (which disables EcoQoS) and keeps Delivery Optimization P2P and Widgets off. Takes a restart."
                        : "Turns off Delivery Optimization P2P uploads and background Widgets/News feeds. Takes a restart.");
                items.Add(new(OsOverheadKey, label, detail,
                    overheadOn || (Pending(OsOverheadKey) && overheadKept), overheadKept, off.Contains(OsOverheadKey), false, Pending(OsOverheadKey)));
            }
        }

        if (system.SupportsDeepPlaybook)
            ReadDeepPlaybookItems(saved, Pending, Tell, items);

        // Memory Integrity: shown while it's on (or Exo turned it off).
        var enabled = system.Dword(Hvci, "Enabled");
        var running = system.IntegrityRunning();
        if (enabled == 1 || running == true || saved.Kept.ContainsKey(IntegrityKey) || Pending(IntegrityKey))
        {
            var isOff = enabled == 0;
            var blocked = isOff ? null : IntegrityBlocked();
            var restart = Pending(IntegrityKey) || isOff && running == true;
            var detail = isOff ? restart ? "Restart to finish. Turn this off to put Memory Integrity back on." : "Memory Integrity is off. Turn this off to put it back on."
                : Pending(IntegrityKey) ? "Goes back on after a restart." : blocked ?? IntegrityWarning;
            items.Add(new(IntegrityKey, "Memory Integrity off", detail, isOff, saved.Kept.ContainsKey(IntegrityKey), false, true, restart, blocked));
        }

        // Legacy non-admin undo entries only: Autopilot manages mouse acceleration automatically and Windows windowed VRR is retired.
        if (system.SupportsUserHardwareToggles)
        {
            var privacyOn = PrivacySettings.All(s => system.UserDword(s.Key, s.Name) == s.Target);
            var privacyKept = saved.Kept.ContainsKey(PrivacyKey);
            if (!privacyOn || privacyKept)
                items.Add(new(PrivacyKey, "Windows ad & suggestion noise off",
                    privacyOn
                        ? "Advertising ID, Start tips, Settings suggestions, Search highlights, and Bing Start search are off (no restart needed)."
                        : "Turns off Windows Advertising ID, Start/Settings tips, Search highlights, and Bing Start search without touching security or Defender (no admin prompt).",
                    privacyOn, privacyKept, off.Contains(PrivacyKey), false, false, null, NeedsRestart: false));

            if (saved.Kept.ContainsKey(VrrKey))
            {
                var vrrOn = HasToken(system.UserDirectXSettings(), VrrToken, "1");
                items.Add(new(VrrKey, "Windows variable refresh rate",
                    "Windows' windowed VRR setting can cause flicker or stutter; hardware G-SYNC/FreeSync works without it. Put back to restore.",
                    vrrOn, true, false, true, false, null, NeedsRestart: false));
            }

            if (saved.Kept.ContainsKey(MouseAccelKey) && system.MouseParams() is { Length: 3 } mp)
            {
                var accelOff = mp[2] == 0;
                items.Add(new(MouseAccelKey, "Disable mouse acceleration",
                    accelOff ? "Enhance pointer precision is off for 1:1 mouse movement (no restart needed)." : "Turns off Windows' Enhance pointer precision so pointer distance matches physical movement (no admin prompt).",
                    accelOff, true, false, true, false, null, NeedsRestart: false));
            }
        }
        return new State(items);
    }

    internal static bool HasToken(string? data, string name, string expectedValue) =>
        (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals($"{name}={expectedValue}", StringComparison.OrdinalIgnoreCase));

    internal static string WithToken(string? data, string name, string value)
    {
        var parts = (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add($"{name}={value}");
        return string.Join(';', parts) + ";";
    }

    internal static string? WithoutToken(string? data, string name)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        var parts = data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return parts.Count == 0 ? null : string.Join(';', parts) + ";";
    }

    /// <summary>Why Memory Integrity stays on here: an installed game, an anti-cheat that can require it, a policy, or the firmware lock. Null when Exo may turn it off.</summary>
    internal string? IntegrityBlocked()
    {
        var games = system.HvciRequiringGames();
        if (games.Count > 0) return $"{Join(games)} require{(games.Count == 1 ? "s" : "")} Memory Integrity (Riot Vanguard / FACEIT), so Autopilot keeps it on.";
        var who = NeedIntegrity.Where(a => system.Exists($@"SYSTEM\CurrentControlSet\Services\{a.Service}")).Select(a => a.Who).Distinct().ToList();
        if (who.Count > 0) return $"{Join(who)} can require Memory Integrity, so Exo leaves it on: games with it may refuse to start without it.";
        if (system.Dword(HvciPolicy, "HypervisorEnforcedCodeIntegrity") is { } policy && policy != 0) return "A policy on this PC turns Memory Integrity on, so Exo leaves it.";
        if (system.Dword(Hvci, "Locked") == 1) return "Memory Integrity is locked on in this PC's firmware, so Exo leaves it.";
        return null;
    }

    /// <summary>
    /// The foreground gets a longer, boosted turn: variable quanta (fixed is 10 in bits 2-3) and a boost (bits 0-1 not 00).
    /// Bits 4-5 (short or long) don't decide it. 2 (Programs), 0x26 and 0x16 pass; 0x18 (Background services), 0x28 and 0x24 don't.
    /// </summary>
    internal static bool ForegroundFirst(int value) => ((value >> 2) & 3) != 2 && (value & 3) != 0;

    /// <summary>No page file: the value missing, or only empty lines (what "No paging file" leaves).</summary>
    internal static bool NoPageFile(string[]? paging) => paging is null || paging.All(string.IsNullOrWhiteSpace);

    private static string S<T>(IReadOnlyCollection<T> list) => list.Count == 1 ? "s" : "";
    private static string Them<T>(IReadOnlyCollection<T> list) => list.Count == 1 ? "it" : "them";

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    internal const string Busy = "Exo is still changing your system settings. Try again when it's done.";

    /// <summary>
    /// Sets the items in <paramref name="only"/> (a switch turned on: taken out of Off), or with null every Best settings item
    /// the player keeps on that isn't set yet (never an opt-in one), in one elevated step behind a restore point. Null when all
    /// went; else what didn't, plainly.
    /// </summary>
    internal async Task<string?> ApplyAsync(IReadOnlyCollection<string>? only = null)
    {
        if (!_gate.Wait(0)) return Busy;
        try { return Logged(await ApplyNowAsync(only).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    internal async Task<string?> ApplyPlaybookAsync()
    {
        if (!_gate.Wait(0)) return Busy;
        try { return Logged(await ApplyNowAsync(null, includePlaybookExtras: true).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    /// <summary>Turns one item off: puts back what Exo recorded and keeps it out of Best settings until it's on again.</summary>
    internal async Task<string?> TurnOffAsync(string key)
    {
        if (!_gate.Wait(0)) return Busy;
        try { return Logged(await TurnOffNowAsync(key).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    private static string? Logged(string? said)
    {
        if (said is not null) AppLog.Warn("System best settings: " + said);
        return said;
    }

    private async Task<string?> ApplyNowAsync(IReadOnlyCollection<string>? only, bool includePlaybookExtras = false)
    {
        if (only is not null && only.Any(k => !Keys.Contains(k))) return "Exo doesn't know that setting.";
        var saved = Settled();
        // What a declined prompt puts back: the player's choices as they were, an Off this press took out included.
        var prior = saved;
        if (only is not null && saved.Off.Any(only.Contains))
        {
            saved = saved with { Off = [.. saved.Off.Where(k => !only.Contains(k))] };
            if (!Save(saved)) return "Exo couldn't save your choice, so nothing changed.";
        }
        var state = StateOf(saved);
        var asked = state.Items.Where(i => !i.On && (only?.Contains(i.Key) ?? ((!i.OptIn || (includePlaybookExtras && (i.Key == ServicesKey || i.Key == IntegrityKey))) && !i.Off && !saved.Off.Contains(i.Key)))).ToList();
        if (asked.FirstOrDefault(i => i.Blocked is not null) is { } held && only is not null) return held.Blocked;
        var wanted = asked.Where(i => i.Blocked is null).Select(i => i.Key).ToList();
        if (wanted.Count == 0) return null;

        var lines = new List<string>();
        var kept = new Dictionary<string, Was>(saved.Kept);
        var said = new List<string>();
        string? clockWas = null, clockAfter = null, bcdWas = null, bcdAfter = null, mmWas = null, mmAfter = null;
        List<string> tasksAttempted = [];
        List<uint> winFeaturesAttempted = [];
        var migratingPt = false;
        int? migratingPtPrior = null;
        foreach (var key in wanted)
        {
            switch (key)
            {
                case TimerKey:
                    clockWas = system.Answer();
                    clockAfter = system.Answer();
                    // Saved with the record below, before the step: what it finds there is settled even if Exo never sees it end.
                    kept[key] = (kept.TryGetValue(key, out var earlier) ? earlier : new Was()) with { Probe = clockWas, ProbeAt = now() };
                    lines.AddRange(TimerLines(clockWas, clockAfter));
                    break;
                case SchedulingKey:
                {
                    var was = system.Dword(PriorityControl, Separation);
                    kept.TryAdd(key, new Was(was, Absent: was is null));
                    lines.Add(RegDword(PriorityControl, Separation, Programs));
                    break;
                }
                case PagefileKey:
                {
                    var was = system.Strings(MemoryManagement, PagingFiles);
                    if (was is not null && was.Any(s => !SafeText().IsMatch(s)))
                    {
                        said.Add("Exo didn't recognize your page file setting, so it didn't change it.");
                        continue;
                    }
                    kept.TryAdd(key, new Was(Strings: was, Absent: was is null));
                    lines.Add(RegStrings(MemoryManagement, PagingFiles, [WindowsManaged]));
                    break;
                }
                case CacheKey:
                {
                    var was = system.Dword(MemoryManagement, LargeSystemCache);
                    kept.TryAdd(key, new Was(was, Absent: was is null));
                    lines.Add(RegDword(MemoryManagement, LargeSystemCache, 0));
                    break;
                }
                case CaptureServiceKey:
                {
                    if (system.Service(CaptureServiceName) is { Start: 4 } svc)
                    {
                        kept.TryAdd(key, new Was(Services: [new ServiceWas(CaptureServiceName, svc.Start, svc.Delayed)]));
                        lines.Add(Sc(CaptureServiceName, "demand"));
                    }
                    break;
                }
                case VrrLfcKey:
                {
                    var adapters = system.NvidiaDisplayClassKeys().Where(k => NvidiaClassKeyPattern().IsMatch(k)).ToList();
                    if (adapters.Count > 0)
                    {
                        var priorEntries = adapters.Select(k =>
                        {
                            var prev = system.Dword(k, VrrFrameSplitBehavior);
                            return prev is { } p ? $"{k}=0x{unchecked((uint)p):X8}" : $"{k}=absent";
                        }).ToArray();
                        kept.TryAdd(key, new Was(Strings: priorEntries));
                        foreach (var adapter in adapters)
                            lines.Add(RegDwordHex(adapter, VrrFrameSplitBehavior, VrrLfcLowestMultiplierUnsigned));
                    }
                    break;
                }
                case PrivacyKey:
                {
                    var priorEntries = PrivacySettings.Select(s =>
                    {
                        var prev = system.UserDword(s.Key, s.Name);
                        return prev is { } p ? $"{s.Key}|{s.Name}={p.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : $"{s.Key}|{s.Name}=absent";
                    }).ToArray();
                    kept.TryAdd(key, new Was(Strings: priorEntries));
                    break;
                }
                case TasksKey:
                {
                    var taskStates = system.ScheduledTasks(TrimmedTasks.Select(t => t.Path));
                    tasksAttempted = TrimmedTasks.Where(t => taskStates.TryGetValue(t.Path, out var isOn) && isOn).Select(t => t.Path).ToList();
                    if (tasksAttempted.Count > 0)
                    {
                        var existing = kept.TryGetValue(key, out var old) ? old.Strings ?? [] : [];
                        var merged = existing.Concat(tasksAttempted.Where(p => !existing.Contains(p, StringComparer.OrdinalIgnoreCase))).ToArray();
                        kept[key] = new Was(Strings: merged);
                        lines.AddRange(tasksAttempted.Select(p => SchTasksChange(p, disable: true)));
                    }
                    break;
                }
                case WinFeaturesKey:
                {
                    if (system.SelfExePath() is not { Length: > 0 } selfExe)
                    {
                        said.Add("Exo couldn't locate its own executable to set Windows 11 feature flags.");
                        continue;
                    }
                    var snapshots = WindowsFeatures.LowLatencyProfileIds.Select(id => system.WinFeature(id) ?? new WindowsFeatures.FeatureSnapshot(id, null, null, null, null)).ToList();
                    var toEnable = snapshots.Where(s => !WindowsFeatures.IsEnabledByOsOrUser(s)).ToList();
                    if (toEnable.Count > 0)
                    {
                        winFeaturesAttempted = toEnable.Select(s => s.Id).ToList();
                        var existing = kept.TryGetValue(key, out var old) ? old.Strings ?? [] : [];
                        var existingIds = existing.Select(ParseWinFeatureRecord).OfType<(uint Id, bool Absent, uint State, uint Options)>().Select(r => r.Id).ToHashSet();
                        var additions = toEnable.Where(s => !existingIds.Contains(s.Id)).Select(s =>
                            s.UserBootState is { } st
                                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{s.Id}={st}:{s.UserBootOptions ?? 0}")
                                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{s.Id}=absent"));
                        kept[key] = new Was(Strings: [.. existing, .. additions]);
                        lines.Add(WinFeaturesCmd(selfExe, winFeaturesAttempted.Select(id => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{id}=2:0"))));
                    }
                    break;
                }
                case OsOverheadKey:
                {
                    var priorEntries = OsOverheadSettings.Select(e =>
                    {
                        var prev = system.Dword(e.Key, e.Name);
                        return prev is { } p ? $"{e.Key}|{e.Name}={p.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : $"{e.Key}|{e.Name}=absent";
                    }).ToArray();
                    kept.TryAdd(key, new Was(Strings: priorEntries));
                    if (NeedsPowerThrottlingMigration(saved, out migratingPtPrior))
                    {
                        migratingPt = true;
                        lines.Add(migratingPtPrior is { } v
                            ? RegDword(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue, v)
                            : RegDelete(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue));
                    }
                    foreach (var entry in OsOverheadSettings)
                        if (system.Dword(entry.Key, entry.Name) != entry.Target)
                            lines.Add(RegDword(entry.Key, entry.Name, entry.Target));
                    break;
                }
                case ServicesKey:
                {
                    var now = Trimmed.Select(s => (s.Name, Start: system.Service(s.Name))).Where(s => s.Start is { Start: 2 })
                        .Select(s => new ServiceWas(s.Name, s.Start!.Value.Start, s.Start.Value.Delayed)).ToList();
                    var record = kept.TryGetValue(key, out var old) ? old.Services ?? [] : [];
                    kept[key] = new Was(Services: [.. record, .. now.Where(n => record.All(r => r.Name != n.Name))]);
                    lines.AddRange(now.Select(s => Sc(s.Name, "demand")));
                    break;
                }
                case IntegrityKey:
                {
                    var was = system.Dword(Hvci, "Enabled");
                    kept.TryAdd(key, new Was(was, Absent: was is null));
                    lines.Add(RegDword(Hvci, "Enabled", 0));
                    break;
                }
                case VrrKey:
                {
                    var raw = system.UserDirectXSettings();
                    kept.TryAdd(key, new Was(Strings: raw is null ? null : [raw], Absent: raw is null));
                    break;
                }
                case MouseAccelKey:
                {
                    if (system.MouseParams() is { Length: 3 } mp)
                        kept.TryAdd(key, new Was(Strings: [mp[0].ToString(), mp[1].ToString(), mp[2].ToString()]));
                    break;
                }
                case GamingUxKey:
                case BackgroundAppsKey:
                    PrepareDeepNonAdminBackup(key, kept);
                    break;
                case BootLatencyKey:
                    bcdWas = system.Answer();
                    bcdAfter = system.Answer();
                    lines.AddRange(BootLatencyLines(bcdWas, bcdAfter));
                    break;
                case MemCompressionKey:
                    mmWas = system.Answer();
                    mmAfter = system.Answer();
                    lines.Add(MemCompressionDisableLine(mmWas, mmAfter));
                    break;
                default:
                    if (IsDeepPlaybookKey(key))
                        PrepareDeepKey(key, kept, lines, out _);
                    break;
            }
        }
        var nonAdminKeys = wanted.Where(IsNonAdminKey).ToList();
        if (lines.Count == 0 && nonAdminKeys.Count == 0) return Said(said);

        // Recorded before anything changes, so a crash or a step cut short still has the way back.
        var changed = new Dictionary<string, DateTimeOffset>(saved.Changed);
        foreach (var key in wanted.Where(k => !DoesNotNeedRestartOnApply(k) && kept.ContainsKey(k))) changed[key] = now();
        if (!Save(saved with { Kept = kept, Changed = changed })) return "Your current settings couldn't be saved for Undo, so nothing changed.";

        if (lines.Count > 0)
        {
            if (includePlaybookExtras)
            {
                lines.Add($"{Tool("ipconfig.exe")} /flushdns");
                lines.Add(StandbyPurger.ElevatedPurgeLine());
            }
            // A restore point in the same step, first (one Windows keeps from the last day stands for it). Exo's own record is the
            // way back for each item, so a PC with System Restore off still gets the change, and is told there's no point.
            var point = system.PointStanding ? null : system.Answer();
            var steps = point is null ? lines : [RestorePoint.Step(point, "Exo Launcher: before system settings", "MODIFY_SETTINGS"), .. lines];
            if (!await system.Admin(steps).ConfigureAwait(false))
            {
                if (clockWas is not null) { system.ReadAnswer(clockWas); system.ReadAnswer(clockAfter!); }
                if (bcdWas is not null) { system.ReadAnswer(bcdWas); system.ReadAnswer(bcdAfter!); }
                if (mmWas is not null) { system.ReadAnswer(mmWas); system.ReadAnswer(mmAfter!); }
                if (point is not null) system.ReadAnswer(point);
                Save(prior);
                return "Windows' permission was declined, so nothing changed.";
            }
            if (point is not null)
            {
                var outcome = RestorePoint.Read(system.ReadAnswer(point));
                system.PointMade(outcome);
                if (outcome is RestorePoint.Outcome.Off or RestorePoint.Outcome.Failed)
                    said.Add(outcome == RestorePoint.Outcome.Off ? "System Restore is off on this PC, so there's no restore point; each switch still puts its setting back." : "Windows didn't make a restore point; each switch still puts its setting back.");
            }
        }

        // Non-admin user toggles apply directly without UAC elevation.
        var after = saved with { Kept = kept, Changed = changed };
        foreach (var key in nonAdminKeys)
        {
            if (key == VrrKey)
            {
                var next = WithToken(system.UserDirectXSettings(), VrrToken, "1");
                if (!system.SetUserDirectXSettings(next) || !HasToken(system.UserDirectXSettings(), VrrToken, "1"))
                {
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't turn variable refresh rate on.");
                }
            }
            else if (key == MouseAccelKey)
            {
                if (!system.SetMouseParams([0, 0, 0]) || system.MouseParams() is not { Length: 3 } mpAfter || mpAfter[2] != 0)
                {
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't turn mouse acceleration off.");
                }
            }
            else if (key == PrivacyKey)
            {
                var ok = PrivacySettings.All(s => system.SetUserDword(s.Key, s.Name, s.Target) && system.UserDword(s.Key, s.Name) == s.Target);
                if (!ok)
                {
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't turn off all ad and suggestion settings.");
                }
            }
            else if (IsDeepPlaybookKey(key))
            {
                ApplyDeepNonAdmin(key, ref after, prior, said);
            }
        }

        // Read back: what didn't take keeps no record, so Off never "puts back" a change that never happened.
        foreach (var key in wanted)
        {
            switch (key)
            {
                case TimerKey:
                {
                    var left = Forced(system.ReadAnswer(clockAfter!));
                    after = Settle(after, out var note);
                    if (note is not null) said.Add(note);
                    else if (left == true)
                    {
                        after = Drop(after, prior, key);
                        said.Add("Windows didn't take its own timer back.");
                    }
                    break;
                }
                case SchedulingKey when system.Dword(PriorityControl, Separation) != Programs:
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't take its scheduling for programs.");
                    break;
                case PagefileKey when NoPageFile(system.Strings(MemoryManagement, PagingFiles)) && after.Kept.ContainsKey(key):
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't take a page file.");
                    break;
                case CacheKey when system.Dword(MemoryManagement, LargeSystemCache) == 1:
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't take its memory setting.");
                    break;
                case CaptureServiceKey when system.Service(CaptureServiceName) is { Start: 4 }:
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't restore Screen Capture Service.");
                    break;
                case VrrLfcKey:
                {
                    var adapters = system.NvidiaDisplayClassKeys().Where(k => NvidiaClassKeyPattern().IsMatch(k)).ToList();
                    if (adapters.Count > 0 && !adapters.All(k => system.Dword(k, VrrFrameSplitBehavior) == VrrLfcLowestMultiplier))
                    {
                        after = Drop(after, prior, key);
                        said.Add("Windows didn't take the NVIDIA VRR LFC setting.");
                    }
                    break;
                }
                case ServicesKey:
                {
                    var still = Trimmed.Where(s => system.Service(s.Name) is { Start: 2 }).Select(s => s.Name).ToHashSet();
                    if (still.Count > 0)
                    {
                        var services = after.Kept[key].Services!.Where(w => !still.Contains(w.Name) || prior.Kept.GetValueOrDefault(key)?.Services?.Any(p => p.Name == w.Name) == true).ToList();
                        after = services.Count == 0 ? Drop(after, prior, key) : after with { Kept = new(after.Kept) { [key] = new Was(Services: services) } };
                        said.Add($"Windows didn't set {Join(Trimmed.Where(s => still.Contains(s.Name)).Select(s => s.Label))} to Manual.");
                    }
                    break;
                }
                case TasksKey when tasksAttempted.Count > 0:
                {
                    var taskStates = system.ScheduledTasks(tasksAttempted);
                    var still = tasksAttempted.Where(p => taskStates.TryGetValue(p, out var isOn) && isOn).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (still.Count > 0)
                    {
                        var prevPaths = prior.Kept.GetValueOrDefault(key)?.Strings?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                        var keptPaths = (after.Kept[key].Strings ?? []).Where(p => !still.Contains(p) || prevPaths.Contains(p)).ToArray();
                        after = keptPaths.Length == 0 ? Drop(after, prior, key) : after with { Kept = new(after.Kept) { [key] = new Was(Strings: keptPaths) } };
                        said.Add("Windows didn't disable all telemetry scheduled tasks.");
                    }
                    break;
                }
                case WinFeaturesKey when winFeaturesAttempted.Count > 0:
                {
                    var still = winFeaturesAttempted.Where(id => system.WinFeature(id) is not { } snap || !WindowsFeatures.IsEnabledByOsOrUser(snap)).ToHashSet();
                    if (still.Count > 0)
                    {
                        var prevIds = (prior.Kept.GetValueOrDefault(key)?.Strings ?? [])
                            .Select(ParseWinFeatureRecord)
                            .OfType<(uint Id, bool Absent, uint State, uint Options)>()
                            .Select(r => r.Id)
                            .ToHashSet();
                        var keptEntries = (after.Kept[key].Strings ?? [])
                            .Where(entry => ParseWinFeatureRecord(entry) is { } rec && (!still.Contains(rec.Id) || prevIds.Contains(rec.Id)))
                            .ToArray();
                        after = keptEntries.Length == 0 ? Drop(after, prior, key) : after with { Kept = new(after.Kept) { [key] = new Was(Strings: keptEntries) } };
                        said.Add("Windows didn't enable all Low Latency Profile feature flags.");
                    }
                    break;
                }
                case OsOverheadKey:
                {
                    var ptFailed = migratingPt && system.Dword(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue) != migratingPtPrior;
                    if (ptFailed || OsOverheadSettings.Any(e => system.Dword(e.Key, e.Name) != e.Target))
                    {
                        after = Drop(after, prior, key);
                        said.Add("Windows didn't apply all OS overhead settings.");
                    }
                    else if (migratingPt && after.Kept.TryGetValue(key, out var existingWas))
                    {
                        after = after with { Kept = new(after.Kept) { [key] = StripLegacyPowerThrottling(existingWas) } };
                    }
                    break;
                }
                case IntegrityKey when system.Dword(Hvci, "Enabled") == 1:
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't turn Memory Integrity off.");
                    break;
                case BootLatencyKey:
                {
                    if (SettleBootLatency(system.ReadAnswer(bcdWas!), system.ReadAnswer(bcdAfter!)) is { } bootPrior)
                    {
                        var nextKept = new Dictionary<string, Was>(after.Kept) { [BootLatencyKey] = new Was(Strings: bootPrior) };
                        var nextChanged = new Dictionary<string, DateTimeOffset>(after.Changed) { [BootLatencyKey] = now() };
                        after = after with { Kept = nextKept, Changed = nextChanged };
                    }
                    else
                    {
                        after = Drop(after, prior, key);
                        said.Add("Windows didn't apply boot latency settings.");
                    }
                    break;
                }
                case MemCompressionKey:
                {
                    if (SettleMemCompression(system.ReadAnswer(mmWas!), system.ReadAnswer(mmAfter!)) is { } mmRecord)
                    {
                        var nextKept = new Dictionary<string, Was>(after.Kept) { [MemCompressionKey] = new Was(Strings: mmRecord) };
                        after = after with { Kept = nextKept };
                    }
                    else
                    {
                        after = Drop(after, prior, key);
                        said.Add("Windows didn't disable memory compression.");
                    }
                    break;
                }
                default:
                    if (IsDeepPlaybookKey(key) && !IsNonAdminKey(key))
                        VerifyDeepApplied(key, ref after, prior, said);
                    break;
            }
        }
        // The probe goes only once what it settled is saved; until then the record saved before the step still names it.
        if (!Save(after)) said.Add("Exo couldn't save what changed; it reads it again next time.");
        else if (clockWas is not null) system.ReadAnswer(clockWas);
        AppLog.Info($"System best settings: {string.Join(", ", wanted)}{(said.Count > 0 ? "; " + string.Join(" ", said) : "")}.");
        return Said(said);
    }

    /// <summary>An item that didn't take: its record goes back to what the press found (a record from before stays).</summary>
    private static Saved Drop(Saved after, Saved prior, string key)
    {
        var kept = new Dictionary<string, Was>(after.Kept);
        var changed = new Dictionary<string, DateTimeOffset>(after.Changed);
        if (prior.Kept.TryGetValue(key, out var was)) kept[key] = was; else kept.Remove(key);
        if (prior.Changed.TryGetValue(key, out var at)) changed[key] = at; else changed.Remove(key);
        return after with { Kept = kept, Changed = changed };
    }

    private async Task<string?> TurnOffNowAsync(string key)
    {
        if (!Keys.Contains(key)) return "Exo doesn't know that setting.";
        var saved = Settled();
        if (saved.Kept.TryGetValue(key, out var was))
        {
            if (key == VrrKey)
            {
                var beforeRaw = was.Absent ? null : was.Strings?.FirstOrDefault();
                var nowRaw = system.UserDirectXSettings();
                var targetRaw = beforeRaw is not null && HasToken(beforeRaw, VrrToken, "1")
                    ? WithToken(nowRaw, VrrToken, "1")
                    : WithoutToken(nowRaw, VrrToken);
                if (!system.SetUserDirectXSettings(targetRaw)) return "Windows didn't put it back. Try again.";
                var keptVrr = new Dictionary<string, Was>(saved.Kept);
                keptVrr.Remove(key);
                saved = saved with { Kept = keptVrr };
                return Save(saved) ? null : "Exo couldn't save your choice.";
            }
            if (key == MouseAccelKey)
            {
                if (was.Strings is not { Length: 3 } parts ||
                    !int.TryParse(parts[0], out var p0) || !int.TryParse(parts[1], out var p1) || !int.TryParse(parts[2], out var p2))
                    return "Exo's record of this setting couldn't be read, so nothing changed.";
                if (!system.SetMouseParams([p0, p1, p2])) return "Windows didn't put it back. Try again.";
                var keptMouse = new Dictionary<string, Was>(saved.Kept);
                keptMouse.Remove(key);
                saved = saved with { Kept = keptMouse };
                return Save(saved) ? null : "Exo couldn't save your choice.";
            }
            if (key == PrivacyKey)
            {
                if (was.Strings is not { Length: > 0 } entries) return "Exo's record of this setting couldn't be read, so nothing changed.";
                var parsed = new List<(string SubKey, string Name, int? Value)>(entries.Length);
                foreach (var entry in entries)
                {
                    var eq = entry.LastIndexOf('=');
                    var bar = eq > 0 ? entry[..eq].LastIndexOf('|') : -1;
                    if (bar <= 0 || eq <= bar + 1) return "Exo's record of this setting couldn't be read, so nothing changed.";
                    var subKey = entry[..bar];
                    var valName = entry[(bar + 1)..eq];
                    if (!PrivacySettings.Any(s => s.Key == subKey && s.Name == valName))
                        return "Exo's record of this setting couldn't be read, so nothing changed.";
                    var rawVal = entry[(eq + 1)..];
                    if (rawVal == "absent") parsed.Add((subKey, valName, null));
                    else if (int.TryParse(rawVal, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var prevVal))
                        parsed.Add((subKey, valName, prevVal));
                    else return "Exo's record of this setting couldn't be read, so nothing changed.";
                }
                foreach (var item in parsed)
                    if (!system.SetUserDword(item.SubKey, item.Name, item.Value))
                        return "Windows didn't put it back. Try again.";
                var keptPrivacy = new Dictionary<string, Was>(saved.Kept);
                keptPrivacy.Remove(key);
                var offPrivacy = saved.Off.Contains(key) ? saved.Off : [.. saved.Off, key];
                return Save(saved with { Kept = keptPrivacy, Off = offPrivacy }) ? null : "Exo couldn't save your choice.";
            }
            if (TryTurnOffDeepNonAdmin(key, was, saved) is { } deepNonAdminResult) return deepNonAdminResult;
            if (key == GamingUxKey || key == BackgroundAppsKey) return null;
            if (key == MemCompressionKey)
            {
                if (was.Strings is not [var mmTok] || (mmTok != "was=true" && mmTok != "was=false"))
                    return "Exo's record of this setting couldn't be read, so nothing changed.";
                if (mmTok == "was=true")
                {
                    var mmCheck = system.Answer();
                    if (!await system.Admin([MemCompressionEnableLine(mmCheck)]).ConfigureAwait(false))
                    {
                        system.ReadAnswer(mmCheck);
                        return "Windows' permission was declined, so nothing changed.";
                    }
                    if (!string.Equals(system.ReadAnswer(mmCheck)?.Trim(), "True", StringComparison.OrdinalIgnoreCase))
                        return "Windows didn't put it back. Try again.";
                }
                var keptMm = new Dictionary<string, Was>(saved.Kept);
                keptMm.Remove(key);
                saved = saved with { Kept = keptMm };
            }
            else
            {
                if (Revert(key, was, system.SelfExePath()) is not { } lines) return "Exo's record of this setting couldn't be read, so nothing changed.";
                // The timer's BCD value can't be read without administrator rights: the same step reads it back after setting it.
                var check = key is TimerKey or BootLatencyKey ? system.Answer() : null;
                if (check is not null) lines = [.. lines, TimerRead(check)];
                if (!await system.Admin(lines).ConfigureAwait(false))
                {
                    if (check is not null) system.ReadAnswer(check);
                    return "Windows' permission was declined, so nothing changed.";
                }
                var back = key switch
                {
                    SchedulingKey => system.Dword(PriorityControl, Separation) == was.Value,
                    PagefileKey => SameStrings(system.Strings(MemoryManagement, PagingFiles), was.Strings),
                    CacheKey => system.Dword(MemoryManagement, LargeSystemCache) == was.Value,
                    CaptureServiceKey => was.Services is [var cs] && system.Service(cs.Name) is { } nowCs && nowCs.Start == cs.Start,
                    VrrLfcKey => VerifyVrrLfcReverted(was),
                    TasksKey => VerifyTasksReverted(was),
                    WinFeaturesKey => VerifyWinFeaturesReverted(was),
                    OsOverheadKey => VerifyOsOverheadReverted(was),
                    BootLatencyKey => system.ReadAnswer(check!)?.Contains(ReadOk, StringComparison.Ordinal) == true,
                    ServicesKey => was.Services!.All(s => system.Service(s.Name) is { } now && now.Start == s.Start && now.Delayed == s.Delayed),
                    IntegrityKey => system.Dword(Hvci, "Enabled") == was.Value,
                    _ when IsDeepPlaybookKey(key) => VerifyDeepReverted(key, was),
                    _ => Forced(system.ReadAnswer(check!)) == true,
                };
                if (!back) return "Windows didn't put it back. Try again.";
                var kept = new Dictionary<string, Was>(saved.Kept);
                kept.Remove(key);
                var changed = new Dictionary<string, DateTimeOffset>(saved.Changed);
                if (key is not (SchedulingKey or CaptureServiceKey or TasksKey)) changed[key] = now();
                saved = saved with { Kept = kept, Changed = changed };
            }
        }
        else if (OptIn.Contains(key)) return "Exo didn't change this, so there's nothing to put back.";
        if ((Defaults.Contains(key) || key == ServicesKey) && !saved.Off.Contains(key)) saved = saved with { Off = [.. saved.Off, key] };
        return Save(saved) ? null : "Exo couldn't save your choice.";
    }

    /// <summary>The last two latency checks (the one before, then the last), and what the last says.</summary>
    internal (LatencyTrace.Result? Before, LatencyTrace.Result? After, string Said) Latency()
    {
        var list = Load().Latency ?? [];
        var after = list.LastOrDefault();
        var before = list.Count > 1 ? list[^2] : null;
        return (before, after, after is null ? "" : LatencySaid(before, after));
    }

    /// <summary>Runs the latency check (Windows asks; ten seconds) and keeps it beside the one before. Null when done, else the line to show.</summary>
    internal async Task<string?> LatencyAsync()
    {
        if (!_gate.Wait(0)) return Busy;
        try
        {
            var answer = await system.Latency().ConfigureAwait(false);
            if (answer is null) return Logged("Windows' permission was declined, so nothing was measured.");
            if (answer.Result is not { } result) return Logged(answer.Problem ?? "The check didn't finish.");
            var saved = Load();
            var list = (saved.Latency ?? []).TakeLast(1).Append(result).ToList();
            Save(saved with { Latency = list });
            AppLog.Info($"System best settings: latency check: DPC {result.DpcMaxUs} µs ({result.DpcDriver}), ISR {result.IsrMaxUs} µs ({result.IsrDriver}), {result.Dpcs} DPCs and {result.Isrs} ISRs in {result.Seconds:0.#} s.");
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Under 500 µs is fine (LatencyMon's green); over 1000 µs is where audio drops out. Microsoft's guidance for a driver's
    /// own routine is far below both (an ISR under 25 µs, a DPC under 100 µs), so a driver past them is the one to update.
    /// </summary>
    internal const double Fine = 500, Long = 1000;

    /// <summary>
    /// The check in words: the longest run and its driver against what's fine, and against the check before only where the
    /// change is past the noise (a fifth and 50 µs).
    /// </summary>
    internal static string LatencySaid(LatencyTrace.Result? before, LatencyTrace.Result after)
    {
        var worst = after.DpcMaxUs >= after.IsrMaxUs ? (Us: after.DpcMaxUs, Driver: after.DpcDriver) : (Us: after.IsrMaxUs, Driver: after.IsrDriver);
        var said = worst.Us < Fine ? "Drivers hand the processor back quickly: fine for games and audio."
            : worst.Us < Long ? $"{worst.Driver ?? "A driver"} held the processor for {Math.Round(worst.Us)} µs: fine for games, close to where audio can crackle."
            : $"{worst.Driver ?? "A driver"} held the processor for {Math.Round(worst.Us)} µs: long enough for audio to crackle or a frame to slip. An update of that driver is the usual fix.";
        if (before is null) return said;
        var was = Math.Max(before.DpcMaxUs, before.IsrMaxUs);
        var change = worst.Us - was;
        if (Math.Abs(change) < 50 || Math.Abs(change) < was / 5) return said + " About the same as the check before.";
        return said + $" The longest run {(change < 0 ? "fell" : "rose")} from {Math.Round(was)} µs to {Math.Round(worst.Us)} µs since the check before.";
    }

    private static bool SameStrings(string[]? a, string[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);

    /// <summary>The lines that put back what <paramref name="was"/> recorded; null when the record holds something Exo wouldn't write.</summary>
    internal static IReadOnlyList<string>? Revert(string key, Was was, string? selfExe = null) => key switch
    {
        TimerKey => was.Clock ? [$"{Tool("bcdedit.exe")} /set {{current}} useplatformclock true"] : [],
        SchedulingKey => [was.Absent ? RegDelete(PriorityControl, Separation) : RegDword(PriorityControl, Separation, was.Value ?? Programs)],
        PagefileKey => was.Absent ? [RegDelete(MemoryManagement, PagingFiles)] : was.Strings is { } s && s.All(SafeText().IsMatch) ? [RegStrings(MemoryManagement, PagingFiles, s)] : null,
        CacheKey => [was.Absent ? RegDelete(MemoryManagement, LargeSystemCache) : RegDword(MemoryManagement, LargeSystemCache, was.Value ?? 0)],
        CaptureServiceKey => was.Services is [var cs] && cs.Name == CaptureServiceName
            ? [Sc(cs.Name, cs.Start switch { 2 => cs.Delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => "demand" })]
            : null,
        VrrLfcKey => RevertVrrLfc(was),
        TasksKey => RevertTasks(was),
        WinFeaturesKey => RevertWinFeatures(was, selfExe),
        OsOverheadKey => RevertOsOverhead(was),
        ServicesKey => was.Services is { } list && list.All(s => Trimmed.Any(t => t.Name == s.Name))
            ? list.Select(s => Sc(s.Name, s.Start switch { 2 => s.Delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => "auto" })).ToList()
            : null,
        IntegrityKey => [was.Absent ? RegDelete(Hvci, "Enabled") : RegDword(Hvci, "Enabled", was.Value ?? 1)],
        _ when IsDeepPlaybookKey(key) && !IsNonAdminKey(key) => RevertDeepKey(key, was),
        _ => null,
    };

    private static string SchTasksChange(string taskPath, bool disable) =>
        $"{Tool("schtasks.exe")} /Change /TN \"{taskPath}\" {(disable ? "/Disable" : "/Enable")}";

    private static string WinFeaturesCmd(string selfExe, IEnumerable<string> tokens) =>
        $"call \"{Quoted(selfExe)}\" {WindowsFeatures.Flag} set {string.Join(' ', tokens)}";

    private static IReadOnlyList<string>? RevertTasks(Was was)
    {
        if (was.Strings is not { Length: > 0 } paths) return null;
        if (paths.Any(p => !TrimmedTasks.Any(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))) return null;
        return paths.Select(p => SchTasksChange(p, disable: false)).ToList();
    }

    private bool VerifyTasksReverted(Was was)
    {
        if (was.Strings is not { Length: > 0 } paths) return false;
        var states = system.ScheduledTasks(paths);
        return paths.All(p => !states.TryGetValue(p, out var enabled) || enabled);
    }

    private static (uint Id, bool Absent, uint State, uint Options)? ParseWinFeatureRecord(string entry)
    {
        var eq = entry.IndexOf('=');
        if (eq <= 0 || eq >= entry.Length - 1) return null;
        if (!uint.TryParse(entry[..eq], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) return null;
        if (!WindowsFeatures.LowLatencyProfileIds.Contains(id)) return null;
        var spec = entry[(eq + 1)..];
        if (spec == "absent") return (id, true, 0u, 0u);
        var colon = spec.IndexOf(':');
        if (colon <= 0 || colon >= spec.Length - 1) return null;
        if (!uint.TryParse(spec[..colon], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var state) || state > WindowsFeatures.StateEnabled) return null;
        if (!uint.TryParse(spec[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var opts) || opts > 1u) return null;
        return (id, false, state, opts);
    }

    private static IReadOnlyList<string>? RevertWinFeatures(Was was, string? selfExe)
    {
        if (string.IsNullOrWhiteSpace(selfExe) || was.Strings is not { Length: > 0 } entries) return null;
        var tokens = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            if (ParseWinFeatureRecord(entry) is not { } rec) return null;
            tokens.Add(rec.Absent
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rec.Id}=reset")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rec.Id}={rec.State}:{rec.Options}"));
        }
        return [WinFeaturesCmd(selfExe, tokens)];
    }

    private bool VerifyWinFeaturesReverted(Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return false;
        foreach (var entry in entries)
        {
            if (ParseWinFeatureRecord(entry) is not { } rec) return false;
            var snap = system.WinFeature(rec.Id);
            if (rec.Absent)
            {
                if (snap?.UserBootState is not null) return false;
            }
            else if (snap?.UserBootState != (int)rec.State)
            {
                return false;
            }
        }
        return true;
    }

    internal async Task<bool> MigrateLegacyPowerThrottlingAsync(bool allowPrompt = false)
    {
        if (!_gate.Wait(0)) return false;
        try
        {
            var saved = Settled();
            if (!saved.Kept.TryGetValue(OsOverheadKey, out var was) || !TryGetLegacyPowerThrottlingPrior(was, out var priorValue))
                return false;
            if (priorValue == 1 || system.Dword(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue) != 1)
            {
                var nextKept = new Dictionary<string, Was>(saved.Kept) { [OsOverheadKey] = StripLegacyPowerThrottling(was) };
                Save(saved with { Kept = nextKept });
                return false;
            }
            if (!allowPrompt && system.WindowsAsks()) return false;
            var line = priorValue is { } v
                ? RegDword(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue, v)
                : RegDelete(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue);
            if (!await system.Admin([line]).ConfigureAwait(false)) return false;
            if (system.Dword(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue) != priorValue) return false;
            var kept = new Dictionary<string, Was>(saved.Kept) { [OsOverheadKey] = StripLegacyPowerThrottling(was) };
            var changed = new Dictionary<string, DateTimeOffset>(saved.Changed) { [OsOverheadKey] = now() };
            Save(saved with { Kept = kept, Changed = changed });
            AppLog.Info("System best settings: migrated legacy PowerThrottlingOff back to restore EcoQoS.");
            return true;
        }
        finally { _gate.Release(); }
    }

    internal static bool TryGetLegacyPowerThrottlingPrior(Was? was, out int? priorValue)
    {
        priorValue = null;
        if (was?.Strings is not { Length: > 0 } entries) return false;
        foreach (var entry in entries)
        {
            if (ParseOsOverheadRecord(entry) is { } rec
                && rec.SubKey == LegacyPowerThrottlingKey
                && rec.Name == LegacyPowerThrottlingValue)
            {
                priorValue = rec.Value;
                return true;
            }
        }
        return false;
    }

    private bool NeedsPowerThrottlingMigration(Saved saved, out int? priorValue)
    {
        priorValue = null;
        if (!saved.Kept.TryGetValue(OsOverheadKey, out var was)) return false;
        if (!TryGetLegacyPowerThrottlingPrior(was, out priorValue)) return false;
        if (priorValue == 1) return false;
        return system.Dword(LegacyPowerThrottlingKey, LegacyPowerThrottlingValue) == 1;
    }

    private static Was StripLegacyPowerThrottling(Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return was;
        var filtered = entries.Where(e =>
            ParseOsOverheadRecord(e) is not { } rec
            || rec.SubKey != LegacyPowerThrottlingKey
            || rec.Name != LegacyPowerThrottlingValue).ToArray();
        return was with { Strings = filtered };
    }

    private static (string SubKey, string Name, int? Value)? ParseOsOverheadRecord(string entry)
    {
        var eq = entry.LastIndexOf('=');
        var bar = eq > 0 ? entry[..eq].LastIndexOf('|') : -1;
        if (bar <= 0 || eq <= bar + 1) return null;
        var subKey = entry[..bar];
        var valName = entry[(bar + 1)..eq];
        var isLegacyPt = subKey == LegacyPowerThrottlingKey && valName == LegacyPowerThrottlingValue;
        if (!isLegacyPt && !OsOverheadSettings.Any(s => s.Key == subKey && s.Name == valName)) return null;
        var rawVal = entry[(eq + 1)..];
        if (rawVal == "absent") return (subKey, valName, null);
        return int.TryParse(rawVal, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var prevVal)
            ? (subKey, valName, prevVal)
            : null;
    }

    private static IReadOnlyList<string>? RevertOsOverhead(Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return null;
        var list = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            if (ParseOsOverheadRecord(entry) is not { } rec) return null;
            list.Add(rec.Value is { } v ? RegDword(rec.SubKey, rec.Name, v) : RegDelete(rec.SubKey, rec.Name));
        }
        return list;
    }

    private bool VerifyOsOverheadReverted(Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return false;
        foreach (var entry in entries)
        {
            if (ParseOsOverheadRecord(entry) is not { } rec) return false;
            if (system.Dword(rec.SubKey, rec.Name) != rec.Value) return false;
        }
        return true;
    }

    private static IReadOnlyList<string>? RevertVrrLfc(Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return null;
        var list = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            var idx = entry.LastIndexOf('=');
            if (idx <= 0) return null;
            var adapter = entry[..idx];
            if (!NvidiaClassKeyPattern().IsMatch(adapter)) return null;
            var val = entry[(idx + 1)..];
            if (val == "absent") list.Add(RegDelete(adapter, VrrFrameSplitBehavior));
            else if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(val[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                list.Add(RegDwordHex(adapter, VrrFrameSplitBehavior, parsed));
            else return null;
        }
        return list;
    }

    private bool VerifyVrrLfcReverted(Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return false;
        foreach (var entry in entries)
        {
            var idx = entry.LastIndexOf('=');
            if (idx <= 0) return false;
            var adapter = entry[..idx];
            var val = entry[(idx + 1)..];
            if (val == "absent")
            {
                if (system.Dword(adapter, VrrFrameSplitBehavior) is not null) return false;
            }
            else if (!val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(val[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                || system.Dword(adapter, VrrFrameSplitBehavior) != unchecked((int)parsed))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The timer's lines: bcdedit's entry for this Windows into <paramref name="was"/>; the value removed only when it's there;
    /// then the same read into <paramref name="after"/>, so Exo knows what it found and what's left.
    /// </summary>
    internal static IReadOnlyList<string> TimerLines(string was, string after) =>
    [
        TimerRead(was),
        $"({Tool("findstr.exe")} /i /c:\"useplatformclock\" \"{Quoted(was)}\" >nul && {Tool("bcdedit.exe")} /deletevalue {{current}} useplatformclock)",
        TimerRead(after),
    ];

    /// <summary>What a read of the boot entry ends with when bcdedit read it: without it the read failed, and says nothing.</summary>
    internal const string ReadOk = "exo-read-ok";

    /// <summary>bcdedit's entry for this Windows into <paramref name="answer"/>, then <see cref="ReadOk"/> only when bcdedit read it.</summary>
    internal static string TimerRead(string answer) =>
        $"({Tool("bcdedit.exe")} /enum {{current}} > \"{Quoted(answer)}\" && echo {ReadOk}>>\"{Quoted(answer)}\")";

    /// <summary>
    /// The platform clock forced in a boot entry read (<see cref="TimerRead"/>): true when the value is there (seen in any
    /// read, even one cut short), false when bcdedit read the entry and it isn't, null when the read didn't happen or failed.
    /// </summary>
    internal static bool? Forced(string? read) =>
        read is null ? null
        : read.Contains("useplatformclock", StringComparison.OrdinalIgnoreCase) ? true
        : read.Contains(ReadOk, StringComparison.Ordinal) ? false
        : null;

    internal static string RegDword(string key, string name, int value) => value < 0
        ? RegDwordHex(key, name, unchecked((uint)value))
        : $"{Tool("reg.exe")} add \"HKLM\\{key}\" /v {RegValueName(name)} /t REG_DWORD /d {value} /f";

    internal static string RegDwordHex(string key, string name, uint value) =>
        $"{Tool("reg.exe")} add \"HKLM\\{key}\" /v {RegValueName(name)} /t REG_DWORD /d 0x{value:X8} /f";

    internal static string RegSz(string key, string name, string value) =>
        $"{Tool("reg.exe")} add \"HKLM\\{key}\" /v {RegValueName(name)} /t REG_SZ /d \"{value}\" /f";

    internal static string RegStrings(string key, string name, IReadOnlyList<string> values) =>
        $"{Tool("reg.exe")} add \"HKLM\\{key}\" /v {RegValueName(name)} /t REG_MULTI_SZ /d \"{string.Join("\\0", values)}\" /f";

    internal static string RegDelete(string key, string name) => $"{Tool("reg.exe")} delete \"HKLM\\{key}\" /v {RegValueName(name)} /f";

    private static string RegValueName(string name) => name.Contains(' ', StringComparison.Ordinal) ? $"\"{name}\"" : name;

    internal static string Sc(string service, string start) => $"{Tool("sc.exe")} config {service} start= {start}";

    /// <summary>A program in System32, unquoted (the folder has no spaces), so the step never starts with a quote cmd would strip.</summary>
    private static string Tool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static string Quoted(string answer) => answer.Replace("\"", "", StringComparison.Ordinal);

    /// <summary>A page file line Exo writes back as it was: a drive or ?, a path, sizes; nothing cmd or reg reads specially.</summary>
    [GeneratedRegex(@"^([A-Za-z?]:\\[^""%^&|<>\r\n\\]*(\\[^""%^&|<>\r\n\\]+)*( \d+ \d+)?)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex SafeText();

    [GeneratedRegex(@"^SYSTEM\\CurrentControlSet\\Control\\Class\\\{4d36e968-e325-11ce-bfc1-08002be10318\}\\\d{4}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    internal static partial Regex NvidiaClassKeyPattern();

    [GeneratedRegex(@"^SYSTEM\\CurrentControlSet\\Enum\\USB\\ROOT_HUB[A-Za-z0-9_]*\\[A-Za-z0-9&_#-]+\\Device Parameters$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    internal static partial Regex UsbRootHubKeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 _.-]+$", RegexOptions.CultureInvariant)]
    internal static partial Regex SafeSzPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    internal static partial Regex SafeBcdTokenPattern();

    private static string? Said(List<string> said) => said.Count == 0 ? null : string.Join(" ", said);

    /// <summary>
    /// The timer's change under way, settled from what its step read before removing anything: the clock was forced (Exo's
    /// record: Off forces it again), it wasn't (Windows picks the HPET itself here: nothing to change, and the timer isn't
    /// offered again), or the read never came (nothing was removed, since the removal runs only behind it, and the press can be
    /// tried again). A record from an earlier change stays whatever the read says. <paramref name="note"/> says why nothing changed.
    /// </summary>
    private Saved Settle(Saved saved, out string? note)
    {
        note = null;
        if (!saved.Kept.TryGetValue(TimerKey, out var record) || record.Probe is not { } probe) return saved;
        var forced = Forced(system.ReadAnswer(probe, keep: true));
        var kept = new Dictionary<string, Was>(saved.Kept);
        var changed = new Dictionary<string, DateTimeOffset>(saved.Changed);
        var native = saved.Native;
        if (forced == true) { kept[TimerKey] = new Was(Clock: true); changed[TimerKey] = record.ProbeAt ?? now(); }
        else if (record.Clock) kept[TimerKey] = record with { Probe = null, ProbeAt = null };
        else kept.Remove(TimerKey);
        if (forced == false && !record.Clock)
        {
            native = [.. native.Where(k => k != TimerKey), TimerKey];
            note = "Windows picks the platform clock on this PC by itself, so there's nothing to change.";
        }
        else if (forced is null) note = "Exo couldn't read Windows' timer setting, so it didn't change it. Try again.";
        return saved with { Kept = kept, Changed = changed, Native = native };
    }

    /// <summary>What Exo keeps, a timer step it didn't see finish settled and saved (its probe goes once that's saved).</summary>
    private Saved Settled()
    {
        var saved = Load();
        var settled = Settle(saved, out _);
        if (!ReferenceEquals(settled, saved) && Save(settled)) system.ReadAnswer(saved.Kept[TimerKey].Probe!);
        return settled;
    }

    private Saved Load()
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } saved)
                return saved with { Off = saved.Off ?? [], Kept = saved.Kept ?? [], Changed = saved.Changed ?? [], Native = saved.Native ?? [], Latency = saved.Latency ?? [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { AppLog.Debug("System best settings: the record couldn't be read: " + ex.GetType().Name); }
        return new Saved([], [], [], [], []);
    }

    private bool Save(Saved saved)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("System best settings: the record couldn't be saved: " + ex.Message);
            return false;
        }
    }
}
