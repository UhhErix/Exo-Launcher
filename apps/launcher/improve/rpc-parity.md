# host-v1 Record & Replay RPC Parity Report

Generated: 2026-11-23T21:57:19.803Z
Capture: `tools/rpc-parity/out/2026-11-23T21-52-00`

## Summary

| Metric | Value |
| --- | ---: |
| Actions recorded | 18 |
| Web drivable actions | 15 / 18 |
| Native drivable actions | 16 / 18 |
| Unique RPC methods called by React (web) | 78 |
| Unique RPC methods called by Native (Rune) | 34 |
| Methods React called that Native never called | 53 |
| Methods Native called that React never called | 9 |
| Chatty methods (>2x call ratio) | 5 |
| Observed push events (web / native) | 10 / 5 |
| React-subscribed push events unhandled by Native (static) | 45 |

## Not drivable

| Action | Client | Reason |
| --- | --- | --- |
| `discord-dm` (Open Discord DM conversation) | web | Discord not signed in in disposable test instance |
| `discord-dm` (Open Discord DM conversation) | native | Discord not signed in in disposable test instance |
| `game-page` (Open a game page from Library) | web | no game tiles in Library |
| `downloads` (Open Downloads sheet) | web | Downloads menu item not found |
| `downloads` (Open Downloads sheet) | native | Native UI has no Downloads sheet button outside Settings |

## Top Missing Methods (React called, Native never called)

| Method | Web calls | Actions | React caller(s) |
| --- | ---: | --- | --- |
| `shell.heartbeat` | 84 | `startup`, `room-store`, `room-discord`, `discord-dm`, `room-music`, `room-devices`, `room-pc`, `room-pc-apps`, `room-library`, `game-page`, `downloads`, `palette`, `settings-general`, `settings-library`, `settings-downloads`, `settings-ingame`, `settings-music`, `settings-about` | `ui/src/lib/heartbeat.ts:15` |
| `app.pendingOpen` | 9 | `startup`, `room-store`, `room-discord`, `room-music`, `room-devices`, `room-pc`, `room-pc-apps`, `room-library`, `settings-general` | `ui/src/components/shell/useShellEvents.ts:279` |
| `telemetry.track` | 9 | `startup`, `room-store`, `room-discord`, `room-music`, `room-devices`, `room-pc`, `room-pc-apps`, `room-library`, `settings-general` | `ui/src/components/LauncherApp.tsx:869` |
| `shell.setChromeLayout` | 6 | `startup`, `room-library`, `palette` | `ui/src/components/shell/useShellLayout.ts:106` |
| `discord.signIn` | 4 | `startup`, `room-discord`, `room-music` | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `gifts.list` | 4 | `startup`, `room-library`, `settings-general` | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `shaders.call` | 3 | `startup`, `room-pc-apps`, `settings-general` | `ui/src/components/shaders/shaderCall.ts:57` |
| `app.log` | 4 | `startup`, `settings-general` | `ui/src/components/LauncherApp.tsx:468`, `ui/src/components/shell/useRoomController.ts:132`, `ui/src/components/shell/useRoomController.ts:288` |
| `accounts.list` | 2 | `startup`, `settings-general` | `ui/src/components/settings/StoreAccounts.tsx:27`, `ui/src/components/shell/GameMenu.tsx:53` |
| `keep.status` | 2 | `startup`, `room-pc` | `ui/src/components/game/OptimizeLine.tsx:46`, `ui/src/components/gpu/PcOverview.tsx:74` |
| `steam.account` | 2 | `startup`, `settings-general` | `ui/src/components/settings/SteamSignInRow.tsx:31` |
| `store.wishlist` | 2 | `startup`, `room-store` | `ui/src/components/store/storeData.ts:266`, `ui/src/components/store/storeData.ts:309`, `ui/src/components/store/storeData.ts:320` |
| `system.tune` | 2 | `room-pc`, `room-pc-apps` | `ui/src/components/gpu/AutoTune.tsx:44`, `ui/src/components/gpu/AutoTune.tsx:60`, `ui/src/components/gpu/usePcState.ts:75` |
| `store.game` | 10 | `room-store` | `ui/src/components/store/storeData.ts:150` |
| `dlss.status` | 6 | `startup` | `ui/src/lib/bridgeCancellation.ts:3`, `ui/src/lib/upscalerCache.ts:34` |
| `web.preferences.get` | 2 | `startup` | `ui/src/components/LauncherApp.tsx:184`, `ui/src/components/onboarding/AppsStep.tsx:22`, `ui/src/components/settings/AppsSection.tsx:24` |
| `apps.installerCache` | 1 | `room-pc-apps` | `ui/src/components/apps/AppsPanel.tsx:821` |
| `apps.packages` | 1 | `room-pc` | `ui/src/components/gpu/ExoAiRoom.tsx:170` |
| `apps.tidy` | 1 | `room-pc-apps` | `ui/src/components/apps/AppsPanel.tsx:145` |
| `art.base` | 1 | `startup` | `ui/src/main.tsx:26` |
| `backup.status` | 1 | `startup` | `ui/src/components/OnboardingPanel.tsx:95`, `ui/src/components/settings/BackupSection.tsx:30`, `ui/src/components/settings/BackupSection.tsx:67` |
| `capture.status` | 1 | `startup` | `ui/src/lib/capture.ts:93`, `ui/src/lib/capture.ts:95` |
| `clients.status` | 1 | `startup` | `ui/src/components/clients/ClientsCard.tsx:55`, `ui/src/components/clients/ClientsCard.tsx:112`, `ui/src/components/clients/ClientsCard.tsx:131` |
| `controller.read` | 1 | `room-pc` | `ui/src/components/devices/ControllerPage.tsx:32`, `ui/src/components/gpu/DriversSection.tsx:298` |
| `discord.clientSettings` | 1 | `startup` | `ui/src/components/discord/GoLivePicker.tsx:320`, `ui/src/components/discord/MeMenus.tsx:126`, `ui/src/components/discord/MeMenus.tsx:132` |
| `discord.home` | 1 | `palette` | `ui/src/components/discord/DiscordClient.tsx:319`, `ui/src/components/discord/MessageTools.tsx:20`, `ui/src/components/shell/ExoIslandCall.tsx:58` |
| `dlss.newer` | 1 | `room-discord` | `ui/src/components/LauncherApp.tsx:111` |
| `exoAccount.overview` | 1 | `startup` | `ui/src/components/account/useAccount.ts:6`, `ui/src/components/account/useAccount.ts:10` |
| `friends.badge` | 1 | `startup` | `ui/src/components/shell/DiscordBadge.tsx:15`, `ui/src/components/shell/DiscordBadge.tsx:16`, `ui/src/lib/hostContract.ts:39` |
| `gpu.display` | 1 | `room-pc` | `ui/src/components/gpu/DisplayTab.tsx:41`, `ui/src/components/gpu/DisplayTab.tsx:66`, `ui/src/components/gpu/autoTuneStages.ts:136` |
| `gpu.driver` | 1 | `room-pc` | `ui/src/components/gpu/DriverRow.tsx:129`, `ui/src/components/gpu/DriverRow.tsx:135`, `ui/src/components/gpu/DriverRow.tsx:148` |
| `hid.close` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:11` |
| `hid.list` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:9` |
| `hid.open` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:10` |
| `hid.sendReport` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:12` |
| `junk.scan` | 1 | `room-pc-apps` | `ui/src/components/OnboardingPanel.tsx:153`, `ui/src/components/OnboardingPanel.tsx:375`, `ui/src/components/apps/AppsPanel.tsx:776` |
| `library.get` | 1 | `startup` | `ui/src/components/LauncherApp.tsx:279`, `ui/src/components/devices/GameProfiles.tsx:32`, `ui/src/lib/bridgeCancellation.ts:2` |
| `mouse.presets.get` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:61` |
| `music.warm` | 1 | `room-music` | `ui/src/lib/music.ts:126` |
| `notes.list` | 1 | `palette` | `ui/src/components/captures/NotesView.tsx:388`, `ui/src/components/captures/NotesView.tsx:395`, `ui/src/components/captures/NotesView.tsx:485` |
| `notes.pending` | 1 | `startup` | `ui/src/lib/notes.ts:46` |
| `runtimes.list` | 1 | `room-pc` | `ui/src/components/gpu/PcApps.tsx:16` |
| `settings.get` | 1 | `startup` | `ui/src/lib/settingsSave.ts:27` |
| `shell.painted` | 1 | `startup` | `ui/src/components/shell/BootGate.tsx:31` |
| `shell.prepare` | 1 | `startup` | `ui/src/components/LauncherApp.tsx:491` |
| `shell.ready` | 1 | `startup` | `ui/src/lib/shellReady.ts:10` |
| `shell.windowState` | 1 | `startup` | `ui/src/components/WindowChrome.tsx:13` |
| `stores.matrix` | 1 | `startup` | `ui/src/components/OnboardingPanel.tsx:47`, `ui/src/components/OnboardingPanel.tsx:250`, `ui/src/components/SettingsPanel.tsx:160` |
| `stores.outages` | 1 | `startup` | `ui/src/components/shell/StoreOutageNotices.tsx:60` |
| `system.drivers` | 1 | `room-pc` | `ui/src/components/gpu/DriversSection.tsx:139`, `ui/src/components/gpu/DriversSection.tsx:190`, `ui/src/components/gpu/DriversSection.tsx:207` |
| `system.health` | 1 | `room-pc` | `ui/src/components/OnboardingPanel.tsx:161`, `ui/src/components/gpu/BestSetup.tsx:34`, `ui/src/components/gpu/BestSetup.tsx:49` |
| `system.interrupts` | 1 | `room-pc` | `ui/src/components/gpu/InterruptRows.tsx:27`, `ui/src/components/gpu/autoTuneStages.ts:191`, `ui/src/components/gpu/usePcState.ts:72` |
| `web.preferences.set` | 1 | `startup` | `ui/src/components/LauncherApp.tsx:189`, `ui/src/components/onboarding/AppsStep.tsx:35`, `ui/src/components/settings/AppsSection.tsx:57` |

## Native-only Methods (Native called, React did not)

| Method | Native calls | Actions | React caller(s) |
| --- | ---: | --- | --- |
| `discord.accounts` | 2 | `room-discord`, `discord-dm` | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `audio.read` | 1 | `room-devices` | `ui/src/components/devices/AudioPage.tsx:88` |
| `eq` | 1 | `room-devices` | — |
| `exoAccount.state` | 1 | `settings-general` | `ui/src/lib/host.ts:2390` |
| `game.get` | 1 | `game-page` | `ui/src/components/shell/useGameActions.ts:261`, `ui/src/components/shell/useRunStatePoll.ts:56`, `ui/src/components/shell/useShellEvents.ts:179` |
| `game.properties` | 1 | `game-page` | `ui/src/components/game/GameProperties.tsx:205` |
| `keyboard.backups` | 1 | `room-devices` | `ui/src/components/devices/KeyboardPage.tsx:42`, `ui/src/components/devices/KeyboardPage.tsx:52`, `ui/src/components/devices/KeyboardPage.tsx:67` |
| `keyboard.read` | 1 | `room-devices` | `ui/src/components/devices/KeyboardPage.tsx:52`, `ui/src/components/devices/KeyboardPage.tsx:67` |
| `shell.bootstrap` | 1 | `startup` | `ui/src/lib/host.ts:2315` |

## Chattiness (>2x call count difference across session)

| Method | Web calls | Native calls | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `music.volume` | 8 | 1 | 8x | web | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `shaders.addon` | 5 | 1 | 5x | web | `ui/src/components/shaders/ShaderTools.tsx:15`, `ui/src/components/shaders/ShadersBoard.tsx:30`, `ui/src/components/shaders/ShadersBoard.tsx:41` |
| `game.extras` | 3 | 1 | 3x | web | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `store.home` | 3 | 1 | 3x | web | `ui/src/components/store/StoreRoom.tsx:451` |
| `spotify.start` | 3 | 1 | 3x | web | `ui/src/lib/musicAdapters.ts:147` |

## Push Event Gaps

### Observed during recorded actions

| Event | Web count | Native count | Native handles? | Actions | React subscriber(s) |
| --- | ---: | ---: | --- | --- | --- |
| `app.updateState` | 2 | 0 | yes | `startup` | `ui/src/components/shell/useAppUpdate.ts:119` |
| `discord.signin` | 2 | 0 | yes | `startup`, `discord-dm` | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |
| `music.status` | 2 | 1 | no | `startup`, `room-music` | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `discord.state` | 1 | 0 | yes | `startup` | `ui/src/components/discord/Chat.tsx:540`, `ui/src/components/discord/NativeDiscordRoom.tsx:28`, `ui/src/components/onboarding/DiscordStep.tsx:14` |
| `game.achievements` | 1 | 0 | no | `startup` | `ui/src/components/game/useGameDetails.ts:68` |
| `gifts.changed` | 1 | 0 | no | `startup` | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `music.playback` | 1 | 0 | yes | `startup` | `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:111`, `ui/src/components/shell/MusicMiniPlayer.tsx:79` |
| `music.volume` | 1 | 0 | yes | `room-devices` | `ui/src/components/shell/MusicVolume.tsx:40` |
| `shell.window` | 1 | 0 | no | `startup` | `ui/src/components/WindowChrome.tsx:16` |
| `store.changed` | 1 | 0 | yes | `startup` | `ui/src/components/store/StoreRoom.tsx:467`, `ui/src/components/store/storeData.ts:274` |

### Static React subscribers missing from native `exo-rpc`

| Event | Observed web | Observed native | React subscriber(s) |
| --- | ---: | ---: | --- |
| `music.status` | 2 | 2 | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `gifts.changed` | 1 | 0 | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `game.achievements` | 1 | 1 | `ui/src/components/game/useGameDetails.ts:68` |
| `shell.window` | 1 | 0 | `ui/src/components/WindowChrome.tsx:16` |
| `discord.colors` | 0 | 0 | `ui/src/components/discord/Chat.tsx:899`, `ui/src/components/discord/Composer.tsx:103`, `ui/src/components/discord/UserMenu.tsx:119` |
| `discord.interaction` | 0 | 0 | `ui/src/components/discord/AppCommands.tsx:255`, `ui/src/components/discord/AppModal.tsx:19`, `ui/src/lib/discordApps.ts:128` |
| `accounts.progress` | 0 | 0 | `ui/src/components/settings/StoreAccounts.tsx:141`, `ui/src/components/settings/StoresSection.tsx:189` |
| `compress.progress` | 0 | 0 | `ui/src/components/game/CompressRow.tsx:45`, `ui/src/components/gpu/CompressGames.tsx:17` |
| `discord.callSong` | 0 | 0 | `ui/src/lib/listenAlong.ts:59`, `ui/src/lib/listenAlong.ts:144` |
| `discord.camera` | 0 | 0 | `ui/src/lib/discordPrefs.ts:396`, `ui/src/lib/goLive.ts:216` |
| `discord.open` | 0 | 0 | `ui/src/components/discord/DiscordClient.tsx:395`, `ui/src/components/shell/useShellEvents.ts:267` |
| `discord.pollVote` | 0 | 0 | `ui/src/components/discord/Chat.tsx:702`, `ui/src/components/discord/messageCache.ts:132` |
| `game.optimizedAgain` | 0 | 0 | `ui/src/components/game/OptimizeLine.tsx:55`, `ui/src/components/shell/useShellEvents.ts:193` |
| `gpu.driver` | 0 | 0 | `ui/src/components/gpu/DriverRow.tsx:135`, `ui/src/components/shell/DriverInstallOverlay.tsx:109` |
| `shaders.event` | 0 | 0 | `ui/src/components/shaders/ShaderTools.tsx:16`, `ui/src/components/shaders/ShadersBoard.tsx:47` |
| `anticheat.missing` | 0 | 0 | `ui/src/components/shell/AntiCheatNotices.tsx:32` |
| `app.open` | 0 | 0 | `ui/src/components/shell/useShellEvents.ts:280` |
| `backup.stopped` | 0 | 0 | `ui/src/components/shell/useShellEvents.ts:263` |
| `bios.progress` | 0 | 0 | `ui/src/components/gpu/DriversSection.tsx:349` |
| `capture.shrunk` | 0 | 0 | `ui/src/components/captures/CapturesSheet.tsx:298` |
| `components.failed` | 0 | 0 | `ui/src/components/shell/useShellEvents.ts:192` |
| `controller.state` | 0 | 0 | `ui/src/components/devices/ControllerPage.tsx:121` |
| `discord.call` | 0 | 0 | `ui/src/components/discord/voice.tsx:98` |
| `discord.dmLine` | 0 | 0 | `ui/src/components/discord/DiscordClient.tsx:718` |
| `discord.keybind` | 0 | 0 | `ui/src/lib/discordPrefs.ts:399` |
| `discord.micLevel` | 0 | 0 | `ui/src/components/discord/settings/VoicePage.tsx:258` |
| `discord.notify` | 0 | 0 | `ui/src/components/shell/DiscordNotices.tsx:20` |
| `discord.notifyClear` | 0 | 0 | `ui/src/components/shell/DiscordNotices.tsx:32` |
| `discord.tts` | 0 | 0 | `ui/src/lib/discordPrefs.ts:383` |
| `discord.userSettings` | 0 | 0 | `ui/src/lib/discordPrefs.ts:203` |
| `discord.video.frame` | 0 | 0 | `ui/src/lib/discordVideo.ts:260` |
| `discord.video.keyframe` | 0 | 0 | `ui/src/lib/goLive.ts:217` |
| `drivers.step` | 0 | 0 | `ui/src/components/gpu/DriversSection.tsx:170` |
| `friends.badge` | 0 | 0 | `ui/src/components/shell/DiscordBadge.tsx:15` |
| `game.optimizeHeld` | 0 | 0 | `ui/src/components/game/OptimizeLine.tsx:56` |
| `gpu.driverInstall` | 0 | 0 | `ui/src/components/shell/DriverInstallOverlay.tsx:115` |
| `hid.changed` | 0 | 0 | `ui/src/lib/mouseRelay.ts:213` |
| `hid.input` | 0 | 0 | `ui/src/lib/mouseRelay.ts:212` |
| `keys.found` | 0 | 0 | `ui/src/components/shell/useShellEvents.ts:300` |
| `notes.open` | 0 | 0 | `ui/src/lib/notes.ts:47` |
| `runtimes.progress` | 0 | 0 | `ui/src/components/runtimes/RuntimesPanel.tsx:92` |
| `shaders.rebuild` | 0 | 0 | `ui/src/components/shaders/ShadersBoard.tsx:351` |
| `store.sale` | 0 | 0 | `ui/src/components/shell/useShellEvents.ts:188` |
| `stores.outage` | 0 | 0 | `ui/src/components/shell/StoreOutageNotices.tsx:59` |
| `versions.new` | 0 | 0 | `ui/src/components/shell/useShellEvents.ts:247` |

## Per-Action Breakdown (sorted by biggest gaps first)

### `startup` — App startup (Library initial load)

- **Gap score:** 516 | **Web:** ok (74 req, 10 ev) | **Native:** ok (1 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 13 | 0.08 | `ui/src/lib/heartbeat.ts:15` |
| `dlss.status` | 6 | 1521.91 | `ui/src/lib/bridgeCancellation.ts:3`, `ui/src/lib/upscalerCache.ts:34` |
| `music.volume` | 3 | 0.31 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `shell.setChromeLayout` | 3 | 4.03 | `ui/src/components/shell/useShellLayout.ts:106` |
| `app.log` | 2 | 0.68 | `ui/src/components/LauncherApp.tsx:468`, `ui/src/components/shell/useRoomController.ts:132`, `ui/src/components/shell/useRoomController.ts:288` |
| `discord.signIn` | 2 | 0.84 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `game.extras` | 2 | 27.9 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `gifts.list` | 2 | 7.71 | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `shaders.addon` | 2 | 1.72 | `ui/src/components/shaders/ShaderTools.tsx:15`, `ui/src/components/shaders/ShadersBoard.tsx:30`, `ui/src/components/shaders/ShadersBoard.tsx:41` |
| `store.home` | 2 | 4009.24 | `ui/src/components/store/StoreRoom.tsx:451` |
| `web.preferences.get` | 2 | 1.75 | `ui/src/components/LauncherApp.tsx:184`, `ui/src/components/onboarding/AppsStep.tsx:22`, `ui/src/components/settings/AppsSection.tsx:24` |
| `accounts.list` | 1 | 112.49 | `ui/src/components/settings/StoreAccounts.tsx:27`, `ui/src/components/shell/GameMenu.tsx:53` |
| `app.checkUpdate` | 1 | 365.93 | `ui/src/components/shell/useAppUpdate.ts:99`, `ui/src/components/shell/useAppUpdate.ts:171`, `ui/src/lib/bridgeCancellation.ts:4` |
| `app.pendingOpen` | 1 | 0.63 | `ui/src/components/shell/useShellEvents.ts:279` |
| `art.base` | 1 | 3.56 | `ui/src/main.tsx:26` |
| `backup.status` | 1 | 56.04 | `ui/src/components/OnboardingPanel.tsx:95`, `ui/src/components/settings/BackupSection.tsx:30`, `ui/src/components/settings/BackupSection.tsx:67` |
| `capture.status` | 1 | 5.93 | `ui/src/lib/capture.ts:93`, `ui/src/lib/capture.ts:95` |
| `clients.status` | 1 | 38.1 | `ui/src/components/clients/ClientsCard.tsx:55`, `ui/src/components/clients/ClientsCard.tsx:112`, `ui/src/components/clients/ClientsCard.tsx:131` |
| `discord.clientSettings` | 1 | 11.89 | `ui/src/components/discord/GoLivePicker.tsx:320`, `ui/src/components/discord/MeMenus.tsx:126`, `ui/src/components/discord/MeMenus.tsx:132` |
| `discord.status` | 1 | 1.52 | `ui/src/components/discord/NativeDiscordRoom.tsx:29`, `ui/src/components/discord/NativeDiscordRoom.tsx:42`, `ui/src/components/onboarding/DiscordStep.tsx:15` |
| `discord.stream` | 1 | 1.82 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `discord.voice` | 1 | 4.05 | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |
| `exoAccount.overview` | 1 | 3.05 | `ui/src/components/account/useAccount.ts:6`, `ui/src/components/account/useAccount.ts:10` |
| `friends.badge` | 1 | 0.38 | `ui/src/components/shell/DiscordBadge.tsx:15`, `ui/src/components/shell/DiscordBadge.tsx:16`, `ui/src/lib/hostContract.ts:39` |
| `hdr.state` | 1 | 6.17 | `ui/src/lib/hdr.ts:10` |
| `hid.close` | 1 | 9.79 | `ui/src/lib/mouseRelay.ts:11` |
| `hid.list` | 1 | 104.73 | `ui/src/lib/mouseRelay.ts:9` |
| `hid.open` | 1 | 6 | `ui/src/lib/mouseRelay.ts:10` |
| `hid.sendReport` | 1 | 13.53 | `ui/src/lib/mouseRelay.ts:12` |
| `keep.status` | 1 | 11.66 | `ui/src/components/game/OptimizeLine.tsx:46`, `ui/src/components/gpu/PcOverview.tsx:74` |
| `library.get` | 1 | 919.48 | `ui/src/components/LauncherApp.tsx:279`, `ui/src/components/devices/GameProfiles.tsx:32`, `ui/src/lib/bridgeCancellation.ts:2` |
| `mouse.presets.get` | 1 | 1.89 | `ui/src/lib/mouseRelay.ts:61` |
| `music.playback` | 1 | 1.9 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |
| `notes.pending` | 1 | 0.27 | `ui/src/lib/notes.ts:46` |
| `settings.get` | 1 | 11.12 | `ui/src/lib/settingsSave.ts:27` |
| `shaders.call` | 1 | 4.18 | `ui/src/components/shaders/shaderCall.ts:57` |
| `shell.painted` | 1 | 0.06 | `ui/src/components/shell/BootGate.tsx:31` |
| `shell.prepare` | 1 | 5.04 | `ui/src/components/LauncherApp.tsx:491` |
| `shell.ready` | 1 | 0.61 | `ui/src/lib/shellReady.ts:10` |
| `shell.windowState` | 1 | 0.67 | `ui/src/components/WindowChrome.tsx:13` |
| `steam.account` | 1 | 0.85 | `ui/src/components/settings/SteamSignInRow.tsx:31` |
| `store.wishlist` | 1 | 9.78 | `ui/src/components/store/storeData.ts:266`, `ui/src/components/store/storeData.ts:309`, `ui/src/components/store/storeData.ts:320` |
| `stores.matrix` | 1 | 494.44 | `ui/src/components/OnboardingPanel.tsx:47`, `ui/src/components/OnboardingPanel.tsx:250`, `ui/src/components/SettingsPanel.tsx:160` |
| `stores.outages` | 1 | 0.89 | `ui/src/components/shell/StoreOutageNotices.tsx:60` |
| `telemetry.track` | 1 | 30.86 | `ui/src/components/LauncherApp.tsx:869` |
| `web.preferences.set` | 1 | 30.6 | `ui/src/components/LauncherApp.tsx:189`, `ui/src/components/onboarding/AppsStep.tsx:35`, `ui/src/components/settings/AppsSection.tsx:57` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.bootstrap` | 1 | 1073.33 | `ui/src/lib/host.ts:2315` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `app.updateState` | 2 | 0 | not emitted on native | `ui/src/components/shell/useAppUpdate.ts:119` |
| `discord.signin` | 1 | 0 | not emitted on native | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |
| `discord.state` | 1 | 0 | not emitted on native | `ui/src/components/discord/Chat.tsx:540`, `ui/src/components/discord/NativeDiscordRoom.tsx:28`, `ui/src/components/onboarding/DiscordStep.tsx:14` |
| `game.achievements` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/game/useGameDetails.ts:68` |
| `gifts.changed` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `music.playback` | 1 | 0 | not emitted on native | `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:111`, `ui/src/components/shell/MusicMiniPlayer.tsx:79` |
| `music.status` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `shell.window` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/WindowChrome.tsx:16` |
| `store.changed` | 1 | 0 | not emitted on native | `ui/src/components/store/StoreRoom.tsx:467`, `ui/src/components/store/storeData.ts:274` |

### `room-pc` — Open Exo AI / PC room

- **Gap score:** 150 | **Web:** ok (31 req, 0 ev) | **Native:** ok (11 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 5 | 0.05 | `ui/src/lib/heartbeat.ts:15` |
| `app.pendingOpen` | 1 | 0.02 | `ui/src/components/shell/useShellEvents.ts:279` |
| `apps.list` | 1 | 2414.55 | `ui/src/components/apps/AppsPanel.tsx:131`, `ui/src/components/apps/AppsPanel.tsx:204`, `ui/src/components/gpu/PcApps.tsx:16` |
| `apps.packages` | 1 | 644.62 | `ui/src/components/gpu/ExoAiRoom.tsx:170` |
| `controller.read` | 1 | 383.09 | `ui/src/components/devices/ControllerPage.tsx:32`, `ui/src/components/gpu/DriversSection.tsx:298` |
| `devices.list` | 1 | 67.44 | `ui/src/components/DevicesRoom.tsx:267`, `ui/src/components/gpu/DriversSection.tsx:296` |
| `gpu.display` | 1 | 605.47 | `ui/src/components/gpu/DisplayTab.tsx:41`, `ui/src/components/gpu/DisplayTab.tsx:66`, `ui/src/components/gpu/autoTuneStages.ts:136` |
| `gpu.driver` | 1 | 734.86 | `ui/src/components/gpu/DriverRow.tsx:129`, `ui/src/components/gpu/DriverRow.tsx:135`, `ui/src/components/gpu/DriverRow.tsx:148` |
| `keep.status` | 1 | 1.03 | `ui/src/components/game/OptimizeLine.tsx:46`, `ui/src/components/gpu/PcOverview.tsx:74` |
| `runtimes.list` | 1 | 280.18 | `ui/src/components/gpu/PcApps.tsx:16` |
| `system.drivers` | 1 | — | `ui/src/components/gpu/DriversSection.tsx:139`, `ui/src/components/gpu/DriversSection.tsx:190`, `ui/src/components/gpu/DriversSection.tsx:207` |
| `system.health` | 1 | 133.94 | `ui/src/components/OnboardingPanel.tsx:161`, `ui/src/components/gpu/BestSetup.tsx:34`, `ui/src/components/gpu/BestSetup.tsx:49` |
| `system.interrupts` | 1 | 73.75 | `ui/src/components/gpu/InterruptRows.tsx:27`, `ui/src/components/gpu/autoTuneStages.ts:191`, `ui/src/components/gpu/usePcState.ts:72` |
| `system.tune` | 1 | 14.15 | `ui/src/components/gpu/AutoTune.tsx:44`, `ui/src/components/gpu/AutoTune.tsx:60`, `ui/src/components/gpu/usePcState.ts:75` |
| `telemetry.track` | 1 | 0.04 | `ui/src/components/LauncherApp.tsx:869` |

### `room-pc-apps` — Open Apps room

- **Gap score:** 100 | **Web:** ok (16 req, 0 ev) | **Native:** ok (2 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 5 | 0.05 | `ui/src/lib/heartbeat.ts:15` |
| `app.pendingOpen` | 1 | 0.02 | `ui/src/components/shell/useShellEvents.ts:279` |
| `apps.icons` | 1 | 1188.17 | `ui/src/components/apps/AppsPanel.tsx:135`, `ui/src/components/apps/AppsPanel.tsx:860` |
| `apps.installerCache` | 1 | 138.45 | `ui/src/components/apps/AppsPanel.tsx:821` |
| `apps.tidy` | 1 | — | `ui/src/components/apps/AppsPanel.tsx:145` |
| `exoAi.state` | 1 | 3.78 | `ui/src/components/gpu/ExoAiPanel.tsx:10` |
| `junk.scan` | 1 | 411.14 | `ui/src/components/OnboardingPanel.tsx:153`, `ui/src/components/OnboardingPanel.tsx:375`, `ui/src/components/apps/AppsPanel.tsx:776` |
| `shaders.call` | 1 | 0.48 | `ui/src/components/shaders/shaderCall.ts:57` |
| `system.tune` | 1 | 0.59 | `ui/src/components/gpu/AutoTune.tsx:44`, `ui/src/components/gpu/AutoTune.tsx:60`, `ui/src/components/gpu/usePcState.ts:75` |
| `telemetry.track` | 1 | 0.04 | `ui/src/components/LauncherApp.tsx:869` |

### `settings-general` — Open Settings > General

- **Gap score:** 98 | **Web:** ok (12 req, 0 ev) | **Native:** ok (4 req, 2 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 3 | 0.06 | `ui/src/lib/heartbeat.ts:15` |
| `app.log` | 2 | 0.11 | `ui/src/components/LauncherApp.tsx:468`, `ui/src/components/shell/useRoomController.ts:132`, `ui/src/components/shell/useRoomController.ts:288` |
| `accounts.list` | 1 | 15.54 | `ui/src/components/settings/StoreAccounts.tsx:27`, `ui/src/components/shell/GameMenu.tsx:53` |
| `app.pendingOpen` | 1 | 0.02 | `ui/src/components/shell/useShellEvents.ts:279` |
| `gifts.list` | 1 | 0.08 | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `shaders.addon` | 1 | 0.46 | `ui/src/components/shaders/ShaderTools.tsx:15`, `ui/src/components/shaders/ShadersBoard.tsx:30`, `ui/src/components/shaders/ShadersBoard.tsx:41` |
| `shaders.call` | 1 | 0.17 | `ui/src/components/shaders/shaderCall.ts:57` |
| `steam.account` | 1 | 0.24 | `ui/src/components/settings/SteamSignInRow.tsx:31` |
| `telemetry.track` | 1 | 0.72 | `ui/src/components/LauncherApp.tsx:869` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.checkUpdate` | 1 | 362.21 | `ui/src/components/shell/useAppUpdate.ts:99`, `ui/src/components/shell/useAppUpdate.ts:171`, `ui/src/lib/bridgeCancellation.ts:4` |
| `exoAccount.state` | 1 | 0.8 | `ui/src/lib/host.ts:2390` |
| `hdr.state` | 1 | 0.3 | `ui/src/lib/hdr.ts:10` |
| `system.about` | 1 | 59.13 | `ui/src/components/gpu/usePcState.ts:68` |

### `room-devices` — Open Devices room

- **Gap score:** 74 | **Web:** ok (14 req, 1 ev) | **Native:** ok (10 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 4 | 0.07 | `ui/src/lib/heartbeat.ts:15` |
| `app.pendingOpen` | 1 | 0.01 | `ui/src/components/shell/useShellEvents.ts:279` |
| `music.playback` | 1 | 0.08 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |
| `music.volume` | 1 | 33.17 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `spotify.start` | 1 | 207.76 | `ui/src/lib/musicAdapters.ts:147` |
| `telemetry.track` | 1 | 0.58 | `ui/src/components/LauncherApp.tsx:869` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `audio.read` | 1 | 26.39 | `ui/src/components/devices/AudioPage.tsx:88` |
| `eq` | 1 | 180.83 | — |
| `keyboard.backups` | 1 | 296.42 | `ui/src/components/devices/KeyboardPage.tsx:42`, `ui/src/components/devices/KeyboardPage.tsx:52`, `ui/src/components/devices/KeyboardPage.tsx:67` |
| `keyboard.read` | 1 | 1232.16 | `ui/src/components/devices/KeyboardPage.tsx:52`, `ui/src/components/devices/KeyboardPage.tsx:67` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `music.volume` | 1 | 0 | not emitted on native | `ui/src/components/shell/MusicVolume.tsx:40` |

### `room-discord` — Open Discord room

- **Gap score:** 66 | **Web:** ok (10 req, 0 ev) | **Native:** ok (4 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 4 | 0.06 | `ui/src/lib/heartbeat.ts:15` |
| `app.pendingOpen` | 1 | 0.02 | `ui/src/components/shell/useShellEvents.ts:279` |
| `discord.signIn` | 1 | 0.08 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `dlss.newer` | 1 | 1.04 | `ui/src/components/LauncherApp.tsx:111` |
| `music.volume` | 1 | 0.07 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `telemetry.track` | 1 | 0.08 | `ui/src/components/LauncherApp.tsx:869` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.accounts` | 1 | 3.05 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `discord.stream` | 1 | 7.4 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `discord.voice` | 1 | 3.2 | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |

### `room-library` — Return to Library room

- **Gap score:** 62 | **Web:** ok (10 req, 0 ev) | **Native:** ok (1 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 4 | 0.06 | `ui/src/lib/heartbeat.ts:15` |
| `shell.setChromeLayout` | 2 | 2.75 | `ui/src/components/shell/useShellLayout.ts:106` |
| `app.pendingOpen` | 1 | 0.02 | `ui/src/components/shell/useShellEvents.ts:279` |
| `game.extras` | 1 | 2.36 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `gifts.list` | 1 | 0.06 | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |
| `telemetry.track` | 1 | 1.02 | `ui/src/components/LauncherApp.tsx:869` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `apps.icons` | 1 | 2044.35 | `ui/src/components/apps/AppsPanel.tsx:135`, `ui/src/components/apps/AppsPanel.tsx:860` |

### `room-store` — Open Store room

- **Gap score:** 60 | **Web:** ok (19 req, 0 ev) | **Native:** ok (1 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `store.game` | 10 | 219.68 | `ui/src/components/store/storeData.ts:150` |
| `shell.heartbeat` | 4 | 0.06 | `ui/src/lib/heartbeat.ts:15` |
| `app.pendingOpen` | 1 | 0.03 | `ui/src/components/shell/useShellEvents.ts:279` |
| `music.volume` | 1 | 0.05 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `store.wishlist` | 1 | 10.17 | `ui/src/components/store/storeData.ts:266`, `ui/src/components/store/storeData.ts:309`, `ui/src/components/store/storeData.ts:320` |
| `telemetry.track` | 1 | 1.04 | `ui/src/components/LauncherApp.tsx:869` |

### `room-music` — Open Music room

- **Gap score:** 58 | **Web:** ok (11 req, 1 ev) | **Native:** ok (3 req, 2 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 4 | 0.05 | `ui/src/lib/heartbeat.ts:15` |
| `app.pendingOpen` | 1 | 0.02 | `ui/src/components/shell/useShellEvents.ts:279` |
| `discord.signIn` | 1 | 0.33 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `music.warm` | 1 | 0.66 | `ui/src/lib/music.ts:126` |
| `telemetry.track` | 1 | 0.64 | `ui/src/components/LauncherApp.tsx:869` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.playback` | 1 | 0.12 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `music.status` | 1 | 1 | emitted but unhandled in native | `ui/src/components/music/NativeMusicRoom.tsx:132` |

### `discord-dm` — Open Discord DM conversation

- **Gap score:** 49 | **Web:** not drivable (Discord not signed in in disposable test instance) (10 req, 1 ev) | **Native:** not drivable (Discord not signed in in disposable test instance) (4 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 9 | 0.06 | `ui/src/lib/heartbeat.ts:15` |
| `music.volume` | 1 | 0.08 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.accounts` | 1 | 0.25 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `discord.status` | 1 | 0.37 | `ui/src/components/discord/NativeDiscordRoom.tsx:29`, `ui/src/components/discord/NativeDiscordRoom.tsx:42`, `ui/src/components/onboarding/DiscordStep.tsx:15` |
| `discord.stream` | 1 | 0.11 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `discord.voice` | 1 | 0.06 | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `discord.signin` | 1 | 0 | not emitted on native | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |

### `palette` — Open Command Palette (Ctrl+K)

- **Gap score:** 40 | **Web:** ok (6 req, 0 ev) | **Native:** ok (0 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 3 | 0.05 | `ui/src/lib/heartbeat.ts:15` |
| `discord.home` | 1 | 2.41 | `ui/src/components/discord/DiscordClient.tsx:319`, `ui/src/components/discord/MessageTools.tsx:20`, `ui/src/components/shell/ExoIslandCall.tsx:58` |
| `notes.list` | 1 | 35.06 | `ui/src/components/captures/NotesView.tsx:388`, `ui/src/components/captures/NotesView.tsx:395`, `ui/src/components/captures/NotesView.tsx:485` |
| `shell.setChromeLayout` | 1 | 3.58 | `ui/src/components/shell/useShellLayout.ts:106` |

### `downloads` — Open Downloads sheet

- **Gap score:** 25 | **Web:** not drivable (Downloads menu item not found) (9 req, 0 ev) | **Native:** not drivable (Native UI has no Downloads sheet button outside Settings) (0 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 9 | 0.06 | `ui/src/lib/heartbeat.ts:15` |

### `game-page` — Open a game page from Library

- **Gap score:** 16 | **Web:** not drivable (no game tiles in Library) (9 req, 0 ev) | **Native:** ok (3 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 9 | 0.06 | `ui/src/lib/heartbeat.ts:15` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `game.extras` | 1 | 7.65 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `game.get` | 1 | 99.82 | `ui/src/components/shell/useGameActions.ts:261`, `ui/src/components/shell/useRunStatePoll.ts:56`, `ui/src/components/shell/useShellEvents.ts:179` |
| `game.properties` | 1 | 352.46 | `ui/src/components/game/GameProperties.tsx:205` |

### `settings-about` — Open Settings > About

- **Gap score:** 10 | **Web:** ok (2 req, 0 ev) | **Native:** ok (0 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 2 | 0.06 | `ui/src/lib/heartbeat.ts:15` |

### `settings-downloads` — Open Settings > Downloads

- **Gap score:** 10 | **Web:** ok (1 req, 0 ev) | **Native:** ok (0 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 1 | 0.06 | `ui/src/lib/heartbeat.ts:15` |

### `settings-ingame` — Open Settings > In game

- **Gap score:** 10 | **Web:** ok (2 req, 0 ev) | **Native:** ok (0 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 2 | 0.06 | `ui/src/lib/heartbeat.ts:15` |

### `settings-library` — Open Settings > Library

- **Gap score:** 10 | **Web:** ok (2 req, 0 ev) | **Native:** ok (0 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 2 | 0.06 | `ui/src/lib/heartbeat.ts:15` |

### `settings-music` — Open Settings > Music

- **Gap score:** 10 | **Web:** ok (1 req, 0 ev) | **Native:** ok (0 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.heartbeat` | 1 | 0.06 | `ui/src/lib/heartbeat.ts:15` |
