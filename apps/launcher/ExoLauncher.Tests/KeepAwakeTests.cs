using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// KeepAwake holds the system awake for overlapping jobs with one Windows call. These fail if: a second job sets the state
/// again, the first release clears a hold another job still needs, a double dispose releases twice, a throwing native call
/// wedges the counter, the call runs on the caller's thread (the flag is per thread), or a process exit leaves it set.
/// </summary>
public sealed class KeepAwakeTests
{
    private const uint Hold = KeepAwake.Continuous | KeepAwake.SystemRequired;

    private sealed class FakeState : IExecutionState
    {
        public readonly List<(uint Flags, int Thread)> Calls = [];
        public bool Fail;

        public void Set(uint flags)
        {
            Calls.Add((flags, Environment.CurrentManagedThreadId));
            if (Fail) throw new InvalidOperationException("denied");
        }
    }

    [Fact]
    public void FirstHoldSetsAndLastReleaseClears()
    {
        var state = new FakeState();
        var awake = new KeepAwake(state, () => true);
        var lease = awake.HoldFor("download");
        lease.Dispose();
        Assert.Equal([Hold, KeepAwake.Continuous], state.Calls.Select(c => c.Flags));
    }

    [Fact]
    public void OverlappingJobsShareOneHold()
    {
        var state = new FakeState();
        var awake = new KeepAwake(state, () => true);
        var first = awake.HoldFor("update");
        var second = awake.HoldFor("shader warm");
        Assert.Single(state.Calls); // the second job adds no native call
        first.Dispose();
        Assert.Single(state.Calls); // the second job still needs it
        second.Dispose();
        Assert.Equal([Hold, KeepAwake.Continuous], state.Calls.Select(c => c.Flags));
    }

    [Fact]
    public void DoubleDisposeReleasesOnce()
    {
        var state = new FakeState();
        var awake = new KeepAwake(state, () => true);
        var a = awake.HoldFor("a");
        var b = awake.HoldFor("b");
        a.Dispose();
        a.Dispose(); // must not release b's hold
        Assert.Single(state.Calls); // b still holds it
        b.Dispose();
        Assert.Equal(2, state.Calls.Count);
    }

    [Fact]
    public void DisabledSettingMakesHoldsNoOp()
    {
        var state = new FakeState();
        var awake = new KeepAwake(state, () => false);
        awake.HoldFor("download").Dispose();
        Assert.Empty(state.Calls);
    }

    [Fact]
    public void NativeFailureDoesNotWedgeTheCounter()
    {
        var state = new FakeState { Fail = true };
        var awake = new KeepAwake(state, () => true);
        awake.HoldFor("download").Dispose(); // the throw is logged, not thrown into the job
        state.Fail = false;
        var lease = awake.HoldFor("download");
        Assert.Equal(Hold, state.Calls[^1].Flags); // the next first hold still sets it
        lease.Dispose();
        Assert.Equal(KeepAwake.Continuous, state.Calls[^1].Flags);
    }

    [Fact]
    public void CallsRunOnOneDedicatedThread()
    {
        var state = new FakeState();
        var awake = new KeepAwake(state, () => true);
        awake.HoldFor("a").Dispose();
        awake.HoldFor("b").Dispose();
        var threads = state.Calls.Select(c => c.Thread).Distinct().ToArray();
        Assert.Single(threads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threads[0]);
    }

    [Fact]
    public void ReleaseAllClearsAndResetsTheCount()
    {
        var state = new FakeState();
        var awake = new KeepAwake(state, () => true);
        var lease = awake.HoldFor("download");
        awake.ReleaseAll(); // process exit: clear whatever is still held
        Assert.Equal(KeepAwake.Continuous, state.Calls[^1].Flags);
        lease.Dispose(); // a late release after ReleaseAll must not go below zero
        awake.HoldFor("again");
        Assert.Equal(Hold, state.Calls[^1].Flags); // count restarted from zero, so this is a first hold
    }
}
