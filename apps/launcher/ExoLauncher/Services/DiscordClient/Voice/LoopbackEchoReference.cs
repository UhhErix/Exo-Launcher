using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services.DiscordClient.Voice;

internal sealed unsafe class LoopbackEchoReference : IDisposable
{
    private const uint StreamLoopback = 0x00020000;
    private const uint AutoConvert = 0x80000000 | 0x08000000;
    private const long BufferHns = 200_000;
    private const int DeviceInvalidated = unchecked((int)0x88890004);
    private const float ActiveThresholdDb = -65f;

    private readonly Action<float[]> _onFrame;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    internal LoopbackEchoReference(Action<float[]> onFrame)
    {
        _onFrame = onFrame;
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo loopback AEC ref", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    internal static float FrameDb(ReadOnlySpan<float> stereo)
    {
        if (stereo.IsEmpty) return -100f;
        double sum = 0;
        for (var i = 0; i < stereo.Length; i++) sum += stereo[i] * (double)stereo[i];
        return (float)(10 * Math.Log10(sum / stereo.Length + 1e-10));
    }

    internal static bool HasActiveReference(ReadOnlySpan<float> stereo) => FrameDb(stereo) > ActiveThresholdDb;

    private void Run()
    {
        CoInitializeEx(0, 0);
        AutopilotCpuSets.PinCurrentThreadToBackgroundCores();
        for (var retry = 0; RunOnce(retry > 0); retry++)
        {
            if (_stop.Token.WaitHandle.WaitOne(retry < 10 ? 500 : 2000)) return;
        }
    }

    private bool RunOnce(bool retrying)
    {
        if (_stop.IsCancellationRequested) return false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? clientObject = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var outputId = App.Services.Settings.Current.DiscordOutputDevice;
            device = AudioDevices.Open(enumerator, false, outputId);
            if (device is null) return retrying;
            var iid = typeof(VoiceAudio.IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out clientObject));
            var client = (VoiceAudio.IAudioClient)clientObject;
            var format = VoiceAudio.Format();
            Marshal.ThrowExceptionForHR(client.Initialize(0, StreamLoopback | AutoConvert, BufferHns, 0, ref format, 0));
            var capIid = typeof(VoiceAudio.IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref capIid, out var capObj));
            var capture = (VoiceAudio.IAudioCaptureClient)capObj;
            try
            {
                Marshal.ThrowExceptionForHR(client.Start());
                var frame = new float[VoiceAudio.FrameFloats];
                var filled = 0;
                while (!_stop.IsCancellationRequested)
                {
                    if (_stop.Token.WaitHandle.WaitOne(10)) break;
                    while (!_stop.IsCancellationRequested)
                    {
                        Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var packet));
                        if (packet == 0) break;
                        Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var count, out var flags, out _, out _));
                        try
                        {
                            var floats = (int)count * Opus.Channels;
                            var offset = 0;
                            while (offset < floats)
                            {
                                var take = Math.Min(VoiceAudio.FrameFloats - filled, floats - offset);
                                if ((flags & 2) != 0)
                                    Array.Clear(frame, filled, take);
                                else
                                    Marshal.Copy(data + offset * sizeof(float), frame, filled, take);
                                filled += take;
                                offset += take;
                                if (filled == VoiceAudio.FrameFloats)
                                {
                                    _onFrame(frame);
                                    filled = 0;
                                }
                            }
                        }
                        finally { Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(count)); }
                    }
                }
                client.Stop();
            }
            finally { Marshal.ReleaseComObject(capture); }
            return false;
        }
        catch (COMException ex) when (ex.HResult == DeviceInvalidated) { return true; }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            AppLog.Info($"Discord voice: loopback echo reference stopped ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            return false;
        }
        finally
        {
            if (clientObject is not null) Marshal.ReleaseComObject(clientObject);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(1000);
        _stop.Dispose();
    }
}
