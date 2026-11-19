using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoCapture.Gpu;
using ExoCapture.Native;
using Vortice.Direct3D11;

namespace ExoCapture.Capture;

/// <summary>
/// A game's own frames, taken inside the game by OBS Studio's signed graphics hook, for a game Windows capture gets no
/// picture from (exclusive full screen). OBS's files run unchanged from <c>obs\</c> beside Exo Capture: its helper
/// finds the graphics offsets, its inject helper loads the hook the way OBS's anti-cheat compatible mode does (a window
/// hook on the game's window thread), and the hook shares each frame as a texture. This side speaks OBS's hook protocol
/// (named events, a keep-alive mutex and the 648-byte hook_info block, hook version 1) and contains no OBS code.
/// Exo decides whether a game may ever get it (no anti-cheat: Exo's own AntiCheatCheck, sent as the game's
/// <c>hook</c>); this side refuses too for Riot games, while FACEIT, ESEA, Vanguard, Easy Anti-Cheat or BattlEye run,
/// for Store apps, and when another app already captures that game.
/// </summary>
internal sealed unsafe class HookSource : IFrameSource
{
    internal const int HookInfoSize = 648;
    private readonly GpuDevice _gpu;
    private readonly uint _pid;
    private readonly nint _window, _process, _keepAlive, _ready, _stop, _restart, _init, _infoMap;
    private readonly byte* _info;
    private ID3D11Texture2D? _texture;
    private bool _flip;

    public string Kind => "(from inside the game)";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Hdr => false;
    public string? Ended { get; private set; }
    public bool HadFrame { get; private set; }

    private HookSource(GpuDevice gpu, uint pid, nint window, nint process, nint keepAlive, nint infoMap, byte* info, nint ready, nint stop, nint restart, nint init)
    {
        (_gpu, _pid, _window, _process, _keepAlive, _infoMap) = (gpu, pid, window, process, keepAlive, infoMap);
        (_ready, _stop, _restart, _init) = (ready, stop, restart, init);
        _info = info;
    }

    /// <summary>Why the hook must not go into this game, or null when it may.</summary>
    internal static string? Refusal(nint window, string? imagePath)
    {
        var path = (imagePath ?? "").ToLowerInvariant();
        if (path.Contains(@"\riot games\") || path.Contains("valorant") || path.Contains("league of legends")) return "a Riot game";
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var name = p.ProcessName.ToLowerInvariant();
                if (name is "vgc" or "vgtray") return "Riot Vanguard is running";
                if (name.StartsWith("faceit", StringComparison.Ordinal)) return "FACEIT is running";
                if (name.StartsWith("esea", StringComparison.Ordinal)) return "ESEA is running";
                if (name.Contains("easyanticheat", StringComparison.Ordinal)) return "Easy Anti-Cheat is running";
                if (name is "beservice" or "beservice_x64") return "BattlEye is running";
            }
        }
        var pid = Win32.ProcessOf(window);
        if (pid == (uint)Environment.ProcessId) return "not used, the window is Exo Capture's own.";
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return "cannot open the game's process.";
        try
        {
            uint length = 0;
            var hr = Win32.GetPackageFullName(process, ref length, null);
            if (hr != 15700 /* APPMODEL_ERROR_NO_PACKAGE */) return "not used for a Store app.";
        }
        finally { Win32.CloseHandle(process); }
        return null;
    }

    internal static HookSource Start(GpuDevice gpu, nint window, string obsFolder, int fps)
    {
        var pid = Win32.ProcessOf(window);
        var thread = Win32.GetWindowThreadProcessId(window, null);
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION | Win32.SYNCHRONIZE, false, pid);
        if (process == 0) throw new CaptureException("cannot open the game's process.");
        nint keepAlive = 0, infoMap = 0, ready = 0, stop = 0, restart = 0, init = 0;
        byte* info = null;
        try
        {
            if (!Win32.IsWow64Process2(process, out var machine, out _)) throw new CaptureException("not used, cannot tell whether the game is 32-bit or 64-bit.");
            var bits = machine == 0 ? "64" : "32";
            var hook = Path.Combine(obsFolder, $"graphics-hook{bits}.dll");
            var inject = Path.Combine(obsFolder, $"inject-helper{bits}.exe");
            var offsetsTool = Path.Combine(obsFolder, $"get-graphics-offsets{bits}.exe");
            if (!File.Exists(hook) || !File.Exists(inject) || !File.Exists(offsetsTool)) throw new CaptureException("OBS's hook files are not installed.");

            var keepName = "CaptureHook_KeepAlive" + pid;
            var other = Win32.OpenMutex(Win32.SYNCHRONIZE, false, keepName);
            if (other != 0) { Win32.CloseHandle(other); throw new CaptureException("another app already captures this game."); }
            keepAlive = Win32.CreateMutex(0, false, keepName);

            var offsets = Offsets(offsetsTool) ?? throw new CaptureException("no graphics offsets from OBS's helper");

            restart = Win32.OpenEvent(Win32.EVENT_MODIFY_STATE | Win32.SYNCHRONIZE, false, "CaptureHook_Restart" + pid);
            if (restart == 0)
            {
                var result = Run(inject, $"\"{hook}\" 1 {thread}", TimeSpan.FromSeconds(10));
                if (result is not 0) throw new CaptureException($"injection did not take ({result}).");
            }
            else Win32.SetEvent(restart); // the hook is there already (an earlier run): start it again

            var until = Environment.TickCount64 + 5000;
            while ((infoMap = Win32.OpenFileMapping(Win32.FILE_MAP_ALL_ACCESS, false, "CaptureHook_HookInfo" + pid)) == 0)
            {
                if (Win32.WaitForSingleObject(process, 0) == Win32.WAIT_OBJECT_0) throw new CaptureException("the game closed while capture was starting.");
                if (Environment.TickCount64 > until) throw new CaptureException("the hook did not load in the game.");
                Thread.Sleep(50);
            }
            info = (byte*)Win32.MapViewOfFile(infoMap, Win32.FILE_MAP_ALL_ACCESS, 0, 0, HookInfoSize);
            if (info == null) throw new CaptureException($"cannot map the hook's shared memory (Windows error {Marshal.GetLastWin32Error()}).");
            if (*(uint*)info != 1) throw new CaptureException($"not used, the hook in the game is version {*(uint*)info}, newer than Exo knows.");

            ready = Open("CaptureHook_HookReady", pid);
            stop = Open("CaptureHook_Stop", pid);
            init = Open("CaptureHook_Initialize", pid);
            if (restart == 0) restart = Open("CaptureHook_Restart", pid);
            if (ready == 0 || stop == 0 || init == 0) throw new CaptureException("its signals are missing.");

            WriteOptions(info, offsets, 10_000_000L / Math.Max(1, fps));
            Win32.SetEvent(init);
            Host.Log("Game hook: started (" + bits + "-bit).");
            return new HookSource(gpu, pid, window, process, keepAlive, infoMap, info, ready, stop, restart, init);
        }
        catch
        {
            if (info != null) Win32.UnmapViewOfFile((nint)info);
            foreach (var h in new[] { infoMap, ready, stop, restart, init, keepAlive, process }) if (h != 0) Win32.CloseHandle(h);
            throw;
        }
    }

    private static nint Open(string name, uint pid) => Win32.OpenEvent(Win32.EVENT_MODIFY_STATE | Win32.SYNCHRONIZE, false, name + pid);

    /// <summary>
    /// hook_info's options, at OBS's offsets: frame_interval (u64 at 56), use_scale/force_shmem/capture_overlay/
    /// allow_srgb_alias (bools at 64 to 67), and the graphics offsets from 68: d3d8 (1), d3d9 (5), dxgi (3), ddraw (8),
    /// dxgi2 (1), d3d12 (1), each a u32.
    /// </summary>
    internal static void WriteOptions(byte* info, GraphicsOffsets o, long frameInterval)
    {
        *(ulong*)(info + 56) = (ulong)frameInterval;
        info[64] = 0; info[65] = 0; info[66] = 0; info[67] = 1;
        var at = (uint*)(info + 68);
        uint[] values =
        [
            o.D3d8Present,
            o.D3d9Present, o.D3d9PresentEx, o.D3d9PresentSwap, o.D3d9ClsOff, o.D3d9IsEx,
            o.DxgiPresent, o.DxgiResize, o.DxgiPresent1,
            0, 0, 0, 0, 0, 0, 0, 0,
            o.DxgiRelease,
            o.D3d12Execute,
        ];
        for (var i = 0; i < values.Length; i++) at[i] = values[i];
    }

    internal readonly record struct GraphicsOffsets(uint D3d8Present, uint D3d9Present, uint D3d9PresentEx, uint D3d9PresentSwap, uint D3d9ClsOff,
        uint D3d9IsEx, uint DxgiPresent, uint DxgiResize, uint DxgiPresent1, uint DxgiRelease, uint D3d12Execute);

    /// <summary>get-graphics-offsets' output: [d3d8] [d3d9] [dxgi] [d3d12] sections of name=0x... lines.</summary>
    internal static GraphicsOffsets? ParseOffsets(string text)
    {
        var values = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            if (uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var v)) values[section + "." + line[..eq].Trim()] = v;
        }
        if (!values.ContainsKey("dxgi.present")) return null;
        uint Get(string key) => values.GetValueOrDefault(key);
        return new GraphicsOffsets(Get("d3d8.present"), Get("d3d9.present"), Get("d3d9.present_ex"), Get("d3d9.present_swap"), Get("d3d9.d3d9_clsoff"),
            Get("d3d9.is_d3d9ex_clsoff"), Get("dxgi.present"), Get("dxgi.resize"), Get("dxgi.present1"), Get("dxgi.release"), Get("d3d12.execute_command_lists"));
    }

    private static GraphicsOffsets? Offsets(string tool)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(tool)! };
        using var p = Process.Start(start);
        if (p is null) return null;
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(10_000)) { Host.Log("Game hook: OBS's offsets helper did not finish in time."); return null; }
        return ParseOffsets(output.GetAwaiter().GetResult());
    }

    private static int? Run(string exe, string arguments, TimeSpan limit)
    {
        var start = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        using var p = Process.Start(start);
        if (p is null) return null;
        // Never ended by Exo Capture: a helper that hangs is left to finish, and the hook is not used.
        if (!p.WaitForExit(limit)) { Host.Log("Game hook: OBS's inject helper did not finish in time."); return null; }
        return p.ExitCode;
    }

    public Frame? Latest()
    {
        if (Ended is not null) return null;
        if (Win32.WaitForSingleObject(_process, 0) == Win32.WAIT_OBJECT_0) { Ended = "the game closed."; return null; }
        // HookReady is set by the hook each time it (re)starts sharing frames: a new texture, or a new size.
        if (Win32.WaitForSingleObject(_ready, 0) == Win32.WAIT_OBJECT_0) OpenTexture();
        if (_texture is null) return null;
        HadFrame = true;
        return new Frame(_texture, Win32.Now(), null, _flip);
    }

    private void OpenTexture()
    {
        var type = *(int*)(_info + 8);
        if (type != 1) { if (type == 0 && *(uint*)(_info + 40) != 0) Ended = "not used, the game's frames come through memory, which Exo does not read."; return; }
        var window = *(uint*)(_info + 12);
        var mapId = *(uint*)(_info + 40);
        Width = (int)*(uint*)(_info + 20);
        Height = (int)*(uint*)(_info + 24);
        _flip = _info[48] != 0;
        var map = Win32.OpenFileMapping(Win32.FILE_MAP_READ, false, $"CaptureHook_Texture_{window}_{mapId}");
        if (map == 0) return;
        try
        {
            var view = Win32.MapViewOfFile(map, Win32.FILE_MAP_READ, 0, 0, 4);
            if (view == 0) return;
            var handle = *(uint*)view;
            Win32.UnmapViewOfFile(view);
            _texture?.Dispose();
            _texture = null;
            try { _texture = _gpu.Device.OpenSharedResource<ID3D11Texture2D>((nint)handle); }
            catch (SharpGen.Runtime.SharpGenException ex) { Host.Log($"Game hook: cannot open the game's frame (0x{ex.HResult:X8}); it may draw on another graphics card."); }
            if (_texture is not null) Host.Log($"Game hook: capturing {Width}x{Height}{(_flip ? " (OpenGL, turned upright)." : ".")}");
        }
        finally { Win32.CloseHandle(map); }
    }

    public void Dispose()
    {
        Win32.SetEvent(_stop);
        _texture?.Dispose();
        Win32.UnmapViewOfFile((nint)_info);
        foreach (var h in new[] { _infoMap, _ready, _stop, _restart, _init, _keepAlive, _process }) if (h != 0) Win32.CloseHandle(h);
        _ = _pid;
        _ = _window;
    }
}
