using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo's side of Exo Shaders (<see cref="ShadersAddon"/>) against the engine built from <c>shaders\</c>, run in its
/// <c>--safe</c> mode on a temporary data folder (nothing compiled, recorded or deleted; the PC's games only read).
/// Ways it could fail: Exo calls a method the engine's dispatcher doesn't have; a press doesn't start a stopped engine; an
/// engine that exits after its idle wait leaves Exo thinking it runs, or fails the press that met it on its way out; the
/// old engine's exit fails the new engine's requests or resets its state.
/// </summary>
public sealed partial class ShadersEngineTests
{
    /// <summary>The built service: EXO_SHADERS_SERVICE, else shaders\engine\src\ExoShaders.Service's Release or Debug build.</summary>
    private static string Engine()
    {
        if (Environment.GetEnvironmentVariable("EXO_SHADERS_SERVICE") is { Length: > 0 } set) return set;
        foreach (var config in new[] { "Release", "Debug" })
        {
            var exe = Repo.PathOf("shaders", "engine", "src", "ExoShaders.Service", "bin", config, "net10.0-windows", "win-x64", "ExoShaders.Service.exe");
            if (File.Exists(exe)) return exe;
        }
        Assert.Skip(@"Exo Shaders' engine isn't built (dotnet build shaders\engine\src\ExoShaders.Service -c Release -p:ExoBuild=true).");
        return "";
    }

    [GeneratedRegex("""CallAsync\("([a-zA-Z.]+)"|"(snapshot|ui\.visible|scan|queue\.[a-zA-Z]+|game\.[a-zA-Z]+|recorder\.[a-zA-Z]+|exo\.library|exitWhenIdle|shutdown)""")]
    private static partial Regex HostMethod();

    [GeneratedRegex("""case "([a-zA-Z.]+)":""")]
    private static partial Regex EngineCase();

    [Fact]
    public void Every_method_Exo_sends_is_one_the_engine_answers()
    {
        var engine = EngineCase().Matches(File.ReadAllText(Repo.PathOf("shaders", "engine", "src", "ExoShaders.Service", "Methods.cs")))
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var host = new[] { "MainWindow.Shaders.cs", Path.Combine("Services", "ShadersAddon.cs"), Path.Combine("Services", "WebHostBridge.Shaders.cs") }
            .SelectMany(file => HostMethod().Matches(File.ReadAllText(Repo.PathOf("ExoLauncher", file))))
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Concat(ShadersAddon.PageMethods).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("exo.library", host);
        Assert.All(host, method => Assert.Contains(method, engine));
    }

    [Fact]
    public async Task A_press_starts_it_and_one_after_its_idle_exit_starts_the_next()
    {
        var engine = Engine();
        var data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "exo-shaders-host-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var addon = new ShadersAddon(engine, ["--data-dir", data, "--safe"]);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            JsonNode? afterExit = null;
            var pressedOnItsWayOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            addon.Event += (name, data) =>
            {
                events.Enqueue(name);
                if (name != "exiting") return;
                // A press the moment the engine says it is going: it must reach the next engine, not fail.
                _ = Task.Run(async () =>
                {
                    try { afterExit = await addon.CallAsync("snapshot", null, ct, "a test press"); pressedOnItsWayOut.TrySetResult(); }
                    catch (Exception ex) { pressedOnItsWayOut.TrySetException(ex); }
                }, ct);
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => addon.CallAsync("snapshot", null, ct));   // nothing pressed: nothing starts
            Assert.False(addon.Running);
            var board = await addon.CallAsync("snapshot", null, ct, "a test press");
            Assert.True(addon.Running);
            Assert.NotNull(board?["games"]);
            Assert.Equal("running", addon.State);

            await addon.CallAsync("exitWhenIdle", new JsonObject { ["seconds"] = 5 }, ct);
            await pressedOnItsWayOut.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
            Assert.NotNull(afterExit?["queue"]);
            Assert.True(addon.Running);   // the next engine, started by the press
            Assert.Contains("ready", events);
            // The old engine's exit didn't take the new one down with it.
            await Task.Delay(1000, ct);
            Assert.True(addon.Running);
            Assert.Equal("running", addon.State);
            Assert.NotNull(await addon.CallAsync("snapshot", null, ct));
        }
        finally
        {
            addon.Dispose();
            for (var i = 0; i < 20 && Directory.Exists(data); i++)
            {
                try { ExoLauncher.Helpers.DeletionGate.DeleteDirectory(data, recursive: true); }
                catch (IOException) { Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { Thread.Sleep(500); }
            }
        }
    }

    [Fact]
    public async Task Stopping_race_on_non_start_methods_returns_at_rest_without_failing()
    {
        var restNode = JsonNode.Parse("""{"rev":0}""");
        var (usedRest, result) = await ShadersAddon.InvokeWithStoppingFallbackAsync(
            "ui.visible",
            starts: false,
            () => true,
            () => throw new InvalidOperationException("the service is stopping"),
            () => restNode);

        Assert.True(usedRest);
        Assert.Same(restNode, result);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShadersAddon.InvokeWithStoppingFallbackAsync(
                "scan",
                starts: true,
                () => false,
                () => throw new InvalidOperationException("start failed"),
                () => restNode));
    }
}
