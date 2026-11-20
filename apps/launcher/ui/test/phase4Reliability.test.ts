// Capture, Go Live, hitching & core reliability invariants (PR 4).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const goLive = read('../src/lib/goLive.ts')
const storeData = read('../src/components/store/storeData.ts')
const storeRoom = read('../src/components/store/StoreRoom.tsx')
const composer = read('../src/components/discord/Composer.tsx')
const musicAdapters = read('../src/lib/musicAdapters.ts')
const driverRow = read('../src/components/gpu/DriverRow.tsx')
const storeAccounts = read('../src/components/settings/StoreAccounts.tsx')
const upscalersSheet = read('../src/components/upscalers/UpscalersSheet.tsx')
const glassCss = read('../src/styles/glass.css')

test('startGoLive preserves live UI state during hot-swap failures', () => {
  assert.match(goLive, /const wasLive = goLive\.state === 'live'/)
  assert.match(goLive, /if \(!wasLive\) setGoLive\(\{ state: 'connecting'/)
  assert.match(goLive, /if \(!wasLive\) setGoLive\(\{ state: 'off' \}\)/)
})

test('Store stops backstage dwell timers and avoids render-phase setLastPage', () => {
  assert.match(storeData, /if \(card\.closest\('\.is-backstage'\)\) \{ settle\(card\); return \}/)
  assert.doesNotMatch(storeData, /if \(card\.closest\('\.is-backstage'\)\) \{ dwell\(card\); return \}/)
  assert.match(storeRoom, /useEffect\(\(\) => \{ if \(page\) setLastPage\(page\) \}, \[page\]\)/)
})

test('Composer registers exo-dc-mention listener once per mount', () => {
  const block = /window\.addEventListener\('exo-dc-mention'[\s\S]*?\}, \[\]\)/m
  assert.match(composer, block)
})

test('musicAdapters leave() guards post-await epoch bump against concurrent entry', () => {
  assert.match(musicAdapters, /const leaveEpoch = \+\+epoch/)
  assert.match(musicAdapters, /epoch === leaveEpoch && snapshot\.ok/)
})

test('DriverRow, StoreAccounts, and UpscalersSheet avoid silent or false-positive failure states', () => {
  assert.match(driverRow, /state\.installed && !msg\.includes\(state\.installed\)/)
  assert.match(storeAccounts, /if \(next !== null\)/)
  assert.match(upscalersSheet, /scanFailed > 0/)
})

test('glass.css bypasses large pane backdrop-filter during root ViewTransitions', () => {
  const vtBlock = /:root\[data-exo-vt\][\s\S]*?\.exo-device-card\.exo-glass\.is-panel[\s\S]*?backdrop-filter:\s*none !important/m
  assert.match(glassCss, vtBlock)
})
