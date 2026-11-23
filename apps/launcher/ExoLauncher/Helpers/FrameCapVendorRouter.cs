using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal static class FrameCapVendorRouter
{
    internal const string BackendOwnInterface = "own_interface";
    internal const string BackendNvidiaDrs = "nvidia_drs";
    internal const string BackendIntelIgcl = "intel_igcl";
    internal const string BackendAmdChill = "amd_chill";
    internal const string BackendNone = "none";

    internal sealed record VendorCapInput(
        IReadOnlyList<string> Adapters,
        bool NvidiaAvailable,
        bool AmdAdlxAvailable,
        bool AmdChillSupported,
        bool IntelIgclAvailable,
        bool IntelFrameLimitSupported,
        bool IntelPerAppSupported,
        bool HasOwnInterfaceCap,
        string? OwnInterfaceSettingName,
        bool AllowsOwnInterface,
        bool IsRiot,
        AntiCheatCheck.Answer AntiCheat);

    internal sealed record CapBackendOption(
        string Id,
        string Vendor,
        string Label,
        bool Available,
        bool PerGameNative,
        string LatencyNote,
        string AntiCheatNote,
        string? Reason = null);

    internal sealed record VendorCapRouting(
        string ActiveVendor,
        string? ActiveAdapter,
        bool IsHybridLaptop,
        string? IntegratedAdapter,
        string RecommendedBackend,
        bool CanApply,
        string Summary,
        IReadOnlyList<CapBackendOption> Backends);

    internal static VendorCapRouting Route(VendorCapInput input)
    {
        var adapters = input.Adapters
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();

        var discrete = adapters.FirstOrDefault(GraphicsService.Discrete);
        var integrated = adapters.FirstOrDefault(a => !GraphicsService.Discrete(a));
        var isHybrid = discrete is not null && integrated is not null;

        var vendors = GraphicsService.Vendors(adapters);
        var activeVendor = GraphicsService.ActiveVendor(
            vendors,
            input.NvidiaAvailable,
            input.AmdAdlxAvailable,
            input.IntelIgclAvailable) ?? vendors.FirstOrDefault() ?? "unknown";

        var activeAdapter = discrete
            ?? adapters.FirstOrDefault(a => GraphicsService.Vendors([a]).Contains(activeVendor))
            ?? adapters.FirstOrDefault();

        var backends = new List<CapBackendOption>();

        if (input.HasOwnInterfaceCap)
        {
            var settingLabel = string.IsNullOrWhiteSpace(input.OwnInterfaceSettingName)
                ? "In-game limiter"
                : $"In-game limiter ({input.OwnInterfaceSettingName})";
            if (input.IsRiot)
            {
                backends.Add(new CapBackendOption(
                    BackendOwnInterface,
                    "game",
                    settingLabel,
                    Available: false,
                    PerGameNative: true,
                    LatencyNote: "Lowest latency (0 extra queued frames).",
                    AntiCheatNote: "Blocked on Riot games; driver-profile caps stay safe.",
                    Reason: "Exo never writes game config files for Riot or Vanguard games."));
            }
            else if (!input.AllowsOwnInterface)
            {
                backends.Add(new CapBackendOption(
                    BackendOwnInterface,
                    "game",
                    settingLabel,
                    Available: false,
                    PerGameNative: true,
                    LatencyNote: "Lowest latency (0 extra queued frames).",
                    AntiCheatNote: "Config file write blocked by anti-cheat policy; driver cap stays safe.",
                    Reason: "Anti-cheat check did not allow writing this game's settings file."));
            }
            else
            {
                backends.Add(new CapBackendOption(
                    BackendOwnInterface,
                    "game",
                    settingLabel,
                    Available: true,
                    PerGameNative: true,
                    LatencyNote: "Lowest latency (0 extra queued frames).",
                    AntiCheatNote: "Safe OwnInterface settings write; zero injection or hooks."));
            }
        }

        if (activeVendor == "nvidia" || (vendors.Contains("nvidia") && input.NvidiaAvailable))
        {
            var available = input.NvidiaAvailable;
            backends.Add(new CapBackendOption(
                BackendNvidiaDrs,
                "nvidia",
                "NVIDIA Driver (DRS)",
                Available: available,
                PerGameNative: true,
                LatencyNote: "Low latency (~0.5 frame); per-game DRS Max Frame Rate (0x10835002).",
                AntiCheatNote: "External driver profile; safe on every anti-cheat class, zero injection.",
                Reason: available ? null : "NVIDIA driver DRS interface is not available on this PC."));
        }

        if (activeVendor == "amd" || (vendors.Contains("amd") && input.AmdAdlxAvailable))
        {
            var available = input.AmdAdlxAvailable && input.AmdChillSupported;
            string? reason = !input.AmdAdlxAvailable
                ? "AMD ADLX is not available; set a per-game cap in AMD Software instead."
                : !input.AmdChillSupported
                    ? "AMD Radeon Chill is not supported by this GPU or driver; set a cap in AMD Software instead."
                    : null;
            backends.Add(new CapBackendOption(
                BackendAmdChill,
                "amd",
                "AMD Radeon Chill",
                Available: available,
                PerGameNative: false,
                LatencyNote: "Low latency (~0–0.5 frame) with Chill Min = Max; applied on launch and restored on exit.",
                AntiCheatNote: "External driver API; safe on every anti-cheat class, zero injection.",
                Reason: reason));
        }

        if (activeVendor == "intel" || (vendors.Contains("intel") && input.IntelIgclAvailable))
        {
            var available = input.IntelIgclAvailable && input.IntelFrameLimitSupported;
            string? reason = !input.IntelIgclAvailable
                ? "Intel IGCL is not available; set a frame limit in Intel Graphics Software instead."
                : !input.IntelFrameLimitSupported
                    ? "This Intel driver does not expose IGCL Frame Limit."
                    : null;
            var label = input.IntelPerAppSupported ? "Intel IGCL (per-app)" : "Intel IGCL (session)";
            var latency = input.IntelPerAppSupported
                ? "Native per-app IGCL Frame Limit; zero injection."
                : "IGCL Frame Limit applied on launch and restored on exit; zero injection.";
            backends.Add(new CapBackendOption(
                BackendIntelIgcl,
                "intel",
                label,
                Available: available,
                PerGameNative: input.IntelPerAppSupported,
                LatencyNote: latency,
                AntiCheatNote: "External driver API; safe on every anti-cheat class, zero injection.",
                Reason: reason));
        }

        var recommended = PickRecommended(backends, activeVendor);
        var canApply = recommended is not null;
        var summary = BuildSummary(recommended, activeVendor, activeAdapter, isHybrid, backends);

        return new VendorCapRouting(
            activeVendor,
            activeAdapter,
            isHybrid,
            integrated,
            recommended?.Id ?? BackendNone,
            canApply,
            summary,
            backends);
    }

    private static CapBackendOption? PickRecommended(List<CapBackendOption> backends, string activeVendor)
    {
        var own = backends.FirstOrDefault(b => b.Id == BackendOwnInterface && b.Available);
        if (own is not null) return own;
        var vendorMatch = backends.FirstOrDefault(b => b.Vendor == activeVendor && b.Available);
        if (vendorMatch is not null) return vendorMatch;
        return backends.FirstOrDefault(b => b.Available);
    }

    private static string BuildSummary(
        CapBackendOption? recommended,
        string activeVendor,
        string? activeAdapter,
        bool isHybrid,
        List<CapBackendOption> backends)
    {
        if (recommended is not null)
        {
            var hybridSuffix = isHybrid && !string.IsNullOrWhiteSpace(activeAdapter)
                ? $" on {activeAdapter}"
                : "";
            return $"{recommended.Label}{hybridSuffix}";
        }
        var firstReason = backends.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Reason))?.Reason;
        if (firstReason is not null) return firstReason;
        return activeVendor == "unknown"
            ? "No supported GPU driver frame limiter detected."
            : $"No automatic frame limiter available for {activeVendor.ToUpperInvariant()}; use the in-game limiter.";
    }
}
