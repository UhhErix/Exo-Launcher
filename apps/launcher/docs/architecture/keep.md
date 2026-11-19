# Keep

## What Keep is

Keep is the one record of every value Exo wrote into this PC, kept with what it replaced, so the value can be read back, watched and put back. A look runs before a launch from Exo, after the game closes and as Exo starts; it compares what Exo wrote with what the file holds now and decides from the evidence whether the game reset the value or the player changed it. The decisions are pure logic, the record is saved before it is acted on, and the page is told one plain word per game and one for the PC.

## The record

`keep.json` sits in Exo's data folder (`PathHelper.AppDataDir`, the same folder as settings.json), and any other path is only for a test flow (`Keep.ForcePathForTest`). It is per PC: it is backed up, and it is never merged by sync, because paths, device ids and profile ids mean nothing on another PC (`PlayerData.cs`, the `keep.json` row: "this PC", `OfThisMachine: true`). A save writes `keep.json.tmp`, flushes it to the disk, then swaps it in with `File.Replace`, keeping the last good copy as `keep.json.bak`. A file that cannot be read is moved aside as `keep.json.unreadable`, never read as "nothing kept"; the `.bak` is used first, and an empty document comes last. Every change goes through `Keep.Update`, under one lock; a save that fails returns false, and the caller never reports success for it.

The document has a `Version` (1) and `Games`, one `KeepGame` per profile id. That entry holds:

- `Entries`: one `KeepEntry` per value Exo wrote, by file key and line id (see below).
- `Context`: the facts in force the last time Keep saw all of them held (driver, Windows build, screens, default audio device, game facts, the file's own time), so a later difference names what changed.
- `HeldAt`: when that was. `CheckedAt`: when a look last read the values back.
- `Keeping`: what Optimize keeps on. Null when it was never pressed or was put back. Its parts: `Groups` (the group ids on), `Marks` (each file's mark of the defaults it was built from, by file key), `Program` and `Built` (the game's program and the time it had when Exo last looked after the game ran), `Facts` (the game's other build facts, such as Steam's build number or a launcher's content times), `Again` (when Exo last wrote its values again after an update; null once the game has run since) and `Pending` (Optimize was pressed before the game had a file).
- `Migrated`: the game's record was brought in from the old `kept.json` and `{id}.on.json`, which are read but never changed or removed. A migrated entry's `Owner` is the profile id alone until the first look that has the game's file fills its group in (below).
- `GameIds`: the library games this record was pressed for, by game id. The page's status is told under each, which is why `keep.status` never walks a game's install folder.
- `Signals`: evidence noted for the next look after a session (Call of Duty's marker found at a session's start). Kept when the put back it explains could not be written, and when a group failed closed or waits; used up by a session's written put back, by a session in which nothing moved and no group closed, and by a waiting Optimize's first write wherever it happens.
- `RefusedSaid`: "the game won't keep some settings" was said already; said once, until Optimize is pressed.
- `Shapes`: each file's line order as last seen, by file key, to tell a file the game rebuilt from one it only edited.

`Doc.Pc` is what this PC holds (`KeepPc`): for the driver side, the driver identity Exo's settings are held under and what it last put back ("The driver side" below); for the Windows side, Game Mode's and Game Bar's records, the windowed-games value's and the Windows build the settings were last seen under ("The Windows side" below).

Beside `Games` the document has `Stores`: one `KeepStore` per store client account, keyed "client/account id" ("steam/123"), holding for each value Exo's quiet wrote (by file, section path and key) what it held before the first write (its text as the file had it, or absent with the depth of the path the file had). It is not a game entry and has no state table: Off returns it and forgets it (`StoreQuietPass`, docs/architecture/store-quiet.md). Epic keeps its own file, `epic-quiet.json`.

One `KeepEntry` is one value:

- `Domain` and `Target`: where it is. Today the only domain is `gameFile`, and the target is the file key plus the line id.
- `Owner`: the game's profile id and the group. A value a look wrote gets both from `GroupsByLine`; a value brought in from the old files has the profile id alone until the first look with the game's file fills the group in.
- `Intended`: what Exo set. `Before`: what the line held before Exo wrote it, which Put back returns.
- `WrittenAt` and `ExoVersion`: when and by which Exo.
- `ReadBack` and `ReadBackAt`: what a fresh read said right after the write, and when.
- `Inputs`: what a target Exo worked out from the PC at that moment was worked out from (screens, default audio device, the file's own resolution), so a target that moved is told from a file that did.
- `LastSeen` and `LastCheckedAt`: the value the last look found, and when.
- `State`: one of the seven states below; it moves only by the table.
- `Found`: the value seen the last time this one was put back with no evidence; moved again with no evidence, to any value, is the player's. Cleared when Optimize takes the value back, not by a look.
- `ResetStreak`: sessions running in which this one value was put back with reset evidence; three running is a back off. Zero once a session runs without putting it back.
- `History`: the last ten things that happened to it, each a time, what was seen, the cause and the action.

## The state table

The states are `Held`, `Undone`, `PutBack`, `Yielded`, `Refused`, `Waiting` and `Skipped`. The events are `Pressed`, `PressedRefused`, `SeenHeld`, `Reset`, `Drift`, `DriftAgain`, `CloudRestore`, `BackOff`, `PutBackFailed`, `PutBackRefused`, `GroupFailsClosed` and `LineGone`. `KeepTable` has a row for every state and event pair, so a value never changes state any other way. Where a row names a set of states, every other state stays where it is and does nothing (`KeepAction.None`).

| State | Event | Next state | Action |
| --- | --- | --- | --- |
| any | `Pressed` | `Held` | `Write` |
| any | `PressedRefused` | `Refused` | none |
| any | `SeenHeld` | `Held` | none |
| `Held`, `PutBack`, `Undone`, `Waiting`, `Skipped` | `Reset` | `PutBack` | `Write` |
| `Held`, `PutBack`, `Undone`, `Waiting`, `Skipped` | `Drift` | `PutBack` | `WriteAndRemember` |
| `Held`, `PutBack`, `Undone`, `Waiting`, `Skipped` | `DriftAgain` | `Yielded` | none |
| `Held`, `PutBack`, `Undone`, `Waiting`, `Skipped` | `CloudRestore` | `Yielded` | none |
| `Held`, `PutBack`, `Undone`, `Waiting`, `Skipped` | `BackOff` | `Refused` | none |
| `PutBack`, `Undone` | `PutBackFailed` | `Undone` | none |
| `PutBack` | `PutBackRefused` | `Refused` | none |
| `Held`, `PutBack`, `Undone`, `Waiting`, `Skipped` | `GroupFailsClosed` | `Waiting` | none |
| `Held`, `PutBack`, `Undone`, `Waiting`, `Skipped` | `LineGone` | `Skipped` | none |

`Write` means Exo's value is written, worked out again from the file as it is now, and read back. `WriteAndRemember` means the same, and the value found is remembered so that the same value again is the player's. The five states a look judges are `Held`, `PutBack`, `Undone`, `Waiting` and `Skipped`: a value the player owns (`Yielded`) or the game won't keep (`Refused`) is not written over by evidence, and keeps that standing when its group fails closed or its line is gone.

The states in plain words: `Held` is Exo's value in place and read back; `Undone` is found not Exo's and not yet put back, because the write could not be made; `PutBack` is Exo wrote it again after finding it moved, and read it back; `Yielded` is the player's (or the cloud's), and Optimize takes it back; `Refused` is the target won't take it or the game keeps undoing it, said once, and Optimize tries again; `Waiting` is nothing to hold yet (no file, or a group failed closed after an update); `Skipped` is the target no longer lists this value.

## The cause rules

`KeepRules.Decide` takes a value found not at what Exo wrote, what Exo found last time it put this one back (null if never), the signals around its file, and this one value's sessions running in which Keep put it back with evidence. In this order:

1. The found value is the intended one: `Held`. Nothing else is asked.
2. A cloud signal is on: `CloudRestore`, which yields to the cloud. The signal is the profile's own check (`Profile.CloudChanged`), which for Call of Duty is Activision's cloud flag on with its `.b` files changed since the last look and no session on this PC. It is read against the previous look's `CheckedAt`, which `KeepOn` takes before `KeepEvidence.ReadBack` reads the values back; `ReadBack` now only reads and logs, and `KeepEvidence.SaveSeen` moves that time on to now once the look has passed every check that would send it home early, so a look that stops does not eat the window the next look reads the cloud against.
3. Reset evidence is on: `BackOff` when this value was put back with evidence in three sessions running (that is `KeepRules.Sessions`), otherwise `Reset`, which puts it back however few lines moved. The count is the entry's own `ResetStreak`: a session running in which this value was not put back ends it at zero.
4. No evidence, however many lines: `DriftAgain` when this value was put back once before with no evidence and has moved again, to any value; otherwise `Drift`, which puts it back once and remembers the value found.

The three signals behind "reset evidence":

- A fingerprint changed since the value was last held: the game's build or content (its program's time, Steam's build number, a launcher's content times) or the file's own mark of the defaults it was built from (`Keeping.Marks`).
- A safe-mode marker was there as the session started: Call of Duty's marker, noted in the record's `Signals`. A session whose put back was written uses the marker up, and so does a session in which nothing moved and no group failed closed (there is nothing left for the marker to explain); one whose put back could not be written keeps it, and so does one whose group failed closed or waits, so the look that re-opens the group still knows why the values moved. A waiting Optimize (`Pending`) written by the start-up or before-launch look uses it up too, whichever look writes it. Clearing a marker left behind (`ClearMarkerWhenGoneAsync`) tells three cases apart: a marker that can be read with a live process id is not cleared, one that can be read with no usable id is cleared five seconds on, and one that cannot be read at all (the game or an antivirus holds it) is never taken for gone, so the wait starts over. The one-shot delete before a launch (`ClearLeftoverMarker`) tries a marker that cannot be read three times, 100 ms apart, and deletes it anyway if it still cannot be read — it is not tried again, and a marker left there makes the game ask for safe mode — while one that becomes readable and whose process runs is left. Whether that process is the game is settled by name: `CodIsRunning` is true only when a running process named `cod` has the id the marker holds (the id alone can be reused by another program); the lookup is behind `GameSettings.ProcessIdsNamed`.
- The file was rebuilt: its line order changed (`Shapes`) and either three or more of Exo's lines moved, or every moved line is back at what it was before Exo (`KeepRules.Rebuilt`). Line order alone is no evidence, because an update adds keys.

For Call of Duty the facts are Steam's build number, the time of `ccs_history.json` and the time of `cod.exe`. The install's `main` folder is not a fact: Windows changes a folder's time whenever a file is added or removed in it, which ordinary play does, so a file added under `main` is not an update. Facts are compared only where both the record and the PC have one, so a stored fact that is no longer read changes nothing.

A group fails closed (`GroupFailsClosed`) when, after an update or while it waits, a line Exo wrote in it is gone or no longer takes its value: the whole group waits, and nothing from before is written. A window-mode line is held apart: only evidence of a reset puts it back in the look, and `GameSettings.Hold` puts those lines back whatever moved them, until Put back. A put back that could not be written (`PutBackFailed`) or that a fresh read says was not taken (`PutBackRefused`) never leaves the value shown as held.

## When a look runs

There are three looks, all started by `GameSettings.StartKeeping`: before a launch from Exo (`GameLaunching`, the look named "before launch", with the window mode put back just before it), after the game and its helpers are closed (the launcher's `GameRunStateChanged` with nothing running, which waits in `KeepEvidence.UntilFreeAsync` for as long as it takes until the game, its launcher and its helpers are gone, or the game starts again, and then looks "after the game closed"), and once as Exo starts (ten seconds in, after the start's own work, over every game whose Optimize was pressed). A game that is running when Exo starts is looked at as it closes.

Nothing is written while the game, its launcher or a helper of it runs: `GameSettings.Busy` is `Running(profile)` or any of `Profile.Helpers` running, and `KeepOn` returns before anything else when it is true. A helper can run while the game does not, and a write as the helpers start makes the game write its older values back at exit, which would read as a reset. A helper is a running process of that name whose executable is under the game's install folder (`Profile.Install`, from `GameEntry.Path`); a helper whose path cannot be read counts as running, a name with no process at all does not, and with no install folder the name alone decides. The process lookup is behind `GameSettings.HelperPaths`. A press is refused while any of them runs too: `Set` asks `Busy` first (inside its lock, before the file is read), and the page's running check is `Busy`, not `Running`, so the button never offers a write that would be refused. A file that cannot be read just now (locked by the game or an antivirus) is not touched, and the next look tries again.

Two more checks cover a game that starts while a look is reading: `KeepOn` asks `Busy` again after the files and the facts are read and again after the values are read back (nothing is judged and nothing is written; the record is left as it is), and `Commit` asks once more, directly before the first file is written, whoever its caller is. `Commit` answers three ways — written, refused because busy, or failed — and a refusal is not a failure: the look returns at once, no state moves, no streak changes and no signal is used up, so the next look judges the session all over again. A refused `Commit` logs one line and touches nothing.

The wait after a session (`KeepEvidence.UntilFreeAsync`) logs once when it is waiting on a launcher helper (its process name only), and once more when it has waited ten minutes — each sentence exactly once however long the wait goes on; from then on it looks every thirty seconds rather than every five, and keeps waiting until the game starts again or Exo closes.

## The old files

An Exo before this record kept the game's undo in `game-settings\kept.json` (what Optimize changed, by game) and what it keeps on in `game-settings\{id}.on.json`. Both are read, never changed or removed, so the older Exo can still undo for one more release.

`MigrateFrom` (`KeepGameFile.cs`) turns one game's old files into a record: every kept line an entry (what it replaced, what Exo set), a line the old files call the player's a yielded entry, and the `.on.json`'s groups, marks, program, built time and again time carried into `Keeping`. Its only caller is `GameRecord` (`KeepGameFile.cs`), which every read of a game's record goes through, and it runs only when `Keep.Load` does not hold that profile id yet: `KeepOn`, `KeptOf`, `KeepingFor`, `SaveKeeping` and the `Pressed` check reach it that way. What the old files bring is saved inside `Keep.Update` (one read-and-save under the record's lock) and only when the saved record still has no such game, so a record another look built in between is kept; `GameRecord` answers what the record holds after that, not what the old files said. Both old files are only ever read, so they stay readable for the older Exo for one release.

The old files do not say which group a line belongs to, so a migrated entry's owner is the profile id alone. The first look that has the game's file fills in each entry's group (the same `GroupsByLine` lookup `SyncRecord` uses), and records each file's `Shapes` as that look finds it, so a file the older Exo had already written is not judged as one the game rebuilt. Until that look, pressing a group takes no migrated value back.

## The status the page gets

`keep.status` (in `WebHostBridge.Keep.cs`, dispatched as an arm of the switch in `WebHostBridge.cs`) is one read-only call with no parameters. It reads the record alone (`Keep.Load`) and answers `KeepStatus.For` over it: `{ "pc": "<word>", "games": { "<game id>": "<word>", ... } }`. It writes nothing, walks no game's folder, reads no library, and it sends no profile id, no setting's name, no value and no count; a game with no status is left out of `games`. On `IOException` or `UnauthorizedAccessException` it answers `{ "pc": "none", "games": {} }`.

A game's status is told under each of its record's `GameIds`: the library game (or games) a look or a press noted it for, through `GameSettings.NoteGame`, which writes the record once and not at all when the id is there already. The call sites are the looks that start from a `GameEntry` (before a launch and after a session) and a successful Optimize press in the page's own call; the start-up look notes the whole library itself (`GameSettings.NoteLibrary`), for every entry whose profile has a record and not just the first, so a game the library lists twice (a Steam copy and a Battle.net one) has its status told under both cards. It only adds: an id is never dropped for not being in the library, because the library at start can be empty or partial (a first run, a games drive not connected, a store that did not answer), and an id left behind costs nothing since the page shows cards only for games the library has. The list holds only games the player pressed Optimize on.

The PC's words are `none`, `optimized`, `putBackAfterDriverUpdate` and `needsYourOk`. A game's are `optimized`, `putBackAfterUpdate`, `yourChangesKept`, `waitsForFirstRun` and `wontKeepSome`. A word is the enum's own name with a lower-case first letter (`KeepStatus.Word`). A game is `wontKeepSome` when any entry is `Refused` (a refused write is never `optimized`), `waitsForFirstRun` while `Pending` or any entry is `Waiting`, `yourChangesKept` when any entry is `Yielded`, `putBackAfterUpdate` when `Keeping.Again` is set, and otherwise `optimized` unless an entry is `Undone`. The PC is `needsYourOk` when something needs an OK, `putBackAfterDriverUpdate` when a driver put a game's values back outside Keep, and otherwise `optimized` when any game is `optimized`, `putBackAfterUpdate` or `yourChangesKept`, and `none` when none is. Nothing in this call asks for an OK or reports a driver put back: `keep.status` calls `ForPc` with the games' statuses alone, so those two words can be asked of it but nothing today says them.

## The driver side

Exo writes two things into the graphics driver: its NVIDIA global profile (`gpu-profile.json`) and one NVIDIA profile per game (`game-settings\{id}.nvidia.json`). Any driver install resets them, whether Exo ran it, Windows Update did, the vendor's app did or someone installed it by hand. `DriverRestore.AfterInstallAsync` puts them back after Exo's own two installers; `DriverKeep` puts them back after every other install, and reads the driver's list of setting ids again.

The driver's identity is one string: every display adapter's driver version, joined, as `GpuCapability.DriverFingerprint` reads it fresh from the driver class key (`DriverIdentity.Current`). It is read once per look and compared with `Keep.Doc.Pc.Driver`, the identity under which Exo's driver-side settings were last seen in place. A look is one of these, in this order, and `DriverKeep.Table` is the only place a step is decided — a test walks every row:

| Situation | What happens | Outcome |
| --- | --- | --- |
| the identity cannot be read | nothing | `Unknown` |
| a driver install is running | nothing (never write during an install) | `Waiting` |
| a game is running | nothing (never write while a game runs) | `Waiting` |
| the record has no driver yet | record the identity, write nothing | `Noted` |
| the identity equals the record's | nothing | `Same` |
| the identity differs and Exo has nothing of its own to put back | forget the cached ids, record the new identity | `Noted` |
| the identity differs and Exo has settings to put back | forget the cached ids; put back the global profile (if Exo had one) and the per-game profiles; if every put-back read back as applied: record the new identity, `DriverPutBack` and its time; log one line naming the old and new identity and what was put back | `PutBack` |
| ... and a put-back did not read back as applied | record nothing (so the next look tries again); log one line | `Failed` |

`Failed` three looks running for the same new identity records the identity anyway, logs once that Exo stopped trying, and answers `GaveUp`. The count is `KeepPc.DriverTries`, with `KeepPc.DriverTryOn` the identity it belongs to, so a different driver starts again; recording an identity puts both back to zero.

`KeepPc` is the PC's section of `keep.json`: `Driver` (the identity Exo's settings are held under), `DriverPutBack` and `DriverPutBackAt` (the identity Exo last put them back under by itself, and when), and `DriverTries`/`DriverTryOn`. A record written before this section loads it empty.

What the driver really took is recorded too, and never shown as applied. The global profile splits its rules by whether the installed driver lists the id (`GpuProfile.NvidiaPlan`): a listed id is written, an unlisted one is skipped, and an id the driver refuses at the write (`NvidiaProfiles.Write` carries it out as `ProfileState.Refused`) joins the skipped. `KeepPc.DriverSkipped` holds those ids in hex, for the identity in `DriverSkippedDriver`; `GpuProfileService.Apply` and `Undo` write them, and `DriverKeep.ForgetSkipped` clears them when the identity changes (a new driver may list and take them). A feature the page names (low latency, the DLSS presets: `GpuProfile.Named`) is `on` only when every id it needs was written and reads back as Exo set it; when one was skipped or refused it is `unavailable`, which is not `off`. The page gets a `partly` boolean (`gpu.profile`'s `skipped` field) and the feature states, never a count or an id; the log gets one line per apply with the ids in hex.

A look runs on Exo's start (in `DriverRestore.AtStartAsync`, after its 30-second delay and its own install check), when a driver install says it is done, and every ten minutes. The ten-minute look costs one registry read when nothing changed. Exo's own installs do not go through the table's put-back: `DriverRestore.AfterInstallAsync` puts the settings back itself and notes the identity through `DriverKeep.NoteExosOwnRestore`, so the look that follows finds `Same` and writes nothing; the watch (`DriverKeepWatch.IsExosOwn`) never looks at a step of Exo's own restoring path. "A game is running" is `GameRunningSignal.Running` (the run-state poll's own answer, which costs nothing) or `LaunchOrchestrator.AnyGameRunning` (Exo's own launch in the moment before the poll sees it); neither scans processes, unlike the page's own `MainWindow.RunningLibraryGames`.

A changed identity is not acted on at once: an installer swaps the driver in steps and may roll back. The first look that sees it only records it (`KeepPc.DriverSeen`, `DriverSeenAt`; outcome `Pending`), and the table's put-back or note runs only once the same identity has stood for two looks at least two minutes apart (`DriverKeep.Settle`). While one waits the watch looks again in two minutes instead of ten (`DriverKeepWatch.NextLook`); an identity that goes back to the recorded one forgets the sighting, and another new identity starts its own two minutes. On a PC with an NVIDIA adapter the identity is NVIDIA's driver versions only, each once and in a fixed order (`DriverIdentity.Of`), so an integrated GPU's driver update, a virtual display driver or a change in the order Windows lists adapters is not a change; a record written with the older all-adapters identity sees one change after this update, which writes nothing when the profile still reads applied.

Before a put-back NvAPI is loaded again (`NvidiaProfiles.Reinitialize`): it keeps talking to the driver it found when Exo started. The global profile is written only when it does not read `applied`, so what the player changed in NVIDIA's own panel since is not written over; an id the driver skipped or refused does not count against `applied` (`GpuProfileService.StatusOf`). Only one look runs at a time (a second one answers `Waiting`). When `keep.json` cannot be saved, this run remembers the sighting, the failed tries and the identity it settled, so a put-back is not repeated at every look; the log says once that it could not be recorded. Exo's own install keeps the list of ids the new driver refused (it was written under the new identity by the restoring step itself), and the watch treats every step of Exo's own install, from `waiting` to its final `done` or `failed`, as Exo's own.

The PC's status says `putBackAfterDriverUpdate` while `DriverPutBack` is the driver installed now and no game has run since `DriverPutBackAt`. "A game has run since" is the newest per-game last-played stamp a session's end writes in `playtime.json` (`PlaytimeService.LastExoSessionEnd`); with none, the page says it for a day (`DriverKeep.PutBackShowsFor`). Nothing in the look needs elevation, and nothing in it asks the driver for permission.

## The Windows side

Four things Exo writes into Windows itself are kept the same way the driver side is: its power plan, Game Mode, Game Bar and the per-program graphics choices (with the global "optimizations for windowed games" value). Each is read back after it is written, put back when something undoes it (where the rules below allow), and undone exactly by its Off. Everything new is in `keep.json`, in `KeepPc` (`Doc.Pc`); no new file is kept.

`KeepPc` holds, for this PC only (nothing syncs):

- `Windows`: the Windows build and update revision ("26300.9711", `PcAbout.BuildAndRevision`) under which these settings were last seen in place.
- `GameMode`: `State` (below) and `Before`, what the value held before Exo's first write (a registry value with its kind, or nothing: not set). `Before` is kept only while Exo holds it (Held, PutBackOnce).
- `GameBar`: `State` (below), and per value and per link handler what it held before Exo's first write: for each of the four DWORDs its value and kind (or not set), for each of the three links (`ms-gamebar`, `ms-gamebarservices`, `ms-gamingoverlay`) the whole key under `HKCU\Software\Classes` (every value and key under it, at most 64 parts and 64 KB; a key too big to keep is named, left alone and never written).
- `WindowedGames`: what `DirectXUserGlobalSettings` held before Exo's first write of its `SwapEffectUpgradeEnable=1` token (or `Declined`: the player undid it, so Exo does not write it again).

A value is a `RegValue` (the start-up guard's record of a registry value: string, expandable string, bytes or number). A value of another kind is never read as "not there" and never written over.

### The power plan

Every value Exo writes into its plan is read back (AC, and DC where the profile writes it). A call Windows refuses is a setting this PC has no device for ("not on this PC", not a failure). A call it accepts and whose read back differs did not take: it is named in the log, is not counted as written, and the page's answer is `ok` false with "Windows didn't take every setting of Exo's plan." The plan is still switched to, and Undo still works. The log names the plan that was on before only when it was not Exo's own ("is on (was {plan})"); with Exo's plan already on it says "already on".

At Exo's start with Exo's plan in use (`PowerPlan.Auto` → `Recheck`), every value of the profile is compared with the plan; only the values another tool changed are written again once and read back, and one log line names them. Nothing is written while a game runs. A deleted plan is still made again and switched to (`Apply`).

### Game Mode

`WindowsGameMode.Table` is the one place a step is decided; a test walks every row. The states are `NotKept`, `Held` (Exo set it and watches), `PutBackOnce` (something else turned it off once; Exo turned it on again), `Yielded` (turned off again: the player's choice; kept across restarts) and `Off` (the player turned Exo's keeping off). The events are `Start` (Exo starts or a Windows update was noticed, and the value is not on), `ChangedByAnother`, `ReadsOn`, `PlayerTurnedOnInExo`, `PlayerTurnedOffInExo` and `FoughtThreeTimesInTenSeconds`. The actions: `Record` (what the value holds now, saved before the first write), `TurnOn` (write 1, read back), `Watch`, `Unwatch`, `PutBack` (exactly what was recorded, deleting the value when it was not set, read back, then forgotten).

| State | Event | Next state | Action |
| --- | --- | --- | --- |
| `NotKept` | `Start` | `Held` | Record, TurnOn, Watch |
| `NotKept` | `ChangedByAnother` | `NotKept` | none |
| `NotKept` | `ReadsOn` | `Held` | Record, Watch |
| `NotKept` | `PlayerTurnedOnInExo` | `Held` | Record, TurnOn, Watch |
| `NotKept` | `PlayerTurnedOffInExo` | `Off` | Unwatch |
| `NotKept` | `FoughtThreeTimesInTenSeconds` | `NotKept` | none |
| `Held` | `Start` | `Held` | TurnOn, Watch |
| `Held` | `ChangedByAnother` | `PutBackOnce` | TurnOn, Watch |
| `Held` | `ReadsOn` | `Held` | Watch |
| `Held` | `PlayerTurnedOnInExo` | `Held` | TurnOn, Watch |
| `Held` | `PlayerTurnedOffInExo` | `Off` | Unwatch, PutBack |
| `Held` | `FoughtThreeTimesInTenSeconds` | `Yielded` | Unwatch |
| `PutBackOnce` | `Start` | `PutBackOnce` | TurnOn, Watch |
| `PutBackOnce` | `ChangedByAnother` | `Yielded` | Unwatch |
| `PutBackOnce` | `ReadsOn` | `PutBackOnce` | Watch |
| `PutBackOnce` | `PlayerTurnedOnInExo` | `Held` | TurnOn, Watch |
| `PutBackOnce` | `PlayerTurnedOffInExo` | `Off` | Unwatch, PutBack |
| `PutBackOnce` | `FoughtThreeTimesInTenSeconds` | `Yielded` | Unwatch |
| `Yielded` | `PlayerTurnedOnInExo` | `Held` | Record, TurnOn, Watch |
| `Yielded` | `PlayerTurnedOffInExo` | `Off` | Unwatch |
| `Yielded` | any other | `Yielded` | none |
| `Off` | `PlayerTurnedOnInExo` | `Held` | Record, TurnOn, Watch |
| `Off` | any other | `Off` | none |

The cause rule: the first time something else turns Game Mode off after Exo set it, Exo turns it on again and remembers (`PutBackOnce`); the second time it is the player's choice and Exo stops writing it (`Yielded`) until the player turns it on in Exo. Three changes found within ten seconds go to `Yielded` at once (the old fast way out, kept as `FoughtThreeTimesInTenSeconds`). A change found while Windows has just updated (`WindowsKeep`, below) is `Start`, not `ChangedByAnother`: an update's reset is Windows', so a second feature update never leaves Game Mode off for good. Off from `Yielded` writes nothing: the value is the player's now, and putting back "not set" would turn on what they turned off. Turning it on in Exo from `Yielded` or `Off` records what the value holds then (the player's own 0), so Off puts that back.

The watch is `RegistryKeyWatch` (a notification on `HKCU\Software\Microsoft\GameBar`) behind `IKeyWatch`; `Unwatch` releases every handle and clears every field, so `Watch` starts again in the same run (the old watch unregistered its handle but kept the field, so it could never start again). Exo stops the watch before it puts a value back, so its own put back is never taken for another's change. The static entry points are `WindowsGameMode.KeepOn()` (start, and each PC › Overview read), `On()` and `Off()`.

Game Mode found already on with no record (an older Exo, the player or Windows set it) is held with "what was there" recorded as Windows' own default, not set: Off then removes the value rather than "restoring" the 1 an older Exo may have written. The uninstaller gives Game Mode back too (`WindowsGameMode.GiveBack`, the `Uninstalling` rows): a held value returns to what was recorded and nothing is remembered; a value that is the player's (Yielded, Off) is left. Neither Game Mode's nor Game Bar's give-back waits on the record: when `keep.json` cannot be saved the values still go back, and the player's own Game Bar Off then says `Game Bar is back as it was, but Exo couldn't save that you turned its keeping off.`

### Game Bar

`WindowsGameBar.Table` (states `NotKept`, `Held`, `Off`; events `Start`, `PlayerTurnedOn`, `PlayerTurnedOff`, `Uninstalling`; actions `Record`, `Write`, `PutBack`) decides every step. At the first look Exo records what each of the four values and three handlers holds, whether or not it needs a write, so Off returns exactly the PC as it was; the record is saved before the first write. It then writes only what is not already as Exo wants it and reads each back; a part that did not take is named in the log and in the result ("Windows didn't take every Game Bar setting."), and is not counted as written. `Off()` puts every recorded value and handler back exactly (a handler is deleted and written again from its record; a part that was not there is deleted), reads each back, forgets the record and keeps the choice (`Off`): Exo does not turn Game Bar off again at the next start. A part that did not come back leaves the record as it was, so Off can be tried again ("Windows didn't put every Game Bar setting back."). The uninstaller (`ExoUninstall.GiveBackWindowsSettings`) calls it with `Uninstalling`, which forgets the record and does not keep a choice, so a reinstall starts over. A key Exo's first write made (and no other value of it) is left empty rather than removed.

### Graphics choices

`GpuPreference.Use` reads each program's value back after `store.Set`; a write that did not take (or threw) is not counted and is reported (`UseReport`: changed, failed). The page's two calls (`UseAndSay`, `UndoAndSay`) answer `ok` false with "Windows didn't take the setting." when nothing that should have been written was. Turning Graphics for games on starts the windowed-games value over; `Undo` puts it back too. The windowed-games value records what it held before its first write; `UndoWindowedGames` puts that back exactly (when a person changed the value since, only Exo's `SwapEffectUpgradeEnable=1` token comes out), reads it back, and keeps the player's choice (`Declined`); the uninstaller's Undo forgets it.

### A look after a Windows update

Windows resets these settings when it updates. `WindowsKeep` compares `KeepPc.Windows` with the build and update revision now, at Exo's start, and decides by one table (the only place a step is decided; a test walks every row):

| Situation | What happens | Outcome |
| --- | --- | --- |
| the build cannot be read | nothing | `Unknown` |
| a game is running | nothing (never write while a game runs; the build stays unrecorded, so the next start looks) | `Waiting` |
| the record has no build yet | record the build, write nothing | `Noted` |
| the build equals the record's | nothing | `Same` |
| the build differs | look at the four once (power plan values, Game Mode, Game Bar, the graphics choices of the programs Exo set), each by its own rules; log one line; then record the build | `Looked` |

Each look does what its own rules allow: a Game Mode the player yielded stays theirs, a Game Bar the player turned off stays off, a program's graphics choice is put back when it exists. Game Mode is told it was an update (`Start`), so it is put back without counting as another change. A look that throws is logged and the build is recorded anyway: a look is once per build, not every start.

### The page's calls

`system.health` takes four more actions, each answering `ok` and a sentence on failure: `gamemode-on`, `gamemode-off`, `gamebar-on` (Exo keeps Game Bar off) and `gamebar-off` (Exo puts it back and stops). No component calls them yet.

## How another optimizer plugs in

Keep is generic over a domain. `IKeepDomain` (`KeepGameFile.cs`) is what a domain must give the engine, and nothing more (the engine owns the record, the state table, the cause rules and the status):

- `string Name { get; }`: the domain's name, which becomes `KeepEntry.Domain` ("gameFile" today).
- `IReadOnlyDictionary<string, string>? Read()`: a fresh read of every value the owner holds, by target. Null when it could not be read (locked, unreadable); the engine changes nothing then, and never takes null for "absent".
- `IReadOnlyDictionary<string, string> Write(IReadOnlyCollection<string> targets)`: works the values out again from what is there now, writes them, reads them back, and returns what each now reads as. A target that did not take is missing from the answer.
- `IReadOnlyDictionary<string, string> Fingerprint()`: the facts that say the thing was rebuilt (a build, a version, a mark).
- `string? Default(string target)`: what the thing held by itself, or null when that is unknown.

Today only game settings files go through Keep, and the one implementation is `GameSettings.GameFileDomain`. It is not yet called by anything: the live look in `GameSettings.Keep.cs` still reads and writes the game's files itself, and `GameFileDomain` is the shape a domain takes when the look is moved onto it, alongside the driver profiles, the power plan, registry values and devices (items C and D).

## Not proven and owed

- Which Call of Duty line moves, and why, is not known. Exo now logs it: every value that moved is one line in app.log, with the file, the line, Exo's value and the value found, when it was written, when it was last seen held, and what else changed since.
- Whether Hell is Us honours its `sg.*` lines is unchecked.
- A player who changes a line again after Exo put it back once is not put back a second time: a second move with no evidence, to any value, is the player's.