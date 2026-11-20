using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Persists Unreal Engine 4/5 user shader pipeline cache settings in existing <c>Engine.ini</c> files so games stop
/// recompiling shaders on every launch. Reversible per game and at uninstall; strictly skips anti-cheat games,
/// read-only files, reparse points, and Steam Cloud files.
/// </summary>
internal static class UnrealShaderIni
{
    private const string BeginMarker = "; Exo Shaders begin";
    private const string EndMarker = "; Exo Shaders end";
    private const string SystemSettingsHeader = "[SystemSettings]";

    private static readonly (string Key, string Value)[] WantedVars =
    [
        ("r.ShaderPipelineCache.Enabled", "1"),
        ("r.ShaderPipelineCache.SaveUserCache", "1"),
        ("r.ShaderPipelineCache.StartupMode", "3"),
    ];

    private sealed class Ledger
    {
        public Dictionary<string, LedgerEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record LedgerEntry(string GameId, bool AddedSection, bool HadTrailingNewLine = true);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lock Gate = new();

    private static string DefaultLedgerPath => Path.Combine(PathHelper.AppDataDir, "unreal-shader-ini.json");

    internal static void Apply(GameEntry game, AntiCheatCheck.Answer antiCheat, Func<bool>? isRunning = null)
    {
        if (!AntiCheatCheck.Allows(game, antiCheat, AntiCheatCheck.Change.GameSettingsFile)) return;
        if (isRunning?.Invoke() == true) return;
        foreach (var path in GameHdr.UnrealEngineIniFiles(game))
        {
            if (GameSettings.InSteamCloud(game, path)) continue;
            ApplyToFile(game.Id, path, DefaultLedgerPath);
        }
    }

    internal static bool ApplyToFile(string gameId, string path, string? ledgerPath = null)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        lock (Gate)
        {
            try
            {
                var raw = File.ReadAllBytes(path);
                if (!BuildPatchedContent(raw, out var patched, out var addedSection, out var originalTrailing)) return false;
                WriteAtomic(path, patched);
                var ledgerFile = ledgerPath ?? DefaultLedgerPath;
                var ledger = LoadLedger(ledgerFile);
                var full = Path.GetFullPath(path);
                var hadTrailing = ledger.Files.TryGetValue(full, out var existing) ? existing.HadTrailingNewLine : originalTrailing;
                if (existing is null || existing.GameId != gameId || existing.AddedSection != addedSection || existing.HadTrailingNewLine != hadTrailing)
                {
                    ledger.Files[full] = new LedgerEntry(gameId, addedSection, hadTrailing);
                    SaveLedger(ledgerFile, ledger);
                }
                AppLog.Info($"Shaders: ensured user shader pipeline cache in Engine.ini for {gameId}.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug($"Shaders: skipped Engine.ini for {gameId}: {ex.Message}");
                return false;
            }
        }
    }

    internal static int Restore(string gameId, string? ledgerPath = null)
    {
        var ledgerFile = ledgerPath ?? DefaultLedgerPath;
        lock (Gate)
        {
            var ledger = LoadLedger(ledgerFile);
            var targets = ledger.Files.Where(kv => string.Equals(kv.Value.GameId, gameId, StringComparison.Ordinal)).ToList();
            return RestoreTargets(targets, ledger, ledgerFile);
        }
    }

    internal static int RestoreAll(string? ledgerPath = null)
    {
        var ledgerFile = ledgerPath ?? DefaultLedgerPath;
        lock (Gate)
        {
            var ledger = LoadLedger(ledgerFile);
            return RestoreTargets(ledger.Files.ToList(), ledger, ledgerFile);
        }
    }

    private static int RestoreTargets(List<KeyValuePair<string, LedgerEntry>> targets, Ledger ledger, string ledgerFile)
    {
        if (targets.Count == 0) return 0;
        var restored = 0;
        var changed = false;
        foreach (var (filePath, entry) in targets)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    var attrs = File.GetAttributes(filePath);
                    if ((attrs & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == 0)
                    {
                        var raw = File.ReadAllBytes(filePath);
                        if (StripPatchedContent(raw, entry.AddedSection, entry.HadTrailingNewLine, out var stripped))
                        {
                            WriteAtomic(filePath, stripped);
                            restored++;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Shaders: could not restore {filePath}: {ex.Message}");
                    continue;
                }
            }
            if (ledger.Files.Remove(filePath)) changed = true;
        }
        if (changed) SaveLedger(ledgerFile, ledger);
        return restored;
    }

    internal static bool BuildPatchedContent(byte[] raw, out byte[] patched, out bool addedSection, out bool hadTrailingNewLine)
    {
        var (bom, encoding, newLine, lines, originalTrailing) = DecodeLines(raw);
        hadTrailingNewLine = originalTrailing;
        var existingBlock = HasExoBlock(lines);
        // Strip only an existing Exo block without dropping a pre-existing [SystemSettings] that holds other keys.
        var strippedLines = RemoveExoBlock(lines, removeAddedSection: false);
        var (stripStart, stripEnd) = FindSystemSettings(strippedLines);
        if (existingBlock && stripStart >= 0 && SectionIsEmpty(strippedLines, stripStart, stripEnd))
            strippedLines = RemoveExoBlock(lines, removeAddedSection: true);

        var (sectionIdx, sectionEndIdx) = FindSystemSettings(strippedLines);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sectionIdx >= 0)
        {
            for (var i = sectionIdx + 1; i < sectionEndIdx; i++)
            {
                var t = strippedLines[i].Trim();
                if (t.Length == 0 || t.StartsWith(';') || t.StartsWith('#')) continue;
                var eq = t.IndexOf('=');
                if (eq > 0) present.Add(t[..eq].Trim());
            }
        }

        var missing = WantedVars.Where(v => !present.Contains(v.Key)).ToList();
        if (missing.Count == 0)
        {
            addedSection = false;
            if (!existingBlock) { patched = raw; return false; }
            patched = EncodeLines(bom, encoding, newLine, strippedLines, originalTrailing);
            return !patched.AsSpan().SequenceEqual(raw);
        }

        var result = new List<string>(strippedLines);
        var block = new List<string> { BeginMarker };
        foreach (var (key, value) in missing) block.Add($"{key}={value}");
        block.Add(EndMarker);

        if (sectionIdx >= 0)
        {
            addedSection = false;
            result.InsertRange(sectionIdx + 1, block);
        }
        else
        {
            addedSection = true;
            if (result.Count > 0 && result[^1].Trim().Length != 0) result.Add("");
            result.Add(SystemSettingsHeader);
            result.AddRange(block);
        }

        patched = EncodeLines(bom, encoding, newLine, result, originalTrailing || addedSection);
        return !patched.AsSpan().SequenceEqual(raw);
    }

    internal static bool StripPatchedContent(byte[] raw, bool addedSection, bool hadTrailingNewLine, out byte[] stripped)
    {
        var (bom, encoding, newLine, lines, currentTrailing) = DecodeLines(raw);
        if (!HasExoBlock(lines)) { stripped = raw; return false; }
        var clean = RemoveExoBlock(lines, addedSection);
        var trailing = addedSection ? hadTrailingNewLine : currentTrailing;
        stripped = EncodeLines(bom, encoding, newLine, clean, trailing);
        return !stripped.AsSpan().SequenceEqual(raw);
    }

    private static bool SectionIsEmpty(IReadOnlyList<string> lines, int start, int end)
    {
        for (var i = start + 1; i < end; i++)
            if (lines[i].Trim().Length != 0) return false;
        return true;
    }

    private static bool HasExoBlock(IReadOnlyList<string> lines) =>
        lines.Any(line => line.Trim() == BeginMarker);

    private static List<string> RemoveExoBlock(IReadOnlyList<string> lines, bool removeAddedSection)
    {
        var list = new List<string>(lines.Count);
        var inside = false;
        var removedAddedHeader = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == BeginMarker)
            {
                if (removeAddedSection && list.Count > 0 && list[^1].Trim().Equals(SystemSettingsHeader, StringComparison.OrdinalIgnoreCase))
                {
                    list.RemoveAt(list.Count - 1);
                    removedAddedHeader = true;
                }
                inside = true;
                continue;
            }
            if (inside)
            {
                if (trimmed == EndMarker)
                {
                    inside = false;
                    if (removedAddedHeader && list.Count > 0 && list[^1].Trim().Length == 0 && i == lines.Count - 1)
                        list.RemoveAt(list.Count - 1);
                }
                continue;
            }
            list.Add(lines[i]);
        }
        return list;
    }

    private static (int Start, int End) FindSystemSettings(IReadOnlyList<string> lines)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.Length >= 2 && t[0] == '[' && t[^1] == ']')
            {
                if (start >= 0) return (start, i);
                if (t.Equals(SystemSettingsHeader, StringComparison.OrdinalIgnoreCase)) start = i;
            }
        }
        return (start, lines.Count);
    }

    private static (byte[] Bom, Encoding Encoding, string NewLine, List<string> Lines, bool HadTrailingNewLine) DecodeLines(byte[] raw)
    {
        byte[] bom = raw switch
        {
            [0xEF, 0xBB, 0xBF, ..] => [0xEF, 0xBB, 0xBF],
            [0xFF, 0xFE, ..] => [0xFF, 0xFE],
            _ => [],
        };
        Encoding enc = bom.Length == 2 ? Encoding.Unicode : new UTF8Encoding(false);
        var text = enc.GetString(raw, bom.Length, raw.Length - bom.Length);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadTrailing = text.EndsWith(newLine, StringComparison.Ordinal);
        if (text.Length == 0) return (bom, enc, newLine, [], false);
        var parts = text.Split(newLine).ToList();
        if (hadTrailing && parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return (bom, enc, newLine, parts, hadTrailing);
    }

    private static byte[] EncodeLines(byte[] bom, Encoding encoding, string newLine, IReadOnlyList<string> lines, bool trailingNewLine)
    {
        if (lines.Count == 0) return bom;
        var text = string.Join(newLine, lines) + (trailingNewLine ? newLine : "");
        var body = encoding.GetBytes(text);
        if (bom.Length == 0) return body;
        var outBytes = new byte[bom.Length + body.Length];
        bom.CopyTo(outBytes, 0);
        body.CopyTo(outBytes, bom.Length);
        return outBytes;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static Ledger LoadLedger(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), JsonOptions) ?? new Ledger() : new Ledger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Ledger();
        }
    }

    private static void SaveLedger(string path, Ledger ledger)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Shaders: could not save Unreal INI ledger: " + ex.Message);
        }
    }
}
