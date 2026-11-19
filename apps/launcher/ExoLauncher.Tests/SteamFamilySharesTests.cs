using ExoLauncher.Adapters;
using SharedApp = ExoLauncher.Adapters.SteamFamilyLibrary.SharedApp;

namespace ExoLauncher.Tests;

/// <summary>
/// The family games Steam last listed, kept per account for while Steam is closed (Exo closes it when idle: 107 of 217
/// games left the library). Ways it could fail: another account's family shows; a share Steam stopped listing stays;
/// a game's name or owners are lost on the way to disk; the file written before 3.26.0 (ids only) no longer reads;
/// a damaged file throws or invents ids or owners; a scan that started for one account writes its family under another
/// one signed in meanwhile.
/// </summary>
public sealed class SteamFamilySharesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-family-" + Guid.NewGuid().ToString("N"));
    private readonly string _was = SteamFamilyShares.FilePath;

    public SteamFamilySharesTests() => SteamFamilyShares.FilePath = Path.Combine(_dir, "steam-family.json");

    private static Dictionary<string, SharedApp> Apps(params SharedApp[] apps) =>
        apps.ToDictionary(app => app.AppId, StringComparer.Ordinal);

    [Fact]
    public void EachAccountKeepsItsOwnFamilyListUntilSteamListsItAgain()
    {
        SteamFamilyShares.Remember("steam:111", Apps(new SharedApp("2767030", "Marvel Rivals", ["200"]), new("1245620", null, [])), () => "steam:111");
        SteamFamilyShares.Remember("steam:222", Apps(new SharedApp("730", null, [])), () => "steam:222");
        Assert.Equal(["1245620", "2767030"], SteamFamilyShares.Recall("steam:111")!.Keys.Order());
        Assert.Equal(["730"], SteamFamilyShares.Recall("steam:222")!.Keys);
        Assert.Null(SteamFamilyShares.Recall("steam:333"));

        var rivals = SteamFamilyShares.Recall("steam:111")!["2767030"];
        Assert.Equal("Marvel Rivals", rivals.Name);
        Assert.Equal(["200"], rivals.OwnerAccountIds);
        Assert.Null(SteamFamilyShares.Recall("steam:111")!["1245620"].Name);

        SteamFamilyShares.Remember("steam:111", Apps(new SharedApp("2767030", "Marvel Rivals", ["200"])), () => "steam:111");   // one share stopped
        Assert.Equal(["2767030"], SteamFamilyShares.Recall("steam:111")!.Keys);
        SteamFamilyShares.Remember("steam:111", Apps(), () => "steam:111");                                            // left the family
        Assert.Empty(SteamFamilyShares.Recall("steam:111")!);
    }

    [Fact]
    public void AFamilyIsNotKeptWhenAnotherAccountSignedInDuringTheScan()
    {
        SteamFamilyShares.Remember("steam:222", Apps(new SharedApp("730", null, [])), () => "steam:222");

        // The scan started for 111; by the time it is written, 222 is signed in.
        Assert.False(SteamFamilyShares.Remember("steam:111", Apps(new SharedApp("2767030", "Marvel Rivals", ["200"])), () => "steam:222"));
        Assert.False(SteamFamilyShares.Remember("steam:111", Apps(new SharedApp("2767030", "Marvel Rivals", ["200"])), () => null));
        Assert.False(SteamFamilyShares.Remember("steam:111", Apps(new SharedApp("2767030", "Marvel Rivals", ["200"])), () => throw new IOException()));

        Assert.Null(SteamFamilyShares.Recall("steam:111"));
        Assert.Equal(["730"], SteamFamilyShares.Recall("steam:222")!.Keys);   // 222's own list is untouched
        Assert.True(SteamFamilyShares.Remember("steam:111", Apps(new SharedApp("2767030", "Marvel Rivals", ["200"])), () => "steam:111"));
        Assert.Equal(["2767030"], SteamFamilyShares.Recall("steam:111")!.Keys);
    }

    [Fact]
    public void TheIdsOnlyFileFromBefore326StillReads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SteamFamilyShares.FilePath, """{"steam:111":["730","570"]}""");

        var kept = SteamFamilyShares.Recall("steam:111")!;

        Assert.Equal(["570", "730"], kept.Keys.Order());
        Assert.All(kept.Values, app => Assert.True(app.Name is null && app.OwnerAccountIds.Count == 0));
    }

    [Fact]
    public void ADamagedFileReadsAsNothingKept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SteamFamilyShares.FilePath, "{ not json");
        Assert.Null(SteamFamilyShares.Recall("steam:111"));
        File.WriteAllText(SteamFamilyShares.FilePath, """{"steam:111":["730","../x","12a"]}""");
        Assert.Equal(["730"], SteamFamilyShares.Recall("steam:111")!.Keys);
        File.WriteAllText(SteamFamilyShares.FilePath, """{"steam:111":{"730":{"Name":"CS2","Owners":["200","x/1",""]},"../x":{}},"steam:222":5}""");
        var kept = SteamFamilyShares.Recall("steam:111")!;
        Assert.Equal(["730"], kept.Keys);
        Assert.Equal(["200"], kept["730"].OwnerAccountIds);
        Assert.Null(SteamFamilyShares.Recall("steam:222"));
    }

    public void Dispose()
    {
        SteamFamilyShares.FilePath = _was;
        try { DeletionGate.DeleteDirectory(_dir, true); } catch (IOException) { }
    }
}
