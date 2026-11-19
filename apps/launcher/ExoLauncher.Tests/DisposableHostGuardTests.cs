using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Tests;

/// <summary>
/// A disposable host (EXO_LAUNCHER_DATA_DIR set, see <see cref="HostMode"/>) writes nothing outside its own data folder.
/// Each writer listed here checks <see cref="HostMode.IsDisposable"/> inside its own body, so the check runs before the
/// write and cannot be bypassed by a caller. The source test fails when a guard is removed; the behavioural tests call
/// the writers that are reachable without a window and show they return before writing.
/// </summary>
public sealed class DisposableHostGuardTests
{
    /// <summary>Each writer's file (under the repo) and a signature unique to its declaration.</summary>
    private static readonly (string File, string Signature)[] GuardedWriters =
    [
        ("ExoLauncher/Services/WebHostBridge.SteamOverlay.cs", "void SteamOverlaySoon()"),
        ("ExoLauncher/Services/WebHostBridge.SteamOverlay.cs", "Task<string?> OverlayPassAsync("),
        ("ExoLauncher/Services/WebHostBridge.SteamOverlay.cs", "Task ApplyPendingSteamSettingsAsync("),
        ("ExoLauncher/Services/WebHostBridge.SteamClient.cs", "void KeepSteamClientCurrent()"),
        ("ExoLauncher/Services/WebHostBridge.SteamClient.cs", "void JoinSteamBetaOnce("),
        ("ExoLauncher/Services/WebHostBridge.SteamClient.cs", "Task RestartSteamForClientAsync("),
        ("ExoLauncher/Services/WebHostBridge.SteamOffline.cs", "RestartSteamAsync(bool offline"),
        ("ExoLauncher/Services/WebHostBridge.SteamMode.cs", "void SteamOnlineAfterFamily("),
        ("ExoLauncher/Services/WebHostBridge.SteamMode.cs", "Task SteamBackOnlineAsync("),
        ("ExoLauncher/Services/WebHostBridge.Shaders.cs", "void KeepShadersPrepared()"),
        ("ExoLauncher/Services/WebHostBridge.Shaders.cs", "Task ShaderPrepareTickAsync("),
        ("ExoLauncher/Services/WebHostBridge.Shaders.cs", "Task<object> ShaderPrepareStartAsync("),
        ("ExoLauncher/Services/WebHostBridge.ControllerSetup.cs", "void HideControllerSoundSoon()"),
        ("ExoLauncher/Services/WebHostBridge.ControllerSetup.cs", "void ReapplyCalibrationsSoon("),
        ("ExoLauncher/Services/WebHostBridge.ControllerSetup.cs", "void ApplyGameProfile("),
        ("ExoLauncher/Services/WebHostBridge.ControllerSetup.cs", "Task<object> ControllerCalibrateAsync("),
        ("ExoLauncher/Services/WebHostBridge.ControllerSetup.cs", "Task<object> ControllerSettingsAsync("),
        ("ExoLauncher/Services/WebHostBridge.Startup.cs", "Task<object> StartupChangeAsync("),
        ("ExoLauncher/Services/WebHostBridge.Startup.cs", "Task<object> StartupCleanAsync("),
        ("ExoLauncher/Services/WebHostBridge.Display.cs", "string? SetScalingOverride("),
        ("ExoLauncher/Services/WebHostBridge.Display.cs", "string? SetVideo("),
        ("ExoLauncher/Services/WebHostBridge.Display.cs", "string? SetDisplay("),
        ("ExoLauncher/Services/WebHostBridge.Display.cs", "string? SetColor("),
        ("ExoLauncher/Services/WebHostBridge.Updates.cs", "Task<object> InstallUpdateAsync("),
        ("ExoLauncher/Services/AppUpdateService.cs", "Task<CheckResult> InstallAsync("),
        ("ExoLauncher/Adapters/EpicQuietSettings.cs", "void HandToExo(bool wanted)"),
        ("ExoLauncher/Adapters/EpicQuietSettings.cs", "void Off()"),
        ("ExoLauncher/Adapters/StoreQuietPass.cs", "(int Written, IReadOnlyList<string> NotTaken) Run(string root, bool wanted)"),
        ("ExoLauncher/Adapters/EpicQuietSettings.cs", "void ApplyIfHanded()"),
        ("ExoLauncher/Adapters/SteamLoginUsers.cs", "void ClearAutoLogin()"),
        ("ExoLauncher/Adapters/SteamLoginUsers.cs", "void RestoreAutoLogin("),
        ("ExoLauncher/Adapters/SteamLoginUsers.cs", "bool WriteRemove("),
        ("ExoLauncher/Adapters/SteamLoginUsers.cs", "bool WriteSwitch("),
        ("ExoLauncher/Helpers/Afmf3Preview.cs", "public void Write(int? value)"),
        ("ExoLauncher/Services/ExoEq.cs", "string? WriteLocked(bool force)"),
        ("ExoLauncher/Services/ExoEq.cs", "void Save(EqStore store)"),
        ("ExoLauncher/Services/ExoEq.cs", "string? LucidOff()"),
        ("ExoLauncher/Services/ExoEq.cs", "void Remove()"),
        ("ExoLauncher/Services/EqualizerApo.cs", "Task<string?> InstallAsync("),
        ("ExoLauncher/Services/LucidRetire.cs", "internal static void Once()"),
        ("ExoLauncher/Adapters/SteamOverlayConfig.cs", "void WriteFile(string path, string text)"),
        ("ExoLauncher/Adapters/SteamClientUpdate.cs", "void JoinBeta(string root)"),
        ("ExoLauncher/Adapters/SteamAdapter.cs", "public async Task<InstallResult> InstallAsync("),
        ("ExoLauncher/Adapters/SteamAdapter.cs", "public async Task<InstallResult> UpdateAsync("),
        ("ExoLauncher/Adapters/SteamLoginUsers.Offline.cs", "string? WriteOffline("),
        ("ExoLauncher/Adapters/SteamLoginUsers.Offline.cs", "bool WriteOnline("),
        ("ExoLauncher/Adapters/SteamOfflineOwed.cs", "static bool ResolvedAtStart("),
        ("ExoLauncher/Adapters/SteamOfflineOwed.cs", "static bool PutBackWhenSteamClosed("),
        ("ExoLauncher/Services/Hid/HidService.cs", "Task SendReportAsync("),
        ("ExoLauncher/Services/Hid/HidService.cs", "Task SendFeatureReportAsync("),
        ("ExoLauncher/Services/ControllerLab.Pad.cs", "bool Set(byte id, ReadOnlySpan<byte> payload)"),
        ("ExoLauncher/Services/ControllerLab.Pad.cs", "bool Output(byte[] report)"),
        ("ExoLauncher/Services/AppUpdateService.cs", "void CleanupUpdateArtifacts()"),
        ("ExoLauncher/Adapters/AmazonAdapter.cs", "Task<AuthResult> AuthenticateAsync("),
        ("ExoLauncher/Services/ExoLinks.cs", "internal static void Register()"),
        ("ExoLauncher/Services/WindowsGameBar.cs", "internal GameBarResult Deliver(GameBarEvent ev)"),
        ("ExoLauncher/Services/WindowsRegistry.cs", "public void Set(string key, string name, RegValue value)"),
        ("ExoLauncher/Services/WindowsRegistry.cs", "public void Delete(string key, string name)"),
        ("ExoLauncher/Services/WindowsRegistry.cs", "public void WriteTree(string key, RegTree tree)"),
        ("ExoLauncher/Services/WindowsRegistry.cs", "public void DeleteTree(string key)"),
        ("ExoLauncher/Services/NoteTextFiles.cs", "internal static bool Offer(string exe)"),
        ("ExoLauncher/Services/NoteTextFiles.cs", "internal static void Withdraw()"),
        ("ExoLauncher/Services/GpuPreference.cs", "internal void UseWindowedGames()"),
        ("ExoLauncher/Services/GpuPreference.cs", "internal bool UndoWindowedGames(bool remember = true)"),
        ("ExoLauncher/Services/GpuPreference.cs", "internal int Recheck()"),
        ("ExoLauncher/Services/GpuPreference.cs", "internal Used UseReport(IEnumerable<string> programs)"),
        ("ExoLauncher/Services/GpuPreference.cs", "internal Undone UndoReport(bool remember = true)"),
        ("ExoLauncher/Services/GpuPreference.cs", "public void Set(string program, string data)"),
        ("ExoLauncher/Services/GpuPreference.cs", "public void Delete(string program)"),
        ("ExoLauncher/Services/InstalledAppsEntry.cs", "internal static void Refresh(string version)"),
        ("ExoLauncher/Services/WindowsGameMode.cs", "internal GameModeLook LookReport(bool windowsUpdated = false)"),
        ("ExoLauncher/Services/WindowsGameMode.cs", "internal string? Deliver(GameModeEvent ev)"),
        ("ExoLauncher/Helpers/StartupApps.cs", "internal static bool Set(RegistryKey? run, RegistryKey approved, string id, bool enabled, DateTime utcNow)"),
        ("ExoLauncher/Helpers/StartupApps.cs", "internal static IReadOnlyList<string> HandOff(RegistryKey? run, RegistryKey approved, RegistryKey? packages,"),
        ("ExoLauncher/Helpers/StartupApps.cs", "internal static IReadOnlyList<string> GiveBack(IReadOnlySet<string> handed)"),
        ("ExoLauncher/Helpers/StartupApps.cs", "internal static IReadOnlyList<string> HandOff(IReadOnlySet<string> handed)"),
        // The id-only writers hand off to these; the guard is in the body that does the work.
        ("ExoLauncher/Services/StartupEntries.Change.cs", "internal static string? Remove(IStartupHost host, string id)"),
        ("ExoLauncher/Services/StartupEntries.Change.cs", "internal static string? Restore(IStartupHost host, string id)"),
        ("ExoLauncher/Services/StartupEntries.Change.cs", "internal static (int Removed, string? Error) Clean(IStartupHost host)"),
        ("ExoLauncher/Helpers/WindowsHdr.cs", "internal static bool Set(Screen screen, bool on)"),
        ("ExoLauncher/Helpers/WindowsHdr.cs", "internal static bool WriteRamp(string gdiName, ushort[] ramp)"),
        ("ExoLauncher/Helpers/DisplayOutput.cs", "internal static string? SetRefresh(string gdiName, int hz)"),
        ("ExoLauncher/Helpers/DisplayOutput.cs", "internal static string? SaveRefresh(string gdiName)"),
        ("ExoLauncher/Helpers/MonitorDdc.cs", "internal static string? SetBrightness(string gdiName, int value)"),
        ("ExoLauncher/Helpers/MonitorDdc.cs", "internal static string? SetContrast(string gdiName, int value)"),
        ("ExoLauncher/Helpers/NvidiaDisplay.cs", "internal static string? Write(uint displayId, OutputColor color, bool auto = false)"),
        ("ExoLauncher/Helpers/NvidiaDisplay.cs", "internal static string? SetVibrance(string gdiName, int level)"),
        ("ExoLauncher/Helpers/NvidiaDisplay.cs", "internal static string? SetHue(string gdiName, int angle)"),
        ("ExoLauncher/Helpers/NvidiaScaling.cs", "internal static string? Write(uint displayId, int code)"),
        ("ExoLauncher/Helpers/NvidiaScaling.cs", "internal static string? WriteOverride(uint displayId, bool on)"),
        ("ExoLauncher/Helpers/NvidiaVideo.cs", "internal static string? Write(string gdiName, int component, int value)"),
        ("ExoLauncher/Helpers/NvidiaColor.cs", "internal static string? Apply(WindowsHdr.Screen screen, Values values)"),
        ("ExoLauncher/Helpers/NvidiaColor.cs", "internal static (List<string> Done, List<string> Failed) ApplySaved(SettingsService settings)"),
        ("ExoLauncher/Services/Notes.cs", "internal NoteInfo? Create(string text, bool markdown, string? gameId, out string? problem)"),
        ("ExoLauncher/Services/Notes.cs", "internal string? AddPhoto(byte[] bytes, out string? problem)"),
        ("ExoLauncher/Services/Notes.cs", "private static void WriteFile(NoteEntry entry, string text, bool fresh)"),
        ("ExoLauncher/Services/Notes.cs", "private static void FollowFirstLine(NoteEntry entry, string text)"),
        ("ExoLauncher/Services/Notes.cs", "internal NoteInfo? Rename(string id, string name, out string? problem)"),
        ("ExoLauncher/Services/Notes.cs", "internal NoteInfo? SetKind(string id, bool markdown, out string? problem)"),
        ("ExoLauncher/MainWindow.Capture.cs", "internal async Task<object> CaptureSaveImageAsync(string? id, string? base64)"),
        ("ExoLauncher/MainWindow.Capture.cs", "private async Task ConfigureCaptureAsync(bool retry = false)"),
        ("ExoLauncher/Services/MouseBridgeRetirement.cs", "private static bool WriteProfilesBack()"),
        ("ExoLauncher/Services/GameSettings.CallOfDuty.cs", "internal static bool ClearLeftoverMarker(Models.GameEntry game, Func<string, (bool Read, int? Pid)>? read = null,"),
        ("ExoLauncher/Services/GameSettings.CallOfDuty.cs", "internal static void ClearShaderCaches(IEnumerable<string> caches)"),
        ("ExoLauncher/Adapters/RiotAccounts.cs", "internal static bool Restore(string dataDir, string storeDir, string key)"),
        ("ExoLauncher/Adapters/RiotAccounts.cs", "internal static void ClearLive(string dataDir)"),
        ("ExoLauncher/Services/ReplacedApps.cs", "internal Dictionary<string, LeftoverSummary> CleanLeftovers()"),
        ("ExoLauncher/Services/ReplacedApps.cs", "public async Task<bool> RemoveAsync(string fullName, CancellationToken ct)"),
        ("ExoLauncher/Services/ReplacedApps.cs", "internal async Task<ReplacedAppRemoval> RemoveAsync(string id, CancellationToken ct)"),
        ("ExoLauncher/Services/ReplacedApps.cs", "internal async Task<(List<string> Done, List<string> Missed)> AgainAsync("),
        ("ExoLauncher/Services/ReplacedApps.cs", "internal static async Task AfterDriverChangeAsync("),
        ("ExoLauncher/Services/ReplacedApps.cs", "internal static async Task<bool> RemoveAsAdminAsync("),
        ("ExoLauncher/Services/RemovalHistory.cs", "internal string Add("),
        ("ExoLauncher/Services/RemovalHistory.cs", "internal List<Removal> NewlyCleared()"),
        ("ExoLauncher/Services/RemovalHistory.cs", "internal async Task<PutBack?> UndoAsync("),
        ("ExoLauncher/Services/LeftoverScan.cs", "internal bool Delete(Leftover item)"),
        ("ExoLauncher/Services/LeftoverScan.cs", "internal List<string> Backup(string app, IEnumerable<Leftover> items)"),
        ("ExoLauncher/Services/LeftoverScan.cs", "internal string? SaveReg(string app, string text)"),
        ("ExoLauncher/Helpers/NvidiaProfiles.Apps.cs", "internal static ProfileState Write("),
        ("ExoLauncher/Helpers/NvidiaProfiles.Game.cs", "internal static GameState ForGame("),
        ("ExoLauncher/Services/LaunchOrchestrator.cs", "public void FollowSteamTransfers(IEnumerable<GameEntry> games)"),
    ];

    public static IEnumerable<object[]> GuardedWriterCases() =>
        GuardedWriters.Select(writer => new object[] { writer.File, writer.Signature });

    [Theory]
    [MemberData(nameof(GuardedWriterCases))]
    public void Each_guarded_writer_checks_the_disposable_host_in_its_own_body(string file, string signature)
    {
        var text = File.ReadAllText(Repo.PathOf(file.Split('/')));
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} is not in {file}");
        Assert.Contains("HostMode.IsDisposable", CodeOf(BodyOf(text, start)));
    }

    /// <summary>
    /// The member's own body: from the declaration to the brace that closes its first `{` (or, for `=>`, to its
    /// semicolon), so a guard in the next member or later in the file does not count.
    /// </summary>
    private static string BodyOf(string text, int start)
    {
        var code = CodeOf(text);
        var open = code.IndexOfAny(['{', '='], start);
        while (open >= 0 && code[open] == '=' && (open + 1 >= code.Length || code[open + 1] != '>')) open = code.IndexOfAny(['{', '='], open + 1);
        Assert.True(open >= 0, "no body after the signature");
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']')
            {
                if (--depth == 0 && code[open] == '{') return code[start..(i + 1)];
            }
            else if (c == ';' && depth == 0 && code[open] == '=') return code[start..(i + 1)];
        }
        return code[start..];
    }

    /// <summary>The text with comments and string literals blanked (same length), so a guard named only in a
    /// comment or a log line does not count.</summary>
    private static string CodeOf(string text) =>
        Regex.Replace(text, @"//[^\n]*|/\*[\s\S]*?\*/|'(?:[^'\\\n]|\\.)'|""""""[\s\S]*?""""""|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""",
            m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));

    [Fact]
    public void The_body_reader_does_not_count_a_comment_or_the_next_member()
    {
        const string text = "    void A()\n    {\n        // HostMode.IsDisposable\n        Write();\n    }\n\n    void B()\n    {\n        if (HostMode.IsDisposable) return;\n    }\n";
        Assert.DoesNotContain("HostMode.IsDisposable", CodeOf(BodyOf(text, text.IndexOf("void A()", StringComparison.Ordinal))));
        Assert.Contains("HostMode.IsDisposable", CodeOf(BodyOf(text, text.IndexOf("void B()", StringComparison.Ordinal))));
        const string arrow = "    int C() => Log(\"HostMode.IsDisposable\");\n    int D() => HostMode.IsDisposable ? 0 : 1;\n";
        Assert.DoesNotContain("HostMode.IsDisposable", CodeOf(BodyOf(arrow, 0)));
    }

    [Fact]
    public async Task A_disposable_host_installs_no_update()
    {
        // The test run is not a disposable host; ForceForTest makes this one.
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        var result = await new AppUpdateService().InstallAsync("0.0.1", null, TestContext.Current.CancellationToken);
        Assert.False(result.Installed);
        Assert.Equal(HostMode.Refusal, result.Message);
    }

    [Fact]
    public async Task A_disposable_host_starts_no_Steam_install_or_update()
    {
        // Both jobs start a Steam helper that writes to the installed app's log, so a disposable host refuses them first.
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        var game = new GameEntry { Id = "steam:730", Title = "Counter-Strike 2", Store = StoreKind.Steam, Installed = false, LaunchTarget = "730" };
        var adapter = new SteamAdapter();

        var install = await adapter.InstallAsync(game, null, null, TestContext.Current.CancellationToken);
        var update = await adapter.UpdateAsync(game, null, TestContext.Current.CancellationToken);

        Assert.False(install.Ok);
        Assert.Equal(HostMode.Refusal, install.Message);
        Assert.False(update.Ok);
        Assert.Equal(HostMode.Refusal, update.Message);
    }

    [Fact]
    public void A_disposable_host_leaves_a_localconfig_file_byte_identical()
    {
        // The one writer of localconfig.vdf (the overlay, Steam Input and the quiet settings all come through it).
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        var folder = Path.Combine(TestDataDirectory.Root, "localconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "localconfig.vdf");
            var before = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                "\"UserLocalConfigStore\"\n{\n\t\"apps\"\n\t{\n\t\t\"730\"\n\t\t{\n\t\t\t\"OverlayAppEnable\"\t\t\"1\"\n\t\t}\n\t}\n}\n");
            File.WriteAllBytes(path, before);

            SteamOverlayConfig.WriteFile(path, "\"UserLocalConfigStore\"\n{\n}\n");

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".exo-tmp"));
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }
    }

    [Fact]
    public void A_disposable_host_registers_no_file_type()
    {
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        Assert.False(NoteTextFiles.Offer("C:\\Exo\\ExoLauncher.exe"));
    }

    [Fact]
    public void A_disposable_host_sets_no_graphics_preference()
    {
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        Assert.Equal(0, GpuPreference.Shared.Use(new[] { "C:\\Exo\\game.exe" }));
        Assert.Equal(0, GpuPreference.Shared.Undo());
    }

    [Fact]
    public void A_disposable_host_changes_no_startup_entry()
    {
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        Assert.Equal(HostMode.Refusal, StartupEntries.Remove("run:cu:Exo"));
        Assert.Equal(HostMode.Refusal, StartupEntries.Restore("run:cu:Exo"));
        Assert.Equal((0, HostMode.Refusal), StartupEntries.Clean());
        Assert.Empty(StartupApps.GiveBack(new HashSet<string> { "x" }));
    }

    [Fact]
    public void A_disposable_host_changes_no_display_hdr_or_colour_setting()
    {
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        Assert.Equal(HostMode.Refusal, DisplayOutput.SetRefresh("\\\\.\\DISPLAY1", 60));
        Assert.Equal(HostMode.Refusal, NvidiaVideo.Write("\\\\.\\DISPLAY1", 0, 0));
        Assert.Equal(HostMode.Refusal, NvidiaDisplay.SetVibrance("\\\\.\\DISPLAY1", 50));
        Assert.False(WindowsHdr.WriteRamp("\\\\.\\DISPLAY1", new ushort[256]));
    }

    [Fact]
    public void A_disposable_host_writes_no_note_or_game_folder_file()
    {
        using var forced = HostMode.ForceForTest(true);
        Assert.True(HostMode.IsDisposable);
        var folder = Path.Combine(TestDataDirectory.Root, "notes-" + Guid.NewGuid().ToString("N"));
        var store = new NoteStore(Path.Combine(TestDataDirectory.Root, "notes-state-" + Guid.NewGuid().ToString("N") + ".json"), folder);
        Assert.Null(store.Create("A note", markdown: false, gameId: null, out var problem));
        Assert.Equal(HostMode.Refusal, problem);
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any());
        Assert.False(RiotAccounts.Restore("C:\\Exo\\no-riot-live", "C:\\Exo\\no-riot-store", "key"));
    }

    [Fact]
    public async Task A_disposable_host_writes_no_removal_record_and_undoes_nothing()
    {
        // RemovalHistory takes its file as a parameter, so the test points it at a temp file. The control run (not
        // disposable) writes a record and can undo it; the same calls as a disposable host change neither the file nor the record.
        var folder = Path.Combine(TestDataDirectory.Root, "removals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "removals.json");
            var history = new RemovalHistory(file, _ => []);
            var none = new LeftoverSummary(0, 0, 0, 0, 0);
            string id;
            using (HostMode.ForceForTest(false)) id = history.Add("Test App", none, [], [], [], DateTime.UtcNow);
            Assert.True(File.Exists(file));
            var before = File.ReadAllText(file);
            using (HostMode.ForceForTest(true))
            {
                Assert.Equal("", history.Add("Other App", none, [], [], [], DateTime.UtcNow));
                Assert.Empty(history.NewlyCleared());
                Assert.Null(await history.UndoAsync(id, _ => true, _ => Task.FromResult(true)));
            }
            Assert.Equal(before, File.ReadAllText(file));
            Assert.Single(history.All(), record => record.Id == id && !record.Undone);
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }
    }
}
