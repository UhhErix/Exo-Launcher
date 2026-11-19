using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Keeps the PC from sleeping while Exo downloads or installs a game or update, or warms a game's shaders (ShaderPrepare).
/// One Windows execution state, ES_SYSTEM_REQUIRED only: the screen may still turn off, and no input is sent and no power
/// setting changes. Overlapping jobs share one hold: the first hold sets it, the last release clears it. A hold is a lease,
/// so a job's <c>using</c> releases it on success, failure and cancel alike; <see cref="ReleaseAll"/> clears it at exit.
/// The flag is per thread, so every call runs on one dedicated thread that owns it. Windows clears the state when that
/// thread ends. Settings > Downloads turns the whole thing off (<see cref="SettingOn"/>); off, a hold does nothing.
/// </summary>
internal sealed class KeepAwake
{
    internal const uint Continuous = 0x80000000, SystemRequired = 0x00000001; // ES_CONTINUOUS, ES_SYSTEM_REQUIRED

    /// <summary>The setting: on by default; Settings > Downloads' switch sets it at startup.</summary>
    internal static Func<bool>? SettingOn;

    internal static readonly KeepAwake Shared = new(new WindowsExecutionState(), () => SettingOn?.Invoke() ?? true);

    private readonly IExecutionState _native;
    private readonly Func<bool> _enabled;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Lock _gate = new();
    private int _holds;

    internal KeepAwake(IExecutionState native, Func<bool> enabled)
    {
        _native = native;
        _enabled = enabled;
        new Thread(() => { foreach (var work in _work.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true,
            Name = "Exo keep-awake",
        }.Start();
    }

    /// <summary>Holds the PC awake until the returned lease is disposed. <paramref name="reason"/> is for the log only.</summary>
    internal static IDisposable Hold(string reason) => Shared.HoldFor(reason);

    /// <summary>Holds this instance's state. Disposing the lease twice releases once.</summary>
    internal IDisposable HoldFor(string reason)
    {
        lock (_gate)
        {
            if (!_enabled()) return new Lease(null);
            if (_holds++ == 0)
            {
                Apply(Continuous | SystemRequired);
                AppLog.Info($"Keep awake: {reason} holds the PC awake.");
            }
            return new Lease(this);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_holds == 0) return;
            if (--_holds > 0) return;
            Apply(Continuous);
            AppLog.Info("Keep awake: no job needs the PC awake; the power plan decides again.");
        }
    }

    /// <summary>Clears the hold whatever the holders say: at process exit, when no job can be left to release it.</summary>
    internal void ReleaseAll()
    {
        lock (_gate)
        {
            _holds = 0;
            Apply(Continuous);
        }
    }

    /// <summary>Runs the native call on the owning thread and waits for it, so the state is set before the caller goes on.</summary>
    private void Apply(uint flags)
    {
        using var done = new ManualResetEventSlim();
        _work.Add(() =>
        {
            try { _native.Set(flags); }
            catch (Exception ex) { AppLog.Debug("Keep awake: " + ex.GetType().Name + ": " + ex.Message); }
            finally { done.Set(); }
        });
        done.Wait();
    }

    /// <summary>The lease a job holds; <c>null</c> owner = Settings has the hold off, so disposing does nothing.</summary>
    private sealed class Lease(KeepAwake? owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (owner is not null && Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }
}

/// <summary>The one call that keeps or releases the system awake, behind an interface so tests count the calls.</summary>
internal interface IExecutionState
{
    void Set(uint flags);
}

internal sealed class WindowsExecutionState : IExecutionState
{
    public void Set(uint flags)
    {
        if (SetThreadExecutionState(flags) == 0) throw new InvalidOperationException("SetThreadExecutionState failed: " + Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);
}
