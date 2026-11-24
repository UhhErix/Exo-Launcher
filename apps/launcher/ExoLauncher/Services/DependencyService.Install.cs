using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Installing runtimes in one go: the plan (<see cref="Plan"/>), each installer downloaded with its measure and checked
/// (<see cref="Verify"/>), then ONE elevated run, so Windows asks once. That run is a PowerShell script Exo writes and
/// starts through a small bootstrap that checks the script's SHA-256 before running it. The script copies each installer
/// into a folder only administrators can write (under Windows\Temp, its permissions set as it is made), checks its SHA-256
/// again there, and only then runs it silently, in order, writing what each returned to a file Exo holds open; then Exo
/// reads this PC again and says per runtime what happened. While it runs, Exo holds every staged file open for reading
/// only, so nothing can swap one between Exo's check and the script's copy. Nothing is ever uninstalled, and Windows
/// Installer itself keeps a newer version in place.
/// </summary>
public sealed partial class DependencyService
{
    internal const string DeferredMessage = "Will install when your game closes";

    internal sealed record Progress(string Phase, string? Id, string? Name, int Index, int Count, long Bytes, long? Total, long DoneBytes, long AllBytes);

    /// <param name="Outcome">installed, newer (a newer one was already there), restart (installed; Windows needs a restart), failed.</param>
    internal sealed record ItemResult(string Id, string Name, string Outcome, string Message, int? Code, string? Detail = null);
    internal sealed record StepOutcome(int Code, string? Detail);

    internal sealed record ParsedLine(string Kind, string? Id, int? Code, string? Detail);

    internal sealed record Result(bool Ok, string? Message, IReadOnlyList<ItemResult> Results, bool Declined = false, bool Canceled = false, object[]? Items = null, bool Deferred = false);

    private sealed record DeferredRequest(IReadOnlyList<string>? Ids, string Mode);

    private sealed class Job(CancellationTokenSource cancel)
    {
        internal CancellationTokenSource Cancellation { get; } = cancel;
        internal bool Installing;
    }

    private Job? _job;
    private DeferredRequest? _deferred;

    internal event Action<Progress>? DeferredProgress;
    internal event Action<Result>? DeferredCompleted;

    internal bool Busy => Volatile.Read(ref _job) is not null;

    internal bool HasDeferred => Volatile.Read(ref _deferred) is not null;
    private const long UnknownSize = 60L * 1024 * 1024;

    public object Cancel()
    {
        var job = _job;
        if (job is null)
        {
            if (Interlocked.Exchange(ref _deferred, null) is not null)
                return new { ok = true, message = "Canceled. Nothing was installed." };
            return new { ok = false, message = "Nothing is installing." };
        }
        lock (job)
        {
            if (job.Installing) return new { ok = false, message = "Windows is installing them now. That can't be stopped halfway." };
            try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        return new { ok = true, message = "Stopping the downloads." };
    }

    internal void OnGameStateChanged()
    {
        if (ShouldDeferForGame(_gameRunning()) || Busy) return;
        var pending = Interlocked.Exchange(ref _deferred, null);
        if (pending is null) return;
        AppLog.Info($"Runtimes: game closed; starting deferred {pending.Mode} install.");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await InstallAsync(pending.Ids, pending.Mode, p => DeferredProgress?.Invoke(p), CancellationToken.None).ConfigureAwait(false);
                DeferredCompleted?.Invoke(result);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Warn("Runtimes: deferred install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        });
    }
    internal static IReadOnlyList<RuntimeStatus> Plan(IReadOnlyList<RuntimeStatus> statuses, string mode, IReadOnlyCollection<string>? ids,
        IReadOnlySet<string>? needed = null)
    {
        var named = ids is { Count: > 0 } ? new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase) : null;
        var pool = statuses.Where(s => s.Runtime.Installable && (named is null || named.Contains(s.Runtime.Id)));
        bool Missing(RuntimeStatus s) => s.State == State.Missing && (named is not null || !s.EndOfSupport && Wanted(s, needed));
        var chosen = mode switch
        {
            "repair" => named is not null ? pool : pool.Where(s => s.Runtime.IsVisualCpp),
            "update" => pool.Where(s => Missing(s) || s.State == State.Outdated),
            _ => pool.Where(Missing),
        };
        return chosen.OrderBy(s => s.Runtime.Order).ToList();
    }

    internal sealed record Step(Runtime Runtime, bool Repair, string? Url, string? Sha256, string? Sha512, long Size, string Version)
    {
        internal string? File;
        internal string? RunSha256;
        internal long StagedBytes;
        internal string? Failure;
        internal string? FailureDetail;
        internal long Bytes;
        internal long? Total;
    }

    internal async Task<Result> InstallAsync(IReadOnlyList<string>? ids, string mode, Action<Progress>? progress, CancellationToken ct, Func<Snapshot>? readBefore = null)
    {
        mode = mode is "update" or "repair" ? mode : "missing";
        if (ShouldDeferForGame(_gameRunning()))
        {
            Volatile.Write(ref _deferred, new DeferredRequest(ids?.ToArray(), mode));
            AppLog.Info($"Runtimes: deferred {mode} while a game is running.");
            return new Result(true, DeferredMessage, [], Deferred: true);
        }
        if (ids?.Where(id => id.StartsWith(AntiCheatPrefix, StringComparison.Ordinal)).ToList() is { Count: > 0 } antiCheat)
        {
            var repaired = new List<ItemResult>();
            foreach (var id in antiCheat) repaired.Add(await RepairAntiCheatAsync(id, ct).ConfigureAwait(false));
            var rest = ids.Except(antiCheat, StringComparer.Ordinal).ToList();
            var fine = repaired.All(r => r.Outcome != "failed");
            if (rest.Count == 0) return new Result(fine, fine ? null : repaired.First(r => r.Outcome == "failed").Message, repaired);
            var more = await InstallAsync(rest, mode, progress, ct).ConfigureAwait(false);
            return more with { Ok = more.Ok && fine, Results = [.. repaired, .. more.Results] };
        }
        using var awake = KeepAwake.Hold("runtime install");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var job = new Job(cancel);
        if (Interlocked.CompareExchange(ref _job, job, null) is not null)
            return new Result(false, "Runtimes are already installing.", []);
        var stage = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        var held = new List<FileStream>();
        try
        {
            await Task.Run(CleanupStaging, cancel.Token).ConfigureAwait(false);
            var dotnet = await DotnetReleasesAsync(cancel.Token, fresh: true).ConfigureAwait(false);
            var before = Evaluate(await Task.Run(() => Shows(readBefore is not null ? readBefore() : CurrentSnapshot()), cancel.Token).ConfigureAwait(false), dotnet);
            var plan = Plan(before, mode, ids, _needed);
            if (plan.Count == 0)
            {
                AppLog.Info($"Runtimes: {mode} plan is empty; nothing to install.");
                return new Result(true, ids is { Count: > 0 } ? "Already on this PC." : "Every runtime is up to date.", [], Items: before.Select(s => Shown(s, _needed)).ToArray());
            }
            AppLog.Info($"Runtimes: {mode} {string.Join(", ", plan.Select(s => s.Runtime.Id))}.");

            var steps = plan.Select(s => ToStep(s, mode == "repair", dotnet)).ToList();
            foreach (var failedStep in steps.Where(s => s.Failure is not null))
                AppLog.Warn($"Runtimes: {failedStep.Runtime.Id} skipped before download: {failedStep.FailureDetail ?? failedStep.Failure}");
            Directory.CreateDirectory(stage);
            await DownloadAllAsync(steps, stage, progress, cancel.Token).ConfigureAwait(false);

            foreach (var step in steps.Where(s => s.Failure is null && s.Runtime.Kind != Kind.Feature))
            {
                if (string.IsNullOrWhiteSpace(step.File) || !File.Exists(step.File) || string.IsNullOrWhiteSpace(step.RunSha256))
                {
                    step.Failure = "Its installer file is missing. Try again.";
                    step.FailureDetail = SanitizeDetail($"Staged installer missing before launch for {step.Runtime.Id} (url={step.Url ?? "none"}, path={step.File ?? "none"}).", stage);
                    AppLog.Warn($"Runtimes: {step.Runtime.Id} missing staged installer before launch: {step.FailureDetail}");
                }
            }

            var runnable = steps.Where(s => s.Failure is null).ToList();
            var outcomes = new Dictionary<string, StepOutcome>(StringComparer.Ordinal);
            var declined = false;
            string? fatal = null;
            int? elevatedExitCode = null;
            if (runnable.Count > 0)
            {
                lock (job) { cancel.Token.ThrowIfCancellationRequested(); job.Installing = true; }
                foreach (var step in runnable.Where(s => s.File is not null))
                    held.Add(new FileStream(step.File!, FileMode.Open, FileAccess.Read, FileShare.Read));
                var index = 0;
                (declined, outcomes, fatal, elevatedExitCode) = await RunElevatedAsync(stage, runnable, held, id =>
                {
                    var step = runnable.First(s => s.Runtime.Id == id);
                    progress?.Invoke(new Progress("install", id, step.Runtime.Name, ++index, runnable.Count, 0, null, 0, 0));
                }).ConfigureAwait(false);
            }
            foreach (var stream in held) await stream.DisposeAsync().ConfigureAwait(false);
            held.Clear();

            if (declined)
            {
                AppLog.Info("Runtimes: Windows elevation declined; nothing was installed.");
                return new Result(false, "Windows' permission was declined, so nothing was installed.", [], Declined: true, Items: before.Select(s => Shown(s, _needed)).ToArray());
            }
            if (fatal is not null) AppLog.Warn("Runtimes: elevated setup failed before running installers: " + fatal);

            progress?.Invoke(new Progress("check", null, null, steps.Count, steps.Count, 0, null, 0, 0));
            ForgetSnapshot();
            var after = Evaluate(await Task.Run(() => Shows(CurrentSnapshot()), CancellationToken.None).ConfigureAwait(false), dotnet);
            var results = steps.Select(step => Outcome(step, before, after, outcomes, fatal, elevatedExitCode)).ToList();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var result = results[i];
                var pathText = step.File is not null ? SanitizeDetail(step.File, stage) : "none";
                var shaText = step.RunSha256 is { Length: > 0 } runSha ? runSha.ToLowerInvariant() : "none";
                var line = $"Runtimes: {result.Id} {result.Outcome} exitCode={(result.Code?.ToString() ?? "none")} url={step.Url ?? "none"} path={pathText} size={step.StagedBytes} sha256={shaText}{(result.Detail is { Length: > 0 } detail ? $" detail={detail}" : "")}.";
                if (result.Outcome == "failed") AppLog.Warn(line);
                else AppLog.Info(line);
            }
            return new Result(results.All(r => r.Outcome != "failed"), null, results, Items: after.Select(s => Shown(s, _needed)).ToArray());
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "Canceled. Nothing was installed.", [], Canceled: true);
        }
        catch (Exception ex)
        {
            // A file or Windows error names the staged installer's path: the log has it, the page does not.
            AppLog.Warn("Runtimes install: " + ex.GetType().Name + ": " + ex.Message);
            return new Result(false, "Exo couldn't finish installing them. The details are in the log.", []);
        }
        finally
        {
            foreach (var stream in held) await stream.DisposeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _job, null);
            try { RemoveStage(stage); }
            catch { /* A failed staging cleanup cannot hide the installer result. */ }
        }
    }

    // Direct3D 12's line for the page's rows, when the page has asked it (never asked here: an install from a game's notice).
    private static Snapshot Shows(Snapshot snapshot) => Graphics.IsValueCreated ? WithGraphics(snapshot) : snapshot;

    internal static Step ToStep(RuntimeStatus status, bool repair, IReadOnlyDictionary<string, DotnetRelease> dotnet)
    {
        var r = status.Runtime;
        if (r.Id.StartsWith("dotnet", StringComparison.Ordinal))
        {
            var key = DotnetKey(r.Id);
            var arch = key[(key.IndexOf('-') + 1)..];
            if (dotnet.GetValueOrDefault(key) is { } release
                && release.Files.TryGetValue(arch, out var file)
                && IsValidDotnetFile(file))
                return new Step(r, repair, file.Url, null, file.Sha512, 0, release.Version);
            return new Step(r, repair, null, null, null, 0, status.Latest)
            {
                Failure = "Microsoft's .NET release list couldn't be read, so Exo can't check its installer. Try again.",
                FailureDetail = $"Release metadata missing or incomplete for {r.Id} ({status.Latest}).",
            };
        }
        if (r.Kind != Kind.Feature && string.IsNullOrWhiteSpace(r.Url))
        {
            return new Step(r, repair, null, null, null, 0, status.Latest)
            {
                Failure = "Its installer address is missing. Try again.",
                FailureDetail = $"Catalog download URL missing for {r.Id} ({status.Latest}).",
            };
        }
        return new Step(r, repair, r.Url, r.Sha256, null, r.Size, status.Latest);
    }

    private static async Task DownloadAllAsync(List<Step> steps, string stage, Action<Progress>? progress, CancellationToken ct)
    {
        var downloads = steps.Where(s => s.Url is not null && s.Failure is null).ToList();
        long All() => downloads.Sum(s => s.Total ?? (s.Size > 0 ? s.Size : UnknownSize));
        long Done() => downloads.Sum(s => s.Bytes);
        for (var i = 0; i < downloads.Count; i++)
        {
            var step = downloads[i];
            var r = step.Runtime;
            var path = Path.Combine(stage, r.Id + Extension(step.Url!));
            void Report(long bytes, long? total)
            {
                step.Bytes = bytes;
                step.Total = total ?? step.Total;
                progress?.Invoke(new Progress("download", r.Id, r.Name, i + 1, downloads.Count, bytes, step.Total ?? (step.Size > 0 ? step.Size : null), Done(), All()));
            }
            try
            {
                Report(0, step.Size > 0 ? step.Size : null);
                AppLog.Info($"Runtimes: {r.Id} downloading url={step.Url} path={SanitizeDetail(path, stage)} expectedBytes={step.Size}.");
                await DownloadToAsync(step.Url!, path, Report, ct, Path.Combine(StagingRoot, "downloads"), step.Size > 0 ? step.Size : null).ConfigureAwait(false);
                progress?.Invoke(new Progress("verify", r.Id, r.Name, i + 1, downloads.Count, step.Bytes, step.Total, Done(), All()));
                await Task.Run(() =>
                {
                    Verify(path, r, step.Sha256, step.Sha512);
                    step.File = r.Kind == Kind.Zip ? Unzip(path, r, stage) : path;
                    if (r.Kind == Kind.Zip) RequireSigned(step.File, r);
                    step.StagedBytes = new FileInfo(step.File).Length;
                    step.RunSha256 = HashOf(step.File, SHA256.Create());
                }, ct).ConfigureAwait(false);
                AppLog.Info($"Runtimes: {r.Id} verified url={step.Url} path={SanitizeDetail(step.File, stage)} size={step.StagedBytes} sha256={step.RunSha256!.ToLowerInvariant()}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
            {
                var fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
                step.StagedBytes = fileSize;
                step.FailureDetail = SanitizeDetail($"url={step.Url} path={path} size={fileSize} {ex.GetType().Name}: {ex.Message}", stage);
                AppLog.Warn($"Runtimes: {r.Id} download/verify failed: {step.FailureDetail}");
                step.Failure = ex is InvalidDataException data && data.Message.StartsWith(r.Name, StringComparison.Ordinal)
                    ? data.Message : "It couldn't be downloaded. Check the connection and try again.";
            }
        }
    }

    private static string Extension(string url)
    {
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        return extension is ".exe" or ".msi" or ".zip" or ".nupkg" ? extension : ".exe";
    }

    /// <summary>The one installer a zip carries (<see cref="Runtime.Entry"/>), next to it in the stage; bounded in size.</summary>
    private static string Unzip(string zip, Runtime runtime, string stage)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(runtime.Entry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{runtime.Name}'s package doesn't hold its installer.");
        if (entry.Length is <= 0 or > ResumableDownload.MaxInstallerBytes) throw new InvalidDataException($"{runtime.Name}'s installer has an unexpected size.");
        var target = Path.Combine(stage, runtime.Id + Path.GetExtension(entry.Name).ToLowerInvariant());
        entry.ExtractToFile(target, overwrite: false);
        return target;
    }

    internal static string Script(string stage, IEnumerable<(Runtime Runtime, bool Repair, string? File, string? Sha256)> steps)
    {
        static string Q(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$s = {Q(stage)}");
        script.AppendLine("$out = Join-Path $s 'results.txt'");
        script.AppendLine("function Say([string]$t) { $f = [IO.File]::Open($out, 'Append', 'Write', 'ReadWrite'); try { $b = [Text.Encoding]::UTF8.GetBytes($t + \"`n\"); $f.Write($b, 0, $b.Length) } finally { $f.Dispose() } }");
        script.AppendLine("function Unwrap($e) { while ($e -and $e.InnerException) { $e = $e.InnerException }; $e }");
        script.AppendLine("function ErrText($e) { $e = Unwrap $e; $w32 = if ($e -is [ComponentModel.Win32Exception]) { $e.NativeErrorCode } else { $null }; $hr = '0x' + $e.HResult.ToString('X8'); $tag = if ($null -ne $w32) { \"$hr, Win32 $w32\" } else { $hr }; \"$($e.GetType().Name) ($tag): $($e.Message -replace '[\\r\\n]+', ' ')\" }");
        script.AppendLine("function B64([string]$t) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($t)) }");
        script.AppendLine("$acl = New-Object Security.AccessControl.DirectorySecurity");
        script.AppendLine("$acl.SetAccessRuleProtection($true, $false)");
        script.AppendLine("foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))) }");
        script.AppendLine("$w = Join-Path $env:SystemRoot ('Temp\\ExoRuntimes-' + [Guid]::NewGuid().ToString('N'))");
        script.AppendLine("try { [void][IO.Directory]::CreateDirectory($w, $acl) } catch { Say ('fatal ' + (B64 (ErrText $_.Exception))); throw }");
        script.AppendLine("$msi = Join-Path $env:SystemRoot 'System32\\msiexec.exe'");
        script.AppendLine("$dism = Join-Path $env:SystemRoot 'System32\\dism.exe'");
        script.AppendLine("function Take([string]$name, [string]$hash) { $script:phase = 'take'; $src = Join-Path $s $name; if (-not [IO.File]::Exists($src)) { throw (New-Object IO.FileNotFoundException('Staged installer file not found.', $src)) }; $to = Join-Path $w $name; $sha = [Security.Cryptography.SHA256]::Create(); try { $attempt = 0; while ($true) { try { $in = [IO.File]::Open($src, 'Open', 'Read', 'Read'); try { $outF = [IO.File]::Open($to, 'CreateNew', 'Write', 'None'); try { $buf = New-Object byte[] 131072; while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $outF.Write($buf, 0, $n); [void]$sha.TransformBlock($buf, 0, $n, $null, 0) }; [void]$sha.TransformFinalBlock($buf, 0, 0) } finally { $outF.Dispose() } } finally { $in.Dispose() }; break } catch [IO.IOException] { if ($attempt -ge 4 -or ($_.Exception.HResult -ne -2147024864 -and $_.Exception.HResult -ne -2147024863)) { throw }; Remove-Item -LiteralPath $to -Force -ErrorAction SilentlyContinue; $attempt++; Start-Sleep -Milliseconds 300; $sha.Dispose(); $sha = [Security.Cryptography.SHA256]::Create() } }; $h = [BitConverter]::ToString($sha.Hash).Replace('-', '') } finally { $sha.Dispose() }; if ($h -ne $hash) { Remove-Item -LiteralPath $to -Force -ErrorAction SilentlyContinue; throw 'changed' }; $to }");
        script.AppendLine("function Run([string]$file, [string]$arguments) { $script:phase = 'run'; if ([string]::IsNullOrWhiteSpace($file) -or -not [IO.File]::Exists($file)) { throw (New-Object IO.FileNotFoundException('Installer file not found before launch.', $file)) }; $i = New-Object Diagnostics.ProcessStartInfo($file, $arguments); $i.UseShellExecute = $false; $i.CreateNoWindow = $true; $i.WorkingDirectory = Split-Path -LiteralPath $file; $attempt = 0; while ($true) { try { $p = [Diagnostics.Process]::Start($i); break } catch { $u = Unwrap $_.Exception; $win32 = if ($u -is [ComponentModel.Win32Exception]) { $u.NativeErrorCode } else { 0 }; if ($attempt -ge 4 -or ($win32 -ne 32 -and $win32 -ne 33)) { throw }; $attempt++; Start-Sleep -Milliseconds 300 } }; $p.WaitForExit(); $p.ExitCode }");
        script.AppendLine("function Step([string]$id, [scriptblock]$body) { Say \"start $id\"; $script:phase = 'run'; $tail = ''; try { $code = @(& $body)[-1] } catch { $u = Unwrap $_.Exception; $w32 = if ($u -is [ComponentModel.Win32Exception]) { $u.NativeErrorCode } else { $u.HResult -band 0xFFFF }; $blocked = ($w32 -in 225, 226, 1260, 1261, 4551) -or (($script:phase -eq 'run') -and (($w32 -eq 5) -or ($u -is [UnauthorizedAccessException]))); $code = if (\"$_\" -eq 'changed') { -1001 } elseif ($blocked) { -1004 } elseif ($script:phase -eq 'take') { -1003 } else { -1002 }; $tail = ' ' + (B64 (\"[$script:phase] \" + (ErrText $u))) }; Say \"done $id $code$tail\" }");
        script.AppendLine("try {");
        foreach (var (runtime, repair, file, sha256) in steps)
        {
            if (runtime.Kind != Kind.Feature && (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(sha256)))
                throw new InvalidOperationException($"Missing staged installer file or SHA-256 for {runtime.Id}.");
            var args = repair && runtime.RepairArgs is { } fix ? fix : runtime.Args;
            var take = file is null ? "" : $"(Take {Q(Path.GetFileName(file))} {Q(sha256 ?? "")})";
            var body = runtime.Kind switch
            {
                Kind.Feature => $"Run $dism {Q(args)}",
                Kind.DirectX => $"$x = {take}; $d = Join-Path $w 'directx'; [void][IO.Directory]::CreateDirectory($d); $c = Run $x ('/Q /C /T:\"' + $d + '\"'); if ($c -ne 0) {{ $c }} else {{ Run (Join-Path $d 'DXSETUP.exe') {Q(args)} }}",
                _ when runtime.Kind == Kind.Msi || file?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true => $"Run $msi ('/i \"' + {take} + '\" ' + {Q(args)})",
                _ => $"Run {take} {Q(args)}",
            };
            script.AppendLine($"  Step {Q(runtime.Id)} {{ {body} }}");
        }
        script.AppendLine("} finally { Remove-Item -LiteralPath $w -Recurse -Force -ErrorAction SilentlyContinue; Say 'end' }");
        return script.ToString();
    }

    internal static ParsedLine? ParseResultLine(string line, string? stagePath = null)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        var parts = trimmed.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["start", var startId]) return new ParsedLine("start", startId, null, null);
        if (parts is ["fatal", var fatalB64]) return new ParsedLine("fatal", null, null, DecodeDetail(fatalB64, stagePath));
        if (parts is ["err", var errId, var errB64]) return new ParsedLine("err", errId, null, DecodeDetail(errB64, stagePath));
        if (parts.Length >= 3 && parts[0] == "done" && int.TryParse(parts[2], out var code))
            return new ParsedLine("done", parts[1], code, parts.Length == 4 ? DecodeDetail(parts[3], stagePath) : null);
        return null;
    }

    internal static void ParseResultLines(IEnumerable<string> lines, IReadOnlySet<string> knownIds, Action<string>? started,
        Dictionary<string, int> codes, Dictionary<string, string> errors, string? stagePath = null)
    {
        foreach (var raw in lines)
        {
            var parsed = ParseResultLine(raw, stagePath);
            if (parsed is { Kind: "start", Id: { } id } && knownIds.Contains(id)) started?.Invoke(id);
            else if (parsed is { Kind: "err", Id: { } errId, Detail: { Length: > 0 } errDetail } && knownIds.Contains(errId))
                errors[errId] = errDetail;
            else if (parsed is { Kind: "done", Id: { } done, Code: { } value })
            {
                codes[done] = value;
                if (parsed.Detail is { Length: > 0 } detail && knownIds.Contains(done)) errors[done] = detail;
            }
        }
    }

    private static string? DecodeDetail(string encoded, string? stagePath = null)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return SanitizeDetail(text, stagePath);
        }
        catch (FormatException) { return null; }
    }

    private static async Task<(bool Declined, Dictionary<string, StepOutcome> Outcomes, string? Fatal, int? ExitCode)> RunElevatedAsync(string stage, List<Step> steps, List<FileStream> held, Action<string> started)
    {
        var script = Script(stage, steps.Select(s => (s.Runtime, s.Repair, s.File, s.RunSha256)));
        var scriptPath = Path.Combine(stage, "run.ps1");
        var bytes = new UTF8Encoding(false).GetBytes(script);
        await File.WriteAllBytesAsync(scriptPath, bytes).ConfigureAwait(false);
        held.Add(new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        var results = new FileStream(Path.Combine(stage, "results.txt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        held.Add(results);

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        AppLog.Info($"Runtimes: launching elevated setup for {string.Join(", ", steps.Select(s => s.Runtime.Id))} stage={SanitizeDetail(stage, stage)} scriptSha256={hash.ToLowerInvariant()}.");
        var bootstrap = $"$b = [IO.File]::ReadAllBytes('{scriptPath.Replace("'", "''", StringComparison.Ordinal)}'); " +
            "$h = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-', ''); " +
            $"if ($h -ne '{hash}') {{ exit 9 }}; & ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString($b)))";
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(powershell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (true, [], null, null); }
        if (process is null) throw new InvalidOperationException("Windows didn't start the runtime installers.");

        var outcomes = new Dictionary<string, StepOutcome>(StringComparer.Ordinal);
        string? fatal = null;
        int? exitCode = null;
        var text = new StringBuilder();
        var reader = new StreamReader(results, Encoding.UTF8);
        void ReadNew()
        {
            text.Append(reader.ReadToEnd());
            var lines = text.ToString().Split('\n');
            text.Clear().Append(lines[^1]);
            foreach (var line in lines[..^1])
            {
                var parsed = ParseResultLine(line, stage);
                if (parsed is { Kind: "start", Id: { } id } && steps.Any(s => s.Runtime.Id == id)) started(id);
                else if (parsed is { Kind: "done", Id: { } done, Code: { } value }) outcomes[done] = new StepOutcome(value, parsed.Detail);
                else if (parsed is { Kind: "fatal", Detail: { } detail }) fatal = detail;
            }
        }
        using (process)
        {
            var exited = process.WaitForExitAsync();
            var deadline = Stopwatch.StartNew();
            while (!exited.IsCompleted && deadline.Elapsed < TimeSpan.FromMinutes(90))
            {
                await Task.WhenAny(exited, Task.Delay(400)).ConfigureAwait(false);
                ReadNew();
            }
            ReadNew();
            if (!exited.IsCompleted) AppLog.Warn("Runtimes: the installers were still running after 90 minutes.");
            else
            {
                exitCode = process.ExitCode;
                if (exitCode == 9) AppLog.Warn("Runtimes: the install script changed before it ran, so it didn't (exitCode=9).");
                else if (exitCode != 0) AppLog.Warn($"Runtimes: elevated setup exited with code {exitCode}.");
                else AppLog.Info("Runtimes: elevated setup exited with code 0.");
            }
        }
        return (false, outcomes, fatal, exitCode);
    }

    internal static ItemResult Outcome(Step step, IReadOnlyList<RuntimeStatus> before, IReadOnlyList<RuntimeStatus> after,
        Dictionary<string, int> codes, Dictionary<string, string>? errors = null, int? elevatedExitCode = null)
    {
        var outcomes = codes.ToDictionary(kv => kv.Key, kv => new StepOutcome(kv.Value, errors?.GetValueOrDefault(kv.Key)), StringComparer.Ordinal);
        return Outcome(step, before, after, outcomes, errors?.GetValueOrDefault(step.Runtime.Id), elevatedExitCode);
    }

    private static ItemResult Outcome(Step step, IReadOnlyList<RuntimeStatus> before, IReadOnlyList<RuntimeStatus> after, Dictionary<string, StepOutcome> outcomes, string? fatal, int? elevatedExitCode = null)
    {
        var r = step.Runtime;
        if (step.Failure is { } failure) return new ItemResult(r.Id, r.Name, "failed", failure, null, step.FailureDetail);
        var was = before.FirstOrDefault(s => s.Runtime.Id == r.Id)?.State;
        var afterStatus = after.FirstOrDefault(s => s.Runtime.Id == r.Id);
        var now = afterStatus?.State;
        if (!outcomes.TryGetValue(r.Id, out var outcome))
        {
            var missingDetail = fatal ?? (elevatedExitCode is { } exit
                ? $"Elevated setup exited with code {exit} before reporting {r.Id}."
                : $"Elevated setup did not report a result for {r.Id}.");
            return new ItemResult(r.Id, r.Name, "failed", "It didn't run: the install stopped before it.", null, missingDetail);
        }
        var (code, detail) = outcome;
        if (code is 3010 or 1641) return new ItemResult(r.Id, r.Name, "restart", "Installed. Restart Windows to finish.", code);
        if (code is 1638 or unchecked((int)0x80070666)) return new ItemResult(r.Id, r.Name, "newer", "A newer version is already installed.", code);
        if (code == 0 && now is State.Current)
            return new ItemResult(r.Id, r.Name, "installed", step.Repair && was is State.Current ? "Reinstalled." : "Installed.", code);
        if (code == 0)
            return new ItemResult(r.Id, r.Name, "failed", "Its installer finished, but Windows doesn't list it yet. Restart Windows, then check again.", code,
                $"Installer exited with code 0 for {r.Id} (expected {step.Version}, detected {afterStatus?.Found?.Shown ?? "not installed"}).");
        if (now is State.Current && was is not State.Current) return new ItemResult(r.Id, r.Name, "installed", "Installed.", code);
        var failDetail = string.IsNullOrWhiteSpace(detail)
            ? $"Installer exited with code {code} (0x{code:X8}) for {r.Id} ({step.Version})."
            : detail;
        return new ItemResult(r.Id, r.Name, "failed", DescribeFailure(code, failDetail), code, failDetail);
    }

    internal static string? SanitizeDetail(string? raw, string? stagePath = null, string? tempRoot = null, string? userProfile = null, string? userName = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = Regex.Replace(raw.Trim(), @"[\r\n\t]+", " ");
        var temp = (tempRoot ?? Path.GetTempPath()).TrimEnd('\\', '/');
        if (temp.Length > 3) text = text.Replace(temp, "%TEMP%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(stagePath))
        {
            var stageTrim = stagePath.TrimEnd('\\', '/');
            if (stageTrim.Length > 3) text = text.Replace(stageTrim, @"%TEMP%\ExoLauncherDependencies", StringComparison.OrdinalIgnoreCase);
        }
        var profile = (userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('\\', '/');
        var user = userName ?? Environment.UserName;
        text = AppLog.Personal(AppLog.Redact(text), user, profile);
        text = Regex.Replace(text, @"[A-Za-z]:\\Users\\[^\\/\s""']+", "%USERPROFILE%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        if (text.Length > 300) text = text[..300];
        return text.Length == 0 ? null : text;
    }

    internal static string DescribeFailure(int code, string? detail)
    {
        if (code is -1002 or -1003 && !string.IsNullOrWhiteSpace(detail))
        {
            if (detail.Contains("FileNotFoundException", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("DirectoryNotFoundException", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("win32=2)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 2)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("win32=3)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 3)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("could not find", StringComparison.OrdinalIgnoreCase))
                return "Its installer file is missing. Try again.";
            if (detail.Contains("win32=32)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 32)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("win32=33)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 33)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("sharing violation", StringComparison.OrdinalIgnoreCase))
                return "Blocked by a running app. Close it and try again.";
            if (detail.Contains("UnauthorizedAccessException", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("win32=5)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 5)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("win32=225)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 225)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("win32=1260)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 1260)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("access to the path", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("contains a virus", StringComparison.OrdinalIgnoreCase))
                return "Blocked by Windows or security software.";
            if (detail.Contains("win32=740)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Win32 740)", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
                return "Windows permission is needed to start its installer.";
        }
        return Words(code);
    }

    internal static string Words(int code, string? detail = null) => code switch
    {
        1602 or 1223 or unchecked((int)0x800704C7) => "It was canceled.",
        1618 => "Another installation was running. Try again once it finishes.",
        1603 => "Its installer hit an error (1603).",
        1612 or 1620 => "Windows Installer couldn't find its files (1612).",
        1625 or 1643 => "A system policy blocked it.",
        1633 => "It doesn't run on this PC's processor.",
        5100 => "This PC doesn't meet its requirements.",
        unchecked((int)0x800F081F) => "Windows Update couldn't supply its files (0x800F081F).",
        unchecked((int)0x800F0906) => "Windows couldn't download its files. Check the connection (0x800F0906).",
        unchecked((int)0x800F0907) => "A group policy keeps Windows from getting its files (0x800F0907).",
        -1001 => "Its file changed after Exo checked it, so it didn't run.",
        -1003 => "Exo couldn't copy it.",
        -1004 => "Windows blocked it (policy or antivirus).",
        -1002 => ShortReason(detail) is { } reason ? $"It couldn't start ({reason})." : "Its installer didn't start.",
        < 0 => $"Its installer stopped with code 0x{code:X8}.",
        _ => $"Its installer stopped with code {code}.",
    };

    private static string? ShortReason(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        var sep = detail.IndexOf(": ", StringComparison.Ordinal);
        var message = (sep >= 0 ? detail[(sep + 2)..] : detail).Trim().TrimEnd('.');
        if (message.Length == 0) return null;
        return message.Length > 140 ? message[..137] + "..." : message;
    }
}
