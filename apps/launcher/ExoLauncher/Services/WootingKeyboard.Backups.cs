using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal static partial class WootingKeyboard
{
    private const long MaxBackupFileBytes = 512 * 1024;
    private static readonly ConcurrentDictionary<string, bool> AutoBackedUp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions BackupJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal sealed record ProfileBackupSlot(int Index, string Name, string RawBodyBase64, string? RgbCoreBase64 = null);

    internal sealed record KeyboardBackupFile(
        int SchemaVersion,
        string Model,
        int ProductId,
        string? Firmware,
        string CreatedUtc,
        int ActiveProfile,
        IReadOnlyList<ProfileBackupSlot> Profiles);

    internal sealed record KeyboardBackupSummary(
        string FileName,
        string Model,
        int ProductId,
        string? Firmware,
        string CreatedUtc,
        int ProfileCount);

    internal sealed record KeyboardBackupResult(
        bool Ok,
        string? Message,
        KeyboardBackupSummary? Created,
        IReadOnlyList<KeyboardBackupSummary> Backups);

    [GeneratedRegex(@"^wooting-[0-9a-f]{4}-\d{8}-\d{6}(?:-\d+)?\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex BackupNamePattern();

    internal static string BackupDir => Path.Combine(PathHelper.AppDataDir, "keyboard-backups");

    internal static string? ResolveBackupPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)) return null;
        if (!Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal)) return null;
        if (!BackupNamePattern().IsMatch(fileName)) return null;
        var baseDir = Path.GetFullPath(BackupDir);
        var full = Path.GetFullPath(Path.Combine(baseDir, fileName));
        if (!full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    internal static string SerializeBackup(KeyboardBackupFile backup) =>
        JsonSerializer.Serialize(backup, BackupJsonOptions);

    internal static KeyboardBackupFile? DeserializeBackup(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<KeyboardBackupFile>(json, BackupJsonOptions);
            if (parsed is null || parsed.SchemaVersion < 1 || string.IsNullOrWhiteSpace(parsed.Model) || parsed.Profiles is null) return null;
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<KeyboardBackupSummary> ListBackups(Peripherals.Peripheral? card)
    {
        var dir = BackupDir;
        if (!Directory.Exists(dir)) return [];
        var familyKey = card?.ProductId is { } pid ? ModelFamilyKey(pid) : (int?)null;
        var list = new List<KeyboardBackupSummary>();
        foreach (var full in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(full);
            if (!BackupNamePattern().IsMatch(name)) continue;
            try
            {
                if (new FileInfo(full).Length > MaxBackupFileBytes) continue;
                var parsed = DeserializeBackup(File.ReadAllText(full));
                if (parsed is null) continue;
                if (familyKey is { } fk && ModelFamilyKey(parsed.ProductId) != fk) continue;
                list.Add(new(name, parsed.Model, parsed.ProductId, parsed.Firmware, parsed.CreatedUtc, parsed.Profiles.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return list
            .OrderByDescending(b => b.CreatedUtc, StringComparer.Ordinal)
            .ThenByDescending(b => b.FileName, StringComparer.Ordinal)
            .ToList();
    }

    internal static KeyboardBackupResult Backup(Peripherals.Peripheral card) =>
        Backup(card, DefaultTransport);

    internal static KeyboardBackupResult Backup(Peripherals.Peripheral card, IWootingTransport transport)
    {
        if (card.VendorId is not { } vid || card.ProductId is not { } pid || ModelOf(vid, pid) is not { } model)
            return new(false, "Wooting keyboard not supported for backup.", null, ListBackups(card));
        if (card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || ResolveInterfacePath(config.Id, transport) is not { } path)
            return new(false, "Connect your Wooting keyboard over USB to back up profiles.", null, ListBackups(card));

        var multi = config.Page == 0xFF55;
        using (HidGate.EnterScope())
        {
            try
            {
                var created = CaptureAndWriteBackupLocked(model.Name, pid, path, multi, model.HasRgb, transport);
                if (created is null)
                    return new(false, "Couldn't read keyboard profiles for backup.", null, ListBackups(card));
                return new(true, null, created, ListBackups(card));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                AppLog.Warn("Wooting: backup failed: " + ex.Message);
                return new(false, "Wooting didn't answer. Close Wootility if it holds the USB channel, then retry.", null, ListBackups(card));
            }
        }
    }

    internal static KeyboardRead RestoreBackup(Peripherals.Peripheral card, string fileName) =>
        RestoreBackup(card, fileName, DefaultTransport);

    internal static KeyboardRead RestoreBackup(Peripherals.Peripheral card, string fileName, IWootingTransport transport)
    {
        if (card.VendorId is not { } vid || card.ProductId is not { } pid || ModelOf(vid, pid) is not { } model)
            return Read(card, transport) with { Ok = false, Message = "Wooting keyboard not supported for restore." };
        if (ResolveBackupPath(fileName) is not { } full || !File.Exists(full))
            return Read(card, transport) with { Ok = false, Message = "Backup file not found." };

        KeyboardBackupFile? backup;
        try
        {
            if (new FileInfo(full).Length > MaxBackupFileBytes)
                return Read(card, transport) with { Ok = false, Message = "Backup file is too large to restore." };
            backup = DeserializeBackup(File.ReadAllText(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Wooting: backup read failed: " + ex.Message);
            return Read(card, transport) with { Ok = false, Message = "Couldn't read keyboard backup." };
        }
        if (backup is null || backup.Profiles.Count == 0)
            return Read(card, transport) with { Ok = false, Message = "Backup file is empty or damaged." };
        return RestoreBackupData(card, backup, transport);
    }

    internal static KeyboardRead RestoreBackupData(Peripherals.Peripheral card, KeyboardBackupFile backup, IWootingTransport transport)
    {
        if (card.VendorId is not { } vid || card.ProductId is not { } pid || ModelOf(vid, pid) is not { } model)
            return Read(card, transport) with { Ok = false, Message = "Wooting keyboard not supported for restore." };
        if (backup.Profiles.Count == 0)
            return Read(card, transport) with { Ok = false, Message = "Backup file is empty or damaged." };
        if (ModelFamilyKey(backup.ProductId) != ModelFamilyKey(pid))
            return Read(card, transport) with { Ok = false, Message = $"Backup belongs to {backup.Model}, not {model.Name}." };

        if (card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || ResolveInterfacePath(config.Id, transport) is not { } path)
            return Read(card, transport) with { Ok = false, Message = "Keyboard config interface unavailable." };

        var multi = config.Page == 0xFF55;
        if (!multi)
            return Read(card, transport) with { Ok = false, Message = "Direct restore requires ARM v2.12+ firmware (0xFF55)." };

        var wootilityInstalled = FindWootilityExe() is not null;
        var bgRunning = IsBackgroundServiceRunning();
        using (HidGate.EnterScope())
        {
            try
            {
                var channel = transport.Inspect(path);
                if (ReferenceEquals(transport, DefaultTransport))
                    EnsureAutoBackupLocked(card, model.Name, pid, path, multi, model.HasRgb, transport);
                SendFeatureChecked(transport, path, multi, EncodeCommand(CmdWootDevInit, 0, 0, 0, 0, multi), CmdWootDevInit);

                foreach (var slot in backup.Profiles)
                {
                    if (slot.Index < 0 || slot.Index > 3 || string.IsNullOrWhiteSpace(slot.RawBodyBase64)) continue;
                    var raw = Convert.FromBase64String(slot.RawBodyBase64);
                    var report = EncodeProfileOutputReport(
                        OutSetKeyboardBase,
                        slot.Index,
                        save: true,
                        raw,
                        multi,
                        channel.OutputReportByteLength,
                        channel.OutputReportSizes);
                    transport.WriteOutput(path, report, multi);
                    transport.Delay(60);

                    if (model.HasRgb && !string.IsNullOrWhiteSpace(slot.RgbCoreBase64))
                    {
                        var rgbRaw = Convert.FromBase64String(slot.RgbCoreBase64);
                        var rgbReport = EncodeProfileOutputReport(
                            OutSetRgbProfileCore,
                            slot.Index,
                            save: true,
                            rgbRaw,
                            multi,
                            channel.OutputReportByteLength,
                            channel.OutputReportSizes);
                        transport.WriteOutput(path, rgbReport, multi);
                        transport.Delay(40);
                        SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdSaveRgbProfile, slot.Index, multi), CmdSaveRgbProfile);
                        transport.Delay(40);
                    }

                    SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdReloadProfile, slot.Index, multi), CmdReloadProfile);
                    transport.Delay(60);
                }

                if (backup.ActiveProfile is >= 0 and <= 3)
                {
                    SendFeatureChecked(transport, path, multi, EncodeCommand(CmdWootDevInit, 0, 0, 0, 0, multi), CmdWootDevInit);
                    SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdActivateProfile, backup.ActiveProfile, multi), CmdActivateProfile);
                    transport.Delay(110);
                    SendFeatureChecked(transport, path, multi, EncodeProfileCommand(CmdReloadProfile, backup.ActiveProfile, multi), CmdReloadProfile);
                    transport.Delay(110);
                }

                foreach (var slot in backup.Profiles)
                {
                    if (slot.Index < 0 || slot.Index > 3 || string.IsNullOrWhiteSpace(slot.RawBodyBase64)) continue;
                    var expected = Convert.FromBase64String(slot.RawBodyBase64);
                    var readBackReply = QueryProfileFeature(transport, path, multi, CmdGetKeyboardProfile, slot.Index);
                    var actual = readBackReply is { Length: > 0 } ? DecodeReplyBody(readBackReply, CmdGetKeyboardProfile, multi) : null;
                    if (actual is null || !actual.AsSpan().SequenceEqual(expected))
                        return ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport)
                            with { Ok = false, Message = $"Restore verification failed on profile P{slot.Index + 1}." };

                    if (model.HasRgb && !string.IsNullOrWhiteSpace(slot.RgbCoreBase64))
                    {
                        var expectedRgb = Convert.FromBase64String(slot.RgbCoreBase64);
                        var rgbReply = QueryProfileFeature(transport, path, multi, CmdGetRgbProfileCore, slot.Index);
                        var actualRgb = rgbReply is { Length: > 0 } ? DecodeReplyBody(rgbReply, CmdGetRgbProfileCore, multi) : null;
                        if (actualRgb is null || !actualRgb.AsSpan().SequenceEqual(expectedRgb))
                            return ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport)
                                with { Ok = false, Message = $"RGB restore verification failed on profile P{slot.Index + 1}." };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException or OperationCanceledException)
            {
                AppLog.Warn("Wooting: restore failed: " + ex.Message);
                return ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport)
                    with { Ok = false, Message = $"Couldn't restore keyboard backup: {ex.Message}" };
            }

            return ReadLocked(card, model, path, multi, wootilityInstalled, bgRunning, transport);
        }
    }

    private static void EnsureAutoBackupLocked(Peripherals.Peripheral card, string modelName, int pid, string path, bool multi, bool hasRgb, IWootingTransport transport)
    {
        if (AutoBackedUp.ContainsKey(card.Id)) return;
        if (CaptureAndWriteBackupLocked(modelName, pid, path, multi, hasRgb, transport) is not null)
            AutoBackedUp[card.Id] = true;
    }

    private static KeyboardBackupSummary? CaptureAndWriteBackupLocked(string modelName, int pid, string path, bool multi, bool hasRgb, IWootingTransport transport)
    {
        var version = QueryFeature(transport, path, multi, CmdGetVersion, 0) is { Length: > 0 } verReply
            ? DecodeVersion(verReply, multi)
            : null;
        var active = QueryFeature(transport, path, multi, CmdGetCurrentProfileIndex, 0) is { Length: > 0 } idxReply
            && DecodeProfileIndex(idxReply, multi) is { } idx
            ? idx.Active
            : 0;
        var slots = new List<ProfileBackupSlot>();
        for (var slot = 0; slot < 4; slot++)
        {
            var metaReply = QueryProfileFeature(transport, path, multi, CmdGetProfileMetadata, slot);
            var meta = metaReply is { Length: > 0 } ? DecodeProfileMetadata(metaReply, multi) : null;
            var name = meta?.Name ?? (slot == 0 ? "Digital" : $"Profile {slot}");
            var profileReply = QueryProfileFeature(transport, path, multi, CmdGetKeyboardProfile, slot);
            if (profileReply is { Length: > 0 } && DecodeReplyBody(profileReply, CmdGetKeyboardProfile, multi) is { Length: > 0 } body)
            {
                string? rgbBase64 = null;
                if (hasRgb && multi
                    && QueryProfileFeature(transport, path, multi, CmdGetRgbProfileCore, slot) is { Length: > 0 } rgbReply
                    && DecodeReplyBody(rgbReply, CmdGetRgbProfileCore, multi) is { Length: > 0 } rgbBody)
                {
                    rgbBase64 = Convert.ToBase64String(rgbBody);
                }
                slots.Add(new(slot, name, Convert.ToBase64String(body), rgbBase64));
            }
        }
        if (slots.Count == 0) return null;

        var dir = BackupDir;
        Directory.CreateDirectory(dir);
        var now = DateTime.UtcNow;
        var createdUtc = now.ToString("O", CultureInfo.InvariantCulture);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = string.Create(CultureInfo.InvariantCulture, $"wooting-{pid:x4}-{stamp}");
        var fileName = $"{baseName}.json";
        var full = Path.Combine(dir, fileName);
        var seq = 1;
        while (File.Exists(full))
        {
            fileName = string.Create(CultureInfo.InvariantCulture, $"{baseName}-{seq++:D2}.json");
            full = Path.Combine(dir, fileName);
        }

        var payload = new KeyboardBackupFile(2, modelName, pid, version, createdUtc, active, slots);
        var json = SerializeBackup(payload);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, full, overwrite: true);
        return new(fileName, modelName, pid, version, createdUtc, slots.Count);
    }

#if DEBUG
    private static void ProbeProtoTree(ReadOnlySpan<byte> data, string path, int depth)
    {
        if (depth > 5) return;
        var at = 0;
        var count = 0;
        while (at < data.Length && count < 48)
        {
            if (!TryReadVarint(data, ref at, out var tag) || tag < 8) break;
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            var node = $"{path}.{field}";
            if (wire == 0 && TryReadVarint(data, ref at, out var v))
            {
                AppLog.Info($"KB_PROBE {node} varint={v} (0x{v:X})");
            }
            else if (wire == 1 && at + 8 <= data.Length)
            {
                at += 8;
                AppLog.Info($"KB_PROBE {node} fixed64");
            }
            else if (wire == 2 && TryReadVarint(data, ref at, out var len) && len <= (ulong)(data.Length - at))
            {
                var slice = data.Slice(at, (int)len);
                at += (int)len;
                var head = Convert.ToHexString(slice[..Math.Min(slice.Length, 24)]);
                AppLog.Info($"KB_PROBE {node} bytes len={slice.Length} head={head}");
                if (LooksLikeProto(slice)) ProbeProtoTree(slice, node, depth + 1);
            }
            else if (wire == 5 && at + 4 <= data.Length)
            {
                at += 4;
                AppLog.Info($"KB_PROBE {node} fixed32");
            }
            else break;
            count++;
        }
    }

    private static bool LooksLikeProto(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return false;
        try { return ProtoSpans(data).Count > 0; }
        catch (InvalidOperationException) { return false; }
    }
#endif
}
