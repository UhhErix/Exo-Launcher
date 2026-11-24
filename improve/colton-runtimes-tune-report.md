# Colton's two bugs: runtimes and Auto tune

PR #133 on `UhhErix/colton-runtimes-tune` (not merged). Gates on this commit: `dotnet build` Release x64, 0 errors; `dotnet test` 7971 total, 7943 passed, 28 skipped, 0 failed; `node tools/check-comments.mjs` ok. `npm run check` not run: no React changed.

## Bug 1: runtimes "Its installer didn't start" (-1002)

**Root cause: not reproduced.** This session is not elevated, and a UAC prompt needs a person, so the elevated repro could not run. The code shows the elevated script wrote -1002 for every failure after the installer launch, including copy errors, sharing violations and Windows blocks. The likely causes, in order: the staged copy under the user's temp folder unreadable by a different admin account; an antivirus lock (sharing violation); a policy or antivirus block (Win32 225, 1260, access denied). None is proven.

**Changes**
- `DependencyService.Install.cs`: each failure has its own code and the real Windows error text. -1003 "Exo couldn't copy it." (copy failed). -1004 "Windows blocked it (policy or antivirus)." (Win32 225, 226, 1260, 1261, 4551, or access denied). -1002 "It couldn't start (reason)." when there is a reason, else "Its installer didn't start." Copy and hash retry on sharing violations (3 tries, 500 ms); Process.Start retries. A folder that can't be created writes a `fatal` line. app.log: `Runtimes: <id> failed (<code>): <detail>`.
- `WebHostBridge.Runtimes.cs`: results carry `detail` for the UI; the UI does not display it yet.
- `ElevatedCommands.Run`: non-zero exit codes logged; return value unchanged.
- `WindowsUpdateDrivers`: the install script records each update's exception message (`Error`); a non-zero exit of the elevated script is logged.
- `UninstallRun`: a non-zero uninstaller exit is logged when its process family is gone.
- Tests: `RuntimesTests` (-1003 and -1004 text, distinct codes and base64 detail); `ElevatedScriptsTests` (a failed Windows Update item reads as not installed with the driver unchanged).

## Bug 2: Auto tune not staying applied unless Exo is open

| # | Hypothesis | Verdict | Evidence |
|---|---|---|---|
| H1 | NVIDIA's clock offset is dropped when the last NVML client exits | **Unlikely on Windows; unproven** | Windows: the kernel-mode driver is loaded at startup and stays loaded until shutdown ([NVIDIA Driver Persistence](https://docs.nvidia.com/deploy/driver-persistence/latest/pdf/Driver_Persistence.pdf)). The persistence-mode option that governs teardown after the last client is listed by nvidia-smi as Linux only ([nvidia-smi man page](https://man.archlinux.org/man/nvidia-smi.1.txt); [legacy persistence mode](https://docs.nvidia.com/deploy/driver-persistence/persistence-mode-legacy.html)). NVML's own docs say locked clocks reset only on reboot or driver reload, and say nothing about the offset. So there is no documented mechanism for the offset to clear when Exo closes on Windows. The holder that was built for this is removed (see below). |
| H2 | Exo undoes the tune on exit or Dispose | **Ruled out by code** | No shutdown or Dispose path calls Stock or Undo. Only UI actions, the worker and uninstall reach them. |
| H3 | The sign-in task never applies, or holds too eagerly | **Found and fixed** | The script checked the wrong provider name (`Microsoft-Kernel-Power`), so a crash never held. Corrected WHEA events (19, 20) held the tune forever. Now it holds only on Kernel-Power 41 or WHEA 18 since the script was written, or when `applying` was left behind. A script error does not hold. |
| H4 | Ryzen wake trigger does not fire | **Unverified on this PC** | No Power-Troubleshooter events, no Wake History; S3 only. Trigger kept (Power-Troubleshooter 1, plus logon). |
| H5 | Radeon ADLX resets on exit | **Untestable here** | No AMD GPU. |
| H6 (new) | Constrained Language Mode: Smart App Control or WDAC puts PowerShell in CLM, so `Add-Type` fails in the task | **Possible; not yet seen** | In CLM, the sign-in script's `Add-Type` throws, so nothing applies at sign-in and the tune only holds while Exo applies it itself. The error note now records `$ExecutionContext.SessionState.LanguageMode` in `last-run.txt` and in app.log, so the next sign-in run shows it. |

**What is in place for H1 (no holder)**
- The sign-in script, after its 120 s wait, reads the NVIDIA offset back with `nvmlDeviceGetClockOffsets` and writes `applied: driver reads <n> MHz (wanted <m>)` to `last-run.txt`.
- When Exo starts with an applied NVIDIA tune, it reads the offset once and logs `Auto tune: at start the driver reads <n> MHz, applied <m>`. Here `<m>` is the value the sign-in script was given. A mismatch means the offset dropped while Exo was closed, which confirms H1 from Colton's log. A match, after a real sign-in, rules H1 out.
- The keep-alive holder (a SYSTEM process with no time limit that put the offset back every 15 s) is removed. Reasons: H1 is unlikely on Windows; while the holder is open it only changes the offset for bad reasons (a TDR or driver reset after an unstable tune, or the user's own Afterburner), so it could turn an unstable tune into a crash loop and fight the user's tools; and it was a never-ending SYSTEM PowerShell process built on an unproven guess.

**Decisive test, if the evidence is needed on the owner's PC:** apply NVIDIA, then close Exo, read `nvidia-smi -q -d CLOCK`, start Exo, read the `at start` line. Needs the owner's permission to close Exo.

**Changes:** `AutoTune.Native.cs` (holder, keep flag, PT0S, ReleaseKeep removed; read-back and language-mode note added; `AtStart` pure function); `TuneNvidia.ReadOffset`; `App.xaml.cs` calls `CheckAtStart`; `PlayerData.cs` (`last-run.txt` claimed); `docs/architecture/auto-tune.md` (no holder, read-back, start check, what to ask).

## What to ask Colton

1. CPU and GPU model, Windows version, whether Windows sleeps (S3) or hibernates.
2. From the next sign-in: the `Auto tune: sign-in task:` line and `last-run.txt`. The `applied: driver reads` line settles whether the offset survived sign-in.
3. After Exo starts with a tune applied: the `Auto tune: at start the driver reads` line. A mismatch with the applied value means the offset dropped while Exo was closed.
4. The `(language mode: …)` text, if the sign-in line is an error. `FullLanguage` is fine; `ConstrainedLanguage` is H6.
5. For runtimes: the `Runtimes: … failed (code): detail` line, whether the account is an administrator and the same account Exo runs as, and whether antivirus or Smart App Control is on.
6. Whether the native Rune PC and Settings show the same Auto tune and runtime rows (not checked in this session).

## Test note

`NetworkBestTests.Overlapping_Presses_TheSecondIsToldToWait` failed once in an earlier run and passed in the last full run. It depends on the machine's real network state, not this diff.
