namespace ExoLauncher.Services;

/// <summary>
/// The numbers a session keeps, as pure functions of frame times in milliseconds (docs/architecture/session-recorder.md).
/// Ways it could be wrong, each with a test: an empty list or one frame (all zeros, or the frame itself); a 1% low that is the
/// average or the slowest single frame; a count of frames for a percent that rounds to zero; a frame of exactly 50 ms; a number
/// that is not a frame (zero, negative, not a number, a pause of five seconds or more) counted as one.
/// </summary>
internal static class FrameStats
{
    /// <summary>A frame over this is a stall.</summary>
    internal const double StallMs = 50;

    /// <summary>A gap this long or longer is the game not drawing (a loading screen, a pause), not a frame.</summary>
    internal const double NotAFrameMs = 5000;

    /// <summary>The kept histogram: one bucket per millisecond from 0 to 50 (the last bucket holds 49 up to and including 50).</summary>
    internal const int HistogramBuckets = 50;

    // The working histogram is finer so the lows are right to 0.005 ms: 0.01 ms steps up to 50 ms, 1 ms steps above.
    private const int Fine = 5000, Coarse = 4950;

    /// <summary>What a session says. Lows are the frame rate of the slowest 1% and 0.1% of frames (at least one frame).</summary>
    internal readonly record struct Result(long Frames, double Seconds, double AverageMs, double AverageFps, double Low1Fps, double Low01Fps, long Stalls, int[] Histogram);

    /// <summary>Frames fed one at a time, in a fixed amount of memory however long the session runs. One owner at a time.</summary>
    internal sealed class Accumulator
    {
        private readonly long[] _counts = new long[Fine + Coarse];
        private readonly double[] _sums = new double[Fine + Coarse];
        private readonly int[] _histogram = new int[HistogramBuckets];
        private long _frames, _stalls;
        private double _totalMs;

        internal long Frames => _frames;

        /// <summary>Seconds of frames so far.</summary>
        internal double Seconds => _totalMs / 1000;

        internal void Add(double ms)
        {
            if (!(ms > 0) || ms >= NotAFrameMs) return;
            _frames++;
            _totalMs += ms;
            if (ms > StallMs) _stalls++;
            else _histogram[Math.Min((int)ms, HistogramBuckets - 1)]++;
            var at = ms <= StallMs ? Math.Min((int)(ms * 100 + 1e-9), Fine - 1) : Fine + Math.Min((int)(ms - StallMs), Coarse - 1);
            _counts[at]++;
            _sums[at] += ms;
        }

        internal Result Snapshot()
        {
            if (_frames == 0) return new(0, 0, 0, 0, 0, 0, 0, new int[HistogramBuckets]);
            var average = _totalMs / _frames;
            return new(_frames, _totalMs / 1000, average, 1000 / average, Low(0.01), Low(0.001), _stalls, (int[])_histogram.Clone());
        }

        private double Low(double share)
        {
            var wanted = Math.Max(1, (long)Math.Ceiling(_frames * share - 1e-9));
            var left = wanted;
            double ms = 0;
            for (var at = _counts.Length - 1; at >= 0 && left > 0; at--)
            {
                if (_counts[at] == 0) continue;
                var take = Math.Min(_counts[at], left);
                ms += _sums[at] / _counts[at] * take;
                left -= take;
            }
            return wanted * 1000 / ms;
        }
    }

    /// <summary>The numbers for a list of frame times, as the accumulator would give them.</summary>
    internal static Result Of(IReadOnlyList<double> frameMs)
    {
        var accumulator = new Accumulator();
        foreach (var ms in frameMs) accumulator.Add(ms);
        return accumulator.Snapshot();
    }
}
