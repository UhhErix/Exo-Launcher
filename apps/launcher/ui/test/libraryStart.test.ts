// When the library may start (src/lib/clients.ts libraryHeld, used by useLibraryStart in LauncherApp). `npm test`.
//
// How it could fail: onboarding's finish reads the library while the Game clients start card still holds it (the read then
// runs twice, or before the Fix); the library waits forever after a failed check; a deferred Fix still holds the library.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { ClientsStatus } from '../src/lib/host.ts'
import { libraryHeld } from '../src/lib/clients.ts'

const needsFix: ClientsStatus = { ok: true, needsFix: true, epic: { layout: 'old', version: null, oldStillThere: false, installing: false }, steam: { channel: 'none' } } as ClientsStatus
const fine: ClientsStatus = { ...needsFix, needsFix: false } as ClientsStatus

test('before onboarding is finished, this gate is open: the library start waits on onboarding itself', () => {
  assert.equal(libraryHeld(false, fine, false, false), false)
})

test('finished onboarding with the Game clients Fix due still holds the library', () => {
  assert.equal(libraryHeld(true, needsFix, false, false), true)
})

test('a deferred Fix (Continue for now) lets the library start', () => {
  assert.equal(libraryHeld(true, needsFix, true, false), false)
})

test('finished onboarding with clients fine lets the library start once', () => {
  assert.equal(libraryHeld(true, fine, false, false), false)
})

test('a check that cannot answer waits for the answer, never on the failure', () => {
  assert.equal(libraryHeld(true, null, false, false), true)
  assert.equal(libraryHeld(true, null, false, true), false)
})
