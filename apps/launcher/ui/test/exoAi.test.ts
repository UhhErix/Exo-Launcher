import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const pcRoom = read('../src/components/gpu/PcRoom.tsx')
const exoAiPanel = read('../src/components/gpu/ExoAiPanel.tsx')
const bestSetup = read('../src/components/gpu/BestSetup.tsx')

test('PcRoom exposes 3 chips (Auto Tune, Apps, Storage) while Exo AI lives inside Auto Tune and Devices lives in TitleBar', () => {
  assert.match(pcRoom, /<AutoTuneChip\b/)
  assert.match(pcRoom, /<AutoTuneOverlay\b/)
  assert.match(pcRoom, /<AppsChip\b/)
  assert.match(pcRoom, /<StorageChip\b/)
  assert.doesNotMatch(pcRoom, /<ExoAiChip\b/)
  assert.doesNotMatch(pcRoom, /<PcMenu title="Exo AI"/)
  assert.doesNotMatch(pcRoom, />Devices</)
  assert.match(read('../src/components/shell/TitleBar.tsx'), /id:\s*'devices'/)

  assert.doesNotMatch(pcRoom, /BestChip|AutopilotChip|CompressChip|upscalersOpen/)
  assert.match(pcRoom, /<PcMenu title="Graphics"[\s\S]*?<MakerPanelRow[\s\S]*?<UpscalersTab/)
  assert.match(pcRoom, /<PcMenu title="Storage"[\s\S]*?<StorageSheet/)
})

test('PcRoom routes legacy performance and autopilot links into Auto Tune tabs while keeping Storage and Graphics links', () => {
  assert.match(pcRoom, /jump\.tab === 'performance'[\s\S]*?openAutoTune\('studio'\)/)
  assert.match(pcRoom, /jump\.tab === 'autopilot'[\s\S]*?openAutoTune\('exo-ai'\)/)
  assert.match(pcRoom, /drives:\s*'storage'/)
  assert.match(pcRoom, /compress:\s*'storage'/)
  assert.match(pcRoom, /upscalers:\s*'graphics'/)
  assert.match(pcRoom, /graphics:\s*'graphics'/)
  assert.match(pcRoom, /control:\s*'graphics'/)
  assert.match(pcRoom, /jump\.tab === 'apps'/)
})

test('ExoAiPanel stays streamlined inside Auto Tune with automatic cooling, Autopilot, and collapsible overrides', () => {
  assert.doesNotMatch(exoAiPanel, /exo-ai-auto-apply|exo-ai-drift|Always apply best settings silently|Background drift check|FAN_PRESET_CHOICES|exo-ai-fan-preset|<AutoTuneRows\b/)
  assert.match(exoAiPanel, /Watching in background/)
  assert.match(exoAiPanel, /label="GPU fans"/)
  assert.match(exoAiPanel, /Automatic/)
  assert.match(exoAiPanel, /<AutopilotControls\b/)
  assert.match(exoAiPanel, /<details className="exo-at-details"/)
  assert.match(exoAiPanel, /<BestSetup\b/)
})

test('BestSetup hides already-optimal steps while keeping systemTuningNeedsRestart', () => {
  assert.match(bestSetup, /systemTuningNeedsRestart\(items\)/)
  assert.match(bestSetup, /pendingSteps/)
  assert.doesNotMatch(bestSetup, /exo-pc-best-done-toggle|already optimal/)
})
