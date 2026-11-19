using System.Globalization;

namespace ExoLauncher.Services;

/// <summary>
/// The wireless doctor: after a test of a mouse on a receiver or Bluetooth, its dropouts (gaps of four slots and at least 1 ms, and how many slots stayed
/// empty), how they fall, and what shares the 2.4 GHz band or sits by the receiver: Wi-Fi on 2.4 GHz, connected Bluetooth devices, a USB 3 device on its
/// USB controller (USB 3's own signal can disturb a 2.4 GHz receiver close by). Exo reads gaps between reports, not the radio, so it says what is
/// nearby and never that any of it is the cause. With no dropouts it says so and lists nothing. It can't read how crowded the band is:
/// Windows asks for location permission for that, and Exo doesn't ask for it.
/// </summary>
internal sealed partial class MouseCore
{
    internal enum Pattern { None, Empty, Spread, Stretch, Beat }

    /// <summary>A test's dropouts. Empty: % of slots with no report. Every: for a Beat, the seconds between them.</summary>
    internal sealed record Dropouts(int Count, double Empty, Pattern Pattern, double? Every = null);

    /// <summary>What's around the receiver. Channel: Wi-Fi's now (null: not on Wi-Fi, or Windows didn't say). Neighbor: a USB 3 device on its USB controller. Bluetooth: the mouse itself is on it.</summary>
    internal sealed record Air(int? Channel, int BluetoothDevices, string? Neighbor, bool Bluetooth);

    /// <summary>What the doctor says. Before: this session's dropouts in the test before this one.</summary>
    internal sealed record Doctor(int Dropouts, double Empty, int? Before, IReadOnlyList<string> Lines);

    private volatile Doctor? _doctor;

    internal Doctor? LastDoctor => _doctor;

    /// <summary>
    /// Reads a test's gaps for dropouts: at least four slots and 1 ms, among the gaps the test judged (so a hand stopping isn't one). Most inside one second are a
    /// Stretch; otherwise six or more evenly spaced (their spacing varying under a quarter) are a Beat.
    /// </summary>
    internal static Dropouts ReadDropouts(IReadOnlyList<Report> reports, long frequency)
    {
        var gaps = Analyze(reports, frequency, 0);
        if (gaps.Rate == 0 || gaps.Checked == 0) return new Dropouts(0, 0, Pattern.None);
        var slot = (double)frequency / gaps.Rate;
        var floor = Math.Max(slot * 4, frequency * 0.001);
        var at = new List<long>();
        foreach (var k in JudgedGaps(reports, frequency, slot))
            if (reports[k + 1].At - reports[k].At >= floor) at.Add(reports[k].At);
        var empty = Math.Round(100 - (gaps.Flow?.Filled ?? 100), 1);
        if (at.Count == 0) return new Dropouts(0, empty, empty >= 0.5 ? Pattern.Empty : Pattern.None);
        if (OneStretch(at, frequency)) return new Dropouts(at.Count, empty, Pattern.Stretch);
        if (at.Count >= 6)
        {
            var spacing = at.Zip(at.Skip(1), (a, b) => (double)(b - a)).ToList();
            var mean = spacing.Average();
            if (Math.Sqrt(spacing.Average(s => (s - mean) * (s - mean))) / mean < 0.25) return new Dropouts(at.Count, empty, Pattern.Beat, Math.Round(mean / frequency, 1));
        }
        return new Dropouts(at.Count, empty, Pattern.Spread);
    }

    /// <summary>The doctor's words, for a mouse that isn't wired; whatever is around is named only when there is something to explain.</summary>
    internal static IReadOnlyList<string> Words(Dropouts d, int? before, Air air)
    {
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var was = before is { } b ? $" {N(b)} before." : "";
        if (d.Pattern == Pattern.None) return [$"No dropouts in this test.{was}"];
        var lines = new List<string>
        {
            d.Pattern switch
            {
                Pattern.Empty => $"No long dropouts, but {d.Empty.ToString("0.0", CultureInfo.InvariantCulture)}% of its slots stayed empty.{was}",
                Pattern.Stretch => $"{N(d.Count)} dropouts, most in one stretch, {d.Empty.ToString("0.0", CultureInfo.InvariantCulture)}% of slots empty.{was}",
                Pattern.Beat => $"{N(d.Count)} dropouts on a steady beat, about every {d.Every?.ToString("0.0", CultureInfo.InvariantCulture)} s: something that repeats, like a Wi-Fi scan, can do that.{was}",
                _ => $"{N(d.Count)} dropouts across the test, {d.Empty.ToString("0.0", CultureInfo.InvariantCulture)}% of slots empty.{was}",
            },
        };
        if (air.Channel is { } channel && channel is >= 1 and <= 14) lines.Add($"Your Wi-Fi is on 2.4 GHz, channel {channel}, the band your mouse uses.");
        if (air.BluetoothDevices > 0) lines.Add(air.BluetoothDevices == 1 ? "A Bluetooth device is connected, on the same band." : $"{air.BluetoothDevices} Bluetooth devices are connected, on the same band.");
        if (air.Bluetooth) lines.Add("Compare it only with other Bluetooth runs.");
        else
        {
            if (air.Neighbor is { } neighbor) lines.Add($"{neighbor} on its USB controller runs at USB 3, which can disturb a receiver right beside it.");
            lines.Add("Try the receiver on a short USB extension, close to the mouse.");
        }
        lines.Add("Exo reads the gaps between reports, not the radio.");
        return lines;
    }

    /// <summary>The doctor's reading of the test, when <paramref name="air"/> finds the mouse isn't wired; a wired one leaves nothing.</summary>
    internal Doctor? Examine(Sample sample, Func<string, Air?> air)
    {
        if (sample.Mouse is not { } mouse || air(mouse) is not { } around)
        {
            _doctor = null;
            return null;
        }
        var d = ReadDropouts(sample.Reports, sample.Frequency);
        return _doctor = new Doctor(d.Count, d.Empty, _doctor?.Dropouts, Words(d, _doctor?.Dropouts, around));
    }

    /// <summary>
    /// What's around a mouse that isn't wired, by its device instance; null when it is wired. <paramref name="channel"/> asks for Wi-Fi's channel only once Exo knows
    /// it has something to say about it.
    /// </summary>
    internal static Air? ReadAir(string mouse, IReadOnlyList<Peripherals.DevNode> tree, IReadOnlyList<Peripherals.Peripheral> cards, Func<int?> channel, Func<IReadOnlyList<Peripherals.Peripheral>, List<UsbPorts.MapController>> map)
    {
        var parents = tree.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Parent, StringComparer.OrdinalIgnoreCase);
        Peripherals.Peripheral? card = null;
        string? id = mouse;
        for (var hops = 0; id is not null && hops < 32 && card is null; hops++)
        {
            var here = id;
            card = cards.FirstOrDefault(c => c.Kind == "mouse" && string.Equals(c.Source, here, StringComparison.OrdinalIgnoreCase));
            id = parents.GetValueOrDefault(here);
        }
        if (card?.Connection is not ("wireless" or "bluetooth")) return null;
        var bluetooth = tree.Where(n => n.Connected == true && n.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) && !string.Equals(n.Container, tree.FirstOrDefault(m => string.Equals(m.Id, card.Source, StringComparison.OrdinalIgnoreCase))?.Container, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Container ?? n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string? neighbor = null;
        if (card.Connection == "wireless")
        {
            var mine = map(cards).FirstOrDefault(c => c.Devices.Any(d => d.Kind == "mouse" && d.Name == card.Name));
            neighbor = mine?.Devices.FirstOrDefault(d => d.Speed == 3 && d.Kind is not "mouse")?.Name;
        }
        return new Air(channel(), bluetooth, neighbor, card.Connection == "bluetooth");
    }
}
