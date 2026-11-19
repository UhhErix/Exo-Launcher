using System.Text;

namespace ExoLauncher.Services;

/// <summary>
/// What Exo may say to a DualSense or DualSense Edge, and what it means when it answers. Every feature report Exo sends
/// or asks for passes <see cref="Allowed"/> first: the firmware report, the test channel's reads (serial, stick
/// fine-tune, NVS status), the stick calibration steps, the fine-tune write and the NVS unlock and lock that keep a
/// DualSense's calibration, and reading an Edge's profile slots (0x70-0x7B; never writing them). Anything else is
/// refused, by name the reports that power the controller off, change its pairing key or touch its firmware (0x08, 0x0A,
/// 0xA0, 0xF0-0xF5), the test channel's reset, an Edge's profile writes (0x60-0x6B) and its stick modules' unlock, store
/// and lock (test channel device 0x15: the modules are write-protected until the controller is opened and modified, so
/// a calibration can't be made to last on an Edge from here; see <see cref="Keep"/>).
///
/// Sequences follow dualshock-tools (the_al, MIT): the calibration channel (0x82, answered on 0x83 as 83 01 target state)
/// and the test channel (0x80, answered on 0x81 as 81 device action 02, its data from byte 4). The ways this could go
/// wrong, covered by ControllerLabTests:
/// - a report outside the list sent because a caller built it by hand;
/// - an Edge's second "end" answer (0x83010103, or 0x83010312 when its stick modules keep their own calibration)
///   taken for a failure, or a wrong answer taken for success;
/// - fine-tune values read from the wrong bytes (12 little-endian u16 from byte 4), or written in the wrong order.
/// </summary>
internal static partial class ControllerLab
{
    /// <summary>The test channel's commands Exo uses: device, action (and the NVS unlock's fixed bytes).</summary>
    internal static readonly byte[] ReadSerial = [0x01, 0x13], NvStatus = [0x03, 0x03], NvLock = [0x03, 0x01],
        NvUnlock = [0x03, 0x02, 0x65, 0x32, 0x40, 0x0C], FinetuneWrite = [0x0C, 0x01];

    /// <summary>The fine-tune read: the DualSense's 0C 02, the Edge's 0C 04.</summary>
    internal static byte[] FinetuneRead(bool edge) => [0x0C, edge ? (byte)0x04 : (byte)0x02];

    /// <summary>The calibration channel's steps: begin, sample and end, for the centers (1) or the range (2).</summary>
    internal static readonly byte[] CenterBegin = [1, 1, 1], CenterSample = [3, 1, 1], CenterEnd = [2, 1, 1], RangeBegin = [1, 1, 2], RangeEnd = [2, 1, 2];

    /// <summary>
    /// Whether Exo may send (<paramref name="set"/>) or ask for this feature report. <paramref name="payload"/> is the
    /// whole command after the ID (the report's zero padding is added when it's sent), so each must match exactly.
    /// </summary>
    internal static bool Allowed(byte id, ReadOnlySpan<byte> payload, bool set, bool edge)
    {
        if (!set) return id is 0x20 or 0x81 or 0x83 || edge && id is >= FirstSlotReport and <= LastSlotReport;
        return id switch
        {
            0x80 => payload.SequenceEqual(ReadSerial) || payload.SequenceEqual(NvStatus) || payload.SequenceEqual(NvLock) || payload.SequenceEqual(NvUnlock)
                || payload.SequenceEqual(FinetuneRead(edge)) || payload.Length == 2 + 24 && payload.StartsWith(FinetuneWrite),
            0x82 => payload.SequenceEqual(CenterBegin) || payload.SequenceEqual(CenterSample) || payload.SequenceEqual(CenterEnd)
                || payload.SequenceEqual(RangeBegin) || payload.SequenceEqual(RangeEnd),
            _ => false,
        };
    }

    /// <summary>An Edge's four profile slots, three reports each: slot 1 (the default) from 0x70, slot 4's last at 0x7B.</summary>
    internal const byte FirstSlotReport = 0x70, LastSlotReport = 0x7B;

    /// <summary>What an Edge holds in one slot: empty, or a profile by its name (null: one whose name doesn't read).</summary>
    internal sealed record Slot(int Number, bool Empty, string? Name);

    /// <summary>
    /// One slot from its first report. As this Edge answers: an empty slot's second byte is 0x10 and the rest zero (slots
    /// 2-4); a slot in use has its part number there (0) and the name in UTF-16 from byte 6 ("Default Profile" in slot 1).
    /// Only the name is read: what else the three reports mean isn't documented anywhere Exo can check.
    /// </summary>
    internal static Slot? SlotOf(int number, ReadOnlySpan<byte> first)
    {
        if (number is < 1 or > 4 || first.Length < 8 || first[0] != FirstSlotReport + (number - 1) * 3) return null;
        if (first[1] == 0x10) return new Slot(number, true, null);
        var name = new StringBuilder();
        for (var i = 6; i + 1 < first.Length; i += 2)
        {
            var c = (char)(first[i] | first[i + 1] << 8);
            if (c == '\0') break;
            if (char.IsControl(c)) return new Slot(number, false, null);
            name.Append(c);
        }
        return new Slot(number, false, name.Length > 0 ? name.ToString() : null);
    }

    /// <summary>The calibration channel's answer (0x83) as a number: 83 01 target state.</summary>
    internal static uint Answer(ReadOnlySpan<byte> reply) => reply.Length >= 4 ? (uint)(reply[0] << 24 | reply[1] << 16 | reply[2] << 8 | reply[3]) : 0;

    /// <summary>
    /// Whether an end step took: a DualSense answers 83 01 target 02 once; an Edge answers 83 01 target 01, then, asked
    /// again, 83 01 target 03 (or 83 01 03 12 for the centers: its modules keep their own, so it lasts until unplugged).
    /// </summary>
    internal static bool Ended(uint answer, int target, bool edge, bool second) => edge
        ? second ? answer == (0x83010003u | (uint)target << 8) || target == 1 && answer == 0x83010312u : answer == (0x83010001u | (uint)target << 8)
        : answer == (0x83010002u | (uint)target << 8);

    /// <summary>The 12 fine-tune values from a test-channel answer (81 0C 02|04 02, then 24 bytes), or null.</summary>
    internal static ushort[]? Finetune(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4 + 24 || reply[0] != 0x81 || reply[1] != 0x0C || reply[2] is not (0x02 or 0x04) || reply[3] != 0x02) return null;
        var values = new ushort[12];
        for (var i = 0; i < 12; i++) values[i] = BitConverter.ToUInt16(reply.Slice(4 + i * 2, 2));
        return values;
    }

    /// <summary>The fine-tune write's payload: 0C 01 and the 12 values, little-endian.</summary>
    internal static byte[] FinetunePayload(IReadOnlyList<ushort> values)
    {
        if (values.Count != 12) throw new ArgumentException("Fine-tune is 12 values.", nameof(values));
        var payload = new byte[2 + 24];
        FinetuneWrite.CopyTo(payload, 0);
        for (var i = 0; i < 12; i++) BitConverter.TryWriteBytes(payload.AsSpan(2 + i * 2, 2), values[i]);
        return payload;
    }

    /// <summary>NVS status (test channel 03 03, answered from byte 1): locked keeps a calibration until unplugged; unlocked saves it.</summary>
    internal static string NvState(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 5) return "unknown";
        var value = (uint)(reply[1] << 24 | reply[2] << 16 | reply[3] << 8 | reply[4]);
        return value switch { 0x03030201 => "locked", 0x03030200 => "unlocked", 0x15010100 => "reconnect", _ => "unknown" };
    }
}
