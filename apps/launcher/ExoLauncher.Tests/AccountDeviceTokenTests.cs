using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The device token (cloud/account/CLIENT.md sections 3 and 6) and the sync answers the engine must survive. Ways this could
/// fail: a stored device token is not sent on sign-in, so the service treats a known PC as a new one and holds it back; an
/// answer's new token is ignored, so the old one stops working; a 204 password change clears the stored one; the token is
/// written in plain text or into a backup; a slow body (408) on one item is held forever instead of retried; a 503 busy
/// ignores Retry-After; a keyring mismatch (500) reads as "can't reach Exo", which sends the screen to the offline path.
/// </summary>
public sealed class AccountDeviceTokenTests : IDisposable
{
    private const string Device = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-device-token-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public AccountDeviceTokenTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    /// <summary>Answers with one status and body, and keeps the JSON the client sent.</summary>
    private sealed class Capture(HttpStatusCode status, string? answer) : HttpMessageHandler
    {
        public JsonObject? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Sent = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(status) { Content = new StringContent(answer ?? "", Encoding.UTF8, "application/json") };
        }
    }

    private static AccountSession Session(string? deviceToken) => new("session-token", "acc-1", "someone@example.invalid", deviceToken);

    [Fact]
    public async Task TheSignInAnswerCarriesTheDeviceTokenToKeepWithTheSession()
    {
        var handler = new Capture(HttpStatusCode.OK, "{\"token\":\"t\",\"account\":{\"id\":\"acc-1\"},\"deviceToken\":\"dev-1\"}");
        using var client = new ExoAccountClient(handler);

        var signIn = ExoAccountClient.ReadSignIn(await client.SignInAsync("someone@example.invalid", "a long password", Device));

        Assert.Equal("dev-1", signIn!.DeviceToken);
    }

    [Fact]
    public async Task TheStoredDeviceTokenIsSentOnSignIn()
    {
        var handler = new Capture(HttpStatusCode.OK, "{\"token\":\"t\",\"account\":{\"id\":\"acc-1\"},\"deviceToken\":\"dev-2\"}");
        using var client = new ExoAccountClient(handler);

        await client.SignInAsync("someone@example.invalid", "a long password", Device, "dev-stored");

        Assert.Equal("dev-stored", handler.Sent!["deviceToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoDeviceTokenIsSentWhenNoneIsStored()
    {
        var handler = new Capture(HttpStatusCode.OK, "{\"token\":\"t\",\"account\":{\"id\":\"acc-1\"}}");
        using var client = new ExoAccountClient(handler);

        await client.SignInAsync("someone@example.invalid", "a long password", Device, null);

        Assert.False(handler.Sent!.ContainsKey("deviceToken"));
    }

    [Fact]
    public async Task APasswordChangeWithoutATokenAnswerKeepsTheStoredOne()
    {
        AccountSessionStore.Save(_folder, Session("dev-kept"));
        var handler = new Capture(HttpStatusCode.NoContent, null);
        using var client = new ExoAccountClient(handler);

        var reply = await client.ChangePasswordAsync("session-token", "old password here", "new password here", "dev-kept");
        ExoAccountService.KeepDeviceToken(_folder, reply);

        Assert.Equal("dev-kept", handler.Sent!["deviceToken"]!.GetValue<string>());
        Assert.Equal("dev-kept", AccountSessionStore.Load(_folder)!.DeviceToken);
    }

    [Fact]
    public async Task APasswordChangeAnswerReplacesTheStoredDeviceToken()
    {
        AccountSessionStore.Save(_folder, Session("dev-old"));
        using var client = new ExoAccountClient(new Capture(HttpStatusCode.OK, "{\"deviceToken\":\"dev-new\"}"));

        var reply = await client.ChangePasswordAsync("session-token", "old password here", "new password here", "dev-old");
        ExoAccountService.KeepDeviceToken(_folder, reply);

        var stored = AccountSessionStore.Load(_folder)!;
        Assert.Equal("dev-new", stored.DeviceToken);
        Assert.Equal("session-token", stored.Token);
    }

    [Fact]
    public void TheDeviceTokenIsProtectedOnDiskAndNotInPlainText()
    {
        const string marker = "dev-plain-marker-7f3a";
        AccountSessionStore.Save(_folder, Session(marker));

        var bytes = File.ReadAllBytes(AccountSessionStore.PathIn(_folder));

        Assert.False(ContainsRun(bytes, Encoding.UTF8.GetBytes(marker)));
        Assert.Equal(marker, AccountSessionStore.Load(_folder)!.DeviceToken);
    }

    [Fact]
    public void ASlowBodyHalvesTheBatchLikeATooBigOne()
    {
        var engine = new AccountSyncEngine(new SyncState());
        engine.Write("cover", "steam_1", "{\"source\":\"store\"}", 1, T0);
        engine.Write("cover", "steam_2", "{\"source\":\"store\"}", 1, T0);
        var batch = engine.NextPush(T0.AddSeconds(3))!;

        engine.ApplyPut(batch, new AccountReply(408, null, "slow-body", null), T0.AddSeconds(3));

        Assert.Equal(1, engine.State.BatchLimit);
        Assert.Empty(engine.State.Held);
    }

    [Fact]
    public void ALoneItemThatStalledIsRetriedNotHeld()
    {
        var engine = new AccountSyncEngine(new SyncState());
        engine.Write("cover", "steam_1", "{\"source\":\"store\"}", 1, T0);
        var batch = engine.NextPush(T0.AddSeconds(3))!;

        engine.ApplyPut(batch, new AccountReply(408, null, "slow-body", null), T0.AddSeconds(3));

        Assert.DoesNotContain("cover/steam_1", engine.State.Held);
        Assert.Contains("cover/steam_1", engine.State.Dirty);
        Assert.Equal(1, engine.State.Failures);
        Assert.Null(engine.NextPush(T0.AddSeconds(3)));
    }

    [Fact]
    public void ABusyAnswerWaitsAtLeastRetryAfter()
    {
        var engine = new AccountSyncEngine(new SyncState());
        engine.Write("cover", "steam_1", "{\"source\":\"store\"}", 1, T0);
        var batch = engine.NextPush(T0.AddSeconds(3))!;
        var now = T0.AddSeconds(3);

        engine.ApplyPut(batch, new AccountReply(503, null, "busy", 30), now);

        Assert.True(engine.State.NextAttempt >= now.AddSeconds(30));
        Assert.Contains("cover/steam_1", engine.State.Dirty);
    }

    [Fact]
    public void AKeyringMismatchSaysSomethingCalmAndIsNotTheOfflineScreen()
    {
        var outcome = ExoAccountOutcome.From(new AccountReply(500, null, "keyring-mismatch", null));

        Assert.False(outcome.Offline);
        Assert.Equal(AccountMessages.For("keyring-mismatch"), outcome.Message);
        Assert.NotEqual(AccountMessages.Generic, outcome.Message);
        Assert.Contains("Try again", outcome.Message);
        Assert.Equal("Exo's account service has a problem. Try again later.", outcome.Message);
    }

    /// <summary>True when <paramref name="needle"/> appears as one run inside <paramref name="haystack"/>.</summary>
    private static bool ContainsRun(byte[] haystack, byte[] needle) =>
        Enumerable.Range(0, Math.Max(0, haystack.Length - needle.Length + 1))
            .Any(start => haystack.AsSpan(start, needle.Length).SequenceEqual(needle));
}
