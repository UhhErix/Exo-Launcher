// The game page's Frame generation word for Exo's own mod (src/components/game/frameGenWord.ts). `npm test` (Node's runner).
//
// How it could fail: another mod over Exo's loader reads as a game update (Phantom Blade Zero, 3.25); a reason shows a
// file name or a second sentence; the other mod's name is dropped when Exo knows it; a broken line isn't marked bad;
// what Exo did by itself is hidden behind the mod's version.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { frameGenWord } from '../src/components/game/frameGenWord.ts'

const broken = { ok: true, state: 'broken', path: 'dlss', version: 'DLSSG SM86 0.4.1' } as const

test('each reason has its own plain sentence, never a game update', () => {
  assert.deepEqual(frameGenWord({ ...broken, reason: 'replaced' }), { text: 'Another mod replaced Exo’s loader', bad: true })
  assert.deepEqual(frameGenWord({ ...broken, reason: 'removed' }), { text: 'Exo’s files were removed', bad: true })
  assert.deepEqual(frameGenWord({ ...broken, reason: 'gone' }), { text: 'This game no longer has DLSS Frame Generation', bad: true })
  // A file of Exo's other than the loader changed: never blamed on another mod's loader.
  assert.deepEqual(frameGenWord({ ...broken, reason: 'changed', mod: 'FSR 4 shader overrides' }), { text: 'Exo’s files were changed', bad: true })
  for (const reason of ['replaced', 'changed', 'removed', 'gone'] as const) {
    const text = frameGenWord({ ...broken, reason })!.text
    assert.doesNotMatch(text, /update|\.dll|—/i)
    assert.doesNotMatch(text, /\.\s/)
  }
})

test('one apostrophe on the line: the host’s straight one reads as the page’s curly one', () => {
  const lines = [
    frameGenWord({ ok: true, state: 'note', note: 'Taken out: it didn\'t run in this game' }),
    frameGenWord({ ok: true, state: 'on', version: 'OptiScaler v0.9.4', note: 'Back on OptiScaler 0.9.4: 0.9.5 didn\'t run' }),
    frameGenWord({ ...broken, reason: 'replaced', mod: 'Someone\'s loader' }),
    frameGenWord({ ok: true, state: 'mod', mod: 'Someone\'s mod' }),
  ]
  for (const word of lines) assert.doesNotMatch(word!.text, /'/)
  assert.equal(lines[0]!.text, 'Taken out: it didn’t run in this game')
  assert.deepEqual(frameGenWord({ ok: true, state: 'native' }), { text: 'Built into the game' })
})

test('hidden is about Exo’s mod, not the game: unread says so, an RTX 40 or 50 game with its own reads native', () => {
  assert.deepEqual(frameGenWord({ ok: true, state: 'hidden' }), { text: 'Not supported in this game' })
  assert.deepEqual(frameGenWord({ ok: true, state: 'hidden', reason: 'unread' }), { text: 'Could not check' })
  // The host sends native for a DLSS frame generation game on an RTX 40 or 50, never hidden.
  assert.deepEqual(frameGenWord({ ok: true, state: 'native' }), { text: 'Built into the game' })
})

test('the mod that replaced Exo’s loader is named when Exo knows it', () => {
  assert.equal(frameGenWord({ ...broken, reason: 'replaced', mod: 'UE4SS' })!.text, 'UE4SS replaced Exo’s loader')
})

test('what Exo did by itself shows before the release, and off and on read as before', () => {
  assert.deepEqual(frameGenWord({ ok: true, state: 'off', note: 'Taken out: the game updated' }), { text: 'Taken out: the game updated' })
  assert.deepEqual(frameGenWord({ ok: true, state: 'off' }), { text: 'Off' })
  assert.deepEqual(frameGenWord({ ok: true, state: 'on', version: 'OptiScaler v0.9.4' }), { text: 'OptiScaler v0.9.4' })
  assert.equal(frameGenWord({ ok: true, state: 'on' }), null)
})
