using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal sealed partial class GameQosPolicy(GameQosPolicy.ISystem system, string statePath)
{
    internal const string FileName = "exo-qos-policies.json";
    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);
    internal const string PolicyRegRoot = @"SOFTWARE\Policies\Microsoft\Windows\QoS";
    internal const string TcpipQosKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
    internal const string DoNotUseNlaValue = "Do not use NLA";
    internal const int DscpExpeditedForwarding = 46;
    internal const string RouterNote = "Tags outgoing game traffic with DSCP 46 (EF). Only routers with SQM/DSCP QoS (OpenWrt CAKE, ASUS/Ubiquiti) or Wi-Fi WMM honor it; ISPs strip DSCP on the internet.";

    internal static GameQosPolicy Shared { get; } = new(new NativeSystem(), DefaultPath);

    internal interface ISystem
    {
        IReadOnlyDictionary<string, (string AppName, int Dscp)> ReadPolicies();
        string? ReadDoNotUseNla();
        Task<bool> RunElevatedAsync(IReadOnlyList<string> lines, CancellationToken ct);
    }

    internal sealed record Entry(string GameId, string Title, string ExeName, string PolicyName, bool Enabled);

    internal sealed record State(IReadOnlyList<Entry> Games, bool NlaBypassed, string Note);

    internal sealed record Saved(string? PriorDoNotUseNla, Dictionary<string, string> Policies);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal static string PolicyNameFor(string gameId)
    {
        var clean = NonAlnum().Replace(gameId ?? "", "_").Trim('_');
        if (clean.Length == 0) clean = "game";
        if (clean.Length > 48) clean = clean[..48];
        return "Exo_DSCP_" + clean;
    }

    internal static string? ExeNameFor(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.Path))
        {
            var name = Path.GetFileName(game.Path.Trim());
            if (IsSafeExeName(name)) return name;
        }
        foreach (var path in GpuPreference.ProgramsOf(game))
        {
            var name = Path.GetFileName(path);
            if (IsSafeExeName(name)) return name;
        }
        return null;
    }

    internal static bool IsSafeExeName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 128
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && SafeExePattern().IsMatch(name);

    internal static bool IsSafePolicyName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SafePolicyPattern().IsMatch(name);

    internal State Read(IEnumerable<GameEntry> library)
    {
        var active = system.ReadPolicies();
        var nla = string.Equals(system.ReadDoNotUseNla(), "1", StringComparison.Ordinal);
        var list = new List<Entry>();
        foreach (var game in library.Where(g => g.Installed).OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (ExeNameFor(game) is not { } exe) continue;
            var policy = PolicyNameFor(game.Id);
            var enabled = active.TryGetValue(policy, out var info)
                && info.Dscp == DscpExpeditedForwarding
                && string.Equals(info.AppName, exe, StringComparison.OrdinalIgnoreCase);
            list.Add(new(game.Id, game.Title, exe, policy, enabled));
        }
        return new(list, nla, RouterNote);
    }

    internal async Task<string?> SetAsync(GameEntry game, bool enable, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return "Exo is still updating QoS policies. Try again when it's done.";
        try
        {
            var policy = PolicyNameFor(game.Id);
            var exe = ExeNameFor(game);
            if (!IsSafePolicyName(policy) || (enable && exe is null))
                return "Exo couldn't identify a valid game executable for QoS tagging.";

            var saved = Load();
            var lines = new List<string>();
            if (enable)
            {
                var priorNla = saved.Policies.Count == 0 ? system.ReadDoNotUseNla() : saved.PriorDoNotUseNla;
                var nextPolicies = new Dictionary<string, string>(saved.Policies, StringComparer.OrdinalIgnoreCase)
                {
                    [policy] = exe!,
                };
                if (!Save(new Saved(priorNla, nextPolicies)))
                    return "Exo couldn't save the QoS record, so nothing changed.";

                lines.AddRange(BuildEnableLines(policy, exe!));
                if (!await system.RunElevatedAsync(lines, ct).ConfigureAwait(false))
                {
                    Save(saved);
                    return "Windows' permission was declined, so nothing changed.";
                }
                var verified = system.ReadPolicies();
                if (!verified.TryGetValue(policy, out var after) || after.Dscp != DscpExpeditedForwarding)
                {
                    Save(saved);
                    return "Windows didn't create the QoS DSCP policy.";
                }
                AppLog.Info($"QoS DSCP 46 enabled for {game.Title} ({exe}).");
                return null;
            }
            else
            {
                var nextPolicies = new Dictionary<string, string>(saved.Policies, StringComparer.OrdinalIgnoreCase);
                nextPolicies.Remove(policy);
                var restoreNla = nextPolicies.Count == 0;
                lines.AddRange(BuildDisableLines(policy, restoreNla, saved.PriorDoNotUseNla));
                if (!await system.RunElevatedAsync(lines, ct).ConfigureAwait(false))
                    return "Windows' permission was declined, so nothing changed.";

                if (!Save(new Saved(restoreNla ? null : saved.PriorDoNotUseNla, nextPolicies)))
                    return "QoS policy removed, but Exo couldn't update its record.";
                AppLog.Info($"QoS DSCP 46 removed for {game.Title}.");
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<string?> EnableAllAsync(IEnumerable<GameEntry> library, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return "Exo is still updating QoS policies. Try again when it's done.";
        try
        {
            var missing = Read(library).Games.Where(g => !g.Enabled && IsSafePolicyName(g.PolicyName) && IsSafeExeName(g.ExeName)).ToList();
            if (missing.Count == 0) return null;

            var saved = Load();
            var priorNla = saved.Policies.Count == 0 ? system.ReadDoNotUseNla() : saved.PriorDoNotUseNla;
            var nextPolicies = new Dictionary<string, string>(saved.Policies, StringComparer.OrdinalIgnoreCase);
            foreach (var g in missing) nextPolicies[g.PolicyName] = g.ExeName;
            if (!Save(new Saved(priorNla, nextPolicies)))
                return "Exo couldn't save the QoS record, so nothing changed.";

            var lines = missing.SelectMany(g => BuildEnableLines(g.PolicyName, g.ExeName)).ToList();
            if (!await system.RunElevatedAsync(lines, ct).ConfigureAwait(false))
            {
                Save(saved);
                return "Windows' permission was declined, so nothing changed.";
            }
            var verified = system.ReadPolicies();
            var failed = missing.Where(g => !verified.TryGetValue(g.PolicyName, out var after) || after.Dscp != DscpExpeditedForwarding).ToList();
            if (failed.Count > 0)
            {
                foreach (var g in failed) nextPolicies.Remove(g.PolicyName);
                Save(new Saved(nextPolicies.Count == 0 ? null : priorNla, nextPolicies));
            }
            AppLog.Info($"QoS DSCP 46 enabled for {missing.Count - failed.Count} of {missing.Count} games.");
            return failed.Count == 0 ? null : $"Windows didn't create the QoS policy for {failed.Count} game{(failed.Count == 1 ? "" : "s")}.";
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static IReadOnlyList<string> BuildEnableLines(string policyName, string exeName)
    {
        if (!IsSafePolicyName(policyName) || !IsSafeExeName(exeName)) return [];
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        var key = $@"HKLM\{PolicyRegRoot}\{policyName}";
        var ps = $"Remove-NetQosPolicy -Name '{policyName}' -Confirm:$false -ErrorAction SilentlyContinue; New-NetQosPolicy -Name '{policyName}' -AppPathNameMatchCondition '{exeName}' -DSCPAction {DscpExpeditedForwarding} -PolicyStore PersistentStore -ErrorAction SilentlyContinue | Out-Null";
        return
        [
            $"{reg} add \"HKLM\\{TcpipQosKey}\" /v \"{DoNotUseNlaValue}\" /t REG_SZ /d \"1\" /f",
            $"{reg} add \"{key}\" /v Version /t REG_SZ /d \"1.0\" /f",
            $"{reg} add \"{key}\" /v \"Application Name\" /t REG_SZ /d \"{exeName}\" /f",
            $"{reg} add \"{key}\" /v Protocol /t REG_SZ /d \"*\" /f",
            $"{reg} add \"{key}\" /v \"Local Port\" /t REG_SZ /d \"*\" /f",
            $"{reg} add \"{key}\" /v \"Local IP\" /t REG_SZ /d \"*\" /f",
            $"{reg} add \"{key}\" /v \"Local IP Prefix Length\" /t REG_SZ /d \"*\" /f",
            $"{reg} add \"{key}\" /v \"Remote Port\" /t REG_SZ /d \"*\" /f",
            $"{reg} add \"{key}\" /v \"Remote IP\" /t REG_SZ /d \"*\" /f",
            $"{reg} add \"{key}\" /v \"Remote IP Prefix Length\" /t REG_SZ /d \"*\" /f",
            $"{reg} add \"{key}\" /v \"DSCP Value\" /t REG_SZ /d \"{DscpExpeditedForwarding}\" /f",
            $"{reg} add \"{key}\" /v \"Throttle Rate\" /t REG_SZ /d \"-1\" /f",
            $"{LeftoverScan.PowerShell} -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(ps))}",
        ];
    }

    internal static IReadOnlyList<string> BuildDisableLines(string policyName, bool restoreNla, string? priorNla)
    {
        if (!IsSafePolicyName(policyName)) return [];
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        var key = $@"HKLM\{PolicyRegRoot}\{policyName}";
        var ps = $"Remove-NetQosPolicy -Name '{policyName}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null";
        var lines = new List<string>
        {
            $"{LeftoverScan.PowerShell} -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(ps))}",
            $"{reg} delete \"{key}\" /f >nul 2>&1",
        };
        if (restoreNla)
        {
            if (priorNla is not null && SafeNlaPattern().IsMatch(priorNla))
                lines.Add($"{reg} add \"HKLM\\{TcpipQosKey}\" /v \"{DoNotUseNlaValue}\" /t REG_SZ /d \"{priorNla}\" /f");
            else
                lines.Add($"{reg} delete \"HKLM\\{TcpipQosKey}\" /v \"{DoNotUseNlaValue}\" /f >nul 2>&1");
        }
        return lines;
    }

    private Saved Load()
    {
        try
        {
            if (File.Exists(statePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(statePath), Json) is { } s)
                return new(s.PriorDoNotUseNla, s.Policies ?? new(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(null, new(StringComparer.OrdinalIgnoreCase));
    }

    private bool Save(Saved saved)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var tmp = statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Json));
            File.Move(tmp, statePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"^Exo_DSCP_[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePolicyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 _.-]+\.exe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SafeExePattern();

    [GeneratedRegex(@"^[01]$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNlaPattern();

    internal sealed class NativeSystem : ISystem
    {
        public IReadOnlyDictionary<string, (string AppName, int Dscp)> ReadPolicies()
        {
            var found = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(PolicyRegRoot);
                if (root is null) return found;
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (!IsSafePolicyName(sub)) continue;
                    using var key = root.OpenSubKey(sub);
                    var app = key?.GetValue("Application Name") as string;
                    var rawDscp = key?.GetValue("DSCP Value")?.ToString();
                    if (!string.IsNullOrWhiteSpace(app) && int.TryParse(rawDscp, out var dscp))
                        found[sub] = (app, dscp);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            return found;
        }

        public string? ReadDoNotUseNla()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TcpipQosKey);
                return key?.GetValue(DoNotUseNlaValue)?.ToString();
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        public Task<bool> RunElevatedAsync(IReadOnlyList<string> lines, CancellationToken ct) =>
            ReplacedApps.RemoveAsAdminAsync(lines, ct);
    }
}
