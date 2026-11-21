using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal static class WindowsFeatures
{
    internal const string Flag = "--win-features";
    internal static readonly uint[] LowLatencyProfileIds = [48433719u, 58989092u, 60716524u, 61391826u];

    internal const uint ConfigTypeBoot = 0u;
    internal const uint ConfigTypeRuntime = 1u;
    internal const uint StateDefault = 0u;
    internal const uint StateDisabled = 1u;
    internal const uint StateEnabled = 2u;
    internal const uint PriorityUser = 8u;
    internal const uint OpFeatureState = 1u;
    internal const uint OpResetState = 4u;

    private const int BsdFeatureConfigurationState = 17;
    private const int BsdStateBootPending = 1;

    internal const string OverridesUserKey = @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\8";

    [StructLayout(LayoutKind.Sequential)]
    internal struct RtlFeatureConfiguration
    {
        public uint FeatureId;
        public uint CompactState;
        public uint VariantPayload;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RtlFeatureConfigurationUpdate
    {
        public uint FeatureId;
        public uint Priority;
        public uint EnabledState;
        public uint EnabledStateOptions;
        public uint Variant;
        public uint VariantPayloadKind;
        public uint VariantPayload;
        public uint Operation;
    }

    internal sealed record FeatureSnapshot(uint Id, uint? EffectiveState, uint? EffectivePriority, int? UserBootState, int? UserBootOptions);

    internal sealed record FeatureOp(uint Id, bool Reset, uint EnabledState, uint EnabledStateOptions);

    [DllImport("ntdll.dll")]
    private static extern int RtlQueryFeatureConfiguration(
        uint featureId,
        uint configurationType,
        ref ulong changeStamp,
        out RtlFeatureConfiguration featureConfiguration);

    [DllImport("ntdll.dll")]
    private static extern int RtlSetFeatureConfigurations(
        ref ulong previousChangeStamp,
        uint configurationType,
        RtlFeatureConfigurationUpdate[] featureConfigurations,
        int featureConfigurationCount);

    [DllImport("ntdll.dll")]
    private static extern int RtlSetSystemBootStatus(
        int bsdItemType,
        ref int data,
        int dataLength,
        IntPtr returnLength);

    internal static uint ObfuscateId(uint featureId)
    {
        var mixed = BinaryPrimitives.ReverseEndianness(featureId ^ 0x74161A4Eu) ^ 0x8FB23D4Fu;
        return BitOperations.RotateLeft(mixed, 1) ^ 0x833EA8FFu;
    }

    internal static bool IsEnabledByOsOrUser(FeatureSnapshot s) =>
        s.UserBootState == (int)StateEnabled
        || (s.EffectiveState == StateEnabled && s.EffectivePriority is not null and not PriorityUser);

    internal static FeatureSnapshot Query(uint featureId)
    {
        uint? effState = null;
        uint? effPriority = null;
        try
        {
            ulong stamp = 0;
            if (RtlQueryFeatureConfiguration(featureId, ConfigTypeRuntime, ref stamp, out var cfg) == 0)
            {
                effState = (cfg.CompactState & 0x30u) >> 4;
                effPriority = cfg.CompactState & 0xFu;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        int? userState = null;
        int? userOptions = null;
        try
        {
            var obf = ObfuscateId(featureId).ToString(CultureInfo.InvariantCulture);
            using var key = Registry.LocalMachine.OpenSubKey($@"{OverridesUserKey}\{obf}");
            if (key is not null)
            {
                if (key.GetValue("EnabledState") is int s) userState = s;
                if (key.GetValue("EnabledStateOptions") is int o) userOptions = o;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return new FeatureSnapshot(featureId, effState, effPriority, userState, userOptions);
    }

    internal static List<FeatureOp>? ParseOps(IEnumerable<string> tokens)
    {
        var ops = new List<FeatureOp>();
        foreach (var raw in tokens)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var eq = raw.IndexOf('=');
            if (eq <= 0 || eq >= raw.Length - 1) return null;
            if (!uint.TryParse(raw[..eq], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
            if (!LowLatencyProfileIds.Contains(id)) return null;
            var spec = raw[(eq + 1)..];
            if (spec.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                ops.Add(new FeatureOp(id, true, 0u, 0u));
                continue;
            }
            var colon = spec.IndexOf(':');
            if (colon <= 0 || colon >= spec.Length - 1) return null;
            if (!uint.TryParse(spec[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var state) || state > StateEnabled) return null;
            if (!uint.TryParse(spec[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var options) || options > 1u) return null;
            ops.Add(new FeatureOp(id, false, state, options));
        }
        return ops.Count == 0 ? null : ops;
    }

    internal static int Run(string[] args)
    {
        var at = Array.FindIndex(args, arg => arg.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || !string.Equals(args.ElementAtOrDefault(at + 1), "set", StringComparison.OrdinalIgnoreCase)) return 2;
        var ops = ParseOps(args.Skip(at + 2));
        if (ops is null) return 2;

        var updates = ops.Select(op => new RtlFeatureConfigurationUpdate
        {
            FeatureId = op.Id,
            Priority = PriorityUser,
            EnabledState = op.EnabledState,
            EnabledStateOptions = op.EnabledStateOptions,
            Variant = 0u,
            VariantPayloadKind = 0u,
            VariantPayload = 0u,
            Operation = op.Reset ? OpResetState : OpFeatureState,
        }).ToArray();

        try
        {
            ulong stamp = 0;
            var status = RtlSetFeatureConfigurations(ref stamp, ConfigTypeRuntime, updates, updates.Length);
            if (status != 0) AppLog.Debug($"Windows features runtime set returned 0x{unchecked((uint)status):X8}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        try
        {
            foreach (var op in ops)
            {
                var obf = ObfuscateId(op.Id).ToString(CultureInfo.InvariantCulture);
                var subKey = $@"{OverridesUserKey}\{obf}";
                if (op.Reset)
                {
                    Registry.LocalMachine.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
                }
                else
                {
                    using var key = Registry.LocalMachine.CreateSubKey(subKey);
                    key.SetValue("EnabledState", unchecked((int)op.EnabledState), RegistryValueKind.DWord);
                    key.SetValue("EnabledStateOptions", unchecked((int)op.EnabledStateOptions), RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("Windows features: " + ex.Message);
            return 1;
        }

        try
        {
            var pending = BsdStateBootPending;
            _ = RtlSetSystemBootStatus(BsdFeatureConfigurationState, ref pending, sizeof(int), IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return 0;
    }
}
