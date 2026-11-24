using System.IO;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class RpcTraceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-rpctrace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root); } catch { }
    }

    [Fact]
    public void DisabledTraceIsOffAndWritesNothing()
    {
        using var trace = new RpcTrace(null);
        Assert.False(trace.IsEnabled);
        using var doc = JsonDocument.Parse("""{"token":"secret"}""");
        trace.RecordRequest("web", "1", "discord.login", doc.RootElement, true);
        trace.RecordReply("web", "1", true, 1.25);
        trace.RecordEvent("web", "discord.state", new { token = "secret" });
    }

    [Fact]
    public void RequestReplyAndEventWriteOnlyShapesAndNeverValues()
    {
        var traceFile = Path.Combine(_root, "trace.jsonl");
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        const string email = "player.secret@example.com";
        const string pathValue = @"C:\Users\SecretPlayer\Documents\private-notes.txt";
        const string dmBody = "Meet me at the secret raid location at 9pm";

        using (var trace = new RpcTrace(traceFile))
        {
            Assert.True(trace.IsEnabled);
            using var paramsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                token = jwt,
                email,
                installPath = pathValue,
                message = dmBody,
                count = 42,
                enabled = true,
                tags = new[] { "secret-tag-alpha", "secret-tag-beta" },
                nested = new
                {
                    authorization = "Bearer " + jwt,
                    items = new[] { new { id = "secret-id-1", secretPath = pathValue } },
                },
            }));
            trace.RecordRequest("web", "req-1", "discord.sendMessage", paramsDoc.RootElement, true);
            trace.RecordReply("web", "req-1", true, 14.567);
            trace.RecordEvent("native", "discord.message", new
            {
                channelId = "998877665544332211",
                content = dmBody,
                authorEmail = email,
                bearer = jwt,
                localPath = pathValue,
            });
        }

        var raw = File.ReadAllText(traceFile);
        Assert.DoesNotContain(jwt, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGci", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(email, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretPlayer", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private-notes", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret raid", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tag", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-id-1", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("998877665544332211", raw, StringComparison.Ordinal);

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        using var reqLine = JsonDocument.Parse(lines[0]);
        Assert.Equal("web", reqLine.RootElement.GetProperty("client").GetString());
        Assert.Equal("req-1", reqLine.RootElement.GetProperty("id").GetString());
        Assert.Equal("discord.sendMessage", reqLine.RootElement.GetProperty("method").GetString());
        var pShape = reqLine.RootElement.GetProperty("params_shape");
        Assert.Equal("string", pShape.GetProperty("token").GetString());
        Assert.Equal("string", pShape.GetProperty("email").GetString());
        Assert.Equal("string", pShape.GetProperty("installPath").GetString());
        Assert.Equal("string", pShape.GetProperty("message").GetString());
        Assert.Equal("number", pShape.GetProperty("count").GetString());
        Assert.Equal("boolean", pShape.GetProperty("enabled").GetString());
        Assert.Equal("string", pShape.GetProperty("tags")[0].GetString());
        Assert.Equal("string", pShape.GetProperty("nested").GetProperty("authorization").GetString());
        Assert.Equal("string", pShape.GetProperty("nested").GetProperty("items")[0].GetProperty("secretPath").GetString());

        using var replyLine = JsonDocument.Parse(lines[1]);
        Assert.Equal("web", replyLine.RootElement.GetProperty("client").GetString());
        Assert.Equal("req-1", replyLine.RootElement.GetProperty("id").GetString());
        Assert.True(replyLine.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(14.57, replyLine.RootElement.GetProperty("ms").GetDouble());

        using var eventLine = JsonDocument.Parse(lines[2]);
        Assert.Equal("native", eventLine.RootElement.GetProperty("client").GetString());
        Assert.Equal("discord.message", eventLine.RootElement.GetProperty("event").GetString());
        var evShape = eventLine.RootElement.GetProperty("payload_shape");
        Assert.Equal("string", evShape.GetProperty("channelId").GetString());
        Assert.Equal("string", evShape.GetProperty("content").GetString());
        Assert.Equal("string", evShape.GetProperty("bearer").GetString());
    }

    [Fact]
    public void UnsafeDynamicObjectKeysAreRedactedToPlaceholder()
    {
        var traceFile = Path.Combine(_root, "keys.jsonl");
        const string emailKey = "alice@secret.org";
        const string pathKey = @"C:\Users\Alice\Games\secret.exe";
        const string tokenKey = "tok_9f8e7d6c5b4a3210fedcba9876543210";

        using (var trace = new RpcTrace(traceFile))
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [emailKey] = "v1",
                [pathKey] = "v2",
                [tokenKey] = "v3",
                ["gameId"] = "steam:620",
            }));
            trace.RecordRequest("native", "req-2", "library.get", doc.RootElement, true);
        }

        var raw = File.ReadAllText(traceFile);
        Assert.DoesNotContain(emailKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenKey, raw, StringComparison.Ordinal);
        Assert.Contains("\"gameId\":\"string\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"$key\":\"string\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeUiFlagIsAcceptedByRuneHostSession()
    {
        var opts = RuneHostSession.TryParse(["--ui=native"], _ => null, _ => false);
        Assert.NotNull(opts);
        Assert.True(opts!.SpawnUi);
    }
}
