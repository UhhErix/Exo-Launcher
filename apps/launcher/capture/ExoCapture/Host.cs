using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoCapture;

/// <summary>
/// The line to Exo: requests come in on stdin as one JSON object per line (<c>{"id","method","params"}</c>), replies
/// (<c>{"id","result"}</c> or <c>{"id","error"}</c>) and events (<c>{"event","data"}</c>) go out on stdout, and stderr is
/// Exo's log. Only the two processes hold these pipes (CONTRACT.md).
/// </summary>
internal static class Host
{
    private static readonly Lock WriteGate = new();
    private static readonly Stream Out = Console.OpenStandardOutput();
    private static readonly Stream Err = Console.OpenStandardError();
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    internal static void Event(string name, JsonNode? data = null) =>
        Write(new JsonObject { ["event"] = name, ["data"] = data ?? new JsonObject() });

    internal static void Reply(long id, JsonNode? result) => Write(new JsonObject { ["id"] = id, ["result"] = result });

    internal static void Fail(long id, string message) => Write(new JsonObject { ["id"] = id, ["error"] = message });

    private static void Write(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions));
        lock (WriteGate)
        {
            try
            {
                Out.Write(bytes);
                Out.Write(NewLine);
                Out.Flush();
            }
            catch (IOException) { } // Exo is gone; stdin's end ends this process.
        }
    }

    /// <summary>A line for Exo's app.log ("Capture: ..." there).</summary>
    internal static void Log(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line.ReplaceLineEndings(" ") + "\n");
        lock (WriteGate)
        {
            try { Err.Write(bytes); Err.Flush(); } catch (IOException) { }
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>A failure meant for the person: its message reaches Exo's page as it is.</summary>
internal sealed class CaptureException(string message) : Exception(message);

/// <summary>Reading request parameters without throwing on a missing or mistyped field.</summary>
internal static class Params
{
    internal static string? Str(this JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static double? Num(this JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    internal static int Int(this JsonNode? node, string name, int fallback) =>
        node.Num(name) is { } d ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : fallback;

    internal static long Long(this JsonNode? node, string name, long fallback) =>
        node.Num(name) is { } d ? (long)d : fallback;

    internal static bool Bool(this JsonNode? node, string name, bool fallback) =>
        node?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
}
