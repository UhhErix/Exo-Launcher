using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ExoLauncher.Services.DiscordClient.Voice;

internal sealed class LoudnessMeter
{
    internal const double AbsoluteGateLufs = -70.0;
    internal const double RelativeGateLu = -10.0;
    internal const float SilenceLufs = -100f;
    internal const int ShortTermWindowBlocks = 27;

    private const double KOffset = -0.691;
    private static readonly double AbsoluteGatePower = Math.Pow(10.0, (AbsoluteGateLufs - KOffset) / 10.0);
    private static readonly double RelativeGateFactor = Math.Pow(10.0, RelativeGateLu / 10.0);

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _hopSamples;
    private readonly bool _unitStage2;
    private readonly double _s1b0, _s1b1, _s1b2, _s1a1, _s1a2;
    private readonly double _s2b0, _s2b1, _s2b2, _s2a1, _s2a2;

    private double _m1z1, _m1z2, _m2z1, _m2z2;
    private Vector128<double> _v1z1, _v1z2, _v2z1, _v2z2;

    private readonly double[] _subEnergies = new double[4];
    private int _subIndex;
    private int _subFilled;
    private double _hopEnergy;
    private int _hopCount;

    private readonly double[] _shortTermRing = new double[ShortTermWindowBlocks];
    private int _shortTermIndex;
    private int _shortTermCount;

    private readonly double[]? _integratedBlocks;
    private int _integratedCount;

    private double _momentaryPower;
    private float _shortTermLufs = SilenceLufs;
    private bool _hasShortTerm;

    internal LoudnessMeter(int sampleRate = Opus.SampleRate, int channels = Opus.Channels, int integratedCapacity = 0)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        _sampleRate = sampleRate;
        _channels = channels;
        _hopSamples = sampleRate / 10;
        _unitStage2 = sampleRate == 48000;
        if (integratedCapacity > 0) _integratedBlocks = new double[integratedCapacity];

        ComputeKWeighting(sampleRate, out _s1b0, out _s1b1, out _s1b2, out _s1a1, out _s1a2,
            out _s2b0, out _s2b1, out _s2b2, out _s2a1, out _s2a2);
    }

    internal int SampleRate => _sampleRate;
    internal int Channels => _channels;
    internal bool HasShortTerm => _hasShortTerm;
    internal float MomentaryLufs => PowerToLufs(_momentaryPower);
    internal float ShortTermLufs => _shortTermLufs;

    internal float IntegratedLufs
    {
        get
        {
            if (_integratedBlocks is null) return _shortTermLufs;
            return ComputeGatedLufs(_integratedBlocks, _integratedCount, out _);
        }
    }

    internal static void ComputeKWeighting(int sampleRate,
        out double s1b0, out double s1b1, out double s1b2, out double s1a1, out double s1a2,
        out double s2b0, out double s2b1, out double s2b2, out double s2a1, out double s2a2)
    {
        if (sampleRate == 48000)
        {
            s1b0 = 1.53512485958697;
            s1b1 = -2.69169618940638;
            s1b2 = 1.19839281085285;
            s1a1 = -1.69065929318241;
            s1a2 = 0.73248077421585;

            s2b0 = 1.0;
            s2b1 = -2.0;
            s2b2 = 1.0;
            s2a1 = -1.99004745483398;
            s2a2 = 0.99007225036621;
            return;
        }

        const double f0Shelf = 1681.974450955533;
        const double gainDb = 3.999843853973347;
        const double qShelf = 0.7071752369554196;
        var kShelf = Math.Tan(Math.PI * f0Shelf / sampleRate);
        var vh = Math.Pow(10.0, gainDb / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0Shelf = 1.0 + kShelf / qShelf + kShelf * kShelf;
        s1b0 = (vh + vb * kShelf / qShelf + kShelf * kShelf) / a0Shelf;
        s1b1 = 2.0 * (kShelf * kShelf - vh) / a0Shelf;
        s1b2 = (vh - vb * kShelf / qShelf + kShelf * kShelf) / a0Shelf;
        s1a1 = 2.0 * (kShelf * kShelf - 1.0) / a0Shelf;
        s1a2 = (1.0 - kShelf / qShelf + kShelf * kShelf) / a0Shelf;

        const double f0Rlb = 38.13547087602444;
        const double qRlb = 0.5003270373238773;
        var kRlbRef = Math.Tan(Math.PI * f0Rlb / 48000.0);
        var a0RlbRef = 1.0 + kRlbRef / qRlb + kRlbRef * kRlbRef;
        var kRlb = Math.Tan(Math.PI * f0Rlb / sampleRate);
        var a0Rlb = 1.0 + kRlb / qRlb + kRlb * kRlb;
        var rlbScale = a0RlbRef / a0Rlb;
        s2b0 = rlbScale;
        s2b1 = -2.0 * rlbScale;
        s2b2 = rlbScale;
        s2a1 = 2.0 * (kRlb * kRlb - 1.0) / a0Rlb;
        s2a2 = (1.0 - kRlb / qRlb + kRlb * kRlb) / a0Rlb;
    }

    internal void Reset()
    {
        _m1z1 = _m1z2 = _m2z1 = _m2z2 = 0;
        _v1z1 = _v1z2 = _v2z1 = _v2z2 = Vector128<double>.Zero;
        Array.Clear(_subEnergies);
        Array.Clear(_shortTermRing);
        _subIndex = 0;
        _subFilled = 0;
        _hopEnergy = 0;
        _hopCount = 0;
        _shortTermIndex = 0;
        _shortTermCount = 0;
        _integratedCount = 0;
        _momentaryPower = 0;
        _shortTermLufs = SilenceLufs;
        _hasShortTerm = false;
    }

    internal float Feed(ReadOnlySpan<float> samples)
    {
        return _channels == 2 ? FeedStereo(samples) : FeedMono(samples);
    }

    internal double FilterStereoFrame(ReadOnlySpan<float> stereo, out bool silentInput)
    {
        var frames = stereo.Length >> 1;
        if (frames == 0)
        {
            silentInput = true;
            return 0;
        }

        var energy = FilterStereoChunk(stereo, 0, frames);
        if (energy <= 1e-4 * frames)
        {
            double raw = 0;
            ref var r0 = ref MemoryMarshal.GetReference(stereo);
            var limit = frames << 1;
            for (var i = 0; i < limit; i += 2)
            {
                double a = Unsafe.Add(ref r0, i);
                double b = Unsafe.Add(ref r0, i + 1);
                raw += a * a + b * b;
            }
            silentInput = raw <= 1e-10 * frames;
        }
        else
        {
            silentInput = false;
        }
        return energy;
    }

    private double FilterStereoChunk(ReadOnlySpan<float> stereo, int startFrame, int frameCount)
    {
        ref var r0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(stereo), startFrame << 1);
        var limit = frameCount << 1;

        var s1b0 = Vector128.Create(_s1b0);
        var s1b1 = Vector128.Create(_s1b1);
        var s1b2 = Vector128.Create(_s1b2);
        var s1a1 = Vector128.Create(-_s1a1);
        var s1a2 = Vector128.Create(-_s1a2);
        var s2a1 = Vector128.Create(-_s2a1);
        var s2a2 = Vector128.Create(-_s2a2);

        var z1 = _v1z1;
        var z2 = _v1z2;
        var z3 = _v2z1;
        var z4 = _v2z2;
        var energyVec = Vector128<double>.Zero;

        if (_unitStage2 && Fma.IsSupported && Sse2.IsSupported)
        {
            for (var i = 0; i < limit; i += 2)
            {
                var x = Sse2.ConvertToVector128Double(
                    Vector128.CreateScalarUnsafe(Unsafe.As<float, double>(ref Unsafe.Add(ref r0, i))).AsSingle());

                var u = Fma.MultiplyAdd(s1b0, x, z1);
                z1 = Fma.MultiplyAdd(s1a1, u, Fma.MultiplyAdd(s1b1, x, z2));
                z2 = Fma.MultiplyAdd(s1a2, u, s1b2 * x);

                var y = u + z3;
                z3 = Fma.MultiplyAdd(s2a1, y, z4 - (u + u));
                z4 = Fma.MultiplyAdd(s2a2, y, u);

                energyVec = Fma.MultiplyAdd(y, y, energyVec);
            }
        }
        else if (_unitStage2 && Sse2.IsSupported)
        {
            for (var i = 0; i < limit; i += 2)
            {
                var x = Sse2.ConvertToVector128Double(
                    Vector128.CreateScalarUnsafe(Unsafe.As<float, double>(ref Unsafe.Add(ref r0, i))).AsSingle());

                var u = s1b0 * x + z1;
                z1 = s1b1 * x + s1a1 * u + z2;
                z2 = s1b2 * x + s1a2 * u;

                var y = u + z3;
                z3 = z4 - (u + u) + s2a1 * y;
                z4 = u + s2a2 * y;

                energyVec += y * y;
            }
        }
        else
        {
            var s2b0 = Vector128.Create(_s2b0);
            var s2b1 = Vector128.Create(_s2b1);
            var s2b2 = Vector128.Create(_s2b2);
            for (var i = 0; i < limit; i += 2)
            {
                var x = Vector128.Create((double)Unsafe.Add(ref r0, i), (double)Unsafe.Add(ref r0, i + 1));

                var u = s1b0 * x + z1;
                z1 = s1b1 * x + s1a1 * u + z2;
                z2 = s1b2 * x + s1a2 * u;

                var y = s2b0 * u + z3;
                z3 = s2b1 * u + s2a1 * y + z4;
                z4 = s2b2 * u + s2a2 * y;

                energyVec += y * y;
            }
        }

        _v1z1 = z1;
        _v1z2 = z2;
        _v2z1 = z3;
        _v2z2 = z4;
        return energyVec.GetElement(0) + energyVec.GetElement(1);
    }

    internal void RecordFrameEnergy(double frameEnergy, int frames)
    {
        if (frames <= 0) return;
        var hopSamples = _hopSamples;
        var remaining = frames;
        var energyPerFrame = frameEnergy / frames;

        while (remaining > 0)
        {
            var need = hopSamples - _hopCount;
            if (remaining < need)
            {
                _hopEnergy += energyPerFrame * remaining;
                _hopCount += remaining;
                return;
            }
            _hopEnergy += energyPerFrame * need;
            CommitHop(_hopEnergy);
            _hopEnergy = 0;
            _hopCount = 0;
            remaining -= need;
        }
    }

    private float FeedMono(ReadOnlySpan<float> mono)
    {
        if (mono.IsEmpty) return SilenceLufs;

        double s1b0 = _s1b0, s1b1 = _s1b1, s1b2 = _s1b2, s1a1 = _s1a1, s1a2 = _s1a2;
        double s2b0 = _s2b0, s2b1 = _s2b1, s2b2 = _s2b2, s2a1 = _s2a1, s2a2 = _s2a2;
        double z1 = _m1z1, z2 = _m1z2, z3 = _m2z1, z4 = _m2z2;

        double hopEnergy = _hopEnergy;
        int hopCount = _hopCount;
        int hopSamples = _hopSamples;
        double frameEnergy = 0;
        int pos = 0;

        while (pos < mono.Length)
        {
            var chunk = Math.Min(mono.Length - pos, hopSamples - hopCount);
            var end = pos + chunk;
            double chunkEnergy = 0;
            for (var i = pos; i < end; i++)
            {
                double x = mono[i];
                double u = s1b0 * x + z1;
                z1 = s1b1 * x - s1a1 * u + z2;
                z2 = s1b2 * x - s1a2 * u;

                double y = s2b0 * u + z3;
                z3 = s2b1 * u - s2a1 * y + z4;
                z4 = s2b2 * u - s2a2 * y;

                chunkEnergy += y * y;
            }
            frameEnergy += chunkEnergy;
            hopEnergy += chunkEnergy;
            hopCount += chunk;
            pos = end;
            if (hopCount == hopSamples)
            {
                CommitHop(hopEnergy);
                hopEnergy = 0;
                hopCount = 0;
            }
        }

        _m1z1 = z1; _m1z2 = z2; _m2z1 = z3; _m2z2 = z4;
        _hopEnergy = hopEnergy;
        _hopCount = hopCount;

        return PowerToLufs(frameEnergy / mono.Length);
    }

    private float FeedStereo(ReadOnlySpan<float> stereo)
    {
        var frames = stereo.Length >> 1;
        if (frames == 0) return SilenceLufs;

        double hopEnergy = _hopEnergy;
        int hopCount = _hopCount;
        int hopSamples = _hopSamples;
        double frameEnergy = 0;
        int pos = 0;

        while (pos < frames)
        {
            var chunk = Math.Min(frames - pos, hopSamples - hopCount);
            var chunkEnergy = FilterStereoChunk(stereo, pos, chunk);
            frameEnergy += chunkEnergy;
            hopEnergy += chunkEnergy;
            hopCount += chunk;
            pos += chunk;
            if (hopCount == hopSamples)
            {
                CommitHop(hopEnergy);
                hopEnergy = 0;
                hopCount = 0;
            }
        }

        _hopEnergy = hopEnergy;
        _hopCount = hopCount;

        return PowerToLufs(frameEnergy / frames);
    }

    private void CommitHop(double energy)
    {
        _subEnergies[_subIndex] = energy;
        _subIndex = (_subIndex + 1) & 3;
        if (_subFilled < 4) _subFilled++;
        if (_subFilled < 4) return;

        var blockPower = (_subEnergies[0] + _subEnergies[1] + _subEnergies[2] + _subEnergies[3]) / (4.0 * _hopSamples);
        _momentaryPower = blockPower;

        _shortTermRing[_shortTermIndex] = blockPower;
        _shortTermIndex++;
        if (_shortTermIndex == ShortTermWindowBlocks) _shortTermIndex = 0;
        if (_shortTermCount < ShortTermWindowBlocks) _shortTermCount++;

        _shortTermLufs = ComputeGatedLufs(_shortTermRing, _shortTermCount, out _hasShortTerm);

        if (_integratedBlocks is not null && blockPower > AbsoluteGatePower && _integratedCount < _integratedBlocks.Length)
            _integratedBlocks[_integratedCount++] = blockPower;
    }

    private static float ComputeGatedLufs(double[] blocks, int count, out bool hasValid)
    {
        double absSum = 0;
        int absCount = 0;
        for (var i = 0; i < count; i++)
        {
            var p = blocks[i];
            if (p > AbsoluteGatePower)
            {
                absSum += p;
                absCount++;
            }
        }
        if (absCount == 0)
        {
            hasValid = false;
            return SilenceLufs;
        }

        var relThreshold = (absSum / absCount) * RelativeGateFactor;
        double relSum = 0;
        int relCount = 0;
        for (var i = 0; i < count; i++)
        {
            var p = blocks[i];
            if (p > AbsoluteGatePower && p > relThreshold)
            {
                relSum += p;
                relCount++;
            }
        }
        if (relCount == 0)
        {
            hasValid = false;
            return SilenceLufs;
        }

        hasValid = true;
        return PowerToLufs(relSum / relCount);
    }

    internal static float PowerToLufs(double power)
    {
        if (power <= 1e-12) return SilenceLufs;
        var lufs = (float)(KOffset + 10.0 * Math.Log10(power));
        return MathF.Max(SilenceLufs, lufs);
    }
}
