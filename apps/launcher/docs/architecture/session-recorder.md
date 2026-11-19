# Session recorder

## What it is

Exo's own measurement of frame times. It exists so that a later step (a setting up or down per session, a lab comparison) can say what a settings change did on this PC from numbers Exo measured itself, not from a vendor's claim. It has no screen: no state is shown to the player yet. It stores locally and sends nothing.

## When it runs

Whenever a library game is in front and Windows lets Exo count its frames (ETW present events, the same `PresentTrace` the FPS overlay uses). Exo's FPS overlay may be off: `FpsCounter.Wants` decides what a followed game's frames are wanted for (the overlay, and the recorder), and the trace runs for either. Windows allows one trace session of Exo's name, so the recorder shares `FpsCounter`'s trace; `PresentTrace.Run(who, on)` keeps one wish per reader (overlay, recorder) and sends events while either wants them. The recorder reads frames from its own place in each process's ring (`FpsWindow.Drain`), so the overlay's twice-a-second read is not disturbed.

The recorder measures every library game except Riot's, anti-cheat games included. It reads Windows' own present events (ETW), the same events the FPS counter has always used, and never opens, reads or writes a game's process or folder: Windows reports each present, Exo keeps those of the game's processes by process id. Nothing is injected or hooked and nothing is written in a game folder. Riot's games (`AntiCheatCheck.IsRiot`) are never recorded. Whether a game is recorded is decided from the game's identity and that Riot rule alone (`SessionRecorderHost.Records`, a test fixes this: an anti-cheat folder changes nothing). `AntiCheatCheck` is not used around `PresentTrace` or `FpsCounter` today (nothing is changed in a game), and the recorder adds no new kind of contact. A game that presents through Vulkan, OpenGL or a flip-only path gives frames only through the kernel's events, which the recorder's own read asks for under the same gate as the overlay's.

Without the right to start a trace the recorder does nothing and its state is `WaitingForRights` (`SessionRecorder.NeedsRights`). Exo asks again every ten minutes while a game runs.

## The lifecycle

One table, `RecorderTable.Step`, walked by a test row by row (`EveryStateAndEventHasARowAndNoStateLacksAnExit`).

| State | GameFront | GameBack | GameClosed | SessionBoundary | RightsOk | RightsMissing | FlushDone | Exit |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `Idle` | `WaitingForRights` (check rights) | stay | stay | stay | stay | stay | stay | `Stopped` |
| `WaitingForRights` | stay | stay | `Idle` | stay | `Recording` (begin) | stay | stay | `Stopped` |
| `Recording` | stay | `Paused` | `Flushing` (save) | `Flushing` (save) | stay | stay | stay | `Stopped` (save) |
| `Paused` | `Recording` (resume) | stay | `Flushing` (save) | `Flushing` (save) | stay | stay | stay | `Stopped` (save) |
| `Flushing` | stay | stay | stay | stay | stay | stay | `Idle` | `Stopped` |
| `Stopped` | stay | stay | stay | stay | stay | stay | stay | stay |

Events are derived from what is observed once a second (`Observation`), so one that a state ignores is sent again at the next look. `SessionBoundary` is another game, or another settings version, taking over. Every way out of `Recording` and `Paused` saves. A session with no frames is not stored.

When it is saved: the game closes, another game or settings version takes over, the bridge is detached (`FpsCounter.Stop`: Exo is closing, or the web page was rebuilt; the recorder goes on and the next look records again), and Exo's real shutdown (`FpsCounter.Shutdown`, from the window's close: the only thing that stops the recorder for good). While recording, the session so far is also written every 5 minutes of measured time, replacing its own earlier write (same game, version and start), so the file does not grow and a crash loses at most 5 minutes. A save that fails (the file is held by another program, or can't be written) keeps the record in memory and is tried again at every look until it is kept; nothing already in the file is replaced.

## What is stored

`session-recorder.json` in Exo's data folder (`SessionStore`), one JSON object with a list of sessions. Per session, per game and per settings version:

- `game`: the library game id. `version`: the settings version, below. `startedUtc`.
- `seconds`: the sum of the frame times measured (time in front, drawing). `frames`.
- `averageMs` and `averageFps`.
- `low1Fps` and `low01Fps`: the frame rate of the slowest 1% and the slowest 0.1% of frames (the average of those frames' rate; at least one frame).
- `stalls`: frames over 50 ms.
- `histogram`: 50 counts, one bucket per millisecond from 0 to 50 (bucket 49 holds 49 up to and including 50). Frames over 50 ms are the stalls and are not in it.
- `usable`: false for a session with under 300 seconds measured: it is stored but is no evidence for comparing settings.

A gap between two presents of 5 seconds or more is not a frame (the game was not drawing: a loading screen, a pause), and is not counted. The lows are worked out from a 0.01 ms histogram in memory (exact to 0.005 ms), so a session of any length is held in fixed memory; the statistics are pure functions (`FrameStats.Of` over a list, `FrameStats.Accumulator` one frame at a time) tested by replay of synthetic lists with known answers.

GPU-busy and CPU-busy share are not stored. `PresentTrace` reports only when frames were presented; it gives nothing from which those could be worked out, and nothing is invented for them.

### The settings version

`SettingsVersion.ForGame`: the first 16 hex characters of a SHA-256 over the Keep entries of the game whose value Exo's is in the file (state `Held` or `PutBack`; `target`, then `intended`), sorted, so the same entries in any order are one version and a changed kept value is another. A value the player took over (`Yielded`), one that could not be written (`Undone`), one the game won't keep (`Refused`) and one with no line to hold (`Waiting`, `Skipped`) is not in the version, so it starts a new one. A game with no such entries has the version `none`. The entries are found by the library game id in the record's `GameIds`. The version is read again once a minute while a game is followed.

### How much, and for how long

At most 20 sessions per game and settings version, nothing older than 90 days, and at most 300 sessions in the file (the oldest go first). A session is about 0.5 KB; the file stays under 256 KB (`TheFileStaysSmall`). It is written beside and swapped in. A file that cannot be read just now (held by another program) is never written over: the save waits. A file that was read and is not the document (damaged, empty) is moved aside to `session-recorder.json.bad` and counts as no sessions.

The file is in the backup registry as this-PC only (`PlayerData.cs`): frame times mean nothing on another PC's hardware.

## What it never does

- Sends anything anywhere: no telemetry, no upload, no network call.
- Writes in a game folder, opens a game process, injects or hooks, or changes a game or a driver.
- Runs for a Riot game.
- Starts anything with Windows, or keeps a timer or a trace after the game closes or Exo exits (the one-second timer exists only while a game is followed).
- Shows anything to the player. A status for the UI is owed to a later item.

## Its own cost

Measured only for the statistics path, in a test harness (`TheStatisticsPathHandlesALongSessionAndItsCostIsMeasured`): a long synthetic session fed through `FrameStats.Accumulator.Add`. It is not a game's CPU share and does not include the trace or the callback that runs for every present. The plan's "under 0.5% of one core" is a target; no game has been run, so it has not been measured. See the report of the build for the number.

## Not done

- The lab runner (alternating A and B over built-in benchmarks).
- A status for the UI, and the privacy text, the help and the changelog (item G).
- GPU-busy and CPU-busy.
