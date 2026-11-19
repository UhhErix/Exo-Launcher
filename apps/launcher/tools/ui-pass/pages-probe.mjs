// Lists what can be pressed on a Discord page. node tools/ui-pass/pages-probe.mjs [server|dm|home]
import { launch, newPage, open, room, sleep } from './harness.mjs'
const which = process.argv[2] ?? 'server'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, ''); await room(p, 'Discord'); await sleep(1000)
if (which === 'server') { await sleep(800); await p.clickSel(".exo-dc-rail-item.is-server", 0); await sleep(1800) }
if (which === 'dm') { await p.clickSel('.exo-dc-side-list .exo-dc-item.is-dm', 0); await sleep(1500) }
console.log(await p.fn(() => [...document.querySelectorAll('.exo-dc button, .exo-dc [role=button], .exo-dc [role=menuitem]')].filter(e=>e.getBoundingClientRect().width>0).map(e => `${e.className.toString().split(' ').slice(0,2).join('.')} | ${e.getAttribute('aria-label')||''} | ${e.textContent.trim().slice(0,30)}`).join('\n')))
process.exit(0)
