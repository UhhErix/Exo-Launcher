using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// apps.list's kept answer (AnswerCache), with a counter for a read and a file in this run's own temp folder. Ways it could
/// fail: an uninstall answered with the list from before it (the page keeps showing the app); a read behind the answer
/// never landing, or landing without telling the page; a read that started before an uninstall replacing the one after
/// it; another version's file answered; the file never written, so every restart waits on a full read.
/// </summary>
public sealed class AnswerCacheTests
{
    private static string NewPath() => Path.Combine(TestDataDirectory.Root, $"answer-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Kept_answer_comes_back_at_once_and_is_read_again_behind_it()
    {
        var reads = 0;
        var cache = new AnswerCache(NewPath(), "1.0", () => $"{{\"n\":{Interlocked.Increment(ref reads)}}}", TimeSpan.Zero);
        var told = 0;
        cache.Changed += () => Interlocked.Increment(ref told);
        Assert.Equal("{\"n\":1}", cache.Get());
        Assert.Equal("{\"n\":1}", cache.Get());   // at once, the read behind it starts
        await cache.Behind();                      // that read, or one more
        Assert.True(told >= 1);
        Assert.NotEqual("{\"n\":1}", cache.Get());
    }

    [Fact]
    public void A_change_here_is_never_answered_with_the_list_from_before_it()
    {
        var reads = 0;
        var cache = new AnswerCache(NewPath(), "1.0", () => $"{{\"n\":{Interlocked.Increment(ref reads)}}}", TimeSpan.FromHours(1));
        Assert.Equal("{\"n\":1}", cache.Get());
        cache.Invalidate();
        Assert.Equal("{\"n\":2}", cache.Get());
        Assert.Equal("{\"n\":2}", cache.Get());
    }

    [Fact]
    public void A_restart_answers_from_the_file_of_the_same_version_only()
    {
        var path = NewPath();
        new AnswerCache(path, "1.0", () => "{\"n\":1}", TimeSpan.FromHours(1)).Get();
        Assert.Equal("{\"n\":1}", new AnswerCache(path, "1.0", () => "{\"n\":9}", TimeSpan.FromHours(1)).Get());
        Assert.Equal("{\"n\":9}", new AnswerCache(path, "2.0", () => "{\"n\":9}", TimeSpan.FromHours(1)).Get());
    }
}
