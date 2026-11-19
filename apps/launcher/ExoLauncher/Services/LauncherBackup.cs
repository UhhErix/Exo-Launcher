using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Exo Launcher's backups: every file the player's choices live in (<see cref="PlayerData.Files"/>: settings, playtime, the
/// Riot ledger and ID, covers and folder pictures, games added by hand, the wishlist, mouse and controller memory, Lucid's
/// settings, Optimize's undo data, start-up choices) copied into one JSON file, so a reinstall or a new PC gets it back. Daily
/// backups go to Documents\Exo\Backups\Launcher; export and import use the same file. The file names carry this PC's short id,
/// so two PCs on one Documents folder never overwrite each other.
///
/// Not in a backup, on purpose: every token and sign-in (Steam, Discord, Epic, GOG, Riot, Exo account sessions, identity). Those
/// are sealed for this Windows user and would not open on a new install; the person signs in again. Only the files the
/// registry names are ever read, so a new token file is never copied by accident.
///
/// A backup is refused, and nothing written, when a file it would copy exists but cannot be read: a good backup of the day is
/// never replaced by a damaged one. A restore validates every file first (all or nothing), keeps a copy of the data it replaces
/// first, then replaces the files together: each is written beside its place, and only when all are written are they moved into
/// place. If one cannot be moved, the ones already replaced are put back from the bytes read first.
/// </summary>
internal static class LauncherBackup
{
    public const string Kind = "exo-launcher-backup";
    public const int Schema = 1;
    public const int KeepDaily = 30;
    public const int KeepBeforeRestore = 3;
    /// <summary>A backup file larger than this is refused before it is read: a restore reads it whole.</summary>
    internal const long MaxBackupBytes = 256L * 1024 * 1024;
    private const int MaxExtraArgs = 1024;

    private const string DailyPrefix = "exo-launcher-backup-";
    private const string BeforePrefix = "exo-launcher-before-restore-";
    private const string DeviceFile = "backup-device.txt";
    private const string AnsweredFile = "backup-answered.txt";

    private static readonly Regex DeviceShape = new("^[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex DailyName = new(@"^exo-launcher-backup-(\d{8})-([0-9a-f]{8})\.json\z", RegexOptions.CultureInvariant);
    // Exo's own two kinds of backup in the folder: the daily, and the copy kept before a restore.
    private static readonly Regex OwnBackupName = new(@"^exo-launcher-(backup-\d{8}|before-restore-\d{1,13})-[0-9a-f]{8}\.json\z", RegexOptions.CultureInvariant);
    private static readonly Regex DayShape = new(@"^\d{4}-\d{2}-\d{2}\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SettingsJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>One file in a backup, by its path inside the data folder (forward slashes).</summary>
    internal sealed record Entry(string Name, byte[] Bytes);

    /// <summary>
    /// The result of a read: Files is the whole list when Ok, empty otherwise. Machine is the salted mark of the PC the backup
    /// was made on, null for a backup that does not carry one. MissingPictures counts the pictures it names that are not in the
    /// backups folder (a restore says so).
    /// </summary>
    internal sealed record Read(bool Ok, string Message, IReadOnlyList<Entry> Files, MachineMark? Machine = null, int MissingPictures = 0);

    /// <summary>
    /// A backup's one-way mark of the PC it was made on: a random salt of its own and SHA-256(salt and the PC's hardware id),
    /// so two backups of one PC carry different marks and the id itself never leaves the PC.
    /// </summary>
    internal sealed record MachineMark(string Salt, string Mark)
    {
        internal static MachineMark Of(string machine, byte[]? salt = null)
        {
            salt ??= RandomNumberGenerator.GetBytes(16);
            return new(Convert.ToHexStringLower(salt), Hash(salt, machine));
        }

        /// <summary>Whether this mark was made on the PC whose hardware id is <paramref name="machine"/>.</summary>
        internal bool Is(string machine)
        {
            try { return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(Convert.FromHexString(Salt), machine)), Encoding.ASCII.GetBytes(Mark)); }
            catch (FormatException) { return false; }
        }

        private static string Hash(byte[] salt, string machine) =>
            Convert.ToHexStringLower(SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(machine)]));
    }

    /// <summary>
    /// Where a backup is made or restored: the Windows user's profile folder and name (a backup holds neither: a path under the
    /// profile becomes <c>%EXO_PROFILE%</c> and a task's user <c>%EXO_USER%</c>, filled in again for whoever restores), this
    /// PC's hardware id (<see cref="PcHealth.MachineId"/>, which outlives a reinstall; only a salted mark of it is written), and
    /// Lucid's own settings file.
    /// </summary>
    internal sealed record Here(string Profile, string User, string Machine, string? LucidSettings = null);

    /// <summary>This PC and Windows user, for a backup or restore of <paramref name="dataDir"/>.</summary>
    internal static Here ThisPc(string dataDir, string? lucidSettings = null) => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.UserDomainName + "\\" + Environment.UserName,
        PcHealth.MachineId() ?? "install-" + DeviceId(dataDir),
        lucidSettings);

    private const string ProfileMark = "%EXO_PROFILE%";
    private const string ProfileMarkSlash = "%EXO_PROFILE_SLASH%";
    private const string UserMark = "%EXO_USER%";
    private static readonly Regex TaskUser = new(@"<(UserId|Author)>([^<]*)</\1>", RegexOptions.CultureInvariant);

    /// <summary>Windows' own accounts a task may run as: never a person's name, and never handed to whoever restores.</summary>
    internal static bool IsWindowsAccount(string user) => user.Trim().ToUpperInvariant() is "S-1-5-18" or "S-1-5-19" or "S-1-5-20"
        or "SYSTEM" or "NT AUTHORITY\\SYSTEM" or "LOCAL SERVICE" or "NT AUTHORITY\\LOCAL SERVICE" or "NT AUTHORITY\\LOCALSERVICE"
        or "NETWORK SERVICE" or "NT AUTHORITY\\NETWORK SERVICE" or "NT AUTHORITY\\NETWORKSERVICE";

    private static bool IsJson(PlayerData.Shape shape) => shape is PlayerData.Shape.JsonObject or PlayerData.Shape.JsonArray or PlayerData.Shape.Json or PlayerData.Shape.Shortcut;

    /// <summary>
    /// The profile folder where it is a whole path part: <c>C:\Users\Al</c> in <c>C:\Users\Al\x</c> but not in
    /// <c>C:\Users\Alice</c>. Backslashes or forward slashes, any case.
    /// </summary>
    private static Regex ProfileIn(string profile, char separator)
    {
        var parts = Path.TrimEndingDirectorySeparator(profile).Split('\\', '/').Select(Regex.Escape);
        return new Regex(string.Join(Regex.Escape(separator.ToString()), parts) + @"(?![^\\/""'\s;,)\]<>&|])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The profile in a text, as written with backslashes or with forward slashes, each to its own mark.</summary>
    private static string MarkProfile(string text, string profile) =>
        ProfileIn(profile, '/').Replace(ProfileIn(profile, '\\').Replace(text, ProfileMark), ProfileMarkSlash);

    /// <summary>
    /// A text file as a backup holds it. A path under the user's profile becomes <see cref="ProfileMark"/>, on a path boundary;
    /// a JSON file is read and each string rewritten (so an escaped form, <c>Jos\u00e9</c>, is caught), and written again only
    /// when one changed. A task export's user becomes <see cref="UserMark"/>, unless it is one of Windows' own accounts. What
    /// is not scrubbed, on purpose: a path outside the profile, and words in arguments or a description, are kept as written.
    /// </summary>
    internal static string Anonymize(string text, PlayerData.Shape shape, Here here)
    {
        if (here.Profile.Length > 3)
        {
            text = IsJson(shape) ? RewriteStrings(text, value => MarkProfile(value, here.Profile)) : MarkProfile(text, here.Profile);
        }
        return shape == PlayerData.Shape.TaskXml
            ? TaskUser.Replace(text, match => IsWindowsAccount(System.Net.WebUtility.HtmlDecode(match.Groups[2].Value)) ? match.Value : $"<{match.Groups[1].Value}>{UserMark}</{match.Groups[1].Value}>")
            : text;
    }

    /// <summary>A text file from a backup as this PC writes it: the profile and user of whoever restores.</summary>
    internal static string Personalize(string text, PlayerData.Shape shape, Here here)
    {
        var profile = Path.TrimEndingDirectorySeparator(here.Profile);
        var slash = profile.Replace('\\', '/');
        if (IsJson(shape)) return RewriteStrings(text, value => value.Replace(ProfileMark, profile, StringComparison.Ordinal).Replace(ProfileMarkSlash, slash, StringComparison.Ordinal));
        var xml = shape == PlayerData.Shape.TaskXml;
        return text.Replace(ProfileMark, xml ? System.Security.SecurityElement.Escape(profile) : profile, StringComparison.Ordinal)
            .Replace(ProfileMarkSlash, xml ? System.Security.SecurityElement.Escape(slash) : slash, StringComparison.Ordinal)
            .Replace(UserMark, System.Security.SecurityElement.Escape(here.User), StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Each string in a JSON document (values and names) through <paramref name="rewrite"/>; the text as it was when none changed.</summary>
    private static string RewriteStrings(string json, Func<string, string> rewrite)
    {
        var changed = false;
        string Change(string value)
        {
            var next = rewrite(value);
            changed |= !string.Equals(next, value, StringComparison.Ordinal);
            return next;
        }
        JsonNode? Walk(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(Change(pair.Key), Walk(pair.Value)))),
            JsonArray array => new JsonArray([.. array.Select(Walk)]),
            JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonValue.Create(Change(value.GetValue<string>())),
            _ => node?.DeepClone(),
        };
        var rewritten = Walk(JsonNode.Parse(json));
        return changed ? rewritten?.ToJsonString(Readable) ?? json : json;
    }
    /// <summary>What a backup, export or restore did, in words the page can show.</summary>
    internal sealed record Outcome(bool Ok, string Message, int Files = 0, Result Kind = Result.Written, string? Path = null);

    /// <summary>The newest daily backup that reads back whole: its day (yyyy-MM-dd) and how many files it holds.</summary>
    internal sealed record Offer(string Path, string Day, int Files, string Device);

    /// <summary>
    /// The default folder: Documents\Exo\Backups\Launcher. MyDocuments is resolved by .NET through the Documents known folder
    /// (SHGetKnownFolderPath), so a moved or OneDrive-redirected Documents is right.
    /// </summary>
    internal static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Exo", "Backups", "Launcher");

    /// <summary>The names a backup may hold, relative to the data folder: the files in <see cref="PlayerData.Files"/>. Anything else is refused.</summary>
    internal static bool IsAllowedName(string name) => PlayerData.Of(name) is not null;

    /// <summary>Whether a backup file of this many bytes is read at all.</summary>
    internal static bool FitsBackupLimit(long length) => length <= MaxBackupBytes;

    /// <summary>Launch arguments as a restore keeps them: at most 1024 characters, control characters dropped; blank is none.</summary>
    internal static string? ClampExtraArgs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var kept = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return kept.Length > MaxExtraArgs ? kept[..MaxExtraArgs] : kept;
    }

    /// <summary>
    /// Reads every file of <see cref="PlayerData.Files"/> that exists in the data folder, in name order. With
    /// <paramref name="lucidSettings"/> (Lucid's own settings file), Lucid's settings as Exo sets them are added as
    /// lucid-held.json, which Exo's next start puts back into Lucid. Throws IOException when one cannot be read.
    /// </summary>
    internal static IReadOnlyList<Entry> Gather(string dataDir, Here? here = null)
    {
        here ??= ThisPc(dataDir);
        var lucidSettings = here.LucidSettings;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var chosen = ChosenNames(dataDir);
        foreach (var file in PlayerData.Files)
        {
            if (!file.Path.Contains('*')) { names.Add(file.Path); continue; }
            var depth = file.Path.Count(c => c == '/');
            var root = Path.Combine(dataDir, file.Path[..file.Path.IndexOf('/')]);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", depth > 1 ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetRelativePath(dataDir, path).Replace(Path.DirectorySeparatorChar, '/');
                if (PlayerData.Of(name) != file) continue;
                if (file.ChosenBySettings && !chosen.Contains(name)) continue;
                names.Add(name);
            }
        }
        var entries = new List<Entry>();
        foreach (var name in names) AddIfThere(entries, dataDir, name, here);
        if (lucidSettings is not null && LucidSnapshot(dataDir, lucidSettings) is { } lucid)
        {
            entries.RemoveAll(entry => entry.Name == LucidHeld);
            entries.Add(new Entry(LucidHeld, lucid));
        }
        return entries;
    }

    private const string LucidHeld = "lucid-held.json";

    /// <summary>
    /// Lucid's settings as Exo sets them (the headphone, favourites, game mode, night shift, sounds and the on/off key), read
    /// from Lucid's own file, with a change still waiting for a game to end laid over them. Null when Lucid has no settings.
    /// </summary>
    private static byte[]? LucidSnapshot(string dataDir, string lucidSettings)
    {
        if (LucidSettings.Load(lucidSettings) is not { } root) return null;
        var view = LucidSettings.Read(root);
        var now = new LucidSettings.Change
        {
            Headphone = view.HeadphoneChosen ? view.HeadphoneId : null,
            Favorites = view.Favorites.ToDictionary(id => id, _ => true, StringComparer.OrdinalIgnoreCase),
            GameMode = view.GameMode,
            KeepOnAltTab = view.KeepOnAltTab,
            OnlyWhileGaming = view.OnlyWhileGaming,
            NightShift = view.NightShift,
            NightStart = view.NightStart,
            NightEnd = view.NightEnd,
            NightSync = view.NightSync,
            Notifications = view.Notifications,
            Discord = view.Discord,
            ToggleSound = view.ToggleSound,
            DetectSound = view.DetectSound,
            Hotkey = (view.HotkeyModifiers, view.HotkeyKey),
        };
        var held = Path.Combine(dataDir, LucidHeld);
        if (File.Exists(held) && LucidSettings.FromStored(File.ReadAllText(held)) is { } waiting) now = now.Then(waiting);
        return Encoding.UTF8.GetBytes(LucidSettings.Stored(now));
    }

    private static void AddIfThere(List<Entry> entries, string dataDir, string name, Here here)
    {
        var path = Path.Combine(dataDir, name.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return;
        var file = PlayerData.Of(name) ?? throw new IOException(name + " is not backed up.");
        var length = new FileInfo(path).Length;
        // One file past its limit stays out, and the rest of the backup is still written.
        if (!FitsBackupLimit(length) || length > file.MaxBytes)
        {
            AppLog.Warn("Backup: a file is too large to back up and was left out: " + name);
            return;
        }
        byte[] bytes;
        if (file.Shape == PlayerData.Shape.Shortcut)
        {
            // What the shortcut does, not its bytes: those carry this PC's name, its volume and tracker ids.
            if (ShellLinkFile.Read(path) is not { } link)
            {
                AppLog.Warn("Backup: a start-up shortcut could not be read and was left out: " + name);
                return;
            }
            bytes = JsonSerializer.SerializeToUtf8Bytes(link);
        }
        else bytes = File.ReadAllBytes(path);
        if (!file.IsBinary)
        {
            try { bytes = Encoding.UTF8.GetBytes(Anonymize(Encoding.UTF8.GetString(bytes), file.Shape, here)); }
            catch (JsonException)
            {
                AppLog.Warn("Backup: a file did not read as what it should hold and was left out: " + name);
                return;
            }
        }
        // A file that would not pass a restore's check (a damaged controller file, a half-written list) stays out on its own,
        // so it never makes the whole backup unrestorable.
        using var held = JsonDocument.Parse(file.IsBinary
            ? new JsonObject { ["base64"] = Convert.ToBase64String(bytes) }.ToJsonString()
            : new JsonObject { ["text"] = Encoding.UTF8.GetString(bytes) }.ToJsonString());
        byte[]? kept;
        try { kept = Content(name, held.RootElement); }
        catch (Exception ex) when (ex is JsonException or FormatException) { kept = null; }
        if (kept is null)
        {
            AppLog.Warn("Backup: a file did not read as what it should hold and was left out: " + name);
            return;
        }
        entries.Add(new Entry(name, kept));
    }

    /// <summary>
    /// A ledger's lines that read as JSON objects, in order. A torn or foreign line is dropped, as the Riot reader drops it, so
    /// one bad line never makes a backup unrestorable.
    /// </summary>
    private static byte[] KeepReadableLines(byte[] bytes)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n').Select(raw => raw.Trim()).Where(RiotLedger.IsWellFormedLine).ToList();
        return Encoding.UTF8.GetBytes(lines.Count == 0 ? "" : string.Join('\n', lines) + "\n");
    }

    /// <summary>The pictures the settings choose (custom covers and folder pictures), so only those are copied.</summary>
    private static HashSet<string> ChosenNames(string dataDir)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var settings = Path.Combine(dataDir, "settings.json");
        if (!File.Exists(settings)) return chosen;
        AppSettings? loaded;
        try { loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settings), SettingsJson); }
        catch (JsonException ex) { throw new IOException("settings.json cannot be read.", ex); }
        if (loaded is null) return chosen;
        foreach (var value in loaded.CustomCoverImages.Values.Concat(loaded.LibraryFolders.Select(folder => folder.Art)))
        {
            var name = Path.GetFileName(value ?? "");
            if (name.Length > 0) chosen.Add("covers/" + name);
        }
        return chosen;
    }

    private const string PicturePrefix = "exo-launcher-picture-";
    private static readonly Regex PictureName = new(@"^exo-launcher-picture-([0-9a-f]{8})-([0-9a-f]{64})\.(png|jpg)\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// A picture's file in the backups folder: the PC that wrote it (its short id) and the picture's SHA-256, so the thirty
    /// daily copies hold it once, and a PC only ever removes pictures it wrote itself.
    /// </summary>
    internal static string PictureFileOf(byte[] bytes, string ext, string device) => $"{PicturePrefix}{device}-{Convert.ToHexStringLower(SHA256.HashData(bytes))}{ext}";

    /// <summary>
    /// A backup document: the files as text (JSON) or base64 (a game's own file), with its kind, schema, when it was made and a
    /// salted mark of the PC (<see cref="MachineMark"/>). With <paramref name="pictures"/> (the backups folder) and this PC's
    /// <paramref name="device"/>, each picture is a file there and the document names it; without (an export), the picture is
    /// in the document, so the file stands alone.
    /// </summary>
    internal static string Serialize(IEnumerable<Entry> entries, DateTimeOffset now, string version, MachineMark? machine = null, string? pictures = null, string? device = null, bool writePictures = true)
    {
        var files = new JsonObject();
        foreach (var entry in entries)
        {
            var shape = PlayerData.Of(entry.Name)?.Shape;
            if (shape == PlayerData.Shape.Picture && pictures is not null && device is not null)
            {
                var name = PictureFileOf(entry.Bytes, Path.GetExtension(entry.Name), device);
                var path = Path.Combine(pictures, name);
                if (writePictures && !File.Exists(path))
                {
                    File.WriteAllBytes(path + ".tmp", entry.Bytes);
                    File.Move(path + ".tmp", path, overwrite: true);
                    // Stamped with the backup's own time, the clock this backup was handed (the clean-up compares these times).
                    File.SetLastWriteTimeUtc(path, now.UtcDateTime);
                }
                files[entry.Name] = new JsonObject { ["picture"] = name };
                continue;
            }
            files[entry.Name] = PlayerData.Of(entry.Name)?.IsBinary == true
                ? new JsonObject { ["base64"] = Convert.ToBase64String(entry.Bytes) }
                : new JsonObject { ["text"] = Encoding.UTF8.GetString(entry.Bytes) };
        }
        var document = new JsonObject
        {
            ["kind"] = Kind,
            ["schema"] = Schema,
            ["createdUtc"] = now.UtcDateTime.ToString("O"),
            ["version"] = version,
            ["machine"] = machine is null ? null : new JsonObject { ["salt"] = machine.Salt, ["mark"] = machine.Mark },
            ["files"] = files,
        };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reads a backup file in full and checks every file's name and shape. Nothing is restored from a refused one. A picture
    /// the document names is read from <paramref name="pictures"/> (the backups folder) and must hash to its name; one that is
    /// not there is counted in <see cref="Read.MissingPictures"/>, so the restore can say so, and the rest is restored.
    /// </summary>
    internal static Read Validate(string text, string? pictures = null)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.GetString() != Kind)
                return Refused("This is not an Exo Launcher backup.");
            if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != Schema)
                return Refused("This backup was made by a different version of Exo, so nothing was restored.");
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return Refused("This backup has no files.");

            var entries = new List<Entry>();
            var missing = 0;
            foreach (var property in files.EnumerateObject())
            {
                if (!IsAllowedName(property.Name)) return Refused($"This backup holds a file Exo does not restore ({property.Name}), so nothing was restored.");
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("picture", out var picture))
                {
                    if (PictureFrom(pictures, picture.GetString(), property.Name) is not { } stored)
                    {
                        AppLog.Warn("Restore: a picture the backup names is not in the backups folder: " + property.Name);
                        missing++;
                        continue;
                    }
                    entries.Add(new Entry(property.Name, stored));
                    continue;
                }
                var bytes = Content(property.Name, value);
                if (bytes is null) return Refused($"{property.Name} in this backup is damaged, so nothing was restored.");
                entries.Add(new Entry(property.Name, bytes));
            }
            if (entries.Count == 0) return Refused("This backup has no files.");
            MachineMark? machine = root.TryGetProperty("machine", out var made) && made.ValueKind == JsonValueKind.Object
                && made.TryGetProperty("salt", out var salt) && made.TryGetProperty("mark", out var mark)
                ? new MachineMark(salt.GetString() ?? "", mark.GetString() ?? "") : null;
            return new Read(true, "", entries, machine, missing);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return Refused("This is not an Exo Launcher backup.");
        }
    }

    private static Read Refused(string message) => new(false, message, []);

    /// <summary>A picture kept in the backups folder, when it is there, hashes to its name and is the picture its entry says.</summary>
    private static byte[]? PictureFrom(string? folder, string? name, string entry)
    {
        if (folder is null || name is null || PictureName.Match(name) is not { Success: true } parts || PlayerData.Of(entry) is not { Shape: PlayerData.Shape.Picture } file) return null;
        var path = Path.Combine(folder, name);
        if (!File.Exists(path) || new FileInfo(path).Length > file.MaxBytes) return null;
        var bytes = File.ReadAllBytes(path);
        return PictureFileOf(bytes, Path.GetExtension(name), parts.Groups[1].Value) == name && Path.GetExtension(name) == Path.GetExtension(entry)
            && Account.AccountSyncApply.IsPictureOf(bytes, Path.GetExtension(entry)) ? bytes : null;
    }
    /// <summary>The bytes of one file's value, or null when its shape is wrong.</summary>
    private static byte[]? Content(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || PlayerData.Of(name) is not { } file) return null;
        if (file.IsBinary)
        {
            if (!value.TryGetProperty("base64", out var encoded) || encoded.ValueKind != JsonValueKind.String) return null;
            if (encoded.GetString()!.Length > (file.MaxBytes + 2) / 3 * 4) return null;
            var bytes = Convert.FromBase64String(encoded.GetString()!);
            if (bytes.Length == 0 || bytes.Length > file.MaxBytes) return null;
            return file.Shape switch
            {
                PlayerData.Shape.Picture => Account.AccountSyncApply.IsPictureOf(bytes, Path.GetExtension(name)) ? bytes : null,
                _ => bytes,
            };
        }
        if (!value.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        var content = text.GetString()!;
        if (Encoding.UTF8.GetByteCount(content) > file.MaxBytes) return null;
        switch (file.Shape)
        {
            case PlayerData.Shape.JsonLines:
                return KeepReadableLines(Encoding.UTF8.GetBytes(content));
            case PlayerData.Shape.RiotId:
                return RiotIdStore.Normalize(content) is { } riotId ? Encoding.UTF8.GetBytes(riotId) : null;
            case PlayerData.Shape.TaskXml:
                return content.TrimStart('\uFEFF').TrimStart().StartsWith('<') ? Encoding.UTF8.GetBytes(content) : null;
            case PlayerData.Shape.Shortcut:
                // What a start-up shortcut does (ShellLinkFile.Link): a target at least.
                return JsonSerializer.Deserialize<ShellLinkFile.Link>(content) is { Target.Length: > 0 } ? Encoding.UTF8.GetBytes(content) : null;
        }
        {
            using var parsed = JsonDocument.Parse(content);
            var kind = parsed.RootElement.ValueKind;
            var fits = file.Shape switch
            {
                PlayerData.Shape.JsonObject => kind == JsonValueKind.Object,
                PlayerData.Shape.JsonArray => kind == JsonValueKind.Array,
                _ => kind is JsonValueKind.Object or JsonValueKind.Array,
            };
            if (!fits) return null;
            if (name == "settings.json" && JsonSerializer.Deserialize<AppSettings>(content, SettingsJson) is null) return null;
        }
        return Encoding.UTF8.GetBytes(content);
    }

    /// <summary>
    /// Puts a validated set of files into the data folder. Keeps a copy of what is there first; all or nothing. A backup made on
    /// another PC (<paramref name="madeOn"/> is not this PC's mark, or the backup carries none) brings none of that PC's undo records
    /// (<see cref="PlayerData.PlayerFile.OfThisMachine"/>): Off would write that PC's game files into this PC's games.
    /// </summary>
    internal static Outcome Restore(string dataDir, IReadOnlyList<Entry> files, string beforeFolder, DateTimeOffset now, string version,
        Here? here = null, MachineMark? madeOn = null, int missingPictures = 0)
    {
        lock (Gate) return RestoreLocked(dataDir, files, beforeFolder, now, version, here ?? ThisPc(dataDir), madeOn, missingPictures);
    }

    private static Outcome RestoreLocked(string dataDir, IReadOnlyList<Entry> files, string beforeFolder, DateTimeOffset now, string version,
        Here here, MachineMark? madeOn, int missingPictures)
    {
        if (madeOn?.Is(here.Machine) != true)
        {
            var left = files.Where(file => PlayerData.Of(file.Name)?.OfThisMachine == true).Select(file => file.Name).ToList();
            if (left.Count > 0) AppLog.Info($"Restore: the backup was made on another PC, so {left.Count} undo records of its games stay out.");
            files = files.Where(file => PlayerData.Of(file.Name)?.OfThisMachine != true).ToList();
        }
        IReadOnlyList<Entry> current;
        try { current = Gather(dataDir, here with { LucidSettings = null }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Outcome(false, "Exo could not read the data it has now, so nothing was restored.");
        }

        if (current.Count > 0)
        {
            try
            {
                var device = DeviceId(dataDir);
                Directory.CreateDirectory(beforeFolder);
                var before = Path.Combine(beforeFolder, $"{BeforePrefix}{now.ToUnixTimeMilliseconds():D13}-{device}.json");
                WriteAtomic(before, Serialize(current, now, version, MachineMark.Of(here.Machine), beforeFolder, device));
                File.SetLastWriteTimeUtc(before, now.UtcDateTime);
                Prune(beforeFolder, BeforePrefix, device, KeepBeforeRestore);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Outcome(false, "Exo could not keep a copy of the data it has now, so nothing was restored.");
            }
        }

        // The files as they are on disk now (not as a backup holds them), to merge with and to put back on a failure.
        var originals = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        try
        {
            foreach (var file in files)
            {
                var target = TargetOf(dataDir, file.Name);
                originals[file.Name] = File.Exists(target) ? File.ReadAllBytes(target) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Outcome(false, "Exo could not read the data it has now, so nothing was restored.");
        }
        var prepared = new List<Entry>();
        int shortcutsLeft = 0, tasksLeft = 0;
        try
        {
            foreach (var file in files)
            {
                switch (Mine(file, here))
                {
                    case Mined.Bytes { Value: var bytes }: prepared.Add(new Entry(file.Name, Prepare(file.Name, bytes, originals.GetValueOrDefault(file.Name)))); break;
                    case Mined.ShortcutLeftOut: shortcutsLeft++; break;
                    case Mined.WindowsTaskLeftOut: tasksLeft++; break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return new Outcome(false, "This backup could not be read, so nothing was restored.");
        }
        var staged = new List<(string Name, string Target, string Temp)>();
        try
        {
            foreach (var file in prepared)
            {
                var target = TargetOf(dataDir, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, file.Bytes);
                staged.Add((file.Name, target, temp));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiscardTemps(staged.Select(item => item.Temp));
            return new Outcome(false, "Exo could not write the restored files, so nothing was restored.");
        }

        var replaced = new List<(string Name, string Target)>();
        try
        {
            foreach (var item in staged)
            {
                File.Move(item.Temp, item.Target, overwrite: true);
                replaced.Add((item.Name, item.Target));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var done in replaced) PutBack(done.Target, originals.GetValueOrDefault(done.Name));
            DiscardTemps(staged.Select(item => item.Temp));
            return new Outcome(false, "Exo could not put every restored file in place, so the data was put back as it was.");
        }
        return new Outcome(true, RestoredWords(missingPictures, shortcutsLeft, tasksLeft), prepared.Count);
    }

    /// <summary>What a restore says: Restored, then one plain sentence for each kind of thing it left out.</summary>
    internal static string RestoredWords(int missingPictures, int shortcutsLeft, int tasksLeft)
    {
        var words = new List<string> { "Restored." };
        if (missingPictures > 0) words.Add(missingPictures == 1 ? "One cover or folder picture was not in the backups folder, so it shows the store's art." : $"{missingPictures} covers or folder pictures were not in the backups folder, so they show the store's art.");
        if (shortcutsLeft > 0) words.Add(shortcutsLeft == 1 ? "One start-up shortcut could not be made again, so it was left out." : $"{shortcutsLeft} start-up shortcuts could not be made again, so they were left out.");
        if (tasksLeft > 0) words.Add(tasksLeft == 1 ? "One start-up task ran as Windows itself, so it was left out." : $"{tasksLeft} start-up tasks ran as Windows itself, so they were left out.");
        return string.Join(' ', words);
    }

    /// <summary>What a file from a backup becomes on this PC: its bytes, or left out (and why).</summary>
    internal abstract record Mined
    {
        internal sealed record Bytes(byte[] Value) : Mined;
        /// <summary>A start-up shortcut the shell could not make again.</summary>
        internal sealed record ShortcutLeftOut : Mined;
        /// <summary>A start-up task that ran as one of Windows' own accounts: never handed to whoever restores.</summary>
        internal sealed record WindowsTaskLeftOut : Mined;
    }

    /// <summary>
    /// A file from a backup as this PC holds it: the restoring user's profile and name back in its text, and a start-up
    /// shortcut made again from what it does. A shortcut that cannot be made, and a task that ran as Windows itself, are left out.
    /// </summary>
    internal static Mined Mine(Entry entry, Here here)
    {
        if (PlayerData.Of(entry.Name) is not { IsBinary: false } file) return new Mined.Bytes(entry.Bytes);
        var raw = Encoding.UTF8.GetString(entry.Bytes);
        if (file.Shape == PlayerData.Shape.TaskXml && TaskUser.Matches(raw).Any(match => match.Groups[1].Value == "UserId" && IsWindowsAccount(System.Net.WebUtility.HtmlDecode(match.Groups[2].Value))))
            return new Mined.WindowsTaskLeftOut();
        var text = Personalize(raw, file.Shape, here);
        if (file.Shape != PlayerData.Shape.Shortcut) return new Mined.Bytes(Encoding.UTF8.GetBytes(text));
        var temp = Path.Combine(Path.GetTempPath(), "exo-restore-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            var link = JsonSerializer.Deserialize<ShellLinkFile.Link>(text) ?? throw new FormatException("Not a shortcut.");
            ShellLinkFile.Write(temp, link);
            return new Mined.Bytes(File.ReadAllBytes(temp));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException)
        {
            AppLog.Warn("Restore: a start-up shortcut could not be made again and was left out: " + ex.GetType().Name);
            return new Mined.ShortcutLeftOut();
        }
        finally { if (File.Exists(temp)) DeletionGate.DeleteFile(temp); }
    }
    /// <summary>
    /// What a restore writes for one file. Playtime and the Riot ledger merge with the data here, so nothing played after the
    /// backup is lost. Settings never carry "onboarding complete": the setup steps after a restore still run. Anything else is
    /// replaced as it is.
    /// </summary>
    internal static byte[] Prepare(string name, byte[] incoming, byte[]? live)
    {
        if (name == "settings.json")
        {
            var settings = JsonNode.Parse(Encoding.UTF8.GetString(incoming)) as JsonObject ?? throw new JsonException("settings.json is not an object");
            settings["onboardingComplete"] = false;
            // The install folder is this PC's: one that is not here is dropped, so the default falls back to a folder that exists.
            if (settings["defaultInstallRoot"] is JsonValue { } root && root.TryGetValue(out string? folder) && !LocalPath.ExistsLocally(folder)) settings.Remove("defaultInstallRoot");
            // This PC's own: a Lucid path, and a launch's elevation and working folder, are never restored from another PC.
            settings.Remove("lucidPath");
            foreach (var launch in (settings["launchOverrides"] as JsonObject)?.Select(pair => pair.Value).OfType<JsonObject>().ToList() ?? [])
            {
                launch.Remove("runAsAdmin");
                launch.Remove("workingDirectory");
                launch["extraArgs"] = ClampExtraArgs(launch["extraArgs"] is JsonValue { } args && args.TryGetValue(out string? raw) ? raw : null);
            }
            return Encode(settings);
        }
        if (name == "playtime.json") return PlaytimeFile(incoming, live);
        if (live is null) return incoming;
        if (name.StartsWith("riot-ledger/", StringComparison.Ordinal)) return MergeLedger(live, incoming);
        return incoming;
    }

    /// <summary>
    /// playtime.json as a restore writes it. A session the backup held open is dropped: this PC counts its own sessions, and that
    /// checkpoint would be counted again. The live file merges in as before.
    /// </summary>
    private static byte[] PlaytimeFile(byte[] incoming, byte[]? live)
    {
        var node = JsonNode.Parse(Encoding.UTF8.GetString(incoming)) as JsonObject ?? throw new JsonException("playtime.json is not an object");
        var hadSessions = node.Remove("activeSessions");
        if (live is null) return hadSessions ? Encode(node) : incoming;
        var merged = Merge(JsonNode.Parse(Encoding.UTF8.GetString(live)), node) as JsonObject ?? throw new JsonException("playtime.json is not an object");
        merged.Remove("activeSessions");
        return Encode(merged);
    }

    private static byte[] Encode(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Two JSON trees as one: objects by key (both sides' keys), numbers keep the larger, dates the later, arrays by their union.
    /// Where the two disagree in kind, the data here is kept.
    /// </summary>
    private static JsonNode? Merge(JsonNode? live, JsonNode? incoming)
    {
        if (live is JsonObject liveObject && incoming is JsonObject incomingObject)
        {
            var merged = new JsonObject();
            foreach (var key in liveObject.Select(pair => pair.Key).Union(incomingObject.Select(pair => pair.Key), StringComparer.Ordinal))
                merged[key] = Merge(liveObject[key], incomingObject[key]);
            return merged;
        }
        if (live is JsonArray liveArray && incoming is JsonArray incomingArray)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var merged = new JsonArray();
            foreach (var item in liveArray.Concat(incomingArray))
                if (seen.Add(item?.ToJsonString() ?? "null")) merged.Add(item?.DeepClone());
            return merged;
        }
        if (live is JsonValue liveValue && incoming is JsonValue incomingValue)
        {
            if (liveValue.TryGetValue(out decimal liveNumber) && incomingValue.TryGetValue(out decimal incomingNumber))
                return JsonValue.Create(Math.Max(liveNumber, incomingNumber));
            if (liveValue.TryGetValue(out string? liveText) && incomingValue.TryGetValue(out string? incomingText)
                && DateTimeOffset.TryParse(liveText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var liveAt)
                && DateTimeOffset.TryParse(incomingText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var incomingAt))
                return JsonValue.Create(liveAt >= incomingAt ? liveText : incomingText);
            return liveValue.DeepClone();
        }
        return (live ?? incoming)?.DeepClone();
    }

    /// <summary>The ledger's lines from both, once each: a line is its "key" (or its whole text when it has none). Here first.</summary>
    private static byte[] MergeLedger(byte[] live, byte[] incoming)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var source in new[] { live, incoming })
            foreach (var raw in Encoding.UTF8.GetString(source).Split('\n'))
            {
                var line = raw.Trim();
                if (!RiotLedger.IsWellFormedLine(line)) continue;
                if (seen.Add(LedgerKey(line))) lines.Add(line);
            }
        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }

    private static string LedgerKey(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
            ? "key:" + key.GetString()
            : "line:" + line;
    }

    private static void PutBack(string target, byte[]? original)
    {
        try
        {
            if (original is null) { if (File.Exists(target)) DeletionGate.DeleteFile(target); }
            else File.WriteAllBytes(target, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Restore: could not put a file back: " + ex.GetType().Name);
        }
    }

    private static void DiscardTemps(IEnumerable<string> temps)
    {
        foreach (var temp in temps)
            try { if (File.Exists(temp)) DeletionGate.DeleteFile(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string TargetOf(string dataDir, string name)
    {
        var target = Path.GetFullPath(Path.Combine(dataDir, name.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A restored file would leave the data folder.");
        return target;
    }

    /// <summary>The daily backup for a day (UTC), this PC's.</summary>
    internal static string DailyFileName(DateTimeOffset now, string device) => $"{DailyPrefix}{now.UtcDateTime:yyyyMMdd}-{device}.json";

    /// <summary>
    /// A backup document that fits <paramref name="cap"/> (what a restore reads), or null when even the smallest does not. A
    /// game's own files (Optimize's originals) go first, largest first, then pictures kept inside the document; each one left
    /// out is said in the log in plain words. A backup Latest or a restore would skip is never written.
    /// </summary>
    internal static (string? Text, int Dropped) Fitting(List<Entry> entries, DateTimeOffset now, string version, MachineMark machine, string? pictures, string? device, long cap)
    {
        // Pictures go to the folder only once the document fits: a backup that is not written leaves none behind.
        string Measure() => Serialize(entries, now, version, machine, pictures, device, writePictures: false);
        var dropped = 0;
        while (Encoding.UTF8.GetByteCount(Measure()) > cap)
        {
            var largest = entries
                .Where(entry => PlayerData.Of(entry.Name)?.Shape is PlayerData.Shape.Bytes || (pictures is null && PlayerData.Of(entry.Name)?.Shape is PlayerData.Shape.Picture))
                .OrderBy(entry => PlayerData.Of(entry.Name)!.Shape == PlayerData.Shape.Bytes ? 0 : 1)
                .ThenByDescending(entry => entry.Bytes.Length)
                .FirstOrDefault();
            if (largest is null) return (null, dropped);
            entries.Remove(largest);
            dropped++;
            AppLog.Warn($"Backup: the backup was too large, so {largest.Name} was left out of it.");
        }
        if (dropped > 0) AppLog.Warn($"Backup: {dropped} files were left out so the backup stays small enough to restore.");
        return (Serialize(entries, now, version, machine, pictures, device), dropped);
    }

    /// <summary>What today's backup came to, as Settings and the shell say it.</summary>
    internal enum Result { Written, Recent, Shrunk, NothingYet, TooLarge, Unreadable, DidNotCheckOut, CouldNotWrite }

    /// <summary>
    /// Each result as one table: whether backups have stopped, and the one plain sentence Settings shows (null: nothing to
    /// say). A stop names the reason and what Exo does next.
    /// </summary>
    internal static (bool Stopped, string? Sentence) Say(Result result) => result switch
    {
        Result.Written => (false, null),
        Result.Recent => (false, null),
        Result.Shrunk => (false, "The last backup left out the game files kept for Put back, so it stays small enough to restore."),
        Result.NothingYet => (false, null),
        Result.TooLarge => (true, "Backups stopped: your data is too large for one backup. Exo keeps the last good one and tries again each day."),
        Result.Unreadable => (true, "Backups stopped: a file Exo backs up could not be read. Exo keeps the last good one and tries again after your next game."),
        Result.DidNotCheckOut => (true, "Backups stopped: today's backup did not read back whole. Exo keeps the last good one and tries again after your next game."),
        Result.CouldNotWrite => (true, "Backups stopped: Exo could not write to your Documents folder. Exo keeps the last good one and tries again after your next game."),
        _ => (false, null),
    };

    /// <summary>One lock for every backup and restore on this PC: the automatic ones, Back up now, export and restore.</summary>
    internal static readonly object Gate = new();

    /// <summary>
    /// Writes today's backup unless one is there and <paramref name="replace"/> is false, then keeps the newest thirty of this
    /// PC's. Pictures are kept once in the folder, by the PC that wrote them and their hash, for all the daily copies.
    /// </summary>
    internal static Outcome BackUpDaily(string dataDir, string folder, DateTimeOffset now, string version, bool replace, Here? here = null, long cap = MaxBackupBytes)
    {
        lock (Gate)
        {
            var outcome = BackUpDailyLocked(dataDir, folder, now, version, replace, here ?? ThisPc(dataDir), cap);
            Remember(dataDir, outcome.Kind, now, outcome.Path);
            return outcome;
        }
    }

    private static Outcome BackUpDailyLocked(string dataDir, string folder, DateTimeOffset now, string version, bool replace, Here here, long cap)
    {
        var device = DeviceId(dataDir);
        var path = Path.Combine(folder, DailyFileName(now, device));
        // While the first-run restore offer is unanswered, a file already there that this install did not write (it kept no record
        // of that name and time) is never replaced, whatever the name says.
        if ((!replace || (!Answered(dataDir) && !WroteHere(dataDir, path))) && File.Exists(path)) return new Outcome(true, "Today's backup is already there.", Kind: Result.Recent);

        List<Entry> entries;
        try { entries = [.. Gather(dataDir, here)]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Outcome(false, "A file Exo backs up could not be read, so today's backup was not written. The last good one is kept.", Kind: Result.Unreadable);
        }
        if (entries.Count == 0) return new Outcome(false, "Nothing to back up yet.", Kind: Result.NothingYet);

        try
        {
            Directory.CreateDirectory(folder);
            var (text, dropped) = Fitting(entries, now, version, MachineMark.Of(here.Machine), folder, device, cap);
            if (text is null)
            {
                AppLog.Warn("Backup: today's backup would be too large to restore, so it was not written. The last good one is kept.");
                return new Outcome(false, "Today's backup would be too large to restore, so it was not written. The last good one is kept.", Kind: Result.TooLarge);
            }
            // The snapshot must read back whole before it replaces the last good one of the day.
            if (!Validate(text, folder).Ok) return new Outcome(false, "Today's backup did not check out, so it was not written. The last good one is kept.", Kind: Result.DidNotCheckOut);
            WriteAtomic(path, text);
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
            Prune(folder, DailyPrefix, device, KeepDaily);
            PrunePictures(folder, device);
            return new Outcome(true, "Backed up.", entries.Count, dropped > 0 ? Result.Shrunk : Result.Written, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Backup: today's backup was not written: " + ex.GetType().Name);
            return new Outcome(false, "Exo could not write today's backup.", Kind: Result.CouldNotWrite);
        }
    }

    private const string LastFile = "backup-last.json";

    /// <summary>The last backup's result on this PC, kept so Settings can say a stop after a restart.</summary>
    internal sealed record Last(Result Kind, DateTimeOffset At, DateTimeOffset? StoppedSince, string? File = null, DateTime? Stamp = null);

    /// <summary>
    /// What to say now, in the middle of a session, about a backup that has just run: the stop's sentence when it is a new stop
    /// (the one before was not a stop, or began at another time), null otherwise, so a stop is said once, when it happens.
    /// </summary>
    internal static (string Sentence, DateTimeOffset Since)? StopToAnnounce(Last? before, Last? after)
    {
        if (after is not { StoppedSince: { } since } || Say(after.Kind) is not { Stopped: true, Sentence: { } sentence }) return null;
        return before is { StoppedSince: { } was } && Say(before.Kind).Stopped && was == since ? null : (sentence, since);
    }

    /// <summary>An automatic backup stopped just now (the sentence, and since when): the shell says it as it happens.</summary>
    internal static event Action<string, DateTimeOffset>? StopAnnounced;

    /// <summary>Raises <see cref="StopAnnounced"/> when the backup that ran since <paramref name="before"/> began a new stop.</summary>
    internal static void AnnounceStop(Last? before, Last? after)
    {
        if (StopToAnnounce(before, after) is { } stop) StopAnnounced?.Invoke(stop.Sentence, stop.Since);
    }

    private static void Remember(string dataDir, Result kind, DateTimeOffset now, string? written)
    {
        if (kind == Result.Recent) return;
        try
        {
            var before = LastResult(dataDir);
            var stopped = Say(kind).Stopped ? before?.StoppedSince ?? now : (DateTimeOffset?)null;
            WriteAtomic(Path.Combine(dataDir, LastFile), JsonSerializer.Serialize(written is null ? new Last(kind, now, stopped, before?.File, before?.Stamp) : new Last(kind, now, stopped, Path.GetFileName(written), now.UtcDateTime)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Backup: the last result was not kept: " + ex.GetType().Name); }
    }

    /// <summary>Whether this install wrote that daily file (the name and time it recorded), so no other file is taken for its own.</summary>
    private static bool WroteHere(string dataDir, string path) =>
        LastResult(dataDir) is { File: { } name, Stamp: { } stamp } && name == Path.GetFileName(path) && File.GetLastWriteTimeUtc(path) == stamp;

    internal static Last? LastResult(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, LastFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<Last>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Deletes pictures this PC wrote (its id in the name) that no backup of Exo's own in the folder names (daily or before a restore; any other file there is ignored) and that are older than every
    /// backup kept there. Documents may be one OneDrive folder for several PCs, each file arriving on its own: another PC's
    /// pictures are never touched, and a backup this PC cannot read, or would skip as too large, means nothing is deleted.
    /// </summary>
    // Each install has its own random id, so a retired install's pictures (another id) are never cleaned up: bounded, and accepted.
    private static void PrunePictures(string folder, string device)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        var oldest = DateTime.MaxValue;
        // Only Exo's own backups count: a big export or a file OneDrive has not brought stops nothing, it is not read.
        foreach (var document in Directory.GetFiles(folder, "exo-launcher-*.json").Where(path => OwnBackupName.IsMatch(Path.GetFileName(path))))
        {
            try
            {
                if (!FitsBackupLimit(new FileInfo(document).Length)) return;
                oldest = new[] { oldest, File.GetLastWriteTimeUtc(document) }.Min();
                using var parsed = JsonDocument.Parse(File.ReadAllText(document));
                if (!parsed.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return;
                foreach (var file in files.EnumerateObject())
                    if (file.Value.ValueKind == JsonValueKind.Object && file.Value.TryGetProperty("picture", out var picture) && picture.GetString() is { } name) named.Add(name);
            }
            // A copy that cannot be read keeps every picture: nothing is deleted on a guess.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return; }
        }
        foreach (var picture in Directory.GetFiles(folder, PicturePrefix + device + "-*"))
        {
            var name = Path.GetFileName(picture);
            if (PictureName.Match(name) is not { Success: true } parts || parts.Groups[1].Value != device || named.Contains(name)) continue;
            if (File.GetLastWriteTimeUtc(picture) >= oldest) continue;
            try { DeletionGate.DeleteBackup(picture, folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Backup: an old backup picture could not be removed."); }
        }
    }

    /// <summary>A backup to a file the person chose (Export), whole in one file. Nothing else is written.</summary>
    internal static Outcome ExportTo(string dataDir, string path, DateTimeOffset now, string version, Here? here = null, long cap = MaxBackupBytes)
    {
        lock (Gate)
        {
            try
            {
                here ??= ThisPc(dataDir);
                List<Entry> entries = [.. Gather(dataDir, here)];
                if (entries.Count == 0) return new Outcome(false, "Nothing to export yet.");
                if (Fitting(entries, now, version, MachineMark.Of(here.Machine), null, null, cap).Text is not { } text)
                    return new Outcome(false, "Your data is too large for one backup file, so nothing was exported.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                WriteAtomic(path, text);
                return new Outcome(true, "Exported.", entries.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Outcome(false, "Exo could not read the data or write the file, so nothing was exported.");
            }
        }
    }
    /// <summary>When Exo writes today's backup again.</summary>
    internal enum Moment { Start, GameClosed, Exit }

    /// <summary>
    /// Whether to write today's backup now, as one table. At start: only when today has none. When a game closes: only when no
    /// other game still runs. At exit: only when the last one is older than <see cref="ExitFresh"/>, so closing Exo (an update,
    /// Windows signing out) is not slowed by a backup that would hold nothing new.
    /// </summary>
    internal static bool Due(Moment moment, bool todayWritten, bool gameRunning, TimeSpan sinceLast) => moment switch
    {
        Moment.Start => !todayWritten,
        Moment.GameClosed => !gameRunning,
        Moment.Exit => !todayWritten || sinceLast >= ExitFresh,
        _ => false,
    };

    internal static readonly TimeSpan ExitFresh = TimeSpan.FromMinutes(10);

    /// <summary>What closing Exo may spend on Lucid and the backup together, inside the 8 s the installer allows.</summary>
    internal static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(6);

    /// <summary>How long the backup at exit may still be waited for once <paramref name="spent"/> of the budget is gone (it is written whole or not at all).</summary>
    internal static TimeSpan BackupMayWait(TimeSpan spent) => spent >= ExitBudget ? TimeSpan.Zero : ExitBudget - spent;

    /// <summary>When this PC's today's backup was written, or null when there is none.</summary>
    internal static DateTimeOffset? WrittenToday(string dataDir, string folder, DateTimeOffset now)
    {
        var path = Path.Combine(folder, DailyFileName(now, DeviceId(dataDir)));
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }
    /// <summary>
    /// Whether this install has answered the restore offer (restored, or declined). It lives beside the data, never in a backup,
    /// so a restore cannot bring it back.
    /// </summary>
    internal static bool Answered(string dataDir) => File.Exists(Path.Combine(dataDir, AnsweredFile));

    internal static void MarkAnswered(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        WriteAtomic(Path.Combine(dataDir, AnsweredFile), "1");
    }

    /// <summary>The offer a first run may make: the newest daily backup, unless this install has already answered it.</summary>
    internal static Offer? OfferFor(string dataDir, string folder) => Answered(dataDir) ? null : Latest(folder, DeviceId(dataDir));

    /// <summary>Setup is done: the restore offer is closed for this install, so another PC's backup is never offered later.</summary>
    internal static void CloseOffer(string dataDir)
    {
        if (!Answered(dataDir)) MarkAnswered(dataDir);
    }

    /// <summary>The newest daily backup in the folder that reads back whole, skipping <paramref name="exceptDevice"/>'s own, or null.</summary>
    internal static Offer? Latest(string folder, string? exceptDevice = null)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (var path in Directory.GetFiles(folder, DailyPrefix + "*.json").OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            var match = DailyName.Match(Path.GetFileName(path));
            if (!match.Success || match.Groups[2].Value == exceptDevice) continue;
            try
            {
                if (!FitsBackupLimit(new FileInfo(path).Length)) continue;
                var read = Validate(File.ReadAllText(path), folder);
                if (!read.Ok) continue;
                var stamp = match.Groups[1].Value;
                return new Offer(path, $"{stamp[..4]}-{stamp[4..6]}-{stamp[6..8]}", read.Files.Count, match.Groups[2].Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>The daily backup for one day and PC, when it is in the folder: the file the page was shown as the offer.</summary>
    internal static string? DailyPath(string folder, string day, string device)
    {
        if (!DayShape.IsMatch(day) || !DeviceShape.IsMatch(device)) return null;
        var path = Path.Combine(folder, $"{DailyPrefix}{day.Replace("-", "", StringComparison.Ordinal)}-{device}.json");
        return File.Exists(path) ? path : null;
    }

    /// <summary>This PC's short id (eight hex), made once and kept in the data folder. It is not in a backup.</summary>
    internal static string DeviceId(string dataDir)
    {
        var path = Path.Combine(dataDir, DeviceFile);
        if (File.Exists(path))
        {
            var saved = File.ReadAllText(path).Trim();
            if (DeviceShape.IsMatch(saved)) return saved;
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        WriteAtomic(path, id);
        return id;
    }

    /// <summary>Deletes this PC's oldest files with the prefix, keeping the newest <paramref name="keep"/> (by name, which sorts by date).</summary>
    private static void Prune(string folder, string prefix, string device, int keep)
    {
        var mine = Directory.GetFiles(folder, prefix + "*.json")
            .Where(path => Path.GetFileName(path).EndsWith("-" + device + ".json", StringComparison.Ordinal))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Skip(keep);
        foreach (var path in mine)
            try { DeletionGate.DeleteBackup(path, folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Backup: an old backup could not be removed."); }
    }

    /// <summary>Writes the text to a temp file beside the path, then moves it over the path: a reader sees the old file or the new one.</summary>
    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
