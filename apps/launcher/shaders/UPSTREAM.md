# Exo Shaders and SCSKiller upstream

Exo Shaders is Exo's own fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) (GPL-3.0-or-later with the
section 7 permission in `LICENSE-EXCEPTION.txt`). It lives here as a source copy, not a submodule: Exo changes it freely
(engine logic, game support, reliability, speed), and takes SCSKiller's releases in while that stays cheap.

## Where it stands

| | |
| --- | --- |
| Upstream base | SCSKiller **v1.4.6**, commit `c6422e3cf0033cef101b8346697dd493c7685776` (2026-11-06) |
| Engine version | `1.4.6-exo.1` (every assembly, `engine/build/publish-service.ps1`) |
| Imported from | the exo-shaders repository at `af4f4f645ff9170c845d9c7c6d0f6539914963b6`, the source of the engine Exo Launcher 3.25.0 and 3.26.0 shipped (published as `exo-shaders-3.25.0.tar.gz` at getexo.app/source) |
| Newest upstream | v1.4.8 (`c5badf849a17756401b42f78c3950c43455a1968`, 2026-11-15), not merged yet: see Next |

Left out of the copy, because Exo neither builds nor ships them: upstream's WinUI app (`src/SCSKiller.App`), its Avalonia
app (`src/SCSKiller.Ui`, `src/SCSKiller.Ui.Models`), its solution, Linux packaging, release signing (`tools/release-sign`),
the Linux host (`tools/scskiller-host`), GitHub workflows and its CODE_SIGNING_POLICY, CONTRIBUTING, SECURITY and the fork's
PRIVACY (Exo Launcher's PRIVACY.md covers the add-on). A merge reports these as modify/delete: keep them deleted.

What is Exo's own is listed in [engine/CHANGELOG.md](engine/CHANGELOG.md). The parts that must survive every merge:

- `App/ExoBuild.cs` and `EXO_BUILD`: the Exo build sends nothing to SCSKiller's servers or Patreon, and keeps the anti-cheat
  module list; Build-Release checks `ExoBuild.Enabled` in the shipped Core.
- No recorder, injection or launch in a game with anti-cheat: no offline or "at your own risk" sessions, whatever
  upstream allows (1.4.8 lets Hunt: Showdown 1896 record at your own risk; Exo does not). `ScsKiller.HostSkips`: Exo
  Launcher's own word on a game (exo.library's `noRecorder`) keeps the recorder out like anti-cheat does.
- Consent first (`ExoConsent`, `RecordOnlyWithConsent`), compiles only when asked, Exo's library sources, `PcIdle`, local
  Advanced Shader Delivery, compile estimates, the pipeline-cache check (`game.verify`, `game.diagnostics`).
- The service and its protocol (`engine/`, `engine/CONTRACT.md`): Exo Launcher's host speaks it.

## Taking a new upstream release

Upstream's history isn't in this repository, so a release comes in as a three-way merge made beside it, then copied
back as one commit:

```
git clone https://github.com/BlueHeisenberg/SCSKiller scs
# a scratch repository: base = upstream's base above, ours = this folder, theirs = the new tag
git init merge && cd merge
git -C ../scs archive v1.4.6 | tar -x && git add -A && git commit -m base && git branch base
git switch -c theirs && (rm everything but .git) && git -C ../scs archive <tag> | tar -x && git add -A && git commit -m theirs
git switch -c ours base && (rm everything but .git) && git -C <Exo-Launcher> archive HEAD:apps/launcher/shaders | tar -x && git add -A && git commit -m ours
git merge theirs
```

Then: keep the left-out folders deleted; resolve each conflict for Exo's rules above (diverge only where ours is clearly
better, else take upstream's); rebuild the native files if `proxy/` changed and record them in
`engine/prebuilt/SOURCE.md`; set the new base and version here and in `publish-service.ps1`; add an `engine/CHANGELOG.md`
entry; copy the result over this folder in one commit ("Exo Shaders: SCSKiller <tag>"), and run the protocol tests,
SCSKiller's tests (`build\test.ps1`) and Exo's. Each change of Exo's own goes in its own commit, described.

## Next

A trial merge of v1.4.8 (2026-11-19) leaves 11 files with conflicts (25 hunks: `ScsKiller.cs` 10, `EpicSource.cs` 5,
`ScsKiller.Offline.cs` 3, `Warmer.cs` 2, one each in `AppStore.cs`, `GameFiles.cs`, `UnrealReader.cs`,
`proxy/warm.cpp`, `proxy/selftest.cpp`) and 23 modify/deletes in the left-out folders. Its `proxy/` changes need the
native files rebuilt (Visual Studio C++ tools and CMake), and those need a run in real games before they ship.
