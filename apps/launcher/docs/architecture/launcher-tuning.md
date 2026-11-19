# Launcher tuning

What can make the store clients Exo hosts use less while hidden and download faster, what Exo does about it, and what it
refuses and why. Exo already hosts and hides the clients (`HiddenStoreRuntime`, `StoreClientCleanup`, `StoreTrayIcons`,
`StartupApps`, Quiet Game Mode); this page is about their own settings, flags and files.

Read-only research, 2026-11-03. Nothing here was written to a real store config; the tests use fakes.

## How to read the grades

| Grade | Meaning |
| --- | --- |
| A | Read in the shipped client on this PC (its binary strings, its UI bundle, its own config or log) or documented by the vendor |
| B | A reproducible third-party recipe with exact keys (a Lutris installer, a maintained tool), or a vendor staff answer |
| C | Forum or guide claims with no exact keys, no measurement, or contradicted elsewhere |
| D | Folklore: no source we can open, or the source disagrees with the shipped client |

"Effect" is graded separately from "exists": a key can be A for existing and C for lowering usage. Only A or B for both, with
no cost the user cannot see, is implemented. A Valve developer wiki page and PCGamingWiki answer 403 to automated fetches,
so nothing here leans on them.

## What this PC could measure

Only Steam, the new Epic launcher and Riot are installed here; nothing was started (no store client, no game).

- Steam, running hidden the way Exo starts it (`-silent -nofriendsui -nochatui`), idle: `steam.exe` plus six `steamwebhelper`
  processes (browser, GPU, network, storage, two renderers). Working set 162 MB, private bytes 820 MB (Windows trims a hidden
  window's working set; private bytes is what it holds). `steamservice.exe` is a Windows service and stays.
- Steam's own record of its last download here: `RecentDownloadRate` 81,180,592 (config.vdf), about 650 Mbit/s, and
  `AllowDownloadsDuringGameplay` is already on. Nothing caps downloads on this PC.
- Epic (0.108.0), from its last log, an 84 s session: four WebView2 renderers (shell, social panel, store, Fab), the store page
  loaded in 4.0 s and Fab in 1.3 s, and the log was written at `verbose` level with the preset `default`.

There is no before/after number for the Epic change, because starting Epic was out of bounds. The owner's check is below.

## Steam

Steam is the client with the most evidence, because its UI bundle (`steamui\*.js`, `steamui\localization`) and `steam.exe` /
`steamclient64.dll` ship with the install and can be read.

### Command line (Exo starts Steam hidden with `-silent -nofriendsui -nochatui`)

| Flag | Grade | Verdict |
| --- | --- | --- |
| `-silent` | A (strings in steam.exe; Exo uses it) | Already used. |
| `-nofriendsui` | A (steamclient64) | Already used. Drops the friends window's web view: this PC's idle tree has two renderers, not three or four. |
| `-nochatui` | D | The string is in neither steam.exe, steamclient64.dll nor friendsui.dll of the installed build, so it does nothing. Harmless; left because it is Exo's tested argument list. |
| `-cef-disable-gpu`, `-cef-disable-gpu-compositing` | A exists, C effect | Not added. They move web-view painting to the CPU. The window is hidden, so there is little to paint, and when the user opens Steam the library scrolls worse. This PC already has the equivalent client setting off (`GPUAccelWebViewsV3` = 0) and still shows a GPU process, so the saving is not demonstrated. |
| `-cef-disable-site-isolation` | A exists | Rejected. Fewer renderers by turning off Chromium's site isolation: a security cost on a surface that renders store and community pages. |
| `-cef-disable-logging`, `-cef-disable-js-logging` | A exists | Not added. Steam's whole `logs` folder is 5.7 MB here; the writes are not a load. |
| `-no-browser`, `-cef-single-process`, `-cef-in-process-gpu` | D | Not in the installed build's strings at all (zero hits in steam.exe, steamclient64.dll and steamwebhelper.exe). Folklore from older or Linux builds. Without the browser Steam cannot sign in. |
| `-nojoy` | A exists | Rejected: turns off controller support, which Exo configures per game through Steam Input. |
| `-noipv6` | A exists | Not added: a network behaviour change for a problem the user has not reported. |
| `-tcp`, `-forcesteamupdate`, `-clearbeta`, `-noverifyfiles`, `-inhibitbootstrap`, `-skipinitialbootstrap` | A exists | Not added. Update and repair controls, not usage controls; skipping verification or updates trades correctness for seconds at start. |

### `steam.cfg` (`BootStrapperInhibitAll`, `BootStrapperInhibitClientChecksum`, `BootStrapperInhibitBootstrapperChecksum`, `BootStrapperInhibitUpdateOnLaunch`, `BootStrapperForceSelfUpdate`)

Grade: the names are A (strings in steam.exe); the claim that it lowers usage is C, from forum posts that call it
unsupported. Rejected. It freezes the client; Exo's Steam helper (`ExoLauncher.SteamIpc`) is written against the client build in
use, and a frozen old client falls behind the games and features it has to serve. It saves one small download a month.

### Client settings (Settings > Interface, Library, Downloads)

Steam keeps these as named settings; the setting ids below are read from `steamui` and the stored names from this PC's
config files. All of them need Steam closed to be written, and Steam rewrites its files as it exits.

| Setting (stored as) | Grade | Verdict |
| --- | --- | --- |
| Library low performance mode (`localconfig.vdf` › `LibraryLowPerfMode`), low bandwidth mode (`LibraryLowBandwidthMode`), disable community content (`LibraryDisableCommunityContent`) | A exists (all three are in this PC's localconfig.vdf; Steam's own strings: "Reduces bandwidth use by disabling features like auto-load of community content") | Not added. They change what the library draws and loads when a game's page is open. A hidden Steam draws nothing, so idle use does not change, and a user who opens Steam gets a plainer library. |
| GPU accelerated rendering in web views (registry `HKCU\Software\Valve\Steam\GPUAccelWebViewsV3`), smooth scrolling (`SmoothScrollWebViews`) | A exists | Not added. Both are 0 on this PC already. Turning GPU off for everyone makes Steam's library slower to open; it is the user's choice, in Steam. |
| Hardware video decoding (`enable_hardware_video_decoding`), DirectWrite (a label in `steamui`, no stored name found) | A exists | Not added: the stored names are not in this PC's registry or config files, and neither claims a lower use. |
| Shader pre-caching (`enable_shader_precache`), background processing of Vulkan shaders (`config.vdf` › `ShaderCacheManager` › `EnableShaderBackgroundProcessing`) | A exists, effect C | Not changed. Turning the background work off moves it into the game's first launch ("processing Vulkan shaders" before Play), so it trades idle CPU for launch time. Steam's own text: pre-caching "may slightly increase disk and bandwidth usage" and makes Vulkan and OpenGL games load faster. |
| Throttle downloads while streaming, download region (`CellIDServerOverride`), limit download speed, "Allow downloads during gameplay" | A exists | Left as the user and Steam have them. Exo already sets downloads-while-playing per game (`SteamGameProperties.Defaults`); the region is Steam's own pick by IP, and choosing a content server by measurement would be CDN scraping, which Exo refuses. |
| Game file transfer over the local network (`download_peer_content`) | A exists | Not changed: it lowers internet use only when another PC on the LAN has the game. |
| `@nClientDownloadEnableHTTP2PlatformWindows 0` (Steam console variable) | C | Rejected. Reported as a fix for some ISPs (one maintained Linux repo, a Reddit thread) and as unofficial and liable to vanish by the same people; no measurement. A Valve variable that can change under a user is not something to set for everyone. |
| Notifications, sounds, launch dialogs | A | Already done by `StoreQuiet`. |
| Run Steam when my computer starts (`run_at_startup`) | A | Already done by `StartupApps` (and `-silent` is why a Windows start is hidden). |
| Steam Cloud (`cloud_enabled`) | A | Never touched: saves. |

### Web helper count

Valve has no setting for it. The count this PC shows (two renderers) comes from `-nofriendsui`. The only flag that lowers it
further is turning off site isolation, rejected above.

### Verdict

Steam is at the floor that its settings allow without a trade the user cannot see. No change.

## Epic Games Launcher

Two launchers exist. The new one (2026, a Tauri app with its data in `%LOCALAPPDATA%\com.epicgames.eglv2`) is what this PC has;
the old one lives under `Portal\Binaries`.

### The reported flags `limitclientticks -nosplash -useallavaliablecores`

Grade D. Judged:

- The new launcher's executable contains none of them (searched as UTF-8 and UTF-16: zero hits for `limitclientticks`,
  `useallavail`, `nosplash`, `lanplay`). It is not an Unreal Engine program and ignores them.
- They are Unreal Engine game switches. As Unreal's command-line documentation describes them (not opened in this session, so C): `-useallavailablecores` (the
  report's `avaliable` is a typo, so as written it is not even that switch) has the engine run its worker threads on every
  core, which raises CPU use, the opposite of the aim; `-nosplash` hides a splash window and saves no work; no source was
  found for `-limitclientticks` as a launcher option.
- `-lanplay` is a networking option for games that host LAN sessions, not a speed option, and is not used.

Nothing is passed. Exo starts the new launcher with its own `--autostart` and the old one with `-silent` (`EpicAdapter.QuietStartFlag`).

### The new launcher's own settings (`settings\app-settings.json`)

The key names below are read from `EpicGamesLauncher.exe` (its serialised settings struct) and from this PC's file. The preset's
effect is read from the same binary: `performanceProfile.preset` is an enum of `default`, `lowResources` and `custom`; the
web view arguments it feeds are `--disable-gpu`, `--in-process-gpu`, `--enable-low-end-device-mode`,
`--disable-background-networking`, `--disable-component-update`, `--aggressive-cache-discard`, `--max-old-space-size=128`,
`--jitless`, `--disk-cache-size=104857600` and `--media-cache-size=33554432`, and switches that kill the audio service,
translate, media router, optimisation hints and privacy-sandbox features. The log records the choice as
`SessionPerfFlags ... preset=Default`, captured once per session, so a change applies at the next start.

| Setting | Grade | Verdict |
| --- | --- | --- |
| `performanceProfile.preset` = `lowResources` | A exists and what it feeds; effect on the process tree C (not measured) | **Done**, set once when the preset is `default`. Four renderers (shell, social, store, Fab) are what the launcher costs hidden, and this is Epic's own switch for fewer resources. The cost is a slower store page for a user who opens Epic: check below. |
| `verboseLogs` = false | A key and log line (`file and stdout log filter: verbose ... mode=verbose`) | **Done**, set once. A debug-level log of the whole session is written to disk on every start (72 KB in 84 s here, a few MB an hour). Costs Epic support diagnostics only. |
| `enableNotifications`, `showWhatsNewOnStartup` | A | Already done. |
| `startOnBoot` | A | Already handed to Exo by `StartupApps`. |
| `hideSocialPanel` | A exists, effect unknown | Not done. The social panel is one of the four renderers, but nothing shows that hiding it stops it loading, and it also holds the user's presence. |
| `externalSourcesEnabled`, `externalRescanIntervalMinutes` | A | Already off here; a user who turned them on wants them. |
| `automaticDownloadsEnabled`, `maxConcurrentDownloads`, `downloadSpeedLimit`, `downloadSchedule*` | A exists | Not changed. Exo downloads and updates Epic games itself with Legendary (see below), so these only matter for a game the user installs from Epic's own window. Turning off the launcher's automatic downloads also turns off the update path for those games, and that is Settings > Downloads' decision, not a hidden one. |
| `verifyAllFilesOnUpdate`, `verifyFilesOnInstall` | A exists | Rejected: skipping verification is an integrity trade. |
| `cpuRendering`, `idleTimeoutMinutes`, `devBuildPollingTier` | A exists, meaning unknown | Not done: values and effect not readable. |

A value the client rewrites or checks is held the way Exo holds Epic's notifications: written only while Epic is closed (it
keeps settings in memory), with the old value kept in `epic-quiet.json`. The preset and the logs are set once: if the user
changes them back in Epic, Exo does not set them again. Exo's uninstall puts every recorded value back, each only while it is
still as Exo left it (`EpicQuietSettings.PutBack`, run by `ExoUninstall`).

### Downloads

Exo downloads Epic games with Legendary, not the launcher. `DownloadTuning` sizes Legendary's workers and shared memory to the
PC and `EpicCdn` picks the fastest of the hosts Epic lists for the game. The old launcher's `Engine.ini` `[Portal.BuildPatch]
ChunkDownloads` recipe is grade C (guides, no source we can open) and is irrelevant to Legendary.

## GOG Galaxy

Not installed here. Exo downloads with gogdl (`DownloadTuning` sizes its threads); Galaxy itself runs only to hand a game over.

| Item | Grade | Verdict |
| --- | --- | --- |
| `/runWithoutUpdating` | C (a GOG forum FAQ for the 2.0 beta) | Rejected: freezes the client. |
| `/minimized`, `/autostart` | D | Nothing we can open confirms them. |
| Bandwidth limit and scheduler | B (GOG's 2017 changelog) | A download setting for Galaxy's own downloads, which Exo does not use. |
| Start with Windows | A | Already handed to Exo (`galaxyclient.exe`). |

No stored name for any Galaxy usage setting was found. Nothing done.

## EA app

Not installed here. No settings file, flag or key was found in anything we can open. EA's help page says background applications
use memory and power and points at closing them; forum threads describe `EABackgroundService` surviving its own "background
services" toggle (C). Disabling that Windows service is rejected (a service change; and the forum reports the app then fails
to start). The start with Windows is handed to Exo (`EADesktop.exe`, `EABackgroundService.exe`). Quiet Game Mode already closes
the client when unused. Nothing done.

## Ubisoft Connect

Not installed here. The only exact keys are a Lutris installer's `settings.yaml` (`%LOCALAPPDATA%\Ubisoft Game Launcher`): `overlay.enabled`,
`overlay.forceunhookgame`, `overlay.fps_enabled`, `overlay.warning_enabled` and `user.closebehavior: CloseBehavior_Close`
(B). Not done:

- The overlay hooks into running games. Whether it stays off, and what it costs a game that wants it, is the user's choice (a
  new row Exo does not want) and its hook is nearer to game processes than Exo goes.
- The client rewrites that file as it exits, and the file's other keys are unknown, so a held value could not be tested here.
- `closebehavior` only decides tray or exit, and Exo already closes unused clients.
- Search results that name `enablegpu` or `enableCloudSync` in this file found no source: D.

Start with Windows is handed to Exo (`upc.exe`).

## Battle.net

Not installed here. `Battle.net.config` (`%APPDATA%\Battle.net`, JSON): `Client.HardwareAcceleration` ("false" is Blizzard staff's
answer to crashes and glitches, B, not a usage claim), `Client.Sound.Enabled`, `Client.GameSearch.BackgroundSearch`,
`Client.Install.DownloadLimitNextPatchInBps` ("0" is no limit, the default) and `Client.Streaming.StreamingEnabled` (Blizzard's
patch notes say streaming was removed, so it does nothing: D for old guides). All values are strings in that file (Lutris
installer, B). Not done: the hardware-acceleration switch moves work from the GPU to the CPU with no measurement that it lowers
the total; the client rewrites the file and no test client was available; Warden and Agent are never touched. Start with Windows
is handed to Exo.

## Riot

Installed here. Riot's client settings share a file family with sign-in tokens (`RiotGamesPrivateSettings.yaml`) and the client
sits beside Vanguard. Nothing is written. Exo's hard stop applies: only the Vanguard tray icon may leave startup, `vgc` and
`vgk` are never touched, and Riot titles are never modified. The Riot client's start with Windows is already handed to Exo.

## Rockstar Games Launcher, Xbox app and Microsoft Store, Amazon Games, itch and the rest

No documented setting, flag or file key for lowering usage was found for any of them in sources we can open. Their start with
Windows is handed to Exo where it is a per-user entry (`amazon`); Xbox and the Microsoft Store are Windows components whose
services Exo does not change.

## What was done

1. Epic (new launcher): `performanceProfile.preset` = `lowResources` and `verboseLogs` = false, set once while Epic is closed
   and its start is Exo's, recorded in `epic-quiet.json`, put back by Exo's uninstall. App.log line: `Epic: ... set quieter and
   lighter while Exo starts it.` Tests: `EpicQuietTests`.

Nothing else cleared the bar. The rest of the page is the record of why, so the next person does not repeat the search.

## What the owner should check

- Open Epic once from Exo (Settings, Open store) and look at the Store and Library pages: this is the one change whose cost is
  visible. If the store is too slow, set Epic's performance preset back to default in its own settings; Exo will not set it
  again.
- To see the saving, start Epic hidden from Exo twice (once with the preset on default in `app-settings.json`, once with
  Exo's change) and compare the `msedgewebview2.exe` processes under `EpicGamesLauncher.exe` in Task Manager after a few
  minutes. Not measured here.
