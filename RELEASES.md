# GitHub releases

The Windows 11 x64 installer and its checksum are available as assets on the
[Exo Launcher 3.26.0 release](https://github.com/UhhErix/Exo-Launcher/releases/tag/v3.26.0).

| App | Version | Release asset | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| Exo Launcher | 3.26.0 | `ExoLauncher-3.26.0-Setup.exe` | 212,673,653 | `4dab24d7573ec66f7a4aa15932663591be2b52382b7632288e0f3882b5e8a0b8` |

Run `Get-FileHash -Algorithm SHA256 <path>` in PowerShell and compare the result
with the SHA-256 above or the `.sha256` file attached to the release.
The existing in-app update endpoint stays unchanged until a compatible cutover is verified.

## Discord announcements

Publish the matching version and its release notes to the Cloudflare update mirror before
publishing a stable `vMAJOR.MINOR.PATCH` release on GitHub. GitHub then calls the existing
Cloudflare announcer, which skips duplicate posts. If the mirror wasn't ready, rerun
the failed announcement job after publishing to Cloudflare.

Tag a commit that includes the announcement workflow. Publish through the GitHub UI
or use a GitHub App or personal access token in automation.
Publishing with the default Actions `GITHUB_TOKEN` does not trigger this workflow.
