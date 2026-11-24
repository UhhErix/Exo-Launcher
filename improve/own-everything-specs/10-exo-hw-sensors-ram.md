# Spec 10 — Exo Hardware Telemetry & RAM Reader (`exo-sensors`): Native Rust HWiNFO64 + ZenTimings + Health Sentinel

- **Inventory Rank:** #10 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `L`
- **Replaces:** **HWiNFO64**, **ZenTimings**, **CPU-Z** (memory/system tabs), **CrystalDiskInfo**, and scattered C# sensor code (`Helpers/GpuSensors.cs`, `NvidiaSensors.cs`, `Services/DriveHeat.cs`, `PcAbout.cs`, `GpuProcessMonitor.cs`)
- **Anti-Cheat Class:** `AC-SAFE` (100% read-only OS APIs — NVML, ADLX, IGCL, DXGI, PDH, NVMe IOCTL SMART, SMBIOS, EventLog WHEA — plus optional WHQL-signed read-only **PawnIO** SMU/IMC modules; **zero WinRing0!**)
- **License:** MIT (clean-room Rust)

---

## 1. Scope

### What It Solves
1. Gamers install **HWiNFO64** (40–80 MB RAM, dense 200-row wall of cryptic numbers, paid Pro for automation), **ZenTimings** (AMD RAM timings), and **CrystalDiskInfo** (NVMe health) just to answer simple questions: *"Is my XMP/EXPO actually on? Is my GPU running at PCIe x16? Is my SSD throttling? Why did my PC stutter?"*
2. Exo currently reads basic GPU/NVMe stats across scattered C# files, missing per-core CPU effective clocks/package power, DDR4/DDR5 timings (tCL/tRCD/tRP/tRAS/tRFC/FCLK/UCLK/MCLK), PCIe link-width checks, and automated hardware misconfiguration alerts!

### In Scope
- **Zero-Driver Tier (Works out of the box, no admin, 100% of PCs):**
  - **GPU Sensors:** NVML / ADLX / IGCL (core/hotspot/VRAM temps, core/mem clocks, board power W, throttle reasons, VRAM usage, PCIe **current vs max** gen & lane width!).
  - **Per-Process GPU Attribution:** Native Rust PDH + DXGI LUID monitor (port of `GpuProcessMonitor.cs`).
  - **Storage / NVMe SMART:** Direct Win32 `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceTemperatureProperty`, NVMe health log: temp, wear %, spare %, media errors, unsafe shutdowns, PCIe NVMe link speed).
  - **System & Memory Topology:** Raw `GetSystemFirmwareTable(RSMB)` SMBIOS Type 0/2/4/17 parser (exact CPU model, motherboard/BIOS version, DIMM slots populated, DDR4/DDR5 configured MT/s vs rated XMP/EXPO MT/s, single vs dual channel detection!).
  - **WHEA & Stability Log:** Native EventLog reader for `Microsoft-Windows-WHEA-Logger` & GPU TDR (`Display` 4101).
- **Enhanced Precision Tier (Optional WHQL-Signed PawnIO Read-Only Modules):**
  - **AMD Ryzen:** `RyzenSMU.bin` (already shipped in Exo!) + IMC register reader → per-core effective clocks, Tctl/Tdie, PPT/TDC/EDC, FCLK/UCLK/MCLK 1:1 vs 2:1 ratio, and full primary/secondary/tertiary DDR4/DDR5 timings (tCL, tRCD, tRP, tRAS, tRC, tRFC, tREFI, CR — ZenTimings parity!).
  - **Intel Core:** Signed PawnIO Intel MSR/IMC read-only module → package/IA power W, per-core DTS temps, thermal throttle flags, IMC timings.
- **Automated Hardware Misconfiguration Sentinel (`sentinel.rs`):**
  - Turns raw sensors into **plain-English automatic diagnostics**:
    - *"Your DDR5 RAM is running at 4800 MT/s (JEDEC default) instead of its rated 6000 MT/s — enable EXPO/XMP in BIOS."*
    - *"Your GPU is running at PCIe x8 Gen 3 instead of x16 Gen 4 — reseat card or check M.2 lane sharing."*
    - *"Your RAM is running in Single-Channel mode (slots A1+A2 instead of A2+B2)."*
    - *"NVMe SSD hit 74 °C thermal throttling during gameplay."*
    - *"3 WHEA hardware errors detected in the last 24 hours — check CPU/RAM undervolt stability."*

### Out of Scope
- Replacing HWiNFO64's obscure industrial/server BMC/UPS sensor catalog (Exo focuses 100% on gaming PC hardware!).
- Any write operations to MSRs/SMU/PCIe config space (this crate is **strictly read-only**!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-sensors/
├── Cargo.toml                  # windows (SysInfo, Storage, Dxgi, Pdh, EventLog, Registry), libloading, serde
├── src/
│   ├── lib.rs                  # Unified TelemetrySnapshot + adaptive poll rate (1 Hz active, 0.1 Hz idle, paused when hidden)
│   ├── smbios.rs               # Pure-Rust SMBIOS Type 0/2/4/17 parser (DIMMs, channels, XMP/EXPO mismatch detector)
│   ├── gpu.rs                  # NVML + ADLX + IGCL unified sensors + PCIe link-width audit + throttle reasons
│   ├── gpu_procs.rs            # PDH + DXGI LUID per-process GPU engine & VRAM monitor
│   ├── nvme_smart.rs           # Win32 IOCTL_STORAGE_QUERY_PROPERTY NVMe/SATA SMART & wear/temp reader
│   ├── whea.rs                 # WHEA-Logger & GPU TDR EventLog watcher
│   ├── pawnio_imc.rs           # Read-only PawnIO RyzenSMU / Intel MSR-IMC timings & CPU power reader
│   └── sentinel.rs             # Automated hardware health advisor (XMP off, single-channel, PCIe drop, throttling, WHEA)
```

---

## 3. Automation & Intelligence Design

1. **First-Boot Hardware Health Audit:** Automatically runs a 200 ms zero-driver SMBIOS/PCIe/SMART/WHEA audit on startup and silently flags real hardware bottlenecks (like forgotten XMP/EXPO or single-channel RAM — two of the biggest free FPS losses in PC gaming!).
2. **Adaptive Zero-Waste Polling:** Polls at `1 Hz` only while PC room or HUD overlay is visible, drops to `0.1 Hz` for thermal/WHEA safety checks during gaming, and **0 Hz** when idle on desktop — avoiding HWiNFO64's notorious polling micro-stutters!
3. **Closed-Loop Integration:** Feeds live temp/power/throttle/WHEA flags directly into `exo-present` (stutter attribution), `exo-gpu` (undervolt safety), and `exo-thermal` (fan curves) in-process with zero IPC overhead.

---

## 4. Measurement Plan

Measured safely (100% read-only!) on this Ryzen 5 5600X + RTX 3070 PC + unit fixtures for SMBIOS Type 17 / NVMe / WHEA edge cases:

| Metric | Baseline (HWiNFO64 + ZenTimings + C# scattered readers) | Target (`exo-sensors`) | How Measured |
| --- | --- | --- | --- |
| **Memory Footprint** | HWiNFO64 ~40–80 MB WS | **<4 MB** delta WS | `GetProcessMemoryInfo` |
| **Active 1 Hz Poll CPU Cost** | HWiNFO64 ~0.5–1.5% CPU (can cause frame jitter if too many sensors polled) | **<0.2% CPU, <1.5 ms** poll duration | `GetProcessTimes` + QPC poll histogram over 60 s |
| **Full Hardware Health Audit Cold Time** | Manual multi-app check | **<200 ms** single pass | QPC timer on SMBIOS + GPU + NVMe + WHEA |
| **Misconfiguration Sentinel Precision** | Manual user reading | **100%** detection on fixture suite (XMP off, single-channel, PCIe x8, 75 °C NVMe, WHEA burst) | Automated golden fixture test suite |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-sensors --locked -- -D warnings`, `cargo test -p exo-sensors --locked`.
2. **Read-Only Enforcement Gate:** Verify crate opens all handles (`CreateFileW` on NVMe, Registry, PawnIO) with read/query permissions only — zero write IOCTLs.
3. **SMBIOS Fuzz & Fixture Gate:** Test `smbios.rs` against real Windows 11 SMBIOS dumps + malformed/truncated byte buffers (zero panics!).
4. **Zero-Stutter Poll Gate:** Verify 1 Hz polling adds `<0.1 ms` p99 jitter to a concurrent synthetic 120 FPS DX12 window.

---

## 6. Safety Rules & Hard Stops

- **100% Read-Only:** Never write to SMBIOS, NVMe, MSRs, SMU, or PCIe registers from `exo-sensors`.
- **No WinRing0:** Strictly use OS APIs + optional WHQL-signed `PawnIO`; gracefully degrade (hide IMC timings with a clean "Optional PawnIO" note) if PawnIO is not installed.
- **Privacy Scrubbing:** Never include motherboard/disk serial numbers in telemetry or crash reports (`PRIVACY.md` compliance).
- **Never Start Installed Exo** during lab runs.
