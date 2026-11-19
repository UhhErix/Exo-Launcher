import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const shotOf = (p) => async (n) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `v-${n}.png`), Buffer.from(data, 'base64')) }
let p = await newPage('void', 1440, 900); let shot = shotOf(p)
await open(p, ''); await room(p, 'Discord'); await sleep(2000)
console.log('add server', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail button, .exo-dc-rail [role=button]')].filter(b => /add a server|add server|join/i.test((b.getAttribute('aria-label')||'') + b.textContent)).map(b => { const r = b.getBoundingClientRect(); return (b.getAttribute('aria-label')||b.textContent.trim()) + '@' + [r.left,r.top,r.width,r.height].map(Math.round).join(',') })))
console.log('section heads', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail h2, .exo-dc-rail h3, .exo-dc-rail [class*=head]')].map(e => e.className + ':' + e.textContent.trim().slice(0,30) + ':' + [...e.querySelectorAll('button')].map(b=>b.getAttribute('aria-label')).join('/'))))
// servers styles: muted vs read
console.log('servers', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail-item.is-server')].map(e => { const n = e.querySelector('*:not(img)'); const cs = getComputedStyle(e); return e.className.replace('exo-dc-rail-item','') + ' | ' + e.textContent.trim().slice(0,22) + ' | col=' + cs.color + ' op=' + cs.opacity + ' fw=' + getComputedStyle(e.querySelector('[class*=name]') || e).fontWeight + ' ncol=' + getComputedStyle(e.querySelector('[class*=name]') || e).color })))
// Escape in find field
const findBtn = await p.fn(() => { const b = [...document.querySelectorAll('.exo-dc-rail button')].find(b => /find|search/i.test(b.getAttribute('aria-label')||'')); if (b) { b.click(); return b.getAttribute('aria-label') } return null })
await sleep(400)
console.log('find', findBtn, 'focus', await p.fn(() => document.activeElement?.tagName + '.' + document.activeElement?.className))
await p.key('Escape'); await sleep(400)
console.log('after esc focus', await p.fn(() => document.activeElement?.tagName + '.' + (document.activeElement?.className||'') + ' ' + (document.activeElement?.getAttribute('aria-label')||'')))
// Play page
const play = await p.fn(() => { const b = [...document.querySelectorAll('.exo-dc-rail button, .exo-dc button')].find(b => /^play$/i.test((b.getAttribute('aria-label')||b.textContent).trim())); if (b) { b.click(); return true } return false })
await sleep(1500); await shot('1440-play'); console.log('play', play)
// forced colours
p = await newPage('forced', 1440, 900); shot = shotOf(p)
await open(p, ''); await room(p, 'Discord'); await sleep(2000); await shot('1440-forced-now')
await p.eval("document.querySelectorAll('.exo-dc-rail-item.is-server')[0].scrollIntoView({block:'nearest'})"); await sleep(300)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(700, 160); await sleep(2500); await shot('1440-forced-server')
console.log('done'); process.exit(0)
