# Spec 14 — Exo Autopilot Core (`exo-autopilot`): Native Rust Unified Process Lasso + ISLC + TimerResolution + Download Pause

- **Inventory Rank:** #14 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** **Process Lasso** ($30 Pro), **ISLC (Intelligent Standby List Cleaner)**, **TimerResolution**, and Exo's C# `Services/Autopilot*.cs`, `StandbyPurger.cs`, `GameSessionDownloadPause.cs`, `PowerPlan.cs`, `BackgroundTrim.cs`
- **Anti-Cheat Class:** `AC-SAFE` (**100% zero touches to the game process!** Never calls `OpenProcess`, `SetProcessAffinityMask`, or `SetPriorityClass` on any game or anti-cheat process — operates strictly on *non-game background hogs* and OS-level timer/memory/BITS APIs!)
- **License:** MIT (clean-room Rust)

---

## 1. Scope

### What It Solves
1. Exo already proved these exact optimizations in C# (`improve/tweak-evidence.md`: CPU Sets, `0.507 ms` timer resolution, standby purge, BITS/Windows Update pause), **but** they are spread across 10 C# files, lack Process Lasso's live **ProBalance** dynamic background CPU-hog dampening, lack smart stutter-triggered standby purging, and rely on C# timers + GC.
2. Gamers pay **$30** for Process Lasso + run ISLC + TimerResolution as 3 separate always-on tray apps, and many users accidentally set CPU affinity *on the game process* (which breaks AMD Dual-CCD X3D V-Cache scheduling or triggers anti-cheat flags!).

### In Scope
- **Unified Native Rust Gameplay Session Engine (`exo-autopilot`):**
  1. **Topology-Aware Background CPU Sets (`cpu_sets.rs`):** Reads `GetSystemCpuSetInformation` once at boot to classify CPU topology (**Intel Hybrid P/E cores**, **AMD Dual-CCD X3D V-Cache vs Freq CCD**, **Dual-CCD**, or **Monolithic ≥6T**). While a game is foreground, softly corrals non-game background hogs (browsers, updaters, indexers, Electron apps) onto E-cores / non-V-Cache CCD / last non-Core-0 cores via `SetProcessDefaultCpuSets` — **never touches the game or anti-cheat processes!**
  2. **Dynamic ProBalance-Class Hog Damper (`probalance.rs`):** Watches non-game CPU usage every 1 s during gameplay; if any background process spikes `>15%` CPU for 2 consecutive ticks, dynamically lowers it to `BELOW_NORMAL_PRIORITY_CLASS` + `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` (Windows 11 EcoQoS!), restoring exact original priority/CPU sets on game exit!
  3. **Micro-Adjusted Timer Resolution (`timer_res.rs`):** Calls `NtSetTimerResolution(5070, TRUE)` (`0.507 ms`, avoiding Win11 `Sleep(1)` clock-tick alignment jitter vs exact `0.500 ms` — verified in `improve/tweak-evidence.md`!) **strictly while a game is foreground**, immediately releasing on Alt-Tab or exit (zero idle battery drain!).
  4. **Smart Standby Memory Shield (`standby.rs`):** Monitors `GlobalMemoryStatusEx` + standby list size; purges standby list (`NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)`) **only** when free physical RAM drops below threshold (`<15%` or `<1.5 GB`) AND `exo-present` reports paging pressure — avoiding ISLC's blind periodic purges that evict useful game asset cache!
  5. **Crash-Safe Session Download & Indexer Pause (`session_pause.rs`):** Suspends `BITS` (`IBackgroundCopyManager`), Delivery Optimization (`DoSvc`), Windows Update (`wuauserv`), and Search Indexer (`WSearch`) while gaming, backed by a persistent journal so even a power cut restores services automatically on next boot!
  6. **Automatic Power Plan & Mouse Accel Guard:** Ensures Exo gaming power plan & raw pointer precision while gaming without fighting user laptops on battery.

### Out of Scope
- Modifying affinity, priority, memory, or threads of **any game process or anti-cheat process** (hard stop!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-autopilot/
├── Cargo.toml                  # windows (Threading, SysInfo, ProcessStatus, Power, Bits), parking_lot, serde
├── src/
│   ├── lib.rs                  # AutopilotEngine + game focus state machine + Drop/crash journal restore
│   ├── topology.rs             # GetSystemCpuSetInformation classifier (Intel Hybrid, AMD X3D, Dual-CCD, Mono)
│   ├── cpu_sets.rs             # SetProcessDefaultCpuSets background corral (strict allowlist/blocklist!)
│   ├── probalance.rs           # Live non-game CPU hog damper (EcoQoS + BELOW_NORMAL + restore)
│   ├── timer_res.rs            # NtSetTimerResolution(5070) foreground-scoped RAII guard
│   ├── standby.rs              # Demand-driven standby list purger (only on real low-free-RAM pressure)
│   ├── session_pause.rs        # Crash-safe BITS / DoSvc / wuauserv / WSearch pause & boot-journal restore
│   └── blocklist.rs            # Unbreakable protection list: games, ALL anti-cheats, audio/DWM/system critical PIDs
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config Topology Intelligence:** Automatically detects Ryzen 7800X3D/7950X3D/9800X3D/9950X3D vs Intel 12th–15th Gen Hybrid vs standard CPUs and applies the exact right background CPU-set mask without user research!
2. **Learns Your PC's Background Offenders:** Tracks which background apps spiked during gameplay across sessions (linked to `exo-present` Spec #07 stutter attribution) and pre-corrals repeat offenders immediately on game launch.
3. **Zero Idle / Battery Waste:** Releases `0.507 ms` timer, CPU sets, EcoQoS, and BITS pauses the instant you Alt-Tab or exit the game — laptop battery life stays 100% untouched!
4. **Power-Cut Proof:** Writes a tiny atomic restore journal (`autopilot-journal.json`) before touching any service/power plan; on next boot/startup, automatically restores everything if the previous session crashed.

---

## 4. Measurement Plan

Measured on this Ryzen 5 5600X PC against a synthetic 120 FPS DX12 game window with an injected synthetic background CPU hog (`burn-cpu.exe` spinning 4 threads at Normal priority):

| Metric | Baseline (No Autopilot / Process Lasso + ISLC + TimerRes) | Target (`exo-autopilot`) | How Measured |
| --- | --- | --- | --- |
| **1% Low FPS Under Synthetic Background CPU Hog** | Unmitigated background hog drops 1% lows sharply | **≥85% recovery** of 1% low frametime vs clean baseline | `exo-present` ETW 1% low comparison (Hog OFF vs Hog ON vs Hog+Autopilot ON) |
| **Timer Resolution Accuracy** | Stock Windows 15.6 ms / exact 0.500 ms Sleep(1) jitter | **0.507 ms** active during foreground; **15.6 ms** restored <100 ms after exit | `NtQueryTimerResolution` + `Sleep(1)` delta histogram |
| **Engine Resident Footprint** | Process Lasso + ISLC + TimerRes: ~45–80 MB RAM, 3 processes | **<3 MB WS / <0.1% CPU**, 0 extra processes | `GetProcessMemoryInfo` + `GetProcessTimes` |
| **Zero-Game-Touch Verification** | Process Lasso can touch game affinity if misconfigured | **0** `OpenProcess` calls on target game or AC PIDs | Instrumented mock OS trait + test audit |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-autopilot --locked -- -D warnings`, `cargo test -p exo-autopilot --locked`.
2. **Topology Classifier Golden Tests:** Unit tests covering Intel 13900K/14900K Hybrid, AMD 7950X3D/9950X3D asymmetric V-Cache CCD, AMD 5600X monolithic 6C/12T, and 4-core low-thread CPUs (where corrals safely no-op!).
3. **Blocklist Invariant Tests:** Prove `cpu_sets.rs` and `probalance.rs` NEVER touch the active game PID tree, any anti-cheat process (`vgc`, `vgtray`, `EasyAntiCheat`, `BEService`, `FACEIT`, etc.), `audiodg.exe`, `dwm.exe`, or `ExoLauncher`/`exo-ui`.
4. **Crash-Journal Restore Test:** Simulate abrupt exit mid-game; verify startup recovery restores every modified priority/CPU-set/BITS state.

---

## 6. Safety Rules & Hard Stops

- **NEVER Touch Game or Anti-Cheat Processes:** Hard-enforced in `blocklist.rs` — zero `OpenProcess`, zero affinity, zero priority, zero working-set trim on any game or anti-cheat tree!
- **Never Touch `audiodg.exe` or `dwm.exe`:** Throttling Windows audio or compositor causes audio crackles and frame drops.
- **No Permanent Service Disabling:** Never set `wuauserv`/`BITS` to Disabled permanently; only session-pause with crash-safe boot restore.
- **Laptop Battery Respect:** Skip power-plan lock and aggressive background timers when running on DC battery.
