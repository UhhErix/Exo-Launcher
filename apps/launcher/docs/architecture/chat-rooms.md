# Chat rooms: Exo's own face on Discord

> **The room was remade in 3.26** (`discord-remade.md`): the band of Home, Messages and Servers described below is now one list (Now, Friends, your conversations, your servers as rows). Where this page says the band or Home, read that list and Now; Play, Games, Party and Clips are unchanged.

Exo's Discord client stays the transport: the person's accounts, servers, DMs, voice, and what Discord already knows about people. Only the face changes, a room at a time, and the Discord layout stays beside it as the fallback until a room does its job better. The rule for every room is the owner's: **simple purpose, simple design; no tagline, no helper text.** A room is judged by whether it says what someone came to see at a glance and lets them do the one thing it is for.

In the chat, a **place** is somewhere people are (a server, a conversation, a call) and a **room** is a purpose-built view of what the person and their friends do together. Exo's Bar has rooms too (Library, Store, Discord...); inside Discord, the word is not shown anywhere: the labels are Games, Party and Clips.

## What makes it Exo, not Discord

1. **Places, not a rail of servers.** Already true in the band (Home, Messages, Servers, then what you pinned). A server becomes a place with a purpose: its voice channels read as gatherings (who is in, what they play), its text channels stay Discord's. Nothing is added to a place that Exo can't name the job of.
2. **Calm by default.** No count on anything that is not addressed to you. Mentions and friend requests are counted (they ask something of you); nothing else is: not unread channels, not clips, not people online. A room's activity is a word (Playing, Compiling shaders, Ready) or a green dot while it is live, never a number that grows. Nothing to catch up on, nothing drawn; with no one around, one quiet line.
3. **Status that means something.** One short vocabulary, each word from something Exo or Discord actually knows (table below). Never invented ("last seen", typing in a room you are not in), never a guess at what a friend is doing.
4. **Exo's own tools in the chat.** A game's room carries the lines of the game page (Optimize, Shaders), a clip's one press is Exo Capture's, a party's ready check is Exo Shaders' and the Library's. What only Exo has is what makes the room Exo's.
5. **What exists only for other Exo users goes over the friend link, never through Discord** (`discord-client.md`, Share my games and the friend link), each new kind of information a switch the person turns on, off until they do.
6. **Catch me up.** A single block, on coming back after a while (or pressed), that says what changed while you were away from what Exo already holds: your mentions and the conversations that are waiting (Discord's own marks), who is in voice now, which friends started a game since you looked (this session's events, in memory), events starting soon. Deterministic, no model, no message text read for the summary and nothing written to disk about other people. Not built in this slice (see Phases).

## The status vocabulary

| Word | Means | From | Who sees it | Default |
| --- | --- | --- | --- | --- |
| Playing | on a game now, how long, what they do in it | Discord's activity; for friends on Exo, the game they opened from Exo (friend link, `game`) | friends | off until Share my games is on |
| Listening | a song, listen along with one press | Discord's Listening activity; for a call, Exo's call relay | friends / the call | on (existing) |
| In voice | in a voice channel or call | Discord's voice state | people who can see the channel | Discord's |
| Streaming | live | Discord's activity | friends | Discord's |
| Installing 60% | getting the game (in tens) | the Launcher's own install events, over the friend link (`prep`) | friends on Exo | **off** until Share when I'm getting ready is on |
| Compiling shaders | Prepare is building this game's shaders | `ShaderPrepare` state, `prep` | friends on Exo | off, same switch |
| Ready | installed and prepared, said for 30 minutes | the same events, `prep` | friends on Exo | off, same switch |

A friend without Exo, or with the switch off, shows only what Discord says: on the game, in voice, online.

## The rooms

Each room: what it is for, the data Exo already has, what it maps to on Discord, what needs new host work, the privacy defaults, and what is not built. The first three are built (Play, below). The other two are decided but not started.

### Games (built)

- **For:** one game, and the people on it. Its art, your time and achievements, what your own PC has done for it, its players now, its latest clips.
- **Exo has:** the Library (title, art, time played, installed), `game.settings` (Optimize's standing, window mode), `shaders.status`, `game.extras` and `game.achievements` (achievements), `capture.list(gameId)`.
- **Discord maps to:** friends' Playing activity grouped by game (`playingGroups`), their details ("Quick match"), whether it came through Exo, and the voice rooms they are in.
- **New host work:** none. The room reads the lines the game page already has (`OptimizeLine`, `ShaderPrecompile`, now with `useAchievements` taken out of `useGameDetails`) and presses them the same way. Its Shaders line opens PC where the game is compiled (`exo-open-pc`).
- **Privacy:** your side never leaves your PC; friends show only what they already show Discord (plus `prep`, if they turned it on). Nothing is sent to anyone.
- **Not building:** a friend's playtime, achievements or library (Discord doesn't send them and Exo doesn't ask a store for another person's), leaderboards and rankings, "who owns this" for people who aren't playing it, a text channel per game (a server's own channels do that), a game page of its own for games you don't have.

### Party (built)

- **For:** who is ready to play one game together, and one press to start your side. The people are the friends playing the game or getting ready for it, you first, with the voice room most of them are in.
- **Exo has:** the Library (installed or not, running), Prepare (a game's shaders being built), install progress, the Play or Install press the Library already has, `shaders.status` for your own line.
- **Discord maps to:** presence (playing), `discord.voiceNow` (which room holds the most of them), `voice.join`.
- **New host work (done):** the friend link's second section, `prep` (`FriendPrep.cs`: its wire form and bounds, `DiscordPrep`: what this Exo says and when, `DiscordSession.Prep.cs`: the events and the switch), `DiscordCache.SetExoPrep` and the `prep` field of friends and presence, `ShaderPrepare.Running`, the setting `DiscordReadyShared` and `discord.shareReady`. Launch together is page logic (`launchPlan`): join the party's room if there is one you are not in and can join, then the game's own Play or Install.
- **Privacy:** **off until the person turns on Share when I'm getting ready** (Settings › Discord › Privacy), and it reads off while Share my games is, since it is the same link. Only titles Share my games may name (never one you hid or added by hand), installs in tens, only to friends with a key, sealed, never through Discord, nothing logged, "ready" goes after 30 minutes or when the game starts. `PRIVACY.md` says so. Nothing is sent to anyone by pressing Launch together: it starts your game and your voice, never theirs.
- **Not building:** invites or ready checks sent as messages, a party that lives on a server, matchmaking, ranks, starting a game on someone else's PC, reading a friend's library to know who has the game, polling any game's process or files to learn how ready someone is. Exo Shaders' own queue (the page's Compile) is not a source yet: the engine sends its state snapshots only while its page is shown (`ui.visible`), and their shape belongs to the engine, so a compile from there is not said. Reading it needs the engine to report progress to the host on its own, which is the engine's change.

### Clips (built)

- **For:** your captures from Exo Capture, newest first, each one press from a friend. A picture opens it in Captures to watch, trim or edit.
- **Exo has:** `capture.list`, `capture.saved`, `capture.shrunk`, Shrink, thumbnails, duration and size, `openCaptures`.
- **Discord maps to:** an attachment on a message in a DM or group DM. The conversations offered are the people on that game first, then your latest.
- **New host work (done):** `discord.shareClip` (`WebHostBridge.DiscordSettings.cs`): the host finds the file by its capture id, so no path crosses the bridge, `ClipShare.Check` (mp4 or png, within 25 MB, a clean short name) decides, and the file is posted through the same upload the composer uses. A clip over 25 MB offers Shrink to share, which is Exo Capture's own Shrink.
- **Privacy:** nothing posts until Share, a person, and a second press in place ("Send?"): a message can't be taken back. The file is read into memory for the post, never written elsewhere, and goes only to Discord's upload. DMs and groups only, not a server's channel: a channel is many people, and wants a channel picker and its own confirm first.
- **Not building:** automatic uploads or auto-shared highlights, one-press sharing into a server channel, a feed of friends' clips (it would read every message for attachments), highlight detection, transcoding beyond Shrink, a link to a clip on Exo's own server (Exo has none).

### Jam (not built)

- **For:** listening together across music services, with lyrics and a shared queue.
- **Exo has:** listen along (`listenAlong.ts`, `listenSync.ts`: song, position, drift correction, the user's own service finds it), Discord's Listening activity and the call's song relay, the Music room's player and lyrics, the Jam box for Spotify's Jam.
- **Discord maps to:** the Listening activity and, in a call, the encrypted call relay.
- **Needs:** the shared queue. Spotify's Jam owns its queue and no other service has one, so across services it would be an Exo list (up to ten items: title, artist, each service's id), a leader, and a new `jam` section on the friend link or the call relay, every listener resolving each item on their own service (`MusicMatch.cs`). That protocol wants two PCs to try it on and a decision about who may add to it.
- **Privacy, when built:** per jam and by joining only, nothing standing; only titles and artists, as listen along sends today.
- **Not building:** audio relayed between PCs (each plays its own copy on its own account), writing into anyone's Spotify queue, a queue that survives the jam. Why not first: Home's Listening block already does the one-press part, and the queue is the large piece.

### Setup (not built)

- **For:** a friend sends their settings for a game; you apply them in one press.
- **Exo has:** Best settings and Optimize (`GameSettings`: Exo's values for this PC written into the game's own file, with a put-back), the upscaler and HDR choices, Exo Mouse's profiles.
- **Needs:** a decision on what a shared setup is. Optimize's values are derived from your PC, not authored, so sharing them tells a friend about your hardware and fits theirs badly. What transfers well is a game's **recipe**: the named choices (a game's `GameSettingsGroup`s, mouse sensitivity and DPI, a crosshair code) with values within each one's own bounds, sent once, sealed, to one friend, and applied only through the writers Exo already has, with Undo.
- **Privacy, when built:** a one-time message the person sends to one friend (not a standing publish), showing what it holds before it goes; never any hardware ids or paths; applying shows what changes first.
- **Not building:** importing a friend's config file (a path and arbitrary content in the one place Exo writes to a game), driver, Windows or BIOS tweaks (PC-specific and able to brick), automatic apply, anything for a game with anti-cheat: Riot titles are never modified, and no anti-cheat is touched.

## How it sits on the Discord layout

- **Opt-in:** Settings › Discord › Chat › Show Play (`playRooms` in the device's Discord prefs, off by default) adds one place, Play, after Servers in the band. Turning it off while it is open returns to Home. Home, Messages and Servers are unchanged, and so is everything under them.
- **Frame:** Play uses Home's own (`.exo-dc-friends`): the title line with its tabs (Games, Party, Clips) in the same glide, the page's scroll and its fade, the call's dock room. A game opens inside Games with a Back to Games on its title line (and Escape).
- **States:** a room waits as skeletons in its own shape (`PlayWait`), says nothing-here in one line (no games, no clips, no party; Exo Capture missing says so with Open Captures), says a failed read with Try again, and never shows a placeholder as content.
- **Keyboard and motion:** every control is a native button in reading order, menus take the arrows (`useChoiceMenu`), Escape goes back from a game, focus is the 2px ring (an outline outside art). Under Reduce motion a card does not lift or give way, the live dot is still and the rooms' arrival is the global fade.
- **Reuse:** `Avatar`, `ExoMark`, `HeroWash`, `GameLine` (via `OptimizeLine` and `ShaderPrecompile`), `Wait`, `WorkingWord`, `ErrorNote`, `useChoiceMenu` and its in-place confirm, `.exo-set-btn`, `.exo-shelf-chips`, the Lobby's place glass and green dot. What it adds is `lib/playRooms.ts` (the pure part, tested), `components/discord/play/` and `styles/discord-play.css`.

## Phases

1. **This slice:** Play with Games, Party and Clips, the `prep` section and its switch, `discord.shareClip`.
2. **Home leads and catches you up:** the Lobby already is Exo's own view; add Catch me up above it (deterministic, from what the session holds), and the status words on the band's chips. Jam as a place inside Play once its queue has a protocol.
3. **Server places:** a server's page keeps Discord's channels and members, and gains its gatherings (voice channels as parties, events as the calendar of the place), still calm: no counts.
4. **Retire only what a room replaces,** one at a time and only with the owner's word. The conversation view (messages, threads, search) stays: nothing in Exo draws a better one yet.

## Checked

`FriendPrepTests` and `ClipShareTests` (host; wire bounds, the tracker's steps, the cache, the gate, the file rules), `ui/test/playRooms.test.ts` (parties, readiness, launch plan, share targets, ordering), `npm run check` and `dead`, the Play views on the browser mock (`?dc=empty`, `?clips=none`, `?clips=fail`, `?hold=...` for the waits). **Not seen** on a real account or between two PCs: the relay carrying `prep`, a real clip posting, Exo Capture's own thumbnails, Optimize on a real game from this room.
