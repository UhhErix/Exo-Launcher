import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  hashRiotId,
  leagueModeFromQueue,
  normalizeDurationSeconds,
  normalizeRegionHint,
  normalizeRiotId,
  orderedClusters,
  parseTimestampMs,
  sumMatchRecords,
  valorantModeFromName,
} from '../src/aggregate.js'

test('normalizeRiotId validates Name#TAG bounds and strips whitespace', () => {
  assert.deepEqual(normalizeRiotId('  TenZ # NA1 '), {
    gameName: 'TenZ',
    tagLine: 'NA1',
    normalized: 'TenZ#NA1',
  })
  assert.equal(normalizeRiotId('NoTag'), null)
  assert.equal(normalizeRiotId('#NA1'), null)
  assert.equal(normalizeRiotId('Name#A'), null)
  assert.equal(normalizeRiotId('A#B#C'), null)
  assert.equal(normalizeRiotId('Name#123456789'), null)
})

test('normalizeRegionHint maps clusters and rejects invalid values', () => {
  assert.deepEqual(normalizeRegionHint('eu'), { cluster: 'europe', henrikRegion: 'eu', valid: true })
  assert.deepEqual(normalizeRegionHint('kr'), { cluster: 'asia', henrikRegion: 'kr', valid: true })
  assert.deepEqual(normalizeRegionHint(null), { cluster: 'americas', henrikRegion: null, valid: true })
  assert.equal(normalizeRegionHint('mars').valid, false)
  assert.deepEqual(orderedClusters('europe'), ['europe', 'americas', 'asia'])
})

test('mode mappers match Exo RiotModes names', () => {
  assert.equal(leagueModeFromQueue(420), 'ranked solo')
  assert.equal(leagueModeFromQueue(450), 'aram')
  assert.equal(leagueModeFromQueue(1700), 'arena')
  assert.equal(leagueModeFromQueue(9999), 'other')
  assert.equal(leagueModeFromQueue(0), 'unknown')

  assert.equal(valorantModeFromName('Competitive'), 'competitive')
  assert.equal(valorantModeFromName('Ranked'), 'competitive')
  assert.equal(valorantModeFromName('Spike Rush'), 'spike rush')
  assert.equal(valorantModeFromName('ggteam'), 'team deathmatch')
  assert.equal(valorantModeFromName('CustomMode'), 'other')
  assert.equal(valorantModeFromName(''), 'unknown')
})

test('normalizeDurationSeconds handles seconds and legacy milliseconds safely', () => {
  assert.equal(normalizeDurationSeconds(1800, true), 1800)
  assert.equal(normalizeDurationSeconds(1_800_000, true), 1800)
  assert.equal(normalizeDurationSeconds(1_800_000, false), 1800)
  assert.equal(normalizeDurationSeconds(-10, true), 0)
  assert.equal(normalizeDurationSeconds(0, true), 0)
  assert.equal(parseTimestampMs(1700000000), 1700000000000)
  assert.equal(parseTimestampMs('2026-10-01T12:00:00Z'), Date.parse('2026-10-01T12:00:00Z'))
})

test('sumMatchRecords deduplicates by match id and aggregates per-mode minutes', async () => {
  const result = sumMatchRecords([
    { id: 'm1', seconds: 2400, mode: 'competitive', atMs: 1700000000000 },
    { id: 'm1', seconds: 2400, mode: 'competitive', atMs: 1700000000000 },
    { id: 'm2', seconds: 1200, mode: 'unrated', atMs: 1700005000000 },
    { id: 'm3', seconds: 0, mode: 'unrated', atMs: 1700006000000 },
  ])
  assert.deepEqual(result, {
    minutes: 60,
    matches: 2,
    modes: { competitive: 40, unrated: 20 },
    latestMatchAt: new Date(1700005000000).toISOString(),
  })

  const hashA = await hashRiotId('Player#NA1')
  const hashB = await hashRiotId('player#na1')
  assert.equal(hashA, hashB)
  assert.equal(hashA.length, 32)
})
