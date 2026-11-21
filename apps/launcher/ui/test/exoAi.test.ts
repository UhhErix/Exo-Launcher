import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const pcRoom = read('../src/components/gpu/PcRoom.tsx')
const exoAiPanel = read('../src/components/gpu/ExoAiPanel.tsx')
const bestSetup = read('../src/components/gpu/BestSetup.tsx')

test('PcRoom consolidates top chips to Exo AI, Apps, Storage, and Devices and merges Upscalers into Graphics', () => {
  assert.match(pcRoom, /<ExoAiChip\b/)
  assert.match(pcRoom, /<AppsChip\b/)
  assert.match(pcRoom, /<StorageChip\b/)
  assert.match(pcRoom, />Devices</)

  assert.doesNotMatch(pcRoom, /BestChip|AutopilotChip|AutoTuneChip|CompressChip|upscalersOpen/)
  assert.match(pcRoom, /<PcMenu title="Graphics"[\s\S]*?<MakerPanelRow[\s\S]*?<UpscalersTab/)
  assert.match(pcRoom, /<PcMenu title="Storage"[\s\S]*?<StorageSheet/)
})

test('PcRoom preserves legacy PcLink jump routing into Exo AI, Storage, and Graphics', () => {
  assert.match(pcRoom, /performance:\s*'exo-ai'/)
  assert.match(pcRoom, /autopilot:\s*'exo-ai'/)
  assert.match(pcRoom, /drives:\s*'storage'/)
  assert.match(pcRoom, /compress:\s*'storage'/)
  assert.match(pcRoom, /upscalers:\s*'graphics'/)
  assert.match(pcRoom, /graphics:\s*'graphics'/)
  assert.match(pcRoom, /control:\s*'graphics'/)
  assert.match(pcRoom, /jump\.tab === 'apps'/)
})

test('ExoAiPanel unifies fan curves, Auto-Tune, Autopilot, and Best Settings without background toggles', () => {
  assert.doesNotMatch(exoAiPanel, /exo-ai-auto-apply|exo-ai-drift|Always apply best settings silently|Background drift check/)
  assert.match(exoAiPanel, /Watching in background/)
  assert.match(exoAiPanel, /GPU fan speed/)
  assert.match(exoAiPanel, /value:\s*'quiet'/)
  assert.match(exoAiPanel, /value:\s*'balanced'/)
  assert.match(exoAiPanel, /value:\s*'performance'/)
  assert.match(exoAiPanel, /<AutoTuneRows\b/)
  assert.match(exoAiPanel, /<AutopilotControls\b/)
  assert.match(exoAiPanel, /<BestSetup\b/)
})

test('BestSetup hides already-optimal steps while keeping systemTuningNeedsRestart', () => {
  assert.match(bestSetup, /systemTuningNeedsRestart\(items\)/)
  assert.match(bestSetup, /pendingSteps/)
  assert.doesNotMatch(bestSetup, /exo-pc-best-done-toggle|already optimal/)
})
