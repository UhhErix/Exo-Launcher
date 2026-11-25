using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Microsoft.UI.Dispatching;

public enum DispatcherQueuePriority
{
    Low = -10,
    Normal = 0,
    High = 10,
}

public delegate void DispatcherQueueHandler();

public sealed class DispatcherQueue
{
    private const uint WmQuit = 0x0012;
    private const uint WmAppDispatch = 0x8001;
    private static readonly IntPtr HwndMessage = new(-3);

    [ThreadStatic]
    private static DispatcherQueue? _current;

    private readonly ConcurrentQueue<DispatcherQueueHandler> _high = new();
    private readonly ConcurrentQueue<DispatcherQueueHandler> _normal = new();
    private readonly ConcurrentQueue<DispatcherQueueHandler> _low = new();
    private readonly int _threadId;
    private IntPtr _hwnd;
    private int _shutdown;

    internal DispatcherQueue(int threadId, IntPtr hwnd)
    {
        _threadId = threadId;
        _hwnd = hwnd;
    }

    public bool HasThreadAccess => Environment.CurrentManagedThreadId == _threadId;

    public static DispatcherQueue? GetForCurrentThread() => _current;

    internal static void SetCurrent(DispatcherQueue? queue) => _current = queue;

    public bool TryEnqueue(DispatcherQueueHandler callback) =>
        TryEnqueue(DispatcherQueuePriority.Normal, callback);

    public bool TryEnqueue(DispatcherQueuePriority priority, DispatcherQueueHandler callback)
    {
        if (callback is null || Volatile.Read(ref _shutdown) != 0) return false;
        var hwnd = Volatile.Read(ref _hwnd);
        if (hwnd == IntPtr.Zero) return false;

        switch (priority)
        {
            case DispatcherQueuePriority.High:
                _high.Enqueue(callback);
                break;
            case DispatcherQueuePriority.Low:
                _low.Enqueue(callback);
                break;
            default:
                _normal.Enqueue(callback);
                break;
        }
        PostMessageW(hwnd, WmAppDispatch, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    public DispatcherQueueTimer CreateTimer() => new(this);

    internal void DrainPending()
    {
        while (Volatile.Read(ref _shutdown) == 0)
        {
            if (_high.TryDequeue(out var work) ||
                _normal.TryDequeue(out work) ||
                _low.TryDequeue(out work))
            {
                work();
                continue;
            }
            break;
        }
    }

    internal void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        var hwnd = Interlocked.Exchange(ref _hwnd, IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
            PostMessageW(hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    internal static IntPtr CreateMessageWindow() =>
        CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    internal static void DestroyMessageWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
    }

    internal static void RunMessageLoop(DispatcherQueue queue)
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WmAppDispatch)
            {
                queue.DrainPending();
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            queue.DrainPending();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);
}

public sealed class DispatcherQueueTimer
{
    private readonly DispatcherQueue _queue;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private bool _running;

    internal DispatcherQueueTimer(DispatcherQueue queue) => _queue = queue;

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value;
            lock (_gate)
            {
                if (_running) _timer?.Change(_interval, IsRepeating ? _interval : Timeout.InfiniteTimeSpan);
            }
        }
    }

    public bool IsRepeating { get; set; } = true;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    public event Action<DispatcherQueueTimer, object>? Tick;

    public void Start()
    {
        lock (_gate)
        {
            _running = true;
            _timer?.Dispose();
            var period = IsRepeating ? _interval : Timeout.InfiniteTimeSpan;
            _timer = new Timer(_ => OnTimer(), null, _interval, period);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimer()
    {
        if (!IsRunning) return;
        _queue.TryEnqueue(() =>
        {
            if (!IsRunning) return;
            if (!IsRepeating) Stop();
            Tick?.Invoke(this, new object());
        });
    }
}

public sealed class DispatcherQueueController
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _exited;

    private DispatcherQueueController(DispatcherQueue queue, Thread thread, TaskCompletionSource exited)
    {
        DispatcherQueue = queue;
        _thread = thread;
        _exited = exited;
    }

    public DispatcherQueue DispatcherQueue { get; }

    public static DispatcherQueueController CreateOnDedicatedThread()
    {
        using var ready = new ManualResetEventSlim(false);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue? created = null;
        var thread = new Thread(() =>
        {
            var hwnd = DispatcherQueue.CreateMessageWindow();
            var queue = new DispatcherQueue(Environment.CurrentManagedThreadId, hwnd);
            DispatcherQueue.SetCurrent(queue);
            created = queue;
            ready.Set();
            try
            {
                DispatcherQueue.RunMessageLoop(queue);
            }
            finally
            {
                DispatcherQueue.SetCurrent(null);
                DispatcherQueue.DestroyMessageWindow(hwnd);
                exited.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "Exo Headless STA Dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return new DispatcherQueueController(created!, thread, exited);
    }

    public ShutdownAwaitable ShutdownQueueAsync()
    {
        DispatcherQueue.RequestShutdown();
        return new ShutdownAwaitable(_exited.Task);
    }

    public readonly struct ShutdownAwaitable(Task task)
    {
        public Task AsTask() => task;
        public System.Runtime.CompilerServices.TaskAwaiter GetAwaiter() => task.GetAwaiter();
    }
}

public sealed class DispatcherQueueSynchronizationContext(DispatcherQueue dispatcherQueue) : SynchronizationContext
{
    private readonly DispatcherQueue _dispatcherQueue = dispatcherQueue;

    public override void Post(SendOrPostCallback d, object? state) =>
        _dispatcherQueue.TryEnqueue(() => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim(false);
        Exception? error = null;
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try { d(state); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }))
        {
            return;
        }
        done.Wait();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public override SynchronizationContext CreateCopy() =>
        new DispatcherQueueSynchronizationContext(_dispatcherQueue);
}
