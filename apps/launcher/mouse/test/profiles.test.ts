import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  AUTO_SWITCH_DEBOUNCE_MS,
  diffSteps,
  profileMatches,
  profilesEqual,
  ProfileAutoSwitcher,
  stepsMatch,
  WriteRateLimiter,
  type ProfileRecord,
  type ProfileStep,
} from '../src/profile-logic.ts'

function fakeTimers() {
  let now = 0
  let nextId = 1
  const queue = new Map<number, { at: number; fn: () => void }>()
  return {
    scheduler: {
      set(fn: () => void, ms: number) {
        const id = nextId++
        queue.set(id, { at: now + ms, fn })
        return id
      },
      clear(handle: unknown) {
        queue.delete(handle as number)
      },
    },
    advance(ms: number) {
      const target = now + ms
      while (true) {
        let soonest: [number, { at: number; fn: () => void }] | null = null
        for (const entry of queue.entries()) {
          if (entry[1].at <= target && (!soonest || entry[1].at < soonest[1].at)) soonest = entry
        }
        if (!soonest) break
        queue.delete(soonest[0])
        now = soonest[1].at
        soonest[1].fn()
      }
      now = target
    },
    pendingCount() {
      return queue.size
    },
  }
}

test('legacy 4-arg and 2-arg setClickMode steps match 6-arg SLX 1.2.8 readback without rewriting', () => {
  const desktopLegacy: ProfileStep[] = [
    ['setDpi', [1600]],
    ['setPawLodMm', [1]],
    ['setClickMode', [0, 0, 0, 0]],
    ['setTmrActuation', [0.1, 0.1, 220, 250]],
    ['setDongleLedMode', [1]],
    ['setTournamentScrollMode', [0]],
  ]
  const slx128Now: ProfileStep[] = [
    ['setDpi', [1600]],
    ['setPawLodMm', [1]],
    ['setClickMode', [0, 0, 0, 0, 2, 2]],
    ['setTmrActuation', [0.1, 0.1, 220, 250]],
    ['setDongleLedMode', [1]],
    ['setTournamentScrollMode', [0]],
  ]

  assert.deepEqual(diffSteps(desktopLegacy, slx128Now), [])
  assert.equal(profileMatches(desktopLegacy, slx128Now), true)

  assert.equal(stepsMatch(['setClickMode', [0, 0]], ['setClickMode', [0, 0, 0, 0, 0, 0]]), true)
  assert.equal(stepsMatch(['setClickMode', [0, 0, 0, 0, 2, 2]], ['setClickMode', [0, 0, 0, 0, 0, 0]]), true)
  assert.equal(stepsMatch(['setClickMode', [1, 1, 0, 0, 2, 2]], ['setClickMode', [0, 0, 0, 0, 0, 0]]), false)
  assert.equal(stepsMatch(['setClickMode', [1, 1, 0, 0, 2, 2]], ['setClickMode', [1, 1, 0, 0, 0, 0]]), false)
})

test('setTmrActuation matches float rounding and Ultraclick 0.10 mm lock', () => {
  assert.equal(
    stepsMatch(['setTmrActuation', [0.1, 0.29000000000000004, 220, 250]], ['setTmrActuation', [10 / 100, 29 / 100, 220, 250]]),
    true,
  )
  assert.equal(
    stepsMatch(['setTmrActuation', [0.25, 0.25]], ['setTmrActuation', [0.25, 0.25, 220, 220]]),
    true,
  )
  const ultraclickTarget: ProfileStep[] = [
    ['setClickMode', [1, 1, 0, 0, 2, 2]],
    ['setTmrActuation', [0.25, 0.25, 220, 220]],
  ]
  const ultraclickReadback: ProfileStep[] = [
    ['setClickMode', [1, 1, 0, 0, 2, 2]],
    ['setTmrActuation', [0.1, 0.1, 220, 220]],
  ]
  assert.deepEqual(diffSteps(ultraclickTarget, ultraclickReadback), [])
})

test('profilesEqual ignores JSON property key order so identical profiles never rewrite profiles.json', () => {
  const fromDisk: ProfileRecord[] = [
    {
      games: [],
      id: 'desktop-1',
      isDefault: true,
      name: 'Desktop',
      polling: { Wireless: 1000 },
      savedAt: '2026-11-24T14:19:14.991Z',
      steps: [['setDpi', [1600]]],
    },
  ]
  const normalized: ProfileRecord[] = [
    {
      id: 'desktop-1',
      name: 'Desktop',
      steps: [['setDpi', [1600]]],
      savedAt: '2026-11-24T14:19:14.991Z',
      isDefault: true,
      games: [],
      polling: { Wireless: 1000 },
    },
  ]
  assert.equal(profilesEqual(fromDisk, normalized), true)
  assert.equal(profilesEqual(fromDisk, [{ ...normalized[0]!, games: ['riot:valorant'] }]), false)
})

test('ProfileAutoSwitcher debounces >= 2 s, suppresses flaps/unbound games, and writes only on switch to a different profile', () => {
  const clock = fakeTimers()
  const switcher = new ProfileAutoSwitcher({ debounceMs: AUTO_SWITCH_DEBOUNCE_MS, timers: clock.scheduler })

  const desktop: ProfileRecord = {
    id: 'desktop',
    name: 'Desktop',
    isDefault: true,
    games: [],
    savedAt: '2026-11-24T14:19:14.991Z',
    steps: [
      ['setDpi', [1600]],
      ['setClickMode', [0, 0, 0, 0, 0, 0]],
      ['setTmrActuation', [0.1, 0.1, 220, 250]],
    ],
  }
  const fps: ProfileRecord = {
    id: 'fps',
    name: 'FPS',
    games: ['steam:1938090', 'riot:valorant'],
    savedAt: '2026-11-24T14:19:50.881Z',
    steps: [
      ['setDpi', [1600]],
      ['setClickMode', [1, 1, 0, 0, 2, 2]],
      ['setTmrActuation', [0.1, 0.1, 220, 220]],
    ],
  }

  let currentSteps: ProfileStep[] = structuredClone(desktop.steps)
  const writes: Array<{ profile: string; diff: ProfileStep[]; notice: string | null }> = []

  switcher.attach({
    profiles: () => [desktop, fps],
    resolveSteps: p => p.steps,
    currentSteps: () => currentSteps,
    apply: (profile, diff, notice) => {
      writes.push({ profile: profile.id, diff, notice })
      currentSteps = structuredClone(profile.steps)
    },
    gameTitle: id => (id === 'riot:valorant' ? 'VALORANT' : id),
  })
  switcher.setActiveProfile(desktop.id, false)

  switcher.onGameRunState('steam:999999', true)
  clock.advance(5_000)
  assert.equal(writes.length, 0)

  switcher.onGameRunState('riot:valorant', true)
  clock.advance(500)
  switcher.onGameRunState('riot:valorant', false)
  clock.advance(5_000)
  assert.equal(writes.length, 0)

  switcher.onGameRunState('riot:valorant', true)
  clock.advance(1_999)
  assert.equal(writes.length, 0)
  clock.advance(1)
  assert.equal(writes.length, 1)
  assert.equal(writes[0]!.profile, 'fps')
  assert.deepEqual(writes[0]!.diff, [
    ['setClickMode', [1, 1, 0, 0, 2, 2]],
    ['setTmrActuation', [0.1, 0.1, 220, 220]],
  ])
  assert.equal(writes[0]!.notice, 'FPS mouse profile on for VALORANT.')

  switcher.onGameRunState('steam:1938090', true)
  clock.advance(5_000)
  assert.equal(writes.length, 1)

  switcher.onGameRunState('steam:1938090', false)
  clock.advance(5_000)
  assert.equal(writes.length, 1)

  switcher.onGameRunState('riot:valorant', false)
  clock.advance(1_999)
  assert.equal(writes.length, 1)
  clock.advance(1)
  assert.equal(writes.length, 2)
  assert.equal(writes[1]!.profile, 'desktop')
  assert.equal(writes[1]!.notice, 'Desktop mouse profile on.')
})

test('manual user edit cancels pending auto-switch so Exo never overwrites user settings', () => {
  const clock = fakeTimers()
  const switcher = new ProfileAutoSwitcher({ debounceMs: AUTO_SWITCH_DEBOUNCE_MS, timers: clock.scheduler })
  const desktop: ProfileRecord = {
    id: 'desktop',
    name: 'Desktop',
    isDefault: true,
    games: [],
    savedAt: '2026-11-24T14:19:14.991Z',
    steps: [['setDpi', [1600]]],
  }
  const fps: ProfileRecord = {
    id: 'fps',
    name: 'FPS',
    games: ['riot:valorant'],
    savedAt: '2026-11-24T14:19:50.881Z',
    steps: [['setDpi', [800]]],
  }
  let writes = 0
  switcher.attach({
    profiles: () => [desktop, fps],
    resolveSteps: p => p.steps,
    currentSteps: () => [['setDpi', [1600]]],
    apply: () => { writes++ },
    gameTitle: () => 'VALORANT',
  })
  switcher.setActiveProfile(desktop.id, false)

  switcher.onGameRunState('riot:valorant', true)
  clock.advance(1_000)
  switcher.setActiveProfile(null, false)
  clock.advance(10_000)
  assert.equal(writes, 0)
  assert.equal(clock.pendingCount(), 0)
})

test('WriteRateLimiter blocks runaway loops per device and recovers after window elapses', () => {
  let now = 1_000
  const limiter = new WriteRateLimiter({ shortWindowMs: 5_000, maxShort: 3, longWindowMs: 60_000, maxLong: 5, now: () => now })

  assert.equal(limiter.tryAcquire('361d:300').allowed, true)
  assert.equal(limiter.tryAcquire('361d:300').allowed, true)
  assert.equal(limiter.tryAcquire('361d:300').allowed, true)
  assert.equal(limiter.tryAcquire('361d:300').allowed, false)
  assert.equal(limiter.tryAcquire('046d:c54d').allowed, true)

  now += 5_001
  assert.equal(limiter.tryAcquire('361d:300').allowed, true)
  assert.equal(limiter.tryAcquire('361d:300').allowed, true)
  assert.equal(limiter.tryAcquire('361d:300').allowed, false)

  now += 60_001
  assert.equal(limiter.tryAcquire('361d:300').allowed, true)
})
