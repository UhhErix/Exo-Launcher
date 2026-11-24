using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

internal sealed partial class WebRtcAgc2 : IDisposable
{
    private const int Half = Opus.FrameSamples / 2;
    private nint _agc;
    private readonly float[] _mono = new float[Half];

    private WebRtcAgc2(nint agc) => _agc = agc;

    internal static WebRtcAgc2? Create()
    {
        try
        {
            var agc = exo_agc2_create();
            return agc != 0 ? new WebRtcAgc2(agc) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Discord voice: WebRTC AGC2 unavailable (" + ex.GetType().Name + ").");
            return null;
        }
    }

    internal unsafe void Process(float[] stereo)
    {
        if (_agc == 0) return;
        for (var half = 0; half < 2; half++)
        {
            var at = half * Half;
            for (var i = 0; i < Half; i++)
                _mono[i] = (stereo[(at + i) * 2] + stereo[(at + i) * 2 + 1]) * 0.5f;
            fixed (float* samples = _mono) exo_agc2_process(_agc, samples);
            for (var i = 0; i < Half; i++)
                stereo[(at + i) * 2] = stereo[(at + i) * 2 + 1] = _mono[i];
        }
    }

    public void Dispose()
    {
        if (_agc == 0) return;
        exo_agc2_destroy(_agc);
        _agc = 0;
    }

    [LibraryImport("exo_apm.dll")]
    private static partial nint exo_agc2_create();

    [LibraryImport("exo_apm.dll")]
    private static unsafe partial int exo_agc2_process(nint agc, float* samples);

    [LibraryImport("exo_apm.dll")]
    private static partial void exo_agc2_destroy(nint agc);
}
