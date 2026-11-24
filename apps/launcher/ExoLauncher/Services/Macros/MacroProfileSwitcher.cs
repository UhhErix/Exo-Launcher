using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Services.Macros;

internal sealed record ProfileSwitchDecision(
    bool Changed,
    string ProfileId,
    string? GameId,
    string? AppExecutable,
    bool SoftwareAllowed,
    bool HardwareWriteAllowed,
    string? BlockedReason);

internal sealed class MacroProfileSwitcher
{
    internal const int DefaultDebounceMs = 350;
    internal const int DefaultExitGraceMs = 1500;
    internal const int DefaultHardwareCooldownMs = 2000;
    internal const int MaxHardwareWritesPerMinute = 10;

    private readonly int _debounceMs;
    private readonly int _exitGraceMs;
    private readonly int _hardwareCooldownMs;
    private readonly Queue<long> _hardwareWriteTimestampsMs = new();

    private readonly HashSet<string> _runningGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _runningOrder = [];
    private string? _foregroundExe;

    private string _activeProfileId = "desktop";
    private string? _activeGameId;
    private string? _activeHardwareHash;
    private long _lastHardwareWriteMs = long.MinValue / 4;

    private string? _pendingProfileId;
    private string? _pendingGameId;
    private string? _pendingExe;
    private long _pendingReadyAtMs;

    private long? _exitGraceUntilMs;
    private string? _graceHeldProfileId;
    private string? _graceHeldGameId;

    internal MacroProfileSwitcher(
        int debounceMs = DefaultDebounceMs,
        int exitGraceMs = DefaultExitGraceMs,
        int hardwareCooldownMs = DefaultHardwareCooldownMs)
    {
        _debounceMs = Math.Max(0, debounceMs);
        _exitGraceMs = Math.Max(0, exitGraceMs);
        _hardwareCooldownMs = Math.Max(0, hardwareCooldownMs);
    }

    internal string ActiveProfileId => _activeProfileId;
    internal string? ActiveGameId => _activeGameId;
    internal int AppliedSwitches { get; private set; }
    internal int SuppressedFlaps { get; private set; }
    internal int HardwareWrites { get; private set; }
    internal int SuppressedHardwareWrites { get; private set; }

    internal void SetInitialActive(string profileId, string? gameId = null, string? hardwareHash = null)
    {
        _activeProfileId = profileId;
        _activeGameId = gameId;
        _activeHardwareHash = hardwareHash;
        _pendingProfileId = null;
        _exitGraceUntilMs = null;
    }

    internal void ObserveGameState(string gameId, bool isRunning, long nowMs, IReadOnlyList<MacroProfile> profiles)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;

        if (isRunning)
        {
            if (_runningGames.Add(gameId))
            {
                _runningOrder.Add(gameId);
            }
            if (string.Equals(_graceHeldGameId, gameId, StringComparison.OrdinalIgnoreCase))
            {
                _exitGraceUntilMs = null;
                _graceHeldGameId = null;
                _graceHeldProfileId = null;
                SuppressedFlaps++;
            }
        }
        else if (_runningGames.Remove(gameId))
        {
            _runningOrder.RemoveAll(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_activeGameId, gameId, StringComparison.OrdinalIgnoreCase) && _exitGraceMs > 0)
            {
                _exitGraceUntilMs = nowMs + _exitGraceMs;
                _graceHeldGameId = gameId;
                _graceHeldProfileId = _activeProfileId;
            }
        }
        else
        {
            return;
        }

        QueueCandidate(nowMs, profiles);
    }

    internal void ObserveForegroundApp(string? executableName, long nowMs, IReadOnlyList<MacroProfile> profiles)
    {
        var normalized = NormalizeExe(executableName);
        if (string.Equals(_foregroundExe, normalized, StringComparison.OrdinalIgnoreCase)) return;
        _foregroundExe = normalized;
        QueueCandidate(nowMs, profiles);
    }

    internal ProfileSwitchDecision Poll(
        long nowMs,
        IReadOnlyList<MacroProfile> profiles,
        Func<string, bool> canAutoEnableSoftwareForGame)
    {
        if (_exitGraceUntilMs.HasValue && nowMs >= _exitGraceUntilMs.Value)
        {
            _exitGraceUntilMs = null;
            _graceHeldGameId = null;
            _graceHeldProfileId = null;
            QueueCandidate(nowMs, profiles);
        }

        if (_pendingProfileId is null || nowMs < _pendingReadyAtMs)
        {
            return new ProfileSwitchDecision(false, _activeProfileId, _activeGameId, _foregroundExe, true, false, null);
        }

        var targetId = _pendingProfileId;
        var targetGame = _pendingGameId;
        var targetExe = _pendingExe;
        _pendingProfileId = null;
        _pendingGameId = null;
        _pendingExe = null;

        if (string.Equals(targetId, _activeProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetGame, _activeGameId, StringComparison.OrdinalIgnoreCase))
        {
            SuppressedFlaps++;
            return new ProfileSwitchDecision(false, _activeProfileId, _activeGameId, targetExe, true, false, null);
        }

        var targetProfile = profiles.FirstOrDefault(p => string.Equals(p.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (targetProfile is null)
        {
            return new ProfileSwitchDecision(false, _activeProfileId, _activeGameId, targetExe, true, false, null);
        }

        var hasSoftwareBindings = targetProfile.Bindings.Any(b => b.Enabled && b.Delivery == MacroDelivery.Software);
        var softwareAllowed = true;
        string? blockedReason = null;

        if (!string.IsNullOrEmpty(targetGame) && hasSoftwareBindings && !canAutoEnableSoftwareForGame(targetGame))
        {
            softwareAllowed = false;
            blockedReason = "Tier 2 software macros are disabled for online, competitive anti-cheat, and unknown games. Only Tier 1 onboard hardware macros are allowed.";
        }

        var nextHash = ComputeOnboardHash(targetProfile);
        var hardwareWriteAllowed = false;
        if (nextHash is not null && !string.Equals(nextHash, _activeHardwareHash, StringComparison.Ordinal))
        {
            hardwareWriteAllowed = TryReserveHardwareWrite(nowMs);
            if (hardwareWriteAllowed)
            {
                _activeHardwareHash = nextHash;
            }
        }

        _activeProfileId = targetProfile.Id;
        _activeGameId = targetGame;
        AppliedSwitches++;

        return new ProfileSwitchDecision(
            true,
            _activeProfileId,
            _activeGameId,
            targetExe,
            softwareAllowed,
            hardwareWriteAllowed,
            blockedReason);
    }

    private void QueueCandidate(long nowMs, IReadOnlyList<MacroProfile> profiles)
    {
        var (candidateId, candidateGame) = ResolveTarget(nowMs, profiles);

        if (string.Equals(candidateId, _activeProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidateGame, _activeGameId, StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingProfileId is not null)
            {
                _pendingProfileId = null;
                _pendingGameId = null;
                _pendingExe = null;
                SuppressedFlaps++;
            }
            return;
        }

        if (_pendingProfileId is not null
            && !string.Equals(_pendingProfileId, candidateId, StringComparison.OrdinalIgnoreCase))
        {
            SuppressedFlaps++;
        }

        _pendingProfileId = candidateId;
        _pendingGameId = candidateGame;
        _pendingExe = _foregroundExe;
        _pendingReadyAtMs = nowMs + _debounceMs;
    }

    private (string ProfileId, string? GameId) ResolveTarget(long nowMs, IReadOnlyList<MacroProfile> profiles)
    {
        for (var i = _runningOrder.Count - 1; i >= 0; i--)
        {
            var gameId = _runningOrder[i];
            var match = profiles.FirstOrDefault(p => !p.IsDefault && p.GameIds.Any(g => string.Equals(g, gameId, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                return (match.Id, gameId);
            }
        }

        if (_exitGraceUntilMs.HasValue && nowMs < _exitGraceUntilMs.Value && _graceHeldProfileId is not null)
        {
            return (_graceHeldProfileId, _graceHeldGameId);
        }

        if (!string.IsNullOrEmpty(_foregroundExe))
        {
            var appMatch = profiles.FirstOrDefault(p => !p.IsDefault && p.AppExecutables.Any(exe =>
                string.Equals(NormalizeExe(exe), _foregroundExe, StringComparison.OrdinalIgnoreCase)));
            if (appMatch is not null)
            {
                return (appMatch.Id, null);
            }
        }

        var fallback = profiles.FirstOrDefault(p => p.IsDefault) ?? profiles.FirstOrDefault();
        return (fallback?.Id ?? "desktop", null);
    }

    private bool TryReserveHardwareWrite(long nowMs)
    {
        while (_hardwareWriteTimestampsMs.Count > 0 && nowMs - _hardwareWriteTimestampsMs.Peek() > 60_000)
        {
            _hardwareWriteTimestampsMs.Dequeue();
        }

        if (nowMs - _lastHardwareWriteMs < _hardwareCooldownMs || _hardwareWriteTimestampsMs.Count >= MaxHardwareWritesPerMinute)
        {
            SuppressedHardwareWrites++;
            return false;
        }

        _lastHardwareWriteMs = nowMs;
        _hardwareWriteTimestampsMs.Enqueue(nowMs);
        HardwareWrites++;
        return true;
    }

    internal static string? ComputeOnboardHash(MacroProfile profile)
    {
        var onboard = profile.Bindings
            .Where(b => b.Enabled && b.Delivery == MacroDelivery.Onboard)
            .OrderBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();
        if (onboard.Length == 0) return null;
        var json = JsonSerializer.Serialize(onboard);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string? NormalizeExe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var file = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(file) ? null : file.ToLowerInvariant();
    }
}
