# Spec 15 — Exo Latency & Network X-Ray (`exo-net-latency`): Native Rust DPC/ISR, Click-to-Photon & Bufferbloat Engine

- **Inventory Rank:** #15 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** **LatencyMon**, **NVIDIA Reflex Analyzer / LDAT** (software click-to-photon mode), **TCP Optimizer**, **PingPlotter** basics, and local-path half of **ExitLag** ($84–120/yr), plus Exo's C# `Helpers/LatencyTrace.cs`, `Services/NetworkBest*.cs`, `NetworkTune*.cs`, `GameQosPolicy.cs`, `CloudflareDns.cs`, `InterruptMode*.cs`
- **Anti-Cheat Class:** `AC-SAFE` (100% read-only kernel ETW `PerfInfo` DPC/ISR + Raw Input + Win32 QoS/NIC/DNS APIs — zero packet hooking, zero game injection!)
- **License:** MIT (clean-room Rust)

---

## 1. Scope

### What It Solves
1. Exo already has a manual 10-second `LatencyTrace.cs` (DPC/ISR), basic `NetworkBest` check, `GameQosPolicy` (DSCP), and `InterruptMode` (MSI) in C#, **but** they are disconnected manual tools: users still run **LatencyMon** to hunt audio/frame crackles, **TCP Optimizer** for NIC settings, and pay **$7–10/mo for ExitLag** when 80% of their lag spikes are actually local Wi-Fi/router bufferbloat, NIC Energy-Efficient Ethernet (EEE), interrupt coalescing, or background downloads!
2. Gamers have no built-in way to measure **real end-to-end Click-to-Photon latency** before and after tuning without buying a $500 LDAT rig or Reflex-certified monitor.

### In Scope
- **Module A — Continuous & On-Demand DPC/ISR Driver Latency Watchdog (`dpc_isr.rs`):**
  - Native Rust port of `LatencyTrace.cs` (own `Exo Latency Check` ETW system logger — never steals NT Kernel Logger!) resolving kernel routine addresses to exact loaded `.sys` drivers (`nvlddmkm.sys`, `ndis.sys`, `Wdf01000.sys`, `storport.sys`).
  - Automatically flags drivers exceeding Microsoft's guidance (`ISR > 25 µs`, `DPC > 100 µs`, audio-dropout danger `> 1000 µs`) and pairs each offender with a concrete automated fix (e.g., enable MSI interrupt mode via `InterruptMode`, disable NIC EEE/Interrupt Moderation, update outdated Realtek/Intel NIC driver!).
- **Module B — Click-to-Photon Latency Lab (`click_to_photon.rs`):**
  - Combines **Raw Input `WM_INPUT` mouse-button-down QPC timestamp** + **`exo-present` ETW `Present → DisplayLatency`** (and reads NVIDIA **PCLStats / Reflex** ETW provider where games emit it!) to measure live **Input → Render → Display** latency in milliseconds — plus optional open USB-serial photodiode sensor support for true hardware-verified photon timing!
- **Module C — Network Path X-Ray & Zero-Config Auto-Optimizer (`net_xray.rs`):**
  - **Parallel DNS Racer:** Benchmarks `1.1.1.1` (Cloudflare), `8.8.8.8` (Google), `9.9.9.9` (Quad9), and current ISP DNS over 20 queries (p50/p95/jitter) and applies the fastest with 1 click (or reverts cleanly).
  - **Hop-by-Hop Bufferbloat & Jitter X-Ray:** Measures idle vs loaded RTT to local gateway (Hop 1 — isolates Wi-Fi/router bufferbloat!) and regional internet hops (`1.1.1.1` + optional game-region endpoints), grading bufferbloat (`A+` to `F`).
  - **NIC & QoS Auto-Tuning:** Audits active NIC registry keys (Energy-Efficient Ethernet, Interrupt Moderation, Flow Control, RSS queues, MSI-X mode) and auto-provisions Windows Policy-based **DSCP EF (`46`) QoS** for every installed online game executable (`GameQosPolicy`) without touching packet drivers!

### Out of Scope
- Paid VPN relay tunnel infrastructure (Exo cannot magically alter ISP BGP peering without global servers; `exo-net-latency` fixes 100% of local/PC/NIC/DNS/bufferbloat/QoS bottlenecks for **$0** and honestly tells the user when an ISP hop beyond their router is the culprit!).
- Any LSP/WFP packet injection or traffic manipulation into game sockets.

---

## 2. Native Rust Crate Layout

```text
crates/exo-net-latency/
├── Cargo.toml                  # windows (Etw, Networking, WinSock, Registry, Psapi), parking_lot, serde
├── src/
│   ├── lib.rs
│   ├── dpc_isr.rs              # PerfInfo ETW DPC/ISR profiler + EnumDeviceDrivers .sys address resolver
│   ├── click_to_photon.rs      # Raw Input QPC + PresentMon DisplayLatency + NVIDIA PCLStats ETW correlator
│   ├── dns_racer.rs            # Parallel UDP/DoH DNS latency & jitter benchmarker (1.1.1.1 / 8.8.8.8 / 9.9.9.9 / ISP)
│   ├── bufferbloat.rs          # IcmpSendEcho2 Hop-1 gateway + loaded-vs-unloaded RTT/jitter analyzer
│   ├── nic_audit.rs            # Active NIC adapter audit (EEE, InterruptModeration, RSS, MSI-X) + reversible registry fixes
│   ├── dscp_qos.rs             # Windows Policy-based QoS DSCP 46 per-game manager (port of GameQosPolicy)
│   └── remediate.rs            # Maps diagnosed root causes -> reversible 1-click fixes (with Keep journal rollback)
```

---

## 3. Automation & Intelligence Design

1. **Instant Root-Cause Plain-English Diagnosis:** Instead of LatencyMon's cryptic hex tables, Exo says: *"Your Realtek 2.5GbE NIC (`rt25cx21x64.sys`) hit a 1,420 µs DPC spike — Energy-Efficient Ethernet & Interrupt Moderation are ON. [Fix & Restart Adapter]"*.
2. **Pre-Game Silent Path Check:** On launch of competitive multiplayer games, runs a `<300 ms` Hop-1 gateway ping check; warns immediately if local Wi-Fi jitter exceeds `15 ms` or if a background download is saturating the router (`exo-autopilot` auto-pauses BITS/Windows Update!).
3. **Before/After Latency Proof:** Automatically displays Click-to-Photon & p95 DPC latency delta after applying Exo optimizations so users see verified millisecond gains on their own PC.
4. **Strict Reversibility:** Every NIC/DNS/QoS/MSI change is recorded in Exo's `Keep` ledger with 1-click full restore.

---

## 4. Measurement Plan

Run safely (read-only trace + local loopback/1.1.1.1 read-only probes) on this Windows 11 x64 PC:

| Metric | Baseline (LatencyMon + TCP Optimizer + C# `LatencyTrace`) | Target (`exo-net-latency`) | How Measured |
| --- | --- | --- | --- |
| **DPC/ISR Event Parsing Throughput** | C# `LatencyTrace.cs` | **>500,000 events/s** with **0 dropped events** & `<0.5%` CPU | 10 s synthetic/real ETW buffer benchmark |
| **DNS Race Time (4 Resolvers × 20 Queries)** | Manual `namebench`/GRC tool (~30 s) | **<800 ms** parallel race | QPC timer on `dns_racer.rs` |
| **Click-to-Photon Measurement Jitter** | Manual video camera counting | **<0.2 ms** timestamp correlation precision | Synthetic DX12 click-to-color-flash test window |
| **Reversible Fix Rollback Accuracy** | TCP Optimizer has weak rollback | **100%** byte-for-byte registry restore in mock registry tests | Unit/integration test suite |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-net-latency --locked -- -D warnings`, `cargo test -p exo-net-latency --locked`.
2. **ETW Parser Fuzz Gate:** Fuzz `dpc_isr.rs` against malformed/truncated 32-bit & 64-bit PerfInfo records + backward QPC ticks (zero panics, matches `LatencyTrace.cs` safety guards).
3. **Network Policy Gate:** All probe hosts (`1.1.1.1`, `8.8.8.8`, `9.9.9.9`, local gateway) explicitly documented in `NetworkDestinations` / `PRIVACY.md`!
4. **No Packet-Hook Gate:** Verify zero `WFP` / `WinDivert` / `LSP` / `Npcap` imports.

---

## 6. Safety Rules & Hard Stops

- **Zero NIC/Registry Writes During Agent Tests:** All NIC tuning, DNS changes, and MSI-X registry writes MUST run strictly against mock registry abstractions in tests — never alter the owner's live network adapter!
- **Never Break Connectivity:** Any NIC setting change must verify gateway reachability within 10 s and automatically roll back if connectivity drops.
- **Zero Anti-Cheat Risk:** Read-only ETW + standard Windows Policy QoS + standard NIC adapter properties only.
- **No Unconsented Elevation:** DPC/ISR kernel tracing and NIC/MSI writes require explicit user consent & UAC elevation (never silent privilege escalation).
