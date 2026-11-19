using System.Text.Json;
using ExoLauncher.Services.Hid;

namespace ExoLauncher.Services;

/// <summary>
/// HID for the Devices room's mouse module: hid.list, hid.open, hid.close,
/// hid.sendReport, hid.sendFeatureReport, hid.receiveFeatureReport,
/// hid.receiveInputReport; hid.input and hid.changed. Report bytes travel as
/// base64. Devices are named by an opaque key, never by a path.
/// </summary>
public sealed partial class WebHostBridge
{
    // HID calls run on the thread pool, so the first two may race to wire the service.
    private int _hidWired;

    private HidService Hid()
    {
        var hid = _services.Hid;
        if (Interlocked.Exchange(ref _hidWired, 1) == 1) return hid;
        hid.Input += OnHidInput;
        hid.Changed += OnHidChanged;
        hid.Start();
        return hid;
    }

    private object HidList(JsonElement p, bool hasParams)
    {
        int[]? vendors = null;
        if (hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("vendorIds", out var v) && v.ValueKind == JsonValueKind.Array)
            vendors = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).Take(256).ToArray();
        // An empty list means every maker: Exo Mouse asks that way and picks its own mice from the answer.
        return new
        {
            ok = true,
            devices = Hid().List(vendors).Select(d => new
            {
                key = d.Key, vendorId = d.VendorId, productId = d.ProductId, productName = d.ProductName,
                collections = d.Collections.Select(c => new
                {
                    usagePage = c.UsagePage, usage = c.Usage,
                    inputIds = c.InputIds, outputIds = c.OutputIds, featureIds = c.FeatureIds,
                    inputLength = c.InputLength, outputLength = c.OutputLength, featureLength = c.FeatureLength,
                }),
            }),
        };
    }

    private async Task<object> HidCallAsync(string method, JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (ReadString(p, hasParams, "device") is not { Length: > 0 } key) return new { ok = false, message = "No device." };
        var reportId = (int)(ReadDouble(p, hasParams, "reportId") ?? 0);
        if (reportId is < 0 or > 255) return new { ok = false, message = "No such report." };
        try
        {
            var hid = Hid();
            switch (method)
            {
                case "hid.open": hid.Open(key); return new { ok = true };
                case "hid.close": hid.Close(key); return new { ok = true };
                case "hid.sendReport": await hid.SendReportAsync(key, reportId, Bytes(p, hasParams), ct).ConfigureAwait(false); return new { ok = true };
                case "hid.sendFeatureReport": await hid.SendFeatureReportAsync(key, reportId, Bytes(p, hasParams), ct).ConfigureAwait(false); return new { ok = true };
                case "hid.receiveFeatureReport":
                    return new { ok = true, data = Convert.ToBase64String(await hid.ReceiveFeatureReportAsync(key, reportId, ct).ConfigureAwait(false)) };
                case "hid.receiveInputReport":
                    return new { ok = true, data = Convert.ToBase64String(await hid.ReceiveInputReportAsync(key, reportId, ct).ConfigureAwait(false)) };
                default: return new { ok = false, message = "Unknown HID call." };
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or FormatException)
        {
            return new { ok = false, message = DeviceFailureMessage(ex) };
        }
    }

    /// <summary>The sentence a failed device call shows: a silent device says so; anything else says nothing of its own.</summary>
    internal static string DeviceFailureMessage(Exception ex) =>
        ex is TimeoutException ? "The device did not answer in time." : UserMessage(ex, "The device did not answer.");

    // ------------------------------------------------------------------ mouse profiles and presets

    private const int MaxMouseFileBytes = 512 * 1024;

    /// <summary>
    /// The mouse module's own saved data, each an opaque JSON object it owns, in Exo's data folder so a backup carries it
    /// (PlayerData): its profiles (profiles.json) and its presets and per-mouse values (presets.json).
    /// </summary>
    private static string MouseFilePath(string name) => Path.Combine(ExoLauncher.Helpers.PathHelper.AppDataDir, "mouse", name);

    private static object MouseFileGet(string name, string what)
    {
        try
        {
            var path = MouseFilePath(name);
            if (!File.Exists(path)) return new { ok = true, data = (object?)null };
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? new { ok = true, data = (object?)doc.RootElement.Clone() }
                : new { ok = true, data = (object?)null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { ok = false, message = $"Exo could not read your mouse {what}." };
        }
    }

    /// <summary>Saves one of the module's files: a JSON object of at most 512 KB, written whole or not at all.</summary>
    private static object MouseFileSet(string name, string what, JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new { ok = false, message = $"No {what} to save." };
        var json = data.GetRawText();
        if (json.Length > MaxMouseFileBytes) return new { ok = false, message = $"Too many {what} to save." };
        try
        {
            var path = MouseFilePath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return new { ok = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new { ok = false, message = $"Exo could not save your mouse {what}." };
        }
    }

    private static byte[] Bytes(JsonElement p, bool hasParams) =>
        ReadString(p, hasParams, "data") is { Length: > 0 } data ? Convert.FromBase64String(data) : [];

    private void OnHidInput(string key, int reportId, byte[] data) =>
        PostEvent("hid.input", new { device = key, reportId, data = Convert.ToBase64String(data) });

    private void OnHidChanged() => PostEvent("hid.changed", new { });
}
