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
                "/v1/passkeys/signin/start" => """{"challenge":"ch-2","rpId":"accounts.getexo.app","origin":"https://accounts.getexo.app","allowCredentials":[]}""",
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

        var signStart = await client.PasskeySignInStartAsync();
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
        Assert.Equal("{}", seen[3].Body);
        Assert.Equal(("POST", "/v1/passkeys/signin/finish"), (seen[4].Method, seen[4].Path));
        Assert.Equal(("DELETE", "/v1/passkeys/cred-1"), (seen[5].Method, seen[5].Path));
    }

    [Fact]
    public void AccountMessages_covers_all_passkey_security_codes()
    {
        foreach (var code in new[] { "passkey-uv-required", "passkeys-unavailable", "reauth-required", "last-sign-in-method", "passkey-replay", "passkey-limit", "passkey-exists" })
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
            var empty = NativeAuthProviders.LoadStatuses(tempDir, _ => null, _ => null);
            Assert.Equal(8, empty.Count);
            Assert.All(empty, p =>
            {
                Assert.False(p.Available);
                Assert.Contains("classic sign-in", p.Note);
                Assert.DoesNotContain("EXO_OAUTH_", p.Note);
                Assert.DoesNotContain("oauth-clients.json", p.Note);
                Assert.True(NativeAuthProviders.UsesClassicFallback(p.Provider, tempDir, _ => null, _ => null));
            });

            var emptyById = empty.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("pkce-system-browser", emptyById["discord"].Flow);
            Assert.Equal("device-code", emptyById["twitch"].Flow);
            Assert.Equal("pkce-system-browser", emptyById["epic"].Flow);
            Assert.Equal("wam", emptyById["microsoft"].Flow);

            File.WriteAllText(
                Path.Combine(tempDir, NativeAuthProviders.ConfigFileName),
                """{"providers":{"spotify":"sp_official_123456","discord":"dc_json_override_1","epic":"PLACEHOLDER_ID_12345","microsoft":"your_client_id_here"}}""");

            string? Baked(string key) => key switch
            {
                "EXO_OAUTH_DISCORD_CLIENT_ID" => "dc_baked_default_0",
                "EXO_OAUTH_RIOT_CLIENT_ID" => "riot_baked_123456",
                _ => null,
            };

            string? Env(string key) => key switch
            {
                "EXO_OAUTH_TWITCH_CLIENT_ID" => "tw_official_876543",
                "EXO_OAUTH_SPOTIFY_CLIENT_ID" => "sp_env_override_99",
                _ => null,
            };

            var ids = NativeAuthProviders.LoadClientIds(tempDir, Env, Baked);
            Assert.Equal("sp_env_override_99", ids["spotify"]);
            Assert.Equal("dc_json_override_1", ids["discord"]);
            Assert.Equal("riot_baked_123456", ids["riot"]);
            Assert.Equal("tw_official_876543", ids["twitch"]);
            Assert.False(ids.ContainsKey("epic"));
            Assert.False(ids.ContainsKey("microsoft"));

            var configured = NativeAuthProviders.LoadStatuses(tempDir, Env, Baked);
            var byId = configured.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);

            Assert.True(byId["spotify"].Available);
            Assert.False(NativeAuthProviders.UsesClassicFallback("spotify", tempDir, Env, Baked));
            Assert.True(byId["discord"].Available);
            Assert.True(byId["twitch"].Available);
            Assert.True(byId["riot"].Available);
            Assert.Contains("cloud token proxy", byId["riot"].Note);
            Assert.False(byId["epic"].Available);
            Assert.True(NativeAuthProviders.UsesClassicFallback("epic", tempDir, Env, Baked));
            Assert.False(byId["microsoft"].Available);
            Assert.True(NativeAuthProviders.UsesClassicFallback("microsoft", tempDir, Env, Baked));
        }
        finally
        {
            DeletionGate.DeleteDirectory(tempDir, recursive: true);
        }
    }
}
