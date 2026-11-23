# Colton's two bugs: runtimes and Auto tune

Branch `UhhErix/colton-runtimes-tune`, PR to `main` (not merged). Gates: `dotnet build` Release x64 passes with 0 warnings; `dotnet test` 7971 total, 7942 passed, 28 skipped, 1 failed, and that failure is intermittent (see the end); `node tools/check-comments.mjs` passes (ratchet lowered where comments were removed). `npm run check` not run: no React files changed.

## Bug 1: runtimes "Its installer didn't start" (-1002)

**Root cause: not reproduced.** Nothing on this PC makes the elevated script fail the way it fails for Colton, and an elevated repro was not possible here (this session is not elevated, and a UAC prompt needs a person). What the code shows:

- The elevated script wrote -1002 for every failure after the installer launch, including a copy error, a sharing violation, and a Windows block. The message therefore said "didn't start" whatever happened. The most likely real causes, from the code path, are in order: the staged copy under the user's temp folder not readable by the elevated account (a different admin account), an antivirus lock on the copied installer (sharing violation), and a policy or antivirus block that Windows reports as access denied or error 225/1260.
- None of these is proven. The change makes each one visible instead of guessing.

**Changes**
- `DependencyService.Install.cs`: the script now reports each failure with its own code and the real Windows error text, base64 in the result file: -1003 "Exo couldn't copy it." (copy failed), -1004 "Windows blocked it (policy or antivirus)." (Win32 225, 226, 1260, 1261, 4551, or access denied during the run), -1002 "It couldn't start (reason)." when there is a reason, else "Its installer didn't start." Copy and hash retry on sharing violations (3 tries, 500 ms). Process.Start retries. A folder that can't be created writes a `fatal` line instead of silence. The text is logged to app.log as `Runtimes: <id> failed (<code>): <detail>`.
- `WebHostBridge.Runtimes.cs`: the result now carries `detail` so the UI can show it under Copy details later. The UI itself does not yet display it (not changed in this PR).
- `ElevatedCommands.Run`: non-zero exit codes are logged (return value unchanged).
- `WindowsUpdateDrivers`: the install script records the exception message per update in the result file (`Error`), and a non-zero exit of the elevated script is logged. The UI still shows the generic message for a failure.
- `UninstallRun`: an uninstaller that exits non-zero is logged when its process family is gone.
- Tests: `RuntimesTests` (codes -1003 and -1004 text, the script's distinct codes and base64 detail), `ElevatedScriptsTests` (Windows Update script keeps each failure's message; a failed result reads as not installed with the driver unchanged).

**Other elevated scripts checked:** EqualizerApo already logs its installer exit code. NetworkBest returns ExitCode to callers. ExoAiFans has no swallowed install error. ElevatedCommands, WindowsUpdateDrivers and UninstallRun are changed as above.

## Bug 2: Auto tune not staying applied unless Exo is open

Hypotheses, each with evidence:

| # | Hypothesis | Verdict | Evidence |
|---|---|---|---|
| H1 | NVML clock offset resets when the last NVML client exits | **Leading, unproven** | Exo holds an NVML session for its whole life (`Helpers/Nvml.cs` `_session`), which fits "holds only while Exo is open". NVIDIA's docs: the locked-clock reset happens on reboot or driver reload; the offset is not documented either way. The persistence-mode text mentions driver teardown after the last client, not clocks. Fix: a holder (below). |
| H2 | Exo undoes the tune on exit or Dispose | **Ruled out by code** | No shutdown or Dispose path calls Stock or Undo. Only UI actions, the worker and uninstall reach them. `TuneNvidia.Dispose` only closes NVML in the worker. |
| H3 | The sign-in task never applies or holds too eagerly | **Found and fixed** | The script checked the wrong provider name (`Microsoft-Kernel-Power`), so a crash never held. Corrected WHEA events (19, 20) held the tune forever. Now: holds only on Kernel-Power 41 or WHEA 18 since the script was written, or when `applying` was left behind; a script error doesn't hold; `last-run.txt` records every run. |
| H4 | Ryzen wake trigger does not fire | **Unverified on this PC** | No Power-Troubleshooter events, no Wake History; S3 only. Trigger kept as Power-Troubleshooter 1 plus logon. Needs Colton's data (below). |
| H5 | Radeon ADLX resets on exit | **Untestable here** | No AMD GPU. Radeon's note already says Undo resets AMD's tuning; not changed. |

**Changes for H1 (NVIDIA holder)**
- The sign-in script's task has no time limit (`PT0S`) for NVIDIA only. The script holds an NVML client open and, every 15 s, reads the clock offset and puts it back if the driver reads it gone. Each put-back is logged to `last-run.txt`.
- An apply writes a `keep` flag. Undo, and any test, removes the flag first and waits 16 s, so the holder never fights a test or undo. Uninstall and the no-part case remove the flag.
- `app.log` gets `Auto tune: sign-in task: <last line of last-run.txt>` once per change, so the applied, held (why) or error (message) result is visible.
- `schtasks` failures now log the exit code and stderr.
- Tests: the script has the holder call and the apply order; PT0S only with NVIDIA; ReleaseKeep removes the flag; the log line is the last line of `last-run.txt`; the sign-in hold rules (Kernel-Power 41, WHEA 18), error path does not hold.

**Decisive test, not run (owner's PC, Exo must be closed, so it needs the owner's permission):** set +15 MHz through the elevated worker, close Exo, read the offset from a fresh process (`nvidia-smi -q -d CLOCK` or NVML), then reset to 0. If the offset is gone with Exo closed, H1 is confirmed; the holder is then the fix. If it survives, H1 is ruled out and the holder can be removed.

**Changes for H3:** `AutoTune.Native.cs` script; `PlayerData.cs` claims `last-run.txt`; `docs/architecture/auto-tune.md` updated (hold rules, holder, last-run).

## What to ask Colton

1. CPU and GPU model, Windows version, and whether Windows sleeps or hibernates (S3 or S0).
2. The `app.log` lines containing `Auto tune` and `Runtimes:`, from before and after the next release.
3. The contents of `C:\Program Files\ExoLauncher Tune\last-run.txt` after a sign-in (and after a wake if it can be done).
4. For the runtimes: the `Runtimes: ... failed (code): detail` line, the Windows account type (admin or standard, and whether it's the same account Exo runs as), and whether an antivirus or Smart App Control is on.
5. Whether the native Rune PC and Settings show the same Auto tune and runtime rows as this build (not checked in this session).

## Test note

`NetworkBestTests.Overlapping_Presses_TheSecondIsToldToWait` failed in the full run this session and in 1 of 3 isolated reruns, with a real-network message ("Windows didn't put back the DNS of Ethernet"). It passes on the other reruns. Its code was not changed in this PR; it depends on the machine's network state, not this diff.
