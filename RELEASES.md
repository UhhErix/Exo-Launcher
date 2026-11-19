# GitHub releases

The Windows 11 x64 installer and its checksum are available as assets on the
[Exo Launcher 3.26.0 release](https://github.com/UhhErix/Exo-Launcher/releases/tag/v3.26.0).

| App | Version | Release asset | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| Exo Launcher | 3.26.0 | `ExoLauncher-3.26.0-Setup.exe` | 212,673,653 | `4dab24d7573ec66f7a4aa15932663591be2b52382b7632288e0f3882b5e8a0b8` |

Run `Get-FileHash -Algorithm SHA256 <path>` in PowerShell and compare the result
with the SHA-256 above or the `.sha256` file attached to the release.
## The in-app updater

Exo Launcher never asks GitHub for its own updates. It asks `updates.getexo.app`, which serves
R2 bucket `exo-releases`. Publishing a stable `vMAJOR.MINOR.PATCH` release here starts
`.github/workflows/launcher-release-mirror.yml`, which puts that release into the bucket so
installed copies are offered it:

1. It reads the release and downloads `ExoLauncher-<version>-Setup.exe` and its `.sha256`. Each
   must match the size and SHA-256 GitHub recorded at upload, and the `.sha256` must name the
   installer's hash. Releases are not signed, so there is no signature to check.
2. From the tag's committed tree it checks `apps/launcher/VERSION`, takes the version's What's New
   entry and `CHANGELOG.md`, and stages the files as `tools/Build-Release.ps1` writes them. A
   `whats-new.json` asset on the release is used instead when there is one.
3. It runs `apps/launcher/cloud/updates/publish-release.ps1 -Publish`. If the bucket already holds
   any of the tag's files with other bytes, the run stops. Otherwise it uploads `launcher/<tag>/`,
   checks those files through the address, then writes `launcher/latest.json` and
   `launcher/CHANGELOG.md`. If `/v1/launcher/latest` doesn't then name the tag, it puts the old
   `latest.json` back.
4. It checks `/v1/launcher/latest` once more: the tag, with the installer's size and SHA-256 as
   GitHub recorded them. Any mismatch fails the run.

Run it by hand from Actions > *Publish release to the update mirror* > *Run workflow* with a tag
(tick *dry_run* for the checks only), or on a PC with gh and wrangler:

```powershell
pwsh -File apps\launcher\cloud\updates\publish-github-release.ps1                 # dry run, newest release
pwsh -File apps\launcher\cloud\updates\publish-github-release.ps1 -Tag v3.27.0 -Publish
```

Attach `artifacts\whats-new.json` from `Build-Release.ps1` to the release beside the installer, so
the mirror gets the exact notes that build wrote. The add-ons' source archives (`addon-source.mjs`,
R2 `exo-source`) still go up by hand before the release: they need the Exo Shaders checkout and the
commit Build-Release printed, and a GitHub release carries neither.

### Cloudflare secrets (once)

The workflow needs two repository secrets. Without them it warns that the release was **not**
mirrored and stops.

1. Cloudflare dashboard > My Profile > API Tokens > Create Token > Custom token (Get started).
2. Name it `exo-github-release-mirror`. Permissions: **Account** > **Workers R2 Storage** >
   **Edit**. Account Resources: **Include** > the Exo account only. Leave out zone permissions and
   the IP filter (GitHub's runners have no fixed address). Set an expiry if you want one. Create the
   token and copy it; Cloudflare shows it only once.
3. Add it in one of two ways:
   - GitHub > UhhErix/Exo-Launcher > Settings > Secrets and variables > Actions > New repository
     secret: `CLOUDFLARE_API_TOKEN` = the token, and `CLOUDFLARE_ACCOUNT_ID` = the account ID
     (dashboard > Workers & Pages, on the right).
   - Or save it as the Windows user variable `EXO_R2_PUBLISH_TOKEN`
     (`[Environment]::SetEnvironmentVariable('EXO_R2_PUBLISH_TOKEN', '<token>', 'User')`). Then
     `gh secret set CLOUDFLARE_API_TOKEN -R UhhErix/Exo-Launcher` can read it from standard input,
     so it is never printed.

The token can edit every R2 bucket in the account, because Cloudflare's account tokens are not
scoped per bucket. Don't reuse the local `CLOUDFLARE_API_TOKEN` that deploys Workers.

## Discord announcements

Publishing a stable `vMAJOR.MINOR.PATCH` release on GitHub also calls the existing Cloudflare
announcer, which skips duplicate posts. It waits up to 20 minutes for the mirror workflow (above)
to serve the tag. If the mirror still isn't ready, rerun the failed announcement job once it is.

Tag a commit that includes both workflows. Publish through the GitHub UI or use a GitHub App or
personal access token in automation. Publishing with the default Actions `GITHUB_TOKEN` triggers
neither workflow.
