using System.Buffers.Binary;
using System.Text;

namespace ExoLauncher.Services.SteamSignIn;

/// <summary>
/// The protobuf wire format for Steam's small sign-in and library messages: varints, fixed 32/64-bit values and
/// length-delimited bytes. Implicit presence, as Valve's proto2 file encodes it: a zero or empty field is not written,
/// so Steam reads the same bytes it would from the Steam client. Written by hand (no protobuf package); the field
/// numbers are in <see cref="SteamAuthMessages"/>, taken from Irori's proto.rs (MIT, github.com/countradooku/irori).
/// </summary>
internal sealed class SteamProtoWriter
{
    private readonly MemoryStream _buffer = new();

    public SteamProtoWriter Varint(int field, ulong value)
    {
        if (value == 0) return this;
        Key(field, 0);
        WriteVarint(value);
        return this;
    }

    public SteamProtoWriter Bool(int field, bool value) => Varint(field, value ? 1UL : 0UL);

    /// <summary>An int32: a negative value is sign-extended to ten bytes, as protobuf does.</summary>
    public SteamProtoWriter Int32(int field, int value) => Varint(field, unchecked((ulong)(long)value));

    public SteamProtoWriter Fixed64(int field, ulong value)
    {
        if (value == 0) return this;
        Key(field, 1);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        _buffer.Write(bytes);
        return this;
    }

    public SteamProtoWriter Float(int field, float value)
    {
        if (value == 0f) return this;
        Key(field, 5);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _buffer.Write(bytes);
        return this;
    }

    public SteamProtoWriter String(int field, string value) =>
        string.IsNullOrEmpty(value) ? this : Bytes(field, Encoding.UTF8.GetBytes(value));

    public SteamProtoWriter Bytes(int field, byte[] value)
    {
        if (value.Length == 0) return this;
        Key(field, 2);
        WriteVarint((ulong)value.Length);
        _buffer.Write(value);
        return this;
    }

    /// <summary>A sub-message: written whenever it is given, even when empty, since its presence is the point.</summary>
    public SteamProtoWriter Message(int field, byte[] message)
    {
        Key(field, 2);
        WriteVarint((ulong)message.Length);
        _buffer.Write(message);
        return this;
    }

    public byte[] ToArray() => _buffer.ToArray();

    private void Key(int field, int wireType) => WriteVarint((ulong)((field << 3) | wireType));

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _buffer.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _buffer.WriteByte((byte)value);
    }
}

/// <summary>
/// Reads one encoded message. Any malformed byte throws <see cref="FormatException"/>; callers turn that into "no answer"
/// rather than a partial one. Repeated fields come back in order; a missing field reads as its default.
/// </summary>
internal sealed class SteamProtoReader
{
    private readonly List<(int Field, int Wire, ulong Number, byte[] Data)> _fields = [];

    public SteamProtoReader(byte[] message)
    {
        var at = 0;
        while (at < message.Length)
        {
            var key = ReadVarint(message, ref at);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (field == 0) throw new FormatException("field 0");
            switch (wire)
            {
                case 0:
                    _fields.Add((field, wire, ReadVarint(message, ref at), []));
                    break;
                case 1:
                    Need(message, at, 8);
                    _fields.Add((field, wire, BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(at, 8)), []));
                    at += 8;
                    break;
                case 2:
                    var length = ReadVarint(message, ref at);
                    if (length > (ulong)(message.Length - at)) throw new FormatException("length");
                    _fields.Add((field, wire, 0, message.AsSpan(at, (int)length).ToArray()));
                    at += (int)length;
                    break;
                case 5:
                    Need(message, at, 4);
                    _fields.Add((field, wire, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(at, 4)), []));
                    at += 4;
                    break;
                default:
                    throw new FormatException("wire type");
            }
        }
    }

    public ulong Varint(int field) => Last(field, 0)?.Number ?? 0;

    public bool Bool(int field) => Varint(field) != 0;

    public ulong Fixed64(int field) => Last(field, 1)?.Number ?? 0;

    public float Float(int field) => Last(field, 5) is { } f ? BitConverter.Int32BitsToSingle((int)f.Number) : 0f;

    public string String(int field) => Encoding.UTF8.GetString(Bytes(field));

    public byte[] Bytes(int field) => Last(field, 2)?.Data ?? [];

    public IEnumerable<byte[]> Messages(int field) =>
        _fields.Where(f => f.Field == field && f.Wire == 2).Select(f => f.Data);

    private (int Field, int Wire, ulong Number, byte[] Data)? Last(int field, int wire)
    {
        for (var i = _fields.Count - 1; i >= 0; i--)
            if (_fields[i].Field == field && _fields[i].Wire == wire) return _fields[i];
        return null;
    }

    private static void Need(byte[] message, int at, int count)
    {
        if (count > message.Length - at) throw new FormatException("truncated");
    }

    private static ulong ReadVarint(byte[] message, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (at >= message.Length) throw new FormatException("truncated varint");
            var b = message[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new FormatException("varint too long");
    }
}
