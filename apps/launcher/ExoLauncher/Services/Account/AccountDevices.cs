namespace ExoLauncher.Services.Account;

/// <summary>
/// The PCs of the account (CLIENT.md 4.3). Each Exo app on this PC keeps its own device ID in its own folder, so one PC can
/// hold several sessions: here they are one PC and show as one "This PC" row. This PC is every device ID the Exo apps on it
/// keep, read from their folders; every other device is another PC.
/// </summary>
internal static class AccountDevices
{
    private static readonly string[] Apps = ["ExoLauncher", "ExoCinema", "ExoBrowser"];

    /// <summary>This PC's device IDs: this app's own, and the ones the other Exo apps keep in their folders.</summary>
    internal static HashSet<string> OfThisPc(string ownId, string? dataDir = null, string? localAppData = null)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (AccountDevice.IsValid(ownId)) ids.Add(ownId);
        if (AccountDevice.PcId() is { } pcId) ids.Add(pcId);
        if (AccountDevice.ReadLegacyId(dataDir) is { } ownLegacy) ids.Add(ownLegacy);
        var local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            foreach (var app in Apps)
            {
                if (AccountDevice.ReadLegacyId(Path.Combine(local, app)) is { } saved)
                    ids.Add(saved);
            }
        }
        return ids;
    }

    /// <summary>The rows with this PC's sessions merged into one, first; the other PCs follow in the service's order.</summary>
    internal static IReadOnlyList<AccountSessionRow> Merge(
        IReadOnlyList<AccountSessionRow> rows,
        string ownId,
        IReadOnlySet<string>? thisPcIds = null)
    {
        var mine = thisPcIds ?? OfThisPc(ownId);
        var here = rows.Where(row => row.Current || mine.Contains(row.Device)).ToList();
        var others = MergeByDevice(rows.Where(row => !(row.Current || mine.Contains(row.Device))));
        if (here.Count == 0) return others;
        return [Combine(here, thisPc: true), .. others];
    }

    private static List<AccountSessionRow> MergeByDevice(IEnumerable<AccountSessionRow> rows)
    {
        var groups = new Dictionary<string, List<AccountSessionRow>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var row in rows)
        {
            if (!groups.TryGetValue(row.Device, out var list))
            {
                list = [];
                groups[row.Device] = list;
                order.Add(row.Device);
            }
            list.Add(row);
        }
        return order.Select(device => Combine(groups[device], thisPc: false)).ToList();
    }

    private static AccountSessionRow Combine(List<AccountSessionRow> group, bool thisPc)
    {
        var newestFirst = group
            .OrderByDescending(row => row.LastUsed)
            .ThenByDescending(row => row.Created)
            .ToList();
        var latest = newestFirst[0];
        var name = newestFirst.Select(row => row.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        return new AccountSessionRow(
            latest.Device,
            group.Min(row => row.Created),
            group.Max(row => row.LastUsed),
            group.Any(row => row.Current),
            ThisPc: thisPc,
            Name: name);
    }
}
