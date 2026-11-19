using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

/// <summary>
/// What Exo writes to a DualSense or DualSense Edge, over USB only and never while a game runs (the bridge checks): its
/// sticks' calibration (the controller's own center and range routines, kept until unplugged; kept for good on a
/// DualSense only when asked) and its fine-tune values put back. Every report goes through <see cref="Pad"/>, which sends
/// nothing <see cref="Allowed"/> refuses. Before Exo first changes a controller it saves what the controller had (its
/// fine-tune values) in its own file, so Undo puts it back. What Exo set is kept per controller by its serial (Unit), read
/// back from the controller where it can be, and sent again each time that controller connects.
/// </summary>
internal static partial class ControllerLab
{
    /// <summary>A controller's control handle for feature reports.</summary>
    internal sealed class Pad : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly int _length;
        internal bool Edge { get; }
        /// <summary>Its output report's length: 48 on a DualSense, 64 on an Edge.</summary>
        internal int OutputLength { get; }

        private Pad(SafeFileHandle handle, int length, int output, bool edge) => (_handle, _length, OutputLength, Edge) = (handle, length, output, edge);

        /// <summary>The controller's gamepad interface, opened for feature reports; null off USB or when it won't open.</summary>
        internal static Pad? Open(Peripherals.Peripheral card)
        {
            if (card.Connection != "usb" || !Supported(card.VendorId, card.ProductId)
                || card.Collections.FirstOrDefault(c => c is { Page: 0x01, Usage: 0x05 }) is not { } pad || Peripherals.InterfacePath(pad.Id) is not { } path) return null;
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            var (length, output) = handle.IsInvalid ? (0, 0) : Lengths(handle);
            if (length < 64) { handle.Dispose(); return null; }
            return new Pad(handle, length, output, card.ProductId == DualSenseEdge);
        }

        private static (int Feature, int Output) Lengths(SafeFileHandle handle)
        {
            if (!HidD_GetPreparsedData(handle, out var preparsed)) return (0, 0);
            try { return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? (caps.FeatureReportByteLength, caps.OutputReportByteLength) : (0, 0); }
            finally { HidD_FreePreparsedData(preparsed); }
        }

        /// <summary>Sends an output report on the interrupt pipe, as games do; only one <see cref="OnlySettings"/> passes.</summary>
        internal bool Output(byte[] report)
        {
            if (HostMode.IsDisposable) return false;
            if (!OnlySettings(report) || report.Length != OutputLength) throw new InvalidOperationException("Exo sends a controller no output report but its settings.");
            return WriteFile(_handle, report, report.Length, out var written, 0) && written == report.Length;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int length, out int written, nint overlapped);

        /// <summary>A feature report the controller holds (its ID at [0]); null when refused or it doesn't answer.</summary>
        internal byte[]? Get(byte id)
        {
            if (!Allowed(id, [], set: false, Edge)) throw new InvalidOperationException($"Exo doesn't ask a controller for report 0x{id:X2}.");
            var buffer = new byte[_length];
            buffer[0] = id;
            return HidD_GetFeature(_handle, buffer, buffer.Length) ? buffer : null;
        }

        /// <summary>Sends a feature report: its ID and the command after it, padded with zeros.</summary>
        internal bool Set(byte id, ReadOnlySpan<byte> payload)
        {
            if (HostMode.IsDisposable) return false;
            if (!Allowed(id, payload, set: true, Edge)) throw new InvalidOperationException($"Exo doesn't send a controller report 0x{id:X2} with that.");
            var buffer = new byte[_length];
            buffer[0] = id;
            payload.CopyTo(buffer.AsSpan(1));
            return HidD_SetFeature(_handle, buffer, buffer.Length);
        }

        /// <summary>A test-channel command (0x80) and its answer (0x81, 81 device action 02), asked for every 50 ms for a second.</summary>
        internal byte[]? Command(ReadOnlySpan<byte> command)
        {
            if (!Set(0x80, command)) return null;
            for (var i = 0; i < 20; i++)
            {
                Thread.Sleep(50);
                if (Get(0x81) is { } reply && reply[1] == command[0] && reply[2] == command[1] && reply[3] == 0x02) return reply;
            }
            return null;
        }

        /// <summary>A calibration step (0x82) and the controller's answer (0x83); 0 when it doesn't answer.</summary>
        internal uint Step(ReadOnlySpan<byte> step) => Set(0x82, step) && Get(0x83) is { } reply ? Answer(reply) : 0;

        public void Dispose() => _handle.Dispose();
    }

    // ------------------------------------------------------------------ calibration

    internal static ushort[]? ReadFinetune(Pad pad) => pad.Command(FinetuneRead(pad.Edge)) is { } reply ? Finetune(reply) : null;

    internal static bool WriteFinetune(Pad pad, IReadOnlyList<ushort> values) => pad.Set(0x80, FinetunePayload(values));

    /// <summary>
    /// The fine-tune values read back after a write. The controller doesn't answer the test channel straight after a
    /// write (an Edge's read took the whole second and came back empty each time, on this PC's), so it is given a moment
    /// and asked up to three times.
    /// </summary>
    internal static ushort[]? ReadSettled(Pad pad)
    {
        for (var i = 0; i < 3; i++)
        {
            Thread.Sleep(150);
            if (ReadFinetune(pad) is { } values) return values;
        }
        return null;
    }

    /// <summary>
    /// What to keep as a controller's calibration after writing it: what it says it holds, else what Exo just wrote. Never
    /// the calibration from before: keeping that gave the controller an older calibration back each time it connected.
    /// </summary>
    internal static ushort[]? ToKeep(ushort[]? read, ushort[]? wrote, ushort[]? earlier) => read ?? wrote ?? earlier;

    /// <summary>locked (a calibration lasts until unplugged), unlocked (it is saved), reconnect, or unknown.</summary>
    internal static string ReadNv(Pad pad) => pad.Set(0x80, NvStatus) && WaitFor(pad, 0x81) is { } reply ? NvState(reply) : "unknown";

    // The NVS status answers from byte 1 with its own code, not "device action 02", so it is read as it comes.
    private static byte[]? WaitFor(Pad pad, byte id)
    {
        Thread.Sleep(50);
        return pad.Get(id);
    }

    /// <summary>The controller's own center calibration: sticks left alone, five samples a tenth of a second apart.</summary>
    internal static bool CalibrateCenters(Pad pad)
    {
        if (pad.Step(CenterBegin) != 0x83010101) return false;
        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(100);
            if (pad.Step(CenterSample) != 0x83010101) return false;
        }
        return End(pad, CenterEnd, target: 1);
    }

    internal static bool BeginRange(Pad pad) => pad.Step(RangeBegin) == 0x83010201;

    internal static bool EndRange(Pad pad) => End(pad, RangeEnd, target: 2);

    private static bool End(Pad pad, ReadOnlySpan<byte> step, int target)
    {
        var first = pad.Step(step);
        if (!pad.Edge) return Ended(first, target, edge: false, second: false);
        return Ended(first, target, edge: true, second: false) && Ended(pad.Step(step), target, edge: true, second: true);
    }

    /// <summary>Locks NVS (so a calibration lasts only until unplugged) and says what it is after.</summary>
    internal static string Lock(Pad pad)
    {
        pad.Command(NvLock);
        Thread.Sleep(200);
        return ReadNv(pad);
    }

    /// <summary>
    /// Keeps a DualSense's calibration after it's unplugged: NVS unlocked, then locked again (dualshock-tools' "save").
    /// Locked again whatever the unlock answered, so a failure never leaves it unlocked. Never on an Edge: its calibration
    /// lives in its stick modules, which are write-protected until the controller is opened and modified (dualshock-tools'
    /// own note on saving one; Battle Beaver's "module not saving" fix), and it answers 83 01 03 12 to say they kept theirs.
    /// </summary>
    internal static bool Keep(Pad pad)
    {
        if (pad.Edge) return false;
        var unlocked = pad.Command(NvUnlock) is not null;
        Thread.Sleep(500);
        return Lock(pad) == "locked" && unlocked;
    }

    /// <summary>An Edge's four profile slots as it holds them; null off an Edge or when it doesn't answer. Reads only.</summary>
    internal static IReadOnlyList<Slot>? ReadSlots(Pad pad)
    {
        if (!pad.Edge) return null;
        var slots = new List<Slot>();
        for (var number = 1; number <= 4; number++)
        {
            if (pad.Get((byte)(FirstSlotReport + (number - 1) * 3)) is not { } first || SlotOf(number, first) is not { } slot) return null;
            slots.Add(slot);
        }
        return slots;
    }

    // ------------------------------------------------------------------ what Exo keeps about a controller

    /// <summary>
    /// What Exo saved before it first changed this controller, and what it set: the fine-tune values the controller had
    /// (Original), the ones Exo's calibration left (Calibrated, put back each time an Edge connects), whether a DualSense
    /// kept it for good (Kept), whether Calibrated's range ends need no margin added (Trimmed: a range calibration's got <see cref="WithMargin"/>'s,
    /// centers alone keep the factory's; a file from before the margin has false), and the light, sound and vibration
    /// settings Exo sends it each time it connects (Settings; null: the controller's own). PutBack: when it last got them
    /// back; Tested: the last stick test, so its page opens with what was measured.
    /// </summary>
    internal sealed record Saved(ushort[]? Original, ushort[]? Calibrated, bool Kept, bool Trimmed = false, PadSettings? Settings = null,
        DateTimeOffset? PutBack = null, StickTest? Tested = null)
    {
        internal static Saved Empty => new(null, null, false);
    }

    /// <summary>The last stick test: at rest, then rolled; a step not run or not measured is null.</summary>
    internal sealed record StickTest(DateTimeOffset At, Rest? RestLeft, Rest? RestRight, Edge? EdgeLeft, Edge? EdgeRight);

    /// <summary>
    /// How far in from the edge a range calibration's ends are put, as a share of each half-axis. The controller's own
    /// routine records the hardest push, so a normal roll stopped a few percent short of full output (an Edge read
    /// "Needs calibration" right after it); Sony's factory calibration leaves about 2 to 7% the same way.
    /// </summary>
    internal const double EdgeMargin = 0.05;

    /// <summary>
    /// The fine-tune values with each axis's ends brought <paramref name="margin"/> of the way in toward its center. The 12
    /// values are each axis's low end (left X, left Y, right X, right Y), then their high ends, then their centers; null
    /// when they aren't (an end on the wrong side of its center), so nothing odd is written.
    /// </summary>
    internal static ushort[]? WithMargin(IReadOnlyList<ushort> values, double margin = EdgeMargin)
    {
        if (values.Count != 12) return null;
        var trimmed = values.ToArray();
        for (var axis = 0; axis < 4; axis++)
        {
            int low = values[axis], high = values[axis + 4], center = values[axis + 8];
            if (low >= center || center >= high) return null;
            trimmed[axis] = (ushort)Math.Round(low + (center - low) * margin);
            trimmed[axis + 4] = (ushort)Math.Round(high - (high - center) * margin);
        }
        return trimmed;
    }

    /// <summary>A range calibration's values: its ends moved from <paramref name="original"/>'s (a center calibration leaves them).</summary>
    internal static bool RangeMoved(IReadOnlyList<ushort> calibrated, IReadOnlyList<ushort>? original) =>
        original is { Count: 12 } && calibrated.Count == 12 && Enumerable.Range(0, 8).Any(i => calibrated[i] != original[i]);

    private static string SavedPath(string id) =>
        Path.Combine(PathHelper.AppDataDir, "controllers", string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");

    private static readonly JsonSerializerOptions SavedJson = new() { WriteIndented = true };

    /// <summary>What Exo saved for this controller: under its unit, which follows it from port to port; under its port's id while its serial isn't known.</summary>
    internal static Saved Load(Peripherals.Peripheral card) =>
        card.Unit is { } unit && File.Exists(SavedPath(unit)) ? Load(unit) : Load(card.Id);

    internal static void Store(Peripherals.Peripheral card, Saved saved)
    {
        Store(card.Unit ?? card.Id, saved);
        if (card.Unit is null || !File.Exists(SavedPath(card.Id))) return;
        try { DeletionGate.DeleteFile(SavedPath(card.Id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn($"Controller: its old file stays: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// What Exo saved for a controller under a port, folded into its unit, newest last. A port's file is written when the
    /// serial wasn't known (Exo before 3.17, or a read that failed), and the unit's file hid it: a change made in such a run
    /// was gone the next time the serial read. Only files that are this controller's (<see cref="SameController"/>):
    /// another controller of the same model never takes this one's calibration. Never during a game: reading the values
    /// sends a command.
    /// </summary>
    internal static void Adopt(Peripherals.Peripheral card)
    {
        if (card.Unit is not { } unit) return;
        var folder = Path.GetDirectoryName(SavedPath(unit))!;
        if (!Directory.Exists(folder)) return;
        var ports = Directory.EnumerateFiles(folder, $"usb-{card.VendorId:x4}-{card.ProductId:x4}-*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .OrderBy(id => File.GetLastWriteTimeUtc(SavedPath(id))).ToList();
        if (ports.Count == 0) return;
        using var pad = Pad.Open(card);
        var now = pad is null ? null : ReadFinetune(pad);
        var mine = Load(unit);
        var at = File.Exists(SavedPath(unit)) ? File.GetLastWriteTimeUtc(SavedPath(unit)) : DateTime.MinValue;
        foreach (var id in ports)
        {
            var saved = Load(id);
            if (!SameController(saved, id == card.Id, mine, now)) continue;
            var when = File.GetLastWriteTimeUtc(SavedPath(id));
            mine = when >= at ? Merged(mine, saved) : Merged(saved, mine);
            at = when > at ? when : at;
            try
            {
                Store(unit, mine);
                DeletionGate.DeleteFile(SavedPath(id));
                AppLog.Info("Controller: what Exo saved for it under a port is with the controller now.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn($"Controller: its saved settings stay with its old port: {ex.GetType().Name}"); }
        }
    }

    /// <summary>
    /// Whether a port's file is this controller's: its factory values are the unit's (no two pairs of sticks share them),
    /// or it holds the file's values right now; a file with no values, only its own port's.
    /// </summary>
    internal static bool SameController(Saved port, bool ownPort, Saved unit, IReadOnlyList<ushort>? now)
    {
        if ((port.Original ?? port.Calibrated) is null) return ownPort;
        if (port.Original is { } theirs && unit.Original is { } mine) return theirs.SequenceEqual(mine);
        return now is not null && Holds(now, port);
    }

    /// <summary>Two files of one controller as one: the newer's choices, the older's for what the newer doesn't say, and the first factory values Exo saw.</summary>
    internal static Saved Merged(Saved older, Saved newer) => newer with
    {
        Original = older.Original ?? newer.Original,
        Calibrated = newer.Calibrated ?? older.Calibrated,
        Kept = newer.Calibrated is null ? older.Kept : newer.Kept,
        Trimmed = newer.Calibrated is null ? older.Trimmed : newer.Trimmed,
        Settings = newer.Settings ?? older.Settings,
        PutBack = newer.PutBack ?? older.PutBack,
        Tested = newer.Tested ?? older.Tested,
    };

    /// <summary>Whether a controller holding <paramref name="now"/> is the one <paramref name="saved"/> is for: its own values or Exo's.</summary>
    internal static bool Holds(IReadOnlyList<ushort> now, Saved saved) =>
        saved.Original?.SequenceEqual(now) == true || saved.Calibrated?.SequenceEqual(now) == true;

    /// <summary>A stick test's step added to what was saved: a rest step starts a new test, a rolled one finishes it.</summary>
    internal static StickTest TestWith(StickTest? last, DateTimeOffset at, bool rolled, Rest? restLeft, Rest? restRight, Edge? edgeLeft, Edge? edgeRight) =>
        rolled ? new StickTest(at, last?.RestLeft, last?.RestRight, edgeLeft, edgeRight) : new StickTest(at, restLeft, restRight, null, null);

    private static Saved Load(string id)
    {
        try { return File.Exists(SavedPath(id)) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath(id)), SavedJson) ?? Saved.Empty : Saved.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Controller: couldn't read what Exo saved for it: {ex.GetType().Name}");
            return Saved.Empty;
        }
    }

    private static void Store(string id, Saved saved)
    {
        var path = SavedPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved, SavedJson));
        File.Move(tmp, path, overwrite: true);
    }
}
