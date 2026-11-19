// Exo Shaders - Exo Launcher's fork of SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 Exo Shaders contributors. GPL-3.0-or-later (see LICENSE, LICENSE-EXCEPTION.txt).
//
// The service's protocol (CONTRACT.md) as Exo's ShadersAddon speaks it, against the built ExoShaders.Service.exe in --safe
// mode on a temporary data folder: nothing is compiled, recorded, deleted or launched, and the PC's own data folder, game
// folders and driver caches are only read.
//
// How it could fail, each checked here: the first line isn't `ready`, or stdout carries something that isn't a protocol
// line; an id comes back changed, or a bad line gets no answer; a method Exo calls goes missing; safe mode lets a compile
// through; exitWhenIdle never exits, or exits without `exiting` or without leaving its last state for the page; closing
// stdin leaves the process running; two services share one data folder; exo.library's noRecorder doesn't keep the
// recorder out of the game Exo named, or isn't kept for the next start.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace ExoShaders.Service.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task ItSaysReadyFirstAndAnswersWhatExoAsks()
    {
        using var dir = new TempDir();
        await using var s = Service.Start(dir.Path);
        JsonObject ready = await s.Event("ready");
        Assert.Equal(1, (int)ready["protocol"]!);
        Assert.True((bool)ready["safe"]!);
        Assert.Equal(dir.Path, ((string)ready["dataDir"]!).TrimEnd('\\'), ignoreCase: true);
        Assert.Equal(s.Pid, (int)ready["pid"]!);

        // Ids come back as they went: a number, a string.
        JsonObject snap = await s.Reply(s.Send("snapshot", id: "board-1"), "board-1");
        Assert.NotNull(snap["result"]?["games"]);
        Assert.NotNull(snap["result"]?["queue"]);
        Assert.False(string.IsNullOrEmpty((string?)snap["result"]?["app"]?["engineVersion"]));

        // The rest of what Exo's host and page call is there (an unknown method would say so).
        foreach (var (method, args) in new (string, JsonObject?)[]
        {
            ("ui.visible", new JsonObject { ["visible"] = false }),
            ("ui.visible", new JsonObject { ["visible"] = true }),
            ("queue.pause", null), ("queue.stop", null), ("exitWhenIdle", new JsonObject { ["seconds"] = 0 }),
            ("recorder.auto", new JsonObject { ["on"] = false }),
        })
        {
            JsonObject reply = await s.Reply(s.Send(method, args));
            Assert.DoesNotContain("unknown method", (string?)reply["error"] ?? "");
        }

        JsonObject unknown = await s.Reply(s.Send("nope"));
        Assert.Equal("unknown method 'nope'", (string?)unknown["error"]);
        // Safe mode refuses what would compile or record.
        foreach (string method in new[] { "game.compile", "queue.start", "recorder.set" })
            Assert.Equal("not available in safe mode", (string?)(await s.Reply(s.Send(method, new JsonObject { ["id"] = "steam:1", ["on"] = true })))["error"]);

        // A line that isn't JSON gets an answer with a null id.
        s.Write("{not json");
        JsonObject bad = await s.Next(m => m.ContainsKey("error") && m["id"] is null);
        Assert.StartsWith("not a valid request", (string?)bad["error"]);

        Assert.All(s.Lines, line => Assert.True(JsonNode.Parse(line) is JsonObject o && (o.ContainsKey("event") || o.ContainsKey("id")), line));
    }

    [Fact]
    public async Task UiVisibleIsExemptWhenQuittingWhileOtherWorkIsRefused()
    {
        using var dir = new TempDir();
        await using var s = Service.Start(dir.Path);
        await s.Event("ready");

        // Trigger graceful shutdown with 5s wait so QuitAsync keeps Quitting == true briefly.
        await s.Reply(s.Send("shutdown", new JsonObject { ["waitSeconds"] = 5 }));

        JsonObject uiReply = await s.Reply(s.Send("ui.visible", new JsonObject { ["visible"] = false }));
        Assert.Null(uiReply["error"]);
        Assert.True((bool)uiReply["result"]!["ok"]!);

        JsonObject pauseReply = await s.Reply(s.Send("queue.pause"));
        Assert.Equal("the service is stopping", (string?)pauseReply["error"]);
    }

    [Fact]
    public async Task ItExitsByItselfOnceIdleAndLeavesItsLastState()
    {
        using var dir = new TempDir();
        await using var s = Service.Start(dir.Path);
        await s.Event("ready");
        await s.Reply(s.Send("exitWhenIdle", new JsonObject { ["seconds"] = 5 }));
        JsonObject exiting = await s.Event("exiting", TimeSpan.FromMinutes(2));
        Assert.Equal("idle", (string?)exiting["reason"]);
        Assert.Equal(0, await s.Exit(TimeSpan.FromSeconds(30)));
        JsonNode atRest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "exo-snapshot.json")))!;
        Assert.Equal(0, (int)atRest["rev"]!);
        Assert.Empty(atRest["queue"]!["waiting"]!.AsArray());
    }

    [Fact]
    public async Task ClosingStdinEndsItAndOneDataFolderHasOneService()
    {
        using var dir = new TempDir();
        await using var s = Service.Start(dir.Path);
        await s.Event("ready");
        await using (var second = Service.Start(dir.Path))
            Assert.Equal(3, await second.Exit(TimeSpan.FromSeconds(30)));   // refused: another service holds the folder
        s.CloseInput();
        Assert.Equal("stdinClosed", (string?)(await s.Event("exiting", TimeSpan.FromMinutes(2)))["reason"]);
        Assert.Equal(0, await s.Exit(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task AGameExoKeepsTheRecorderOutOfNeverGetsItAndTheWordIsKept()
    {
        using var dir = new TempDir();
        string game = Directory.CreateDirectory(Path.Combine(dir.Path, "games-root", "Quiet Game")).FullName;
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(game, "QuietGame.exe"));
        await using var s = Service.Start(dir.Path);
        await s.Event("ready");
        var games = new JsonArray
        {
            new JsonObject { ["id"] = "test.quiet", ["title"] = "Quiet Game", ["store"] = "Test", ["installDir"] = game, ["exe"] = "QuietGame.exe", ["noRecorder"] = true },
        };
        JsonObject sent = await s.Reply(s.Send("exo.library", new JsonObject { ["games"] = games }));
        Assert.True((bool)sent["result"]!["changed"]!);

        JsonObject? listed = null;
        for (var sw = Stopwatch.StartNew(); listed is null && sw.Elapsed < TimeSpan.FromMinutes(2); await Task.Delay(500))
        {
            JsonObject snap = await s.Reply(s.Send("snapshot"));
            listed = snap["result"]!["games"]!.AsArray().OfType<JsonObject>().FirstOrDefault(g => (string?)g["id"] == "exo:test.quiet");
        }
        Assert.NotNull(listed);
        Assert.Equal(ScsKiller.SkipHost, (string?)listed!["recorder"]!["skip"]);
        Assert.False((bool)listed["recorder"]!["canSet"]!);
        Assert.False((bool)listed["recorder"]!["on"]!);

        // Kept in exo-library.json, so the next start keeps it out before Exo sends its list.
        var kept = new ExoLibrarySource(dir.Path).Entries.Single();
        Assert.True(kept.NoRecorder);
    }

    [Fact]
    public void NoRecorderSurvivesTheLibraryFileAndAnEntryWithoutItAllowsTheRecorder()
    {
        using var dir = new TempDir();
        string a = Directory.CreateDirectory(Path.Combine(dir.Path, "a")).FullName, b = Directory.CreateDirectory(Path.Combine(dir.Path, "b")).FullName;
        Assert.True(new ExoLibrarySource(dir.Path).Replace([new("a", "A", "Steam", a, NoRecorder: true), new("b", "B", "Steam", b)]));
        var read = new ExoLibrarySource(dir.Path).Entries.ToDictionary(e => e.Id);
        Assert.True(read["a"].NoRecorder);
        Assert.False(read["b"].NoRecorder);
        // The word changing is a change: the list is saved and the games are scanned again.
        Assert.True(new ExoLibrarySource(dir.Path).Replace([new("a", "A", "Steam", a), new("b", "B", "Steam", b)]));
        Assert.False(new ExoLibrarySource(dir.Path).Entries.Single(e => e.Id == "a").NoRecorder);
    }
}

/// <summary>A temporary data folder, deleted after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "exo-shaders-test-" + Guid.NewGuid().ToString("N")[..8])).FullName.TrimEnd('\\');

    public void Dispose()
    {
        for (int i = 0; i < 10; i++)
        {
            try { Directory.Delete(Path, recursive: true); return; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}

/// <summary>One ExoShaders.Service.exe in --safe mode, as Exo's ShadersAddon runs it: hidden, stdin/stdout NDJSON.</summary>
internal sealed class Service : IAsyncDisposable
{
    private readonly Process _p;
    private readonly BlockingCollection<JsonObject> _in = new();
    private readonly List<JsonObject> _seen = [];
    private readonly ConcurrentQueue<string> _lines = new();
    private int _next;

    public int Pid => _p.Id;
    public IEnumerable<string> Lines => _lines;

    private Service(Process p)
    {
        _p = p;
        _ = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                _lines.Enqueue(line);
                if (JsonNode.Parse(line) is JsonObject o) _in.Add(o);
            }
            _in.CompleteAdding();
        });
        _ = p.StandardError.ReadToEndAsync();
    }

    /// <summary>The built service: EXO_SHADERS_SERVICE, else the Release then Debug build of engine\src\ExoShaders.Service.</summary>
    public static string Exe()
    {
        if (Environment.GetEnvironmentVariable("EXO_SHADERS_SERVICE") is { Length: > 0 } set) return set;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string project = System.IO.Path.Combine(dir.FullName, "src", "ExoShaders.Service");
            if (!Directory.Exists(project)) continue;
            foreach (string config in new[] { "Release", "Debug" })
            {
                string exe = System.IO.Path.Combine(project, "bin", config, "net10.0-windows", "win-x64", "ExoShaders.Service.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        throw new FileNotFoundException("Build engine\\src\\ExoShaders.Service first (dotnet build -c Release -p:ExoBuild=true), or set EXO_SHADERS_SERVICE.");
    }

    public static Service Start(string dataDir)
    {
        var info = new ProcessStartInfo(Exe())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
            ArgumentList = { "--data-dir", dataDir, "--safe" },
        };
        return new Service(Process.Start(info)!);
    }

    public void Write(string line)
    {
        _p.StandardInput.WriteLine(line);
        _p.StandardInput.Flush();
    }

    /// <summary>Sends a request; its id (a number unless <paramref name="id"/> is given).</summary>
    public JsonNode Send(string method, JsonObject? args = null, string? id = null)
    {
        JsonNode reqId = id is null ? JsonValue.Create(Interlocked.Increment(ref _next))! : JsonValue.Create(id)!;
        Write(new JsonObject { ["id"] = reqId.DeepClone(), ["method"] = method, ["params"] = args ?? new JsonObject() }.ToJsonString());
        return reqId;
    }

    public void CloseInput() => _p.StandardInput.Close();

    public Task<JsonObject> Reply(JsonNode id, string? expect = null) =>
        Next(m => m["id"] is JsonNode got && JsonNode.DeepEquals(got, id) && (expect is null || (string?)got == expect));

    public async Task<JsonObject> Event(string name, TimeSpan? timeout = null) =>
        (await Next(m => (string?)m["event"] == name, timeout))["data"] as JsonObject ?? [];

    /// <summary>The next line (or one seen already and not taken) that matches.</summary>
    public Task<JsonObject> Next(Func<JsonObject, bool> match, TimeSpan? timeout = null) => Task.Run(() =>
    {
        lock (_seen)
        {
            if (_seen.FirstOrDefault(match) is { } early) { _seen.Remove(early); return early; }
        }
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(1));
        while (DateTime.UtcNow < until)
        {
            if (!_in.TryTake(out JsonObject? m, TimeSpan.FromMilliseconds(200)))
            {
                if (_in.IsCompleted) break;
                continue;
            }
            if (match(m)) return m;
            lock (_seen) _seen.Add(m);
        }
        throw new TimeoutException("the service sent no such line; it sent: " + string.Join(" | ", _lines.TakeLast(5).Select(l => l.Length > 200 ? l[..200] : l)));
    });

    public async Task<int> Exit(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _p.WaitForExitAsync(cts.Token);
        return _p.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_p.HasExited)
        {
            try
            {
                Write("{\"id\":0,\"method\":\"shutdown\",\"params\":{\"waitSeconds\":5}}");
                _p.StandardInput.Close();
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            try { await Exit(TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) { }
        }
        _p.Dispose();
    }
}
