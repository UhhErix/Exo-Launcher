using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// PC › Updates' BIOS update for ASUS boards, as Exo's own in place of ASUS's tools. Exo never writes the BIOS: the board's
/// own EZ Flash 3 does, which checks ASUS's signature on the file itself. Exo gets everything ready for it:
/// <list type="number">
/// <item>The newest released BIOS from ASUS's support API (GetPDBIOS, the same list PC › Updates reads), downloaded only
/// from ASUS's CDN (https://dlcdnets.asus.com) and checked against the SHA-256 ASUS publishes with it.</item>
/// <item>The BIOS file (the .CAP in ASUS's zip) taken out and copied to the EFI system partition, which EZ Flash lists
/// as a drive, so no USB stick is needed.</item>
/// <item>BitLocker, when it protects C:, paused for one restart: a new BIOS changes what the TPM measures, and Windows
/// would otherwise ask for the recovery key.</item>
/// <item>Restart into the BIOS setup (shutdown /r /fw), only on its own press, asked first.</item>
/// </list>
/// The copy, the pause and the restart run as administrator in one Windows prompt each. What was staged is kept in
/// bios-prep.json, so the row says "ready" after a restart of Exo too.
/// </summary>
internal static partial class BiosPrep
{
    internal sealed record Staged(string Version, string File, DateTime At, bool BitLockerPaused);

    /// <summary>The newest released BIOS file ASUS lists for a board: its version, download and SHA-256.</summary>
    internal sealed record AsusFile(string Version, Uri Url, string Sha256);

    private static string Folder => Path.Combine(PathHelper.AppDataDir, "bios");
    private static string StatePath => Path.Combine(PathHelper.AppDataDir, "bios-prep.json");
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(5));
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>What is staged for EZ Flash now, or null.</summary>
    internal static Staged? Current()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<Staged>(File.ReadAllText(StatePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// ASUS's GetPDBIOS answer's newest released BIOS file with its download and hash; null when it has none, or its link
    /// isn't ASUS's CDN over https, or its hash isn't a SHA-256.
    /// </summary>
    internal static AsusFile? ParseFile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Result", out var result) || !result.TryGetProperty("Obj", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        AsusFile? best = null;
        var bestNumber = -1;
        foreach (var group in groups.EnumerateArray())
        {
            if (Str(group, "Name") != "BIOS" || !group.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (Str(file, "IsRelease") != "1" || Beta(Str(file, "Title")) || Beta(Str(file, "Description"))) continue;
                if (Str(file, "Version") is not { } version || BiosUpdate.AsusNumber(version) is not { } number || number <= bestNumber) continue;
                var link = file.TryGetProperty("DownloadUrl", out var urls) && urls.ValueKind == JsonValueKind.Object ? Str(urls, "Global") : null;
                var sha = Str(file, "sha256")?.Trim().ToUpperInvariant();
                if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != "dlcdnets.asus.com"
                    || sha is null || !Sha256Hex().IsMatch(sha)) continue;
                best = new AsusFile(version.Trim(), url, sha);
                bestNumber = number;
            }
        }
        return best;
    }

    /// <summary>
    /// Downloads, checks and stages the newest BIOS for this board; null when it is ready for EZ Flash, else the line to
    /// show. <paramref name="progress"/> hears the download's share (0 to 1).
    /// </summary>
    internal static async Task<string?> PrepareAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) return "The BIOS is already being got ready.";
        try
        {
            var firmware = PcAbout.Bios;
            if (PcAbout.Maker(firmware.Maker) != "ASUS" || !PcAbout.Real(firmware.Product)) return "Exo gets ASUS boards' BIOS ready; this board's maker has its own tool.";
            var model = firmware.Product!.Trim();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.asus.com/support/api/product.asmx/GetPDBIOS?website=global&model={Uri.EscapeDataString(model)}");
            request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0");
            using var listed = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!listed.IsSuccessStatusCode) return "ASUS's BIOS list didn't answer. Try again later.";
            if (ParseFile(await listed.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is not { } file) return "ASUS lists no BIOS Exo can check for this board.";
            if (BiosUpdate.AsusNumber(firmware.Version) is { } have && BiosUpdate.AsusNumber(file.Version) is { } latest && have >= latest) return "This BIOS is already the newest.";

            Directory.CreateDirectory(Folder);
            var zip = Path.Combine(Folder, $"{file.Version}.zip");
            if (!File.Exists(zip) || Hash(zip) != file.Sha256)
            {
                var part = zip + ".part";
                await DownloadAsync(file.Url, part, progress, ct).ConfigureAwait(false);
                if (Hash(part) != file.Sha256)
                {
                    DeletionGate.DeleteFile(part);
                    AppLog.Warn($"BIOS: {file.Version}'s download didn't match ASUS's SHA-256.");
                    return "The download didn't match ASUS's checksum, so it wasn't used. Try again.";
                }
                File.Move(part, zip, overwrite: true);
            }
            if (Capsule(zip) is not { } capsule)
            {
                AppLog.Warn($"BIOS: {file.Version}'s archive has no single .CAP Exo can copy to the EFI partition.");
                return "ASUS's file holds no BIOS Exo recognizes.";
            }
            var cap = Path.Combine(Folder, capsule.Name);
            capsule.Entry.ExtractToFile(cap, overwrite: true);
            capsule.Archive.Dispose();

            if (Stage(cap) is { } refused) return refused;
            var paused = File.Exists(Path.Combine(Folder, "bitlocker-paused.txt"));
            Save(new Staged(file.Version, capsule.Name, DateTime.UtcNow, paused));
            AppLog.Info($"BIOS: {file.Version} ({capsule.Name}) is on the EFI partition for EZ Flash{(paused ? "; BitLocker paused for one restart" : "")}.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or TaskCanceledException)
        {
            AppLog.Warn("BIOS: not got ready: " + ex.Message);
            return "The BIOS wasn't got ready. Try again.";
        }
        finally { Gate.Release(); }
    }

    /// <summary>Restarts into the BIOS setup, where EZ Flash 3 is under Tool; null when Windows took it, else the line to show.</summary>
    internal static string? RestartIntoBios()
    {
        if (Current() is null) return "Get the BIOS ready first.";
        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        return ElevatedCommands.Run([$"\"{shutdown}\" /r /fw /t 15 /c \"Exo: restarting into the BIOS for EZ Flash\""], "BIOS");
    }

    /// <summary>Restarts into the BIOS setup or Windows' recovery menu, in 15 s; null when Windows took it, else the line to show.</summary>
    internal static string? RestartInto(bool firmware)
    {
        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        return ElevatedCommands.Run([$"\"{shutdown}\" /r {(firmware ? "/fw" : "/o")} /t 15 /c \"Exo: restarting into {(firmware ? "the BIOS" : "recovery")}\""], firmware ? "BIOS" : "Recovery");
    }

    /// <summary>A plain Windows restart in 5 s (the driver screen's Restart now): no prompt, any user may. Null when Windows took it.</summary>
    internal static string? RestartWindows()
    {
        try
        {
            var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            using var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shutdown, "/r /t 5 /c \"Exo: restarting to finish the graphics driver update\"") { UseShellExecute = false, CreateNoWindow = true });
            return run is null ? "Windows didn't restart. Restart from the Start menu." : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Restart: " + ex.Message);
            return "Windows didn't restart. Restart from the Start menu.";
        }
    }

    /// <summary>
    /// Copies the BIOS file to the root of the EFI system partition, and pauses BitLocker on C: for one restart when it's
    /// on, in one Windows prompt; each step that worked leaves a marker in Exo's BIOS folder, read back here.
    /// </summary>
    private static string? Stage(string cap)
    {
        var name = Path.GetFileName(cap);
        var staged = Path.Combine(Folder, "staged.txt");
        var paused = Path.Combine(Folder, "bitlocker-paused.txt");
        foreach (var marker in new[] { staged, paused }) if (File.Exists(marker)) DeletionGate.DeleteFile(marker);
        if (!CapsuleName(name) || !Safe(cap) || !Safe(staged) || !Safe(paused)) return "Exo can't stage the BIOS from this folder.";
        if (FreeLetter() is not { } letter) return "No drive letter is free to open the EFI partition.";
        var sys = Environment.SystemDirectory;
        var bde = Path.Combine(sys, "manage-bde.exe");
        var commands = new List<string>
        {
            $"\"{Path.Combine(sys, "mountvol.exe")}\" {letter}: /s",
            $"copy /y \"{cap}\" {letter}:\\{name} && echo ok> \"{staged}\"",
            $"\"{Path.Combine(sys, "mountvol.exe")}\" {letter}: /d",
            $"\"{bde}\" -status C: | \"{Path.Combine(sys, "find.exe")}\" \"Protection On\" && \"{bde}\" -protectors -disable C: -RebootCount 1 && echo ok> \"{paused}\"",
        };
        if (ElevatedCommands.Run(commands, "BIOS") is { } refused) return refused;
        return File.Exists(staged) ? null : "The BIOS file couldn't be copied to the EFI partition.";
    }

    private static async Task DownloadAsync(Uri url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report((double)done / total);
        }
    }

    /// <summary>The zip's one BIOS capsule (*.CAP at any depth), or null when it has none or more than one.</summary>
    private static (ZipArchive Archive, ZipArchiveEntry Entry, string Name)? Capsule(string zip)
    {
        var archive = ZipFile.OpenRead(zip);
        var caps = archive.Entries.Where(entry => entry.Name.EndsWith(".CAP", StringComparison.OrdinalIgnoreCase) && entry.Length > 1 << 20).ToList();
        if (caps.Count != 1 || !CapsuleName(caps[0].Name)) { archive.Dispose(); return null; }
        return (archive, caps[0], caps[0].Name);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static char? FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'M'; letter--) if (!used.Contains(letter)) return letter;
        return null;
    }

    private static void Save(Staged staged)
    {
        File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(staged));
        File.Move(StatePath + ".tmp", StatePath, overwrite: true);
    }

    /// <summary>A path cmd reads as it is.</summary>
    private static bool Safe(string path) => path.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '!']) < 0;

    private static bool Beta(string? text) => text?.Contains("beta", StringComparison.OrdinalIgnoreCase) == true;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("^[0-9A-F]{64}$")]
    private static partial Regex Sha256Hex();

    /// <summary>
    /// ASUS names the capsule after the board (ROG-STRIX-B550-F-GAMING-WIFI-II-ASUS-3645.CAP). Letters, digits and
    /// hyphens only, under the EFI partition's 255-character limit.
    /// </summary>
    internal static bool CapsuleName(string name) => SafeName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,200}\.CAP$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeName();
}
