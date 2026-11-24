using System.Net;
using System.Text;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

public sealed class ExoAccountPasskeyTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, body);
        }
    }

    [Fact]
    public void WindowsWebAuthn_probe_reports_os_support()
    {
        var avail = WindowsWebAuthn.Probe();
        Assert.True(avail.Supported);
        Assert.True(avail.ApiVersion >= 1);
    }

    [Fact]
    public async Task Client_sends_passkey_requests_including_reauth_password_and_reads_flagged()
    {
        var seen = new List<(string Method, string Path, string? Auth, string? Body)>();
        using var handler = new StubHandler((req, body) =>
        {
            seen.Add((req.Method.Method, req.RequestUri!.AbsolutePath, req.Headers.Authorization?.ToString(), body));
            var json = req.RequestUri.AbsolutePath switch
            {
                "/v1/passkeys" => """{"passkeys":[{"id":"cred-1","label":"Desktop","created":1700000000,"lastUsed":1700000050,"flagged":true}]}""",
                "/v1/passkeys/register/start" => """{"challenge":"ch-1","rp":{"id":"accounts.getexo.app","name":"Exo Account"},"origin":"https://accounts.getexo.app","user":{"id":"dXNlcg"}}""",
                "/v1/passkeys/register/finish" => """{"ok":true,"id":"cred-1","label":"Desktop"}""",
                "/v1/passkeys/signin/start" => """{"challenge":"ch-2","rpId":"accounts.getexo.app","origin":"https://accounts.getexo.app","allowCredentials":["cred-1"]}""",
                "/v1/passkeys/signin/finish" => """{"token":"tok-1","account":{"id":"acc-1","email":"a@b.test"},"deviceToken":"dev-1"}""",
                _ => """{"ok":true}""",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        using var client = new ExoAccountClient(handler, new Uri("https://accounts.getexo.app/"));

        var list = await client.ListPasskeysAsync("tok-bearer");
        Assert.True(list.Ok);
        Assert.Equal("cred-1", list.Body?["passkeys"]?[0]?["id"]?.GetValue<string>());
        Assert.True(list.Body?["passkeys"]?[0]?["flagged"]?.GetValue<bool>());

        var regStart = await client.PasskeyRegisterStartAsync("tok-bearer", "correct-horse-battery", "dev-tok");
        Assert.True(regStart.Ok);
        Assert.Equal("ch-1", regStart.Body?["challenge"]?.GetValue<string>());

        var regFin = await client.PasskeyRegisterFinishAsync("tok-bearer", "ch-1", "cdj", "att", "Desktop");
        Assert.True(regFin.Ok);

        var signStart = await client.PasskeySignInStartAsync("a@b.test");
        Assert.True(signStart.Ok);
        Assert.Equal("ch-2", signStart.Body?["challenge"]?.GetValue<string>());

        var signFin = await client.PasskeySignInFinishAsync("ch-2", "dev-a", "cred-1", "cdj", "auth", "sig");
        Assert.True(signFin.Ok);
        var signIn = ExoAccountClient.ReadSignIn(signFin);
        Assert.NotNull(signIn);
        Assert.Equal("tok-1", signIn!.Token);
        Assert.Equal("acc-1", signIn.AccountId);

        var del = await client.DeletePasskeyAsync("tok-bearer", "cred-1");
        Assert.True(del.Ok);

        Assert.Equal(6, seen.Count);
        Assert.Equal(("GET", "/v1/passkeys"), (seen[0].Method, seen[0].Path));
        Assert.Equal("Bearer tok-bearer", seen[0].Auth);
        Assert.Equal(("POST", "/v1/passkeys/register/start"), (seen[1].Method, seen[1].Path));
        Assert.Contains("correct-horse-battery", seen[1].Body);
        Assert.Contains("dev-tok", seen[1].Body);
        Assert.Equal(("POST", "/v1/passkeys/register/finish"), (seen[2].Method, seen[2].Path));
        Assert.Equal(("POST", "/v1/passkeys/signin/start"), (seen[3].Method, seen[3].Path));
        Assert.Equal(("POST", "/v1/passkeys/signin/finish"), (seen[4].Method, seen[4].Path));
        Assert.Equal(("DELETE", "/v1/passkeys/cred-1"), (seen[5].Method, seen[5].Path));
    }

    [Fact]
    public void AccountMessages_covers_all_passkey_security_codes()
    {
        foreach (var code in new[] { "passkey-uv-required", "reauth-required", "last-sign-in-method", "passkey-replay", "passkey-limit", "passkey-exists" })
        {
            Assert.NotEqual(AccountMessages.Generic, AccountMessages.For(code));
        }
    }

    [Fact]
    public void NativeAuthProviders_marks_unconfigured_as_unavailable_and_rejects_placeholders()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "exo-auth-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var empty = NativeAuthProviders.LoadStatuses(tempDir, _ => null);
            Assert.NotEmpty(empty);
            Assert.All(empty, p =>
            {
                Assert.False(p.Available);
                Assert.Contains("Unavailable", p.Note);
            });

            File.WriteAllText(
                Path.Combine(tempDir, NativeAuthProviders.ConfigFileName),
                """{"providers":{"spotify":"sp_official_123456","epic":"PLACEHOLDER_ID_12345","microsoft":"your_client_id_here"}}""");

            var configured = NativeAuthProviders.LoadStatuses(tempDir, key => key == "EXO_OAUTH_TWITCH_CLIENT_ID" ? "tw_official_876543" : null);
            var byId = configured.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);

            Assert.True(byId["spotify"].Available);
            Assert.True(byId["twitch"].Available);
            Assert.False(byId["epic"].Available);
            Assert.False(byId["microsoft"].Available);
        }
        finally
        {
            DeletionGate.DeleteDirectory(tempDir, recursive: true);
        }
    }
}
