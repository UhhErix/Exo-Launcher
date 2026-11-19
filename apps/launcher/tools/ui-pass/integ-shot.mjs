// The remade room against its two mock-ups: Now, Friends, a server's first channel, a second server, a DM, people on,
// the head's More menu and the long-lists scenario, at 1920x1080, 1440x900 and 1280x720; Now and a server at 960x600.
//   node tools/ui-pass/integ-shot.mjs [only-width]      (shots in EXO_UIPASS_SHOTS, named integ-*)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const only = Number(process.argv[2] ?? 0)
await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => {
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(join(SHOTS, `integ-${name}.png`), Buffer.from(data, 'base64'))
}
const facts = () => p.fn(() => {
  const box = (e) => { if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)].join(',') }
  const page = document.querySelector('.exo-dc > .exo-dc-stage > :not(.exo-dc-columns, .exo-dc-fit-probe, .exo-dc-popover)')
  const sel = [...document.querySelectorAll('.exo-dc-rail [aria-current="true"]')].map(e => e.textContent.trim().slice(0, 20))
  return JSON.stringify({ list: box(document.querySelector('.exo-dc > .exo-dc-rail')), page: box(page), people: box(document.querySelector('.exo-dc-columns')), you: box(document.querySelector('.exo-dc-foot')), sel })
})
const top = (n) => p.clickSel('.exo-dc-rail .exo-dc-side-list > .exo-dc-item.is-big', n)
const serverAt = async (n) => {
  await p.eval(`document.querySelectorAll('.exo-dc-rail-item.is-server')[${n}].scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await p.clickSel('.exo-dc-rail-item.is-server', n).catch(() => {}); await p.mouse(1000, 500); await sleep(1800)
}
const sizes = [[1920, 1080], [1440, 900], [1280, 720]].filter(([w]) => !only || w === only)
for (const [w, h] of sizes) {
  await p.view(w, h, { dpr: 1 }); await sleep(500)
  await open(p, ''); await room(p, 'Discord'); await sleep(1200); await shot(`${w}-now`); console.log(w, 'now', await facts())
  await top(1); await sleep(1200); await shot(`${w}-friends`); console.log(w, 'friends', await facts())
  await serverAt(0); await shot(`${w}-server`); console.log(w, 'server', await facts())
  const pill = await p.fn(() => !!document.querySelector('.exo-dc-chat-head [aria-pressed]'))
  if (pill) {
    await p.eval(`(() => { const b = [...document.querySelectorAll('.exo-dc-chat-head [aria-pressed]')].pop(); if (b.getAttribute('aria-pressed') !== 'true') b.click() })()`); await sleep(1000)
    await shot(`${w}-people`); console.log(w, 'people', await facts())
    await p.eval(`(() => { const b = [...document.querySelectorAll('.exo-dc-chat-head [aria-pressed]')].pop(); if (b.getAttribute('aria-pressed') === 'true') b.click() })()`); await sleep(500)
  }
  await p.clickSel('.exo-dc-chat-head button[aria-label="More"]', 0).catch(e => console.log('more', e.message)); await sleep(500); await shot(`${w}-more`)
  await p.key('Escape'); await sleep(300)
  await serverAt(2); await shot(`${w}-server2`); console.log(w, 'server2', await facts())
  await p.eval(`document.querySelector('.exo-dc-rail .exo-dc-side-list .exo-dc-item.is-dm').scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await p.clickSel('.exo-dc-rail .exo-dc-side-list .exo-dc-item.is-dm', 0).catch(e => console.log('dm', e.message)); await sleep(1500); await shot(`${w}-dm`); console.log(w, 'dm', await facts())
  await open(p, 'dc=long'); await room(p, 'Discord'); await sleep(1500); await serverAt(0); await shot(`${w}-long`); console.log(w, 'long', await facts())
}
if (!only || only === 960) {
  await p.view(960, 600, { dpr: 1 }); await sleep(500)
  await open(p, ''); await room(p, 'Discord'); await sleep(1200); await shot('960-now'); console.log(960, 'now', await facts())
  await serverAt(0); await shot('960-server'); console.log(960, 'server', await facts())
}
console.log('done')
process.exit(0)
