using System.Globalization;
using System.Text;

namespace ExoLauncher.Services;

/// <summary>
/// Says whether a link's address imitates a brand it isn't, before Exo opens it from chat or a note. Entirely local: it reads
/// the link's text and a short built-in list of brands, and looks nothing up. The patterns are the ones phishing uses
/// (NextDNS and others name them): a look-alike spelling (disc0rd, rnicrosoft, steamcornmunity), letters from another
/// alphabet (Cyrillic "а" for "a", punycode xn-- labels), a brand with a bait word (discord-nitro-gift, steamfree, paypal-login),
/// the brand's own address as a decoy before the real one (steamcommunity.com.evil.ru), and a name before an @
/// (https://discord.com@evil.example). It never guesses at an address that is simply someone else's: a word like "steam"
/// alone, a fan site's name or a bait word without a brand all pass, and the brands' own addresses always pass.
/// </summary>
internal static class LinkLookalike
{
    /// <summary>What the check found: the brand imitated, or null when the address only hides where it goes.</summary>
    internal sealed record Finding(string? Brand);

    // First, so the fields below that fold text through Skeleton find it built.
    private static readonly Dictionary<char, char> Confusables = BuildConfusables();

    private sealed record Brand(string Name, string[] Domains, string[] Unique, string[] Generic, bool CountrySites = false);

    // Official addresses (the site and every subdomain of it). A token is Unique when no one else would spell it
    // (steamcommunity), Generic when it is also a word (steam, riot, epic): a Generic one is only a hit as a look-alike
    // spelling or with a bait word. CountrySites: the brand has its own site per country (nvidia.fr), so its name under a
    // two-letter country domain is fine.
    private static readonly Brand[] Brands =
    [
        new("Discord", ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordstatus.com", "discord.gift", "discord.new", "discord.co", "dis.gd", "discordcdn.com", "discord.dev", "discord.design", "discord.store", "discordmerch.com", "discordsays.com", "discordactivities.com"], ["discordapp"], ["discord"]),
        new("Steam", ["steampowered.com", "steamcommunity.com", "steamgames.com", "steamstatic.com", "steamcontent.com", "steamusercontent.com", "steam-chat.com", "steamserver.net", "valvesoftware.com", "steam.tv", "steamdeck.com"], ["steamcommunity", "steampowered", "steamgames", "steamstatic"], ["steam"]),
        new("Epic Games", ["epicgames.com", "unrealengine.com", "fortnite.com", "epicgames.dev", "easyanticheat.net", "kidswebservices.com"], ["epicgames", "fortnite"], ["epic"]),
        new("Riot Games", ["riotgames.com", "leagueoflegends.com", "playvalorant.com", "valorant.com", "riotcdn.net", "riotgames.net"], ["riotgames", "leagueoflegends"], ["riot"]),
        new("Roblox", ["roblox.com", "rbxcdn.com", "rbx.com", "robloxlabs.com", "roblox.qq.com"], ["roblox"], []),
        new("NVIDIA", ["nvidia.com", "geforce.com", "nvidiagrid.net", "nvidia.co.uk", "nvidia.com.cn", "nvidia.cn"], ["nvidia", "geforce"], [], CountrySites: true),
        new("Microsoft", ["microsoft.com", "microsoftonline.com", "microsoftstore.com", "live.com", "office.com", "office365.com", "xbox.com", "windows.com", "windowsupdate.com", "msn.com", "bing.com", "outlook.com", "skype.com", "hotmail.com", "msft.net", "sharepoint.com", "onedrive.com", "azure.com", "visualstudio.com"], ["microsoft"], [], CountrySites: true),
        new("PayPal", ["paypal.com", "paypal.me", "paypalobjects.com", "paypal-community.com", "paypal.com.br", "paypal.com.au", "paypal.co.uk", "paypal.com.mx"], ["paypal"], [], CountrySites: true),
        new("Blizzard", ["blizzard.com", "battle.net", "blizzard.cn", "battlenet.com.cn"], ["blizzard"], []),
        new("Spotify", ["spotify.com", "scdn.co", "spotifycdn.com", "spotify.link", "spotify.net"], ["spotify"], []),
        new("Twitch", ["twitch.tv", "twitchcdn.net", "jtvnw.net", "ttvnw.net", "twitch.com"], [], ["twitch"]),
    ];

    // Words a scam puts beside a brand to sound official or tempting.
    private static readonly string[] Baits =
    [
        "nitro", "gift", "free", "giveaway", "promo", "promotion", "bonus", "reward", "rewards", "claim", "drop", "airdrop",
        "login", "logon", "signin", "verify", "verification", "auth", "authenticate", "security", "secure", "account", "accounts",
        "support", "help", "helpdesk", "service", "update", "upgrade", "wallet", "billing", "payment", "trade", "trading", "tradeoffer",
        "offer", "boost", "partner", "partners", "event", "code", "redeem", "giftcard", "download", "app", "community", "official",
        "staff", "moderator", "admin", "safety", "appeal", "unban", "alert", "warning", "prime", "premium", "gold", "vip",
    ];

    // Real third-party sites whose names sit one letter from a brand's: never a hit.
    private static readonly string[] Known = ["paypay.com", "paypay.ne.jp", "steamgifts.com", "steamdb.info", "steamcharts.com", "steamspy.com", "steamrep.com", "steamladder.com", "steamid.io"];

    // Where a person can pick the name in front, so the name in front is the one that counts (discord-nitro.vercel.app).
    private static readonly string[] HostedUnder =
    [
        "github.io", "vercel.app", "pages.dev", "netlify.app", "workers.dev", "web.app", "firebaseapp.com", "herokuapp.com", "azurewebsites.net",
        "blogspot.com", "wixsite.com", "weebly.com", "000webhostapp.com", "glitch.me", "repl.co", "onrender.com", "fly.dev", "surge.sh",
        "ngrok.io", "ngrok-free.app", "trycloudflare.com", "duckdns.org", "sites.google.com", "framer.app", "carrd.co", "gitbook.io", "webflow.io", "r2.dev",
    ];

    private static readonly HashSet<string> SecondLevels = new(["co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "gob", "nom", "mil"]);
    private static readonly HashSet<string> TldWords = new(["com", "net", "org", "gg", "co", "tv", "me", "io", "gift", "app", "dev"]);

    /// <summary>The finding for a link, or null when it looks like what it says. Only http and https links are looked at.</summary>
    internal static Finding? Check(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host.Contains(':') || host.All(c => char.IsAsciiDigit(c) || c == '.')) return null;
        var labels = host.Split('.');
        if (labels.Length < 2) return null;

        var official = Official(host);
        if (official is not null) return null;
        if (Known.Any(k => Under(host, k))) return null;

        var unicode = Unicode(host);
        var ulabels = unicode.Split('.');
        if (ulabels.Length != labels.Length) ulabels = labels;

        // A name before the @ that reads like an address: https://discord.com@evil.example/
        var user = Uri.UnescapeDataString(uri.UserInfo);
        if (user.Contains('.') && user.Any(char.IsLetter))
            return new Finding(Brands.FirstOrDefault(b => Mentions(user, b))?.Name);

        var suffix = SuffixLength(labels);
        var registrable = labels.Length - suffix - 1;
        if (registrable < 0) return null;

        // The brand's own address used as a decoy: steamcommunity.com.evil.ru, login.discord.com.verify.xyz
        var padded = "." + string.Join('.', ulabels) + ".";
        foreach (var brand in Brands)
            foreach (var domain in brand.Domains)
            {
                var at = padded.IndexOf("." + domain + ".", StringComparison.Ordinal);
                if (at < 0) continue;
                // nvidia.com.tw, microsoft.com.au: the brand's own site in a country.
                var rest = padded[(at + domain.Length + 2)..];
                if (brand.CountrySites && rest.Length == 3 && rest[2] == '.' && char.IsAsciiLetter(rest[0]) && char.IsAsciiLetter(rest[1])) continue;
                return new Finding(brand.Name);
            }

        // The label a person registered: the whole of it, then its parts.
        var label = ulabels[registrable];
        var hosted = HostedUnder.Any(h => Under(host, h));
        var country = !hosted && labels[^1].Length == 2 && labels[^1] != "tv";
        if (WholeName(label, country, hosted) is { } whole) return whole;
        for (var i = 0; i <= registrable; i++)
            if (Pieces(ulabels[i]) is { } bait) return bait;
        return null;
    }

    /// <summary>The brand whose own address this host is (itself or any subdomain of it), or null.</summary>
    private static string? Official(string host)
    {
        foreach (var brand in Brands)
            if (brand.Domains.Any(d => Under(host, d))) return brand.Name;
        return null;
    }

    private static bool Under(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static bool Mentions(string text, Brand brand)
    {
        var skeleton = Skeleton(text);
        return brand.Unique.Concat(brand.Generic).Any(t => skeleton.Contains(Skeleton(t), StringComparison.Ordinal));
    }

    private static string Unicode(string asciiHost)
    {
        try { return new IdnMapping().GetUnicode(asciiHost).ToLowerInvariant(); }
        catch (ArgumentException) { return asciiHost; }
    }

    // How many labels at the end are the public suffix (com; co.uk; vercel.app).
    private static int SuffixLength(string[] labels)
    {
        var host = string.Join('.', labels);
        var hosted = HostedUnder.Where(h => Under(host, h)).Select(h => h.Count(c => c == '.') + 1).DefaultIfEmpty(0).Max();
        if (hosted > 0) return hosted;
        var n = labels.Length;
        return n >= 3 && labels[n - 1].Length == 2 && SecondLevels.Contains(labels[n - 2]) ? 2 : 1;
    }

    // The registered name as a whole, hyphens and all, against each brand: a look-alike spelling of it, or its unique name
    // under an address that isn't its own (not under a host where anyone may take the name: microsoft.github.io is Microsoft's).
    private static Finding? WholeName(string label, bool country, bool hosted)
    {
        var plain = label.Replace("-", "");
        var skeleton = Skeleton(plain);
        foreach (var brand in Brands)
        {
            foreach (var token in brand.Unique.Concat(brand.Generic))
            {
                var isUnique = brand.Unique.Contains(token);
                if (label == token)
                {
                    if (isUnique && !hosted && !(brand.CountrySites && country)) return new Finding(brand.Name);
                    continue;
                }
                var ts = Skeleton(token);
                if (skeleton == ts) return new Finding(brand.Name);
                if (token.Length >= 8 && Math.Abs(skeleton.Length - ts.Length) <= 1 && Distance(skeleton, ts) <= 1) return new Finding(brand.Name);
            }
        }
        return null;
    }

    // A brand's name together with a bait word in one label: discord-nitro-gift, steamfree, free-nitro-discord.
    private static Finding? Pieces(string label)
    {
        var parts = label.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => Skeleton(p.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))).Where(p => p.Length > 0).ToArray();
        string? found = null;
        var bait = false;
        foreach (var part in parts)
        {
            if (Joined(part) is { } mixed) { found ??= mixed.Brand; bait |= mixed.Bait; continue; }
            if (Name(part) is { } name) found ??= name;
            else if (Segments(part)) bait = true;
        }
        return found is not null && bait ? new Finding(found) : null;
    }

    // One hyphen-free piece that is a brand and bait words run together (discordnitro, freesteam).
    private static (string Brand, bool Bait)? Joined(string part)
    {
        foreach (var brand in Brands)
            foreach (var token in brand.Unique.Concat(brand.Generic))
            {
                var ts = Skeleton(token);
                for (var at = part.IndexOf(ts, StringComparison.Ordinal); at >= 0; at = part.IndexOf(ts, at + 1, StringComparison.Ordinal))
                {
                    var before = part[..at];
                    var after = part[(at + ts.Length)..];
                    if (before.Length + after.Length == 0) continue;
                    if (Segments(before) && Segments(after)) return (brand.Name, true);
                }
            }
        return null;
    }

    private static string? Name(string part)
    {
        foreach (var brand in Brands)
            foreach (var token in brand.Unique.Concat(brand.Generic))
            {
                var ts = Skeleton(token);
                if (part == ts) return brand.Name;
                if (token.Length >= 8 && Math.Abs(part.Length - ts.Length) <= 1 && Distance(part, ts) <= 1) return brand.Name;
            }
        return null;
    }

    private static readonly string[] BaitSkeletons = Baits.Select(Skeleton).OrderByDescending(b => b.Length).ToArray();

    // Whether the text is nothing but bait words (an empty text counts: the brand stands alone on that side).
    private static bool Segments(string text)
    {
        if (text.Length == 0) return true;
        var ok = new bool[text.Length + 1];
        ok[0] = true;
        for (var i = 1; i <= text.Length; i++)
            foreach (var b in BaitSkeletons)
                if (b.Length <= i && ok[i - b.Length] && string.CompareOrdinal(text, i - b.Length, b, 0, b.Length) == 0) { ok[i] = true; break; }
        return ok[text.Length];
    }

    // What the letters would look like to someone reading fast: other alphabets' letters become the Latin ones they copy,
    // accents go, and the shapes that pass for each other fold together (0 and o, 1 l and i, rn and m, vv and w).
    private static string Skeleton(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(Confusables.TryGetValue(c, out var latin) ? latin : c);
        }
        return sb.ToString().Replace("vv", "w").Replace("rn", "m")
            .Replace('0', 'o').Replace('1', 'l').Replace('i', 'l').Replace('3', 'e').Replace('4', 'a').Replace('5', 's');
    }

    private static Dictionary<char, char> BuildConfusables()
    {
        var map = new Dictionary<char, char>();
        // Pairs of (the look-alike, the plain Latin letter or hyphen it passes for): Cyrillic, Greek, Armenian, Latin extras, dashes.
        const string pairs = "аa" + "еe" + "оo" + "рp" + "сc" + "уy" + "хx" + "іi" + "јj" + "ѕs" + "ԁd" + "ԛq" + "ԝw" + "һh" + "ӏl" + "үy" + "кk" + "қk" + "мm" + "нh" + "тt"
            + "ɡg" + "ıi" + "ɩi" + "ɑa" + "øo" + "đd" + "łl" + "ðd" + "οo" + "αa" + "νv" + "ιi" + "τt" + "ρp" + "υu" + "κk" + "εe" + "χx" + "ηn" + "ωw" + "ոn" + "օo" + "սu" + "ѵv"
            + "‐-" + "‑-" + "–-" + "—-" + "−-";
        for (var i = 0; i < pairs.Length; i += 2) map[pairs[i]] = pairs[i + 1];
        return map;
    }

    private static int Distance(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
