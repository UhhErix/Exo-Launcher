using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.GameCompression;

namespace ExoLauncher.Services;

/// <summary>
/// Windows' side of <see cref="GameCompression"/>: WOF's file compression (wofutil.dll's WofSetFileDataLocation to compress,
/// FSCTL_DELETE_EXTERNAL_BACKING to put a file back, WofIsExternalFile to read it), a file's size on disk
/// (GetCompressedFileSizeW), the volume's cluster, and the sampler: the Compression API's XPRESS Huffman codec (cabinet.dll),
/// the codec WOF's XPRESS algorithms use, run on the very chunks WOF would make, so an estimate is what WOF would really
/// save (CompactGUI estimates with LZ4 instead). Nothing here opens a file for writing; a file another program holds is in use.
/// </summary>
internal static partial class GameCompressionNative
{
    private const uint WofProviderFile = 2;
    private const uint FsctlDeleteExternalBacking = 0x00090314;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCompressionInfo
    {
        public uint Algorithm;
        public uint Flags;
    }

    /// <summary>What happened to one file.</summary>
    internal enum Outcome { Done, NotWorth, InUse, DiskFull, Unsupported, Failed }

    private const int NotBeneficial = unchecked((int)0x80070158);   // ERROR_COMPRESSION_NOT_BENEFICIAL
    private const int DiskFull = unchecked((int)0x80070070);        // ERROR_DISK_FULL
    private const int HandleDiskFull = unchecked((int)0x80070027);  // ERROR_HANDLE_DISK_FULL
    private const int NotSupported = unchecked((int)0x80070032);    // ERROR_NOT_SUPPORTED
    private const int InvalidFunction = unchecked((int)0x80070001); // ERROR_INVALID_FUNCTION: the volume has no WOF
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    internal static Outcome FromHResult(int hr) => hr switch
    {
        >= 0 => Outcome.Done,
        NotBeneficial => Outcome.NotWorth,
        DiskFull or HandleDiskFull => Outcome.DiskFull,
        NotSupported or InvalidFunction => Outcome.Unsupported,
        SharingViolation or LockViolation => Outcome.InUse,
        _ => Outcome.Failed,
    };

    /// <summary>An exception opening a file: in use, the drive full, or failed (never done).</summary>
    private static Outcome FromIo(IOException ex) => FromHResult(ex.HResult) is var outcome && outcome != Outcome.Done ? outcome : Outcome.Failed;

    /// <summary>Compresses one file with <paramref name="algorithm"/>. A file opened for writing elsewhere is in use.</summary>
    internal static Outcome Compress(string path, Algorithm algorithm)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var info = new FileCompressionInfo { Algorithm = (uint)algorithm, Flags = 0 };
            return FromHResult(WofSetFileDataLocation(handle, WofProviderFile, ref info, (uint)Marshal.SizeOf<FileCompressionInfo>()));
        }
        catch (IOException ex) { return FromIo(ex); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return Outcome.Failed; }
    }

    /// <summary>Puts one file back as Windows keeps it normally. Not compressed is done too.</summary>
    internal static Outcome Uncompress(string path)
    {
        if (Current(path) is null) return Outcome.Done;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (DeviceIoControl(handle, FsctlDeleteExternalBacking, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) return Outcome.Done;
            return FromHResult(Marshal.GetHRForLastWin32Error());
        }
        catch (IOException ex) { return FromIo(ex); }
        catch (UnauthorizedAccessException) { return Outcome.Failed; }
    }

    /// <summary>The WOF algorithm a file is kept in, or null when it isn't (or is another provider's, such as a Windows image's).</summary>
    internal static Algorithm? Current(string path)
    {
        try
        {
            var length = (uint)Marshal.SizeOf<FileCompressionInfo>();
            var hr = WofIsExternalFile(path, out var external, out var provider, out var info, ref length);
            if (hr < 0 || external == 0 || provider != WofProviderFile || info.Algorithm > 3) return null;
            return (Algorithm)info.Algorithm;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>A file's bytes on disk (compressed or sparse), or -1 when Windows can't say.</summary>
    internal static long OnDisk(string path)
    {
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) return -1;
        return ((long)high << 32) | low;
    }

    /// <summary>The volume's cluster in bytes (4 KB when Windows can't say).</summary>
    internal static long Cluster(string root)
    {
        var volume = Path.GetPathRoot(root);
        return volume is not null && GetDiskFreeSpace(volume, out var sectors, out var bytes, out _, out _) && sectors * bytes > 0 ? (long)sectors * bytes : 4096;
    }

    /// <summary>A spinning disk under this folder (it pays to seek): one thread, and LZX is worth its CPU.</summary>
    internal static bool HardDrive(string root)
    {
        try
        {
            if (Path.GetPathRoot(root) is not { Length: >= 2 } volume || PcAbout.DiskNumber(volume[..2]) is not { } number) return false;
            using var disk = PcAbout.Open($@"\\.\PhysicalDrive{number}");
            return !disk.IsInvalid && PcAbout.Seeks(disk) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ------------------------------------------------------------------ the sampler

    /// <summary>
    /// One thread's XPRESS Huffman compressor in raw mode: each chunk compressed on its own, as WOF does. Null from
    /// <see cref="Open"/> when the codec isn't there; the estimate then keeps the local prior.
    /// </summary>
    internal sealed class Sampler : IDisposable
    {
        private const uint XpressHuff = 4;
        private const uint Raw = 1u << 29;
        private IntPtr _handle;
        private readonly byte[] _out = new byte[2 * 32768 + 4096];

        private Sampler(IntPtr handle) => _handle = handle;

        internal static Sampler? Open()
        {
            try { return CreateCompressor(XpressHuff | Raw, IntPtr.Zero, out var handle) && handle != IntPtr.Zero ? new Sampler(handle) : null; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }

        /// <summary>What one chunk costs once compressed (<see cref="ChunkCost"/>).</summary>
        internal unsafe long Cost(ReadOnlySpan<byte> chunk)
        {
            nuint size;
            bool ok;
            fixed (byte* input = chunk)
            fixed (byte* output = _out)
                ok = Compress(_handle, input, (nuint)chunk.Length, output, (nuint)_out.Length, out size);
            return ChunkCost(chunk.Length, ok ? (long)size : null);
        }

        /// <summary>
        /// Up to <paramref name="maxChunks"/> chunks of <paramref name="chunk"/> bytes read evenly through the file, each
        /// costed. A file that can't be read gives nothing (Raw 0).
        /// </summary>
        internal Sample Measure(string path, long length, int chunk, int maxChunks)
        {
            var buffer = new byte[chunk];
            long raw = 0, packed = 0;
            try
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var chunks = Math.Max(1, length / chunk);
                var take = (int)Math.Min(chunks, maxChunks);
                for (var i = 0; i < take; i++)
                {
                    var at = chunks <= maxChunks ? i * (long)chunk : i * (chunks / take) * (long)chunk;
                    var read = RandomAccess.Read(handle, buffer, at);
                    if (read <= 0) break;
                    raw += read;
                    packed += Cost(buffer.AsSpan(0, read));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
            return new Sample(raw, packed);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CloseCompressor(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ imports

    [DllImport("wofutil.dll")]
    private static extern int WofSetFileDataLocation(SafeFileHandle file, uint provider, ref FileCompressionInfo info, uint length);

    [DllImport("wofutil.dll", CharSet = CharSet.Unicode)]
    private static extern int WofIsExternalFile(string path, out int isExternal, out uint provider, out FileCompressionInfo info, ref uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetCompressedFileSizeW")]
    private static extern uint GetCompressedFileSize(string path, out uint high);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceW")]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateCompressor(uint algorithm, IntPtr allocation, out IntPtr handle);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern unsafe bool Compress(IntPtr handle, byte* input, nuint inputSize, byte* output, nuint outputSize, out nuint compressedSize);

    [DllImport("cabinet.dll")]
    private static extern bool CloseCompressor(IntPtr handle);
}
