using System.IO.Compression;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The zip pre-check that runs before ZipFile opens a DLSS Swapper archive (DlssSwapService.SwapperCatalog.cs), and the catalog rule that
/// every archive carries its SHA-256. The pre-check reads the end-of-central-directory record and walks the central directory itself, so
/// the counts and offsets a crafted zip states are checked against what is in the file. Ways this could fail: a multi-disk record, counts
/// that disagree, a directory that does not end where the record starts, a header that is not where the record says, more headers than
/// the record says or than 1,000, and zip64 markers all reach ZipFile; a catalog entry without a hash is trusted. Every case is a real zip
/// with one field of its record changed, so it fails only for the reason named.
/// </summary>
public sealed class DlssSwapperZipShapeTests
{
    private static string TempZip(int entries = 2, string? comment = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-zipshape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "archive.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i < entries; i++) zip.CreateEntry($"f{i}.txt");
            if (comment is not null) zip.Comment = comment;
        }
        return path;
    }

    private static void CleanUp(string path) => DeletionGate.DeleteDirectory(Path.GetDirectoryName(path)!, recursive: true);

    /// <summary>The record's offset in the file (the last 22 bytes when there is no comment).</summary>
    private static int RecordAt(byte[] bytes, string? comment) => bytes.Length - 22 - (comment?.Length ?? 0);

    private static void Set16(byte[] bytes, int at, ushort value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
    private static void Set32(byte[] bytes, int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);

    [Fact]
    public void AnUnchangedZipHasAConsistentShape()
    {
        var path = TempZip();
        try
        {
            Assert.Null(DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void ACommentedZipWhoseRecordEndsTheFileIsConsistent()
    {
        var path = TempZip(comment: "hello");
        try
        {
            Assert.Null(DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Theory]
    [InlineData(4, 1, "it spans disks")]        // this disk number
    [InlineData(6, 1, "it spans disks")]        // the central directory's disk number
    [InlineData(8, 1, "its entry counts differ")] // entries on this disk, against the total
    [InlineData(10, 1, "its entry counts differ")] // the total is then the entries on disk plus this one; the walk also disagrees
    public void ACraftedRecordWithAChangedCountOrDiskIsRefused(int field, ushort value, string reason)
    {
        var path = TempZip(entries: 2);
        try
        {
            var bytes = File.ReadAllBytes(path);
            Set16(bytes, RecordAt(bytes, null) + field, value);
            File.WriteAllBytes(path, bytes);

            var refusal = DlssSwapService.ZipShapeRefusal(path);
            Assert.NotNull(refusal);
            Assert.Contains(reason, refusal);
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void ACraftedRecordWithZip64MarkersIsRefused()
    {
        var path = TempZip();
        try
        {
            var bytes = File.ReadAllBytes(path);
            Set16(bytes, RecordAt(bytes, null) + 10, 0xFFFF);
            File.WriteAllBytes(path, bytes);

            Assert.Equal("it is zip64", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void ACentralDirectoryThatDoesNotEndWhereTheRecordStartsIsRefused()
    {
        var path = TempZip();
        try
        {
            var bytes = File.ReadAllBytes(path);
            var record = RecordAt(bytes, null);
            // The directory's size claims four bytes more than it has.
            Set32(bytes, record + 12, BitConverter.ToUInt32(bytes, record + 12) + 4);
            File.WriteAllBytes(path, bytes);

            Assert.Equal("its central directory does not end where its record starts", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void ACommentLengthThatDoesNotEndTheFileIsRefused()
    {
        var path = TempZip();
        try
        {
            var bytes = File.ReadAllBytes(path);
            Set16(bytes, RecordAt(bytes, null) + 20, 5);
            File.WriteAllBytes(path, bytes);

            Assert.Equal("its comment length does not end the file", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void ACentralDirectoryHeaderNotWhereItsRecordSaysIsRefused()
    {
        var path = TempZip();
        try
        {
            var bytes = File.ReadAllBytes(path);
            var record = RecordAt(bytes, null);
            var offset = (int)BitConverter.ToUInt32(bytes, record + 16);
            bytes[offset] = 0; // the first header's signature
            File.WriteAllBytes(path, bytes);

            Assert.Equal("a central directory header is not where its record says", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void AHeaderCountThatIsNotTheRecordsTotalIsRefused()
    {
        var path = TempZip(entries: 2);
        try
        {
            var bytes = File.ReadAllBytes(path);
            var record = RecordAt(bytes, null);
            // One header in the directory, but the record says two entries spread over one... the record is changed to one entry:
            // the walk then finds two headers where the record says one.
            Set16(bytes, record + 8, 1);
            Set16(bytes, record + 10, 1);
            File.WriteAllBytes(path, bytes);

            Assert.Equal("it has 2 central directory headers, its record says 1", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void AZipWithMoreThanOneThousandEntriesIsRefusedByItsRecord()
    {
        var path = TempZip(entries: 1001);
        try
        {
            Assert.Equal("it has 1001 entries, more than 1000", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void AWalkOfMoreThanOneThousandHeadersStopsEvenWhenTheRecordIsSmall()
    {
        var path = TempZip(entries: 1001);
        try
        {
            var bytes = File.ReadAllBytes(path);
            var record = RecordAt(bytes, null);
            // The record claims one entry, so the record-level check passes; the walk stops at 1,000 headers.
            Set16(bytes, record + 8, 1);
            Set16(bytes, record + 10, 1);
            File.WriteAllBytes(path, bytes);

            Assert.Equal("it has more than 1000 central directory headers", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void ACatalogEntryWithoutItsHashIsDropped()
    {
        const string json = "{\"schema\":2,\"archives\":[{\"file\":\"nvngx_dlss.dll\",\"version\":\"310.9.17.0\",\"dllSize\":9," +
            "\"zip\":{\"url\":\"https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/x.zip\",\"size\":4}}]}";

        Assert.Empty(DlssSwapService.ParseSwapperCatalog(json));
    }
}
