# Building Exo Launcher from source

You may build and run Exo on your own devices to check it against the official release (see [LICENSE](LICENSE)). This page is the whole path from a clean Windows 11 PC to a running build.

## What you need

- Windows 11 x64.
- Visual Studio 2022 or newer, or its Build Tools, with the **.NET desktop development** workload and the Windows 11 SDK (10.0.26100). Add **Desktop development with C++** only if you build the add-ons.
- The .NET SDK named in `global.json` (10.0.x; stable releases only).
- Node.js 24.19 or newer (CI uses 26) with npm.
- Git.

## Build and run

```powershell
# From the repository root:
cd apps/launcher
npm ci --prefix ui
npm ci --prefix mouse
pwsh -File Run-ExoLauncher.ps1
```

`Run-ExoLauncher.ps1` builds the Debug host and the UI, then starts Exo. `-NoBuild` starts the last build.

## The same checks CI runs

From `ui/`:

```powershell
npm run check
npm run dead
npm run build
```

From `mouse/`: `npm run check`, then `npm run build`.

From the repository root:

```powershell
dotnet build ExoLauncher.sln -c Release -p:Platform=x64
dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64
```

The internal security harnesses are omitted from this snapshot. The installer checks (`tools\Test-InstallerPayload.ps1`, `tools\Test-UpdateRollback.ps1`) run without installing anything.

## The add-ons

Three programs ship beside Exo and are separate programs, each run as its own process. Exo Mouse (`mouse/`) and Exo Shaders (`shaders/`) are in this repository, and each release's source is at [getexo.app/source](https://getexo.app/source); Exo Capture is omitted from this snapshot; see the [third-party notices](THIRD_PARTY_NOTICES.md):

| Add-on | License | Source | Build |
| --- | --- | --- | --- |
| Exo Capture | Exo's own terms (`capture/LICENSE`) | Not published | `pwsh -File captureuild.ps1` (Visual Studio C++ tools, CMake, Ninja) |
| Exo Mouse | AGPL-3.0-or-later | [getexo.app/source](https://getexo.app/source) | `npm run build` in `mouse/` |
| Exo Shaders | GPL-3.0-or-later, with a section 7 exception | `shaders/` and [getexo.app/source](https://getexo.app/source) | `pwsh -File shaders\engine\build\publish-service.ps1` (the .NET SDK; its native files are prebuilt from `shaders\proxy`) |

## Checking a build against the release

- The installer of each release has a SHA-256 beside it. That checks your download, not your build.
- Builds are not promised to be byte-for-byte reproducible: a different compiler, SDK or ReadyToRun pass changes the bytes. To check the official release against the source, read what it does rather than hash it: [PRIVACY.md](PRIVACY.md) lists every network destination, [SECURITY.md](SECURITY.md) the trust boundaries, and the tests in `ExoLauncher.Tests` (`NetworkDestinationsTests`, `BannedNativeImportsTests`) fail the build when code strays from them.
- The upstream script `tools/Build-Release.ps1` publishes the host and UI and packages the add-ons with NSIS. It needs the omitted Exo Capture source to build the same installer.

## When something does not build

Send a report through [getexo.app/help](https://getexo.app/help) with the command you ran and the last lines of its output. Leave out tokens, account details and personal paths.
