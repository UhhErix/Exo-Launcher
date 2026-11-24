using System.Buffers;
using System.IO;
using System.Text.Json;

namespace ExoLauncher.Services;

internal sealed class RpcTrace : IDisposable
{
    internal const string EnvVariable = "EXO_RPC_TRACE";
    private const int MaxDepth = 8;
    private const int MaxKeys = 64;
    private const int MaxArraySample = 16;

    internal static RpcTrace Default { get; } = new(Environment.GetEnvironmentVariable(EnvVariable));
    internal static bool Enabled => Default.IsEnabled;

    internal readonly bool IsEnabled;
    private readonly string? _filePath;
    private readonly Lock _gate = new();
    private FileStream? _stream;

    internal RpcTrace(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            IsEnabled = false;
            return;
        }

        _filePath = Path.GetFullPath(filePath.Trim());
        IsEnabled = true;
    }

    internal static string ClientKind(IHostTransport? transport) =>
        transport is NamedPipeHostTransport ? "native" : "web";

    internal void RecordRequest(string client, string id, string method, JsonElement paramsElement, bool hasParams)
    {
        if (!IsEnabled) return;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteString("client", client);
            writer.WriteString("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params_shape");
            if (hasParams)
                WriteShape(writer, paramsElement, 0);
            else
                writer.WriteNullValue();
            writer.WriteEndObject();
        }
        WriteLine(buffer.WrittenSpan);
    }

    internal void RecordReply(string client, string id, bool ok, double elapsedMs)
    {
        if (!IsEnabled) return;
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteString("client", client);
            writer.WriteString("id", id);
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("ms", Math.Round(elapsedMs, 2));
            writer.WriteEndObject();
        }
        WriteLine(buffer.WrittenSpan);
    }

    internal void RecordEvent(string client, string eventName, object? data)
    {
        if (!IsEnabled) return;
        JsonElement payloadElement = default;
        var hasPayload = false;
        if (data is not null)
        {
            try
            {
                payloadElement = ExoBridgeProtocol.SerializeToElement(data);
                hasPayload = true;
            }
            catch
            {
                hasPayload = false;
            }
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteString("client", client);
            writer.WriteString("event", eventName);
            writer.WritePropertyName("payload_shape");
            if (hasPayload)
                WriteShape(writer, payloadElement, 0);
            else
                writer.WriteNullValue();
            writer.WriteEndObject();
        }
        WriteLine(buffer.WrittenSpan);
    }

    internal static void WriteShape(Utf8JsonWriter writer, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteStringValue("null");
                return;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteStringValue("boolean");
                return;
            case JsonValueKind.Number:
                writer.WriteStringValue("number");
                return;
            case JsonValueKind.String:
                writer.WriteStringValue("string");
                return;
            case JsonValueKind.Object:
                if (depth >= MaxDepth)
                {
                    writer.WriteStringValue("object");
                    return;
                }
                writer.WriteStartObject();
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                {
                    if (seenKeys.Count >= MaxKeys) break;
                    var safeName = SanitizeKey(prop.Name);
                    if (!seenKeys.Add(safeName)) continue;
                    writer.WritePropertyName(safeName);
                    WriteShape(writer, prop.Value, depth + 1);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                if (depth >= MaxDepth)
                {
                    writer.WriteStringValue("array");
                    return;
                }
                writer.WriteStartArray();
                var count = 0;
                var allObjects = true;
                var hasAny = false;
                JsonElement first = default;
                foreach (var item in element.EnumerateArray())
                {
                    if (!hasAny)
                    {
                        first = item;
                        hasAny = true;
                    }
                    if (item.ValueKind != JsonValueKind.Object) allObjects = false;
                    count++;
                    if (count >= MaxArraySample) break;
                }

                if (hasAny)
                {
                    if (allObjects)
                    {
                        WriteMergedObjectArrayShape(writer, element, depth + 1);
                    }
                    else
                    {
                        WriteShape(writer, first, depth + 1);
                    }
                }
                writer.WriteEndArray();
                return;
        }
    }

    private static void WriteMergedObjectArrayShape(Utf8JsonWriter writer, JsonElement array, int depth)
    {
        if (depth >= MaxDepth)
        {
            writer.WriteStringValue("object");
            return;
        }
        writer.WriteStartObject();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var sampled = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (sampled >= MaxArraySample || seenKeys.Count >= MaxKeys) break;
            sampled++;
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in item.EnumerateObject())
            {
                if (seenKeys.Count >= MaxKeys) break;
                var safeName = SanitizeKey(prop.Name);
                if (!seenKeys.Add(safeName)) continue;
                writer.WritePropertyName(safeName);
                WriteShape(writer, prop.Value, depth + 1);
            }
        }
        writer.WriteEndObject();
    }

    internal static string SanitizeKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 48) return "$key";
        var first = key[0];
        if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z') || first == '_'))
            return "$key";
        var digits = 0;
        for (var i = 1; i < key.Length; i++)
        {
            var c = key[i];
            if (c >= '0' && c <= '9')
            {
                digits++;
                continue;
            }
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '-')
                continue;
            return "$key";
        }
        if (key.Length >= 20 && digits >= 6) return "$key";
        return key;
    }

    private void WriteLine(ReadOnlySpan<byte> utf8Line)
    {
        if (_filePath is null) return;
        try
        {
            lock (_gate)
            {
                if (_stream is null)
                {
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
                }
                _stream.Write(utf8Line);
                _stream.WriteByte((byte)'\n');
                _stream.Flush();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }
    }
}
