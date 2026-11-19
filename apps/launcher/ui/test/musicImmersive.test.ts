// Music's Immersive (src/components/music/Immersive.tsx, immersiveRules.ts, shortcuts.ts). `npm test`.
//
// How it could fail: the ambient light is bright enough on white art that the words over it drop under 4.5:1 (its two
// tokens changed apart from this check); F is missing for a service, or takes a key the service already uses; the Bar comes back for a pointer far from the top (it never
// stays away) or never comes back for a key; the Bar hides under the pointer, a focused control or an open menu;
// Immersive is remembered across a restart (it must start off).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { BAR_REVEAL_PX, IMMERSIVE_KEY, barMayHide, contrastOver, immersiveWasOn, rememberImmersive, wakesBar, withImmersiveKey } from '../src/components/music/immersiveRules.ts'
import { immersiveVolumeAction, nextImmersiveVolume } from '../src/lib/musicVolume.ts'

const tokens = readFileSync(new URL('../src/tokens.css', import.meta.url), 'utf8')
const token = (name: string) => new RegExp(`--${name}:\\s*([^;]+);`).exec(tokens)?.[1]?.trim()
const grey = (hex: string) => parseInt(hex.slice(1, 3), 16) / 255

test('the words over the ambient light keep 4.5:1 on the brightest art', () => {
  const art = Number(token('exo-immersive-art'))
  const dim = Number(/rgb\(0 0 0 \/ ([\d.]+)\)/.exec(token('exo-immersive-dim') ?? '')?.[1])
  assert.ok(art > 0 && art <= 1, `art ${art}`)
  assert.ok(dim > 0 && dim < 1, `dim ${dim}`)
  // White, the artist line (muted) and the lyrics not yet sung (faint).
  for (const name of ['color-fg', 'color-muted', 'color-faint']) {
    const word = grey(token(name) ?? '')
    assert.ok(contrastOver({ art, dim }, word) >= 4.5, `${name}: ${contrastOver({ art, dim }, word).toFixed(2)}:1`)
  }
})

test('F is Immersive beside the keys a service has, and never takes one it uses', () => {
  const service = { 'Ctrl+F': 'search', 'K': 'toggle', 'Escape': 'closePanel' }
  assert.deepEqual(withImmersiveKey(service, 'immersive'), { ...service, [IMMERSIVE_KEY]: 'immersive' })
  assert.deepEqual(withImmersiveKey({ [IMMERSIVE_KEY]: 'theirs' }, 'immersive'), { [IMMERSIVE_KEY]: 'theirs' })
  // No service binds F alone today (Spotify, the Music app, YouTube Music), so Immersive has it in all three.
  const shortcuts = readFileSync(new URL('../src/components/music/shortcuts.ts', import.meta.url), 'utf8')
  assert.doesNotMatch(shortcuts, new RegExp(`'${IMMERSIVE_KEY}':`))
  assert.match(shortcuts, /withImmersiveKey\(serviceBindings\(/)
})

test('the Bar comes back for any key, or the pointer near the top only', () => {
  assert.equal(wakesBar({ kind: 'key' }), true)
  assert.equal(wakesBar({ kind: 'pointer', y: 0 }), true)
  assert.equal(wakesBar({ kind: 'pointer', y: BAR_REVEAL_PX }), true)
  assert.equal(wakesBar({ kind: 'pointer', y: BAR_REVEAL_PX + 1 }), false)
  assert.equal(wakesBar({ kind: 'pointer', y: 800 }), false)
  assert.ok(BAR_REVEAL_PX > 52, 'reaches under the Bar\'s 52px band, where the page sees the pointer')
})

test('the Bar never hides under the pointer, a focused control or anything open', () => {
  assert.equal(barMayHide({ hovered: false, focused: false, open: false }), true)
  assert.equal(barMayHide({ hovered: true, focused: false, open: false }), false)
  assert.equal(barMayHide({ hovered: false, focused: true, open: false }), false)
  assert.equal(barMayHide({ hovered: false, focused: false, open: true }), false)
})

test('Immersive starts off and is remembered for the session only', () => {
  assert.equal(immersiveWasOn(), false)
  rememberImmersive(true)
  assert.equal(immersiveWasOn(), true)
  rememberImmersive(false)
  assert.equal(immersiveWasOn(), false)
})

test('Immersive volume shortcuts toggle mute with M and step 5% with Up/Down', () => {
  assert.equal(immersiveVolumeAction('m'), 'mute')
  assert.equal(immersiveVolumeAction('M'), 'mute')
  assert.equal(immersiveVolumeAction('ArrowUp'), 5)
  assert.equal(immersiveVolumeAction('ArrowDown'), -5)
  assert.equal(immersiveVolumeAction('Space'), null)

  assert.equal(nextImmersiveVolume('mute', 60, 60), 0)
  assert.equal(nextImmersiveVolume('mute', 0, 60), 60)
  assert.equal(nextImmersiveVolume('mute', 0, 0), 25)
  assert.equal(nextImmersiveVolume(5, 98, 98), 100)
  assert.equal(nextImmersiveVolume(-5, 2, 2), 0)
})
