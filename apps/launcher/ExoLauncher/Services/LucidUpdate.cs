using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>A Lucid release as the release list states it, accepted by <see cref="LucidUpdate.ParseRelease"/>.</summary>
internal sealed record LucidOffer(Version Version, string Tag, long Size, string Sha256, string Url);

/// <summary>What a Lucid exe says about itself.</summary>
internal sealed record LucidFile(Version? Version, string? Product, string? Company, string? Signer);

/// <summary>What the update keeps between runs (<c>lucid-update.json</c>): when to look next, what it refused, what it installed.</summary>
internal sealed class LucidUpdateState
{
    public DateTime? NextCheck { get; set; }
    /// <summary>SHA-256 of releases that failed their check or didn't start: never downloaded again.</summary>
    public List<string> Refused { get; set; } = [];
    /// <summary>SHA-256 and size of the exe Exo put in the store.</summary>
    public string? Sha256 { get; set; }
    public long Size { get; set; }
    /// <summary>A verified download waiting for no game or call to be swapped in.</summary>
    public string? PendingSha256 { get; set; }
    public string? PendingVersion { get; set; }
    /// <summary>The last failure, in words for the Lucid row; empty when the last run was fine.</summary>
    public string? Note { get; set; }
}

/// <summary>Where the downloaded Lucid lives: outside Exo's app folder, which Exo's own updates replace.</summary>
internal sealed class LucidStore(string folder, string stateFile)
{
    internal string Folder { get; } = folder;
    internal string Current { get; } = Path.Combine(folder, "Lucid.exe");
    /// <summary>The Lucid replaced by the last update, kept so a new one that fails can be put back.</summary>
    internal string Previous { get; } = Path.Combine(folder, "Lucid.previous.exe");
    /// <summary>A verified download not swapped in yet.</summary>
    internal string Pending { get; } = Path.Combine(folder, "Lucid.pending.exe");
    internal string Download { get; } = Path.Combine(folder, "Lucid.download.exe");

    internal LucidUpdateState Load()
    {
        try
        {
            if (File.Exists(stateFile) && JsonSerializer.Deserialize<LucidUpdateState>(File.ReadAllText(stateFile)) is { } state) return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new LucidUpdateState();
    }

    internal void Save(LucidUpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stateFile))!);
            var staged = stateFile + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(state));
            File.Move(staged, stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Lucid: the update's notes weren't kept: " + ex.Message); }
    }
}

/// <summary>What the update does outside the store, as functions, so its order is tested with fakes and no network or Lucid.</summary>
internal sealed record LucidUpdateSteps(
    Func<DateTime> Now,
    // Lucid is on (Start with Exo, set up) and is the one Exo hosts, not a copy of the user's.
    Func<bool> On,
    // A game or a call is on.
    Func<bool> Busy,
    // The newest release's JSON from Exo's cache, null when it didn't come.
    Func<CancellationToken, Task<string?>> Release,
    Func<LucidOffer, string, CancellationToken, Task> Download,
    Func<string, LucidFile> Inspect,
    // The exe Exo runs now (newest of the shipped and the downloaded), or null.
    Func<string?> HostedExe,
    Func<bool> Running,
    // Closes every Lucid Exo hosts; false when one is still running.
    Func<Task<bool>> Stop,
    Func<string, bool> Start,
    Func<Task<bool>> Started,
    // Held around the stop, swap and start, which must not meet a settings change (it closes and starts Lucid too).
    Func<Task> Lock,
    Action Unlock)
{
    internal long Floor { get; init; } = LucidUpdate.Smallest;

    /// <summary>False while Exo holds no per-release SHA-256 pin (<see cref="LucidUpdate.Pins"/>): the look does nothing.</summary>
    internal bool Pinned { get; init; } = true;
}

internal enum LucidUpdateOutcome { Off, Busy, NotDue, Offline, Refused, Nothing, Waiting, Installed, RolledBack, Failed }

/// <summary>
/// Lucid's updates, which Exo fetches itself so users get the author's newest Lucid and not the exe Exo shipped. Once a day
/// while Lucid is on and nothing is playing or in a call, one request to Exo's cache of Lucid's release list (tekkusai/Lucid). A newer
/// release is downloaded from Exo's cache of tekkusai/Lucid only, checked (the size and SHA-256 the release states for the asset, the
/// name, a Windows program, Lucid's own name and version, the publisher the running copy has), kept in a store outside Exo's
/// app folder, and swapped in after the Lucid Exo hosts is closed (<see cref="LucidStop"/>); the old one stays for a put-back.
/// Ways it could fail: a partial or altered download replaces a working Lucid; a download that passes the size but not the
/// digest, or a lookalike asset, URL or file, is taken; a game or call meets the restart; the swap happens with Lucid still
/// running from the old file; a new Lucid that does not start is left in place; a release refused once is fetched again
/// every day; Exo's own update deletes or downgrades the downloaded copy; the check runs while Lucid is off.
/// </summary>
internal static partial class LucidUpdate
{
    /// <summary>
    /// The per-release SHA-256 pins Exo holds, by tag. Empty: Lucid's update is dormant, so no release is looked up, offered or
    /// downloaded (AGENTS.md: nothing from GitHub, and no key). A release is offered only with its own pin here.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal const string ReleaseUrl = "https://" + VerifiedGitHubReleaseDownloader.CacheHost + "/v1/cache/tekkusai/Lucid/releases/latest";
    internal const string AssetName = "Lucid.exe";
    internal const long Smallest = 5L * 1024 * 1024;
    internal const long Largest = 300L * 1024 * 1024;
    internal static readonly TimeSpan Every = TimeSpan.FromDays(1);
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex TagShape();
    [GeneratedRegex(@"^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex DigestShape();
    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d+){1,3})(?![\d.])")]
    private static partial Regex NumbersIn();

    /// <summary>"Lucid 6.0.4" is 6.0.4.0; null when the text holds no version. Parts compare as numbers: 6.0.10 is newer than 6.0.9.</summary>
    internal static Version? ReleaseVersion(string? text)
    {
        if (NumbersIn().Match(text ?? "") is not { Success: true } found) return null;
        var parts = found.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : -1).ToList();
        if (parts.Any(p => p < 0)) return null;
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>The same version in four parts, so 6.0.4 and 6.0.4.0 are equal.</summary>
    internal static Version Canonical(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>
    /// The one release this update accepts, from the release list's JSON, or why not. Only a published, final release with exactly one
    /// asset called Lucid.exe, uploaded, of a sane size, with a SHA-256 digest the release states, whose download link is the
    /// one this repository's tag makes; anything else is refused whatever it says.
    /// </summary>
    internal static (LucidOffer? Offer, string? Why) ParseRelease(string json, long floor = Smallest)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "it isn't a release");
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False) return (null, "it is a draft");
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind != JsonValueKind.False) return (null, "it is a prerelease");
            if (Str(root, "tag_name") is not { } tag || !TagShape().IsMatch(tag)) return (null, "its tag isn't a plain name");
            var version = ReleaseVersion(Str(root, "name")) ?? ReleaseVersion(tag);
            if (version is null) return (null, "it names no version");
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return (null, "it has no files");
            var wanted = assets.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object && Str(a, "name") == AssetName).ToList();
            if (wanted.Count != 1) return (null, $"it has {wanted.Count} files called {AssetName}");
            var asset = wanted[0];
            if (Str(asset, "state") != "uploaded") return (null, "its file isn't uploaded");
            if (!asset.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size < floor || size > Largest) return (null, "its size isn't one of Lucid's");
            if (Str(asset, "digest") is not { } digest || DigestShape().Match(digest) is not { Success: true } hex) return (null, "the release states no SHA-256 for it");
            var url = $"https://{VerifiedGitHubReleaseDownloader.CacheHost}/v1/cache/tekkusai/Lucid/releases/download/{tag}/{AssetName}";
            if (!string.Equals(Str(asset, "browser_download_url"), url, StringComparison.Ordinal)) return (null, "its link isn't this repository's");
            return (new LucidOffer(version, tag, size, hex.Groups[1].Value.ToLowerInvariant(), url), null);
        }
        catch (JsonException) { return (null, "it doesn't read"); }

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>The exe to run of the shipped and the downloaded one: the newer; the shipped one when they are the same or the other can't be read.</summary>
    internal static string? Newest(string? shipped, string? downloaded, Func<string, Version?> version)
    {
        if (downloaded is null) return shipped;
        if (shipped is null) return downloaded;
        return version(downloaded) is { } theirs && version(shipped) is { } ours && Canonical(theirs) > Canonical(ours) ? downloaded : shipped;
    }

    /// <summary>
    /// Why a download isn't the release GitHub described, null when it is: its size and SHA-256 are the stated ones, it is a
    /// Windows program, its file version is at least the release's and newer than the running one's, its product is Lucid,
    /// and its publisher (company, and the signer when the running one has one) is the running one's.
    /// </summary>
    internal static string? CheckFile(string path, LucidOffer offer, LucidFile download, LucidFile? running)
    {
        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "it can't be read"; }
        if (length != offer.Size) return $"it is {length} bytes, not the {offer.Size} that the release states";
        if (!Sha256Of(path).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase)) return "its SHA-256 isn't the one the release states";
        if (!IsProgram(path)) return "it isn't a Windows program";
        if (download.Version is not { } version) return "it has no file version";
        if (Canonical(version) < offer.Version) return $"it is version {version}, not {offer.Version}";
        if (running?.Version is { } current && Canonical(version) <= Canonical(current)) return $"it is version {version}, no newer than {current}";
        if (download.Product?.Contains("Lucid", StringComparison.OrdinalIgnoreCase) != true) return $"its product is {download.Product}";
        if (!string.IsNullOrWhiteSpace(running?.Company) && !string.Equals(download.Company?.Trim(), running.Company.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"its publisher is {download.Company}, not {running.Company}";
        if (!string.IsNullOrWhiteSpace(running?.Signer) && !string.Equals(download.Signer, running.Signer, StringComparison.OrdinalIgnoreCase)) return "it isn't signed by " + running.Signer;
        return null;
    }

    internal static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>A Windows program: "MZ", and "PE\0\0" where its header says.</summary>
    internal static bool IsProgram(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(file);
            if (file.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
            file.Position = 0x3C;
            var header = reader.ReadInt32();
            if (header <= 0 || header > file.Length - 4) return false;
            file.Position = header;
            return reader.ReadUInt32() == 0x00004550;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// One look at whether Lucid should update, and the update itself when a newer release checks out. Safe to call every
    /// half hour: it does nothing while Lucid is off or a game or call is on, asks the cache once a day, and fetches a release
    /// it has refused never again.
    /// </summary>
    internal static async Task<LucidUpdateOutcome> RunAsync(LucidStore store, LucidUpdateSteps steps, CancellationToken ct)
    {
        // Dormant without a pin: nothing is asked of GitHub, and no release is offered (see Pins).
        if (!steps.Pinned) return LucidUpdateOutcome.Off;
        if (!steps.On()) return LucidUpdateOutcome.Off;
        if (steps.Busy()) return LucidUpdateOutcome.Busy;
        var state = store.Load();

        // A verified download that waited for a game or call to end goes in before anything is asked of the cache.
        if (File.Exists(store.Pending))
        {
            if (state.PendingSha256 is { } waiting && File.Exists(store.Pending) && Sha256Of(store.Pending).Equals(waiting, StringComparison.OrdinalIgnoreCase))
                return await InstallAsync(store, state, steps, waiting).ConfigureAwait(false);
            TryDelete(store.Pending);
            state.PendingSha256 = state.PendingVersion = null;
            store.Save(state);
        }

        var now = steps.Now();
        if (state.NextCheck is { } next && now < next) return LucidUpdateOutcome.NotDue;

        string? json = null;
        try { json = await steps.Release(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException)
        {
            AppLog.Info("Lucid: the release list didn't come: " + ex.Message);
        }
        if (json is null)
        {
            state.NextCheck = now + RetryAfter;
            store.Save(state);
            return LucidUpdateOutcome.Offline;
        }
        state.NextCheck = now + Every;

        var (offer, why) = ParseRelease(json, steps.Floor);
        if (offer is null)
        {
            AppLog.Warn("Lucid: the newest release wasn't taken: " + why + ".");
            store.Save(state);
            return LucidUpdateOutcome.Refused;
        }
        var hosted = steps.HostedExe();
        var running = hosted is null ? null : steps.Inspect(hosted);
        if ((running?.Version is { } have && offer.Version <= Canonical(have)) || state.Refused.Contains(offer.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            state.Note = null;
            store.Save(state);
            return LucidUpdateOutcome.Nothing;
        }

        Directory.CreateDirectory(store.Folder);
        TryDelete(store.Download);
        try { await steps.Download(offer, store.Download, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Warn($"Lucid: {offer.Version} didn't download: {ex.Message}");
            TryDelete(store.Download);
            state.NextCheck = now + RetryAfter;
            store.Save(state);
            return LucidUpdateOutcome.Offline;
        }
        if (CheckFile(store.Download, offer, steps.Inspect(store.Download), running) is { } bad)
        {
            AppLog.Warn($"Lucid: {offer.Version} wasn't taken: {bad}.");
            TryDelete(store.Download);
            state.Refused.Add(offer.Sha256);
            state.Note = $"Lucid {Shown(offer.Version)} didn't pass its check, so it wasn't installed.";
            store.Save(state);
            return LucidUpdateOutcome.Refused;
        }
        try { File.Move(store.Download, store.Pending, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Lucid: the download couldn't be kept: " + ex.Message);
            TryDelete(store.Download);
            state.NextCheck = now + RetryAfter;
            store.Save(state);
            return LucidUpdateOutcome.Failed;
        }
        AppLog.Info($"Lucid: {offer.Version} downloaded and passed its check ({offer.Size} bytes, SHA-256 {offer.Sha256}).");
        state.PendingSha256 = offer.Sha256;
        state.PendingVersion = Shown(offer.Version);
        store.Save(state);
        // The download took a while: a game or call may have started since.
        if (steps.Busy()) return LucidUpdateOutcome.Waiting;
        return await InstallAsync(store, state, steps, offer.Sha256).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes Lucid, puts the pending download over the store's exe (the old one kept as the previous), reads it back, and
    /// starts it; any step failing leaves, or puts back, the Lucid that ran before and starts it again.
    /// </summary>
    private static async Task<LucidUpdateOutcome> InstallAsync(LucidStore store, LucidUpdateState state, LucidUpdateSteps steps, string sha256)
    {
        await steps.Lock().ConfigureAwait(false);
        try
        {
            if (steps.Busy()) return LucidUpdateOutcome.Waiting;
            var wasRunning = steps.Running();
            if (wasRunning && !await steps.Stop().ConfigureAwait(false))
            {
                AppLog.Warn("Lucid: it didn't close, so the update waits.");
                return LucidUpdateOutcome.Waiting;
            }
            var version = state.PendingVersion ?? "the new version";
            var hadStored = File.Exists(store.Current);
            var (oldSha, oldSize) = (state.Sha256, state.Size);
            string? failure = null;
            var fileError = false;
            try
            {
                if (hadStored) File.Move(store.Current, store.Previous, overwrite: true);
                File.Move(store.Pending, store.Current);
                if (!Sha256Of(store.Current).Equals(sha256, StringComparison.OrdinalIgnoreCase)) failure = "it read back differently";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex.Message; fileError = true; }

            if (failure is null && steps.Start(store.Current) && await steps.Started().ConfigureAwait(false))
            {
                state.Sha256 = sha256;
                state.Size = new FileInfo(store.Current).Length;
                state.PendingSha256 = state.PendingVersion = null;
                state.Note = null;
                store.Save(state);
                AppLog.Info($"Lucid: updated to {version}.");
                return LucidUpdateOutcome.Installed;
            }

            // The new one didn't go in whole or didn't start: the one that ran before comes back.
            AppLog.Warn($"Lucid: {version} {(failure is null ? "didn't start" : "wasn't put in: " + failure)}; the one before is back.");
            await steps.Stop().ConfigureAwait(false);
            PutBack(store, hadStored);
            TryDelete(store.Pending);
            // A release that read back wrong or didn't start is not tried again; a file Windows wouldn't move says nothing about it.
            if (!fileError) state.Refused.Add(sha256);
            state.PendingSha256 = state.PendingVersion = null;
            (state.Sha256, state.Size) = hadStored ? (oldSha, oldSize) : (null, 0);
            state.Note = fileError ? null : $"Lucid {version} didn't start, so the one before is back.";
            store.Save(state);
            if (steps.HostedExe() is { } back) steps.Start(back);
            return failure is null ? LucidUpdateOutcome.RolledBack : LucidUpdateOutcome.Failed;
        }
        finally { steps.Unlock(); }
    }

    /// <summary>The store's exe given up for the previous one, or gone altogether when there was none (the shipped Lucid is then the newest).</summary>
    internal static void PutBack(LucidStore store, bool hadStored)
    {
        TryDelete(store.Current);
        if (!hadStored || !File.Exists(store.Previous)) return;
        try { File.Move(store.Previous, store.Current); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Lucid: the one before couldn't be put back: " + ex.Message); }
    }

    internal static string Shown(Version v) => LucidCompanion.Shown(v);

    private static void TryDelete(string file)
    {
        try { DeletionGate.DeleteFile(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
