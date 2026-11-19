using System.Text;

namespace ExoLauncher.Helpers;

/// <summary>
/// Valve's binary KeyValues, as Steam writes its appcache (stats/UserGameStats_*.bin and UserGameStatsSchema_*.bin): each
/// entry a type byte, a NUL-ended name, then the value; 0x00 opens a node closed by 0x08. Read only: nodes become
/// dictionaries (ordinal keys), strings strings, int32 int, float float, uint64 and int64 long. Null for a file that
/// isn't one (a cut-off write, another format).
/// </summary>
internal static class ValveKv
{
    internal static Dictionary<string, object>? Read(byte[] bytes)
    {
        try
        {
            var at = 0;
            return Node(bytes, ref at, depth: 0);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException) { return null; }
    }

    private static Dictionary<string, object> Node(byte[] b, ref int at, int depth)
    {
        if (depth > 32) throw new InvalidDataException("Too deep.");
        var node = new Dictionary<string, object>(StringComparer.Ordinal);
        while (true)
        {
            if (at >= b.Length)
            {
                // Only the file's top level may end without its closing 0x08; inside a node that is a cut-off write.
                if (depth > 0) throw new InvalidDataException("Cut off inside a node.");
                return node;
            }
            var type = b[at++];
            if (type is 0x08 or 0x0B) return node;
            var name = Text(b, ref at);
            switch (type)
            {
                case 0x00: node[name] = Node(b, ref at, depth + 1); break;
                case 0x01: node[name] = Text(b, ref at); break;
                case 0x02: node[name] = BitConverter.ToInt32(b, at); at += 4; break;
                case 0x03: node[name] = BitConverter.ToSingle(b, at); at += 4; break;
                case 0x07 or 0x0A: node[name] = BitConverter.ToInt64(b, at); at += 8; break;
                default: throw new InvalidDataException($"Type {type}.");
            }
        }
    }

    private static string Text(byte[] b, ref int at)
    {
        var end = Array.IndexOf(b, (byte)0, at);
        if (end < 0) throw new InvalidDataException("Unended name.");
        var text = Encoding.UTF8.GetString(b, at, end - at);
        at = end + 1;
        return text;
    }

    internal static Dictionary<string, object>? Child(this Dictionary<string, object>? node, string name) =>
        node is not null && node.TryGetValue(name, out var value) ? value as Dictionary<string, object> : null;

    internal static long? Number(this Dictionary<string, object>? node, string name) =>
        node is not null && node.TryGetValue(name, out var value) ? value switch { int i => i, long l => l, string s when long.TryParse(s, out var p) => p, _ => null } : null;

    internal static string? Str(this Dictionary<string, object>? node, string name) =>
        node is not null && node.TryGetValue(name, out var value) ? value as string : null;
}
