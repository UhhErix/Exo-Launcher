using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Reads and writes the driver's global profile, the one NVIDIA Control
/// Panel's Global Settings edit (NvAPI_DRS_GetCurrentGlobalProfile; on a
/// stock install that is Profile Inspector's Base Profile).
///
/// "Driver default" removes the user's value: a value NVIDIA predefines is
/// restored (RestoreProfileDefaultSetting), any other is deleted. A value is
/// reported only when it is the user's own DWORD in this profile; inherited,
/// predefined and non-DWORD values read as the default.
///
/// Interface ids (nvapi_interface.h, R615 SDK): GetCurrentGlobalProfile
/// 0x617BFF9F, GetSetting 0x73BF8338, SetSetting 0x577DD202,
/// DeleteProfileSetting 0xE4A26362, RestoreProfileDefaultSetting 0x53F0381E,
/// SaveSettings 0xFCBC7E14, EnumAvailableSettingIds 0xF020614A,
/// SYS_GetDriverAndBranchVersion 0x2926AAAD. NVDRS_SETTING_VER1 is still
/// the newest setting structure.
/// </summary>
internal static unsafe partial class NvidiaProfiles
{
    /// <summary>
    /// What the driver holds: a value per asked id (null = driver default), and the ids a write was refused for (the driver
    /// does not take them: nothing was written for them, and the profile must not say it was).
    /// </summary>
    internal sealed record ProfileState(bool Ok, string? Message, IReadOnlyDictionary<uint, uint?> Values, IReadOnlyList<uint>? Refused = null);

    // NVDRS_SETTING_V1 field offsets.
    private const int SettingIdAt = 4 + UnicodeBytes;
    private const int SettingTypeAt = SettingIdAt + 4;
    private const int SettingLocationAt = SettingIdAt + 8;
    private const int CurrentPredefinedAt = SettingIdAt + 12;
    private const int PredefinedValidAt = SettingIdAt + 16;
    private const int CurrentValueAt = SettingIdAt + 20 + 4100;
    private const int NotSupported = -104;

    /// <summary>The global profile's values of <paramref name="ids"/>.</summary>
    internal static ProfileState Read(IReadOnlyCollection<uint> ids)
    {
        ProfileState? state = null;
        var result = RunGlobal((session, profile) =>
        {
            state = ReadValues(session, profile, ids);
            return new Result(state.Ok, null, state.Message);
        });
        return state ?? new ProfileState(false, result.Message, new Dictionary<uint, uint?>());
    }

    /// <summary>
    /// Goes through the changes with <paramref name="set"/> (one driver call per id, its NvAPI status): an id the driver does not
    /// take (NVAPI_NOT_SUPPORTED) is carried out in <c>Refused</c> and the rest go on; any other status stops at once and is
    /// returned with its id. Pure, so the carry is tested without a driver.
    /// </summary>
    internal static (List<uint> Refused, uint FailedId, int? FailedStatus) ApplyChanges(IReadOnlyList<KeyValuePair<uint, uint?>> changes, Func<uint, uint?, int> set)
    {
        var refused = new List<uint>();
        foreach (var (id, value) in changes)
        {
            var status = set(id, value);
            if (status == NotSupported) { refused.Add(id); continue; }
            if (status != Ok) return (refused, id, status);
        }
        return (refused, 0, null);
    }

    /// <summary>
    /// Writes each change (null = driver default) to the global profile in one
    /// session and save, then reads <paramref name="readBack"/> from what the driver saved.
    /// </summary>
    internal static ProfileState Write(IReadOnlyList<KeyValuePair<uint, uint?>> changes, IReadOnlyCollection<uint> readBack)
    {
        if (HostMode.IsDisposable) return new ProfileState(false, HostMode.Refusal, new Dictionary<uint, uint?>());
        ProfileState? state = null;
        var result = RunGlobal((session, profile) =>
        {
            // A setting this driver won't take (NVAPI_NOT_SUPPORTED: one it no longer has, or a value it refuses) is
            // left as it is and named in the log. One refusal used to stop every change after it, so Undo put back
            // nothing at all.
            var (refused, failedId, failedStatus) = ApplyChanges(changes,
                (id, value) => value is { } dword ? SetDword(session, profile, id, dword) : Reset(session, profile, id));
            if (failedStatus is { } fail)
            {
                AppLog.Warn($"NVIDIA profile: setting 0x{failedId:X8} was refused (NvAPI status {fail}).");
                return Failed("change the setting", fail);
            }
            if (changes.Count > 0 && refused.Count == changes.Count) return Failed("change the setting", NotSupported);
            var saved = F1(0xFCBC7E14)(session);
            if (saved != Ok) return Failed("save the driver profile", saved);
            if (refused.Count > 0)
                AppLog.Warn("NVIDIA global profile: the driver doesn't take " +
                    string.Join(", ", refused.Select(id => $"0x{id:X8}")) + "; left as they are.");
            AppLog.Info($"NVIDIA global profile: {changes.Count - refused.Count} settings; " +
                string.Join(", ", changes.Where(change => !refused.Contains(change.Key))
                    .Select(change => $"0x{change.Key:X8}={(change.Value is { } v ? "0x" + v.ToString("X") : "default")}")) + ".");
            state = ReadValues(session, profile, readBack) with { Refused = refused };
            return new Result(state.Ok, null, state.Message);
        });
        return state is { } done && result.Ok ? done : new ProfileState(false, result.Message ?? state?.Message, new Dictionary<uint, uint?>(), state?.Refused);
    }

    /// <summary>Every setting id this driver lists as known (its own catalogue), or empty.</summary>
    internal static IReadOnlySet<uint> KnownSettingIds()
    {
        if (!Available) return new HashSet<uint>();
        lock (Gate)
        {
            try
            {
                const int capacity = 8192;
                var ids = new uint[capacity];
                var count = (uint)capacity;
                int status;
                fixed (uint* buffer = ids)
                    status = F2(0xF020614A)((nint)buffer, (nint)(&count));
                if (status != Ok) return new HashSet<uint>();
                return ids.Take((int)Math.Min(count, capacity)).ToHashSet();
            }
            catch (Exception ex)
            {
                AppLog.Warn("NVIDIA setting list failed: " + ex.Message);
                return new HashSet<uint>();
            }
        }
    }

    /// <summary>The driver's version as NVIDIA writes it (61742 is 617.42), or null.</summary>
    internal static uint? DriverVersion()
    {
        if (!Available) return null;
        lock (Gate)
        {
            try
            {
                uint version = 0;
                var branch = stackalloc byte[64];
                return F2(0x2926AAAD)((nint)(&version), (nint)branch) == Ok ? version : null;
            }
            catch { return null; }
        }
    }

    private static Result RunGlobal(Func<nint, nint, Result> body) =>
        RunSession(session =>
        {
            nint profile = 0;
            var status = F2(0x617BFF9F)(session, (nint)(&profile));
            if (status != Ok || profile == 0) return Failed("find the global profile", status);
            return body(session, profile);
        });

    private static ProfileState ReadValues(nint session, nint profile, IReadOnlyCollection<uint> ids)
    {
        var values = new Dictionary<uint, uint?>();
        var setting = Marshal.AllocHGlobal(SettingSize);
        try
        {
            foreach (var id in ids)
            {
                new Span<byte>((void*)setting, SettingSize).Clear();
                *(uint*)setting = SettingVersion;
                var status = F4(0x73BF8338)(session, profile, (nint)id, setting);
                if (status == SettingNotFound) { values[id] = null; continue; }
                if (status != Ok) { Failed("read the setting", status); return new ProfileState(false, "The NVIDIA driver could not read the setting.", values); }
                var own = *(uint*)(setting + SettingLocationAt) == 0 && *(uint*)(setting + CurrentPredefinedAt) == 0 && *(uint*)(setting + SettingTypeAt) == 0;
                values[id] = own ? *(uint*)(setting + CurrentValueAt) : null;
            }
            return new ProfileState(true, null, values);
        }
        finally { Marshal.FreeHGlobal(setting); }
    }

    private static int SetDword(nint session, nint profile, uint id, uint value)
    {
        var setting = Marshal.AllocHGlobal(SettingSize);
        try
        {
            new Span<byte>((void*)setting, SettingSize).Clear();
            *(uint*)setting = SettingVersion;
            *(uint*)(setting + SettingIdAt) = id;
            *(uint*)(setting + CurrentValueAt) = value;
            return F3(0x577DD202)(session, profile, setting);
        }
        finally { Marshal.FreeHGlobal(setting); }
    }

    /// <summary>Puts the driver's own value back: NVIDIA's predefined one, or none.</summary>
    private static int Reset(nint session, nint profile, uint id)
    {
        var setting = Marshal.AllocHGlobal(SettingSize);
        try
        {
            new Span<byte>((void*)setting, SettingSize).Clear();
            *(uint*)setting = SettingVersion;
            var status = F4(0x73BF8338)(session, profile, (nint)id, setting);
            if (status == SettingNotFound) return Ok;
            if (status != Ok) return status;
            if (*(uint*)(setting + SettingLocationAt) != 0 || *(uint*)(setting + CurrentPredefinedAt) != 0) return Ok;
            status = *(uint*)(setting + PredefinedValidAt) != 0
                ? F3(0x53F0381E)(session, profile, (nint)id)
                : F3(0xE4A26362)(session, profile, (nint)id);
            return status == SettingNotFound ? Ok : status;
        }
        finally { Marshal.FreeHGlobal(setting); }
    }
}
