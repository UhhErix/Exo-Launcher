# GitHub releases

The Windows 11 x64 installer and its checksum are available as assets on the
[Exo Launcher 3.30.3 release](https://github.com/UhhErix/Exo-Launcher/releases/tag/v3.30.3).

| App | Version | Release asset | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| Exo Launcher | 3.30.3 | `ExoLauncher-3.30.3-Setup.exe` | 137,037,376 | `60bf6f1b969c0d64d32b70593b43c2501dbc6af3a49505e1b9bcf5a4e085d4d8` |

Run `Get-FileHash -Algorithm SHA256 <path>` in PowerShell and compare the result
with the SHA-256 above or the `.sha256` file attached to the release.
## The in-app updater

Exo Launcher never asks GitHub for its own updates. It asks `updates.getexo.app` (Worker `exo-updates`), and that Worker
reads this repository's releases itself, so publishing a stable `vMAJOR.MINOR.PATCH` release here is the whole release:
there is no upload step, no workflow and no Cloudflare token in this repository.

1. Within about five minutes the Worker sees the release (it asks GitHub's API for the newest stable release at most once
   every five minutes, without a token). It offers it only when the installer and its `.sha256` are attached, the
   `.sha256` names the installer's SHA-256, and GitHub's own size and SHA-256 for the installer agree with it; the tag's
   `apps/launcher/VERSION` must be the version and its `CHANGELOG.md` must hold the entry.
2. It writes the release's small files into R2 bucket `exo-releases`, never replacing ones already there: the `.sha256`,
   `whats-new.json` (the release's `whats-new.json` asset, else the version's entry in the tag's `ui/src/lib/whatsNew.json`),
   `files.json` when the release has the file-level update, then `launcher/CHANGELOG.md` and `launcher/latest.json`.
3. It copies the installer (and `files.pack`) from the release into R2, checking the SHA-256 as R2 stores it; a copy that
   doesn't match is deleted and never served. Until the copy is there, a download passes straight through from GitHub, so
   players' PCs still only ever contact `updates.getexo.app`.
4. If GitHub is down or refuses, the Worker keeps answering with what R2 already holds.

Attach `artifacts\whats-new.json` from `Build-Release.ps1` beside the installer and its `.sha256`, so the notes are exactly
the ones that build wrote. The offered version never moves backwards: to pull a bad release, publish a fixed newer one.
The add-ons' source archives (`addon-source.mjs`, R2 `exo-source`) still go up by hand before the release: they need the
commit Build-Release printed, which a GitHub release doesn't carry.

The repository secret `CLOUDFLARE_ACCOUNT_ID` (and `CLOUDFLARE_API_TOKEN`, if it was set) was only for the upload workflow
this replaced, and nothing uses it now. Details: `apps/launcher/cloud/updates/README.md`.

### Only the files that changed

Each release also carries `files.json` and `files.pack`, which `tools/Build-Release.ps1` writes
into `artifacts` beside the installer (`tools/AppFiles`, format in
`apps/launcher/ExoLauncher/Services/AppFiles.cs`):

- `files.pack` is every file of the installed app folder (the payload with its
  `payload-manifest.json`, and `ExoSetup.exe`), each compressed with zstd on its own, one after
  another in path order. The same folder always packs to the same bytes.
- `files.json` lists each file's path, size, SHA-256, and the offset and length of its bytes in
  the pack, with the pack's size and SHA-256 and the *installer revision*: one SHA-256 over
  `tools/ExoLauncher.nsi` and the installer's helper scripts.

`pwsh -File tools\Build-Release.ps1 -Release vX.Y.Z` uploads both (and `whats-new.json`) to an
existing GitHub release; it never replaces an asset that is already there. The installer stays the
download for new installs.

When `/v1/launcher/latest` lists the two (`/v1/launcher/<tag>/files.json` and `files.pack`, or
the same under `download/`), Update now reads `files.json`, hashes the installed app folder (a
cache keyed by size and write time makes a second look quick), downloads only the changed and new
files as byte ranges of the pack (adjacent ones merged, four at a time), checks each against its
SHA-256, and builds the new folder beside the installed one from hard links to the unchanged files.
Exo then closes and `tools/installer/Apply-ExoDelta.ps1` swaps the folders as Setup does (the same
payload check, `app.previous` kept until `Complete-ExoUpdate.ps1` sees the new build start), puts
`ExoSetup.exe` and the uninstaller in, and sets the version and size in Apps & features. Because it
compares with what is installed, skipping versions works the same way.
A try that stops before the swap (cancelled, offline, Exo closed) keeps the files it already downloaded
and checked in the updates folder's `delta-parts`, named by SHA-256; the next try checks each again and
downloads only the rest.

The installer is used instead when the release lists no files, the copy was not installed by an
installer that recorded its revision (every install before this change, and developer swaps), the
release's installer revision differs (it may change something outside the app folder), a game runs,
the changed files are not smaller than the installer, the last file-level try at that version failed,
or anything is refused or fails to verify on the way. Releases are not code-signed today; the files a
file-level update writes are the same bytes as the installer's.

## Discord announcements

The release card is posted by the same Worker the first time it offers a new release, once per tag. Publishing a stable
release also runs `.github/workflows/announce-release.yml`, which asks the Worker for the card straight away (the Worker
reads the new release from GitHub then) and retries for up to 20 minutes while the release isn't ready to offer. Both ask
for the same card; the Worker posts it once.

Publish through the GitHub UI or use a GitHub App or personal access token in automation. Publishing with the default
Actions `GITHUB_TOKEN` doesn't start the workflow; the Worker still posts the card when it sees the release.
