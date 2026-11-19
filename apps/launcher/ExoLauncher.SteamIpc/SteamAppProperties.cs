using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;

namespace ExoLauncher.SteamIpc;

/// <summary>
/// <c>props</c> and <c>set</c>: a game's Properties through the same Steam client
/// calls Steam's own Properties page makes (SteamClient.Apps.SetDLCEnabled,
/// SetAppAutoUpdateBehavior, JoinAppContentBeta, ToggleAppSteamCloudEnabled ...).
/// Exo never edits Steam's files for these; Steam writes its own manifests.
///
/// Every method is found from <c>steamclient64.dll</c> by decoding the x64 RIP-relative
/// <c>LEA</c> ASCII method and interface names inside each IPC stub (with 24-bit hash
/// prefix disambiguation for overloaded names and 24-bit fallback if needed). A method
/// that is missing or matches more than once is refused instead of guessed.
/// </summary>
internal static partial class Program
{
    // IClientEngine slot that returns IClientRemoteStorage (checked by RTTI before use).
    internal const int EngineGetIClientRemoteStorage = 24;

    private static SteamIpcStubResolver.StubSpec SpecBIsDlcEnabled => SteamIpcStubResolver.BIsDlcEnabled;
    private static SteamIpcStubResolver.StubSpec SpecSetDlcEnabled => SteamIpcStubResolver.SetDlcEnabled;
    private static SteamIpcStubResolver.StubSpec SpecIsAppDlcInstalled => SteamIpcStubResolver.IsAppDlcInstalled;
    private static SteamIpcStubResolver.StubSpec SpecGetCurrentLanguage => SteamIpcStubResolver.GetCurrentLanguage;
    private static SteamIpcStubResolver.StubSpec SpecSetCurrentLanguage => SteamIpcStubResolver.SetCurrentLanguage;
    private static SteamIpcStubResolver.StubSpec SpecGetActiveBeta => SteamIpcStubResolver.GetActiveBeta;
    private static SteamIpcStubResolver.StubSpec SpecSetActiveBeta => SteamIpcStubResolver.SetActiveBeta;
    private static SteamIpcStubResolver.StubSpec SpecCheckBetaPassword => SteamIpcStubResolver.CheckBetaPassword;
    private static SteamIpcStubResolver.StubSpec SpecGetAutoUpdate => SteamIpcStubResolver.GetAutoUpdate;
    private static SteamIpcStubResolver.StubSpec SpecSetAutoUpdate => SteamIpcStubResolver.SetAutoUpdate;
    private static SteamIpcStubResolver.StubSpec SpecGetBackground => SteamIpcStubResolver.GetBackground;
    private static SteamIpcStubResolver.StubSpec SpecSetBackground => SteamIpcStubResolver.SetBackground;
    private static SteamIpcStubResolver.StubSpec SpecNumLibraryFolders => SteamIpcStubResolver.NumLibraryFolders;
    private static SteamIpcStubResolver.StubSpec SpecLibraryFolderPath => SteamIpcStubResolver.LibraryFolderPath;
    private static SteamIpcStubResolver.StubSpec SpecAppLibraryFolder => SteamIpcStubResolver.AppLibraryFolder;
    private static SteamIpcStubResolver.StubSpec SpecMoveApp => SteamIpcStubResolver.MoveApp;
    private static SteamIpcStubResolver.StubSpec SpecMoveProgress => SteamIpcStubResolver.MoveProgress;
    private static SteamIpcStubResolver.StubSpec SpecCloudForAccount => SteamIpcStubResolver.CloudForAccount;
    private static SteamIpcStubResolver.StubSpec SpecCloudForApp => SteamIpcStubResolver.CloudForApp;
    private static SteamIpcStubResolver.StubSpec SpecSetCloudForApp => SteamIpcStubResolver.SetCloudForApp;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
    private delegate IntPtr GetClientInterfaceFn(IntPtr self, int user, int pipe, string version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppDlcOutFn(IntPtr self, uint app, uint dlc, out byte extra);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AppDlcSetFn(IntPtr self, uint app, uint dlc, [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppDlcFn(IntPtr self, uint app, uint dlc);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int AppIntFn(IntPtr self, uint app);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int AppSetIntFn(IntPtr self, uint app, int value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppSetIntBoolFn(IntPtr self, uint app, int value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppBufferFn(IntPtr self, uint app, IntPtr buffer, int length);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppStringFn(IntPtr self, uint app, IntPtr text);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NoArgIntFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int FolderPathFn(IntPtr self, int index, IntPtr buffer, int length);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool MoveProgressFn(IntPtr self, uint app, out ulong done, out ulong total, out uint seconds);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NoArgBoolFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppBoolFn(IntPtr self, uint app);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AppSetBoolFn(IntPtr self, uint app, [MarshalAs(UnmanagedType.I1)] bool value);

    /// <summary>
    /// <c>props &lt;app&gt; [dlc,dlc,...]</c>: one JSON line with what Steam holds for
    /// the game now. Reads only. Exit 3 when Steam is not running or signed in.
    /// </summary>
    private static int RunProps(string[] args, uint appId)
    {
        var dlcs = args.Length >= 3 ? ParseAppIds(args[2]) : [];
        if (!TryConnectWithStorage(out var engine, out var pipe, out var engineVt, out var manager, out var storage))
            return 3;
        try
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteBoolean("ok", true);
                if (Find<AppIntFn>(manager, SpecGetAutoUpdate) is { } autoUpdate)
                    json.WriteNumber("autoUpdate", autoUpdate(manager, appId));
                if (Find<AppIntFn>(manager, SpecGetBackground) is { } background)
                    json.WriteNumber("background", background(manager, appId));
                if (Find<AppIntFn>(manager, SpecGetCurrentLanguage) is { } language)
                    json.WriteNumber("language", language(manager, appId));
                if (Find<AppBufferFn>(manager, SpecGetActiveBeta) is { } activeBeta)
                    json.WriteString("beta", ReadString((buffer, length) => activeBeta(manager, appId, buffer, length)) ?? "");
                if (storage != IntPtr.Zero &&
                    Find<NoArgBoolFn>(storage, SpecCloudForAccount) is { } cloudAccount &&
                    Find<AppBoolFn>(storage, SpecCloudForApp) is { } cloudApp)
                {
                    json.WriteBoolean("cloudAccount", cloudAccount(storage));
                    json.WriteBoolean("cloud", cloudApp(storage, appId));
                }
                if (Find<AppIntFn>(manager, SpecAppLibraryFolder) is { } appFolder)
                    json.WriteNumber("folder", appFolder(manager, appId));
                if (Find<NoArgIntFn>(manager, SpecNumLibraryFolders) is { } folderCount &&
                    Find<FolderPathFn>(manager, SpecLibraryFolderPath) is { } folderPath)
                {
                    json.WriteStartArray("folders");
                    var count = Math.Clamp(folderCount(manager), 0, 64);
                    for (var index = 0; index < count; index++)
                        json.WriteStringValue(ReadString((buffer, length) => folderPath(manager, index, buffer, length) > 0) ?? "");
                    json.WriteEndArray();
                }
                if (Find<MoveProgressFn>(manager, SpecMoveProgress) is { } move &&
                    move(manager, appId, out var moved, out var toMove, out _) && toMove > 0)
                {
                    json.WriteNumber("moveDone", moved);
                    json.WriteNumber("moveTotal", toMove);
                }
                var enabled = Find<AppDlcOutFn>(manager, SpecBIsDlcEnabled);
                var installed = Find<AppDlcFn>(manager, SpecIsAppDlcInstalled);
                // For a DLC with files, IsAppDlcInstalled says only whether they are on disk: an owned DLC not downloaded
                // yet read as unknown and got no switch. Ownership comes from the public ISteamApps, as `owned` asks it.
                var owns = dlcs.Count > 0 ? OwnershipProbe() : null;
                try
                {
                    json.WriteStartArray("dlc");
                    foreach (var dlc in dlcs)
                    {
                        json.WriteStartObject();
                        json.WriteNumber("id", dlc);
                        if (enabled is not null) json.WriteBoolean("enabled", enabled(manager, appId, dlc, out _));
                        if (installed is not null) json.WriteBoolean("installed", installed(manager, appId, dlc));
                        if (owns is { } probe) json.WriteBoolean("owned", probe.Subscribed(probe.Apps, dlc));
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                }
                finally
                {
                    if (owns is { } probe) ReleasePublic(probe.Client, probe.ClientVt, probe.Pipe, probe.User);
                }
                json.WriteEndObject();
            }
            Console.Out.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
            Console.Out.Flush();
            return 0;
        }
        finally
        {
            ReleasePipe(engine, engineVt, pipe);
        }
    }

    /// <summary>
    /// <c>set &lt;app&gt; &lt;what&gt; [value...]</c>, one change the way Steam's own
    /// Properties page makes it: <c>dlc &lt;id&gt; 0|1</c>, <c>beta &lt;name|-&gt;</c>,
    /// <c>betacode</c> (the code on stdin, never on the command line), <c>autoupdate &lt;n&gt;</c>,
    /// <c>background &lt;n&gt;</c>, <c>cloud 0|1</c>, <c>language &lt;n&gt;</c>, <c>move &lt;folder&gt;</c>.
    /// Exit 0 when Steam accepted it, 1 when it refused, 2 for a bad request, 3 when unreachable.
    /// </summary>
    private static int RunSet(string[] args, uint appId)
    {
        if (args.Length < 3) return 2;
        var what = args[2].Trim().ToLowerInvariant();
        string? Arg(int index) => args.Length > index ? args[index] : null;
        if (!TryConnectWithStorage(out var engine, out var pipe, out var engineVt, out var manager, out var storage))
            return 3;
        try
        {
            var code = what switch
            {
                "dlc" when uint.TryParse(Arg(3), out var dlc) && Arg(4) is "0" or "1" =>
                    Call<AppDlcSetFn>(manager, SpecSetDlcEnabled, set => set(manager, appId, dlc, Arg(4) == "1")),
                "beta" when Arg(3) is { } beta && IsBranchName(beta) =>
                    CallString(manager, SpecSetActiveBeta, appId, beta == "-" ? "" : beta),
                "betacode" => Console.In.ReadLine()?.Trim() is { Length: > 0 and <= 256 } secret
                    ? CallString(manager, SpecCheckBetaPassword, appId, secret)
                    : 2,
                "autoupdate" when int.TryParse(Arg(3), out var behavior) && behavior is >= 0 and <= 3 =>
                    CallBool<AppSetIntBoolFn>(manager, SpecSetAutoUpdate, set => set(manager, appId, behavior)),
                "background" when int.TryParse(Arg(3), out var behavior) && behavior is >= 0 and <= 2 =>
                    CallBool<AppSetIntBoolFn>(manager, SpecSetBackground, set => set(manager, appId, behavior)),
                "language" when int.TryParse(Arg(3), out var language) && language is >= 0 and < 64 =>
                    Call<AppSetIntFn>(manager, SpecSetCurrentLanguage, set => set(manager, appId, language)),
                "move" when int.TryParse(Arg(3), out var folder) && folder is >= 0 and < 64 =>
                    CallBool<AppSetIntFn>(manager, SpecMoveApp, move => move(manager, appId, folder) == 0),
                "cloud" when Arg(3) is "0" or "1" && storage != IntPtr.Zero =>
                    Call<AppSetBoolFn>(storage, SpecSetCloudForApp, set => set(storage, appId, Arg(3) == "1")),
                _ => 2,
            };
            Note($"Steam IPC set appId={appId}; {what}; exit={code}.");
            return code;
        }
        finally
        {
            ReleasePipe(engine, engineVt, pipe);
        }
    }

    /// <summary>Steam branch names are short identifiers; "-" is the default branch.</summary>
    internal static bool IsBranchName(string value) =>
        value == "-" || value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static int Call<T>(IntPtr target, SteamIpcStubResolver.StubSpec spec, Action<T> call) where T : Delegate
    {
        if (Find<T>(target, spec) is not { } method) return 1;
        call(method);
        return 0;
    }

    private static int CallBool<T>(IntPtr target, SteamIpcStubResolver.StubSpec spec, Func<T, bool> call) where T : Delegate =>
        Find<T>(target, spec) is { } method ? call(method) ? 0 : 1 : 1;

    private static int CallString(IntPtr manager, SteamIpcStubResolver.StubSpec spec, uint appId, string text)
    {
        if (Find<AppStringFn>(manager, spec) is not { } method) return 1;
        var native = Marshal.StringToCoTaskMemUTF8(text);
        try { return method(manager, appId, native) ? 0 : 1; }
        finally { Marshal.ZeroFreeCoTaskMemUTF8(native); }
    }

    private static string? ReadString(Func<IntPtr, int, bool> fill)
    {
        const int Size = 1024;
        var buffer = Marshal.AllocHGlobal(Size);
        try
        {
            Marshal.Copy(new byte[Size], 0, buffer, Size);
            if (!fill(buffer, Size - 1)) return null;
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static T? Find<T>(IntPtr target, SteamIpcStubResolver.StubSpec spec) where T : Delegate
    {
        var slot = FindSlot(target, spec);
        if (slot == IntPtr.Zero)
        {
            Note($"Steam IPC: method {spec.InterfaceName}::{spec.MethodName} (0x{spec.HashPrefix24:x8}) not found uniquely on this Steam build; skipped.");
            return null;
        }
        return Marshal.GetDelegateForFunctionPointer<T>(slot);
    }

    private static IntPtr FindSlot(IntPtr target, SteamIpcStubResolver.StubSpec spec)
    {
        if (target == IntPtr.Zero || SteamclientModule == IntPtr.Zero) return IntPtr.Zero;
        var moduleStart = SteamclientModule.ToInt64();
        var moduleEnd = moduleStart + ModuleSize(SteamclientModule);
        var table = Marshal.ReadIntPtr(target);
        var slots = new long[SteamIpcStubResolver.MaxVtableSlots];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = Marshal.ReadIntPtr(table, i * IntPtr.Size).ToInt64();
        return (IntPtr)SteamIpcStubResolver.FindSlot(slots, moduleStart, moduleEnd, ReadModuleMemory, spec);
    }

    private static bool ReadModuleMemory(long address, Span<byte> destination)
    {
        var moduleStart = SteamclientModule.ToInt64();
        var moduleEnd = moduleStart + ModuleSize(SteamclientModule);
        if (address < moduleStart || address + destination.Length > moduleEnd) return false;
        var temp = new byte[destination.Length];
        Marshal.Copy((IntPtr)address, temp, 0, temp.Length);
        temp.CopyTo(destination);
        return true;
    }

    private static long ModuleSize(IntPtr module)
    {
        var header = Marshal.ReadInt32(module, 0x3C);
        return (uint)Marshal.ReadInt32(module, header + 0x50);
    }

    private static bool TryConnectWithStorage(out IntPtr engine, out int pipe, out IntPtr[] engineVt,
        out IntPtr manager, out IntPtr storage)
    {
        storage = IntPtr.Zero;
        if (!TryConnect(out engine, out pipe, out engineVt, out manager)) return false;
        try
        {
            if (ConnectedUser != 0)
            {
                var candidate = Marshal.GetDelegateForFunctionPointer<GetClientInterfaceFn>(
                    engineVt[EngineGetIClientRemoteStorage])(engine, ConnectedUser, pipe, "CLIENTREMOTESTORAGE_INTERFACE_VERSION001");
                if (ReadRttiName(candidate).Contains("IClientRemoteStorage", StringComparison.Ordinal)) storage = candidate;
                else Note("Steam IPC: IClientRemoteStorage not where expected; cloud skipped.");
            }
        }
        catch (Exception ex) { Note($"Steam IPC: remote storage unavailable: {ex.GetType().Name}."); }
        return true;
    }
}
