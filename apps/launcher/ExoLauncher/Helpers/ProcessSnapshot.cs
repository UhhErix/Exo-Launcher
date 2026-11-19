using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// One process as the kernel's own list has it: what Task Manager's Details tab reads, with no handle opened. Times are
/// 100 ns units (CreateTime a FILETIME, CPU the user and kernel time together), memory and I/O in bytes. The private working
/// set is what Task Manager calls Memory.
/// </summary>
internal readonly record struct ProcessRow(int Pid, int Parent, string Name, int Threads, int Handles, int Session, long CreateTime,
    long CpuTime, long PrivateWorkingSet, long PrivateBytes, long ReadBytes, long WriteBytes, int BasePriority, long HardFaults);

/// <summary>
/// Every process in one NtQuerySystemInformation(SystemProcessInformation) call, the read <see cref="BackgroundTrim.ProcessTable"/>
/// makes for names, here with the counters too (Autopilot's telemetry and PC › Task Manager). The kernel's list is read in place;
/// nothing is opened, so a protected process (anti-cheat, a game under it) is read the way Task Manager reads it, and never touched.
/// Ways it could be wrong, each with a test: a field read at the wrong offset (the layout is SYSTEM_PROCESS_INFORMATION for x64,
/// phnt's ntexapi.h); a name that runs past its entry; a list whose next offset loops or points outside the buffer.
/// </summary>
internal static class ProcessSnapshot
{
    // SYSTEM_PROCESS_INFORMATION (x64).
    internal const int NextAt = 0x00, ThreadsAt = 0x04, PrivateWorkingSetAt = 0x08, HardFaultsAt = 0x10, CreateAt = 0x20, UserAt = 0x28,
        KernelAt = 0x30, NameLengthAt = 0x38, NamePointerAt = 0x40, BasePriorityAt = 0x48, PidAt = 0x50, ParentAt = 0x58, HandlesAt = 0x60,
        SessionAt = 0x64, PrivateBytesAt = 0xC8, ReadBytesAt = 0xE8, WriteBytesAt = 0xF0, EntrySize = 0x100;

    private const int SystemProcessInformation = 5, StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static int _bytes = 512 * 1024;

    /// <summary>The list now; empty when Windows wouldn't give it.</summary>
    internal static unsafe List<ProcessRow> Read()
    {
        var size = Volatile.Read(ref _bytes);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                fixed (byte* start = buffer)
                {
                    var status = NtQuerySystemInformation(SystemProcessInformation, start, buffer.Length, out var needed);
                    if (status == StatusInfoLengthMismatch)
                    {
                        size = Math.Max(buffer.Length, needed) + 64 * 1024;
                        continue;
                    }
                    if (status < 0 || needed < EntrySize) return [];
                    Volatile.Write(ref _bytes, Math.Max(size, needed + 64 * 1024));
                    return Parse(buffer.AsSpan(0, Math.Min(buffer.Length, needed)), (nint)start);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        return [];
    }

    /// <summary>
    /// The rows in <paramref name="data"/>, a SystemProcessInformation buffer that sat at <paramref name="baseAddress"/> (each
    /// name is a pointer into the same buffer). A name pointing outside it reads as no name; a next offset that does not move
    /// forward inside the buffer ends the list.
    /// </summary>
    internal static List<ProcessRow> Parse(ReadOnlySpan<byte> data, nint baseAddress)
    {
        var rows = new List<ProcessRow>(Math.Min(1024, data.Length / EntrySize + 1));
        for (var at = 0; at + EntrySize <= data.Length;)
        {
            var entry = data[at..];
            var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(entry[NameLengthAt..]);
            var pointer = BinaryPrimitives.ReadUInt64LittleEndian(entry[NamePointerAt..]);
            var name = "";
            if (pointer != 0 && nameBytes > 0 && pointer >= (ulong)baseAddress)
            {
                var offset = pointer - (ulong)baseAddress;
                if (offset <= (ulong)(data.Length - nameBytes))
                    name = new string(MemoryMarshal.Cast<byte, char>(data.Slice((int)offset, nameBytes & ~1)));
            }
            rows.Add(new ProcessRow(
                (int)BinaryPrimitives.ReadInt64LittleEndian(entry[PidAt..]),
                (int)BinaryPrimitives.ReadInt64LittleEndian(entry[ParentAt..]),
                name,
                BinaryPrimitives.ReadInt32LittleEndian(entry[ThreadsAt..]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[HandlesAt..]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[SessionAt..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[CreateAt..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[UserAt..]) + BinaryPrimitives.ReadInt64LittleEndian(entry[KernelAt..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[PrivateWorkingSetAt..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[PrivateBytesAt..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[ReadBytesAt..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[WriteBytesAt..]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[BasePriorityAt..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[HardFaultsAt..])));
            var next = BinaryPrimitives.ReadInt32LittleEndian(entry[NextAt..]);
            if (next <= 0 || next > data.Length - at) break;
            at += next;
        }
        return rows;
    }

    [DllImport("ntdll.dll")] private static extern unsafe int NtQuerySystemInformation(int infoClass, byte* info, int length, out int returned);
}
