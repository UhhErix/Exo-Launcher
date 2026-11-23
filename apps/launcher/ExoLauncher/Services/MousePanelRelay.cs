using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static partial class MousePanelRelay
{
    internal const string WrapperUrl = "https://" + WebViewTrustPolicy.TrustedAppHost + "/mouse-panel.html";
    internal const string MouseOrigin = "https://" + WebViewTrustPolicy.MouseHost;
    internal const string MouseStartUrl = "https://" + WebViewTrustPolicy.MouseHost + "/index.html";
    private const string CoverPrefix = "https://" + CoverArtService.VirtualHost + "/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly HashSet<string> HidMethods = new(StringComparer.Ordinal)
    {
        "hid.list",
        "hid.open",
        "hid.close",
        "hid.sendReport",
        "hid.sendFeatureReport",
        "hid.receiveFeatureReport",
        "hid.receiveInputReport",
    };

    private static readonly HashSet<string> ExoMethods = new(StringComparer.Ordinal)
    {
        "exo.games",
        "exo.profiles.read",
        "exo.profiles.write",
        "exo.presets.read",
        "exo.presets.write",
        "exo.textScale",
        "exo.motion",
        "exo.theme",
        "exo.mouseCore",
    };

    private static readonly HashSet<string> CoreActions = new(StringComparer.Ordinal)
    {
        "measure",
        "drivers",
        "spread",
        "unspread",
        "apply",
        "undo",
    };

    internal static readonly string WrapperHtml =
        "<!doctype html><html style=\"background:#000;height:100%;margin:0;overflow:hidden\">" +
        "<body style=\"background:#000;height:100%;margin:0;overflow:hidden\">" +
        "<iframe id=\"m\" src=\"" + MouseStartUrl + "\" title=\"Mouse\" " +
        "style=\"border:0;display:block;width:100%;height:100%;background:#000\"></iframe>" +
        "<script>(()=>{const o='" + MouseOrigin + "',f=document.getElementById('m');" +
        "window.addEventListener('message',e=>{if(e.origin!==o||e.source!==f.contentWindow)return;window.chrome.webview.postMessage(e.data)});" +
        "window.chrome.webview.addEventListener('message',e=>{f.contentWindow?.postMessage(e.data,o)});" +
        "f.addEventListener('load',()=>{window.chrome.webview.postMessage({exo:'mouse-host',ready:true})});})();</script>" +
        "</body></html>";

    internal sealed record MouseGameItem(string Id, string Title, string? Cover);

    internal sealed record MouseStateNotice(
        string Name,
        string? Maker,
        int? Battery,
        bool Charging,
        bool Low,
        string? Image,
        bool? Wireless);

    internal static bool IsAllowedMethod(string? method) =>
        method is not null && (HidMethods.Contains(method) || ExoMethods.Contains(method));

    internal static bool IsHidMethod(string? method) =>
        method is not null && HidMethods.Contains(method);

    internal static bool IsTrustedWrapperSource(string? source) =>
        string.Equals(source, WrapperUrl, StringComparison.OrdinalIgnoreCase);

    internal static string? ValidateCoreAction(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("action", out var actionEl)
            || actionEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (actionEl.ValueKind == JsonValueKind.String)
        {
            var action = actionEl.GetString();
            if (action is not null && CoreActions.Contains(action))
                return action;
        }
        throw new InvalidOperationException("Exo Mouse asked for an unknown CPU core action.");
    }

    internal static double TextScaleFor(string? textSize) => textSize switch
    {
        "small" => 0.9,
        "large" => 1.14,
        _ => 1.0,
    };

    internal static string TextScaleString(string? textSize) =>
        TextScaleFor(textSize).ToString("G", CultureInfo.InvariantCulture);

    internal static string MotionFor(bool reduceMotion) => reduceMotion ? "reduce" : "full";

    internal static int RailWidth(double roomWidth, double scale)
    {
        var clampedScale = double.IsFinite(scale) && scale > 0.5 && scale < 2.0 ? scale : 1.0;
        var room = double.IsFinite(roomWidth) ? Math.Max(0.0, roomWidth) : 0.0;
        var raw = Math.Max(340.0 * clampedScale, Math.Min(560.0 * clampedScale, room * 0.215));
        return (int)Math.Round(raw, MidpointRounding.AwayFromZero);
    }

    internal static Dictionary<string, string> BuildLookTokens()
    {
        const string glassyRing =
            "inset 0 1px 0 rgb(255 255 255 / 0.17), " +
            "inset 1px 0 0 rgb(255 255 255 / 0.06), " +
            "inset -1px 0 0 rgb(255 255 255 / 0.06), " +
            "inset 0 -1px 0 rgb(255 255 255 / 0.03), " +
            "inset 0 16px 28px -22px rgb(255 255 255 / 0.1)";
        const string panelEdge = glassyRing + ", 0 0 40px rgb(0 0 0 / 0.5)";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--bg"] = "#000000",
            ["--ink"] = "#ffffff",
            ["--muted"] = "#b4b4b4",
            ["--faint"] = "#9a9a9a",
            ["--good"] = "#3dd68c",
            ["--bad"] = "#ff5c5c",
            ["--primary-ink"] = "#000000",
            ["--primary-hover"] = "#e8e8e8",
            ["--track"] = "rgb(255 255 255 / 0.17)",
            ["--hair"] = "rgb(255 255 255 / 0.055)",
            ["--pane-fill"] = "transparent",
            ["--pane-edge"] = glassyRing,
            ["--line-soft"] = "rgb(255 255 255 / 0.055)",
            ["--hover-fill"] = "rgb(255 255 255 / 0.1)",
            ["--selected"] = "rgb(255 255 255 / 0.15)",
            ["--pressed"] = "rgb(255 255 255 / 0.133)",
            ["--fill"] = "transparent",
            ["--rim"] = glassyRing,
            ["--rim-pressed"] = glassyRing,
            ["--field-rim"] = glassyRing,
            ["--panel-fill"] = "rgb(0 0 0 / 0.36)",
            ["--panel-edge"] = panelEdge,
            ["--panel-ink"] = "#ffffff",
            ["--panel-muted"] = "#dcdcdc",
            ["--panel-hover"] = "rgb(255 255 255 / 0.07)",
            ["--panel-selected"] = "rgb(255 255 255 / 0.1)",
            ["--panel-pressed"] = "rgb(255 255 255 / 0.14)",
            ["--hero-fill"] = "#000000",
            ["--hero-edge"] = "inset 0 0 0 1px rgb(255 255 255 / 0.055)",
        };
    }

    internal static (List<MouseGameItem> Games, Dictionary<string, string> CardMap) BuildInstalledGames(
        IReadOnlyList<GameEntry> library,
        Func<string, string> formatThumbnailUrl)
    {
        var cardMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var games = new List<MouseGameItem>();

        foreach (var g in library)
        {
            if (string.IsNullOrEmpty(g.Id)) continue;
            cardMap[g.Id] = g.Id;
            if (g.Variants is not null)
            {
                foreach (var v in g.Variants)
                {
                    if (!string.IsNullOrEmpty(v.Id) && !cardMap.ContainsKey(v.Id))
                        cardMap[v.Id] = g.Id;
                }
            }
            if (!g.Installed) continue;
            string? cover = null;
            if (g.CoverUrl is { } raw && raw.StartsWith(CoverPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var file = ExtractCoverFileName(raw);
                if (!string.IsNullOrEmpty(file))
                    cover = formatThumbnailUrl(file);
            }
            games.Add(new MouseGameItem(g.Id, g.Title ?? "", cover));
        }

        games.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return (games, cardMap);
    }

    internal static string? ExtractCoverFileName(string coverUrl)
    {
        if (!coverUrl.StartsWith(CoverPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = coverUrl[CoverPrefix.Length..];
        var q = rest.IndexOf('?');
        if (q >= 0) rest = rest[..q];
        var slash = rest.LastIndexOf('/');
        var file = slash >= 0 ? rest[(slash + 1)..] : rest;
        if (string.IsNullOrWhiteSpace(file) || file.Contains("..", StringComparison.Ordinal)) return null;
        return file;
    }

    internal static string? ValidateNotice(JsonElement root)
    {
        if (!root.TryGetProperty("notice", out var el) || el.ValueKind != JsonValueKind.String) return null;
        var text = el.GetString();
        return text is { Length: > 0 and <= 160 } ? text : null;
    }

    internal static bool TryParseMouseState(JsonElement root, out MouseStateNotice? state)
    {
        state = null;
        if (!root.TryGetProperty("state", out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return true;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return false;
        var rawName = nameEl.GetString() ?? "";
        var name = rawName.Length > 60 ? rawName[..60] : rawName;
        int? battery = null;
        if (el.TryGetProperty("battery", out var batEl) && batEl.ValueKind == JsonValueKind.Number && batEl.TryGetDouble(out var b)
            && double.IsFinite(b) && b >= 0 && b <= 100)
        {
            battery = (int)Math.Round(b, MidpointRounding.AwayFromZero);
        }
        string? image = null;
        if (el.TryGetProperty("image", out var imgEl) && imgEl.ValueKind == JsonValueKind.String)
        {
            var rawImg = imgEl.GetString();
            if (rawImg is not null && OpenMouseImagePattern().IsMatch(rawImg))
                image = rawImg;
        }
        bool? wireless = el.TryGetProperty("wireless", out var wEl) && wEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? wEl.GetBoolean()
            : null;
        string? maker = null;
        if (el.TryGetProperty("maker", out var mEl) && mEl.ValueKind == JsonValueKind.String)
        {
            var trimmed = mEl.GetString()?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                maker = trimmed.Length > 40 ? trimmed[..40] : trimmed;
        }
        var charging = el.TryGetProperty("charging", out var cEl) && cEl.ValueKind == JsonValueKind.True;
        var low = el.TryGetProperty("low", out var lEl) && lEl.ValueKind == JsonValueKind.True;
        state = new MouseStateNotice(name, maker, battery, charging, low, image, wireless);
        return true;
    }

    internal static string SerializeReply(long id, object result) =>
        JsonSerializer.Serialize(new { exo = "mouse", id, result }, JsonOptions);

    internal static string SerializeError(long id, string error) =>
        JsonSerializer.Serialize(new { exo = "mouse", id, error }, JsonOptions);

    internal static string SerializeEvent(string eventName, object? data) =>
        JsonSerializer.Serialize(new { exo = "mouse", @event = eventName, data }, JsonOptions);

    internal sealed record MouseGameRunState(string Id, bool IsRunning);

    internal static string SerializeGameRunState(string cardId, bool isRunning) =>
        SerializeEvent("game.runState", new MouseGameRunState(cardId, isRunning));

    [GeneratedRegex(@"^https://img\.openmouse\.app/[a-z0-9-]+\.(png|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenMouseImagePattern();
}
