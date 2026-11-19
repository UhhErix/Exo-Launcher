using ExoLauncher.Helpers;
namespace ExoLauncher.Tests;

/// <summary>
/// Ways a crash report could name a person, a Discord server or a game the service cannot recognise: a Discord id, a
/// Discord name, a library title, an account name left in the excerpt. ReportNames is static, so every test starts and
/// ends with an empty registry, and the class runs in its own collection so no other test sees its names.
/// </summary>
[Collection("ReportNames")]
public sealed class ReportNamesTests : IDisposable
{
    private const string Profile = @"C:\Users\Nobody";

    public ReportNamesTests() => ReportNames.Clear();

    public void Dispose() => ReportNames.Clear();

    private static string Scrub(string text) => CrashReport.Scrub(text, "nobody", Profile, "NOMACHINE");

    [Fact]
    public void ADiscordIdIsRemoved()
    {
        // Snowflakes are 17 to 19 digits; the generic long-number rule takes every one of them out, whatever the length.
        var excerpt = Scrub("WARN Discord: user 123456789012345678 and 80351110224678912 and 1234567890123456789 joined");

        Assert.DoesNotContain("123456789012345678", excerpt);
        Assert.DoesNotContain("80351110224678912", excerpt);
        Assert.DoesNotContain("1234567890123456789", excerpt);
        Assert.Contains("<id>", excerpt);
    }

    [Fact]
    public void ADiscordUserNameIsRemoved()
    {
        // The gateway registers a user's global name and username when it reads the user (DiscordCache.ReadUser).
        ReportNames.Add("Quillbeak", "quillbeak_9");

        var excerpt = Scrub("INFO Discord: Quillbeak (quillbeak_9) sent a message in #general");

        Assert.DoesNotContain("Quillbeak", excerpt);
        Assert.DoesNotContain("quillbeak_9", excerpt);
        Assert.Contains("<name>", excerpt);
    }

    [Fact]
    public void ALibraryGameTitleIsRemovedFromAnExcerpt()
    {
        ReportNames.Add("Starfall Tactics");

        var excerpt = Scrub("[2026-11-05T19:42:10.1230000Z] WARN Launch: Starfall Tactics did not start in time");

        Assert.DoesNotContain("Starfall Tactics", excerpt);
        Assert.Contains("<name>", excerpt);
    }

    [Fact]
    public void AnAccountNameIsRemoved()
    {
        // A store account's login or persona name, as SteamLoginUsers and EpicAccounts read it.
        ReportNames.Add("tallgrove88");

        var excerpt = Scrub("INFO Settings: signed in to Steam as tallgrove88");

        Assert.DoesNotContain("tallgrove88", excerpt);
        Assert.Contains("<name>", excerpt);
    }

    [Fact]
    public void ALongerTitleWinsOverAShorterOneItStartsWith()
    {
        ReportNames.Add("Starfall", "Starfall Tactics");

        var excerpt = Scrub("Starfall Tactics crashed; Starfall saved");

        Assert.Equal("<name> crashed; <name> saved", excerpt);
        Assert.Equal("Starfall Tactics", ReportNames.Snapshot()[0]);
    }

    [Fact]
    public void ANameShorterThanThreeCharactersIsNotRegistered()
    {
        ReportNames.Add("ab", " x ", "", "abc");

        Assert.Equal(new[] { "abc" }, ReportNames.Snapshot());
        Assert.Equal("ab <name> x", Scrub("ab abc x"));
    }

    [Fact]
    public void ExistingRulesStillRemoveWhatTheyDid()
    {
        // Paths, URLs, emails, addresses, GUIDs and long numbers are scrubbed exactly as before the registry existed.
        // A path runs to the end of its line, so it sits on a line of its own.
        var excerpt = Scrub("see https://a.example.com/p?token=1 and mail pat@contoso.org from 10.0.0.12\nid 3f2504e0-4f89-11d3-9a0c-0305e82c3301 n 12345678901\n" + @"opened C:\Users\Erix\file.txt");

        Assert.Contains("<path>", excerpt);
        Assert.Contains("<url>", excerpt);
        Assert.Contains("[redacted-email]", excerpt);
        Assert.Contains("<address>", excerpt);
        Assert.Contains("<id>", excerpt);
        Assert.DoesNotContain("pat@contoso.org", excerpt);
        Assert.DoesNotContain("12345678901", excerpt);
    }

    [Fact]
    public void ClearForgetsEveryName()
    {
        ReportNames.Add("Quillbeak");
        ReportNames.Clear();

        Assert.Empty(ReportNames.Snapshot());
        Assert.Equal("Quillbeak", Scrub("Quillbeak"));
    }
}
