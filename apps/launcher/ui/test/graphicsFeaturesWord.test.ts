import test from 'node:test'
import assert from 'node:assert/strict'
import { graphicsFeatureStateLabel, graphicsFeatureWord } from '../src/components/game/graphicsFeaturesWord.ts'
import type { GraphicsFeatureRow } from '../src/lib/host.ts'

const baseRow: GraphicsFeatureRow = {
  id: 'rayTracing',
  name: 'Ray tracing',
  state: 'off',
  route: 'Northlight renderer.ini (all GPUs unlocked)',
  routeKind: 'config',
  canToggle: true,
  needsAntiCheatConfirm: false,
}

test('graphicsFeatureStateLabel formats on, off, and set in game states', () => {
  assert.equal(graphicsFeatureStateLabel('on'), 'On')
  assert.equal(graphicsFeatureStateLabel('off'), 'Off')
  assert.equal(graphicsFeatureStateLabel('inGame'), 'Set in game')
})

test('graphicsFeatureWord combines state and route with good flag when on', () => {
  const word = graphicsFeatureWord({ ...baseRow, state: 'on' }, false, null)
  assert.deepEqual(word, { text: 'On · Northlight renderer.ini (all GPUs unlocked)', good: true })
})

test('graphicsFeatureWord shows set in game state clearly without hiding unavailable rows', () => {
  const word = graphicsFeatureWord({ ...baseRow, state: 'inGame', route: 'NVIDIA Reflex built into game', routeKind: 'inGame', canToggle: false }, false, null)
  assert.deepEqual(word, { text: 'Set in game · NVIDIA Reflex built into game', good: false })
})

test('graphicsFeatureWord shows working and error states', () => {
  assert.deepEqual(graphicsFeatureWord(baseRow, true, null), { text: 'Applying', working: true })
  assert.deepEqual(graphicsFeatureWord({ ...baseRow, state: 'on' }, true, null), { text: 'Reverting', working: true })
  assert.deepEqual(graphicsFeatureWord(baseRow, false, 'Close game first.'), { text: 'Close game first.', bad: true })
})
