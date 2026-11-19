using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// The Exo account's client side (plan §2.17), against a stand-in for the account service. What would go wrong silently:
/// - the browser's way back answered for a request that isn't the grant (a favicon, a connection opened ahead), so the
///   sign-in ends without one;
/// - a grant swapped without the PKCE verifier that matches the challenge the browser carried;
/// - the service's refusal read as a hang until the ten-minute wait runs out;
/// - account.sealed readable by another PC's keys, or left behind when this PC leaves its profile.
/// </summary>
public sealed class AccountClientTests
{
    private static readonly Uri Service = new("https://account.example/");

    private sealed class StandIn(Func<HttpRequestMessage, JsonObject?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject json) =>
        new(status) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    [Fact]
    public async Task TheBrowserComesBackToThisPcAndOnlyTheVerifierSwapsTheGrant()
    {
        string? challenge = null;
        var service = new StandIn((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/session" || body is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var verifier = body["verifier"]!.GetValue<string>();
            var matches = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == challenge;
            return matches && body["grant"]!.GetValue<string>() == "grant-1" && body["device"]!.GetValue<string>() == "d1"
                ? Json(HttpStatusCode.OK, new JsonObject { ["token"] = "t1", ["account"] = new JsonObject { ["id"] = "a1" }, ["created"] = true, ["provider"] = "google", ["email"] = "someone@example.com" })
                : Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "bad-grant" });
        });
        var client = new AccountClient(Service, ExoHttp.Create(TimeSpan.FromSeconds(10), service));
        using var browser = ExoHttp.Create(TimeSpan.FromSeconds(10));
        HttpStatusCode? favicon = null;
        string? page = null;
        Uri? opened = null;
        Task? coming = null;

        var signedIn = await client.SignInAsync("google", "d1", start =>
        {
            opened = start;
            var query = Query(start);
            challenge = query["challenge"];
            // The service has signed the person in and sends the browser back; it asks for a favicon first.
            coming = Task.Run(async () =>
            {
                favicon = (await browser.GetAsync($"http://127.0.0.1:{query["port"]}/favicon.ico")).StatusCode;
                page = await browser.GetStringAsync($"http://127.0.0.1:{query["port"]}/exo-account?grant=grant-1");
            });
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await coming!;

        Assert.Equal("https://account.example/v1/start", opened!.GetLeftPart(UriPartial.Path));
        Assert.Equal("google", Query(opened)["provider"]);
        Assert.Equal(43, challenge!.Length);
        Assert.Equal(HttpStatusCode.NotFound, favicon);
        Assert.Contains("signed in to Exo", page);
        Assert.True(signedIn.Created);
        Assert.Equal(new AccountClient.Session("t1", "a1", "google", "someone@example.com"), signedIn.Session);
    }

    [Fact]
    public async Task TheServicesRefusalComesBackAtOnce()
    {
        var client = new AccountClient(Service, ExoHttp.Create(TimeSpan.FromSeconds(10), new StandIn((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound))));
        using var browser = ExoHttp.Create(TimeSpan.FromSeconds(10));
        string? page = null;
        Task? coming = null;
        var refused = await Assert.ThrowsAsync<AccountClient.AccountException>(() => client.SignInAsync("apple", "d1", start =>
        {
            coming = Task.Run(async () => page = await browser.GetStringAsync($"http://127.0.0.1:{Query(start)["port"]}/exo-account?error=email-not-verified"));
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));
        await coming!;
        Assert.Equal("email-not-verified", refused.Code);
        Assert.Contains("didn't finish", page);
    }

    [Fact]
    public async Task AnEndedSessionReadsAsSignedOutAndAWrongCodeAsAWrongCode()
    {
        // Both are 401s from the service; only its own code tells them apart.
        var client = new AccountClient(Service, ExoHttp.Create(TimeSpan.FromSeconds(10), new StandIn((request, _) =>
            Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = request.RequestUri!.AbsolutePath == "/v1/session" ? "wrong-code" : "signed-out" }))));
        var ended = await Assert.ThrowsAsync<AccountClient.AccountException>(() =>
            client.GetRecoveryAsync(new("t1", "a1", "email", null), TestContext.Current.CancellationToken));
        Assert.Equal("signed-out", ended.Code);
        var wrong = await Assert.ThrowsAsync<AccountClient.AccountException>(() =>
            client.EmailFinishAsync("ticket", "123456", "d1", TestContext.Current.CancellationToken));
        Assert.Equal("wrong-code", wrong.Code);
    }

    [Fact]
    public void TheSessionOpensOnlyOnThisPcAndGoesWhenItLeavesItsProfile()
    {
        var store = new TestKeys.Store();
        using var mine = store.OpenOrCreate();
        var path = Path.Combine(TestDataDirectory.Root, "identity-" + Guid.NewGuid().ToString("N"));
        var folder = new IdentityFolder(path, store);
        try
        {
            Assert.Null(folder.ReadAccount());
            var session = new AccountClient.Session("t1", "a1", "apple", null);
            folder.SaveAccount(mine, session);
            Assert.Equal(session, folder.ReadAccount());

            var other = new TestKeys.Store();
            other.OpenOrCreate().Dispose();
            Assert.Null(new IdentityFolder(path, other).ReadAccount());

            folder.Forget();
            Assert.False(File.Exists(Path.Combine(path, IdentityFiles.Account)));
        }
        finally { DeletionGate.DeleteDirectory(path, recursive: true); }
    }

    [Fact]
    public void TheAccountKeepsTheCodeWithItsProfile()
    {
        var text = AccountClient.RecoveryCodeText("exo-1", "AAAA-BBBB");
        Assert.Equal("{\"code\":\"AAAA-BBBB\",\"exoId\":\"exo-1\"}", text);
        Assert.Equal(("exo-1", "AAAA-BBBB"), AccountClient.ReadRecoveryCodeText(text));
        Assert.Null(AccountClient.ReadRecoveryCodeText("AAAA-BBBB"));
    }
}
