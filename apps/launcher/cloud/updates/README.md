# Exo update address

Where every Exo app (Launcher, Cinema) asks for its newest version and downloads it:
`https://updates.getexo.app` (`https://exo-updates.exo-erix.workers.dev` is its older name). The apps name this address and
nothing else, so moving downloads never strands an installed copy.

It is a Cloudflare Worker (`src/worker.js`, plain JavaScript, no build step) with one R2 bucket, `exo-releases`, as its
only source (binding `MIRROR`). GitHub is not asked for anything; releases reach the bucket through `publish-release.ps1`, run by
hand or, for an Exo Launcher GitHub release, by the workflow in "From a GitHub release" below.

## What an app asks

| Request | Answer |
| --- | --- |
| `GET /v1/<app>/latest` (`launcher`, `cinema`) | `{ app, tag, version, source: "mirror", files: { setup, sha256, whatsNew }, checks: { setup, sha256, whatsNew } }`, kept at the edge for one minute; an ETag, so a poll that holds it gets a 304 |
| `GET /v1/<app>/download/<tag>/<file>` | The bucket's copy, with byte ranges and a 304 for a matching `If-None-Match`; kept at the edge an hour; 404 for a release that has no such file |
| `GET /v1/<app>/changelog` | The app's `CHANGELOG.md` as `text/markdown; charset=utf-8` (R2 object `<app>/CHANGELOG.md`), the source of getexo.app's Changelog pages; kept at the edge a minute; 404 until uploaded |
| `GET /v1/<app>/payload/<tag>` | The release's file list (R2 object `<app>/<tag>/payload-manifest.json`): every file of the installed app with its path, size and SHA-256. Kept at the edge an hour; a 404 nothing keeps for a release that has none, and the app then updates through Setup |
| `GET /v1/<app>/file/<sha256>` | One app file, gzipped, by the SHA-256 of its bytes (R2 object `<app>/files/<sha256>.gz`), with byte ranges; `immutable` for a year, since the name says what the bytes are; a 404 nothing keeps until uploaded |
| `GET /v1/advisories.json` | Exo Launcher's signed driver advisories (R2 object `advisories.json`), kept at the edge an hour; 404 until uploaded |
| `GET /v1/cache/<github path>` | Exo's copy of a GitHub path the Launcher pins, read-only, by exact key (R2 object `cache/<same path>`), never a redirect; pinned content is `immutable` for a year. A folder listing is `GET /v1/cache/<owner>/<repo>/contents/<folder>?ref=<40-hex commit>` (R2 `cache/<owner>/<repo>/contents/<folder>@<commit>`); a folder has no listing of its own. 404 for anything not stored |

### Exo's cache (`/v1/cache/`)

The Launcher stopped asking GitHub: every pinned download (the tools Nile, Legendary and gogdl, and the DLSS, FidelityFX and
XeSS files at their pinned commits) is read from `updates.getexo.app/v1/cache/<the same path>`. `cache-mirror.mjs` fills it:

```powershell
node cloud\updates\cache-mirror.mjs --launcher C:\Users\Erix\Code\Exo\ExoLauncher-fix-offgithub --rev fix/3.25-off-github
```

It reads the pins from the Launcher's committed source (URLs, sizes, SHA-256s, and each tag from its comment), downloads each
upstream file once, checks its size and SHA-256 before anything is uploaded, and never overwrites an object with a different
hash (`_mirror/manifest.json` in the bucket, outside the served prefix, records what is there). Re-running skips what is
present. Folder listings and tag commits come from the GitHub API and must agree with the pinned bytes before upload.
`cache-licences.json` records each licence file's first hash; a later change is refused. Tests: `node --test test`.

**What is not mirrored, and why:** the dynamic "latest" lookups for FrameGen's three mods and Lucid's release list have
no pin in the Launcher, so nothing is stored for them and they answer 404; the Launcher's fallbacks apply. The vendor
repos' newest releases and DLSS Swapper's newer archives are stored by the cache watch (below). The pinned DLSS Swapper
archive zips (`dlss-swapper-downloads.beeradmoore.com/dlss`, `dlss_d`, `dlss_g`, DLSS 310.9.16) are mirrored at the same
path under `cache/`; the Launcher pins each archive by SHA-256 (DlssSwapService.Releases.cs) and the mirror checks the zip
against that pin. Each zip's inner DLL is checked for its pinned size and SHA-256 and a valid NVIDIA Corporation
Authenticode signature before upload, read without running it. The NVIDIA DLSS licence sits beside each zip. Lucid's own
update is dormant in the Launcher: it holds no per-release SHA-256 pin (`LucidUpdate.Pins`), so nothing is fetched for it.

**Licensing (decided):** the vendor DLLs (NVIDIA DLSS, AMD FidelityFX, Intel XeSS) are mirrored with each upstream licence
file (or the third-party notice where a commit has no licence file) stored next to them in the same folder, served only by
exact key, with no directory listing. The mirror's `cache-licences.json` is the only record of those licence hashes.

**The voice model:** the TitaNet model (`SpeakerModel.cs`, sherpa-onnx's `nemo_en_titanet_small.onnx`, 40,257,283 bytes) is a pin
like the tools: `cache-mirror.mjs` reads it from `SpeakerModel.cs` (the same file list as the tools) and stores it at
`cache/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/nemo_en_titanet_small.onnx`, where the Launcher asks.
Without it Only My Voice would get a 404, so the mirror must have run for 3.25.0 before the release is published.

**GPL tools (Legendary, gogdl, Nile, OptiScaler):** their binaries are mirrored as pinned. Their corresponding source for each
pinned tag (`codeload.github.com/<owner>/<repo>/tar.gz/refs/tags/<tag>`) goes to R2 bucket `exo-source` for getexo.app/source,
with `node source-mirror.mjs --launcher <checkout> --rev <rev>`. Exo's own copyleft add-ons and the bundled OBS hook source go
there with `node addon-source.mjs` (below). Whether the bucket exists now is for `wrangler r2 bucket list` to say: this page
used to say it did not (only `exo-source-backup`), and exo-site's `wrangler.toml` binds `exo-source`.

**Exo's own add-ons' source (`addon-source.mjs`):** Exo Mouse (AGPL-3.0-or-later, `mouse/`), Exo Shaders (GPL-3.0-or-later, the
commit `Build-Release.ps1` prints) and Exo Shield (GPL-3.0, `ExoLauncher/Assets/Extensions/exo-shield`) are built from committed
trees into `<name>-<launcher version>.tar.gz` (deterministic: the commit's time, a fixed gzip header), each with its licence file
and an `EXO-SOURCE.txt` naming the commit; `fetch-obs --tag 32.2.2` adds OBS Studio's tag archive (the one step that reads the
network). `build` and `fetch-obs` write `artifacts/source/` only; `upload --dir artifacts/source` prints its plan, and with `--yes`
puts the archives and then the merged `source/index.json` (the tools' entries stay; a release already listed with other bytes is
refused). Exo Capture is not here: since 3.18.0 it is under its own unpublished terms.

`HEAD` answers the same status and headers as `GET`, with no body. Only an app's own files ever pass: `Exo<App>-Setup.exe`,
its `.sha256` and `whats-new.json`. A tag must look like `v1.2.3`.

`checks` gives each file's `size` and `sha256` from the manifest, so an app verifies its download against what the release
says. An app still checks the installer's SHA-256 itself.

## The manifest (`<app>/latest.json`, schema 2)

```json
{ "schema": 2, "app": "launcher", "tag": "v3.24.1", "version": "3.24.1",
  "files": {
    "setup":    { "name": "ExoLauncher-Setup.exe", "size": 123, "sha256": "..." },
    "sha256":   { "name": "ExoLauncher-Setup.exe.sha256", "size": 80, "sha256": "..." },
    "whatsNew": { "name": "whats-new.json", "size": 456, "sha256": "..." } } }
```

The Worker refuses a manifest with a bad tag, a wrong file name or a malformed hash (503, never a guess). A tag-only
manifest from before schema 2 still answers, with no `checks`.

Releases are not signed: Exo holds no keys (owner's rule), so the apps check the SHA-256, the size and the host. The
signing code that once waited for keys is gone; a stray `files.sig` in a manifest is ignored and a `.sig` is never served.
`CACHE_SCHEMA` in `src/worker.js` is 3 (the edge key names it, so a deploy never serves an older answer).

## What it sees

The IP address of each request, while it's answered. It writes no logs (`observability.enabled = false`, no `console`
calls) and stores nothing about who asked.

## Release in one command

`release.ps1` runs a whole release for one app, and stops at the first failure:

```powershell
# app: launcher | cinema; repo: the app's checkout; version: X.Y.Z, newer than its VERSION
pwsh -File cloud\updates\release.ps1 -App cinema -Repo C:\Users\Erix\Code\Exo\ExoCinema -Version 1.5.1 -WhatIf
pwsh -File cloud\updates\release.ps1 -App cinema -Repo C:\Users\Erix\Code\Exo\ExoCinema -Version 1.5.1
```

1. Preflight, read only: the repo has no uncommitted changes to tracked files and is on `release/<version>`; no `v<version>`
   or `built/v<version>` tag exists; `CHANGELOG.md` has the `## <version>` entry; the app's What's New file has an entry for
   it; `exo-site` sits beside the repo and its `npm test` passes.
2. Writes `VERSION` (launcher: `tools\Publish-Docs.ps1` writes the release page, without `-PublicCommit`).
3. The repo's `tools\Build-Release.ps1` writes `artifacts\` (installer, `.sha256`, `whats-new.json`); `CHANGELOG.md` is copied
   in and must hold the entry.
4. `publish-release.ps1 -WhatIf` must pass; then `publish-release.ps1` uploads, verifies `/v1/<app>/latest` and asks the
   Worker for the release card (`POST /v1/<app>/announce`).
5. `exo-site`: `npm run build` (it reads the version the mirror now serves) and `npx wrangler@4.165.0 deploy`.
6. Live check: `updates.getexo.app/v1/<app>/latest` names the tag, `getexo.app/download/` shows the version, and
   `getexo.app/<app>/changelog` has the entry.

The version bump is left uncommitted; commit and tag it yourself. Nothing is pushed, tagged or made into a GitHub release.
`-WhatIf` runs the preflight and the site's tests and prints every step, with the checks that would fail (the plan still
prints); it changes, builds, uploads, deploys and posts nothing. `cloud\updates\test\release.test.mjs` tests the checks on
throwaway repos (`npm test` here runs it).

## At each release

The release steps are one script: `release.ps1` runs them all, and `publish-release.ps1` is its upload step, which uploads, verifies, and announces. Run it with `-WhatIf` first: it
checks the files and prints the plan, and uploads, deletes, asks and verifies nothing.

The release card runs last, after a live pass. `publish-release.ps1` sends `POST https://updates.getexo.app/v1/<app>/announce`
with `{"tag":"vX.Y.Z","announce":true}`, and the `exo-updates` Worker (`src/announce.js`) posts the card to the app's Discord
releases channel. The Worker acts only for a tag the mirror already serves as newest, and posts each tag once: it claims the tag
in R2 (`announce/<app>/<tag>.json`) before posting, so a re-upload of a live tag posts nothing. `-NoAnnounce` sends
`"announce":false`, which the Worker ignores (nothing is recorded, so a later run can still post the card). A failed card is a warning; the release is still live.

Each app has its webhook and an optional role, as Worker secrets (set once, from `cloud/updates`):

```powershell
npx wrangler@4.165.0 secret put DISCORD_WEBHOOK_LAUNCHER     # the launcher releases channel's webhook URL
npx wrangler@4.165.0 secret put DISCORD_WEBHOOK_CINEMA       # the cinema releases channel's webhook URL
npx wrangler@4.165.0 secret put DISCORD_ROLE_LAUNCHER        # optional: a role id to ping once per release
```

A missing webhook is logged and the card is skipped; a webhook that isn't `https://discord.com/api/webhooks/` (or
`discordapp.com`) is refused. The card has the version, the What's New titles and the download link; the mention is cleared
after it fires. `cloud\updates\test\announce.test.mjs` tests the payload, the missing and refused webhook, the once-per-tag
claim, the removal of older cards and the route (`node --test test`).

The channel keeps one card per app: after the new card is up, the Worker deletes the cards of earlier tags (the cards still
up are listed in `announce/<app>/cards.json`; before that file exists it reads the claims). A delete Discord refuses is tried
again by any later announce call for the live tag, which posts nothing. Without `DISCORD_ROLE_<APP>` the card posts with no
mention: the role is pinged silently (no sound), once, only when that secret holds its id.

The route needs no token: it can only post a card for a tag the mirror serves as newest, once. Anyone could ask early for a
live tag's card, once; nothing else can be posted or suppressed.

```powershell
# app: launcher | cinema; tag: vX.Y.Z; dir: the folder holding the three release files
pwsh -File cloud\updates\publish-release.ps1 -App launcher -Tag v3.24.1 -Dir artifacts            # the dry run (the default)
pwsh -File cloud\updates\publish-release.ps1 -App launcher -Tag v3.24.1 -Dir artifacts -Publish
```

It checks that the three files are in the folder, that the `.sha256` is the installer's SHA-256, that `whats-new.json` is
for the tag, that the tag isn't older than the one served now, and that `<app>/<tag>/` does not already hold any of the three
with other bytes (a published tag's files are never replaced; the same bytes are not uploaded again, so a re-run after a
failed check is safe). Then, in this order, so no app is ever offered a release that is not fully there:

1. it uploads the three files to `<app>/<tag>/`;
2. it checks them through `updates.getexo.app` by tag: each answers 200 with no redirect, the served installer's SHA-256 and
   size are the local ones, the `.sha256` names the same hash, `whats-new.json` is for the tag. A failure stops here, with
   `<app>/latest.json` never touched;
3. only then it writes `<app>/latest.json` (schema 2, with each file's size and SHA-256) and checks that `/v1/<app>/latest`
   names the tag with matching `checks`. If it doesn't, the `latest.json` from before the run is put back. Just before the
   write it reads `<app>/latest.json` from the bucket; when that already names a newer tag (another run published it
   meanwhile) it stops and leaves it alone;
4. when `-Dir` holds the app's `CHANGELOG.md`, it uploads it to `<app>/CHANGELOG.md` and checks `/v1/<app>/changelog`.

With `-Payload <app folder>` the release can also be installed file by file (file-level updates; the design, the cases it
covers and the measured results are in `misc\updates\file-level-updates.md` in the Exo folder). `-Dir` must then hold the
`payload-manifest.json` the build wrote, and the folder must be exactly what that list says. Between steps 2 and 3:

- each distinct file is asked for with a HEAD at `/v1/<app>/file/<sha256>`; those not served yet are gzipped and uploaded
  to `<app>/files/<sha256>.gz`, several at once (`-UploadsAtOnce`), so a release uploads only what changed;
- each upload is read back through the address and must unpack to its SHA-256;
- the file list goes to `<app>/<tag>/payload-manifest.json` last and is checked at `/v1/<app>/payload/<tag>`. An app only
  starts a file-level update once the list is there, so files without a list are harmless and a list never comes first.

A failure in this part is a warning, never a failed release: the list is not left published and the apps use Setup for
that version. `release.ps1` passes `-Payload` by itself when the build wrote the list (`-SetupOnly` leaves it out).
`test/publish-payload.test.mjs` runs it against the real Worker over a folder (`test/fixtures/local-service.mjs`, which
also serves a walk-through on this PC: `node test/fixtures/local-service.mjs <bucket folder> [port]`).

Without `-Publish` it is the dry run: the checks and the plan, nothing uploaded. It prints PASS or FAIL and exits 1 on FAIL;
putting the old `latest.json` back is tried three times. `EXO_TEST_WRANGLER` and `EXO_TEST_UPDATES_BASE` (the test's stand-ins) are refused
unless `-TestRun` is passed, so one left in a shell fails the run. `test/publish.test.mjs` runs the script against a folder standing in for R2 and a
local server standing in for the Worker: the order, a bad served file, a latest that never moves, and a tag that already
holds another build.
Copy the app's `CHANGELOG.md` into `-Dir` at every release. The Changelog page shows whatever was uploaded last: a release
without the file leaves the previous list on getexo.app.

Older tags are kept unless asked for: `-PruneOlder -OlderTag v3.24.0` deletes only the three files of each tag named, and
only after the new release verifies. Wrangler's version is `$WranglerVersion` in the script.

## From a GitHub release (`publish-github-release.ps1`)

When a stable Exo Launcher release is published on GitHub, `.github/workflows/launcher-release-mirror.yml` runs
`publish-github-release.ps1 -Tag <tag> -Publish` on a Windows runner. It needs the repo secrets `CLOUDFLARE_API_TOKEN` and
`CLOUDFLARE_ACCOUNT_ID`; without them it warns and stops. The token steps are in the root `RELEASES.md`. The script:

- reads the release with gh (published, not a draft or pre-release) and downloads `ExoLauncher-<version>-Setup.exe` and its
  `.sha256`, each checked against the size and SHA-256 GitHub recorded at upload (`digest`); the `.sha256` must name the
  installer's hash and file;
- reads the tag's committed tree (`git show`, never the working tree): `VERSION` must be the tag's version, `CHANGELOG.md`
  must hold the entry, and the version's What's New entry (`ui/src/lib/whatsNew.json`) is written the way `Build-Release.ps1`
  writes `whats-new.json` (a `whats-new.json` asset on the release is used instead when there is one);
- stages `ExoLauncher-Setup.exe`, `ExoLauncher-Setup.exe.sha256` (`"<hash>  ExoLauncher-Setup.exe"`, ASCII, no newline),
  `whats-new.json` and `CHANGELOG.md`, byte for byte as a local build does, and runs `publish-release.ps1` on them, so all its
  checks, its order and its rollback apply;
- with `-Publish`, checks `/v1/launcher/latest` once more against GitHub's size and SHA-256. Without it (the default) it is
  the dry run: it prints the R2 keys and runs `publish-release.ps1`'s dry run, which still reads the bucket (wrangler login).

It does not publish the add-ons' source (`addon-source.mjs`), which needs the Exo Shaders checkout and Build-Release's
commit, so that stays a step before the release. It passes no `-Payload` (a GitHub release has no app folder), so the
release updates through Setup.

## Deploying

Deploy with `npx wrangler@4.165.0 deploy` from this folder, with the owner's OK. The Worker never asks GitHub: every release a
live app can ask for, and each app's `CHANGELOG.md`, must be in the bucket before the Worker that serves it is deployed.

Last deployed: version `5fc7bfe7-666a-4c91-bbc0-ccd24a2f8e1f` (2026-11-09, release/3.25.0 base plus the `/v1/cache/` route; the
version before it, `45905582-35b0-4e7e-85c9-5ee635e144b3` of 2026-11-07, is the live release/3.25.0 code with the Discord card).
Checked live after it: all 30 pinned binaries (HEAD and GET 200, size and SHA-256 match, immutable cache header, no redirect),
7 folder listings and 7 tag commits (match), 12 licence files (size and SHA-256 match), and `/v1/launcher/latest`,
`/v1/advisories.json`, `/v1/launcher/changelog`, `POST /v1/launcher/announce` (400 with no body) unchanged. An app-file path and a
folder path through `/v1/cache/` answer 404. `CACHE_SCHEMA` 3. The deployed versions still have the `.sig` route this
code removed (no release ever had a `.sig`, so nothing served changes).

### The cache watch (`src/watch.js`)

A Cron Trigger (`[triggers]` in `wrangler.toml`, every 15 minutes) runs `runWatch`. It fits the free Workers plan: each run does at most one
discovery step (one vendor repo's newest release, or DLSS Swapper's manifest; a full round every six hours) and copies at most one queued item
(one file streamed from upstream into R2, or one small JSON answer). Bytes stream through a native `DigestStream` into R2 with
`FixedLengthStream`, so R2 receives exactly the listing's size, and the stored object's SHA-256 is recorded. Nothing is hashed in JavaScript.

**Vendor SDK files (NVIDIA DLSS, FidelityFX SDK, XeSS):** the Launcher checks each DLL before use: Authenticode publisher and subject,
version, newer than known, and the git blob hash the listing states. A repo's order is its files, then its licence, then `releases/latest` last,
so a reader never sees a release whose files are missing. A refused or repeatedly failing item drops its repo (its files and pointer).

**DLSS Swapper archives:** there is no git blob hash on this path. The catalog gives each archive its zip's SHA-256 when the watch streamed it (the
Launcher checks it when present), and the Launcher always checks the DLL inside: vendor signature, version and size. A Swapper archive that keeps
failing is rejected on its own, recorded in `state.swapper.rejected`, and skipped by discovery; its batch goes on.

**What the watch refuses:** a non-NVIDIA DLSS-G build (any DLSS Swapper entry that mentions `sm86`, `sdli1995` or `dlssg_for`, and DLSS-G entries
that do not say NVIDIA DLSS-G); a version whose major part is over 1000; any file or archive over 256 MiB; a manifest over 4 MiB; more than 50
archives per DLL file; a queue over 500 items. Upstream requests go only to `api.github.com`, `raw.githubusercontent.com`,
`beeradmoore.github.io` and the archive host, and each redirect is checked by hand against that list.

Never-overwrite is by size: a key that exists must have the listing's size, and is then kept (its bytes are not compared, which would mean reading
them back). A different size is refused. The mirror's canonical bytes are written for commits and listings, so a key cache-mirror already wrote is
present, not refused.

**Crash safety:** a run writes the queue head and its attempt count to `_watch/state.json` before it does any work. A run killed mid-work leaves
that attempt counted, and the next run puts the item back, or drops it after `MAX_ATTEMPTS`.

**Watched DLSS Swapper families:** the DLSS families (`nvngx_dlss`, `nvngx_dlssd`, `nvngx_dlssg`) and the XeSS families (`libxess`, `libxess_dx11`,
`libxess_fg`, `libxell`), each newer than the newest pin in `SWAPPER_FLOOR`. FSR 3.1 is not watched: its loader versions do not order like its
releases, so the Launcher takes FSR 3.1 from its pins only.

**Measured (local workerd, instant fake upstream):** a release with a 40 MB file took 9 cron ticks; the slowest tick 792 ms wall, mean 102 ms (wall
times: workerd's CPU time is not exposed locally). The heaviest in-Worker parse, the 542 KB DLSS Swapper manifest, takes 0.6 ms of JSON parsing in
V8.

Tests: `node --test test` (the scheduled handler in workerd against a fake upstream; the security fixes in `test/watch-security.test.mjs`; the
floor and the vendor file names checked against the Launcher's sources).

**Deploy (not done; the owner approves):** `npx wrangler@4.165.0 deploy` from this folder, on the free plan. Check the first runs through
`_watch/state.json` in the bucket: the Worker's logs are off (`[observability]`).
