using System.Runtime.InteropServices;
using ExoCapture.Native;

namespace ExoCapture.Audio;

/// <summary>Where a sound track's samples come from.</summary>
internal enum SoundSource
{
    /// <summary>Everything the default output plays.</summary>
    System,
    /// <summary>One app and everything it started (process loopback, include).</summary>
    App,
    /// <summary>Everything except one app and what it started (process loopback, exclude).</summary>
    AllBut,
    /// <summary>A microphone.</summary>
    Microphone,
}

/// <summary>
/// One WASAPI capture in shared mode, converted by Windows to 48 kHz stereo float: the output's loopback, an app's sound
/// (Windows 11's process loopback, by process tree), or a microphone. Each packet comes with the QPC time of its first
/// sample, so every track lines up with the picture.
/// </summary>
internal sealed unsafe class WasapiCapture : IDisposable
{
    internal const int Rate = 48000, Channels = 2;
    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly nint _client, _capture, _event;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly Action<float[], int, long> _packet;
    internal string? Failed { get; private set; }

    /// <summary><paramref name="packet"/>: interleaved samples, the number of frames, the QPC time (100 ns) of the first one.</summary>
    private WasapiCapture(nint client, Action<float[], int, long> packet, string name, bool loopback)
    {
        _client = client;
        _packet = packet;
        _event = CreateEventW(0, false, false, 0);
        var format = new WaveFormatEx { Tag = 3, Channels = Channels, Rate = Rate, BytesPerSecond = Rate * 8, BlockAlign = 8, Bits = 32 };
        uint flags = 0x40000 /* EVENTCALLBACK */ | 0x80000000 /* AUTOCONVERTPCM */ | 0x08000000 /* SRC_DEFAULT_QUALITY */;
        if (loopback) flags |= 0x20000;
        Check(Vt<nint, int, uint, long, long, nint, nint, int>(client, 3)(client, 0, flags, 2_000_000, 0, (nint)(&format), 0), name);
        Check(Vt<nint, nint, int>(client, 13)(client, _event), name);
        nint capture;
        var iid = IidCaptureClient;
        Check(Vt<nint, nint, nint, int>(client, 14)(client, (nint)(&iid), (nint)(&capture)), name);
        _capture = capture;
        Check(Vt<nint, int>(client, 10)(client), name);
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo Capture sound: " + name };
        _thread.Start();
    }

    /// <summary>Opens a source; null (with the reason logged) when it can't, so the track stays silent rather than failing.</summary>
    internal static WasapiCapture? Open(SoundSource source, uint pid, string? deviceId, Action<float[], int, long> packet, string name)
    {
        try
        {
            var client = source switch
            {
                SoundSource.App or SoundSource.AllBut => ProcessClient(pid, include: source == SoundSource.App),
                SoundSource.Microphone => DeviceClient(capture: true, deviceId),
                _ => DeviceClient(capture: false, null),
            };
            return new WasapiCapture(client, packet, name, loopback: source != SoundSource.Microphone);
        }
        catch (Exception ex) when (ex is CaptureException or COMException or TimeoutException)
        {
            Host.Log("Sound: " + name + " did not open: " + ex.Message);
            return null;
        }
    }

    private static nint DeviceClient(bool capture, string? id)
    {
        var clsid = ClsidEnumerator;
        var iid = IidEnumerator;
        nint enumerator;
        Check(CoCreateInstance(&clsid, 0, 0x17, &iid, &enumerator), "sound devices");
        try
        {
            nint device = 0;
            if (!string.IsNullOrEmpty(id))
                fixed (char* text = id)
                    if (Vt<nint, nint, nint, int>(enumerator, 5)(enumerator, (nint)text, (nint)(&device)) < 0) device = 0;
            if (device == 0)
                Check(Vt<nint, int, int, nint, int>(enumerator, 4)(enumerator, capture ? 1 : 0, 0, (nint)(&device)), capture ? "no microphone" : "sound outputs");
            try
            {
                nint client;
                var clientIid = IidAudioClient;
                Check(Vt<nint, nint, uint, nint, nint, int>(device, 3)(device, (nint)(&clientIid), 0x17, 0, (nint)(&client)), "sound device");
                return client;
            }
            finally { Marshal.Release(device); }
        }
        finally { Marshal.Release(enumerator); }
    }

    private static nint ProcessClient(uint pid, bool include)
    {
        var parameters = new ActivationParams { Type = 1, ProcessId = pid, Mode = include ? 0 : 1 };
        var blob = new PropVariant { Type = 65, Size = (uint)sizeof(ActivationParams), Data = (nint)(&parameters) };
        using var handler = new Completion();
        var face = handler.Pointer;
        try
        {
            var iid = IidAudioClient;
            nint operation;
            fixed (char* path = @"VAD\Process_Loopback")
                Check(ActivateAudioInterfaceAsync(path, &iid, &blob, face, &operation), "app sound");
            try
            {
                if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("app sound did not open in time");
                int result;
                nint unknown;
                Check(Vt<nint, nint, nint, int>(operation, 3)(operation, (nint)(&result), (nint)(&unknown)), "app sound");
                Check(result, "app sound");
                return unknown;
            }
            finally { Marshal.Release(operation); }
        }
        finally { }
    }

    private void Run()
    {
        uint index = 0;
        Win32.AvSetMmThreadCharacteristics("Audio", ref index);
        var scratch = new float[Rate * Channels];
        try
        {
            while (!_stop)
            {
                Win32.WaitForSingleObject(_event, 20);
                while (true)
                {
                    byte* data;
                    uint frames, flags;
                    ulong position, qpc;
                    var hr = Vt<nint, nint, nint, nint, nint, nint, int>(_capture, 3)(_capture, (nint)(&data), (nint)(&frames), (nint)(&flags), (nint)(&position), (nint)(&qpc));
                    if (hr == 0x08890001 /* AUDCLNT_S_BUFFER_EMPTY */ || frames == 0)
                    {
                        if (hr == 0) Vt<nint, uint, int>(_capture, 4)(_capture, frames);
                        break;
                    }
                    if (hr < 0) { Failed = $"sound read (0x{hr:X8})"; return; }
                    var count = (int)Math.Min(frames, (uint)(scratch.Length / Channels));
                    if ((flags & 0x2) != 0) Array.Clear(scratch, 0, count * Channels); // silent
                    else new ReadOnlySpan<float>(data, count * Channels).CopyTo(scratch);
                    // QPC is in the device's 100 ns units already; 0 when the endpoint gives none (then: now, less the packet).
                    var at = qpc != 0 ? (long)qpc : Win32.Now() - count * 10_000_000L / Rate;
                    Vt<nint, uint, int>(_capture, 4)(_capture, frames);
                    _packet(scratch, count, at);
                }
            }
        }
        catch (Exception ex) { Failed = "sound: " + ex.Message; }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        Vt<nint, int>(_client, 11)(_client);
        Marshal.Release(_capture);
        Marshal.Release(_client);
        Win32.CloseHandle(_event);
    }

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new CaptureException($"{step} (0x{hr:X8})");
    }

    private static delegate* unmanaged[Stdcall]<T1, TR> Vt<T1, TR>(nint o, int s) => (delegate* unmanaged[Stdcall]<T1, TR>)(*(void***)o)[s];
    private static delegate* unmanaged[Stdcall]<T1, T2, TR> Vt<T1, T2, TR>(nint o, int s) => (delegate* unmanaged[Stdcall]<T1, T2, TR>)(*(void***)o)[s];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, TR> Vt<T1, T2, T3, TR>(nint o, int s) => (delegate* unmanaged[Stdcall]<T1, T2, T3, TR>)(*(void***)o)[s];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, T4, TR> Vt<T1, T2, T3, T4, TR>(nint o, int s) => (delegate* unmanaged[Stdcall]<T1, T2, T3, T4, TR>)(*(void***)o)[s];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, TR> Vt<T1, T2, T3, T4, T5, TR>(nint o, int s) => (delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, TR>)(*(void***)o)[s];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, T6, TR> Vt<T1, T2, T3, T4, T5, T6, TR>(nint o, int s) => (delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, T6, TR>)(*(void***)o)[s];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, T6, T7, TR> Vt<T1, T2, T3, T4, T5, T6, T7, TR>(nint o, int s) => (delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, T6, T7, TR>)(*(void***)o)[s];

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams { public int Type; public uint ProcessId; public int Mode; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public nint Data;
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);
    [DllImport("mmdevapi.dll")] private static extern int ActivateAudioInterfaceAsync(char* path, Guid* iid, PropVariant* parameters, nint handler, nint* operation);
    [DllImport("kernel32.dll")] private static extern nint CreateEventW(nint attributes, bool manual, bool initial, nint name);
}

/// <summary>
/// The completion handler ActivateAudioInterfaceAsync calls back: a COM object built by hand (IUnknown, IAgileObject,
/// IActivateAudioInterfaceCompletionHandler), so nothing of .NET's COM runs on Windows' audio thread.
/// </summary>
internal sealed unsafe class Completion : IDisposable
{
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidAgile = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly Guid IidHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
    private static readonly nint* Vtable = MakeVtable();
    private readonly nint* _object;
    private readonly GCHandle _self;
    internal readonly ManualResetEventSlim Done = new(false);

    internal Completion()
    {
        _self = GCHandle.Alloc(this);
        _object = (nint*)NativeMemory.AllocZeroed(3, (nuint)sizeof(nint));
        _object[0] = (nint)Vtable;
        _object[1] = 1; // references
        _object[2] = GCHandle.ToIntPtr(_self);
    }

    internal nint Pointer => (nint)_object;

    private static nint* MakeVtable()
    {
        var v = (nint*)NativeMemory.AllocZeroed(4, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged[Stdcall]<nint*, Guid*, nint*, int>)&QueryInterface;
        v[1] = (nint)(delegate* unmanaged[Stdcall]<nint*, uint>)&AddRef;
        v[2] = (nint)(delegate* unmanaged[Stdcall]<nint*, uint>)&Release;
        v[3] = (nint)(delegate* unmanaged[Stdcall]<nint*, nint, int>)&ActivateCompleted;
        return v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int QueryInterface(nint* self, Guid* iid, nint* result)
    {
        if (*iid == IidUnknown || *iid == IidAgile || *iid == IidHandler)
        {
            Interlocked.Increment(ref *(long*)&self[1]);
            *result = (nint)self;
            return 0;
        }
        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint AddRef(nint* self) => (uint)Interlocked.Increment(ref *(long*)&self[1]);

    // The block is freed by Dispose, after Windows has let go of it; a count reaching zero frees nothing early.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint Release(nint* self) => (uint)Math.Max(0, Interlocked.Decrement(ref *(long*)&self[1]));

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int ActivateCompleted(nint* self, nint operation)
    {
        if (GCHandle.FromIntPtr(self[2]).Target is Completion c) c.Done.Set();
        return 0;
    }

    public void Dispose()
    {
        // Windows may still hold a reference for a moment after the callback; the few bytes stay until it can't.
        if (Interlocked.Read(ref *(long*)&_object[1]) <= 1)
        {
            _self.Free();
            NativeMemory.Free(_object);
        }
    }
}
