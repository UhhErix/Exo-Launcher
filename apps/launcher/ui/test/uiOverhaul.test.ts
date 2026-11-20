// UI overhaul invariants (PR 3): Liquid Glass & perf split, Top Bar unification, zero-scroll GameSheet, and Apps room.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

const gamePage = read('../src/components/GamePage.tsx')
const gameCard = read('../src/components/GameCard.tsx')
const gameSections = read('../src/components/game/GameSections.tsx')
const titleBar = read('../src/components/shell/TitleBar.tsx')
const appsPanel = read('../src/components/apps/AppsPanel.tsx')
const appsCss = read('../src/styles/apps.css')
const glassCss = read('../src/styles/glass.css')

test('GamePage removes the side drawer and renders GameStats, GameLines, and GameShared inline', () => {
  assert.doesNotMatch(gamePage, /exo-game-info-pane|exo-game-info-toggle|is-info/)
  assert.match(gamePage, /<GameShared\b/)
  assert.match(gamePage, /<GameStats\b/)
  assert.match(gamePage, /<GameLines\b/)
})

test('GameCard surfaces Steam Family provenance while GameSections keeps it out of numeric stats', () => {
  assert.match(gameCard, /game\.familyShared/)
  assert.match(gameCard, /game\.sharedBy/)
  assert.match(gameSections, /export function GameShared\b/)
  assert.doesNotMatch(gameSections, /<Fact label="Shared by"/)
})

test('TitleBar applies exo-glass across Search, Rooms, and Tools capsules', () => {
  assert.match(titleBar, /className="exo-glass exo-bar-search"/)
  assert.match(titleBar, /className="exo-glass exo-top-rooms exo-glide"/)
  assert.match(titleBar, /className="exo-glass exo-titlebar-tools"/)
})

test('AppsPanel uses concise tab labels and content-visibility optimization in apps.css', () => {
  assert.match(appsPanel, /\{ id: 'windows', label: 'Windows' \}/)
  assert.match(appsPanel, /\{ id: 'startup', label: 'Startup' \}/)
  assert.match(appsCss, /content-visibility:\s*auto/)
})

test('glass.css does not apply backdrop-filter to static black-surface controls', () => {
  const blurBlock = /\/\* True media-overlay plates[\s\S]*?\{[\s\S]*?backdrop-filter:/m.exec(glassCss)?.[0] ?? ''
  assert.ok(blurBlock.length > 0)
  assert.doesNotMatch(blurBlock, /\.exo-set-btn|\.exo-pc-chip|\.exo-field\b/)
  assert.match(glassCss, /background-image:\s*var\(--exo-surface-control\)/)
})
