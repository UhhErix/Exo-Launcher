# The native UI moves to Rune (`anomalyco/rune`)

- Decided by: owner, 2026-11-20
- Status: in force; supersedes [2026-11-09](2026-11-09-native-ui-gpui.md)
- Rule: AGENTS.md › Stack; [host-transport.md](../architecture/host-transport.md)

The planned native UI target moves from GPUI (`exo-gpui`) to Rune (`anomalyco/rune`, Rust + `wgpu` DX12). The .NET 10 host keeps all store integrations, game launch, tuning, Discord, capture and shader add-on orchestration, and security boundaries behind `IHostTransport`, communicating over an ACL-hardened named pipe using the `host-v1.json` contract. WebView2 stays in the host only for embedded browser pages that cannot leave a browser. Until the native Rune shell ships, the React/WebView2 stack remains the shipping UI covered by AGENTS.md's rules and checks.
