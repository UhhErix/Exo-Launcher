# Lucid inside Exo

Decision record for "Build Lucid inside of Exo". Read with `Services/LucidCompanion.cs` (process hosting), `Services/LucidSettings.cs` (its settings file), `Services/LucidCatalog.cs` (headphone list), `Services/LucidOutput.cs` (where the EQ plays), `Services/WebHostBridge.Lucid.cs` (the RPC), `tools/Get-Lucid.ps1` (the pinned file) and `ui/src/components/settings/LucidSection.tsx` (the rows, shown on Devices > audio).

## What we are allowed to do

Lucid is by tekkusai (file marked Copyright 2026 Kurosun LLC). There is no license on GitHub. The one grant is a direct message from the author: Exo may bundle the exe, with full credit. Everything below is read against that sentence and nothing wider.

| Allowed (the grant, or ordinary use of a program on the user's own PC) | Not covered, so not done |
| --- | --- |
| Ship `Lucid.exe` byte for byte, at a pinned SHA-256 (`tools/Get-Lucid.ps1`; the installer's payload manifest verifies it) | Patching, repacking, stripping or re-signing the exe; shipping a changed build |
| Credit: the audio page ("Built on Lucid by tekkusai"), README, `THIRD_PARTY_NOTICES.md`, PRIVACY.md | Presenting Lucid's tunings, names or look as Exo's own |
| Start it with its own `/startup` switch (the one its Windows sign-in entry uses), close it as Windows closes an app at sign-out, remove its tray icon with the same remover Exo uses for store clients | Killing it as a way to control it, injecting into it, or driving its window with synthetic input (banned in Exo anyway). The one exception is ending the copy inside Exo's own folder after it was asked to close and did not, because it would otherwise block replacing that folder (see the decision below); asked of the author in question 5 |
| Read and write its settings file the way it does, with it closed, keeping every key Exo does not own | Using any settings key or file format Exo has not seen by reading the file Lucid itself writes |
| Read the headphone list out of the exe at run time for Exo's picker (`LucidCatalog`), keeping no copy | Copying that list, the corrections or the tunings into Exo, Exo's repo or Exo's own EQ |
| | Downloading the author's newer releases of `Lucid.exe` to users' PCs and running them instead of the bundled one (the update decision below): not agreed yet, the grant is for bundling. The exact question is below and is answered before a release ships it |
| Run beside Equalizer APO, which Lucid itself downloads from its author's repository on Set up | Shipping Equalizer APO, or writing Lucid's block in APO's `config.txt` (Exo only reads it, `LucidOutput`) |

No decompiling. The only part of the exe Exo opens is the .NET single-file bundle's table of contents and the one embedded resource that is a JSON list of headphone names; that is already shipped and is on the list of questions to the author below.

## The two ways "inside" could go

**(a) Hosted.** Lucid's exe stays Lucid's, as one engine Exo owns the life of. Its UI is replaced by Exo's page, its process is hidden, started and stopped by Exo, and nothing of it is visible to the user outside Exo.

**(b) Exo-native.** Exo's own code reproduces what the user gets from Lucid: a per-headphone correction curve written to Equalizer APO, switched per device and game.

### Decision: (a), and (b) is not built

(b) cannot be done inside the grant. What the user gets from Lucid is its data (the correction and the tunings per headphone, and per-game profiles) and the exe that applies it. Exo has Lucid's public README and nothing else: the repository documents no format, no switch and no API (it lists `Lucid.exe`, its hash, Equalizer APO installers and a few update pointer files). Reproducing the behavior means either reading the author's data out of the exe and keeping it (not granted) or reverse engineering the exe (not allowed here, and Exo has no right to it). A path that needs neither exists in principle, a correction set built from public measurements (AutoEq is MIT) written to an Exo-owned block of `config.txt`. That is a different product, not "Lucid inside Exo": it would compete with Lucid's own block as a second writer of the same file, it takes the one reason users choose Lucid (the author's tuning) off the table, and it is not requested here. If the owner wants it, it is its own design with its own page; it does not replace this one.

So "inside" means (a), as far as the grant and Exo's rules let it go. Most of (a) was already built; this pass closed the last visible seams.

## What "inside" means now

| Ask | Where it stands |
| --- | --- |
| Started and stopped by Exo | On Exo's start (`AppServices` > `StartWithExoAsync`) when Lucid is on; closed with Exo (`LetGoAsync`, inside the 8 s the installer allows). Turning the switch off closes it; the EQ already written keeps playing, as Equalizer APO plays the files Lucid wrote. |
| No tray, no window | Started `/startup` (no window), tray icon removed (`HideTray`: once after each start, then a 30 s look that costs a mutex check while nothing changed, none during a game). Its window exists only for Repair. |
| No separate startup entry | Taking over sets Lucid's `RunAtStartup` off, which its next start applies (`TakeOver`). A sign-in entry that comes back for the copy inside Exo (Lucid's own window can re-add it) is now listed in Apps > Startup as remove, with the user's own separate copy still keep (`StartupCatalog.json`, `Under`). |
| One uninstall | The shipped exe lives in `addons\lucid` inside Exo's app folder, so Exo's uninstall takes it; a newer one Exo downloaded lives in `%LOCALAPPDATA%\ExoLauncher\addons\lucid-current`, which `--uninstall-cleanup` deletes (the Close script ends it first). What stays is what Lucid made outside Exo: its settings (`%APPDATA%\LucidEQ`, `%ProgramData%\LucidEQ`) and Equalizer APO. APO is listed in PC > Apps, kept ("Lucid's EQ runs on it") while Lucid's settings file exists and offered for removal when it does not (`AppsKeep`, `AppsOrphans`). Exo does not delete Lucid's settings folders (they are the user's EQ; deleting is a question below). |
| Always the author's newest | Users get the author's latest Lucid, not the exe Exo shipped: see **Decision: Exo keeps the Lucid it hosts up to date** below. The shipped exe (`Get-Lucid.ps1`, pinned) is the floor: what a fresh install runs and what Exo falls back to. Lucid's own check when it starts and its Update in its own window (Repair) are unchanged and not relied on. |
| Devices / audio room | The audio page is the only place Lucid appears. One headphone pick (the page's), Game Mode, Night Shift, sounds, the EQ's output with Equalizer APO's device picker, the on/off key. |
| One row, not two | The page had "Start Lucid with Exo" and a second "Lucid" row (running, Repair). It is one row now: **Lucid**, its version and what it is doing ("Runs out of sight while Exo is open", "Restarts after your game", "Off"), Repair and the switch. Onboarding's Best settings step has the same row and wording. |
| Headphone choice | Lucid's list in Exo's picker (search, favorites, kinds), a USB or wireless headset offered by name (`LucidCatalog.Guess`), the card's name and picture follow the pick. |
| Presets per game | Not built. Lucid's Game Mode switches EQ per game by itself, in its own profiles, whose format Exo has not been given. Exo exposes the three switches Lucid has (Game Mode, keep on Alt+Tab, only while gaming). A per-game preset chosen in Exo needs the author's format (question 3). |
| Best settings step | Onboarding's PC step already ends with the Lucid rows after the other settings: Set up (says first what it will do), then the switch. The later PC room is unchanged. |
| Minimal idle cost | Lucid's own process is the cost and it is the author's (not measured here). Exo adds one 30 s timer that does a mutex check while idle, two file watchers on Lucid's two small files, and a 5 s look only while a change waits for a game. |
| Clean first run | Nothing starts before Set up on a PC without Equalizer APO (`Ready`): Lucid would download and install at start. Set up says what it will do (download 12 MB from the author's repository, Windows' permission, devices, restart). |
| Safe around games | A change to Lucid's settings restarts it (about 3 s without EQ), so it waits for the game to end (`Playing`, `Held`), survives Exo closing, and the page shows it as already made. |

## Decision: Lucid ends with Exo, and Exo makes sure it does

Found in `app.log`: `Lucid: Restart Manager didn't close it (351)`. The hidden Lucid (`/startup`, no window) does not answer Windows' sign-out close, so the polite close that was meant to end it with Exo did nothing, and the Lucid inside `app\addons\lucid` outlived Exo. A process running from the app folder stops everything that renames or deletes that folder: `tools/swap-dev.ps1` ("A process from Exo's folder is still running"), the installer's folder swap, the uninstaller's `RMDir /r`, and a settings change (which needs Lucid closed to write its file).

Should Lucid instead stay running after Exo, started by Exo, so per-game EQ switching, Night Shift and the Alt+Tab keep-on carry on? No:

- What the user hears does not need Lucid. Equalizer APO plays the files Lucid wrote, and that is the persistence that matters; only Lucid's switching (per game, per device, schedules) waits for Exo, and the shipped CHANGELOG already says so ("Lucid now closes when Exo closes").
- Exo keeps nothing running when it is closed (AGENTS.md: no tray agents; every hosted session closes with Exo). A hidden process that survives the window is exactly that, and a user who closed Exo has no way to see it or stop it, since its tray icon is removed.
- It would sit in the folder an update has to replace, so every update would need to kill a process the user believes is gone.
- Someone who wants Lucid to run without Exo has the separate copy: Exo leaves a Lucid outside its folder alone (`Choose`, `StopHostedAsync`), and turning Start with Exo off hands them theirs. There is no "stay" setting; a setting nobody asked for is more surface than this needs.

So the rule is: **the Lucid inside Exo's folder ends when Exo ends, and whatever replaces or removes the folder first makes sure of it.** `LucidCompanion.StopHostedAsync` (the logic is `LucidStop`) does it in this order, in Exo and, as the same steps in PowerShell, in the installer scripts (`Stop-ExoHostedLucid` in `tools/installer/Close-ExoLauncher.ps1`):

1. List the Lucid processes whose exe is inside Exo's app folder or in the store of the Lucid it downloaded (`LucidCompanion.HostedFolders`). A Lucid anywhere else, or in a sibling whose name only starts like the folder's (`app.previous-dev-*`, `app-old`), is not in the list and is never asked or ended. The test is a full path compared after `GetFullPath` (so `..` and capitals cannot fool it), against the folder plus a separator; a relative path never matches. The PowerShell side is stricter: the exact `addons\lucid\Lucid.exe`.
2. Ask: the Restart Manager (the sign-out close) in Exo; `CloseMainWindow` in the scripts. Wait up to 3 s, or 0.3 s when Windows would not take the ask at all.
3. Whatever still runs is ended, one process at a time, after reading it again from Windows: same id, same start time, same exe (`ProcessTermination.Kill`, which refuses anti-cheat; an id reused by another program since the list is not touched). Then it is checked gone, and one that cannot be ended is reported, not assumed stopped.

Where it runs, each path first and the restore after it:

| Path | Stops Lucid | Brings it back |
| --- | --- | --- |
| Exo closing, or Start with Exo turned off | `AppServices.Shutdown` > `LetGoAsync` > `StopHostedAsync` | Next Exo start (`StartWithExoAsync`); turning the switch off is meant to leave it off |
| Exo's own update | `AppUpdateService` stops it just before it starts the installer, so Exo is not still closing it when the installer's 8 s runs out | The updated Exo starts it with itself. If the installer does not start, the updater starts that Lucid again at once |
| Installer (update or manual) and uninstaller | `Close-ExoLauncher.ps1`, after Exo is gone: ask, wait, end the exact file | The new Exo starts it (Start with Exo); an uninstall leaves nothing to bring back |
| `tools/swap-dev.ps1` | The same function, after the installed Exo closes (an older Exo cannot do it itself) | The next Exo start |
| A settings change | `CloseAsync` stops the one in Exo's folder the same way, then asks a separate copy only | Lucid restarts after its file is written, as before |

Restoring is Exo's own start, not a stored flag: Start with Exo on means Exo starts Lucid whenever it starts, so "the user had it on" survives an update without a second record to go stale. A separate copy outside the folder is never part of any of this.

Not verified (no installed Exo, Lucid or Equalizer APO was touched): that the real hidden Lucid still ignores the Restart Manager ask on every PC, that ending it leaves its settings file whole (Lucid saves through a temporary file and a replace, so an end should lose at most the last change), and what, if anything, Lucid does on a clean exit that an end skips. The path logic and the order of steps are covered by `LucidStopTests` and `tools/Test-InstallerPayload.ps1`; the PowerShell stop was run against stand-in `Lucid.exe` copies in a temporary folder.

## Decision: Exo keeps the Lucid it hosts up to date

**Status: off.** `LucidCompanion.UpdatesAvailable` is a compile-time `false` since 3.23.0, because the author has not agreed (see The permission question below). With it off no timer starts, no request goes to GitHub, the Lucid row shows nothing of an update, and PRIVACY.md and `NetworkDestinations` carry no row for it. The code and `LucidUpdateTests` stay. To turn it on after his yes: set the constant to true, restore the PRIVACY.md row (the text of `NetworkDestinations.LucidUpdates`) and the four hosts in `NetworkDestinations`; `LucidUpdateTests.UpdatesAreOffUntilPrivacyNamesThem` fails until both are back.

The owner's rule: users must always get the author's latest Lucid, not stay frozen at the exe Exo shipped. Lucid's own updater cannot do it for a hosted Lucid (it offers an update only in its own window, which only Repair shows), and Exo's releases lag the author's. **Dormant (2026-11-09).** Exo holds no per-release SHA-256 pin (`LucidUpdate.Pins` is empty), so it does not fetch the newest release: `LucidUpdate.RunAsync` returns before any request, and nothing is offered. The mechanism below applies once a release has its pin. The design it would follow: Exo fetches the newest release itself (`Services/LucidUpdate.cs`, wired in `Services/LucidCompanion.Update.cs`).

| Question | Answer |
| --- | --- |
| Where from | Not used while dormant (no pin, so Exo contacts no host for Lucid). Once a release is pinned: Exo's own cache (updates.getexo.app, the same host as its other pinned downloads), which serves the release list and the file; each is checked against the SHA-256 Exo holds for that release. Exo never reads GitHub at run time. The release itself is the author's Lucid release (tekkusai/Lucid), mirrored by Exo. |
| When | Dormant until a release is pinned. Then once a day (the next look is 24 h after an answer, 1 h after a failed request or download), only while Lucid is on (Start with Exo, set up, and the Lucid in use is the one Exo hosts, not a separate copy of the user's) and no game or Discord call is on. A look every 30 minutes costs those checks and nothing else. Lucid off: no request. A game or call: no request, no download, no restart; a verified download waits on disk and goes in at the next quiet look. |
| What is accepted | A published final release with exactly one asset named `Lucid.exe`, uploaded, 5 to 300 MiB, with a `sha256:` digest and the link this tag makes. The download must be exactly the stated size and SHA-256, a Windows program (MZ and PE headers), at least the release's version and newer than the Lucid running, product Lucid, the company the running one has, and the signer when the running one is signed. Anything else is thrown away, the digest is remembered as refused so it is never fetched again, and the row says so. |
| Where it lives | `%LOCALAPPDATA%\ExoLauncher\addons\lucid-current\Lucid.exe`, outside the app folder, so Exo's own updates never replace or delete it, and the app folder is not held by it. `Lucid.previous.exe` beside it is the Lucid before. `lucid-update.json` in Exo's data folder records the size and SHA-256 Exo put there, what was refused and when to look next. Exo uses the stored exe only if it still matches that record (`LucidCompanion.Downloaded`). |
| What Exo runs | The newer of the shipped exe and the downloaded one (`LucidUpdate.Newest`), the shipped one when they are the same or the other cannot be read. When Exo itself ships a newer Lucid, it wins and the older download is ignored. |
| The swap | Stop the hosted Lucid (`LucidStop`: ask, wait, then end only that exact process; the downloaded copy's folder counts as Exo's), move the stored exe to `Lucid.previous.exe`, move the verified download in, read it back, start it and wait for it to hold its single-instance mutex. Any step failing, or a new Lucid that does not start, puts the Lucid before back and starts it (the shipped one when none was downloaded before), refuses that release for good and tells the row. The swap holds the same gate as a settings change, and Exo closing waits a moment for it. |
| What the user sees | Nothing when it works; the Lucid row's version line shows the version running. When an update fails its check or does not start, an error line under the Lucid row says which and that the Lucid before is back (`updateNote`, cleared by the next good look). |
| One-press rollback | The previous exe is kept and put back automatically when the new one does not start (`LucidUpdate.PutBack`). There is no button: a Lucid that starts but misbehaves is the author's to fix in his next release, which Exo then fetches. |
| Privacy | Dormant: no request. Once a release is pinned, one request a day to Exo's cache (updates.getexo.app) and the file when newer; no cookies, no account, nothing about the user. PRIVACY.md (Lucid, and the table row) and `NetworkDestinations.LucidUpdates` say so. |

### The permission question (to ask the author before a release ships this)

The grant on record is: Exo may bundle `Lucid.exe` with full credit. Downloading his newer releases to users' PCs is a different act, so it is asked, in these words:

> Hi tekkusai, thanks again for letting Exo bundle Lucid with credit. One more thing, if you are happy with it: so Exo users always get your latest Lucid and not the version we last shipped, Exo would, once a day while Lucid is on, ask the GitHub API for the newest release of github.com/tekkusai/Lucid, and when it is newer than the Lucid on the PC, download the Lucid.exe asset from that release and run it in place of the bundled one. Exo would check it against the size and SHA-256 GitHub states for the asset, check that it is a Windows program named Lucid by your company, keep the exe byte for byte (no patching, repacking or re-signing), and put the previous version back if the new one does not start. Nothing about the user is sent, and nothing is mirrored or re-hosted by us. Is automatic fetching of your releases by Exo OK with you? If you would rather we did not, or want a different release address, file name or version marker (a tag per version would let us pin and roll back more cleanly), we will change it.

Until he answers yes, the code is written and ships switched off (`UpdatesAvailable`); if he says no, delete `LucidUpdate`, `LucidCompanion.Update.cs` and the store, and go back to "pinned by Exo's release".

## Constraints to keep

- Exo is the only thing that starts the copy inside it; no Run key, no Startup folder, no scheduled task of Exo's own for it.
- Lucid Voice stays off (`KeepVoiceOff`); Exo Voice handles the mic.
- Anti-cheat and games: Lucid is a user-mode EQ on Equalizer APO, which only the audio engine loads. Nothing here touches a game process.
- A separate copy the user runs stays theirs: used only while newer, never edited (`Choose`).
- Exo reads `config.txt` and never writes it.
- Credit stays on the page that shows Lucid, in the README, notices and PRIVACY.md; the name Lucid is the feature's name in the UI.

## Not verified until the owner runs it

- That the one merged row looks right and its switch and Repair behave on a real PC.
- That a sign-in entry for the bundled copy is listed as remove in Apps > Startup (tested as a catalog lookup only).
- Idle memory and CPU of the process with Exo open (not measured).
- Anything that depends on Lucid running: this pass did not start Lucid, touch Equalizer APO or change an audio device.

## Questions for the author (tekkusai)

0. May Exo fetch your newest releases automatically? The exact question is under **The permission question**.
1. Is there a switch or setting that turns off the version check when Lucid starts (and so any update offer), so a version Exo ships stays the version the user runs?
2. Does the permission to bundle the exe cover Exo showing the headphone list that Lucid ships (names, kinds and grade, read from the exe at run time, never copied) in Exo's own picker, with the credit as it is?
3. Would you share the format of the per-game profiles in `%APPDATA%\LucidEQ\settings.json` so Exo's page can offer a profile per game, or say that Exo should not touch them?
4. When a user removes Lucid from Exo, may Exo delete `%APPDATA%\LucidEQ` and `%ProgramData%\LucidEQ` on the user's request, and run Equalizer APO's own uninstaller (with Windows' permission)?
5. Is Lucid fine being started and stopped by a host with `/startup` and a Windows Restart Manager close, or is there a quieter way you would prefer (a hosted or headless mode, a command to apply new settings without a restart)? The hidden Lucid does not answer the Restart Manager (error 351), so Exo ends the copy in its own folder when asking fails, only to replace that folder; is that acceptable, or is there a close we can ask for that it does answer?
