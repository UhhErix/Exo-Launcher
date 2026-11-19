using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the window rules could go wrong, on records only (no real window is read, hidden or shown):
/// - the decision table has a row no window reaches, or lets a window reach the wrong row (a main window taken for a prompt);
/// - a sign-in, two-factor or payment window is taken for a shell or a toast and hidden;
/// - a small window the player can type in is taken for a notification;
/// - when the rules cannot tell, the window is hidden instead of shown;
/// - a title is matched as part of a longer word (a "terms" inside another word) or only in one case;
/// - a store is recognised by its file name, or a path that only starts with a client's folder name;
/// - a suspension outlives its reason, or one ending cuts another short;
/// - the sweep never slows down, or never stops;
/// - a launch waiting on a client times out while the player is answering that client's prompt.
/// </summary>
public sealed class StoreWindowRulesTests
{
    private static StoreWindow Window(string title = "", int width = 1000, int height = 700, nint owner = 0,
        string className = "Chrome_WidgetWin_1", long style = 0x00CF0000, long exStyle = 0x00040000) =>
        new(1, 1, className, title, true, owner, 100, 100, 100 + width, 100 + height, style, exStyle);

    [Fact]
    public void TheTableWalk()
    {
        var rows = StoreWindowRules.Table;
        foreach (var (row, index) in rows.Select((row, index) => (row, index)))
        {
            var facts = StoreWindowRules.FactsOf(row.Sample);
            Assert.True(row.Matches(facts), $"row {index} ({row.Name}) does not match its own sample");
            Assert.Equal(row.Kind, StoreWindowRules.Classify(row.Sample));
            Assert.Same(row, StoreWindowRules.Decide(row.Sample));
            // No earlier row takes the sample: the walk proves the order, not only each row on its own.
            Assert.DoesNotContain(rows.Take(index), earlier => earlier.Matches(facts));
        }
        Assert.Contains(rows, row => row.Kind == StoreWindowKind.Prompt);
        Assert.Contains(rows, row => row.Kind == StoreWindowKind.Shell);
        Assert.Contains(rows, row => row.Kind == StoreWindowKind.Toast);
        Assert.Contains(rows, row => row.Kind == StoreWindowKind.Other);
        // A window no row names still has an answer: the last row takes everything.
        Assert.True(rows[^1].Matches(default));
        Assert.Equal(StoreWindowKind.Prompt, rows[^1].Kind);
    }

    [Fact]
    public void AnOwnedDialogIsAPrompt()
    {
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 400, 300, owner: 7)));
        // Owned, however big and whatever the title: the owner's window is the main window, this is a question to it.
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("Steam", 1100, 800, owner: 7)));
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 300, 200, owner: 7, exStyle: StoreWindowRules.WsExNoActivate | StoreWindowRules.WsExToolWindow)));
    }

    [Fact]
    public void ASmallWindowThatTakesFocusIsAPromptNotAToast()
    {
        // Small, no owner, nothing in the title: it can take keyboard focus, so the player may be meant to type in it.
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 360, 120)));
        // Topmost or a tool window does not make a focusable window a notification.
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 360, 120, exStyle: StoreWindowRules.WsExTopmost)));
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 360, 120, exStyle: StoreWindowRules.WsExToolWindow)));
        // Narrow but tall, and wide but short, are both small.
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 639, 900)));
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 1200, 399)));
    }

    [Fact]
    public void WhenTheRulesCannotTellItIsAPrompt()
    {
        // Small, no owner, never takes focus, but neither topmost nor a tool window: nothing says notification.
        var unclear = Window("", 360, 120, exStyle: StoreWindowRules.WsExNoActivate);
        var row = StoreWindowRules.Decide(unclear);
        Assert.Equal(StoreWindowKind.Prompt, row.Kind);
        Assert.Equal("the rules cannot tell", row.Name);
    }

    [Fact]
    public void ANotificationToastIsAToast()
    {
        var toast = Window("Friend is online", 360, 120,
            exStyle: StoreWindowRules.WsExNoActivate | StoreWindowRules.WsExTopmost | StoreWindowRules.WsExToolWindow);
        Assert.Equal(StoreWindowKind.Toast, StoreWindowRules.Classify(toast));
    }

    [Fact]
    public void TheMainWindowIsAShellWhateverItsTitle()
    {
        foreach (var title in new[] { "Steam", "Something else entirely", "", "Steam — Bibliothek", "Играть" })
            Assert.Equal(StoreWindowKind.Shell, StoreWindowRules.Classify(Window(title)));
    }

    [Fact]
    public void AZeroSizeOrInputMethodWindowIsLeftAlone()
    {
        Assert.Equal(StoreWindowKind.Other, StoreWindowRules.Classify(Window("x", 0, 0)));
        Assert.Equal(StoreWindowKind.Other, StoreWindowRules.Classify(Window("Default IME", 400, 400, className: "IME")));
        Assert.Equal(StoreWindowKind.Other, StoreWindowRules.Classify(Window("MSCTFIME UI", 400, 400, className: "MSCTFIME UI")));
    }

    [Fact]
    public void ADialogStyleWindowIsAPrompt()
    {
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 800, 600, className: "#32770")));
        // A dialog frame without a border is a dialog box; an ordinary captioned window has both bits and is not.
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 800, 600, style: StoreWindowRules.WsDlgFrame)));
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window("", 800, 600, exStyle: StoreWindowRules.WsExDlgModalFrame)));
        Assert.Equal(StoreWindowKind.Shell, StoreWindowRules.Classify(Window("", 800, 600, style: StoreWindowRules.WsCaption)));
    }

    [Theory]
    [InlineData("Sign in")]
    [InlineData("Steam - Sign-in")]
    [InlineData("SIGN IN TO EPIC")]
    [InlineData("Battle.net Login")]
    [InlineData("Log in")]
    [InlineData("Enter your password")]
    [InlineData("Verification required")]
    [InlineData("Two-factor authentication")]
    [InlineData("Two factor code")]
    [InlineData("2FA")]
    [InlineData("Authenticator")]
    [InlineData("Security code")]
    [InlineData("Captcha")]
    [InlineData("Payment")]
    [InlineData("Purchase")]
    [InlineData("Checkout")]
    [InlineData("Redeem a code")]
    [InlineData("Activate")]
    [InlineData("Confirm")]
    [InlineData("End User License Agreement")]
    [InlineData("Licence")]
    [InlineData("Terms of service")]
    [InlineData("Subscriber Agreement")]
    [InlineData("EULA")]
    public void ATitleWithAPromptWordIsAPrompt(string title)
    {
        Assert.True(StoreWindowRules.HasPromptWord(title), title);
        Assert.Equal(StoreWindowKind.Prompt, StoreWindowRules.Classify(Window(title)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Steam")]
    [InlineData("Epic Games Launcher")]
    [InlineData("Riot Client")]
    [InlineData("Designing")]          // "sign" inside a word
    [InlineData("Determine")]          // "term" inside a word
    [InlineData("Termsheet")]          // "terms" starting a longer word
    [InlineData("Unconfirmed")]        // "confirm" inside a word
    [InlineData("Reactivated")]        // "activate" is not whole here
    public void APlainTitleIsNotAPromptWord(string title) =>
        Assert.False(StoreWindowRules.HasPromptWord(title), title);

    [Fact]
    public void TheSweepSlowsAfterTenSecondsAndStopsWithNoClient()
    {
        // The first ten seconds after a client's process starts, or the hider was started for a launch: fast, client or not.
        Assert.Equal(TimeSpan.FromMilliseconds(250), SweepCadence.Next(0, anyClientRunning: false));
        Assert.Equal(TimeSpan.FromMilliseconds(250), SweepCadence.Next(0, anyClientRunning: true));
        Assert.Equal(TimeSpan.FromMilliseconds(250), SweepCadence.Next(9.99, anyClientRunning: true));
        // Then every two seconds while a client runs, however long it has run.
        Assert.Equal(TimeSpan.FromSeconds(2), SweepCadence.Next(10, anyClientRunning: true));
        Assert.Equal(TimeSpan.FromSeconds(2), SweepCadence.Next(90 * 60, anyClientRunning: true));
        // And it stops when none does.
        Assert.Null(SweepCadence.Next(10, anyClientRunning: false));
        Assert.Null(SweepCadence.Next(3600, anyClientRunning: false));
    }

    // --- a program is the store by its path -------------------------------------------------------------------------

    private static readonly Dictionary<StoreKind, string[]> Roots = new()
    {
        [StoreKind.Steam] = [@"C:\Program Files (x86)\Steam"],
        [StoreKind.Riot] = [@"C:\Riot Games\Riot Client"],
        [StoreKind.Xbox] = [@"C:\Program Files\WindowsApps\Microsoft.GamingApp_*"],
    };

    private static IReadOnlyList<string> RootsOf(StoreKind store) => Roots.TryGetValue(store, out var roots) ? roots : [];

    [Fact]
    public void AStoreIsItsProgramByPathNotByName()
    {
        Assert.Equal(StoreKind.Steam, StoreClientIdentity.Of(@"C:\Program Files (x86)\Steam\steam.exe", RootsOf));
        Assert.Equal(StoreKind.Steam, StoreClientIdentity.Of(@"c:\program files (x86)\steam\bin\cef\steamwebhelper.exe", RootsOf));
        Assert.Null(StoreClientIdentity.Of(@"D:\Tools\steam.exe", RootsOf));
        // A folder that only starts with the client's folder name is another folder.
        Assert.Null(StoreClientIdentity.Of(@"C:\Program Files (x86)\Steam Tools\steam.exe", RootsOf));
        Assert.Null(StoreClientIdentity.Of(@"C:\Riot Games\VALORANT\live\VALORANT.exe", RootsOf));
        Assert.Equal(StoreKind.Riot, StoreClientIdentity.Of(@"C:\Riot Games\Riot Client\UX\RiotClientUx.exe", RootsOf));
        // An app package's folder is its name and a version.
        Assert.Equal(StoreKind.Xbox, StoreClientIdentity.Of(@"C:\Program Files\WindowsApps\Microsoft.GamingApp_2511.1.1.0_x64__8wekyb3d8bbwe\GamingApp.exe", RootsOf));
        Assert.Null(StoreClientIdentity.Of(@"C:\Program Files\WindowsApps\Other_1.0\GamingApp.exe", RootsOf));
    }

    [Fact]
    public void ANullOrEmptyPathIsNoStore()
    {
        Assert.Null(StoreClientIdentity.Of(null, RootsOf));
        Assert.Null(StoreClientIdentity.Of("", RootsOf));
        Assert.False(StoreClientIdentity.BelongsTo(StoreKind.Steam, " ", RootsOf));
    }

    [Fact]
    public void TheEpicLauncherFolderIsTrimmedFromAnyLayout()
    {
        Assert.Equal(@"C:\Users\u\AppData\Local\Epic Games\Launcher",
            StoreClientIdentity.TrimTo(@"C:\Users\u\AppData\Local\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", @"Epic Games\Launcher"));
        Assert.Equal(@"C:\Elsewhere", StoreClientIdentity.TrimTo(@"C:\Elsewhere\EpicGamesLauncher.exe", @"Epic Games\Launcher"));
    }

    // --- a suspension ends when its reason ends -----------------------------------------------------------------------

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    [Fact]
    public void ASuspensionEndsWhenItsReasonEnds()
    {
        var clock = new Clock();
        var book = new SuspensionBook(clock.Read);
        var removal = book.Hold(StoreKind.Ubisoft, "removing a game", TimeSpan.FromMinutes(20));
        Assert.True(book.IsSuspended(StoreKind.Ubisoft));
        // The removal is confirmed a minute in: the store's windows are not left visible for the other 19 minutes.
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(book.IsSuspended(StoreKind.Ubisoft));
        removal.Dispose();
        Assert.False(book.IsSuspended(StoreKind.Ubisoft));
        Assert.True(book.IsEmpty);
        // Ending twice is harmless.
        removal.Dispose();
        Assert.False(book.IsSuspended(StoreKind.Ubisoft));
    }

    [Fact]
    public void TwoSuspensionsEndOnlyWhenBothDo()
    {
        var clock = new Clock();
        var book = new SuspensionBook(clock.Read);
        var removal = book.Hold(StoreKind.Epic, "removing a game", TimeSpan.FromMinutes(20));
        var install = book.Hold(StoreKind.Epic, "install in its launcher", TimeSpan.FromHours(2));
        removal.Dispose();
        Assert.True(book.IsSuspended(StoreKind.Epic));
        install.Dispose();
        Assert.False(book.IsSuspended(StoreKind.Epic));
        // Other stores are never affected.
        var other = book.Hold(StoreKind.Gog, "x", TimeSpan.FromMinutes(1));
        Assert.False(book.IsSuspended(StoreKind.Epic));
        Assert.True(book.IsSuspended(StoreKind.Gog));
        other.Dispose();
    }

    [Fact]
    public void ASuspensionStopsAtItsTimeLimitIfNobodyEndsIt()
    {
        var clock = new Clock();
        var book = new SuspensionBook(clock.Read);
        _ = book.Hold(StoreKind.Steam, "launch needs the player", TimeSpan.FromMinutes(30));
        clock.Now += TimeSpan.FromMinutes(29);
        Assert.True(book.IsSuspended(StoreKind.Steam));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.False(book.IsSuspended(StoreKind.Steam));
        book.Prune(_ => true);
        Assert.True(book.IsEmpty);
    }

    [Fact]
    public void ASuspensionThatWaitsForTheClientEndsWhenTheClientHasClosed()
    {
        var clock = new Clock();
        var book = new SuspensionBook(clock.Read);
        _ = book.Hold(StoreKind.Riot, "launch needs the player", TimeSpan.FromMinutes(30), endsWhenClientExits: true);
        // A client Exo has only just started may not be up yet.
        book.Prune(_ => false);
        Assert.True(book.IsSuspended(StoreKind.Riot));
        clock.Now += SuspensionBook.ClientGrace + TimeSpan.FromSeconds(1);
        book.Prune(_ => true);
        Assert.True(book.IsSuspended(StoreKind.Riot));
        book.Prune(_ => false);
        Assert.False(book.IsSuspended(StoreKind.Riot));
    }

    [Fact]
    public void TheSameReasonForTheSameStoreIsOneSuspensionThatIsExtended()
    {
        var clock = new Clock();
        var book = new SuspensionBook(clock.Read);
        _ = book.Hold(StoreKind.Steam, StoreRuntimeReason, TimeSpan.FromSeconds(30));
        clock.Now += TimeSpan.FromSeconds(20);
        var renewed = book.Hold(StoreKind.Steam, StoreRuntimeReason, TimeSpan.FromSeconds(30));
        clock.Now += TimeSpan.FromSeconds(20);
        Assert.True(book.Has(StoreKind.Steam, StoreRuntimeReason));
        Assert.Equal([StoreKind.Steam], book.StoresWith(StoreRuntimeReason));
        renewed.Dispose();
        Assert.False(book.IsSuspended(StoreKind.Steam));
    }

    private const string StoreRuntimeReason = HiddenStoreRuntime.UserOpenReason;

    [Fact]
    public void TheRuntimeSuspendsAndEndsThroughItsHandle()
    {
        // Paradox is used by no other test: the runtime's book is process-wide.
        Assert.False(HiddenStoreRuntime.IsSuspended(StoreKind.Paradox));
        var first = HiddenStoreRuntime.Suspend(StoreKind.Paradox, "test: one", TimeSpan.FromMinutes(5));
        var second = HiddenStoreRuntime.Suspend(StoreKind.Paradox, "test: two", TimeSpan.FromMinutes(5));
        first.Dispose();
        Assert.True(HiddenStoreRuntime.IsSuspended(StoreKind.Paradox));
        second.Dispose();
        Assert.False(HiddenStoreRuntime.IsSuspended(StoreKind.Paradox));
    }

    // --- a launch waiting on a prompt does not time out ---------------------------------------------------------------

    [Fact]
    public void ALaunchWaitingOnAPromptDoesNotTimeOut()
    {
        var clock = new Clock();
        var prompt = false;
        var told = new List<bool>();
        var wait = new PromptAwareDeadline(TimeSpan.FromSeconds(35), clock.Read, () => prompt, told.Add);
        Assert.False(wait.Expired);
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.False(wait.Expired);
        // A sign-in comes up at 10 s and the player takes five minutes: well past the 35 s.
        prompt = true;
        for (var i = 0; i < 300; i++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.False(wait.Expired, $"timed out {i} s into the prompt");
        }
        Assert.Equal([true], told);
        // The prompt is answered: the rest of the 35 s is still there.
        prompt = false;
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.False(wait.Expired);
        Assert.Equal([true, false], told);
        clock.Now += TimeSpan.FromSeconds(24);
        Assert.False(wait.Expired);
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.True(wait.Expired);
    }

    [Fact]
    public void AWaitWithNoPromptTimesOutOnTime()
    {
        var clock = new Clock();
        var wait = new PromptAwareDeadline(TimeSpan.FromSeconds(35), clock.Read);
        clock.Now += TimeSpan.FromSeconds(34);
        Assert.False(wait.Expired);
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.True(wait.Expired);
        Assert.Equal(TimeSpan.Zero, wait.Remaining);
    }

    [Fact]
    public void ThePauseForAPromptStopsAtTenMinutes()
    {
        var clock = new Clock();
        var wait = new PromptAwareDeadline(TimeSpan.FromSeconds(35), clock.Read, () => true);
        // A prompt that is never answered is not waited on for ever: 10 minutes, then the 35 s.
        var seconds = 0;
        while (!wait.Expired && seconds < 3600)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            seconds++;
        }
        Assert.InRange(seconds, 10 * 60 + 34, 10 * 60 + 36);
    }

    [Fact]
    public async Task RiotsWaitForItsServiceDoesNotTimeOutWhileAPromptIsUp()
    {
        var start = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var clock = new Clock();
        var prompt = true;
        var ready = await ExoLauncher.Adapters.Riot.RiotClientApi.EnsureReadyAsync(
            // Riot's service comes up 3 minutes 20 seconds in, shortly after the player has answered the sign-in.
            _ => Task.FromResult(clock.Now >= start.AddSeconds(200)),
            () => { },
            TimeSpan.FromSeconds(35), CancellationToken.None,
            utcNow: clock.Read,
            delay: (span, _) => { clock.Now += span; if (clock.Now >= start.AddSeconds(180)) prompt = false; return Task.CompletedTask; },
            promptVisible: () => prompt);
        // With a clock of 35 s this would have given up at the first prompt.
        Assert.True(ready);
    }

    [Fact]
    public async Task RiotsWaitStillTimesOutWithNoPrompt()
    {
        var clock = new Clock();
        var ready = await ExoLauncher.Adapters.Riot.RiotClientApi.EnsureReadyAsync(
            _ => Task.FromResult(false), () => { }, TimeSpan.FromSeconds(35), CancellationToken.None,
            utcNow: clock.Read,
            delay: (span, _) => { clock.Now += span; return Task.CompletedTask; });
        Assert.False(ready);
        Assert.InRange((clock.Now - new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)).TotalSeconds, 35, 36);
    }

    [Fact]
    public void TheSentenceForANeededPlayerNamesTheStore()
    {
        Assert.Equal("Riot needs you to finish something in its window.", PromptAwareDeadline.NeedsYou("Riot"));
        var said = new List<string>();
        var watch = new PromptWatch(StoreKind.Riot, "Riot", said.Add);
        watch.Changed(true);
        watch.Changed(false);
        Assert.Equal(["Riot needs you to finish something in its window.", "Preparing launch…"], said);
    }
}
