using System.Net;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// A session the service ended, and the service's busy answer. What would go wrong silently:
/// - a session revoked from another PC (or ended by a password change or reset elsewhere) stays in the session file, so
///   this PC reads as signed in, the account sheet never asks, and every sync repeats the same 401 with nothing to press;
/// - clearing it drops the queue, so what this PC changed never reaches the account after the next sign-in;
/// - a sign-in made while the old session's request was on the way is wiped by the late 401;
/// - the page is not told, so the sheet stays shut until the next start;
/// - a busy answer (the service's Argon2 slots full at launch-day load) is shown as "can't reach" with Continue without an
///   account, or is retried forever, or a real outage is waited on as if it were busy.
/// </summary>
[Collection("AccountSessionStore")]
public sealed class AccountSessionEndTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-account-end-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { DeletionGate.DeleteDirectory(dir, recursive: true); } catch { }
    }

    private static AccountSession Session(string token) => new(token, "16cf69" + "c10577" + "24d840" + "48297a" + "367130" + "71", "person@example.com", "dev-1");

    /// <summary>Answers each request with the next of its replies (the last repeats), and counts them.</summary>
    private sealed class Script(params (HttpStatusCode Status, string Body, int? RetryAfter)[] replies) : HttpMessageHandler
    {
        internal int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body, retry) = replies[Math.Min(Calls++, replies.Length - 1)];
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retry is { } seconds) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        }
    }

    private static readonly (HttpStatusCode, string, int?) Busy = (HttpStatusCode.ServiceUnavailable, "{\"error\":\"busy\"}", 1);
    private static readonly (HttpStatusCode, string, int?) SignedIn = (HttpStatusCode.OK, "{\"token\":\"t-new\",\"account\":{\"id\":\"16cf69" + "c10577" + "24d840" + "48297a" + "367130" + "71\"},\"deviceToken\":\"dev-2\"}", null);

    [Fact]
    public void BusyWaitWalksEveryRow()
    {
        AccountReply Reply(int status, string? error, int? retry) => new(status, null, error, retry);
        // Not busy: never sent again, whatever the status.
        Assert.Null(ExoAccountClient.BusyWait(Reply(200, null, null), TimeSpan.Zero, 0, throttled: false));
        Assert.Null(ExoAccountClient.BusyWait(Reply(503, "not-configured", 1), TimeSpan.Zero, 0, throttled: false));
        Assert.Null(ExoAccountClient.BusyWait(Reply(500, "server-error", null), TimeSpan.Zero, 0, throttled: false));
        Assert.Null(ExoAccountClient.BusyWait(Reply(0, "network", null), TimeSpan.Zero, 0, throttled: false));
        Assert.Null(ExoAccountClient.BusyWait(Reply(429, "locked", 900), TimeSpan.Zero, 0, throttled: false));
        // Busy within the budget: its Retry-After, held to 1 to 2 seconds (none given waits 1).
        Assert.Equal(TimeSpan.FromSeconds(1), ExoAccountClient.BusyWait(Reply(503, "busy", 1), TimeSpan.Zero, 0, throttled: false));
        Assert.Equal(TimeSpan.FromSeconds(1), ExoAccountClient.BusyWait(Reply(503, "busy", null), TimeSpan.Zero, 0, throttled: false));
        Assert.Equal(TimeSpan.FromSeconds(1), ExoAccountClient.BusyWait(Reply(503, "busy", 0), TimeSpan.Zero, 0, throttled: false));
        Assert.Equal(TimeSpan.FromSeconds(2), ExoAccountClient.BusyWait(Reply(503, "busy", 60), TimeSpan.Zero, 0, throttled: false));
        Assert.Equal(TimeSpan.FromSeconds(1), ExoAccountClient.BusyWait(Reply(503, "busy", 1), ExoAccountClient.BusyBudget - TimeSpan.FromSeconds(1), 4, throttled: false));
        // Busy past the budget: the answer stands.
        Assert.Null(ExoAccountClient.BusyWait(Reply(503, "busy", 2), ExoAccountClient.BusyBudget - TimeSpan.FromSeconds(1), 4, throttled: false));
        Assert.Null(ExoAccountClient.BusyWait(Reply(503, "busy", 1), ExoAccountClient.BusyBudget, 0, throttled: false));
        // A password route: the service counts each try against its per-address limit, so busy is sent again once at most.
        Assert.Equal(TimeSpan.FromSeconds(1), ExoAccountClient.BusyWait(Reply(503, "busy", 1), TimeSpan.Zero, 0, throttled: true));
        Assert.Null(ExoAccountClient.BusyWait(Reply(503, "busy", 1), TimeSpan.FromSeconds(1), 1, throttled: true));
        Assert.Null(ExoAccountClient.BusyWait(Reply(429, "slow-down", null), TimeSpan.Zero, 0, throttled: true));
    }

    [Fact]
    public async Task ASyncBusyAnswerIsWaitedOutAndTheNextAnswerIsTheOneThatCounts()
    {
        var script = new Script(Busy, Busy, (HttpStatusCode.OK, "{\"items\":[],\"cursor\":0,\"more\":false}", null));
        var waits = new List<TimeSpan>();
        using var client = new ExoAccountClient(script, wait: (span, _) => { waits.Add(span); return Task.CompletedTask; });

        var reply = await client.PullAsync("t-old", 0);

        Assert.True(reply.Ok);
        Assert.Equal(3, script.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], waits);
    }

    [Fact]
    public async Task ASyncBusyPastTheBudgetStopsThere()
    {
        var script = new Script(Busy);
        var waited = TimeSpan.Zero;
        using var client = new ExoAccountClient(script, wait: (span, _) => { waited += span; return Task.CompletedTask; });

        var reply = await client.PullAsync("t-old", 0);

        Assert.Equal("busy", reply.Error);
        Assert.True(waited <= ExoAccountClient.BusyBudget, waited.ToString());
        Assert.Equal(6, script.Calls);
    }

    /// <summary>Every password route answers busy only after the service's 10-a-minute limit counted the try.</summary>
    public static IEnumerable<object[]> PasswordRoutes() =>
    [
        ["sign-up"], ["sign-in"], ["password"], ["new-code"], ["reset"], ["delete"],
    ];

    private static Task<AccountReply> Call(ExoAccountClient client, string route) => route switch
    {
        "sign-up" => client.CreateAccountAsync("person@example.com", "correct horse battery", "0123456789abcdef0123456789abcdef"),
        "sign-in" => client.SignInAsync("person@example.com", "correct horse battery", "0123456789abcdef0123456789abcdef"),
        "password" => client.ChangePasswordAsync("t-old", "correct horse battery", "another horse battery"),
        "new-code" => client.NewRecoveryCodeAsync("t-old", "correct horse battery"),
        "reset" => client.ResetAsync("person@example.com", "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01", "another horse battery", "0123456789abcdef0123456789abcdef"),
        _ => client.DeleteAccountAsync("t-old", "correct horse battery"),
    };

    [Theory]
    [MemberData(nameof(PasswordRoutes))]
    public async Task APasswordRouteIsSentAgainOnceAtMostSoBusyNeverSpendsTheSignInLimit(string route)
    {
        var script = new Script(Busy);
        var waits = 0;
        using var client = new ExoAccountClient(script, wait: (_, _) => { waits++; return Task.CompletedTask; });

        var outcome = ExoAccountOutcome.From(await Call(client, route));

        Assert.Equal(2, script.Calls);
        Assert.Equal(1, waits);
        Assert.False(outcome.Offline);
        Assert.Equal("busy", outcome.Error);
        Assert.Equal("Exo's account service is busy. Try again in a moment.", outcome.Message);
    }

    [Fact]
    public async Task APasswordRouteThatIsBusyOnceThenAnswersSignsIn()
    {
        var script = new Script(Busy, SignedIn);
        using var client = new ExoAccountClient(script, wait: (_, _) => Task.CompletedTask);

        Assert.True((await client.SignInAsync("person@example.com", "correct horse battery", "0123456789abcdef0123456789abcdef")).Ok);
        Assert.Equal(2, script.Calls);
    }
    [Fact]
    public async Task AnOutageIsStillOfflineAndIsNotWaitedOn()
    {
        var script = new Script((HttpStatusCode.BadGateway, "", null));
        var waits = 0;
        using var client = new ExoAccountClient(script, wait: (_, _) => { waits++; return Task.CompletedTask; });

        var outcome = ExoAccountOutcome.From(await client.SignInAsync("person@example.com", "correct horse battery", "0123456789abcdef0123456789abcdef"));

        Assert.True(outcome.Offline);
        Assert.Equal(1, script.Calls);
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task ASyncThatHearsSignedOutEndsTheSessionKeepsTheQueueAndTellsThePage()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, Session("t-old"));
        var told = 0;
        void Count(string changed) { if (changed == dir) Interlocked.Increment(ref told); }
        AccountSessionStore.Changed += Count;
        try
        {
            var host = new AccountSyncHost(dir,
                new ExoAccountClient(new Script((HttpStatusCode.Unauthorized, "{\"error\":\"signed-out\"}", null))),
                () => AccountSessionStore.Load(dir), _ => { });
            host.Write("settings", "app", "{\"ShowLibraryTools\":true}");

            await host.SyncAsync(CancellationToken.None);

            Assert.Null(AccountSessionStore.Load(dir));
            Assert.False(File.Exists(AccountSessionStore.PathIn(dir)));
            Assert.Equal(1, told);
            // The queue stays for the same account's next sign-in.
            Assert.Contains("settings/app", AccountSyncStore.Load(dir, Session("t-old").AccountId).Dirty);
            // A change made now waits for an account instead of going to the dead session's queue.
            host.Write("settings", "app", "{\"ShowLibraryTools\":false}");
            Assert.True(File.Exists(Path.Combine(dir, AccountSyncHost.PendingFile)));
        }
        finally
        {
            AccountSessionStore.Changed -= Count;
        }
    }

    [Fact]
    public void ALateSignedOutForAnOldTokenLeavesANewerSignInAlone()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, Session("t-new"));

        Assert.False(AccountSessionStore.EndIfCurrent(dir, "t-old"));
        Assert.Equal("t-new", AccountSessionStore.Load(dir)?.Token);

        Assert.True(AccountSessionStore.EndIfCurrent(dir, "t-new"));
        Assert.Null(AccountSessionStore.Load(dir));
    }

    [Fact]
    public async Task AnOutageDuringSyncDoesNotEndTheSession()
    {
        var dir = FreshDir();
        AccountSessionStore.Save(dir, Session("t-old"));
        var host = new AccountSyncHost(dir,
            new ExoAccountClient(new Script((HttpStatusCode.BadGateway, "", null))),
            () => AccountSessionStore.Load(dir), _ => { });

        await host.SyncAsync(CancellationToken.None);

        Assert.Equal("t-old", AccountSessionStore.Load(dir)?.Token);
    }
}
