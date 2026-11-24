# host-v1 Record & Replay RPC Parity Report

Generated: 2026-11-24T00:55:06.763Z
Capture: `tools/rpc-parity/out/2026-11-24T00-50-40`

## Summary

| Metric | Value |
| --- | ---: |
| Actions recorded | 18 |
| Web drivable actions | 15 / 18 |
| Native drivable actions | 17 / 18 |
| Unique RPC methods called by React (web) | 78 |
| Unique RPC methods called by Native (Rune) | 62 |
| Methods React called that Native never called | 33 |
| Methods Native called that React never called | 17 |
| Chatty methods (>2x call ratio) | 7 |
| Observed push events (web / native) | 9 / 6 |
| React-subscribed push events unhandled by Native (static) | 23 |

## Not drivable

| Action | Client | Reason |
| --- | --- | --- |
| `discord-dm` (Open Discord DM conversation) | web | Discord not signed in in disposable test instance |
| `discord-dm` (Open Discord DM conversation) | native | Discord not signed in in disposable test instance |
| `game-page` (Open a game page from Library) | web | no game tiles in Library |
| `downloads` (Open Downloads sheet) | web | Downloads menu item not found |

## Top Missing Methods (React called, Native never called)

| Method | Web calls | Actions | React caller(s) |
| --- | ---: | --- | --- |
| `discord.signIn` | 4 | `startup`, `room-discord`, `room-music` | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `gifts.list` | 4 | `startup`, `room-library`, `settings-general` | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `shaders.call` | 3 | `startup`, `room-pc-apps`, `settings-general` | `ui/src/components/shaders/shaderCall.ts:57` |
| `system.tune` | 3 | `room-pc`, `room-pc-apps` | `ui/src/components/gpu/AutoTune.tsx:44`, `ui/src/components/gpu/AutoTune.tsx:60`, `ui/src/components/gpu/usePcState.ts:75` |
| `accounts.list` | 2 | `startup`, `settings-general` | `ui/src/components/settings/StoreAccounts.tsx:27`, `ui/src/components/shell/GameMenu.tsx:53` |
| `steam.account` | 2 | `startup`, `settings-general` | `ui/src/components/settings/SteamSignInRow.tsx:31` |
| `store.game` | 10 | `room-store` | `ui/src/components/store/storeData.ts:150` |
| `web.preferences.get` | 2 | `startup` | `ui/src/components/LauncherApp.tsx:184`, `ui/src/components/onboarding/AppsStep.tsx:22`, `ui/src/components/settings/AppsSection.tsx:24` |
| `apps.installerCache` | 1 | `room-library` | `ui/src/components/apps/AppsPanel.tsx:821` |
| `apps.packages` | 1 | `room-pc` | `ui/src/components/gpu/ExoAiRoom.tsx:170` |
| `apps.tidy` | 1 | `room-pc-apps` | `ui/src/components/apps/AppsPanel.tsx:145` |
| `backup.status` | 1 | `startup` | `ui/src/components/OnboardingPanel.tsx:95`, `ui/src/components/settings/BackupSection.tsx:30`, `ui/src/components/settings/BackupSection.tsx:67` |
| `clients.status` | 1 | `startup` | `ui/src/components/clients/ClientsCard.tsx:55`, `ui/src/components/clients/ClientsCard.tsx:112`, `ui/src/components/clients/ClientsCard.tsx:131` |
| `controller.read` | 1 | `room-pc` | `ui/src/components/devices/ControllerPage.tsx:32`, `ui/src/components/gpu/DriversSection.tsx:298` |
| `discord.clientSettings` | 1 | `startup` | `ui/src/components/discord/GoLivePicker.tsx:320`, `ui/src/components/discord/MeMenus.tsx:126`, `ui/src/components/discord/MeMenus.tsx:132` |
| `dlss.newer` | 1 | `room-discord` | `ui/src/components/LauncherApp.tsx:111` |
| `exoAccount.overview` | 1 | `startup` | `ui/src/components/account/useAccount.ts:6`, `ui/src/components/account/useAccount.ts:10` |
| `gpu.display` | 1 | `room-pc` | `ui/src/components/gpu/DisplayTab.tsx:41`, `ui/src/components/gpu/DisplayTab.tsx:66`, `ui/src/components/gpu/autoTuneStages.ts:136` |
| `gpu.driver` | 1 | `room-pc` | `ui/src/components/gpu/DriverRow.tsx:129`, `ui/src/components/gpu/DriverRow.tsx:135`, `ui/src/components/gpu/DriverRow.tsx:148` |
| `hid.close` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:11` |
| `hid.list` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:9` |
| `hid.open` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:10` |
| `hid.sendReport` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:12` |
| `junk.scan` | 1 | `room-library` | `ui/src/components/OnboardingPanel.tsx:153`, `ui/src/components/OnboardingPanel.tsx:375`, `ui/src/components/apps/AppsPanel.tsx:776` |
| `library.get` | 1 | `startup` | `ui/src/components/LauncherApp.tsx:279`, `ui/src/components/devices/GameProfiles.tsx:32`, `ui/src/lib/bridgeCancellation.ts:2` |
| `mouse.presets.get` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:61` |
| `music.warm` | 1 | `room-music` | `ui/src/lib/music.ts:126` |
| `runtimes.list` | 1 | `room-pc` | `ui/src/components/gpu/PcApps.tsx:16` |
| `stores.matrix` | 1 | `startup` | `ui/src/components/OnboardingPanel.tsx:47`, `ui/src/components/OnboardingPanel.tsx:250`, `ui/src/components/SettingsPanel.tsx:160` |
| `system.drivers` | 1 | `room-pc` | `ui/src/components/gpu/DriversSection.tsx:139`, `ui/src/components/gpu/DriversSection.tsx:190`, `ui/src/components/gpu/DriversSection.tsx:207` |
| `system.health` | 1 | `room-pc` | `ui/src/components/OnboardingPanel.tsx:161`, `ui/src/components/gpu/BestSetup.tsx:34`, `ui/src/components/gpu/BestSetup.tsx:49` |
| `system.interrupts` | 1 | `room-pc` | `ui/src/components/gpu/InterruptRows.tsx:27`, `ui/src/components/gpu/autoTuneStages.ts:191`, `ui/src/components/gpu/usePcState.ts:72` |
| `web.preferences.set` | 1 | `startup` | `ui/src/components/LauncherApp.tsx:189`, `ui/src/components/onboarding/AppsStep.tsx:35`, `ui/src/components/settings/AppsSection.tsx:57` |

## Native-only Methods (Native called, React did not)

| Method | Native calls | Actions | React caller(s) |
| --- | ---: | --- | --- |
| `discord.accounts` | 3 | `startup`, `room-discord`, `discord-dm` | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `game.progress` | 2 | `game-page`, `downloads` | `ui/src/components/DownloadsSheet.tsx:39`, `ui/src/lib/hostContract.ts:23` |
| `audio.read` | 1 | `room-devices` | `ui/src/components/devices/AudioPage.tsx:88` |
| `compress.state` | 1 | `game-page` | `ui/src/components/game/CompressRow.tsx:36`, `ui/src/components/game/CompressRow.tsx:41` |
| `dlss.frameGen` | 1 | `game-page` | `ui/src/components/game/FrameGenRow.tsx:84` |
| `downloads.list` | 1 | `downloads` | `ui/src/components/DownloadsSheet.tsx:30` |
| `eq` | 1 | `room-devices` | — |
| `exoAccount.state` | 1 | `settings-general` | `ui/src/lib/host.ts:2398` |
| `game.get` | 1 | `game-page` | `ui/src/components/shell/useGameActions.ts:261`, `ui/src/components/shell/useRunStatePoll.ts:56`, `ui/src/components/shell/useShellEvents.ts:179` |
| `game.properties` | 1 | `game-page` | `ui/src/components/game/GameProperties.tsx:205` |
| `game.settings` | 1 | `game-page` | `ui/src/components/game/OptimizeLine.tsx:38`, `ui/src/components/game/OptimizeLine.tsx:67` |
| `gpu.game` | 1 | `game-page` | `ui/src/components/game/FrameGenRow.tsx:36`, `ui/src/components/game/FrameGenRow.tsx:52` |
| `hdr.game` | 1 | `game-page` | `ui/src/components/game/HdrRow.tsx:39`, `ui/src/components/game/HdrRow.tsx:54` |
| `keyboard.backups` | 1 | `room-devices` | `ui/src/components/devices/KeyboardPage.tsx:50`, `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |
| `keyboard.read` | 1 | `room-devices` | `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |
| `shaders.status` | 1 | `game-page` | `ui/src/components/game/ShaderPrecompile.tsx:65` |
| `shell.bootstrap` | 1 | `startup` | `ui/src/lib/host.ts:2323` |

## Chattiness (>2x call count difference across session)

| Method | Web calls | Native calls | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `music.volume` | 18 | 2 | 9x | web | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `shell.setChromeLayout` | 12 | 2 | 6x | web | `ui/src/components/shell/useShellLayout.ts:106` |
| `dlss.status` | 6 | 1 | 6x | web | `ui/src/lib/bridgeCancellation.ts:3`, `ui/src/lib/upscalerCache.ts:34` |
| `shaders.addon` | 5 | 1 | 5x | web | `ui/src/components/shaders/ShaderTools.tsx:15`, `ui/src/components/shaders/ShadersBoard.tsx:30`, `ui/src/components/shaders/ShadersBoard.tsx:41` |
| `discord.voice` | 1 | 3 | 3x | native | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |
| `game.extras` | 3 | 1 | 3x | web | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `store.home` | 3 | 1 | 3x | web | `ui/src/components/store/StoreRoom.tsx:451` |

## Push Event Gaps

### Observed during recorded actions

| Event | Web count | Native count | Native handles? | Actions | React subscriber(s) |
| --- | ---: | ---: | --- | --- | --- |
| `discord.signin` | 2 | 0 | yes | `startup`, `room-discord` | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |
| `music.status` | 2 | 0 | no | `startup`, `game-page` | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `game.achievements` | 1 | 0 | yes | `startup` | `ui/src/components/game/useGameDetails.ts:68` |
| `gifts.changed` | 1 | 0 | no | `startup` | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `music.playback` | 1 | 0 | yes | `startup` | `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:111`, `ui/src/components/shell/MusicMiniPlayer.tsx:79` |
| `shell.window` | 1 | 0 | yes | `startup` | `ui/src/components/WindowChrome.tsx:16` |
| `store.changed` | 1 | 0 | yes | `startup` | `ui/src/components/store/StoreRoom.tsx:467`, `ui/src/components/store/storeData.ts:274` |

### Static React subscribers missing from native `exo-rpc`

| Event | Observed web | Observed native | React subscriber(s) |
| --- | ---: | ---: | --- |
| `music.status` | 2 | 2 | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `gifts.changed` | 1 | 0 | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `discord.colors` | 0 | 0 | `ui/src/components/discord/Chat.tsx:899`, `ui/src/components/discord/Composer.tsx:103`, `ui/src/components/discord/UserMenu.tsx:119` |
| `discord.interaction` | 0 | 0 | `ui/src/components/discord/AppCommands.tsx:255`, `ui/src/components/discord/AppModal.tsx:19`, `ui/src/lib/discordApps.ts:128` |
| `accounts.progress` | 0 | 0 | `ui/src/components/settings/StoreAccounts.tsx:141`, `ui/src/components/settings/StoresSection.tsx:189` |
| `discord.callSong` | 0 | 0 | `ui/src/lib/listenAlong.ts:59`, `ui/src/lib/listenAlong.ts:144` |
| `discord.camera` | 0 | 0 | `ui/src/lib/discordPrefs.ts:396`, `ui/src/lib/goLive.ts:216` |
| `discord.pollVote` | 0 | 0 | `ui/src/components/discord/Chat.tsx:702`, `ui/src/components/discord/messageCache.ts:132` |
| `shaders.event` | 0 | 0 | `ui/src/components/shaders/ShaderTools.tsx:16`, `ui/src/components/shaders/ShadersBoard.tsx:47` |
| `bios.progress` | 0 | 0 | `ui/src/components/gpu/DriversSection.tsx:349` |
| `capture.shrunk` | 0 | 0 | `ui/src/components/captures/CapturesSheet.tsx:298` |
| `controller.state` | 0 | 0 | `ui/src/components/devices/ControllerPage.tsx:121` |
| `discord.keybind` | 0 | 0 | `ui/src/lib/discordPrefs.ts:399` |
| `discord.micLevel` | 0 | 0 | `ui/src/components/discord/settings/VoicePage.tsx:258` |
| `discord.tts` | 0 | 0 | `ui/src/lib/discordPrefs.ts:383` |
| `discord.userSettings` | 0 | 0 | `ui/src/lib/discordPrefs.ts:203` |
| `discord.video.frame` | 0 | 0 | `ui/src/lib/discordVideo.ts:260` |
| `discord.video.keyframe` | 0 | 0 | `ui/src/lib/goLive.ts:217` |
| `drivers.step` | 0 | 0 | `ui/src/components/gpu/DriversSection.tsx:170` |
| `hid.changed` | 0 | 0 | `ui/src/lib/mouseRelay.ts:213` |
| `hid.input` | 0 | 0 | `ui/src/lib/mouseRelay.ts:212` |
| `runtimes.progress` | 0 | 0 | `ui/src/components/runtimes/RuntimesPanel.tsx:92` |
| `shaders.rebuild` | 0 | 0 | `ui/src/components/shaders/ShadersBoard.tsx:351` |

## Per-Action Breakdown (sorted by biggest gaps first)

### `startup` — App startup (Library initial load)

- **Gap score:** 320 | **Web:** ok (75 req, 10 ev) | **Native:** ok (28 req, 3 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `dlss.status` | 6 | 1460.04 | `ui/src/lib/bridgeCancellation.ts:3`, `ui/src/lib/upscalerCache.ts:34` |
| `discord.signIn` | 2 | 0.9 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `game.extras` | 2 | 8.44 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `gifts.list` | 2 | 3.88 | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `shaders.addon` | 2 | 65 | `ui/src/components/shaders/ShaderTools.tsx:15`, `ui/src/components/shaders/ShadersBoard.tsx:30`, `ui/src/components/shaders/ShadersBoard.tsx:41` |
| `store.home` | 2 | 2157.84 | `ui/src/components/store/StoreRoom.tsx:451` |
| `web.preferences.get` | 2 | 1.72 | `ui/src/components/LauncherApp.tsx:184`, `ui/src/components/onboarding/AppsStep.tsx:22`, `ui/src/components/settings/AppsSection.tsx:24` |
| `accounts.list` | 1 | 174.53 | `ui/src/components/settings/StoreAccounts.tsx:27`, `ui/src/components/shell/GameMenu.tsx:53` |
| `backup.status` | 1 | 81.83 | `ui/src/components/OnboardingPanel.tsx:95`, `ui/src/components/settings/BackupSection.tsx:30`, `ui/src/components/settings/BackupSection.tsx:67` |
| `clients.status` | 1 | 24.69 | `ui/src/components/clients/ClientsCard.tsx:55`, `ui/src/components/clients/ClientsCard.tsx:112`, `ui/src/components/clients/ClientsCard.tsx:131` |
| `discord.clientSettings` | 1 | 10.38 | `ui/src/components/discord/GoLivePicker.tsx:320`, `ui/src/components/discord/MeMenus.tsx:126`, `ui/src/components/discord/MeMenus.tsx:132` |
| `discord.stream` | 1 | 2.01 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `exoAccount.overview` | 1 | 7.47 | `ui/src/components/account/useAccount.ts:6`, `ui/src/components/account/useAccount.ts:10` |
| `hdr.state` | 1 | 3.07 | `ui/src/lib/hdr.ts:10` |
| `hid.close` | 1 | 6.16 | `ui/src/lib/mouseRelay.ts:11` |
| `hid.list` | 1 | 269.26 | `ui/src/lib/mouseRelay.ts:9` |
| `hid.open` | 1 | 4.83 | `ui/src/lib/mouseRelay.ts:10` |
| `hid.sendReport` | 1 | 12.78 | `ui/src/lib/mouseRelay.ts:12` |
| `keep.status` | 1 | 13.37 | `ui/src/components/game/OptimizeLine.tsx:46`, `ui/src/components/gpu/PcOverview.tsx:74` |
| `library.get` | 1 | 1620.65 | `ui/src/components/LauncherApp.tsx:279`, `ui/src/components/devices/GameProfiles.tsx:32`, `ui/src/lib/bridgeCancellation.ts:2` |
| `mouse.presets.get` | 1 | 11.61 | `ui/src/lib/mouseRelay.ts:61` |
| `music.playback` | 1 | 1.75 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |
| `shaders.call` | 1 | 6.49 | `ui/src/components/shaders/shaderCall.ts:57` |
| `steam.account` | 1 | 1.14 | `ui/src/components/settings/SteamSignInRow.tsx:31` |
| `store.wishlist` | 1 | 86.09 | `ui/src/components/store/storeData.ts:266`, `ui/src/components/store/storeData.ts:309`, `ui/src/components/store/storeData.ts:320` |
| `stores.matrix` | 1 | 1169.07 | `ui/src/components/OnboardingPanel.tsx:47`, `ui/src/components/OnboardingPanel.tsx:250`, `ui/src/components/SettingsPanel.tsx:160` |
| `web.preferences.set` | 1 | 38.41 | `ui/src/components/LauncherApp.tsx:189`, `ui/src/components/onboarding/AppsStep.tsx:35`, `ui/src/components/settings/AppsSection.tsx:57` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.accounts` | 1 | 6.7 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `shell.bootstrap` | 1 | 870.1 | `ui/src/lib/host.ts:2323` |

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `music.volume` | 3 | 1 | 3x | web | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `discord.signin` | 1 | 0 | not emitted on native | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |
| `game.achievements` | 1 | 0 | not emitted on native | `ui/src/components/game/useGameDetails.ts:68` |
| `gifts.changed` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `music.playback` | 1 | 0 | not emitted on native | `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:111`, `ui/src/components/shell/MusicMiniPlayer.tsx:79` |
| `music.status` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `shell.window` | 1 | 0 | not emitted on native | `ui/src/components/WindowChrome.tsx:16` |
| `store.changed` | 1 | 0 | not emitted on native | `ui/src/components/store/StoreRoom.tsx:467`, `ui/src/components/store/storeData.ts:274` |

### `room-pc` — Open Exo AI / PC room

- **Gap score:** 140 | **Web:** ok (36 req, 0 ev) | **Native:** ok (18 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.setChromeLayout` | 2 | 3.79 | `ui/src/components/shell/useShellLayout.ts:106` |
| `apps.list` | 1 | — | `ui/src/components/apps/AppsPanel.tsx:131`, `ui/src/components/apps/AppsPanel.tsx:204`, `ui/src/components/gpu/PcApps.tsx:16` |
| `apps.packages` | 1 | 1676.05 | `ui/src/components/gpu/ExoAiRoom.tsx:170` |
| `controller.read` | 1 | 499.25 | `ui/src/components/devices/ControllerPage.tsx:32`, `ui/src/components/gpu/DriversSection.tsx:298` |
| `devices.list` | 1 | 320.99 | `ui/src/components/DevicesRoom.tsx:267`, `ui/src/components/gpu/DriversSection.tsx:296` |
| `gpu.display` | 1 | 1035.67 | `ui/src/components/gpu/DisplayTab.tsx:41`, `ui/src/components/gpu/DisplayTab.tsx:66`, `ui/src/components/gpu/autoTuneStages.ts:136` |
| `gpu.driver` | 1 | 715.05 | `ui/src/components/gpu/DriverRow.tsx:129`, `ui/src/components/gpu/DriverRow.tsx:135`, `ui/src/components/gpu/DriverRow.tsx:148` |
| `keep.status` | 1 | 0.93 | `ui/src/components/game/OptimizeLine.tsx:46`, `ui/src/components/gpu/PcOverview.tsx:74` |
| `music.volume` | 1 | 0.03 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `runtimes.list` | 1 | — | `ui/src/components/gpu/PcApps.tsx:16` |
| `system.drivers` | 1 | — | `ui/src/components/gpu/DriversSection.tsx:139`, `ui/src/components/gpu/DriversSection.tsx:190`, `ui/src/components/gpu/DriversSection.tsx:207` |
| `system.health` | 1 | 396.75 | `ui/src/components/OnboardingPanel.tsx:161`, `ui/src/components/gpu/BestSetup.tsx:34`, `ui/src/components/gpu/BestSetup.tsx:49` |
| `system.interrupts` | 1 | 100.79 | `ui/src/components/gpu/InterruptRows.tsx:27`, `ui/src/components/gpu/autoTuneStages.ts:191`, `ui/src/components/gpu/usePcState.ts:72` |
| `system.tune` | 1 | 193.69 | `ui/src/components/gpu/AutoTune.tsx:44`, `ui/src/components/gpu/AutoTune.tsx:60`, `ui/src/components/gpu/usePcState.ts:75` |

### `settings-general` — Open Settings > General

- **Gap score:** 72 | **Web:** ok (13 req, 0 ev) | **Native:** ok (15 req, 2 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `accounts.list` | 1 | 27.42 | `ui/src/components/settings/StoreAccounts.tsx:27`, `ui/src/components/shell/GameMenu.tsx:53` |
| `gifts.list` | 1 | 0.06 | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `music.volume` | 1 | 0.05 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `shaders.addon` | 1 | 0.26 | `ui/src/components/shaders/ShaderTools.tsx:15`, `ui/src/components/shaders/ShadersBoard.tsx:30`, `ui/src/components/shaders/ShadersBoard.tsx:41` |
| `shaders.call` | 1 | 0.2 | `ui/src/components/shaders/shaderCall.ts:57` |
| `steam.account` | 1 | 0.16 | `ui/src/components/settings/SteamSignInRow.tsx:31` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.checkUpdate` | 1 | 232.2 | `ui/src/components/shell/useAppUpdate.ts:99`, `ui/src/components/shell/useAppUpdate.ts:171`, `ui/src/lib/bridgeCancellation.ts:4` |
| `exoAccount.state` | 1 | 0.86 | `ui/src/lib/host.ts:2398` |
| `hdr.state` | 1 | 0.33 | `ui/src/lib/hdr.ts:10` |
| `system.about` | 1 | 8.35 | `ui/src/components/gpu/usePcState.ts:68` |

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `shell.heartbeat` | 3 | 8 | 2.67x | native | `ui/src/lib/heartbeat.ts:15` |

### `room-library` — Return to Library room

- **Gap score:** 60 | **Web:** ok (15 req, 0 ev) | **Native:** ok (8 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.setChromeLayout` | 2 | 2.42 | `ui/src/components/shell/useShellLayout.ts:106` |
| `apps.installerCache` | 1 | 181.55 | `ui/src/components/apps/AppsPanel.tsx:821` |
| `game.extras` | 1 | 15.52 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `gifts.list` | 1 | 0.06 | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `junk.scan` | 1 | 343.33 | `ui/src/components/OnboardingPanel.tsx:153`, `ui/src/components/OnboardingPanel.tsx:375`, `ui/src/components/apps/AppsPanel.tsx:776` |
| `music.volume` | 1 | 0.06 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

### `room-pc-apps` — Open Apps room

- **Gap score:** 60 | **Web:** ok (18 req, 0 ev) | **Native:** ok (12 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.setChromeLayout` | 2 | 2.86 | `ui/src/components/shell/useShellLayout.ts:106` |
| `system.tune` | 2 | 23.49 | `ui/src/components/gpu/AutoTune.tsx:44`, `ui/src/components/gpu/AutoTune.tsx:60`, `ui/src/components/gpu/usePcState.ts:75` |
| `apps.tidy` | 1 | — | `ui/src/components/apps/AppsPanel.tsx:145` |
| `exoAi.state` | 1 | 12.48 | `ui/src/components/gpu/ExoAiPanel.tsx:10` |
| `music.volume` | 1 | 0.06 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `shaders.call` | 1 | 0.36 | `ui/src/components/shaders/shaderCall.ts:57` |

### `game-page` — Open a game page from Library

- **Gap score:** 42 | **Web:** not drivable (no game tiles in Library) (12 req, 1 ev) | **Native:** ok (24 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 2 | 0.07 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.pendingOpen` | 1 | 0.56 | `ui/src/components/shell/useShellEvents.ts:279` |
| `compress.state` | 1 | 172.76 | `ui/src/components/game/CompressRow.tsx:36`, `ui/src/components/game/CompressRow.tsx:41` |
| `dlss.frameGen` | 1 | 1484.36 | `ui/src/components/game/FrameGenRow.tsx:84` |
| `dlss.status` | 1 | 1432.98 | `ui/src/lib/bridgeCancellation.ts:3`, `ui/src/lib/upscalerCache.ts:34` |
| `game.extras` | 1 | 6.87 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `game.get` | 1 | 117.33 | `ui/src/components/shell/useGameActions.ts:261`, `ui/src/components/shell/useRunStatePoll.ts:56`, `ui/src/components/shell/useShellEvents.ts:179` |
| `game.progress` | 1 | 2.43 | `ui/src/components/DownloadsSheet.tsx:39`, `ui/src/lib/hostContract.ts:23` |
| `game.properties` | 1 | 462.82 | `ui/src/components/game/GameProperties.tsx:205` |
| `game.settings` | 1 | 622.43 | `ui/src/components/game/OptimizeLine.tsx:38`, `ui/src/components/game/OptimizeLine.tsx:67` |
| `gpu.game` | 1 | 25.98 | `ui/src/components/game/FrameGenRow.tsx:36`, `ui/src/components/game/FrameGenRow.tsx:52` |
| `hdr.game` | 1 | 113.15 | `ui/src/components/game/HdrRow.tsx:39`, `ui/src/components/game/HdrRow.tsx:54` |
| `keep.status` | 1 | 18.83 | `ui/src/components/game/OptimizeLine.tsx:46`, `ui/src/components/gpu/PcOverview.tsx:74` |
| `shaders.status` | 1 | 8.7 | `ui/src/components/game/ShaderPrecompile.tsx:65` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `music.status` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/music/NativeMusicRoom.tsx:132` |

### `room-discord` — Open Discord room

- **Gap score:** 42 | **Web:** ok (10 req, 1 ev) | **Native:** ok (12 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.signIn` | 1 | 0.08 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `dlss.newer` | 1 | 1.12 | `ui/src/components/LauncherApp.tsx:111` |
| `music.volume` | 1 | 0.06 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.accounts` | 1 | 0.16 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `discord.stream` | 1 | 1.89 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `discord.voice` | 1 | 0.11 | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `discord.signin` | 1 | 0 | not emitted on native | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |

### `room-devices` — Open Devices room

- **Gap score:** 38 | **Web:** ok (21 req, 0 ev) | **Native:** ok (22 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 2 | 0.23 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `shell.setChromeLayout` | 2 | 2.65 | `ui/src/components/shell/useShellLayout.ts:106` |
| `music.playback` | 1 | 0.06 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `audio.read` | 1 | 26.69 | `ui/src/components/devices/AudioPage.tsx:88` |
| `eq` | 1 | 263.91 | — |
| `keyboard.backups` | 1 | 8.79 | `ui/src/components/devices/KeyboardPage.tsx:50`, `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |
| `keyboard.read` | 1 | 224.55 | `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |

### `discord-dm` — Open Discord DM conversation

- **Gap score:** 35 | **Web:** not drivable (Discord not signed in in disposable test instance) (9 req, 0 ev) | **Native:** not drivable (Discord not signed in in disposable test instance) (11 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 1 | 0.06 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.pendingOpen` | 1 | 0.03 | `ui/src/components/shell/useShellEvents.ts:279` |
| `discord.accounts` | 1 | 0.13 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `discord.status` | 1 | 0.21 | `ui/src/components/discord/NativeDiscordRoom.tsx:29`, `ui/src/components/discord/NativeDiscordRoom.tsx:42`, `ui/src/components/onboarding/DiscordStep.tsx:15` |
| `discord.stream` | 1 | 0.09 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `discord.voice` | 1 | 0.04 | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |

### `room-music` — Open Music room

- **Gap score:** 22 | **Web:** ok (13 req, 0 ev) | **Native:** ok (12 req, 2 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.signIn` | 1 | 0.31 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `music.warm` | 1 | 0.66 | `ui/src/lib/music.ts:126` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.playback` | 1 | 0.18 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |

### `palette` — Open Command Palette (Ctrl+K)

- **Gap score:** 20 | **Web:** ok (7 req, 0 ev) | **Native:** ok (6 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 1 | 0.06 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `shell.setChromeLayout` | 1 | 4.92 | `ui/src/components/shell/useShellLayout.ts:106` |

### `room-store` — Open Store room

- **Gap score:** 20 | **Web:** ok (19 req, 0 ev) | **Native:** ok (9 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `store.game` | 10 | 136.69 | `ui/src/components/store/storeData.ts:150` |
| `music.volume` | 1 | 0.05 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

### `downloads` — Open Downloads sheet

- **Gap score:** 16 | **Web:** not drivable (Downloads menu item not found) (10 req, 0 ev) | **Native:** ok (16 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 1 | 0.06 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.pendingOpen` | 1 | 0.05 | `ui/src/components/shell/useShellEvents.ts:279` |
| `downloads.list` | 1 | 3.21 | `ui/src/components/DownloadsSheet.tsx:30` |
| `game.progress` | 1 | 0.05 | `ui/src/components/DownloadsSheet.tsx:39`, `ui/src/lib/hostContract.ts:23` |

### `settings-ingame` — Open Settings > In game

- **Gap score:** 14 | **Web:** ok (3 req, 0 ev) | **Native:** ok (6 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 1 | 0.05 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `shell.heartbeat` | 2 | 6 | 3x | native | `ui/src/lib/heartbeat.ts:15` |

### `settings-about` — Open Settings > About

- **Gap score:** 4 | **Web:** ok (1 req, 0 ev) | **Native:** ok (4 req, 0 ev)

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `shell.heartbeat` | 1 | 4 | 4x | native | `ui/src/lib/heartbeat.ts:15` |

### `settings-downloads` — Open Settings > Downloads

- **Gap score:** 4 | **Web:** ok (1 req, 0 ev) | **Native:** ok (6 req, 0 ev)

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `shell.heartbeat` | 1 | 6 | 6x | native | `ui/src/lib/heartbeat.ts:15` |

### `settings-library` — Open Settings > Library

- **Gap score:** 4 | **Web:** ok (2 req, 0 ev) | **Native:** ok (6 req, 0 ev)

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `shell.heartbeat` | 2 | 6 | 3x | native | `ui/src/lib/heartbeat.ts:15` |

### `settings-music` — Open Settings > Music

- **Gap score:** 4 | **Web:** ok (1 req, 0 ev) | **Native:** ok (7 req, 0 ev)

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `shell.heartbeat` | 1 | 7 | 7x | native | `ui/src/lib/heartbeat.ts:15` |
