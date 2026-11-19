# Contributing to Exo Launcher

Thanks for helping. Bug reports, ideas, clearer wording and code are all welcome. This page covers the whole repository; the Launcher's own working rules are in [apps/launcher/AGENTS.md](apps/launcher/AGENTS.md).

## Before you start

- **Report a bug or suggest a feature** in [Issues](https://github.com/UhhErix/Exo-Launcher/issues/new/choose). Ask questions in [Discussions](https://github.com/UhhErix/Exo-Launcher/discussions).
- **Security problems** go through [private vulnerability reporting](https://github.com/UhhErix/Exo-Launcher/security/advisories/new), never a public issue. See [SECURITY.md](.github/SECURITY.md).
- **Open an issue before the code** for anything that changes what Exo sends over the network, or adds a download, an install or an elevated step.
- Keep passwords, tokens, account exports, personal paths and unblurred screenshots out of issues and pull requests.

Everyone follows the [code of conduct](.github/CODE_OF_CONDUCT.md).

## What's where

Everything is under [apps/launcher](apps/launcher).

| Folder | What it is |
| --- | --- |
| `ExoLauncher/` | The Windows host: WinUI 3 on .NET, with one WebView2 |
| `ui/` | The app's page: React and Tailwind, talking to the host over JSON-RPC (`ui/src/lib/host.ts`) |
| `mouse/` | Exo Mouse, the Devices room: a separate AGPL-3.0-or-later program |
| `ExoLauncher.SteamIpc/` | The helper every Steam install, update and uninstall goes through |
| `ExoLauncher.Tests/` | xUnit tests for the host's pure logic |
| `cloud/`, `relay/` | Exo's own Cloudflare Workers: account, covers, reports, updates and the relay |
| `setup/`, `voice/` | The installer and the voice libraries (C++) |
| `tools/` | The end-to-end suite (`tools/e2e`), release and maintenance scripts |
| `docs/` | Help pages for users; developer detail is in `docs/architecture/` |

Read the page you need: [ARCHITECTURE.md](apps/launcher/ARCHITECTURE.md) for the shape, [CONTEXT.md](apps/launcher/CONTEXT.md) for the words Exo uses, [DESIGN.md](apps/launcher/DESIGN.md) for the look. The [Rooms roadmap](ROADMAP.md) says what each Room has today.

## Build and run

You need Windows 11 x64. [BUILDING.md](apps/launcher/BUILDING.md) lists the tools and versions. Then:

```powershell
cd apps/launcher
npm ci --prefix ui
npm ci --prefix mouse
pwsh -File Run-ExoLauncher.ps1
```

`Run-ExoLauncher.ps1` builds the Debug host and the page, then starts Exo. For hot reload, run `npm run dev` in `ui/` and start the Debug build with `EXO_UI_DEV_URL=http://localhost:5174`.

## Checks to run

Launcher CI (`.github/workflows/launcher-ci.yml`) runs the UI check and the .NET tests on every pull request, and its `test` job must pass before a PR merges. Run the checks for what you changed before you open one anyway: CI does not cover Workers, Exo Shaders or the installed app. Commands run from `apps/launcher`.

| You changed | Run |
| --- | --- |
| `ui/` | `npm run check`, `npm run dead` and `npm run build`, in `ui/` |
| `mouse/` | `npm run check` and `npm run build`, in `mouse/` |
| The host or `ExoLauncher.SteamIpc/` | `dotnet build ExoLauncher.sln -c Release -p:Platform=x64`, then `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` |
| A Worker in `cloud/` or `relay/` | `npm ci`, then `npm test`, in that folder |
| `tools/e2e/` | `node --test tools/e2e/lib/*.test.mjs` |
| Anything you can see in the app | `node tools/e2e/run.mjs --changed` on the installed app ([tools/e2e/README.md](apps/launcher/tools/e2e/README.md)) |

When a check fails, fix the code, not the check. An exception goes in the check's own config, with the reason beside it. AGENTS.md lists every rule the checks enforce.

## The lines that do not move

A change that crosses one of these won't be merged. The full list is in [AGENTS.md](apps/launcher/AGENTS.md).

- Exo never edits a game's files or touches anti-cheat, and never kills an anti-cheat process. The one file change it makes, an upscaler swap, uses the vendor's signed file, keeps the original and asks first.
- Nothing downloads, installs or elevates without the user's say-so.
- No ads, no third-party analytics and no tray agents.
- Every host Exo's native code contacts is listed in `ExoLauncher/NetworkDestinations.cs` and in [PRIVACY.md](apps/launcher/PRIVACY.md).
- Secrets and file paths stay in native code; the page gets only what it shows.
- Exo itself never captures the screen or sends synthetic input.

## Pull requests

- Branch from `main` and keep one change per pull request. The template asks what changed and how you tested it.
- Match the code around it: its naming, its comments and Exo's design. For anything visual, follow DESIGN.md and the `exo-design` skill in `apps/launcher/.agents/skills/`.
- Words in the app are plain and short, in sentence case, with no emoji.
- A change people will notice adds an entry to the next release's section of [CHANGELOG.md](apps/launcher/CHANGELOG.md), which becomes that release's notes. Write it in the same voice and end it with how it was checked: `_Verified: on screen._`, `_Verified: end-to-end._` or `_Checked in code only._`
- Change the docs a change makes wrong in the same pull request. Developer detail goes in `docs/architecture/`.

[@UhhErix](https://github.com/UhhErix) owns every file. `main` takes changes only through pull requests, which are squash-merged; direct pushes and force-pushes are blocked. Dependabot opens weekly updates for GitHub Actions, npm and NuGet.

## What you agree to

By contributing, you agree to license your contribution under the [MIT license](LICENSE). Send only work you have the right to share.

Exo Mouse (`mouse/`) is AGPL-3.0-or-later. Never move Exo code into `mouse/` or AGPL code into `ui/`. Third-party components keep their own terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). For Exo's names and logos, see [TRADEMARKS.md](apps/launcher/TRADEMARKS.md).
