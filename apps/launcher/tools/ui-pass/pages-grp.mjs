import { launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
await p.clickText('Kai, Ada', '.exo-dc-side-list .exo-dc-item.is-dm'); await sleep(1400)
console.log(await p.fn(() => {
  const a = document.querySelector('.exo-dc-group-members'); const out = []
  for (let e = a; e && e !== document.body && out.length < 6; e = e.parentElement) { const r = e.getBoundingClientRect(); const s = getComputedStyle(e); out.push(`${e.tagName.toLowerCase()}.${[...e.classList].join('.')} ${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)} disp=${s.display} dir=${s.flexDirection} as=${s.alignSelf} ai=${s.alignItems} jc=${s.justifyContent} pos=${s.position} gridArea=${s.gridArea}`) }
  return out.join('\n')
}))
process.exit(0)
