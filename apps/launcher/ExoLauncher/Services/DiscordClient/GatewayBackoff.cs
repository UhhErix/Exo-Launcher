namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// How long the Discord gateway waits before it connects again: 1 s, doubling to 60 s, plus up to a second of jitter so
/// clients don't all come back at once. Discord's 4008 (too many messages) waits at least a minute.
/// The backoff starts over only after a connection that stayed up for a minute. It used to start over on READY, so a
/// gateway that dropped right after each READY came back every second or two, indefinitely.
/// </summary>
internal static class GatewayBackoff
{
    internal static readonly TimeSpan Start = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan Stable = TimeSpan.FromMinutes(1);

    /// <summary>The wait before the next connect, and the backoff the attempt after it starts from.</summary>
    internal static (TimeSpan Wait, TimeSpan Next) Plan(TimeSpan backoff, bool limited, bool stable, int jitterMs)
    {
        if (limited) backoff = Cap;
        else if (stable) backoff = Start;
        var wait = backoff + TimeSpan.FromMilliseconds(jitterMs);
        return (wait, TimeSpan.FromTicks(Math.Min(Cap.Ticks, backoff.Ticks * 2)));
    }
}
