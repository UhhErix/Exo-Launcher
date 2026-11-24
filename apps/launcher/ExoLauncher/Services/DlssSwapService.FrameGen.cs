using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Frame generation through a mod, per game, on a card without the frame generation the game offers.
///
/// Exo picks the package; every card is meant to get one. An RTX 20 or 30 whose game speaks DLSS frame generation (310
/// or newer) through Streamline 2 on Direct3D 12 gets NVIDIA's own, through DLSSG SM86 (<see cref="DlssgSm86"/>): one
/// proxy, the ini, and that release's folder, preset A at 2x. That holds only while Exo can offer it (its pin in
/// <see cref="PinnedFrameGenReleases"/>, none today), the game's folder has its loader's name free, and it hasn't failed
/// its first session there (<see cref="FrameGenMemory.DlssFailed"/>). Otherwise, and on every other card a mod helps (a
/// GeForce GTX 900 or newer, a Radeon RX 400 or newer and Radeon graphics in laptops and handhelds, an Intel Arc), the
/// game gets OptiScaler (github.com/optiscaler/OptiScaler, GPL-3.0; <see cref="OptiScaler"/>), so the game's switch runs
/// FidelityFX. A game that has frame generation of its own then needs nothing. An RTX 40 or newer has NVIDIA's own, so
/// it gets no mod. An older MFG Unlocker install is taken out; an earlier DLSSG SM86 install stays and keeps working.
/// Best settings puts the chosen package in by itself, in the background, and never in a game with anti-cheat.
///
/// Each mod's release is pinned in code by size and SHA-256 (<see cref="PinnedFrameGenReleases"/>) and fetched from
/// Exo's cache through <see cref="VerifiedGitHubReleaseDownloader"/>; the cache is never asked which release is newest,
/// so a newer mod arrives with a Launcher release. A release is accepted only with the files it expects (x64 images,
/// the mod's own DLL by its OriginalFilename, AMD's and Intel's libraries signed by them). Releases before a mod's
/// minimum and releases known not to work (<see cref="RefusedFrameGenReleases"/>) are refused. While no game runs, Exo
/// moves each game to a newer pinned release, keeping the release before in its own copy: if the next session ends
/// within 30 seconds, the game goes back to the release before and the new one is never offered to it again.
///
/// Off until Best settings' switch is on: it is a community mod that loads into the game, it adds lag, and it downloads.
/// Turning that switch off takes it out of every game. Never: on a GeForce RTX 40 or newer (DLSS Frame Generation, and an
/// RTX 50 has multi frame generation), in a game with anti-cheat of any kind (a mod that loads into the game, whatever its
/// upscalers were allowed), beside another mod. If a game later shows anti-cheat, a patch changes its program
/// (<see cref="FrameGenGameUpdated"/>), the game no longer ships DLSS frame generation, or the card no longer needs it,
/// Exo takes it out before the game starts (and as its page opens).
///
/// Exo's record (<see cref="FrameGenManifestName"/>, beside the game's program) lists every file it wrote with its size
/// and SHA-256. Off removes each file only while its bytes are what Exo wrote (the loader only while it is also the mod's
/// own DLL), the settings file, what the mod writes as it runs (its logs and status), and the mod's folder once it is
/// empty, so the game's folder is as it was; no file the game ships or another mod put there is replaced or removed, and
/// one that changed since stays, as does one a mod manager deployed over Exo's (its backup, name.vortex_backup, beside
/// it), whatever its bytes. Damage after it went in has a reason (<see cref="FrameGenDamage"/>): another mod over
/// Exo's loader is left alone and keeps the game off (Off then keeps a record of that one file, so Exo's own bytes put
/// back under the name later still go; <see cref="FrameGenInstall.Out"/>); files of Exo's that went go back once, at the
/// next pass, and the game stays off if they go again.
/// The upscaler scan reads these files as the mod's (DlssSwapService.Mods.cs) and never updates them.
/// </summary>
public sealed partial class DlssSwapService
{
    internal const string FrameGenManifestName = "exo-frame-gen.json";

    // ------------------------------------------------------------------ the mods

    /// <param name="Source">The file in the release.</param>
    /// <param name="Target">Where it goes under the game's program folder; empty for the mod's loader, named per game.</param>
    /// <param name="OffNvidia">Only a Radeon or Arc needs it.</param>
    /// <param name="Optional">A release may leave it out (license texts).</param>
    internal sealed record FrameGenFileSpec(string Source, string Target, bool OffNvidia = false, bool Optional = false);

    /// <param name="Asset">The release file's name, the one file Exo takes from a release.</param>
    /// <param name="Dll">The loader's own name (its OriginalFilename), checked before it is installed or removed.</param>
    /// <param name="Settings">The mod's settings file beside the loader, which Exo writes and the mod's menu may rewrite.</param>
    /// <param name="Loaders">Names the loader takes, the mod's own order; the first the game's folder doesn't have.</param>
    /// <param name="Logs">Files the mod writes beside its loader as it runs, by name or by the start of the name.</param>
    /// <param name="Folder">The mod's own folder beside its loader, if it has one.</param>
    /// <param name="Minimum">Older releases are refused.</param>
    internal sealed record FrameGenMod(
        string Name, string Owner, string Repo, Regex Asset, long MaxSize, string Dll, string Settings, string[] Loaders,
        FrameGenFileSpec[] Files, string[] Logs, string[] LogPrefixes, string? Folder, Version Minimum, string? Payload = null);

    /// <summary>NVIDIA's own frame generation on RTX 20, 30 and 40; from 1.5.0 it runs on RTX 30.</summary>
    internal static readonly FrameGenMod MfgUnlocker = new(
        "MFG Unlocker", "dashdogy", "MFG-Unlocker", MfgAsset(), 64L << 20, "MFGUnlocker.dll", "RTXMFG-Universal.json",
        ["version.dll", "winmm.dll", "dxgi.dll", "dinput8.dll", "wininet.dll", "winhttp.dll"],
        [new("MFGUnlocker.dll", "")],
        [], ["RTXMFG-", "RTX40MFG-"], null, new(1, 5, 0));

    /// <summary>FSR frame generation under the game's DLSS switch; 0.9 brought the DLSSG input.</summary>
    internal static readonly FrameGenMod OptiScaler = new(
        "OptiScaler", "optiscaler", "OptiScaler", OptiScalerAsset(), 256L << 20, "OptiScaler.dll", "OptiScaler.ini",
        ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"],
        [
            new("OptiScaler.dll", ""),
            new("amd_fidelityfx_dx12.dll", @"OptiScaler\amd_fidelityfx_dx12.dll"),
            new("amd_fidelityfx_framegeneration_dx12.dll", @"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll"),
            new("amd_fidelityfx_upscaler_dx12.dll", @"OptiScaler\amd_fidelityfx_upscaler_dx12.dll", Optional: true),
            new("amd_fidelityfx_vk.dll", @"OptiScaler\amd_fidelityfx_vk.dll", Optional: true),
            new("amd_fidelityfx_framegeneration_vk.dll", @"OptiScaler\amd_fidelityfx_framegeneration_vk.dll", Optional: true),
            new("amd_fidelityfx_upscaler_vk.dll", @"OptiScaler\amd_fidelityfx_upscaler_vk.dll", Optional: true),
            new("dlssg_to_fsr3_amd_is_better.dll", @"OptiScaler\dlssg_to_fsr3_amd_is_better.dll"),
            new("fakenvapi.dll", @"OptiScaler\fakenvapi.dll", OffNvidia: true),
            new("fakenvapi.ini", @"OptiScaler\fakenvapi.ini", OffNvidia: true),
            new("libxess.dll", @"OptiScaler\libxess.dll", OffNvidia: true),
            new("libxess_dx11.dll", @"OptiScaler\libxess_dx11.dll", Optional: true),
            new("libxess_fg.dll", @"OptiScaler\libxess_fg.dll", Optional: true),
            new("libxell.dll", @"OptiScaler\libxell.dll", Optional: true),
            new("D3D12_Optiscaler/D3D12Core.dll", @"OptiScaler\D3D12_Optiscaler\D3D12Core.dll", Optional: true),
            new("Licenses/DirectX_LICENSE.txt", @"OptiScaler\Licenses\DirectX_LICENSE.txt", Optional: true),
            new("Licenses/FidelityFX_v1_LICENSE.md", @"OptiScaler\Licenses\FidelityFX_v1_LICENSE.md", Optional: true),
            new("Licenses/FidelityFX_v2_LICENSE.md", @"OptiScaler\Licenses\FidelityFX_v2_LICENSE.md", Optional: true),
            new("Licenses/XeSS_LICENSE.txt", @"OptiScaler\Licenses\XeSS_LICENSE.txt", OffNvidia: true, Optional: true),
        ],
        ["OptiScaler.log", "dlssg_to_fsr3.log", "fakenvapi.log"], [], "OptiScaler", new(0, 9, 0));

    /// <summary>
    /// DLSS frame generation on RTX 20 and 30 (github.com/sdli1995/dlssg_for_sm86). One proxy beside the game, the ini,
    /// and the dlssg_sm86 folder from the release. 0.4.0 is the layout Exo knows.
    /// </summary>
    internal static readonly FrameGenMod DlssgSm86 = new(
        "DLSSG SM86", "sdli1995", "dlssg_for_sm86", DlssgSm86Asset(), 160L << 20, "version.dll", "dlssg_sm86.ini",
        // The proxy exports version.dll's functions and no others: under any other name a game importing that DLL can't start.
        ["version.dll"],
        [new("version.dll", ""), new("dlssg_sm86.ini", "dlssg_sm86.ini")],
        [], [], "dlssg_sm86", new(0, 4, 0), "dlssg_sm86");

    /// <summary>Names an earlier Exo renamed DLSSG SM86's proxy to, read only to put it back (<see cref="RepairDlssLoader"/>).</summary>
    private static readonly string[] DlssgSm86RenamedTo = ["winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll"];

    internal static readonly FrameGenMod[] FrameGenMods = [MfgUnlocker, OptiScaler, DlssgSm86];

    /// <summary>Releases known not to work ("Repo@tag"): never installed or moved to. Empty today.</summary>
    internal static readonly HashSet<string> RefusedFrameGenReleases = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^MFGUnlocker-v\d+(?:\.\d+)*(?:-[\w.]+)?\.zip$")]
    private static partial Regex MfgAsset();

    [GeneratedRegex(@"^Optiscaler_\d[\w.\-]*\.7z$", RegexOptions.IgnoreCase)]
    private static partial Regex OptiScalerAsset();

    [GeneratedRegex(@"^dlssg-release-\d[\w.\-]*-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex DlssgSm86Asset();

    /// <summary>
    /// DLSSG SM86's version.dll and the shader recorder's d3d12.dll in one game can hang it before its window opens, at no CPU,
    /// every launch (Hell is Us; either one alone starts it). Frame generation is what the player asked for, so the recorder
    /// gives way. The recorder is active where its scskiller.ini sits beside the game's program.
    /// </summary>
    internal bool RecorderClashes(string gameId) =>
        FrameGenFolderFor(gameId) is { } folder && ReadFrameGenManifest(folder) is { Out: false } install && install.Mod == DlssgSm86.Name && File.Exists(Path.Combine(folder, "scskiller.ini"));

    private static FrameGenMod? FrameGenModNamed(string? name) =>
        FrameGenMods.FirstOrDefault(mod => mod.Name == name) ?? LegacyFrameGenMods.FirstOrDefault(mod => mod.Name == name);

    /// <summary>
    /// Exo Bridge: OptiScaler's fork that an Exo build of mid-November 2026 put into games ("Exo Bridge, a fork of OptiScaler",
    /// its loader OptiScaler.dll by its own name), whose code went with that build. Exo still knows it as its own, so its record
    /// isn't forgotten at launch (which left its files in the game for good): the next round takes it out by the record's file
    /// hashes (<see cref="FrameGenKeepCurrentAsync"/>) and Best settings puts today's mod in. Never installed or updated.
    /// </summary>
    internal static readonly FrameGenMod ExoBridgeLegacy = new(
        "Exo Bridge", "exo", "exo-bridge", NoReleaseAsset(), 0, "OptiScaler.dll", "OptiScaler.ini",
        ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"],
        [], ["OptiScaler.log", "dlssg_to_fsr3.log", "fakenvapi.log"], ["RTXMFG-", "RTX40MFG-"], "OptiScaler", new(0, 0, 0));

    internal static readonly FrameGenMod[] LegacyFrameGenMods = [ExoBridgeLegacy];

    private static bool IsLegacy(FrameGenMod mod) => LegacyFrameGenMods.Contains(mod);

    [GeneratedRegex(@"(?!)")]
    private static partial Regex NoReleaseAsset();

    // ------------------------------------------------------------------ the newest release

    /// <summary>A mod's release: its tag, the release file, its size and the SHA-256 the release publishes for it (lower case).</summary>
    internal sealed record FrameGenRelease(string Tag, string Asset, long Size, string Sha256);

    /// <summary>Swapped in tests: the mod's newest release, checked (<see cref="ParseLatestRelease"/>).</summary>
    internal Func<FrameGenMod, CancellationToken, Task<FrameGenRelease>> FrameGenLatest { get; init; } = LatestFrameGenAsync;

    /// <summary>Swapped in tests: why a mod can't be offered (<see cref="Unoffered"/>), or null.</summary>
    internal Func<FrameGenMod, string?> UnofferedReason { get; init; } = Unoffered;

    /// <summary>
    /// Swapped in tests: a folder holding the release's files by their release paths, checked. A release with no asset
    /// (a rollback) comes only from Exo's own copy.
    /// </summary>
    internal Func<FrameGenMod, FrameGenRelease, CancellationToken, Task<string>> FrameGenSource { get; init; } = FetchFrameGenAsync;

    /// <summary>Swapped in tests: a file's OriginalFilename, to know the mod's own loader.</summary>
    internal Func<string, string?> OriginalName { get; init; } = ReadOriginalName;

    /// <summary>Swapped in tests: the NVIDIA driver's frame caps; null where there is no NVIDIA driver to ask.</summary>
    internal IFrameGenDriver? FrameGenDriver { get; init; } = NvidiaFrameGenDriver.Instance;

    internal static Func<string, string?> FrameGenConsentLookup { get; set; } = _ => null;

    private static bool FrameGenRiskAccepted(GameEntry game, string? antiCheat, string? explicitConsent)
    {
        if (antiCheat is null) return true;
        if (string.Equals(explicitConsent, antiCheat, StringComparison.OrdinalIgnoreCase)) return true;
        var saved = FrameGenConsentLookup(game.Id);
        return string.Equals(saved, antiCheat, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The version a tag names: "v1.5.0-hotfix.1" is 1.5.0.</summary>
    internal static Version? TagVersion(string? tag)
    {
        var match = TagNumbers().Match(tag ?? "");
        return match.Success && Version.TryParse(match.Groups[1].Value.Contains('.') ? match.Groups[1].Value : match.Groups[1].Value + ".0", out var version) ? version : null;
    }

    [GeneratedRegex(@"^[vV]?(\d+(?:\.\d+){0,3})")]
    private static partial Regex TagNumbers();

    /// <summary>
    /// The releases Exo takes for each mod, by repo, pinned in code with the size and SHA-256 of the release file, as the
    /// upstream release states them (each the latest non-prerelease at 2026-11-09, downloaded and hashed by hand). The cache
    /// serves bytes only: a newer mod arrives with a Launcher release.
    /// </summary>
    internal static readonly (string Repo, FrameGenRelease Release)[] PinnedFrameGenReleases =
    [
        ("MFG-Unlocker", new("v1.7.2", "MFGUnlocker-v1.7.2.zip", 5334929, "e002d624e3e7ba5cedab7211043c096fe8f555c30ff43500235a0686c7893f22")),
        ("OptiScaler", new("v0.9.5", "Optiscaler_0.9.5-final.20261116.7z", 70418897, "77e3ed76bed04e1fb32bacb5a72799ff172f3da22ed5231c0cf3e84fe6761662")),
        ("dlssg_for_sm86", new("0.4.2", "dlssg-release-0.4.2-x64.zip", 89769758, "492bbd0c8429af27cf74dabcd51f733c56993d0e2ccf176c503c19ef090b2133")),
    ];

    /// <summary>The mod's newest pinned release, or why Exo takes none of them.</summary>
    internal static FrameGenRelease NewestPinnedRelease(FrameGenMod mod, IEnumerable<(string Repo, FrameGenRelease Release)> pins)
    {
        var candidates = pins.Where(pin => pin.Repo == mod.Repo).Select(pin => pin.Release).ToList();
        if (candidates.Count == 0)
            throw new InvalidDataException($"Exo has no pinned release of {mod.Name} yet; the next Launcher update brings one.");
        var release = candidates.MaxBy(candidate => TagVersion(candidate.Tag))!;
        if (TagVersion(release.Tag) is not { } version || version < mod.Minimum)
            throw new InvalidDataException($"{mod.Name} {release.Tag} is older than Exo takes.");
        if (RefusedFrameGenReleases.Contains(mod.Repo + "@" + release.Tag))
            throw new InvalidDataException($"{mod.Name} {release.Tag} is known not to work, so Exo waits for the next.");
        if (release.Size <= 0 || release.Size > mod.MaxSize)
            throw new InvalidDataException($"{mod.Name} {release.Tag} isn't the size its release can be.");
        if (VerifiedGitHubReleaseDownloader.ParseSha256Digest("sha256:" + release.Sha256) is null)
            throw new InvalidDataException($"{mod.Name} {release.Tag} has no valid pinned SHA-256.");
        return release;
    }

    /// <summary>
    /// Why a mod can't be offered, or null when it can. A mod's pin is its one switch: with no pinned release Exo has
    /// nothing to fetch, so it is never downloaded. DLSSG SM86 has none today (where its files come from is the owner's
    /// open decision), so an RTX 20 or 30 takes OptiScaler instead (<see cref="FrameGenGame.NoDlssgHere"/>); adding its row
    /// to <see cref="PinnedFrameGenReleases"/> offers it again, with nothing else to change.
    /// </summary>
    internal static string? Unoffered(FrameGenMod mod) => null;

    internal static string? Unoffered(FrameGenMod mod, IEnumerable<(string Repo, FrameGenRelease Release)> pins) =>
        pins.Any(pin => pin.Repo == mod.Repo) ? null : "Not available yet";

    /// <summary>
    /// The mod's newest release: GitHub's latest release of the mod's repo, when its asset matches the mod's file pattern, size
    /// limit and a published SHA-256 digest, and is not older than the newest pin. Otherwise (GitHub unreachable, rate limited, an
    /// asset with no digest) the newest pinned release, which Exo's cache holds.
    /// </summary>
    internal static async Task<FrameGenRelease> LatestFrameGenAsync(FrameGenMod mod, CancellationToken ct)
    {
        FrameGenRelease? pinned = null;
        try { pinned = NewestPinnedRelease(mod, PinnedFrameGenReleases); } catch (InvalidDataException) { }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var url = $"https://{UpstreamSource.GitHubApi}/repos/{mod.Owner}/{mod.Repo}/releases/latest";
            using var response = await UpstreamSource.GetAsync(Http, url, UpstreamSource.IsUpstreamHop, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = await ReadCappedTextAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), 1 << 20, timeout.Token).ConfigureAwait(false);
            if (text is not null && ParseUpstreamRelease(mod, JsonNode.Parse(text)) is { } latest &&
                (pinned is null || TagVersion(latest.Tag) >= TagVersion(pinned.Tag)))
                return latest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Info($"{mod.Name}: GitHub's newest release not read ({ex.Message}); using the one Exo pins.");
        }
        return pinned ?? throw new InvalidDataException($"{mod.Name} could not be reached and Exo pins no release of it.");
    }

    /// <summary>
    /// A GitHub release answer as a mod release Exo takes, or null: not a draft or prerelease, a tag at least the mod's minimum
    /// and not refused, and exactly one asset matching the mod's file pattern with a size within its limit and a sha256 digest.
    /// </summary>
    internal static FrameGenRelease? ParseUpstreamRelease(FrameGenMod mod, JsonNode? release)
    {
        if (release is not JsonObject node) return null;
        if (node["draft"]?.GetValue<bool>() == true || node["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = node["tag_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tag) || tag.Contains('/') || tag.Contains('\\') || TagVersion(tag) is not { } version || version < mod.Minimum) return null;
        if (RefusedFrameGenReleases.Contains(mod.Repo + "@" + tag)) return null;
        if (node["assets"] is not JsonArray assets) return null;
        var matches = assets.OfType<JsonObject>().Where(asset => asset["name"]?.GetValue<string>() is { } name && mod.Asset.IsMatch(name)).ToList();
        if (matches.Count != 1) return null;
        var picked = matches[0];
        var size = picked["size"]?.GetValue<long>() ?? 0;
        if (size <= 0 || size > mod.MaxSize) return null;
        if (VerifiedGitHubReleaseDownloader.ParseSha256Digest(picked["digest"]?.GetValue<string>()) is not { } digest) return null;
        return new FrameGenRelease(tag, picked["name"]!.GetValue<string>(), size, Convert.ToHexString(digest).ToLowerInvariant());
    }

    private static readonly SemaphoreSlim FrameGenFetch = new(1, 1);
    private static string FrameGenCacheFolder(FrameGenMod mod, string tag) => Path.Combine(PathHelper.AppDataDir, "frame-gen", mod.Repo, tag);
    internal const string VerifiedName = "verified.json";

    /// <summary>
    /// Exo's copy of the release when every file it recorded is there with its bytes; else the release file through
    /// <see cref="VerifiedGitHubReleaseDownloader"/> (its digest), its files taken out (only the ones Exo knows,
    /// never a path the archive names), checked (<see cref="CheckFrameGenFolder"/>) and recorded, the release file
    /// deleted, and only the three newest releases of the mod kept.
    /// </summary>
    internal static async Task<string> FetchFrameGenAsync(FrameGenMod mod, FrameGenRelease release, CancellationToken ct)
    {
        await FrameGenFetch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = FrameGenCacheFolder(mod, release.Tag);
            if (CachedFrameGen(folder)) return folder;
            if (release.Asset.Length == 0) throw new InvalidDataException($"Exo no longer has {mod.Name} {release.Tag}.");
            var parent = Path.GetDirectoryName(folder)!;
            Directory.CreateDirectory(parent);
            var archive = Path.Combine(parent, release.Asset);
            var unpack = folder + ".unpack-" + Guid.NewGuid().ToString("N");
            try
            {
                await VerifiedGitHubReleaseDownloader.Upstream.DownloadPinnedAsync(
                    new GitHubReleaseAsset(mod.Owner, mod.Repo, release.Tag, release.Asset, release.Size, release.Sha256), archive, IsFrameGenArchive, ct).ConfigureAwait(false);
                Directory.CreateDirectory(unpack);
                await UnpackFrameGenAsync(mod, archive, unpack, ct).ConfigureAwait(false);
                if (CheckFrameGenFolder(mod, unpack, ReadOriginalName, (path, name) => HasTrustedVendorSignature(path, name)) is { } bad)
                    throw new InvalidDataException($"{mod.Name} {release.Tag}: {bad}");
                var hashes = mod.Files.Select(spec => Path.Combine(unpack, spec.Source)).Where(File.Exists)
                    .ToDictionary(path => Path.GetRelativePath(unpack, path), path => FileSha256(path)!, StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(Path.Combine(unpack, VerifiedName), JsonSerializer.Serialize(hashes));
                if (Directory.Exists(folder)) DeletionGate.DeleteDirectory(folder, recursive: true);
                Directory.Move(unpack, folder);
                foreach (var old in Directory.EnumerateDirectories(parent).Where(dir => !dir.Contains(".unpack-", StringComparison.Ordinal))
                             .OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(3))
                    try { DeletionGate.DeleteDirectory(old, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Frame generation cache: " + ex.Message); }
            }
            finally
            {
                try { if (Directory.Exists(unpack)) DeletionGate.DeleteDirectory(unpack, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Frame generation unpack left: " + ex.Message); }
                TryDelete(archive);
            }
            return folder;
        }
        finally { FrameGenFetch.Release(); }
    }

    /// <summary>Every file Exo recorded for the release is there with the bytes it recorded.</summary>
    internal static bool CachedFrameGen(string folder)
    {
        try
        {
            var record = Path.Combine(folder, VerifiedName);
            if (!File.Exists(record) || JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(record)) is not { Count: > 0 } hashes) return false;
            return hashes.All(item => !Path.IsPathRooted(item.Key) && !item.Key.Contains("..", StringComparison.Ordinal) &&
                                      item.Value.Equals(FileSha256(Path.Combine(folder, item.Key)), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>A zip starts PK 03 04; a 7-Zip file '7z' BC AF 27 1C.</summary>
    private static bool IsFrameGenArchive(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[6];
            return stream.Read(head) == 6 && (head[..4].SequenceEqual((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]) ||
                                              head.SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]));
        }
        catch (IOException) { return false; }
    }

    /// <summary>Only the files the mod's layout names, each to the path Exo builds from that name.</summary>
    private static async Task UnpackFrameGenAsync(FrameGenMod mod, string archive, string unpack, CancellationToken ct)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var unpackRoot = Path.GetFullPath(unpack);
            if (!unpackRoot.EndsWith(Path.DirectorySeparatorChar)) unpackRoot += Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;
                var named = mod.Files.Any(spec => spec.Source.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!named && !PayloadMember(mod, name)) continue;
                if (entry.Length > mod.MaxSize) continue;
                var to = Path.GetFullPath(Path.Combine(unpackRoot, name));
                if (!to.StartsWith(unpackRoot, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                entry.ExtractToFile(to, overwrite: false);
            }
            return;
        }
        // Windows' tar (libarchive) reads 7-Zip; it is asked only for the names the archive lists that the layout knows.
        var listed = await TarAsync(["-tf", archive], ct).ConfigureAwait(false);
        var members = mod.Files.Select(spec => spec.Source)
            .Concat(listed.Where(name => PayloadMember(mod, name.Replace('\\', '/'))))
            .Where(name => listed.Contains(name, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (members.Count > 0) await TarAsync(["-xf", archive, "-C", unpack, .. members], ct).ConfigureAwait(false);
    }

    private static async Task<List<string>> TarAsync(string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var tar = Process.Start(info) ?? throw new InvalidOperationException("Windows' tar did not start.");
        var output = await tar.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await tar.WaitForExitAsync(ct).ConfigureAwait(false);
        if (tar.ExitCode != 0) throw new InvalidDataException("The release file didn't unpack.");
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// What is wrong with a release's files, or null: a file the layout needs missing, a DLL that isn't an x64 image,
    /// a loader that isn't the mod's own DLL, or an AMD or Intel library without its maker's signature.
    /// </summary>
    internal static string? CheckFrameGenFolder(FrameGenMod mod, string folder, Func<string, string?> originalName, Func<string, string, bool> vendorSigned)
    {
        foreach (var spec in mod.Files)
        {
            var path = Path.Combine(folder, spec.Source);
            if (!File.Exists(path))
            {
                if (spec.Optional) continue;
                return $"its release has no {spec.Source}.";
            }
            var name = Path.GetFileName(spec.Source);
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (PeMachine(path) != PeMachineAmd64) return $"{name} isn't a Windows x64 file.";
            if (spec.Target.Length == 0)
            {
                // DLSSG SM86's proxy carries no version info to compare; its release and the NVIDIA signature below vouch for it.
                var original = originalName(path);
                if (!(mod == DlssgSm86 && string.IsNullOrEmpty(original)) && !string.Equals(original, mod.Dll, StringComparison.OrdinalIgnoreCase))
                    return $"{name} isn't {mod.Name}'s own DLL.";
            }
            if (ExpectedVendor(name) is not null && !vendorSigned(path, name)) return $"{name} isn't signed by its maker.";
        }
        if (mod.Payload is { Length: > 0 } payload)
        {
            var runtime = Path.Combine(folder, payload, "runtime", "nvngx_dlssg.dll");
            if (!File.Exists(runtime)) return "its release has no dlssg_sm86 runtime.";
            if (!vendorSigned(runtime, "nvngx_dlssg.dll")) return "nvngx_dlssg.dll isn't signed by NVIDIA.";
        }
        return null;
    }

    /// <summary>A file inside the mod's payload folder, and nothing else the archive might name.</summary>
    private static bool PayloadMember(FrameGenMod mod, string name)
    {
        if (mod.Payload is not { Length: > 0 } payload) return false;
        var norm = name.Replace('\\', '/');
        if (norm.Contains("..", StringComparison.Ordinal) || norm.Contains(':') || norm.StartsWith('/')) return false;
        if (!norm.StartsWith(payload.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(norm);
        return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cubin", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ptx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The payload as it sits in Exo's copy of the release, relative to that copy.</summary>
    private static IEnumerable<(string Relative, string Full)> PayloadFiles(FrameGenMod mod, string source)
    {
        if (mod.Payload is not { Length: > 0 } payload) yield break;
        var root = Path.Combine(source, payload);
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (PayloadMember(mod, relative)) yield return (relative, file);
        }
    }

    /// <summary>DLSS on, preset A (the quality model), 2x: the release's own notes warn of edge artifacts at higher multipliers on a low frame rate. The release's other keys stay as it ships them.</summary>
    private static void TuneDlssIni(string settings, string from)
    {
        var text = File.Exists(settings) && File.ReadAllText(settings).Contains("[FrameGeneration]", StringComparison.Ordinal)
            ? File.ReadAllText(settings)
            : File.Exists(from) ? File.ReadAllText(from) : "";
        text = SetIniValue(text, "General", "Enabled", "1");
        text = SetIniValue(text, "FrameGeneration", "Engine", "DLSS");
        text = SetIniValue(text, "DLSSG", "Preset", "A");
        text = SetIniValue(text, "FrameGeneration", "MaxGeneratedFrames", "1");
        File.WriteAllText(settings, text);
    }

    /// <summary>
    /// What the DLSSG SM86 log says. <c>ran</c>: it created DLSS frame generation. <c>loaded</c>: the proxy loaded; at the
    /// mod's default log level that is all its log records, so it doesn't say frame generation stayed off. Null: no log.
    /// </summary>
    internal static string? DlssLogState(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;
        if (Regex.IsMatch(log, @"dlssg_create_calls\D+[1-9]", RegexOptions.CultureInvariant)) return "ran";
        return "loaded";
    }

    private static string? ReadOriginalName(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).OriginalFilename; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // ------------------------------------------------------------------ the card

    /// <param name="Vendor">nvidia, amd or intel.</param>
    /// <param name="Series">The GeForce series (9 for the GTX 900s, 10, 16, 20, 30, 40, 50); 0 for a Radeon or an Intel.</param>
    /// <param name="Class">
    /// What kind of card the name says it is: rtx, gtx16, gtx10, gtx900; radeon (RX 5000 and newer, Radeon PRO and AI PRO
    /// cards), polaris (RX 400 and 500), vega (RX Vega, Radeon VII), integrated (600M and newer, Strix, Z1, Z2, Ryzen
    /// AI); arc (A and B cards, laptops' A-series M included), arc-integrated (Arc Graphics, Arc 1x0V, Arc 1x0T), arc-other
    /// (any other Arc: a workstation Arc Pro).
    /// </param>
    internal sealed record FrameGenCard(string Vendor, int Series, string Class = "")
    {
        /// <summary>NVIDIA's own frame generation runs on it (RTX 40 and newer).</summary>
        internal bool DlssFrameGen => Vendor == "nvidia" && Series >= 40;
        /// <summary>NVIDIA's multi frame generation runs on it (RTX 50 and newer): nothing for a mod to add.</summary>
        internal bool MultiFrame => Vendor == "nvidia" && Series >= 50;
        /// <summary>DLSSG SM86 runs NVIDIA's own frame generation on it: an RTX 20 or 30.</summary>
        internal bool Dlssg => Vendor == "nvidia" && Series is 20 or 30;
    }

    /// <summary>
    /// The cards a mod brings frame generation to, by name: GeForce RTX 20 to 50 (workstation RTX by their generation)
    /// and GeForce GTX 900, 10 and 16; Radeon RX 400 and newer, RX Vega and Radeon VII, the 600M and newer integrated
    /// Radeons, Strix, Z1, Z2 and Ryzen AI graphics; Intel Arc. Null for anything else: older GeForce and MX chips, older
    /// Radeons and Vega graphics inside a processor, a Radeon named with no model ("AMD Radeon Graphics": as likely a
    /// two-unit desktop chip as a handheld's), Intel graphics before Arc.
    /// </summary>
    internal static FrameGenCard? FrameGenCardFor(string? gpu)
    {
        if (string.IsNullOrWhiteSpace(gpu)) return null;
        var name = gpu.ToUpperInvariant();
        if (name.Contains("NVIDIA", StringComparison.Ordinal) || name.Contains("GEFORCE", StringComparison.Ordinal) ||
            name.Contains("QUADRO", StringComparison.Ordinal) || name.Contains("RTX", StringComparison.Ordinal) || name.Contains("GTX", StringComparison.Ordinal))
        {
            var series = GpuProfile.NvidiaSeries(gpu) ?? (Gtx900().IsMatch(name) ? 9 : 0);
            return series switch
            {
                >= 20 => new("nvidia", series, "rtx"),
                16 => new("nvidia", 16, "gtx16"),
                10 => new("nvidia", 10, "gtx10"),
                9 => new("nvidia", 9, "gtx900"),
                _ => null,
            };
        }
        if (name.Contains("RADEON", StringComparison.Ordinal) || name.Contains("AMD", StringComparison.Ordinal))
        {
            if (FluidMotionFrames.Discrete().Match(name) is { Success: true } rx)
                return int.Parse(rx.Groups[1].Value, CultureInfo.InvariantCulture) >= 5000 ? new("amd", 0, "radeon") : null;
            if (Polaris().IsMatch(name)) return new("amd", 0, "polaris");
            if (VegaCard().IsMatch(name)) return new("amd", 0, "vega");
            if (FluidMotionFrames.Integrated().Match(name) is { Success: true } apu) return apu.Groups[1].Value[0] >= '6' ? new("amd", 0, "integrated") : null;
            if (FluidMotionFrames.Strix().IsMatch(name) || HandheldOrAi().IsMatch(name)) return new("amd", 0, "integrated");
            return name.Contains("AI PRO", StringComparison.Ordinal) ? new("amd", 0, "radeon") : null;
        }
        if (!name.Contains("INTEL", StringComparison.Ordinal) || !ArcName().IsMatch(name)) return null;
        return new("intel", 0, ArcCard().IsMatch(name) ? "arc" : ArcIntegrated().IsMatch(name) ? "arc-integrated" : "arc-other");
    }

    [GeneratedRegex(@"\bARC\b")]
    private static partial Regex ArcName();

    // "Arc A770", "Arc B580", a laptop's "Arc A770M": a card. "Arc Graphics", "Arc 140V" and "Arc 140T" are inside the processor.
    [GeneratedRegex(@"\bARC(?:\(TM\))?\s+[AB]\d{3}M?\b")]
    private static partial Regex ArcCard();

    // Meteor Lake's and Arrow Lake's "Arc Graphics", Lunar Lake's "Arc 130V" and "Arc 140V", Arrow Lake H's "Arc 130T" and "Arc 140T".
    [GeneratedRegex(@"\bARC(?:\(TM\))?\s+(?:GRAPHICS\b|1\d0[VT]\b)")]
    private static partial Regex ArcIntegrated();

    // "GTX 970", "GTX 980 Ti", "GTX 950": Maxwell. Not "GTX 750" (an older Maxwell without what frame generation needs).
    [GeneratedRegex(@"\bGTX\s*9[5-8]0(?!\d)")]
    private static partial Regex Gtx900();

    // "RX 580", "RX 480", "RX 570X", "RX 460", "RX 550": three digits, so never an "RX 5500" or an RTX 5080.
    [GeneratedRegex(@"\bRX\s*-?\s*[45][4-9]0X?(?![\dA-Z])")]
    private static partial Regex Polaris();

    // "RX Vega 64", "RX Vega 56", "Radeon VII": cards. "Radeon Vega 8 Graphics" and Raven Ridge's "Radeon RX Vega 11 Graphics" are inside a processor.
    [GeneratedRegex(@"\bRX\s+VEGA\b(?!\s+\d+\s+GRAPHICS\b)|\bRADEON\s+VII\b")]
    private static partial Regex VegaCard();

    // The handhelds' Ryzen Z1 and Z2 and Ryzen AI: Windows names their Radeon only "AMD Radeon Graphics", so Exo reads it
    // with the processor's name beside it (SystemSpecs.FrameGenName).
    [GeneratedRegex(@"\bZ[12]\b|\bRYZEN\s+AI\b")]
    private static partial Regex HandheldOrAi();

    // ------------------------------------------------------------------ the game

    /// <summary>One file Exo wrote under the game's program folder: its size, SHA-256 (null for the settings file, which the mod's own menu rewrites) and when it was written.</summary>
    internal sealed record FrameGenFile(string Path, long Size, string? Sha256, DateTime Written);

    /// <summary>The driver's frame cap Exo moved into OptiScaler: the cap, and the game's own profile value before (null: none of its own).</summary>
    internal sealed record FrameGenCap(uint Fps, uint? Before, string? Profile);

    /// <summary>Exo's record of its install, beside the game's program.</summary>
    /// <param name="Release">The release's tag now in the game.</param>
    /// <param name="Input">The input OptiScaler reads (dlssg or nukems); dlss for DLSSG SM86.</param>
    /// <param name="Previous">The release before an update, kept in Exo's copy to go back to.</param>
    /// <param name="Proven">A session ran on this release since it went in.</param>
    /// <param name="Refused">Releases that failed in this game, never moved to again.</param>
    /// <param name="Session">DLSSG SM86's last session, when its log shows it: ran.</param>
    /// <param name="Auto">Best settings put it in, not the player (<see cref="FrameGenEverywhereAsync"/>).</param>
    /// <param name="ExeSize">The game's program as it was when the mod went in: a patch that changes it takes the mod out.</param>
    /// <param name="Out">
    /// Exo took its mod out, but another mod's file is over Exo's loader (a mod manager keeps Exo's as its backup), so the
    /// record stays with that one file: once it is gone, or Exo's own bytes are back under the name, Exo's goes and so does
    /// the record (<see cref="FrameGenOff"/>).
    /// </param>
    internal sealed record FrameGenInstall(
        string Mod, string Release, string Loader, string Input, string Exe, List<FrameGenFile> Files, FrameGenCap? Cap = null,
        string? Previous = null, bool Proven = true, List<string>? Refused = null, string? Session = null,
        bool Auto = false, long? ExeSize = null, DateTime? ExeWritten = null, bool Out = false);

    /// <summary>What Exo knows about a game for frame generation, from one walk of its install and the upscaler scan.</summary>
    /// <param name="Folder">The folder of the game's own program, where the mod goes (or went).</param>
    /// <param name="DlssFrameGen">It ships NVIDIA's frame generation (not a mod's copy).</param>
    /// <param name="DlssgMajor">The major version of that runtime (310 for DLSS 4).</param>
    /// <param name="OwnFrameGen">It ships FSR or XeSS frame generation of its own.</param>
    /// <param name="Streamline">sl.interposer.dll's major version, or null when the game has none Exo can read.</param>
    /// <param name="Vulkan">Its program loads Vulkan and not Direct3D 12.</param>
    /// <param name="OtherMod">A mod Exo didn't put in, also beside Exo's own (the one that replaced Exo's loader).</param>
    /// <param name="NoDlssgHere">
    /// DLSSG SM86 can't be this game's: Exo doesn't offer it (<see cref="Unoffered(FrameGenMod)"/>), it failed its first
    /// session here (<see cref="FrameGenMemory.DlssFailed"/>), or a file Exo didn't write has its loader's name.
    /// </param>
    /// <param name="DlssgOnDisk">An nvngx_dlssg.dll is somewhere in the install outside Exo's mod folder, whatever the scan says.</param>
    internal sealed record FrameGenGame(
        string? Folder, string? Exe, bool DlssFrameGen, int? DlssgMajor, bool OwnFrameGen, int? Streamline, bool Vulkan,
        string? OtherMod, string? AntiCheat, FrameGenInstall? Installed, bool NoDlssgHere = false, bool DlssgOnDisk = true,
        bool HasUpscaler = false, string Api = "dx12", bool NoMfgHere = false, bool VersionDllTaken = false, bool DlssFailed = false)
    {
        internal bool DlssgGone => !DlssFrameGen && !DlssgOnDisk;
    }

    internal static string? FrameGenPath(FrameGenCard? card, FrameGenGame game) => FrameGenPath(card, game, null);

    internal static string? FrameGenPath(FrameGenCard? card, FrameGenGame game, GameEntry? entry)
    {
        if (card is null)
            return game.OwnFrameGen ? "native" : "optifg";
        if (card.DlssFrameGen)
        {
            if (game.DlssFrameGen || game.OwnFrameGen) return "native";
            return "optifg";
        }
        if (card.Dlssg)
        {
            if (game.OwnFrameGen && (game.AntiCheat is not null || (entry is not null && GameSettings.IsWutheringWavesEntry(entry))))
                return "native";
            if (game.DlssFrameGen)
            {
                if (!game.Vulkan && game.Api != "dx11" && game.Streamline is >= 2 && game.DlssgMajor >= 310 && !game.NoDlssgHere && !game.VersionDllTaken)
                    return "dlss";
                if (!game.DlssFailed && !game.NoMfgHere && (game.Vulkan || game.Streamline is null or < 2 || game.DlssgMajor is null or < 310 || game.VersionDllTaken))
                    return "mfg";
                if (game.OwnFrameGen) return "native";
                return "fsr";
            }
            if (game.OwnFrameGen) return "native";
            return "optifg";
        }
        if (game.OwnFrameGen) return "native";
        if (game.DlssFrameGen) return "fsr";
        return "optifg";
    }

    internal static string? FrameGenModFallbackPath(FrameGenCard? card, FrameGenGame game)
    {
        if (card is { DlssFrameGen: true })
            return game.DlssFrameGen ? null : "optifg";
        if (card is { Dlssg: true } && game.DlssFrameGen)
        {
            if (!game.Vulkan && game.Api != "dx11" && game.Streamline is >= 2 && game.DlssgMajor >= 310 && !game.NoDlssgHere && !game.VersionDllTaken)
                return "dlss";
            if (!game.DlssFailed && !game.NoMfgHere)
                return "mfg";
            return "fsr";
        }
        if (game.DlssFrameGen) return "fsr";
        return "optifg";
    }

    internal static string? FrameGenPackageLabel(string? path, string? modName = null) => path switch
    {
        "dlss" => DlssgSm86.Name,
        "mfg" => MfgUnlocker.Name,
        "fsr" => OptiScaler.Name,
        "optifg" => "OptiScaler FG",
        _ => modName,
    };

    internal static FrameGenMod? FrameGenModForPath(string? path) => path switch
    {
        "dlss" => DlssgSm86,
        "mfg" => MfgUnlocker,
        "fsr" or "optifg" => OptiScaler,
        _ => null,
    };

    private sealed class FrameGenFound
    {
        public List<(string Path, long Size)> Exes { get; } = [];
        public List<string> Interposers { get; } = [];
        public List<string> Manifests { get; } = [];
        public List<string> Dlssgs { get; } = [];
        public List<string> Upscalers { get; } = [];
        public bool HasD3d12Core { get; set; }
    }

    internal static string FrameGenInput(FrameGenGame game) => game.Vulkan || game.Streamline is not >= 2 ? "nukems" : "dlssg";

    internal static string FrameGenInputFor(FrameGenGame game, string? path) => path switch
    {
        "dlss" => "dlss",
        "mfg" => "mfg",
        "optifg" => "upscaler",
        _ => FrameGenInput(game),
    };

    internal static string? FindGameProgram(string? gamePath)
    {
        var root = ResolveInstallRoot(gamePath);
        if (root is null || IsWindowsAppsPath(root)) return null;
        var found = new FrameGenFound();
        try { FrameGenWalk(root, found, 0, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return GameProgram(found.Exes, found.Dlssgs.Select(path => Path.GetDirectoryName(path)!));
    }

    private static readonly string[] TemporalUpscalerFiles =
    [
        "nvngx_dlss.dll",
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_vk.dll",
        "amd_fidelityfx_upscaler_vk.dll",
        "libxess.dll",
        "libxess_dx11.dll",
    ];

    internal FrameGenGame ReadFrameGenGame(GameEntry game, string root, ScanResult? scan, CancellationToken ct)
    {
        var found = new FrameGenFound();
        FrameGenWalk(root, found, 0, ct);
        FrameGenInstall? installed = null;
        string? installedFolder = null;
        foreach (var manifest in found.Manifests)
        {
            var folder = Path.GetDirectoryName(manifest)!;
            if (ReadFrameGenManifest(folder) is not { } read) continue;
            installed = read;
            installedFolder = folder;
            break;
        }
        var rows = scan?.Files ?? [];
        var dlssg = rows.Where(dll => OwnFile(dll, "nvngx_dlssg.dll")).ToList();
        var exe = installed is not null ? Path.Combine(installedFolder!, installed.Exe) : GameProgram(found.Exes, dlssg.Select(dll => Path.GetDirectoryName(dll.Path)!));
        var programFolder = installedFolder ?? (exe is null ? null : Path.GetDirectoryName(exe));
        var imports = exe is not null ? ReadPeImportNames(exe) : null;
        var hasDx12 = found.HasD3d12Core ||
            (imports is not null && imports.Contains("d3d12.dll")) ||
            rows.Any(dll => OwnFile(dll, "amd_fidelityfx_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll"));
        var hasVulkan = (imports is not null && imports.Contains("vulkan-1.dll")) ||
            rows.Any(dll => OwnFile(dll, "amd_fidelityfx_vk.dll", "amd_fidelityfx_upscaler_vk.dll", "amd_fidelityfx_framegeneration_vk.dll"));
        var hasDx11 = imports is not null && imports.Contains("d3d11.dll");
        var vulkan = hasVulkan && !hasDx12;
        var api = hasDx12 ? "dx12" : hasVulkan ? "vulkan" : hasDx11 ? "dx11" : "unknown";
        var ours = FrameGenModNamed(installed?.Mod);
        var recorded = (installed?.Files ?? []).Select(file => Path.GetFullPath(Path.Combine(installedFolder!, file.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownFolder = ours?.Folder is { } modFolder && installedFolder is not null ? Path.Combine(installedFolder, modFolder) + Path.DirectorySeparatorChar : null;
        bool Exos(string? mod) => ours is not null && (mod == ours.Name || ours == OptiScaler && mod == "dlssg-to-fsr3");
        bool InExos(string path) => recorded.Contains(Path.GetFullPath(path)) || ownFolder is not null && path.StartsWith(ownFolder, StringComparison.OrdinalIgnoreCase);
        var other = rows.Where(dll => dll.Mod is not null && !Exos(dll.Mod) && !InExos(dll.Path)).Select(dll => dll.Mod).FirstOrDefault();
        if (other is null && programFolder is not null)
        {
            var marked = MarkersIn(programFolder, new(StringComparer.OrdinalIgnoreCase)).Where(mod => !Exos(mod)).ToList();
            other = marked.Count > 0 ? MostSpecific(marked)
                : FrameGenMods.FirstOrDefault(mod => mod != ours && FrameGenLeftovers(programFolder, mod).Any())?.Name ?? LoaderModIn(programFolder);
        }
        var dlssgName = Path.Combine(programFolder ?? root, DlssgSm86.Loaders[0]);
        var versionTaken = File.Exists(dlssgName) && !recorded.Contains(Path.GetFullPath(dlssgName));
        var memory = FrameGenMemoryFor(game.Id);
        var noDlssg = UnofferedReason(DlssgSm86) is not null || memory.DlssFailed || versionTaken;
        var noMfg = UnofferedReason(MfgUnlocker) is not null || memory.MfgFailed;
        var hasUpscaler = rows.Any(dll => OwnFile(dll, TemporalUpscalerFiles)) || found.Upscalers.Any(path => !InExos(path));
        return new FrameGenGame(
            programFolder,
            exe,
            dlssg.Count > 0,
            dlssg.Select(dll => TryParseVersion(dll.CurrentVersion)?.Major).Max(),
            rows.Any(dll => OwnFile(dll, OwnFrameGenFiles)) || GameSettings.IsWutheringWaves(game),
            found.Interposers.Select(path => TryParseVersion(FileVersion(path))?.Major).Max(),
            vulkan,
            other,
            AntiCheatCheck.FromScan(game, scan?.AntiCheat).Label,
            installed,
            noDlssg,
            found.Dlssgs.Any(path => !InExos(path)),
            hasUpscaler,
            api,
            noMfg,
            versionTaken,
            memory.DlssFailed);
    }

    private static bool OwnFile(DetectedDll dll, params string[] names) => dll.Mod is null && names.Contains(dll.FileName, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] OwnFrameGenFiles = ["amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_framegeneration_vk.dll", "libxess_fg.dll"];

    /// <summary>
    /// The game ships frame generation of its own that an RTX 40 or newer runs: NVIDIA's (nvngx_dlssg.dll), FSR's or
    /// XeSS's, not a mod's copy. Read from the upscaler scan alone, so an RTX 50 is never walked for it.
    /// </summary>
    internal static bool ShipsFrameGen(IReadOnlyList<DetectedDll> rows) =>
        rows.Any(dll => OwnFile(dll, "nvngx_dlssg.dll") || OwnFile(dll, OwnFrameGenFiles));

    /// <summary>What a mod writes beside its loader as it runs, there now.</summary>
    private static IEnumerable<string> FrameGenLeftovers(string folder, FrameGenMod mod)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return files.Where(path => Path.GetFileName(path) is var name &&
            (mod.Logs.Contains(name, StringComparer.OrdinalIgnoreCase) || mod.LogPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>The game's programs (not a launcher, helper or anti-cheat), Streamline's interposers and Exo's records, one walk.</summary>
    private static void FrameGenWalk(string dir, FrameGenFound found, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 16) return;
        List<FileInfo> files;
        try { files = new DirectoryInfo(dir).EnumerateFiles().ToList(); }
        catch (Exception) when (depth > 0) { return; }
        foreach (var file in files)
        {
            if (file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && GpuPreference.IsGameProgram(file.Name) &&
                !NotTheGame.Any(word => file.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                found.Exes.Add((file.FullName, file.Length));
            else if (file.Name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase)) found.Interposers.Add(file.FullName);
            else if (file.Name.Equals(FrameGenManifestName, StringComparison.OrdinalIgnoreCase)) found.Manifests.Add(file.FullName);
            else if (file.Name.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase)) found.Dlssgs.Add(file.FullName);
            else if (file.Name.Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase)) found.HasD3d12Core = true;
            else if (file.Name.StartsWith("ffx_fsr2", StringComparison.OrdinalIgnoreCase) ||
                     file.Name.StartsWith("ffx_fsr3upscaler", StringComparison.OrdinalIgnoreCase))
                found.Upscalers.Add(file.FullName);
        }
        List<DirectoryInfo> children;
        try { children = new DirectoryInfo(dir).EnumerateDirectories().ToList(); }
        catch (Exception) when (depth > 0) { return; }
        foreach (var child in children)
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0 || SkipScanFolderNames.Contains(child.Name) || AntiCheatForFolder(child.Name) is not null) continue;
            FrameGenWalk(child.FullName, found, depth + 1, ct);
        }
    }

    /// <summary>Programs in a game's folder that aren't the game: its launchers, web helpers and overlays.</summary>
    private static readonly string[] NotTheGame = ["launcher", "helper", "cefprocess", "browser", "overlay"];

    /// <summary>
    /// The game's own program, where the mod goes: a 64-bit program, beside NVIDIA's frame generation where one is
    /// (Cyberpunk 2077's bin\x64), else the largest (an Unreal game's Project\Binaries\Win64\*-Shipping.exe, never its
    /// small launcher at the top, nor Phantom Blade Zero's 1 MB Steam stub beside its 511 MB game).
    /// </summary>
    internal static string? GameProgram(IReadOnlyList<(string Path, long Size)> exes, IEnumerable<string> dlssgFolders)
    {
        var x64 = exes.Where(exe => PeMachine(exe.Path) == PeMachineAmd64).ToList();
        var folders = dlssgFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var beside = x64.Where(exe => folders.Contains(Path.GetDirectoryName(exe.Path)!)).ToList();
        return (beside.Count > 0 ? beside : x64).OrderByDescending(exe => exe.Size).ThenBy(exe => exe.Path.Length).Select(exe => exe.Path).FirstOrDefault();
    }

    /// <summary>Exo's record in the game's program folder, or null when there is none or it names anything Exo wouldn't write.</summary>
    internal static FrameGenInstall? ReadFrameGenManifest(string folder)
    {
        var path = Path.Combine(folder, FrameGenManifestName);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            var install = JsonSerializer.Deserialize<FrameGenInstall>(File.ReadAllText(path));
            if (install is null || FrameGenModNamed(install.Mod) is not { } mod || !(mod == DlssgSm86 ? DlssgSm86RenamedTo.Append(mod.Loaders[0]) : mod.Loaders).Contains(install.Loader, StringComparer.OrdinalIgnoreCase) ||
                install.Exe != Path.GetFileName(install.Exe) || !install.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || TagVersion(install.Release) is null)
                return null;
            return install.Files.All(file => FrameGenPathAllowed(file.Path, install.Loader, mod)) ? install : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Exo writes only the mod's loader, its settings file, and files inside its own folder.</summary>
    private static bool FrameGenPathAllowed(string? path, string loader, FrameGenMod mod)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split('\\', '/').Any(part => part is "" or "." or "..")) return false;
        return path.Equals(loader, StringComparison.OrdinalIgnoreCase) || path.Equals(mod.Settings, StringComparison.OrdinalIgnoreCase) ||
               mod.Folder is { } own && path.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static void SaveFrameGenManifest(string folder, FrameGenInstall install)
    {
        var path = Path.Combine(folder, FrameGenManifestName);
        var temp = path + NewSuffix;
        File.WriteAllText(temp, JsonSerializer.Serialize(install));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// What happened to Exo's files since they went in, or null when every one is there as written (the same size and
    /// time, or the same bytes). <c>replaced</c>: the loader is there with bytes that aren't Exo's (another mod's loader
    /// under the name, as a mod manager deploys it); this wins, so the other mod is left alone. <c>changed</c>: another of
    /// Exo's files has other bytes (a newer AMD or Intel library the player dropped in). <c>removed</c>: a file is gone
    /// (antivirus, a store's verify, the player). A file Exo can't read now says nothing: it is checked again next time.
    /// </summary>
    internal static (string Reason, string File)? FrameGenDamage(string folder, FrameGenInstall install)
    {
        (string, string)? changed = null, removed = null;
        foreach (var file in install.Files)
        {
            var path = Path.Combine(folder, file.Path);
            try
            {
                if (!File.Exists(path))
                {
                    removed ??= ("removed", file.Path);
                    continue;
                }
                var isLoader = file.Path.Equals(install.Loader, StringComparison.OrdinalIgnoreCase);
                // A mod manager's copy under Exo's loader's name, Exo's own kept as its backup: the manager's now, whatever its bytes.
                if (isLoader && ManagerBackedUp(path)) return ("replaced", file.Path);
                if (file.Sha256 is null) continue;
                var info = new FileInfo(path);
                if (info.Length == file.Size && info.LastWriteTimeUtc == file.Written) continue;
                if (FileSha256(path) is not { } sha || file.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase)) continue;
                if (isLoader) return ("replaced", file.Path);
                changed ??= ("changed", file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* held by something: no verdict now */ }
        }
        return changed ?? removed;
    }

    /// <summary>Backups a mod manager keeps of the file it deployed over (Vortex: name.vortex_backup).</summary>
    private static readonly string[] ManagerBackupSuffixes = [".vortex_backup"];

    /// <summary>A mod manager deployed this file and keeps the one it went over as its backup: the file is the manager's.</summary>
    private static bool ManagerBackedUp(string path) => ManagerBackupSuffixes.Any(suffix => File.Exists(path + suffix));

    // ------------------------------------------------------------------ the line

    /// <summary>
    /// What the game page's Frame generation line shows. <c>hidden</c>: nothing; <c>native</c>: the game's own FSR or
    /// XeSS frame generation is the one for this card; <c>off</c> and <c>on</c>: AMD FidelityFX through OptiScaler,
    /// or DLSS; <c>broken</c>: Exo's install is damaged, or the game is kept off for that, and <see cref="Reason"/> says
    /// how; <c>mod</c>: another mod is in the game; <c>note</c>: Exo just took it out for a reason this card no longer
    /// shows the line for.
    /// </summary>
    /// <param name="Version">The mod and release in the game: "DLSSG SM86 0.4.1".</param>
    /// <param name="Session">The last session (see <see cref="FrameGenInstall.Session"/>).</param>
    /// <param name="Mod">The other mod in the game; for <c>replaced</c>, the one whose file is over Exo's loader, when that file says so (<see cref="ReplacedBy"/>).</param>
    /// <param name="Note">Why Exo took it out, or went back to the release before, by itself; or why it can't go in (anti-cheat).</param>
    /// <param name="Reason">
    /// Why it is broken (<see cref="FrameGenDamage"/>): <c>replaced</c> (another mod's file is where Exo's loader was; the
    /// game stays off), <c>changed</c> (another of Exo's files has other bytes; Exo leaves it), <c>removed</c> (Exo's files
    /// went; they go back once), <c>gone</c> (the game no longer ships DLSS Frame Generation; Exo takes its files out).
    /// </param>
    public sealed record FrameGenStatus(
        string State, string? Path = null, string? Version = null,
        string? Session = null, string? Mod = null, string? AntiCheat = null, string? Note = null, string? Reason = null,
        string? Package = null, bool CanToggle = false, string? RiskWarning = null, bool RiskAccepted = false);

    private static readonly ConcurrentDictionary<string, string> FrameGenNotes = new(StringComparer.OrdinalIgnoreCase);

    internal const string DlssgGoneNote = "This game no longer has DLSS Frame Generation";

    public FrameGenStatus FrameGenState(GameEntry game, string? gpu, CancellationToken ct) => FrameGenRead(game, gpu, ct).Status;

    internal FrameGenVerdict FrameGenRead(GameEntry game, string? gpu, CancellationToken ct)
    {
        var root = ResolveInstallRoot(game.Path);
        if (root is null || IsWindowsAppsPath(root)) return FrameGenDecide(new(Readable: false, Unread: "unread"));
        if (AntiCheatCheck.ChoseWithoutAntiCheat(game) && SessionHeldFrameGen(game.Id) is { } held)
        {
            var heldPath = held.Mod == OptiScaler.Name ? (held.Input == "upscaler" ? "optifg" : "fsr") : held.Mod == MfgUnlocker.Name ? "mfg" : "dlss";
            return new(new("on", Path: heldPath, Version: $"{held.Mod} {held.Release}",
                Note: $"{held.Mod} {held.Release}, in the game only while you play it from Exo",
                Package: FrameGenPackageLabel(heldPath, held.Mod), CanToggle: true));
        }
        var card = FrameGenCardFor(gpu);
        FrameGenNotes.TryRemove(game.Id, out var note);
        for (var pass = 0; ; pass++)
        {
            FrameGenGame layout;
            try { layout = ReadFrameGenGame(game, root, CachedScan(game, ct), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return FrameGenDecide(new(Readable: false, Note: note, Unread: "unread")); }
            var facts = FrameGenFactsFor(game, card, layout, note);
            var verdict = FrameGenDecide(facts);
            switch (verdict.Next)
            {
                case FrameGenNext.FinishOff:
                    if (pass > 1 || !FrameGenOff(game).Ok) return FrameGenDecide(facts with { Pending = true });
                    continue;
                case FrameGenNext.TakeOut:
                    if (pass > 1 || !FrameGenOff(game).Ok) return FrameGenDecide(facts with { Pending = true });
                    AppLog.Info($"Frame generation taken out: gameId={game.Id}; {facts.TakeOut}");
                    if (facts.Wanted is "dlss" or "mfg") FrameGenEverywhereSoon();
                    note = facts.TakeOut;
                    continue;
            }
            switch (verdict.Next)
            {
                case FrameGenNext.KeepOff: KeepFrameGenOff(game.Id, "replaced", layout.Installed!.Loader); break;
                case FrameGenNext.Forget: ForgetReplaced(game.Id); break;
            }
            return Named(verdict, layout);
        }
    }

    private FrameGenVerdict Named(FrameGenVerdict verdict, FrameGenGame layout)
    {
        if (layout.Installed is not { } install || verdict.Status.State is not ("on" or "broken") || verdict.Status.Path is null) return verdict;
        var status = verdict.Status;
        if (status.Reason == "replaced") status = status with { Mod = ReplacedBy(layout.Folder!, install.Loader) };
        if (!install.Out) status = status with { Version = $"{install.Mod} {install.Release}", Session = install.Session };
        return verdict with { Status = status };
    }

    private FrameGenFacts FrameGenFactsFor(GameEntry game, FrameGenCard? card, FrameGenGame layout, string? note)
    {
        var wanted = FrameGenPath(card, layout, game);
        var native = card is { DlssFrameGen: true } && (layout.DlssFrameGen || layout.OwnFrameGen) || (wanted == "native");
        var antiCheat = layout.AntiCheat;
        var riskAccepted = antiCheat is not null && FrameGenRiskAccepted(game, antiCheat, null);
        var riskWarning = antiCheat is null ? null : FrameGenAntiCheat.ConfirmationWarning(game, antiCheat);
        var fallbackPath = wanted == "native" ? FrameGenModFallbackPath(card, layout) : null;
        var facts = new FrameGenFacts(true, wanted, Note: note, Native: native, AntiCheat: antiCheat,
            RiskWarning: riskWarning, RiskAccepted: riskAccepted, FallbackPath: fallbackPath);
        if (layout.Installed is { } install && FrameGenModNamed(install.Mod) is { } mod)
        {
            var damage = FrameGenDamage(layout.Folder!, install)?.Reason;
            return facts with
            {
                Record = mod.Name, RecordInput = install.Input, Out = install.Out, Damage = damage,
                DlssgGone = install.Input != "upscaler" && layout.DlssgGone,
                TakeOut = install.Out ? null : FrameGenTakeOut(card, mod, antiCheat, layout.Folder!, install, wanted, layout.DlssgGone, riskAccepted),
            };
        }
        if (wanted is null or "native" || layout.Folder is null) return facts with { Folder = layout.Folder is not null };
        if (layout.OtherMod is { } other) return facts with { OtherMod = other };
        var targetMod = FrameGenModForPath(wanted);
        return facts with
        {
            NeverAllowed = antiCheat is not null && NeverAllowed(game, antiCheat),
            KeptOff = FrameGenKeptOffHere(game.Id, layout.Folder),
            Unoffered = targetMod is null ? null : UnofferedReason(targetMod),
        };
    }

    /// <summary>What happens next for a game (<see cref="FrameGenDecide"/>).</summary>
    internal enum FrameGenNext
    {
        /// <summary>Nothing: the line shows, and Best settings leaves the game as it is.</summary>
        Nothing,
        /// <summary>Exo takes its mod out (<see cref="FrameGenOff"/>) and reads the game again; the reason is the line's note.</summary>
        TakeOut,
        /// <summary>Only Exo's record of its loader is left and the other mod's file over it went: Off finishes, and the game is read again.</summary>
        FinishOff,
        /// <summary>Another mod's file is over Exo's loader: remembered, so Best settings never puts Exo's in beside it.</summary>
        KeepOff,
        /// <summary>Exo's install is whole: no longer kept off for another mod's file.</summary>
        Forget,
        /// <summary>Exo's files went: Best settings takes the rest out and puts them back, once (<see cref="PutRemovedBack"/>).</summary>
        PutBack,
        /// <summary>Best settings puts the mod in (<see cref="FrameGenOnAsync"/>).</summary>
        PutIn,
    }

    /// <summary>What the table reads about one game.</summary>
    /// <param name="Readable">Exo read the game. False: no install, a Store app, a card no mod adds to with nothing of Exo's there, or a folder Exo can't read.</param>
    /// <param name="Wanted">The route for this card and game (<see cref="FrameGenPath"/>).</param>
    /// <param name="Record">The mod Exo's record in the game names, or null for none.</param>
    /// <param name="Out">The record is only Exo's loader left under another mod's file (<see cref="FrameGenInstall.Out"/>).</param>
    /// <param name="Damage">What happened to Exo's files (<see cref="FrameGenDamage"/>): replaced, changed, removed, or null.</param>
    /// <param name="TakeOut">Why Exo's mod can't stay (<see cref="FrameGenTakeOut"/>), or null.</param>
    /// <param name="DlssgGone">The game no longer ships DLSS Frame Generation.</param>
    /// <param name="Folder">Exo found the game's program folder.</param>
    /// <param name="OtherMod">Another mod in the game (<see cref="FrameGenGame.OtherMod"/>).</param>
    /// <param name="AntiCheat">The game's anti-cheat label (<see cref="AntiCheatCheck.Answer.Label"/>: found, possibly Valve Anti-Cheat or why it is unknown); <paramref name="NeverAllowed"/>: Riot's, Vanguard's or an install Exo couldn't read.</param>
    /// <param name="AntiCheatSentence">The line's one sentence for it, from <see cref="AntiCheatCheck.Sentence"/>.</param>
    /// <param name="KeptOff">Why Exo's memory keeps the game off (<see cref="FrameGenMemory.KeptOff"/>).</param>
    /// <param name="Unoffered">Why the route's mod can't be offered (<see cref="Unoffered(FrameGenMod)"/>).</param>
    /// <param name="Note">What Exo did by itself, shown once.</param>
    /// <param name="Pending">Exo's Off for this record couldn't run or didn't finish (the game is running, a file stayed): it comes out next time.</param>
    /// <param name="Native">No mod route, but the game ships frame generation this card runs (an RTX 40 or 50 with the game's DLSS, FSR or XeSS frame generation).</param>
    /// <param name="Unread">Why the game wasn't read: <c>unread</c> (no install, a Store app, a folder Exo can't read) or <c>card</c> (a card no mod adds to).</param>
    internal sealed record FrameGenFacts(
        bool Readable, string? Wanted = null, string? Record = null, bool Out = false, string? Damage = null, string? TakeOut = null,
        bool DlssgGone = false, bool Folder = true, string? OtherMod = null, string? AntiCheat = null, bool NeverAllowed = false,
        string? KeptOff = null, string? Unoffered = null, string? Note = null, bool Pending = false, string? AntiCheatSentence = null,
        bool Native = false, string? Unread = null, string? RecordInput = null, string? RiskWarning = null, bool RiskAccepted = false,
        string? FallbackPath = null);

    internal sealed record FrameGenVerdict(FrameGenStatus Status, FrameGenNext Next = FrameGenNext.Nothing);

    internal static FrameGenVerdict FrameGenDecide(FrameGenFacts f)
    {
        var hidden = new FrameGenVerdict(new(f.Note is null ? "hidden" : "note", Note: f.Note));
        if (!f.Readable) return f.Note is null ? new(new("hidden", Reason: f.Unread)) : hidden;
        if (f.Record is { } record)
        {
            var path = record == OptiScaler.Name ? (f.RecordInput == "upscaler" ? "optifg" : "fsr") : record == MfgUnlocker.Name ? "mfg" : "dlss";
            var pkg = FrameGenPackageLabel(path, record);
            if (f.Pending) return new(new("note", Path: path, Note: Coming(f.TakeOut ?? f.Note), Package: pkg));
            if (f.Out) return new(new("broken", Path: path, Note: f.Note, Reason: "replaced", Package: pkg), f.Damage == "replaced" ? FrameGenNext.Nothing : FrameGenNext.FinishOff);
            if (f.TakeOut is not null) return new(new("on", Path: path, Note: f.Note, Package: pkg, CanToggle: true, AntiCheat: f.AntiCheat, RiskWarning: f.RiskWarning, RiskAccepted: f.RiskAccepted), FrameGenNext.TakeOut);
            var reason = f.Damage ?? (f.DlssgGone ? "gone" : null);
            var next = reason switch { "replaced" => FrameGenNext.KeepOff, "removed" => FrameGenNext.PutBack, null => FrameGenNext.Forget, _ => FrameGenNext.Nothing };
            return new(new(reason is null ? "on" : "broken", Path: path, Note: f.Note, Reason: reason, Package: pkg, CanToggle: reason is null, AntiCheat: f.AntiCheat, RiskWarning: f.RiskWarning, RiskAccepted: f.RiskAccepted), next);
        }
        if (f.Wanted is null)
            return f.Native && f.Note is null ? new(new("native")) : hidden;
        if (f.Wanted == "native")
        {
            if (f.Note is not null) return hidden;
            if (f.FallbackPath is { } fb && f.Folder)
                return new(new("native", Path: fb, Package: FrameGenPackageLabel(fb), AntiCheat: f.AntiCheat, CanToggle: true, RiskWarning: f.RiskWarning, RiskAccepted: f.RiskAccepted));
            return new(new("native"));
        }
        if (!f.Folder) return hidden;
        if (f.OtherMod is { } other) return new(new("mod", Mod: NamedMod(other)));
        if (f.AntiCheat is { } antiCheat)
            return new(new("off", Path: f.Wanted, AntiCheat: antiCheat, Note: f.Note, Package: FrameGenPackageLabel(f.Wanted), CanToggle: true, RiskWarning: f.RiskWarning ?? FrameGenAntiCheat.ConfirmationWarning(null, antiCheat), RiskAccepted: f.RiskAccepted));
        if (f.KeptOff is { } kept) return new(new("broken", Path: f.Wanted, Note: f.Note, Reason: kept, Package: FrameGenPackageLabel(f.Wanted), CanToggle: true));
        if (f.Unoffered is { } unavailable) return new(new("note", Note: unavailable, Package: FrameGenPackageLabel(f.Wanted)));
        return new(new("off", Path: f.Wanted, Note: f.Note, Package: FrameGenPackageLabel(f.Wanted), CanToggle: true), FrameGenNext.PutIn);
    }

    /// <summary>A take-out that hasn't happened yet, in the same words: "Taken out: the game updated" is "Coming out: the game updated".</summary>
    private static string Coming(string? why) =>
        why is null ? "Coming out: what is left of Exo's files"
            : why.StartsWith("Taken out: ", StringComparison.Ordinal) ? "Coming out: " + why["Taken out: ".Length..] : why;

    private static string? NamedMod(string? mod) => mod == UnnamedMod ? "A mod" : mod;

    /// <summary>Why Exo's frame generation can no longer stay in a game, or null.</summary>
    /// <param name="gone">The game no longer ships DLSS frame generation (<see cref="FrameGenGame.DlssgGone"/>).</param>
    internal static string? FrameGenTakeOut(FrameGenCard? card, FrameGenMod mod, string? antiCheat, string folder, FrameGenInstall install, string? wanted, bool gone = false, bool antiCheatAccepted = false)
    {
        if (card is null && install.Auto) return "Taken out: this graphics card can't run it";
        if (mod == OptiScaler && wanted == "dlss" && install.Input != "upscaler") return "Taken out: this card uses DLSS Frame Generation";
        if (card is { MultiFrame: true } && !(mod == OptiScaler && install.Input == "upscaler")) return "Taken out: this card has DLSS Multi Frame Generation";
        if (card is { DlssFrameGen: true } && !(mod == OptiScaler && install.Input == "upscaler") && mod != MfgUnlocker) return "Taken out: this card has DLSS Frame Generation";
        if (mod == DlssgSm86 && card is not { Vendor: "nvidia", Series: 20 or 30 }) return "Taken out: this card uses a different frame generation";
        if (mod == MfgUnlocker && card is not { Vendor: "nvidia", Series: >= 20 and <= 40 }) return "Taken out: this card uses a different frame generation";
        if (antiCheat is not null && !antiCheatAccepted)
            return AntiCheatCheck.TakenOut(antiCheat);
        if (FrameGenGameUpdated(folder, install)) return "Taken out: the game updated";
        if (gone && install.Input != "upscaler") return DlssgGoneNote;
        return null;
    }

    /// <summary>
    /// The game's program isn't the one the mod went in beside (a patch changed it): a mod built against the old one may
    /// break the new one, so it comes out. A record from before Exo kept the program's size reads as unchanged.
    /// </summary>
    internal static bool FrameGenGameUpdated(string folder, FrameGenInstall install)
    {
        if (install.ExeSize is not { } size || install.ExeWritten is not { } written) return false;
        try
        {
            var exe = new FileInfo(Path.Combine(folder, install.Exe));
            return exe.Exists && (exe.Length != size || exe.LastWriteTimeUtc != written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Why frame generation can't go into the game now, or null.</summary>
    private static string? FrameGenRefusal(GameEntry game, FrameGenGame layout, string? path, bool auto = true, string? acceptedAntiCheat = null)
    {
        if (layout.AntiCheat is { } antiCheat && (auto || !FrameGenRiskAccepted(game, antiCheat, acceptedAntiCheat)))
            return FrameGenAntiCheat.ConfirmationWarning(game, antiCheat);
        if (layout.Installed is { Out: true }) return "A mod is already in this game, so Exo leaves it alone.";
        if (layout.Installed is not null) return null;
        if (path == "native") return "This game has frame generation of its own. Turn it on in the game's settings.";
        if (path is null) return layout.DlssFrameGen ? "This graphics card doesn't need a mod for this game's frame generation." : "This game has no frame generation route.";
        if (layout.OtherMod is { } mod) return (mod == UnnamedMod ? "A mod" : mod) + " is already in this game, so Exo leaves it alone.";
        if (layout.Folder is null || layout.Exe is null) return "Exo couldn't find the game's program.";
        return null;
    }

    // ------------------------------------------------------------------ on and off

    /// <summary>
    /// Puts the mod for this card and game in (see the class summary): every refusal checked first, the newest release
    /// fetched and checked, then the refusals again under the game's write lock from a fresh walk. Best settings calls this
    /// (<see cref="FrameGenEverywhereAsync"/>), so the game's first session has to prove it.
    /// </summary>
    internal async Task<(bool Ok, string Message)> FrameGenOnAsync(GameEntry game, string? gpu, CancellationToken ct, bool auto = true, string? acceptedAntiCheat = null)
    {
        var root = ResolveInstallRoot(game.Path);
        if (root is null) return (false, "The install folder is unavailable.");
        if (IsWindowsAppsPath(root)) return (false, StoreLockedMessage);
        var card = FrameGenCardFor(gpu);
        var effectiveCard = card ?? (auto ? null : new FrameGenCard("other", 0));
        var layout = ReadFrameGenGame(game, root, ScanAndStore(game, ct), ct);
        var path = layout.Installed is null ? FrameGenPath(card, layout, game) : null;
        if (!auto && path == "native" && FrameGenModFallbackPath(card, layout) is { } fb) path = fb;
        if (FrameGenRefusal(game, layout, path, auto, acceptedAntiCheat) is { } refused) return (false, refused);
        if (layout.Installed is not null) return (true, "Already on.");
        if (effectiveCard is null) return (false, "This graphics card can't run frame generation from a mod.");
        var mod = FrameGenModForPath(path);
        if (mod is null) return (false, "This game has no frame generation mod route.");
        if (UnofferedReason(mod) is { } unavailable) return (false, unavailable);

        FrameGenRelease release;
        string source;
        try
        {
            release = await FrameGenLatest(mod, ct).ConfigureAwait(false);
            source = await FrameGenSource(mod, release, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"Frame generation: {mod.Name}'s release didn't arrive: {ex.Message}");
            return (false, $"Could not get {mod.Name}: {ex.Message}");
        }

        (bool Ok, string Message) result;
        using (var mutation = AcquireMutationGate(game))
            lock (mutation.Sync)
                result = FrameGenOnLocked(game, root, effectiveCard, mod, release, source, auto, acceptedAntiCheat);
        ScanAndStore(game);
        return result;
    }

    private (bool Ok, string Message) FrameGenOnLocked(
        GameEntry game, string root, FrameGenCard card, FrameGenMod mod, FrameGenRelease release, string source, bool auto, string? acceptedAntiCheat = null)
    {
        if (IsGameRunning(game, root)) return (false, GameRunningMessage);
        var fresh = ReadFrameGenGame(game, root, ScanGame(game), CancellationToken.None);
        var path = FrameGenPath(card.Vendor == "other" ? null : card, fresh, game);
        if (!auto && path == "native" && FrameGenModFallbackPath(card.Vendor == "other" ? null : card, fresh) is { } fb) path = fb;
        if (FrameGenRefusal(game, fresh, path, auto, acceptedAntiCheat) is { } refused) return (false, refused);
        if (fresh.Installed is not null) return (true, "Already on.");
        if (FrameGenModForPath(path) != mod) return (false, "The game changed. Try again.");
        var folder = fresh.Folder!;
        var names = mod == OptiScaler && fresh.Vulkan ? ["winmm.dll", .. mod.Loaders.Where(name => name != "winmm.dll")] : mod.Loaders;
        var loader = names.FirstOrDefault(name => !File.Exists(Path.Combine(folder, name)));
        if (loader is null) return (false, $"The game's folder has every name {mod.Name} can load as.");
        if (File.Exists(Path.Combine(folder, mod.Settings)) || mod.Folder is { } own && Directory.Exists(Path.Combine(folder, own)) || FrameGenLeftovers(folder, mod).Any())
            return (false, $"{mod.Name} is already in this game, so Exo leaves it alone.");
        if (!Path.TrimEndingDirectorySeparator(folder).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) && !IsSafeGamePath(root, folder))
            return (false, OutsideGameMessage);

        var input = FrameGenInputFor(fresh, path);
        var cap = mod == OptiScaler && card.Vendor == "nvidia" && FrameGenDriver?.GlobalCap() is > 0 and var fps ? fps : (uint?)null;
        var exe = new FileInfo(fresh.Exe!);
        var install = new FrameGenInstall(mod.Name, release.Tag, loader, input, exe.Name, [], Proven: false, Auto: auto,
            ExeSize: exe.Length, ExeWritten: exe.LastWriteTimeUtc);
        try
        {
            if (mod.Folder is { } modFolder) Directory.CreateDirectory(Path.Combine(folder, modFolder));
            SaveFrameGenManifest(folder, install);
            foreach (var spec in FrameGenSpecs(mod, card, source))
            {
                install.Files.Add(CopyFrameGenFile(folder, spec.Target.Length == 0 ? loader : spec.Target, Path.Combine(source, spec.Source)));
                SaveFrameGenManifest(folder, install);
            }
            foreach (var (relative, from) in PayloadFiles(mod, source))
            {
                install.Files.Add(CopyFrameGenFile(folder, relative, from));
                SaveFrameGenManifest(folder, install);
            }
            var settings = Path.Combine(folder, mod.Settings);
            if (mod == DlssgSm86) TuneDlssIni(settings, Path.Combine(source, mod.Settings));
            else if (mod == MfgUnlocker) File.WriteAllText(settings, MfgSettingsText());
            else File.WriteAllText(settings, FrameGenIniText(input, cap));
            var written = new FrameGenFile(mod.Settings, new FileInfo(settings).Length, null, File.GetLastWriteTimeUtc(settings));
            var at = install.Files.FindIndex(file => file.Path.Equals(mod.Settings, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) install.Files[at] = written;
            else install.Files.Add(written);
            SaveFrameGenManifest(folder, install);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Frame generation didn't go into {game.Title}: {ex.Message}");
            RemoveFrameGen(folder, install, mod);
            return (false, FailMessage(ex));
        }

        if (cap is { } moved)
        {
            if (FrameGenDriver!.SetGameCap(game, fresh.Exe!, 0, out var before, out var profile))
            {
                install = install with { Cap = new(moved, before, profile) };
                SaveFrameGenManifest(folder, install);
            }
            else AppLog.Info($"Frame generation in {game.Title}: the driver kept its {moved} fps cap for the game; OptiScaler caps at it too.");
        }
        RememberFrameGen(game.Id, folder);
        var risk = fresh.AntiCheat is { } ac ? $"; anti-cheat {ac}; user accepted the risk" : "";
        AppLog.Info($"Frame generation on: gameId={game.Id}; {mod.Name} {release.Tag} as {loader} beside {install.Exe}; input={input}; " +
                    $"dlssg={fresh.DlssgMajor?.ToString(CultureInfo.InvariantCulture) ?? "-"}; streamline={fresh.Streamline?.ToString(CultureInfo.InvariantCulture) ?? "none"}; vulkan={fresh.Vulkan}; card={card.Vendor} {card.Series}; cap={(cap?.ToString(CultureInfo.InvariantCulture) ?? "none")}{risk}.");
        return (true, input == "upscaler" ? "OptiScaler FG active" : "Turn on DLSS Frame Generation in the game");
    }

    internal async Task<(bool Ok, string Message)> FrameGenSetAsync(
        GameEntry game, string? gpu, bool enabled, bool acceptRisk, string? shownAntiCheat, CancellationToken ct)
    {
        if (!enabled)
        {
            if (AntiCheatCheck.ChoseWithoutAntiCheat(game) && DropSessionHeldFrameGen(game.Id))
                return (true, "Off.");
            return FrameGenOff(game);
        }
        ForgetReplaced(game.Id);
        ChangeFrameGenMemory(game.Id, memory => memory with { KeptOff = null, PutBack = 0 });
        var accepted = acceptRisk ? shownAntiCheat : null;
        return await FrameGenOnAsync(game, gpu, ct, auto: false, acceptedAntiCheat: accepted).ConfigureAwait(false);
    }

    internal Task<(bool Ok, string Message)> FrameGenSetAsync(
        GameEntry game, string? gpu, bool enabled, CancellationToken ct) =>
        FrameGenSetAsync(game, gpu, enabled, acceptRisk: false, shownAntiCheat: null, ct);

    /// <summary>The release's files this card needs: a Radeon's or Arc's extras only there, a license text where the release has it.</summary>
    private static IEnumerable<FrameGenFileSpec> FrameGenSpecs(FrameGenMod mod, FrameGenCard? card, string source) =>
        mod.Files.Where(spec => (card?.Vendor != "nvidia" || !spec.OffNvidia) && (!spec.Optional || File.Exists(Path.Combine(source, spec.Source))));

    /// <summary>Takes Exo's frame generation out of the game and puts its NVIDIA profile's frame cap back.</summary>
    public (bool Ok, string Message) FrameGenOff(GameEntry game)
    {
        var root = ResolveInstallRoot(game.Path);
        if (root is null) return (false, "The install folder is unavailable.");
        (bool Ok, string Message) result;
        using (var mutation = AcquireMutationGate(game))
            lock (mutation.Sync)
            {
                if (IsGameRunning(game, root)) return (false, GameRunningMessage);
                var layout = ReadFrameGenGame(game, root, null, CancellationToken.None);
                if (layout.Installed is not { } install || FrameGenModNamed(install.Mod) is not { } mod)
                {
                    ForgetFrameGen(game.Id);
                    return (true, "Off.");
                }
                var (kept, others) = RemoveFrameGen(layout.Folder!, install, mod);
                if (install.Cap is { } cap && FrameGenDriver is { } driver && !driver.SetGameCap(game, layout.Exe!, cap.Before, out _, out _))
                    AppLog.Warn($"Frame generation off in {game.Title}: the driver didn't take back its frame cap for the game.");
                if (ReadFrameGenManifest(layout.Folder!) is { Out: true })
                {
                    // Another mod's file is over Exo's loader: Exo keeps its record of that one file, and the game stays off,
                    // on every way out (the player, Best settings, a take-out), so nothing of Exo's goes in beside it.
                    KeepFrameGenOff(game.Id, "replaced", install.Loader);
                }
                else
                {
                    ForgetFrameGen(game.Id);
                    if (install.Out) ForgetReplaced(game.Id);
                }
                AppLog.Info($"Frame generation off: gameId={game.Id}; {mod.Name} {install.Release}; kept={(kept.Count == 0 ? "none" : string.Join(", ", kept))}; " +
                            $"others'={(others.Count == 0 ? "none" : string.Join(", ", others))}.");
                // A file that changed since is someone else's now (another mod's loader under the name): it stays, and Exo's own are out.
                result = kept.Count == 0 ? (true, "Off. The game's folder is as it was.")
                    : (true, $"Off. Exo left {string.Join(" and ", kept)} where it is: it changed since Exo put it there.");
            }
        ScanAndStore(game);
        return result;
    }

    /// <summary>
    /// Removes what is still Exo's: each file it recorded while its bytes are what Exo wrote (the loader only while it is
    /// also the mod's own DLL), the settings file, what the mod wrote as it ran (its logs beside the loader and in its
    /// folder's logs), every folder of the mod's that is then empty, and Exo's record. A file that changed since stays, and
    /// so does any file in the mod's folder Exo didn't write. While another mod's file is over the loader, the settings file
    /// stays if it changed since too, the logs stay (they may be the other mod's), and the record stays with the loader
    /// alone (<see cref="FrameGenInstall.Out"/>). Kept: Exo's files that changed, by name; others: files that were never Exo's.
    /// </summary>
    private (List<string> Kept, List<string> Others) RemoveFrameGen(string folder, FrameGenInstall install, FrameGenMod mod)
    {
        var kept = new List<string>();
        var others = new List<string>();
        // The loader first: whose it is now says whose the settings file and the logs are.
        foreach (var file in install.Files.Where(file => file.Sha256 is not null)
                     .OrderByDescending(file => file.Path.Equals(install.Loader, StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(folder, file.Path);
            if (!File.Exists(path)) continue;
            if (!StillExos(path, file, install, mod, over: false)) { kept.Add(file.Path); continue; }
            ClearReadOnly(path);
            DeletionGate.DeleteFile(path);
        }
        var over = kept.Contains(install.Loader, StringComparer.OrdinalIgnoreCase);
        foreach (var file in install.Files.Where(file => file.Sha256 is null))
        {
            var path = Path.Combine(folder, file.Path);
            if (!File.Exists(path)) continue;
            if (!StillExos(path, file, install, mod, over)) { kept.Add(file.Path); continue; }
            ClearReadOnly(path);
            DeletionGate.DeleteFile(path);
        }
        if (!over && !install.Out) foreach (var leftover in FrameGenLeftovers(folder, mod).ToList()) TryDelete(leftover);
        if (!install.Out && mod.Folder is { } own && Path.Combine(folder, own) is var modFolder && Directory.Exists(modFolder) &&
            (File.GetAttributes(modFolder) & FileAttributes.ReparsePoint) == 0)
            ClearModFolder(modFolder, mod, logs: !over, folder, others);
        others.RemoveAll(path => kept.Contains(path, StringComparer.OrdinalIgnoreCase));
        var manifest = Path.Combine(folder, FrameGenManifestName);
        if (over)
        {
            // The loader, and any file of Exo's a mod manager keeps as its backup: when the manager puts them back, they go.
            var left = install.Files.Where(file => file.Path.Equals(install.Loader, StringComparison.OrdinalIgnoreCase) ||
                                                   kept.Contains(file.Path, StringComparer.OrdinalIgnoreCase) && ManagerBackedUp(Path.Combine(folder, file.Path))).ToList();
            SaveFrameGenManifest(folder, install with { Files = left, Cap = null, Out = true });
        }
        else TryDelete(manifest);
        return (kept, others);
    }

    /// <summary>
    /// Whether a file Exo's record lists is still Exo's: a hashed one while its bytes are what Exo wrote (the loader only
    /// while it is also the mod's own DLL); the settings file unless another mod is over the loader
    /// (<paramref name="over"/>) and it changed since. Never one a mod manager keeps a backup of.
    /// </summary>
    private bool StillExos(string path, FrameGenFile file, FrameGenInstall install, FrameGenMod mod, bool over)
    {
        if (ManagerBackedUp(path)) return false;
        if (file.Sha256 is null)
        {
            var info = new FileInfo(path);
            return !over || info.Length == file.Size && info.LastWriteTimeUtc == file.Written;
        }
        var loader = file.Path.Equals(install.Loader, StringComparison.OrdinalIgnoreCase);
        var original = loader ? OriginalName(path) : null;
        return file.Sha256.Equals(FileSha256(path), StringComparison.OrdinalIgnoreCase) &&
               (!loader || mod == DlssgSm86 && string.IsNullOrEmpty(original) || string.Equals(original, mod.Dll, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The mod's folder once Exo's files in it are out: the mod's logs go (<paramref name="logs"/>), a link is never followed,
    /// a file Exo didn't write stays (listed in <paramref name="others"/>), and each folder left empty goes, deepest first.
    /// </summary>
    private static void ClearModFolder(string dir, FrameGenMod mod, bool logs, string root, List<string> others)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) others.Add(Path.GetRelativePath(root, entry.FullName));
            else if (entry is DirectoryInfo sub) ClearModFolder(sub.FullName, mod, logs, root, others);
            // Exo's own staging copy, left by a write that never finished, goes too.
            else if (logs && IsModLog(entry, mod) || entry.Name.EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase)) TryDelete(entry.FullName);
            else others.Add(Path.GetRelativePath(root, entry.FullName));
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) DeletionGate.DeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Frame generation folder left: " + ex.Message); }
    }

    /// <summary>A file the mod writes in its own folder as it runs: one of its logs by name, or a .log in a logs folder.</summary>
    private static bool IsModLog(FileSystemInfo file, FrameGenMod mod) =>
        mod.Logs.Contains(file.Name, StringComparer.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && file is FileInfo { Directory.Name: var parent } && parent.Equals("logs", StringComparison.OrdinalIgnoreCase);

    /// <summary>A release file into the game, through a temporary copy checked against it; never over a file that is there.</summary>
    private static FrameGenFile CopyFrameGenFile(string folder, string target, string source)
    {
        var dest = Path.GetFullPath(Path.Combine(folder, target));
        if (!IsUnderRoot(folder, dest)) throw new InvalidDataException(OutsideGameMessage);
        if (File.Exists(dest)) throw new IOException($"{target} is already in the game's folder.");
        var sha = FileSha256(source) ?? throw new IOException($"Exo's copy of {Path.GetFileName(source)} can't be read.");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var temp = dest + NewSuffix;
        try
        {
            File.Copy(source, temp, overwrite: true);
            if (!sha.Equals(FileSha256(temp), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The written file did not match the release's.");
            File.Move(temp, dest, overwrite: false);
        }
        finally { TryDelete(temp); }
        return new(target, new FileInfo(dest).Length, sha, File.GetLastWriteTimeUtc(dest));
    }

    // ------------------------------------------------------------------ keeping it current

    /// <summary>
    /// Every game with Exo's frame generation on the mod's newest release, while it and no store work on it run: the
    /// files of the release in the game checked as Exo wrote them, the new ones staged and checked beside them, then put
    /// in their place; the release before stays in Exo's copy to go back to (<see cref="FrameGenAfterSession"/>). No
    /// request at all while no game has it. The games moved.
    /// </summary>
    internal async Task<int> FrameGenKeepCurrentAsync(IEnumerable<GameEntry> games, string? gpu, Func<GameEntry, bool> busy, CancellationToken ct)
    {
        var moved = 0;
        var latest = new Dictionary<string, FrameGenRelease?>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (FrameGenFolderFor(game.Id) is not { } folder || ReadFrameGenManifest(folder) is not { Out: false } install || FrameGenModNamed(install.Mod) is not { } mod) continue;
            if (IsLegacy(mod))
            {
                // A mod Exo no longer ships: out by its record, and the game goes round again for today's.
                if (busy(game)) continue;
                var (ok, message) = FrameGenOff(game);
                AppLog.Info($"Frame generation in {game.Title}: {mod.Name} {install.Release} is no longer Exo's mod; {(ok ? message : "not taken out: " + message)}");
                if (ok) FrameGenEverywhereSoon();
                continue;
            }
            if (!latest.TryGetValue(mod.Repo, out var release))
            {
                try { release = await FrameGenLatest(mod, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Info($"Frame generation: {mod.Name}'s newest release: {ex.Message}");
                    release = null;
                }
                latest[mod.Repo] = release;
            }
            if (release is null || install.Refused?.Contains(release.Tag, StringComparer.OrdinalIgnoreCase) == true ||
                TagVersion(release.Tag) is not { } next || TagVersion(install.Release) is not { } now || next <= now) continue;
            if (busy(game) || ResolveInstallRoot(game.Path) is not { } root || IsGameRunning(game, root)) continue;
            string source;
            try { source = await FrameGenSource(mod, release, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Info($"Frame generation: {mod.Name} {release.Tag} didn't arrive: {ex.Message}");
                continue;
            }
            using (var mutation = AcquireMutationGate(game))
                lock (mutation.Sync)
                {
                    if (IsGameRunning(game, root) || ReadFrameGenManifest(folder) is not { } current || current.Release != install.Release) continue;
                    try
                    {
                        var updated = SwapFrameGen(folder, current, mod, release.Tag, source, FrameGenCardFor(gpu)) with { Previous = current.Release, Proven = false, Session = null };
                        SaveFrameGenManifest(folder, updated);
                        moved++;
                        AppLog.Info($"Frame generation in {game.Title}: {mod.Name} {current.Release} → {release.Tag}; the release before is kept to go back to.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        AppLog.Warn($"Frame generation in {game.Title}: {mod.Name} {release.Tag} didn't go in: {ex.Message}");
                    }
                }
            ScanAndStore(game);
        }
        return moved;
    }

    /// <summary>
    /// The game's files of one release replaced by another's: each of Exo's files checked as Exo wrote it first (a file
    /// someone changed stops it), the new ones staged beside them and checked, then moved over; a file the new release
    /// no longer has goes; the settings file stays as it is.
    /// </summary>
    private static FrameGenInstall SwapFrameGen(string folder, FrameGenInstall install, FrameGenMod mod, string tag, string source, FrameGenCard? card)
    {
        foreach (var file in install.Files.Where(file => file.Sha256 is not null))
            if (!file.Sha256!.Equals(FileSha256(Path.Combine(folder, file.Path)), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{file.Path} changed since Exo put it there.");
        var specs = FrameGenSpecs(mod, card ?? new(install.Files.Any(file => file.Path.Contains("fakenvapi", StringComparison.OrdinalIgnoreCase)) ? "amd" : "nvidia", 0), source)
            .Select(spec => (Target: spec.Target.Length == 0 ? install.Loader : spec.Target, From: Path.Combine(source, spec.Source)))
            .Concat(PayloadFiles(mod, source).Select(file => (Target: file.Relative, From: file.Full))).ToList();
        var staged = new List<(string Target, string Temp, string Sha)>();
        try
        {
            foreach (var (target, from) in specs)
            {
                var dest = Path.GetFullPath(Path.Combine(folder, target));
                if (!IsUnderRoot(folder, dest)) throw new InvalidDataException(OutsideGameMessage);
                var sha = FileSha256(from) ?? throw new IOException($"Exo's copy of {Path.GetFileName(from)} can't be read.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(from, dest + NewSuffix, overwrite: true);
                if (!sha.Equals(FileSha256(dest + NewSuffix), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The written file did not match the release's.");
                staged.Add((target, dest + NewSuffix, sha));
            }
            var files = new List<FrameGenFile>();
            foreach (var (target, temp, sha) in staged)
            {
                var dest = Path.Combine(folder, target);
                if (File.Exists(dest)) ClearReadOnly(dest);
                File.Move(temp, dest, overwrite: true);
                files.Add(new(target, new FileInfo(dest).Length, sha, File.GetLastWriteTimeUtc(dest)));
            }
            foreach (var gone in install.Files.Where(file => file.Sha256 is not null && files.All(kept => !kept.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase))))
                TryDelete(Path.Combine(folder, gone.Path));
            files.AddRange(install.Files.Where(file => file.Sha256 is null));
            return install with { Release = tag, Files = files };
        }
        finally
        {
            foreach (var (_, temp, _) in staged) TryDelete(temp);
        }
    }

    /// <summary>
    /// As a session of a game with Exo's frame generation ends, on a release not yet proven in this game: OptiScaler's
    /// session failed if it ended within 30 seconds; DLSSG SM86's also if it left no log (<see cref="DlssSessionFailed"/>).
    /// A failed session after an update sends the game back to the release before, from Exo's copy, and the new one is
    /// never moved to again here. Best settings' first try that failed comes out: OptiScaler's for good (the caller keeps
    /// the game off), DLSSG SM86's to OptiScaler at the next pass, once (<see cref="FrameGenMemory.DlssFailed"/>), and
    /// OptiScaler's own first session decides whether it stays. A session in which Exo's files weren't all there as it
    /// wrote them (another mod over its loader, a file changed or gone) didn't run Exo's mod, so it says nothing about it.
    /// </summary>
    /// <returns>True when the game must now be kept off by the caller: Best settings' first try came out, and Exo couldn't remember the DLSSG SM86 fallback either.</returns>
    internal bool FrameGenAfterSession(GameEntry game, TimeSpan? ran, string? gpu)
    {
        if (FrameGenFolderFor(game.Id) is not { } folder || ReadFrameGenManifest(folder) is not { Out: false } install || FrameGenModNamed(install.Mod) is not { } mod) return false;
        if (FrameGenDamage(folder, install) is { } damage)
        {
            AppLog.Info($"Frame generation in {game.Title}: the session says nothing about {mod.Name}, its files weren't as Exo wrote them ({damage.Reason}).");
            return false;
        }
        // A take-out that couldn't run yet (the game updated, shows anti-cheat, the card changed): the session ran against
        // that, not against the mod, so it isn't a failed session. The mod comes out at the next read or launch.
        if (ResolveInstallRoot(game.Path) is { } root && TakeOutDue(game, gpu, root, folder, mod, install) is { } due)
        {
            AppLog.Info($"Frame generation in {game.Title}: the session says nothing about {mod.Name}; it comes out ({due}).");
            return false;
        }
        var state = mod == DlssgSm86 ? DlssLogState(DlssLog(folder, DateTime.UtcNow - (ran ?? TimeSpan.Zero))) : null;
        var failed = mod == DlssgSm86 ? DlssSessionFailed(install, ran, state) : !install.Proven && ran < TimeSpan.FromSeconds(30);
        var why = mod == DlssgSm86 && state is null && ran >= TimeSpan.FromSeconds(30) ? "left no log" : "closed within 30 s";
        // Best settings' first try in a game, with no release before it to go back to: out, rather than in the way.
        if (failed && install.Auto && install.Previous is null)
        {
            var off = FrameGenOff(game);
            if (!off.Ok)
            {
                AppLog.Warn($"Frame generation in {game.Title} from Best settings: {mod.Name}'s first session {why}; {off.Message}.");
                return false;
            }
            FrameGenNotes[game.Id] = "Taken out: it didn't run in this game";
            if (mod == MfgUnlocker)
            {
                if (!ChangeFrameGenMemory(game.Id, memory => memory with { MfgFailed = true })) return true;
                FrameGenEverywhereSoon();
                AppLog.Warn($"Frame generation in {game.Title} from Best settings: MFG Unlocker's first session {why}; taken out, OptiScaler next.");
                return false;
            }
            if (mod != DlssgSm86)
            {
                AppLog.Warn($"Frame generation in {game.Title} from Best settings: the game closed within 30 s; taken out.");
                return true;
            }
            // OptiScaler next, once. If Exo can't remember that, the game is kept off rather than going round again.
            if (!ChangeFrameGenMemory(game.Id, memory => memory with { DlssFailed = true }))
            {
                AppLog.Warn($"Frame generation in {game.Title} from Best settings: DLSSG SM86's first session {why}; taken out, and kept off: Exo couldn't save that it failed here.");
                return true;
            }
            FrameGenEverywhereSoon();
            AppLog.Warn($"Frame generation in {game.Title} from Best settings: DLSSG SM86's first session {why}; taken out, OptiScaler next.");
            return false;
        }
        using var mutation = AcquireMutationGate(game);
        lock (mutation.Sync)
        {
            if (failed && install.Previous is { } previous)
            {
                try
                {
                    var source = FrameGenSource(mod, new FrameGenRelease(previous, "", 0, ""), CancellationToken.None).GetAwaiter().GetResult();
                    var back = SwapFrameGen(folder, install, mod, previous, source, FrameGenCardFor(gpu)) with
                    {
                        Previous = null, Proven = true, Session = null, Refused = [.. install.Refused ?? [], install.Release],
                    };
                    SaveFrameGenManifest(folder, back);
                    var note = $"Back on {mod.Name} {previous.TrimStart('v', 'V')}: {install.Release.TrimStart('v', 'V')} didn't run";
                    FrameGenNotes[game.Id] = note;
                    AppLog.Warn($"Frame generation in {game.Title}: {note} (the session {why}).");
                    return false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    AppLog.Warn($"Frame generation in {game.Title}: going back to {mod.Name} {previous} failed: {ex.Message}");
                }
            }
            try
            {
                SaveFrameGenManifest(folder, mod == DlssgSm86
                    ? install with { Session = state == "ran" ? "ran" : install.Session, Proven = install.Proven || state is not null }
                    : install with { Proven = install.Proven || !failed });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Frame generation in {game.Title}: after the session: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// A DLSSG SM86 session on a release not yet proven in the game failed: the game closed within 30 seconds, or a session
    /// of any length left no log (the proxy never loaded). A session Exo didn't time says nothing.
    /// </summary>
    internal static bool DlssSessionFailed(FrameGenInstall install, TimeSpan? ran, string? logState) =>
        !install.Proven && ran is { } time && (time < TimeSpan.FromSeconds(30) || logState is null);

    /// <summary>
    /// DLSSG SM86's first session from Best settings failed (<see cref="DlssSessionFailed"/>), with no release before it to
    /// go back to: it comes out. A release Exo moved the game to goes back to the one before instead.
    /// </summary>
    internal static bool DlssFirstSessionFailed(FrameGenInstall install, TimeSpan? ran, string? logState) =>
        install is { Auto: true, Previous: null } && DlssSessionFailed(install, ran, logState);

    /// <summary>
    /// An earlier Exo renamed the proxy when a session left no log, and a game importing the DLL it was renamed to then
    /// couldn't start (STATUS_ENTRYPOINT_NOT_FOUND). It goes back to version.dll. A file Exo didn't record stays.
    /// </summary>
    private FrameGenInstall? RepairDlssLoader(GameEntry game, string folder, FrameGenInstall install)
    {
        using var mutation = AcquireMutationGate(game);
        lock (mutation.Sync)
        {
            try
            {
                var repaired = DlssLoaderRepaired(folder, install);
                SaveFrameGenManifest(folder, repaired);
                AppLog.Info($"Frame generation in {game.Title}: {install.Loader} went back to {repaired.Loader}.");
                return repaired;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Frame generation in {game.Title}: {install.Loader} not put back: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Moves a renamed proxy back to the mod's own name on disk; what the manifest should now say.</summary>
    internal static FrameGenInstall DlssLoaderRepaired(string folder, FrameGenInstall install)
    {
        var (from, to) = (Path.Combine(folder, install.Loader), Path.Combine(folder, DlssgSm86.Loaders[0]));
        if (File.Exists(from))
        {
            if (!File.Exists(to)) File.Copy(from, to);
            DeletionGate.DeleteFile(from);
        }
        return install with
        {
            Loader = DlssgSm86.Loaders[0],
            Files = install.Files.Select(file => file.Path.Equals(install.Loader, StringComparison.OrdinalIgnoreCase) ? file with { Path = DlssgSm86.Loaders[0] } : file).ToList(),
        };
    }

    /// <summary>The newest DLSSG SM86 log written since <paramref name="since"/>, or null when the proxy wrote none.</summary>
    private static string? DlssLog(string folder, DateTime since)
    {
        var dir = Path.Combine(folder, "dlssg_sm86", "logs");
        if (!Directory.Exists(dir)) return null;
        var file = Directory.EnumerateFiles(dir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return file is null || File.GetLastWriteTimeUtc(file) < since ? null : TryReadText(file);
    }

    // ------------------------------------------------------------------ OptiScaler's settings

    /// <summary>OptiScaler's settings for the game: frame generation on from its input, its files in its folder, and the driver's frame cap if Exo moved one.</summary>
    internal static string MfgSettingsText() =>
        """{"multiplier":2,"dlssgPreset":0,"dlssSrPreset":0,"dlssRrPreset":0,"vsyncMode":0,"reflexFrameLimitFps":0,"followGame":true,"mode":"follow","dynamicTargetFrameRate":0,"xemfgStandalone":0}""";

    internal static string FrameGenIniText(string input, uint? cap)
    {
        var text = SetIniValue("", "FrameGen", "Enabled", "true");
        text = SetIniValue(text, "FrameGen", "FGInput", input);
        if (input is "dlssg" or "upscaler") text = SetIniValue(text, "FrameGen", "FGOutput", "fsrfg");
        else if (input == "nukems") text = SetIniValue(text, "FrameGen", "FGOutput", "nukems");
        if (input == "upscaler") text = SetIniValue(text, "OptiFG", "HUDFix", "true");
        text = SetIniValue(text, "Libraries", "OptiDllPath", OptiScaler.Folder!);
        text = SetIniValue(text, "Log", "LogToFile", "true");
        text = SetIniValue(text, "Log", "LogLevel", "2");
        if (cap is > 0) text = SetIniValue(text, "Framerate", "FramerateLimit", cap.Value.ToString(CultureInfo.InvariantCulture));
        return text;
    }

    /// <summary>The ini with key=value in [section]: in place where the key is, at the section's end, or in a new section at the file's end.</summary>
    internal static string SetIniValue(string text, string section, string key, string value)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
        if (lines is [""]) lines.Clear();
        var line = $"{key}={value}";
        var start = lines.FindIndex(item => item.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.AddRange([$"[{section}]", line]);
        }
        else
        {
            var end = lines.FindIndex(start + 1, item => item.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var at = lines.FindIndex(start + 1, end - start - 1, item => item.Contains('=') && item.Split('=', 2)[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) lines[at] = line;
            else
            {
                var insert = end;
                while (insert > start + 1 && lines[insert - 1].Trim().Length == 0) insert--;
                lines.Insert(insert, line);
            }
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    // ------------------------------------------------------------------ as the game starts and ends, and every six hours

    private static readonly ConcurrentDictionary<string, DateTime> FrameGenStarted = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _frameGenUpdates;
    private static int _frameGenUpdating;

    /// <summary>Checks Exo's frame generation as each game starts and ends, and keeps it on the newest release (<see cref="FrameGenKeepCurrentAsync"/>).</summary>
    internal static void WatchFrameGen(LaunchOrchestrator launcher, SettingsService settings, LibraryService library)
    {
        var service = new DlssSwapService();
        launcher.GameLaunching += game =>
        {
            // A game started without its anti-cheat: Exo's mod and recorder go in for this session only (SessionMods.cs).
            try { service.ForSessionAtLaunch(game, SystemSpecs.FrameGenGpu, settings.Shared.FrameGenEverywhere, settings.FrameGenKeptOff, id => settings.SetFrameGenKeptOff(id, true)); }
            catch (Exception ex) { AppLog.Warn("Frame generation for a session without anti-cheat: " + ex.Message); }
            try { service.FrameGenAtLaunch(game, SystemSpecs.FrameGenGpu); }
            catch (Exception ex) { AppLog.Warn("Frame generation at launch: " + ex.Message); }
            // The session's clock starts once the mod is ready (a download at Play is not playtime): a first session
            // under 30 seconds still takes a new release out (FrameGenAfterSession).
            FrameGenStarted[game.Id] = DateTime.UtcNow;
        };
        launcher.GameSessionCompleted += game => _ = Task.Run(() =>
        {
            try
            {
                TimeSpan? ran = FrameGenStarted.TryRemove(game.Id, out var at) ? DateTime.UtcNow - at : null;
                // Best settings' first try failed and came out: it doesn't go back in by itself.
                if (service.FrameGenAfterSession(game, ran, SystemSpecs.FrameGenGpu)) settings.SetFrameGenKeptOff(game.Id, true);
                // Started without its anti-cheat: what went in for the session comes out (a successor still running: as it exits).
                if (AntiCheatCheck.ChoseWithoutAntiCheat(game)) service.TakeOutAfterSession(game, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex) { AppLog.Warn("Frame generation after a session: " + ex.Message); }
        });
        // Leftovers of a session without anti-cheat: at Exo's start (it closed or crashed mid-session), after a launch that
        // never reached the game, and as such a game starts or stops outside an Exo session.
        void TakeOutIdle(IEnumerable<GameEntry> games) => _ = Task.Run(async () =>
        {
            try { await service.TakeOutWhereIdle(games, game => launcher.InExoSession(game.Id), launcher.IsGameBusy, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Frame generation leftovers of a session without anti-cheat: " + ex.Message); }
        });
        launcher.GameLaunchFailed += game => TakeOutIdle([game]);
        launcher.GameRunStateChanged += (id, _) =>
        {
            if (!launcher.InExoSession(id) && library.Find(id) is { } game && AntiCheatCheck.ChoseWithoutAntiCheat(game)) TakeOutIdle([game]);
        };
        _ = Task.Run(async () =>
        {
            try { TakeOutIdle(await library.GetLibraryAsync().ConfigureAwait(false)); }
            catch (Exception ex) { AppLog.Warn("Frame generation leftovers at start: " + ex.Message); }
        });
        _frameGenUpdates = new Timer(_ =>
        {
            if (launcher.AnyGameRunning || Interlocked.Exchange(ref _frameGenUpdating, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await service.TakeOutWhereIdle(library.PeekCachedLibrary(), game => launcher.InExoSession(game.Id), launcher.IsGameBusy, CancellationToken.None).ConfigureAwait(false);
                    // Best settings' frame generation first, so a game it goes into is on the newest release with the rest.
                    if (settings.Shared.FrameGenEverywhere)
                        await service.FrameGenEverywhereAsync(library.PeekCachedLibrary(), SystemSpecs.FrameGenGpu, launcher.IsGameBusy, settings.FrameGenKeptOff, CancellationToken.None,
                            id => settings.SetFrameGenKeptOff(id, true)).ConfigureAwait(false);
                    else service.FrameGenEverywhereOff(library.PeekCachedLibrary(), launcher.IsGameBusy);
                    await service.FrameGenKeepCurrentAsync(library.PeekCachedLibrary(), SystemSpecs.FrameGenGpu, launcher.IsGameBusy, CancellationToken.None).ConfigureAwait(false);
                    // Then the games' own DLSS, FSR and XeSS files (DlssSwapService.AutoApply.cs).
                    await service.UpscalersKeepCurrentAsync(library.PeekCachedLibrary(), launcher.IsGameBusy, settings.UpscalerConsent, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { AppLog.Warn("Frame generation updates: " + ex.Message); }
                finally { Volatile.Write(ref _frameGenUpdating, 0); }
            });
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));
        // Best settings' switch changed, or a game installed: a minute later rather than at the next six-hour round.
        _frameGenEverywhereSoon = () => _frameGenUpdates?.Change(TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
        library.LibraryUpdated += FrameGenEverywhereSoon;
    }

    /// <summary>
    /// On the launch's own thread, before the game starts, for a game Exo put frame generation in: taken out if the
    /// game now shows anti-cheat, a patch changed its program, or the card no longer needs it; a damaged install goes
    /// to app.log; OptiScaler's frame cap follows the driver's.
    /// </summary>
    internal void FrameGenAtLaunch(GameEntry game, string? gpu)
    {
        if (FrameGenFolderFor(game.Id) is not { } folder) return;
        if (ReadFrameGenManifest(folder) is not { } install || FrameGenModNamed(install.Mod) is not { } mod || ResolveInstallRoot(game.Path) is not { } root)
        {
            ForgetFrameGen(game.Id);
            return;
        }
        if (install.Out)
        {
            // Exo's mod is already out. Its loader back under the name (the other mod's manager put its backup back) would
            // load with nothing behind it, so it goes before the game starts.
            if (FrameGenDamage(folder, install) is not ("replaced", _)) AppLog.Info($"Frame generation at launch: gameId={game.Id}; the other mod's file went; {FrameGenOff(game).Message}");
            return;
        }
        if (mod == DlssgSm86 && install.Loader != mod.Loaders[0] && RepairDlssLoader(game, folder, install) is { } repaired) install = repaired;
        if (TakeOutDue(game, gpu, root, folder, mod, install) is { } why)
        {
            var off = FrameGenOff(game);
            if (off.Ok) FrameGenNotes[game.Id] = why;
            AppLog.Info($"Frame generation at launch: gameId={game.Id}; {why}; {(off.Ok ? "done" : off.Message)}.");
            return;
        }
        if (FrameGenDamage(folder, install) is { } damage)
        {
            if (damage.Reason == "replaced") KeepFrameGenOff(game.Id, "replaced", damage.File);
            AppLog.Warn($"Frame generation in {game.Title}: {damage.File} " + damage.Reason switch
            {
                "replaced" => "is another mod's now, so Exo leaves it alone.",
                "changed" => "changed since Exo put it there, so Exo leaves it alone.",
                _ => "went since it went in.",
            });
        }
        AppLog.Info($"Frame generation in {game.Title}: {mod.Name} {install.Release}.");
        if (mod == OptiScaler && FrameGenCardFor(gpu)?.Vendor == "nvidia") FollowDriverCap(game, folder, install);
    }

    /// <summary>Why Exo's mod must come out of the game now (<see cref="FrameGenTakeOut"/>), from a fresh look at the game and the shallow anti-cheat checks, or null.</summary>
    private string? TakeOutDue(GameEntry game, string? gpu, string root, string folder, FrameGenMod mod, FrameGenInstall install)
    {
        var antiCheat = AntiCheatCheck.FromScan(game, ShallowAntiCheat(root) ?? ShallowAntiCheat(folder) ?? CachedScan(game, CancellationToken.None).AntiCheat).Label;
        string? wanted = null;
        var gone = false;
        try
        {
            var layout = ReadFrameGenGame(game, root, CachedScan(game, CancellationToken.None), CancellationToken.None);
            wanted = FrameGenPath(FrameGenCardFor(gpu), layout, game);
            gone = layout.DlssgGone;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Frame generation take-out check: " + ex.Message); }
        var accepted = antiCheat is not null && FrameGenRiskAccepted(game, antiCheat, null);
        return FrameGenTakeOut(FrameGenCardFor(gpu), mod, antiCheat, folder, install, wanted, gone, accepted);
    }

    /// <summary>The driver's global frame cap as it is now: moved into OptiScaler's ini and off in the game's profile, or back where it was.</summary>
    private void FollowDriverCap(GameEntry game, string folder, FrameGenInstall install)
    {
        if (FrameGenDriver is not { } driver || driver.GlobalCap() is not { } global) return;
        var exe = Path.Combine(folder, install.Exe);
        var want = global > 0 ? global : (uint?)null;
        if (want == install.Cap?.Fps)
        {
            if (want is not null && driver.GameCap(game, exe) is not 0) driver.SetGameCap(game, exe, 0, out _, out _);
            return;
        }
        var ini = Path.Combine(folder, OptiScaler.Settings);
        if (File.Exists(ini))
            File.WriteAllText(ini, SetIniValue(File.ReadAllText(ini), "Framerate", "FramerateLimit", want?.ToString(CultureInfo.InvariantCulture) ?? "auto"));
        var cap = install.Cap;
        if (want is { } fps)
            cap = cap is not null ? cap with { Fps = fps }
                : driver.SetGameCap(game, exe, 0, out var before, out var profile) ? new FrameGenCap(fps, before, profile) : null;
        else if (cap is not null)
        {
            driver.SetGameCap(game, exe, cap.Before, out _, out _);
            cap = null;
        }
        SaveFrameGenManifest(folder, install with { Cap = cap });
        AppLog.Info($"Frame generation in {game.Title}: the driver's frame cap is now {(want?.ToString(CultureInfo.InvariantCulture) ?? "off")}, and OptiScaler follows it.");
    }

    // ------------------------------------------------------------------ which games have it

    private static readonly object FrameGenIndexGate = new();

    /// <summary>Swapped in tests: where Exo lists the games it put frame generation in, and in which folder.</summary>
    internal string FrameGenIndexPath { get; init; } = Path.Combine(PathHelper.AppDataDir, "frame-gen", "games.json");

    private Dictionary<string, string> FrameGenIndex()
    {
        try
        {
            return File.Exists(FrameGenIndexPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FrameGenIndexPath)) is { } read
                ? new(read, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveFrameGenIndex(Action<Dictionary<string, string>> change)
    {
        lock (FrameGenIndexGate)
        {
            var index = FrameGenIndex();
            change(index);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FrameGenIndexPath)!);
                var temp = FrameGenIndexPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(index));
                File.Move(temp, FrameGenIndexPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Frame generation list not saved: " + ex.Message); }
        }
    }

    private void RememberFrameGen(string gameId, string folder) => SaveFrameGenIndex(index => index[gameId] = folder);
    private void ForgetFrameGen(string gameId) => SaveFrameGenIndex(index => index.Remove(gameId));

    internal string? FrameGenFolderFor(string gameId)
    {
        lock (FrameGenIndexGate) return FrameGenIndex().GetValueOrDefault(gameId);
    }

    // ------------------------------------------------------------------ what Exo learned in each game

    /// <summary>What Exo learned about frame generation in one game on this PC, beside <see cref="FrameGenIndexPath"/>.</summary>
    /// <param name="DlssFailed">DLSSG SM86's first session failed here: the game takes OptiScaler, and never DLSSG SM86 again.</param>
    /// <param name="PutBack">How often Exo put its removed files back; once, then the game is kept off.</param>
    /// <param name="KeptOff">Why Best settings leaves the game off: <c>replaced</c> (another mod took Exo's loader's name) or <c>removed</c> (Exo's files went twice).</param>
    /// <param name="File">For <c>replaced</c>, the file the other mod holds: once it is gone, the game is no longer kept off.</param>
    internal sealed record FrameGenMemory(bool DlssFailed = false, int PutBack = 0, string? KeptOff = null, string? File = null, bool MfgFailed = false);

    private string FrameGenMemoryPath => Path.Combine(Path.GetDirectoryName(FrameGenIndexPath)!, "learned.json");

    private Dictionary<string, FrameGenMemory> FrameGenMemories()
    {
        try
        {
            return File.Exists(FrameGenMemoryPath) && JsonSerializer.Deserialize<Dictionary<string, FrameGenMemory>>(File.ReadAllText(FrameGenMemoryPath)) is { } read
                ? new(read, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    internal FrameGenMemory FrameGenMemoryFor(string gameId)
    {
        lock (FrameGenIndexGate) return FrameGenMemories().GetValueOrDefault(gameId) ?? new();
    }

    /// <summary>One game's memory changed and saved; false when it couldn't be saved, so a caller that relies on it can stop instead.</summary>
    private bool ChangeFrameGenMemory(string gameId, Func<FrameGenMemory, FrameGenMemory> change) =>
        SaveFrameGenMemories(all =>
        {
            var before = all.GetValueOrDefault(gameId) ?? new();
            var after = change(before);
            if (after == before) return false;
            if (after == new FrameGenMemory()) all.Remove(gameId);
            else all[gameId] = after;
            return true;
        });

    /// <summary>Every game's memory, changed by <paramref name="change"/> (true when it changed anything) and saved; false when it couldn't be saved.</summary>
    private bool SaveFrameGenMemories(Func<Dictionary<string, FrameGenMemory>, bool> change)
    {
        lock (FrameGenIndexGate)
        {
            var all = FrameGenMemories();
            if (!change(all)) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FrameGenMemoryPath)!);
                var temp = FrameGenMemoryPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(all));
                File.Move(temp, FrameGenMemoryPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Frame generation memory not saved: " + ex.Message);
                return false;
            }
        }
    }

    private bool KeepFrameGenOff(string gameId, string reason, string? file = null) =>
        ChangeFrameGenMemory(gameId, memory => memory with { KeptOff = reason, File = file });

    /// <summary>
    /// Best settings' switch is off: no game is kept off for damage any more (its files that went, another mod over its
    /// loader), so none reads as broken while Exo has nothing in it, and each starts afresh when the switch is on again.
    /// What failed in a game (DLSSG SM86's first session) stays learned.
    /// </summary>
    private void ForgetKeptOff() =>
        SaveFrameGenMemories(all =>
        {
            var changed = false;
            foreach (var (id, memory) in all.ToList())
            {
                var cleared = memory with { PutBack = 0, KeptOff = null, File = null };
                if (cleared == memory) continue;
                changed = true;
                if (cleared == new FrameGenMemory()) all.Remove(id);
                else all[id] = cleared;
            }
            return changed;
        });

    /// <summary>Exo's install is whole again (the other mod's manager put Exo's file back): no longer kept off for it.</summary>
    private void ForgetReplaced(string gameId) =>
        ChangeFrameGenMemory(gameId, memory => memory.KeptOff == "replaced" ? memory with { KeptOff = null, File = null } : memory);

    /// <summary>Why Best settings keeps this game off, or null; <c>replaced</c> ends once the other mod's file is gone from <paramref name="folder"/>.</summary>
    private string? FrameGenKeptOffHere(string gameId, string folder)
    {
        var memory = FrameGenMemoryFor(gameId);
        if (memory is { KeptOff: "replaced", File: { } file } && !File.Exists(Path.Combine(folder, file)))
        {
            ForgetReplaced(gameId);
            return null;
        }
        return memory.KeptOff;
    }

    // ------------------------------------------------------------------ the driver's frame cap

    /// <summary>The NVIDIA driver's frame caps (FRL V3, fps, 0 off).</summary>
    internal interface IFrameGenDriver
    {
        /// <summary>The global profile's cap, 0 when off; null when there is no NVIDIA driver to ask.</summary>
        uint? GlobalCap();

        /// <summary>The game's own profile's cap, null when it has none of its own or the driver didn't answer.</summary>
        uint? GameCap(GameEntry game, string exe);

        /// <summary>Sets the game's own cap (null: none of its own), with what it held before; false when the driver refused.</summary>
        bool SetGameCap(GameEntry game, string exe, uint? fps, out uint? before, out string? profile);
    }

    private sealed class NvidiaFrameGenDriver : IFrameGenDriver
    {
        internal static readonly NvidiaFrameGenDriver Instance = new();

        public uint? GlobalCap() =>
            NvidiaProfiles.Available && NvidiaProfiles.Read([GpuProfile.FrameRateLimit]) is { Ok: true } state
                ? state.Values.GetValueOrDefault(GpuProfile.FrameRateLimit) ?? 0
                : null;

        public uint? GameCap(GameEntry game, string exe) =>
            NvidiaProfiles.Available && NvidiaProfiles.ForGame(Program(game, exe), [GpuProfile.FrameRateLimit]) is { Ok: true } state
                ? state.After.GetValueOrDefault(GpuProfile.FrameRateLimit)
                : null;

        public bool SetGameCap(GameEntry game, string exe, uint? fps, out uint? before, out string? profile)
        {
            before = null;
            profile = null;
            if (!NvidiaProfiles.Available) return false;
            var state = NvidiaProfiles.ForGame(Program(game, exe), [GpuProfile.FrameRateLimit], new Dictionary<uint, uint?> { [GpuProfile.FrameRateLimit] = fps });
            if (!state.Ok)
            {
                AppLog.Info($"Frame generation: {game.Title}'s NVIDIA profile: {state.Message}");
                return false;
            }
            before = state.Before.GetValueOrDefault(GpuProfile.FrameRateLimit);
            profile = state.Profile;
            return true;
        }

        /// <summary>The game's program; a profile the driver finds for it must be the game's (NVIDIA's own by its title, or Exo's).</summary>
        private static NvidiaProfiles.GameProgram Program(GameEntry game, string exe) =>
            new(exe, game.Title, "", (name, _) => name.Contains(game.Title, StringComparison.OrdinalIgnoreCase) ||
                                                 StoreService.TitleKey(name) == StoreService.TitleKey(game.Title));
    }
}
