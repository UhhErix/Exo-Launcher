import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const manual = read('../src/components/gpu/GpuManualTuning.tsx')
const vfCurve = read('../src/components/gpu/VfCurveEditor.tsx')
const fanCurve = read('../src/components/gpu/FanCurveEditor.tsx')
const pcPartMenus = read('../src/components/gpu/PcPartMenus.tsx')
const hostTs = read('../src/lib/host.ts')
const css = read('../src/styles/gpu-tuning.css')

test('Graphics menu mounts Manual tuning with Auto/Manual, editors, actions, and honest notices', () => {
  assert.match(pcPartMenus, /title="Manual tuning"[\s\S]*?<GpuManualTuning\b/)
  assert.match(manual, /label:\s*'Auto'/)
  assert.match(manual, /label:\s*'Manual'/)
  assert.match(manual, /Locked by your GPU maker/)
  assert.match(manual, /I understand, unlock tuning/)
  assert.match(manual, /<VfCurveEditor\b/)
  assert.match(manual, /<FanCurveEditor\b/)
  assert.match(manual, /Find my undervolt/)
  assert.match(manual, /Reset GPU to factory defaults\?/)
  assert.match(manual, />\s*Apply\s*</)
  assert.match(fanCurve, /Software fan curves stay active while Exo is open/)
  assert.match(vfCurve, /Flatten right/)
})

test('Host contract defines all GPU manual tuning RPCs and push events', () => {
  for (const method of ['gpu.tuning.state', 'gpu.tuning.apply', 'gpu.tuning.reset', 'gpu.tuning.waiver', 'gpu.tuning.test', 'gpu.tuning.undervolt']) {
    assert.match(hostTs, new RegExp(`'${method.replaceAll('.', '\\.')}'`))
  }
  assert.match(hostTs, /'gpu\.tuningChanged':/)
  assert.match(hostTs, /'gpu\.tuningTestProgress':/)
})

test('GPU tuning styles use design tokens with no literal hex or rgba colors', () => {
  assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b/)
  assert.doesNotMatch(css, /rgba?\(/)
})
