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

    internal sealed record ProfileBackupSlot(int Index, string Name, string RawBodyBase64);

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
        var familyPid = card?.ProductId is { } pid ? pid & 0xFFF0 : (int?)null;
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
                if (familyPid is { } fp && (parsed.ProductId & 0xFFF0) != fp) continue;
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

    internal static KeyboardBackupResult Backup(Peripherals.Peripheral card)
    {
        if (card.ProductId is not { } pid || ModelOf(pid) is not { } model)
            return new(false, "Wooting keyboard not supported for backup.", null, ListBackups(card));
        if (card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || Peripherals.InterfacePath(config.Id) is not { } path)
            return new(false, "Connect your Wooting keyboard over USB to back up profiles.", null, ListBackups(card));

        var multi = config.Page == 0xFF55;
        using (HidGate.EnterScope())
        {
            try
            {
                var created = CaptureAndWriteBackupLocked(model.Name, pid, path, multi);
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

    internal static KeyboardRead RestoreBackup(Peripherals.Peripheral card, string fileName)
    {
        if (card.ProductId is not { } pid || ModelOf(pid) is not { } model)
            return Read(card) with { Ok = false, Message = "Wooting keyboard not supported for restore." };
        if (ResolveBackupPath(fileName) is not { } full || !File.Exists(full))
            return Read(card) with { Ok = false, Message = "Backup file not found." };

        KeyboardBackupFile? backup;
        try
        {
            if (new FileInfo(full).Length > MaxBackupFileBytes)
                return Read(card) with { Ok = false, Message = "Backup file is too large to restore." };
            backup = DeserializeBackup(File.ReadAllText(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Wooting: backup read failed: " + ex.Message);
            return Read(card) with { Ok = false, Message = "Couldn't read keyboard backup." };
        }
        if (backup is null || backup.Profiles.Count == 0)
            return Read(card) with { Ok = false, Message = "Backup file is empty or damaged." };
        if ((backup.ProductId & 0xFFF0) != (pid & 0xFFF0))
            return Read(card) with { Ok = false, Message = $"Backup belongs to {backup.Model}, not {model.Name}." };

        if (card.Collections.FirstOrDefault(c => c.Page is 0xFF55 or 0x1337) is not { } config || Peripherals.InterfacePath(config.Id) is not { } path)
            return Read(card) with { Ok = false, Message = "Keyboard config interface unavailable." };

        var multi = config.Page == 0xFF55;
        if (!multi)
            return Read(card) with { Ok = false, Message = "Direct restore requires an ARM Wooting keyboard." };

        using (HidGate.EnterScope())
        {
            try
            {
                EnsureAutoBackupLocked(card, model.Name, pid, path, multi);
                foreach (var slot in backup.Profiles)
                {
                    if (slot.Index < 0 || slot.Index > 3 || string.IsNullOrWhiteSpace(slot.RawBodyBase64)) continue;
                    var raw = Convert.FromBase64String(slot.RawBodyBase64);
                    var report = EncodeProfileOutputReport(OutSetKeyboardBase, slot.Index, save: true, raw, multi);
                    WriteOutputReport(path, report);
                    Thread.Sleep(60);
                    SendFeatureOnly(path, multi, EncodeProfileCommand(CmdReloadProfile, slot.Index, multi));
                    Thread.Sleep(60);
                }
                if (backup.ActiveProfile is >= 0 and <= 3)
                {
                    SendFeatureOnly(path, multi, EncodeCommand(CmdWootDevInit, 0, 0, 0, 0, multi));
                    SendFeatureOnly(path, multi, EncodeProfileCommand(CmdActivateProfile, backup.ActiveProfile, multi));
                    Thread.Sleep(110);
                    SendFeatureOnly(path, multi, EncodeProfileCommand(CmdReloadProfile, backup.ActiveProfile, multi));
                    Thread.Sleep(110);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
            {
                AppLog.Warn("Wooting: restore failed: " + ex.Message);
                return Read(card) with { Ok = false, Message = "Couldn't restore keyboard backup." };
            }
        }
        return Read(card);
    }

    private static void EnsureAutoBackupLocked(Peripherals.Peripheral card, string modelName, int pid, string path, bool multi)
    {
        if (AutoBackedUp.ContainsKey(card.Id)) return;
        if (CaptureAndWriteBackupLocked(modelName, pid, path, multi) is not null)
            AutoBackedUp[card.Id] = true;
    }

    private static KeyboardBackupSummary? CaptureAndWriteBackupLocked(string modelName, int pid, string path, bool multi)
    {
        var version = QueryFeature(path, multi, CmdGetVersion, 0) is { Length: > 0 } verReply ? DecodeVersion(verReply) : null;
        var active = QueryFeature(path, multi, CmdGetCurrentProfileIndex, 0) is { Length: > 0 } idxReply && DecodeProfileIndex(idxReply) is { } idx
            ? idx.Active
            : 0;
        var slots = new List<ProfileBackupSlot>();
        for (var slot = 0; slot < 4; slot++)
        {
            var metaReply = QueryProfileFeature(path, multi, CmdGetProfileMetadata, slot);
            var meta = metaReply is { Length: > 0 } ? DecodeProfileMetadata(metaReply) : null;
            var name = meta?.Name ?? (slot == 0 ? "Digital" : $"Profile {slot}");
            var profileReply = QueryProfileFeature(path, multi, CmdGetKeyboardProfile, slot);
            if (profileReply is { Length: > 0 } && DecodeReplyBody(profileReply, CmdGetKeyboardProfile, multi) is { Length: > 0 } body)
                slots.Add(new(slot, name, Convert.ToBase64String(body)));
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

        var payload = new KeyboardBackupFile(1, modelName, pid, version, createdUtc, active, slots);
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
