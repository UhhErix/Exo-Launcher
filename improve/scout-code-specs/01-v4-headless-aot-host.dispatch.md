EXO LAB: Headless NativeAOT Host for v4 + Zero-Wait Speculative Boot (task-name: v4-headless-aot-host)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... the code itself, the pipelines, the flow, the technology ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."
> Vision: the best performance from every part of every PC, judged as Exo v4 (native Rust/Rune UI + .NET host).

## Today (Measured on Ryzen 5 5600X / 16 GB / RTX 3070)

- Real `app.log` (median of 7 launches of 3.30.2): `managedEntryMs=303 ms`, of which **`winuiMs=271 ms` (89%!)** is pure WinUI 3 bootstrap before `window-ready`; host startup RAM is **230 MB WS / 175 MB private** (`Trim:` line).
- `ExoLauncher.csproj` ships all 6 `Microsoft.WindowsAppSDK.*` packages + `Microsoft.Graphics.Win2D` + 92 unused locale folders (~60+ MB of disk), even though in v4 `exo-ui.exe` draws every pixel.
- `ExoLauncher.csproj:28-37` skips NativeAOT because of reflection JSON and `[ComImport]`, YET static scan of all 206,822 LOC found ONLY **4 `[ComImport]` interfaces** in the entire repo — all 4 are tiny WIC wrappers in `ExoLauncher/Services/CoverArtService.cs:1598-1680`!
- `Services/RuneHostSession.cs:141` still spins up WinAppSDK `DispatcherQueueController.CreateOnDedicatedThread()` and `Program.cs:114-116` still initializes WinRT `AppInstance`, plus `RuneHostSession.cs:187` sets `exo-ui.exe`'s CWD to `app\` (locking the install dir against updates).
- `ExoLauncher.SteamIpc` (2.86 MB) and `ExoCapture` (4.56 MB) ALREADY ship on .NET 10 NativeAOT in this repo.

## 1. Benchmark & Prototype First (Under 2 Days)

- Measure baseline cold start (`Process.Start` → first `exo-ui` presented frame with real 215-game Library visible) and private working set of current `ExoLauncher.exe --ui=rune` + `exo-ui.exe` on a disposable temp data dir (never touch `%LOCALAPPDATA%\ExoLauncher`).
- Measure `ExoLauncher.dll` + WinUI DLL footprint (`~224.5 MB` root today).

## 2. Phase 1 — WinUI-Free Headless Host (`ExoLauncher.Host` / v4 Entry)

- Replace WinRT `AppInstance` single-instance activation in `Program.cs:114-131` with a 40-line Win32 Named Mutex + Named Pipe activation forwarder (zero Windows App SDK dependency).
- Replace `DispatcherQueueController` in `RuneHostSession.cs:141` with a lightweight dedicated Win32 message-pump STA thread (`GetMessageW`/`DispatchMessageW` + `PostThreadMessage`) that hosts `CoreWebView2` for hidden Music players (`Spotify`/`Apple Music`/`YouTube Music`) and Store Auth windows on a hidden `CreateWindowExW` message-only HWND — zero WinUI 3, zero Win2D, zero XAML.
- Fix `RuneHostSession.cs:187`: set `exo-ui.exe`'s `WorkingDirectory = PathHelper.AppDataDir`.
- Convert the **only 4 `[ComImport]` interfaces** (`CoverArtService.cs:1598-1680`) to `.NET 10` `[GeneratedComInterface]` (or WIC C-ABI `[LibraryImport]`).
- Build Phase 1 with `PublishReadyToRunComposite=true`, `<SatelliteResourceLanguages>en-US</SatelliteResourceLanguages>`, and verify all 4,353 `ExoLauncher.Tests` + live `exo-ui` + hidden Music WebView2 + Store Auth pass.

## 3. Phase 2 — NativeAOT Host + Zero-Wait Speculative Boot

- Coordinate with `single-source-rpc-codegen` (`exo-idl`) so RPC serialization uses `[JsonSerializable]` SourceGen (eliminating `JsonSerializerIsReflectionEnabledByDefault=true`) and migrate hot `[DllImport]`s to `[LibraryImport]` (start with startup-path P/Invokes, then complete the 605 sites).
- Flip `<PublishAot>true</PublishAot>` on the v4 headless host target and verify `IlcOptimizationPreference=Speed` + size/RAM/startup.
- **Zero-Wait Speculative Boot:** Host persists a versioned, checksummed binary snapshot (`library.snap`, flat/zero-copy `rkyv` layout) on library change. On cold launch, `exo-ui.exe` mmaps `library.snap` and renders the interactive Library grid on **frame 0 (<16 ms)** while the NativeAOT host starts and connects its named pipe asynchronously in parallel, reconciling any delta seamlessly.
- **Targets:** Host cold start `<35 ms` (down from 303 ms), `exo-ui` interactive frame `<50 ms` (down from 915 ms web / ~400 ms current Rune), host private RAM `<35 MB` (down from 175 MB), install footprint `-85 MB`.

## 4. Deliver

- Branch `lab/v4-headless-aot-host` from `origin/main`.
- `improve/v4-headless-aot-host-report.md`: before/after startup timeline (ms), private RAM (MB), binary/folder size (MB), and compatibility matrix (Music WebView2, Store Auth, Discord, Capture, Shaders, HID, AutoTune).
- Working Phase 1 WinUI-free headless host + Phase 2 NativeAOT proof (or full cutover if zero blockers) passing `ExoLauncher.Tests` and live `exo-ui` parity check.
- Never install dev builds over `%LOCALAPPDATA%\ExoLauncher\app` unless explicitly approved; test in an isolated staging dir.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "v4 headless AOT host results" --body "<startup ms before/after, private RAM before/after, size delta, phase status>"`. Effort: high.
