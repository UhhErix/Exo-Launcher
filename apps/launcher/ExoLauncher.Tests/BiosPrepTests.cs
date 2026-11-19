using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways PC › Updates' BIOS update could go wrong silently: an older or unreleased BIOS picked over the newest release, a
/// download taken from anywhere but ASUS's CDN over https, or a file kept without ASUS's SHA-256 to check it against.
/// The answers under Fixtures/BiosUpdate are ASUS's own (GetPDBIOS).
/// </summary>
public sealed class BiosPrepTests
{
    private static string Answer(string name) => File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "BiosUpdate", name + ".json"));

    [Fact]
    public void The_newest_release_comes_with_ASUS_link_and_hash()
    {
        var file = BiosPrep.ParseFile(Answer("asus-rog-strix-b550-f-gaming-wifi-ii"));
        Assert.NotNull(file);
        Assert.Equal("3645", file.Version);
        Assert.Equal("dlcdnets.asus.com", file.Url.Host);
        Assert.Equal("FD46E796741AAEFB36114226B86D16D3412BCD2886BF2330339C1BC1D112C46F", file.Sha256);
    }

    [Theory]
    [InlineData("http://dlcdnets.asus.com/pub/x.zip", "FD46E796741AAEFB36114226B86D16D3412BCD2886BF2330339C1BC1D112C46F")]
    [InlineData("https://example.com/pub/x.zip", "FD46E796741AAEFB36114226B86D16D3412BCD2886BF2330339C1BC1D112C46F")]
    [InlineData("https://dlcdnets.asus.com/pub/x.zip", "")]
    [InlineData("https://dlcdnets.asus.com/pub/x.zip", "not-a-hash")]
    public void A_file_without_ASUS_https_link_or_a_SHA256_is_never_used(string url, string sha)
    {
        var json = $$$"""{"Result":{"Obj":[{"Name":"BIOS","Files":[{"Version":"3645","IsRelease":"1","Title":"","Description":"","DownloadUrl":{"Global":"{{{url}}}"},"sha256":"{{{sha}}}"}]}]}}""";
        Assert.Null(BiosPrep.ParseFile(json));
    }

    [Theory]
    [InlineData("ROG-STRIX-B550-F-GAMING-WIFI-II-ASUS-3645.CAP", true)]
    [InlineData("R550FGW2.CAP", true)]
    [InlineData("../x.CAP", false)]
    [InlineData("has space.CAP", false)]
    [InlineData("x.cap.exe", false)]
    public void A_capsule_is_named_as_ASUS_ships_it(string name, bool ok) =>
        Assert.Equal(ok, BiosPrep.CapsuleName(name));

    [Fact]
    public void An_unreleased_or_beta_BIOS_is_never_the_update()
    {
        const string json = """{"Result":{"Obj":[{"Name":"BIOS","Files":[{"Version":"3700","IsRelease":"0","Title":"","Description":"","DownloadUrl":{"Global":"https://dlcdnets.asus.com/a.zip"},"sha256":"FD46E796741AAEFB36114226B86D16D3412BCD2886BF2330339C1BC1D112C46F"},{"Version":"3690","IsRelease":"1","Title":"Beta","Description":"","DownloadUrl":{"Global":"https://dlcdnets.asus.com/b.zip"},"sha256":"FD46E796741AAEFB36114226B86D16D3412BCD2886BF2330339C1BC1D112C46F"},{"Version":"3645","IsRelease":"1","Title":"","Description":"","DownloadUrl":{"Global":"https://dlcdnets.asus.com/c.zip"},"sha256":"FD46E796741AAEFB36114226B86D16D3412BCD2886BF2330339C1BC1D112C46F"}]}]}}""";
        Assert.Equal("3645", BiosPrep.ParseFile(json)?.Version);
    }
}
