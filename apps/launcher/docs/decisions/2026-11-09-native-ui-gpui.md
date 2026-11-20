# The UI moves to GPUI on `exo-gpui`

- Decided by: owner, 2026-11-09
- Status: superseded by [2026-11-20](2026-11-20-native-ui-rune.md)
- Rule: AGENTS.md › Stack
- Plan: `..\misc\gpui-foundation\PLAN.md`

Only the UI moves, to GPUI on our fork `exo-gpui`. The .NET host stays and talks to the native UI over named pipes with the `host-v1.json` contract, and WebView2 stays in the host for pages that cannot leave a browser. Until the native UI ships, the React/WebView2 stack is what ships and what AGENTS.md's rules and checks cover; no new UI work starts on a third stack.
