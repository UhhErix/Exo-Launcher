import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildFrameGenView, routeShortLabel } from '../src/components/game/frameGenWord.ts'
import type { FrameGenRoute, GameFrameGen } from '../src/lib/host.ts'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

function makeRoute(overrides: Partial<FrameGenRoute>): FrameGenRoute {
  return {
    id: 'native_dlss',
    tier: 'native',
    kind: 'native_dlss',
    label: 'Built into the game (DLSS)',
    status: 'active',
    summary: 'Built into the game · DLSS',
    gpuSupported: true,
    gpuNote: 'RTX 4070 supports DLSS Frame Generation.',
    antiCheatVerdict: 'safe',
    antiCheatNote: 'Safe: built into the game.',
    autoEnable: false,
    latencyNote: 'Lowest latency.',
    selectable: true,
    active: true,
    ...overrides,
  }
}

test('FrameGenRow never returns null and GamePage renders one unified line', () => {
  const rowSource = fs.readFileSync(path.join(root, 'src/components/game/FrameGenRow.tsx'), 'utf8')
  const pageSource = fs.readFileSync(path.join(root, 'src/components/GamePage.tsx'), 'utf8')
  const optSource = fs.readFileSync(path.join(root, 'src/components/game/OptimizeLine.tsx'), 'utf8')

  assert.doesNotMatch(rowSource, /\breturn\s+null\b/)
  assert.match(rowSource, /<GameReading\s+name="Frame generation"/)
  assert.equal((pageSource.match(/<FrameGenRow\b/g) ?? []).length, 1)
  assert.doesNotMatch(pageSource, /FrameGenModRow/)
  assert.doesNotMatch(optSource, /name="Smooth Motion"/)
})

test('loading and error states always render a visible Frame generation line', () => {
  const loading = buildFrameGenView(null, null)
  assert.equal(loading.waiting, true)
  assert.equal(loading.word, null)

  const failed = buildFrameGenView({
    ok: false,
    bestRouteId: 'unavailable',
    selectedRouteId: 'unavailable',
    state: 'unread',
    word: 'Could not check',
    good: false,
    bad: true,
    routes: [],
  }, null)
  assert.equal(failed.waiting, false)
  assert.deepEqual(failed.word, { text: 'Could not check', bad: true })
})

test('unavailable route displays exact reason and how-to-enable menu details', () => {
  const reason = 'Off · Anti-Cheat Expert blocks mods'
  const how = 'Install Lossless Scaling on Steam (App 993090) for zero-injection frame generation.'
  const state: GameFrameGen = {
    ok: true,
    bestRouteId: 'unavailable',
    selectedRouteId: 'unavailable',
    state: 'unavailable',
    word: reason,
    good: false,
    bad: false,
    reason,
    howToEnable: how,
    routes: [
      makeRoute({
        id: 'unavailable',
        tier: 'unavailable',
        kind: 'unavailable',
        label: 'Not available',
        status: 'unavailable',
        summary: reason,
        gpuSupported: false,
        selectable: false,
        active: false,
        reason,
        howToEnable: how,
      }),
    ],
  }

  const view = buildFrameGenView(state, null)
  assert.equal(view.word?.text, reason)
  assert.equal(view.triggerLabel, 'Not available')
  assert.ok(view.items.some(i => i.id === 'info:how' && i.label === how))
})

test('Wuthering Waves routes expose best route and selectable fallbacks in menu', () => {
  const state: GameFrameGen = {
    ok: true,
    bestRouteId: 'native_dlss',
    selectedRouteId: 'native_dlss',
    state: 'native',
    word: 'Built into the game · DLSS',
    good: false,
    bad: false,
    routes: [
      makeRoute({ id: 'native_dlss', tier: 'native', kind: 'native_dlss' }),
      makeRoute({
        id: 'driver_smooth_motion',
        tier: 'driver',
        kind: 'driver_smooth_motion',
        label: 'NVIDIA Smooth Motion',
        status: 'available',
        summary: 'NVIDIA Smooth Motion · Off',
        active: false,
      }),
      makeRoute({
        id: 'external_lossless_scaling',
        tier: 'external',
        kind: 'external_lossless_scaling',
        label: 'Lossless Scaling',
        status: 'available',
        summary: 'Lossless Scaling · Ready',
        active: false,
      }),
    ],
  }

  const view = buildFrameGenView(state, null)
  assert.equal(view.word?.text, 'Built into the game · DLSS')
  assert.equal(view.triggerLabel, 'Built in · DLSS')
  assert.equal(view.checkedId, 'route:native_dlss')
  assert.ok(view.items.some(i => i.id === 'route:driver_smooth_motion'))
  assert.ok(view.items.some(i => i.id === 'route:external_lossless_scaling'))
  assert.ok(view.items.some(i => i.id === 'route:off'))
  assert.ok(!view.items.some(i => i.id.startsWith('xess:')))
})

test('Intel XeSS override choices appear only when driver_xess_override route exists', () => {
  const state: GameFrameGen = {
    ok: true,
    bestRouteId: 'native_xess',
    selectedRouteId: 'native_xess',
    state: 'native',
    word: 'Built into the game · XeSS (2x)',
    good: true,
    bad: false,
    routes: [
      makeRoute({
        id: 'native_xess',
        tier: 'native',
        kind: 'native_xess',
        label: 'Built into the game (XeSS)',
        summary: 'Built into the game · XeSS',
      }),
      makeRoute({
        id: 'driver_xess_override',
        tier: 'driver',
        kind: 'driver_xess_override',
        label: 'Intel XeSS-FG override',
        summary: 'Intel XeSS override · 2x',
        choiceValue: '2x',
        choices: ['game', '2x', '3x', '4x'],
      }),
    ],
  }

  const view = buildFrameGenView(state, null)
  assert.equal(routeShortLabel(state.routes[1]), 'XeSS 2x')
  const xessItems = view.items.filter(i => i.id.startsWith('xess:'))
  assert.equal(xessItems.length, 4)
  assert.equal(xessItems.find(i => i.id === 'xess:2x')?.on, true)
})
