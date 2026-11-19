using System.IO.Compression;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a game's zip could go wrong silently:
/// - a zip with a console game's file inside (a ROM or disc image) or an entry reaching outside its folder is accepted;
/// - an update loses what the game made beside itself (saves, settings, mods).
/// </summary>
public sealed class GameArchiveTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("exo-archive-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    private string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    [Fact]
    public void Clean_RefusesGameFilesAndEscapes()
    {
        Assert.True(GameArchive.Clean(Zip(("Starship.exe", "x"), ("assets/a.txt", "x"))));
        Assert.False(GameArchive.Clean(Zip(("Starship.exe", "x"), ("baserom.z64", "rom"))));
        Assert.False(GameArchive.Clean(Zip(("../evil.exe", "x"))));
    }

    [Theory]
    [InlineData("a/b.txt", true)]
    [InlineData("../b.txt", false)]
    [InlineData("a/../../b.txt", false)]
    [InlineData("C:/Windows/b.txt", false)]
    [InlineData("/b.txt", false)]
    [InlineData("a/.../b.txt", false)]
    public void SafeEntry_KeepsEveryPathInside(string name, bool safe) => Assert.Equal(safe, GameArchive.SafeEntry(name));

    [Fact]
    public void Unpack_AndKeepMade_CarryThePortsOwnFilesAcrossAnUpdate()
    {
        var old = Path.Combine(_folder, "old");
        GameArchive.Unpack(Zip(("Starship.exe", "v1"), ("starship.o2r", "port v1")), old);
        File.WriteAllText(Path.Combine(old, "sf64.o2r"), "made from the player's copy");
        Directory.CreateDirectory(Path.Combine(old, "saves"));
        File.WriteAllText(Path.Combine(old, "saves", "slot1.sav"), "save");

        var fresh = Path.Combine(_folder, "fresh");
        GameArchive.Unpack(Zip(("Starship.exe", "v2"), ("starship.o2r", "port v2")), fresh);
        GameArchive.KeepMade(old, fresh);

        Assert.Equal("v2", File.ReadAllText(Path.Combine(fresh, "Starship.exe")));
        Assert.Equal("port v2", File.ReadAllText(Path.Combine(fresh, "starship.o2r")));
        Assert.Equal("made from the player's copy", File.ReadAllText(Path.Combine(fresh, "sf64.o2r")));
        Assert.Equal("save", File.ReadAllText(Path.Combine(fresh, "saves", "slot1.sav")));
    }
}
