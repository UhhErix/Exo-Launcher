using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Echo cancellation for a call on speakers: WebRTC's AEC3 (webrtc-audio-processing 2.1, BSD-3-Clause; Krisp's own echo
/// cancellation is AEC3 too), built with its high-pass filter and nothing else into exo_apm.dll by voice\build.ps1. It is
/// fed what Exo plays (<see cref="Render"/>, on the speakers' thread) and run on the microphone before anything else
/// (<see cref="Capture"/>, on the voice thread, so noise suppression gets the voice without the call in it), each 20 ms
/// stereo frame as two of its 10 ms mono ones. It finds the delay between the two sides itself, and follows a new device.
/// Measured on speech through a simulated room (70 ms away, 0.3 s of reverb, a slightly overdriven speaker): the echo 28 dB
/// down, a voice with nothing playing within 0.4 dB of itself (9 ms later), 0.05 ms of CPU per 10 ms.
/// </summary>
internal sealed partial class EchoCanceller : IDisposable
{
    private const int Half = Opus.FrameSamples / 2;
    private const int DisposingFlag = int.MinValue;
    private readonly Lock _renderGate = new();
    private readonly Lock _captureGate = new();
    private readonly Func<nint, float[], int> _renderNative;
    private readonly Func<nint, float[], int> _captureNative;
    private readonly Action<nint> _destroyNative;
    private nint _apm;
    private int _state;
    private readonly float[] _render = new float[Half], _capture = new float[Half];

    private EchoCanceller(nint apm) : this(apm, NativeRender, NativeCapture, exo_aec_destroy) { }

    internal EchoCanceller(
        nint apm,
        Func<nint, float[], int> renderNative,
        Func<nint, float[], int> captureNative,
        Action<nint> destroyNative)
    {
        _apm = apm;
        _renderNative = renderNative;
        _captureNative = captureNative;
        _destroyNative = destroyNative;
    }

    private static unsafe int NativeRender(nint apm, float[] samples)
    {
        fixed (float* ptr = samples) return exo_aec_render(apm, ptr);
    }

    private static unsafe int NativeCapture(nint apm, float[] samples)
    {
        fixed (float* ptr = samples) return exo_aec_capture(apm, ptr);
    }

    /// <summary>A canceller, or null when exo_apm.dll can't load.</summary>
    internal static EchoCanceller? Create()
    {
        try { return exo_aec_create() is var apm && apm != 0 ? new EchoCanceller(apm) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Discord voice: echo cancellation unavailable (" + ex.GetType().Name + ").");
            return null;
        }
    }

    /// <summary>A 20 ms stereo frame Exo plays (silence when deafened: nothing reaches the speakers).</summary>
    internal void Render(ReadOnlySpan<float> stereo, bool silent)
    {
        if (!TryEnter(out var apm)) return;
        try
        {
            lock (_renderGate)
            {
                if ((Volatile.Read(ref _state) & DisposingFlag) != 0) return;
                for (var half = 0; half < 2; half++)
                {
                    for (var i = 0; i < Half; i++)
                        _render[i] = silent ? 0 : (stereo[(half * Half + i) * 2] + stereo[(half * Half + i) * 2 + 1]) * 0.5f;
                    _renderNative(apm, _render);
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Takes the echo out of a 20 ms stereo microphone frame, in place.</summary>
    internal void Capture(float[] stereo)
    {
        if (!TryEnter(out var apm)) return;
        try
        {
            lock (_captureGate)
            {
                if ((Volatile.Read(ref _state) & DisposingFlag) != 0) return;
                for (var half = 0; half < 2; half++)
                {
                    var at = half * Half;
                    for (var i = 0; i < Half; i++) _capture[i] = (stereo[(at + i) * 2] + stereo[(at + i) * 2 + 1]) * 0.5f;
                    _captureNative(apm, _capture);
                    for (var i = 0; i < Half; i++) stereo[(at + i) * 2] = stereo[(at + i) * 2 + 1] = _capture[i];
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>After both threads stopped.</summary>
    public void Dispose()
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if ((current & DisposingFlag) != 0) return;
            var next = current | DisposingFlag;
            if (Interlocked.CompareExchange(ref _state, next, current) == current)
            {
                if (current == 0) DestroyNativeOnce();
                return;
            }
        }
    }

    private bool TryEnter(out nint apm)
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if ((current & DisposingFlag) != 0)
            {
                apm = 0;
                return false;
            }
            if (Interlocked.CompareExchange(ref _state, current + 1, current) == current)
            {
                apm = Volatile.Read(ref _apm);
                if (apm != 0) return true;
                Exit();
                return false;
            }
        }
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _state) == DisposingFlag)
            DestroyNativeOnce();
    }

    private void DestroyNativeOnce()
    {
        var apm = Interlocked.Exchange(ref _apm, 0);
        if (apm != 0) _destroyNative(apm);
    }

    [LibraryImport("exo_apm.dll")]
    private static partial nint exo_aec_create();

    [LibraryImport("exo_apm.dll")]
    private static unsafe partial int exo_aec_render(nint apm, float* samples);

    [LibraryImport("exo_apm.dll")]
    private static unsafe partial int exo_aec_capture(nint apm, float* samples);

    [LibraryImport("exo_apm.dll")]
    private static partial void exo_aec_destroy(nint apm);
}
