import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
const shot = async (n, clip) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png', ...(clip ? { clip: { ...clip, scale: 2 } } : {}) }); writeFileSync(join(SHOTS, `${n}.png`), Buffer.from(data, 'base64')) }
const desc = () => { const a = document.activeElement; if (!a || a === document.body) return 'BODY'; const r = a.getBoundingClientRect(); return (a.getAttribute('aria-label') || a.textContent.trim().slice(0, 25)) + ' | ' + String(a.className).slice(0, 30) + ` @${Math.round(r.left)},${Math.round(r.top)}` }
console.log('DOM order', await p.fn(() => [...document.querySelector('main.exo-dc').children].map(c => c.tagName + '.' + String(c.className).slice(0, 40)).join(' > ')))
// tab from Odin (last DM) onward by focusing Add friend... go from last DM
await p.fn(() => { const l = [...document.querySelectorAll('.exo-dc-side-list .exo-dc-item.is-dm')].pop(); l.focus() })
const seq = []
for (let i = 0; i < 60; i++) { await p.key('Tab'); await sleep(30); const d = await p.fn(desc); seq.push(d) }
console.log(seq.join('\n'))
// shift-tab from first rail row
await p.fn(() => document.querySelector('.exo-dc-rail-item.is-server').focus())
await p.key('Tab', { shift: true }); await sleep(50); console.log('shift-tab from first server =>', await p.fn(desc))
// focus first server and screenshot
await p.fn(() => document.querySelector('.exo-dc-rail-item.is-server').focus()); await sleep(100); await shot('rk-focus-server-row', { x: 0, y: 600, width: 320, height: 480 })
await p.fn(() => [...document.querySelectorAll('.exo-dc-rail-item.is-add')].pop().focus()); await sleep(100); await shot('rk-focus-add', { x: 0, y: 700, width: 320, height: 380 })
console.log('add row rect', await p.fn(() => { const a = document.querySelector('.exo-dc-rail-item.is-add'); const r = a.getBoundingClientRect(); const l = document.querySelector('.exo-dc-rail-list').getBoundingClientRect(); const f = document.querySelector('.exo-dc-foot').getBoundingClientRect(); const rail = document.querySelector('.exo-dc-rail'); return JSON.stringify({ add: [r.top, r.bottom], list: [l.top, l.bottom], foot: [f.top, f.bottom], sh: rail.scrollHeight, ch: rail.clientHeight, ov: getComputedStyle(rail).overflowY, lov: getComputedStyle(document.querySelector('.exo-dc-rail-list')).overflowY }) }))
// find input flow
await p.fn(() => document.querySelector('button[aria-label="Find a conversation"]').focus())
await p.key('Enter'); await sleep(200); console.log('after Enter on find:', await p.fn(desc))
await p.key('Escape'); await sleep(200); console.log('after Esc (empty):', await p.fn(desc))
await p.fn(() => document.querySelector('button[aria-label="Find a conversation"]').focus()); await p.key('Enter'); await sleep(100)
await p.key('Tab'); await sleep(200); console.log('after Tab from empty find:', await p.fn(desc), await p.fn(() => !!document.querySelector('.exo-dc-find')))
await p.fn(() => document.querySelector('button[aria-label="Find a conversation"]').focus()); await p.key('Enter'); await sleep(100)
await p.eval(`document.execCommand('insertText', false, 'ad')`); await sleep(200); await p.key('Escape'); await sleep(200); console.log('after Esc with query:', await p.fn(desc))
// new conversation
await p.fn(() => document.querySelector('button[aria-label="New conversation"]').focus()); await p.key('Enter'); await sleep(500)
console.log('after new conv:', await p.fn(desc), await p.fn(() => document.querySelector('.exo-dc-menu.is-start')?.parentElement?.tagName))
await p.key('Escape'); await sleep(200); console.log('after esc new conv:', await p.fn(desc))
console.log('console', JSON.stringify(p.console.filter(c=>/nest|DOM|validate|Warning/.test(c.text)).slice(0,3)))
// server strip
await p.clickSel('.exo-dc-rail-item.is-server', 1).catch(() => {}); await sleep(1500)
await p.fn(() => { const s = [...document.querySelectorAll('.exo-dc-rail-item.is-server')]; s[0].focus() }); await sleep(100)
await shot('rk-strip-focus0', { x: 0, y: 40, width: 320, height: 100 })
console.log('strip', await p.fn(() => { const l = document.querySelector('.exo-dc-rail-list'); const r = l.getBoundingClientRect(); const it = [...l.children].map(c => Math.round(c.getBoundingClientRect().left)).slice(0, 3); return JSON.stringify({ l: [r.left, r.top, r.right, r.bottom], it, sw: l.scrollWidth, cw: l.clientWidth, ov: getComputedStyle(l).overflow }) }))
await p.fn(() => { const l = document.querySelector('.exo-dc-rail-list'); [...l.children].pop().focus() }); await sleep(200)
await shot('rk-strip-focuslast', { x: 0, y: 40, width: 320, height: 100 })
console.log('strip last focus', await p.fn(desc), await p.fn(() => { const l = document.querySelector('.exo-dc-rail-list'); return JSON.stringify([l.scrollLeft, l.scrollWidth, l.clientWidth]) }))
// tab order inside server
await p.fn(() => document.querySelector('.exo-dc-rail .is-home').focus())
const s2 = []; for (let i = 0; i < 20; i++) { await p.key('Tab'); await sleep(30); s2.push(await p.fn(desc)) } console.log(s2.join('\n'))
process.exit(0)
