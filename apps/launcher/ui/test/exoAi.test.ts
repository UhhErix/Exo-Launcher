import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const pcRoom = read('../src/components/gpu/PcRoom.tsx')
const exoAiRoom = read('../src/components/gpu/ExoAiRoom.tsx')
const pcPartMenus = read('../src/components/gpu/PcPartMenus.tsx')
const appsRoom = read('../src/components/apps/AppsRoom.tsx')
const exoAiPanel = read('../src/components/gpu/ExoAiPanel.tsx')
const bestSetup = read('../src/components/gpu/BestSetup.tsx')
const storageRoom = read('../src/components/gpu/StorageRoom.tsx')
const titleBar = read('../src/components/shell/TitleBar.tsx')
const pcSections = read('../src/components/gpu/pcSections.ts')

test('Top-bar rooms divide Exo AI, Apps, and PC cleanly without modal bloat', () => {
  assert.match(titleBar, /id:\s*'devices'/)
  assert.match(titleBar, /id:\s*'exo-ai',\s*label:\s*'Exo AI'/)
  assert.match(titleBar, /id:\s*'apps',\s*label:\s*'Apps'/)
  assert.match(titleBar, /id:\s*'system',\s*label:\s*'PC'/)

  assert.match(exoAiRoom, /<ThisPc\b/)
  assert.match(exoAiRoom, /<AutoTuneCinema\b/)
  assert.match(exoAiRoom, /<AutoTuneRows\b/)
  assert.match(exoAiRoom, /<StorageChip\b/)
  assert.match(exoAiRoom, /<StorageRoom\b/)
  assert.match(exoAiRoom, /<PcPartMenus\b/)

  assert.match(appsRoom, /<AppsPanel\b/)

  assert.match(pcRoom, /<DriversSection\b/)
  assert.match(pcRoom, /<ShadersBoard\b/)
  assert.match(pcRoom, /<StorageChip\b/)
  assert.match(pcRoom, /<StorageRoom\b/)
  assert.match(pcRoom, /<PcPartMenus\b/)
  assert.doesNotMatch(pcRoom, /<AutoTuneOverlay\b|<AppsSheet\b|<AutoTuneChip\b|<AppsChip\b/)

  assert.match(pcPartMenus, /<PcMenu title="Graphics"[\s\S]*?<MakerPanelRow[\s\S]*?<UpscalersTab/)
  assert.match(storageRoom, /className="exo-storage-room"/)
  assert.match(storageRoom, /<CompressGames\b/)
})

test('pcLinkRoom and PcRoom route legacy links to Exo AI, Apps, Storage, and part menus cleanly', () => {
  assert.match(pcSections, /if \(tab === 'apps'\) return 'apps'/)
  assert.match(pcSections, /if \(tab === 'performance' \|\| tab === 'autopilot' \|\| tab === 'overview' \|\| tab === 'network'\) return 'exo-ai'/)
  assert.match(pcRoom, /jump\.tab === 'drives' \|\| jump\.tab === 'compress'[\s\S]*?openStorage\(\)/)
  assert.match(pcRoom, /upscalers:\s*'graphics'/)
  assert.match(pcRoom, /graphics:\s*'graphics'/)
  assert.match(pcRoom, /control:\s*'graphics'/)
})

test('ExoAiRoom and ExoAiPanel keep zero Overrides clutter and host Auto Tune controls', () => {
  assert.doesNotMatch(exoAiPanel, /Overrides|<Popover\b|exo-ai-auto-apply|exo-ai-drift|Always apply best settings silently|Background drift check|FAN_PRESET_CHOICES|exo-ai-fan-preset|<AutoTuneRows\b|<details|Checking every 30m|Applied automatically|Watching in background/)
  assert.doesNotMatch(exoAiRoom, /Overrides|<Popover\b/)
  assert.match(exoAiPanel, /ExoAiFooter/)
  assert.match(exoAiPanel, /Exo AI · Active/)
  assert.match(exoAiRoom, /<AutopilotControls\b/)
  assert.match(exoAiRoom, /<BestSetup\b/)
  assert.match(exoAiRoom, /<SystemGroup\b/)
  assert.match(exoAiRoom, /<NetworkGroup\b/)
  assert.match(exoAiRoom, /<InterruptRows\b/)
})

test('BestSetup hides already-optimal steps while keeping systemTuningNeedsRestart', () => {
  assert.match(bestSetup, /systemTuningNeedsRestart\(items\)/)
  assert.match(bestSetup, /pendingSteps/)
  assert.doesNotMatch(bestSetup, /exo-pc-best-done-toggle|already optimal/)
})
