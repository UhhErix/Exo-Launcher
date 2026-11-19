// Where the list's white marker sits against the chosen row's glyph and label, on Now and inside a server.
import { launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1440, 900)
await p.view(1440, 900, { dpr: 1 })
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
const probe = () => p.fn(() => {
  const g = [...document.querySelectorAll('.exo-dc-rail .exo-glide')].find(x => x.querySelector('[aria-current="true"]'))
  const sel = g?.querySelector('[aria-current="true"]'); if (!sel) return 'none'
  const m = getComputedStyle(g, '::before'); const gr = g.getBoundingClientRect(); const r = sel.getBoundingClientRect()
  const icon = sel.querySelector('svg')?.getBoundingClientRect()
  return JSON.stringify({ row: [r.left, r.top, r.width, r.height].map(Math.round), markLeft: Math.round(gr.left + parseFloat(m.left) + parseFloat(g.style.getPropertyValue('--glide-x') || '0')), markW: m.width, icon: icon && Math.round(icon.left), cls: g.className })
})
console.log('now', await probe())
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1800)
console.log('server', await probe())
process.exit(0)
