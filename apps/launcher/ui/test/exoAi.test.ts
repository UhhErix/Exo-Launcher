import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const pcRoom = read('../src/components/gpu/PcRoom.tsx')
const exoAiPanel = read('../src/components/gpu/ExoAiPanel.tsx')
const bestSetup = read('../src/components/gpu/BestSetup.tsx')
const storageRoom = read('../src/components/gpu/StorageRoom.tsx')

test('PcRoom exposes 3 chips and routes Auto Tune and Storage to native full-screen rooms', () => {
  assert.match(pcRoom, /<AutoTuneChip\b/)
  assert.match(pcRoom, /<AutoTuneOverlay\b/)
  assert.match(pcRoom, /<AppsChip\b/)
  assert.match(pcRoom, /<StorageChip\b/)
  assert.match(pcRoom, /<StorageRoom\b/)
  assert.doesNotMatch(pcRoom, /<ExoAiChip\b|<PcMenu title="Exo AI"|<PcMenu title="Storage"|>Devices</)
  assert.match(read('../src/components/shell/TitleBar.tsx'), /id:\s*'devices'/)

  assert.doesNotMatch(pcRoom, /BestChip|AutopilotChip|CompressChip|upscalersOpen|StorageSheet/)
  assert.match(pcRoom, /<PcMenu title="Graphics"[\s\S]*?<MakerPanelRow[\s\S]*?<UpscalersTab/)
  assert.match(storageRoom, /className="exo-storage-room"/)
  assert.match(storageRoom, /<CompressGames\b/)
})

test('PcRoom routes legacy performance, autopilot, drives, and compress links cleanly', () => {
  assert.match(pcRoom, /jump\.tab === 'performance'[\s\S]*?openAutoTune\(false\)/)
  assert.match(pcRoom, /jump\.tab === 'autopilot'[\s\S]*?openAutoTune\(true\)/)
  assert.match(pcRoom, /jump\.tab === 'drives' \|\| jump\.tab === 'compress'[\s\S]*?openStorage\(\)/)
  assert.match(pcRoom, /upscalers:\s*'graphics'/)
  assert.match(pcRoom, /graphics:\s*'graphics'/)
  assert.match(pcRoom, /control:\s*'graphics'/)
  assert.match(pcRoom, /jump\.tab === 'apps'/)
})

test('ExoAiPanel stays a 1-line status footer with on-demand Overrides popover', () => {
  assert.doesNotMatch(exoAiPanel, /exo-ai-auto-apply|exo-ai-drift|Always apply best settings silently|Background drift check|FAN_PRESET_CHOICES|exo-ai-fan-preset|<AutoTuneRows\b|<details|Checking every 30m|Applied automatically|Watching in background/)
  assert.match(exoAiPanel, /ExoAiFooter/)
  assert.match(exoAiPanel, /Exo AI · Active/)
  assert.match(exoAiPanel, /Overrides/)
  assert.match(exoAiPanel, /<Popover\b/)
  assert.match(exoAiPanel, /<AutopilotControls\b/)
  assert.match(exoAiPanel, /<BestSetup\b/)
  assert.match(exoAiPanel, /<SystemGroup\b/)
  assert.match(exoAiPanel, /<NetworkGroup\b/)
  assert.match(exoAiPanel, /<InterruptRows\b/)
})

test('BestSetup hides already-optimal steps while keeping systemTuningNeedsRestart', () => {
  assert.match(bestSetup, /systemTuningNeedsRestart\(items\)/)
  assert.match(bestSetup, /pendingSteps/)
  assert.doesNotMatch(bestSetup, /exo-pc-best-done-toggle|already optimal/)
})
