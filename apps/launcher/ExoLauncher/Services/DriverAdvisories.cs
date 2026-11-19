using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Driver and firmware versions with a known problem, checked before Exo offers or installs any of them: the Drivers list
/// (<see cref="DriverCatalog.Apply"/>), the graphics driver row, the chipset row, the version watch, and again inside every
/// install path right before it installs (NVIDIA, AMD and Intel graphics, the chipset package, Intel's network driver,
/// Windows Update's drivers). An advised-against version is never offered ("621.01 skipped: unstable in a game"); one
/// that runs says so on its row with the way out (go back to X, update to at least Y).
///
/// The table is DriverAdvisories.json, built into Exo, and the same bytes signed as Exo's own feed at
/// https://updates.getexo.app/v1/advisories.json (cloud/updates, README › Driver advisories), so a newly found bad driver
/// is held back without an Exo release. The feed is read at most once a day with a plain GET (no query, no cookies,
/// nothing about this PC), checked against <see cref="PublicKey"/> (ECDSA P-256 over the payload's bytes) and used only
/// when its serial is above the built-in table's; one that doesn't verify, or is older than the one kept (a replay), is
/// ignored and the kept or built-in table stands.
/// </summary>
internal sealed class DriverAdvisories
{
    /// <summary>What to do on a PC running an affected version: nothing but never offer it, go back to To, or update to at least To.</summary>
    internal enum Advice { Skip, Back, Update }

    /// <summary>
    /// One known problem. Device: a pattern (case-insensitive) over the row's subject, "kind:device": "gpu:nvidia NVIDIA
    /// GeForce RTX 3070", "chipset:amd AMD Ryzen 5 5600X 6-Core Processor", "network:PCI\VEN_8086&amp;DEV_125C&amp;SUBSYS_…&amp;REV_06\…
    /// Intel(R) Ethernet Controller I226-V", "windows:&lt;device id&gt; &lt;device name&gt;", "bios:ASUS ROG STRIX B550-F GAMING
    /// WIFI II", "ssd:Samsung SSD 990 PRO 2TB"; a chip revision is part of the device id (REV_01). From: the first affected
    /// version (null: every earlier one); Before: the first one without the problem (null: not fixed yet). Why: what goes
    /// wrong, in a few words, for "621.01 skipped: …". Game: only while a game whose title has it is installed; MinBuild and
    /// MaxBuild: only on these Windows builds. Url and Date (yyyy-MM-dd or yyyy-MM): the source and the day it said so, or
    /// the day Exo read it when the source has no date.
    /// </summary>
    internal sealed record Advisory(string Id, string Device, string? From, string? Before, string Why, Advice Do, string? To,
        string Url, string Date, string? Game = null, int MinBuild = 0, int MaxBuild = int.MaxValue);

    /// <summary>A table and its serial (yyyyMMddNN): a feed is used only above the built-in serial.</summary>
    internal sealed record Table(long Serial, IReadOnlyList<Advisory> Advisories);

    /// <summary>What a match needs to know of this PC: the installed games' titles and Windows' build.</summary>
    internal sealed record Conditions(IReadOnlyCollection<string> Games, int Build);

    internal const string FeedUrl = "https://updates.getexo.app/v1/advisories.json";

    /// <summary>The feed's signing key (SEC1 P-256, base64url). Its private half stays off every repo (cloud/updates/advisories.mjs).</summary>
    internal const string PublicKey = "BKEDEIG8uzDtfLbZtLHJK1jINiWwANgrPMdehA96W77tNbQyzxHtv-lXFAHMMIBnOI9c76ix8wMt-TXCRCl2I3c";

    private const string Key = "feed";
    private const int MaxFeedBytes = 256 * 1024;
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MatchLimit = TimeSpan.FromMilliseconds(50);
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(30));

    /// <summary>The table built into this Exo (DriverAdvisories.json).</summary>
    internal static Table BuiltIn { get; } = Parse(ReadBuiltIn()) ?? throw new InvalidDataException("Exo's built-in driver advisories don't read.");

    internal static DriverAdvisories Shared { get; } = new(BuiltIn, Base64Url.DecodeFromChars(PublicKey), ThisPc,
        new LatestCache<string>("Exo's driver advisories", TimeSpan.FromDays(1), "driver-advisories.json"), FetchAsync);

    private readonly Table _builtIn;
    private readonly byte[] _key;
    private readonly Func<Conditions> _now;
    private readonly LatestCache<string>? _feed;
    private readonly Func<CancellationToken, Task<string?>>? _fetch;
    private Opened? _opened;

    private sealed record Opened(string Text, Table? Table);

    internal DriverAdvisories(Table builtIn, byte[] key, Func<Conditions> now, LatestCache<string>? feed = null, Func<CancellationToken, Task<string?>>? fetch = null)
    {
        _builtIn = builtIn;
        _key = key;
        _now = now;
        _feed = feed;
        _fetch = fetch;
    }

    /// <summary>The table in force: the kept feed's when it verifies and is newer than the built-in one, else the built-in one.</summary>
    internal Table Current
    {
        get
        {
            if (_feed?.Kept(Key) is not { } text) return _builtIn;
            var opened = _opened;
            if (opened is null || !ReferenceEquals(opened.Text, text)) _opened = opened = new Opened(text, Open(text, _key));
            return opened.Table is { } feed && feed.Serial > _builtIn.Serial ? feed : _builtIn;
        }
    }

    /// <summary>Reads the feed when the kept one is a day old; the first time, waits a few seconds for it, then goes on with the built-in table.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        if (_feed is null || _fetch is null) return;
        try { await _feed.GetAsync(Key, LookupAsync, ct).WaitAsync(FirstWait, ct).ConfigureAwait(false); }
        catch (TimeoutException) { /* the table in force stands until it lands */ }
    }

    /// <summary>The feed, verified and no older than the one kept; anything else fails the lookup, which keeps the last good one.</summary>
    private async Task<string?> LookupAsync(CancellationToken ct)
    {
        var text = await _fetch!(ct).ConfigureAwait(false) ?? throw new FormatException("Exo's driver advisories were empty.");
        var table = Accept(text, _feed!.Kept(Key), _key);
        if (table.Serial > _builtIn.Serial) AppLog.Info($"Drivers: Exo's advisories feed {table.Serial} ({table.Advisories.Count} entries) is in force.");
        return text;
    }

    /// <summary>A downloaded feed's table when it verifies and isn't older than <paramref name="kept"/> (a replay); else FormatException.</summary>
    internal static Table Accept(string text, string? kept, ReadOnlySpan<byte> key)
    {
        var table = Open(text, key) ?? throw new FormatException("Exo's driver advisories didn't verify.");
        if (kept is not null && Open(kept, key) is { } before && table.Serial < before.Serial)
            throw new FormatException($"Exo's driver advisories went back from {before.Serial} to {table.Serial}.");
        return table;
    }

    /// <summary>The advisory against <paramref name="version"/> of the device <paramref name="subject"/> names, on this PC now, or null.</summary>
    internal Advisory? Against(string subject, string? version, Conditions? now = null) =>
        version is null ? null : Find(Current.Advisories, subject, version, now ?? _now());

    /// <summary>What an install path answers instead of installing an advised-against version, or null to go ahead.</summary>
    internal string? Refuse(string subject, string? version) =>
        Against(subject, version) is { } advisory ? Skipped(version!, advisory) + "." : null;

    // ---- Pure ----

    /// <summary>The row's words for an offered version left out: "621.01 skipped: unstable in a game".</summary>
    internal static string Skipped(string version, Advisory advisory) => $"{version} skipped: {advisory.Why}";

    /// <summary>The graphics card's subject: "gpu:nvidia NVIDIA GeForce RTX 3070".</summary>
    internal static string Gpu(string vendor, string? card) => $"gpu:{vendor} {card}".TrimEnd();

    /// <summary>The chipset package's subject: "chipset:amd AMD Ryzen 5 5600X 6-Core Processor".</summary>
    internal static string Chipset(string vendor, string? cpu) => $"chipset:{vendor} {cpu?.Trim()}".TrimEnd();

    /// <summary>The first advisory in <paramref name="table"/> against <paramref name="version"/> of <paramref name="subject"/>, or null.</summary>
    internal static Advisory? Find(IReadOnlyList<Advisory> table, string subject, string version, Conditions now)
    {
        var colon = subject.IndexOf(':', StringComparison.Ordinal);
        var kind = colon > 0 ? subject[..colon] : subject;
        return table.FirstOrDefault(a => now.Build >= a.MinBuild && now.Build <= a.MaxBuild
            && (a.Game is null || now.Games.Any(g => g.Contains(a.Game, StringComparison.OrdinalIgnoreCase)))
            && Matches(a.Device, subject)
            && (a.From is null || Compare(kind, version, a.From) >= 0)
            && (a.Before is null || Compare(kind, version, a.Before) < 0)
            && (a.From is not null || a.Before is not null || Compare(kind, version, version) is not null));
    }

    private static bool Matches(string pattern, string subject)
    {
        try { return Regex.IsMatch(subject, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchLimit); }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { return false; }
    }

    /// <summary>
    /// Two versions of one kind, or null when they don't compare (a firmware of another line, text that isn't a version).
    /// SSD firmware by its line (<see cref="Firmware"/>); an ASUS BIOS by its number; everything else by <see cref="VersionNumber"/>.
    /// </summary>
    internal static int? Compare(string kind, string a, string b) =>
        kind == "ssd" ? Firmware(a, b)
        : kind == "bios" && BiosUpdate.AsusNumber(a) is { } x && BiosUpdate.AsusNumber(b) is { } y ? x.CompareTo(y)
        : VersionNumber.Parse(a) is null || VersionNumber.Parse(b) is null ? null
        : VersionNumber.Compare(a, b);

    /// <summary>
    /// Two firmware versions of one line: Samsung numbers a line by its first character (0B2QJXD7 before 1B2QJXD7), WD by six
    /// digits whose first three name the line (731120WD before 731130WD). Another line or an unknown scheme: null.
    /// </summary>
    internal static int? Firmware(string a, string b)
    {
        a = a.Trim().ToUpperInvariant();
        b = b.Trim().ToUpperInvariant();
        if (a.Length != b.Length || a.Length < 2) return null;
        if (b.Length == 8 && b.EndsWith("WD", StringComparison.Ordinal))
            return a.EndsWith("WD", StringComparison.Ordinal) && a[..3] == b[..3]
                && int.TryParse(a[..6], NumberStyles.None, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(b[..6], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? x.CompareTo(y) : null;
        return a[1..] == b[1..] && char.IsAsciiDigit(a[0]) && char.IsAsciiDigit(b[0]) ? a[0].CompareTo(b[0]) : null;
    }

    /// <summary>The feed's table when its signature verifies against <paramref name="key"/>, else null. The file is { payload: base64, signature: base64 }.</summary>
    internal static Table? Open(string envelope, ReadOnlySpan<byte> key)
    {
        byte[] payload, signature;
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || Text(doc.RootElement, "payload") is not { } p || Text(doc.RootElement, "signature") is not { } s) return null;
            payload = Convert.FromBase64String(p);
            signature = Convert.FromBase64String(s);
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
        return P256.Verify(key, payload, signature) ? Parse(payload) : null;
    }

    /// <summary>
    /// A table's JSON ({ serial, advisories: [...] }, DriverAdvisories.json's shape), or null when it isn't one. An entry
    /// without an id, a pattern that compiles, a reason, an https source and its date, or a version to go to for back and
    /// update, is left out.
    /// </summary>
    internal static Table? Parse(byte[] json)
    {
        try
        {
            ReadOnlyMemory<byte> text = json.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? json.AsMemory(3) : json;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("serial", out var serial) || !serial.TryGetInt64(out var number)
                || !root.TryGetProperty("advisories", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var advisories = new List<Advisory>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object && Entry(e) is { } advisory) advisories.Add(advisory);
                else AppLog.Warn("Drivers: an advisory was left out: " + (e.ValueKind == JsonValueKind.Object ? Text(e, "id") : e.ValueKind.ToString()));
            }
            return new Table(number, advisories);
        }
        catch (JsonException) { return null; }
    }

    private static Advisory? Entry(JsonElement e)
    {
        if (Text(e, "id") is not { } id || Text(e, "device") is not { } device || Text(e, "why") is not { } why
            || Text(e, "url") is not { } url || !url.StartsWith("https://", StringComparison.Ordinal) || Text(e, "date") is not { } date
            || !DateOnly.TryParseExact(date.Length == 7 ? date + "-01" : date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
        Advice? advice = Text(e, "do") switch { "skip" => Advice.Skip, "back" => Advice.Back, "update" => Advice.Update, _ => null };
        var to = Text(e, "to");
        if (advice is not { } doing || (doing != Advice.Skip && to is null)) return null;
        try { _ = new Regex(device, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchLimit); }
        catch (ArgumentException) { return null; }
        return new Advisory(id, device, Text(e, "from"), Text(e, "before"), why, doing, to, url, date, Text(e, "game"),
            Int(e, "minBuild") ?? 0, Int(e, "maxBuild") ?? int.MaxValue);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    // ---- This PC ----

    private static byte[] ReadBuiltIn()
    {
        using var stream = typeof(DriverAdvisories).Assembly.GetManifestResourceStream("DriverAdvisories.json")
            ?? throw new InvalidDataException("Exo's built-in driver advisories are missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static Conditions ThisPc() =>
        new([.. App.Services.Library.PeekCachedLibrary().Where(g => g.Installed).Select(g => g.Title)], Environment.OSVersion.Version.Build);

    /// <summary>The feed, as a plain GET with nothing about this PC; at most <see cref="MaxFeedBytes"/>.</summary>
    private static async Task<string?> FetchAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(FeedUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new FormatException("Exo's driver advisories are too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxFeedBytes + 1];
        var read = 0;
        for (int n; read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false)) > 0;) read += n;
        if (read > MaxFeedBytes) throw new FormatException("Exo's driver advisories are too large.");
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
