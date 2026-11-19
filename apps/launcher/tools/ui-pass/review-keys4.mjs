import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
const shot = async (n, clip) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png', clip: { ...clip, scale: 3 } }); writeFileSync(join(SHOTS, `${n}.png`), Buffer.from(data, 'base64')) }
await p.clickSel('.exo-dc-rail-item.is-server', 1).catch(() => {}); await sleep(1500)
await p.fn(() => document.querySelector('.exo-dc-rail .is-home').focus()); await p.key('Tab'); await sleep(150)
console.log(await p.fn(() => { const a = document.activeElement; return a.getAttribute('aria-label') + ' ' + getComputedStyle(a).outline + ' ' + a.getBoundingClientRect().left + ' ' + a.closest('.exo-dc-rail-list').getBoundingClientRect().left }))
await shot('rk-strip-kb0', { x: 0, y: 50, width: 200, height: 60 })
await p.key('Tab'); await sleep(100); await shot('rk-strip-kb1', { x: 0, y: 50, width: 200, height: 60 })
// home: focus first Home button with Tab? where is Home focus-visible in strip
await p.fn(() => document.querySelector('.exo-dc-rail .is-home').focus()); await p.key('Tab', { shift: true }); await p.key('Tab'); await sleep(100)
console.log('active', await p.fn(() => document.activeElement.getAttribute('aria-label')))
await shot('rk-strip-home', { x: 0, y: 50, width: 200, height: 60 })
// hit areas in strip and home
console.log(await p.fn(() => [...document.querySelectorAll('.exo-dc-rail button')].map(b => { const r = b.getBoundingClientRect(); return b.getAttribute('aria-label') + ' ' + Math.round(r.width) + 'x' + Math.round(r.height) + ' disp ' + getComputedStyle(b).display }).join('\n')))
// badge overlap of last visible with list clip
process.exit(0)
