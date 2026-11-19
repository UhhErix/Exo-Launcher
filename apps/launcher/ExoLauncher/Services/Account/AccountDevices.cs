namespace ExoLauncher.Services.Account;

/// <summary>
/// The PCs of the account (CLIENT.md 4.3). Each Exo app on this PC keeps its own device ID in its own folder, so one PC can
/// hold several sessions: here they are one PC and show as one "This PC" row. This PC is every device ID the Exo apps on it
/// keep, read from their folders; every other device is another PC.
/// </summary>
internal static class AccountDevices
{
    private static readonly string[] Apps = ["ExoLauncher", "ExoCinema"];

    /// <summary>This PC's device IDs: this app's own, and the ones the other Exo apps keep in their folders.</summary>
    internal static HashSet<string> OfThisPc(string ownId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { ownId };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var app in Apps)
        {
            try
            {
                var saved = File.ReadAllText(Path.Combine(local, app, AccountDevice.FileName)).Trim();
                if (AccountDevice.IsValid(saved)) ids.Add(saved);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return ids;
    }

    /// <summary>The rows with this PC's sessions merged into one, first; the other PCs follow in the service's order.</summary>
    internal static IReadOnlyList<AccountSessionRow> Merge(IReadOnlyList<AccountSessionRow> rows, string ownId)
    {
        var mine = OfThisPc(ownId);
        var here = rows.Where(row => row.Current || mine.Contains(row.Device)).ToList();
        if (here.Count == 0) return rows;
        var others = rows.Where(row => !(row.Current || mine.Contains(row.Device))).ToList();
        var latest = here.MaxBy(row => row.LastUsed)!;
        var merged = new AccountSessionRow(latest.Device, here.Min(row => row.Created), here.Max(row => row.LastUsed), here.Any(row => row.Current), ThisPc: true);
        return [merged, .. others];
    }
}
