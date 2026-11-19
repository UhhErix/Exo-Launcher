// The Bar with its mini players live (a song, a Discord call, a mouse), at several window widths.
//   node tools/ui-pass/serve.mjs   (keep running)
//   node tools/ui-pass/bar.mjs [tag]
import { join } from 'node:path'
import { launch, newPage, open, sleep, SHOTS } from './harness.mjs'

const tag = process.argv[2] ?? 'bar'
await launch()
const mouse = `import('/src/lib/mouseState.ts').then((m) => { m.publishMouseState({ name: 'Pulsar X2', battery: 82, charging: false, low: false, wireless: true }); m.publishMouseProfile('Valorant') })`
for (const [w, h] of [[1920, 1080], [1600, 900], [1280, 720], [1024, 640]]) {
  const p = await newPage('void', w, h)
  await open(p, 'live')
  await p.eval(mouse)
  await sleep(1500)
  await p.shot(join(SHOTS, `${tag}-live-${w}.png`), { x: 0, y: 0, width: w, height: 64 }, 2)
  await p.shot(join(SHOTS, `${tag}-live-${w}-page.png`))
  const boxes = await p.fn(() => [...document.querySelectorAll('.exo-bar > * , .exo-bar-center > *, .exo-bar-side > *')].map((e) => { const b = e.getBoundingClientRect(); return `${e.className.split(' ').slice(0, 3).join('.')} ${Math.round(b.left)}..${Math.round(b.right)} (${Math.round(b.width)})` }))
  console.log(w, boxes.join(' | '))
  await p.close()
}
process.exit(0)
