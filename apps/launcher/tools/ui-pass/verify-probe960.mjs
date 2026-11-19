import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 960, 600)
await p.view(960, 600, { dpr: 1 })
const shot = async (n) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `v-${n}.png`), Buffer.from(data, 'base64')) }
await open(p, ''); await room(p, 'Discord'); await sleep(1500)
console.log('rows', await p.fn(() => [...document.querySelectorAll('.exo-dc-side-list .exo-dc-item.is-dm')].slice(0,6).map(e => { const cs = getComputedStyle(e); return e.className + ' | bg=' + cs.backgroundColor + ' img=' + cs.backgroundImage.slice(0,60) + ' shadow=' + cs.boxShadow.slice(0,60) })))
await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(700, 300); await sleep(1800)
console.log('head', await p.fn(() => [...document.querySelectorAll('.exo-dc-chat-head button')].map(b => (b.getAttribute('aria-label') || b.textContent.trim()) + (b.getAttribute('aria-pressed') ? '[p]' : '') + ':' + Math.round(b.getBoundingClientRect().width))))
await p.clickSel('.exo-dc-chat-head button[aria-label="More"]', 0).catch(e => console.log('more', e.message)); await sleep(600); await shot('960-more')
console.log('menu', await p.fn(() => [...document.querySelectorAll('[role=menu] [role=menuitem], [role=menuitemcheckbox]')].map(e => e.textContent.trim())))
await p.key('Escape'); await sleep(300)
const n = await p.fn(() => document.querySelectorAll('.exo-dc-rail-item.is-server').length)
console.log('servers', n)
await p.eval(`document.querySelectorAll('.exo-dc-rail-item.is-server')[2].scrollIntoView({ block: 'nearest' })`); await sleep(400)
console.log('srv2 box', await p.fn(() => { const r = document.querySelectorAll('.exo-dc-rail-item.is-server')[2].getBoundingClientRect(); return [r.top, r.bottom, r.left, r.right].map(Math.round).join(',') }))
try { await p.clickSel('.exo-dc-rail-item.is-server', 2); } catch (e) { console.log('click2 err', e.message) }
await sleep(1800); await shot('960-server2')
console.log('sel', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail [aria-current="true"]')].map(e => e.textContent.trim().slice(0, 24))))
await open(p, 'dc=long'); await room(p, 'Discord'); await sleep(1500); await shot('960-long-now')
await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(700, 300); await sleep(1800); await shot('960-long')
console.log('done'); process.exit(0)
