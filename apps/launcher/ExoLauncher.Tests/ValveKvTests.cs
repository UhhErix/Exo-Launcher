using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam's binary KeyValues (its appcache stats) can mislead achievements:
/// - a file cut off inside a node reads as a shorter, complete-looking node (the doc says a cut-off write is null);
/// - a cut-off name, string or number reads as nothing instead of failing;
/// - a well-formed file, read in full, stops reading early or is refused.
/// </summary>
public sealed class ValveKvTests
{
    private static byte[] Bytes(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] Open(string name) => [0x00, .. Encoding.UTF8.GetBytes(name), 0x00];

    private static byte[] Text(string name, string value) => [0x01, .. Encoding.UTF8.GetBytes(name), 0x00, .. Encoding.UTF8.GetBytes(value), 0x00];

    private static readonly byte[] Close = [0x08];

    [Fact]
    public void AWellFormedFileReadsInFull()
    {
        var root = ValveKv.Read(Bytes(Open("a"), Text("name", "x"), Close));

        Assert.NotNull(root);
        Assert.Equal("x", root.Child("a").Str("name"));
    }

    [Fact]
    public void AFileCutOffInsideANodeIsNull()
    {
        // Ends after a complete entry, before the node's closing 0x08: a cut-off write, not a shorter file.
        Assert.Null(ValveKv.Read(Bytes(Open("a"), Text("name", "x"))));
    }

    [Fact]
    public void AFileCutOffInsideADeeperNodeIsNull()
    {
        Assert.Null(ValveKv.Read(Bytes(Open("a"), Open("b"), Text("name", "x"), Close)));
    }

    [Fact]
    public void AFileCutOffInsideAStringOrNumberIsNull()
    {
        Assert.Null(ValveKv.Read(Bytes(Open("a"), [0x01, (byte)'n', 0x00, (byte)'x'])));
        Assert.Null(ValveKv.Read(Bytes(Open("a"), [0x02, (byte)'n', 0x00, 1, 2])));
    }
}
