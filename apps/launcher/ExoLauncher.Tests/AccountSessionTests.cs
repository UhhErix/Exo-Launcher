using ExoLauncher.Helpers;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The account token on this PC and what must never see it. What would go wrong silently:
/// - the token readable in a file a backup copies, or as plain text on disk;
/// - a damaged or foreign token file read as signed in, or deleted, which would drop the user's local data with it;
/// - the device id changing on restart, so every start shows as a new PC;
/// - a token, a recovery code or an email reaching app.log through the scrubber.
/// </summary>
public sealed class AccountSessionTests
{
    private static string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-account-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly AccountSession Session = new(
        Token: "Qm9vdH" + "N0cmFw" + "VG9rZW" + "4tZXhh" + "bXBsZU" + "FBQUFB" + "QUFBQU" + "FBQUFB" + "QUFBQQ",
        AccountId: "16cf69c1057724d84048297a36713071",
        Email: "person@example.com");

    [Fact]
    public void TheSavedSessionReadsBackOnTheNextStart()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, Session);

        Assert.Equal(Session, AccountSessionStore.Load(dir));
    }

    [Fact]
    public void TheTokenAndEmailAreNotInTheFileInPlainText()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, Session);

        var bytes = File.ReadAllBytes(AccountSessionStore.PathIn(dir));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(Session.Token, text);
        Assert.DoesNotContain(Session.Email, text);
    }

    [Fact]
    public void ADamagedFileReadsAsSignedOutAndIsKept()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, Session);
        var path = AccountSessionStore.PathIn(dir);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        Assert.Null(AccountSessionStore.Load(dir));
        Assert.True(File.Exists(path), "a damaged token file is left in place, so local data is never touched over it");
    }

    [Fact]
    public void AFileThatIsNotProtectedReadsAsSignedOut()
    {
        var dir = FreshDir();
        File.WriteAllText(AccountSessionStore.PathIn(dir), "{\"token\":\"plain\",\"accountId\":\"x\",\"email\":\"y\"}");

        Assert.Null(AccountSessionStore.Load(dir));
    }

    [Fact]
    public void NoFileIsSignedOut()
    {
        Assert.Null(AccountSessionStore.Load(FreshDir()));
    }

    [Fact]
    public void ClearingRemovesTheTokenOnly()
    {
        var dir = FreshDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        AccountSessionStore.Save(dir, Session);

        AccountSessionStore.Clear(dir);

        Assert.Null(AccountSessionStore.Load(dir));
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void TheDeviceIdIsThirtyTwoLowerHexAndSurvivesARestart()
    {
        var dir = FreshDir();
        var first = AccountDevice.Id(dir);
        var second = AccountDevice.Id(dir);

        Assert.True(AccountDevice.IsValid(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void ADeviceIdThatIsNotHexIsReplaced()
    {
        var dir = FreshDir();
        File.WriteAllText(Path.Combine(dir, "device-id.txt"), "NOT-A-DEVICE");

        var id = AccountDevice.Id(dir);

        Assert.True(AccountDevice.IsValid(id));
        Assert.NotEqual("NOT-A-DEVICE", id);
    }

    [Theory]
    [InlineData("Authorization: Bearer Qm9vdH" + "N0cmFw" + "VG9rZW" + "4tZXhh" + "bXBsZU" + "FBQUFB" + "QUFBQU" + "FBQUFB" + "QUFBQQ")]
    [InlineData("account token=Qm9vdH" + "N0cmFw" + "VG9rZW" + "4tZXhh" + "bXBsZU" + "FBQUFB" + "QUFBQU" + "FBQUFB" + "QUFBQQ done")]
    [InlineData("reply {\"token\":\"Qm9vdH" + "N0cmFw" + "VG9rZW" + "4tZXhh" + "bXBsZU" + "FBQUFB" + "QUFBQU" + "FBQUFB" + "QUFBQQ\"}")]
    [InlineData("unlabeled Qm9vdH" + "N0cmFw" + "VG9rZW" + "4tZXhh" + "bXBsZU" + "FBQUFB" + "QUFBQU" + "FBQUFB" + "QUFBQQ after")]
    public void ASessionTokenIsRedactedWhereverItAppears(string line)
    {
        var redacted = AppLog.Redact(line);

        Assert.DoesNotContain("Qm9vdHN0cmFwVG9rZW4", redacted);
    }

    [Theory]
    [InlineData("created {\"recoveryCode\":\"K7Q2-9TMA-XB4R-LZ8P-WH3D-6NVC\"}")]
    [InlineData("recovery code: K7Q2-9TMA-XB4R-LZ8P-WH3D-6NVC")]
    public void ARecoveryCodeIsRedacted(string line)
    {
        var redacted = AppLog.Redact(line);

        Assert.DoesNotContain("K7Q2-9TMA", redacted);
    }
}
