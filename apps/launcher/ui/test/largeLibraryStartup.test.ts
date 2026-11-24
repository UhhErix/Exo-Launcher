import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { performance } from 'node:perf_hooks'
import type { Game } from '../src/lib/host.ts'
import { computeFilterFlags, matchesQuery, parseLibraryQuery } from '../src/lib/librarySearch.ts'
import { calculateGridWindow, GRID_OVERSCAN_ROWS, resolveGridCardLayout } from '../src/lib/gridWindow.ts'

const STORES = ['steam', 'epic', 'gog', 'ubisoft', 'rockstar', 'paradox'] as const
const TAGS = ['action', 'rpg', 'strategy', 'indie', 'single-player', 'multiplayer', 'co-op', 'controller']

function buildLargeLibrary(count: number): Game[] {
  const now = Date.now()
  return Array.from({ length: count }, (_, i) => {
    const store = STORES[i % STORES.length]
    const installed = i % 3 === 0
    return {
      id: `${store}:${10000 + i}`,
      title: `Field Test Game ${String(i).padStart(4, '0')}: Edition ${i % 10}`,
      store,
      stores: i % 5 === 0 ? [store, 'steam'] : [store],
      installed,
      owned: true,
      canInstall: !installed,
      status: installed ? 'Ready' : 'Not installed',
      playtimeMinutes: (i * 17) % 12000,
      lastPlayedUtc: i % 4 === 0 ? new Date(now - (i % 30) * 86_400_000).toISOString() : null,
      sizeBytes: installed ? (i + 1) * 100_000_000 : null,
      tags: [TAGS[i % TAGS.length], TAGS[(i + 3) % TAGS.length]],
      updateAvailable: i % 25 === 0,
      familyShared: i % 19 === 0,
    }
  })
}

test('large 600-game multi-store startup derivations and windowing stay well under 200 ms', () => {
  const games = buildLargeLibrary(600)

  const t0 = performance.now()
  const flags = computeFilterFlags(games)
  const filterMs = performance.now() - t0
  assert.equal(flags.installed, true)
  assert.equal(flags.notInstalled, true)
  assert.equal(flags.stores.length, 6)
  assert.ok(filterMs < 200, `computeFilterFlags took ${filterMs.toFixed(1)} ms`)

  const t1 = performance.now()
  const query = parseLibraryQuery('steam co-op installed')
  const matched = games.filter(g => matchesQuery(g, query))
  const searchMs = performance.now() - t1
  assert.ok(matched.length > 0)
  assert.ok(searchMs < 200, `matchesQuery took ${searchMs.toFixed(1)} ms`)

  const t2 = performance.now()
  const layout = resolveGridCardLayout(1440)
  const win = calculateGridWindow({
    itemCount: games.length,
    containerWidth: 1440,
    viewportStart: 0,
    viewportHeight: 900,
    cardWidth: layout.cardWidth,
    cardHeight: layout.cardWidth * 1.5,
    columnGap: 10,
    rowGap: 10,
    paddingInline: 10,
    paddingTop: 8,
    paddingBottom: 40,
    overscanRows: GRID_OVERSCAN_ROWS,
  })
  const windowMs = performance.now() - t2
  assert.ok(win.endIndex - win.startIndex < 100, 'first paint only mounts visible + overscan cards')
  assert.ok(windowMs < 200, `calculateGridWindow took ${windowMs.toFixed(1)} ms`)
})

test('startup shell defers command palette building and background room mounts off critical paint', () => {
  const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')
  const launcherApp = read('../src/components/LauncherApp.tsx')
  const roomController = read('../src/components/shell/useRoomController.ts')
  const shellEvents = read('../src/components/shell/useShellEvents.ts')
  const libraryPane = read('../src/components/shell/LibraryPane.tsx')
  const shelves = read('../src/components/shell/useShelves.ts')

  assert.match(launcherApp, /commandOpen \? buildShellCommands\(/)
  assert.match(roomController, /startTransition\(\(\) => \{\s*setWarming\(true\)/)
  assert.match(roomController, /startTransition\(\(\) => setOpenedRooms\(/)
  assert.match(shellEvents, /startTransition\(\(\) => setGames\(\(prev\) => mergeLibraryUpdate\(/)
  assert.match(libraryPane, /useLibrarySearch\(\s*games,\s*shelfView\(showHidden, props\.hidden, grid\),\s*showHidden,\s*!showingHome/)
  assert.match(shelves, /const continueCollator = new Intl\.Collator/)
})
