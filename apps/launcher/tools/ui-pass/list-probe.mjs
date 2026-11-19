// Probes the Discord room's one list in place: every outline, border or ring drawn inside it (there should be none but
// the things you act on).
//   node tools/ui-pass/list-probe.mjs
import { launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const p = await newPage('void', 1920, 1080)
await p.view(1920, 1080, { dpr: 1 })
await open(p, '')
await room(p, 'Discord'); await sleep(1200)
await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800)
const lines = await p.fn(() => {
  const out = []
  const r = document.querySelector('.exo-dc > .exo-dc-rail')
  for (const e of [r, ...r.querySelectorAll('*')]) for (const pe of ['', '::before', '::after']) {
    const cs = getComputedStyle(e, pe || null)
    if (pe && cs.content === 'none') continue
    const bits = []
    if (cs.boxShadow !== 'none') bits.push('shadow ' + cs.boxShadow)
    if (parseFloat(cs.borderTopWidth) > 0 || parseFloat(cs.borderLeftWidth) > 0) bits.push('border ' + cs.borderTopWidth + ' ' + cs.borderTopStyle + ' ' + cs.borderTopColor)
    if (cs.outlineStyle !== 'none') bits.push('outline ' + cs.outline)
    const box = e.getBoundingClientRect()
    if (bits.length && box.width > 100) out.push(String(e.className?.baseVal ?? e.className) + pe + ' ' + Math.round(box.width) + 'x' + Math.round(box.height) + ' | ' + bits.join(' | '))
  }
  return out.slice(0, 40)
})
console.log(lines.join('\n'))
process.exit(0)
