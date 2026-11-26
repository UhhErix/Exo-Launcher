using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

internal static unsafe class ExoVoiceMl
{
    private static readonly Lock LoadGate = new();
    private static nint _library;
    private static delegate* unmanaged[Cdecl]<uint> _version;
    private static delegate* unmanaged[Cdecl]<byte*, byte*, int, nint> _dpdfCreate;
    private static delegate* unmanaged[Cdecl]<nint, void> _dpdfDestroy;
    private static delegate* unmanaged[Cdecl]<nint, float, void> _dpdfAtten;
    private static delegate* unmanaged[Cdecl]<nint, int> _dpdfRate;
    private static delegate* unmanaged[Cdecl]<nint, float*, int, float> _dpdfProcess;
    private static delegate* unmanaged[Cdecl]<byte*, byte*, int, nint> _titanetCreate;
    private static delegate* unmanaged[Cdecl]<nint, void> _titanetDestroy;
    private static delegate* unmanaged[Cdecl]<nint, float*, int, float*, int, int> _titanetEmbed;
    private static delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int, nint> _voxCreate;
    private static delegate* unmanaged[Cdecl]<nint, void> _voxDestroy;
    private static delegate* unmanaged[Cdecl]<nint, float*, int, int> _voxEnroll;
    private static delegate* unmanaged[Cdecl]<nint, float*, float*, int, float> _voxProcess;

    private static readonly Lock TitaNetGate = new();
    private static nint _titanet;
    private static string? _titanetModel;

    internal static string LibraryPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "exo_voice_ml.dll");
    internal static string OnnxRuntimePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");

    internal static bool EnsureLoaded()
    {
        lock (LoadGate)
        {
            if (_library != 0) return true;
            if (!File.Exists(OnnxRuntimePath) || !File.Exists(LibraryPath)) return false;
            if (!NativeLibrary.TryLoad(OnnxRuntimePath, out _) || !NativeLibrary.TryLoad(LibraryPath, out var lib))
                return false;
            if (!NativeLibrary.TryGetExport(lib, "exo_voice_ml_version", out var ver)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_dpdfnet_create", out var dc)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_dpdfnet_destroy", out var dd)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_dpdfnet_set_atten_lim", out var da)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_dpdfnet_sample_rate", out var dr)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_dpdfnet_process_48k_stereo", out var dp)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_titanet_create", out var tc)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_titanet_destroy", out var td)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_titanet_embed", out var te)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_voxlock_create", out var vc)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_voxlock_destroy", out var vd)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_voxlock_enroll_16k", out var ve)
                || !NativeLibrary.TryGetExport(lib, "exo_voice_voxlock_process_16k", out var vp))
                return false;

            _version = (delegate* unmanaged[Cdecl]<uint>)ver;
            _dpdfCreate = (delegate* unmanaged[Cdecl]<byte*, byte*, int, nint>)dc;
            _dpdfDestroy = (delegate* unmanaged[Cdecl]<nint, void>)dd;
            _dpdfAtten = (delegate* unmanaged[Cdecl]<nint, float, void>)da;
            _dpdfRate = (delegate* unmanaged[Cdecl]<nint, int>)dr;
            _dpdfProcess = (delegate* unmanaged[Cdecl]<nint, float*, int, float>)dp;
            _titanetCreate = (delegate* unmanaged[Cdecl]<byte*, byte*, int, nint>)tc;
            _titanetDestroy = (delegate* unmanaged[Cdecl]<nint, void>)td;
            _titanetEmbed = (delegate* unmanaged[Cdecl]<nint, float*, int, float*, int, int>)te;
            _voxCreate = (delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int, nint>)vc;
            _voxDestroy = (delegate* unmanaged[Cdecl]<nint, void>)vd;
            _voxEnroll = (delegate* unmanaged[Cdecl]<nint, float*, int, int>)ve;
            _voxProcess = (delegate* unmanaged[Cdecl]<nint, float*, float*, int, float>)vp;
            _library = lib;
            return _version() >= 1;
        }
    }

    internal static bool WarmTitaNet(string modelPath)
    {
        lock (TitaNetGate)
        {
            if (_titanet != 0 && string.Equals(_titanetModel, modelPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!EnsureLoaded() || !File.Exists(modelPath)) return false;
            if (_titanet != 0)
            {
                _titanetDestroy(_titanet);
                _titanet = 0;
            }
            var ortBytes = Encoding.UTF8.GetBytes(OnnxRuntimePath + "\0");
            var modelBytes = Encoding.UTF8.GetBytes(modelPath + "\0");
            var threads = Environment.ProcessorCount >= 4 ? 2 : 1;
            fixed (byte* ort = ortBytes)
            fixed (byte* model = modelBytes)
            {
                _titanet = _titanetCreate(ort, model, threads);
            }
            if (_titanet == 0) return false;
            _titanetModel = modelPath;
            return true;
        }
    }

    internal static float[]? EmbedTitaNet(ReadOnlySpan<float> voice16k, string modelPath)
    {
        if (voice16k.Length < 400) return null;
        lock (TitaNetGate)
        {
            if (!WarmTitaNet(modelPath)) return null;
            var output = new float[192];
            int dim;
            fixed (float* inp = voice16k)
            fixed (float* outp = output)
            {
                dim = _titanetEmbed(_titanet, inp, voice16k.Length, outp, output.Length);
            }
            if (dim <= 0) return null;
            return dim == output.Length ? output : output[..dim];
        }
    }

    internal static nint CreateDpdfNet(string modelPath, int intraThreads = 1)
    {
        if (!EnsureLoaded() || !File.Exists(modelPath)) return 0;
        var ortBytes = Encoding.UTF8.GetBytes(OnnxRuntimePath + "\0");
        var modelBytes = Encoding.UTF8.GetBytes(modelPath + "\0");
        fixed (byte* ort = ortBytes)
        fixed (byte* model = modelBytes)
            return _dpdfCreate(ort, model, intraThreads);
    }

    internal static void DestroyDpdfNet(nint handle)
    {
        if (handle != 0 && _dpdfDestroy != null) _dpdfDestroy(handle);
    }

    internal static void SetDpdfNetAttenuation(nint handle, float db)
    {
        if (handle != 0 && _dpdfAtten != null) _dpdfAtten(handle, db);
    }

    internal static int DpdfNetSampleRate(nint handle) =>
        handle != 0 && _dpdfRate != null ? _dpdfRate(handle) : 0;

    internal static float ProcessDpdfNet48kStereo(nint handle, Span<float> stereo)
    {
        if (handle == 0 || _dpdfProcess == null || stereo.IsEmpty) return float.NaN;
        fixed (float* ptr = stereo)
            return _dpdfProcess(handle, ptr, stereo.Length);
    }

    internal static nint CreateVoxLock(string enrollPath, string chunkPath, int intraThreads = 1)
    {
        if (!EnsureLoaded() || !File.Exists(enrollPath) || !File.Exists(chunkPath)) return 0;
        var ortBytes = Encoding.UTF8.GetBytes(OnnxRuntimePath + "\0");
        var enrollBytes = Encoding.UTF8.GetBytes(enrollPath + "\0");
        var chunkBytes = Encoding.UTF8.GetBytes(chunkPath + "\0");
        fixed (byte* ort = ortBytes)
        fixed (byte* enr = enrollBytes)
        fixed (byte* chk = chunkBytes)
            return _voxCreate(ort, enr, chk, intraThreads);
    }

    internal static void DestroyVoxLock(nint handle)
    {
        if (handle != 0 && _voxDestroy != null) _voxDestroy(handle);
    }

    internal static bool EnrollVoxLock16k(nint handle, ReadOnlySpan<float> voice16k)
    {
        if (handle == 0 || _voxEnroll == null || voice16k.Length < 8000) return false;
        fixed (float* ptr = voice16k)
            return _voxEnroll(handle, ptr, voice16k.Length) != 0;
    }

    internal static float ProcessVoxLock16k(nint handle, ReadOnlySpan<float> in16k, Span<float> out16k)
    {
        if (handle == 0 || _voxProcess == null || in16k.Length != out16k.Length || in16k.IsEmpty)
            return float.NaN;
        fixed (float* inp = in16k)
        fixed (float* outp = out16k)
            return _voxProcess(handle, inp, outp, in16k.Length);
    }
}

internal sealed class NeuralNoiseSuppressor : IDisposable
{
    private nint _dpdf;
    private nint _vox;
    private readonly DelayTracker _delay = new();
    private readonly MyVoice.Decimator _decimator = new();
    private readonly float[] _mono48 = new float[Opus.FrameSamples];
    private readonly float[] _outMono48 = new float[Opus.FrameSamples];
    private readonly float[] _mono16In = new float[Opus.FrameSamples / 3];
    private readonly float[] _mono16Out = new float[Opus.FrameSamples / 3];
    private const float LevelSeconds = 3f, LevelFrom = -70f, MostLiftDb = 40f, MostCutDb = 20f;
    private float _levelDb = float.NaN;
    private float _lastSample16;
    private float _lastLimit = float.NaN;
    private double _emaMsPer10Ms;
    private bool _failed;

    internal VoiceModelTier Tier { get; }
    internal Overload Load { get; } = new();
    internal int DelaySamples => _delay.Samples;
    internal float? InputDb => _delay.InputDb;
    internal float? SpeechDb { get; private set; }
    internal double MeasuredMsPer10Ms => _emaMsPer10Ms;

    private NeuralNoiseSuppressor(VoiceModelTier tier, nint dpdf, nint vox)
    {
        Tier = tier;
        _dpdf = dpdf;
        _vox = vox;
    }

    internal static NeuralNoiseSuppressor? CreateUnverifiedForTests(string modelPath)
    {
        var h = ExoVoiceMl.CreateDpdfNet(modelPath, intraThreads: 1);
        return h != 0 ? new NeuralNoiseSuppressor(VoiceModelTier.DpdfNetBaseline, h, 0) : null;
    }

    internal static NeuralNoiseSuppressor? Create(VoiceModelTier tier)
    {
        try
        {
            if (tier == VoiceModelTier.DpdfNetBaseline)
            {
                if (!VoiceModels.BaselineReady()) return null;
                var h = ExoVoiceMl.CreateDpdfNet(VoiceModels.BaselineModelPath, intraThreads: 1);
                return h != 0 ? new NeuralNoiseSuppressor(tier, h, 0) : null;
            }
            if (tier == VoiceModelTier.DpdfNet48kHr)
            {
                if (!VoiceModels.HighQualityReady()) return null;
                var h = ExoVoiceMl.CreateDpdfNet(VoiceModels.HighQualityModelPath, intraThreads: 1);
                return h != 0 ? new NeuralNoiseSuppressor(tier, h, 0) : null;
            }
            if (tier == VoiceModelTier.VoxLockTse)
            {
                if (!VoiceModels.VoxLockReady()) return null;
                if (VoiceModels.LoadEnrollmentAudio() is not { } enroll) return null;
                var h = ExoVoiceMl.CreateVoxLock(VoiceModels.VoxLockEnrollPath, VoiceModels.VoxLockChunkPath, intraThreads: 1);
                if (h == 0) return null;
                if (!ExoVoiceMl.EnrollVoxLock16k(h, enroll))
                {
                    ExoVoiceMl.DestroyVoxLock(h);
                    return null;
                }
                return new NeuralNoiseSuppressor(tier, 0, h);
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Discord voice: exo_voice_ml unavailable (" + ex.GetType().Name + ").");
            return null;
        }
    }

    internal void Process(float[] stereo, int limitDb)
    {
        if (_failed || stereo.Length != VoiceAudio.FrameFloats) return;
        double power = 0;
        for (var i = 0; i < Opus.FrameSamples; i++)
        {
            var s = _mono48[i] = 0.5f * (stereo[2 * i] + stereo[2 * i + 1]);
            power += s * (double)s;
        }
        var db = 10f * MathF.Log10((float)(power / Opus.FrameSamples) + 1e-12f);
        if (db > LevelFrom && (SpeechDb is null || SpeechDb > VoiceGate.SpeechDb))
            _levelDb = float.IsNaN(_levelDb) ? db : _levelDb + (db - _levelDb) * Opus.FrameSamples / (LevelSeconds * Opus.SampleRate);
        var gain = float.IsNaN(_levelDb) ? 1f : MathF.Pow(10f, Math.Clamp(NoiseSuppressor.ModelDb - _levelDb, -MostCutDb, MostLiftDb) / 20f);
        if (gain != 1f)
        {
            for (var i = 0; i < stereo.Length; i++) stereo[i] *= gain;
            for (var i = 0; i < Opus.FrameSamples; i++) _mono48[i] *= gain;
        }

        var t0 = Stopwatch.GetTimestamp();
        float speech;
        if (_dpdf != 0)
        {
            if (MathF.Abs(limitDb - _lastLimit) > 0.5f)
            {
                ExoVoiceMl.SetDpdfNetAttenuation(_dpdf, limitDb);
                _lastLimit = limitDb;
            }
            speech = ExoVoiceMl.ProcessDpdfNet48kStereo(_dpdf, stereo);
        }
        else if (_vox != 0)
        {
            _decimator.Run(_mono48, _mono16In);
            speech = ExoVoiceMl.ProcessVoxLock16k(_vox, _mono16In, _mono16Out);
            var prev = _lastSample16;
            for (var i = 0; i < _mono16Out.Length; i++)
            {
                var cur = _mono16Out[i];
                var s0 = prev + (cur - prev) * (1f / 3f);
                var s1 = prev + (cur - prev) * (2f / 3f);
                var b = i * 6;
                stereo[b] = stereo[b + 1] = s0;
                stereo[b + 2] = stereo[b + 3] = s1;
                stereo[b + 4] = stereo[b + 5] = cur;
                prev = cur;
            }
            _lastSample16 = prev;
        }
        else
        {
            if (gain != 1f)
                for (var i = 0; i < stereo.Length; i++) stereo[i] /= gain;
            return;
        }

        var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        var msPer10 = ms * 0.5;
        _emaMsPer10Ms = _emaMsPer10Ms <= 0 ? msPer10 : _emaMsPer10Ms * 0.9 + msPer10 * 0.1;
        Load.Add(ms);

        if (float.IsNaN(speech))
        {
            _failed = true;
            SpeechDb = null;
            if (gain != 1f)
                for (var i = 0; i < stereo.Length; i++) stereo[i] /= gain;
            return;
        }

        SpeechDb = speech;
        if (gain != 1f)
        {
            for (var i = 0; i < stereo.Length; i++) stereo[i] /= gain;
            for (var i = 0; i < Opus.FrameSamples; i++) _mono48[i] /= gain;
        }
        for (var i = 0; i < Opus.FrameSamples; i++)
            _outMono48[i] = stereo[2 * i];
        _delay.Add(_mono48, _outMono48);
    }

    public void Dispose()
    {
        var d = Interlocked.Exchange(ref _dpdf, 0);
        if (d != 0) ExoVoiceMl.DestroyDpdfNet(d);
        var v = Interlocked.Exchange(ref _vox, 0);
        if (v != 0) ExoVoiceMl.DestroyVoxLock(v);
    }
}
