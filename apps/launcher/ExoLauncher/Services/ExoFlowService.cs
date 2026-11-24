using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Services;

internal static class ExoFlowService
{
    internal const string PipeName = "exo-flow-v1";

    internal sealed record ExoFlowProfile(
        string Multiplier = "fixed_2x",
        uint TargetHz = 120,
        bool HudMaskEnabled = true,
        float HudSensitivity = 0.55f,
        string Scaler = "off",
        float Sharpness = 0.35f,
        string Backend = "auto");

    internal sealed record FlowResponse(bool Ok, string? Message);

    internal static string? FindExePath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] direct =
        [
            Path.Combine(baseDir, "exo-flow.exe"),
            Path.Combine(baseDir, "native-ui", "exo-flow.exe")
        ];
        foreach (var candidate in direct)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var rel = Path.Combine(dir.FullName, "native-ui", "target", "release", "exo-flow.exe");
            if (File.Exists(rel))
                return rel;
            var dbg = Path.Combine(dir.FullName, "native-ui", "target", "debug", "exo-flow.exe");
            if (File.Exists(dbg))
                return dbg;
            dir = dir.Parent;
        }

        return null;
    }

    internal static bool IsAvailable() => FindExePath() is not null;

    internal static bool EnsureRunning()
    {
        var exe = FindExePath();
        if (exe is null)
            return false;

        try
        {
            if (Process.GetProcessesByName("exo-flow").Length > 0)
                return true;

            var psi = new ProcessStartInfo(exe, $"--pipe {PipeName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Exo Flow sidecar launch failed: " + ex.Message);
            return false;
        }
    }

    internal static Task<FlowResponse> StartAsync(long hwnd, ExoFlowProfile? profile, CancellationToken ct)
    {
        EnsureRunning();
        var p = profile ?? new ExoFlowProfile();
        return SendAsync(
            new
            {
                op = "framegen.flow.start",
                hwnd = (ulong)Math.Max(0, hwnd),
                profile = new
                {
                    multiplier = p.Multiplier,
                    target_hz = p.TargetHz,
                    hud_mask_enabled = p.HudMaskEnabled,
                    hud_sensitivity = p.HudSensitivity,
                    scaler = p.Scaler,
                    sharpness = p.Sharpness,
                    backend = p.Backend
                }
            },
            ct);
    }

    internal static async Task StopIfRunningAsync(CancellationToken ct)
    {
        if (Process.GetProcessesByName("exo-flow").Length == 0)
            return;
        await SendAsync(new { op = "framegen.flow.stop" }, ct).ConfigureAwait(false);
    }

    internal static Task<FlowResponse> StatusAsync(CancellationToken ct)
        => SendAsync(new { op = "framegen.flow.status" }, ct);

    private static async Task<FlowResponse> SendAsync(object command, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(750, ct).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command));
            await pipe.WriteAsync(payload, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);

            var buf = new byte[16384];
            var read = await pipe.ReadAsync(buf, ct).ConfigureAwait(false);
            if (read <= 0)
                return new FlowResponse(false, "Exo Flow sidecar returned an empty response.");

            using var doc = JsonDocument.Parse(buf.AsMemory(0, read));
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var msg = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
            return new FlowResponse(ok, msg);
        }
        catch (Exception ex)
        {
            return new FlowResponse(false, ex.Message);
        }
    }
}
