import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const shotOf = (p) => async (n) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `v-${n}.png`), Buffer.from(data, 'base64')) }
for (const [w, h] of [[960, 600], [1280, 720]]) {
const p = await newPage('void', w, h); const shot = shotOf(p)
await p.view(w, h, { dpr: 1 })
await open(p, ''); await room(p, 'Discord'); await sleep(1500)
await sleep(1500); await p.eval("document.querySelectorAll('.exo-dc-rail-item.is-server')[0].scrollIntoView({block:'nearest'})"); await sleep(300); await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(700, 160); await sleep(2500); console.log(w, 'sel', await p.fn(() => [...document.querySelectorAll('.exo-dc-rail [aria-current="true"]')].map(e => e.textContent.trim().slice(0, 24)))); await shot(w + '-srv-b')
console.log(w, 'heads', await p.fn(() => [...document.querySelectorAll('header')].map(hd => hd.className + ' :: ' + [...hd.querySelectorAll('button')].map(b => (b.getAttribute('aria-label') || b.textContent.trim()).slice(0,20) + (b.hasAttribute('aria-pressed') ? '[p]' : '') + '@' + Math.round(b.getBoundingClientRect().left)).join(' | '))))
const more = await p.fn(() => { const b = [...document.querySelectorAll('.exo-dc-chat-head button')].find(b => /more/i.test(b.getAttribute('aria-label') || '')); if (b) { b.click(); return b.getAttribute('aria-label') } return null })
await sleep(700); await shot(w + '-more-b')
console.log(w, 'more', more, await p.fn(() => [...document.querySelectorAll('[role=menu] *')].filter(e => /menuitem/.test(e.getAttribute('role') || '')).map(e => e.textContent.trim())))
console.log(w, 'menuBox', await p.fn(() => { const m = document.querySelector('[role=menu]'); if (!m) return null; const r = m.getBoundingClientRect(); return [r.left, r.top, r.right, r.bottom].map(Math.round).join(',') }))
await p.close?.()
}
console.log('done'); process.exit(0)
