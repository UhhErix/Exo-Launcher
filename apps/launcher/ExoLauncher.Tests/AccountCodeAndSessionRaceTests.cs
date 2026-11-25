using System.Net;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The recovery code waiting to be confirmed, the session file's lock, and the Settings actions that hear signed-out.
/// What would go wrong silently:
/// - a sign-up or reset saves the session as plainly signed in, so quitting on the code screen leaves an account whose code
///   the player never saw, and nothing ever asks again; a confirmed code is still reported as pending;
/// - a device-token refresh or another write drops the pending flag;
/// - a sign-in written while an old session is being ended is deleted with it (the check and the delete not under one lock);
/// - the page is told the session changed when the delete failed and nothing changed;
/// - a Settings action that hears signed-out keeps the dead session, or one that hears another refusal throws the live one away.
/// </summary>
[Collection("AccountSessionStore")]
public sealed class AccountCodeAndSessionRaceTests : IDisposable
{
    private const string Account = "16cf69c1057724d84048297a36713071";
    private const string Device = "0123456789abcdef0123456789abcdef";
    private readonly List<string> _tempDirs = [];

    private string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-account-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, AccountDevice.FileName), Device);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { DeletionGate.DeleteDirectory(dir, recursive: true); } catch { }
    }

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static Answer SignUpAnswer(bool withCode) => new(HttpStatusCode.Created,
        "{\"token\":\"t-new\",\"account\":{\"id\":\"" + Account + "\"},\"deviceToken\":\"dev-1\"" + (withCode ? ",\"recoveryCode\":\"ABCD-EFGH-JKMN-PQRS-TVWX-YZ01\"" : "") + "}");

    [Fact]
    public async Task ASignUpLeavesItsCodeToConfirmAcrossARestartUntilItIsTypedBack()
    {
        var dir = FreshDir();
        var outcome = await ExoAccountService.CreateAsync(dir, "person@example.com", "correct horse battery", SignUpAnswer(withCode: true));

        Assert.True(outcome.Ok);
        Assert.Equal("ABCD-EFGH-JKMN-PQRS-TVWX-YZ01", outcome.RecoveryCode);
        // Exo quits on the code screen: the file read at the next start still says the code waits.
        Assert.True(AccountSessionStore.Load(dir)?.CodePending);

        ExoAccountService.ConfirmCode(dir);
        Assert.False(AccountSessionStore.Load(dir)?.CodePending);
        Assert.Equal("t-new", AccountSessionStore.Load(dir)?.Token);
    }

    [Fact]
    public async Task ASignUpAnswerWithoutACodeStillWaitsForOneSoANewCodeIsMade()
    {
        var dir = FreshDir();
        await ExoAccountService.CreateAsync(dir, "person@example.com", "correct horse battery", SignUpAnswer(withCode: false));
        Assert.True(AccountSessionStore.Load(dir)?.CodePending);
    }

    [Fact]
    public async Task ASignInHasNoCodeToConfirmAndAResetHasOne()
    {
        var dir = FreshDir();
        await ExoAccountService.SignInAsync(dir, "person@example.com", "correct horse battery",
            new Answer(HttpStatusCode.OK, "{\"token\":\"t-in\",\"account\":{\"id\":\"" + Account + "\"},\"deviceToken\":\"dev-1\"}"));
        Assert.False(AccountSessionStore.Load(dir)?.CodePending);

        await ExoAccountSettings.ResetAsync(dir, "person@example.com", "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01", "another horse battery", SignUpAnswer(withCode: true));
        Assert.True(AccountSessionStore.Load(dir)?.CodePending);
    }

    [Fact]
    public async Task ANewCodeFromSettingsWaitsToBeConfirmedAndADeviceTokenRefreshKeepsTheFlag()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, new AccountSession("t-old", Account, "person@example.com", "dev-1"));

        var outcome = await ExoAccountSettings.NewRecoveryCodeAsync(dir, "correct horse battery",
            new Answer(HttpStatusCode.OK, "{\"recoveryCode\":\"ABCD-EFGH-JKMN-PQRS-TVWX-YZ01\",\"deviceToken\":\"dev-2\"}"));

        Assert.True(outcome.Ok);
        var saved = AccountSessionStore.Load(dir);
        Assert.True(saved?.CodePending);
        Assert.Equal("dev-2", saved?.DeviceToken);
        ExoAccountService.KeepDeviceToken(dir, new AccountReply(200, System.Text.Json.Nodes.JsonNode.Parse("{\"deviceToken\":\"dev-3\"}"), null, null));
        Assert.True(AccountSessionStore.Load(dir)?.CodePending);
        Assert.Equal("dev-3", AccountSessionStore.Load(dir)?.DeviceToken);
    }

    [Fact]
    public void ASignInThatLandsBetweenTheTokenCheckAndTheDeleteIsKept()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, new AccountSession("t-old", Account, "person@example.com"));
        Thread? signIn = null;
        using var landed = new ManualResetEventSlim();
        AccountSessionStore.BetweenCheckAndDelete = () =>
        {
            // A sign-in on another thread at the worst moment. Under the lock it waits; without it, it lands and is deleted.
            signIn = new Thread(() => { AccountSessionStore.Save(dir, new AccountSession("t-new", Account, "person@example.com")); landed.Set(); });
            signIn.Start();
            landed.Wait(TimeSpan.FromMilliseconds(300));
        };
        try
        {
            Assert.True(AccountSessionStore.EndIfCurrent(dir, "t-old"));
        }
        finally
        {
            AccountSessionStore.BetweenCheckAndDelete = null;
        }
        Assert.True(signIn!.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal("t-new", AccountSessionStore.Load(dir)?.Token);
        Assert.False(AccountSessionStore.Ended(dir), "a sign-in clears the ended note");
    }

    [Fact]
    public void ADeleteThatFailedTellsNobodyAndKeepsTheSession()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, new AccountSession("t-old", Account, "person@example.com"));
        var told = 0;
        void Count(string changed) { if (changed == dir) Interlocked.Increment(ref told); }
        AccountSessionStore.Changed += Count;
        try
        {
            using (new FileStream(AccountSessionStore.PathIn(dir), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                AccountSessionStore.Clear(dir);
                Assert.False(AccountSessionStore.EndIfCurrent(dir, "t-old"));
            }
            Assert.Equal(0, told);
            Assert.Equal("t-old", AccountSessionStore.Load(dir)?.Token);
            Assert.False(AccountSessionStore.Ended(dir));

            AccountSessionStore.Clear(dir);
            Assert.Equal(1, told);
            AccountSessionStore.Clear(dir);
            Assert.Equal(1, told);
        }
        finally
        {
            AccountSessionStore.Changed -= Count;
        }
    }

    /// <summary>The Settings actions that send this PC's session, each answered as the test says.</summary>
    public static IEnumerable<object[]> SettingsActions() => [["sessions"], ["revoke"], ["password"], ["new-code"], ["delete"], ["export"]];

    private static async Task<string?> Run(string action, string dir, HttpMessageHandler handler) => action switch
    {
        "sessions" => (await ExoAccountSettings.SessionsAsync(dir, handler)).Outcome.Error,
        "revoke" => (await ExoAccountSettings.RevokeAsync(dir, "fedcba9876543210fedcba9876543210", handler)).Error,
        "password" => (await ExoAccountSettings.ChangePasswordAsync(dir, "correct horse battery", "another horse battery", handler)).Error,
        "new-code" => (await ExoAccountSettings.NewRecoveryCodeAsync(dir, "correct horse battery", handler)).Error,
        "delete" => (await ExoAccountSettings.DeleteAsync(dir, "correct horse battery", handler)).Error,
        _ => (await ExoAccountSettings.ExportAsync(dir, DateTimeOffset.UtcNow, handler)).Error,
    };

    [Theory]
    [MemberData(nameof(SettingsActions))]
    public async Task ASettingsActionThatHearsSignedOutEndsTheSessionAndSaysWhy(string action)
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, new AccountSession("t-old", Account, "person@example.com"));

        var error = await Run(action, dir, new Answer(HttpStatusCode.Unauthorized, "{\"error\":\"signed-out\"}"));

        Assert.Equal("signed-out", error);
        Assert.Null(AccountSessionStore.Load(dir));
        Assert.True(AccountSessionStore.Ended(dir), "the sheet can say the session ended");
    }

    [Theory]
    [MemberData(nameof(SettingsActions))]
    public async Task ASettingsActionThatHearsAnotherRefusalKeepsTheSession(string action)
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, new AccountSession("t-old", Account, "person@example.com"));

        await Run(action, dir, new Answer(HttpStatusCode.Forbidden, "{\"error\":\"wrong-password\"}"));

        Assert.Equal("t-old", AccountSessionStore.Load(dir)?.Token);
        Assert.False(AccountSessionStore.Ended(dir));
    }
    /// <summary>Answers after running <paramref name="during"/>, as if the request took long enough for something else to happen.</summary>
    private sealed class During(Action during) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            during();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    [Fact]
    public async Task SignOutRemovesOnlyTheSessionItSignedOutNotASignInThatLandedDuringTheCall()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, new AccountSession("t-old", Account, "person@example.com"));

        await ExoAccountSettings.SignOutAsync(dir, new During(() => AccountSessionStore.Save(dir, new AccountSession("t-new", Account, "person@example.com"))));

        Assert.Equal("t-new", AccountSessionStore.Load(dir)?.Token);

        await ExoAccountSettings.SignOutAsync(dir, new During(() => { }));
        Assert.Null(AccountSessionStore.Load(dir));
        Assert.False(AccountSessionStore.Ended(dir), "a sign-out is not a session the service ended");
    }
}

/// <summary>The classes that use AccountSessionStore's static seams (Changed, BetweenCheckAndDelete) run one at a time.</summary>
[CollectionDefinition("AccountSessionStore", DisableParallelization = true)]
public sealed class AccountSessionStoreCollection;