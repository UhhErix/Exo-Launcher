using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The line a partial junk clean shows ("Deleted 1.2 GB; 3 files kept: in use by X"). Ways it fails: a partial clean shows
/// no reason (the holder message was dropped when some files went); a clean with nothing kept says files stayed.
/// </summary>
public sealed class JunkKeptNoteTests
{
    [Fact]
    public void NothingKeptHasNoNote() =>
        Assert.Null(WebHostBridge.KeptNote(0, "Those files are in use by X."));

    [Fact]
    public void APartialCleanNamesTheHolder() =>
        Assert.Equal("3 files kept. Those files are in use by X.", WebHostBridge.KeptNote(3, "Those files are in use by X."));

    [Fact]
    public void OneFileIsSingular() =>
        Assert.Equal("1 file kept.", WebHostBridge.KeptNote(1, null));
}
