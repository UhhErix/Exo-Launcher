using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ExoCapture.Live;
using ExoCapture.Ui;

namespace ExoCapture.Tests;

/// <summary>
/// The line Exo speaks. Ways it could fail: a field Exo sends is read under another name or type, so a setting never
/// takes; a reply or event lacks a field Exo's CaptureAddon reads (buffering, recording, hotkeys.*.ok, printScreenOk,
/// replayMemoryBudget), so Capture shows stale state; an unknown method isn't answered "Unknown request." (Exo maps that
/// to "needs updating"); the folder name or item id differs from Exo's own SafeCaptureName and CaptureId, so a capture
/// is listed under no game or its thumbnail never shows; Go Live's frame header or the voice feed's packet header is
/// laid out differently on the two sides.
/// </summary>
public sealed class ProtocolTests
{
    [Fact]
    public void ConfigureReadsWhatExoSends()
    {
        // MainWindow.CaptureParameters, field for field.
        var p = JsonNode.Parse("""
            {"quality":70,"replaySeconds":120,"maxFps":120,"replayMode":"always","cursor":false,"folder":"C:\\Caps","thumbs":"C:\\Data\\thumbs",
             "printScreen":true,"callVoices":true,"mic":"{0.0.1.00000000}.{abc}","cleanClips":true,"font":"C:\\f.ttf",
             "hotkeys":{"saveReplay":"Ctrl+Alt+F10","record":"Ctrl+Alt+F9","snip":"Ctrl+Alt+F8","screenshot":"Ctrl+Alt+F7"},"replayMaxBytes":805306368}
            """);
        var c = Config.From(p, new Config());
        Assert.Equal(70, c.Quality);
        Assert.Equal(120, c.ReplaySeconds);
        Assert.Equal(120, c.MaxFps);
        Assert.Equal("always", c.ReplayMode);
        Assert.False(c.Cursor);
        Assert.Equal(@"C:\Caps", c.Folder);
        Assert.Equal(@"C:\Data\thumbs", c.Thumbs);
        Assert.True(c.PrintScreen);
        Assert.True(c.CallVoices);
        Assert.True(c.CleanClips);
        Assert.Equal("{0.0.1.00000000}.{abc}", c.Mic);
        Assert.Equal(805306368L, c.ReplayMaxBytes);
        Assert.Equal("Ctrl+Alt+F7", c.Hotkeys["screenshot"]);
    }

    [Fact]
    public void ConfigureKeepsWhatItIsNotSentAndClampsTheRest()
    {
        var first = Config.From(JsonNode.Parse("""{"quality":80,"replayMode":"off"}"""), new Config());
        var next = Config.From(JsonNode.Parse("""{"quality":500,"replaySeconds":2,"maxFps":90,"replayMode":"sometimes"}"""), first);
        Assert.Equal(100, next.Quality);
        Assert.Equal(5, next.ReplaySeconds);
        Assert.Equal(60, next.MaxFps); // 60, or 120
        Assert.Equal("off", next.ReplayMode); // an unknown mode keeps the one before
        // An older Exo sends no screenshot key: the add-on's own default stays.
        Assert.Equal("Ctrl+Alt+F7", next.Hotkeys["screenshot"]);
    }

    [Theory]
    [InlineData("Ctrl+Alt+F10", Native.Win32.MOD_CONTROL | Native.Win32.MOD_ALT, 0x79u)]
    [InlineData("Ctrl+Alt+F7", Native.Win32.MOD_CONTROL | Native.Win32.MOD_ALT, 0x76u)]
    [InlineData("Alt+Shift+S", Native.Win32.MOD_ALT | Native.Win32.MOD_SHIFT, (uint)'S')]
    [InlineData("PrintScreen", 0u, 0x2Cu)]
    [InlineData("ctrl+9", Native.Win32.MOD_CONTROL, (uint)'9')]
    public void HotkeysParseAsExoWritesThem(string text, uint modifiers, uint key)
    {
        var hotkey = Hotkey.Parse(text);
        Assert.NotNull(hotkey);
        Assert.Equal(modifiers, hotkey.Value.Modifiers);
        Assert.Equal(key, hotkey.Value.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Banana")]
    [InlineData("F25")]
    public void ABadHotkeyIsNone(string text) => Assert.Null(Hotkey.Parse(text));

    [Theory]
    // MainWindow.SafeCaptureName's cases: what Windows refuses becomes a space, dots and spaces trimmed, 80 at most.
    [InlineData("Halo: Infinite", "Halo  Infinite")]
    [InlineData("What?.", "What")]
    [InlineData("   ", "Capture")]
    [InlineData("a/b\\c", "a b c")]
    public void FolderNamesMatchExos(string title, string folder) => Assert.Equal(folder, Controller.SafeName(title));

    [Fact]
    public void ALongTitleIsCutLikeExoCutsIt()
    {
        var title = new string('x', 79) + " .yz" + new string('q', 20);
        Assert.Equal(new string('x', 79), Controller.SafeName(title));
    }

    [Fact]
    public void ItemIdsMatchExosFnv1a()
    {
        // FNV-1a 64 of "a" is af63dc4c8601ec8c; Exo's CaptureId is the same function over the path's UTF-8.
        Assert.Equal("af63dc4c8601ec8c", Controller.CaptureId("a"));
        Assert.Equal(16, Controller.CaptureId(@"C:\Users\x\Videos\Exo Captures\Desktop\2026-01-01 10.00.00 clip.mp4").Length);
    }

    [Fact]
    public void GoLiveHeadersReadBackAsExoReadsThem()
    {
        var header = new byte[16];
        GoLive.Header(header, 123_456, key: true, time: 9_876_543_210);
        // GoLiveCapture.ParseHeader: u32 length, u32 flags & 1, i64 time, little-endian.
        Assert.Equal(123_456u, BitConverter.ToUInt32(header, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(header, 4) & 1);
        Assert.Equal(9_876_543_210L, BitConverter.ToInt64(header, 8));
        GoLive.Header(header, 5, key: false, time: 1);
        Assert.Equal(0u, BitConverter.ToUInt32(header, 4) & 1);
    }

    [Fact]
    public void VoiceFeedHeadersAreRecordedVoicesPackets()
    {
        // RecordedVoice.Send: i64 time, i32 sample frames (960 for 20 ms), i32 channels (2).
        var header = new byte[16];
        BitConverter.TryWriteBytes(header.AsSpan(0, 8), 1234567L);
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), 960);
        BitConverter.TryWriteBytes(header.AsSpan(12, 4), 2);
        Assert.Equal((1234567L, 960, 2), VoiceFeed.Parse(header));
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), 0);
        Assert.Null(VoiceFeed.Parse(header));
    }

    /// <summary>The built add-on itself, over its pipes: ready first, every reply carrying its id, an unknown method refused.</summary>
    [Fact]
    public void TheAddonAnswersOverItsPipes()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "ExoCapture.exe");
        Assert.True(File.Exists(exe), "the add-on is built beside the tests");
        var folder = Directory.CreateTempSubdirectory("exo-capture-test").FullName;
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = folder,
        })!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        try
        {
            var ready = Line(process);
            Assert.Equal("ready", ready.Str("event"));
            var data = ready["data"]!;
            foreach (var field in new[] { "buffering", "recording", "printScreenOk", "replayMemoryBudget", "cleansClips" })
                Assert.True(data[field] is JsonValue, field);
            Assert.True(data["hotkeys"]?["saveReplay"]?["ok"] is JsonValue);

            Send(process, """{"id":7,"method":"no.such.thing","params":{}}""");
            var refused = Line(process, 7);
            Assert.Equal("Unknown request.", refused.Str("error"));

            var configure = new JsonObject
            {
                ["id"] = 8,
                ["method"] = "configure",
                ["params"] = new JsonObject { ["replayMode"] = "off", ["folder"] = folder, ["thumbs"] = Path.Combine(folder, "thumbs"), ["hotkeys"] = new JsonObject() },
            };
            Send(process, configure.ToJsonString());
            var state = Line(process, 8)["result"]!;
            Assert.False(state["buffering"]!.GetValue<bool>());
            Assert.False(state["recording"]!.GetValue<bool>());

            Send(process, """{"id":9,"method":"replay.save","params":{}}""");
            Assert.Equal("Instant replay is off", Line(process, 9).Str("error"));

            Send(process, """{"id":10,"method":"trim","params":{"path":"C:\\Windows\\win.ini"}}""");
            Assert.Equal("That file is not in the captures folder.", Line(process, 10).Str("error"));

            process.StandardInput.Close(); // Exo going: the add-on ends by itself
            Assert.True(process.WaitForExit(15_000));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.StandardInput.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static void Send(Process process, string line)
    {
        process.StandardInput.WriteLine(line);
        process.StandardInput.Flush();
    }

    /// <summary>The next line (or, given an id, the reply with that id; events in between are skipped).</summary>
    private static JsonNode Line(Process process, long? id = null)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var read = process.StandardOutput.ReadLineAsync();
            if (!read.Wait(TimeSpan.FromSeconds(30)) || read.Result is not { } line) break;
            var node = JsonNode.Parse(line)!;
            if (id is null || node["id"] is JsonValue v && v.GetValue<double>() == id) return node;
        }
        throw new TimeoutException("no line from the add-on");
    }
}
