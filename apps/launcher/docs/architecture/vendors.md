# Vendor backends (developer notes)

How Exo drives each store's tool or client: commands, progress parsing, GOG's owned games and the Steam helper's internals. The user-level summary is `docs/VENDORS.md`, a reader doc; keep internals here, not there.

Exo Launcher prefers shelling out to mature open tools over re-implementing store protocols.

## Strategy

| Backend | Store | License | Pin approach |
| --- | --- | --- | --- |
| [Legendary](https://github.com/derrod/legendary) | Epic | GPL-3.0 | Exo downloads a fixed release from GitHub and checks its size and SHA-256 |
| [heroic-gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GOG | GPL-3.0 | Same |
| [Nile](https://github.com/imLinguin/nile) | Amazon Games | GPL-3.0 | Same |
| Official clients | Steam, Epic Games, GOG Galaxy, Riot, Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games, Rockstar Games Launcher, itch, Minecraft, Roblox, Paradox, Wargaming Game Center | Proprietary | Detected from exact executables and registration; never redistributed by Exo |

Exo does not bundle these GPL tools into its installer: it downloads them into `%LocalAppData%\ExoLauncher\tools\` when a feature first needs them (`VerifiedGitHubReleaseDownloader`; there only the pinned file is trusted, `PinnedToolCache`) and runs each as its own program. A copy on PATH, or Heroic's own, is used instead. Adapters say so plainly when a backend is missing.

Steam (`ExoLauncher.SteamIpc`), Epic (Legendary), GOG (gogdl), Riot and Amazon Games (Nile, when present) have wired game-library actions. The other clients list only proven on-disk installs and launch those titles (`Adapters/OfficialInstalledLibraries*.cs`, `AgentPresentAdapters.cs`): Update opens the store's own app through its protocol where it has one (`uplay://install/`, `battlenet://`, `ms-windows-store://pdp/`, `wgc://`), and Remove runs the game's own Windows uninstaller, or opens the store's app when Windows has none (every Xbox game). Exo does not invent account state or ownership.

## Legendary (Epic)

```text
legendary auth --code <code> | --import | --delete
legendary status --json
legendary list --json --include-non-installable
legendary list-installed --json
legendary install <AppName> -y --base-path <path> --max-workers <n> --max-shared-memory <MiB> [--preferred-cdn <host>]
legendary install <AppName> -y --update-only --max-workers <n> --max-shared-memory <MiB> [--preferred-cdn <host>]
legendary install <AppName> -y --repair
legendary import <AppName> <path>
legendary egl-sync --one-shot --import-only
legendary launch <AppName> --skip-version-check
legendary uninstall <AppName> -y
```

Install and update are tuned to the PC (`Adapters/Cli/DownloadTuning.cs`): one worker per CPU thread up to 16 and one per 128 MiB free, shared memory an eighth of free memory between 512 MiB and Legendary's 2 GiB default (a title that needs more fails Legendary's analysis before downloading, and Exo reruns it untuned). `--preferred-cdn` is the fastest host Epic lists for the game, timed by `Adapters/Cli/EpicCdn.cs` (a 256 KB ranged GET of the manifest per host, 2 s, kept 6 hours); when nothing is measured Legendary picks as usual. Commands are built in `Adapters/Cli/LegendaryCli.cs`; Legendary keeps its config in Exo's data folder (`LEGENDARY_CONFIG_PATH`, `EpicAccounts`).

Every install, update and verify run of Legendary, gogdl and Nile, with every process it starts, sits in a job object at below-normal CPU priority and low I/O priority, idle and very low while a game or streaming software runs (`Adapters/Cli/CliJobPriority.cs`, `CliRunner.RunAsync(download: true)`); quick library and sign-in calls run as before.

Progress is parsed from DLManager stdout (`Progress: N%`, and the raw speed on the `+ Download` line; the `+ Disk` line is write speed and is ignored). gogdl and Nile print the same lines without the `%`. `Adapters/Cli/CliProgress.cs` reads all three; tool lines never reach the UI, which gets Exo's own status text.

## gogdl (GOG)

```text
gogdl --auth-config-path <file> auth [--code <code>]
gogdl download <id> --platform windows --path <root> --max-workers <n>   # writes <root>\<GameFolder>
gogdl update <id> --platform windows --path <game folder> --max-workers <n>
gogdl repair <id> --platform windows --path <game folder>   # verifies the installed build only
gogdl launch <game folder> <id> --platform windows
```

`download` appends the game's own folder name to `--path`; `update`, `repair` and `launch` take the folder that holds `goggame-<id>.info`. Exo downloads into a staging folder and promotes that game folder to `GOG\<id>`. `repair` never moves to a newer build, so Update runs `update`.

Galaxy is optional for the happy path; without gogdl, GOG's registry titles launch as local executables.

gogdl has **no stable “list owned” CLI**. Owned-but-not-installed titles come from GOG's own Galaxy library service (`galaxy-library.gog.com`, signed in with gogdl's credentials; names and types from `gamesdb.gog.com` and `api.gog.com`), synchronized in the background into `%LocalAppData%\ExoLauncher\gog-owned.json` (`Services/GogOwnedLibraryService.cs`). Library scans read only that cache, for the signed-in account; Heroic's and Galaxy's own library caches are never shown, since they may be another account's.

## Nile (Amazon Games)

```text
nile auth --login | --status | --logout
nile library sync
nile install <id> [--base-path <path>] --max-workers <n>
nile update <id> --max-workers <n>
nile verify <id>
nile launch <id> [-- <args>]
nile uninstall <id>
```

Without Nile, Amazon Games titles come from the app's proven installs (`fuel.json`).

## Riot

Official flags only (no CDN scrapers):

```text
RiotClientServices.exe --launch-product=<id> --launch-patchline=live
RiotClientServices.exe --uninstall-product=<id> --uninstall-patchline=live
RiotClientInstall.exe --skip-to-install
```

Vanguard (`vgk` / `vgc`) is never force-closed.

## Steam

Exo commands the already-running official Steam client through a small helper
(`ExoLauncher.SteamIpc.exe` → `steamclient64.dll` IClientAppManager). Steam
stays on disk where Valve installed it. Exo does not copy the Steam tree, does
not click Steam chrome, and does not use DepotDownloader.

```text
install / update / uninstall <appid>   via the live Steam client (IPC)
steam.exe -applaunch <appid> [args]    launch
steam://validate/<appid>               repair
steam://uninstall/<appid>              uninstall when the IPC can't (Steam asks to confirm, shown)
```

Progress is Steam’s live download job: `appmanifest_*.acf` when those counters
are moving, otherwise the job totals from `content_log` plus bytes in
`steamapps/downloading/<appid>`. A leftover full ACF or a one-shot
`download 0/total` line is not shown as 0% or 100%. The helper identifies
`IClientAppManager` by RTTI on this Steam build (`GetIClientAppManager` is
engine slot 43; `InstallApp` takes the app id, a folder, a library folder index and a legacy flag; `ExoLauncher.SteamIpc/Program.cs`).

## Other stores

Epic, GOG, Riot and Amazon Games already command agents (Legendary, gogdl,
RiotClientServices, Nile). The rest stay list, launch and hand off to their
own app until an official agent exists.
