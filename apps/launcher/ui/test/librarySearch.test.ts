import { test } from 'node:test'
import assert from 'node:assert/strict'
import { matchesFilters, matchesQuery, parseLibraryQuery } from '../src/lib/librarySearch.ts'

const game = (over: Record<string, unknown>) => ({ id: 'x', title: 'X', store: 'steam', installed: true, status: '', deps: [], launchNote: '', ...over }) as never
const forza = game({ id: 'forza', title: 'Forza Horizon 5', tags: ['racing', 'multiplayer', 'co-op'], playtimeMinutes: 0 })
const dirt = game({ id: 'dirt', title: 'DiRT Rally', tags: ['racing', 'single-player'], playtimeMinutes: 600, installed: false })
const halo = game({ id: 'halo', title: 'Halo Infinite', store: 'xbox', tags: ['action', 'co-op'], playtimeMinutes: 1500, lastPlayedUtc: new Date().toISOString() })
const epic = game({ id: 'fn', title: 'Fortnite', store: 'epic', playtimeMinutes: 60 })
const all = [forza, dirt, halo, epic]
const run = (q: string) => all.filter(g => matchesQuery(g, parseLibraryQuery(q))).map(g => (g as { id: string }).id)

// How it could fail: "haven't played" reads "played" as its own filter and finds the opposite; filler words ("games",
// "I") must be in the title so nothing matches; a genre search keeps games Steam knows nothing about.
test('a sentence becomes filters, not title words', () => {
  assert.deepEqual(run("racing games I haven't played"), ['forza'])
  assert.deepEqual(parseLibraryQuery("racing games I haven't played").understood, ['Not played', 'Racing'])
  assert.deepEqual(run('co-op installed'), ['forza', 'halo'])
  assert.deepEqual(run('racing not installed'), ['dirt'])
  assert.deepEqual(run('racing'), ['forza', 'dirt'])
})

test('stores, hours and title words combine', () => {
  assert.deepEqual(run('steam over 5 hours'), ['dirt'])
  assert.deepEqual(run('under 2 hours'), ['forza', 'fn'])
  assert.deepEqual(run('halo'), ['halo'])
  assert.deepEqual(run('forza horizon'), ['forza'])
  assert.deepEqual(run('epic'), ['fn'])
  assert.deepEqual(run('played lately'), ['halo'])
})

// Within a group any filter does (Steam or Xbox); across groups every one must (Steam or Xbox, and installed).
test('filters in a group widen, groups narrow', () => {
  const stores = [{ kind: 'store', store: 'steam' }, { kind: 'store', store: 'xbox' }] as const
  assert.deepEqual(all.filter(g => matchesFilters(g, [...stores])).map(g => (g as { id: string }).id), ['forza', 'dirt', 'halo'])
  assert.deepEqual(all.filter(g => matchesFilters(g, [...stores, { kind: 'installed' }])).map(g => (g as { id: string }).id), ['forza', 'halo'])
  assert.equal(matchesFilters(forza, [{ kind: 'friends' }], { friendsPlaying: new Set(['forza']) }), true)
  assert.equal(matchesFilters(dirt, [{ kind: 'friends' }], { friendsPlaying: new Set(['forza']) }), false)
})
