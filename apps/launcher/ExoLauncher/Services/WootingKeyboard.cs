using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Hid;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

internal static partial class WootingKeyboard
{
    internal const int VendorId = 0x31E3;
    internal const byte MagicSingle = 0xD0;
    internal const byte MagicMulti = 0xD1;
    internal const byte MagicWord1 = 0xDA;
    internal const byte StatusOk = 0x88;

    internal const byte CmdGetVersion = 0x01;
    internal const byte CmdGetCurrentProfileIndex = 0x0B;
    internal const byte CmdGetDeviceConfig = 0x13;
    internal const byte CmdActivateProfile = 0x17;
    internal const byte CmdWootDevInit = 0x21;
    internal const byte CmdReloadProfile = 0x26;
    internal const byte CmdGetKeyboardProfile = 0x27;
    internal const byte CmdGetProfileMetadata = 0x37;
    internal const byte OutSetKeyboardBase = 0x11;

    internal const int ActuationZero = 16384;
    internal const int ActuationUnitsPerMm = 4096;
    internal const double MinActuationMm = 0.10;
    internal const double MaxActuationMm = 4.00;
    internal const double MinRtMm = 0.15;
    internal const double MaxRtMm = 2.35;

    private static readonly (byte ReportId, int BodyCapacity)[] ArmOutputSizes =
    [
        (1, 32), (2, 62), (3, 254), (4, 510), (5, 1022), (6, 2046),
    ];

    private static readonly Lock HidGate = new();

    internal sealed record ModelInfo(string Name, bool HallEffect, bool MultiReport, int MaxScanHz);

    internal sealed record ProfileSlot(int Index, string Name);

    internal sealed record ProfileSettings(
        double? ActuationMm,
        bool? DigitalOutput,
        bool? RapidTrigger,
        double? RapidTriggerMm,
        bool? Tachyon,
        int? TickRate,
        byte[] RawBody);

    internal sealed record KeyboardRead(
        bool Ok,
        string? Message,
        string Model,
        bool HallEffect,
        int MaxScanHz,
        string? Firmware,
        string? Layout,
        int ActiveProfile,
        IReadOnlyList<ProfileSlot> Profiles,
        double? ActuationMm,
        bool? RapidTrigger,
        double? RapidTriggerMm,
        bool? Tachyon,
        bool CanWrite,
        bool WootilityInstalled,
        bool BackgroundServiceRunning);

    internal static bool IsSupported(Peripherals.Peripheral card) =>
        card.Kind == "keyboard" && card.VendorId == VendorId && card.Connection == "usb" && card.ProductId is { } pid && ModelOf(pid) is not null;

    internal static ModelInfo? ModelOf(int pid) => (pid & 0xFFF0) switch
    {
        0x1100 => new("Wooting One", false, false, 1000),
        0x1200 => new("Wooting Two", false, false, 1000),
        0x1210 => new("Wooting Two Lekker Edition", true, false, 1000),
        0x1220 => new("Wooting Two HE (AVR)", true, false, 1000),
        0x1230 => new("Wooting Two HE", true, true, 1000),
        0x1300 => new("Wooting 60HE (AVR)", true, false, 1000),
        0x1310 => new("Wooting 60HE", true, true, 1000),
        0x1320 => new("Wooting 60HE+", true, true, 1000),
        0x1340 => new("Wooting 60HE v2", true, true, 8000),
        0x1400 => new("Wooting 80HE", true, true, 8000),
        0x1410 => new("Wooting 80HE+", true, true, 8000),
        0x1500 => new("Wooting UwU", true, true, 1000),
        0x1510 => new("Wooting UwU RGB", true, true, 1000),
        _ => null,
    };

    internal static int ActuationToRaw(double mm) =>
        (int)Math.Round(ActuationZero + Math.Clamp(mm, MinActuationMm, MaxActuationMm) * ActuationUnitsPerMm, MidpointRounding.AwayFromZero);

    internal static double RawToActuation(int raw) =>
        Math.Round(Math.Clamp((raw - ActuationZero) / (double)ActuationUnitsPerMm, 0.0, MaxActuationMm), 2);

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

    internal static byte[] NormalizeReply(ReadOnlySpan<byte> raw, bool multiReport)
    {
        if (raw.Length < 5) return [];
        if (multiReport && raw[0] >= 1 && raw[0] <= 6 && raw[1] == MagicMulti && raw[2] == MagicWord1)
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

    internal static string? DecodeVersion(ReadOnlySpan<byte> reply)
    {
        if (!ReplyOk(reply, CmdGetVersion) || reply.Length < 7) return null;
        var (maj, min, pat) = (reply[4], reply[5], reply[6]);
        return maj == 0 && min == 0 && pat == 0 ? null : $"{maj}.{min}.{pat}";
    }

    internal static (int FlashDefault, int Active)? DecodeProfileIndex(ReadOnlySpan<byte> reply)
    {
        if (!ReplyOk(reply, CmdGetCurrentProfileIndex) || reply.Length < 5) return null;
        var flash = reply[4];
        var active = reply.Length > 6 ? reply[6] : flash;
        return (flash, active);
    }

    internal static (bool Exists, string? Name)? DecodeProfileMetadata(ReadOnlySpan<byte> reply)
    {
        if (!ReplyOk(reply, CmdGetProfileMetadata) || reply.Length < 6) return null;
        if (reply[4] == 0) return (false, null);
        var fields = DecodeProtoFields(reply[6..]);
        var raw = fields.FirstOrDefault(f => f.Field == 1 && f.Bytes is not null)?.Bytes;
        if (raw is null || raw.Length == 0) return (true, null);
        var name = Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim();
        return (true, name.Length > 0 ? name : null);
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
        return new ProfileSettings(
            actRaw.HasValue ? RawToActuation(actRaw.Value) : null,
            Flag(2),
            Flag(3),
            rtRaw.HasValue ? RawToActuation(rtRaw.Value) : null,
            Flag(5),
            Val(6),
            body);
    }

    internal static byte[] PatchKeyboardProfile(byte[] completeMessage, double? actuationMm, bool? rapidTrigger, double? rapidTriggerMm, bool? tachyon)
    {
        var outerSpans = ProtoSpans(completeMessage);
        if (outerSpans.Count(s => s.Field == 1) != 1) throw new InvalidOperationException("Incomplete Wooting keyboard profile.");
        var baseField = DecodeProtoFields(completeMessage).FirstOrDefault(f => f.Field == 1 && f.Bytes is not null)?.Bytes
            ?? throw new InvalidOperationException("Missing Wooting base profile block.");

        var edited = (byte[])baseField.Clone();
        if (actuationMm is { } act)
            edited = ReplaceVarintField(edited, 1, (ulong)ActuationToRaw(act));
        if (rapidTrigger is { } rt)
            edited = ReplaceVarintField(edited, 3, rt ? 1UL : 0UL);
        if (rapidTriggerMm is { } rtMm)
            edited = ReplaceVarintField(edited, 4, (ulong)ActuationToRaw(Math.Clamp(rtMm, MinRtMm, MaxRtMm)));
        if (tachyon is { } tac)
            edited = ReplaceVarintField(edited, 5, tac ? 1UL : 0UL);

        return ReplaceBytesField(completeMessage, 1, edited);
    }

    internal static byte[] EncodeProfileOutputReport(byte commandId, int profileIndex, bool save, byte[] completeMessage, bool multiReport)
    {
        var flags = (byte)((save ? 1 : 0) | ((profileIndex & 0x1F) << 1));
        if (!multiReport)
        {
            var standard = new byte[Math.Max(65, completeMessage.Length + 7)];
            standard[0] = 0;
            standard[1] = MagicSingle;
            standard[2] = MagicWord1;
            standard[3] = commandId;
            standard[4] = flags;
            standard[5] = (byte)completeMessage.Length;
            Buffer.BlockCopy(completeMessage, 0, standard, 6, completeMessage.Length);
            return standard;
        }
        var picked = ArmOutputSizes.FirstOrDefault(s => completeMessage.Length <= s.BodyCapacity - 6);
        if (picked.ReportId == 0) throw new InvalidOperationException("Wooting profile exceeds max output report size.");
        var report = new byte[1 + picked.BodyCapacity];
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

    internal static KeyboardRead Read(Peripherals.Peripheral card)
    {
        var model = card.ProductId is { } pid ? ModelOf(pid) : null;
        var modelName = model?.Name ?? card.Name;
        var wootilityExe = FindWootilityExe();
        var bgRunning = IsBackgroundServiceRunning();
        if (model is null || card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || Peripherals.InterfacePath(config.Id) is not { } path)
        {
            return new(false, "Connect your Wooting keyboard over USB to read its settings.", modelName, model?.HallEffect ?? true,
                model?.MaxScanHz ?? 1000, null, FormatLayout(card.Layout), 0, [], null, null, null, null, false, wootilityExe is not null, bgRunning);
        }

        var multi = config.Page == 0xFF55;
        using var scope = HidGate.EnterScope();
        try
        {
            var version = QueryFeature(path, multi, CmdGetVersion, 0) is { Length: > 0 } verReply ? DecodeVersion(verReply) : null;
            var active = QueryFeature(path, multi, CmdGetCurrentProfileIndex, 0) is { Length: > 0 } idxReply && DecodeProfileIndex(idxReply) is { } idx
                ? idx.Active
                : 0;
            var profiles = new List<ProfileSlot>();
            for (var slot = 0; slot < 4; slot++)
            {
                if (QueryProfileFeature(path, multi, CmdGetProfileMetadata, slot) is { Length: > 0 } metaReply
                    && DecodeProfileMetadata(metaReply) is { Exists: true } meta)
                {
                    var fallback = slot == 0 ? "Digital" : $"Profile {slot}";
                    profiles.Add(new(slot, meta.Name ?? fallback));
                }
            }
            if (profiles.Count == 0)
            {
                for (var slot = 0; slot < 4; slot++)
                    profiles.Add(new(slot, slot == 0 ? "Digital" : $"Profile {slot}"));
            }

            var profileReply = QueryProfileFeature(path, multi, CmdGetKeyboardProfile, active);
#if DEBUG
            if (Environment.GetEnvironmentVariable("EXO_KB_PROBE") == "1"
                && profileReply is { Length: > 0 }
                && DecodeReplyBody(profileReply, CmdGetKeyboardProfile, multi) is { Length: > 0 } probeBody)
            {
                AppLog.Info($"KB_PROBE model={modelName} slot={active} multi={multi} totalLen={probeBody.Length}");
                ProbeProtoTree(probeBody, "root", 0);
            }
#endif
            var settings = profileReply is { Length: > 0 } ? DecodeKeyboardProfile(profileReply, multi) : null;
            var layout = card.Layout;
            if (layout is null && QueryFeature(path, multi, CmdGetDeviceConfig, 0) is { Length: > 0 } cfgReply)
            {
                var withHeader = multi ? new byte[] { 1 }.Concat(cfgReply).ToArray() : cfgReply;
                if (Peripherals.LayoutByte(withHeader) is { } lb) layout = Peripherals.LayoutOf(lb);
            }

            return new(
                true,
                null,
                modelName,
                model.HallEffect,
                model.MaxScanHz,
                version,
                FormatLayout(layout),
                active,
                profiles,
                settings?.ActuationMm,
                settings?.RapidTrigger,
                settings?.RapidTriggerMm,
                settings?.Tachyon,
                settings is not null && multi,
                wootilityExe is not null,
                bgRunning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            AppLog.Warn("Wooting: read failed: " + ex.Message);
            return new(false, "Wooting didn't answer. Close Wootility if it holds the USB channel, then retry.", modelName,
                model.HallEffect, model.MaxScanHz, null, FormatLayout(card.Layout), 0, [], null, null, null, null, false, wootilityExe is not null, bgRunning);
        }
    }

    internal static KeyboardRead SwitchProfile(Peripherals.Peripheral card, int slot)
    {
        if (slot < 0 || slot > 3) return Read(card) with { Ok = false, Message = "Profile slot out of range." };
        if (card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || Peripherals.InterfacePath(config.Id) is not { } path)
            return Read(card) with { Ok = false, Message = "Keyboard config interface unavailable." };

        var multi = config.Page == 0xFF55;
        using (HidGate.EnterScope())
        {
            try
            {
                SendFeatureOnly(path, multi, EncodeCommand(CmdWootDevInit, 0, 0, 0, 0, multi));
                SendFeatureOnly(path, multi, EncodeProfileCommand(CmdActivateProfile, slot, multi));
                Thread.Sleep(110);
                SendFeatureOnly(path, multi, EncodeProfileCommand(CmdReloadProfile, slot, multi));
                Thread.Sleep(110);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn("Wooting: profile switch failed: " + ex.Message);
                return Read(card) with { Ok = false, Message = "Couldn't switch keyboard profile." };
            }
        }
        return Read(card);
    }

    internal static KeyboardRead UpdateProfile(Peripherals.Peripheral card, double? actuationMm, bool? rapidTrigger, double? rapidTriggerMm, bool? tachyon)
    {
        if (card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || Peripherals.InterfacePath(config.Id) is not { } path)
            return Read(card) with { Ok = false, Message = "Keyboard config interface unavailable." };

        var multi = config.Page == 0xFF55;
        if (!multi)
            return Read(card) with { Ok = false, Message = "Direct actuation editing requires an ARM Wooting keyboard." };

        using (HidGate.EnterScope())
        {
            try
            {
                var active = QueryFeature(path, multi, CmdGetCurrentProfileIndex, 0) is { Length: > 0 } idxReply && DecodeProfileIndex(idxReply) is { } idx
                    ? idx.Active
                    : 0;
                var currentReply = QueryProfileFeature(path, multi, CmdGetKeyboardProfile, active);
                var current = currentReply is { Length: > 0 } ? DecodeKeyboardProfile(currentReply, multi) : null;
                if (current is null)
                    return Read(card) with { Ok = false, Message = "Couldn't read the active profile before saving." };

                if (card.ProductId is { } pid && ModelOf(pid) is { } model)
                    EnsureAutoBackupLocked(card, model.Name, pid, path, multi);

                var patched = PatchKeyboardProfile(current.RawBody, actuationMm, rapidTrigger, rapidTriggerMm, tachyon);
                var output = EncodeProfileOutputReport(OutSetKeyboardBase, active, save: true, patched, multi);
                WriteOutputReport(path, output);
                Thread.Sleep(90);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                AppLog.Warn("Wooting: profile write failed: " + ex.Message);
                return Read(card) with { Ok = false, Message = "Couldn't apply keyboard settings." };
            }
        }
        return Read(card);
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
        _ => null,
    };

    private static byte[]? QueryFeature(string path, bool multi, byte cmd, byte param0) =>
        TransactFeature(path, multi, EncodeCommand(cmd, param0, 0, 0, 0, multi), cmd);

    private static byte[]? QueryProfileFeature(string path, bool multi, byte cmd, int slot) =>
        TransactFeature(path, multi, EncodeProfileCommand(cmd, slot, multi), cmd);

    private static void SendFeatureOnly(string path, bool multi, byte[] payload)
    {
        using var control = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (control.IsInvalid || GetCaps(control) is not { } caps || caps.FeatureReportByteLength < 8)
            throw new IOException("HID feature channel unavailable.");
        var frame = new byte[caps.FeatureReportByteLength];
        Buffer.BlockCopy(payload, 0, frame, 0, Math.Min(payload.Length, frame.Length));
        if (!HidD_SetFeature(control, frame, frame.Length))
            throw new IOException("HidD_SetFeature refused command.");
    }

    private static void WriteOutputReport(string path, byte[] report)
    {
        using var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
        if (handle.IsInvalid) throw new IOException("HID output channel unavailable.");
        using var stream = new FileStream(handle, FileAccess.Write, 0, isAsync: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        stream.WriteAsync(report, cts.Token).AsTask().GetAwaiter().GetResult();
    }

    private static byte[]? TransactFeature(string path, bool multi, byte[] payload, byte expectedCmd)
    {
        var readHandle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
        if (readHandle.IsInvalid) { readHandle.Dispose(); return null; }
        using var reader = new FileStream(readHandle, FileAccess.Read, 0, isAsync: true);
        using var control = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (control.IsInvalid || GetCaps(control) is not { } caps || caps.FeatureReportByteLength < 8 || caps.InputReportByteLength < 8)
            return null;

        var frame = new byte[caps.FeatureReportByteLength];
        Buffer.BlockCopy(payload, 0, frame, 0, Math.Min(payload.Length, frame.Length));
        if (!HidD_SetFeature(control, frame, frame.Length)) return null;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        var report = new byte[caps.InputReportByteLength];
        var assembled = new List<byte>();
        for (var tries = 0; tries < 12; tries++)
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
                if (report[0] == 0) assembled.AddRange(report[1..n]);
                if (assembled.Count >= 64)
                {
                    var norm = NormalizeReply(assembled.ToArray(), false);
                    if (norm.Length >= 4 && norm[2] == expectedCmd) return norm;
                    assembled.Clear();
                }
            }
        }
        return assembled.Count >= 4 ? NormalizeReply(assembled.ToArray(), false) : null;
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
