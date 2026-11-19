using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways log redaction can fail (security/attacks/local, attack 5, "unlabeled token"):
/// - a JWT, Discord-style or OAuth token logged with no label reaches app.log;
/// - a SHA-256 hash, a version, a URL or a path that a support log needs is redacted.
/// </summary>
public sealed class AppLogRedactTests
{
    [Theory]
    [InlineData("eyJhbG" + "ciOiJI" + "UzI1Ni" + "J9.eyJ" + "zdWIiO" + "iIxMjM" + "0NTY3O" + "DkwIn0" + ".SflKx" + "wRJSMe" + "KKF2QT" + "4fwpMe" + "Jf36PO" + "k6yJV_" + "adQssw" + "5c")]
    // A synthetic Discord-shaped token, split so secret scanners do not take it for a real one.
    [InlineData("MTA4OTk2NTYyMDM0MDM0MDAzMg" + ".GxYz12." + "abcdefGHIJKLmnopQRSTuvwxYZ0123456789ab")]
    [InlineData("BQDa8xK2fPq7Lm3NzR9vT1wY5uH6jE0cG4iO2sA8dF3kL7pM9nB1vC5xZ6qW")]
    public void UnlabeledTokensAreRedacted(string token)
    {
        var line = AppLog.Redact("reply carried " + token + " then closed");
        Assert.DoesNotContain(token, line);
        Assert.Contains("reply carried [redacted] then closed", line);
    }

    [Theory]
    [InlineData("login key=A1b2C3" + "d4E5f6 done", "A1b2C3" + "d4E5f6")]
    [InlineData("request key: s3cr3t-value", "s3cr3t-value")]
    public void ABareKeyAssignmentIsRedacted(string line, string secret)
    {
        var redacted = AppLog.Redact(line);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Theory]
    [InlineData("sha256 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 matched")]
    [InlineData("Downloaded https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe")]
    [InlineData(@"Scanned C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64")]
    [InlineData("Spotify 1.3.5.3058, Chrome/151.0.7922.138, nvngx_dlss.dll 310.9.16.0")]
    [InlineData("dlss-swapper-downloads.beeradmoore.com answered 200")]
    public void SupportDetailStaysReadable(string line) => Assert.Equal(line, AppLog.Redact(line));
}
