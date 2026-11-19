# Shaders after a driver change

A graphics driver install throws away every game's compiled shaders. Exo compiles them again by itself: no scheduled task, no start-up entry, nothing to press. Code: `ExoLauncher\Services\ShaderRebuild.cs` (the table and the record), `ShaderRebuildWatch.cs` (the timer and the real calls), `ScsKillerStatus.StaleForDriver` (which games), `MainWindow.Shaders.cs` (`ShadersRebuildStartAsync`, `ShadersRebuildPauseAsync`).

## What a look does

One look a minute, and one 30 seconds after a driver install says it is done. A look reads the driver's identity (`DriverIdentity.Current`, the signal the driver side of Keep uses) and compares it with `shader-rebuild.json`'s `Driver`.

- The first look on a PC only records the identity.
- A changed identity is only noted at first; the queue is built once it has stood for two looks two minutes apart (an installer swaps the driver in steps and may roll back). Exo's own install's final "done" is taken as standing at once. Nothing is queued during an install.
- The queue is every installed game whose record says its shaders were compiled for a driver version that is not installed now (`CompiledForAnotherDriver`). A game never compiled is not queued, and Riot's games are never listed. Order: most played, then most recently played, then name.

## The table

`ShaderRebuild.Table`; a test walks every row.

| State | Driver changed | PC free | PC in use | All ready | Stalled |
| --- | --- | --- | --- | --- | --- |
| Idle | Waiting (queue) | | | | |
| Waiting | Waiting (queue) | Running (start) | | Done | Done |
| Running | Waiting (pause, queue) | Running (start again only if the engine stopped) | Paused (pause) | Done | Done |
| Paused | Waiting (queue) | Running (start) | | Done | Done |
| Done | Waiting (queue) | | | | |

"PC free" is no driver install and `PcIdle.Now`: no game, mains power, no input, sound or full screen. "Start" adds what is left of the queue to the engine's own queue and runs it (`queue.add`, `queue.start`, `queue.resume`); the engine goes on from where a stopped compile ended. "Stalled": the engine stopped by itself three times running with no more games ready, or could not be asked three times; the run ends and the log says so once. Pausing a long game many times is not a stall.

## What the page gets

`shaders.rebuild` (a call) and the `shaders.rebuild` event: `state` (idle, waiting, running, paused, done), `ready` and `total`. Nothing else. With nothing queued the state is idle whatever happened.

## The record

`shader-rebuild.json` in Exo's data folder, this PC only (in the backup registry, never synced): the driver the queue was built for, a sighting that waits, the state, the queue, and the stall count. When it cannot be saved, the run remembers which identity it queued for, so a look does not queue again every minute.

## Not built, not proven

- Nothing has compiled for real: the engine calls were never run against the engine from this path.
- Raising the driver's shader cache size and switching NVIDIA's own automatic shader compile off (the note's steps 5 and 6) are not built: both are global driver settings done in an elevated step, and the second needs the owner's yes.
- A new game is not compiled before its first launch (step 7), and shared recordings (step 8) are a separate design.
