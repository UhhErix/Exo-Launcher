# Autopilot

Exo's realtime game optimizer: what it reads while a game is in front, what it reports after, and (later sections) what it changes while a game runs and how every change is put back. Persistent tuning (power plan, MMCSS, interrupts, services, VBS) is Best settings', not Autopilot's.

## Telemetry

Autopilot rides the session recorder's one-second look (`SessionRecorderHost`, `docs/architecture/session-recorder.md`): the same present events from the same ETW session (`PresentTrace`), so there is no second trace and the same rights apply. A library game other than Riot's is watched while it is in front; behind, nothing is read and the sampler closes.

Each second `AutopilotSampler` reads, with no administrator rights and no driver:

| Number | Source | Why this source |
| --- | --- | --- |
| Processor use | `GetSystemTimes` | What the FPS counter already reads |
| Memory in use | `GlobalMemoryStatusEx` | Same |
| Card use, temperature, video memory and its size | `GpuStats` (D3DKMT statistics, what Task Manager reads) | Any maker's card, no vendor library |
| Standby list, free and zero pages | `\Memory\Standby Cache {Reserve,Normal Priority,Core} Bytes`, `\Memory\Free & Zero Page List Bytes` (PDH, English names) | The query of the memory lists through `NtQuerySystemInformation(SystemMemoryListInformation)` needs SeProfileSingleProcessPrivilege; the counters don't (checked on this PC as a standard user, `TheLiveSamplerReadsThisPcWithoutRights`) |
| DPC and interrupt time | `\Processor(*)\% DPC Time`, `% Interrupt Time`, the busiest processor | A DPC storm sits on one processor; `_Total` averages it away |
| Processor clocks | `\Processor Information(_Total)\% Processor Performance` | Against base clock; over 100 is boost |
| Thermal limit | `\Thermal Zone Information(*)\% Passive Limit` | ACPI's own; many desktops have no zone, and then it is null |
| Each process's processor time, writes, hard faults | `NtQuerySystemInformation(SystemProcessInformation)` (`ProcessSnapshot`) | One call, nothing opened: a protected game or anti-cheat process is read the way Task Manager reads it |

There is no CPU temperature: Windows has no plain source for it, and the others need a kernel driver.

The frame times come from PresentMon's events (Intel's PresentMon is MIT; Exo uses the provider and event ids from its headers, not its code or its service). The lows are FrameView's and reviewers' definition (the average rate of the slowest 1% and 0.1% of frames), from `FrameStats`.

### Stutters

`StutterAnalyzer` (pure, `AutopilotTelemetryTests`): a stutter is a frame over 2.5 times the typical frame time (CapFrameX's stutter factor, its default) and at least 8 ms over it, so a 400 fps game's 6 ms frame is not one; typical is the median of the last 30 seconds' average frame times, so a slower scene moves the line with it. A stutter is put down to each of these that held in its second or the one before:

| Finding | Rule |
| --- | --- |
| A background app jumped | At least half a core and three times what it used in the session's smooth seconds (learned from zero, so an app first seen busy is not taken as always busy) |
| Video memory full | 97% of the card's own memory or more: what doesn't fit is read over PCIe |
| Card hot | 87 °C or more, about where GeForce and Radeon cards lower their clocks (labelled as heat, not as a measured throttle: the D3DKMT statistics give no throttle reason) |
| Processor slowed | An ACPI passive limit under 100%, or at 85% or less of the session's usual performance while at least half busy |
| Memory paging | The game's processes taking 150 hard faults a second or more |
| Driver latency | One processor 20% or more of the second in DPCs or interrupts |
| Likely shader compiling | The game writing a megabyte or more while using 30% more processor than usual (labelled "likely") |
| Unexplained | None of those |

A background app blamed for two stutters in a session is learned: the next time that game runs, Autopilot holds it back from the start.

ISLC's rule (free memory under 1 GB with a standby list of 1 GB or more) is counted, not acted on: each report keeps how many seconds met it and how many stutters came in them. That is the evidence for whether a standby-list cleaner would do anything on this PC (see Rejected).

### Reports

At the session's end (a game closed, another took over, Exo closing), with a minute of frames at least, the report is kept in `autopilot-reports.json` (this PC only, the 50 newest, nothing older than 90 days) and the page is told (`autopilot.report`). PC › Autopilot shows it afterwards; nothing is shown or said during play. Nothing leaves the PC.

Its cost: one process list a second (the same kernel call `BackgroundTrim.ProcessTable` makes, measured there at 4.6 ms for 240 processes) and one counter sample, only while a game is in front. Not yet measured beside a running game.
