// The account gate (lib/accountPrompt.ts). How it could fail: a signed-in PC sees the sheet, a signed-out one gets past it,
// the sheet flashes before the account's state is read, Continue without an account (offline) does not let the run
// through or holds after the service is back, a code waiting to be confirmed lets the PC past, or the sheet closes under
// the recovery code when the session is saved.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { accountGateDue, accountSheetOpen, laterEnds, updateSheetShown } from '../src/lib/accountPrompt.ts'

const signedOut = { signedIn: false, codePending: false }
const signedIn = { signedIn: true, codePending: false }
const pending = { signedIn: true, codePending: true }

test('every row of the gate table', () => {
  assert.equal(accountGateDue(null, false), false, 'not read yet')
  assert.equal(accountGateDue(undefined, true), false, 'not read yet, later')
  assert.equal(accountGateDue(signedOut, false), true)
  assert.equal(accountGateDue(signedOut, false, true), true, 'signed out: a code elsewhere changes nothing')
  assert.equal(accountGateDue(signedOut, true), false, 'Continue without an account')
  assert.equal(accountGateDue(pending, false), true, 'a code nobody confirmed: make a new one')
  assert.equal(accountGateDue(pending, false, true), false, 'Settings shows that code now')
  assert.equal(accountGateDue(pending, true), false)
  assert.equal(accountGateDue(signedIn, false), false)
  assert.equal(accountGateDue(signedIn, true), false)
})

test('once up, the sheet holds itself while a code shows or a request is on its way; it does not open over another screen\'s code', () => {
  const base = { whatsNewOpen: false, laterThisRun: false }
  // Up, then the session is saved (signed in, code pending) and the code shows: it stays, whatever the gate says.
  assert.equal(accountSheetOpen({ ...base, wasOpen: true, overview: signedIn, codesOnScreen: 1, requests: 0 }), true)
  assert.equal(accountSheetOpen({ ...base, wasOpen: true, overview: signedIn, codesOnScreen: 0, requests: 1 }), true)
  assert.equal(accountSheetOpen({ ...base, wasOpen: true, overview: signedIn, codesOnScreen: 1, requests: 0, whatsNewOpen: true }), true)
  // The code typed back: it goes.
  assert.equal(accountSheetOpen({ ...base, wasOpen: true, overview: signedIn, codesOnScreen: 0, requests: 0 }), false)
  // Down while Settings shows its own code or waits on its request: it stays down, even with a code pending.
  assert.equal(accountSheetOpen({ ...base, wasOpen: false, overview: pending, codesOnScreen: 1, requests: 0 }), false)
  assert.equal(accountSheetOpen({ ...base, wasOpen: false, overview: pending, codesOnScreen: 0, requests: 1 }), false)
  assert.equal(accountSheetOpen({ ...base, wasOpen: false, overview: pending, codesOnScreen: 0, requests: 0 }), true)
  // Signed out: up, but not over What's new.
  assert.equal(accountSheetOpen({ ...base, wasOpen: false, overview: signedOut, codesOnScreen: 0, requests: 0 }), true)
  assert.equal(accountSheetOpen({ ...base, wasOpen: false, overview: signedOut, codesOnScreen: 0, requests: 0, whatsNewOpen: true }), false)
})
test('Continue without an account ends when the service answers and no game runs', () => {
  assert.equal(laterEnds({ reachable: true, gameRunning: false }), true)
  assert.equal(laterEnds({ reachable: true, gameRunning: true }), false)
  assert.equal(laterEnds({ reachable: false, gameRunning: false }), false)
})

test('the update sheet never shows over the account sheet, nor while Exo starts', () => {
  assert.equal(updateSheetShown(false, false), true)
  assert.equal(updateSheetShown(false, true), false)
  assert.equal(updateSheetShown(true, false), false)
})
