// The room against its two mock-ups after the polish pass: Now (an open server in the list), a server's first channel
// (new-messages label, column width, head line), reactions' add glyph, the label scrolled away (the pill), a second
// server's head, closing and reopening a server's row, people at 960 (the panel over the page), forced colours.
//   node tools/ui-pass/polish-shot.mjs [width height]     (shots in EXO_UIPASS_SHOTS, named polish-*)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const W = Number(process.argv[2] ?? 1440), H = Number(process.argv[3] ?? 900)
await launch()
const p = await newPage('void', W, H)
await p.view(W, H, { dpr: 1 })
const shot = async (name) => {
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  const file = join(SHOTS, `polish-${W}-${name}.png`)
  writeFileSync(file, Buffer.from(data, 'base64'))
  console.log('shot', file)
}
const facts = (tag) => p.fn((tag) => {
  const box = (e) => { if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)].join(',') }
  const page = document.querySelector('.exo-dc > .exo-dc-stage > :not(.exo-dc-columns, .exo-dc-fit-probe, .exo-dc-popover)')
  const open = [...document.querySelectorAll('.exo-dc-rail-item.is-server[aria-expanded="true"]')].map(e => e.textContent.trim().slice(0, 20))
  const channels = [...document.querySelectorAll('.exo-dc-rail-open .exo-dc-item')].map(e => e.textContent.trim().slice(0, 14)).slice(0, 8)
  const topic = document.querySelector('.exo-dc-chat-head > .exo-dc-chat-topic')?.textContent?.trim() ?? null
  const label = document.querySelector('.exo-dc-new')
  const pill = document.querySelector('.exo-dc-new-bar')
  const me = document.querySelector('.exo-dc-me-who')?.textContent?.trim() ?? null
  const composer = document.querySelector('.exo-dc-compose textarea, .exo-dc-compose [contenteditable], .exo-dc-compose [placeholder]')
  return JSON.stringify({ tag, page: box(page), people: box(document.querySelector('.exo-dc-columns')), peopleOver: !!document.querySelector('.exo-dc-columns.is-over'),
    open, channels, topic, newLabel: label ? [label.textContent, box(label)] : null, pill: pill ? box(pill) : null, me,
    composer: composer?.getAttribute('placeholder') ?? composer?.getAttribute('aria-placeholder') ?? null, add: !!document.querySelector('.exo-dc > .exo-dc-rail > .exo-dc-rail-label [aria-label="Add a server"]') })
}, tag)
const serverAt = async (n) => {
  await p.eval(`document.querySelectorAll('.exo-dc-rail-item.is-server')[${n}].scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await p.clickSel('.exo-dc-rail-item.is-server', n); await p.mouse(W - 40, H - 200); await sleep(1800)
}
const now = async () => { await p.clickSel('.exo-dc-rail .exo-dc-side-list > .exo-dc-item.is-big', 0); await sleep(1500) }

await open(p, ''); await room(p, 'Discord'); await sleep(1500)
console.log('now-fresh', await facts('now-fresh')); await shot('now-fresh')
await serverAt(0); console.log('server', await facts('server')); await shot('server')
// The pointer on a message with reactions: the add glyph after them.
const hovered = await p.fn(() => { const r = [...document.querySelectorAll('.exo-dc-msg')].find(m => m.querySelector('.exo-dc-reactions')); if (!r) return null; r.scrollIntoView({ block: 'center' }); const b = r.getBoundingClientRect(); return [b.left + b.width / 2, b.top + 20] })
if (hovered) { await sleep(400); await p.mouse(hovered[0], hovered[1]); await sleep(500); await shot('reaction-add')
  await p.clickSel('.exo-dc-reaction-add > button', 0).catch(e => console.log('add', e.message)); await sleep(700); await shot('reaction-picker'); console.log('picker', await p.fn(() => !!document.querySelector('.exo-dc-reaction-add .exo-dc-picker'))); await p.key('Escape'); await sleep(300) }
// The label scrolled out of view: the pill to jump there.
await p.eval(`(() => { const s = document.querySelector('.exo-dc-scroll'); s.scrollTop = s.scrollHeight })()`); await sleep(800)
console.log('scrolled', await facts('scrolled')); await shot('label-away')
await p.mouse(W - 40, H - 200)
await now(); console.log('now-open', await facts('now-open')); await shot('now-open')
// The open server's row closes it; pressed again it opens at its last channel.
await p.clickSel('.exo-dc-rail-item.is-server[aria-expanded="true"]', 0); await sleep(800); console.log('closed', await facts('closed')); await shot('now-closed')
await serverAt(0); console.log('reopened', await facts('reopened'))
await serverAt(2); console.log('server2', await facts('server2')); await shot('server2')
await p.eval(`document.querySelector('.exo-dc-rail .exo-dc-side-list .exo-dc-item.is-dm').scrollIntoView({ block: 'nearest' })`); await sleep(300)
await p.clickSel('.exo-dc-rail .exo-dc-side-list .exo-dc-item.is-dm', 0); await sleep(1500); console.log('dm', await facts('dm')); await shot('dm')
// Restart: the open server is remembered.
await open(p, ''); await room(p, 'Discord'); await sleep(1500); console.log('restart', await facts('restart')); await shot('restart')
// People: column at this size, then the panel over the page at 960.
await serverAt(0)
await p.clickSel('.exo-dc-people-pill', 0).catch(e => console.log('pill', e.message)); await sleep(1200); console.log('people', await facts('people')); await shot('people')
await p.clickSel('.exo-dc-people-pill', 0).catch(() => {}); await sleep(500)
await p.view(960, 600, { dpr: 1 }); await sleep(1200)
console.log('960', await facts('960'))
await p.clickSel('.exo-dc-people-pill', 0).catch(e => console.log('pill960', e.message)); await sleep(1200)
{ const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, 'polish-960-people-over.png'), Buffer.from(data, 'base64')) }
console.log('960-over', await facts('960-over'))
await p.key('Escape'); await sleep(500); console.log('960-escape', await facts('960-escape'))
await p.clickSel('.exo-dc-people-pill', 0).catch(() => {}); await sleep(800)
await p.click(500, 400); await sleep(500); console.log('960-outside', await facts('960-outside'))
console.log('done')
process.exit(0)
