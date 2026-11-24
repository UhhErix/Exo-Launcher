EXO DATAFLOW: Source-Aware Self-Healing Crash Triage, Commit Attribution & Signed Mitigation Feed (task-name: df-08-self-healing-triage)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

When a new release hits a crash on a specific GPU/driver/OS combination, Exo should (1) group it cleanly without collapsing unrelated async crashes, (2) automatically pinpoint the exact introducing commit/PR in <30 s, and (3) heal affected players within an hour via local circuit breakers and Ed25519-signed declarative safe-mode mitigations — without waiting days for a full release.

## Today

- `cloud/reports/src/fingerprint.js:29-50` takes the first 3 `at ...` stack frames verbatim, without skipping CLR/WinUI/Rust async plumbing (`System.Runtime.CompilerServices.TaskAwaiter`, `ExceptionDispatchInfo.Throw`, `AsyncTaskMethodBuilder`), causing unrelated async crashes to collapse into a single GitHub issue!
- `cloud/reports/src/issues.js:125-143` calls Workers AI (`@cf/meta/llama-3.3-70b-instruct-fp8-fast`) on the first report only with a generic prompt and **zero** visibility into Exo's git history, release diff, or past closed issues.
- `ExoLauncher/Helpers/CrashReport.cs:194, 257, 291` pretty-prints JSON (`WriteIndented = true`) on the wire then re-parses to compact, uses a per-call `HttpClient`, and sends uncompressed 8 KB excerpts.
- `ExoLauncher/Services/DriverAdvisories.cs` + `cloud/updates/advisories.mjs` already implement an Ed25519-signed daily policy JSON (`/v1/advisories.json`), and `MainWindow.SafeMode.cs` already exists — but neither is wired for subsystem crash self-healing!

## 1. Benchmark first, with real numbers

- Build a 30-case crash-grouping test corpus (async C# exceptions, WinUI XAML/COM crashes, Rust `exo-ui` panics, WebView2 crashes, capture/shader addon crashes, duplicate variations across OS builds):
  - Measure grouping precision & recall (Rand index / F1) of current `fingerprint.js` vs plumbing-aware fingerprinter.
  - Measure commit-attribution top-3 accuracy on 15 historical Exo bugfix commits (`v3.26`–`v3.30`) using `git diff` file/symbol overlap.
  - Measure local subsystem circuit-breaker recovery time & Ed25519 mitigation verification latency (<1 ms).
- **Targets:** Misgrouped/duplicate crash issues **-70%**, top-3 suspected-commit accuracy **≥80%**, local crash-loop auto-recovery **after 2 crashes (0 manual steps)**, signed mitigation rollout **<1 hour** with zero new app binary.

## 2. Architecture & implementation

1. **Plumbing-Aware Fingerprinting (`cloud/reports/src/fingerprint.js`):**
   - Maintain an explicit frame-skip list (`System.Runtime.*`, `System.Threading.Tasks.*`, `Microsoft.UI.Dispatching.*`, `std::panicking::*`, `core::ops::*`) so the fingerprint picks the top 3 **Exo application frames** (`ExoLauncher.*`, `exo_ui::*`, `exo_rpc::*`, `ExoCapture.*`).
   - Support both .NET stack traces and v4 Rust panic backtraces cleanly.
2. **Release-Diff Commit Attribution & Regression Auto-Verification (`cloud/reports/src/{issues.js, github.js}`):**
   - When a new crash group opens for `version`, query GitHub's `GET /repos/UhhErix/Exo-Launcher/compare/v<prev>...v<version>` (cached in D1/Cache API per version pair!) to get changed file paths & commit subjects.
   - Match the crash's top Exo class/module names against changed files, include the top 3 suspected commits + PR links in the Workers AI triage prompt and the GitHub issue body!
   - Add crash-free verification: when a closed issue sees 0 reports in `vNext` after N reported sessions (or spikes >5× baseline velocity), auto-comment / auto-reopen with exact version velocity!
3. **Local Subsystem Circuit Breakers + Signed Mitigation Feed (`ExoLauncher/Services/SafeMitigations.cs` + `cloud/updates/advisories.mjs`):**
   - **Local self-healing (offline, zero server needed):** Track per-subsystem crash counters (`hevc_encode`, `hw_video_decode`, `quic_http3`, `shm_ipc`, `dlss_scan`, `shader_addon`) in `subsystem-health.json` keyed by `(appVersion, gpuDriverVersion)`. After 2 crashes in 10 minutes inside a subsystem, auto-trip its safe fallback (e.g., H.264 encode, software decode, named-pipe fallback) and log a single clear notice! Resets automatically when Exo or the GPU driver updates.
   - **Remote signed mitigations:** Add an optional `mitigations` array to the existing Ed25519-signed `/v1/advisories.json` schema (`advisories.mjs`). Each entry is strictly allowlisted (`id`, `minVersion`, `maxVersion`, `osBuild?`, `gpuVendor?`, `driverRange?`, `flag` from a fixed enum of safe fallbacks — **never arbitrary code, URLs, or paths**), signed offline by the maintainer's Ed25519 key (never auto-signed by AI!).
4. **Wire Cleanup (`CrashReport.cs`):**
   - Compact JSON on wire (`WriteIndented = false`), shared `ExoHttp.Strict`, and gzip/Brotli POST compression.

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (add `SafeMitigationsTests`, circuit-breaker tests, Ed25519 signature tampering tests).
- `npm test` in `apps/launcher/cloud/reports` and `apps/launcher/cloud/updates` (read-only on production — never deploy!).

## 4. Deliver

- Branch `lab/df-08-self-healing-triage` from `origin/main`.
- `improve/df-08-self-healing-triage-report.md` with grouping F1 before/after, commit-attribution accuracy table, circuit-breaker demo log, and signed mitigation security model.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-08 self-healing crash triage" --body "<grouping F1 improvement, commit attribution accuracy, mitigation & circuit-breaker verification>"`. Effort: high.
