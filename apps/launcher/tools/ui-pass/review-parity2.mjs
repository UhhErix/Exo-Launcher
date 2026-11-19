import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await p.view(1920, 1080, { dpr: 1 })
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `${name}.png`), Buffer.from(data, 'base64')) }
await open(p, '')
await room(p, 'Discord'); await sleep(1200)
console.log('home list', await p.fn(() => { const l = document.querySelector('.exo-dc-rail-list'); const rail = document.querySelector('.exo-dc-rail'); const a = document.querySelector('.is-add').getBoundingClientRect(); const f = document.querySelector('.exo-dc-foot').getBoundingClientRect(); return JSON.stringify({ ov: getComputedStyle(l).overflowY, sh: l.scrollHeight, ch: l.clientHeight, railOv: getComputedStyle(rail).overflowY, railH: rail.getBoundingClientRect().height, railBottom: rail.getBoundingClientRect().bottom, add: [a.top, a.bottom], foot: [f.top, f.bottom], side: document.querySelector('.exo-dc-side').getBoundingClientRect().bottom }) }))
// hover on home row -> tip
const hov = async (sel, i) => { const r = await p.fn((s, i) => { const b = document.querySelectorAll(s)[i]; const r = b.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 } }, sel, i); await p.mouse(r.x, r.y, 'move'); await sleep(900); return p.fn(() => { const t = document.querySelector('.exo-dc-tip'); if (!t) return null; const r = t.getBoundingClientRect(); return [t.textContent, Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height)].join('|') }) }
console.log('tip home row 0 (voice 2)', await hov('.exo-dc-rail-item.is-server', 0))
await shot('p-home-tip')
console.log('tip home row 4 (voice 7)', await hov('.exo-dc-rail-item.is-server', 4))
await shot('p-home-tip2')
// right-click on a row
const r = await p.fn(() => { const b = document.querySelectorAll('.exo-dc-rail-item.is-server')[2].getBoundingClientRect(); return { x: b.x + 40, y: b.y + 10 } })
await p.rclick(r.x, r.y); await sleep(500); await shot('p-home-ctx')
console.log('menu', await p.fn(() => [...document.querySelectorAll('.exo-dc-menu button, .exo-dc-menu [role=menuitem]')].map(e => e.textContent.trim()).join(' / ')))
await p.key('Escape'); await sleep(300)
// into server, right-click on strip pic
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1500)
const r2 = await p.fn(() => { const b = document.querySelectorAll('.exo-dc-rail-item.is-server')[3].getBoundingClientRect(); return { x: b.x + 14, y: b.y + 14 } })
await p.rclick(r2.x, r2.y); await sleep(500); await shot('p-strip-ctx')
console.log('menu2', await p.fn(() => [...document.querySelectorAll('.exo-dc-menu button, .exo-dc-menu [role=menuitem]')].map(e => e.textContent.trim()).join(' / ')))
await p.key('Escape'); await sleep(300)
// scroll strip to end
await p.fn(() => { const l = document.querySelector('.exo-dc-rail-list'); l.scrollLeft = 9999; return 1 })
await sleep(300); await shot('p-strip-end')
console.log('strip', await p.fn(() => { const l = document.querySelector('.exo-dc-rail-list'); return JSON.stringify({ sw: l.scrollWidth, cw: l.clientWidth }) }))
// folder? and which item is the unread/muted look in strip
console.log('opac', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail-item.is-server')].map(b => b.getAttribute('aria-label') + ':' + getComputedStyle(b.querySelector('img,.exo-dc-initials')).opacity).join('\n')))
console.log('done'); process.exit(0)
