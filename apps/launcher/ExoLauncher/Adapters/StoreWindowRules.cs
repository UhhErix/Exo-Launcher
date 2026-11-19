using System.Text.RegularExpressions;

namespace ExoLauncher.Adapters;

/// <summary>What a window of a store client is, from what Windows says about it (never from an exact title).</summary>
internal enum StoreWindowKind
{
    /// <summary>A question the player has to answer: never hidden, shown and brought to the front.</summary>
    Prompt,
    /// <summary>The client's main window: hidden while Exo keeps the store out of sight.</summary>
    Shell,
    /// <summary>A notification that appeared by itself: hidden.</summary>
    Toast,
    /// <summary>A zero-size or input-method window: left alone.</summary>
    Other,
}

/// <summary>One top-level window as the hider reads it (the seam's record, see <see cref="IStoreWindows"/>).</summary>
internal readonly record struct StoreWindow(
    nint Handle, int ProcessId, string ClassName, string Title, bool Visible, nint Owner,
    int Left, int Top, int Right, int Bottom, long Style, long ExStyle)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// The rules that choose what the hider does to a window, as pure functions over <see cref="StoreWindow"/>:
/// one decision table (<see cref="Table"/>, first row that matches wins) and the word list a title is checked against.
/// Titles are used only for that word list: a client that changes its title, a localised title or an untitled window
/// is still a shell, and a sign-in is never hidden.
/// </summary>
internal static class StoreWindowRules
{
    internal const int ShellMinWidth = 640;
    internal const int ShellMinHeight = 400;

    internal const long WsCaption = 0x00C00000;
    internal const long WsDlgFrame = 0x00400000;
    internal const long WsExDlgModalFrame = 0x00000001;
    internal const long WsExTopmost = 0x00000008;
    internal const long WsExToolWindow = 0x00000080;
    internal const long WsExAppWindow = 0x00040000;
    internal const long WsExNoActivate = 0x08000000;

    /// <summary>Windows' own input-method windows: shown, the 0x0 window never paints and Windows puts up a "not responding" ghost.</summary>
    internal static readonly string[] ExcludedClasses = ["IME", "MSCTFIME UI"];

    /// <summary>
    /// Words in a title that mean the player has to answer something. Matched case-insensitively as whole words; a space or
    /// a hyphen in a phrase matches either. The first group is the old agreement and redeem lists.
    /// </summary>
    internal static readonly string[] PromptWords =
    [
        "EULA", "License Agreement", "Subscriber Agreement", "End User License", "Activate", "Product Activation", "Register Product", "Redeem",
        "licence", "license", "agreement", "terms", "sign in", "signin", "log in", "login", "password", "verify", "verification",
        "two-factor", "2FA", "authenticator", "security code", "captcha", "payment", "purchase", "checkout", "activation",
        "register", "confirm", "confirmation",
    ];

    private static readonly Regex PromptPattern = new(
        @"(?<![\p{L}\p{N}])(?:" + string.Join("|", PromptWords.Select(word =>
            string.Join(@"[\s\-]+", word.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)))) + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    internal static bool HasPromptWord(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        try { return PromptPattern.IsMatch(title); }
        catch (RegexMatchTimeoutException) { return true; } // cannot tell: a prompt is the safe answer
    }

    /// <summary>The facts a row asks about, worked out once per window.</summary>
    internal readonly record struct Facts(
        bool Empty, bool ExcludedClass, bool Owned, bool PromptWord, bool DialogStyle, bool Small, bool NoActivate, bool TopmostOrToolWindow);

    internal static Facts FactsOf(StoreWindow window) => new(
        Empty: window.Width <= 0 || window.Height <= 0,
        ExcludedClass: ExcludedClasses.Contains(window.ClassName, StringComparer.Ordinal),
        Owned: window.Owner != 0,
        PromptWord: HasPromptWord(window.Title),
        DialogStyle: window.ClassName == "#32770" || (window.Style & WsCaption) == WsDlgFrame || (window.ExStyle & WsExDlgModalFrame) != 0,
        Small: window.Width < ShellMinWidth || window.Height < ShellMinHeight,
        NoActivate: (window.ExStyle & WsExNoActivate) != 0,
        TopmostOrToolWindow: (window.ExStyle & (WsExTopmost | WsExToolWindow)) != 0);

    /// <param name="Name">What the row says, in a word or two.</param>
    /// <param name="Kind">What a window that this row matches is.</param>
    /// <param name="Matches">The condition, over <see cref="Facts"/>.</param>
    /// <param name="Sample">A window that no earlier row matches and this one does (the table walk proves it).</param>
    internal sealed record Row(string Name, StoreWindowKind Kind, Func<Facts, bool> Matches, StoreWindow Sample);

    private static StoreWindow Sample(string title = "", string className = "Chrome_WidgetWin_1", nint owner = 0,
        int width = 1000, int height = 700, long style = 0x00CF0000, long exStyle = 0x00040000) =>
        new(1, 1, className, title, true, owner, 0, 0, width, height, style, exStyle);

    /// <summary>
    /// The decision table, in order; the first row that matches decides. The last row matches everything: when the rules
    /// cannot tell, a window is a prompt, because showing a notification is a smaller fault than hiding a sign-in.
    /// </summary>
    internal static readonly Row[] Table =
    [
        new("zero-size or input-method window", StoreWindowKind.Other,
            f => f.Empty || f.ExcludedClass, Sample(width: 0, height: 0)),
        new("owned by another window", StoreWindowKind.Prompt,
            f => f.Owned, Sample(owner: 9, width: 400, height: 300)),
        new("a prompt word in the title", StoreWindowKind.Prompt,
            f => f.PromptWord, Sample(title: "Sign in")),
        new("dialog style", StoreWindowKind.Prompt,
            f => f.DialogStyle, Sample(className: "#32770", width: 500, height: 300)),
        new("big, no owner, not a dialog (the main window)", StoreWindowKind.Shell,
            f => !f.Small, Sample()),
        new("small, no owner, never takes focus, topmost or tool window (a notification)", StoreWindowKind.Toast,
            f => f.NoActivate && f.TopmostOrToolWindow, Sample(width: 360, height: 120, exStyle: WsExNoActivate | WsExTopmost | WsExToolWindow)),
        new("small, no owner, takes keyboard focus", StoreWindowKind.Prompt,
            f => !f.NoActivate, Sample(width: 360, height: 120)),
        new("the rules cannot tell", StoreWindowKind.Prompt,
            _ => true, Sample(width: 360, height: 120, exStyle: WsExNoActivate)),
    ];

    internal static Row Decide(StoreWindow window)
    {
        var facts = FactsOf(window);
        return Table.First(row => row.Matches(facts));
    }

    internal static StoreWindowKind Classify(StoreWindow window) => Decide(window).Kind;
}

/// <summary>How often the hider looks, as a pure function so it can be tested.</summary>
internal static class SweepCadence
{
    internal static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan Slow = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FastFor = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The wait before the next sweep, or null when sweeping stops. Fast for the first 10 seconds after a client's process
    /// started or the hider was started for a launch, then every 2 seconds while a client runs, and stopped when none does
    /// (the event hook, the fast path, wakes it again when a client's window appears).
    /// </summary>
    internal static TimeSpan? Next(double secondsSinceStart, bool anyClientRunning) =>
        secondsSinceStart < FastFor.TotalSeconds ? Fast : anyClientRunning ? Slow : null;
}
