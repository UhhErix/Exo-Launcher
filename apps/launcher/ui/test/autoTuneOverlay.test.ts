import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const overlaySrc = read('../src/components/gpu/AutoTuneOverlay.tsx')
const stagesSrc = read('../src/components/gpu/autoTuneStages.ts')
const overlayCss = read('../src/styles/auto-tune-overlay.css')
const systemGroupSrc = read('../src/components/gpu/SystemGroup.tsx')
const pcRoomCss = read('../src/styles/pc-room.css')

test('AutoTuneOverlay runs all 5 safe stages, embeds silicon rows, and respects reduce-motion/inactive pause', () => {
  for (const id of ['cooling', 'graphics', 'system', 'cleanup', 'drivers']) {
    assert.match(stagesSrc, new RegExp(`id:\\s*'${id}'`))
  }
  assert.match(stagesSrc, /host\.gpuDisplay\('set', \{ display: screen\.id, best: true \}\)/)
  assert.match(stagesSrc, /host\.systemTuning\('apply'\)/)
  assert.match(stagesSrc, /item\.id !== 'bin'/)
  assert.match(overlaySrc, /<AutoTuneRows\b/)
  assert.match(overlayCss, /html\[data-exo-inactive="true"\]/)
  assert.match(overlayCss, /:root\[data-exo-motion="reduce"\]/)
  assert.match(pcRoomCss, /\.exo-pc-chip:has\(> svg:first-child\)/)
})

test('SystemGroup maps icons for vrrLfc and privacy tuning keys', () => {
  assert.match(systemGroupSrc, /vrrLfc:\s*<Monitor/)
  assert.match(systemGroupSrc, /privacy:\s*<EyeOff/)
})
