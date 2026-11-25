// Room-switch hitch & Devices lifecycle invariants (PR 5).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const glide = read('../src/lib/glide.ts')
const bootGate = read('../src/components/shell/BootGate.tsx')
const devicesRoom = read('../src/components/DevicesRoom.tsx')
const kit = read('../../tools/e2e/lib/kit.mjs')

test('glide skips hidden/backstage placement and only recalculates when data-exo-vt clears', () => {
  assert.match(glide, /if \(!box\.offsetParent \|\| box\.closest\('\.is-backstage, \[hidden\]'\)\) return/)
  assert.match(glide, /const onTransition = \(\) => \{ if \(!document\.documentElement\.hasAttribute\('data-exo-vt'\)\) again\(\) \}/)
})

test('BootGate avoids duplicate getAnimations scan during ViewTransition room switch', () => {
  assert.match(bootGate, /if \(!floating && document\.documentElement\.dataset\.exoVt === 'room'\)/)
})

test('DevicesRoom reads computed styles once per sendRail and decouples ResizeObserver from shown', () => {
  const matches = devicesRoom.match(/getComputedStyle\(document\.documentElement\)/g) ?? []
  assert.equal(matches.length, 1)
  assert.match(devicesRoom, /useEffect\(\(\) => \{\s*if \(!frame\.current\) return[\s\S]*?\}, \[sendRail\]\)/m)
  assert.match(devicesRoom, /useEffect\(\(\) => \{ if \(shown\) sendRail\(\) \}, \[shown, sendRail\]\)/)
})

test('kit.nav switches directly to top-level Bar rooms including Devices', () => {
  assert.match(kit, /export const ROOMS = \[[^\]]*'Devices'[^\]]*\]/)
  assert.match(kit, /export const BAR_ROOMS = ROOMS/)
  assert.match(kit, /async nav\(room\) \{\s*await k\.closeAll\(\)\s*const t0 = Date\.now\(\)/)
})
