using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The Shaders page and the methods Exo lets it ask its engine, which nobody notices drifting apart until a button
/// fails with "Unknown request.": a page method left off the list (Refresh, Pause, Resume and Stop were, in 3.15.1's
/// first build); a method the page never calls left on it, widening what the page can reach for nothing; a method that
/// does no work (reading the board, saying it is on screen, moving or pausing what isn't running) starting the engine,
/// which then runs with nothing pressed.
/// </summary>
public sealed partial class ShadersPageMethodsTests
{
    [Fact]
    public void Only_what_you_press_starts_the_engine()
    {
        Assert.Equal([
            "cache.freeOldDriver",
            "driver.setCacheLimit",
            "driver.setCleanupGuard",
            "game.compile",
            "queue.add",
            "queue.resume",
            "queue.start",
            "recorder.auto",
            "scan",
        ], ShadersAddon.StartMethods.Order(StringComparer.Ordinal));
        Assert.True(ShadersAddon.StartMethods.IsSubsetOf(ShadersAddon.PageMethods));
    }

    /// <summary>Community recordings are gone with SCSKiller's servers: the page offers no Patreon sign-in, no sharing and no upload,
    /// and the engine is never asked for them through the bridge.</summary>
    [Fact]
    public void The_page_offers_no_community_sharing()
    {
        Assert.DoesNotContain(ShadersAddon.PageMethods, method => method.StartsWith("sharing.", StringComparison.Ordinal));
        var source = string.Join('\n', Directory.GetFiles(Repo.PathOf("ui", "src", "components", "shaders"), "*.ts*").Select(File.ReadAllText));
        Assert.False(Regex.IsMatch(source, "Patreon|sharing|Sharing|setUpload|signIn"), "The Shaders page still names community sharing.");
    }

    private static HashSet<string> PageCalls()
    {
        var source = string.Join('\n', Directory.GetFiles(Repo.PathOf("ui", "src", "components", "shaders"), "*.tsx").Select(File.ReadAllText));
        return [.. EngineMethod().Matches(source).Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public void Every_method_the_page_calls_is_allowed()
    {
        var missing = PageCalls().Except(ShadersAddon.PageMethods).ToList();
        Assert.True(missing.Count == 0, "Not allowed for the Shaders page: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_allowed_method_is_one_the_page_calls()
    {
        var unused = ShadersAddon.PageMethods.Except(PageCalls()).ToList();
        Assert.True(unused.Count == 0, "Allowed but never called by the Shaders page: " + string.Join(", ", unused));
    }

    /// <summary>An engine method in the page's source: call('snapshot'), or a step of run(): ['queue.add', ...] / [cond ? 'queue.resume' : 'queue.pause'].</summary>
    [GeneratedRegex(@"(?:call\(|run\(|\[|\? |: )'((?:snapshot|scan|ui\.[a-zA-Z]+|queue\.[a-zA-Z]+|game\.[a-zA-Z]+|recorder\.[a-zA-Z]+|driver\.[a-zA-Z]+|cache\.[a-zA-Z]+))'")]
    private static partial Regex EngineMethod();
}
