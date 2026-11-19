using System.Text.Json.Nodes;

namespace ExoLauncher.Services.Account;

/// <summary>
/// Collects every page of the account's export (CLIENT.md 7.7): from 0, continuing from each page's cursor while more is
/// true. A tombstone stays as a deleted item. If an item changed while the pages were read, the highest version is kept.
/// </summary>
internal static class AccountExport
{
    internal sealed record Result(bool Ok, string? Error, JsonObject? File);

    internal static async Task<Result> CollectAsync(ExoAccountClient client, string token, DateTimeOffset now)
    {
        var latest = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        JsonNode? account = null;
        long after = 0;
        for (var page = 0; page < 10_000; page++)
        {
            var reply = await client.ExportPageAsync(token, after);
            if (!reply.Ok || reply.Body is not JsonObject body) return new Result(false, reply.Error ?? "bad-response", null);
            account ??= body["account"]?.DeepClone();
            foreach (var item in body["items"] as JsonArray ?? [])
            {
                if (item is not JsonObject obj) continue;
                var key = obj["app"]?.GetValue<string>() + "/" + obj["type"]?.GetValue<string>() + "/" + obj["id"]?.GetValue<string>();
                if (!latest.TryGetValue(key, out var seen) || Version(obj) > Version(seen))
                    latest[key] = (JsonObject)obj.DeepClone();
            }
            var cursor = body["cursor"]?.GetValue<long>() ?? after;
            if (body["more"]?.GetValue<bool>() != true) break;
            after = cursor;
        }

        var items = new JsonArray();
        foreach (var item in latest.Values.OrderBy(o => o["app"]?.GetValue<string>()).ThenBy(o => o["type"]?.GetValue<string>()).ThenBy(o => o["id"]?.GetValue<string>()))
            items.Add(item);
        return new Result(true, null, new JsonObject
        {
            ["format"] = 1,
            ["exported"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["account"] = account?.DeepClone(),
            ["items"] = items,
        });
    }

    private static long Version(JsonObject item) => item["version"]?.GetValue<long>() ?? 0;
}
