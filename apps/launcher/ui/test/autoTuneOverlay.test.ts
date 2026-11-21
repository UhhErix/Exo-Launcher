import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const overlaySrc = read('../src/components/gpu/AutoTuneOverlay.tsx')
const autoTuneSrc = read('../src/components/gpu/AutoTune.tsx')
const exoAiSrc = read('../src/components/gpu/ExoAiPanel.tsx')
const stagesSrc = read('../src/components/gpu/autoTuneStages.ts')
const overlayCss = read('../src/styles/auto-tune-overlay.css')
const systemGroupSrc = read('../src/components/gpu/SystemGroup.tsx')
const pcRoomCss = read('../src/styles/pc-room.css')

test('AutoTuneOverlay runs all 6 safe stages, uses native Exo surfaces, and eliminates AI slop', () => {
  for (const id of ['cooling', 'graphics', 'system', 'bloat', 'cleanup', 'drivers']) {
    assert.match(stagesSrc, new RegExp(`id:\\s*'${id}'`))
  }
  assert.match(stagesSrc, /host\.gpuDisplay\('set', \{ display: screen\.id, best: true \}\)/)
  assert.match(stagesSrc, /host\.systemTuning\('playbook'\)/)
  assert.match(stagesSrc, /host\.systemInterrupts\(/)
  assert.match(stagesSrc, /item\.id !== 'bin'/)
  assert.match(stagesSrc, /if \(clean\.message\)[\s\S]*status:\s*'done'/)
  assert.doesNotMatch(stagesSrc, /clean\.message[\s\S]*status:\s*'warn'/)

  assert.match(overlaySrc, /host\.appsPackages\(\)/)
  assert.match(overlaySrc, /className="exo-system-panel exo-at-panel"/)
  assert.match(overlaySrc, /<Progress\b/)
  assert.match(overlaySrc, /<ExoAiFooter\b/)
  assert.doesNotMatch(overlaySrc, /role="tablist"|exo-at-bg-glow|exo-at-bg-grid|exo-at-orbit|exo-at-reactor-card/)

  assert.match(exoAiSrc, /<Popover\b/)
  assert.doesNotMatch(exoAiSrc, /Checking every 30m|Applied automatically|Watching in background|<details/)

  assert.match(autoTuneSrc, /<Progress\b/)
  assert.doesNotMatch(autoTuneSrc, /Cooler|label="Goal"/)
  assert.match(autoTuneSrc, /'faster'/)
  assert.match(autoTuneSrc, /Tune GPU/)
  assert.match(autoTuneSrc, /Tune CPU/)

  assert.match(overlayCss, /var\(--color-bg\)/)
  assert.match(overlayCss, /var\(--exo-type-page\)/)
  assert.match(overlayCss, /:root\[data-exo-motion="reduce"\]/)
  assert.doesNotMatch(overlayCss, /exo-at-bg-glow|exo-at-bg-grid|exo-at-orbit|exo-at-reactor-card/)
  assert.match(pcRoomCss, /\.exo-pc-chip:has\(> svg:first-child\)/)
  assert.doesNotMatch(pcRoomCss, /\.exo-ai-status|\.exo-ai-pills|\.exo-ai-pill|\.exo-storage-section/)
})

test('SystemGroup maps icons for playbook tuning keys', () => {
  assert.match(systemGroupSrc, /vrrLfc:\s*<Monitor/)
  assert.match(systemGroupSrc, /privacy:\s*<EyeOff/)
  assert.match(systemGroupSrc, /tasks:\s*<Clock/)
  assert.match(systemGroupSrc, /winFeatures:\s*<Bolt/)
  assert.match(systemGroupSrc, /osOverhead:\s*<Performance/)
  assert.match(systemGroupSrc, /kernelLatency:\s*<Cpu/)
  assert.match(systemGroupSrc, /usbPower:\s*<Mouse/)
  assert.match(systemGroupSrc, /aiTelemetry:\s*<EyeOff/)
  assert.match(systemGroupSrc, /storageFs:\s*<Server/)
  assert.match(systemGroupSrc, /gamingUx:\s*<Performance/)
  assert.match(systemGroupSrc, /amdGpuPower:\s*<GraphicsCard/)
  assert.match(systemGroupSrc, /hags:\s*<Monitor/)
  assert.match(systemGroupSrc, /mpoOff:\s*<Monitor/)
  assert.match(systemGroupSrc, /hibernateOff:\s*<Clock/)
  assert.match(systemGroupSrc, /backgroundApps:\s*<Bolt/)
  assert.match(systemGroupSrc, /svcHostSplit:\s*<Memory/)
  assert.match(systemGroupSrc, /gpuPstate:\s*<GraphicsCard/)
  assert.match(systemGroupSrc, /cpuMitigations:\s*<Lock/)
})
