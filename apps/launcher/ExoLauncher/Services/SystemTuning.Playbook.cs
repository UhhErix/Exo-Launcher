using System.Globalization;

namespace ExoLauncher.Services;

internal sealed partial class SystemTuning
{
    internal const string KernelLatencyKey = "kernelLatency",
        UsbPowerKey = "usbPower",
        AiTelemetryKey = "aiTelemetry",
        StorageFsKey = "storageFs",
        GamingUxKey = "gamingUx",
        AmdGpuPowerKey = "amdGpuPower",
        HagsKey = "hags",
        MpoOffKey = "mpoOff",
        HibernateOffKey = "hibernateOff",
        BackgroundAppsKey = "backgroundApps",
        SvcHostSplitKey = "svcHostSplit",
        GpuPstateKey = "gpuPstate",
        CpuMitigationsKey = "cpuMitigations";

    internal const string KernelSessionKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
        GlobalTimerValue = "GlobalTimerResolutionRequests",
        MmcssKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
        MmcssGamesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
        FthKey = @"SOFTWARE\Microsoft\FTH",
        UsbEnumRoot = @"SYSTEM\CurrentControlSet\Enum\USB",
        UsbSurpriseKey = @"SYSTEM\CurrentControlSet\Control\USB\AutomaticSurpriseRemoval",
        UsbSurpriseValue = "AttemptRecoveryFromUsbPowerDrain",
        FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem",
        LastAccessValue = "NtfsDisableLastAccessUpdate",
        ShortNamesValue = "NtfsDisable8dot3NameCreation",
        GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
        HwSchModeValue = "HwSchMode",
        DwmKey = @"SOFTWARE\Microsoft\Windows\Dwm",
        OverlayTestModeValue = "OverlayTestMode",
        PowerControlKey = @"SYSTEM\CurrentControlSet\Control\Power",
        HibernateEnabledValue = "HibernateEnabled",
        SessionPowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
        HiberbootEnabledValue = "HiberbootEnabled",
        ControlKey = @"SYSTEM\CurrentControlSet\Control",
        SvcHostSplitValue = "SvcHostSplitThresholdInKB",
        FeatureOverrideValue = "FeatureSettingsOverride",
        FeatureOverrideMaskValue = "FeatureSettingsOverrideMask",
        SwapEffectToken = "SwapEffectUpgradeEnable";

    internal sealed record HklmSpec(string Key, string Name, int? Dword = null, string? Sz = null, bool DesktopOnly = false, bool AcceptLowTwoBitsBit0 = false);

    internal sealed record HkcuDwordSpec(string Key, string Name, int Target);

    internal static readonly HklmSpec[] KernelLatencySpecs =
    [
        new(KernelSessionKey, GlobalTimerValue, Dword: 1, DesktopOnly: true),
        new(MmcssKey, "SystemResponsiveness", Dword: 10),
        new(MmcssGamesKey, "GPU Priority", Dword: 8),
        new(MmcssGamesKey, "Priority", Dword: 6),
        new(MmcssGamesKey, "Scheduling Category", Sz: "High"),
        new(MmcssGamesKey, "SFIO Priority", Sz: "High"),
        new(FthKey, "Enabled", Dword: 0),
    ];

    internal static readonly string[] UsbHubPowerValues =
    [
        "EnhancedPowerManagementEnabled",
        "AllowIdleIrpInD3",
        "SelectiveSuspendEnabled",
    ];

    internal static readonly HklmSpec[] AiTelemetrySpecs =
    [
        new(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", Dword: 1),
        new(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", Dword: 0),
        new(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", Dword: 1),
        new(@"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", Dword: 1),
        new(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", Dword: 0),
        new(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", Dword: 1),
        new(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowDeviceNameInTelemetry", Dword: 0),
        new(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AITEnable", Dword: 0),
        new(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", Dword: 1),
        new(@"SOFTWARE\Policies\Microsoft\Windows\WDI\{9c5a40da-b965-4fc3-8781-88dd50a6299d}", "ScenarioExecutionEnabled", Dword: 0),
        new(@"SOFTWARE\Policies\Microsoft\Windows\System", "RSoPLogging", Dword: 0),
        new(@"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", Dword: 1),
    ];

    internal static readonly HklmSpec[] StorageFsSpecs =
    [
        new(FileSystemKey, LastAccessValue, Dword: 1, AcceptLowTwoBitsBit0: true),
        new(FileSystemKey, ShortNamesValue, Dword: 1),
    ];

    internal static readonly HklmSpec[] HagsSpecs =
    [
        new(GraphicsDriversKey, HwSchModeValue, Dword: 2),
    ];

    internal static readonly HklmSpec[] MpoOffSpecs =
    [
        new(DwmKey, OverlayTestModeValue, Dword: 5),
    ];

    internal static readonly HklmSpec[] CpuMitigationsSpecs =
    [
        new(MemoryManagement, FeatureOverrideValue, Dword: 3),
        new(MemoryManagement, FeatureOverrideMaskValue, Dword: 3),
    ];

    internal static readonly HkcuDwordSpec[] GamingUxDwordSpecs =
    [
        new(@"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference", 3),
        new(@"System\GameConfigStore", "GameDVR_Enabled", 0),
        new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
    ];

    internal static readonly HkcuDwordSpec GamingUxNvidiaTelemetrySpec =
        new(@"Software\NVIDIA Corporation\NVControlPanel2\Client", "OptInOrOutPreference", 0);

    internal static readonly string[] AccessibilityHotkeyKeys =
    [
        @"Control Panel\Accessibility\StickyKeys",
        @"Control Panel\Accessibility\Keyboard Response",
        @"Control Panel\Accessibility\ToggleKeys",
    ];

    internal static readonly HkcuDwordSpec[] BackgroundAppsSpecs =
    [
        new(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),
        new(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0),
    ];

    internal static bool IsDeepPlaybookKey(string key) => key is KernelLatencyKey or UsbPowerKey or AiTelemetryKey
        or StorageFsKey or GamingUxKey or AmdGpuPowerKey or HagsKey or MpoOffKey or HibernateOffKey
        or BackgroundAppsKey or SvcHostSplitKey or GpuPstateKey or CpuMitigationsKey;

    internal static bool IsNonAdminKey(string key) => key is VrrKey or MouseAccelKey or PrivacyKey or GamingUxKey or BackgroundAppsKey;

    internal static bool DoesNotNeedRestartOnApply(string key) =>
        key is TimerKey or SchedulingKey or CaptureServiceKey or PrivacyKey or TasksKey or GamingUxKey or BackgroundAppsKey or VrrKey or MouseAccelKey;

    internal static string? TokenValue(string? data, string name) =>
        (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) is { } match
            ? match[(name.Length + 1)..]
            : null;

    private static bool SpecMatches(ISystem system, HklmSpec spec)
    {
        if (spec.Sz is { } sz)
            return string.Equals(system.StringValue(spec.Key, spec.Name), sz, StringComparison.OrdinalIgnoreCase);
        var val = system.Dword(spec.Key, spec.Name);
        return spec.AcceptLowTwoBitsBit0
            ? val is { } v && (v & 3) == 1
            : val == spec.Dword;
    }

    private IReadOnlyList<HklmSpec> ActiveHklmSpecs(string key, bool laptop) => key switch
    {
        KernelLatencyKey => [.. KernelLatencySpecs.Where(s => !s.DesktopOnly || !laptop)],
        UsbPowerKey => laptop ? [] : BuildUsbPowerSpecs(),
        AiTelemetryKey => AiTelemetrySpecs,
        StorageFsKey => StorageFsSpecs,
        AmdGpuPowerKey => laptop ? [] : BuildAmdPowerSpecs(),
        HagsKey => HagsSpecs,
        MpoOffKey => MpoOffSpecs,
        SvcHostSplitKey => BuildSvcHostSpecs(),
        GpuPstateKey => laptop ? [] : BuildGpuPstateSpecs(),
        CpuMitigationsKey => CpuMitigationsSpecs,
        _ => [],
    };

    private IReadOnlyList<HklmSpec> BuildUsbPowerSpecs()
    {
        var list = new List<HklmSpec> { new(UsbSurpriseKey, UsbSurpriseValue, Dword: 0, DesktopOnly: true) };
        foreach (var hubKey in system.UsbRootHubKeys())
        {
            if (!UsbRootHubKeyPattern().IsMatch(hubKey)) continue;
            foreach (var val in UsbHubPowerValues)
                list.Add(new(hubKey, val, Dword: 0, DesktopOnly: true));
        }
        return list;
    }

    private IReadOnlyList<HklmSpec> BuildAmdPowerSpecs()
    {
        var list = new List<HklmSpec>();
        foreach (var k in system.AmdDisplayClassKeys().Where(x => NvidiaClassKeyPattern().IsMatch(x)))
        {
            list.Add(new(k, "EnableULPS", Dword: 0, DesktopOnly: true));
            list.Add(new(k, "EnableAspmL0s", Dword: 0, DesktopOnly: true));
            list.Add(new(k, "EnableAspmL1", Dword: 0, DesktopOnly: true));
        }
        return list;
    }

    private IReadOnlyList<HklmSpec> BuildSvcHostSpecs()
    {
        var kb = system.TotalMemoryKb();
        if (kb < 4_194_304L) return [];
        var target = (int)Math.Clamp(kb, 4_194_304L, 268_435_456L);
        return [new(ControlKey, SvcHostSplitValue, Dword: target)];
    }

    private IReadOnlyList<HklmSpec> BuildGpuPstateSpecs()
    {
        var list = new List<HklmSpec>();
        foreach (var k in system.NvidiaDisplayClassKeys().Where(x => NvidiaClassKeyPattern().IsMatch(x)))
            list.Add(new(k, "DisableDynamicPstate", Dword: 1, DesktopOnly: true));
        foreach (var k in system.AmdDisplayClassKeys().Where(x => NvidiaClassKeyPattern().IsMatch(x)))
        {
            list.Add(new(k, "DisablePowerGating", Dword: 1, DesktopOnly: true));
            list.Add(new(k, "PP_GPUPowerDownEnabled", Dword: 0, DesktopOnly: true));
            list.Add(new(k, "DisableDynamicPstate", Dword: 1, DesktopOnly: true));
        }
        return list;
    }

    private IReadOnlyList<HkcuDwordSpec> ActiveGamingUxDwords() =>
        system.NvidiaDisplayClassKeys().Any(x => NvidiaClassKeyPattern().IsMatch(x))
            ? [.. GamingUxDwordSpecs, GamingUxNvidiaTelemetrySpec]
            : GamingUxDwordSpecs;

    private static bool AccessibilityHotkeyOff(string? raw) =>
        raw is null || (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && (parsed & 4) == 0);

    private bool IsGamingUxApplied()
    {
        if (!HasToken(system.UserDirectXSettings(), SwapEffectToken, "1")) return false;
        if (!ActiveGamingUxDwords().All(s => system.UserDword(s.Key, s.Name) == s.Target)) return false;
        return AccessibilityHotkeyKeys.All(k => AccessibilityHotkeyOff(system.UserString(k, "Flags")));
    }

    private void ReadDeepPlaybookItems(Saved saved, Func<string, bool> pending, Func<string, string, string> tell, List<Item> items)
    {
        var laptop = system.IsLaptop();
        var off = saved.Off;

        void AddHklmDefault(string key, string label, string onDetail, string offDetail, bool forceShow = true)
        {
            var specs = ActiveHklmSpecs(key, laptop);
            var kept = saved.Kept.ContainsKey(key);
            var isPend = pending(key);
            if ((specs.Count == 0 || !forceShow) && !kept && !isPend) return;
            var allSet = specs.Count > 0 && specs.All(s => SpecMatches(system, s));
            items.Add(new(key, label, tell(key, allSet ? onDetail : offDetail),
                allSet || (isPend && kept), kept, off.Contains(key), false, isPend));
        }

        void AddHklmOptIn(string key, string label, string onDetail, string offDetail)
        {
            var specs = ActiveHklmSpecs(key, laptop);
            var kept = saved.Kept.ContainsKey(key);
            var isPend = pending(key);
            if (specs.Count == 0 && !kept && !isPend) return;
            var allSet = key == SvcHostSplitKey
                ? (specs.Count == 1 && system.Dword(ControlKey, SvcHostSplitValue) is { } cur && cur >= specs[0].Dword!.Value)
                : specs.Count > 0 && specs.All(s => SpecMatches(system, s));
            items.Add(new(key, label, tell(key, allSet ? onDetail : offDetail),
                allSet || (isPend && kept), kept, false, true, isPend));
        }

        AddHklmDefault(
            KernelLatencyKey,
            "Kernel timer, MMCSS & FTH latency tuning",
            laptop ? "MMCSS game scheduling priority high and Fault Tolerant Heap off." : "Global timer resolution requests on, MMCSS game priority high, and FTH off.",
            laptop ? "Sets MMCSS game scheduling priority high and turns off Fault Tolerant Heap. Takes a restart." : "Enables global timer resolution requests for games, boosts MMCSS game priority, and turns off FTH. Takes a restart.");

        if (!laptop || saved.Kept.ContainsKey(UsbPowerKey) || pending(UsbPowerKey))
            AddHklmDefault(
                UsbPowerKey,
                "USB controller & hub power saving off",
                "USB root hubs stay out of selective suspend and D3 sleep for consistent input polling.",
                "Stops Windows powering down desktop USB root hubs between mouse and keyboard inputs. Takes a restart.",
                forceShow: !laptop);

        AddHklmDefault(
            AiTelemetryKey,
            "Windows AI Recall, Copilot & telemetry policies off",
            "Windows Recall, Click to Do, Copilot, telemetry policies, and WPBT are disabled.",
            "Disables Windows Recall, Click to Do, Copilot, CEIP telemetry policies, RSoP logging, and WPBT without breaking updates or Defender. Takes a restart.");

        AddHklmDefault(
            StorageFsKey,
            "NTFS last-access & 8.3 short names off",
            "NTFS last-access timestamp writes and legacy 8.3 filename generation are disabled.",
            "Turns off NTFS last-access directory writes and legacy 8.3 short-name generation to trim disk overhead. Takes a restart.");

        var uxOn = IsGamingUxApplied();
        var uxKept = saved.Kept.ContainsKey(GamingUxKey);
        items.Add(new(
            GamingUxKey,
            "Windowed optimizations, Game DVR & hotkey traps off",
            uxOn
                ? "Windowed swap-effect upgrades on; Game DVR, voice call ducking, and Sticky/Filter/Toggle hotkey traps off (no restart needed)."
                : "Turns on DirectX windowed game optimizations and disables background Game DVR, call volume ducking, and Shift hotkey popups (no admin prompt).",
            uxOn, uxKept, off.Contains(GamingUxKey), false, false, null, NeedsRestart: false));

        if (!laptop && BuildAmdPowerSpecs().Count > 0 || saved.Kept.ContainsKey(AmdGpuPowerKey) || pending(AmdGpuPowerKey))
            AddHklmDefault(
                AmdGpuPowerKey,
                "AMD GPU ULPS & PCIe ASPM power gating off",
                "Radeon ULPS sleep and PCIe ASPM link power gating are disabled.",
                "Disables Radeon ULPS deep sleep and PCIe ASPM L0s/L1 link power gating on desktop GPUs. Takes a restart.",
                forceShow: !laptop && BuildAmdPowerSpecs().Count > 0);

        var hagsVal = system.Dword(GraphicsDriversKey, HwSchModeValue);
        var hagsShow = hagsVal == 1 || saved.Kept.ContainsKey(HagsKey) || pending(HagsKey);
        if (hagsShow)
            AddHklmDefault(
                HagsKey,
                "Hardware-accelerated GPU scheduling",
                "Hardware-accelerated GPU scheduling (HAGS) is enabled.",
                "Hardware-accelerated GPU scheduling was turned off; restores it on for lower frame presentation overhead. Takes a restart.");

        AddHklmOptIn(
            MpoOffKey,
            "Multiplane Overlay (MPO) off",
            "Desktop Window Manager Multiplane Overlay is disabled.",
            "Fixes flicker or micro-stutter on mixed-refresh multi-monitor setups; keep off unless you hit stutter. Takes a restart.");

        var hibKept = saved.Kept.ContainsKey(HibernateOffKey);
        var hibPend = pending(HibernateOffKey);
        if (!laptop || hibKept || hibPend)
        {
            var hibOff = system.Dword(PowerControlKey, HibernateEnabledValue) == 0
                && system.Dword(SessionPowerKey, HiberbootEnabledValue) is null or 0;
            items.Add(new(
                HibernateOffKey,
                "Hibernation & Fast Startup off",
                tell(HibernateOffKey, hibOff ? "Hibernation and Fast Startup are off for clean kernel boots and free drive space." : "Reclaims hiberfil.sys drive space and ensures clean kernel boots on desktop PCs. Takes a restart."),
                hibOff || (hibPend && hibKept), hibKept, false, true, hibPend));
        }

        var bgOn = BackgroundAppsSpecs.All(s => system.UserDword(s.Key, s.Name) == s.Target);
        var bgKept = saved.Kept.ContainsKey(BackgroundAppsKey);
        items.Add(new(
            BackgroundAppsKey,
            "Background Store apps off",
            bgOn
                ? "Microsoft Store background tasks are paused (no restart needed)."
                : "Stops Microsoft Store apps running silently in the background (may pause Phone Link or Mail notifications; no restart needed).",
            bgOn, bgKept, false, true, false, null, NeedsRestart: false));

        AddHklmOptIn(
            SvcHostSplitKey,
            "Consolidate service hosts",
            "Service host split threshold matches system RAM to consolidate svchost processes.",
            "Merges dozens of svchost.exe processes into shared hosts to lower background process overhead. Takes a restart.");

        AddHklmOptIn(
            GpuPstateKey,
            "Force max GPU clocks (no P-state drops)",
            "Dynamic GPU P-state downclocking is disabled for steady desktop clock speeds.",
            "Stops desktop GPU clock-ramp micro-stutters by disabling dynamic P-states (raises idle power and heat). Takes a restart.");

        AddHklmOptIn(
            CpuMitigationsKey,
            "CPU Spectre/Meltdown mitigations off",
            "OS CPU Spectre/Meltdown mitigations are disabled. Turn off to restore protection.",
            "Less secure: disables OS Spectre/Meltdown mitigations for extra CPU throughput on a dedicated gaming PC. Takes a restart.");
    }

    private void PrepareDeepKey(string key, Dictionary<string, Was> kept, List<string> lines, out List<HklmSpec> recordedSpecs)
    {
        recordedSpecs = [];
        var laptop = system.IsLaptop();
        if (key == HibernateOffKey)
        {
            var hibPrev = system.Dword(PowerControlKey, HibernateEnabledValue);
            var fastPrev = system.Dword(SessionPowerKey, HiberbootEnabledValue);
            kept.TryAdd(key, new Was(Strings:
            [
                $"{PowerControlKey}|{HibernateEnabledValue}={(hibPrev is { } hp ? $"d:{hp.ToString(CultureInfo.InvariantCulture)}" : "absent")}",
                $"{SessionPowerKey}|{HiberbootEnabledValue}={(fastPrev is { } fp ? $"d:{fp.ToString(CultureInfo.InvariantCulture)}" : "absent")}",
            ]));
            lines.Add($"{Tool("powercfg.exe")} /h off");
            if (fastPrev != 0)
                lines.Add(RegDword(SessionPowerKey, HiberbootEnabledValue, 0));
            return;
        }

        var specs = ActiveHklmSpecs(key, laptop);
        if (specs.Count == 0) return;
        recordedSpecs = [.. specs];
        var priorEntries = specs.Select(s =>
        {
            if (s.Sz is not null)
            {
                var prev = system.StringValue(s.Key, s.Name);
                return prev is not null ? $"{s.Key}|{s.Name}=s:{prev}" : $"{s.Key}|{s.Name}=absent";
            }
            var prevDw = system.Dword(s.Key, s.Name);
            return prevDw is { } d ? $"{s.Key}|{s.Name}=d:{d.ToString(CultureInfo.InvariantCulture)}" : $"{s.Key}|{s.Name}=absent";
        }).ToArray();
        kept.TryAdd(key, new Was(Strings: priorEntries));

        foreach (var s in specs)
        {
            if (SpecMatches(system, s)) continue;
            if (s.Sz is { } sz)
            {
                if (SafeSzPattern().IsMatch(sz)) lines.Add(RegSz(s.Key, s.Name, sz));
            }
            else if (s.Dword is { } dw)
            {
                lines.Add(RegDword(s.Key, s.Name, dw));
            }
        }

        if (key == KernelLatencyKey && system.Dword(FthKey, "Enabled") != 0)
            lines.Add($"{Tool("rundll32.exe")} fthsvc.dll,FthSysprepSpecialize");
    }

    private void ApplyDeepNonAdmin(string key, ref Saved after, Saved prior, List<string> said)
    {
        if (key == GamingUxKey)
        {
            var dxOk = system.SetUserDirectXSettings(WithToken(system.UserDirectXSettings(), SwapEffectToken, "1"))
                && HasToken(system.UserDirectXSettings(), SwapEffectToken, "1");
            var dwOk = ActiveGamingUxDwords().All(s => system.UserDword(s.Key, s.Name) == s.Target || (system.SetUserDword(s.Key, s.Name, s.Target) && system.UserDword(s.Key, s.Name) == s.Target));
            var axOk = true;
            foreach (var axKey in AccessibilityHotkeyKeys)
            {
                var raw = system.UserString(axKey, "Flags");
                if (raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && (parsed & 4) != 0)
                {
                    var next = (parsed & ~4).ToString(CultureInfo.InvariantCulture);
                    if (!system.SetUserString(axKey, "Flags", next) || system.UserString(axKey, "Flags") != next)
                    {
                        axOk = false;
                        break;
                    }
                }
            }
            if (!dxOk || !dwOk || !axOk)
            {
                after = Drop(after, prior, key);
                said.Add("Windows didn't apply all gaming UX settings.");
            }
        }
        else if (key == BackgroundAppsKey)
        {
            var ok = BackgroundAppsSpecs.All(s => system.SetUserDword(s.Key, s.Name, s.Target) && system.UserDword(s.Key, s.Name) == s.Target);
            if (!ok)
            {
                after = Drop(after, prior, key);
                said.Add("Windows didn't turn off background Store apps.");
            }
        }
    }

    private void PrepareDeepNonAdminBackup(string key, Dictionary<string, Was> kept)
    {
        if (key == GamingUxKey)
        {
            var entries = new List<string>
            {
                $"dx={(TokenValue(system.UserDirectXSettings(), SwapEffectToken) is { } tok ? tok : "absent")}",
            };
            foreach (var s in ActiveGamingUxDwords())
            {
                var prev = system.UserDword(s.Key, s.Name);
                entries.Add(prev is { } p ? $"{s.Key}|{s.Name}=d:{p.ToString(CultureInfo.InvariantCulture)}" : $"{s.Key}|{s.Name}=absent");
            }
            foreach (var axKey in AccessibilityHotkeyKeys)
            {
                var prev = system.UserString(axKey, "Flags");
                entries.Add(prev is not null ? $"{axKey}|Flags=s:{prev}" : $"{axKey}|Flags=absent");
            }
            kept.TryAdd(key, new Was(Strings: [.. entries]));
        }
        else if (key == BackgroundAppsKey)
        {
            var entries = BackgroundAppsSpecs.Select(s =>
            {
                var prev = system.UserDword(s.Key, s.Name);
                return prev is { } p ? $"{s.Key}|{s.Name}={p.ToString(CultureInfo.InvariantCulture)}" : $"{s.Key}|{s.Name}=absent";
            }).ToArray();
            kept.TryAdd(key, new Was(Strings: entries));
        }
    }

    private string? TryTurnOffDeepNonAdmin(string key, Was was, Saved saved)
    {
        if (key == BackgroundAppsKey)
            return RevertUserDwordList(key, was, saved, BackgroundAppsSpecs);

        if (key != GamingUxKey) return null;
        if (was.Strings is not { Length: > 0 } entries) return "Exo's record of this setting couldn't be read, so nothing changed.";
        string? dxTokenBefore = null;
        bool hasDx = false;
        var dwReverts = new List<(string Key, string Name, int? Value)>();
        var axReverts = new List<(string Key, string Name, string? Value)>();
        foreach (var entry in entries)
        {
            if (entry.StartsWith("dx=", StringComparison.Ordinal))
            {
                var val = entry[3..];
                if (val == "absent") dxTokenBefore = null;
                else if (SafeSzPattern().IsMatch(val)) dxTokenBefore = val;
                else return "Exo's record of this setting couldn't be read, so nothing changed.";
                hasDx = true;
                continue;
            }
            var parsed = ParseTaggedEntry(entry);
            if (parsed is null) return "Exo's record of this setting couldn't be read, so nothing changed.";
            var (subKey, name, kind, rawVal) = parsed.Value;
            if (GamingUxDwordSpecs.Any(s => s.Key == subKey && s.Name == name)
                || (GamingUxNvidiaTelemetrySpec.Key == subKey && GamingUxNvidiaTelemetrySpec.Name == name))
            {
                if (kind == 'a') dwReverts.Add((subKey, name, null));
                else if (kind == 'd' && int.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv)) dwReverts.Add((subKey, name, dv));
                else return "Exo's record of this setting couldn't be read, so nothing changed.";
            }
            else if (name == "Flags" && AccessibilityHotkeyKeys.Contains(subKey))
            {
                if (kind == 'a') axReverts.Add((subKey, name, null));
                else if (kind == 's' && rawVal is not null) axReverts.Add((subKey, name, rawVal));
                else return "Exo's record of this setting couldn't be read, so nothing changed.";
            }
            else
            {
                return "Exo's record of this setting couldn't be read, so nothing changed.";
            }
        }
        if (hasDx)
        {
            var nowRaw = system.UserDirectXSettings();
            var targetRaw = dxTokenBefore is not null
                ? WithToken(nowRaw, SwapEffectToken, dxTokenBefore)
                : WithoutToken(nowRaw, SwapEffectToken);
            if (!system.SetUserDirectXSettings(targetRaw)) return "Windows didn't put it back. Try again.";
        }
        foreach (var dw in dwReverts)
            if (!system.SetUserDword(dw.Key, dw.Name, dw.Value))
                return "Windows didn't put it back. Try again.";
        foreach (var ax in axReverts)
            if (!system.SetUserString(ax.Key, ax.Name, ax.Value))
                return "Windows didn't put it back. Try again.";
        return CompleteNonAdminTurnOff(saved, key);
    }

    private string? RevertUserDwordList(string key, Was was, Saved saved, IReadOnlyList<HkcuDwordSpec> allowed)
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
            if (!allowed.Any(s => s.Key == subKey && s.Name == valName))
                return "Exo's record of this setting couldn't be read, so nothing changed.";
            var rawVal = entry[(eq + 1)..];
            if (rawVal == "absent") parsed.Add((subKey, valName, null));
            else if (int.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prevVal))
                parsed.Add((subKey, valName, prevVal));
            else return "Exo's record of this setting couldn't be read, so nothing changed.";
        }
        foreach (var item in parsed)
            if (!system.SetUserDword(item.SubKey, item.Name, item.Value))
                return "Windows didn't put it back. Try again.";
        return CompleteNonAdminTurnOff(saved, key);
    }

    private string? CompleteNonAdminTurnOff(Saved saved, string key)
    {
        var kept = new Dictionary<string, Was>(saved.Kept);
        kept.Remove(key);
        var off = Defaults.Contains(key) && !saved.Off.Contains(key) ? (List<string>)[.. saved.Off, key] : saved.Off;
        return Save(saved with { Kept = kept, Off = off }) ? null : "Exo couldn't save your choice.";
    }

    private void VerifyDeepApplied(string key, ref Saved after, Saved prior, List<string> said)
    {
        if (key == HibernateOffKey)
        {
            if (system.Dword(PowerControlKey, HibernateEnabledValue) != 0
                || system.Dword(SessionPowerKey, HiberbootEnabledValue) is not (null or 0))
            {
                after = Drop(after, prior, key);
                said.Add("Windows didn't turn off hibernation.");
            }
            return;
        }
        var specs = ActiveHklmSpecs(key, system.IsLaptop());
        if (specs.Count == 0) return;
        var ok = key == SvcHostSplitKey
            ? (system.Dword(ControlKey, SvcHostSplitValue) is { } cur && cur >= specs[0].Dword!.Value)
            : specs.All(s => SpecMatches(system, s));
        if (!ok)
        {
            after = Drop(after, prior, key);
            said.Add($"Windows didn't apply all {key} settings.");
        }
    }

    private static (string SubKey, string Name, char Kind, string? Value)? ParseTaggedEntry(string entry)
    {
        var eq = entry.LastIndexOf('=');
        var bar = eq > 0 ? entry[..eq].LastIndexOf('|') : -1;
        if (bar <= 0 || eq <= bar + 1) return null;
        var subKey = entry[..bar];
        var valName = entry[(bar + 1)..eq];
        var spec = entry[(eq + 1)..];
        if (spec == "absent") return (subKey, valName, 'a', null);
        if (spec.Length < 3 || spec[1] != ':') return null;
        var kind = spec[0];
        if (kind is not ('d' or 's')) return null;
        return (subKey, valName, kind, spec[2..]);
    }

    private static bool IsAllowedHklmEntry(string key, string subKey, string valName) => key switch
    {
        KernelLatencyKey => KernelLatencySpecs.Any(s => s.Key == subKey && s.Name == valName),
        UsbPowerKey => (subKey == UsbSurpriseKey && valName == UsbSurpriseValue)
            || (UsbRootHubKeyPattern().IsMatch(subKey) && UsbHubPowerValues.Contains(valName)),
        AiTelemetryKey => AiTelemetrySpecs.Any(s => s.Key == subKey && s.Name == valName),
        StorageFsKey => StorageFsSpecs.Any(s => s.Key == subKey && s.Name == valName),
        AmdGpuPowerKey => NvidiaClassKeyPattern().IsMatch(subKey) && valName is "EnableULPS" or "EnableAspmL0s" or "EnableAspmL1",
        HagsKey => GraphicsDriversKey == subKey && HwSchModeValue == valName,
        MpoOffKey => DwmKey == subKey && OverlayTestModeValue == valName,
        SvcHostSplitKey => ControlKey == subKey && SvcHostSplitValue == valName,
        GpuPstateKey => NvidiaClassKeyPattern().IsMatch(subKey) && valName is "DisableDynamicPstate" or "DisablePowerGating" or "PP_GPUPowerDownEnabled",
        CpuMitigationsKey => CpuMitigationsSpecs.Any(s => s.Key == subKey && s.Name == valName),
        HibernateOffKey => (subKey == PowerControlKey && valName == HibernateEnabledValue)
            || (subKey == SessionPowerKey && valName == HiberbootEnabledValue),
        _ => false,
    };

    private static IReadOnlyList<string>? RevertDeepKey(string key, Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return null;
        if (key == HibernateOffKey)
        {
            var lines = new List<string>();
            bool restoreHibernateOn = true;
            foreach (var entry in entries)
            {
                var parsed = ParseTaggedEntry(entry);
                if (parsed is null || !IsAllowedHklmEntry(key, parsed.Value.SubKey, parsed.Value.Name)) return null;
                var (subKey, name, kind, raw) = parsed.Value;
                if (name == HibernateEnabledValue)
                {
                    if (kind == 'd' && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hv) && hv == 0)
                        restoreHibernateOn = false;
                    else if (kind != 'a' && (kind != 'd' || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                        return null;
                }
                else if (name == HiberbootEnabledValue)
                {
                    if (kind == 'a') lines.Add(RegDelete(subKey, name));
                    else if (kind == 'd' && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fv))
                        lines.Add(RegDword(subKey, name, fv));
                    else return null;
                }
            }
            if (restoreHibernateOn) lines.Insert(0, $"{Tool("powercfg.exe")} /h on");
            return lines;
        }

        var list = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            var parsed = ParseTaggedEntry(entry);
            if (parsed is null || !IsAllowedHklmEntry(key, parsed.Value.SubKey, parsed.Value.Name)) return null;
            var (subKey, name, kind, raw) = parsed.Value;
            if (kind == 'a')
            {
                list.Add(RegDelete(subKey, name));
            }
            else if (kind == 'd' && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv))
            {
                list.Add(RegDword(subKey, name, dv));
            }
            else if (kind == 's' && raw is not null && SafeSzPattern().IsMatch(raw))
            {
                list.Add(RegSz(subKey, name, raw));
            }
            else
            {
                return null;
            }
        }
        return list;
    }

    private bool VerifyDeepReverted(string key, Was was)
    {
        if (was.Strings is not { Length: > 0 } entries) return false;
        foreach (var entry in entries)
        {
            var parsed = ParseTaggedEntry(entry);
            if (parsed is null) return false;
            var (subKey, name, kind, raw) = parsed.Value;
            if (kind == 'a')
            {
                if (key == HibernateOffKey && name == HibernateEnabledValue)
                {
                    if (system.Dword(subKey, name) == 0) return false;
                }
                else if (subKey == MmcssGamesKey && name is "Scheduling Category" or "SFIO Priority")
                {
                    if (system.StringValue(subKey, name) is not null) return false;
                }
                else if (system.Dword(subKey, name) is not null)
                {
                    return false;
                }
            }
            else if (kind == 'd')
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected)) return false;
                if (system.Dword(subKey, name) != expected) return false;
            }
            else if (kind == 's')
            {
                if (system.StringValue(subKey, name) != raw) return false;
            }
        }
        return true;
    }
}
