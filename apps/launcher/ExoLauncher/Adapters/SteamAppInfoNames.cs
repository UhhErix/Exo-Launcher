using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Reads Steam's local appinfo (its copy of every app's store record) for names, whole records and launch entries.
/// Two layouts: since November 2026 Steam writes up to 256 shards, <c>appcache/appinfo/appinfo_00.vdf</c> to
/// <c>appinfo_ff.vdf</c> (magic 0x0756442A); older Steam writes one <c>appcache/appinfo.vdf</c> (v41, magic 0x07564429).
/// The shard layout, read off real files (all little-endian):
/// <list type="bullet">
/// <item>Header, 44 bytes: magic, universe, shard index, shard count (256), app count, index size, key count, key table
/// size, data size, then 8 bytes Exo doesn't use. The file is exactly the header, index, key table and data.</item>
/// <item>Index: per app its id, the offset and size of its record in the data, the number of bytes that follow (change
/// number, time, PICS token, hashes, kind and parent; not read), then those bytes.</item>
/// <item>Key table: key count NUL-terminated strings, shared by every record in the shard.</item>
/// <item>Record: a SHA-1 of the rest, then binary KeyValues whose keys are int32 indexes into the key table, as in v41.</item>
/// </list>
/// Which shard holds an app is Steam's own hash (not the id's low byte), so a lookup reads every shard; together they are
/// a few MB. A shard with no apps is not written.
/// </summary>
internal static class SteamAppInfoNames
{
    private const uint MagicV41 = 0x07564429;
    private const uint MagicShard = 0x0756442A;
    private const int ShardHeader = 44;
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly TimeSpan ReloadTtl = TimeSpan.FromMinutes(10);
    /// <summary>How soon a catalog read while Steam was writing a shard is read again.</summary>
    internal static readonly TimeSpan PartialTtl = TimeSpan.FromSeconds(15);
    private static bool _partial;
    private static string? _root;
    private static string? _stamp;
    private static DateTime _loadedAtUtc;
    private static string? _failedRoot;
    private static DateTime _retryAtUtc;
    private static IReadOnlyDictionary<string, Entry> _loaded =
        new Dictionary<string, Entry>(StringComparer.Ordinal);

    public readonly record struct Entry(string Name, string Type, string ParentAppId = "");

    /// <summary>
    /// One of an app's <c>config/launch</c> entries, as Steam offers it in its Play menu. <see cref="Type"/> is Steam's word
    /// ("default", "option1", "none" ...), "" when the entry has none; <see cref="OsList"/> and <see cref="BetaKey"/> come
    /// from the entry's own <c>config</c> ("" when it applies to every OS or branch).
    /// </summary>
    internal sealed record LaunchEntry(
        string Id, string Executable, string Arguments, string Description, string Type, string WorkingDir, string OsList, string BetaKey);

    /// <summary>
    /// The files Steam's appinfo is in under <paramref name="steamRoot"/>: the shards when the appinfo folder holds any
    /// (an old appinfo.vdf left beside them is stale), else appinfo.vdf, else none. A folder that can't be listed gives none.
    /// </summary>
    internal static IReadOnlyList<string> Files(string steamRoot)
    {
        var folder = Path.Combine(steamRoot, "appcache", "appinfo");
        try
        {
            if (Directory.Exists(folder))
            {
                var shards = Directory.GetFiles(folder, "appinfo_*.vdf")
                    .Where(file => IsShardName(Path.GetFileName(file)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (shards.Length > 0) return shards;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var single = Path.Combine(steamRoot, "appcache", "appinfo.vdf");
        return File.Exists(single) ? [single] : [];
    }

    private static bool IsShardName(string name) =>
        name.Length == "appinfo_00.vdf".Length && name.StartsWith("appinfo_", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase) && Uri.IsHexDigit(name[8]) && Uri.IsHexDigit(name[9]);

    /// <summary>A file read while Steam may be writing it; null when it can't be read or is over 256 MB.</summary>
    internal static byte[]? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The names, types and parents of every app Steam has a record of under <paramref name="steamRoot"/>.</summary>
    public static IReadOnlyDictionary<string, Entry> Load(string? steamRoot)
        => LoadAt(steamRoot, DateTime.UtcNow);

    internal static IReadOnlyDictionary<string, Entry> LoadAt(string? steamRoot, DateTime nowUtc)
    {
        var files = string.IsNullOrWhiteSpace(steamRoot) ? [] : Files(steamRoot);
        if (files.Count == 0)
            return new Dictionary<string, Entry>(StringComparer.Ordinal);

        lock (Gate)
        {
            var sameRoot = string.Equals(_root, steamRoot, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(_failedRoot, steamRoot, StringComparison.OrdinalIgnoreCase) && nowUtc < _retryAtUtc)
                return sameRoot ? _loaded : new Dictionary<string, Entry>(StringComparer.Ordinal);
            var stamp = Stamp(files);
            if (sameRoot && !_partial && stamp is not null && _stamp == stamp)
                return _loaded;
            // Steam rewrites appinfo while running. Re-parsing several MB on every 30s library scan is what made Steam
            // miss the 25s adapter timeout. Keep a short TTL even when the files moved; a catalog missing a shard Steam was
            // writing (a cold load keeps the rest) is read again soon instead.
            if (sameRoot && _loaded.Count > 0 && nowUtc - _loadedAtUtc < (_partial ? PartialTtl : ReloadTtl))
                return _loaded;
            var partial = false;
            try
            {
                var parsed = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    var bytes = ReadShared(file) ?? throw new IOException("Steam appinfo can't be read.");
                    if (!IsKnownFormat(bytes))
                        throw new InvalidDataException("Steam appinfo header is incomplete or unsupported.");
                    // A file Steam is still writing: keep what an earlier whole read had.
                    if (ParseInto(bytes, parsed)) continue;
                    if (sameRoot && _loaded.Count > 0 && !_partial)
                        throw new InvalidDataException("Steam appinfo ended before its last record.");
                    partial = true;
                }
                if (parsed.Count == 0 && _loaded.Count > 0 && sameRoot)
                    throw new InvalidDataException("Steam appinfo did not contain a complete catalog.");
                _loaded = parsed;
                AppLog.Debug($"Steam appinfo: {parsed.Count} names from {files.Count} file(s){(partial ? ", some mid-write" : "")}.");
            }
            catch
            {
                _failedRoot = steamRoot;
                _retryAtUtc = nowUtc + TimeSpan.FromSeconds(2);
                return sameRoot ? _loaded : new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
            _root = steamRoot;
            _stamp = stamp;
            _loadedAtUtc = nowUtc;
            _partial = partial;
            _failedRoot = null;
            _retryAtUtc = default;
            return _loaded;
        }
    }

    /// <summary>
    /// Each file's name, size and write time, as one string: it changes whenever Steam writes, adds or removes a file. Null
    /// when a file went away between listing and reading (Steam is rewriting the folder).
    /// </summary>
    internal static string? Stamp(IReadOnlyList<string> files)
    {
        var stamp = new StringBuilder();
        try
        {
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (!info.Exists) return null;
                stamp.Append(info.Name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return stamp.ToString();
    }

    /// <summary>The names, types and parents in one file (either layout). What was read before a damaged record is kept.</summary>
    internal static IReadOnlyDictionary<string, Entry> Parse(byte[] data)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        ParseInto(data, result);
        return result;
    }

    /// <summary>Adds one file's names to <paramref name="result"/>; false when the file isn't whole.</summary>
    private static bool ParseInto(byte[] data, Dictionary<string, Entry> result)
    {
        int commonIdx = -1, nameIdx = -1, typeIdx = -1, parentAppIdIdx = -1, parentIdx = -1;
        string[]? indexed = null;
        return EachRecord(data, (appId, start, end, table) =>
        {
            if (!ReferenceEquals(indexed, table))
            {
                indexed = table;
                commonIdx = IndexOf(table, "common");
                nameIdx = IndexOf(table, "name");
                typeIdx = IndexOf(table, "type");
                // DLC, soundtracks, mod uploaders and bonus content name their base
                // game in common/parent; a few older records use parentappid.
                parentAppIdIdx = IndexOf(table, "parentappid");
                parentIdx = IndexOf(table, "parent");
            }
            if (commonIdx < 0 || nameIdx < 0) return true;
            var pos = start;
            string? name = null;
            string? type = null;
            string? parentAppId = null;
            WalkDict(
                data, ref pos, end, table, commonIdx, nameIdx, typeIdx, parentAppIdIdx, parentIdx,
                inCommon: false, ref name, ref type, ref parentAppId);
            if (!string.IsNullOrWhiteSpace(name))
                result[appId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new Entry(
                    name.Trim(),
                    type?.Trim() ?? "",
                    parentAppId?.Trim() ?? "");
            return true;
        });
    }

    /// <summary>The file is appinfo of a layout Exo reads: the v41 file or a shard.</summary>
    internal static bool IsKnownFormat(byte[] data) =>
        data.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(data) is MagicV41 or MagicShard;

    /// <summary>
    /// Every app's whole record, one at a time. False when the file isn't of the format Exo reads, or ends before its last
    /// record (Steam is writing it): what was read then is not the whole file.
    /// </summary>
    internal static bool EachApp(byte[] data, Action<uint, SteamKeyValues> each) =>
        EachRecord(data, (appId, start, end, table) =>
        {
            each(appId, ReadRecord(data, start, end, table));
            return true;
        });

    /// <summary>
    /// The whole record (<c>common</c>, <c>extended</c>, <c>depots</c> ...) of each
    /// requested app, for a game's Properties. Apps not in the file are left out.
    /// </summary>
    internal static Dictionary<uint, SteamKeyValues> ReadApps(byte[] data, IReadOnlySet<uint> appIds)
    {
        var result = new Dictionary<uint, SteamKeyValues>();
        if (appIds.Count == 0) return result;
        EachRecord(data, (appId, start, end, table) =>
        {
            if (appIds.Contains(appId) && !result.ContainsKey(appId))
                result[appId] = ReadRecord(data, start, end, table);
            return result.Count < appIds.Count;
        });
        return result;
    }

    /// <summary>
    /// The whole record of each requested app from every appinfo file under <paramref name="steamRoot"/>. Apps Steam has
    /// no record of, and files that can't be read, are left out.
    /// </summary>
    internal static Dictionary<uint, SteamKeyValues> ReadApps(string steamRoot, IReadOnlySet<uint> appIds)
    {
        var result = new Dictionary<uint, SteamKeyValues>();
        foreach (var file in Files(steamRoot))
        {
            if (result.Count == appIds.Count) break;
            if (ReadShared(file) is not { } bytes) continue;
            foreach (var (id, app) in ReadApps(bytes, appIds.Where(id => !result.ContainsKey(id)).ToHashSet()))
                result[id] = app;
        }
        return result;
    }

    /// <summary>An app's <c>config/launch</c> entries in Steam's order; an entry without a program is left out.</summary>
    internal static IReadOnlyList<LaunchEntry> LaunchEntries(SteamKeyValues app)
    {
        var entries = new List<LaunchEntry>();
        foreach (var (id, entry) in app.Section("config/launch").Sections)
        {
            var executable = entry.Value("executable")?.Trim() ?? "";
            if (executable.Length == 0) continue;
            var description = entry.Value("description")?.Trim();
            if (string.IsNullOrEmpty(description)) description = entry.Section("description_loc").Value("english")?.Trim() ?? "";
            var config = entry.Section("config");
            entries.Add(new LaunchEntry(
                id,
                executable,
                entry.Value("arguments")?.Trim() ?? "",
                description,
                entry.Value("type")?.Trim() ?? "",
                entry.Value("workingdir")?.Trim() ?? "",
                config.Value("oslist")?.Trim() ?? "",
                config.Value("betakey")?.Trim() ?? ""));
        }
        return entries;
    }

    /// <summary>
    /// The Play-menu entry that starts the game without its anti-cheat, as Steam itself offers it (NBA 2K26: the default is
    /// <c>start_protected_game.exe</c>, Easy Anti-Cheat's bootstrapper, and option1 "NBA 2K26 without EAC (offline only)" is
    /// <c>NBA2K26.exe</c>). <see cref="Option"/> is Steam's own word for it ("option1"), what <c>steam://launch</c> takes.
    /// </summary>
    internal sealed record AntiCheatFreeLaunch(string Option, string Description, string Executable);

    /// <summary>
    /// The entry of <paramref name="entries"/> that avoids the anti-cheat, or null. Only for a game with more than one
    /// Windows entry for every branch (an entry tied to a beta applies only there): a protected one, first in Steam's order
    /// (its program the anti-cheat's bootstrapper, or the other's words saying it is without), and another named by
    /// Steam's "optionN" whose program is not a bootstrapper and which either says it is without anti-cheat or offline, or
    /// sits beside a bootstrapper. An entry that only runs another program (an editor, a benchmark) beside a plain game
    /// is not one.
    /// </summary>
    internal static AntiCheatFreeLaunch? WithoutAntiCheat(IReadOnlyList<LaunchEntry> entries)
    {
        var usable = entries.Where(entry => entry.BetaKey.Length == 0 && entry.Type != "none" &&
                (entry.OsList.Length == 0 || entry.OsList.Contains("windows", StringComparison.OrdinalIgnoreCase)) &&
                entry.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (usable.Count < 2) return null;
        var main = usable.FirstOrDefault(entry => entry.Type is "default" or "") ?? usable[0];
        foreach (var entry in usable)
        {
            if (ReferenceEquals(entry, main) || !IsOption(entry.Type) || IsAntiCheatBootstrapper(entry.Executable) || SaysWithAntiCheat(entry.Description)) continue;
            if (SaysWithoutAntiCheat(entry.Description) || IsAntiCheatBootstrapper(main.Executable))
                return new AntiCheatFreeLaunch(entry.Type, entry.Description, entry.Executable);
        }
        return null;
    }

    private static bool IsOption(string type) =>
        type.Length is > 6 and <= 9 && type.StartsWith("option", StringComparison.Ordinal) && type[6..].All(char.IsAsciiDigit) && type[6] != '0';

    /// <summary>A program whose only job is to start the anti-cheat and then the game.</summary>
    internal static bool IsAntiCheatBootstrapper(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable.Replace('\\', '/').Split('/')[^1]);
        return name.Equals("start_protected_game", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("BattlEye", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_BE", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_EAC", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("BELauncher", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("EACLauncher", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex AntiCheatWord = new(
        @"(?<![a-z])(EAC|easy\s*anti-?cheat|anti-?cheat|BattlEye)(?![a-z])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex WithoutWord = new(
        @"(?<![a-z])(without|no|disabled?|off)(?![a-z])[^.]{0,40}?(?<![a-z])(EAC|easy\s*anti-?cheat|anti-?cheat|BattlEye)(?![a-z])|(?<![a-z])offline(?![a-z])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool SaysWithoutAntiCheat(string description) => WithoutWord.IsMatch(description);

    /// <summary>Names an anti-cheat and does not say it is without it ("Play with EasyAntiCheat").</summary>
    private static bool SaysWithAntiCheat(string description) => AntiCheatWord.IsMatch(description) && !SaysWithoutAntiCheat(description);

    /// <summary>One record's key values; the record's single root is "appinfo", and callers read below it.</summary>
    private static SteamKeyValues ReadRecord(byte[] data, int start, int end, string[] table)
    {
        var record = new SteamKeyValues();
        var at = start;
        ReadSection(data, ref at, end, table, record, 0);
        return record.Keys.Count == 1 ? record.Section(record.Keys[0]) : record;
    }

    /// <summary>A record: its app id, where its key values start and end in the file, and the key table they index.
    /// Answers whether to go on.</summary>
    private delegate bool RecordVisitor(uint appId, int start, int end, string[] table);

    /// <summary>
    /// Walks every record of either layout until <paramref name="visit"/> answers false. False when the file isn't of a
    /// layout Exo reads or isn't whole (Steam is writing it); records before the damage have been visited.
    /// </summary>
    private static bool EachRecord(byte[] data, RecordVisitor visit)
    {
        if (data.Length < 16) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(data) switch
        {
            MagicV41 => EachRecordV41(data, visit),
            MagicShard => EachRecordShard(data, visit),
            _ => false,
        };
    }

    private static bool EachRecordV41(byte[] data, RecordVisitor visit)
    {
        var tableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        if (tableOffset < 16 || tableOffset >= data.Length) return false;
        var endOfApps = (int)tableOffset;
        var table = ReadStringTable(data, endOfApps);
        if (table.Length == 0) return false;
        var pos = 16;
        while (pos + 4 <= endOfApps)
        {
            var appId = ReadU32(data, ref pos, endOfApps);
            if (appId == 0) return true;
            if (pos + 4 > endOfApps) return false;
            var size = ReadU32(data, ref pos, endOfApps);
            var blobEnd = pos + (long)size;
            if (blobEnd > endOfApps || pos + 60 > blobEnd) return false;
            if (!visit(appId, pos + 60, (int)blobEnd, table)) return true;
            pos = (int)blobEnd;
        }
        return false;
    }

    private static bool EachRecordShard(byte[] data, RecordVisitor visit)
    {
        if (data.Length < ShardHeader) return false;
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        var shard = U(8);
        var shards = U(12);
        var apps = U(16);
        long indexSize = U(20), keyCount = U(24), keySize = U(28), dataSize = U(32);
        if (shards is 0 or > 65536 || shard >= shards || keyCount is 0 or > 1_000_000) return false;
        if (ShardHeader + indexSize + keySize + dataSize != data.Length) return false;
        var indexEnd = ShardHeader + (int)indexSize;
        var keysEnd = indexEnd + (int)keySize;
        var table = new string[keyCount];
        var at = indexEnd;
        for (var i = 0; i < table.Length; i++)
        {
            var nul = Array.IndexOf(data, (byte)0, at, keysEnd - at);
            if (nul < 0) return false;
            table[i] = Encoding.UTF8.GetString(data, at, nul - at);
            at = nul + 1;
        }
        if (at != keysEnd) return false;
        var pos = ShardHeader;
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        for (var i = 0u; i < apps; i++)
        {
            if (pos + 16 > indexEnd) return false;
            var appId = U(pos);
            long offset = U(pos + 4), size = U(pos + 8), more = U(pos + 12);
            pos += 16;
            if (more > indexEnd - pos || size <= hash.Length || offset + size > dataSize) return false;
            pos += (int)more;
            var start = keysEnd + (int)offset;
            // The record opens with a SHA-1 of the rest: one that doesn't match is a record Steam is still writing.
            SHA1.HashData(data.AsSpan(start + hash.Length, (int)size - hash.Length), hash);
            if (!hash.SequenceEqual(data.AsSpan(start, hash.Length))) return false;
            if (!visit(appId, start + hash.Length, start + (int)size, table)) return true;
        }
        return pos == indexEnd;
    }

    private static void ReadSection(byte[] data, ref int pos, int end, string[] table, SteamKeyValues into, int depth)
    {
        while (pos < end)
        {
            var kind = data[pos++];
            if (kind == 8 || pos + 4 > end) return;
            var index = (int)ReadU32(data, ref pos, end);
            var key = index >= 0 && index < table.Length ? table[index] : "";
            switch (kind)
            {
                case 0:
                    if (depth > 32) return;
                    ReadSection(data, ref pos, end, table, into.Add(key), depth + 1);
                    break;
                case 1:
                    into.Set(key, ReadCString(data, ref pos, end));
                    break;
                case 2:
                    into.Set(key, ((int)ReadU32(data, ref pos, end)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case 7:
                case 10:
                    if (pos + 8 > end) return;
                    into.Set(key, kind == 7
                        ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    pos += 8;
                    break;
                case 3:
                case 4:
                case 6:
                    pos += 4;
                    break;
                case 5:
                    SkipUtf16(data, ref pos, end);
                    break;
                default:
                    return;
            }
        }
    }

    private static void WalkDict(
        byte[] data,
        ref int pos,
        int end,
        string[] table,
        int commonIdx,
        int nameIdx,
        int typeIdx,
        int parentAppIdIdx,
        int parentIdx,
        bool inCommon,
        ref string? name,
        ref string? type,
        ref string? parentAppId)
    {
        while (pos < end)
        {
            var kind = data[pos++];
            if (kind == 8) return;
            if (pos + 4 > end) return;
            var key = (int)ReadU32(data, ref pos, end);
            switch (kind)
            {
                case 0:
                    WalkDict(
                        data, ref pos, end, table, commonIdx, nameIdx, typeIdx, parentAppIdIdx, parentIdx,
                        inCommon: key == commonIdx, ref name, ref type, ref parentAppId);
                    break;
                case 1:
                    var value = ReadCString(data, ref pos, end);
                    if (!inCommon) break;
                    if (key == nameIdx && name is null) name = value;
                    else if (key == typeIdx && type is null) type = value;
                    else if (IsParentKey(key, parentAppIdIdx, parentIdx) && parentAppId is null && !string.IsNullOrWhiteSpace(value) && value.Trim() != "0")
                        parentAppId = value.Trim();
                    break;
                case 2:
                    var number = ReadU32(data, ref pos, end);
                    if (inCommon && IsParentKey(key, parentAppIdIdx, parentIdx) && parentAppId is null && number != 0)
                        parentAppId = number.ToString();
                    break;
                case 3:
                case 4:
                case 6:
                    pos += 4;
                    break;
                case 7:
                case 10:
                    pos += 8;
                    break;
                case 5:
                    SkipUtf16(data, ref pos, end);
                    break;
                default:
                    return;
            }
        }
    }

    private static bool IsParentKey(int key, int parentAppIdIdx, int parentIdx) =>
        (parentAppIdIdx >= 0 && key == parentAppIdIdx) || (parentIdx >= 0 && key == parentIdx);

    private static string[] ReadStringTable(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return [];
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        if (count is < 1 or > 1_000_000) return [];
        var table = new string[count];
        var pos = offset + 4;
        for (var i = 0; i < count; i++)
        {
            if (pos >= data.Length) return [];
            table[i] = ReadCString(data, ref pos, data.Length);
        }
        return table;
    }

    private static int IndexOf(string[] table, string value)
    {
        for (var i = 0; i < table.Length; i++)
        {
            if (string.Equals(table[i], value, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static uint ReadU32(byte[] data, ref int pos, int end)
    {
        if (pos + 4 > end || pos + 4 > data.Length) return 0;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        return value;
    }

    private static string ReadCString(byte[] data, ref int pos, int end)
    {
        var start = pos;
        while (pos < end && pos < data.Length && data[pos] != 0)
            pos++;
        var text = start < pos
            ? Encoding.UTF8.GetString(data, start, pos - start)
            : "";
        if (pos < end && pos < data.Length && data[pos] == 0)
            pos++;
        return text;
    }

    private static void SkipUtf16(byte[] data, ref int pos, int end)
    {
        while (pos + 1 < end && pos + 1 < data.Length)
        {
            if (data[pos] == 0 && data[pos + 1] == 0)
            {
                pos += 2;
                return;
            }
            pos += 2;
        }
    }
}
