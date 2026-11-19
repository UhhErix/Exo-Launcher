// The Game clients step and card (src/lib/clients.ts). `npm test` (Node's own runner, which reads TypeScript as it is).
//
// How it could fail: the step is offered on a PC with neither store; the heading names the wrong fix (Steam for Epic, or
// neither); Continue lets setup pass while an old Epic or stable Steam is still there; Continue is held on a step other
// than Game clients, or while the check has not answered yet. A check that cannot be read (a failed read, or a Steam branch
// Exo can't read) must show the step and hold Continue, never skip it.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { ClientsStatus } from '../src/lib/host.ts'
import { clientsBlocks, clientsFixCopy, clientsGateShows, clientsHeading, clientsOffered, footerNext, libraryHeld, offersLater, footerHidden } from '../src/lib/clients.ts'

// needsFix is only a confirmed old client; problem is a read that failed (unknown), which holds onboarding but never the start card.
function status(layout: 'none' | 'old' | 'new' | 'unknown', channel: 'none' | 'stable' | 'beta' | 'unknown', needsFix: boolean, problem: string | null = null): ClientsStatus {
  return { ok: true, needsFix, problem, epic: { layout, version: null, oldStillThere: false, installing: false }, steam: { channel } }
}

test('the heading names what this PC is missing', () => {
  assert.equal(clientsHeading(status('old', 'stable', true)), 'Exo needs the new Epic launcher and Steam Beta')
  assert.equal(clientsHeading(status('old', 'beta', true)), 'Exo needs the new Epic launcher')
  assert.equal(clientsHeading(status('new', 'stable', true)), 'Exo needs Steam Beta')
  assert.equal(clientsHeading(status('new', 'beta', false)), 'Your game clients are ready')
  assert.equal(clientsHeading(status('new', 'unknown', false, 'Exo could not read Steam.')), 'Exo couldn’t check your game clients')
  assert.equal(clientsHeading(status('unknown', 'beta', false, 'Exo could not read Epic.')), 'Exo couldn’t check your game clients')
})

test('the step is offered when a store is installed, and not for a PC with neither', () => {
  assert.equal(clientsOffered(null), false)
  assert.equal(clientsOffered(status('none', 'none', false)), false)
  assert.equal(clientsOffered(status('old', 'none', true)), true)
  assert.equal(clientsOffered(status('none', 'stable', true)), true)
  assert.equal(clientsOffered(status('none', 'unknown', true, 'x')), true)
})

test('a check that failed still offers the step, so it is never skipped', () => {
  assert.equal(clientsOffered(null, true), true)
  assert.equal(clientsOffered(null, false), false)
})

test('Continue waits on the Game clients step while its check fails', () => {
  assert.equal(clientsBlocks('clients', status('old', 'beta', true)), true)
  assert.equal(clientsBlocks('clients', status('new', 'stable', true)), true)
  assert.equal(clientsBlocks('clients', status('new', 'beta', false)), false)
  assert.equal(clientsBlocks('clients', status('none', 'unknown', true, 'x')), true)
})

test('Continue is not held on other steps, or before the check has answered', () => {
  assert.equal(clientsBlocks('stores', status('old', 'stable', true)), false)
  assert.equal(clientsBlocks('clients', null), false)
})

test('an unreadable check holds onboarding but never the start card (a read glitch must not lock anyone out)', () => {
  const unknownSteam = status('new', 'unknown', false, 'Exo could not read Steam.')
  const unknownEpic = status('unknown', 'beta', false, 'Exo could not read Epic.')
  assert.equal(clientsBlocks('clients', unknownSteam), true)
  assert.equal(clientsBlocks('clients', unknownEpic), true)
  assert.equal(clientsGateShows(unknownSteam, false), false)
  assert.equal(clientsGateShows(unknownEpic, false), false)
  assert.equal(libraryHeld(true, unknownEpic, false, false), false)
})

test('a confirmed old client still holds the start card, and an unknown Epic is never offered a download', () => {
  assert.equal(clientsGateShows(status('old', 'beta', true), false), true)
  assert.equal(clientsFixCopy(status('unknown', 'beta', false, 'x')), 'Nothing changes while a game plays.')
})

test('Continue is held while the check cannot be read, and only Continue for now lets it go', () => {
  assert.equal(clientsBlocks('clients', null, false, true), true)
  assert.equal(clientsBlocks('clients', null, true, true), false)
  assert.equal(clientsBlocks('stores', null, false, true), false)
})

// Gate escape after a failed Fix (Continue for now) and the Fix copy: how these could fail. The gate holds the room
// after a failed fix with no way out; Continue for now releases setup and the card for good before the next start;
// the Fix copy promises a change this PC does not need (Steam on a PC with only Epic's old launcher), or leaves one out.

test('Continue for now lets setup go on while the check still fails, and the gate stays down for this run', () => {
  const failing = status('old', 'stable', true)
  assert.equal(clientsBlocks('clients', failing, false), true)
  assert.equal(clientsBlocks('clients', failing, true), false)
  assert.equal(clientsGateShows(failing, false), true)
  assert.equal(clientsGateShows(failing, true), false)
  assert.equal(clientsGateShows(status('new', 'beta', false), false), false)
})

test('the Fix copy names only the changes that apply to this PC', () => {
  assert.equal(
    clientsFixCopy(status('old', 'stable', true)),
    "Fix closes Steam and restarts it on Steam Beta, and downloads Epic's new launcher and opens its installer. Nothing changes while a game plays.",
  )
  assert.equal(clientsFixCopy(status('new', 'stable', true)), 'Fix closes Steam and restarts it on Steam Beta. Nothing changes while a game plays.')
  assert.equal(clientsFixCopy(status('old', 'beta', true)), "Fix downloads Epic's new launcher and opens its installer. Nothing changes while a game plays.")
})

// The footer's one button (footerNext). How it could fail: a check that cannot answer leaves Continue disabled with no way on;
// a Continue for now beside it would be a second control for one action; a restore's Start fresh is the primary by mistake.
test('an unreadable or failed clients check makes the footer button Continue for now, enabled, and it defers', () => {
  assert.deepEqual(footerNext('clients', status('new', 'unknown', false, 'Exo could not read Steam.'), false, false, false), { blocked: false, label: 'Continue for now', quiet: true, defers: true })
  assert.deepEqual(footerNext('clients', null, false, true, false), { blocked: false, label: 'Continue for now', quiet: true, defers: true })
})

// A hidden Epic installer holds setup until it closes. After 30 s of it running, the footer offers Continue for now; before that
// it still holds. How it could fail: an installer window that never closes keeps the person in setup with no way on.
test('an Epic installer running 30 s lets the footer defer the clients step, and holds it before that', () => {
  assert.deepEqual(footerNext('clients', status('old', 'beta', true), false, false, false, true), { blocked: false, label: 'Continue for now', quiet: true, defers: true })
  assert.equal(footerNext('clients', status('old', 'beta', true), false, false, false, false).blocked, true)
})
test('a clients check that needs its Fix holds Continue until it passes, with no deferral', () => {
  const next = footerNext('clients', status('old', 'beta', true), false, false, false)
  assert.equal(next.blocked, true)
  assert.equal(next.defers, false)
  assert.equal(next.label, undefined)
})

test('after a deferral the footer is the normal Continue again', () => {
  assert.equal(footerNext('clients', null, true, true, false).defers, false)
})

test('the restore step is quiet with Start fresh, and waits while its restore runs', () => {
  assert.deepEqual(footerNext('restore', null, false, false, false), { blocked: false, label: 'Start fresh', quiet: true, defers: false })
  assert.equal(footerNext('restore', null, false, false, true).blocked, true)
})

// The card's Continue for now (offersLater): shown after a failed Fix or a long installer wait, and only with something to defer.
// How it could fail: a control shown with nothing to defer; a long wait that never offers the way on.
test('the card offers Continue for now after a failed Fix or a long installer wait, and only where it can defer', () => {
  assert.equal(offersLater(true, false, () => {}), true)
  assert.equal(offersLater(false, true, () => {}), true)
  assert.equal(offersLater(false, false, () => {}), false)
  assert.equal(offersLater(true, true, undefined), false)
})

// The footer is hidden on welcome, and on a restore step held after a refused restart. How it could fail: Start fresh or Continue
// shows on a restore whose data is already in and whose saves are frozen; a plain restore step hides its footer.
test('the footer is hidden on welcome and on a held restore step only', () => {
  assert.equal(footerHidden('welcome', false), true)
  assert.equal(footerHidden('restore', true), true)
  assert.equal(footerHidden('restore', false), false)
  assert.equal(footerHidden('clients', true), false)
})
