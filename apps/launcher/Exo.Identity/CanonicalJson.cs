using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// Signed JSON records (device records, device-list changes): RFC 8785 canonical JSON restricted, as plan §2.1 and
/// §2.11 say, to ASCII keys, strings (any text), booleans, arrays, objects and integers below 2^53,
/// so a canonicalizer stays trivial and every app signs the same bytes. Anything else (a float, null, -0,
/// a repeated key, a lone surrogate, a non-ASCII key) is refused rather than written two ways; an absent value is a
/// left-out key. A record carries its signature in a <c>sig</c> field: base64url P1363 ECDSA P-256 (low s) over the
/// record's label and then the canonical JSON of the record without <c>sig</c>, so a signature made for one kind of
/// record can't be passed off as another.
/// </summary>
public static class CanonicalJson
{
    public const long MaxInteger = (1L << 53) - 1;
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    /// <summary>The canonical bytes; throws <see cref="FormatException"/> for anything outside the subset.</summary>
    public static byte[] Serialize(JsonNode node)
    {
        var text = new StringBuilder();
        Write(node, text);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>A JSON object from the wire, or null when it isn't one or repeats a key.</summary>
    public static JsonObject? ParseObject(ReadOnlySpan<byte> utf8)
    {
        try { return JsonNode.Parse(utf8, documentOptions: Strict) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static JsonObject Sign(JsonObject record, string label, IDeviceSigner signer)
    {
        var signed = (JsonObject)record.DeepClone();
        signed.Remove("sig");
        signed["sig"] = Base64Url.EncodeToString(signer.Sign(Signed(label, signed)));
        return signed;
    }

    /// <summary>True when the record's <c>sig</c> is the given key's signature over the record under this label.</summary>
    public static bool Verify(JsonObject record, string label, ReadOnlySpan<byte> publicKey)
    {
        if (record["sig"] is not JsonValue sig || !sig.TryGetValue<string>(out var text) || Bytes(text) is not { } signature) return false;
        try { return P256.Verify(publicKey, Signed(label, record), signature); }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// An integer in the subset (magnitude below 2^53, not written -0), or null. Values built in code hold an int or a
    /// long; values parsed from the wire hold the JSON text, where "1.0" and "1e0" aren't integers.
    /// </summary>
    public static long? Integer(JsonValue value)
    {
        long n;
        if (value.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out n) || element.GetRawText() == "-0") return null;
        }
        else if (value.TryGetValue<long>(out var big)) n = big;
        else if (value.TryGetValue<int>(out var small)) n = small;
        else return null;
        return Math.Abs(n) <= MaxInteger ? n : null;
    }

    /// <summary>
    /// The hex SHA-256 of a record's canonical form without its own <c>sig</c> (plan §2.13): how a device-list change
    /// names the one before it, and the last resort when two changes come at once. The signature stays out because
    /// it isn't the content: its author can sign again at will, so a hash over it could be ground, and two copies of one
    /// change would look like two changes. A record nested inside (an <c>add</c>'s device record) keeps its
    /// <c>sig</c>: that is content, covered by the outer signature.
    /// </summary>
    public static string Hash(JsonObject record) => Convert.ToHexStringLower(SHA256.HashData(Serialize(Unsigned(record))));

    /// <summary>
    /// Bytes from base64url (RFC 4648 §5) as the records write them, without padding; null for anything else (padding,
    /// white space, a character outside the alphabet, leftover bits that aren't zero), so one value has one spelling.
    /// </summary>
    public static byte[]? Bytes(string text)
    {
        try { return Base64Url.DecodeFromChars(text) is { } bytes && Base64Url.EncodeToString(bytes) == text ? bytes : null; }
        catch (FormatException) { return null; }
    }

    private static JsonObject Unsigned(JsonObject record)
    {
        var unsigned = (JsonObject)record.DeepClone();
        unsigned.Remove("sig");
        return unsigned;
    }

    private static byte[] Signed(string label, JsonObject record) => [.. Encoding.ASCII.GetBytes(label), .. Serialize(Unsigned(record))];

    private static void Write(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case JsonObject obj:
                text.Append('{');
                var first = true;
                // Keys are ASCII, so ordinal order is RFC 8785's UTF-16 code unit order.
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!key.All(char.IsAscii)) throw new FormatException("A key is not ASCII.");
                    if (!first) text.Append(',');
                    first = false;
                    WriteString(key, text);
                    text.Append(':');
                    Write(value, text);
                }
                text.Append('}');
                return;
            case JsonArray array:
                text.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    Write(array[i], text);
                }
                text.Append(']');
                return;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        string s;
                        // A lone surrogate written as \ud800 parses, then can't be read as a string.
                        try { s = value.GetValue<string>(); }
                        catch (InvalidOperationException) { throw new FormatException("A string isn't valid UTF-16."); }
                        WriteString(s, text);
                        return;
                    case JsonValueKind.True: text.Append("true"); return;
                    case JsonValueKind.False: text.Append("false"); return;
                    case JsonValueKind.Number when Integer(value) is { } n:
                        text.Append(n.ToString(CultureInfo.InvariantCulture));
                        return;
                }
                break;
        }
        throw new FormatException("Only objects, arrays, strings, booleans and integers below 2^53.");
    }

    // ECMAScript's JSON.stringify escaping, which RFC 8785 adopts: the two-character escapes, \u00xx (lowercase) for
    // the other controls, everything else as itself.
    private static void WriteString(string value, StringBuilder text)
    {
        text.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                text.Append(c).Append(value[++i]);
                continue;
            }
            if (char.IsSurrogate(c)) throw new FormatException("A lone surrogate.");
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ': text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: text.Append(c); break;
            }
        }
        text.Append('"');
    }
}
