using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoCapture;
using ExoCapture.Native;

// Exo Capture: started hidden by Exo (Services/CaptureAddon.cs), which speaks to it over stdin and stdout (CONTRACT.md).
// `ExoCapture.exe --check <file>...` reads clips back through Windows' decoder (the self-test of what it writes).
Win32.SetProcessDpiAwarenessContext(-4); // per-monitor v2: the snip and desktop duplication see real pixels
Vortice.MediaFoundation.MediaFactory.MFStartup(true).CheckError();
AppDomain.CurrentDomain.UnhandledException += (_, e) => Host.Log("Exo Capture could not start: " + e.ExceptionObject);

if (args.Length > 0 && args[0] == "--check") return SelfTest.Check(args[1..]);
if (args.Length > 0 && args[0] == "--encoders") return SelfTest.Encoders();
if (args.Length > 1 && args[0] == "--window") return SelfTest.Window(args[1]);
if (args.Length > 2 && args[0] == "--hook") return SelfTest.Hook(args[1], args[2]);
if (args.Length > 2 && args[0] == "--ssim") return SelfTest.Compare(args[1], args[2]);

Controller controller;
try { controller = new Controller(); }
catch (Exception ex)
{
    Host.Log("Exo Capture could not start: " + ex.Message);
    return 1;
}

var gpu = "";
try { gpu = ExoCapture.Gpu.GpuDevice.Shared().Name; }
catch (Exception ex) when (ex is CaptureException or SharpGen.Runtime.SharpGenException)
{
    Host.Log($"Capture unavailable on this GPU (hr=0x{ex.HResult:X8}): {ex.Message}");
}
var ready = controller.Ready();
Host.Log($"Exo Capture on {(string.IsNullOrEmpty(gpu) ? "unavailable GPU" : gpu)}, encoder {ready.Str("encoder") ?? "chosen at the first capture"}, codec {ready.Str("codec") ?? (ready.Bool("av1", false) ? "AV1" : ready.Bool("hevc", false) ? "HEVC" : "H.264")}");
Host.Event("ready", ready);

var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var pending = new List<Task>();
while (input.ReadLine() is { } line)
{
    if (line.Length == 0) continue;
    JsonObject? message;
    try { message = JsonNode.Parse(line) as JsonObject; }
    catch (JsonException) { continue; }
    if (message is null) continue;
    var id = message["id"] is JsonValue v && v.TryGetValue<double>(out var n) ? (long)n : 0;
    var method = message.Str("method") ?? "";
    if (method == "shutdown") break;
    var parameters = message["params"];
    pending.RemoveAll(t => t.IsCompleted);
    pending.Add(Task.Run(() =>
    {
        try { Host.Reply(id, controller.Handle(method, parameters, id)); }
        catch (CaptureException ex) { Host.Fail(id, ex.Message); }
        catch (ExoCapture.Edit.Skip ex) { Host.Fail(id, ex.Message); }
        catch (Exception ex)
        {
            Host.Log($"{method} failed: {ex.GetType().Name}: {ex.Message}");
            Host.Fail(id, ex is SharpGen.Runtime.SharpGenException com ? $"{method} failed (0x{com.HResult:X8})" : $"{method} failed: {ex.Message}");
        }
    }));
}

// Exo closed its end (or asked): a recording is finished and saved before Exo Capture goes.
controller.Dispose();
Task.WaitAll([.. pending], TimeSpan.FromSeconds(5));
return 0;
