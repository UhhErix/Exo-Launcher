import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const act = () => { const a = document.activeElement; if (!a || a === document.body) return 'BODY'; return (a.getAttribute('aria-label') || a.textContent.trim().slice(0, 18)) + ' @' + Math.round(a.getBoundingClientRect().top) + ' in ' + (a.closest('nav,main>div')?.className.split(' ')[0]) }
const p = await newPage('void', 1600, 900)
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
await p.fn(() => { const b = [...document.querySelectorAll('.exo-nav-btn')].find(e => e.textContent.includes('Devices')); b.focus() })
const t = []
for (let i = 0; i < 40; i++) { await p.key('Tab'); await sleep(40); t.push(await p.fn(act)) }
console.log(t.join('\n'))
console.log(await p.fn(() => [...document.querySelector('main.exo-dc').children].map(e => e.tagName + '.' + String(e.className).split(' ')[0]).join(' > ')))
console.log(await p.fn(() => { const s = document.querySelector('.exo-dc-side-list'); return s ? s.parentElement.tagName + '.' + s.parentElement.className + ' / in rail: ' + !!s.closest('.exo-dc-rail') : 'none' }))
process.exit(0)
