// Music's Immersive on the mock host (Immersive.tsx): entering with F and the button, the art mid-flight, the Bar
// fading away and back, Esc leaving, Reduce motion, and a small window. immersive-*.png in EXO_UIPASS_SHOTS.
import { join } from 'node:path'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const shot = (p, name) => p.shot(join(SHOTS, `immersive-${name}.png`))
const state = (p) => p.fn(() => ({
  immersive: !!document.querySelector('.exo-music-immersive'),
  lyrics: !!document.querySelector('.exo-music-immersive.has-lyrics'),
  bar: document.documentElement.dataset.exoImmersive ?? null,
  barOpacity: getComputedStyle(document.querySelector('.exo-titlebar')).opacity,
  roomInert: document.querySelector('.exo-music')?.inert ?? null,
  vt: document.documentElement.dataset.exoVt ?? null,
}))
const out = {}

const p = await newPage('void', 1920, 1080)
await open(p, '')
await room(p, 'Music'); await sleep(2500)
await shot(p, 'room-1920')
await p.mouse(960, 600)
await p.key('f')
await sleep(120)
out.midFlight = await state(p)
await shot(p, 'enter-mid-1920')
await sleep(400)
out.entered = await state(p)
await shot(p, 'on-1920')
await sleep(3200)
out.idle = await state(p)
await shot(p, 'bar-away-1920')
await p.mouse(960, 40)
await sleep(300)
out.pointerTop = await state(p)
await shot(p, 'bar-back-1920')
await sleep(3000)
await p.key('ArrowDown')
await sleep(300)
out.afterKey = await state(p)
await p.key('Escape')
await sleep(500)
out.escaped = await state(p)
await shot(p, 'left-1920')
// The strip's button, then its own exit control.
await p.clickSel('.exo-music-bar-side button[aria-label="Immersive"]')
await sleep(500)
out.button = await state(p)
await p.clickSel('button[aria-label="Leave full window"]')
await sleep(500)
out.exitControl = await state(p)

for (const [w, h] of [[1280, 720], [1024, 640], [800, 600]]) {
  await p.view(w, h, { dpr: 1 }); await sleep(400)
  await p.mouse(400, 400); await p.key('f'); await sleep(600)
  await shot(p, `on-${w}`)
  await p.key('Escape'); await sleep(500)
}
await p.close()

const r = await newPage('void', 1920, 1080, { reduce: true })
await open(r, '')
await room(r, 'Music'); await sleep(2500)
await r.fn(() => { document.documentElement.dataset.exoMotion = 'reduce' })
await r.clickSel('.exo-music-bar-side button[aria-label="Immersive"]')
await sleep(60)
out.reduceMid = await state(r)
await shot(r, 'reduce-mid-1920')
await sleep(500)
out.reduce = await state(r)
await r.close()

console.log(JSON.stringify(out, null, 1))
process.exit(0)
