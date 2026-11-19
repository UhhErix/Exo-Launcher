using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

/// <summary>
/// A Ryzen desktop CPU's all-core Curve Optimizer, through PawnIO (pawnio.eu), which the user installs, and the signed
/// RyzenSMU module Exo ships under the LGPL (Assets/pawnio). Only IOCTLs, the interface PawnIO's license exception covers.
/// Depth d is margin -d on all cores, set with MP1 message 0x36 on Zen 3, 4 and 5; read back on Zen 4 and 5 with RSMU 0xD5.
/// The value is gone at reboot and after sleep. Addresses and messages: docs/architecture/auto-tune.md. Only the worker
/// opens the driver, which admits administrators alone. Nothing here has been run on a CPU.
/// </summary>
internal sealed class TuneRyzen : ITunePart, IDisposable
{
    internal enum Zen { Three = 3, Four = 4, Five = 5 }

    private const uint Mp1Message = 0x3B10530, Mp1Response = 0x3B1057C, Mp1Argument = 0x3B109C4;
    private const uint SetAllMargin = 0x36, ReadMargin = 0xD5, ResponseOk = 1, TemperatureRegister = 0x59800;
    private const int MailboxArguments = 6;
    private const int Furthest = 30, Step = 5, StopC = 85;
    private static readonly Version LeastDriver = new(2, 1, 0);

    private readonly Zen _zen;
    private readonly PawnIo _driver;

    public string Id => "ryzen";
    public TuneLimits Limits { get; }

    /// <summary>Which Curve Optimizer a CPU has, from Windows' processor identifier ("AMD64 Family 25 Model 33 Stepping 2"); null for what isn't supported.</summary>
    internal static Zen? Generation(string? identifier, string? name)
    {
        if (identifier is null || !identifier.StartsWith("AMD64 Family ", StringComparison.Ordinal)) return null;
        var parts = identifier.Split(' ');
        if (parts.Length < 5 || !int.TryParse(parts[2], out var family) || !int.TryParse(parts[4], out var model)) return null;
        var isX3d = name?.Contains("X3D", StringComparison.OrdinalIgnoreCase) == true;
        return (family, model) switch
        {
            // Vermeer. AMD locks Curve Optimizer on the 5000 X3D parts.
            (25, 0x21) => isX3d ? null : Zen.Three,
            (25, 0x61) => Zen.Four, // Raphael
            (26, 0x44) => Zen.Five, // Granite Ridge
            _ => null,
        };
    }

    /// <summary>The 16-bit two's complement the mailbox takes; refuses anything but a negative or zero margin.</summary>
    internal static uint MarginArgument(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        return (ushort)(short)-depth;
    }

    /// <summary>Tctl in °C from the temperature register (k10temp: the top 11 bits in eighths, 49 less in the wider range).</summary>
    internal static double Temperature(uint register) =>
        (register >> 21) * 0.125 - ((register & (1u << 19)) != 0 || (register & (3u << 16)) == 3u << 16 ? 49 : 0);

    internal static TuneLimits For(int cores) => new(Furthest, Step, Step, StopC, 60 * cores, 360 * cores, 30 * cores);

    /// <summary>The installed PawnIO's version; null when it isn't installed.</summary>
    internal static Version? DriverVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key?.GetValue("DisplayVersion") is string text && Version.TryParse(text, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    internal static bool DriverReady() => DriverVersion() is { } version && version >= LeastDriver;

    internal TuneRyzen(Zen zen, int cores, string module)
    {
        _zen = zen;
        Limits = For(cores);
        _driver = PawnIo.Open(module);
    }

    public void Settle(TuneRun stock) { }

    public void Apply(int depth)
    {
        Limits.Check(depth);
        SetMargin(depth);
    }

    public void Stock() => SetMargin(0);

    /// <summary>The deepest margin the SMU reports across the first CCD's cores (a fused-off core may answer 0, or not at all); null on Zen 3, whose read isn't certain.</summary>
    public int? Read()
    {
        if (_zen == Zen.Three) return null;
        int? deepest = null;
        using var bus = PawnIo.PciBus();
        for (uint core = 0; core < 8; core++)
        {
            var arguments = new long[MailboxArguments + 1];
            arguments[0] = ReadMargin;
            arguments[1] = core << 20;
            if (_driver.Try("ioctl_send_smu_command", arguments, MailboxArguments) is { } answer)
                deepest = Math.Max(deepest ?? 0, -(short)(ushort)answer[0]);
        }
        return deepest;
    }

    public int[]? SignIn(int depth) => [Limits.Check(depth)];

    internal double? TemperatureC()
    {
        using var bus = PawnIo.PciBus();
        return _driver.Try("ioctl_read_smu_register", [TemperatureRegister], 1) is { } value ? Temperature((uint)value[0]) : null;
    }

    private void SetMargin(int depth)
    {
        using var bus = PawnIo.PciBus();
        // The mailbox: wait for it to be free, clear its response, the arguments, the message, wait for the answer.
        Wait();
        Write(Mp1Response, 0);
        Write(Mp1Argument, MarginArgument(depth));
        for (uint i = 1; i < MailboxArguments; i++) Write(Mp1Argument + i * 4, 0);
        Write(Mp1Message, SetAllMargin);
        var answer = Wait();
        if (answer != ResponseOk)
        {
            AppLog.Warn($"Auto tune: the SMU answered {answer}.");
            throw new TuneDriverException("Your processor refused that value, so you're back at stock.");
        }
    }

    private uint Wait()
    {
        for (var tries = 0; tries < 500; tries++)
        {
            var value = _driver.Try("ioctl_read_smu_register", [Mp1Response], 1)?[0] ?? throw new TuneDriverException("PawnIO didn't answer.");
            if (value != 0) return (uint)value;
            Thread.Sleep(2);
        }
        throw new TuneDriverException("Your processor didn't answer, so Exo stopped.");
    }

    private void Write(uint register, uint value)
    {
        if (_driver.Try("ioctl_write_smu_register", [register, value], 0) is null) throw new TuneDriverException("PawnIO didn't answer.");
    }

    public void Dispose() => _driver.Dispose();
}

/// <summary>PawnIO's device: a handle with one signed module loaded, whose named functions take and give 64-bit values.</summary>
internal sealed unsafe class PawnIo : IDisposable
{
    private const uint DeviceType = 41394u << 16, LoadBinary = DeviceType | (0x821u << 2), Execute = DeviceType | (0x841u << 2);
    private const int NameLength = 32;
    private readonly SafeFileHandle _handle;

    private PawnIo(SafeFileHandle handle) => _handle = handle;

    internal static PawnIo Open(string module)
    {
        var handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid) throw new TuneDriverException("PawnIO isn't running. Restart your PC, or install it again from pawnio.eu.");
        var bytes = File.ReadAllBytes(module);
        fixed (byte* input = bytes)
            if (!DeviceIoControl(handle, LoadBinary, input, (uint)bytes.Length, null, 0, out _, 0))
            {
                handle.Dispose();
                throw new TuneDriverException("PawnIO wouldn't load Exo's module. Update PawnIO from pawnio.eu.");
            }
        return new PawnIo(handle);
    }

    /// <summary>The answer, or null when the function said no.</summary>
    internal long[]? Try(string function, long[] input, int outputs)
    {
        var buffer = new byte[NameLength + input.Length * 8];
        System.Text.Encoding.ASCII.GetBytes(function, buffer);
        Buffer.BlockCopy(input, 0, buffer, NameLength, input.Length * 8);
        var output = new long[Math.Max(outputs, 1)];
        fixed (byte* sent = buffer)
        fixed (long* got = output)
            return DeviceIoControl(_handle, Execute, sent, (uint)buffer.Length, got, (uint)(outputs * 8), out _, 0) ? output : null;
    }

    /// <summary>The lock every tool shares for this bus (RyzenSMU.p: callers "should acquire" Access_PCI).</summary>
    internal static IDisposable PciBus() => new BusLock();

    private sealed class BusLock : IDisposable
    {
        private readonly Mutex _mutex = new(false, @"Global\Access_PCI");

        internal BusLock()
        {
            try { if (!_mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new TuneDriverException("Another program holds your processor's mailbox, so Exo stopped."); }
            catch (AbandonedMutexException) { /* Its holder went away; the lock is ours. */ }
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);
}
