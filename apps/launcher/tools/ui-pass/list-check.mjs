// The Discord room's one list, checked: no Shop row, a DM open from inside a server, Play's rows, the find field giving
// focus back, the open server's row unfilled at rest, its More menu, and the marker under forced colours.
//   node tools/ui-pass/list-check.mjs      (shots in EXO_UIPASS_SHOTS, named list-check-*)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => {
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(join(SHOTS, `list-check-${name}.png`), Buffer.from(data, 'base64'))
}
await p.view(1920, 1080, { dpr: 1 })
await open(p, '')
await room(p, 'Discord'); await sleep(1200)
console.log('rows', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail-home .exo-dc-item.is-big')].map(b => b.textContent).join('|')))
// Find: open, Escape, focus back on the glyph.
await p.clickSel('.exo-dc-dm-label .exo-dc-icon-btn', 0); await sleep(300)
await p.key('Escape'); await sleep(300)
console.log('focus after escape', await p.fn(() => document.activeElement?.getAttribute('aria-label')))
console.log('label tag', await p.fn(() => document.querySelector('.exo-dc-dm-label')?.tagName))
// A server, then the pointer away: the open row has no fill.
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1800)
await p.mouse(1000, 600); await sleep(400)
console.log('open row', await p.fn(() => { const b = document.querySelector('.exo-dc-rail-row.is-open > .exo-dc-rail-item'); const cs = getComputedStyle(b); return [cs.backgroundColor, cs.color, cs.fontWeight, b.querySelector('.exo-dc-rail-chevron svg') ? 'chevron' : ''].join(' ') }))
console.log('channels head shown', await p.fn(() => { const h = document.querySelector('.exo-dc-rail-open .exo-dc-channels-head'); return h ? getComputedStyle(h).display : 'none' }))
console.log('badge names', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail button:has(> .exo-dc-badge)')].slice(0, 6).map(b => b.getAttribute('aria-label')).join(' | ')))
await shot('server-rest')
await p.clickSel('.exo-dc-rail-more', 0); await sleep(600); await shot('server-more')
console.log('menu', await p.fn(() => [...document.querySelectorAll('[role=menu] [role=menuitem]')].slice(0, 6).map(b => b.textContent).join('|')))
await p.key('Escape'); await sleep(400)
// A conversation from inside the server: Home again, the server closed.
await p.clickSel('.exo-dc-rail-home .exo-dc-item.is-dm', 0); await sleep(1500)
console.log('after dm', await p.fn(() => ({ open: !!document.querySelector('.exo-dc-rail-open'), chosen: document.querySelector('.exo-dc-rail-home [aria-current="true"]')?.getAttribute('aria-label') })))
await shot('dm')
// The user menu holds the Shop.
await p.clickSel('.exo-dc-me button', 0).catch(() => {}); await sleep(600)
console.log('me menu has Shop', await p.fn(() => [...document.querySelectorAll('[role=menuitem]')].some(b => b.textContent.trim() === 'Shop')))
await shot('me-menu'); await p.key('Escape'); await sleep(300)
// Forced colours: the marker is the highlight.
const f = await newPage('forced', 1920, 1080)
await f.view(1920, 1080, { forced: true })
await open(f, ''); await room(f, 'Discord'); await sleep(1200)
await f.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1800); await f.mouse(1000, 600); await sleep(300)
console.log('forced marker', await f.fn(() => { const g = document.querySelector('.exo-dc-rail-open .exo-glide'); const cs = getComputedStyle(g, '::before'); return [cs.backgroundColor, cs.opacity, cs.width].join(' ') }))
{ const { data } = await f.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, 'list-check-forced.png'), Buffer.from(data, 'base64')) }
console.log('done')
process.exit(0)
