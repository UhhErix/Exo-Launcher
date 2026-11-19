/**
 * The account gate. An Exo account is required (owner, 2026-11-09): while this PC is signed out, the account sheet is up at
 * every start and there is no way past it but creating an account or signing in. The one exception is Exo's account
 * service being out of reach: Continue without an account lets the run through (`laterThisRun`) until the service answers
 * again with no game running, and the next start asks again. A recovery code that was issued and not yet typed back keeps
 * the sheet up as well, unless another screen (Settings) is showing that code right now.
 *
 * | overview read | signed in | code pending | code on another screen | later | gate |
 * | ------------- | --------- | ------------ | ---------------------- | ----- | ---- |
 * | no            | any       | any          | any                    | any   | shut (no flash before the read) |
 * | yes           | no        | any          | any                    | no    | open |
 * | yes           | no        | any          | any                    | yes   | shut |
 * | yes           | yes       | yes          | no                     | no    | open (make a new code) |
 * | yes           | yes       | yes          | yes                    | any   | shut (that screen confirms it) |
 * | yes           | yes       | yes          | no                     | yes   | shut |
 * | yes           | yes       | no           | any                    | any   | shut |
 */
export interface GateOverview {
  signedIn: boolean
  codePending: boolean
}

export function accountGateDue(overview: GateOverview | null | undefined, laterThisRun: boolean, codeOnAnotherScreen = false): boolean {
  if (!overview) return false
  if (laterThisRun) return false
  if (!overview.signedIn) return true
  return overview.codePending && !codeOnAnotherScreen
}

/**
 * Whether the shell's account sheet is up, given whether it was up a moment ago. It holds itself, with no wiring from the
 * step inside it: once up, it stays while any recovery code is on screen or any account request is on its way (the
 * sheet is modal, so while it is up those are its own), whatever the account state says meanwhile. A session saved before
 * the code is typed back can't close it under the code, and no prop can be left out to break that. Otherwise it waits for
 * the What's new sheet, then follows the gate, which leaves a code or a request elsewhere (Settings) to that screen.
 *
 * | was up | code or request now | What's new | gate due | up |
 * | ------ | ------------------- | ---------- | -------- | -- |
 * | yes    | yes                 | any        | any      | yes |
 * | any    | no                  | yes        | any      | no  |
 * | any    | no                  | no         | yes/no   | the gate |
 * | no     | yes                 | yes        | any      | no  |
 * | no     | yes                 | no         | any      | the gate (which a code elsewhere keeps shut while signed in) |
 */
export function accountSheetOpen(input: {
  wasOpen: boolean
  whatsNewOpen: boolean
  overview: GateOverview | null | undefined
  laterThisRun: boolean
  codesOnScreen: number
  requests: number
}): boolean {
  const busy = input.codesOnScreen > 0 || input.requests > 0
  if (input.wasOpen && busy) return true
  if (input.whatsNewOpen) return false
  return accountGateDue(input.overview, input.laterThisRun, busy)
}
/** After Continue without an account: the sheet comes back once the service answers and no game is running. */
export function laterEnds(probe: { reachable: boolean; gameRunning: boolean }): boolean {
  return probe.reachable && !probe.gameRunning
}

/** The update sheet waits for start-up and never shows over the account sheet: Update now restarts Exo, and a recovery code
 * on screen would be lost unconfirmed. */
export function updateSheetShown(booting: boolean, accountSheetUp: boolean): boolean {
  return !booting && !accountSheetUp
}
