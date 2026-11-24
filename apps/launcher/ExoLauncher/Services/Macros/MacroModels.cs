using System.Text.Json.Serialization;

namespace ExoLauncher.Services.Macros;

internal static class MacroDelivery
{
    internal const string Software = "software";
    internal const string Onboard = "onboard";
}

internal static class MacroPlaybackMode
{
    internal const string Once = "once";
    internal const string Repeat = "repeat";
    internal const string Hold = "hold";
    internal const string Toggle = "toggle";
}

internal static class MacroStepKind
{
    internal const string KeyDown = "keyDown";
    internal const string KeyUp = "keyUp";
    internal const string MouseDown = "mouseDown";
    internal const string MouseUp = "mouseUp";
    internal const string MouseMove = "mouseMove";
    internal const string MouseWheel = "mouseWheel";
    internal const string PadButtonDown = "padButtonDown";
    internal const string PadButtonUp = "padButtonUp";
    internal const string PadAxis = "padAxis";
    internal const string Delay = "delay";
    internal const string Text = "text";
    internal const string LaunchApp = "launchApp";
    internal const string Layer = "layer";
}

internal sealed record MacroInputSpec
{
    public string DeviceKind { get; init; } = "keyboard";
    public string? DeviceId { get; init; }
    public string Code { get; init; } = "";
}

internal sealed record MacroTrigger
{
    public IReadOnlyList<MacroInputSpec> Chord { get; init; } = [];
    public int ChordWindowMs { get; init; } = 45;
    public string LayerId { get; init; } = "base";
    public string Mode { get; init; } = MacroPlaybackMode.Once;
    public int RepeatCount { get; init; } = 1;
    public int JitterMs { get; init; }
    public bool SwallowInput { get; init; } = true;
}

internal sealed record MacroStep
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = MacroStepKind.Delay;
    public string? Code { get; init; }
    public int? Dx { get; init; }
    public int? Dy { get; init; }
    public double? Value { get; init; }
    public double DelayMs { get; init; }
    public string? Text { get; init; }
    public string? AppPath { get; init; }
    public string? LayerId { get; init; }
}

internal sealed record MacroBinding
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Delivery { get; init; } = MacroDelivery.Software;
    public string? OnboardDeviceId { get; init; }
    public MacroTrigger Trigger { get; init; } = new();
    public IReadOnlyList<MacroStep> Steps { get; init; } = [];
    public string UpdatedAt { get; init; } = "";
}

internal sealed record MacroLayer
{
    public string Id { get; init; } = "base";
    public string Name { get; init; } = "Base";
    public string Activation { get; init; } = "base";
    public MacroInputSpec? Trigger { get; init; }
}

internal sealed record MacroProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
    public IReadOnlyList<string> GameIds { get; init; } = [];
    public IReadOnlyList<string> AppExecutables { get; init; } = [];
    public string ActiveLayerId { get; init; } = "base";
    public IReadOnlyList<MacroLayer> Layers { get; init; } = [new MacroLayer()];
    public IReadOnlyList<MacroBinding> Bindings { get; init; } = [];
}

internal sealed record MacroGameConsent
{
    public string GameId { get; init; } = "";
    public string AntiCheatLabel { get; init; } = "";
    public string ConfirmedAt { get; init; } = "";
}

internal sealed record MacroGamePolicy
{
    public string GameId { get; init; } = "";
    public string Title { get; init; } = "";
    public string AntiCheatKind { get; init; } = "none";
    public IReadOnlyList<string> AntiCheatNames { get; init; } = [];
    public string? AntiCheatLabel { get; init; }
    public bool SoftwareAllowed { get; init; }
    public string AllowedTier { get; init; } = "onboard-only";
    public bool RequiresConfirmation { get; init; }
    public bool Confirmed { get; init; }
    public bool AutoEnableAllowed { get; init; }
    public string? SoftwareWarning { get; init; }
    public string OnboardNote { get; init; } = "Tier 1 onboard device memory macros run in hardware with no software injection.";
}

internal sealed record MacroDeviceCapability
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "keyboard";
    public bool SupportsOnboard { get; init; }
    public string? Protocol { get; init; }
    public string? Connection { get; init; }
}

internal sealed record MacroLatencySummary
{
    public int Samples { get; init; }
    public double P50Us { get; init; }
    public double P95Us { get; init; }
    public double P99Us { get; init; }
    public double MaxUs { get; init; }
    public double TargetUs { get; init; } = 1000.0;
}

internal sealed record MacroStepTrace
{
    public string StepId { get; init; } = "";
    public string Kind { get; init; } = "";
    public double TargetOffsetMs { get; init; }
    public double ActualOffsetMs { get; init; }
    public double ErrorUs { get; init; }
}

internal sealed record MacroTestRunResult
{
    public bool Ok { get; init; }
    public string? Message { get; init; }
    public string Delivery { get; init; } = MacroDelivery.Software;
    public double TotalMs { get; init; }
    public MacroLatencySummary Latency { get; init; } = new();
    public IReadOnlyList<MacroStepTrace> Trace { get; init; } = [];
}

internal sealed record MacroStoreDocument
{
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public string ActiveProfileId { get; init; } = "desktop";
    public List<MacroProfile> Profiles { get; init; } = [];
    public List<MacroGameConsent> Consents { get; init; } = [];
}

internal sealed record MacroStateSnapshot
{
    public bool Ok { get; init; } = true;
    public bool Enabled { get; init; }
    public string ActiveProfileId { get; init; } = "desktop";
    public string? ActiveGameId { get; init; }
    public bool SoftwarePausedForAntiCheat { get; init; }
    public string? PauseReason { get; init; }
    public bool Recording { get; init; }
    public string? RecordingMacroId { get; init; }
    public int DebounceMs { get; init; }
    public int ExitGraceMs { get; init; }
    public int HardwareWriteCooldownMs { get; init; }
    public IReadOnlyList<MacroProfile> Profiles { get; init; } = [];
    public IReadOnlyList<MacroDeviceCapability> Devices { get; init; } = [];
    public IReadOnlyList<MacroGamePolicy> GamePolicies { get; init; } = [];
    public MacroLatencySummary Latency { get; init; } = new();

    [JsonIgnore]
    public int ProfileSwitchCount { get; init; }
}
