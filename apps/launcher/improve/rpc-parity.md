# host-v1 Record & Replay RPC Parity Report

Generated: 2026-11-24T02:04:36.109Z
Capture: `tools/rpc-parity/out/2026-11-24T02-00-45`

## Summary

| Metric | Value |
| --- | ---: |
| Actions recorded | 18 |
| Web drivable actions | 15 / 18 |
| Native drivable actions | 17 / 18 |
| Unique RPC methods called by React (web) | 79 |
| Unique RPC methods called by Native (Rune) | 90 |
| Methods React called that Native never called | 10 |
| Methods Native called that React never called | 21 |
| Chatty methods (>2x call ratio) | 3 |
| Observed push events (web / native) | 10 / 6 |
| React-subscribed push events unhandled by Native (static) | 16 |

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
| `gpu.processes` | 2 | `room-pc`, `room-pc-apps` | `ui/src/components/gpu/GpuProcessesSection.tsx:33` |
| `discord.clientSettings` | 1 | `startup` | `ui/src/components/discord/GoLivePicker.tsx:320`, `ui/src/components/discord/MeMenus.tsx:126`, `ui/src/components/discord/MeMenus.tsx:132` |
| `hid.close` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:11` |
| `hid.list` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:9` |
| `hid.open` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:10` |
| `hid.sendReport` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:12` |
| `library.get` | 1 | `startup` | `ui/src/components/LauncherApp.tsx:279`, `ui/src/components/devices/GameProfiles.tsx:32`, `ui/src/lib/bridgeCancellation.ts:2` |
| `mouse.presets.get` | 1 | `startup` | `ui/src/lib/mouseRelay.ts:61` |
| `music.warm` | 1 | `room-music` | `ui/src/lib/music.ts:126` |

## Native-only Methods (Native called, React did not)

| Method | Native calls | Actions | React caller(s) |
| --- | ---: | --- | --- |
| `discord.accounts` | 3 | `startup`, `room-discord`, `discord-dm` | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `game.progress` | 2 | `game-page`, `downloads` | `ui/src/components/DownloadsSheet.tsx:39`, `ui/src/lib/hostContract.ts:23` |
| `audio.read` | 1 | `room-devices` | `ui/src/components/devices/AudioPage.tsx:88` |
| `bios.update` | 1 | `room-pc` | `ui/src/components/gpu/AutoTuneCinema.tsx:143`, `ui/src/components/gpu/DriversSection.tsx:348`, `ui/src/components/gpu/DriversSection.tsx:355` |
| `compress.state` | 1 | `game-page` | `ui/src/components/game/CompressRow.tsx:36`, `ui/src/components/game/CompressRow.tsx:41` |
| `dlss.frameGen` | 1 | `game-page` | `ui/src/lib/host.ts:2882` |
| `downloads.list` | 1 | `downloads` | `ui/src/components/DownloadsSheet.tsx:30` |
| `eq` | 1 | `room-devices` | — |
| `fps.counter` | 1 | `settings-general` | `ui/src/components/settings/GeneralSection.tsx:127`, `ui/src/components/settings/GeneralSection.tsx:131`, `ui/src/components/settings/GeneralSection.tsx:144` |
| `game.frameGen` | 1 | `game-page` | `ui/src/components/game/FrameGenRow.tsx:23`, `ui/src/components/game/FrameGenRow.tsx:57` |
| `game.get` | 1 | `game-page` | `ui/src/components/shell/useGameActions.ts:261`, `ui/src/components/shell/useRunStatePoll.ts:56`, `ui/src/components/shell/useShellEvents.ts:179` |
| `game.properties` | 1 | `game-page` | `ui/src/components/game/GameProperties.tsx:205` |
| `game.settings` | 1 | `game-page` | `ui/src/components/game/OptimizeLine.tsx:37`, `ui/src/components/game/OptimizeLine.tsx:66` |
| `gpu.afmf3` | 1 | `room-pc` | `ui/src/components/gpu/Afmf3Row.tsx:18`, `ui/src/components/gpu/Afmf3Row.tsx:25` |
| `gpu.game` | 1 | `game-page` | `ui/src/lib/host.ts:3202` |
| `hdr.game` | 1 | `game-page` | `ui/src/components/game/HdrRow.tsx:39`, `ui/src/components/game/HdrRow.tsx:54` |
| `keyboard.backups` | 1 | `room-devices` | `ui/src/components/devices/KeyboardPage.tsx:50`, `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |
| `keyboard.read` | 1 | `room-devices` | `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |
| `shaders.rebuild` | 1 | `room-pc` | `ui/src/components/shaders/ShadersBoard.tsx:350`, `ui/src/components/shaders/ShadersBoard.tsx:351` |
| `shaders.status` | 1 | `game-page` | `ui/src/components/game/ShaderPrecompile.tsx:65` |
| `shell.bootstrap` | 1 | `startup` | `ui/src/lib/host.ts:2406` |

## Chattiness (>2x call count difference across session)

| Method | Web calls | Native calls | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `music.volume` | 8 | 2 | 4x | web | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |
| `discord.voice` | 1 | 3 | 3x | native | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |
| `game.extras` | 3 | 1 | 3x | web | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |

## Push Event Gaps

### Observed during recorded actions

| Event | Web count | Native count | Native handles? | Actions | React subscriber(s) |
| --- | ---: | ---: | --- | --- | --- |
| `discord.signin` | 2 | 0 | yes | `startup`, `room-discord` | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |
| `music.status` | 2 | 1 | no | `startup`, `room-music` | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `game.achievements` | 1 | 0 | yes | `startup` | `ui/src/components/game/useGameDetails.ts:68` |
| `gifts.changed` | 1 | 0 | yes | `startup` | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `music.volume` | 1 | 0 | yes | `room-devices` | `ui/src/components/shell/MusicVolume.tsx:40` |
| `shell.window` | 1 | 0 | yes | `startup` | `ui/src/components/WindowChrome.tsx:16` |

### Static React subscribers missing from native `exo-rpc`

| Event | Observed web | Observed native | React subscriber(s) |
| --- | ---: | ---: | --- |
| `music.status` | 2 | 2 | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `discord.colors` | 0 | 0 | `ui/src/components/discord/Chat.tsx:899`, `ui/src/components/discord/Composer.tsx:103`, `ui/src/components/discord/UserMenu.tsx:119` |
| `discord.interaction` | 0 | 0 | `ui/src/components/discord/AppCommands.tsx:255`, `ui/src/components/discord/AppModal.tsx:19`, `ui/src/lib/discordApps.ts:128` |
| `discord.callSong` | 0 | 0 | `ui/src/lib/listenAlong.ts:59`, `ui/src/lib/listenAlong.ts:144` |
| `discord.camera` | 0 | 0 | `ui/src/lib/discordPrefs.ts:396`, `ui/src/lib/goLive.ts:216` |
| `discord.pollVote` | 0 | 0 | `ui/src/components/discord/Chat.tsx:702`, `ui/src/components/discord/messageCache.ts:132` |
| `capture.shrunk` | 0 | 0 | `ui/src/components/captures/CapturesSheet.tsx:298` |
| `controller.state` | 0 | 0 | `ui/src/components/devices/ControllerPage.tsx:121` |
| `discord.keybind` | 0 | 0 | `ui/src/lib/discordPrefs.ts:399` |
| `discord.micLevel` | 0 | 0 | `ui/src/components/discord/settings/VoicePage.tsx:258` |
| `discord.tts` | 0 | 0 | `ui/src/lib/discordPrefs.ts:383` |
| `discord.userSettings` | 0 | 0 | `ui/src/lib/discordPrefs.ts:203` |
| `discord.video.frame` | 0 | 0 | `ui/src/lib/discordVideo.ts:260` |
| `discord.video.keyframe` | 0 | 0 | `ui/src/lib/goLive.ts:217` |
| `hid.changed` | 0 | 0 | `ui/src/lib/mouseRelay.ts:213` |
| `hid.input` | 0 | 0 | `ui/src/lib/mouseRelay.ts:212` |

## Per-Action Breakdown (sorted by biggest gaps first)

### `startup` — App startup (Library initial load)

- **Gap score:** 158 | **Web:** ok (75 req, 10 ev) | **Native:** ok (52 req, 6 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.signIn` | 2 | 0.72 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `game.extras` | 2 | 8.67 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `discord.clientSettings` | 1 | 32.72 | `ui/src/components/discord/GoLivePicker.tsx:320`, `ui/src/components/discord/MeMenus.tsx:126`, `ui/src/components/discord/MeMenus.tsx:132` |
| `discord.stream` | 1 | 1.81 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `hdr.state` | 1 | 3.85 | `ui/src/lib/hdr.ts:10` |
| `hid.close` | 1 | 0.98 | `ui/src/lib/mouseRelay.ts:11` |
| `hid.list` | 1 | 97.28 | `ui/src/lib/mouseRelay.ts:9` |
| `hid.open` | 1 | 6.22 | `ui/src/lib/mouseRelay.ts:10` |
| `hid.sendReport` | 1 | 12.72 | `ui/src/lib/mouseRelay.ts:12` |
| `library.get` | 1 | 973.23 | `ui/src/components/LauncherApp.tsx:279`, `ui/src/components/devices/GameProfiles.tsx:32`, `ui/src/lib/bridgeCancellation.ts:2` |
| `mouse.presets.get` | 1 | 10.78 | `ui/src/lib/mouseRelay.ts:61` |
| `music.playback` | 1 | 1.75 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.accounts` | 1 | 2.11 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `shell.bootstrap` | 1 | 1208.47 | `ui/src/lib/host.ts:2406` |

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `music.volume` | 4 | 1 | 4x | web | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `discord.signin` | 1 | 0 | not emitted on native | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |
| `game.achievements` | 1 | 0 | not emitted on native | `ui/src/components/game/useGameDetails.ts:68` |
| `gifts.changed` | 1 | 0 | not emitted on native | `ui/src/components/settings/StoresSection.tsx:161`, `ui/src/components/shell/GiftsShelf.tsx:94` |
| `music.status` | 1 | 1 | emitted but unhandled in native | `ui/src/components/music/NativeMusicRoom.tsx:132` |
| `shell.window` | 1 | 0 | not emitted on native | `ui/src/components/WindowChrome.tsx:16` |

### `room-devices` — Open Devices room

- **Gap score:** 38 | **Web:** ok (14 req, 1 ev) | **Native:** ok (20 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.playback` | 1 | 0.06 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |
| `music.volume` | 1 | 26.69 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `audio.read` | 1 | 10.15 | `ui/src/components/devices/AudioPage.tsx:88` |
| `eq` | 1 | 77.37 | — |
| `keyboard.backups` | 1 | 27.67 | `ui/src/components/devices/KeyboardPage.tsx:50`, `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |
| `keyboard.read` | 1 | 93.08 | `ui/src/components/devices/KeyboardPage.tsx:61`, `ui/src/components/devices/KeyboardPage.tsx:76` |

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `spotify.start` | 1 | 3 | 3x | native | `ui/src/lib/musicAdapters.ts:147` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `music.volume` | 1 | 0 | not emitted on native | `ui/src/components/shell/MusicVolume.tsx:40` |

### `discord-dm` — Open Discord DM conversation

- **Gap score:** 35 | **Web:** not drivable (Discord not signed in in disposable test instance) (12 req, 0 ev) | **Native:** not drivable (Discord not signed in in disposable test instance) (11 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 2 | 0.06 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.pendingOpen` | 1 | 0.03 | `ui/src/components/shell/useShellEvents.ts:279` |
| `discord.accounts` | 1 | 0.39 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `discord.status` | 1 | 0.92 | `ui/src/components/discord/NativeDiscordRoom.tsx:29`, `ui/src/components/discord/NativeDiscordRoom.tsx:42`, `ui/src/components/onboarding/DiscordStep.tsx:15` |
| `discord.stream` | 1 | 0.22 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `discord.voice` | 1 | 0.18 | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |

### `room-music` — Open Music room

- **Gap score:** 30 | **Web:** ok (10 req, 1 ev) | **Native:** ok (11 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.signIn` | 1 | 0.27 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |
| `music.warm` | 1 | 0.58 | `ui/src/lib/music.ts:126` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.playback` | 1 | 4.64 | `ui/src/components/discord/JamBox.tsx:15`, `ui/src/components/discord/JamBox.tsx:16`, `ui/src/components/music/MusicPlayer.tsx:108` |
| `music.volume` | 1 | 10.47 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `music.status` | 1 | 0 | not emitted and unhandled in native | `ui/src/components/music/NativeMusicRoom.tsx:132` |

### `game-page` — Open a game page from Library

- **Gap score:** 26 | **Web:** not drivable (no game tiles in Library) (9 req, 0 ev) | **Native:** ok (21 req, 1 ev)

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.pendingOpen` | 1 | 0.05 | `ui/src/components/shell/useShellEvents.ts:279` |
| `compress.state` | 1 | 15.15 | `ui/src/components/game/CompressRow.tsx:36`, `ui/src/components/game/CompressRow.tsx:41` |
| `dlss.frameGen` | 1 | 87.86 | `ui/src/lib/host.ts:2882` |
| `game.extras` | 1 | 12.73 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `game.frameGen` | 1 | 310.79 | `ui/src/components/game/FrameGenRow.tsx:23`, `ui/src/components/game/FrameGenRow.tsx:57` |
| `game.get` | 1 | 51.05 | `ui/src/components/shell/useGameActions.ts:261`, `ui/src/components/shell/useRunStatePoll.ts:56`, `ui/src/components/shell/useShellEvents.ts:179` |
| `game.progress` | 1 | 1.26 | `ui/src/components/DownloadsSheet.tsx:39`, `ui/src/lib/hostContract.ts:23` |
| `game.properties` | 1 | 144.04 | `ui/src/components/game/GameProperties.tsx:205` |
| `game.settings` | 1 | 314.54 | `ui/src/components/game/OptimizeLine.tsx:37`, `ui/src/components/game/OptimizeLine.tsx:66` |
| `gpu.game` | 1 | 17.04 | `ui/src/lib/host.ts:3202` |
| `hdr.game` | 1 | 44.33 | `ui/src/components/game/HdrRow.tsx:39`, `ui/src/components/game/HdrRow.tsx:54` |
| `keep.status` | 1 | 0.39 | `ui/src/components/game/OptimizeLine.tsx:45`, `ui/src/components/gpu/PcOverview.tsx:97` |
| `shaders.status` | 1 | 4.56 | `ui/src/components/game/ShaderPrecompile.tsx:65` |

### `room-discord` — Open Discord room

- **Gap score:** 24 | **Web:** ok (7 req, 1 ev) | **Native:** ok (19 req, 1 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `discord.signIn` | 1 | 0.07 | `ui/src/components/discord/DiscordSignIn.tsx:21`, `ui/src/components/discord/DiscordSignIn.tsx:25` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `store.game` | 7 | 299.27 | `ui/src/components/store/storeData.ts:150` |
| `discord.accounts` | 1 | 0.09 | `ui/src/components/discord/MeMenus.tsx:51`, `ui/src/components/discord/MeMenus.tsx:102` |
| `discord.stream` | 1 | 1.52 | `ui/src/components/discord/voice.tsx:274`, `ui/src/components/discord/voice.tsx:290`, `ui/src/components/discord/voice.tsx:763` |
| `discord.voice` | 1 | 0.04 | `ui/src/components/discord/voice.tsx:52`, `ui/src/components/discord/voice.tsx:87`, `ui/src/components/discord/voice.tsx:93` |

**Push event gaps:**

| Event | Web | Native | Reason | React subscriber(s) |
| --- | ---: | ---: | --- | --- |
| `discord.signin` | 1 | 0 | not emitted on native | `ui/src/components/discord/DiscordSignIn.tsx:19`, `ui/src/components/discord/MeMenus.tsx:108` |

### `room-pc-apps` — Open Apps room

- **Gap score:** 22 | **Web:** ok (16 req, 0 ev) | **Native:** ok (15 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `gpu.processes` | 1 | 4.01 | `ui/src/components/gpu/GpuProcessesSection.tsx:33` |
| `system.tune` | 1 | 0.43 | `ui/src/components/gpu/AutoTune.tsx:44`, `ui/src/components/gpu/AutoTune.tsx:60`, `ui/src/components/gpu/usePcState.ts:75` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `apps.packages` | 1 | 913.78 | `ui/src/components/gpu/ExoAiRoom.tsx:172` |

### `room-library` — Return to Library room

- **Gap score:** 20 | **Web:** ok (7 req, 0 ev) | **Native:** ok (6 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `game.extras` | 1 | 0.85 | `ui/src/components/game/useGameDetails.ts:58`, `ui/src/components/shell/HomeDeck.tsx:238` |
| `gifts.list` | 1 | 0.06 | `ui/src/components/settings/StoresSection.tsx:160`, `ui/src/components/shell/GiftsShelf.tsx:93` |

### `room-pc` — Open Exo AI / PC room

- **Gap score:** 16 | **Web:** ok (32 req, 0 ev) | **Native:** ok (33 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `gpu.processes` | 1 | 440.77 | `ui/src/components/gpu/GpuProcessesSection.tsx:33` |

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `bios.update` | 1 | 1.55 | `ui/src/components/gpu/AutoTuneCinema.tsx:143`, `ui/src/components/gpu/DriversSection.tsx:348`, `ui/src/components/gpu/DriversSection.tsx:355` |
| `gpu.afmf3` | 1 | 6.42 | `ui/src/components/gpu/Afmf3Row.tsx:18`, `ui/src/components/gpu/Afmf3Row.tsx:25` |
| `shaders.rebuild` | 1 | 0.67 | `ui/src/components/shaders/ShadersBoard.tsx:350`, `ui/src/components/shaders/ShadersBoard.tsx:351` |

### `settings-general` — Open Settings > General

- **Gap score:** 16 | **Web:** ok (13 req, 0 ev) | **Native:** ok (26 req, 2 ev)

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `web.preferences.get` | 2 | 1.01 | `ui/src/components/LauncherApp.tsx:184`, `ui/src/components/onboarding/AppsStep.tsx:22`, `ui/src/components/settings/AppsSection.tsx:24` |
| `app.checkUpdate` | 1 | 151.52 | `ui/src/components/shell/useAppUpdate.ts:99`, `ui/src/components/shell/useAppUpdate.ts:171`, `ui/src/lib/bridgeCancellation.ts:4` |
| `backup.status` | 1 | 3.06 | `ui/src/components/OnboardingPanel.tsx:95`, `ui/src/components/settings/BackupSection.tsx:30`, `ui/src/components/settings/BackupSection.tsx:67` |
| `exoAccount.overview` | 1 | 0.23 | `ui/src/components/account/useAccount.ts:6`, `ui/src/components/account/useAccount.ts:10` |
| `fps.counter` | 1 | 2.85 | `ui/src/components/settings/GeneralSection.tsx:127`, `ui/src/components/settings/GeneralSection.tsx:131`, `ui/src/components/settings/GeneralSection.tsx:144` |
| `hdr.state` | 1 | 0.24 | `ui/src/lib/hdr.ts:10` |
| `stores.matrix` | 1 | 141.6 | `ui/src/components/OnboardingPanel.tsx:47`, `ui/src/components/OnboardingPanel.tsx:250`, `ui/src/components/SettingsPanel.tsx:160` |
| `system.about` | 1 | 7.69 | `ui/src/components/gpu/usePcState.ts:68` |

### `room-store` — Open Store room

- **Gap score:** 14 | **Web:** ok (19 req, 0 ev) | **Native:** ok (10 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `music.volume` | 1 | 0.05 | `ui/src/components/music/Immersive.tsx:276`, `ui/src/components/music/shortcuts.ts:29`, `ui/src/components/music/shortcuts.ts:30` |

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `store.game` | 10 | 3 | 3.33x | web | `ui/src/components/store/storeData.ts:150` |

### `palette` — Open Command Palette (Ctrl+K)

- **Gap score:** 10 | **Web:** ok (6 req, 0 ev) | **Native:** ok (6 req, 0 ev)

**React called, Native did not:**

| Method | Web calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `shell.setChromeLayout` | 1 | 2.32 | `ui/src/components/shell/useShellLayout.ts:106` |

### `downloads` — Open Downloads sheet

- **Gap score:** 6 | **Web:** not drivable (Downloads menu item not found) (9 req, 0 ev) | **Native:** ok (12 req, 0 ev)

**Native called, React did not:**

| Method | Native calls | Median ms | React caller(s) |
| --- | ---: | ---: | --- |
| `app.pendingOpen` | 1 | 0.05 | `ui/src/components/shell/useShellEvents.ts:279` |
| `downloads.list` | 1 | 2.42 | `ui/src/components/DownloadsSheet.tsx:30` |
| `game.progress` | 1 | 0.05 | `ui/src/components/DownloadsSheet.tsx:39`, `ui/src/lib/hostContract.ts:23` |

### `settings-ingame` — Open Settings > In game

- **Gap score:** 4 | **Web:** ok (1 req, 0 ev) | **Native:** ok (3 req, 0 ev)

**Chattiness (>2x):**

| Method | Web | Native | Ratio | Chattier | React caller(s) |
| --- | ---: | ---: | ---: | --- | --- |
| `shell.heartbeat` | 1 | 3 | 3x | native | `ui/src/lib/heartbeat.ts:15` |

### `settings-about` — Open Settings > About

- **Gap score:** 0 | **Web:** ok (2 req, 0 ev) | **Native:** ok (4 req, 0 ev)

No RPC or push-event gaps observed for this action.

### `settings-downloads` — Open Settings > Downloads

- **Gap score:** 0 | **Web:** ok (2 req, 0 ev) | **Native:** ok (4 req, 0 ev)

No RPC or push-event gaps observed for this action.

### `settings-library` — Open Settings > Library

- **Gap score:** 0 | **Web:** ok (2 req, 0 ev) | **Native:** ok (4 req, 0 ev)

No RPC or push-event gaps observed for this action.

### `settings-music` — Open Settings > Music

- **Gap score:** 0 | **Web:** ok (2 req, 0 ev) | **Native:** ok (4 req, 0 ev)

No RPC or push-event gaps observed for this action.
