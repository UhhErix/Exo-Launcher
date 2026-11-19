// Dumps the computed look of the shipped UI's key parts (and the Bar's icons) as JSON, so mock-ups can be drawn with the
// app's own values.   node tools/ui-pass/serve.mjs (running)   node tools/ui-pass/tokens.mjs <out.json>
import { writeFileSync } from 'node:fs'
import { launch, newPage, open, room, sleep } from './harness.mjs'

const out = process.argv[2] ?? 'tokens.json'
const PROPS = ['backgroundColor', 'backgroundImage', 'border', 'borderRadius', 'boxShadow', 'backdropFilter', 'color', 'fontFamily', 'fontSize', 'fontWeight', 'letterSpacing', 'lineHeight', 'padding', 'height', 'width', 'gap', 'outline']
const grab = (selectors, props) => Object.fromEntries(selectors.map((sel) => {
  const el = document.querySelector(sel)
  if (!el) return [sel, null]
  const cs = getComputedStyle(el)
  const before = getComputedStyle(el, '::before')
  const picked = Object.fromEntries(props.map((p) => [p, cs[p]]).filter(([, v]) => v && v !== 'none' && v !== 'normal' && v !== 'rgba(0, 0, 0, 0)' && v !== '0px'))
  if (before.content && before.content !== 'none') picked['::before'] = Object.fromEntries(['backgroundColor', 'backgroundImage', 'border', 'borderRadius', 'boxShadow', 'backdropFilter', 'inset', 'opacity'].map((p) => [p, before[p]]).filter(([, v]) => v && v !== 'none' && v !== 'rgba(0, 0, 0, 0)' && v !== '0px'))
  return [sel, picked]
}))

await launch()
const p = await newPage('void', 1920, 1080)
await open(p, 'live')
await sleep(1200)
const result = {}
result.library = await p.fn(grab, ['body', '.exo-bar', '.exo-bar-search', '.exo-bar-search-label', '.exo-top-rooms', '.exo-nav-btn', '.exo-nav-btn.is-on', '.exo-titlebar-tools', '.exo-bar-capsule', '.exo-music-mini', '.exo-mini-title', '.exo-mini-artist', '.exo-call', '.exo-continue-card', '.exo-continue-open', '.exo-continue-title', '.exo-continue-meta', '.exo-continue-play', '.exo-shelf-title', '.exo-shelf-count', '.exo-tile', '.exo-tile-hit', '.exo-shelf-sort-trigger', '.exo-gift-card', '.exo-set-btn', '.exo-set-btn.is-primary', '.exo-account-banner'], PROPS)
result.icons = await p.fn(() => Object.fromEntries([...document.querySelectorAll('.exo-nav-btn')].map((b) => [b.textContent.trim(), b.querySelector('svg')?.outerHTML]).concat([
  ['search-mark', document.querySelector('.exo-bar-search svg')?.outerHTML],
  ['capture', document.querySelector('.exo-capture-btn svg')?.outerHTML],
  ['settings', document.querySelector('.exo-settings-btn svg')?.outerHTML],
  ['window', [...document.querySelectorAll('.exo-titlebar-tools svg')].slice(-3).map((s) => s.outerHTML)],
  ['mini', [...document.querySelectorAll('.exo-music-mini svg')].map((s) => s.outerHTML)],
  ['call', [...document.querySelectorAll('.exo-call svg')].map((s) => s.outerHTML)],
  ['play', document.querySelector('.exo-continue-play svg')?.outerHTML],
])))
result.vars = await p.fn(() => { const cs = getComputedStyle(document.documentElement); const names = [...document.styleSheets].flatMap((s) => { try { return [...s.cssRules] } catch { return [] } }).filter((r) => r.selectorText === ':root').flatMap((r) => [...r.style]).filter((n) => n.startsWith('--')); return Object.fromEntries([...new Set(names)].map((n) => [n, cs.getPropertyValue(n).trim()]).filter(([, v]) => v.length < 120)) })
await room(p, 'PC'); await sleep(900)
result.pc = await p.fn(grab, ['.exo-pc-about', '.exo-pc-about-name', '.exo-pc-chips > *', '.exo-pc-fact-value', '.exo-pc-fact-detail', '.exo-pc-fact-note', '.exo-set-item', '.exo-set-item-label', '.exo-set-item-detail', '.exo-glass.is-panel'], PROPS)
await p.clickSel('.exo-settings-btn'); await sleep(900)
result.settings = await p.fn(grab, ['.exo-settings-sheet, [class*="settings"][role="dialog"]', '.exo-set-group-head h3', '.exo-set-item-label', '.exo-switch, [role="switch"]'], PROPS)
writeFileSync(out, JSON.stringify(result, null, 1))
console.log('written', out, Object.keys(result.vars).length, 'vars')
process.exit(0)
