using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>Reading AMD's and Intel's driver pages: which page is this PC's, and the driver it offers; and Intel's chipset package.</summary>
internal static partial class GpuDriverUpdate
{
    /// <summary>AMD's driver selector: its product list (every card and processor, with its driver page) rides in the page.</summary>
    internal const string AmdSelector = "https://www.amd.com/en/support/download/drivers.html";
    private const string AmdPages = "https://www.amd.com/en/support/downloads/drivers.html/";

    /// <summary>Intel's driver pages, newest family first: Arc and Core Ultra, 11th–14th Gen (and their Atom, Pentium and Celeron), 7th–10th Gen.</summary>
    internal static readonly string[] IntelPages =
    [
        "https://www.intel.com/content/www/us/en/download/785597/intel-arc-graphics-windows.html",
        "https://www.intel.com/content/www/us/en/download/864990/intel-11th-14th-gen-processor-graphics-windows.html",
        "https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html",
    ];

    /// <summary>
    /// The selector, then the card's (or processor's) page, plus AMD's newest optional/hotfix release notes when newer:
    /// two requests through <paramref name="get"/>, then optional release-note probes through <paramref name="tryGet"/>.
    /// </summary>
    internal static async Task<Latest?> AmdLatestAsync(string card, string? cpu, Func<string, Task<string>> get, Func<string, Task<string?>>? tryGet = null)
    {
        var page = AmdProductPage(AmdProducts(await get(AmdSelector).ConfigureAwait(false)), card, cpu);
        if (page is null) return null;
        var found = AmdDriver(await get(page).ConfigureAwait(false));
        if (tryGet is null || found is null || found.Url.Contains("vega-polaris", StringComparison.OrdinalIgnoreCase)) return found;
        var candidates = await Task.WhenAll(AmdReleaseNoteUrls(found.Version).Select(async url =>
        {
            try
            {
                var html = await tryGet(url).ConfigureAwait(false);
                return html is null ? null : AmdReleaseNote(html, card, cpu, found.Url);
            }
            catch (HttpRequestException) { return null; }
        })).ConfigureAwait(false);
        foreach (var candidate in candidates)
            if (candidate is not null && VersionNumber.Compare(candidate.Version, found.Version) > 0) found = candidate;
        return found;
    }

    /// <summary>
    /// Intel's page for this PC, read through <paramref name="get"/> one at a time until one fits: every Arc (a card, or
    /// built into a Core Ultra) is on Intel's Arc driver, whose list names its cards with their memory ("Arc™ A770 Graphics
    /// (8GB)") and the older pages list none; any other GPU is on the first page that names the card or the processor. When
    /// that page's version table lists a newer Non-WHQL/hotfix build than the headline download, follow that row's page.
    /// </summary>
    internal static async Task<Latest?> IntelLatestAsync(string card, string? cpu, Func<string, Task<string>> get)
    {
        string? matchedHtml = null, matchedPage = null;
        if (card.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            matchedPage = IntelPages[0];
            matchedHtml = await get(matchedPage).ConfigureAwait(false);
        }
        else
        {
            foreach (var page in IntelPages)
            {
                var html = await get(page).ConfigureAwait(false);
                if (IntelCovers(html, card, cpu)) { matchedHtml = html; matchedPage = page; break; }
            }
        }
        if (matchedHtml is null || matchedPage is null) return null;
        var best = IntelDriver(matchedHtml, matchedPage);
        if (best is null) return null;
        var newerRow = IntelListedVersions(matchedHtml)
            .Where(row => VersionNumber.Newer(row.Version, best.Version))
            .OrderByDescending(row => row.Version, VersionNumber.Order)
            .FirstOrDefault();
        if (newerRow.Url is { } rowUrl)
        {
            try
            {
                if (IntelDriver(await get(rowUrl).ConfigureAwait(false), rowUrl) is { } listed
                    && VersionNumber.Compare(listed.Version, best.Version) > 0)
                    best = listed;
            }
            catch (HttpRequestException) { }
        }
        return best;
    }

    /// <summary>Every product AMD's selector lists, its title and its driver page, from the JSON the page carries in an attribute.</summary>
    internal static IReadOnlyList<(string Title, string Url)> AmdProducts(string html)
    {
        var at = html.IndexOf("data-component=\"coveo-browse-documentation\"", StringComparison.Ordinal);
        var json = at < 0 ? Match.Empty : DataJson().Match(html, at);
        if (!json.Success) return [];
        using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(json.Groups[1].Value));
        if (!doc.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) return [];
        var list = new List<(string, string)>();
        foreach (var product in products.EnumerateArray())
            if (product.ValueKind == JsonValueKind.Object
                && product.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                && product.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                list.Add((title.GetString()!, url.GetString()!));
        return list;
    }

    /// <summary>
    /// The card's driver page among AMD's products: the graphics product named as the card is, else (an integrated GPU,
    /// which Windows names "AMD Radeon(TM) Graphics" or "AMD Radeon 780M Graphics") its processor's page. Only AMD's own pages.
    /// </summary>
    internal static string? AmdProductPage(IReadOnlyList<(string Title, string Url)> products, string card, string? cpu)
    {
        string? Find(string kind, string? name) => Named(name) is not { } plain ? null
            : products.FirstOrDefault(product => product.Url.StartsWith(AmdPages + kind + "/", StringComparison.OrdinalIgnoreCase) && Plain(product.Title) == plain).Url;
        return Find("graphics", card) ?? Find("processors", cpu is null ? null : AmdCpuTail().Replace(cpu, ""));
    }

    /// <summary>
    /// The newest AMD Software: Adrenalin Edition a card's or processor's page links in its Windows 11 section, or null.
    /// AMD labels each Recommended (WHQL), Optional or Hotfix. A PRO card's page also lists AMD Software: PRO Edition, which is left out.
    /// </summary>
    internal static Latest? AmdDriver(string html)
    {
        if (AmdSection(html, "Windows 11") is not { } section) return null;
        Latest? newest = null;
        foreach (Match article in AmdArticle().Matches(section))
        {
            var body = article.Groups[1].Value;
            var title = WebUtility.HtmlDecode(AmdTitle().Match(body).Groups[1].Value);
            if (!title.Contains("Adrenalin", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match field in AmdField().Matches(body)) fields.TryAdd(field.Groups[1].Value.Trim(), WebUtility.HtmlDecode(field.Groups[2].Value).Trim());
            var revision = fields.GetValueOrDefault("Revision Number") ?? "";
            var version = AmdVersion().Match(revision);
            var link = AmdLink().Match(body);
            if (!version.Success || !link.Success) continue;
            var date = DateTime.TryParseExact(fields.GetValueOrDefault("Release Date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateTime?)null;
            var kind = AmdReleaseKind(revision + " " + title);
            var found = new Latest("amd", version.Groups[1].Value, link.Groups[1].Value, Bytes(fields.GetValueOrDefault("File Size")), Day(date), kind);
            // The newest wins; between two of one version, AMD's recommended one.
            var cmp = newest is null ? 1 : VersionNumber.Compare(found.Version, newest.Version);
            if (cmp > 0 || (cmp == 0 && found.Kind == "recommended"))
                newest = found;
        }
        return newest;
    }

    private static string AmdReleaseKind(string label) =>
        label.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ? "hotfix"
        : label.Contains("Optional", StringComparison.OrdinalIgnoreCase) || label.Contains("Preview", StringComparison.OrdinalIgnoreCase) || label.Contains("Beta", StringComparison.OrdinalIgnoreCase) ? "optional"
        : "recommended";

    /// <summary>
    /// Candidate AMD release-note URLs for optional/hotfix releases newer than <paramref name="version"/> (next month's .1/.2,
    /// same month's next two minor drops, and a 4-part point hotfix).
    /// </summary>
    internal static IReadOnlyList<string> AmdReleaseNoteUrls(string version)
    {
        var raw = version.Split('.');
        if (raw.Length is < 3 or > 4) return [];
        var nums = new int[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            if (!int.TryParse(raw[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return [];
        var (yy, m, p) = (nums[0], nums[1], nums[2]);
        var q = nums.Length > 3 ? nums[3] : 0;
        var (nextYy, nextM) = m == 12 ? (yy + 1, 1) : (yy, m + 1);
        const string Root = "https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-";
        return
        [
            $"{Root}{nextYy}-{nextM}-2.html",
            $"{Root}{nextYy}-{nextM}-1.html",
            $"{Root}{yy}-{m}-{p + 2}.html",
            $"{Root}{yy}-{m}-{p + 1}.html",
            $"{Root}{yy}-{m}-{p}-{(q > 0 ? q + 1 : 1)}.html",
        ];
    }

    /// <summary>
    /// An Adrenalin optional/hotfix driver read from AMD's release-notes article for <paramref name="card"/>/<paramref name="cpu"/>,
    /// failing closed unless the article's title, Radeon Product Compatibility table and Windows 11 full package match.
    /// </summary>
    internal static Latest? AmdReleaseNote(string html, string card, string? cpu, string? preferredUrl = null)
    {
        var titleMatch = AmdHtmlTitle().Match(html);
        if (!titleMatch.Success) return null;
        var title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
        if (!title.Contains("Adrenalin", StringComparison.OrdinalIgnoreCase)
            || title.Contains("PRO Edition", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Linux", StringComparison.OrdinalIgnoreCase))
            return null;
        var version = AmdVersion().Match(title);
        if (!version.Success || !AmdReleaseNoteCovers(html, card, cpu)) return null;
        var links = AmdLink().Matches(html).Select(m => m.Groups[1].Value)
            .Where(u => !u.Contains("minimalsetup", StringComparison.OrdinalIgnoreCase)
                && !u.Contains("_web.exe", StringComparison.OrdinalIgnoreCase)
                && (u.Contains("win11", StringComparison.OrdinalIgnoreCase) || u.Contains("win10-win11", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (links.Count == 0) return null;
        var suffix = preferredUrl is null ? Match.Empty : AmdPackageSuffix().Match(preferredUrl);
        var url = (suffix.Success ? links.FirstOrDefault(u => u.EndsWith(suffix.Value, StringComparison.OrdinalIgnoreCase)) : null)
            ?? links.FirstOrDefault(u => u.EndsWith("-c.exe", StringComparison.OrdinalIgnoreCase))
            ?? links[0];
        var metaDate = AmdMetaDate().Match(html);
        var day = metaDate.Success && DateTime.TryParseExact(metaDate.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTime?)null;
        return new Latest("amd", version.Groups[1].Value, url, 0, Day(day), AmdReleaseKind(title));
    }

    /// <summary>Whether an AMD release-notes page's Radeon Product Compatibility section lists the user's card or Ryzen/Athlon iGPU.</summary>
    internal static bool AmdReleaseNoteCovers(string html, string card, string? cpu)
    {
        var start = html.IndexOf("id=\"Compatibility\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = html.IndexOf("Radeon Product Compatibility", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        var end = html.IndexOf("Compatible Operating Systems", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = html.IndexOf("Packaged Contents", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0 || end <= start) return false;
        var compat = WebUtility.HtmlDecode(html[start..end]);
        var discrete = AmdDiscreteModel().Match(card);
        if (discrete.Success)
        {
            var token = discrete.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value;
            return !string.IsNullOrEmpty(token) && Regex.IsMatch(compat, $@"(?<![\w-]){Regex.Escape(token)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (string.IsNullOrWhiteSpace(cpu)) return false;
        if (cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)) return compat.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        if (cpu.Contains("Athlon", StringComparison.OrdinalIgnoreCase)) return compat.Contains("Athlon", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>The page's part under the accordion header naming the OS ("Windows 11 - 64-Bit Edition"), or null.</summary>
    private static string? AmdSection(string html, string os)
    {
        foreach (var part in html.Split("class=\"accordion-item\"", StringSplitOptions.None).Skip(1))
            if (AccordionHeader().Match(part) is { Success: true } header && header.Groups[1].Value.Contains(os, StringComparison.OrdinalIgnoreCase)) return part;
        return null;
    }

    /// <summary>Whether an Intel driver page lists the card ("Intel® Arc™ B580 Graphics") or the processor ("Intel® Core™ i7-12700K Processor (25M Cache, …)").</summary>
    internal static bool IntelCovers(string html, string card, string? cpu)
    {
        var products = IntelProducts(html);
        if (Named(card) is { } plain && products.Any(product => Plain(product) == plain)) return true;
        if (IntelCpuModel(cpu) is not { } model) return false;
        var exact = new Regex($@"(?<![\w-]){Regex.Escape(model)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return products.Any(product => exact.IsMatch(product));
    }

    /// <summary>The products an Intel driver page says it is for.</summary>
    internal static IReadOnlyList<string> IntelProducts(string html) =>
        IntelProduct().Matches(html).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value).Trim()).Where(name => name.Length > 0).Distinct().ToList();

    /// <summary>
    /// The processor's model as Intel's product lists write it ("i7-12700K", "155H", "N100"): the last part of its name with a
    /// number in it, before " CPU @ 2.90GHz". Null without one.
    /// </summary>
    internal static string? IntelCpuModel(string? cpu)
    {
        if (cpu is null) return null;
        var name = IntelCpuTail().Replace(Marks().Replace(cpu, " "), "");
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(part => part.Length >= 3 && part.Any(char.IsAsciiDigit) && !Ordinal().IsMatch(part));
    }

    /// <summary>
    /// The driver an Intel page offers: the version it installs, its gfx_win installer (whose name carries the version's last
    /// two parts: gfx_win_101.9150.exe is 32.0.101.9150), the size and SHA-512 listed beside it, and the page's date.
    /// Non-WHQL / Beta / Hotfix pages classify as Kind "hotfix"; WHQL pages stay "intel".
    /// </summary>
    internal static Latest? IntelDriver(string html, string page)
    {
        var link = IntelLink().Match(html);
        var version = IntelVersion().Match(html) is { Success: true } hv ? hv.Groups[1].Value
            : IntelDownloadVersion().Match(html) is { Success: true } mv ? mv.Groups[1].Value : null;
        if (!link.Success || version is null || !version.EndsWith("." + link.Groups[1].Value, StringComparison.Ordinal)) return null;
        var details = html[link.Index..];
        var size = IntelSize().Match(details);
        var sha = IntelSha().Match(details);
        var date = IntelDate().Match(html);
        var day = date.Success && DateTime.TryParseExact(date.Groups[1].Value, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTime?)null;
        var kind = IntelNonWhql(html, version) ? "hotfix" : "intel";
        return new Latest("intel", version, link.Value, Bytes(size.Success ? size.Groups[1].Value : null), Day(day), kind,
            sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null, page);
    }

    /// <summary>Whether an Intel download page is for a Non-WHQL / Hotfix / Beta build (ignoring Intel's generic FAQ disclaimer text).</summary>
    internal static bool IntelNonWhql(string html, string version) =>
        IntelNonWhqlNotes().IsMatch(html)
        || Regex.IsMatch(html, $@"(?:Graphics Driver|Version)\s+{Regex.Escape(version)}\s+(?:Non-WHQL|Hotfix|Beta)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every version row listed in Intel's previous/available versions table or selector on a download page, with its absolute Intel URL.</summary>
    internal static IReadOnlyList<(string Version, string Url)> IntelListedVersions(string html)
    {
        var list = new List<(string Version, string Url)>();
        static string Abs(string href) => href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? href : "https://www.intel.com" + href;
        foreach (var row in html.Split("<tr", StringSplitOptions.None).Skip(1))
        {
            var end = row.IndexOf("</tr>", StringComparison.OrdinalIgnoreCase);
            var cell = end > 0 ? row[..end] : row;
            var ver = IntelTableVersion().Match(cell);
            var link = IntelTableLink().Match(cell);
            if (ver.Success && link.Success) list.Add((ver.Groups[1].Value, Abs(link.Groups[1].Value)));
        }
        foreach (Match option in IntelOptionVersion().Matches(html))
            list.Add((option.Groups[2].Value, Abs(option.Groups[1].Value)));
        return list;
    }

    /// <summary>
    /// The package Intel's Chipset Device Software page offers (<see cref="ChipsetUpdate"/>): the version the page is for
    /// (its DownloadVersion), its SetupChipset.exe on Intel's download server (not the release notes or a zip of the INF
    /// files), and the SHA-256 listed in that download's own details. Null when the page names no installer or version.
    /// </summary>
    internal static ChipsetUpdate.Package? IntelChipset(string html)
    {
        var version = IntelDownloadVersion().Match(html);
        var link = IntelChipsetLink().Match(html);
        if (!version.Success || !link.Success || !Version.TryParse(version.Groups[1].Value, out _)) return null;
        var next = html.IndexOf("downloadmirror.intel.com", link.Index + link.Length, StringComparison.OrdinalIgnoreCase);
        var sha = IntelSha256().Match(html[link.Index..(next < 0 ? html.Length : next)]);
        return new(version.Groups[1].Value, link.Value, sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null);
    }

    /// <summary>A date the page shows, as the row reads it: the day, at local midnight.</summary>
    private static string? Day(DateTime? day) => day?.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture);

    /// <summary>"887 MB", "862.6 MB", "1.2 GB" as bytes, as the pages count them (1 MB = 1,000,000); 0 when it can't tell.</summary>
    internal static long Bytes(string? size)
    {
        var match = size is null ? Match.Empty : SizeText().Match(size);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0;
        return (long)(value * match.Groups[2].Value.ToUpperInvariant() switch { "GB" => 1e9, "MB" => 1e6, _ => 1e3 });
    }

    /// <summary>A product name to compare: no maker, marks, "Graphics" or "Series", lower case ("AMD Radeon™ RX 9070 XT" and "AMD Radeon RX 9070 XT" are "radeon rx 9070 xt").</summary>
    internal static string Plain(string name) => Spaces().Replace(Noise().Replace(name, " "), " ").Trim().ToLowerInvariant();

    /// <summary>
    /// The name to look for, or null when it names no model: "AMD Radeon(TM) Graphics" ("radeon") or "Intel(R) UHD Graphics"
    /// ("uhd") would match a page for some other chip.
    /// </summary>
    private static string? Named(string? name) =>
        name is null || Plain(name) is not { Length: > 0 } plain || (!plain.Any(char.IsAsciiDigit) && !plain.Contains(' ')) ? null : plain;

    [GeneratedRegex(@"ResultCode\s*=\s*(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AmdResultCode();

    [GeneratedRegex("data-json=\"([^\"]*)\"")]
    private static partial Regex DataJson();

    // " w/ Radeon 780M Graphics", " with Radeon Graphics", " 8-Core Processor", " Processor".
    [GeneratedRegex(@"\s+(?:w/|with)\s.*$|\s+\d+-Core\s+Processor.*$|\s+Processor\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex AmdCpuTail();

    [GeneratedRegex(@"<button\s+class=""accordion-button[^""]*""[^>]*>(.*?)</button>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AccordionHeader();

    [GeneratedRegex(@"<article[^>]*driver-download-details[^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AmdArticle();

    [GeneratedRegex(@"<h4[^>]*>\s*(.*?)\s*</h4>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AmdTitle();

    [GeneratedRegex(@"<strong>\s*([^<]+?)\s*</strong>\s*<p>\s*([^<]*?)\s*</p>", RegexOptions.IgnoreCase)]
    private static partial Regex AmdField();

    [GeneratedRegex(@"Adrenalin\s+(?:Edition\s+)?(\d+\.\d+\.\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdVersion();

    [GeneratedRegex(@"href=""(https://drivers\.amd\.com/drivers/[^""]+?\.exe)""", RegexOptions.IgnoreCase)]
    private static partial Regex AmdLink();

    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AmdHtmlTitle();

    [GeneratedRegex(@"-[a-z]\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex AmdPackageSuffix();

    [GeneratedRegex(@"<meta\s+[^>]*property=""og:updated_time""[^>]*content=""(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex AmdMetaDate();

    [GeneratedRegex(@"\b(?:RX\s+(Vega\s+\d+|[0-9]{3,4}[A-Z]?)|PRO\s+([WR]\d{3,4}[A-Z]?)|R[579]\s+(\d{3,4})|HD\s+(\d{4})|(VII))\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdDiscreteModel();

    [GeneratedRegex(@"other-valid-products-panel__product\b[^>]*>\s*<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex IntelProduct();

    [GeneratedRegex(@"https://downloadmirror\.intel\.com/\d+/gfx_win_(\d+\.\d+)(?:_[\w.-]+)?\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex IntelLink();

    [GeneratedRegex(@"Graphics Driver (\d+\.\d+\.\d+\.\d+)")]
    private static partial Regex IntelVersion();

    [GeneratedRegex(@"ReleaseNotes_+[^""'\s<>]*Non-WHQL\.pdf", RegexOptions.IgnoreCase)]
    private static partial Regex IntelNonWhqlNotes();

    [GeneratedRegex(@"<td\s+data-label=""Version"">\s*[^<]*?\b(\d+\.\d+\.\d+\.\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelTableVersion();

    [GeneratedRegex(@"href=""((?:https://www\.intel\.com)?/content/www/us/en/download/\d+/\d+/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex IntelTableLink();

    [GeneratedRegex(@"<option\b[^>]*value=""((?:https://www\.intel\.com)?/content/www/us/en/download/\d+/\d+/[^""]+)""[^>]*>[^<]*?\b(\d+\.\d+\.\d+\.\d+)\b[^<]*</option>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex IntelOptionVersion();

    [GeneratedRegex(@"Size:\s*([\d.]+\s*[KMG]B)", RegexOptions.IgnoreCase)]
    private static partial Regex IntelSize();

    [GeneratedRegex(@"SHA512:\s*([0-9a-fA-F]{128})\b")]
    private static partial Regex IntelSha();

    [GeneratedRegex(@"Date</label>\s*<span[^>]*>\s*(\d{1,2}/\d{1,2}/\d{4})\s*</span>")]
    private static partial Regex IntelDate();

    [GeneratedRegex(@"<meta\s+name=""DownloadVersion""\s+content=""(\d+(?:\.\d+){1,3})""", RegexOptions.IgnoreCase)]
    private static partial Regex IntelDownloadVersion();

    [GeneratedRegex(@"https://downloadmirror\.intel\.com/\d+/SetupChipset\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex IntelChipsetLink();

    [GeneratedRegex(@"SHA256:\s*([0-9a-fA-F]{64})\b")]
    private static partial Regex IntelSha256();

    // " CPU @ 2.90GHz", " @ 1.10GHz", " CPU".
    [GeneratedRegex(@"\s+(?:CPU\b.*|@.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCpuTail();

    [GeneratedRegex(@"^\d+(?:st|nd|rd|th)$", RegexOptions.IgnoreCase)]
    private static partial Regex Ordinal();

    [GeneratedRegex(@"\((?:R|TM)\)|[®™]")]
    private static partial Regex Marks();

    [GeneratedRegex(@"([\d.]+)\s*([KMG]B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeText();

    // Marks (and AMD's page's U+FFFD and zero-width spaces), makers, and the words a name may or may not carry.
    [GeneratedRegex(@"\((?:R|TM)\)|[®™�​]|\b(?:AMD|ATI|Intel|Series|Graphics)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
