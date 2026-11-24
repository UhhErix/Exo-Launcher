# public-fix: tabs unclickable after a game closes

Status: **not fixed, no PR.** The bug reproduced once under real mouse input (0 of 6 tabs responded). The page's own hit-test was correct at that moment, so the click is being taken by the native title-bar caption on the host, not by the page. I could not reproduce it again on demand, and I could not drive a real game session on a signed-out test instance. No code was changed. Tour shortcuts and keyboard issues are out of scope.

## What was verified

- **Page hit-test is correct when it fails.** With the failing state live, `document.elementFromPoint` at the Store tab's centre (CSS 611,26) returned the Store `BUTTON.exo-nav-btn`. The page's measured tab holes (CSS y 11 to 41, x 477 to 1115) match the tab positions.
- **The page does not re-send its layout.** After dispatching `resize`, the page sent zero `shell.setChromeLayout` messages. `ui/src/components/shell/useShellLayout.ts:104-106` de-duplicates on `last`, so the page believes the host already has these holes. The host's applied caption therefore differs from the page's holes, or the native hit-test is stale.
- **Click results.** Failing run: 0 of 6 tabs responded, with the room unchanged. Control runs (maximize, then place smaller, no minimize) on the same instance: 6 of 6 each time.

## Reproduction log (test instance, `EXO_E2E_TEST_INSTANCE`-style disposable data dir)

| Run | Steps | Result |
|---|---|---|
| 1 | maximize, place 2080,60 1600x960, real-click tabs | 6/6 |
| 2 | fresh instance: maximize, minimize 45 s, restore, place | 6/6 |
| 3 | after run 2 and other activity: maximize, minimize 45 s, restore, place | **0/6** |
| 4 | same instance, no minimize (control) | 0/6 (inherited state) |
| 5 | fresh instance: control, then minimize 45 s cycle | 6/6, 6/6 |
| 6 | minimize 5 s cycle x3, and a capture before clicks | 6/6 each |
| 7 | replay of game-run, capture, minimize cycle | 6/6 |

Run 3 is the only failure. Every later attempt passed, so the trigger is not yet known.

## Blockers

- **Signed-out test instance.** `ui/src/components/account/AccountShell.tsx:49` shows the account sheet while the PC is signed out (`ui/src/lib/accountPrompt.ts`, `accountGateDue`). The sheet has no skip. With it up, the Home hero's Play button is not rendered, so no game can be started through the UI.
- **Launch guard.** `tools/e2e/cdp.mjs` `refuseLaunch` makes `shell.click` refuse Play buttons. A real mouse click (`tools/e2e/lib/win.mjs` `click`) is the only route, and it needs the Play button visible.
- **Stand-in game.** A copy of `notepad.exe` in `<DataDir>\Games\PfStandIn\` is listed by `ExoLauncher/Adapters/LocalAdapter.cs` (scan roots at ~line 91), so it is usable once the account gate is out of the way.
- **Not done:** signing in with a real account, or copying the owner's session into the test data dir. Both need the owner's approval.

## Candidate root causes (unverified)

1. **`ExoLauncher/MainWindow.Caption.cs:102-110`** (`UpdateCaptionDragRegions`). Only the Maximize region is cleared before it is set (line 109). Caption and Passthrough are set on top of whatever is there. The comment at lines 107-108 records that after an activation or a minimize the native hit-test can stop answering while `GetRegionRects` still lists the rects, and that re-setting identical rects does nothing. If Caption or Passthrough goes stale the same way, the tabs (holes) fall back under caption. Re-running `UpdateCaptionDragRegions` on Activated (`MainWindow.xaml.cs` ~131-138) or RegionsChanged (`MainWindow.Caption.cs:40`, `RestoreCaptionIfReplaced` at line 74) would set identical rects again and change nothing. This fits the symptom: a window restore or fullscreen triggers a different set of rects that clears it. **Not confirmed.**
2. **`ExoLauncher/MainWindow.Caption.cs:80`** (`RestoreCaptionIfReplaced`). It returns early when the current caption equals `_appliedCaption`. So it cannot repair a region that is listed correctly but hit-tested wrongly.
3. **`ExoLauncher/MainWindow.Caption.cs:102`**. `if (caption.Length == 0) return;` leaves the previous regions in place. Not seen in the runs.
4. **`ExoLauncher/MainWindow.PageScale.cs:47,63`**. `PageZoom` is only refreshed when the zoom changes, and `Ui/CaptionDragRegions.cs:33-58` scales every hole by it. A stale zoom would shift holes, but no shift was measured.

## Suggested next step

Clear Caption and Passthrough before `SetRegionRects` in `UpdateCaptionDragRegions`, as is already done for Maximize, and add a regression test. For a fail-before check, either:
- a signed-in test instance, with the sequence maximize, minimize, restore, restore smaller, real-click tabs, or
- a host unit test that cannot exercise native regions, so it would only cover the pure part.

The fail-before step is the blocker. Until the failing state can be reproduced on demand, the fix cannot be verified.

## Native Rune UI (`feat/native-polish-2`)

- The Rune UI has no `InputNonClientPointerSource`, no region rects and no hole mechanism. A search of `apps/launcher/native-ui` found none.
- Its title bar drags through `.window_drag()` at `crates/exo-ui/src/shell.rs:449`, with the bar component in `crates/exo-kit/src/components.rs:48`.
- **The WebView caption-hole cause does not apply to Rune.** Whether Rune's own drag or hit-test has a similar stale-state issue was not inspected. Nothing in `native-ui` was edited.
