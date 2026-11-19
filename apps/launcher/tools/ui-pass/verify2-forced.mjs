// Reviewer probe: the message box's border under forced colours.
import { launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const f = await newPage('forced', 1440, 900)
await f.view(1440, 900, { dpr: 1, forced: true })
await open(f, ''); await room(f, 'Discord'); await sleep(1500)
await f.eval(`document.querySelector('.exo-dc-rail-item.is-server').scrollIntoView({block:'nearest'})`); await sleep(300)
await f.clickSel('.exo-dc-rail-item.is-server', 0); await f.mouse(1400, 700); await sleep(2000)
console.log(await f.fn(() => JSON.stringify({ forced: matchMedia('(forced-colors: active)').matches, rows: [...document.querySelectorAll('.exo-dc-input-row, .exo-dc-compose, .exo-dc-compose-panel')].map(e => { const s = getComputedStyle(e); const b = e.getBoundingClientRect(); return [e.className.toString().slice(0, 60), s.borderTopWidth, s.borderTopStyle, s.borderTopColor, s.boxShadow.slice(0, 40), Math.round(b.left), Math.round(b.top), Math.round(b.width), Math.round(b.height)] }) })))
process.exit(0)
