# Discord, remade (3.26)

Owner, 2026-11-10: "redesign the whole ux and layout", "it just has to feel familiar not exact", "don't forget about the ux
experience and people coming over from discord", "you still need server settings, moderation stuff all of that", "we can
make links look really nice, videos and photos ... think of every avenue, every feature".

This is the design every part of the Discord room is built to. Mock-ups: `..\exo-design-system\launcher-mockups`
(boards Discord and DiscordFriends; `tools/rooms5.mjs`). Styles: `ui/src/styles/discord-home.css` (frame, list),
`discord-now.css` (Now), `discord-chat.css` (the conversation), `discord-media.css` (links, pictures, video, files),
`discord-pages.css` (voice, threads, forum, requests, shop, settings).

## What changes, and why
1. **It opens on Now.** Discord makes you click round servers to learn where everyone is. Now shows the voice rooms your
   friends are in (Join is one press), the friends playing or listening to something, and what is unread (conversations
   and channels, newest first, with Mark all read). Friends is a place of its own, the list's second row with how many
   are online: Online, All and Pending, a small search and Add friend. (A place saved as `friends` by an older build opens
   Friends; the room lands on Now.)
2. **One list, with names, and it never changes shape.** Now, Friends, Message requests while any wait, your eight
   newest conversations (each with its last line; the one you are in shows even when older; the rest open in place under
   one quiet All conversations row with the count, so the servers stay near the top), then your
   servers under their heading (its + is Add a server) as rows (picture, name, who is in voice, mentions). One server
   is open in place on every page: its channels sit indented under its row, the other servers below it, so another
   server is one press away as it was on Discord's rail. It is the server you are in, and it stays open while you are on
   Now, Friends or in a conversation; its row closes it, a closed server's row opens it at its last channel, and the open
   server is remembered across restarts. (The first build collapsed the servers into a strip of pictures inside a
   server; the owner chose the mock-up, where the list is the same on every page: `launcher-mockups/shots/Discord.png`
   and `DiscordFriends.png`, 2026-11-10.)
3. **The conversation is one column you can read**, centred, at most 860px. People are on demand: a pill of faces in the
   head opens the member list; it is not a column that is always there.

## What stays, so it is familiar
Servers in your own order; `#` channels and categories; voice channels with who is in them; a face with a status dot;
unread is bold and a mention is a red count; the server's name opens the server's menu (invite, settings, notifications,
privacy, leave); right-click menus on servers, channels, messages and people; Discord's keys (Ctrl+K, Alt+Up/Down
channels, Ctrl+Alt+Up/Down servers, Escape marks read, Ctrl+Shift+M mute, Ctrl+Shift+D deafen); you at the list's foot
as one line (your face with its status dot, your name, then mute and deafen); a call's controls there and in the top bar (a call sits LEFT of the Rooms, the song RIGHT).
Nothing Discord can do is removed: server settings, roles, moderation, AutoMod, audit log, onboarding, events, threads,
forum, polls, soundboard, Go Live, the shop and message requests all keep their entry points.

## The look (all of it)
- Open, as the Library is: pure black, no panels, no rule under a head, no line between columns, no divider between rows.
  Columns are told apart by space and type.
- The only outlines are things you act on or objects: a field, a button, a card, a capsule. One line: `--exo-line`,
  `--exo-hair` wide (`--glassy-ring` as a shadow, or a 1px border).
- What is chosen is never outlined: a pill in a group is white with black words; a row is full ink beside a white marker.
- A place is a title (`--exo-type-title`, 20px). Search is one small pill or a glyph at a head's end, never a bar across.
- No explanatory sentences. Labels are one to three words, sentence case. Tokens only, no literal greys.
- Every avatar is round, a letter in a ring when it has no picture.

## Every part
| Part | Design |
| --- | --- |
| Message | Face 40px, name and time on one line, words at the reading size; a run from one person within 7 minutes shows the face once. Hover lifts the row and shows its tools as a small capsule at its top right (react, reply, more). |
| Reply | A quiet line above the message: a small face, the name and the first line, muted; pressing it goes to the original. No spine line drawn down the side. |
| Reactions | Pills (emoji and count); yours is white with black figures. One "add" glyph after them while the pointer is on the message or focus is in it, opening the same picker as the message's react tool. |
| Link | One card, the card's corner, the one line: the site's name small and muted, the title in ink (two lines at most), a description (three lines at most), and the picture large under them (16:9, rounded), or small at the right when it is a square icon. No colour bar. The whole card opens the link. |
| Video link (YouTube, Twitch clip, an .mp4) | The picture with one round play button at its centre and the length at its bottom right on a dark plate; it plays in place; the title and channel sit under it. |
| Post (X, Bluesky) | The author's face, name and handle, the words at the reading size, its pictures as a gallery, the time. |
| Invite | The server's picture, name, how many are online, and Join (the white button); Joined when you are in it. |
| A game or store link | Exo's own card: the game's cover, name, and Play, Install or View in Store. This is what Discord cannot do. |
| Picture | Its own shape, the card's corner (14px), no border, at most 420px tall and 520px wide. Several: a gallery (2 side by side; 3 one tall and two stacked; 4 a square of four; more a grid with "+N" on the last). A spoiler is blurred with one word on it. Pressing one opens the viewer. A picture, GIF, sticker or video that would not load is never an empty box: it is one line in the file card's family, a glyph and its name (or "GIF"), opening the original; in a gallery it fills its cell. |
| Viewer | The picture on black at its full size, the room dimmed behind; arrows for the next and previous in the message, its name and Open original and Save at the foot; Escape closes. |
| Video file | As a video link; its controls are a single bar on a dark plate at the foot that shows on hover (play, time, a scrubber, volume, full screen). |
| Audio and voice message | One card: a round play button, the waveform as bars in ink (played part white, the rest muted), the length. |
| File | One card: a glyph for its kind, its name, its size, and one download glyph. A text file shows its first lines in the card and opens to all of it. |
| Code | A block in the mono face with the one line and the small corner, a Copy glyph at its top right on hover; inline code is ink on no fill with the one line. |
| Poll | The question as a small title, each answer a row that fills with its share once you vote (yours white), the count and the time left under them. |
| Sticker, GIF, big emoji | No frame; a GIF has a small "GIF" plate at its corner. |
| New messages | One red label in the flow where they start ("10 new messages") with a quiet Mark as read beside it while any are unread. The small pill under the head shows only while that label is off screen, to jump there; it stands clear of the list, which fades out under it, so a date label never meets it. |
| Date | Between days: a quiet label at the centre ("November 9, 2026"), no line either side. |
| Message box | A pill at the conversation's foot: add, the words, then GIF or sticker and emoji as glyphs. A reply or edit shows as one line above it inside the same shape. Attachments wait above it as small cards with a remove glyph. |
| Head of a conversation | The title (`#` name, or the person and their status), then one muted line: the server's name and the topic in a server; in a conversation what they are doing ("Playing VALORANT", "Listening to Get Lucky") or else their status in a word or two ("Online", "Do not disturb"). At the end: the faces pill (up to three faces and the online count; it reads the server's people once if the room has not yet), search as a glyph that becomes a field, and one More glyph holding Threads, Pinned messages and Notification settings (a tool that is on carries the menu's check). A conversation keeps Call and Video as glyphs and holds Pinned messages and Add friends in More. The Inbox glyph ends the row. |
| People | On demand: a column that slides in beside the conversation when the faces pill is pressed, grouped by role as Discord has it, and remembered per server. When the page is too narrow for the column beside a readable conversation, the pill stays and opens the same people as a panel over the page's right side, which Escape or a press outside closes. |
| Profile | A card: banner, face, name and handle, status and what they are doing, roles as pills, connections as rows, a note field; Message and More at its head. For a conversation's person it is the same card in the people's column (their width and place; the conversation ends where it starts), shown until its head toggle hides it; without room for the column the toggle stays and opens it as the same panel over the page's right side, which Escape or a press outside closes and which is not remembered. |
| Voice room | The people as large tiles that fill the page (a face, the name, a green edge while speaking, a picture when they share a screen or camera); the controls as one capsule at the foot (mute, deafen, camera, share, soundboard, leave in red). The chat of the room opens beside it on demand. |
| Go Live / a stream | The stream fills the page, the people a row of faces under it; the viewer count and quality on a plate at its corner. |
| Thread | Opens beside the conversation as its own column with a Back glyph; a thread's row in the list is indented under its channel, the thread glyph one step in from the channel glyphs. |
| Forum | Posts as cards in a grid (title, first line, a picture when there is one, replies and time); tags as pills at the head; New post is the white button. |
| Events | Cards: a date tile, the name, where, and Interested. |
| Search | The glyph in the head becomes a field with Discord's filters (from, in, has, before, after) as suggestions; results replace the conversation with a Back row. |
| Friends, Requests, Shop | Friends and Message requests are rows at the list's top under Now (its glyph a lightning bolt; requests only while any wait); a conversation's row is open and its name plain (no colour or nameplate; those show on the person's profile, the members and messages); the Shop is in your menu at the list's foot. Their pages are in the same open style. |
| Server menu and settings | The menu on the server's name. Settings open as a sheet shaped like Exo's Settings (pages down the left, rows on the right): Overview, Roles, Members, Invites, Emoji and stickers, Moderation (AutoMod, audit log, bans), Community (onboarding, rules), Integrations. Rows are a name and a control, with no explaining lines. |
| Moderation in place | On a member or message: Timeout, Kick, Ban, Delete, in its menu, each asked once in place (never a second window). |
| Notices | Exo's own (bottom right). A mention while you are elsewhere: the face, who and where, the line, and Reply. |
| Sign-in | The code to scan on black, one line of what it is, and the risk note Discord's terms require. |
| Empty and failed | One short line and one button where one helps ("No friends here yet." "Discord did not answer." Try again). |

## One with Exo
Owner, 2026-11-10: "since we will be making it our own chat app and basically competing with discord make it feel one with
the ecosystem". The room is not a Discord skin inside a launcher; it is the launcher's chat, and it knows what the rest of
Exo knows.
| Where | What it does that Discord cannot |
| --- | --- |
| Now › Playing | Friends in a game are grouped by the game, with the Library's own cover and Play, Install or Find (`lib/friendsPlaying.ts`: `playingGroups`, `GameLinkContext`). |
| A friend's row | What their Exo is doing to get a game ready (installing 40%, compiling shaders, ready), when they share it. |
| A game or store link in a message | Exo's card: cover, name, Play, Install or View in Store. |
| The message box's add | A capture from Exo's Captures (a clip or a screenshot) beside a file from the PC. |
| Listening | A friend's song opens in Exo's Music; a Jam is joined from its card. |
| A call | Its controls are in the top bar on every room (left of the Rooms), and the game you start is told to the call. |
| The Library and a game's page | Who is playing it now and the voice room they are in, with Join. |
| Notices and Search | A mention is one of Exo's notices; Ctrl+K finds a person, a channel or a server with everything else. |
| The look | The same parts as every room: titles, the white pill, the one line, the Library's cards for a voice room and a game. |

The name: the room is Discord's while it speaks only Discord's network. Exo's own rooms (Play, `docs/architecture/chat-rooms.md`)
are the start of Exo's own chat; when that ships the room becomes Chat, holding both, with Discord as a network you
connect in it. Until then nothing in the room says "Discord" except its name in the top bar and the sign-in.
