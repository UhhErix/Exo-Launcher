using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ExoLauncher.Services.DiscordClient.Voice;

internal sealed class SoftKneeLimiter
{
    internal const float DefaultCeilingDb = -1f;
    internal const float DefaultKneeDb = 6f;
    internal const float DefaultLookaheadMs = 2f;
    internal const float DefaultReleaseMs = 60f;

    private readonly float _ceilingDb;
    private readonly float _ceilingLinear;
    private readonly float _kneeDb;
    private readonly float _kneeStartDb;
    private readonly float _kneeEndDb;
    private readonly float _kneeStartLinear;
    private readonly float _invTwoKnee;
    private readonly float _releaseAlpha;

    private readonly int _lookahead;
    private readonly int _window;
    private readonly float[] _delayInterleaved;
    private int _delayPos;

    private readonly float[] _dequeVal;
    private readonly long[] _dequeIdx;
    private readonly int _dequeMask;
    private int _dequeHead;
    private int _dequeTail;
    private long _sampleIndex;

    private readonly float[] _gainRing;
    private int _gainPos;
    private float _heldGain = 1f;
    private bool _unityActive = true;

    internal SoftKneeLimiter(
        int sampleRate = Opus.SampleRate,
        float ceilingDb = DefaultCeilingDb,
        float kneeDb = DefaultKneeDb,
        float lookaheadMs = DefaultLookaheadMs,
        float releaseMs = DefaultReleaseMs)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _ceilingDb = ceilingDb;
        _ceilingLinear = MathF.Pow(10f, ceilingDb / 20f);
        _kneeDb = MathF.Max(0f, kneeDb);
        _kneeStartDb = ceilingDb - _kneeDb * 0.5f;
        _kneeEndDb = ceilingDb + _kneeDb * 0.5f;
        _kneeStartLinear = MathF.Pow(10f, _kneeStartDb / 20f);
        _invTwoKnee = _kneeDb > 0f ? 1f / (2f * _kneeDb) : 0f;
        _releaseAlpha = 1f - MathF.Exp(-1f / MathF.Max(1f, sampleRate * releaseMs * 0.001f));

        _lookahead = Math.Max(1, (int)MathF.Round(sampleRate * lookaheadMs * 0.001f));
        _window = _lookahead + 1;
        _delayInterleaved = new float[_lookahead * 2];

        var cap = 1;
        while (cap < _window + 2) cap <<= 1;
        _dequeVal = new float[cap];
        _dequeIdx = new long[cap];
        _dequeMask = cap - 1;

        _gainRing = new float[_window];
        Array.Fill(_gainRing, 1f);
    }

    internal int LookaheadSamples => _lookahead;
    internal float CeilingDb => _ceilingDb;
    internal float CeilingLinear => _ceilingLinear;
    internal float KneeDb => _kneeDb;

    internal void Reset()
    {
        Array.Clear(_delayInterleaved);
        _delayPos = 0;
        _dequeHead = 0;
        _dequeTail = 0;
        _sampleIndex = 0;
        Array.Fill(_gainRing, 1f);
        _gainPos = 0;
        _heldGain = 1f;
        _unityActive = true;
    }

    internal float TargetGain(float peak)
    {
        if (peak <= _kneeStartLinear) return 1f;
        var xDb = 20f * MathF.Log10(peak);
        if (_kneeDb <= 0f || xDb >= _kneeEndDb)
            return _ceilingLinear / peak;
        var over = xDb - _kneeStartDb;
        var gainDb = -(over * over) * _invTwoKnee;
        return MathF.Pow(10f, gainDb / 20f);
    }

    internal void Process(Span<float> stereo)
    {
        var frames = stereo.Length >> 1;
        if (frames == 0) return;

        if (_unityActive && FrameMaxAbs(stereo) <= _kneeStartLinear)
        {
            DelayPassThrough(stereo, frames);
            return;
        }

        var delay = _delayInterleaved;
        var delayLen = _lookahead * 2;
        var delayPos = _delayPos * 2;

        var dequeVal = _dequeVal;
        var dequeIdx = _dequeIdx;
        var mask = _dequeMask;
        var head = _dequeHead;
        var tail = _dequeTail;
        var idx = _sampleIndex;
        var window = _window;

        var gainRing = _gainRing;
        var gainPos = _gainPos;
        var held = _heldGain;
        var releaseAlpha = _releaseAlpha;
        var ceiling = _ceilingLinear;
        var invWindow = 1.0 / window;

        double gainSum = 0;
        for (var k = 0; k < window; k++) gainSum += gainRing[k];

        var limit = frames << 1;
        for (var i = 0; i < limit; i += 2)
        {
            var inL = stereo[i];
            var inR = stereo[i + 1];
            var peak = MathF.Max(MathF.Abs(inL), MathF.Abs(inR));

            while (tail != head && dequeVal[(tail - 1) & mask] <= peak)
                tail = (tail - 1) & mask;
            dequeVal[tail] = peak;
            dequeIdx[tail] = idx;
            tail = (tail + 1) & mask;
            if (dequeIdx[head] <= idx - window)
                head = (head + 1) & mask;
            idx++;

            var windowPeak = dequeVal[head];
            var desired = TargetGain(windowPeak);
            held = desired < held ? desired : held + (desired - held) * releaseAlpha;

            gainSum -= gainRing[gainPos];
            gainRing[gainPos] = held;
            gainSum += held;
            if (++gainPos == window) gainPos = 0;

            var smoothGain = (float)(gainSum * invWindow);

            var outL = delay[delayPos] * smoothGain;
            var outR = delay[delayPos + 1] * smoothGain;
            delay[delayPos] = inL;
            delay[delayPos + 1] = inR;
            delayPos += 2;
            if (delayPos == delayLen) delayPos = 0;

            stereo[i] = Math.Clamp(outL, -ceiling, ceiling);
            stereo[i + 1] = Math.Clamp(outR, -ceiling, ceiling);
        }

        _delayPos = delayPos >> 1;
        _dequeHead = head;
        _dequeTail = tail;
        _sampleIndex = idx;
        _gainPos = gainPos;
        _heldGain = held;

        if (held >= 0.99999f && (head == tail || dequeVal[head] <= _kneeStartLinear) && gainSum >= window - 1e-4)
        {
            Array.Fill(gainRing, 1f);
            _heldGain = 1f;
            _dequeHead = 0;
            _dequeTail = 0;
            _unityActive = true;
        }
        else
        {
            _unityActive = false;
        }
    }

    private void DelayPassThrough(Span<float> stereo, int frames)
    {
        var delay = _delayInterleaved;
        var delayLen = _lookahead * 2;
        var delayPos = _delayPos * 2;
        var total = frames << 1;

        if (total >= delayLen)
        {
            var firstPart = delayLen - delayPos;
            var keepStart = total - delayLen;
            Span<float> tailCopy = stackalloc float[delayLen];
            stereo.Slice(keepStart, delayLen).CopyTo(tailCopy);
            stereo.Slice(0, keepStart).CopyTo(stereo.Slice(delayLen, keepStart));
            delay.AsSpan(delayPos, firstPart).CopyTo(stereo.Slice(0, firstPart));
            if (delayPos > 0) delay.AsSpan(0, delayPos).CopyTo(stereo.Slice(firstPart, delayPos));
            tailCopy.CopyTo(delay);
            _delayPos = 0;
        }
        else
        {
            for (var i = 0; i < total; i += 2)
            {
                var inL = stereo[i];
                var inR = stereo[i + 1];
                stereo[i] = delay[delayPos];
                stereo[i + 1] = delay[delayPos + 1];
                delay[delayPos] = inL;
                delay[delayPos + 1] = inR;
                delayPos += 2;
                if (delayPos == delayLen) delayPos = 0;
            }
            _delayPos = delayPos >> 1;
        }
        _sampleIndex += frames;
    }

    private static float FrameMaxAbs(ReadOnlySpan<float> samples)
    {
        var i = 0;
        var max = 0f;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var vMax = Vector<float>.Zero;
            var limit = samples.Length - Vector<float>.Count;
            ref var r0 = ref MemoryMarshal.GetReference(samples);
            for (; i <= limit; i += Vector<float>.Count)
                vMax = Vector.Max(vMax, Vector.Abs(Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref r0, i))));
            for (var k = 0; k < Vector<float>.Count; k++)
                max = MathF.Max(max, vMax[k]);
        }
        for (; i < samples.Length; i++)
            max = MathF.Max(max, MathF.Abs(samples[i]));
        return max;
    }
}
