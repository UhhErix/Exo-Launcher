using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Tests;

/// <summary>
/// The hand-written protobuf used for Steam's sign-in. A wrong tag or wire type would read as a default and make a
/// sign-in look like it never started, so the bytes are pinned against values worked out by hand from the protobuf
/// spec (field 1 varint 150 is 0x08 0x96 0x01).
/// </summary>
public sealed class SteamProtoWireTests
{
    [Fact]
    public void VarintAndTagMatchTheProtobufSpec()
    {
        var bytes = new SteamProtoWriter().Varint(1, 150).ToArray();

        Assert.Equal([0x08, 0x96, 0x01], bytes);
    }

    [Fact]
    public void ZeroAndEmptyFieldsAreNotWritten()
    {
        var bytes = new SteamProtoWriter().Varint(1, 0).String(2, "").Bytes(3, []).Bool(4, false).ToArray();

        Assert.Empty(bytes);
    }

    [Fact]
    public void NegativeInt32IsSignExtendedToTenBytes()
    {
        var bytes = new SteamProtoWriter().Int32(1, -1).ToArray();

        Assert.Equal(11, bytes.Length);
        Assert.Equal(0x08, bytes[0]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }, bytes[1..]);
    }

    [Fact]
    public void RoundTripsEveryWireType()
    {
        var message = new SteamProtoWriter()
            .Varint(1, 76561198000000000)
            .String(2, "héllo")
            .Bytes(3, [1, 2, 3])
            .Fixed64(4, 0xDEADBEEFCAFEUL)
            .Float(5, 2.5f)
            .Bool(6, true)
            .ToArray();

        var r = new SteamProtoReader(message);

        Assert.Equal(76561198000000000UL, r.Varint(1));
        Assert.Equal("héllo", r.String(2));
        Assert.Equal([1, 2, 3], r.Bytes(3));
        Assert.Equal(0xDEADBEEFCAFEUL, r.Fixed64(4));
        Assert.Equal(2.5f, r.Float(5));
        Assert.True(r.Bool(6));
    }

    [Fact]
    public void RepeatedSubMessagesKeepTheirOrder()
    {
        var message = new SteamProtoWriter()
            .Message(5, new SteamProtoWriter().Varint(1, 2).ToArray())
            .Message(5, new SteamProtoWriter().Varint(1, 3).ToArray())
            .ToArray();

        var kinds = new SteamProtoReader(message).Messages(5).Select(m => new SteamProtoReader(m).Varint(1)).ToList();

        Assert.Equal([2UL, 3UL], kinds);
    }

    [Theory]
    [InlineData(new byte[] { 0x08 })]                 // varint key with no value
    [InlineData(new byte[] { 0x12, 0x05, 0x41 })]     // length 5 but only one byte left
    [InlineData(new byte[] { 0x0B })]                 // wire type 3 (group), not supported
    [InlineData(new byte[] { 0x00, 0x01 })]           // field number 0
    [InlineData(new byte[] { 0x09, 0x01, 0x02 })]     // fixed64 cut short
    public void MalformedBytesThrowInsteadOfReadingDefaults(byte[] malformed)
    {
        Assert.Throws<FormatException>(() => new SteamProtoReader(malformed));
    }
}
