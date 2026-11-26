# Exo's own update: checks on the installed app before a release

Exo Setup's update mode (`setup/src/main.cpp`, `/UPDATE`) and the two Updating Exo windows have no automated test (C++ with
no test project, and real windows). Run these on a spare Windows user or a VM with the build that is about to ship
installed, and a staging copy of the release on an update address the build can reach. Each step says what must be seen;
anything else is a fail. Write down the date, the build and the result of each step in the release's notes.

## Setup

- Install the release candidate (call it N) over the version before it (N-1) only through Exo's own update, never by
  running the installer by hand, so the path tested is the player's.
- Keep `%LOCALAPPDATA%\ExoLauncher\logs\app.log` open in a viewer that follows the end.
- Have a small game installed that runs windowed (any store), to stand in for "a game is running".

## 1. Update now with no game

1. Open N-1. The update sheet says Update available and Exo Launcher N. Press Update now.
2. Seen: the bar fills with Downloading, then Exo closes, and within a second Exo Setup's small window says Updating Exo
   with its bar moving. There is no moment with no Exo window on screen for more than about a second.
3. Seen: the new Exo N opens by itself, and the Updating Exo window says Exo is updated and goes by itself a moment later.
4. Seen: no Updating Exo window at the next start of Exo (the kept record for N is dropped silently).

## 2. Update now during a game

1. Open N-1, start the windowed game, alt-tab back to Exo, press Update now on the sheet.
2. Seen: the download finishes, Exo N-1 closes, Setup's Updating Exo window shows, and Exo N opens. The game keeps
   running the whole time and is not closed or paused.
3. Close the game. Seen: Exo N is still open and shows the new version in Settings › About.

## 3. A launch while the update goes in

1. During step 1.2 (Setup's window up), double-click Exo's Start menu shortcut three times.
2. Seen: nothing else opens; Setup's window stays; Exo N opens once at the end. app.log of the new build has no second
   start in the middle.
3. During step 1.2 again, open a text file with Exo (Open with, Exo) or a `exolauncher://pc` link.
4. Seen: Exo N opens on Notes with that file, or on the PC room.

## 4. An update that does not take

1. Use a release candidate that is built to fail its start (for example, one whose page never loads), and update to it with
   Update now.
2. Seen: Setup's window says Not updated and "Exo will try the next one.", and goes by itself within about ten seconds; the
   version before opens once (not twice), and Exo says "The update didn't install. Exo will try the next one." once.
3. Seen: the next start of Exo does not offer that version by itself and shows no Updating Exo window; Settings › About
   still offers Install update.

## 5. No working WebView2 in Setup

1. On a VM where Setup's WebView2 cannot start (remove the runtime after Exo is installed is not possible: use a VM image
   without it and a build of Setup that skips its own WebView2 install), run Update now.
2. Seen: no installer wizard appears; the update goes in silently and Exo N opens.
3. Seen, in a build of Setup whose page is made never to send its first message (or with WebView2's page blocked): after about
   ten seconds Exo N-1 opens again and Setup's window closes. Run Update now a second time: Setup does not run twice at once.

## 6. 3.25.0 updating by its own silent path

1. Install 3.25.0 and update it to the release candidate. Every installed copy takes this path first: no Setup window shows.
2. Seen: Exo closes, the new Exo opens by itself, the health receipt arrives (app.log of the new build says the startup
   check passed), and no rollback happens in the three minutes after.

## 7. Offline

1. With the network off, start Exo. Seen: no update notice of any kind.
2. Press Update now (from Settings › About or a kept sheet). Seen: one plain sentence that the update could not be
   fetched, and nothing closes.

## 8. The engine fails before it swaps

1. Hold a file open inside the install's app folder so the engine cannot move the folder, then press Update now.
2. Seen: Exo reopens once (not in a loop). The next start still offers that version and does not skip it, because nothing
   was installed and nothing rolled back; after the third try the kept installer is dropped.

## 9. After an update, the required account sheet

1. Update to the release candidate on a PC that is signed out. Seen: the new Exo opens on the account sheet.
2. Stay on that sheet for over three minutes. Seen: no rollback; Exo N stays.

## 10. A double-click during the three-minute check

1. During the engine's startup check (the first 180 seconds after the new Exo opens), double-click Exo's shortcut.
2. Seen: the new Exo comes to the front; no second Exo and no older one opens.

## 11. A kept update for N when N+1 is out

1. Keep an update for N (step 2), then publish N+1 to staging. Open Exo.
2. Seen: Exo applies or offers N+1 only; the kept installer for N is removed and never runs.

## 12. A kept update when Exo starts with Windows

1. Keep an update (step 2), close the game, sign out and in so Exo starts with Windows.
2. Seen: the update goes in at that start or waits for a game; it never opens a second Exo and never loops.

## 13. The update from an Exo that hosted Lucid

1. On Exo 3.25 or earlier with Lucid on, update to 3.26 or later.
2. Seen: the installer closes the hosted Lucid (`Close-ExoLauncher.ps1`), the new Exo starts without `addons\lucid`, Lucid's settings show on the audio page as Exo EQ, and no Lucid runs from Exo's folders.

## 14. Not enough free space

1. On a VM with the install's drive nearly full, press Update now.
2. Seen: one plain sentence that there is not enough room, naming nothing else; no download starts and nothing closes.

## 15. The release script against staging

1. Run `tools\publish-release.ps1` against the staging bucket as a dry run. Seen: it lists what it would upload and uploads
   nothing; it checks the served files before it would upload `latest.json`.
2. Run it again with `-Publish`. Seen: the files, then `latest.json`; run it a third time for the same tag and it refuses
   to overwrite that tag's files.
