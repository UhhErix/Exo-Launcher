import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createBeat } from '../src/lib/heartbeatCore.ts'

// How it could fail: a hidden or unfocused Exo keeps beating (the host wakes every second for nothing), the beat does not
// start again when Exo comes back (the watchdog then reports a hang that is not one), or beats pile up on a slow host.
test('the beat runs only while the page is active and starts again when it comes back', async () => {
  let active = false
  let sent = 0
  let live = 0
  const beat = createBeat({
    active: () => active,
    send: () => { sent++; return Promise.resolve() },
    every: () => ++live,
    stop: () => { live-- },
  })
  beat.sync()
  assert.equal(beat.running(), false)
  assert.equal(sent, 0)
  active = true
  beat.sync()
  beat.sync()
  assert.equal(beat.running(), true)
  assert.equal(live, 1, 'one timer however many syncs')
  assert.equal(sent, 1, 'a beat right away')
  active = false
  beat.sync()
  assert.equal(beat.running(), false)
  assert.equal(live, 0)
  await Promise.resolve()
  await Promise.resolve()
  active = true
  beat.sync()
  assert.equal(sent, 2)
  assert.equal(live, 1)
})

test('a beat still on its way is not sent again', async () => {
  let sent = 0
  let run: () => void = () => {}
  let release: () => void = () => {}
  const beat = createBeat({
    active: () => true,
    send: () => { sent++; return new Promise<void>(done => { release = done }) },
    every: fn => { run = fn; return 1 },
    stop: () => {},
  })
  beat.sync()
  run()
  run()
  assert.equal(sent, 1)
  release()
  await Promise.resolve()
  await Promise.resolve()
  run()
  assert.equal(sent, 2)
})

// How it could fail: the page stops beating when Exo loses focus to another Exo window (a file picker, a pop-out) while the
// host's watchdog stays armed, and a false "page stopped responding" report is filed.
test('the page keeps beating without focus and stops only when hidden or minimized', async () => {
  const { beatWanted } = await import('../src/lib/heartbeatCore.ts')
  assert.equal(beatWanted({}), true)
  assert.equal(beatWanted({ exoInactive: 'true' }), true, 'another Exo window in front')
  assert.equal(beatWanted({ exoInactive: 'true', exoHidden: 'false' }), true)
  assert.equal(beatWanted({ exoHidden: 'true', exoInactive: 'true' }), false)
})
