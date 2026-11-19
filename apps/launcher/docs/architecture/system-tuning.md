# System tuning (Best settings › System)

`Services/SystemTuning.cs` decides, `SystemTuning.Native.cs` reads Windows and runs the one elevated step,
`WebHostBridge.SystemTuning.cs` is `system.tuning`, and `ui/src/components/gpu/SystemGroup.tsx` draws the group under
Network in the Best settings menu. It is built like the Network group (`NetworkBest`): one switch per item, an exact record of
what was there (`exo-system-tuning.json`), detection read from Windows without administrator rights, and Off that puts back
exactly the recorded value and keeps the item out of Use best settings until its switch is on again.

The bar for an item: a documented Windows behaviour or a measured result, a value Exo can read back, and a way back. What is
already done elsewhere in Exo is not done twice (see the last table).

## How a press runs

1. The items still to set are read (`Read`). Use best settings takes only the Best settings items the player keeps on;
   an opt-in item is set only by its own switch.
2. Each item's current value is recorded, and the record saved, before anything changes.
3. One elevated step (`ReplacedApps.RemoveAsAdminAsync`: one Windows prompt however many lines): a System Restore point
   first (`RestorePoint.Step`, "Exo Launcher: before system settings", `MODIFY_SETTINGS`; a point Exo made in the last day
   stands for it, as Windows keeps one a day), then fixed `reg.exe`, `sc.exe` and `bcdedit.exe` lines. A PC with System
   Restore off still gets the change and is told there is no point: each item's own record is the way back.
4. Everything is read back. A value Windows didn't take keeps no record, so Off never "puts back" a change that never
   happened. A declined prompt puts the record back as it was.
5. Items that need a restart say "Restart to finish" (or "Goes back to how it was after a restart") until Windows next
   starts; the group says "Restart Windows to finish". Exo never restarts the PC.

Every key and value written is a constant in `SystemTuning.cs` (the allowlist). Services are the two names in `Trimmed`;
a record naming anything else is refused (`Revert` returns null). Page file lines written back must match `SafeText`.

## TrustedInstaller

Not needed, so not built. Every key written is writable by Administrators (checked on a Windows 11 26300 PC with
`Get-Acl`): `Control\PriorityControl`, `Session Manager\Memory Management`, `DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity`.
Some service keys grant Administrators only read (`TrkWks`'s does), which is why services change through the service
manager (`sc.exe config`), as Windows' own Services console does, never by writing `Start`.

## Shipped

| Item | Default | Change | Detected by | Evidence |
| --- | --- | --- | --- | --- |
| `timer` Windows' own timer | Best settings | `bcdedit /deletevalue {current} useplatformclock`, only when `bcdedit /enum {current}` lists it (the step writes what it found and what's left). Off: `useplatformclock true`. Restart. | `QueryPerformanceFrequency` is 14,318,180 Hz (the HPET); Windows 10 1809+ reports 10 MHz on the TSC | Microsoft's [Acquiring high-resolution time stamps](https://learn.microsoft.com/windows/win32/sysinfo/acquiring-high-resolution-time-stamps): the TSC is the preferred source and platform timers are much slower to read. `useplatformclock` is on BitLocker's [ignored BCD list](https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/bcd-settings-and-bitlocker), so no recovery prompt. A PC where Windows itself picks the HPET (no invariant TSC) is found by the step and left alone (`Native`). |
| `scheduling` Windows' scheduling for programs | Best settings | `Win32PrioritySeparation` = 2 (Programs). Restart. | Fixed quanta (bits 2-3 = 10) or no foreground boost (bits 0-1 = 00). 0x26, 0x16 and the like are already Programs' variable, boosted quanta and are left alone. | Windows' own two choices (System Properties › Performance Options › Advanced: Programs is 2, Background services 0x18) and the value's bit layout (Windows Internals, 7th ed., ch. 4, Quantum: length, fixed or variable, foreground boost). 0x26 is the community's "gaming" value and decodes to the same as the default 2 on a client, so there is nothing to gain by writing it. |
| `pagefile` A page file, managed by Windows | Best settings | `PagingFiles` = `?:\pagefile.sys` (what "Automatically manage" writes) where the value lists no file. A size the user set stays. Restart. | `PagingFiles` missing or empty | Microsoft's [How to determine the appropriate page file size](https://learn.microsoft.com/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows): without one the commit limit is the RAM, and apps that commit past it fail; crash dumps need it. |
| `cache` Memory for programs first | Best settings | `LargeSystemCache` = 0 where it's 1. Restart. | `LargeSystemCache` = 1 | 1 is the file-server setting: "the memory manager favors the system working set over that of processes" (Windows Internals, ch. 11, Cache size; Microsoft's [Ask the Performance Team on large system cache issues](https://techcommunity.microsoft.com/t5/ask-the-performance-team/prf-memory-management-large-system-cache-issues/ba-p/373756)). Only putting back Windows' default; no gaming benchmark exists either way. |
| `services` Services set to Manual | Opt-in | `sc config <name> start= demand` for Downloaded Maps Manager (`MapsBroker`) and Program Compatibility Assistant Service (`PcaSvc`), only those Automatic now. Off: `auto` or `delayed-auto` as recorded. Restart. | Each service's `Start` and `DelayedAutostart` | Both are "OK to disable" in Microsoft's [security guidelines for system services](https://learn.microsoft.com/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server); Manual (not Disabled) still lets Maps start its broker. That list is for servers (it also says OK for Windows Audio and Xbox Live), so only these two, which no game, anti-cheat or driver uses, are taken from it. No gain is measured in games: it is fewer services at boot, which is why it's opt-in. Distributed Link Tracking Client was left out: the same guidance has no recommendation for it. |
| `integrity` Memory Integrity off | Opt-in, asks twice, never in Best settings or first run | `HypervisorEnforcedCodeIntegrity\Enabled` = 0 (what Windows Security writes). Off: the recorded value. Restart. | `Enabled` and Windows' code integrity options (`NtQuerySystemInformation` class 103, `CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED`) | Microsoft's own [Options to optimize gaming performance in Windows 11](https://prod.support.services.microsoft.com/windows/options-to-optimize-gaming-performance-in-windows-11-a255f612-2949-4373-a566-ff6f3f474613) (October 2022): turn off Memory Integrity and the Virtual Machine Platform for the most frames, back on after. Reviews measured 5-10% in some games (Tom's Hardware, PC Gamer, 2022). Refused while Riot Vanguard (`vgk`/`vgc`: [VAN: RESTRICTION 5](https://support.riotgames.com/riot/penalties/error-van-restriction-5) asks some players to turn it on) or FACEIT (`FACEIT`, `FACEITService`: [VBS for players over 3000 Elo](https://support.faceit.com/hc/en-us/articles/27611617036188-CS2-Anti-Cheat-Measures-ENC)) is installed, under the Device Guard policy, or with the UEFI lock (`Locked` = 1). Secure Boot, TPM and VBS itself are never touched, so games that need those (EA Javelin, Call of Duty) are unaffected. |

## Left out, with why

| Candidate | Why not |
| --- | --- |
| MMCSS `Games` task: GPU Priority, SFIO Priority | Microsoft's [MMCSS page](https://learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service) says GPU Priority "is not yet used" and SFIO Priority "is not used". |
| MMCSS `Games`: Priority, Scheduling Category High | Applies only to threads that join the Games task (`AvSetMmThreadCharacteristics`), which few games do; High puts them at 23-26, above most of the system, which Microsoft reserves for pro audio. No measurement shows a gain. |
| NetworkThrottlingIndex, SystemResponsiveness | Rejected in the Network group (#37): see `NetworkBest`'s summary. |
| A High/Ultimate Performance copy as Exo's plan | Exo's plan already exists (`PowerPlan`, `PowerProfiles`): a copy of Balanced on purpose, so Game Mode's and AMD's CCD parking and Intel's Thread Director policies keep working; minimum state 100, EPP 0, core parking off per CPU type, idle states left on (idle off placed last in AlchemyTweaks' 39-plan test), PCIe link and USB selective suspend off; laptops on battery keep Balanced's; Undo puts back the plan it replaced. |
| HAGS | Already a This PC row: offered on only for cards whose frame generation needs it (RTX 40/50, RX 7000+, Arc); otherwise it measures as a wash, so Exo says nothing. |
| Fullscreen optimizations off | Microsoft's [Demystifying Full Screen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/): flip-model FSO matches exclusive fullscreen latency. Windows 11's Optimizations for windowed games are on already (`GpuPreference`, `SwapEffectUpgradeEnable=1`, in Keep). |
| MPO off (`OverlayTestMode=5`) | A workaround for one flicker bug (NVIDIA's own article, since fixed in the driver), not detectable from the PC; it costs windowed games their overlay flip. Listed in `nvidia-driver-install.md` as a bug workaround. |
| NVIDIA low latency, prefer maximum performance, shader cache unlimited | Already in Exo's NVIDIA profile (`Helpers/GpuProfile.cs`: `0x0005F543`=1, `0x1057EB71`=1, `0x00AC8497` Unlimited with 50 GB free). |
| MSI mode for GPU, NIC, audio, xHCI | Already Device interrupts (`InterruptMode`), opt-in in the same menu, per device from its PCI capabilities, never storage or the boot path, a backup and Revert per device. |
| IRQ affinity policy | No reproducible measurement on Windows 11; the NIC spreads with RSS, and `InterruptMode` never writes affinity. |
| DisablePagingExecutive | Only matters under memory pressure, where keeping kernel code resident starves programs instead; no measured gain. |
| Memory compression off | Microsoft: compression avoids hard faults to disk under pressure, which is when games stutter; off only adds paging. Not written either way (its state needs administrator rights to read). |
| GlobalTimerResolutionRequests | Windows 11 stopped granting a background process's timer resolution to others; the game in front still gets the resolution it asks for. |
| Background apps policy | Windows 11 dropped the global switch; per-app values only touch Store apps that Windows already suspends when not in front, and turning them off breaks the Xbox app's notifications and downloads. |
| Do not disturb while gaming | On by default in Windows 11 and kept in a cloud-synced binary blob with no supported write. |
| Xbox Game Bar background recording | Already kept off (`WindowsGameBar`, in Keep), with Game Mode left on. |
| Telemetry (DiagTrack, AllowTelemetry) | The supported route is the Group Policy, which greys out Settings; which value Settings' own switch writes isn't documented, and DiagTrack's cost isn't measurable in a game. |
| Spectre/Meltdown mitigations, TDR off, Defender | Security: never. Defender is the player's own choice and Exo has no switch for it. |
| Network QoS (DSCP for games) | Home routers ignore or strip DSCP upstream; nothing the PC sets reaches the game server. |
| Services beyond the two | Everything games, anti-cheat, audio, networking, Windows Update, Defender, capture (`CaptureService`, Windows.Graphics.Capture: a debloat that stopped it broke Discord's screen share), sign-in, search, printing and input need stays as Windows has it. SysMain, DiagTrack, Spooler, WSearch and Shell Hardware Detection (AutoPlay) have no measured gaming gain and visible costs; Microsoft's server list also rates Windows Audio, Bluetooth, push notifications and Xbox Live as OK to disable, which a gaming PC needs. |
