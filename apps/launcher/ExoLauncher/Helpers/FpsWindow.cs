namespace ExoLauncher.Helpers;

/// <summary>What a game's presents say now. <paramref name="Seen"/>: it has presented at least once, so a 0 means it stalled.</summary>
internal readonly record struct FpsReading(int Fps, int? Low, bool Seen);

/// <summary>
/// One process's frames: a ring of present timestamps, written by the trace's callback (one writer, no allocation) and
/// read twice a second by another thread. The FPS is frames over the last second that has arrived: ETW delivers in
/// batches up to a second late, so it is counted back from the newest frame, not from now. A game with nothing new
/// for <see cref="StallSeconds"/> reads 0, never its last number. Smoothed so its digits hold still: a small wobble
/// is averaged and changes the number only once it has moved by most of one, and a real change (a menu, a loading screen) lands at once.
/// Only what is asked for is worked out: without 1% low it reads the last two seconds and no more.
/// </summary>
internal sealed class FpsWindow(long frequency)
{
    internal const int Capacity = 2048;
    internal const double StallSeconds = 2;
    private const double LowSeconds = 10, SnapShare = 0.2, Ease = 0.5, HoldWithin = 0.6, ReadSeconds = 2;
    private const int MinGapsForLow = 100;
    // The writer may be this far into the oldest slots while they are copied.
    private const int WriterMargin = 64;

    // One reader at a time and nothing kept between reads, so its scratch is shared by every window on its thread.
    [ThreadStatic] private static long[]? t_frames;
    [ThreadStatic] private static long[]? t_gaps;

    private readonly long[] _ring = new long[Capacity];
    private long _written, _drained, _lastDrained = long.MinValue;
    private double _smooth;
    private int _shown = -1;
    private int? _low;

    internal void Add(long ticks)
    {
        var at = _written;
        _ring[(int)(at & (Capacity - 1))] = ticks;
        Volatile.Write(ref _written, at + 1);
    }

    /// <summary>
    /// The gaps between frames since the last drain, in milliseconds, each once (the session recorder's read; it has its own place
    /// in the ring and leaves <see cref="Read"/> alone). The first frame has no gap. Frames the ring overran since the last drain
    /// are skipped and counted in the answer. On the draining thread only.
    /// </summary>
    internal long Drain(List<double> gapsMs)
    {
        var written = Volatile.Read(ref _written);
        var from = _drained;
        long lost = 0;
        const int room = Capacity - WriterMargin;
        if (written - from > room)
        {
            lost = written - from - room;
            from = written - room;
            _lastDrained = long.MinValue;
        }
        for (var at = from; at < written; at++)
        {
            var ticks = _ring[(int)(at & (Capacity - 1))];
            if (_lastDrained != long.MinValue && ticks > _lastDrained) gapsMs.Add((ticks - _lastDrained) * 1000.0 / frequency);
            _lastDrained = ticks;
        }
        _drained = written;
        return lost;
    }

    /// <summary>On the reading thread only: it holds the smoothing.</summary>
    internal FpsReading Read(long now, bool withLow = false)
    {
        var written = Volatile.Read(ref _written);
        var available = (int)Math.Min(written, Capacity - WriterMargin);
        if (available == 0) return new(0, null, false);
        var scratch = t_frames ??= new long[Capacity];
        var oldest = _ring[(int)((written - 1) & (Capacity - 1))] - (long)(ReadSeconds * frequency);
        var start = Capacity;
        for (var i = 0; i < available; i++)
        {
            var ticks = _ring[(int)((written - 1 - i) & (Capacity - 1))];
            if (!withLow && ticks < oldest && i > 0) break;
            scratch[--start] = ticks;
        }
        var frames = scratch.AsSpan(start);
        // A session's common buffer keeps them in order; should buffers ever hand some over late, this is the only sort.
        if (!InOrder(frames)) frames.Sort();
        var newest = frames[^1];
        if (withLow) _low = LowOf(frames, frequency) ?? _low;
        if (now - newest > StallSeconds * frequency)
        {
            _smooth = 0;
            _shown = 0;
            return new(0, _low, true);
        }
        return new(Hold(RateOf(frames, frequency)), _low, true);
    }

    private static bool InOrder(ReadOnlySpan<long> frames)
    {
        for (var i = 1; i < frames.Length; i++)
            if (frames[i] < frames[i - 1]) return false;
        return true;
    }

    /// <summary>
    /// Frames per second over the last second up to the newest frame. The span starts at the frame before that second where
    /// there is one, so a long hitch inside it counts; a lone first frame says nothing yet.
    /// </summary>
    private static double RateOf(ReadOnlySpan<long> frames, long frequency)
    {
        var newest = frames[^1];
        var start = newest - frequency;
        var first = frames.Length - 1;
        while (first > 0 && frames[first] > start) first--;
        var span = newest - frames[first];
        return span <= 0 ? 0 : (frames.Length - 1 - first) * (double)frequency / span;
    }

    /// <summary>The average rate of the slowest 1% of frames in the last ten seconds; null before there are enough of them to say.</summary>
    private static int? LowOf(ReadOnlySpan<long> frames, long frequency)
    {
        var start = frames[^1] - (long)(LowSeconds * frequency);
        var first = frames.Length - 1;
        while (first > 0 && frames[first - 1] >= start) first--;
        var count = frames.Length - 1 - first;
        if (count < MinGapsForLow) return null;
        var gaps = (t_gaps ??= new long[Capacity]).AsSpan(0, count);
        for (var i = 0; i < count; i++) gaps[i] = frames[first + i + 1] - frames[first + i];
        gaps.Sort();
        var slowest = count / 100;
        long sum = 0;
        for (var i = count - slowest; i < count; i++) sum += gaps[i];
        return sum <= 0 ? null : (int)Math.Round(slowest * (double)frequency / sum);
    }

    private int Hold(double rate)
    {
        if (_shown <= 0 || Math.Abs(rate - _smooth) > _smooth * SnapShare)
        {
            _smooth = rate;
            return _shown = (int)Math.Round(rate);
        }
        _smooth += Ease * (rate - _smooth);
        if (Math.Abs(_smooth - _shown) >= HoldWithin) _shown = (int)Math.Round(_smooth);
        return _shown;
    }
}

/// <summary>What a trace event counts as: a present call a game made (DXGI, D3D9), or one the kernel saw (any API).</summary>
internal enum PresentKind { None, Api, Kernel }

/// <summary>
/// The presents of the game's processes, one <see cref="FpsWindow"/> each, nothing for any other process. A process's API
/// presents are its frames; what the kernel saw counts only for a process with none of them (Vulkan, OpenGL), so a frame
/// is never counted twice. The kernel can report one present twice (one flip per plane), so reports closer together than
/// any real frame are one.
/// </summary>
internal sealed class PresentSlots(long frequency)
{
    internal const int MaxProcesses = 16;
    private readonly long _apiCovers = (long)(FpsWindow.StallSeconds * frequency);
    private readonly long _sameFlip = frequency / 4000;

    private sealed class Slot(int pid, long frequency)
    {
        internal readonly int Pid = pid;
        internal readonly FpsWindow Window = new(frequency);
        internal long LastApi = long.MinValue / 2, LastKernel = long.MinValue / 2;
    }

    private Slot[] _slots = [];

    /// <summary>Which processes count from now on; a process kept keeps its frames.</summary>
    internal void Watch(IEnumerable<int> pids)
    {
        var old = Volatile.Read(ref _slots);
        Volatile.Write(ref _slots, pids.Distinct().Take(MaxProcesses)
            .Select(pid => Array.Find(old, slot => slot.Pid == pid) ?? new Slot(pid, frequency)).ToArray());
    }

    /// <summary>The same processes with nothing seen yet: after a pause, the frames from before it say nothing about now.</summary>
    internal void Forget() => Volatile.Write(ref _slots, Array.ConvertAll(Volatile.Read(ref _slots), slot => new Slot(slot.Pid, frequency)));

    /// <summary>From the trace's callback: it runs for every present it is sent and allocates nothing.</summary>
    internal void Record(int pid, PresentKind kind, long ticks)
    {
        foreach (var slot in Volatile.Read(ref _slots))
        {
            if (slot.Pid != pid) continue;
            if (kind == PresentKind.Api) slot.LastApi = ticks;
            else
            {
                if (ticks - slot.LastApi < _apiCovers || ticks - slot.LastKernel < _sameFlip) return;
                slot.LastKernel = ticks;
            }
            slot.Window.Add(ticks);
            return;
        }
    }

    /// <summary>
    /// The frame gaps of the process in front if it presented, else of whichever presented the most (never added together), each
    /// gap once. Every process's place in its ring moves on, so a process left behind does not pile up. Returns the frames lost to a ring overrun.
    /// </summary>
    internal int Drain(int front, List<double> gapsMs)
    {
        List<double>? pick = null;
        var pickPid = 0;
        var lost = 0L;
        foreach (var slot in Volatile.Read(ref _slots))
        {
            var gaps = new List<double>();
            lost += slot.Window.Drain(gaps);
            if (gaps.Count == 0) continue;
            if (pick is null || slot.Pid == front || (pickPid != front && gaps.Count > pick.Count)) (pick, pickPid) = (gaps, slot.Pid);
        }
        if (pick is not null) gapsMs.AddRange(pick);
        return (int)Math.Min(lost, int.MaxValue);
    }

    /// <summary>Whether any of the processes has made an API present lately: then the kernel's reports are of no use.</summary>
    internal bool ApiLately(long now)
    {
        foreach (var slot in Volatile.Read(ref _slots))
            if (now - slot.LastApi < _apiCovers) return true;
        return false;
    }

    /// <summary>The process in front if it is presenting, else whichever presents the most. Their rates are never added.</summary>
    internal FpsReading Read(long now, int front, bool withLow = false)
    {
        FpsReading best = default, inFront = default;
        foreach (var slot in Volatile.Read(ref _slots))
        {
            var reading = slot.Window.Read(now, withLow);
            if (slot.Pid == front) inFront = reading;
            if (reading.Fps > best.Fps || !best.Seen && reading.Seen) best = reading;
        }
        return inFront.Fps > 0 ? inFront : best;
    }
}
