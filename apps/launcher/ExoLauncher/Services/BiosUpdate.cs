using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The BIOS for the Drivers list: this PC's board and BIOS, and the newest release BIOS its maker lists, with the maker's
/// BIOS page for the board. Exo never flashes firmware; it only says and links. ASUS only (desktop boards and laptops):
/// its support API answers by model name. MSI's, Gigabyte's and ASRock's lists sit behind bot checks that turn away
/// anything but a browser (403s and a script challenge), so their boards, and every other maker's, have the version and
/// date alone. The request carries the model and nothing else. Read at most once a day, kept on disk; a maker that can't
/// be read leaves Latest null.
/// </summary>
internal static partial class BiosUpdate
{
    /// <summary>
    /// What the Drivers list shows for the BIOS. Board: "ASUS ROG STRIX B550-F GAMING WIFI II". Version and Date
    /// (yyyy-MM-dd): what this PC runs. Latest, LatestDate and Page: the maker's newest release BIOS and its BIOS page for
    /// the board, all null when the maker isn't read. UpToDate: null while Latest is.
    /// </summary>
    internal sealed record Bios(string? Board, string Version, string? Date, string? Latest, string? LatestDate, bool? UpToDate, string? Page);

    /// <summary>The newest release BIOS the maker lists for a board: its version, release date (yyyy-MM-dd) and the board's BIOS page.</summary>
    internal sealed record Newest(string Version, string? Date, string Page);

    /// <summary>The first look waits this long; past it the BIOS shows without the newest, and the look goes on for the next showing.</summary>
    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(5);

    private static readonly LatestCache<Newest> Lists = new("ASUS's BIOS list", TimeSpan.FromDays(1), "bios-newest.json");
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(30));

    /// <summary>This PC's BIOS against its maker's newest; null when Windows doesn't say which BIOS it runs.</summary>
    internal static async Task<Bios?> ReadAsync(CancellationToken ct)
    {
        var firmware = await Task.Run(() => PcAbout.Bios, ct).ConfigureAwait(false);
        if (firmware.Version is null) return null;
        Newest? newest = null;
        try { newest = await NewestAsync(firmware.Maker, firmware.Product, ct).WaitAsync(FirstLook, ct).ConfigureAwait(false); }
        catch (TimeoutException) { /* still reading: kept for the next showing */ }
        return Compare(PcAbout.Named(firmware.Maker, firmware.Product), firmware.Version, firmware.Date, newest);
    }

    /// <summary>The newest BIOS for a board, or null: not an ASUS board, no model, or ASUS not read yet.</summary>
    internal static Task<Newest?> NewestAsync(string? maker, string? product, CancellationToken ct) =>
        PcAbout.Maker(maker) == "ASUS" && PcAbout.Real(product)
            ? Lists.GetAsync(product.Trim(), token => AsusAsync(product.Trim(), token), ct)
            : Task.FromResult<Newest?>(null);

    /// <summary>
    /// The BIOS against the newest: up to date when its number is at least the newest's (a beta newer than the newest
    /// release counts). Without a newest, or when either version isn't a number ASUS gives its BIOSes, only what runs.
    /// </summary>
    internal static Bios Compare(string? board, string version, string? date, Newest? newest) =>
        newest is not null && AsusNumber(version) is { } have && AsusNumber(newest.Version) is { } latest
            ? new Bios(board, version, date, newest.Version, newest.Date, have >= latest, newest.Page)
            : new Bios(board, version, date, null, null, null, null);

    /// <summary>ASUS's BIOS page for a model, the one its support site opens by name.</summary>
    internal static string AsusPage(string model) => $"https://www.asus.com/supportonly/{Uri.EscapeDataString(model)}/helpdesk_bios/";

    internal static string? SupportPage(string? maker, string? product)
    {
        var brand = PcAbout.Maker(maker);
        var model = PcAbout.Real(product) ? product.Trim() : null;
        return brand switch
        {
            "ASUS" => model is not null ? AsusPage(model) : "https://www.asus.com/support/download-center/",
            "MSI" => model is not null ? $"https://www.msi.com/search/{Uri.EscapeDataString(model)}" : "https://www.msi.com/support/download",
            "Gigabyte" => model is not null ? $"https://www.gigabyte.com/Search?kw={Uri.EscapeDataString(model)}" : "https://www.gigabyte.com/Support/Consumer/Download",
            "ASRock" => model is not null ? $"https://www.asrock.com/support/index.asp?Model={Uri.EscapeDataString(model)}" : "https://www.asrock.com/support/index.asp",
            "Biostar" => "https://www.biostar.com.tw/app/en/support/download.php",
            "NZXT" => "https://support.nzxt.com/hc/en-us",
            "EVGA" => "https://www.evga.com/support/download/",
            "Supermicro" => "https://www.supermicro.com/en/support/resources/bios_ipmi.php",
            "Dell" or "Alienware" => "https://www.dell.com/support/home/en-us/products",
            "Lenovo" => "https://pcsupport.lenovo.com/",
            "HP" => "https://support.hp.com/us-en/drivers",
            "Acer" => "https://www.acer.com/us-en/support/drivers-and-manuals",
            "Framework" => "https://knowledgebase.frame.work/bios-and-drivers-downloads-rJ3PaCexh",
            "Intel" => "https://www.intel.com/content/www/us/en/download-center/home.html",
            _ => null,
        };
    }

    private static async Task<Newest?> AsusAsync(string model, CancellationToken ct)
    {
        var url = $"https://www.asus.com/support/api/product.asmx/GetPDBIOS?website=global&model={Uri.EscapeDataString(model)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        Lists.SlowDown(response);
        response.EnsureSuccessStatusCode();
        return ParseAsus(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), model);
    }

    /// <summary>
    /// ASUS's GetPDBIOS answer: its "BIOS" group's files, each a version ("3645", a laptop's "319"), a release date
    /// ("2026/09/14") and IsRelease ("1"). A file ASUS hasn't released ("0") or calls a beta is never the update. The
    /// newest is the highest number. Null when ASUS doesn't know the model (Status FAIL) or lists no released BIOS.
    /// </summary>
    internal static Newest? ParseAsus(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("Obj", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        (int Number, string Version, string? Date)? best = null;
        foreach (var group in groups.EnumerateArray())
        {
            if (Str(group, "Name") != "BIOS" || !group.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (Str(file, "IsRelease") != "1" || Beta(Str(file, "Title")) || Beta(Str(file, "Description"))) continue;
                if (Str(file, "Version") is not { } version || AsusNumber(version) is not { } number || number <= best?.Number) continue;
                best = (number, version.Trim(), ReleaseDate(Str(file, "ReleaseDate")));
            }
        }
        return best is { } newest ? new Newest(newest.Version, newest.Date, AsusPage(model)) : null;
    }

    /// <summary>
    /// An ASUS BIOS version as its number: a board's "3636", a laptop's "GA402RJ.319" (the registry) or "319" (ASUS's list).
    /// Null for anything else.
    /// </summary>
    internal static int? AsusNumber(string? version) =>
        version is not null && AsusVersion().Match(version.Trim()) is { Success: true } match
        && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool Beta(string? text) => text?.Contains("beta", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>ASUS's "2026/09/14" as yyyy-MM-dd, or null.</summary>
    private static string? ReleaseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The whole version, or a laptop's model (GA402RJ, UX425EA), a dot and the number; never another maker's "P3.20" or "1.A0".
    [GeneratedRegex(@"^(?:[A-Z0-9_-]{4,}\.)?(\d{2,5})$", RegexOptions.IgnoreCase)]
    private static partial Regex AsusVersion();
}
