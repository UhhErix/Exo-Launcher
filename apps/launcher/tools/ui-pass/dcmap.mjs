// The Discord room's frame as laid out: each structural box with its place, radius, edge and padding.
//   node tools/ui-pass/dcmap.mjs [server|friends|dm]
import { launch, newPage, open, room, sleep } from './harness.mjs'

const which = process.argv[2] ?? 'server'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, '')
await room(p, 'Discord')
await sleep(1200)
if (which === 'server') { await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800) }
const rows = await p.fn(() => {
  const sels = ['.exo-dc', '.exo-dc > *', '.exo-dc-rail > *', '.exo-dc-rail-list > *:nth-child(-n+3)', '.exo-dc-foot > *', '.exo-dc-me > *', '.exo-dc-stage > *', '.exo-dc-stage > * > *', '.exo-dc-columns > *', '.exo-dc-columns > * > *',
    '.exo-dc-chat > *', '.exo-dc-chat-col > *', '.exo-dc-friends > *', '.exo-dc-chat-head > *', '.exo-dc-side-list > *:nth-child(-n+4)', '.exo-dc-channels-list > *:nth-child(-n+6)', '.exo-dc-members-list > *:nth-child(-n+4)']
  const seen = new Set(), out = []
  for (const sel of sels) for (const e of document.querySelectorAll(sel)) {
    if (seen.has(e)) continue; seen.add(e)
    const r = e.getBoundingClientRect(); if (!r.width || !r.height) continue
    const s = getComputedStyle(e)
    const edge = s.boxShadow === 'none' ? '' : 'shadow', border = ['Top', 'Right', 'Bottom', 'Left'].filter(k => parseFloat(s[`border${k}Width`]) > 0).map(k => k[0]).join('')
    out.push(`${Math.round(r.left)},${Math.round(r.top)} ${Math.round(r.width)}x${Math.round(r.height)}  ${e.tagName.toLowerCase()}.${[...e.classList].join('.')}  r=${s.borderRadius} ${edge}${border ? ' border:' + border : ''} bg=${s.backgroundColor === 'rgba(0, 0, 0, 0)' ? '-' : s.backgroundColor} m=${s.margin} p=${s.padding} pos=${s.position}`)
  }
  return out
})
console.log(rows.join('\n'))
process.exit(0)
