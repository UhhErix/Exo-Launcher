# ADR-0012: Onboarding step gating, focus preservation, and Shaders board state ordering

## Status

Accepted — 2026-11-20. Extracted from design comments across `apps/launcher/ui/src/components/onboarding/`, `apps/launcher/ui/src/components/OnboardingPanel.tsx`, and `apps/launcher/ui/src/components/shaders/`.

## Context

First-run setup (`OnboardingPanel`) coordinates destructive cleanup, account recovery codes, backup restoration, and PC optimization in one sequential modal flow, while `PC > Graphics > Shaders` (`ShadersBoard`) renders live shader-compilation queues and ETAs for a separate headless process (`ExoShaders.Service.exe`) without spawning it unnecessarily.

## Decision

1. **Recovery code and password-reset invariants:** A 24-character recovery code is shown once and must be confirmed by typing it back before setup advances (`RecoveryCodeScreen.tsx`); `accountCode.ts` strips dashes/spaces, normalizes case, and maps `O`, `I`, and `L` to `0`, `1`, and `1` to match the issuing alphabet. Password resets (`ResetScreen.tsx`) issue a new recovery code and hold the same confirmation gate (`AccountStep.tsx`) so closing mid-flow never strands an unconfirmed code. Auth lockouts keep submit controls disabled until `Retry-After` expires.
2. **Restore serialization and focus stability:** `RestoreStep.tsx` holds the panel `busy` while restoring and restarting so `Start fresh` cannot race a restart; if restart fails after restore, saves are frozen and the step holds without offering skipping actions. In `AppsStep.tsx`, step tiles remain interactive (never `disabled`) during async saves so arrow-key radio navigation never drops keyboard focus.
3. **Onboarding visibility, deferred scan, and step transitions:** `OnboardingPanel.tsx` omits upcoming steps that have nothing to offer on the current PC without removing the step currently on screen (`model.ts`, `OnboardingPanel.tsx`). The slow cleanup junk scan runs once lazily when reaching `Discord` (the step immediately preceding `Clean up`) and blocks `Continue` only there so earlier steps stay instant. `useLeavingStep.tsx` retains an inert copy of the outgoing step for 120 ms for exit animation. Wait-before-continue gates (`Continue for now`) unlock Epic/client steps after 30 s timeouts.
4. **Shaders board passive snapshot loading and state precedence:** Opening `ShadersBoard.tsx` reads the engine's last exited snapshot from disk without launching `ExoShaders.Service.exe`; only explicit `Refresh` and `Compile` actions spawn the engine. In `snapshotOrder.ts`, exited/prior engine snapshots normalize to `rev 0` while a running engine counts revisions from `1`, preventing an idle startup scan snapshot from overwriting live compilation state.
5. **Queue sorting, adaptive poster grid, and Unreal first-launch automation:** Waiting/ready games sort by compiling first, then active queue/user drag order, then shortest learned/rough ETA (handling `Infinity` pairs deterministically before falling back to title order). `fitFor` computes the column count (`>= 112px` floor, capped at `WIDEST`) maximizing poster area within the visible pane, breaking ties toward more columns; narrow captions bind numbers and units with non-breaking spaces and word joiners around ranges (`"15\u2060–\u206030\u00a0min"`). Unreal first-launch shader captures (`ShaderPrepare.cs`) only appear after engine compilation finishes and run automatically only when idle.

## Consequences

- `AccountStep.tsx` currently mirrors inline parent callbacks into a ref on each render; tracked for cleanup in GitHub issue [#102](https://github.com/UhhErix/Exo-Launcher/issues/102).
- `catch {}` blocks around pointer capture (`ShadersBoard.tsx`) remain intentionally empty per ESLint `allowEmptyCatch: true` when pointers release before capture attaches.
