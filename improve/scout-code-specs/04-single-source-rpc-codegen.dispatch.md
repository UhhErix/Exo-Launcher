EXO LAB: Single-Source Schema-Driven RPC Codegen (`exo-idl`) Across C#, Rust & TypeScript (task-name: single-source-rpc-codegen)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... how everything gets sent ... the code itself ... to see if we can make something better, more experimental, not seen before."
> Vision: zero-drift polyglot contracts, zero-reflection AOT-ready JSON, and sub-second contract verification across the .NET host, Rust/Rune `exo-ui`, and TypeScript `ui/`.

## Today (Measured at `e0289d9`)

- **342 RPC methods and 78 host events** are hand-maintained in **3 separate languages**:
  - C# host: 61 `ExoLauncher/Services/WebHostBridge*.cs` files (>16,500 LOC) returning reflection-serialized anonymous objects (`new { ... }`), with `0` `[JsonSerializable]` attributes (`JsonSerializerIsReflectionEnabledByDefault=true` blocks NativeAOT!).
  - TypeScript UI: `ui/src/lib/host.ts` (3,192 LOC) + `hostMock.ts` (2,310 LOC).
  - Rust native UI: `native-ui/crates/exo-rpc/src/` (`lib.rs`, `types.rs`, `discord_types.rs`, `pc_types.rs`, `media_types.rs` = **5,032 LOC** of hand-written DTOs and method constants!).
- `ui/scripts/check-host-contract.mjs` takes **19.16 seconds** on Ryzen 5 5600X (concatenating all 755 `.cs` files into memory!), generates ONLY 3 records (`ExoUpdateResponse`, `ExoWhatsNewRelease`, `ExoWhatsNewItem`), validates **zero** RPC request/response field shapes across C#/TS/Rust, and missed **2 orphaned dead arms** in `WebHostBridge.cs` (`store.calendar` at L986, `devices.usbMap` at L996)!

## 1. Design the Schema & Compiler (`tools/exo-idl`)

- Author a concise, human-readable typed schema format (e.g., `contracts/host-v1/*.toml` or `.exo` / typed JSON Schema) capturing for every method and event:
  - method name, request params struct, response struct, event payload struct;
  - scheduling lane (`interactive | io | background`), max request bytes, default timeout ms, cancellation support;
  - doc summary (kept out of generated code comments so `check-comments.mjs` stays 0-comment clean with `// Generated from exo-idl`).
- Build a fast compiler (`tools/exo-idl`, Rust binary or zero-dep Node/TS tool) that validates the schema and emits deterministically in **<200 ms**:
  1. **C# (`ExoLauncher/Generated/ExoRpc.g.cs`)**: `sealed record` DTOs + `[JsonSerializable]` `JsonSerializerContext` (`ExoRpcJsonContext`, 100% NativeAOT-compatible, zero reflection!) + typed `IExoRpcHandlers` / generated switch dispatcher + `MaxCharactersFor` table!
  2. **Rust (`native-ui/crates/exo-rpc/src/generated.rs`)**: `#[derive(Serialize, Deserialize, Debug, Clone)]` structs + `pub const METHOD_*` + typed `RpcClient` extension methods + `HostEvent` enum variants!
  3. **TypeScript (`ui/src/lib/hostWire.g.ts`)**: exact exported types + `HostEventMap` + runtime schema descriptors + `ui/contracts/host-v1.json`!
  4. **Bidirectional Orphan & Compatibility Gate**: fails if `WebHostBridge.cs` has ANY arm not in schema (immediately catching `store.calendar` and `devices.usbMap`), or if a breaking wire change is made without bumping protocol version or adding an explicit migration allowlist.

## 2. Incremental Migration Plan (No Flag-Day Risk)

- **Milestone 1 (<2 days proof):** Migrate the 30 hottest boot/core methods (`shell.bootstrap`, `library.*`, `game.*`, `store.*`, `settings.*`, `art.base`, `app.*`) + all 78 events into `contracts/host-v1/`. Generate C# SourceGen JSON + Rust `exo-rpc` + TS `hostWire.g.ts`. Delete the 2 orphaned dead arms (`store.calendar`, `devices.usbMap`) and verify `ExoBridgeProtocol` serializes generated DTOs with zero reflection!
- **Milestone 2:** Migrate Discord, Music, PC, Devices, Shaders, Capture, and Accounts domains incrementally so `WebHostBridge` reaches 100% typed SourceGen JSON and `exo-rpc` drops ~4,500+ LOC of hand-written boilerplate.
- **Bonus experimental:** Generate a **wire-level roundtrip fuzz test** (C# encodes sample DTO -> Rust `exo-rpc` decodes & re-encodes -> C# decodes -> byte-for-byte/field equality) so a serialization mismatch between C# `System.Text.Json` and Rust `serde_json` is impossible!

## 3. Targets & Deliver

- **Targets:** Codegen + contract check runtime **19.16 s → <0.5 s** (`>35x` faster); **0** orphaned bridge arms; **0** reflection JSON in migrated RPCs (unblocking NativeAOT host!); **100%** compile-time field parity across C#, Rust, and TS; **>4,000 LOC** hand-written boilerplate deleted once complete.
- Branch `lab/single-source-rpc-codegen` from `origin/main`.
- `improve/single-source-rpc-codegen-report.md`: schema design, codegen timing, C#/Rust/TS roundtrip test output, reflection-free serialization proof, and migration tracker for all 342 methods & 78 events.
- Verify `ExoLauncher.Tests`, `cargo test --workspace --locked`, and `ui` `npm run check` all pass.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "single-source RPC codegen results" --body "<codegen ms, methods/events migrated, LOC deleted, roundtrip & gate status>"`. Effort: high.
