using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services.Macros;

internal sealed class MacroService
{
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AppServices _services;
    private readonly Lock _gate = new();
    private readonly MacroScheduler _scheduler = new();
    private readonly MacroProfileSwitcher _switcher = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private MacroStoreDocument? _doc;
    private bool _recording;
    private string? _recordingMacroId;
    private bool _softwarePausedForAntiCheat;
    private string? _pauseReason;
    private string? _activeGameId;

    internal event Action<string, string?, bool, string?>? StateChanged;

    internal MacroService(AppServices services)
    {
        _services = services;
    }

    private static string StorePath => Path.Combine(PathHelper.AppDataDir, "macros", "macros.json");

    internal MacroStateSnapshot GetState()
    {
        var doc = EnsureLoaded();
        var policies = BuildGamePolicies(doc.Consents);
        var devices = ReadDevices();

        lock (_gate)
        {
            return new MacroStateSnapshot
            {
                Ok = true,
                Enabled = doc.Enabled,
                ActiveProfileId = _switcher.ActiveProfileId,
                ActiveGameId = _activeGameId,
                SoftwarePausedForAntiCheat = _softwarePausedForAntiCheat,
                PauseReason = _pauseReason,
                Recording = _recording,
                RecordingMacroId = _recordingMacroId,
                DebounceMs = MacroProfileSwitcher.DefaultDebounceMs,
                ExitGraceMs = MacroProfileSwitcher.DefaultExitGraceMs,
                HardwareWriteCooldownMs = MacroProfileSwitcher.DefaultHardwareCooldownMs,
                Profiles = doc.Profiles.ToList(),
                Devices = devices,
                GamePolicies = policies,
                Latency = _scheduler.SnapshotMetrics(),
                ProfileSwitchCount = _switcher.AppliedSwitches,
            };
        }
    }

    internal (bool Ok, string? Message, MacroProfile? Profile) SaveProfile(MacroProfile input)
    {
        var name = input.Name.Trim();
        if (name.Length is 0 or > 48)
        {
            return (false, "Profile name must be between 1 and 48 characters.", null);
        }

        var id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N")[..12] : input.Id.Trim();
        var clean = input with
        {
            Id = id,
            Name = name,
            Layers = input.Layers.Count > 0 ? input.Layers : [new MacroLayer()],
            ActiveLayerId = string.IsNullOrWhiteSpace(input.ActiveLayerId) ? "base" : input.ActiveLayerId,
            Bindings = input.Bindings.Select(SanitizeBinding).ToList(),
        };

        lock (_gate)
        {
            var doc = EnsureLoadedLocked();
            var idx = doc.Profiles.FindIndex(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                var existing = doc.Profiles[idx];
                clean = clean with { IsDefault = existing.IsDefault };
                doc.Profiles[idx] = clean;
            }
            else
            {
                clean = clean with { IsDefault = false };
                doc.Profiles.Add(clean);
            }
            PersistLocked(doc);
        }

        EmitChanged("profileSaved");
        return (true, null, clean);
    }

    internal (bool Ok, string? Message) DeleteProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return (false, "Profile id is required.");

        lock (_gate)
        {
            var doc = EnsureLoadedLocked();
            var target = doc.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (target is null) return (false, "Profile not found.");
            if (target.IsDefault) return (false, "The Desktop default profile cannot be deleted.");

            doc.Profiles.Remove(target);
            if (string.Equals(_switcher.ActiveProfileId, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                var fallback = doc.Profiles.FirstOrDefault(p => p.IsDefault)?.Id ?? "desktop";
                _switcher.SetInitialActive(fallback);
                _doc = doc with { ActiveProfileId = fallback };
                doc = _doc;
            }
            PersistLocked(doc);
        }

        EmitChanged("profileDeleted");
        return (true, null);
    }

    internal (bool Ok, string? Message, bool SoftwarePaused) ActivateProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return (false, "Profile id is required.", false);

        var doc = EnsureLoaded();
        var profile = doc.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return (false, "Profile not found.", false);

        var policies = BuildGamePolicies(doc.Consents);
        var blockedPolicy = profile.GameIds
            .Select(gid => policies.FirstOrDefault(p => string.Equals(p.GameId, gid, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(p => p is not null && p.RequiresConfirmation && !p.Confirmed);

        var hasSoftware = profile.Bindings.Any(b => b.Enabled && b.Delivery == MacroDelivery.Software);
        var paused = blockedPolicy is not null && hasSoftware;
        var reason = paused ? blockedPolicy!.SoftwareWarning : null;

        lock (_gate)
        {
            var loaded = EnsureLoadedLocked();
            _switcher.SetInitialActive(profile.Id, _activeGameId, MacroProfileSwitcher.ComputeOnboardHash(profile));
            _softwarePausedForAntiCheat = paused;
            _pauseReason = reason;
            _doc = loaded with { ActiveProfileId = profile.Id };
            PersistLocked(_doc);
        }

        EmitChanged("profileActivated");
        return (true, reason, paused);
    }

    internal bool SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            var doc = EnsureLoadedLocked();
            _doc = doc with { Enabled = enabled };
            PersistLocked(_doc);
        }
        EmitChanged("enabledChanged");
        return enabled;
    }

    internal (bool Ok, string? Message, MacroGamePolicy? Policy) ConfirmGame(string gameId, bool confirmed)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return (false, "Game id is required.", null);
        var game = _services.Library.Find(gameId);
        if (game is null) return (false, "Game not found in library.", null);

        var answer = AntiCheatCheck.Cached(game);
        MacroGamePolicy evaluated;

        lock (_gate)
        {
            var doc = EnsureLoadedLocked();
            doc.Consents.RemoveAll(c => string.Equals(c.GameId, game.Id, StringComparison.OrdinalIgnoreCase));
            var label = answer.Label ?? (AntiCheatCheck.IsRiot(game) ? "Riot Vanguard" : null);
            if (confirmed && label is not null)
            {
                doc.Consents.Add(new MacroGameConsent
                {
                    GameId = game.Id,
                    AntiCheatLabel = label,
                    ConfirmedAt = DateTime.UtcNow.ToString("O"),
                });
            }
            PersistLocked(doc);
            evaluated = MacroPolicy.Evaluate(game, answer, doc.Consents);
            if (string.Equals(_activeGameId, game.Id, StringComparison.OrdinalIgnoreCase))
            {
                _softwarePausedForAntiCheat = evaluated.RequiresConfirmation && !evaluated.Confirmed;
                _pauseReason = _softwarePausedForAntiCheat ? evaluated.SoftwareWarning : null;
            }
        }

        EmitChanged("consentChanged");
        return (true, null, evaluated);
    }

    internal (bool Ok, bool Recording, string? MacroId) SetRecording(bool start, string? macroId)
    {
        lock (_gate)
        {
            _recording = start;
            _recordingMacroId = start ? macroId : null;
        }
        EmitChanged(start ? "recordStart" : "recordStop");
        return (true, _recording, _recordingMacroId);
    }

    internal MacroTestRunResult TestRun(string profileId, string bindingId, CancellationToken ct)
    {
        var doc = EnsureLoaded();
        var profile = doc.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        var binding = profile?.Bindings.FirstOrDefault(b => string.Equals(b.Id, bindingId, StringComparison.OrdinalIgnoreCase));
        if (binding is null)
        {
            return new MacroTestRunResult { Ok = false, Message = "Macro binding not found." };
        }

        var sink = new DryRunMacroSink();
        return _scheduler.Execute(binding, sink, ct);
    }

    internal void ObserveGameRunState(string gameId, bool isRunning)
    {
        var doc = EnsureLoaded();
        var nowMs = _clock.ElapsedMilliseconds;
        lock (_gate)
        {
            _switcher.ObserveGameState(gameId, isRunning, nowMs, doc.Profiles);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(MacroProfileSwitcher.DefaultDebounceMs + 25).ConfigureAwait(false);
            ApplyPendingSwitch();
            if (!isRunning)
            {
                await Task.Delay(MacroProfileSwitcher.DefaultExitGraceMs + 25).ConfigureAwait(false);
                ApplyPendingSwitch();
            }
        });
    }

    internal void ApplyPendingSwitch()
    {
        var doc = EnsureLoaded();
        var policies = BuildGamePolicies(doc.Consents);
        var nowMs = _clock.ElapsedMilliseconds;
        ProfileSwitchDecision decision;

        lock (_gate)
        {
            decision = _switcher.Poll(nowMs, doc.Profiles, gid =>
            {
                var p = policies.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
                return MacroPolicy.CanRunSoftwareMacros(p, manualActivation: false);
            });
            if (!decision.Changed) return;
            _activeGameId = decision.GameId;
            _softwarePausedForAntiCheat = !decision.SoftwareAllowed;
            _pauseReason = decision.BlockedReason;
        }

        EmitChanged("profileAutoSwitch");
    }

    private void EmitChanged(string reason)
    {
        StateChanged?.Invoke(reason, _switcher.ActiveProfileId, _softwarePausedForAntiCheat, _pauseReason);
    }

    private MacroStoreDocument EnsureLoaded()
    {
        lock (_gate)
        {
            return EnsureLoadedLocked();
        }
    }

    private MacroStoreDocument EnsureLoadedLocked()
    {
        if (_doc is not null) return _doc;

        MacroStoreDocument? loaded = null;
        try
        {
            if (File.Exists(StorePath))
            {
                loaded = JsonSerializer.Deserialize<MacroStoreDocument>(File.ReadAllText(StorePath), StoreJson);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Macros: couldn't read macros.json: {ex.GetType().Name}");
        }

        if (loaded is null || loaded.Profiles.Count == 0)
        {
            loaded = CreateSeedDocument();
        }

        if (!loaded.Profiles.Any(p => p.IsDefault))
        {
            loaded.Profiles.Insert(0, CreateDesktopProfile());
        }

        _doc = loaded;
        var active = loaded.Profiles.FirstOrDefault(p => string.Equals(p.Id, loaded.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            ?? loaded.Profiles[0];
        _switcher.SetInitialActive(active.Id, null, MacroProfileSwitcher.ComputeOnboardHash(active));
        return _doc;
    }

    private void PersistLocked(MacroStoreDocument doc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, StoreJson));
            File.Move(temp, StorePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Macros: couldn't write macros.json: {ex.GetType().Name}");
        }
    }

    private IReadOnlyList<MacroGamePolicy> BuildGamePolicies(IReadOnlyList<MacroGameConsent> consents)
    {
        try
        {
            return _services.Library.PeekCachedLibrary()
                .Where(g => g.Installed)
                .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .Take(120)
                .Select(g => MacroPolicy.Evaluate(g, AntiCheatCheck.Cached(g), consents))
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return [];
        }
    }

    private IReadOnlyList<MacroDeviceCapability> ReadDevices()
    {
        try
        {
            var running = _services.Launcher.AnyGameRunning;
            return Peripherals.List(() => running)
                .Where(d => d.Kind is "mouse" or "keyboard" or "controller")
                .Select(d => new MacroDeviceCapability
                {
                    Id = d.Id,
                    Name = d.Name,
                    Kind = d.Kind,
                    SupportsOnboard = d.Kind == "mouse"
                        || (d.Kind == "keyboard" && WootingKeyboard.IsSupported(d))
                        || (d.Kind == "controller" && d.ProductId == ControllerLab.DualSenseEdge),
                    Protocol = d.Kind == "mouse" ? "openmouse"
                        : (d.Kind == "keyboard" && WootingKeyboard.IsSupported(d)) ? "wooting-hid"
                        : (d.Kind == "controller" && d.ProductId == ControllerLab.DualSenseEdge) ? "dualsense-edge"
                        : null,
                    Connection = d.Connection,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    private static MacroBinding SanitizeBinding(MacroBinding b) => b with
    {
        Id = string.IsNullOrWhiteSpace(b.Id) ? Guid.NewGuid().ToString("N")[..12] : b.Id.Trim(),
        Name = string.IsNullOrWhiteSpace(b.Name) ? "Untitled macro" : b.Name.Trim()[..Math.Min(b.Name.Trim().Length, 48)],
        Delivery = b.Delivery == MacroDelivery.Onboard ? MacroDelivery.Onboard : MacroDelivery.Software,
        Trigger = b.Trigger with
        {
            ChordWindowMs = Math.Clamp(b.Trigger.ChordWindowMs, 10, 250),
            RepeatCount = Math.Clamp(b.Trigger.RepeatCount, 1, 100),
            JitterMs = Math.Clamp(b.Trigger.JitterMs, 0, 50),
            LayerId = string.IsNullOrWhiteSpace(b.Trigger.LayerId) ? "base" : b.Trigger.LayerId,
        },
        UpdatedAt = DateTime.UtcNow.ToString("O"),
    };

    private static MacroStoreDocument CreateSeedDocument() => new()
    {
        Version = 1,
        Enabled = true,
        ActiveProfileId = "desktop",
        Profiles = [CreateDesktopProfile()],
        Consents = [],
    };

    private static MacroProfile CreateDesktopProfile() => new()
    {
        Id = "desktop",
        Name = "Desktop",
        IsDefault = true,
        ActiveLayerId = "base",
        Layers =
        [
            new MacroLayer { Id = "base", Name = "Base", Activation = "base" },
            new MacroLayer
            {
                Id = "shift",
                Name = "Shift layer",
                Activation = "hold",
                Trigger = new MacroInputSpec { DeviceKind = "mouse", Code = "Mouse4" },
            },
        ],
        Bindings =
        [
            new MacroBinding
            {
                Id = "seed-onboard-remap",
                Name = "Mouse 5 to Push-to-Talk",
                Enabled = true,
                Delivery = MacroDelivery.Onboard,
                Trigger = new MacroTrigger
                {
                    Chord = [new MacroInputSpec { DeviceKind = "mouse", Code = "Mouse5" }],
                    LayerId = "base",
                    Mode = MacroPlaybackMode.Hold,
                    JitterMs = 0,
                },
                Steps =
                [
                    new MacroStep { Id = "s1", Kind = MacroStepKind.KeyDown, Code = "F13", DelayMs = 0 },
                    new MacroStep { Id = "s2", Kind = MacroStepKind.Delay, DelayMs = 12 },
                    new MacroStep { Id = "s3", Kind = MacroStepKind.KeyUp, Code = "F13", DelayMs = 0 },
                ],
                UpdatedAt = DateTime.UtcNow.ToString("O"),
            },
            new MacroBinding
            {
                Id = "seed-chord-clip",
                Name = "Controller LB + RB to Clip",
                Enabled = true,
                Delivery = MacroDelivery.Software,
                Trigger = new MacroTrigger
                {
                    Chord =
                    [
                        new MacroInputSpec { DeviceKind = "controller", Code = "LB" },
                        new MacroInputSpec { DeviceKind = "controller", Code = "RB" },
                    ],
                    ChordWindowMs = 45,
                    LayerId = "base",
                    Mode = MacroPlaybackMode.Once,
                    JitterMs = 0,
                },
                Steps =
                [
                    new MacroStep { Id = "c1", Kind = MacroStepKind.KeyDown, Code = "F10", DelayMs = 0 },
                    new MacroStep { Id = "c2", Kind = MacroStepKind.Delay, DelayMs = 16 },
                    new MacroStep { Id = "c3", Kind = MacroStepKind.KeyUp, Code = "F10", DelayMs = 0 },
                ],
                UpdatedAt = DateTime.UtcNow.ToString("O"),
            },
        ],
    };
}
