using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The account service's recorded answers (cloud/account/client-fixtures, kept beside the live Worker's source) replayed against a
/// fake handler. What would go wrong silently:
/// - a request the service would refuse (wrong method, path, or no bearer / JSON content type) sent by the client;
/// - a status or error code read as something else, so the screen shows the wrong sentence;
/// - an error code the service can send that no screen says in words.
/// </summary>
public sealed class AccountFixtureTests
{
    private static readonly string[] Files = ["sign-up", "sign-in", "account", "sync"];

    /// <summary>Cases that send no JSON Content-Type on purpose: the client always sends JSON, so they are not replayable.</summary>
    private static bool Replayable(string name) => !name.Contains("wrong-content-type", StringComparison.Ordinal);

    public static IEnumerable<object[]> Cases() =>
        from file in Files
        from c in Load(file)["cases"]!.AsArray()
        where Replayable(c!["name"]!.GetValue<string>())
        select new object[] { file, c!["name"]!.GetValue<string>() };

    private static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Repo.PathOf("cloud", "account", "client-fixtures", name + ".json")))!;

    private static JsonNode Case(string file, string name) =>
        Load(file)["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;

    /// <summary>A placeholder such as {{token-1}} matches any value, so the test checks the shape and not the secret.</summary>
    private static Regex PathPattern(string path) =>
        new("^" + string.Join("[^/?]+", Regex.Split(path, @"\{\{[^}]*\}\}").Select(Regex.Escape)) + "$");

    /// <summary>Answers with the recorded response, and fails the test when the request is not the recorded one.</summary>
    private sealed class Replay(JsonNode recorded) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var req = recorded["request"]!;
            Assert.Equal(req["method"]!.GetValue<string>(), request.Method.Method);
            Assert.True(PathPattern(req["path"]!.GetValue<string>()).IsMatch(request.RequestUri!.PathAndQuery), request.RequestUri.PathAndQuery);

            var auth = req["headers"]?["Authorization"];
            if (auth is null) Assert.Null(request.Headers.Authorization);
            else Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);

            if (req["body"] is JsonObject body)
            {
                Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
                var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Assert.Equal(body.Select(p => p.Key).OrderBy(k => k), sent.Select(p => p.Key).OrderBy(k => k));
            }

            var res = recorded["response"]!;
            var response = new HttpResponseMessage((HttpStatusCode)res["status"]!.GetValue<int>());
            if (res["body"] is { } answer) response.Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ServiceAnswersReplayThroughTheClient(string file, string name)
    {
        var recorded = Case(file, name);
        using var client = new ExoAccountClient(new Replay(recorded));
        var req = recorded["request"]!;
        JsonObject? body = req["body"] as JsonObject;

        var reply = await client.SendAsync(
            new HttpMethod(req["method"]!.GetValue<string>()),
            req["path"]!.GetValue<string>().Replace("{{device-2}}", new string('a', 32)),
            req["headers"]?["Authorization"] is null ? null : "token-value",
            body, TestContext.Current.CancellationToken);

        var res = recorded["response"]!;
        Assert.Equal(res["status"]!.GetValue<int>(), reply.Status);
        Assert.Equal(res["body"]?["error"]?.GetValue<string>(), reply.Error);
        if (res["body"] is JsonObject expected && reply.Error is null)
            foreach (var key in expected.Select(p => p.Key))
                Assert.NotNull(reply.Body?[key]);
    }

    [Fact]
    public void EveryErrorTheServiceSendsHasAnAnswerOnScreenOrIsAClientBug()
    {
        var errors = Load("errors")["errors"]!.AsArray();
        Assert.NotEmpty(errors);
        foreach (var entry in errors)
        {
            var code = entry!["error"]!.GetValue<string>();
            if (code == "locked") continue; // shown through AccountMessages.Locked, with its countdown (tested below)
            var text = AccountMessages.For(code);
            if (AccountMessages.Shown.Contains(code))
                Assert.NotEqual(AccountMessages.Generic, text);
            else
                Assert.Equal(AccountMessages.Generic, text);
        }
        // The list of what each screen says is the spec's table (CLIENT.md 4.2): nothing may be missing from it.
        foreach (var code in new[] { "bad-email", "bad-password", "common-password", "email-taken", "wrong-credentials", "wrong-code", "wrong-password", "slow-down", "signed-out", "account-full", "server-error", "not-configured" })
            Assert.Contains(code, AccountMessages.Shown);
    }

    [Fact]
    public void LockedCountsDownInMinutesAndSeconds()
    {
        Assert.Equal("Too many tries for this email from this PC. Wait 1:30 and try again.", AccountMessages.Locked(90));
        Assert.Equal("Too many tries for this email from this PC. Wait 0:07 and try again.", AccountMessages.Locked(7));
    }
}
