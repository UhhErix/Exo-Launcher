# Exo Power Guard — Native v4 Graduation Report (`feat/native-power-guard`)

- **Origin:** Owner link `https://x.com/wccftech/status/2125221648186196189` (**RTX Lens**, `Codeh4ck/RTXLens` v1.1.9, LGPL-3.0 + LibreHardwareMonitor MPL-2.0) -> Verdict **`BUILD-OWN`** -> Graduated into native v4 (`feat/native-power-guard` from `origin/feat/native-polish-2`).
- **License:** 100% MIT (`exo-power-guard` has **0 external crates**; zero LGPL code copied; zero vendor binaries redistributed; zero kernel drivers / zero WinRing0 — 100% anti-cheat safe).

---

## 1. Architecture Decision: On-Demand Rust Sidecar vs .NET Host (Measured)

We evaluated putting GPU/connector telemetry inside the `.NET 10` host (`ExoLauncher`) vs our dedicated pure-Rust sidecar (`exo-power-guard.exe`, NDJSON over stdio):

| Metric (Measured on Owner's RTX 3070, Driver 621.35) | In-Process Host / UI (`nvml.dll` loaded in-proc) | **On-Demand Rust Sidecar (`exo-power-guard.exe`) — CHOSEN** |
| --- | --- | --- |
| Binary size | Part of `ExoLauncher` / `exo-ui` (`35.35 MB`) | **`411.5 KB`** (`421,376 B` stripped release exe, 0 external crates) |
| Hidden / idle RAM cost (PC room closed & no game running) | **`+24.19 MB` private / `34.97 MB` WS pinned permanently** (`nvmlInit_v2` maps ~21 MB that stays mapped after `nvmlShutdown` + `FreeLibrary`, documented in `Nvml.cs:26`) | **`0.00 MB` RAM, `0.00%` CPU, `0` threads** (sidecar exits cleanly on stdin EOF; OS reclaims the entire 21 MB `nvml.dll` mapping) |
| Parent (`exo-ui`) RAM overhead while sidecar active | `+24.19 MB` private | **`+0.11 MB` private** (`before=1.00 MB`, `active=1.11 MB`, `after_stop=1.08 MB`) |
| Sidecar startup -> first NDJSON frame | n/a | **`353.75 ms`** |
| Sidecar clean shutdown (stdin close -> process exit) | n/a | **`98.28 ms`** |
| Active poll latency (1 Hz while PC open or game runs) | `0.94–1.11 ms` NVML+NvAPI (`0.10%` of 1 core) | `0.94–1.11 ms` NVML+NvAPI (`0.10%` of 1 core); `3.31–3.46 ms` D3DKMT fallback (`0.89 MB` private) |
| GPU VRAM / game frame-time impact | `0 MiB` / `0.00 ms` | **`0 MiB` / `0.00 ms`** (pure user-mode, zero injection) |
| `.NET` host & `host-v1.json` churn | Required | **Zero** (`.NET` host untouched) |

**Winner:** On-demand Rust sidecar (`exo-power-guard.exe sidecar --interval-ms 1000`), spawned automatically by `exo-ui` **only while the PC room is open OR a game is running**, and stopped immediately when hidden & idle (**0.00 MB** hidden cost, **220× lower parent RAM overhead**).

---

## 2. Cross-Vendor Hardware Coverage & Honest Capability Disclosure

| Tier | Backend / Transport | GPUs Covered | Readings & Honest Limitations |
| --- | --- | --- | --- |
| 1 | `backends/nvidia.rs` (`nvml.dll` + `nvapi64.dll`) | **All** NVIDIA GTX / RTX / Quadro / laptop (no `"GeForce RTX"` lock) | Board watts, enforced + min/max power limits (`100–265 W` on RTX 3070), core/mem temp, clocks, VRAM, fan %/RPM, P-state, PCIe gen/width, card temp limits, HW power-brake/thermal throttle flags, + **ASUS ROG Astral 6-pin I2C** (`NvAPI_I2CReadEx`, `0x2B << 1`, reg `0x80`). Honestly notes public NVIDIA APIs omit hotspot temp and standard cards lack per-pin shunts. |
| 2 | `backends/amd.rs` (`amdadlx64.dll` ADLX 2.0) | **All** AMD Radeon RX 5000–9000 & APUs | Total board watts, core + **hotspot (`110 °C` spec)** temp, clocks, VRAM, fan RPM, usage. Honestly notes per-pin current requires inline/PSU shunts. |
| 3 | `backends/intel.rs` (`ControlLib.dll` IGCL 1.1) | **All** Intel Arc A/B-series & Iris Xe / Core Ultra | Energy-delta card/package watts, GPU + VRAM temp, clocks, fan RPM, HW power/temp/current throttle flags. Honestly notes per-pin current requires inline/PSU shunts. |
| 4 | `backends/d3dkmt.rs` (`gdi32.dll` `D3DKMT`) | **Every Windows WDDM GPU** (zero vendor DLLs) | 0.1 °C core temp, fan RPM, WDDM power deci-percent, mem clock, dedicated VRAM, GPU util %. Honestly notes no absolute watts, throttle bits, or per-pin shunts. |
| Per-pin A | `per_pin/wireview.rs` (Win32 SetupAPI + COM 115200 8N1) | **Any GPU** with Thermal Grizzly WireView Pro II (`VID_0483/PID_5740`) | 6-pin V/A/W, 4 NTC connector temps (0.1 °C), cable rating (`150–600 W`), HW fault/log bitmasks. |
| Per-pin B | `per_pin/msi_psu.rs` (Win32 HID `VID_0DB0`, `0xAA6F`/`0x808C`) | **Any GPU** with MSI MPG Ai1300TS / Ai1600TS | 2×6 PSU-side pin currents decoded via PMBus Linear11 (`65 B` HID reports). |

---

## 3. Sustained Fault Alerts & 3-Stage Opt-In Protective Actions

- **Default ON:** `Alerts on sustained fault` — emits a sticky native Exo toast (`push_rich_notice`) when a thermal, throttle, overpower, or pin fault persists (`>= 3 s` default). Zero automatic device writes or shutdowns.
- **All 3 protective stages are independently opt-in (`false` by default) and run in strict escalation order:**
  1. **Lower GPU power limit** (`opt_in_lower_power_limit`): scales enforced limit to 70% clamped to the card's hardware `[min_power_limit_w, max_power_limit_w]` floor (`168 W` on 240 W RTX 3070). Tested exclusively via `MockActionExecutor` / `dev_mock_actions = true` — **never written to the owner's RTX 3070**.
  2. **Close running game** (`opt_in_close_game`): if fault persists after Stage 1, stops the active game gracefully via Exo's `stop_game_held` (`game.stop` RPC — never `Process.Kill`, never touches anti-cheat).
  3. **Shut down PC** (`opt_in_shutdown_pc`): last resort if fault persists after Stage 2 (`shutdown.exe /s /t 15 /d p:0:0` with cancelable 15 s countdown).

---

## 4. Native PC Room UI (`exo-ui`)

- **PC room (`pc_ai.rs` + `power_guard.rs`):** Full-width Void `#000` hairline card (`exo-ai-power-guard-card`) placed below the 2-column bento grid (preserving `left_col`/`right_col` balance within 40 px and `EXO_MAX_BACKDROP_BLURS`):
  - Header with live status badge, watts / limit / °C / RPM, sample count, and **Export HTML** button.
  - Inline 48-bar native Rune **Session graph** (`pg-session-graph`) color-coded by thermal/pin severity using `exo-kit` tokens.
  - Honest GPU backend, capability, rail/12V-2x6 pin readings, and shunt-requirement note.
  - Right-side sustained-fault opt-in controls (`Alerts: On`, `1. Power limit: Off`, `2. Close game: Off`, `3. Shut down: Off`).
- **PC > Graphics popover (`pc_menus.rs`):** Full `Power guard` switch rows below `Manual tuning`.
- **Windows debug stack fix (`crates/exo-ui/build.rs`):** Reserves an 8 MB MSVC main-thread stack (`/STACK:8388608`) so `parity-snap.ps1` debug `exo-ui.exe` renders cleanly without overflowing Windows's 1 MB default stack.

---

## 5. Verification Evidence (Fresh on `feat/native-power-guard`)

- `cargo fmt -p exo-power-guard -- --check` + `rustfmt --edition 2024 --check` on touched `exo-ui` files: **PASS (exit 0)**
- `cargo clippy --workspace --locked -- -D warnings`: **PASS (exit 0, 0 warnings across all 4 crates)**
- `cargo test --workspace --locked`: **PASS (exit 0) — 269 passed, 0 failed, 1 ignored** (`exo-kit` 9, `exo-power-guard` 11, `exo-rpc` 27 + 1 ignored bench, `exo-ui` 222 including 3 new `power_guard` tests + `exo_ai_bento_balance_header_alignment_and_blur_cap` + `gpu_manual_tuning_gridlines_and_motion_gate`)
- `dotnet test`: not required (zero `.NET` host files modified).
- **Live RTX 3070 telemetry & sidecar benchmark (Driver 621.35):**
  - `nvidia-nvml-nvapi`: `57.561 W / 240.000 W` (`min 100 W`, `max 265 W`), `38 °C`, `1950 / 7001 MHz`, `1003 RPM`, `23.98%` TDP; `windows-d3dkmt`: `38.4 °C`, `1003 RPM` (0 RPM delta), `23.90%` TDP (`0.08%` delta), `7001 MHz` mem.
  - `bench-sidecar`: first frame `353.75 ms`, clean stop `98.28 ms`, parent RAM delta `+0.11 MB` vs `+24.19 MB` in-process NVML.
  - `simulate-fault`: verified `AlertSustained` (`t=+2s`) -> `LowerPowerLimit(168 W)` (`t=+4s`) -> `CloseGame` (`t=+8s`) -> `ShutdownPc` (`t=+12s`) -> clean recovery (`t=+13s`) via `MockActionExecutor` (zero writes to owner's RTX 3070).
- **Parity snap (`parity-snap.ps1 -Scenes pc`):** rendered `power-guard-pc.png` (`1920x1080`, `133.9 KB`) and `power-guard-tall-pc.png` (`1920x1400`, `189.3 KB`) against repo-built `ExoLauncher.exe` (never installed Exo) and visually verified the rendered `Power guard` card, session graph, typography, and opt-in pills.
- **Not verified on physical hardware:** AMD Radeon ADLX, Intel Arc IGCL, ASUS ROG Astral I2C, Thermal Grizzly WireView Pro II COM, and MSI MPG Ai1300TS/Ai1600TS HID live devices (none attached to this PC; codecs and state machine verified by unit tests).
