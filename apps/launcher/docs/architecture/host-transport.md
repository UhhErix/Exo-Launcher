# Host Transport (`IHostTransport`)

`WebHostBridge` talks to its active UI shell through `IHostTransport` (`ExoLauncher/Services/IHostTransport.cs`) instead of coupling directly to `CoreWebView2`.

## Implementations

| Transport | File | Role |
| --- | --- | --- |
| `WebViewHostTransport` | `ExoLauncher/Services/WebViewHostTransport.cs` | Shipping React/WebView2 shell bridge using `PostWebMessageAsJson`, `WebViewTrustPolicy.IsTrustedBridgeSource`, and shared-buffer video frames. |
| `NamedPipeHostTransport` | `ExoLauncher/Services/NamedPipeHostTransport.cs` | Out-of-process transport for the native Rune (`anomalyco/rune`) UI client over `\\.\pipe\exo-launcher-rpc-*`. |

## Named Pipe Wire Protocol & Security

- **ACL hardening:** `NamedPipeServerStreamAcl.Create` applies a `PipeSecurity` DACL granting `PipeAccessRights.FullControl` exclusively to `WindowsIdentity.GetCurrent().User` (current-user SID). `WorldSid` and `AuthenticatedUserSid` are never granted access.
- **Framing:** Bidirectional UTF-8 newline-delimited JSON (`\n` terminated) carrying standard `host-v1.json` requests, responses, and events.
- **Inbound size limit:** `MaxMessageBytes = 4 * 1024 * 1024` (4 MiB). Any client message exceeding 4 MiB before `\n` triggers immediate disconnect and `SessionReset` before accepting a new connection.
- **Outbound concurrency:** Outgoing writes serialize through a bounded single-reader `Channel<string>` writer loop so concurrent host events never interleave mid-line.
- **Binary video frames:** `PostBinaryFrame(metaJson, frame)` emits `{"event":"binaryFrame","meta":<metaJson>,"data":"<base64>"}` on the pipe (or uses `CoreWebView2SharedBuffer` on WebView2).
