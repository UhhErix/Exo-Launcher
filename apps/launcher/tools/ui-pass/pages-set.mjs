import { launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
await p.fn(() => window.dispatchEvent(new CustomEvent('exo-discord-settings', { detail: 'profiles' }))); await sleep(1800)
console.log(await p.fn(() => {
  const card = document.querySelector('.exo-dc-pf'); if (!card) return 'no card'
  const out = []
  const walk = (e, d) => { if (d > 3) return; const r = e.getBoundingClientRect(); const s = getComputedStyle(e); out.push(`${' '.repeat(d)}${e.tagName.toLowerCase()}.${[...e.classList].join('.')} ${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)} pos=${s.position} mt=${s.marginTop} ${s.display}`); for (const c of e.children) walk(c, d + 1) }
  walk(card, 0); return out.join('\n')
}))
process.exit(0)
