using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Hid;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

internal static partial class WootingKeyboard
{
    internal const int VendorId = 0x31E3;
    internal const int LegacyVendorId = 0x03EB;
    internal const byte MagicSingle = 0xD0;
    internal const byte MagicMulti = 0xD1;
    internal const byte MagicWord1 = 0xDA;
    internal const byte StatusOk = 0x88;
    internal const byte StatusUnsupported = 0x66;

    internal const byte CmdGetVersion = 0x01;
    internal const byte CmdSaveRgbProfile = 0x08;
    internal const byte CmdGetDigitalProfilesCount = 0x09;
    internal const byte CmdGetAnalogProfilesCount = 0x0A;
    internal const byte CmdGetCurrentProfileIndex = 0x0B;
    internal const byte CmdGetDeviceConfig = 0x13;
    internal const byte CmdActivateProfile = 0x17;
    internal const byte CmdWootDevSingleColor = 0x1E;
    internal const byte CmdWootDevResetColor = 0x1F;
    internal const byte CmdWootDevResetAll = 0x20;
    internal const byte CmdWootDevInit = 0x21;
    internal const byte CmdGetRgbColorPage1 = 0x23;
    internal const byte CmdGetRgbColorPage2 = 0x24;
    internal const byte CmdReloadProfile = 0x26;
    internal const byte CmdGetKeyboardProfile = 0x27;
    internal const byte CmdGetRgbProfileCore = 0x32;
    internal const byte CmdGetProfileMetadata = 0x37;

    internal const byte OutSetRgbColorPage1 = 0x0E;
    internal const byte OutSetRgbColorPage2 = 0x0F;
    internal const byte OutSetKeyboardBase = 0x11;
    internal const byte OutSetRgbProfileCore = 0x16;

    internal const int ActuationZero = 16384;
    internal const int ActuationUnitsPerMm = 4096;
    internal const double MinActuationMm = 0.10;
    internal const double MaxActuationMm = 4.00;
    internal const double MinRtMm = 0.10;
    internal const double MaxRtMm = 2.35;

    internal static readonly (byte ReportId, int BodyCapacity)[] DefaultArmOutputSizes =
    [
        (1, 32), (2, 62), (3, 254), (4, 510), (5, 1022), (6, 2046),
    ];

    private static readonly Lock HidGate = new();
    private static readonly IWootingTransport DefaultTransport = new HidWootingTransport();

    internal sealed record ModelInfo(
        string Name,
        string FormFactor,
        bool HallEffect,
        bool HasRapidTrigger,
        bool HasRgb,
        bool MultiReport,
        int MaxScanHz,
        double MinActuationMm,
        double MaxActuationMm,
        bool LegacyV1 = false);

    internal sealed record CatalogEntry(int VendorId, int BaseProductId, bool HasGamepadVariants, ModelInfo Model);

    internal static readonly IReadOnlyList<CatalogEntry> Catalog =
    [
        new(LegacyVendorId, 0xFF01, false, new("Wooting One", "tkl", false, false, true, false, 1000, 1.5, 3.6, LegacyV1: true)),
        new(LegacyVendorId, 0x2402, false, new("Wooting One", "tkl", false, false, true, false, 1000, 1.5, 3.6, LegacyV1: true)),
        new(LegacyVendorId, 0xFF02, false, new("Wooting Two", "full", false, false, true, false, 1000, 1.5, 3.6, LegacyV1: true)),
        new(LegacyVendorId, 0x2403, false, new("Wooting Two", "full", false, false, true, false, 1000, 1.5, 3.6, LegacyV1: true)),
        new(VendorId, 0x1100, true, new("Wooting One", "tkl", false, false, true, false, 1000, 1.5, 3.6)),
        new(VendorId, 0x1200, true, new("Wooting Two", "full", false, false, true, false, 1000, 1.5, 3.6)),
        new(VendorId, 0x1210, true, new("Wooting Two Lekker Edition", "full", true, true, true, false, 1000, 0.1, 4.0)),
        new(VendorId, 0x1220, true, new("Wooting Two HE (AVR)", "full", true, true, true, false, 1000, 0.1, 4.0)),
        new(VendorId, 0x1230, true, new("Wooting Two HE", "full", true, true, true, true, 1000, 0.1, 4.0)),
        new(VendorId, 0x1300, true, new("Wooting 60HE (AVR)", "60", true, true, true, false, 1000, 0.1, 4.0)),
        new(VendorId, 0x1310, true, new("Wooting 60HE", "60", true, true, true, true, 1000, 0.1, 4.0)),
        new(VendorId, 0x1320, true, new("Wooting 60HE+", "60", true, true, true, true, 1000, 0.1, 4.0)),
        new(VendorId, 0x1340, true, new("Wooting 60HE v2", "60", true, true, true, true, 8000, 0.1, 4.0)),
        new(VendorId, 0x1400, true, new("Wooting 80HE", "80", true, true, true, true, 8000, 0.1, 4.0)),
        new(VendorId, 0x1410, true, new("Wooting 80HE+", "80", true, true, true, true, 8000, 0.1, 4.0)),
        new(VendorId, 0x1500, true, new("Wooting UwU", "keypad3", true, true, false, true, 1000, 0.1, 4.0)),
        new(VendorId, 0x1510, true, new("Wooting UwU RGB", "keypad3", true, true, true, true, 1000, 0.1, 4.0)),
    ];

    internal sealed record HidChannelInfo(
        int InputReportByteLength,
        int OutputReportByteLength,
        int FeatureReportByteLength,
        IReadOnlyList<(byte ReportId, int BodyCapacity)> OutputReportSizes);

    internal interface IWootingTransport
    {
        HidChannelInfo Inspect(string path);
        byte[]? TransactFeature(string path, bool multi, byte[] payload, byte expectedCmd);
        void WriteOutput(string path, byte[] framedReport, bool multi);
        void Delay(int milliseconds);
    }

    internal sealed record ProfileSlot(int Index, string Name);

    internal sealed record ProfileSettings(
        double? ActuationMm,
        bool? DigitalOutput,
        bool? RapidTrigger,
        double? RapidTriggerMm,
        bool? Tachyon,
        int? TickRate,
        byte[] RawBody,
        bool? ContinuousRapidTrigger = null,
        bool? TachyonMode = null);

    internal sealed record RgbCoreSettings(int BrightnessPercent, int BrightnessRaw, byte[] RawBody);

    internal sealed record KeyboardRead(
        bool Ok,
        string? Message,
        string Model,
        string FormFactor,
        bool HallEffect,
        bool HasRapidTrigger,
        bool HasRgb,
        int MaxScanHz,
        double MinActuationMm,
        double MaxActuationMm,
        string? Firmware,
        string? Layout,
        string? LayoutCode,
        int ActiveProfile,
        IReadOnlyList<ProfileSlot> Profiles,
        double? ActuationMm,
        bool? RapidTrigger,
        double? RapidTriggerMm,
        bool? Tachyon,
        int? RgbBrightness,
        bool CanWrite,
        bool WootilityInstalled,
        bool BackgroundServiceRunning);

    internal static bool IsWootingIds(int? vid, int? pid) =>
        vid is { } v && pid is { } p && ModelOf(v, p) is not null;

    internal static bool IsSupported(Peripherals.Peripheral card) =>
        card.Kind == "keyboard" && card.Connection == "usb" && IsWootingIds(card.VendorId, card.ProductId);

    internal static ModelInfo? ModelOf(int pid) => ModelOf(VendorId, pid);

    internal static ModelInfo? ModelOf(int vid, int pid)
    {
        if (vid == LegacyVendorId)
        {
            return pid switch
            {
                0xFF01 or 0x2402 => Catalog[0].Model,
                0xFF02 or 0x2403 => Catalog[2].Model,
                _ => null,
            };
        }
        if (vid != VendorId) return null;
        var basePid = pid & 0xFFF0;
        for (var i = 0; i < Catalog.Count; i++)
        {
            var entry = Catalog[i];
            if (entry.VendorId == VendorId && entry.BaseProductId == basePid) return entry.Model;
        }
        return null;
    }

    internal static int ModelFamilyKey(int pid) => pid is 0xFF01 or 0xFF02 or 0x2402 or 0x2403 ? pid : pid & 0xFFF0;

    internal static int ActuationToRaw(double mm, double minMm = MinActuationMm, double maxMm = MaxActuationMm) =>
        (int)Math.Round(ActuationZero + Math.Clamp(mm, minMm, maxMm) * ActuationUnitsPerMm, MidpointRounding.AwayFromZero);

    internal static double RawToActuation(int raw) =>
        Math.Round(Math.Clamp((raw - ActuationZero) / (double)ActuationUnitsPerMm, 0.0, MaxActuationMm), 2);

    internal static int BrightnessPercentToRaw(int percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) * 255.0 / 100.0, MidpointRounding.AwayFromZero);

    internal static int BrightnessRawToPercent(int raw) =>
        (int)Math.Round(Math.Clamp(raw, 0, 255) * 100.0 / 255.0, MidpointRounding.AwayFromZero);

    internal static byte[] EncodeCommand(byte cmd, byte p0, byte p1, byte p2, byte p3, bool multiReport, int length = 8)
    {
        var buf = new byte[Math.Max(8, length)];
        buf[0] = multiReport ? (byte)1 : (byte)0;
        buf[1] = multiReport ? MagicMulti : MagicSingle;
        buf[2] = MagicWord1;
        buf[3] = cmd;
        buf[4] = p3;
        buf[5] = p2;
        buf[6] = p1;
        buf[7] = p0;
        return buf;
    }

    internal static byte[] EncodeProfileCommand(byte cmd, int slot, bool multiReport, int length = 8) =>
        multiReport
            ? EncodeCommand(cmd, 0, 0, 0, (byte)slot, true, length)
            : EncodeCommand(cmd, (byte)slot, 0, 0, 0, false, length);

    internal static byte[] EncodeFeatureFrame(byte[] payload, int featureReportByteLength)
    {
        var frame = new byte[Math.Max(8, featureReportByteLength)];
        Buffer.BlockCopy(payload, 0, frame, 0, Math.Min(payload.Length, frame.Length));
        return frame;
    }

    internal static IReadOnlyList<(byte ReportId, int BodyCapacity)> ParseOutputReportSizes(ReadOnlySpan<byte> valueCapsBytes, int count)
    {
        var byId = new Dictionary<byte, int>();
        for (var i = 0; i < count; i++)
        {
            var offset = i * CapsSize;
            if (offset + 22 > valueCapsBytes.Length) break;
            var reportId = valueCapsBytes[offset + CapsReportIdOffset];
            if (reportId == 0) continue;
            var bitSize = BinaryPrimitives.ReadUInt16LittleEndian(valueCapsBytes.Slice(offset + 18, 2));
            var reportCount = BinaryPrimitives.ReadUInt16LittleEndian(valueCapsBytes.Slice(offset + 20, 2));
            var bodyBytes = (bitSize * reportCount + 7) / 8;
            if (bodyBytes <= 0) continue;
            if (!byId.TryGetValue(reportId, out var existing) || bodyBytes > existing)
                byId[reportId] = bodyBytes;
        }
        return byId
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();
    }

    internal static byte[] NormalizeReply(ReadOnlySpan<byte> raw, bool multiReport)
    {
        if (raw.Length < 5) return [];
        if (multiReport && raw[0] >= 1 && raw[0] <= 16 && raw[1] == MagicMulti && raw[2] == MagicWord1)
            return raw[1..].ToArray();
        if (!multiReport && raw[0] == 0 && raw[1] == MagicSingle && raw[2] == MagicWord1)
            return raw[1..].ToArray();
        if ((raw[0] == MagicMulti || raw[0] == MagicSingle) && raw[1] == MagicWord1)
            return raw.ToArray();
        return [];
    }

    internal static bool ReplyOk(ReadOnlySpan<byte> reply, byte expectedCmd) =>
        reply.Length >= 4
        && (reply[0] == MagicMulti || reply[0] == MagicSingle)
        && reply[1] == MagicWord1
        && reply[2] == expectedCmd
        && reply[3] == StatusOk;

    internal static string? DecodeVersion(ReadOnlySpan<byte> reply, bool multiReport = true)
    {
        if (!ReplyOk(reply, CmdGetVersion)) return null;
        if (multiReport && reply[0] == MagicMulti)
        {
            if (DecodeReplyBody(reply, CmdGetVersion, true) is not { Length: >= 3 } body) return null;
            var (maj, min, pat) = (body[0], body[1], body[2]);
            if (maj == 0 && min == 0 && pat == 0) return null;
            if (body.Length > 3)
            {
                var suffix = Encoding.ASCII.GetString(body.AsSpan(3)).TrimEnd('\0').Trim();
                if (suffix.Length > 0 && suffix.All(c => c is >= '!' and <= '~'))
                    return $"{maj}.{min}.{pat}-{suffix}";
            }
            return $"{maj}.{min}.{pat}";
        }
        if (reply.Length < 7) return null;
        var (sMaj, sMin, sPat) = (reply[4], reply[5], reply[6]);
        return sMaj == 0 && sMin == 0 && sPat == 0 ? null : $"{sMaj}.{sMin}.{sPat}";
    }

    internal static (int FlashDefault, int Active)? DecodeProfileIndex(ReadOnlySpan<byte> reply, bool multiReport = true)
    {
        if (!ReplyOk(reply, CmdGetCurrentProfileIndex)) return null;
        if (multiReport && reply[0] == MagicMulti)
        {
            if (DecodeReplyBody(reply, CmdGetCurrentProfileIndex, true) is not { Length: >= 1 } body) return null;
            var active = body[0];
            var secondary = body.Length > 1 ? body[1] : active;
            return (secondary, active);
        }
        if (reply.Length < 5) return null;
        return (reply[4], reply[4]);
    }

    internal static (bool Exists, string? Name)? DecodeProfileMetadata(ReadOnlySpan<byte> reply, bool multiReport = true)
    {
        if (!ReplyOk(reply, CmdGetProfileMetadata)) return null;
        var body = DecodeReplyBody(reply, CmdGetProfileMetadata, multiReport);
        if (body is null || body.Length == 0) return (false, null);
        var fields = DecodeProtoFields(body);
        var raw = fields.FirstOrDefault(f => f.Field == 1 && f.Bytes is not null)?.Bytes;
        if (raw is null || raw.Length == 0) return (true, null);
        var name = Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim();
        return (true, name.Length > 0 ? name : null);
    }

    internal static string? DecodeDeviceLayout(ReadOnlySpan<byte> reply, bool multiReport, bool legacyV1 = false)
    {
        if (!ReplyOk(reply, CmdGetDeviceConfig)) return null;
        if (multiReport && reply[0] == MagicMulti)
        {
            if (DecodeReplyBody(reply, CmdGetDeviceConfig, true) is { Length: > 5 } body)
                return Peripherals.LayoutOf(body[5]);
            return null;
        }
        var index = legacyV1 ? 9 : 10;
        return reply.Length > index ? Peripherals.LayoutOf(reply[index]) : null;
    }

    internal static byte[]? DecodeReplyBody(ReadOnlySpan<byte> reply, byte cmd, bool multiReport)
    {
        if (!ReplyOk(reply, cmd)) return null;
        if (multiReport)
        {
            if (reply.Length < 6) return null;
            var len = reply[4] | (reply[5] << 8);
            return reply.Length >= 6 + len ? reply.Slice(6, len).ToArray() : null;
        }
        if (reply.Length < 5) return null;
        var shortLen = reply[4];
        return reply.Length >= 5 + shortLen ? reply.Slice(5, shortLen).ToArray() : null;
    }

    internal static ProfileSettings? DecodeKeyboardProfile(ReadOnlySpan<byte> reply, bool multiReport)
    {
        if (DecodeReplyBody(reply, CmdGetKeyboardProfile, multiReport) is not { Length: > 0 } body) return null;
        var outer = DecodeProtoFields(body);
        var nested = outer.FirstOrDefault(f => f.Field == 1 && f.Bytes is not null)?.Bytes;
        if (nested is null) return null;
        var fields = DecodeProtoFields(nested);
        int? Val(int n) => fields.FirstOrDefault(f => f.Field == n && f.Varint.HasValue)?.Varint is { } v ? (int)v : null;
        bool? Flag(int n) => Val(n) is { } v ? v != 0 : null;
        var actRaw = Val(1);
        var rtRaw = Val(4);
        var tachyon = Flag(5);
        var tachyonMode = Flag(9);
        return new ProfileSettings(
            actRaw.HasValue ? RawToActuation(actRaw.Value) : null,
            Flag(2),
            Flag(3),
            rtRaw.HasValue ? RawToActuation(rtRaw.Value) : null,
            tachyon,
            Val(6),
            body,
            Flag(7),
            tachyonMode);
    }

    internal static RgbCoreSettings? DecodeRgbCore(ReadOnlySpan<byte> reply, bool multiReport)
    {
        if (DecodeReplyBody(reply, CmdGetRgbProfileCore, multiReport) is not { Length: > 0 } body) return null;
        var outer = DecodeProtoFields(body);
        var nested = outer.FirstOrDefault(f => f.Field == 1 && f.Bytes is not null)?.Bytes;
        if (nested is null) return null;
        var fields = DecodeProtoFields(nested);
        var bright = fields.FirstOrDefault(f => f.Field == 1 && f.Varint.HasValue)?.Varint;
        if (!bright.HasValue) return null;
        var raw = (int)bright.Value;
        return new RgbCoreSettings(BrightnessRawToPercent(raw), raw, body);
    }

    internal static byte[] PatchKeyboardProfile(
        byte[] completeMessage,
        double? actuationMm,
        bool? rapidTrigger,
        double? rapidTriggerMm,
        bool? tachyon,
        double minActuationMm = MinActuationMm,
        double maxActuationMm = MaxActuationMm)
    {
        var outerSpans = ProtoSpans(completeMessage);
        if (outerSpans.Count(s => s.Field == 1) != 1) throw new InvalidOperationException("Incomplete Wooting keyboard profile.");
        var baseField = DecodeProtoFields(completeMessage).FirstOrDefault(f => f.Field == 1 && f.Bytes is not null)?.Bytes
            ?? throw new InvalidOperationException("Missing Wooting base profile block.");

        var baseSpans = ProtoSpans(baseField);
        var hasField9 = baseSpans.Any(s => s.Field == 9);

        var edited = (byte[])baseField.Clone();
        if (actuationMm is { } act)
            edited = ReplaceVarintField(edited, 1, (ulong)ActuationToRaw(act, minActuationMm, maxActuationMm));
        if (rapidTrigger is { } rt)
            edited = ReplaceVarintField(edited, 3, rt ? 1UL : 0UL);
        if (rapidTriggerMm is { } rtMm)
            edited = ReplaceVarintField(edited, 4, (ulong)ActuationToRaw(Math.Clamp(rtMm, MinRtMm, MaxRtMm)));
        if (tachyon is { } tac)
        {
            var flag = tac ? 1UL : 0UL;
            edited = ReplaceVarintField(edited, 5, flag);
            if (hasField9) edited = ReplaceVarintField(edited, 9, flag);
        }

        return ReplaceBytesField(completeMessage, 1, edited);
    }

    internal static byte[] PatchRgbCore(byte[] completeMessage, int brightnessPercent)
    {
        var outerSpans = ProtoSpans(completeMessage);
        if (outerSpans.Count(s => s.Field == 1) != 1) throw new InvalidOperationException("Incomplete Wooting RGB core profile.");
        var baseField = DecodeProtoFields(completeMessage).FirstOrDefault(f => f.Field == 1 && f.Bytes is not null)?.Bytes
            ?? throw new InvalidOperationException("Missing Wooting RGB base block.");

        var edited = ReplaceVarintField((byte[])baseField.Clone(), 1, (ulong)BrightnessPercentToRaw(brightnessPercent));
        return ReplaceBytesField(completeMessage, 1, edited);
    }

    internal static byte[] EncodeProfileOutputReport(
        byte commandId,
        int profileIndex,
        bool save,
        byte[] completeMessage,
        bool multiReport,
        int outputReportByteLength = 0,
        IReadOnlyList<(byte ReportId, int BodyCapacity)>? reportSizes = null)
    {
        var flags = (byte)((save ? 1 : 0) | ((profileIndex & 0x1F) << 1));
        if (!multiReport)
        {
            if (completeMessage.Length > 255) throw new InvalidOperationException("Standard Wooting report exceeds 255 bytes.");
            var minLen = Math.Max(65, completeMessage.Length + 6);
            var targetLen = Math.Max(outputReportByteLength, minLen);
            var standard = new byte[targetLen];
            standard[0] = 0;
            standard[1] = MagicSingle;
            standard[2] = MagicWord1;
            standard[3] = commandId;
            standard[4] = flags;
            standard[5] = (byte)completeMessage.Length;
            Buffer.BlockCopy(completeMessage, 0, standard, 6, completeMessage.Length);
            return standard;
        }
        var ladder = reportSizes is { Count: > 0 } ? reportSizes : DefaultArmOutputSizes;
        var picked = ladder.FirstOrDefault(s => completeMessage.Length <= s.BodyCapacity - 6);
        if (picked.ReportId == 0) throw new InvalidOperationException("Wooting profile exceeds max output report size.");
        var paddedLen = Math.Max(outputReportByteLength, 1 + picked.BodyCapacity);
        var report = new byte[paddedLen];
        report[0] = picked.ReportId;
        report[1] = MagicMulti;
        report[2] = MagicWord1;
        report[3] = commandId;
        report[4] = flags;
        report[5] = (byte)(completeMessage.Length & 0xFF);
        report[6] = (byte)((completeMessage.Length >> 8) & 0xFF);
        Buffer.BlockCopy(completeMessage, 0, report, 7, completeMessage.Length);
        return report;
    }

    internal static IReadOnlyList<byte[]> SplitOutputPackets(byte[] framedReport, int outputReportByteLength, bool multiReport)
    {
        if (outputReportByteLength <= 0) return [framedReport];
        if (framedReport.Length == outputReportByteLength) return [framedReport];
        if (framedReport.Length < outputReportByteLength)
        {
            var padded = new byte[outputReportByteLength];
            Buffer.BlockCopy(framedReport, 0, padded, 0, framedReport.Length);
            return [padded];
        }
        if (!multiReport && outputReportByteLength == 65)
        {
            var payload = framedReport.AsSpan(1);
            var packets = new List<byte[]>();
            for (var offset = 0; offset < payload.Length; offset += 64)
            {
                var chunk = new byte[65];
                chunk[0] = 0;
                var take = Math.Min(64, payload.Length - offset);
                payload.Slice(offset, take).CopyTo(chunk.AsSpan(1));
                packets.Add(chunk);
            }
            return packets;
        }
        throw new InvalidOperationException($"Wooting report ({framedReport.Length} B) exceeds HID output report length ({outputReportByteLength} B).");
    }

    internal static string? VerifyProfileReadBack(
        KeyboardRead after,
        double? actuationMm,
        bool? rapidTrigger,
        double? rapidTriggerMm,
        bool? tachyon,
        int? rgbBrightness,
        double minActuationMm = MinActuationMm,
        double maxActuationMm = MaxActuationMm)
    {
        if (!after.Ok)
            return after.Message ?? "Couldn't read back keyboard settings after saving.";
        if (actuationMm is { } expectedAct)
        {
            var clamped = Math.Round(Math.Clamp(expectedAct, minActuationMm, maxActuationMm), 2);
            if (after.ActuationMm is not { } actualAct || Math.Abs(actualAct - clamped) > 0.02)
                return $"Actuation didn't stick on the keyboard (read back {after.ActuationMm?.ToString("0.00") ?? "nothing"} mm instead of {clamped:0.00} mm).";
        }
        if (rapidTrigger is { } expectedRt && after.RapidTrigger != expectedRt)
            return $"Rapid Trigger didn't stick on the keyboard (expected {(expectedRt ? "on" : "off")}).";
        if (rapidTriggerMm is { } expectedRtMm)
        {
            var clampedRt = Math.Round(Math.Clamp(expectedRtMm, MinRtMm, MaxRtMm), 2);
            if (after.RapidTriggerMm is not { } actualRt || Math.Abs(actualRt - clampedRt) > 0.02)
                return $"Rapid Trigger sensitivity didn't stick (read back {after.RapidTriggerMm?.ToString("0.00") ?? "nothing"} mm instead of {clampedRt:0.00} mm).";
        }
        if (tachyon is { } expectedTac && after.Tachyon != expectedTac)
            return $"Tachyon mode didn't stick on the keyboard (expected {(expectedTac ? "on" : "off")}).";
        if (rgbBrightness is { } expectedRgb)
        {
            var clampedRgb = Math.Clamp(expectedRgb, 0, 100);
            if (after.RgbBrightness is not { } actualRgb || Math.Abs(actualRgb - clampedRgb) > 2)
                return $"RGB brightness didn't stick on the keyboard (read back {after.RgbBrightness?.ToString() ?? "nothing"}% instead of {clampedRgb}%).";
        }
        return null;
    }

    internal static KeyboardRead Read(Peripherals.Peripheral card) => Read(card, DefaultTransport);

    internal static KeyboardRead Read(Peripherals.Peripheral card, IWootingTransport transport)
    {
        var model = card.VendorId is { } vid && card.ProductId is { } pid ? ModelOf(vid, pid) : null;
        var modelName = model?.Name ?? card.Name;
        var formFactor = model?.FormFactor ?? "60";
        var wootilityExe = FindWootilityExe();
        var bgRunning = IsBackgroundServiceRunning();
        var defaultLayoutCode = model?.FormFactor == "keypad3" ? "keypad" : card.Layout;
        if (model is null || card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || ResolveInterfacePath(config.Id, transport) is not { } path)
        {
            return new(false, "Connect your Wooting keyboard over USB to read its settings.", modelName, formFactor,
                model?.HallEffect ?? true, model?.HasRapidTrigger ?? true, model?.HasRgb ?? true,
                model?.MaxScanHz ?? 1000, model?.MinActuationMm ?? MinActuationMm, model?.MaxActuationMm ?? MaxActuationMm,
                null, FormatLayout(defaultLayoutCode), defaultLayoutCode, 0, [], null, null, null, null, null, false,
                wootilityExe is not null, bgRunning);
        }

        var multi = config.Page == 0xFF55;
        using var scope = HidGate.EnterScope();
        return ReadLocked(card, model, path, multi, wootilityExe is not null, bgRunning, transport);
    }

    private static KeyboardRead ReadLocked(
        Peripherals.Peripheral card,
        ModelInfo model,
        string path,
        bool multi,
        bool wootilityInstalled,
        bool bgRunning,
        IWootingTransport transport)
    {
        var defaultLayoutCode = model.FormFactor == "keypad3" ? "keypad" : card.Layout;
        try
        {
            var version = QueryFeature(transport, path, multi, CmdGetVersion, 0) is { Length: > 0 } verReply
                ? DecodeVersion(verReply, multi)
                : null;
            var active = QueryFeature(transport, path, multi, CmdGetCurrentProfileIndex, 0) is { Length: > 0 } idxReply
                && DecodeProfileIndex(idxReply, multi) is { } idx
                ? idx.Active
                : 0;
            var profiles = ReadProfileSlotsLocked(transport, path, multi);
            var profileReply = QueryProfileFeature(transport, path, multi, CmdGetKeyboardProfile, active);
#if DEBUG
            if (Environment.GetEnvironmentVariable("EXO_KB_PROBE") == "1"
                && profileReply is { Length: > 0 }
                && DecodeReplyBody(profileReply, CmdGetKeyboardProfile, multi) is { Length: > 0 } probeBody)
            {
                AppLog.Info($"KB_PROBE model={model.Name} slot={active} multi={multi} totalLen={probeBody.Length}");
                ProbeProtoTree(probeBody, "root", 0);
            }
#endif
            var settings = profileReply is { Length: > 0 } ? DecodeKeyboardProfile(profileReply, multi) : null;
            var rgbCore = model.HasRgb && multi
                && QueryProfileFeature(transport, path, multi, CmdGetRgbProfileCore, active) is { Length: > 0 } rgbReply
                ? DecodeRgbCore(rgbReply, multi)
                : null;

            var layoutCode = defaultLayoutCode;
            if (layoutCode is null && QueryFeature(transport, path, multi, CmdGetDeviceConfig, 0) is { Length: > 0 } cfgReply)
                layoutCode = DecodeDeviceLayout(cfgReply, multi, model.LegacyV1);

            return new(
                true,
                null,
                model.Name,
                model.FormFactor,
                model.HallEffect,
                model.HasRapidTrigger,
                model.HasRgb,
                model.MaxScanHz,
                model.MinActuationMm,
                model.MaxActuationMm,
                version,
                FormatLayout(layoutCode),
                layoutCode,
                active,
                profiles,
                settings?.ActuationMm,
                model.HasRapidTrigger ? settings?.RapidTrigger : null,
                model.HasRapidTrigger ? settings?.RapidTriggerMm : null,
                settings?.Tachyon,
                rgbCore?.BrightnessPercent,
                settings is not null && multi,
                wootilityInstalled,
                bgRunning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            AppLog.Warn("Wooting: read failed: " + ex.Message);
            return new(false, "Wooting didn't answer. Close Wootility if it holds the USB channel, then retry.",
                model.Name, model.FormFactor, model.HallEffect, model.HasRapidTrigger, model.HasRgb,
                model.MaxScanHz, model.MinActuationMm, model.MaxActuationMm, null, FormatLayout(defaultLayoutCode), defaultLayoutCode,
                0, [], null, null, null, null, null, false, wootilityInstalled, bgRunning);
        }
    }

    private static List<ProfileSlot> ReadProfileSlotsLocked(IWootingTransport transport, string path, bool multi)
    {
        var profiles = new List<ProfileSlot>();
        for (var slot = 0; slot < 4; slot++)
        {
            if (QueryProfileFeature(transport, path, multi, CmdGetProfileMetadata, slot) is { Length: > 0 } metaReply
                && DecodeProfileMetadata(metaReply, multi) is { Exists: true } meta)
            {
                var fallback = slot == 0 ? "Digital" : $"Profile {slot}";
                profiles.Add(new(slot, meta.Name ?? fallback));
            }
        }
        if (profiles.Count > 0) return profiles;

        var count = 4;
        if (!multi)
        {
            var digital = QueryFeature(transport, path, false, CmdGetDigitalProfilesCount, 0);
            var analog = QueryFeature(transport, path, false, CmdGetAnalogProfilesCount, 0);
            var dCount = digital is { Length: > 4 } && ReplyOk(digital, CmdGetDigitalProfilesCount) ? digital[4] : 0;
            var aCount = analog is { Length: > 4 } && ReplyOk(analog, CmdGetAnalogProfilesCount) ? analog[4] : 0;
            var total = dCount + aCount;
            if (total is >= 1 and <= 8) count = total;
        }
        for (var slot = 0; slot < count; slot++)
            profiles.Add(new(slot, slot == 0 ? "Digital" : $"Profile {slot}"));
        return profiles;
    }

    internal static KeyboardRead SwitchProfile(Peripherals.Peripheral card, int slot) =>
        SwitchProfile(card, slot, DefaultTransport);

    internal static KeyboardRead SwitchProfile(Peripherals.Peripheral card, int slot, IWootingTransport transport)
    {
        if (slot < 0 || slot > 3) return Read(card, transport) with { Ok = false, Message = "Profile slot out of range." };
        var model = card.VendorId is { } vid && card.ProductId is { } pid ? ModelOf(vid, pid) : null;
        if (model is null || card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || ResolveInterfacePath(config.Id, transport) is not { } path)
            return Read(card, transport) with { Ok = false, Message = "Keyboard config interface unavailable." };

        var multi = config.Page == 0xFF55;
        using (HidGate.EnterScope())
        {
            try
            {
                SendFeatureChecked(transport, path, multi, EncodeCommand(CmdWootDevInit, 0, 0, 0, 0, multi), CmdWootDevInit);
                SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdActivateProfile, slot, multi), CmdActivateProfile);
                transport.Delay(110);
                SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdReloadProfile, slot, multi), CmdReloadProfile);
                transport.Delay(110);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                AppLog.Warn("Wooting: profile switch failed: " + ex.Message);
                return ReadLocked(card, model, path, multi, FindWootilityExe() is not null, IsBackgroundServiceRunning(), transport)
                    with { Ok = false, Message = $"Couldn't switch keyboard profile: {ex.Message}" };
            }

            var after = ReadLocked(card, model, path, multi, FindWootilityExe() is not null, IsBackgroundServiceRunning(), transport);
            if (!after.Ok) return after;
            if (after.ActiveProfile != slot)
                return after with { Ok = false, Message = $"Profile switch to P{slot + 1} didn't take effect on the keyboard (still on P{after.ActiveProfile + 1})." };
            return after;
        }
    }

    internal static KeyboardRead UpdateProfile(
        Peripherals.Peripheral card,
        double? actuationMm,
        bool? rapidTrigger,
        double? rapidTriggerMm,
        bool? tachyon,
        int? rgbBrightness = null) =>
        UpdateProfile(card, actuationMm, rapidTrigger, rapidTriggerMm, tachyon, rgbBrightness, DefaultTransport);

    internal static KeyboardRead UpdateProfile(
        Peripherals.Peripheral card,
        double? actuationMm,
        bool? rapidTrigger,
        double? rapidTriggerMm,
        bool? tachyon,
        int? rgbBrightness,
        IWootingTransport transport)
    {
        var model = card.VendorId is { } vid && card.ProductId is { } pid ? ModelOf(vid, pid) : null;
        if (model is null || card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || ResolveInterfacePath(config.Id, transport) is not { } path)
            return Read(card, transport) with { Ok = false, Message = "Keyboard config interface unavailable." };

        var multi = config.Page == 0xFF55;
        if (!multi)
            return Read(card, transport) with { Ok = false, Message = "Direct actuation editing requires ARM v2.12+ firmware (0xFF55). Use Wootility or update firmware." };

        if (rgbBrightness.HasValue && !model.HasRgb)
            return Read(card, transport) with { Ok = false, Message = $"{model.Name} doesn't have RGB lighting." };

        if ((rapidTrigger.HasValue || rapidTriggerMm.HasValue) && !model.HasRapidTrigger)
            return Read(card, transport) with { Ok = false, Message = $"{model.Name} doesn't support Rapid Trigger." };

        var wootilityInstalled = FindWootilityExe() is not null;
        var bgRunning = IsBackgroundServiceRunning();
        var wantsBasePatch = actuationMm.HasValue || rapidTrigger.HasValue || rapidTriggerMm.HasValue || tachyon.HasValue;
        var wantsRgbPatch = rgbBrightness.HasValue;

        if (!wantsBasePatch && !wantsRgbPatch)
            return Read(card, transport);

        using (HidGate.EnterScope())
        {
            try
            {
                var channel = transport.Inspect(path);
                SendFeatureChecked(transport, path, multi, EncodeCommand(CmdWootDevInit, 0, 0, 0, 0, multi), CmdWootDevInit);

                var active = QueryFeature(transport, path, multi, CmdGetCurrentProfileIndex, 0) is { Length: > 0 } idxReply
                    && DecodeProfileIndex(idxReply, multi) is { } idx
                    ? idx.Active
                    : 0;

                if (card.ProductId is { } cardPid && ReferenceEquals(transport, DefaultTransport))
                    EnsureAutoBackupLocked(card, model.Name, cardPid, path, multi, model.HasRgb, transport);

                if (wantsBasePatch)
                {
                    var currentReply = QueryProfileFeature(transport, path, multi, CmdGetKeyboardProfile, active);
                    var current = currentReply is { Length: > 0 } ? DecodeKeyboardProfile(currentReply, multi) : null;
                    if (current is null)
                        return ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport)
                            with { Ok = false, Message = "Couldn't read the active profile before saving." };

                    var patched = PatchKeyboardProfile(
                        current.RawBody,
                        actuationMm,
                        rapidTrigger,
                        rapidTriggerMm,
                        tachyon,
                        model.MinActuationMm,
                        model.MaxActuationMm);
                    var output = EncodeProfileOutputReport(
                        OutSetKeyboardBase,
                        active,
                        save: true,
                        patched,
                        multi,
                        channel.OutputReportByteLength,
                        channel.OutputReportSizes);
                    transport.WriteOutput(path, output, multi);
                    transport.Delay(60);
                }

                if (wantsRgbPatch)
                {
                    var rgbReply = QueryProfileFeature(transport, path, multi, CmdGetRgbProfileCore, active);
                    var rgbCurrent = rgbReply is { Length: > 0 } ? DecodeRgbCore(rgbReply, multi) : null;
                    if (rgbCurrent is null)
                        return ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport)
                            with { Ok = false, Message = "Couldn't read RGB settings before saving." };

                    var patchedRgb = PatchRgbCore(rgbCurrent.RawBody, rgbBrightness!.Value);
                    var rgbOutput = EncodeProfileOutputReport(
                        OutSetRgbProfileCore,
                        active,
                        save: true,
                        patchedRgb,
                        multi,
                        channel.OutputReportByteLength,
                        channel.OutputReportSizes);
                    transport.WriteOutput(path, rgbOutput, multi);
                    transport.Delay(40);
                    SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdSaveRgbProfile, active, multi), CmdSaveRgbProfile);
                    transport.Delay(40);
                }

                SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdReloadProfile, active, multi), CmdReloadProfile);
                transport.Delay(90);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException)
            {
                AppLog.Warn("Wooting: profile write failed: " + ex.Message);
                return ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport)
                    with { Ok = false, Message = $"Couldn't apply keyboard settings: {ex.Message}" };
            }

            var after = ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport);
            var verifyError = VerifyProfileReadBack(
                after,
                actuationMm,
                rapidTrigger,
                rapidTriggerMm,
                tachyon,
                rgbBrightness,
                model.MinActuationMm,
                model.MaxActuationMm);
            if (verifyError is not null)
            {
                AppLog.Warn("Wooting: read-back verification failed: " + verifyError);
                return after with { Ok = false, Message = verifyError };
            }
            return after;
        }
    }

    internal static bool LaunchWootility()
    {
        try
        {
            if (FindWootilityExe() is { } exe)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return true;
            }
            Process.Start(new ProcessStartInfo("https://wootility.io/") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("Wooting: couldn't launch Wootility: " + ex.Message);
            return false;
        }
    }

    internal static string? FindWootilityExe()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        var candidate = Path.Combine(local, "Programs", "wootility", "Wootility.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool IsBackgroundServiceRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("wooting-bg-service");
            var any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return any;
        }
        catch { return false; }
    }

    private static string? FormatLayout(string? layout) => layout switch
    {
        "ansi" => "ANSI",
        "iso" => "ISO",
        "jis" => "JIS",
        "ansi-split" => "ANSI · Split spacebar",
        "iso-split" => "ISO · Split spacebar",
        "keypad" => "3-key keypad",
        _ => null,
    };

    private static string? ResolveInterfacePath(string collectionId, IWootingTransport transport) =>
        ReferenceEquals(transport, DefaultTransport) ? Peripherals.InterfacePath(collectionId) : collectionId;

    private static byte[]? QueryFeature(IWootingTransport transport, string path, bool multi, byte cmd, byte param0) =>
        transport.TransactFeature(path, multi, EncodeCommand(cmd, param0, 0, 0, 0, multi), cmd);

    private static byte[]? QueryProfileFeature(IWootingTransport transport, string path, bool multi, byte cmd, int slot) =>
        transport.TransactFeature(path, multi, EncodeProfileCommand(cmd, slot, multi), cmd);

    private static void SendFeatureChecked(IWootingTransport transport, string path, bool multi, byte[] payload, byte expectedCmd)
    {
        var reply = transport.TransactFeature(path, multi, payload, expectedCmd);
        if (reply is null || reply.Length < 4)
            throw new IOException($"Keyboard didn't respond to command 0x{expectedCmd:X2}.");
        if (reply[3] != StatusOk)
            throw new IOException($"Keyboard rejected command 0x{expectedCmd:X2} (status 0x{reply[3]:X2}).");
    }

    private sealed class HidWootingTransport : IWootingTransport
    {
        public HidChannelInfo Inspect(string path)
        {
            using var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (handle.IsInvalid) throw new IOException("HID channel unavailable.");
            if (!HidD_GetPreparsedData(handle, out var preparsed))
                throw new IOException("HID descriptor unavailable.");
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess)
                    throw new IOException("HID capabilities unavailable.");
                IReadOnlyList<(byte ReportId, int BodyCapacity)> outputSizes = [];
                if (caps.NumberOutputValueCaps > 0)
                {
                    var len = caps.NumberOutputValueCaps;
                    var buf = new byte[len * CapsSize];
                    if (HidP_GetValueCaps(ReportType.Output, buf, ref len, preparsed) == HidpStatusSuccess)
                        outputSizes = ParseOutputReportSizes(buf, len);
                }
                return new HidChannelInfo(caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength, outputSizes);
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        public byte[]? TransactFeature(string path, bool multi, byte[] payload, byte expectedCmd)
        {
            var readHandle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
            if (readHandle.IsInvalid) { readHandle.Dispose(); return null; }
            using var reader = new FileStream(readHandle, FileAccess.Read, 0, isAsync: true);
            using var control = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (control.IsInvalid || GetCaps(control) is not { } caps || caps.FeatureReportByteLength < 8 || caps.InputReportByteLength < 8)
                return null;

            var frame = EncodeFeatureFrame(payload, caps.FeatureReportByteLength);
            if (!HidD_SetFeature(control, frame, frame.Length))
                throw new IOException($"HidD_SetFeature failed for command 0x{expectedCmd:X2}.");

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
            var expectedLen = caps.InputReportByteLength == 65 ? 256 : caps.InputReportByteLength - 1;
            var report = new byte[caps.InputReportByteLength];
            var assembled = new List<byte>(expectedLen);
            for (var tries = 0; tries < 16; tries++)
            {
                var n = reader.ReadAsync(report, cts.Token).AsTask().GetAwaiter().GetResult();
                if (n <= 0) continue;
                if (multi)
                {
                    var norm = NormalizeReply(report.AsSpan(0, n), true);
                    if (norm.Length >= 4 && norm[2] == expectedCmd) return norm;
                }
                else
                {
                    if (report[0] != 0) continue;
                    var chunk = report.AsSpan(1, n - 1);
                    if (assembled.Count == 0 && (chunk.Length < 4 || chunk[0] != MagicSingle || chunk[1] != MagicWord1 || chunk[2] != expectedCmd))
                        continue;
                    assembled.AddRange(chunk);
                    if (assembled.Count >= expectedLen)
                        return assembled.ToArray();
                }
            }
            return assembled.Count >= 4 ? assembled.ToArray() : null;
        }

        public void WriteOutput(string path, byte[] framedReport, bool multi)
        {
            using var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
            if (handle.IsInvalid) throw new IOException("HID output channel unavailable.");
            if (GetCaps(handle) is not { } caps || caps.OutputReportByteLength <= 0)
                throw new IOException("HID output report length unavailable.");
            var packets = SplitOutputPackets(framedReport, caps.OutputReportByteLength, multi);
            using var stream = new FileStream(handle, FileAccess.Write, 0, isAsync: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var packet in packets)
                stream.WriteAsync(packet, cts.Token).AsTask().GetAwaiter().GetResult();
        }

        public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
    }

    private static HidpCaps? GetCaps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try { return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? caps : null; }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private sealed record ProtoField(int Field, ulong? Varint, byte[]? Bytes);
    private sealed record ProtoSpan(int Field, byte[] Raw);

    private static List<ProtoField> DecodeProtoFields(ReadOnlySpan<byte> data)
    {
        var list = new List<ProtoField>();
        var at = 0;
        while (at < data.Length)
        {
            if (!TryReadVarint(data, ref at, out var tag) || tag < 8) break;
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (wire == 0)
            {
                if (!TryReadVarint(data, ref at, out var val)) break;
                list.Add(new(field, val, null));
            }
            else if (wire == 1)
            {
                if (at + 8 > data.Length) break;
                at += 8;
            }
            else if (wire == 2)
            {
                if (!TryReadVarint(data, ref at, out var len) || len > (ulong)(data.Length - at)) break;
                list.Add(new(field, null, data.Slice(at, (int)len).ToArray()));
                at += (int)len;
            }
            else if (wire == 5)
            {
                if (at + 4 > data.Length) break;
                at += 4;
            }
            else break;
        }
        return list;
    }

    private static List<ProtoSpan> ProtoSpans(ReadOnlySpan<byte> data)
    {
        var spans = new List<ProtoSpan>();
        var at = 0;
        while (at < data.Length)
        {
            var start = at;
            if (!TryReadVarint(data, ref at, out var tag) || tag < 8) throw new InvalidOperationException("Invalid Wooting protobuf tag.");
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (wire == 0)
            {
                if (!TryReadVarint(data, ref at, out _)) throw new InvalidOperationException("Truncated Wooting protobuf varint.");
            }
            else if (wire == 1)
            {
                if (at + 8 > data.Length) throw new InvalidOperationException("Truncated Wooting protobuf fixed64.");
                at += 8;
            }
            else if (wire == 2)
            {
                if (!TryReadVarint(data, ref at, out var len) || len > (ulong)(data.Length - at)) throw new InvalidOperationException("Truncated Wooting protobuf bytes.");
                at += (int)len;
            }
            else if (wire == 5)
            {
                if (at + 4 > data.Length) throw new InvalidOperationException("Truncated Wooting protobuf fixed32.");
                at += 4;
            }
            else throw new InvalidOperationException("Unsupported Wooting protobuf wire type.");
            spans.Add(new(field, data[start..at].ToArray()));
        }
        return spans;
    }

    private static byte[] ReplaceVarintField(byte[] message, int field, ulong value)
    {
        var encoded = new List<byte>();
        WriteVarint(encoded, ((ulong)field << 3) | 0UL);
        WriteVarint(encoded, value);
        return ReplaceSpan(message, field, [.. encoded]);
    }

    private static byte[] ReplaceBytesField(byte[] message, int field, byte[] payload)
    {
        var encoded = new List<byte>();
        WriteVarint(encoded, ((ulong)field << 3) | 2UL);
        WriteVarint(encoded, (ulong)payload.Length);
        encoded.AddRange(payload);
        return ReplaceSpan(message, field, [.. encoded]);
    }

    private static byte[] ReplaceSpan(byte[] message, int field, byte[] replacement)
    {
        var spans = ProtoSpans(message);
        var output = new List<byte>(message.Length + replacement.Length);
        var replaced = false;
        foreach (var span in spans)
        {
            if (span.Field == field)
            {
                if (!replaced) { output.AddRange(replacement); replaced = true; }
                continue;
            }
            output.AddRange(span.Raw);
        }
        if (!replaced) output.AddRange(replacement);
        return [.. output];
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int at, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (at < data.Length && shift < 64)
        {
            var b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static void WriteVarint(List<byte> target, ulong value)
    {
        while (value >= 0x80)
        {
            target.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        target.Add((byte)value);
    }
}
