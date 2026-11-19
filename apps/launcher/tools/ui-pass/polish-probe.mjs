// The new-messages label in a server's first channel: Alt+click (Mark unread) a message ten from the end, then the label in
// view, scrolled away below (the pill), and Mark as read.  node tools/ui-pass/polish-probe.mjs [width height]
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const W = Number(process.argv[2] ?? 1440), H = Number(process.argv[3] ?? 900)
await launch()
const p = await newPage('void', W, H)
await p.view(W, H, { dpr: 1 })
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); const f = join(SHOTS, `polish-${W}-${name}.png`); writeFileSync(f, Buffer.from(data, 'base64')); console.log('shot', f) }
await open(p, ''); await room(p, 'Discord'); await sleep(1500)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(2000)
const probe = () => p.fn(() => {
  const s = document.querySelector('.exo-dc-scroll')
  const label = document.querySelector('.exo-dc-new')
  return JSON.stringify({ top: Math.round(s.scrollTop), label: label ? [label.textContent, Math.round(label.getBoundingClientRect().left), Math.round(label.getBoundingClientRect().top)] : null, pill: document.querySelector('.exo-dc-new-bar')?.textContent ?? null })
})
const at = await p.fn(() => {
  const all = [...document.querySelectorAll('.exo-dc-msg:not(.is-mine):not(.is-system)')]
  const m = all[all.length - 10]
  m.scrollIntoView({ block: 'center' })
  m.querySelector('.exo-dc-msg-body').dispatchEvent(new MouseEvent('click', { bubbles: true, altKey: true }))
  return m.dataset.msgId ?? null
})
await sleep(1200); await p.mouse(W - 30, H - 120)
const view = await p.fn(() => { const l = document.querySelector('.exo-dc-new'); if (!l) return false; l.scrollIntoView({ block: 'center' }); return true })
await sleep(900)
console.log('marked', at, await probe()); await shot('new-label')
await p.eval(`(() => { const s = document.querySelector('.exo-dc-scroll'); s.scrollTop = 0 })()`); await sleep(900)
console.log('away', view, await probe()); await shot('new-pill')
await p.clickSel('.exo-dc-new-bar-jump', 0).catch(e => console.log('jump', e.message)); await sleep(1200)
console.log('jumped', await probe())
await p.clickSel('.exo-dc-new-read', 0).catch(e => console.log('read', e.message)); await sleep(900)
console.log('read', await probe())
process.exit(0)
