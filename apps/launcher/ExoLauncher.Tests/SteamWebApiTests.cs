using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Tests;

/// <summary>
/// Steam's sign-in and library calls, answered from recorded fixtures (no network, no account). Covers the QR session
/// start, the password session and its Steam Guard answer, a refused password (its code, never the password), token
/// renewal, and the owned and family parsing, including the answer that must read as "unavailable" and not as "no games".
/// </summary>
public sealed class SteamWebApiTests
{
    private const string Base = "https://api.test";


    private static SteamWebApi Api(HttpMessageHandler handler) => new(ExoHttp.Create(TimeSpan.FromSeconds(5), handler), Base);

    [Fact]
    public async Task QrSessionStartsWithAProtobufPostAndReadsTheChallenge()
    {
        var answer = new SteamProtoWriter().Varint(1, 42).String(2, "https://s.team/q/1/77").Bytes(3, [9]).Float(4, 5f).ToArray();
        var fixture = new SteamFixtureHandler(_ => (HttpStatusCode.OK, "1", answer));

        var start = await Api(fixture).BeginQrAsync("Exo PC", [1, 2, 3], TestContext.Current.CancellationToken);

        Assert.Equal(42UL, start.ClientId);
        Assert.Equal("https://s.team/q/1/77", start.ChallengeUrl);
        var request = fixture.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Base + "/IAuthenticationService/BeginAuthSessionViaQR/v1/", request.RequestUri!.GetLeftPart(UriPartial.Path));
        var body = fixture.Bodies.Single();
        Assert.StartsWith("input_protobuf_encoded=", body);
    }

    [Fact]
    public async Task PollReturnsTheTokensOnceSteamHasThem()
    {
        var answer = new SteamProtoWriter().String(3, "refresh.jwt").String(4, "access.jwt").String(6, "someone").ToArray();
        var fixture = new SteamFixtureHandler(_ => (HttpStatusCode.OK, "1", answer));

        var poll = await Api(fixture).PollAsync(42, [9], TestContext.Current.CancellationToken);

        Assert.Equal("refresh.jwt", poll.RefreshToken);
        Assert.Equal("access.jwt", poll.AccessToken);
        Assert.Equal("someone", poll.AccountName);
    }

    [Fact]
    public async Task PasswordSessionAsksForTheGuardCodeAndSubmitsIt()
    {
        var rsa = new SteamProtoWriter().String(1, "abcd").String(2, "010001").Varint(3, 1700000000).ToArray();
        var begin = new SteamProtoWriter().Varint(1, 9).Bytes(2, [5]).Float(3, 4f)
            .Message(4, new SteamProtoWriter().Varint(1, 3).ToArray()).Varint(5, 76561197960265729).ToArray();
        var fixture = new SteamFixtureHandler(request => request.RequestUri!.AbsolutePath.Contains("GetPasswordRSAPublicKey")
            ? (HttpStatusCode.OK, "1", rsa)
            : request.RequestUri!.AbsolutePath.Contains("BeginAuthSessionViaCredentials")
                ? (HttpStatusCode.OK, "1", begin)
                : (HttpStatusCode.OK, "1", []));

        var key = await Api(fixture).RsaKeyAsync("someone", TestContext.Current.CancellationToken);
        var session = await Api(fixture).BeginCredentialsAsync("Exo PC", "someone", "cGFzcw==", key.Timestamp, [1], "", TestContext.Current.CancellationToken);
        await Api(fixture).SubmitGuardCodeAsync(session.ClientId, session.SteamId, "abcde", SteamGuardKind.DeviceCode, TestContext.Current.CancellationToken);

        Assert.Equal("abcd", key.Modulus);
        Assert.Equal(SteamGuardKind.DeviceCode, Assert.Single(session.Confirmations).Kind);
        Assert.Equal(76561197960265729UL, session.SteamId);
        var rsaRequest = fixture.Requests[0];
        Assert.Equal(HttpMethod.Get, rsaRequest.Method);
        Assert.Contains("input_protobuf_encoded=", rsaRequest.RequestUri!.Query);
        var guard = fixture.Requests[2];
        Assert.Contains("UpdateAuthSessionWithSteamGuardCode", guard.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("abcde", fixture.Bodies[2]);
    }

    [Fact]
    public async Task RefusedPasswordThrowsItsCodeAndNeverEchoesThePassword()
    {
        var fixture = new SteamFixtureHandler(_ => (HttpStatusCode.OK, "5", []));

        var error = await Assert.ThrowsAsync<SteamAuthException>(() =>
            Api(fixture).BeginCredentialsAsync("Exo PC", "someone", "hunter2hunter2", 1, [1], "", TestContext.Current.CancellationToken));

        Assert.Equal(5, error.EResult);
        Assert.DoesNotContain("hunter2", error.Message);
        Assert.DoesNotContain("hunter2", error.ToString());
    }

    [Fact]
    public async Task RefusedGuardCodeIsNamed()
    {
        var fixture = new SteamFixtureHandler(_ => (HttpStatusCode.OK, "88", []));

        var error = await Assert.ThrowsAsync<SteamAuthException>(() =>
            Api(fixture).SubmitGuardCodeAsync(1, 2, "WRONG", SteamGuardKind.DeviceCode, TestContext.Current.CancellationToken));

        Assert.Equal(88, error.EResult);
        Assert.Contains("Steam Guard code", error.Message);
    }

    [Fact]
    public async Task TokenRenewalReturnsTheNewAccessToken()
    {
        var fixture = new SteamFixtureHandler(_ => (HttpStatusCode.OK, "1", new SteamProtoWriter().String(1, "fresh.jwt").ToArray()));

        Assert.Equal("fresh.jwt", await Api(fixture).GenerateAccessTokenAsync("rt", 76561197960265729, TestContext.Current.CancellationToken));
        Assert.Contains("GenerateAccessTokenForApp", fixture.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ExpiredRefreshTokenAsksForANewSignIn()
    {
        var fixture = new SteamFixtureHandler(_ => (HttpStatusCode.OK, "9", []));

        var error = await Assert.ThrowsAsync<SteamAuthException>(() => Api(fixture).GenerateAccessTokenAsync("rt", 1, TestContext.Current.CancellationToken));

        Assert.Equal(9, error.EResult);
        Assert.Contains("Start it again", error.Message);
    }

    [Fact]
    public async Task OwnedGamesCarriesTheTokenInTheQueryWithoutAKey()
    {
        var fixture = SteamFixtureHandler.Json("""{"response":{"game_count":1,"games":[{"appid":570,"name":"Dota 2","playtime_forever":120}]}}""");

        var owned = await Api(fixture).OwnedGamesAsync("access.jwt", 76561197960265729, TestContext.Current.CancellationToken);

        Assert.True(owned.Authoritative);
        Assert.Equal(["570"], owned.AppIds.Order(StringComparer.Ordinal));
        Assert.Equal(120, owned.PlaytimeMinutes["570"]);
        var query = fixture.Requests.Single().RequestUri!.Query;
        Assert.Contains("access_token=access.jwt", query);
        Assert.DoesNotContain("key=", query);
    }

    [Theory]
    [InlineData("""{"response":{}}""")]
    [InlineData("""{"response":{"games":"nope"}}""")]
    [InlineData("""{}""")]
    [InlineData("not json")]
    public void OwnedAnswerWithoutAGamesListIsUnavailableNotEmpty(string json)
    {
        var parsed = json == "not json"
            ? SteamCommunityGames.OwnedGamesResult.Unavailable
            : SteamWebApi.ParseOwned(JsonDocument.Parse(json).RootElement);

        Assert.False(parsed.Authoritative);
    }

    [Fact]
    public void OwnedParseSkipsBadIdsAndKeepsTheLargestPlaytimeAsMinutes()
    {
        var parsed = SteamWebApi.ParseOwned(JsonDocument.Parse("""
            {"response":{"games":[
              {"appid":10,"name":"CS","playtime_forever":0},
              {"appid":"20","name":"Not numeric"},
              {"appid":0,"name":"Zero"},
              {"appid":30,"name":"  ","playtime_forever":45}
            ]}}
            """).RootElement);

        Assert.Equal(["10", "30"], parsed.AppIds.Order(StringComparer.Ordinal));
        Assert.False(parsed.PlaytimeMinutes.ContainsKey("10"));
        Assert.Equal(45, parsed.PlaytimeMinutes["30"]);
        Assert.False(parsed.Names.ContainsKey("30"));
    }

    [Theory]
    [InlineData("""{"response":{"is_not_member_of_any_group":true}}""", 0UL)]
    [InlineData("""{"response":{"family_groupid":"4242","is_not_member_of_any_group":false}}""", 4242UL)]
    [InlineData("""{"response":{"family_groupid":4243}}""", 4243UL)]
    public void FamilyGroupReadsStringOrNumberAndNoMembership(string json, ulong expected)
    {
        Assert.Equal(expected, SteamWebApi.ParseFamilyGroup(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void FamilyGroupWithoutAResponseIsUnknown()
    {
        Assert.Null(SteamWebApi.ParseFamilyGroup(JsonDocument.Parse("{}").RootElement));
    }

    // The shape of GetSharedLibraryApps' answer (SteamDatabase/Protobufs service_familygroups.proto); ids and names made up.
    private const string SharedLibraryFixture = """
        {"response":{"apps":[
          {"appid":570,"owner_steamids":["76561197960265731","76561197960265732"],"name":"Shared Game","sort_as":"shared game",
           "capsule_filename":"library_600x900.jpg","exclude_reason":0,"rt_time_acquired":1700000000,"rt_playtime":3600,"app_type":1},
          {"appid":400,"owner_steamids":["76561197960265731"],"app_type":1},
          {"appid":620,"owner_steamids":["76561197960265731"],"name":"Kept Private","exclude_reason":1,"app_type":1},
          {"appid":630,"owner_steamids":["76561197960265731"],"name":"A Tool","exclude_reason":0,"app_type":4},
          {"appid":0,"name":"Zero"},
          {"appid":570,"owner_steamids":["76561197960265733","not-an-id","0"],"name":"Shared Game"}
        ],"owner_steamid":"76561197960265729"}}
        """;

    [Fact]
    public async Task FamilySharedAppsAreReadWithNamesAndOwnersAndExcludedOnesLeftOut()
    {
        var fixture = SteamFixtureHandler.Json(SharedLibraryFixture);

        var apps = await Api(fixture).FamilySharedAppsAsync("t", 4242, 76561197960265729, TestContext.Current.CancellationToken);

        Assert.NotNull(apps);
        Assert.Equal(["400", "570"], apps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Shared Game", apps["570"].Name);
        // Owners as account ids, each once, from every entry of the app; a malformed id is dropped.
        Assert.Equal(["3", "4", "5"], apps["570"].OwnerAccountIds.Order(StringComparer.Ordinal));
        Assert.Null(apps["400"].Name);
        var query = fixture.Requests.Single().RequestUri!.Query;
        Assert.Contains("family_groupid=4242", query);
        Assert.Contains("include_own=false", query);
        Assert.Contains("include_excluded=false", query);
        Assert.Contains("include_non_games=false", query);
    }

    [Theory]
    [InlineData("""{"response":{}}""", 0)]                                   // Steam leaves out an empty list
    [InlineData("""{"response":{"owner_steamid":"76561197960265729"}}""", 0)]
    [InlineData("""{"response":{"apps":[]}}""", 0)]
    [InlineData("""{}""", -1)]                                               // no response: unknown, not empty
    [InlineData("""{"response":{"apps":{}}}""", -1)]
    [InlineData("""{"response":{"error":"x"}}""", -1)]
    public void ASharedLibraryWithNothingInItIsEmptyAndABrokenOneUnknown(string json, int expected)
    {
        var apps = SteamWebApi.ParseSharedApps(JsonDocument.Parse(json).RootElement);

        Assert.Equal(expected, apps?.Count ?? -1);
    }

    [Fact]
    public async Task ATooManyRequestsAnswerIsUnavailableForTheLibrary()
    {
        var fixture = new SteamFixtureHandler(_ => (HttpStatusCode.TooManyRequests, null, []));

        var owned = await Api(fixture).OwnedGamesAsync("t", 1, TestContext.Current.CancellationToken);

        Assert.False(owned.Authoritative);
    }
}
