# Voice cuts out on noisy USB condenser mics

Branch `UhhErix/voice-cutout` · Tester: Colton · Mic: Maono AU-A04 (USB condenser) · Public WebView2 build

## Summary

A quiet word spoken over a loud condenser's room noise was judged by the gate as room noise and shut. Lost speech frames scored a DeepFilterNet speech score of +2 to +9 dB and sat 2 to 6 dB above the mic's floor. The gate's loudness check required 6 dB above the floor, so those frames stayed shut. The gate fix lowers that margin to 2 dB only for frames the speech score already calls speech. Pure room noise still never opens the gate: it scores about -15 dB (max -3.5 dB) and the speech-score check still holds it shut.

Fixture results (offline, same scenarios before and after): true audible cutouts drop to 0 in 7 of 9 scenarios. The quiet case drops from 247 lost frames (47.0%) to 7 (1.3%), with the longest remaining gap at 100 ms. No room-noise frame opened the gate in any scenario (0 of 173, before and after).

## Causes

| # | Cause | Status | Evidence |
|---|---|---|---|
| 1 | Gate's loudness margin (6 dB over the mic floor) shuts quiet speech over a noisy condenser floor | **Confirmed and fixed** | Gate trace: lost speech frames at SNR 2–9 dB, 2–6 dB over the mic floor. Fixture before/after below. |
| 2 | Suppression (DeepFilterNet) removes part of quiet voice | **Not isolated.** Not measured separately. | Not changed. The -45 quiet case still has a 100 ms residual gap. |
| 3 | Capture format path (WASAPI shared mode converts the device's native rate to 48 kHz float) drops or delays packets | **Not isolated.** Logging added so the next run shows it. | `LateDrops` / `BehindDrops` counters are now in the per-minute gate log. Format line logged at capture open. |
| 4 | USB condenser suspend / re-enumeration interrupts capture | **Not isolated.** No device-side reproduction. | Not changed. Visible in the new per-minute log if drops climb during a session. |

## Fixture results

Offline run of `VoiceChain` at 48 kHz stereo, 4 s lead-in and 3 s trail. `trueLost` = speech frames the gate did not pass. Source speech: 26.8 s of speech (16-bit, 48 kHz). Noise: hiss + brown-ish fan + 60/120/180 Hz hum at the stated floor and mix.

| Scenario | trueLost before → after |
|---|---|
| -45 dB floor, mix | 22 → 0 |
| -42 dB floor, mix | 62 → 0 |
| -40 dB floor, 95% buzz | 34 → 0 |
| -45 dB floor, 80 Hz hum | 6 → 0 |
| -45 dB floor, 80 Hz hum, quiet voice | 77 → 0 |
| -40 dB floor, hot voice | 7 → 0 |
| -45 dB floor, quiet voice (peak -18 dB) | 247 → 7 (47.0% → 1.3%; longest gap 100 ms) |
| -50 dB floor, mix | 0 → 0 |
| Fifine (clean mic) reference | 0 → 0 |

`noiseOpen` (room-noise frames that opened the gate): 0 of 173 in every scenario, before and after.

## Changes

1. **Gate margin for scored speech** (`ExoLauncher/Services/DiscordClient/Voice/VoiceInput.cs`, `VoiceGate`). New constants `MicOverFloorDb = 6`, `MicVoiceOverFloorDb = 2`. When the speech score is above `VoiceOverNoiseDb` (0 dB), the loudness check uses the 2 dB margin; otherwise it keeps 6 dB. All other loudness conditions are unchanged.
2. **Per-minute gate log** (`VoiceConnection.cs` ~line 449). Adds `LateDrops` and `BehindDrops` counts.
3. **Capture format log** (`VoiceAudio.cs` ~line 305). Logs the opened format and the device's native rate at capture start.
4. **MicCatalog** (`MicCatalog.cs` ~line 470). Adds `Maono AU-A04` and `Maono AU-A04TR` as USB mics with the close-tip. No VID/PID (none found in public USB ID listings).
5. **Tests**
   - `ExoLauncher.Tests/VoiceCutoutGateTests.cs` (new, 3 tests, no plugin needed):
     - a quiet word over a noisy floor opens the gate (fails with the old 6 dB margin, passes with 2 dB);
     - room noise over its floor never opens the gate, even when suppression keeps it;
     - a word scored as noise does not open the gate on level alone.
   - `ExoLauncher.Tests/VoiceCutoutFixtureTests.cs` (new): offline fixture. Skipped unless `EXO_VOICE_SPEECH_WAV` points to a 48 kHz speech WAV. Writes its report to the temp directory.

## Caveats

- **AU-A04 specs are unverified.** The gain knob and 192 kHz / 24-bit claims come from the tester's description and retail listings; reviews conflict. Exo's MicCatalog entry says only "turn the knob down", which the tester can check.
- **AU-A04 has no VID/PID** in this change. Name matching only.
- **Causes 2–4 are not fixed or measured.** The per-minute log will show drops (cause 3) and session behaviour (cause 4) on the next run.
- **Not verified on the installed app** (Windows 11, real mic). Checked in code and offline fixture only.

## Native Rune UI

The native UI (`native-ui/crates/exo-ui/src/discord_voice.rs`, ~lines 475–530) shows only the microphone device list and Mic volume (±10 steps). The React settings (`ui/src/components/discord/settings/VoicePage.tsx`) also expose:

- Voice cleanup: Exo Voice / Standard / off, with a "not running" hint;
- noise suppression for others;
- Input Sensitivity: automatic or threshold, with a live level meter;
- a "gated before Exo" hint.

**Follow-up for native:** none of these controls exist in the native UI. Parity is needed if the React controls remain the reference. No change made in this PR.

## Gates

- `dotnet build ExoLauncher/ExoLauncher.csproj -c Release -p:Platform=x64`: succeeded, 0 warnings, 0 errors.
- `dotnet test ExoLauncher.Tests -c Release -p:Platform=x64 --no-build`: **7964 total, 0 failed, 7959 passed, 5 skipped** (skips are environment-gated: FPS previews, symlinks, Shaders engine build, voice speech WAV, speaker test dir).
- `node tools/check-comments.mjs`: passed (2389 legacy files, 176 zero-comment files).

No installed app was touched. No real data directory was written.
