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
const tokensCss = read('../src/tokens.css')

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

test('Liquid Glass renders real backdrop-filter blur over AMOLED black surfaces without fake gradient sheens', () => {
  assert.match(tokensCss, /--glassy-overlay:\s*rgb\(0 0 0 \/ 0\.36\)/)
  assert.doesNotMatch(tokensCss, /--glassy-fresnel-rim|--glassy-control-sheen|--exo-surface-control/)
  assert.doesNotMatch(glassCss, /--glassy-fresnel-rim|--glassy-control-sheen|--exo-surface-control|mask-composite/)
  const blurBlock = /:where\(([\s\S]*?)\n\)\s*\{\s*backdrop-filter:\s*var\(--exo-control-glass\);/m.exec(glassCss)?.[1] ?? ''
  assert.ok(blurBlock.length > 0)
  assert.match(blurBlock, /\.exo-badge\b/)
  assert.match(blurBlock, /\.exo-game-close\b/)
  assert.match(blurBlock, /\.exo-dc-msg-actions\b/)
  assert.doesNotMatch(blurBlock, /\.exo-set-btn/)
  assert.doesNotMatch(blurBlock, /\.exo-pc-chip/)
  assert.doesNotMatch(blurBlock, /\.exo-field\b/)
})
