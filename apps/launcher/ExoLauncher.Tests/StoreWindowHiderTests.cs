using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Tests;

/// <summary>
/// What the hider does to windows, on a fake window source and fake processes (nothing real is listed, hidden or shown):
/// - a main window is left visible because its title is not the one the code knew (renamed, localised, untitled);
/// - a sign-in, two-factor, payment or licence window is hidden, or shown only once the store's suspension was opened;
/// - a program with a store's file name, or one whose path cannot be read, is taken for the store;
/// - anti-cheat or a Riot game window is touched because it sits under a client's folder;
/// - a shell Exo hid comes back with another style than it had, or a prompt an older pass hid stays hidden;
/// - a notification is shown, or a window the rules cannot place is hidden;
/// - a prompt is brought to the front on every sweep.
/// </summary>
public sealed class StoreWindowHiderTests
{
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string RiotUx = @"C:\Riot Games\Riot Client\UX\RiotClientUx.exe";
    private const long AppWindow = 0x00040000;

    private static readonly Dictionary<StoreKind, string[]> Roots = new()
    {
        [StoreKind.Steam] = [@"C:\Program Files (x86)\Steam"],
        [StoreKind.Epic] = [@"C:\Users\u\AppData\Local\Epic Games\Launcher"],
        [StoreKind.Riot] = [@"C:\Riot Games\Riot Client"],
    };

    private sealed class FakeWindows : IStoreWindows
    {
        public readonly List<StoreWindow> All = [];
        public readonly List<string> Calls = [];

        public IReadOnlyList<StoreWindow> Enumerate(IReadOnlySet<int> processIds) =>
            All.Where(window => processIds.Contains(window.ProcessId)).ToArray();

        public StoreWindow? Read(nint handle) => All.Where(w => w.Handle == handle).Select(w => (StoreWindow?)w).FirstOrDefault();
        public int ProcessIdOf(nint handle) => Read(handle)?.ProcessId ?? 0;
        public void Hide(nint handle) { Calls.Add($"hide {handle}"); Update(handle, w => w with { Visible = false }); }
        public void Show(nint handle) { Calls.Add($"show {handle}"); Update(handle, w => w with { Visible = true }); }
        public void BringToFront(nint handle) => Calls.Add($"front {handle}");
        public void SetExStyle(nint handle, long exStyle) { Calls.Add($"style {handle} {exStyle:X}"); Update(handle, w => w with { ExStyle = exStyle }); }

        public void Update(nint handle, Func<StoreWindow, StoreWindow> change)
        {
            var at = All.FindIndex(w => w.Handle == handle);
            if (at >= 0) All[at] = change(All[at]);
        }

        public int Count(string call) => Calls.Count(c => c == call);
        public bool Touched(nint handle) => Calls.Any(c => c.EndsWith($" {handle}") || c.Contains($" {handle} "));
    }

    private sealed class FakeProcesses : IStoreProcesses
    {
        public readonly Dictionary<int, (string Name, string? Path)> Table = [];
        public string? PathOf(int processId) => Table.TryGetValue(processId, out var p) ? p.Path : null;
        public string? NameOf(int processId) => Table.TryGetValue(processId, out var p) ? p.Name : null;
        public IReadOnlyList<(int ProcessId, string Name)> Running(IEnumerable<string> processNames) =>
            Table.Where(p => processNames.Contains(p.Value.Name, StringComparer.OrdinalIgnoreCase)).Select(p => (p.Key, p.Value.Name)).ToArray();
    }

    private sealed class Rig
    {
        public readonly FakeWindows Windows = new();
        public readonly FakeProcesses Processes = new();
        public readonly List<string> Log = [];
        public bool Quiet = true;
        public bool Suspended;
        public long Now = 1_000;
        public readonly StoreWindowEngine Engine;

        public Rig()
        {
            Engine = new StoreWindowEngine(Windows, Processes, HiddenStoreRuntime.StoreForProcess,
                (store, path) => StoreClientIdentity.BelongsTo(store, path, s => Roots.TryGetValue(s, out var r) ? r : []),
                _ => Quiet, _ => Suspended, Log.Add, () => Now);
        }

        public void Program(int pid, string name, string? path) => Processes.Table[pid] = (name, path);

        public StoreWindow Add(nint handle, int pid, string title = "", int width = 1000, int height = 700, nint owner = 0,
            string className = "Chrome_WidgetWin_1", long style = 0x00CF0000, long exStyle = AppWindow, bool visible = true)
        {
            var window = new StoreWindow(handle, pid, className, title, visible, owner, 0, 0, width, height, style, exStyle);
            Windows.All.Add(window);
            return window;
        }

        public void Sweep() => Engine.Sweep(Processes.Table.Keys.ToHashSet());
        public StoreWindow Get(nint handle) => Windows.Read(handle)!.Value;
        public void Seconds(int seconds) => Now += seconds * 1000L;
    }

    private static Rig WithSteam()
    {
        var rig = new Rig();
        rig.Program(10, "steamwebhelper", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe");
        return rig;
    }

    [Fact]
    public void AStoreIsItsProgramByPathNotByName()
    {
        var rig = new Rig();
        rig.Program(10, "steam", SteamExe);
        rig.Program(11, "steam", @"D:\Tools\steam.exe"); // another program with Steam's file name
        rig.Add(100, 10, "Steam");
        rig.Add(101, 11, "Steam");
        rig.Sweep();
        Assert.False(rig.Get(100).Visible);
        Assert.True(rig.Get(101).Visible);
        Assert.False(rig.Windows.Touched(101));
    }

    [Fact]
    public void AProgramWhosePathCannotBeReadIsNotTheStore()
    {
        var rig = new Rig();
        rig.Program(10, "steam", null);
        rig.Add(100, 10, "Steam");
        rig.Sweep();
        rig.Seconds(10);
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.False(rig.Windows.Touched(100));
        // One line for the program name, however many sweeps.
        Assert.Single(rig.Log, line => line.Contains("steam") && line.Contains("cannot be read"));
    }

    [Theory]
    [InlineData("Steam")]
    [InlineData("A title the code has never heard of")]
    [InlineData("Steam — Bibliothek")]      // localised
    [InlineData("Играть")]                  // not Latin
    [InlineData("")]                        // untitled
    public void TheMainWindowIsHiddenWhateverItsTitle(string title)
    {
        var rig = WithSteam();
        rig.Add(100, 10, title);
        rig.Sweep();
        Assert.False(rig.Get(100).Visible);
        // Off the taskbar while hidden, and the style it had is kept.
        Assert.Contains("style 100 " + ((AppWindow | StoreWindowRules.WsExToolWindow) & ~AppWindow).ToString("X"), rig.Windows.Calls);
    }

    [Fact]
    public void AMainWindowIsLeftAloneWhenTheStoreIsNotKeptQuiet()
    {
        var rig = WithSteam();
        rig.Quiet = false; // the player opened the client
        rig.Add(100, 10, "Steam");
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        rig.Quiet = true;
        rig.Suspended = true; // a step that needs the client's window
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.False(rig.Windows.Touched(100));
    }

    [Theory]
    [InlineData("Sign in to Steam", 360, 480)]
    [InlineData("Steam - Sign in", 1100, 800)]
    [InlineData("Anmelden - Log in", 420, 520)]
    [InlineData("Sign in", 360, 480)]
    public void ASignInWindowIsNeverHidden(string title, int width, int height)
    {
        var rig = WithSteam();
        rig.Add(100, 10, title, width, height);
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.Equal(0, rig.Windows.Count("hide 100"));
        // Whether or not anything is suspended.
        rig.Suspended = true;
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
    }

    [Theory]
    [InlineData("Two-factor authentication")]
    [InlineData("Enter your security code")]
    [InlineData("Steam Guard Authenticator")]
    [InlineData("2FA")]
    public void ATwoFactorWindowIsNeverHidden(string title)
    {
        var rig = WithSteam();
        rig.Add(100, 10, title, 400, 400);
        rig.Add(101, 10, title, 1000, 700);
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.True(rig.Get(101).Visible);
        Assert.DoesNotContain(rig.Windows.Calls, call => call.StartsWith("hide"));
    }

    [Theory]
    [InlineData("Payment method")]
    [InlineData("Checkout")]
    [InlineData("Confirm your purchase")]
    public void APaymentWindowIsNeverHidden(string title)
    {
        var rig = WithSteam();
        rig.Add(100, 10, title, 500, 600);
        rig.Add(101, 10, title, 1280, 800);
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.True(rig.Get(101).Visible);
        Assert.DoesNotContain(rig.Windows.Calls, call => call.StartsWith("hide"));
    }

    [Fact]
    public void ALicenceWindowIsShownAndBroughtToTheFrontOnce()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "End User License Agreement", 600, 500);
        rig.Sweep();
        rig.Sweep();
        rig.Seconds(10);
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.Equal(1, rig.Windows.Count("front 100"));
        Assert.Equal(0, rig.Windows.Count("hide 100"));
        // The store's suspension is not what keeps it on screen.
        Assert.False(rig.Suspended);
    }

    [Fact]
    public void APromptOfAStoreThePlayerHasOpenedIsNotBroughtToTheFront()
    {
        var rig = WithSteam();
        rig.Quiet = false;
        rig.Add(100, 10, "Sign in", 400, 400);
        rig.Sweep();
        Assert.Equal(0, rig.Windows.Count("front 100"));
        Assert.True(rig.Get(100).Visible);
    }

    [Fact]
    public void AnOwnedDialogIsAPrompt()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "Steam");           // the main window, owner of the dialog
        rig.Add(101, 10, "", 380, 240, owner: 100);
        rig.Sweep();
        Assert.False(rig.Get(100).Visible);  // the main window is hidden ...
        Assert.True(rig.Get(101).Visible);   // ... the question it asks is not
        Assert.Equal(1, rig.Windows.Count("front 101"));
    }

    [Fact]
    public void ANotificationToastIsHidden()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "Friend is online", 360, 120, exStyle: StoreWindowRules.WsExNoActivate | StoreWindowRules.WsExTopmost | StoreWindowRules.WsExToolWindow);
        rig.Sweep();
        Assert.False(rig.Get(100).Visible);
    }

    [Fact]
    public void ASmallWindowThatTakesFocusIsAPromptNotAToast()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "", 360, 140, exStyle: AppWindow | StoreWindowRules.WsExTopmost);
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.Equal(0, rig.Windows.Count("hide 100"));
    }

    [Fact]
    public void WhenTheRulesCannotTellItIsAPrompt()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "", 360, 140, exStyle: StoreWindowRules.WsExNoActivate);
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.Equal(0, rig.Windows.Count("hide 100"));
    }

    [Fact]
    public void APromptThatWasHiddenByAnOlderPassIsShown()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "Client", exStyle: AppWindow | 0x200);
        rig.Sweep();
        Assert.False(rig.Get(100).Visible);
        // The same window now asks for a sign-in (an older pass hid it while it was a plain shell).
        rig.Windows.Update(100, w => w with { Title = "Sign in" });
        rig.Sweep();
        rig.Sweep();
        Assert.True(rig.Get(100).Visible);
        Assert.Equal(AppWindow | 0x200, rig.Get(100).ExStyle);
        Assert.Equal(1, rig.Windows.Count("front 100"));
    }

    [Fact]
    public void TheShellComesBackWithItsOriginalStyle()
    {
        var rig = WithSteam();
        const long original = AppWindow | 0x100 | 0x200000;
        rig.Add(100, 10, "Steam", exStyle: original);
        rig.Sweep();
        Assert.False(rig.Get(100).Visible);
        Assert.NotEqual(original, rig.Get(100).ExStyle);

        rig.Engine.Reveal(new HashSet<int> { 10 });
        Assert.True(rig.Get(100).Visible);
        Assert.Equal(original, rig.Get(100).ExStyle);
        Assert.Equal(1, rig.Windows.Count("front 100"));
    }

    [Fact]
    public void StylesComeBackWithoutChangingWhetherAWindowIsVisible()
    {
        var rig = WithSteam();
        const long original = AppWindow | 0x100;
        rig.Add(100, 10, "Steam", exStyle: original);
        rig.Sweep();
        rig.Engine.RestoreTrackedStyles();
        Assert.Equal(original, rig.Get(100).ExStyle);
        Assert.False(rig.Get(100).Visible);
        // Restored once: nothing to restore the second time.
        var calls = rig.Windows.Calls.Count;
        rig.Engine.RestoreTrackedStyles();
        Assert.Equal(calls, rig.Windows.Calls.Count);
    }

    [Fact]
    public void RevealLeavesNotificationsHiddenAndOpensNoWindowTheClientNeverShowed()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "Friend is online", 360, 120, exStyle: StoreWindowRules.WsExNoActivate | StoreWindowRules.WsExTopmost | StoreWindowRules.WsExToolWindow);
        rig.Add(101, 10, "TrayIconClass", 400, 430, visible: false, exStyle: 0);   // never shown by the client
        rig.Add(102, 10, "", 1000, 700, visible: false);                            // an untitled helper surface
        rig.Add(103, 10, "Steam", 1000, 700, visible: false);                       // the main window, started hidden
        rig.Sweep();
        rig.Engine.Reveal(new HashSet<int> { 10 });
        Assert.False(rig.Get(100).Visible);
        Assert.False(rig.Get(101).Visible);
        Assert.False(rig.Get(102).Visible);
        Assert.True(rig.Get(103).Visible);
    }

    [Fact]
    public void AnAntiCheatProcessIsNeverTouched()
    {
        var rig = new Rig();
        // Under a client's folder, with a big window: still never touched, never revealed, never even looked up by path.
        rig.Program(20, "vgc", @"C:\Riot Games\Riot Client\vgc.exe");
        rig.Program(21, "EasyAntiCheat_EOS", @"C:\Users\u\AppData\Local\Epic Games\Launcher\EasyAntiCheat_EOS.exe");
        rig.Program(22, "BEService", @"C:\Program Files (x86)\Steam\BEService.exe");
        rig.Program(23, "start_protected_game", @"C:\Program Files (x86)\Steam\start_protected_game.exe");
        rig.Program(24, "Vanguard", @"C:\Riot Games\Riot Client\Vanguard.exe");
        for (var i = 0; i < 5; i++) rig.Add(200 + i, 20 + i, "Anti-cheat", 1000, 700);
        rig.Sweep();
        rig.Engine.Reveal(new HashSet<int> { 20, 21, 22, 23, 24 });
        Assert.Empty(rig.Windows.Calls);
        Assert.All(Enumerable.Range(200, 5), handle => Assert.True(rig.Get(handle).Visible));
    }

    [Fact]
    public void ARiotGameWindowIsNeverTouched()
    {
        var rig = new Rig();
        rig.Program(30, "RiotClientUx", RiotUx);
        rig.Program(31, "VALORANT-Win64-Shipping", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe");
        rig.Program(32, "LeagueClientUx", @"C:\Riot Games\League of Legends\LeagueClientUx.exe");
        // Even a game that was installed under the client's own folder.
        rig.Program(33, "League of Legends", @"C:\Riot Games\Riot Client\League of Legends.exe");
        rig.Add(300, 30, "Riot Client");
        rig.Add(301, 31, "VALORANT");
        rig.Add(302, 32, "League of Legends");
        rig.Add(303, 33, "League of Legends");
        rig.Sweep();
        rig.Engine.Reveal(new HashSet<int> { 30, 31, 32, 33 });
        // The Riot Client's own window is the only one touched.
        Assert.True(rig.Windows.Touched(300));
        Assert.False(rig.Windows.Touched(301));
        Assert.False(rig.Windows.Touched(302));
        Assert.False(rig.Windows.Touched(303));
        Assert.All(new nint[] { 301, 302, 303 }, handle => Assert.True(rig.Get(handle).Visible));
    }

    [Fact]
    public void TheClassOfEachKindOfWindowIsLoggedOnceForTheClient()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "Steam", className: "SDL_app");
        rig.Add(101, 10, "Steam", className: "SDL_app");
        rig.Add(102, 10, "Sign in", 400, 400, className: "SDL_app");
        rig.Sweep();
        rig.Sweep();
        Assert.Single(rig.Log, line => line.Contains("Steam") && line.Contains("SDL_app") && line.Contains("Shell"));
        Assert.Single(rig.Log, line => line.Contains("SDL_app") && line.Contains("Prompt"));
        // Titles are never written to the log.
        Assert.DoesNotContain(rig.Log, line => line.Contains("Sign in"));
    }

    [Fact]
    public void AWindowThatIsGoneIsForgottenAndAReusedHandleIsBroughtForwardAgain()
    {
        var rig = WithSteam();
        rig.Add(100, 10, "Sign in", 400, 400);
        rig.Sweep();
        rig.Windows.All.Clear();
        rig.Sweep();
        rig.Add(100, 10, "Sign in", 400, 400);
        rig.Sweep();
        Assert.Equal(2, rig.Windows.Count("front 100"));
    }

    [Fact]
    public void PromptsAndMainWindowsAreToldApartForALaunchWaitingOnTheClient()
    {
        var rig = new Rig();
        rig.Program(30, "RiotClientUx", RiotUx);
        rig.Add(300, 30, "Riot Client");
        var ids = new HashSet<int> { 30 };
        Assert.False(rig.Engine.PromptVisible(StoreKind.Riot, ids));
        rig.Add(301, 30, "Sign in", 420, 520);
        Assert.True(rig.Engine.PromptVisible(StoreKind.Riot, ids));
        rig.Windows.Update(301, w => w with { Visible = false });
        Assert.False(rig.Engine.PromptVisible(StoreKind.Riot, ids));
        Assert.Equal([StoreKind.Riot], rig.Engine.VisibleShells(new HashSet<StoreKind> { StoreKind.Riot, StoreKind.Steam }, ids));
    }
}
