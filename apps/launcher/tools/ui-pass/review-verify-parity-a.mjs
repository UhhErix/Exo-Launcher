import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await p.view(1920, 1080, { dpr: 1 })
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
// home
console.log('HOME', await p.fn(() => {
  const l = document.querySelector('.exo-dc-rail-list'); const add = document.querySelector('.exo-dc-rail-item.is-add'); const foot = document.querySelector('.exo-dc-foot')
  const hl = document.querySelector('.exo-dc-rail .is-home')
  return JSON.stringify({ list: l.getBoundingClientRect().toJSON(), sh: l.scrollHeight, ch: l.clientHeight, ovy: getComputedStyle(l).overflowY, add: add.getBoundingClientRect().toJSON(), foot: foot?.getBoundingClientRect().toJSON(), homeDisp: getComputedStyle(hl).display, current: document.querySelector('[aria-label="Now"]') ? 'now' : 'other' })
}))
await shot('home')
// hover a server row at home
const r0 = await p.fn(() => { const b = document.querySelectorAll('.exo-dc-rail-item.is-server')[3].getBoundingClientRect(); return { x: b.x + 40, y: b.y + b.height / 2 } })
await p.mouse(r0.x, r0.y, 'move'); await sleep(900)
console.log('HOMETIP', await p.fn(() => { const t = document.querySelector('.exo-dc-tip'); return t ? JSON.stringify(t.getBoundingClientRect().toJSON()) + t.textContent : 'none' }))
await shot('home-tip')
await p.mouse(5, 5, 'move'); await sleep(300)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1600)
console.log('SERVER', await p.fn(() => {
  const l = document.querySelector('.exo-dc-rail-list'); const add = document.querySelector('.exo-dc-rail-item.is-add'); const home = document.querySelector('.exo-dc-rail .is-home')
  const items = [...document.querySelectorAll('.exo-dc-rail-item.is-server')].map(e => { const i = e.querySelector('img,.exo-dc-initials'); return [e.getAttribute('aria-label'), e.className.replace('exo-dc-rail-item ', ''), i && getComputedStyle(i).opacity, Math.round(e.getBoundingClientRect().x), getComputedStyle(e, '::before').content] })
  return JSON.stringify({ sw: l.scrollWidth, cw: l.clientWidth, sbw: getComputedStyle(l).scrollbarWidth, addDisp: getComputedStyle(add).display, addX: add.getBoundingClientRect().x, homeClass: home.className, homeBefore: getComputedStyle(home, '::before').content, homeLabel: home.getAttribute('aria-label'), dms: document.querySelector('.exo-dc-rail-dms') ? getComputedStyle(document.querySelector('.exo-dc-rail-dms')).display : 'absent', items }, null, 0)
}))
await shot('server')
const r = await p.fn(() => { const b = document.querySelector('.exo-dc-rail-list').getBoundingClientRect(); return { x: b.x + 100, y: b.y + b.height / 2 } })
await p.mouse(r.x, r.y, 'move')
await p.send('Input.dispatchMouseEvent', { type: 'mouseWheel', x: r.x, y: r.y, deltaX: 0, deltaY: 400 })
await sleep(600)
console.log('WHEEL', await p.fn(() => document.querySelector('.exo-dc-rail-list').scrollLeft))
await sleep(600)
console.log('STRIPTIP', await p.fn(() => { const t = document.querySelector('.exo-dc-tip'); return t ? JSON.stringify(t.getBoundingClientRect().toJSON()) + t.textContent : 'none' }))
await shot('server-tip')
await p.mouse(900, 600, 'move'); await sleep(200)
await p.clickSel('.exo-dc-rail-item.is-home', 0); await sleep(1500)
console.log('AFTERHOME', await p.fn(() => JSON.stringify({ now: !!document.querySelector('[aria-label="Now"]'), chat: !!document.querySelector('.exo-dc-chat-col'), cur: document.querySelector('.exo-dc-side-list [aria-current="true"]')?.textContent?.slice(0, 40) })))
await shot('after-home')
process.exit(0)
