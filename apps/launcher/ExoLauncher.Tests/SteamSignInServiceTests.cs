using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Tests;

/// <summary>
/// The Steam sign-in against recorded answers: the QR path, the password path with Steam Guard and phone approval, a
/// refused or expired sign-in, token renewal, sign-out that clears the tokens even when Steam can't be reached, the
/// account check that keeps one account's library from landing on another, and the scrubber that keeps tokens and
/// passwords out of the log.
/// </summary>
public sealed class SteamSignInServiceTests
{
    private const ulong SteamId = 76561197960265729;
    private const string Base = "https://api.test";

    private sealed class MemoryVault : ISteamTokenVault
    {
        public string? Json { get; set; }
        public string? Read() => Json;
        public bool Write(string? json) { Json = json; return true; }
    }

    private static string Jwt(object payload)
    {
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("{\"alg\":\"none\"}"u8.ToArray());
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return header + "." + body + ".sig";
    }

    private static readonly string Refresh = Jwt(new { sub = SteamId.ToString(), exp = 2_000_000_000L, aud = new[] { "web" } });
    private static readonly string FreshAccess = Jwt(new { sub = SteamId.ToString(), exp = 1_900_000_000L });
    private static readonly string StaleAccess = Jwt(new { sub = SteamId.ToString(), exp = 1_000_000_000L });

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static (SteamWebApi Api, SteamFixtureHandler Handler) Fixture(Func<HttpRequestMessage, (HttpStatusCode, string?, byte[])> answer)
    {
        var handler = new SteamFixtureHandler(answer);
        return (new SteamWebApi(ExoHttp.Create(TimeSpan.FromSeconds(5), handler), Base), handler);
    }

    private static SteamSignInService Service(SteamWebApi api, MemoryVault vault) =>
        new(api, vault, () => Now, SteamSignInService.MachineIdFor("PC", "user"), "Exo on PC");


    private static byte[] QrChallenge() => new SteamProtoWriter().Varint(1, 42).String(2, "https://s.team/q/1/77").Bytes(3, [9]).Float(4, 5f).ToArray();

    private static byte[] Done(string access = "") =>
        new SteamProtoWriter().String(3, Refresh).String(4, access).String(6, "someone").ToArray();

    [Fact]
    public async Task QrSignInGivesTheChallengeThenSavesTheTokens()
    {
        var polls = 0;
        var (api, _) = Fixture(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("BeginAuthSessionViaQR")) return (HttpStatusCode.OK, "1", QrChallenge());
            polls++;
            return polls == 1
                ? (HttpStatusCode.OK, "1", new SteamProtoWriter().Varint(1, 42).ToArray())
                : (HttpStatusCode.OK, "1", Done(FreshAccess));
        });
        var vault = new MemoryVault();
        var service = Service(api, vault);

        var start = await service.BeginQrAsync(TestContext.Current.CancellationToken);
        var waiting = await service.PollAsync(TestContext.Current.CancellationToken);
        var done = await service.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal("waiting", start.State);
        Assert.Equal("https://s.team/q/1/77", start.ChallengeUrl);
        Assert.Equal(5, start.IntervalSeconds);
        Assert.Equal("waiting", waiting.State);
        Assert.Equal("done", done.State);
        Assert.Equal("someone", done.AccountName);
        var state = service.State();
        Assert.True(state.SignedIn);
        Assert.Equal(SteamId, state.SteamId);
        Assert.Contains(Refresh, vault.Json);
        var after = await service.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal("failed", after.State);
    }

    [Fact]
    public async Task QrSignInExpiresWhenSteamSaysSo()
    {
        var (api, _) = Fixture(request => request.RequestUri!.AbsolutePath.Contains("BeginAuthSessionViaQR")
            ? (HttpStatusCode.OK, "1", QrChallenge())
            : (HttpStatusCode.OK, "9", []));
        var service = Service(api, new MemoryVault());
        await service.BeginQrAsync(TestContext.Current.CancellationToken);

        var step = await service.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal("expired", step.State);
        Assert.Equal("failed", (await service.PollAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task PasswordSignInSendsAnEncryptedPasswordAndStopsAtSteamGuard()
    {
        using var rsa = RSA.Create(1024);
        var parameters = rsa.ExportParameters(false);
        var modulus = Convert.ToHexString(parameters.Modulus!);
        var exponent = Convert.ToHexString(parameters.Exponent!);
        var (api, handler) = Fixture(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("GetPasswordRSAPublicKey"))
                return (HttpStatusCode.OK, "1", new SteamProtoWriter().String(1, modulus).String(2, exponent).Varint(3, 1700000000).ToArray());
            if (path.Contains("BeginAuthSessionViaCredentials"))
                return (HttpStatusCode.OK, "1", new SteamProtoWriter().Varint(1, 9).Bytes(2, [5]).Float(3, 4f)
                    .Message(4, new SteamProtoWriter().Varint(1, 3).String(2, "u***@mail").ToArray()).Varint(5, SteamId).ToArray());
            return (HttpStatusCode.OK, "1", Done(FreshAccess));
        });
        var service = Service(api, new MemoryVault());

        var guard = await service.BeginPasswordAsync("someone", "hunter2-correct", TestContext.Current.CancellationToken);

        Assert.Equal("guard", guard.State);
        Assert.Equal("DeviceCode", guard.Guard);
        var begin = handler.Bodies.Single(b => b.Contains("input_protobuf_encoded"));
        Assert.DoesNotContain("hunter2", begin);
        Assert.DoesNotContain("hunter2", string.Join('\n', handler.Requests.Select(r => r.RequestUri!.ToString())));

        var afterCode = await service.SubmitGuardAsync("abcde", TestContext.Current.CancellationToken);
        Assert.Equal("waiting", afterCode.State);
        var done = await service.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal("done", done.State);
        Assert.True(service.State().SignedIn);
    }

    [Fact]
    public async Task PasswordSignInWithAPhoneApprovalAsksForApproval()
    {
        using var rsa = RSA.Create(1024);
        var parameters = rsa.ExportParameters(false);
        var (api, _) = Fixture(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("GetPasswordRSAPublicKey"))
                return (HttpStatusCode.OK, "1", new SteamProtoWriter().String(1, Convert.ToHexString(parameters.Modulus!))
                    .String(2, Convert.ToHexString(parameters.Exponent!)).Varint(3, 1).ToArray());
            return (HttpStatusCode.OK, "1", new SteamProtoWriter().Varint(1, 9).Bytes(2, [5])
                .Message(4, new SteamProtoWriter().Varint(1, 4).ToArray()).Varint(5, SteamId).ToArray());
        });

        var step = await Service(api, new MemoryVault()).BeginPasswordAsync("someone", "pw", TestContext.Current.CancellationToken);

        Assert.Equal("approve", step.State);
    }

    [Fact]
    public async Task SubmittingAGuardCodeWithoutOneAskedForFails()
    {
        var (api, _) = Fixture(_ => (HttpStatusCode.OK, "1", []));

        var step = await Service(api, new MemoryVault()).SubmitGuardAsync("abcde", TestContext.Current.CancellationToken);

        Assert.Equal("failed", step.State);
    }

    [Fact]
    public async Task CancellingDropsThePendingSignIn()
    {
        var (api, _) = Fixture(_ => (HttpStatusCode.OK, "1", QrChallenge()));
        var service = Service(api, new MemoryVault());
        await service.BeginQrAsync(TestContext.Current.CancellationToken);

        service.Cancel();

        Assert.Equal("failed", (await service.PollAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task ExpiredAccessTokenIsRenewedAndSaved()
    {
        var vault = new MemoryVault { Json = Saved(StaleAccess) };
        var (api, handler) = Fixture(_ => (HttpStatusCode.OK, "1", new SteamProtoWriter().String(1, FreshAccess).ToArray()));

        var tokens = await Service(api, vault).FreshTokensAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(tokens);
        Assert.Equal(FreshAccess, tokens.AccessToken);
        Assert.Contains(FreshAccess, vault.Json);
        Assert.Contains("GenerateAccessTokenForApp", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ValidAccessTokenIsNotRenewed()
    {
        var vault = new MemoryVault { Json = Saved(FreshAccess) };
        var (api, handler) = Fixture(_ => (HttpStatusCode.OK, "1", []));

        var tokens = await Service(api, vault).FreshTokensAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FreshAccess, tokens!.AccessToken);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RefreshRejectedSignsOutLocally()
    {
        var vault = new MemoryVault { Json = Saved(StaleAccess) };
        var (api, _) = Fixture(_ => (HttpStatusCode.OK, "9", []));

        var tokens = await Service(api, vault).FreshTokensAsync(TestContext.Current.CancellationToken);

        Assert.Null(tokens);
        Assert.Null(vault.Json);
        Assert.False(Service(api, vault).State().SignedIn);
    }

    [Fact]
    public async Task SignOutClearsTheTokensEvenWhenSteamCannotBeReached()
    {
        var vault = new MemoryVault { Json = Saved(FreshAccess) };
        var (api, handler) = Fixture(_ => (HttpStatusCode.InternalServerError, null, []));

        await Service(api, vault).SignOutAsync(TestContext.Current.CancellationToken);

        Assert.Null(vault.Json);
        Assert.Contains("RevokeToken", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Contains("access_token=", handler.Bodies.Single());
        Assert.DoesNotContain("access_token", handler.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task SignOutWithNothingSavedDoesNothingToSteam()
    {
        var vault = new MemoryVault();
        var (api, handler) = Fixture(_ => (HttpStatusCode.OK, "1", []));

        await Service(api, vault).SignOutAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Null(vault.Json);
    }

    [Fact]
    public async Task LibraryIsReadForTheSignedInAccountOnly()
    {
        var vault = new MemoryVault { Json = Saved(FreshAccess) };
        var (api, _) = Fixture(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("GetOwnedGames")) return (HttpStatusCode.OK, null, Encoding.UTF8.GetBytes("""{"response":{"games":[{"appid":570,"playtime_forever":60}]}}"""));
            if (path.Contains("GetFamilyGroupForUser")) return (HttpStatusCode.OK, null, Encoding.UTF8.GetBytes("""{"response":{"family_groupid":"4242"}}"""));
            return (HttpStatusCode.OK, null, Encoding.UTF8.GetBytes("""{"response":{"apps":[{"appid":400}]}}"""));
        });
        var service = Service(api, vault);

        var mine = await service.LibraryAsync(SteamId, TestContext.Current.CancellationToken);
        var other = await service.LibraryAsync(SteamId + 2, TestContext.Current.CancellationToken);

        Assert.NotNull(mine);
        Assert.Equal(["570"], mine.Value.Owned.AppIds.Order(StringComparer.Ordinal));
        Assert.Equal(["400"], mine.Value.Family!.Apps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(4242UL, mine.Value.Family.GroupId);
        Assert.Null(other);
    }

    [Fact]
    public async Task NoFamilyIsAnEmptyListAndNoAnswerIsUnknown()
    {
        var (none, _) = Fixture(request => request.RequestUri!.AbsolutePath.Contains("GetFamilyGroupForUser")
            ? (HttpStatusCode.OK, null, Encoding.UTF8.GetBytes("""{"response":{"is_not_member_of_any_group":true}}"""))
            : (HttpStatusCode.OK, null, Encoding.UTF8.GetBytes("""{"response":{"games":[]}}""")));
        var (down, _) = Fixture(_ => (HttpStatusCode.ServiceUnavailable, null, []));
        var (sharesDown, _) = Fixture(request => request.RequestUri!.AbsolutePath.Contains("GetFamilyGroupForUser")
            ? (HttpStatusCode.OK, null, Encoding.UTF8.GetBytes("""{"response":{"family_groupid":"4242"}}"""))
            : (HttpStatusCode.TooManyRequests, null, []));
        var ct = TestContext.Current.CancellationToken;

        var noFamily = await SteamSignInService.LibraryWithTokenAsync(none, "t", SteamId, ct);
        var noAnswer = await SteamSignInService.LibraryWithTokenAsync(down, "t", SteamId, ct);
        var limited = await SteamSignInService.LibraryWithTokenAsync(sharesDown, "t", SteamId, ct);

        Assert.Equal(0UL, noFamily.Family!.GroupId);
        Assert.Empty(noFamily.Family.Apps);
        Assert.Null(noAnswer.Family);
        Assert.False(noAnswer.Owned.Authoritative);
        Assert.Null(limited.Family);   // in a family, but its games did not come: what was kept stands
    }

    [Fact]
    public void ClaimsReadTheAccountAndItsEnd()
    {
        var claims = SteamTokenClaims.Parse(Jwt(new { sub = 76561197960265729UL, exp = 123L }));

        Assert.Equal(SteamId, claims.SteamId);
        Assert.Equal(123L, claims.ExpiresAt);
        Assert.Equal(SteamId, SteamTokenClaims.Parse(Jwt(new { sub = SteamId.ToString() })).SteamId);
    }

    [Fact]
    public void MalformedTokenIsRefused()
    {
        Assert.ThrowsAny<Exception>(() => SteamTokenClaims.Parse("not-a-token"));
    }

    [Fact]
    public void PasswordIsEncryptedWithPkcs1ForTheAccountKey()
    {
        using var rsa = RSA.Create(1024);
        var p = rsa.ExportParameters(false);

        var encrypted = SteamPasswordCrypto.Encrypt(Convert.ToHexString(p.Modulus!), Convert.ToHexString(p.Exponent!), "correct horse");

        using var check = RSA.Create();
        check.ImportParameters(rsa.ExportParameters(true));
        Assert.Equal("correct horse", Encoding.UTF8.GetString(check.Decrypt(Convert.FromBase64String(encrypted), RSAEncryptionPadding.Pkcs1)));
    }

    [Fact]
    public void MachineIdIsStableForOnePcAndUser()
    {
        Assert.Equal(SteamSignInService.MachineIdFor("PC", "user"), SteamSignInService.MachineIdFor("PC", "user"));
        Assert.NotEqual(SteamSignInService.MachineIdFor("PC", "user"), SteamSignInService.MachineIdFor("PC", "other"));
        Assert.Equal(16, SteamSignInService.MachineIdFor("PC", "user").Length);
    }

    [Theory]
    [InlineData("https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?access_token=eyJhbGciOi.eyJzdWIiOi.sig9Zq&steamid=1")]
    [InlineData("access_token=eyJhbG" + "ciOiJS" + "UzI1Ni" + "J9.eyJ" + "zdWIiO" + "iIxIn0" + ".Xq9zL" + "mN2b1R" + "kZW1v")]
    [InlineData("refresh_token: eyJhbG" + "ciOiJS" + "UzI1Ni" + "J9.eyJ" + "zdWIiO" + "iIxIn0" + ".Xq9zL" + "mN2b1R" + "kZW1v")]
    [InlineData("password=hunter2-correct")]
    public void ScrubberRemovesTokensAndPasswordsFromLogText(string text)
    {
        var scrubbed = AppLog.Redact(text);

        Assert.DoesNotContain("eyJ", scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.Contains("[redacted]", scrubbed);
    }

    private static string Saved(string access) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["Account"] = "someone",
            ["SteamId"] = SteamId.ToString(),
            ["Refresh"] = Refresh,
            ["Access"] = access,
        });
}
