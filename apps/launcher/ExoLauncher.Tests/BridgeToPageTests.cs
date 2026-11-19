using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// What the bridge lets the page see of a game and of a failure: never a file path (AGENTS.md). Ways this could fail: a new
/// field copies an adapter's string straight across, a local game's program path rides in launchTarget, an exception's own
/// message (a path, a host, a JSON path) is sent as the error.
/// </summary>
public sealed class BridgeToPageTests
{
    private const string Program = @"C:\Users\Someone\Games\Portable Game\game.exe";

    private static GameEntry LocalGame() => new()
    {
        Id = "local:1",
        Title = "Portable Game",
        Store = StoreKind.Local,
        Installed = true,
        Path = @"C:\Users\Someone\Games\Portable Game",
        LaunchTarget = Program,
        Status = @"Ready (C:\Users\Someone\Games\Portable Game)",
        LaunchNote = @"Starts " + Program + " directly.",
        Deps = [@"vcredist at \\server\share\vc.exe", "DirectX"],
        Variants =
        [
            new GameVariant { Id = "local:1", Store = StoreKind.Local, Installed = true, Path = @"C:\Games\x", LaunchTarget = Program, Status = @"D:\x\y.log failed" },
        ],
    };

    [Fact]
    public void NoFieldOfAMappedGameCarriesAFilePath()
    {
        var json = JsonSerializer.Serialize(WebHostBridge.GameForPage(LocalGame(), new GameRunState(false, false), _ => new GameRunState(false, false)));
        using var doc = JsonDocument.Parse(json);
        var strings = new List<(string Path, string Value)>();
        Walk(doc.RootElement, "", strings);
        Assert.NotEmpty(strings);
        foreach (var (path, value) in strings)
            Assert.False(Regex.IsMatch(value, @"[A-Za-z]:[\\/]|\\\\"), $"{path} reaches the page with a file path: {value}");
    }

    [Fact]
    public void AStoreIdStillReachesThePage()
    {
        var steam = new GameEntry { Id = "steam:620", Title = "Portal 2", Store = StoreKind.Steam, LaunchTarget = "620", Status = "Ready" };
        var json = JsonSerializer.Serialize(WebHostBridge.GameForPage(steam, new GameRunState(false, false), _ => new GameRunState(false, false)));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("620", doc.RootElement.GetProperty("launchTarget").GetString());
        Assert.Equal("Ready", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ATextWithAPathInItIsDroppedWholeAndOtherTextStays()
    {
        Assert.Equal("", WebHostBridge.PageText(@"Could not read C:\Program Files\Some Game\a.dll next to the game."));
        Assert.Equal("", WebHostBridge.PageText(@"Missing \\server\share\x"));
        Assert.Equal("Ready to play", WebHostBridge.PageText("Ready to play"));
        Assert.Equal("", WebHostBridge.PageText(null));
    }

    private static Exception Thrown(Action what)
    {
        try { what(); } catch (Exception ex) { return ex; }
        throw new InvalidOperationException("nothing was thrown");
    }

    // Stands for a service: Exo's own code throwing a sentence it wrote for the player.
    private static void ExoServiceFails(string sentence) => throw new InvalidOperationException(sentence);

    [Fact]
    public void AnInvalidOperationExoThrewKeepsItsSentenceButTheSameTypeFromAFrameworkDoesNot()
    {
        const string generic = "Exo could not finish that.";
        Assert.Equal("Windows doesn't see the Xbox controller. Connect it and try again.",
            WebHostBridge.UserMessage(Thrown(() => ExoServiceFails("Windows doesn't see the Xbox controller. Connect it and try again.")), generic));
        Assert.Equal("That backup is not Exo's.",
            WebHostBridge.UserMessage(Thrown(() => throw new InvalidDataException("That backup is not Exo's.")), generic));
        // The same exception types thrown by the framework say nothing of their own.
        var frameworkInvalidOperation = Thrown(() => new List<int>().First());
        Assert.IsType<InvalidOperationException>(frameworkInvalidOperation);
        Assert.Equal(generic, WebHostBridge.UserMessage(frameworkInvalidOperation, generic));
        var frameworkInvalidData = Thrown(() => new System.IO.Compression.ZipArchive(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24])));
        Assert.IsType<InvalidDataException>(frameworkInvalidData);
        Assert.Equal(generic, WebHostBridge.UserMessage(frameworkInvalidData, generic));
    }

    [Fact]
    public void ASentenceExoThrewStillLosesAPathTheUserNameOrAToken()
    {
        const string generic = "Exo could not finish that.";
        Assert.Equal(generic, WebHostBridge.UserMessage(Thrown(() => ExoServiceFails(@"Cannot use C:\Users\Someone\x here.")), generic));
        Assert.Equal(generic, WebHostBridge.UserMessage(Thrown(() => ExoServiceFails("Could not open the folder of " + Environment.UserName + ".")), generic));
        Assert.Equal(generic, WebHostBridge.UserMessage(Thrown(() => ExoServiceFails("The sign-in 0123456789abcdef0123456789abcdef0123 was refused.")), generic));
        Assert.Equal(generic, WebHostBridge.UserMessage(Thrown(() => ExoServiceFails("")), generic));
    }

    [Fact]
    public void TenRealServiceSentencesPassAsWrittenWhenExoThrowsThem()
    {
        // Ten of the plain sentences Exo's services throw today (picked from the code), thrown from Exo code, through the same check.
        string[] sentences =
        [
            "Windows doesn't see the Xbox controller. Connect it and try again.",
            "Exo doesn't read this controller yet.",
            "Exo couldn't open the controller.",
            "Legendary did not start.",
            "gogdl did not start.",
            "Exo Capture did not start.",
            "Exo Capture is not running.",
            "Update redirected too many times.",
            "Windows did not start the verified installer.",
            "Your graphics driver didn't answer. Try again.",
        ];
        foreach (var sentence in sentences)
            Assert.Equal(sentence, WebHostBridge.UserMessage(Thrown(() => ExoServiceFails(sentence)), "generic"));
    }

    [Fact]
    public void OtherExceptionsSayNothingOfTheirOwn()
    {
        const string generic = "Exo could not finish that.";
        foreach (Exception other in new Exception[]
        {
            new IOException(@"The process cannot access 'C:\Users\Someone\a.db'."),
            new JsonException("The JSON value could not be converted. Path: $.secret.name"),
            new System.Runtime.InteropServices.COMException(@"Cannot find D:\z\q.dll", unchecked((int)0x80070002)),
            new ArgumentException("Value cannot be null. (Parameter 'token')"),
            new InvalidOperationException("never thrown, so not known to be Exo's"),
        })
            Assert.Equal(generic, WebHostBridge.UserMessage(other, generic));
    }
    [Fact]
    public void CommonWindowsFailuresSayWhatHappenedAndWhatToDo()
    {
        Assert.Equal(WebHostBridge.DriveFull, WebHostBridge.UserMessage(new IOException(@"There is not enough space on the disk. : 'D:\x'", unchecked((int)0x80070070)), "g"));
        Assert.Equal(WebHostBridge.NoInternet, WebHostBridge.UserMessage(new HttpRequestException("No such host is known. (internal.example:443)"), "g"));
        Assert.Equal(WebHostBridge.FileInUse, WebHostBridge.UserMessage(new IOException(@"sharing violation on 'C:\Games\x.pak'", unchecked((int)0x80070020)), "g"));
        Assert.Equal(WebHostBridge.FileInUse, WebHostBridge.UserMessage(new IOException("lock violation", unchecked((int)0x80070021)), "g"));
        Assert.Equal(WebHostBridge.AccessRefused, WebHostBridge.UserMessage(new UnauthorizedAccessException(@"Access to 'C:\x' is denied."), "g"));
        Assert.Equal(WebHostBridge.AccessRefused, WebHostBridge.UserMessage(new System.ComponentModel.Win32Exception(5), "g"));
        Assert.Equal(WebHostBridge.PromptDeclined, WebHostBridge.UserMessage(new System.ComponentModel.Win32Exception(1223), "g"));
        Assert.Equal(WebHostBridge.TookTooLong, WebHostBridge.UserMessage(new TimeoutException(@"Timed out at \\server\x"), "g"));
        Assert.Equal("g", WebHostBridge.UserMessage(new System.ComponentModel.Win32Exception(2), "g"));
        // None of them carries a path, a host or the exception's own words, so each passes the page's own filter.
        foreach (var line in new[] { WebHostBridge.DriveFull, WebHostBridge.FileInUse, WebHostBridge.AccessRefused, WebHostBridge.PromptDeclined, WebHostBridge.NoInternet, WebHostBridge.TookTooLong })
            Assert.Equal(line, WebHostBridge.Safe(line));
    }

    [Fact]
    public void ASilentDeviceSaysSoInTime()
    {
        Assert.Equal("The device did not answer in time.", WebHostBridge.DeviceFailureMessage(new TimeoutException(@"Timed out reading \\?\hid#vid_1234")));
        Assert.Equal("The device did not answer.", WebHostBridge.DeviceFailureMessage(new IOException(@"\\?\hid#vid_1234 failed")));
    }

    [Theory]
    [InlineData("Sale details at https://store.steampowered.com/app/620/ today", "Sale details at https://store.steampowered.com/app/620/ today")]
    [InlineData("See http://example.com/Users/a/b", "See http://example.com/Users/a/b")]
    [InlineData(@"Open \\server\share\games", "")]
    [InlineData("Open //server/share/games", "")]
    [InlineData("Saved in C:/Users/Someone/Games", "")]
    [InlineData("Saved in /Users/Someone/Games", "")]
    [InlineData("Saved in ~/Games/x", "")]
    public void AUrlIsNotAPathButSharesAndProfilePathsAre(string text, string expected) =>
        Assert.Equal(expected, WebHostBridge.PageText(text));

    private static void Walk(JsonElement e, string path, List<(string, string)> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: into.Add((path, e.GetString()!)); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, path + "." + p.Name, into); break;
            case JsonValueKind.Array: var i = 0; foreach (var x in e.EnumerateArray()) Walk(x, path + "[" + i++ + "]", into); break;
        }
    }
}
