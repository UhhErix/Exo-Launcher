namespace ExoCapture.Capture;

/// <summary>One encoded sample of the session: the video (track -1) or a sound track (0 mix, 1 game, 2 voice, 3 mic), in MP4 form, at its QPC time (100 ns).</summary>
internal sealed record Packet(int Track, byte[] Data, long Time, bool Key)
{
    internal const int Video = -1;
    internal bool IsVideo => Track == Video;
}

/// <summary>
/// Instant replay: the session's encoded packets kept in memory, from a key frame (one a second), for at most the
/// replay's seconds and at most its memory budget, whichever is less. Whole seconds go from the front, never part of
/// one, so what is kept always starts on a key frame and plays from its first frame.
/// </summary>
internal sealed class ReplayBuffer
{
    private readonly List<Packet> _packets = [];
    private int _start;
    private readonly Lock _gate = new();
    private long _bytes;

    internal int Seconds { get; private set; }
    internal long MaxBytes { get; private set; }

    internal ReplayBuffer(int seconds, long maxBytes) => Set(seconds, maxBytes);

    internal void Set(int seconds, long maxBytes)
    {
        lock (_gate)
        {
            Seconds = Math.Clamp(seconds, 5, 600);
            MaxBytes = Math.Max(16L << 20, maxBytes);
            Trim();
        }
    }

    internal long Bytes { get { lock (_gate) return _bytes; } }

    internal void Add(Packet packet)
    {
        lock (_gate)
        {
            // Nothing before the first key frame: it could not be played.
            if (_packets.Count - _start == 0 && !(packet.IsVideo && packet.Key)) return;
            _packets.Add(packet);
            _bytes += packet.Data.Length;
            if (packet.IsVideo && packet.Key) Trim();
            else if (_bytes > MaxBytes) Trim();
        }
    }

    /// <summary>Drops whole key-frame groups from the front while what is left is still at least the replay's length, or while it is over budget.</summary>
    private void Trim()
    {
        while (true)
        {
            var count = _packets.Count - _start;
            if (count == 0) break;
            var next = NextKey(_start + 1);
            if (next < 0) break;
            var newest = NewestVideoTime();
            var tooLong = newest - _packets[next].Time >= Seconds * 10_000_000L;
            var tooBig = _bytes > MaxBytes;
            if (!tooLong && !tooBig) break;
            for (var i = _start; i < next; i++) _bytes -= _packets[i].Data.Length;
            _start = next;
        }
        // Compact now and then, so the list doesn't grow without end.
        if (_start > 4096 && _start > _packets.Count / 2)
        {
            _packets.RemoveRange(0, _start);
            _start = 0;
        }
    }

    private int NextKey(int from)
    {
        for (var i = from; i < _packets.Count; i++)
            if (_packets[i].IsVideo && _packets[i].Key) return i;
        return -1;
    }

    private long NewestVideoTime()
    {
        for (var i = _packets.Count - 1; i >= _start; i--)
            if (_packets[i].IsVideo) return _packets[i].Time;
        return 0;
    }

    /// <summary>How much playable replay is held now, in milliseconds.</summary>
    internal long RetainedMs
    {
        get
        {
            lock (_gate)
            {
                if (_packets.Count - _start == 0) return 0;
                return Math.Max(0, (NewestVideoTime() - _packets[_start].Time) / 10_000);
            }
        }
    }

    /// <summary>The packets of the last <paramref name="seconds"/> (or all), from a key frame.</summary>
    internal List<Packet> Take(int? seconds = null)
    {
        lock (_gate)
        {
            if (_packets.Count - _start == 0) return [];
            var from = _start;
            if (seconds is int s)
            {
                var cut = NewestVideoTime() - s * 10_000_000L;
                for (var i = from; i < _packets.Count; i++)
                    if (_packets[i].IsVideo && _packets[i].Key)
                    {
                        if (_packets[i].Time > cut) break;
                        from = i;
                    }
            }
            return _packets.GetRange(from, _packets.Count - from);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _packets.Clear();
            _start = 0;
            _bytes = 0;
        }
    }

    /// <summary>
    /// The replay length a memory budget holds at a bit rate (the encoder's rate plus about 0.7 Mbit/s for four AAC tracks),
    /// at most the length asked for and never under five seconds: what Exo can promise for a save.
    /// </summary>
    internal static int SecondsFor(int asked, long maxBytes, long videoBitsPerSecond)
    {
        var perSecond = (videoBitsPerSecond + 4 * 160_000) / 8.0;
        var fits = (int)(maxBytes / Math.Max(1, perSecond));
        return Math.Clamp(Math.Min(asked, fits), 5, 600);
    }
}
