namespace Exo.Identity;

/// <summary>
/// RFC 4648 base32 without padding, for what a person reads or types: the Exo ID, the recovery code, the QR's key hash.
/// Its alphabet has no 0, 1, 8 or 9, so reading accepts those for the letters they look like (O, I, B), any case, and
/// skips spaces and hyphens, so a code copied by hand in groups still reads.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new char[(data.Length * 8 + 4) / 5];
        int bits = 0, value = 0, at = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text[at++] = Alphabet[(value >> (bits - 5)) & 31];
                bits -= 5;
            }
        }
        if (bits > 0) text[at] = Alphabet[(value << (5 - bits)) & 31];
        return new string(text);
    }

    /// <summary>The bytes, or null when a character isn't base32, the length can't come from whole bytes, or the
    /// leftover bits aren't zero (so one string never reads as two different values).</summary>
    public static byte[]? Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int bits = 0, value = 0, chars = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-') continue;
            var c = char.ToUpperInvariant(raw) switch { '0' => 'O', '1' => 'I', '8' => 'B', var other => other };
            var digit = Alphabet.IndexOf(c);
            if (digit < 0) return null;
            chars++;
            value = (value << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
            value &= (1 << bits) - 1;
        }
        if (bits >= 5 || value != 0 || chars != (output.Count * 8 + 4) / 5) return null;
        return [.. output];
    }
}
