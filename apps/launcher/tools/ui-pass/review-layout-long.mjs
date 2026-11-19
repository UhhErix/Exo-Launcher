import { launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, 'dc=long'); await room(p, 'Discord'); await sleep(1200)
for (const h of [1080, 720, 600]) {
  await p.view(1600, h); await sleep(700)
  console.log(h, await p.fn(() => { const q = s => { const e = document.querySelector(s); if (!e) return null; const b = e.getBoundingClientRect(); return { r: [b.left, b.top, b.width, b.height].map(Math.round).join(','), sh: e.scrollHeight, ch: e.clientHeight, ov: getComputedStyle(e).overflowY } }; return JSON.stringify({ side: q('.exo-dc-side:not(.exo-dc-channels)'), sidelist: q('.exo-dc-side-list'), rail: q('.exo-dc-rail'), raillist: q('.exo-dc-rail-list'), foot: q('.exo-dc-foot'), n: document.querySelectorAll('.exo-dc-rail-item.is-server').length, dms: document.querySelectorAll('.exo-dc-item.is-dm').length }) }))
}
process.exit(0)
