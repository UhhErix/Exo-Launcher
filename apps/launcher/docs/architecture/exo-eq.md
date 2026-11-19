# Exo EQ

Decision record for "research if we can do what Lucid does in Exo but better ourselves" (owner, 2026-11-19). Read with
`Services/ExoEq.cs` (the service), `Services/ExoEqConfig.cs` (every byte Exo writes for Equalizer APO),
`Services/ExoEqSettings.cs` (exo-eq.json and the sounds), `Services/AutoEqCatalog.cs` (the headphone list),
`Services/EqualizerApo.cs` (detection, Device Selector, install), `Services/LucidImport.cs` and `Services/LucidRetire.cs`
(the move from Lucid), `Services/WebHostBridge.Eq.cs` (the RPC) and `ui/src/components/devices/EqPanels.tsx` (the page).
It replaces "Lucid inside", which hosted tekkusai's closed-source Lucid from `addons\lucid` until 3.25.

## Decision

Yes: Exo writes Equalizer APO's config itself. Lucid is a front end that writes Equalizer APO config files (its
`lucid_temp.txt` holds a Preamp and `Filter: ON PK …` lines, plus a VST plugin of its own); it reads its settings only at
start, so every change meant closing and restarting it, a few seconds without EQ, held back while a game ran. Exo EQ does
the same job with no process at all: Equalizer APO reloads its config the moment a file changes.

| Lucid | Exo EQ |
| --- | --- |
| Headphone catalog (its own, closed) | AutoEq's recommended results, about 6,000 models, each from its most accurate measurement, who measured it named |
| A change restarts Lucid (about 3 s of no EQ; waits for a game to end) | A change rewrites exo.txt; APO applies it live, during a game too |
| Auto device switching: Lucid watches Windows and rewrites its block | Every output's block is in exo.txt at once and APO picks by Device line: no watcher, no gap, works with Exo closed |
| Per-game EQ: Lucid's own game EQs for the titles it detects | A sound per game from your library, on at the game's start through Exo's run-state hook, off at its exit |
| Preamp per profile | Preamp worked out from the whole chain's response (biquads summed over 20 Hz to 20 kHz), so stacked boosts never clip |
| Signature tunings in a closed VST plugin | Not reproducible (they are Lucid's); Bass, Vocal, Footsteps and a ten-band Custom instead, plus Compare (A/B) |
| Night Shift, Windows Night light sync | Same, a gentle high shelf |
| Lucid Voice (mic chain) | Not carried: Exo Voice handles the mic |
| On/off hotkey, cue sounds, Discord status, notifications | Dropped: the switch is on the page; Exo has its own notifications |

## Equalizer APO

- **Format** (sourceforge.net/p/equalizerapo/wiki/Configuration reference): `config.txt` in the config folder
  (`HKLM\SOFTWARE\EqualizerAPO\ConfigPath`), one command per line. `Preamp: -N dB`; `Filter[ n]: ON <type> Fc <Hz> Hz Gain <dB> dB Q <q>`
  with PK/PEQ, LS/LSC and HS/HSC (shelves take a Q or a slope), LP, HP, BP, NO, AP and custom IIR; `GraphicEQ: f g; f g; …`
  (linear on a log frequency axis); `Convolution: <file>` (the file's rate must match the device's); `Include: <file>`
  (relative to the including file); `Device: <pattern>; <pattern>` (each pattern's words must all appear in the device
  name, connection name and endpoint GUID, `all` matches every device; until the next Device line, nothing applies to an
  unmatched device); `Channel:`, `Stage:`, `If/Eval`. Exo writes only PK, LSC and HSC, one block per output:
  `Device: {guid}` … `Device: all` / `Channel: all`, so nothing leaks to another output or to what follows.
- **Live reload**: APO's documentation has saving config.txt, or changing an Include, change the sound at once. Exo writes
  through a temporary file and a move so APO never reads half a file.
- **Per device**: APO runs only on the outputs ticked in its Device Selector (`DeviceSelector.exe`, Configurator in older
  versions), which puts its effect in the endpoint's `FxProperties`; it plays after the audio service restarts, so after a
  reboot. Exo reads FxProperties (installed on this output?), the key's last write time against the boot time (turned on
  since the PC started: restart), and `PKEY_AudioEndpoint_Disable_SysFx` (Windows' enhancements off keeps APO out), and says
  which on the page, with Choose outputs (the Device Selector, which asks Windows itself) and Sound settings.
- **Licence**: GPL-2.0 (SourceForge project page), by Jonas Thedering. Exo never ships, links or changes it; it writes a
  text file APO reads.
- **Download and install**: the official files are on SourceForge (`/projects/equalizerapo/files/1.4.2/`; 1.4.2 of
  2025-03-21 is the newest). The installer is NSIS and unsigned, as are APO's binaries (checked on this PC), so Exo pins it:
  size 11,980,366 and MD5 `410aab97…` from SourceForge's own file list, SHA-256 `7403be74…` from Chocolatey's reviewed
  `equalizerapo` 1.4.2 package: two independent sources; anything else is not run. It runs with `/S` and `runas` (one UAC
  prompt; the installer requires admin itself) and opens its Device Selector when done. A reboot is needed before APO plays.
- **How Lucid did it**: Lucid downloads `EqualizerAPO-x64-1.4.2.exe` from its own GitHub repository (tekkusai/Lucid, a copy
  of the same size as SourceForge's), and its old first-run script extracted it with 7-Zip and bound devices itself. Exo uses
  the official source instead.

## Headphone data

- **AutoEq** (github.com/jaakkopasanen/AutoEq): MIT, Copyright Jaakko Pasanen. Its `results/` folder holds a precomputed
  ParametricEQ.txt per headphone; `results/README.md` is the recommended list, one result per model.
- **The measurements behind them** are third parties' (oratory1990, crinacle, Rtings, Innerfidelity, Super Review and
  others). AutoEq's MIT licence covers its code; the measurement owners' terms are their own. oratory1990 has objected to a
  paid app using his presets and suggested owners may ask to be left out of AutoEq; crinacle's raw data is no longer in
  AutoEq's `measurements/` (only a name index), though results made from it remain.
- **So Exo bundles none of it.** The list is fetched from AutoEq's repository when the picker first opens (kept a week on the
  PC), and one model's ParametricEQ.txt when the player picks it (kept in exo-eq.json so it plays offline), as a player
  fetching it from autoeq.app would; the page and exo.txt name who measured it. `NetworkDestinations.AutoEq` and PRIVACY.md
  list the request. Exo is free; nothing is resold.

## Per game and per output

Exo's run-state poll (`Launcher.GameRunStateChanged`, games started anywhere) calls `ExoEq.GamesChanged`, which rewrites
exo.txt with the running game's sound or the output's own; the first library game running that has a sound wins. Outputs
need no hook (Device lines). Night Shift is a one-minute timer that exists only while Night Shift is on.

## Alternatives considered

- **Exo's own APO** (an audio processing object DLL registered per endpoint): what Equalizer APO already is. Writing one
  means a signed in-process DLL inside `audiodg.exe`, the same per-device registration and reboot, and owning crashes in
  Windows' audio engine; nothing gained for a parametric EQ.
- **A WASAPI loopback or virtual device pipeline** (capture, process, render to the real device): adds latency, needs a
  signed virtual audio driver to be the default device, and breaks exclusive mode and spatial audio; worse.
- **Equalizer APO as the engine is the sane choice.** Peace and the others are front ends of the same engine; Exo is now one.

## The move from Lucid

`LucidRetire.Once` at the first start of 3.26 (and `tools/installer/Close-ExoLauncher.ps1` during the update before it):
close the Lucid in `addons\lucid` or `addons\lucid-current` (`LucidStop`: ask with the Restart Manager, wait, end only that
exact exe), remove a Run entry for it, read `%APPDATA%\LucidEQ\settings.json` (headphone by name, matched in AutoEq's list
once it loads; the outputs its config.txt block named; LastEqState; Night Shift; Game Mode's per-game EQ as Footsteps for the
same games in Exo's library), and once that headphone's correction is in Exo EQ, turn Lucid's EQ block in config.txt into
comments when it was Exo's Lucid that played it (so the sound is not corrected twice; until then, offline or for a headphone
AutoEq lacks, it keeps playing what Lucid last wrote; Lucid Voice's block stays), and delete lucid-update.json, lucid-held.json and
`addons\lucid-current`. Lucid's own folders are only read. A Lucid the player runs on their own is never closed and its block
stays; the page offers "Turn Lucid's off" while it plays. An older Exo's own `# Exo EQ begin … end` block is removed (its
first line said Exo owns it), keeping a Lucid block found inside it.

## Not verified until the owner runs it

- That Equalizer APO reloads exo.txt live through the Include on a real PC, with no gap in the sound.
- The install flow: SourceForge's redirect lands on a `*.dl.sourceforge.net` mirror and the bytes match the pin, `/S`
  installs with one prompt, the Device Selector opens after, and the page's restart note clears after a reboot.
- The migration on a PC with a hosted Lucid (its headphone, the legacy block in config.txt, the per-game list).
