using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// A raw UI-thread callback (a DispatcherQueue post, a DispatcherQueueTimer tick, a WebView2 or AppSDK event) that throws
/// fails fast without reaching Application.UnhandledException, so its crash report is never written. Every such callback
/// runs through <see cref="UiGuard"/>. The source test below fails when a new raw callback is added unwrapped; the scanner
/// tests show it catches the raw forms. XAML-routed events are not scanned: they already reach the unhandled handler.
/// </summary>
public sealed class UiGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-uiguard-" + Guid.NewGuid().ToString("N"));
    private readonly Action<Exception> _previous = UiGuard.Record;

    public UiGuardTests()
    {
        Directory.CreateDirectory(_dir);
        UiGuard.Record = ex => new PendingReports(_dir).Write(ex);
    }

    public void Dispose()
    {
        UiGuard.Record = _previous;
        try { DeletionGate.DeleteDirectory(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void AnActionThatThrowsWritesOnePendingFileAndRethrowsTheSameException()
    {
        var boom = new InvalidOperationException("boom");

        var thrown = Assert.Throws<InvalidOperationException>(() => UiGuard.Run(() => { throw boom; }));

        Assert.Same(boom, thrown);
        Assert.Single(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public void AFunctionThatReturnsPassesItsValueThroughAndRecordsNothing()
    {
        Assert.Equal(3, UiGuard.Run(() => 3));

        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public async Task AnAsyncFunctionThatThrowsIsRecordedAndRethrown()
    {
        var boom = new InvalidOperationException("late");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => UiGuard.RunAsync(() => Task.FromException(boom)));

        Assert.Same(boom, thrown);
        Assert.Single(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public void ARawTryEnqueueIsReported()
    {
        var found = UiCallbackScanner.Scan("Snippet.cs", "queue.TryEnqueue(() => Refresh());", []);

        Assert.Single(found);
    }

    [Fact]
    public void AWrappedTryEnqueueIsAccepted()
    {
        var found = UiCallbackScanner.Scan("Snippet.cs", "queue.TryEnqueue(() => UiGuard.Run(() => Refresh()));", []);

        Assert.Empty(found);
    }

    [Fact]
    public void AnEnqueueThroughTheHelperIsAccepted()
    {
        var found = UiCallbackScanner.Scan("Snippet.cs", "queue.Enqueue(Refresh);", []);

        Assert.Empty(found);
    }

    [Fact]
    public void ARawWebViewHandlerIsReportedAndAWrappedOneIsNot()
    {
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "core.NavigationCompleted += (_, e) => Done(e);", []));
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "core.WebMessageReceived += OnMessage;", []));
        Assert.Empty(UiCallbackScanner.Scan("Snippet.cs", "core.NavigationCompleted += (_, e) => UiGuard.Run(() => Done(e));", []));
    }

    [Fact]
    public void ARawTimerTickIsReportedAndAWrappedOneIsNot()
    {
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "timer.Tick += (_, _) =>\n{\n    Save();\n};", []));
        Assert.Empty(UiCallbackScanner.Scan("Snippet.cs", "timer.Tick += (_, _) => UiGuard.Run(() => Save());", []));
    }

    [Fact]
    public void AUiGuardNamedOnlyInACommentGuardsNothing()
    {
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "queue.TryEnqueue(() => Refresh() /* UiGuard.Run later */);", []));
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "core.NavigationCompleted += (_, e) =>\n{\n    // wrapped by UiGuard.Run upstream\n    Done(e);\n};", []));
    }

    [Fact]
    public void WorkBeforeOrAroundTheGuardIsReported()
    {
        // The first statement runs unguarded; the guard only covers what comes after it.
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "timer.Tick += (_, _) =>\n{\n    Save();\n    UiGuard.Run(() => Log());\n};", []));
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "queue.TryEnqueue(() => Refresh(UiGuard.Run(() => 1)));", []));
        Assert.Single(UiCallbackScanner.Scan("Snippet.cs", "core.ProcessFailed += (_, e) => Report(e, x => queue.Enqueue(x));", []));
    }

    [Fact]
    public void AGuardThatIsTheWholeBodyIsAccepted()
    {
        Assert.Empty(UiCallbackScanner.Scan("Snippet.cs", "timer.Tick += (_, _) =>\n{\n    UiGuard.Run(() => { Save(); Log(); });\n};", []));
        Assert.Empty(UiCallbackScanner.Scan("Snippet.cs", "core.NavigationCompleted += async (s, e) => await UiGuard.RunAsync(() => DoneAsync(e));", []));
        Assert.Empty(UiCallbackScanner.Scan("Snippet.cs", "core.ProcessFailed += (_, e) => _queue.Enqueue(() => Recover(e));", []));
    }

    [Fact]
    public void AnAllowlistedXamlEventIsNotReported()
    {
        var allowlist = new[] { ("Snippet.cs", "Tick", "XAML DispatcherTimer; reaches Application.UnhandledException") };

        Assert.Empty(UiCallbackScanner.Scan("Snippet.cs", "timer.Tick += (_, _) => Save();", allowlist));
    }

    [Fact]
    public void EveryRawCallbackInTheLauncherIsWrappedOrAllowlisted()
    {
        var root = Repo.PathOf("ExoLauncher");
        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
            found.AddRange(UiCallbackScanner.Scan(relative, File.ReadAllText(path), UiCallbackScanner.Allowlist));
        }

        Assert.Empty(found);
    }

    [Fact]
    public void EveryAllowlistEntryStillMatchesItsSource()
    {
        var root = Repo.PathOf("ExoLauncher");
        foreach (var (file, name, reason) in UiCallbackScanner.Allowlist)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            var text = File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Matches(@"\b" + name + @"\s*\+=", text);
        }
    }
}

/// <summary>
/// Finds raw UI-thread callbacks in source text that are not wrapped in <see cref="UiGuard"/>. Text-based: it reads the
/// argument or handler up to its closing bracket or semicolon, so it does not parse C#.
/// </summary>
internal static class UiCallbackScanner
{
    /// <summary>Events raised on the UI thread as raw callbacks: WebView2, the AppSDK pointer source and AppInstance, and DispatcherQueueTimer ticks.</summary>
    internal static readonly string[] RawEvents =
    [
        "WebMessageReceived", "NavigationStarting", "NavigationCompleted", "NewWindowRequested", "ProcessFailed",
        "ContextMenuRequested", "ScriptDialogOpening", "DOMContentLoaded", "PermissionRequested",
        "IsDocumentPlayingAudioChanged", "WindowCloseRequested", "WebResourceRequested", "ServerCertificateErrorDetected",
        "ProcessInfosChanged", "BrowserProcessExited", "ContainsFullScreenElementChanged", "RegionsChanged",
        "PointerPressed", "PointerReleased", "PointerEntered", "PointerExited", "Activated", "Tick",
    ];

    /// <summary>Raw callbacks that are deliberately not wrapped, with the reason. Paths are relative to ExoLauncher/.</summary>
    internal static readonly (string File, string Event, string Reason)[] Allowlist =
    [
        ("MainWindow.xaml.cs", "Activated", "Window.Activated is a XAML event; it reaches Application.UnhandledException"),
        ("Helpers/ExoConnectedTheme.cs", "PointerEntered", "XAML Button pointer event (UIElement), not an AppSDK source"),
        ("Helpers/ExoConnectedTheme.cs", "PointerExited", "XAML Button pointer event (UIElement), not an AppSDK source"),
        ("MainWindow.Confirm.cs", "PointerEntered", "XAML Button pointer event (UIElement), not an AppSDK source"),
        ("MainWindow.Confirm.cs", "PointerExited", "XAML Button pointer event (UIElement), not an AppSDK source"),
        ("MainWindow.ConnectedApps.cs", "NavigationStarting", "method group, so the -= at unsubscribe still matches; Navigation's body is wrapped where it is declared"),
        ("MainWindow.ConnectedApps.cs", "ProcessFailed", "method group, so the -= at unsubscribe still matches; Failed's body is wrapped where it is declared"),
        ("Services/StoreGifts.cs", "NavigationCompleted", "method group, so the -= in finally still matches; Completed's body is wrapped where it is declared"),
        ("Services/WebHostBridge.cs", "WebMessageReceived", "method group, so the -= in Detach still matches; OnMessage's body is wrapped where it is declared"),
        ("Services/WebHostBridge.cs", "NavigationStarting", "method group, so the -= in Detach still matches; OnBridgeNavigationStarting's body is wrapped where it is declared"),
        ("MainWindow.xaml.cs", "Tick", "method group, so the -= in StopWebViewWindowGuard still matches; OnWebViewWindowGuardTick's body is wrapped where it is declared"),
        ("Services/WebHostBridge.Gifts.cs", "Tick", "method group, so the -= in StopGiftsTimer still matches; OnGiftsTimerTick's body is wrapped where it is declared"),
    ];

    /// <summary>Returns one message per raw callback that is not wrapped and not allowlisted.</summary>
    internal static List<string> Scan(string file, string text, IEnumerable<(string File, string Event, string Reason)> allowlist)
    {
        var found = new List<string>();
        var allowed = allowlist.ToList();
        // Comments go first (kept as spaces, so line numbers stay): a UiGuard named in a comment guards nothing.
        text = Regex.Replace(text, @"//[^\n]*|/\*[\s\S]*?\*/", m => Regex.Replace(m.Value, @"[^\n]", " "));

        if (file != "Helpers/UiGuard.cs")
        {
            foreach (Match match in Regex.Matches(text, @"TryEnqueue\s*\("))
            {
                var args = Span(text, match.Index + match.Length);
                if (!WrapsItsBody(args)) found.Add(Where(file, text, match.Index, "raw TryEnqueue"));
            }
        }

        foreach (var name in RawEvents)
        {
            if (allowed.Any(entry => entry.File == file && entry.Event == name)) continue;
            foreach (Match match in Regex.Matches(text, @"\." + name + @"\s*\+="))
            {
                var handler = Span(text, match.Index + match.Length);
                if (!WrapsItsBody(handler)) found.Add(Where(file, text, match.Index, "raw " + name + " handler"));
            }
        }

        return found;
    }

    /// <summary>
    /// True when the callback is a lambda whose whole body runs inside UiGuard (or goes through the queue helper that
    /// wraps it): the body is `UiGuard.Run(...)`, `await UiGuard.RunAsync(...)`, `x.Enqueue(...)`, or a block whose only
    /// statement is one of those. A UiGuard call after other work, or nested inside it, leaves that work unguarded.
    /// </summary>
    private static bool WrapsItsBody(string callback)
    {
        var arrow = callback.IndexOf("=>", StringComparison.Ordinal);
        if (arrow < 0) return false;
        var body = callback[(arrow + 2)..].Trim();
        if (body.StartsWith('{'))
        {
            var inner = body[1..].TrimEnd();
            if (inner.EndsWith('}')) inner = inner[..^1];
            inner = inner.Trim();
            // One statement only: the first statement, up to its own semicolon, is all there is.
            if (Span(inner, 0).Trim().Length != inner.TrimEnd().TrimEnd(';').Trim().Length) return false;
            body = inner;
        }
        if (body.StartsWith("await ", StringComparison.Ordinal)) body = body[6..].TrimStart();
        return body.StartsWith("UiGuard.", StringComparison.Ordinal) || Regex.IsMatch(body, @"^[\w.]+\.Enqueue\(");
    }

    /// <summary>Text from <paramref name="start"/> up to the bracket or semicolon that closes it at depth zero.</summary>
    private static string Span(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']')
            {
                if (--depth < 0) return text[start..i];
            }
            else if (c == ';' && depth == 0) return text[start..i];
        }
        return text[start..];
    }

    private static string Where(string file, string text, int index, string what)
    {
        var line = 1;
        for (var i = 0; i < index; i++) if (text[i] == '\n') line++;
        return $"{file}:{line}: {what}";
    }
}
