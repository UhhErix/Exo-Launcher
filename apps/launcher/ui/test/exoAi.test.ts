import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const exoAiRoom = read('../src/components/gpu/ExoAiRoom.tsx')
const pcPartMenus = read('../src/components/gpu/PcPartMenus.tsx')
const appsRoom = read('../src/components/apps/AppsRoom.tsx')
const exoAiPanel = read('../src/components/gpu/ExoAiPanel.tsx')
const storageRoom = read('../src/components/gpu/StorageRoom.tsx')
const titleBar = read('../src/components/shell/TitleBar.tsx')
const pcSections = read('../src/components/gpu/pcSections.ts')

test('Top-bar rooms host Exo AI and Apps directly with no separate PC room', () => {
  assert.match(titleBar, /id:\s*'devices'/)
  assert.match(titleBar, /id:\s*'exo-ai',\s*label:\s*'Exo AI'/)
  assert.match(titleBar, /id:\s*'apps',\s*label:\s*'Apps'/)
  assert.doesNotMatch(titleBar, /id:\s*'system'/)

  assert.match(exoAiRoom, /<ThisPc\b/)
  assert.match(exoAiRoom, /<AutoTuneCinema\b/)
  assert.match(exoAiRoom, /<AutoTuneRows\b/)
  assert.match(exoAiRoom, /<DriversSection\b/)
  assert.match(exoAiRoom, /<ShadersBoard\b/)
  assert.match(exoAiRoom, /<StorageChip\b/)
  assert.match(exoAiRoom, /<StorageRoom\b/)
  assert.match(exoAiRoom, /<PcPartMenus\b/)
  assert.doesNotMatch(exoAiRoom, /<AutoTuneOverlay\b|<AppsSheet\b|<AutoTuneChip\b|<AppsChip\b/)

  assert.match(appsRoom, /<AppsPanel\b/)

  assert.match(pcPartMenus, /<PcMenu title="Graphics"[\s\S]*?<MakerPanelRow[\s\S]*?<UpscalersTab/)
  assert.match(storageRoom, /className="exo-storage-room"/)
  assert.match(storageRoom, /<CompressGames\b/)
})

test('pcLinkRoom and ExoAiRoom route legacy PC links to Exo AI, Apps, Storage, and part menus cleanly', () => {
  assert.match(pcSections, /return tab === 'apps' \? 'apps' : 'exo-ai'/)
  assert.match(exoAiRoom, /jump\.tab === 'drives' \|\| jump\.tab === 'compress'[\s\S]*?onStorage\(\)/)
  assert.match(exoAiRoom, /upscalers:\s*'graphics'/)
  assert.match(exoAiRoom, /graphics:\s*'graphics'/)
  assert.match(exoAiRoom, /control:\s*'graphics'/)
})

test('ExoAiRoom and ExoAiPanel keep zero Overrides or redundant System/Autopilot walls', () => {
  assert.doesNotMatch(exoAiPanel, /Overrides|<Popover\b|exo-ai-auto-apply|exo-ai-drift|Always apply best settings silently|Background drift check|FAN_PRESET_CHOICES|exo-ai-fan-preset|<AutoTuneRows\b|<details|Checking every 30m|Applied automatically|Watching in background/)
  assert.doesNotMatch(exoAiRoom, /Overrides|<Popover\b|<AutopilotControls\b|<BestSetup\b|<SystemGroup\b|<NetworkGroup\b|<InterruptRows\b/)
  assert.match(exoAiRoom, /Auto Tune complete/)
  assert.match(exoAiRoom, /Run again/)
})
