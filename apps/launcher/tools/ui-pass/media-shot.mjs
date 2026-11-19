// The Discord room's message media: open a conversation, scroll up in steps from the newest, shoot each step.
//   EXO_UIPASS_CDP=9393 EXO_UIPASS_SHOTS=... node tools/ui-pass/media-shot.mjs [steps] [extra: viewer|hover]
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const steps = Number(process.argv[2] ?? 5)
const extra = process.argv.slice(3)
await launch()
const p = await newPage('void', 1920, 1080)
await p.view(1920, 1080, { dpr: 1 })
const shot = async (name) => {
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(join(SHOTS, `${name}.png`), Buffer.from(data, 'base64'))
}
await open(p, '')
await room(p, 'Discord'); await sleep(1000)
await p.clickSel('.exo-dc-side-list .exo-dc-item.is-dm', 0).catch(() => {}); await sleep(2000)
const find = `(() => { const c = document.querySelector('.exo-dc-messages, .exo-dc-msg-scroll, [data-dc-scroller]') || [...document.querySelectorAll('*')].filter(e => e.scrollHeight > e.clientHeight + 50 && /auto|scroll/.test(getComputedStyle(e).overflowY) && e.querySelector('.exo-dc-msg, .exo-dc-message')).sort((a, b) => b.clientHeight - a.clientHeight)[0]; return c })()`
for (let i = 0; i < steps; i++) {
  await p.eval(`(() => { const c = ${find}; if (c) c.scrollTop = c.scrollHeight - ${i} * (c.clientHeight - 80) - c.clientHeight })()`)
  await sleep(700); await shot(`media-${i}`)
}
if (extra.includes('viewer')) {
  await p.eval(`(() => { const c = ${find}; c.scrollTop = c.scrollHeight })()`); await sleep(400)
  await p.eval(`(() => { const c = ${find}; const b = c.querySelector('.exo-dc-mosaic .exo-dc-media'); b && b.scrollIntoView({block:'center'}); })()`); await sleep(300)
  await p.clickSel('.exo-dc-mosaic .exo-dc-media', 0).catch((e) => console.log(String(e))); await sleep(900); await shot('media-viewer')
  await p.key('Escape'); await sleep(300)
}
for (const [i, e] of extra.filter((x) => x.startsWith('sel:')).entries()) {
  const [sel, n = '0'] = e.slice(4).split('@')
  await p.eval(`(() => { const x = document.querySelectorAll(${JSON.stringify(sel)})[${n}]; x && x.scrollIntoView({ block: 'center' }) })()`); await sleep(1600)
  if (extra.includes('hover')) { const r = await p.eval(`(() => { const b = document.querySelectorAll(${JSON.stringify(sel)})[${n}]?.getBoundingClientRect(); return b ? { x: b.x + b.width / 2, y: b.y + b.height / 2 } : null })()`); if (r) { await p.mouse(r.x, r.y, 'move'); await sleep(300) } }
  await shot(`media-sel${i}`)
}
console.log(await p.eval(`(() => { const c = ${find}; return c ? c.className + ' ' + c.scrollHeight : 'none' })()`))
console.log(JSON.stringify(await p.eval(`[...document.querySelectorAll('[class*="exo-dc-"]')].slice(0,0)`)))
process.exit(0)
