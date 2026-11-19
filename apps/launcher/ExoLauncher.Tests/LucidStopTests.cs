using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Stopping the Lucid that Exo hosts before an update, install, uninstall or swap renames or deletes its folder.
/// Ways it could fail: a Lucid the user runs from another folder is asked or ended with it; a folder that only starts like
/// Exo's (app.previous-dev, app-old), a path with .. or other capitals, or a relative path is taken for, or hides from,
/// Exo's folder; a process is ended before anyone asked it, or after it closed and another program was given its id; one
/// that refuses is reported as stopped; Windows refusing the ask makes Exo wait out the whole time for nothing.
/// </summary>
public sealed class LucidStopTests
{
    private const string App = @"C:\Users\me\AppData\Local\ExoLauncher\app";
    private const string Hosted = App + @"\addons\lucid\Lucid.exe";
    private const string Theirs = @"C:\Users\me\Documents\Lucid.exe";

    [Theory]
    [InlineData(Hosted, App, true)]
    [InlineData(@"c:\users\ME\appdata\local\exolauncher\APP\addons\lucid\lucid.exe", App, true)]
    [InlineData(Hosted, App + @"\", true)]
    [InlineData(App + @"\addons\..\addons\lucid\Lucid.exe", App, true)]
    // Not inside: a sibling sharing the first letters, the folder itself, a copy elsewhere, a way out through .., nothing.
    [InlineData(@"C:\Users\me\AppData\Local\ExoLauncher\app.previous-dev-1\addons\lucid\Lucid.exe", App, false)]
    [InlineData(@"C:\Users\me\AppData\Local\ExoLauncher\app-old\Lucid.exe", App, false)]
    [InlineData(App, App, false)]
    [InlineData(App + @"\", App, false)]
    [InlineData(Theirs, App, false)]
    [InlineData(App + @"\addons\..\..\Lucid.exe", App, false)]
    [InlineData(@"addons\lucid\Lucid.exe", App, false)]
    [InlineData(Hosted, @"app", false)]
    [InlineData("", App, false)]
    [InlineData(null, App, false)]
    [InlineData(Hosted, null, false)]
    public void Only_a_file_inside_the_folder_is_inside_it(string? file, string? folder, bool expected) =>
        Assert.Equal(expected, LucidStop.IsUnder(file, folder));

    private sealed class Fake(params LucidProcess[] processes) : ILucidProcesses
    {
        public List<LucidProcess> Alive { get; } = [.. processes];
        public List<string> Calls { get; } = [];
        public bool Answers = true;       // a Lucid that closes when asked
        public bool Windows = true;       // Windows takes the ask
        public bool Allows = true;        // Windows lets the end happen
        public int Waits;

        public IReadOnlyList<LucidProcess> Running() => [.. Alive];

        public bool AskToClose(IReadOnlyList<LucidProcess> which)
        {
            Calls.Add("ask " + string.Join(",", which.Select(p => p.Id)));
            if (Windows && Answers) Alive.RemoveAll(which.Contains);
            return Windows;
        }

        public bool End(LucidProcess which)
        {
            Calls.Add("end " + which.Id);
            if (!Allows) return false;
            Alive.Remove(which);
            return true;
        }

        public Task Wait(TimeSpan time) { Waits++; return Task.CompletedTask; }
    }

    private static readonly LucidProcess InExo = new(10, 100, Hosted);
    private static readonly LucidProcess Separate = new(20, 200, Theirs);
    private static readonly TimeSpan Ask = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Nothing_in_Exos_folder_running_asks_and_ends_nothing()
    {
        var os = new Fake(Separate);

        var result = await LucidStop.HostedAsync(os, [App], Ask);

        Assert.Equal(new LucidStopped(0, 0, 0, 0), result);
        Assert.Empty(os.Calls);
        Assert.Contains(Separate, os.Alive);
    }

    [Fact]
    public async Task A_Lucid_that_closes_when_asked_is_not_ended()
    {
        var os = new Fake(InExo);

        var result = await LucidStop.HostedAsync(os, [App], Ask);

        Assert.Equal(new LucidStopped(1, 1, 0, 0), result);
        Assert.Equal(["ask 10"], os.Calls);
    }

    [Fact]
    public async Task One_that_will_not_close_is_asked_first_waited_for_and_then_ended()
    {
        var os = new Fake(InExo) { Answers = false };

        var result = await LucidStop.HostedAsync(os, [App], Ask);

        Assert.Equal(new LucidStopped(1, 0, 1, 0), result);
        Assert.Equal(["ask 10", "end 10"], os.Calls);
        Assert.Equal(30, os.Waits); // all 3 seconds given, in steps of 100 ms
        Assert.Empty(os.Alive);
    }

    [Fact]
    public async Task A_separate_copy_is_never_asked_or_ended_even_beside_one_that_is()
    {
        var os = new Fake(Separate, InExo) { Answers = false };

        var result = await LucidStop.HostedAsync(os, [App], Ask);

        Assert.Equal(new LucidStopped(1, 0, 1, 0), result);
        Assert.Equal(["ask 10", "end 10"], os.Calls);
        Assert.Equal([Separate], os.Alive);
    }

    [Fact]
    public async Task A_sibling_folder_that_shares_the_name_is_not_Exos()
    {
        var sibling = new LucidProcess(30, 300, @"C:\Users\me\AppData\Local\ExoLauncher\app.previous-dev-1\addons\lucid\Lucid.exe");
        var os = new Fake(sibling) { Answers = false };

        var result = await LucidStop.HostedAsync(os, [App], Ask);

        Assert.Equal(0, result.Found);
        Assert.Empty(os.Calls);
    }

    [Fact]
    public async Task When_Windows_will_not_ask_the_wait_is_short_and_the_end_still_comes()
    {
        var os = new Fake(InExo) { Windows = false };

        var result = await LucidStop.HostedAsync(os, [App], Ask);

        Assert.Equal(new LucidStopped(1, 0, 1, 0), result);
        Assert.InRange(os.Waits, 1, 5);
    }

    [Fact]
    public async Task One_that_cannot_be_ended_is_reported_left()
    {
        var os = new Fake(InExo) { Answers = false, Allows = false };

        var result = await LucidStop.HostedAsync(os, [App], Ask);

        Assert.Equal(new LucidStopped(1, 0, 0, 1), result);
        Assert.Equal(["ask 10", "end 10"], os.Calls);
    }

    [Fact]
    public async Task An_id_given_to_another_program_after_the_close_is_left_alone()
    {
        var os = new Fake(InExo);
        // Lucid closes when asked and a new process takes its id before the look: same id, another start time.
        var reused = new LucidProcess(10, 999, Hosted);
        var asked = false;
        var wrapped = new Reuse(os, () => { asked = true; os.Alive.Clear(); os.Alive.Add(reused); });

        var result = await LucidStop.HostedAsync(wrapped, [App], Ask);

        Assert.True(asked);
        Assert.Equal(new LucidStopped(1, 1, 0, 0), result);
        Assert.DoesNotContain("end 10", os.Calls);
        Assert.Contains(reused, os.Alive);
    }

    [Fact]
    public async Task The_Lucid_Exo_downloaded_into_its_data_folder_is_stopped_too_but_not_a_folder_that_shares_its_name()
    {
        const string Data = @"C:\Users\me\AppData\Local\ExoLauncher\addons\lucid-current";
        var downloaded = new LucidProcess(40, 400, Data + @"\Lucid.exe");
        var lookalike = new LucidProcess(50, 500, Data + @"-old\Lucid.exe");
        var os = new Fake(downloaded, lookalike, Separate) { Answers = false };

        var result = await LucidStop.HostedAsync(os, [App, Data], Ask);

        Assert.Equal(new LucidStopped(1, 0, 1, 0), result);
        Assert.Equal(["ask 40", "end 40"], os.Calls);
        Assert.Equal([lookalike, Separate], os.Alive);
    }

    // The fake, but its ask swaps Lucid for another process with the same id.
    private sealed class Reuse(Fake inner, Action onAsk) : ILucidProcesses
    {
        public IReadOnlyList<LucidProcess> Running() => inner.Running();
        public bool AskToClose(IReadOnlyList<LucidProcess> which) { inner.Calls.Add("ask"); onAsk(); return true; }
        public bool End(LucidProcess which) => inner.End(which);
        public Task Wait(TimeSpan time) => inner.Wait(time);
    }
}
