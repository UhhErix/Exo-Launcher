import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const overlaySrc = read('../src/components/gpu/AutoTuneOverlay.tsx')
const autoTuneSrc = read('../src/components/gpu/AutoTune.tsx')
const stagesSrc = read('../src/components/gpu/autoTuneStages.ts')
const overlayCss = read('../src/styles/auto-tune-overlay.css')
const systemGroupSrc = read('../src/components/gpu/SystemGroup.tsx')
const pcRoomCss = read('../src/styles/pc-room.css')

test('AutoTuneOverlay runs all 5 safe stages, hosts silicon & Exo AI, and respects reduce-motion/inactive pause', () => {
  for (const id of ['cooling', 'graphics', 'system', 'cleanup', 'drivers']) {
    assert.match(stagesSrc, new RegExp(`id:\\s*'${id}'`))
  }
  assert.match(stagesSrc, /host\.gpuDisplay\('set', \{ display: screen\.id, best: true \}\)/)
  assert.match(stagesSrc, /host\.systemTuning\('apply'\)/)
  assert.match(stagesSrc, /item\.id !== 'bin'/)
  assert.match(stagesSrc, /if \(clean\.message\)[\s\S]*status:\s*'done'/)
  assert.doesNotMatch(stagesSrc, /clean\.message[\s\S]*status:\s*'warn'/)

  assert.match(overlaySrc, /<AutoTuneRows\b[^>]*variant="cards"/)
  assert.match(overlaySrc, /<ExoAiPanel\b/)
  assert.match(overlaySrc, /role="tablist"/)

  assert.doesNotMatch(autoTuneSrc, /Cooler|label="Goal"/)
  assert.match(autoTuneSrc, /'faster'/)
  assert.match(autoTuneSrc, /Tune GPU/)
  assert.match(autoTuneSrc, /Tune CPU/)

  assert.match(overlayCss, /html\[data-exo-inactive="true"\]/)
  assert.match(overlayCss, /:root\[data-exo-motion="reduce"\]/)
  assert.match(pcRoomCss, /\.exo-pc-chip:has\(> svg:first-child\)/)
})

test('SystemGroup maps icons for vrrLfc and privacy tuning keys', () => {
  assert.match(systemGroupSrc, /vrrLfc:\s*<Monitor/)
  assert.match(systemGroupSrc, /privacy:\s*<EyeOff/)
})
