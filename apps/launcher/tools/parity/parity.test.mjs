import test from 'node:test'
import assert from 'node:assert/strict'
import { join } from 'node:path'
import { ROOMS, STATES, filterStates } from './states.mjs'
import { assertSafeDataDir } from './react-driver.mjs'
import { evaluateNativeOpened } from './native-driver.mjs'
import {
  buildSummary,
  compareCursors,
  compareGlass,
  compareMenus,
  renderHtmlGallery,
  renderMarkdownReport,
  scoreStateResult,
} from './compare.mjs'

test('state catalogue has unique IDs, valid rooms, and full interactive coverage', () => {
  assert.ok(STATES.length >= 45, `expected at least 45 states, got ${STATES.length}`)
  const ids = new Set()
  const kinds = new Set()
  const rooms = new Set()
  for (const s of STATES) {
    assert.ok(s.id && !ids.has(s.id), `duplicate or empty state id: ${s.id}`)
    ids.add(s.id)
    assert.ok(ROOMS.includes(s.room), `unknown room ${s.room} on ${s.id}`)
    assert.ok([1, 2, 3].includes(s.priority), `invalid priority on ${s.id}`)
    assert.ok(s.react && Array.isArray(s.react.steps), `missing react steps on ${s.id}`)
    assert.ok(s.native && typeof s.native.scene === 'string', `missing native scene on ${s.id}`)
    kinds.add(s.kind)
    rooms.add(s.room)
  }
  for (const requiredKind of ['room', 'subpage', 'popover', 'context-menu', 'dropdown', 'modal', 'hover', 'pressed', 'tab']) {
    assert.ok(kinds.has(requiredKind), `missing interaction kind: ${requiredKind}`)
  }
  for (const requiredRoom of ROOMS) {
    assert.ok(rooms.has(requiredRoom), `missing states for room: ${requiredRoom}`)
  }
})

test('filterStates filters by room and state id', () => {
  const lib = filterStates({ rooms: ['library'] })
  assert.ok(lib.length > 0 && lib.every((s) => s.room === 'library'))
  const one = filterStates({ ids: ['library-homedeck', 'store-game-page'] })
  assert.deepEqual(
    one.map((s) => s.id),
    ['library-homedeck', 'store-game-page'],
  )
})

test('assertSafeDataDir refuses installed ExoLauncher folder', () => {
  const installed = join(process.env.LOCALAPPDATA || '', 'ExoLauncher')
  assert.throws(() => assertSafeDataDir(installed), /Refused/)
  assert.throws(() => assertSafeDataDir(join(installed, 'nested')), /Refused/)
  assert.ok(assertSafeDataDir(join(process.env.TEMP || 'C:\\Temp', 'exo-parity-safe')).includes('exo-parity-safe'))
})

test('compareMenus detects missing and extra items case-insensitively', () => {
  const state = { expectMenu: true }
  const eq = compareMenus(state, { opened: true, menuItems: ['Play', 'Properties'] }, { menuItems: ['play', 'Properties'] })
  assert.equal(eq.status, 'pass')
  assert.equal(eq.equal, true)

  const diff = compareMenus(
    state,
    { opened: true, menuItems: ['Play', 'Manage', 'Hide'] },
    { menuItems: ['Play', 'Extra'] },
  )
  assert.equal(diff.status, 'fail')
  assert.deepEqual(diff.missing, ['Manage', 'Hide'])
  assert.deepEqual(diff.extra, ['Extra'])
})

test('compareCursors compares matching probe points and flags mismatches', () => {
  const res = compareCursors(
    {
      cursors: [
        { name: 'bar:search', x: 90, y: 24, cursor: 'pointer' },
        { name: 'ui:0:Play', x: 200, y: 400, cursor: 'pointer' },
      ],
    },
    {
      cursors: [
        { name: 'bar:search', x: 90, y: 24, cursor: 'pointer' },
        { name: 'ui:0:Play', x: 200, y: 400, cursor: 'default', hitKey: 'play-btn' },
      ],
    },
  )
  assert.equal(res.status, 'fail')
  assert.equal(res.checked, 2)
  assert.equal(res.matched, 1)
  assert.equal(res.mismatches.length, 1)
  assert.equal(res.mismatches[0].react, 'pointer')
  assert.equal(res.mismatches[0].native, 'default')
})

test('compareGlass flags missing glass and late-appearing glass', () => {
  const late = compareGlass(
    { expectGlass: true },
    { glass: [{ rect: [0, 0, 100, 40] }] },
    { glass: [{ rect: [0, 0, 100, 40] }], glassAtFrame0: [], glassAppearedLate: true },
  )
  assert.equal(late.status, 'fail')
  assert.equal(late.appearedLate, true)

  const missing = compareGlass({ expectGlass: true }, { glass: [{ rect: [0, 0, 100, 40] }] }, { glass: [] })
  assert.equal(missing.status, 'fail')
})

test('evaluateNativeOpened checks menu, hover, and state summary fields', () => {
  assert.equal(
    evaluateNativeOpened({ menuOpen: true }, { popupMenuOpen: false, elementsCount: 5, stateSummary: {} }, null, []),
    false,
  )
  assert.equal(
    evaluateNativeOpened({ menuOpen: true }, { popupMenuOpen: true, elementsCount: 5, stateSummary: {} }, null, ['Play']),
    true,
  )
  assert.equal(
    evaluateNativeOpened(
      { room: 'Store', storeGameOpen: true },
      { elementsCount: 10, stateSummary: { room: 'Store', storeGameOpen: true } },
      null,
      [],
    ),
    true,
  )
})

test('scoreStateResult and report renderers produce ranked markdown and HTML', () => {
  const checks = {
    opened: { status: 'fail', react: true, native: false, detail: 'failed to open in native' },
    menus: { status: 'fail', reactItems: ['Play', 'Properties'], nativeItems: [], missing: ['Play', 'Properties'], extra: [] },
    cursors: { status: 'fail', checked: 2, matched: 0, mismatches: [{ name: 'card', x: 100, y: 200, react: 'pointer', native: 'default' }] },
    glass: { status: 'pass', reactCount: 2, nativeCount: 2, appearedLate: false, detail: 'react=2, native=2' },
    pixels: { status: 'fail', meanAbsDiff: 42.5, maxCellDiff: 90, worstCells: [], sheet: 'sheets/x.png' },
  }
  const scored = scoreStateResult({ priority: 1 }, checks)
  assert.equal(scored.severity, 'CRITICAL')
  assert.ok(scored.score >= 200)

  const item = {
    id: 'library-homedeck-card-menu',
    room: 'library',
    name: 'Library card menu',
    kind: 'context-menu',
    priority: 1,
    scene: 'library',
    reactShot: 'react/library-homedeck-card-menu.png',
    nativeShot: 'native/library-homedeck-card-menu.png',
    checks,
    ...scored,
  }
  const payload = { generatedAt: '2026-11-22T00:00:00Z', summary: buildSummary([item]), items: [item] }
  const md = renderMarkdownReport(payload)
  assert.ok(md.includes('## Ranked Gap List (Grouped by Room)'))
  assert.ok(md.includes('library-homedeck-card-menu'))
  const html = renderHtmlGallery(payload)
  assert.ok(html.includes('Exo Interactive Parity Gallery'))
  assert.ok(html.includes('library-homedeck-card-menu'))
})
