# Exo's end-to-end suite

The installed Exo, driven the way a person drives it and checked the way a person would see it. Node 24.19+ (the UI's own minimum, `ui/package.json`), no
packages, Windows with PowerShell 7.

```
node tools/e2e/run.mjs                       # everything (about 30 minutes)
node tools/e2e/run.mjs --suites rooms,stress # some suites
node tools/e2e/run.mjs --skip zoom --quick   # fewer rounds, no restarts as a 4K screen
node tools/e2e/run.mjs --attach --keep       # drive the Exo already running on --port 9339 and leave it there
node tools/e2e/run.mjs --no-post                   # nothing goes to Discord: no test message and no invite in the test place
node tools/e2e/run.mjs --allow-game                # run although a game is playing (its real clicks and window moves may take focus from the game)
node tools/e2e/run.mjs --changed --plan      # which suites the changed files can reach; touches nothing
node tools/e2e/run.mjs --changed             # run only those (changes against HEAD, untracked files included)
node tools/e2e/run.mjs --changed main        # the same against another base
```

`--changed` maps files to suites in `lib/affected.mjs` (tests: `node --test tools/e2e/lib/affected.test.mjs`). It errs
wide: any app change also runs `shell`, and an app file no rule knows is named in the plan instead of dropped. `--suites`
names suites outright and wins over `--changed`.

The report lands in `e2e-reports/<time>/`: `summary.md` (read this), `report.json` (every check with its evidence and
numbers), `screenshots/` and `SHA256SUMS.txt` (`sha256sum -c SHA256SUMS.txt` in Git Bash checks the folder). Each check
is **PASS**, **WARN** (works, but a budget or a soft rule was missed), **FAIL** or **SKIP** (couldn't be tried here,
and why). Exit code 0: nothing failed; 1: a check failed; 2: the run itself couldn't go on.

**Test instance.** With `EXO_E2E_TEST_INSTANCE=1` the run starts a disposable web host through `tools/test-instance.ps1`
instead of the installed Exo: its own data folder under `EXO_E2E_TEST_FOLDER` (default `%TEMP%\exo-e2e-test-instance`,
recreated each launch), its window on the second screen, and a free debug port. The installed Exo is never closed, and its
settings files are neither saved nor restored. The run closes the test instance at the end unless `--keep`. It refuses
without a second screen and while a game runs, like the script does.

## What it checks

| Suite | What |
| --- | --- |
| `shell` | Startup time (from app.log's milestones), a start that reads no room nobody opened, app.log and the console clean, **the screen showing the whole page** (a screenshot of the screen against the page's own picture), the window filling its monitor, the Bar's parts, the window buttons clicked for real. |
| `rooms` | Every room: layout (nothing cut off by the window, no sideways scroll, no control under something), **contrast measured from pixels** (words against what is really behind them, glass and art included), no white block on the black page, pictures loaded, controls named, no console errors. |
| `library` | One Steam app holding bought games (Call of Duty) named by the newest and found by each; every sort, a game's sheet by click and keyboard, its menu, Ctrl+K to a game, a folder made, opened, recolored and deleted, the More menu, Continue, the whole grid scrolled, and a game's Optimize and Frame generation lines as the host reads them (never pressed). |
| `store` | Shelves, the banner's picks, a shelf's arrows, search, a game you own (Library in place of its price, on its card and page), the calendar (days, exact dates only), a game's page with its requirements. Nothing bought or wishlisted; Free on Epic (Epic's own site) is never clicked. |
| `discord` | Signed in, Home (the band, your conversations with you, mute, deafen and settings at their foot, Friends), the band (one chosen pill, faces one size and reachable, a name tip on hover), the chosen row readable on its white pill, the lists reaching their last item, a conversation opening without changing other unread counts, a conversation's reading width, times and one lit path, the test channel opened, a message sent, reacted to and deleted, invite links (live and dead), server pages, Discord's settings as a sheet over the room (each page, the gear, and none left in Exo's Settings), the voice room's head and labelled controls, a profile. Only in the test place, the Exo server: #mod-chat (the test message is deleted even when a step fails), and a call in hangout joined muted and deafened, then left, mute and deafen put back as they were. |
| `music` | Home, search, a playlist and back, the player. Nothing is played. |
| `system` | The PC room, all on one page at 1080p: This PC (its name, Best settings beside it, Windows and parts), every section named on the page at the rooms' title size, Best settings' menu naming its steps with no network row, the card's Graphics menu with the driver's version, each drive with how full it is and its free space (readings only), Network's adapter, link speed and where Exo's settings stand (never optimized), the screen's Display menu with the control panel and its menus (opened, never changed), Shaders' games, the Apps sheet, and Shaders' engine leaving once the board is off screen. |
| `devices` | Exo Mouse loads, finds the mouse, wears Exo's look. |
| `capture` | Capture settings from Captures (the menu, then Settings on the sheet, and again after All captures), showing Make recordings smaller as saved. Snip from the title bar's Capture menu, round after round (Exo Capture's overlay is given Return, so each snip is of the screen under the cursor, and goes to the Recycle Bin after): the overlay showing within 150 ms, the editor drawn no larger than it is shown, the page's memory and WebView2's back where they were after, and no `webview-process-failed` in app.log. Notes from the menu (or Captures, then Notes) and New note. |
| `settings` | Every section, every switch in General and Downloads turned and turned back, Text size, Escape. |
| `appearance` | What floats (a game's sheet, Ctrl+K, a menu, Settings, Discord's pages) read as the rooms do; Liquid Glass only on what floats and the controls (a page-sized blur fails) and the Bar hides a white sheet slid under it. |
| `polish` | A design review's eye on every room, what floats over it and Discord's Home, server and conversation: no code leaking into words, no control under 16 px (under 24 warns), controls in a row on one middle line, nothing moving after the page settles, glass on everything that floats; warnings for type off Exo's scale, corners off its set, stray line greys, blurry pictures, keyboard focus you can't see, and one kind of control at two heights. Discord's panels share one corner, edge and gap. |
| `layout` | Every room and what it opens (a game's sheet, menus, Ctrl+K, each Settings section, a Store page, Music's search, a playlist and panels, each page of PC, Discord's channel with its member list, threads, pins, inbox, server pages, a forum and each settings section), then at three window sizes and the largest text size (and `zoom` as on a 1440p and a 4K screen): no two controls over each other, no control cut by what holds it, no words cut off (`lib/page.mjs` › `layoutFaults`, also part of every full look). Each fault names the element's path and box and is cropped into `screenshots/`. |
| `motion` | Frames timed against the screen's own rate while Exo moves (rooms, a sheet, menus, Ctrl+K); no animation over 0.7 s; Reduce motion stops the movement. |
| `perf` | Room changes (p95), switching into Discord, Library wheel scrolling, the page's memory over rounds of use, idle CPU of Exo and its WebView2, host call times. |
| `stress` | Rooms clicked faster than they open, sheets, Ctrl+K and menus opened and shut over and over, the window resized again and again; then Exo must be whole, error-free, and not bigger. |
| `zoom` | Exo restarted as on a 1440p and a 4K screen (`EXO_PAGE_SCREEN`): the whole page on screen, real clicks landing on the tabs, a smaller window; then each monitor. |

## Rules it keeps

- Never during a Discord call (it would end it) or while a game runs: it stops before starting.
- It closes Exo normally, never kills it, and starts it with a loopback-only debug port in its own environment.
- `settings.json`, `web-client-preferences.json` and `window-placement.json` are saved first and put back byte for byte
  at the end (`lib/exo.mjs` › GUARDED), then Exo starts again normally.
- Nothing is bought, installed, played aloud or posted outside the Discord test place; real mouse clicks and window
  moves happen on Exo's own window only (keep hands off the mouse while `shell`, `stress` and `zoom` run).
- Nothing personal reaches the report: e-mail addresses and long ids are masked.

## How it is built

`run.mjs` runs the suites in `suites/`. `lib/check.mjs` records checks and writes the report; `lib/exo.mjs` starts,
closes and reads Exo (its log, its host); `lib/win.ps1` (through `lib/win.mjs`) is the screen and the real mouse:
captures of what the screen shows, clicks, window placement, monitors, process CPU and memory; `lib/page.mjs` holds
the probes that run inside the page; `lib/contrast.mjs` measures words against the pixels behind them;
`lib/image.mjs` compares the screen with the page; `lib/kit.mjs` is what every suite uses (going to a room, closing
what floats, the full look at a view). `cdp.mjs` is the DevTools client.

A new check goes in the suite it belongs to: `run.check(suite, id, name, async (c) => [...failures])`, with
`c.warn`, `c.skip`, `c.metric(name, value, unit, budget)`, `c.shot(label)` and `c.evidence`. Look at the screen, not
only the page: `screenMatchesPage` (suites/shell.mjs) and `t.win.capture` are how.
