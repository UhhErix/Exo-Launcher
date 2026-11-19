using System.Runtime.InteropServices;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// Auto tune's GPU load: a Direct3D 11 compute shader whose output is hashed, the same work every pass, so a wrong answer
/// shows as a different hash and a driver reset as a lost device. Each pass is a few milliseconds of shader work, far under
/// the driver's 2 s timeout. Direct3D's slots (d3d11.h and dxgi.h, SDK 10.0.26100, counted from the vtable structs by script):
/// ID3D11Device CreateBuffer 3, CreateUnorderedAccessView 8, CreateComputeShader 18, GetDeviceRemovedReason 39;
/// ID3D11DeviceContext Map 14, Unmap 15, Dispatch 41, CopyResource 47, CSSetUnorderedAccessViews 68, CSSetShader 69;
/// ID3DBlob GetBufferPointer 3, GetBufferSize 4; IDXGIFactory1 EnumAdapters1 12; IDXGIAdapter1 GetDesc1 10 (VendorId at 256).
/// WARP (software, used by the tests) runs the same calls without touching a GPU.
/// </summary>
internal sealed unsafe class GpuLoad : IDisposable
{
    private const uint SdkVersion = 7, GroupSize = 64;
    private const int DriverUnknown = 0, DriverWarp = 5;
    private const int DeviceRemoved = unchecked((int)0x887A0005), DeviceHung = unchecked((int)0x887A0006), DeviceReset = unchecked((int)0x887A0007);
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private readonly nint _device, _context, _buffer, _staging, _view, _shader;
    private readonly uint _groups;
    private readonly int _bytes;

    /// <summary>A lost device or wrong card: the test's own words.</summary>
    internal sealed class Lost(string message) : Exception(message);

    /// <param name="vendor">The PCI vendor whose card to load (0x10DE, 0x1002); 0 for WARP.</param>
    internal GpuLoad(uint vendor, uint groups, uint iterations)
    {
        _groups = groups;
        var elements = groups * GroupSize;
        _bytes = (int)(elements * 4);
        var code = Compile(iterations);
        try
        {
            var adapter = vendor == 0 ? 0 : FindAdapter(vendor);
            var create = (delegate* unmanaged[Stdcall]<nint, int, nint, uint, nint, uint, uint, nint*, int*, nint*, int>)Export("d3d11.dll", "D3D11CreateDevice");
            nint device, context;
            int level;
            var made = create(adapter, vendor == 0 ? DriverWarp : DriverUnknown, 0, 0, 0, 0, SdkVersion, &device, &level, &context);
            if (adapter != 0) Release(adapter);
            if (made < 0) throw new Lost("Direct3D didn't start on this card.");
            _device = device;
            _context = context;

            var desc = stackalloc uint[6] { (uint)_bytes, 0, 0x80, 0, 0x40, 4 }; // default usage, unordered access, structured, 4 bytes each
            nint buffer, staging, view, shader;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Slot(_device, 3))(_device, desc, 0, &buffer));
            _buffer = buffer;
            var read = stackalloc uint[6] { (uint)_bytes, 3, 0, 0x20000, 0, 0 }; // staging, CPU read
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Slot(_device, 3))(_device, read, 0, &staging));
            _staging = staging;
            var uav = stackalloc uint[5] { 0, 1, 0, elements, 0 }; // DXGI_FORMAT_UNKNOWN, a buffer from element 0
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, nint*, int>)Slot(_device, 8))(_device, _buffer, uav, &view));
            _view = view;
            var pointer = ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(code, 3))(code);
            var size = ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(code, 4))(code);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Slot(_device, 18))(_device, pointer, size, 0, &shader));
            _shader = shader;

            var views = stackalloc nint[1] { _view };
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(_context, 68))(_context, 0, 1, views, null);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(_context, 69))(_context, _shader, 0, 0);
        }
        finally { Release(code); }
    }

    /// <summary>The hash of one pass's output; throws <see cref="Lost"/> when the driver reset or the device went.</summary>
    internal ulong Pass()
    {
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Slot(_context, 41))(_context, _groups, 1, 1);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(_context, 47))(_context, _staging, _buffer);
        var mapped = stackalloc nint[2];
        var answer = ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, nint*, int>)Slot(_context, 14))(_context, _staging, 0, 1, 0, mapped);
        Check(answer);
        var hash = Hash(new ReadOnlySpan<uint>((void*)mapped[0], _bytes / 4));
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 15))(_context, _staging, 0);
        var removed = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_device, 39))(_device);
        if (removed < 0) throw new Lost("The graphics driver reset.");
        return hash;
    }

    private static ulong Hash(ReadOnlySpan<uint> words)
    {
        var hash = 14695981039346656037UL;
        foreach (var word in words) hash = (hash ^ word) * 1099511628211UL;
        return hash;
    }

    private static nint Compile(uint iterations)
    {
        var source = System.Text.Encoding.ASCII.GetBytes($$"""
            RWStructuredBuffer<uint> o : register(u0);
            [numthreads({{GroupSize}},1,1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                float a = float(id.x % 1024) * 0.001f + 0.5f;
                float b = 1.0f + float(id.x % 97) * 0.01f;
                uint h = id.x * 2654435761u;
                [loop] for (uint i = 0; i < {{iterations}}u; i++)
                {
                    a = sqrt(a * a + b) * sin(a) + cos(b);
                    b = frac(a * 0.37f) + 0.5f;
                    h = (h ^ asuint(a)) * 16777619u;
                }
                o[id.x] = h;
            }
            """);
        var compile = (delegate* unmanaged[Stdcall]<byte*, nuint, nint, nint, nint, byte*, byte*, uint, uint, nint*, nint*, int>)Export("d3dcompiler_47.dll", "D3DCompile");
        nint code = 0, errors = 0;
        fixed (byte* text = source, entry = "main\0"u8, target = "cs_5_0\0"u8)
        {
            var answer = compile(text, (nuint)source.Length, 0, 0, 0, entry, target, 0, 0, &code, &errors);
            if (errors != 0) Release(errors);
            if (answer < 0 || code == 0) throw new Lost("The test's shader didn't compile.");
        }
        return code;
    }

    private static nint FindAdapter(uint vendor)
    {
        var create = (delegate* unmanaged[Stdcall]<Guid*, nint*, int>)Export("dxgi.dll", "CreateDXGIFactory1");
        var id = FactoryId;
        nint factory;
        if (create(&id, &factory) < 0) throw new Lost("Direct3D didn't start.");
        try
        {
            var desc = stackalloc byte[320];
            for (uint index = 0; ; index++)
            {
                nint adapter;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, index, &adapter) < 0)
                    throw new Lost("Windows doesn't list this card for Direct3D.");
                if (((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(adapter, 10))(adapter, desc) >= 0 && *(uint*)(desc + 256) == vendor) return adapter;
                Release(adapter);
            }
        }
        finally { Release(factory); }
    }

    private static nint Export(string dll, string name) =>
        NativeLibrary.GetExport(NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, dll)), name);

    private static void Check(int answer)
    {
        if (answer is DeviceRemoved or DeviceHung or DeviceReset) throw new Lost("The graphics driver reset.");
        if (answer < 0) throw new Lost($"Direct3D answered 0x{answer:X8}.");
    }

    private static nint Slot(nint self, int index) => (*(nint**)self)[index];

    private static void Release(nint self)
    {
        if (self != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(self, 2))(self);
    }

    public void Dispose()
    {
        foreach (var item in new[] { _shader, _view, _staging, _buffer, _context, _device }) Release(item);
    }
}

/// <summary>Auto tune's CPU load, one pass on the calling thread: an FFT round trip in plain double precision and a walk of memory, hashed.</summary>
internal sealed class CpuLoad
{
    private readonly double[] _re, _im, _cos, _sin;
    private readonly ulong[] _memory;

    internal CpuLoad(int fftBits, int memoryWords)
    {
        var size = 1 << fftBits;
        (_re, _im, _cos, _sin) = (new double[size], new double[size], new double[size / 2], new double[size / 2]);
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / size);
            _sin[i] = Math.Sin(2 * Math.PI * i / size);
        }
        _memory = new ulong[memoryWords];
    }

    internal ulong Pass()
    {
        var seed = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < _re.Length; i++)
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            _re[i] = (seed & 0xFFFFF) / 1048576.0 - 0.5;
            _im[i] = 0;
        }
        Transform(forward: true);
        Transform(forward: false);
        var hash = 14695981039346656037UL;
        foreach (var value in _re) hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(value)) * 1099511628211UL;
        foreach (var value in _im) hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(value)) * 1099511628211UL;
        for (var i = 0; i < _memory.Length; i++)
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            _memory[i] = seed;
        }
        for (var i = 0; i < _memory.Length; i++) hash = (hash ^ _memory[(int)(_memory[i] % (ulong)_memory.Length)]) * 1099511628211UL;
        return hash;
    }

    private void Transform(bool forward)
    {
        var n = _re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (_re[i], _re[j]) = (_re[j], _re[i]); (_im[i], _im[j]) = (_im[j], _im[i]); }
        }
        var sign = forward ? -1.0 : 1.0;
        for (var length = 2; length <= n; length <<= 1)
        {
            var half = length / 2;
            var stride = n / length;
            for (var start = 0; start < n; start += length)
                for (var k = 0; k < half; k++)
                {
                    var (wr, wi) = (_cos[k * stride], sign * _sin[k * stride]);
                    var (xr, xi) = (_re[start + k + half], _im[start + k + half]);
                    var (tr, ti) = (xr * wr - xi * wi, xr * wi + xi * wr);
                    _re[start + k + half] = _re[start + k] - tr;
                    _im[start + k + half] = _im[start + k] - ti;
                    _re[start + k] += tr;
                    _im[start + k] += ti;
                }
        }
        if (forward) return;
        for (var i = 0; i < n; i++) { _re[i] /= n; _im[i] /= n; }
    }
}

/// <summary>The GPU's test: passes of <see cref="GpuLoad"/>, each checked against the first (taken at stock), a second apart at most between checks.</summary>
internal sealed class TuneGpuTest(uint vendor, Func<GpuSensors?> sensors) : ITuneTest
{
    // About a million invocations of 256 loops a pass.
    private const uint Groups = 16384, Iterations = 256;
    private GpuLoad? _load;
    private ulong? _answer;

    public double? TemperatureC() => sensors() is { } read ? read.Limit is { OnHotspot: true } ? read.HotspotC ?? read.TemperatureC : read.TemperatureC : null;

    public TuneRun Run(TimeSpan length, Func<string?> check)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var passes = 0L;
        int? peak = null;
        var nextCheck = TimeSpan.Zero;
        try
        {
            _load ??= New();
            while (clock.Elapsed < length)
            {
                var hash = _load.Pass();
                _answer ??= hash;
                if (hash != _answer) return new TuneRun(passes / clock.Elapsed.TotalSeconds, peak, "A wrong answer from the card.");
                passes++;
                if (clock.Elapsed < nextCheck) continue;
                nextCheck = clock.Elapsed + TimeSpan.FromSeconds(1);
                if (sensors()?.ClockMhz is { } mhz) peak = Math.Max(peak ?? mhz, mhz);
                if (check() is { } reason) return new TuneRun(passes / clock.Elapsed.TotalSeconds, peak, reason);
            }
            return new TuneRun(passes / clock.Elapsed.TotalSeconds, peak, null);
        }
        catch (GpuLoad.Lost lost)
        {
            _load?.Dispose();
            _load = null;
            return new TuneRun(passes / Math.Max(clock.Elapsed.TotalSeconds, 0.001), peak, lost.Message);
        }
    }

    private GpuLoad New()
    {
        // Right after a driver reset Direct3D can answer no for a moment.
        for (var attempt = 0; ; attempt++)
        {
            try { return new GpuLoad(vendor, Groups, Iterations); }
            catch (GpuLoad.Lost) when (attempt < 4) { Thread.Sleep(3000); }
        }
    }
}

/// <summary>
/// The CPU's test: one thread pinned to each physical core in turn, length split evenly, each pass checked against the first
/// (at stock); nine seconds of load, then a second of small bursts with idle between, since Curve Optimizer fails most at
/// one core's highest boost and in light load (docs/architecture/auto-tune.md).
/// </summary>
internal sealed class TuneCpuTest(IReadOnlyList<int> cpus, Func<double?> temperature) : ITuneTest
{
    private readonly CpuLoad _heavy = new(16, 1 << 22), _light = new(10, 1 << 8);
    private ulong? _heavyAnswer, _lightAnswer;

    public double? TemperatureC() => temperature();

    public TuneRun Run(TimeSpan length, Func<string?> check)
    {
        TuneRun? answer = null;
        var thread = new Thread(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var passes = 0L;
            var each = length / Math.Max(cpus.Count, 1);
            foreach (var cpu in cpus)
            {
                _ = SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << cpu);
                var stop = clock.Elapsed + each;
                var nextCheck = TimeSpan.Zero;
                while (clock.Elapsed < stop)
                {
                    var load = clock.Elapsed + TimeSpan.FromSeconds(9);
                    while (clock.Elapsed < load && clock.Elapsed < stop)
                    {
                        var hash = _heavy.Pass();
                        _heavyAnswer ??= hash;
                        if (hash != _heavyAnswer) { answer = new TuneRun(passes / clock.Elapsed.TotalSeconds, null, $"A wrong answer on core {cpu}."); return; }
                        passes++;
                        if (clock.Elapsed < nextCheck) continue;
                        nextCheck = clock.Elapsed + TimeSpan.FromSeconds(1);
                        if (check() is { } reason) { answer = new TuneRun(passes / clock.Elapsed.TotalSeconds, null, reason); return; }
                    }
                    for (var burst = 0; burst < 20 && clock.Elapsed < stop; burst++)
                    {
                        var hash = _light.Pass();
                        _lightAnswer ??= hash;
                        if (hash != _lightAnswer) { answer = new TuneRun(passes / clock.Elapsed.TotalSeconds, null, $"A wrong answer on core {cpu}."); return; }
                        Thread.Sleep(50);
                    }
                }
            }
            answer = new TuneRun(passes / clock.Elapsed.TotalSeconds, null, null);
        }) { IsBackground = true, Name = "Exo auto tune load" };
        thread.Start();
        thread.Join();
        return answer ?? new TuneRun(0, null, "The test stopped.");
    }

    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
}

/// <summary>
/// Windows' own record of hardware errors as they happen: an event log subscription, so the count costs nothing to read
/// however large the log is. WHEA-Logger is the CPU's machine checks and corrected errors (18, 19 and the rest); a GPU
/// also counts driver resets: Display 4101 (a timeout recovered), nvlddmkm 13, 14 and 153.
/// </summary>
internal static unsafe class TuneEvents
{
    private static int _count;
    private static nint _subscription;

    internal static int Count => Volatile.Read(ref _count);

    internal static string Query(bool gpu) => gpu
        ? "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] or (Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (EventID=13 or EventID=14 or EventID=153))]]"
        : "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]";

    /// <summary>Starts counting what's logged from now on. False when Windows won't: the worker then refuses to test blind.</summary>
    internal static bool Start(bool gpu)
    {
        if (_subscription != 0) return true;
        _subscription = EvtSubscribe(0, 0, "System", Query(gpu), 0, 0, &Delivered, 1 /* EvtSubscribeToFutureEvents */);
        return _subscription != 0;
    }

    /// <summary>Whether Windows' event log accepts a query: read-only, for the tests.</summary>
    internal static bool Parses(string query)
    {
        var handle = EvtQuery(0, "System", query, 0x101 /* channel path, oldest first */);
        if (handle == 0) return false;
        EvtClose(handle);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint Delivered(int action, nint context, nint record)
    {
        if (action == 1) Interlocked.Increment(ref _count);
        return 0;
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtSubscribe(nint session, nint signal, string path, string query, nint bookmark, nint context,
        delegate* unmanaged[Stdcall]<int, nint, nint, uint> callback, int flags);

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtQuery(nint session, string path, string query, int flags);

    [DllImport("wevtapi.dll")] private static extern bool EvtClose(nint handle);
}
