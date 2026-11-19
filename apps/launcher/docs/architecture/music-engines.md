# Music engines

Exo's Music room is Exo's own player (`ui/src/components/music/`, one adapter per service in
`ui/src/lib/musicAdapters.ts`). Behind it, the chosen service has one hidden engine, chosen for what
the service can do there. It starts with Exo (the room is preloaded behind the splash); switching
services closes the previous engine. An idle engine is suspended, so waiting costs memory, not
processor time, and after 15 idle minutes it is closed; opening Music starts it again (`MainWindow.Music.cs`).

| Service | Engine | Why |
|---|---|---|
| Spotify | open.spotify.com, hidden (`Helpers/SpotifyNative.cs`, through the page's own platform APIs) | Exo's own player, nothing to install. Exo signs in through Spotify's own page. Exo never starts, hosts, installs or prompts for the Spotify desktop app, and one the user runs is closed whenever Exo starts Spotify's player (WM_CLOSE, then the processes that stay after 3 s are ended; `Helpers/SpotifyApp.cs`), because a running app takes the Connect device and the player's play is refused. Spotify documents Jam and Lossless for its apps and does not mention the web player; Exo reaches Jam through the page's own social-connect module (checked in code only), and the page's audio settings answer nothing (the Sound panel reads that as none). |
| Apple Music | music.apple.com, hidden (`Helpers/AppleMusicNative.cs`, through the page's own MusicKit instance) | The Windows Apple Music app (AppleInc.AppleMusicWin, checked at 1.1540) is a native WinUI app on Apple's media stack with no reachable API for the library, queue or quality. Its link protocols (`music://`, `itmss://`, music.apple.com links) didn't even start it from outside, and it publishes no media session until something plays. So Exo can't drive it reliably, and the web engine stays: AAC at 256 kbps (High) or 64 kbps (Standard), no lossless or Dolby Atmos. Revisit if Apple adds automation to the Windows app. |
| YouTube Music | music.youtube.com, hidden (`Helpers/YouTubeMusicNative.cs`) | There is no desktop app and no lossless; the web page is the best engine. |

One client: the service is chosen in Settings › Music (the room's screens without a player link there). Web players share Exo's embedded profile, so each keeps its sign-in; one engine runs at a time. Exo never decrypts audio itself: protected streams play through each service's licensed player, in the page's Widevine module or in Spotify's app.

Shared on top, for every service: one queue, Now Playing, library views, the bar's mini player, and
Exo's own sleep timer (the player's Sound panel, `SoundPanel.tsx`).

Why no native (browserless) engine, what Spotify Jam and Exo Jam share, and the licensing and ban-risk research: `music-native.md`.
