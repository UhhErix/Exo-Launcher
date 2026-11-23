# Auto tune: research and decisions

The ask: one press, Exo finds a lower-voltage or faster setting for a CPU or GPU, proves it, and goes away by itself if it goes wrong. Researched November 2026. Marks: **solid** (a maker's header, doc or source code, or read on this PC), **inferred** (reasoned from solid facts, not stated or not seen), **unknown** (looked and found nothing), **reports** (forums and tool authors, more than one but not a maker).

Nothing here has run on real hardware with a value changed. The only hardware read is the NVML getter table below, which is read-only.

## What ships, in one table

| Part | Route | Driver | State |
| --- | --- | --- | --- |
| NVIDIA GeForce, Maxwell or newer, desktop | NVML clock offset and clock lock, Exo's own search | The display driver's own, nothing added | Built. Fakes only. The owner's RTX 3070 is the first real run |
| AMD Radeon, desktop | AMD's own auto tuning through ADLX, Exo's test before and after | The display driver's own | Built. Fakes only, and no Radeon here to try it on |
| AMD Ryzen 5000, 7000, 9000 desktop | All-core Curve Optimizer through PawnIO, Exo's search | PawnIO, which the user installs | Built, **hidden for everyone** (`AutoTuneService.RyzenAvailable` is false) until PawnIO has been tried with Vanguard and EAC running (see The owner's decision) |
| Intel Arc | IGCL exists and can do it | The display driver's own | Not built: no card to run it on, see GPU › Intel |
| Intel CPU | Locked on most parts | Would need a driver write to MSR 0x150 | Not built, see CPU › Intel |
| Curve Shaper | Commands known, not documented | PawnIO | Not built, see CPU › AMD |
| Laptops | - | - | Out, nothing shows |

## CPU, AMD

**The public SDK can't set anything. Solid.** AMD's Ryzen Master Monitoring SDK is read-only: "a subset, offering read-only access" with about 20 calls, version 3.0.1.4971 (February 2026). [AMD](https://www.amd.com/en/developer/ryzen-master-monitoring-sdk.html). Ryzen Master itself has no documented interface for another program.

**Curve Optimizer is an SMU mailbox message. Solid as to what the tools send; AMD documents none of it.** From ZenStates-Core ([Zen3Settings](https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Settings/Zen3Settings.cs), [Zen4Settings](https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Settings/Zen4Settings.cs), [Zen5Settings](https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Settings/Zen5Settings.cs), [SetPsmMarginAllCores](https://github.com/irusanov/ZenStates-Core/blob/master/Hardware/Smu/Commands/SetPsmMarginAllCores.cs)):

| | Zen 3 (Vermeer, 5000) | Zen 4 (Raphael, 7000) | Zen 5 (Granite Ridge, 9000) |
| --- | --- | --- | --- |
| MP1 mailbox (message, response, argument) | 0x3B10530, 0x3B1057C, 0x3B109C4 | same | same (inherits Zen 4) |
| Set all cores | MP1 0x36 | MP1 0x36 (RSMU 0x7) | same as Zen 4 |
| Set one core | MP1 0x35 | MP1 0x35 (RSMU 0x6) | same as Zen 4 |
| Read one core | RSMU 0x7C, marked "Not sure" in the source | RSMU 0xD5 | same as Zen 4 |
| Range the source comments give | -30 to +30 | -50 to +50 | -50 to +50 |
| Curve Shaper | none | RSMU 0xA6 set, 0x84 read (in the Zen 4 table, used for Zen 5) | same |

The argument is the margin as a 16-bit two's complement in the low half. Per-core masks need the physical CCD and core, read from fuses or the APOB, because disabled cores are skipped: that is a further read with its own risk of a wrong core. **Decision: all-core only.** One value for the CPU, set with 0x36, tested one core at a time. It is limited by the weakest core, so it leaves gains on the table, but it can't address the wrong core. ZenStates-Core is GPL-3.0, so Exo copies no code from it: only these facts, written independently.

**Curve Shaper. Solid that it exists; its bands are not public.** AMD: "selective voltage adjustments across temperature and frequency bands", Ryzen 9000 only. [Ryzen Master 3.1.0 notes](https://www.amd.com/en/resources/support-articles/release-notes/RN-RYZEN-MASTER-3-1-0.html). ZenStates-Core knows five frequency tiers with three margins each, and has an unnamed bit in the argument. Testing a temperature band needs the CPU held in that band, which Exo can't do. **Not built.** Curve Optimizer alone is what every automatic tool in Prior art tunes.

**A value set through the SMU is lost at reboot. Reports, consistent, and from the tool that depends on it.** CoreCycler's own config: the adjusted values "are NOT permanent, so after a regular reboot they will not be applied anymore". [config](https://github.com/sp00n/corecycler/blob/master/configs/default.config.ini). The PBO2 Tuner guide: you do it again "every time you restart or wake from sleep", which is why it builds a scheduled task. [guide](https://github.com/PrimeO7/How-to-undervolt-AMD-RYZEN-5800X3D-Guide-with-PBO2-Tuner). AMD doesn't state it. **Inferred:** the SMU re-initialises from the BIOS's values at power-on, and sleep loses them too, so Exo re-applies on wake as well as at sign-in. This property is the reason the CPU route can be safe at all: a value that crashes the PC is gone when it comes back.

**Whether Curve Optimizer needs PBO on in the BIOS: unknown.** Guides say Curve Optimizer sits under PBO in the BIOS; tools setting it at runtime don't check. If it has no effect, the before and after simply show no change, and Exo says so.

**Parts excluded: inferred from AMD's own locks.** 5800X3D, 5700X3D, 5600X3D (Curve Optimizer is locked by AMD on those; the PBO2 Tuner guide exists because of it). APUs (other mailboxes). Threadripper and EPYC (not tested by anyone Exo can cite here).

### The driver choice

SMU access needs a kernel driver. **Solid.**

| | PawnIO | WinRing0 | AMD's Ryzen Master driver |
| --- | --- | --- | --- |
| Signing | Signed; 2.1.0 (Feb 2026) has a new signature; 2.2.0 is the latest (March 2026) | Signed, old | Signed by AMD |
| Status | Used by LibreHardwareMonitor, Fan Control, CoreCycler and ZenStates-Core | Defender flags it as `HackTool:Win32/Winring0` ([report](https://it.slashdot.org/story/25/03/14/1351225/windows-defender-now-flags-winring0-driver-as-security-threat-breaking-multiple-pc-monitoring-tools)); the projects above left it for that reason ([CoreCycler releases](https://github.com/sp00n/corecycler/releases)) | Has had a privilege escalation, [CVE-2020-12928](https://cve.circl.lu/cve/CVE-2020-12928), fixed in 2.2.0.1543 |
| Who can open it | SYSTEM and Administrators only: the INF sets `D:P(A;;GA;;;SY)(A;;GA;;;BA)` ([PawnIO.inf.in](https://github.com/namazso/PawnIO/blob/master/PawnIO/PawnIO.inf.in)) | Any user on old builds | Not public |
| What it runs | Signature-checked bytecode modules ([vm.cpp](https://github.com/namazso/PawnIO/blob/master/PawnIO/src/vm.cpp)) | Raw port and MSR access | Private |
| Interface | IOCTL, public | IOCTL | Undocumented, and using it behind AMD's back would be reverse engineering a licensed product |
| License | GPL-2.0-or-later, with an exception for "independent modules that communicate with PawnIO solely through the device IO control interface" ([README](https://github.com/namazso/PawnIO/blob/master/README.md)); modules LGPL-2.1 ([modules](https://github.com/namazso/PawnIO.Modules)) | Old BSD-style, abandoned | AMD's EULA, no right to ship it |
| May Exo redistribute it | The driver: only with the GPL's duties, and the website says "© 2025 namazso. All rights reserved" beside the open-source edition, so **ask the author before bundling** (admin@namazso.eu is the licensing contact). The module: yes, under the LGPL | Not worth asking | No |

**Decision: PawnIO, and Exo does not bundle or install the driver.** The user installs it from [pawnio.eu](https://pawnio.eu); Exo checks for 2.1.0 or newer in the registry's uninstall key and otherwise offers to open that page. Exo talks to it only through IOCTL, which is what the exception covers. Exo ships the signed `RyzenSMU.bin` from [PawnIO.Modules 0.2.11](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11) under the LGPL, in `Assets/pawnio/` with its COPYING, noted in THIRD_PARTY_NOTICES. The driver checks the module's signature itself, so a swapped file won't load. It was downloaded as data and never run here.

[`RyzenSMU.p`](https://github.com/namazso/PawnIO.Modules/blob/main/RyzenSMU.p) **solid:** it checks for an AMD CPU, exposes `ioctl_send_smu_command` (RSMU, any id up to 0xFF), and `ioctl_read_smu_register` and `ioctl_write_smu_register` limited to 0x3B10000-0x3B10FFF, 0x56000-0x5AFFF and 0x6F000-0x6FFFF. The MP1 mailbox sits in the first range, so MP1 goes through register writes, and temperature (SMN 0x59800, [k10temp](https://github.com/torvalds/linux/blob/master/drivers/hwmon/k10temp.c)) in the second. Callers should hold `\BaseNamedObjects\Access_PCI`, which Exo does. PawnIO 2.1.0 changed something that broke CoreCycler's detection and its bundled tools until they were fixed ([releases](https://github.com/sp00n/corecycler/releases)), so a future PawnIO can break this too: Exo then says the driver didn't answer.

**A safety consequence of the driver. Inferred.** The INF is `StartType = 3` on a root-enumerated device, so once installed it is loaded at boot whether Exo uses it or not, until the user uninstalls it from Windows. Exo can't load it only for the test. This is why the anti-cheat answer below matters.

## CPU, Intel

**Not built. Solid that it's locked on most parts; unlocked K chips are the only real case.** Plundervolt ([CVE-2019-11157, INTEL-SA-00289](https://www.intel.com/content/www/us/en/security-center/advisory/intel-sa-00289.html)) covers 6th-10th generation, fixed by firmware. Afterwards "Undervolt Protection" reached 12th and 13th generation through BIOS updates, letting makers block software undervolting; on some systems even a small undervolt with updated microcode caused a large performance drop or a crash on boot. Reports, not Intel. [r/intel thread](https://libreddit.winscloud.net/r/intel/comments/10slejm/intel_blocks_undervolting_the_whole_story/jf7wmzm/?context=3). A program can't reliably tell ahead of time which it is, and a crash on boot breaks the rule this feature is built on. PawnIO's `IntelMSR` module does allow writing the OC mailbox ([IntelMSR.p](https://github.com/namazso/PawnIO.Modules/blob/main/IntelMSR.p)), and CoreCycler's Intel path still uses WinRing0. No Intel CPU here to test on. Left for later only if the owner has one.

## GPU

### NVIDIA

**NVML is public, documented and enough. Solid.** [`nvml.h`](https://github.com/NVIDIA/nvidia-settings/blob/main/src/nvml.h), the library every NVIDIA driver installs:

- `nvmlDeviceSetClockOffsets`: "For Maxwell or newer", "Requires privileged user". Exo uses the graphics clock in P0. The older `nvmlDeviceSetGpcClkVfOffset` is deprecated in its favor.
- `nvmlDeviceSetGpuLockedClocks`: Volta or newer, admin. "After system reboot or driver reload GPU clocks go back to their default value."
- `nvmlDeviceSetPowerManagementLimit`: "Limit is not persistent across reboots or driver unloads." Exo doesn't touch this.

**Read on this PC's RTX 3070, driver 621.12, read-only:** `nvmlDeviceGetClockOffsets` answers success, current 0, range -1000 to +1000 MHz. `nvmlDeviceGetGpcClkMinMaxVfOffset` answers not supported. Maximum graphics clock 2100 MHz; power limit 100-265 W, default and current 240 W. So the current call works on a GeForce on Windows for reading. **Unknown: whether the setter does**; `fully supported devices` in NVIDIA's words can exclude GeForce. If it doesn't, the first step reports it and goes back. Using NVML for this on GeForce is established on Linux. **Inferred** for Windows.

**How an offset becomes an undervolt. Inferred, and the well-known recipe.** A positive offset moves the whole voltage-frequency curve up, so each clock is reached at a lower voltage. Lock the top clock to what the card reached at stock and it stops there, at less voltage: same speed, less heat ("Cooler"). Without the lock the card clocks higher up to the same top voltage ("Faster"). Neither can ask for more voltage than the curve's stock top. The two calls compose on Linux; on Windows it's the first real run that shows it.

**Whether the offset itself is lost at reboot: inferred yes.** The header states it for the clock lock and the power limit but not the offset. Afterburner has "apply at startup" because of it. Exo doesn't lean on it: after a crash, the read says whether an offset is still there, and the row says Undo.

**The undocumented route isn't needed.** Afterburner writes the VF curve through private NVAPI calls that aren't in the [public header](https://github.com/NVIDIA/nvapi/blob/main/nvapi.h), which has `NvAPI_GPU_GetPstates20` but no setter. Afterburner's RTCore64 driver is itself on the blocklist ([CVE-2019-16098](https://splintersfury.github.io/KernelSight/case-studies/CVE-2019-16098)).

**NVIDIA's own Automatic Tuning isn't reachable.** A toggle in the NVIDIA App's overlay, 10-20 minutes, rescans monthly, and "will not void your hardware warranty" says NVIDIA. [TechRadar](https://www.techradar.com/computing/gpu/nvidia-rtx-gpus-get-one-of-the-coolest-features-ever-automatic-overclocking-that-wont-affect-your-warranty), [KitGuru](https://www.kitguru.net/gaming/joao-silva/nvidia-app-now-lets-you-automatically-overclock-your-gpu-without-voiding-the-warranty). No API found. **Unknown** whether the warranty sentence covers Exo's route; assume not, and say so.

### AMD

**ADLX has AMD's own auto tuning, public. Solid.** [`IGPUAutoTuning.h`](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUAutoTuning.h): `IsSupportedUndervoltGPU`, `IsSupportedOverclockGPU`, `StartUndervoltGPU`, `StartOverclockGPU`, each asynchronous with a completion listener. [`IGPUTuning.h`](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUTuning.h): `ResetToFactory` "resets settings of Auto Tuning, Manual GPU Tuning, Manual Fan Tuning, Manual VRAM Tuning and Manual Power Tuning". Exo uses Start and Reset and nothing manual, so the search and the limits are AMD's.

**It doesn't vanish at reboot. Inferred; AMD's header doesn't say.** AMD Software keeps tuning across restarts and is known to restore defaults after a crash. Exo re-applies nothing for a Radeon. **Undo also resets any manual tuning the user had in AMD Software**, because the only reset is whole; the row says so.

### Intel

**IGCL can do it, with a waiver. Solid.** [`igcl_api.h`](https://github.com/intel/drivers.gpu.control-library/blob/master/include/igcl_api.h): `ctlOverclockWaiverSet` ("the user is indicate that they are accepting a reduction in the lifetime of the part"; the application must show that notice), `ctlOverclockGpuFrequencyOffsetSetV2`, `ctlOverclockResetToDefault`, `ctlOverclockGetProperties`. Intel says Arc "cannot be undervolted using Arc Control" and that overclocking is desktop cards only ([Intel](https://www.intel.com/content/www/us/en/support/articles/000090329.html)). Its voltage offset is described as going above warranty limits, which breaks the hard limit, so only a frequency offset would be allowed.

**Not built:** no Arc card here, and the first real run on anything should be one the owner can watch. The engine takes it as a fourth part once there's a card to try.

## Anti-cheat

**The finding: no evidence of a ban for changing these values, and documented blocks of a driver being present.** No maker statement either way was found for Curve Optimizer, undervolting or NVML. "No evidence" means looked and found nothing; it doesn't mean safe.

| Anti-cheat | PawnIO | Evidence |
| --- | --- | --- |
| FACEIT | **Blocked, not banned.** FACEIT's own page listed `PawnIO.sys`, saying its certificate "is also used by cheat software" ([FACEIT](https://support.faceit.com/hc/en-us/articles/360014237259--Forbidden-driver-error-message-and-blocked-drivers), read through a search result, the page refused a direct fetch). The author moved to a new signature and closed the issue: "This should be fixed as of 2.1.0" ([PawnIO.Setup#1](https://github.com/namazso/PawnIO.Setup/issues/1)). LibreHardwareMonitor's maintainer: "No ban, it will simply not start AFAIK" ([discussion](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/discussions/2195)). | **Solid** that it was blocked, **unknown** whether FACEIT's page is stale |
| Riot Vanguard | **Unknown.** Nobody says. Someone asked and got no answer ([Fan Control #3537](https://github.com/Rem0o/FanControl.Releases/discussions/3537)). Vanguard's stated policy (2020) is to disable software with known exploits ([AFK Gaming](https://afkgaming.com/esports/originals/3862-valorant-anti-cheat-vanguard-blocks-other-computer-hardware-and-software-even-after-update)). `vgk` is running on the owner's PC, so the first CPU run answers this one | Unknown |
| EA Javelin | Reported working in Battlefield 6 for a month with no problem (same discussion). WinRing0 is refused by name ([EA forum](https://forums.ea.com/discussions/battlefield-2042-technical-issues-en/security-violation/12549894)) | Reports |
| Easy Anti-Cheat, BattlEye, Ricochet | **Unknown.** Ricochet's driver runs only while the game is open ([Activision](https://support.activision.com/uk/en/articles/ricochet-overview)), so it isn't there during a test | Unknown |

**The GPU routes load no new driver.** NVML and ADLX are the display driver's own. **Inferred:** nothing for an anti-cheat to block. A clock offset is what the NVIDIA App's own sliders set, on far more PCs than Exo. Never run during a game.

**What Exo does about it:**
- The GPU row says nothing about anti-cheat, because research finds nothing to say.
- The CPU row says it up front while PawnIO isn't installed: the driver stays loaded until uninstalled, FACEIT has blocked it, and whether others do isn't known.
- No game gets anything special, because no test or change ever runs during one. If an anti-cheat refuses to start, uninstalling PawnIO from Windows is the fix, and the line says that.
- A kernel driver stays out of `Use best settings` and every automatic path. AGENTS.md's hard stops hold: Exo never touches anti-cheat.

## Prior art

| Tool | How it finds a value | What it costs |
| --- | --- | --- |
| [CoreCycler](https://github.com/sp00n/corecycler) | One worker pinned to one core, moving through all of them. Default Prime95 SSE, Huge FFTs, because the lightest load boosts highest, with periodic suspend to simulate idle transitions. Automatic Test Mode only moves values toward stable after an error, step 1 on Ryzen, 5 mV on Intel. Restart after a crash through a logon task, 120 s later, because "Windows treats crashes or reboots that happen within 120 seconds of the boot as a failed boot". WHEA is watched, and a warning counts as an error | Default 6 minutes a core. README's own example: 12 hours a core |
| Ryzen Master Curve Optimizer | Applies without a restart since 2.13 ([report](https://www.neowin.net/news/amd-updates-ryzen-master-with-8000g-support-and-improved-curve-optimizer/)). What its Auto OC does, and whether it tests anything: nothing citable found. **Unknown** | - |
| Hydra, PBO Tuner | PBO2 Tuner has no search and sets what you tell it. Hydra: nothing citable found on method or duration. **Unknown** | - |
| y-cruncher, OCCT | CoreCycler uses y-cruncher `19-ZN2` and `04-P4P` as a faster way of finding errors on Ryzen. All self-checked by known results | - |
| NVIDIA Scanner | NVIDIA's own arithmetic test across voltage and frequency points, through a private DLL; on the last failure at top voltage it builds the curve from the known-good points. 10-20 minutes, "conservative", gains of 3-5%. [Tom's Hardware](https://www.tomshardware.com/news/scanner-overclock-nvidia-pascal-gpu,38295.html), [Guru3D](https://www.guru3d.com/review/msi-geforce-gtx-1660-gaming-x-review/page-30/) | - |

**What catches an unstable Curve Optimizer. Reports, many, and CoreCycler's whole premise.** Not all-core heat: an all-core load keeps boost down, so it never reaches the voltage points that fail. What fails is one core at its highest boost in a light load, and at idle: "stress test ran for 9 hours with no errors, while the machine still crashed at idle" ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/a/729605)), restarts "after 1.5 to 3 days, mostly overnight while idle" ([AMD forum](https://community.amd.com:443/t5/pc-processors/ryzen-5900x-system-constantly-crashing-restarting-whea-logger-id/m-p/489311)). WHEA 18 is a machine check, 19 a corrected error, and neither is guaranteed: a 9800X3D froze before logging anything ([Tom's](https://forums.tomshardware.com/threads/9800x3d-pbo-undervolt-freezes-my-pc-before-it-can-log-into-event-viewer.3873468)), and CoreCycler notes "Ryzen 9000 systems" can freeze rather than reset, which **Exo can't recover from without the user pressing the power button**.

### What Exo's test is, honestly

Exo doesn't ship Prime95 or y-cruncher: neither license was checked for it, and neither was needed to build this. So:

- **CPU** (`TuneLoad.Cpu`): one thread pinned to each physical core in turn, a double-precision FFT round trip and a memory hash, each checked against the answer the same code gave at stock. 9 s of load, 1 s idle, with light bursts between. **60 s a core per step, 6 minutes a core on the final value** (CoreCycler's default). **It is less thorough than Prime95: inferred**, nobody has compared them. What it misses is caught later by the sign-in rule (a crash or a WHEA error after tuning turns it off).
- **GPU** (`TuneLoad.Gpu`): a Direct3D 11 compute shader, its output hashed and checked against stock's, device loss being a failure. 3 minutes a step, 10 minutes on the final value, in line with the Scanner's 10-20 minutes for a whole run. **It works the shader core and not the rest of the pipeline.** A game can still find what it didn't.
- **Both** watch WHEA and, for a GPU, driver resets (Display 4101, nvlddmkm 13, 14, 153) through the event log during each step, and refuse to tune at all if the run at stock already reports any.

## Warranty

- **AMD, solid:** "operation of the product outside of AMD's published specifications will void any AMD warranty". [Ryzen Master user guide](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/Warning). The 3.1.1 notes: damage from use outside specifications or factory settings isn't covered "under any AMD product warranty", and may fall outside the board or system maker's. [notes](https://www.amd.com/en/resources/support-articles/release-notes/RN-RYZEN-MASTER-3-1-1.html). Threadripper 7000 has a fuse that records it ([Guru3D](https://www.guru3d.com/story/amd-overclocking-does-not-void-the-warranty-of-threadripper-7000-processors/?print)). **Whether Curve Optimizer below stock counts as outside specifications: AMD doesn't carve it out, so Exo says it can.**
- **Intel, solid:** Intel "does not warranty the operation of processors beyond their specifications". [Tom's Hardware](https://www.tomshardware.com/news/intel-warns-of-damage-from-non-k-alder-lake-cpu-overclocking). The overclocking protection plan ended in 2021. [PCWorld](https://www.pcworld.com/article/394157/overclock-at-your-own-risk-intel-axes-its-overclocking-warranty.html). IGCL's waiver is quoted above.
- **NVIDIA, reports:** its own Automatic Tuning doesn't void the warranty; nothing found for NVML from another program. A card maker's warranty is the card maker's.

## Exo's hard limits

| Limit | Value | Source |
| --- | --- | --- |
| CPU margin | 0 to -30 on every generation, negative only | The range on Zen 3 is -30 (ZenStates-Core's comment); Zen 4 and 5 allow -50 but Exo keeps one limit. CoreCycler's guide starts at -15 to -20 |
| CPU heat | Stop at 85 °C | Below every part's maximum as remembered from AMD's product pages: 90 °C on the 5600X, 95 °C on Ryzen 7000 and 9000 X parts, 89 °C on the 7800X3D. **Inferred, not re-read for this.** A single-core load shouldn't reach it |
| GPU offset, NVIDIA | 0 to +240 MHz in 30 MHz steps, never above the driver's own maximum, never negative | `nvmlDeviceGetClockOffsets`'s range. +240 is Exo's own choice: the driver's ±1000 is no guide |
| GPU clock cap, Cooler | The peak the card reached in the stock run, never above `nvmlDeviceGetMaxClockInfo` | NVML |
| GPU power | Never touched | - |
| GPU voltage | Never set. Only clock offsets | NVML has no voltage setter |
| GPU heat | Stop 5 °C short of the card's own maximum operating temperature | `nvmlDeviceGetTemperatureThreshold` (GPU_MAX), ADLX's limit on a Radeon; not the temperature target, which a card sits at normally |
| Radeon | AMD's own search and limits | ADLX |
| Errors | One WHEA or driver reset event, a wrong answer, or a lost device fails the step. Any at stock stops the run | CoreCycler's rule |

## Going away by itself

- **Off until pressed.** Not in Use best settings, not in first run, no notice. Laptops and unsupported parts show no chip.
- **Marker first.** `testing.json`, written through to disk, before each value goes on. Found later, the PC went down on that value: it goes on the failed list, no search goes that deep again, and nothing is applied.
- **Stays in Program Files.** State, marker and sign-in script live in `%ProgramFiles%\ExoLauncher Tune`, which only an administrator can write. A task running as SYSTEM must not read a script a normal program could change, and must not write into a folder one could redirect. That rules out `%LOCALAPPDATA%` and `ExoLauncher.exe` itself, which sits in a user-writable folder.
- **At sign-in and on wake**, only after a value passed: a scheduled task runs a generated `apply.ps1`, integers only. It skips everything if the marker is there or Exo is gone. It holds (marks `held`, applies nothing more until Apply in Exo) only on a Kernel-Power 41 or a WHEA-Logger 18 (fatal) logged since the script was written, or when `applying` was left behind. Corrected WHEA events, and a script error, don't hold; a script error is noted and the value stays. It writes `applying` first and removes it 120 s later (CoreCycler's reason above). So at most one crash, never a loop: Windows always boots at stock first.
- **Every sign-in run writes `last-run.txt`** (`<time> applied`, `held: <why>`, `skipped: <why>` or `error: <message>`). Exo writes its first line to `app.log` as `Auto tune: sign-in task: …` once per change, so a support log says what sign-in did.
- **NVIDIA holder.** The NVML offset may reset when the last NVML client exits (inferred, unverified on Windows). So the NVIDIA sign-in task stays running as a client with no time limit (`PT0S`): it reads the offset every 15 s and puts it back, logging each put-back to `last-run.txt`, while the `keep` flag exists. The worker removes the flag and waits 16 s before its first test or undo, so the holder never fights a test.
- **Undo** puts stock back, removes the task, script and keep flag. One press, Windows' prompt only.
- **Exo closed, game started, on battery:** back to stock and stop. Exo must stay open while it tests; the worker reads `Local\ExoLauncher.GameRunning`, which exists only while Exo runs.
- **Uninstalling Exo** removes the task and folder with Windows' permission, since the script would outlive it.

The task is the one thing here that starts with Windows. AGENTS.md's rule is that Exo's icon starts nothing with Windows and there are no tray agents. This runs, sets a value and exits, exists only after a value passed, and goes with Undo. The brief asked for it.

## The owner's decision

The CPU side has a legal answer but not a clean one. **Decided: option 2.** `RyzenAvailable` is false, so no Ryzen row shows for anyone, the GPU ships alone, and the code and tests stay. It comes back by flipping that one line after the test below. Everything else is decided.

1. **Keep the CPU row (built that way).** Shows on Ryzen 5000, 7000 and 9000 desktops. The user installs PawnIO themselves, warned up front. Risk: a user finds out Vanguard or another anti-cheat refuses PawnIO; uninstalling fixes it. Before release, the owner tries PawnIO with VALORANT on this PC, since `vgk` runs here, and that one result moves Vanguard from unknown to known.
2. **Hold the CPU row until that test.** One line: `AutoTuneService.RyzenAvailable` becomes false. The GPU ships alone.
3. **Remove it.** Delete `TuneRyzen`, the module and the row.
4. **Ask namazso to bundle PawnIO** (admin@namazso.eu), as with OpenMouse: ask the author. Only needed if a one-press install is wanted; it doesn't change the anti-cheat answer.

## First real run

NVIDIA first, Cooler, on an idle PC with nothing unsaved and no game open.

1. PC › the Auto tune chip › Tune, confirm, accept Windows' prompt. Keep Exo open. About 40 minutes.
2. **The first thing that can fail:** `nvmlDeviceSetClockOffsets` or the clock lock refused on a GeForce on Windows. The row then says the driver doesn't allow it and nothing changed; `app.log` has the NVML code.
3. While it runs, a second tool (HWiNFO, nvidia-smi) should show the graphics clock capped and the voltage lower for the same clock. That the two calls compose this way is the claim only a real card can prove.
4. Afterwards read `C:\Program Files\ExoLauncher Tune\state.json`, then restart and run `nvidia-smi -q -d CLOCK` before Exo starts anything: **the offset is expected to be gone until sign-in, and the task puts it back**. If it survives a restart with the task removed, the volatility claim is wrong and this document must change.
5. Press Undo. The task `Exo\AutoTune` and `apply.ps1` should both be gone.

What can go wrong: a driver reset or a crash during the test (expected, handled: the value fails, stock returns, press Tune again); the test passes and a game crashes later (the test works the shader core only; Undo, and the sign-in rule catches a whole-PC crash); the task fails as SYSTEM (the value simply doesn't return at sign-in).

The CPU run only after deciding the above: install PawnIO, try VALORANT first with nothing tuned, since `vgk` runs on this PC, then Tune. A hard freeze rather than a reset needs the power button, and nothing Exo does can help; the marker still catches it at the next start. Zen 3 has no read-back, so only the before and after say whether the value did anything.
