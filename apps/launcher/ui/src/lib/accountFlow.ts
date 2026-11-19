/**
 * The account step's screens (CLIENT.md 4.1), one table for every place it runs: the shell's account sheet, setup and
 * Settings. The recovery code is shown once and cannot be fetched again, so "code" has one way out: the player types it
 * back (confirm). Nothing else leaves it, not a host event, not a refresh, not Escape, not a press outside the sheet.
 *
 * | From                                  | Event                         | To                                          |
 * | ------------------------------------- | ----------------------------- | ------------------------------------------- |
 * | code                                  | confirm                       | signedIn                                    |
 * | code                                  | anything else                 | code (unchanged)                            |
 * | signedIn                              | anything                      | signedIn (the step is over)                 |
 * | any form, busy                        | answer                        | per the answer (below), not busy            |
 * | any form, busy                        | anything else                 | unchanged (one request at a time)           |
 * | create, signin, reset, newCode        | submit                        | the same screen, busy                       |
 * | choose, offline                       | submit                        | unchanged                                   |
 * | choose, create, signin, reset, offline| open (choose/create/signin/reset/newCode) | that screen                     |
 * | newCode                               | open reset                    | reset (Forgot your password?)               |
 * | newCode                               | open anything else            | unchanged (the account exists: no going back to create) |
 * | newCode, offline                      | signedOut                     | choose (Sign out of this PC: the session went) |
 * | any other screen                      | signedOut                     | unchanged                                   |
 * | (busy) answer code                    |                               | code                                        |
 * | (busy) answer noCode                  |                               | newCode (created, but no code came back)    |
 * | (busy) answer signedIn                |                               | signedIn                                    |
 * | (busy) answer offline                 |                               | offline                                     |
 * | (busy) answer refused                 |                               | the same form, not busy                     |
 *
 * newCode is the step that makes a new recovery code with the password once: a code that was issued and never typed
 * back (Exo quit on the code screen, or the answer lacked one) is replaced, so no account keeps a code nobody saw. It is
 * never a dead end: a player who forgot the password resets it there with a saved code, and Sign out of this PC goes back to
 * the choice. Every screen has a way on: the table test walks that too.
 */
export type AccountScreen = 'choose' | 'create' | 'signin' | 'reset' | 'newCode' | 'code' | 'offline' | 'signedIn'

export interface FlowState {
  screen: AccountScreen
  busy: boolean
}

export type FlowEvent =
  | { type: 'open'; to: 'choose' | 'create' | 'signin' | 'reset' | 'newCode' }
  | { type: 'submit' }
  | { type: 'answer'; result: 'code' | 'noCode' | 'signedIn' | 'offline' | 'refused' }
  | { type: 'confirm' }
  | { type: 'signedOut' }

const FORMS: readonly AccountScreen[] = ['create', 'signin', 'reset', 'newCode']
const OPENS_FROM: readonly AccountScreen[] = ['choose', 'create', 'signin', 'reset', 'offline']

export function nextFlow(state: FlowState, event: FlowEvent): FlowState {
  if (state.screen === 'code') return event.type === 'confirm' ? { screen: 'signedIn', busy: false } : state
  if (state.screen === 'signedIn') return state
  if (state.busy) {
    if (event.type !== 'answer') return state
    const to: Record<typeof event.result, AccountScreen> = { code: 'code', noCode: 'newCode', signedIn: 'signedIn', offline: 'offline', refused: state.screen }
    return { screen: to[event.result], busy: false }
  }
  if (event.type === 'submit') return FORMS.includes(state.screen) ? { screen: state.screen, busy: true } : state
  if (event.type === 'signedOut') return state.screen === 'newCode' || state.screen === 'offline' ? { screen: 'choose', busy: false } : state
  if (event.type === 'open') {
    if (state.screen === 'newCode') return event.to === 'reset' ? { screen: 'reset', busy: false } : state
    return OPENS_FROM.includes(state.screen) ? { screen: event.to, busy: false } : state
  }
  return state
}

/** Where the step starts: a code that waits to be confirmed with no code in hand makes a new one first. */
/**
 * The email the reset form starts with. From the new-code step the player is signed in, so the form opens with the
 * account's own email instead of an empty field; anything the player already typed stays, and a sign-in's own form keeps
 * what was typed there.
 */
export function resetEmail(typed: string, from: 'signin' | 'newCode', accountEmail: string | null | undefined): string {
  if (typed.trim() !== '') return typed
  return from === 'newCode' ? accountEmail?.trim() ?? '' : ''
}

export function firstScreen(codePending: boolean | undefined): AccountScreen {
  return codePending ? 'newCode' : 'choose'
}

/** Setup skips the account step when this PC is already signed in with nothing left to confirm. */
export function setupSkipsAccount(overview: { signedIn: boolean; codePending: boolean } | null, screen: AccountScreen): boolean {
  return overview !== null && overview.signedIn && !overview.codePending && screen === 'choose'
}
