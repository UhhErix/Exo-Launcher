// The Discord room after the reviewer's ten items, at one size: a conversation with its person (column, or the panel over
// the page at 960), the head's muted line, the list (thread glyph, plain names, the newest eight and All conversations),
// date labels and the new-messages pill, media that would not load, and forced colours.
//   node tools/ui-pass/dcfix-shots.mjs [width height] [scenario]   (shots in EXO_UIPASS_SHOTS, named dcfix-<w>[-sc]-*)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const W = Number(process.argv[2] ?? 1440), H = Number(process.argv[3] ?? 900), SC = process.argv[4] ?? ''
const ONLY = (process.env.DCFIX_ONLY ?? '').split(',').filter(Boolean)
const want = (k) => !ONLY.length || ONLY.includes(k)
await launch()
const tag = `${W}${SC ? '-' + SC : ''}`
const p = await newPage('void', W, H)
await p.view(W, H, { dpr: 1 })
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); const f = join(SHOTS, `dcfix-${tag}-${name}.png`); writeFileSync(f, Buffer.from(data, 'base64')); console.log('shot', f) }
const box = (sel) => p.fn((s) => { const e = document.querySelector(s); if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)].join(',') }, sel)
const facts = (label) => p.fn((label) => {
  const b = (e) => { if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)].join(',') }
  const col = document.querySelector('.exo-dc > .exo-dc-stage > .exo-dc-chat-col')
  const cols = document.querySelector('.exo-dc > .exo-dc-stage > .exo-dc-columns')
  const head = document.querySelector('.exo-dc-chat-head')
  const topic = document.querySelector('.exo-dc-chat-head > .exo-dc-chat-topic')?.textContent ?? null
  const toggle = document.querySelector('.exo-dc-profile-toggle')
  const servers = [...document.querySelectorAll('.exo-dc-rail .exo-dc-rail-label, .exo-dc-rail .exo-dc-rail-item.is-server')].slice(0, 2).map(e => e.textContent.trim().slice(0, 14) + '@' + b(e))
  const foot = b(document.querySelector('.exo-dc-foot'))
  const rail = document.querySelector('.exo-dc-rail')
  return JSON.stringify({ label, col: b(col), cols: cols ? [cols.className, b(cols)] : null, head: b(head), topic, toggle: toggle ? [toggle.getAttribute('aria-pressed'), b(toggle)] : null, servers, foot, railScroll: rail ? [rail.scrollTop, rail.scrollHeight, rail.clientHeight] : null, docW: document.documentElement.scrollWidth })
}, label)
const dm = async (n = 0) => {
  await p.eval(`document.querySelectorAll('.exo-dc-rail .exo-dc-item.is-dm')[${n}]?.scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await p.clickSel('.exo-dc-rail .exo-dc-item.is-dm', n); await p.mouse(W - 40, H - 200); await sleep(1600)
}

// Into the first server's #welcome (its row toggles it open or shut; a channel row goes there).
const toWelcome = async (q) => {
  await q.eval(`document.querySelector('.exo-dc-rail-item.is-server').scrollIntoView({ block: 'nearest' })`); await sleep(300)
  if (!(await q.eval(`!!document.querySelector('.exo-dc-rail-item.is-server[aria-expanded="true"]')`))) { await q.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1600) }
  await q.eval(`[...document.querySelectorAll('.exo-dc-rail .exo-dc-item')].find(e => e.textContent.trim() === 'welcome')?.scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await q.clickText('welcome', '.exo-dc-rail .exo-dc-item'); await q.mouse(W - 40, H - 200); await sleep(2000)
}
const markUnread = async (q) => {
  await q.waitFor(() => document.querySelectorAll('.exo-dc-msg:not(.is-mine):not(.is-system)').length >= 10)
  await q.fn(() => { const all = [...document.querySelectorAll('.exo-dc-msg:not(.is-mine):not(.is-system)')]; const m = all[all.length - 10]; m.scrollIntoView({ block: 'center' }); m.querySelector('.exo-dc-msg-body').dispatchEvent(new MouseEvent('click', { bubbles: true, altKey: true })) })
  await sleep(1200); await q.mouse(W - 30, H - 120)
  await q.eval(`(() => { const s = document.querySelector('.exo-dc-scroll'); s.scrollTop = 0 })()`); await sleep(900)
}
await open(p, SC ? `dc=${SC}` : ''); await room(p, 'Discord'); await sleep(1800)
if (want('list')) {
  console.log('now', await facts('now')); await shot('now')
  console.log('rows', await p.fn(() => JSON.stringify([...document.querySelectorAll('.exo-dc-rail .exo-dc-item.is-dm, .exo-dc-all-dms')].slice(0, 10).map(e => { const n = e.querySelector('.exo-dc-item-name'); const s = getComputedStyle(n); return [n.textContent.slice(0, 18), Math.round(n.getBoundingClientRect().left), s.color, s.fontWeight] }))))
}

if (want('dm')) {
  await dm(0)
  console.log('dm', await facts('dm')); await shot('dm')
  // The toggle: at a size with room it hides the column; without room it opens the panel, Escape and a press outside close it.
  await p.clickSel('.exo-dc-profile-toggle', 0).catch(e => console.log('toggle', e.message)); await sleep(900)
  console.log('dm-toggled', await facts('dm-toggled')); await shot('dm-toggled')
  if (await p.eval(`!!document.querySelector('.exo-dc-columns.is-over')`)) {
    await p.key('Escape'); await sleep(500); console.log('dm-escape', await facts('dm-escape'))
    await p.clickSel('.exo-dc-profile-toggle', 0); await sleep(800)
    await p.click(Math.round(W * 0.45), Math.round(H * 0.5)); await sleep(500); console.log('dm-outside', await facts('dm-outside'))
  } else { await p.clickSel('.exo-dc-profile-toggle', 0); await sleep(600) }
}

if (want('server')) {
  // A server with its people column, for the same measure as the conversation's person.
  await p.eval(`document.querySelector('.exo-dc-rail-item.is-server').scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(W - 40, H - 200); await sleep(1800)
  await shot('server')
  const pressed = await p.eval(`document.querySelector('.exo-dc-people-pill')?.getAttribute('aria-pressed')`)
  if (pressed !== 'true') { await p.clickSel('.exo-dc-people-pill', 0).catch(e => console.log('pill', e.message)); await sleep(1200) }
  console.log('people', await facts('people')); await shot('people')
  await p.clickSel('.exo-dc-people-pill', 0).catch(() => {}); await sleep(600)
  if (await p.eval(`!!document.querySelector('.exo-dc-columns.is-over')`)) { await p.key('Escape'); await sleep(400) }
}

if (want('thread')) {
  // The thread's row under #general: its glyph against the channels' glyphs.
  if (!(await p.eval(`!!document.querySelector('.exo-dc-thread-item')`))) {
    await p.eval(`document.querySelector('.exo-dc-rail-item.is-server').scrollIntoView({ block: 'nearest' })`); await sleep(300)
    await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(W - 40, H - 200); await sleep(1800)
  }
  await p.eval(`document.querySelector('.exo-dc-thread-item')?.scrollIntoView({ block: 'center' })`); await sleep(400)
  console.log('thread', await p.fn(() => JSON.stringify([...document.querySelectorAll('.exo-dc-rail .exo-dc-chan .exo-dc-item, .exo-dc-rail .exo-dc-thread-item')].slice(0, 6).map(e => { const g = e.querySelector('.exo-dc-hash svg, .exo-dc-hash'); const r = g?.getBoundingClientRect(); return [e.textContent.trim().slice(0, 16), r ? Math.round(r.left) : null, r ? Math.round(r.width) : null] }))))
  const t = await p.fn(() => { const e = document.querySelector('.exo-dc-thread-item'); if (!e) return null; const r = e.getBoundingClientRect(); return [r.top] })
  if (t) { const top = Math.max(0, Math.round(t[0]) - 160); const { data } = await p.send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: top, width: 340, height: 300, scale: 1 } }); const f = join(SHOTS, `dcfix-${tag}-thread.png`); writeFileSync(f, Buffer.from(data, 'base64')); console.log('shot', f) }
}

if (want('all')) {
  // All conversations: open in place (the row keeps its place), then closed again.
  await p.eval(`document.querySelector('.exo-dc-all-dms')?.scrollIntoView({ block: 'nearest' })`); await sleep(400)
  const before = await box('.exo-dc-all-dms')
  await p.clickSel('.exo-dc-all-dms', 0).catch(e => console.log('all', e.message)); await sleep(700)
  console.log('all-open', before, await box('.exo-dc-all-dms'), await p.eval(`document.querySelectorAll('.exo-dc-rail .exo-dc-item.is-dm').length`)); await shot('all-open')
  await p.clickSel('.exo-dc-all-dms', 0).catch(e => console.log('all2', e.message)); await sleep(700)
  console.log('all-closed', await box('.exo-dc-all-dms'), await p.eval(`document.querySelectorAll('.exo-dc-rail .exo-dc-item.is-dm').length`))
  // An older conversation you are in stays in the list when All conversations is closed (the long list).
  if (SC === 'long') {
  await p.clickSel('.exo-dc-all-dms', 0); await sleep(600)
  await p.eval(`document.querySelectorAll('.exo-dc-rail .exo-dc-item.is-dm')[30].scrollIntoView({ block: 'center' })`); await sleep(400)
  const older = await p.fn(() => { const rows = [...document.querySelectorAll('.exo-dc-rail .exo-dc-item.is-dm')]; const r = rows[30]; r.click(); return r.textContent.trim().slice(0, 20) }); await sleep(1500)
  await p.eval(`document.querySelector('.exo-dc-all-dms').scrollIntoView({ block: 'center' })`); await sleep(300)
  await p.clickSel('.exo-dc-all-dms', 0); await sleep(700)
  await p.eval(`document.querySelector('.exo-dc-rail').scrollTop = 0`); await sleep(400)
  console.log('older-in', older, await p.fn(() => JSON.stringify([...document.querySelectorAll('.exo-dc-rail .exo-dc-item.is-dm')].map(e => e.textContent.trim().slice(0, 14) + (e.getAttribute('aria-current') === 'true' ? '*' : '')))))
  await shot('older-in')
  }
}

if (want('read')) {
  // #welcome: the new-messages label, then scrolled away (the pill and a date label), then Mark as read clears it all.
  // Mark unread (Alt+click) a message ten from the end, as the reviewer did; then the list's top: the pill over a date label.
  await toWelcome(p); await markUnread(p)
  console.log('pill', await box('.exo-dc-new-bar'), await box('.exo-dc-day'), await p.eval(`document.querySelector('.exo-dc-new-bar')?.textContent ?? null`)); await shot('pill')
  const pill = await p.fn(() => { const b = document.querySelector('.exo-dc-new-bar'); if (!b) return null; const r = b.getBoundingClientRect(); return [Math.round(r.left) - 40, Math.round(r.top) - 30, Math.round(r.width) + 80, 90] })
  if (pill) { const { data } = await p.send('Page.captureScreenshot', { format: 'png', clip: { x: pill[0], y: pill[1], width: pill[2], height: pill[3], scale: 2 } }); const f = join(SHOTS, `dcfix-${tag}-pill-zoom.png`); writeFileSync(f, Buffer.from(data, 'base64')); console.log('shot', f) }
  await p.clickSel('.exo-dc-new-bar-read', 0).catch(e => console.log('read', e.message)); await sleep(800)
  console.log('after-read', await p.eval(`JSON.stringify({ bar: !!document.querySelector('.exo-dc-new-bar'), label: document.querySelector('.exo-dc-new')?.textContent ?? null })`))
  await p.eval(`(() => { const s = document.querySelector('.exo-dc-scroll'); s.scrollTop = s.scrollHeight })()`); await sleep(600)
  await shot('welcome-end')
  console.log('media', await p.fn(() => JSON.stringify({ failed: [...document.querySelectorAll('.exo-dc-media-failed')].map(e => e.textContent), videos: [...document.querySelectorAll('.exo-dc-msg video')].map(v => [v.currentSrc.slice(-30), v.readyState, v.error?.code ?? null, Math.round(v.getBoundingClientRect().height)]) })))
}

if (want('server2')) {
  // A second server: its own name and channels, not Night Raid's.
  await p.eval(`document.querySelectorAll('.exo-dc-rail-item.is-server')[1].scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await p.clickSel('.exo-dc-rail-item.is-server', 1); await p.mouse(W - 40, H - 200); await sleep(1800)
  console.log('server2', await p.fn(() => JSON.stringify({ head: document.querySelector('.exo-dc-chat-head')?.textContent?.slice(0, 80), channels: [...document.querySelectorAll('.exo-dc-rail-open .exo-dc-item, [data-dc-slot] .exo-dc-item')].map(e => e.textContent.trim().slice(0, 16)) })))
  await shot('server2')
}

if (want('fail')) {
  // Every picture and clip fails (faces and icons aside): each becomes a one-line card, nothing is left as an empty box.
  const f = await newPage('void', W, H); await f.view(W, H, { dpr: 1 }); f.failMedia = true
  await open(f, SC ? `dc=${SC}` : ''); await room(f, 'Discord'); await sleep(1500)
  await f.eval(`document.querySelector('.exo-dc-rail .exo-dc-item.is-dm').scrollIntoView({ block: 'nearest' })`); await sleep(300)
  await f.clickSel('.exo-dc-rail .exo-dc-item.is-dm', 0); await f.mouse(W - 40, H - 200); await sleep(2500)
  const holes = () => f.fn(() => JSON.stringify({ cards: [...document.querySelectorAll('.exo-dc-media-failed')].map(e => e.textContent), empty: [...document.querySelectorAll('.exo-dc-msg img, .exo-dc-msg video')].filter(e => { const r = e.getBoundingClientRect(); return r.height > 40 && (e.tagName === 'IMG' ? e.complete && !e.naturalWidth : e.error) }).map(e => e.className + ' ' + (e.currentSrc || e.src).slice(-24)) }))
  console.log('fail-end', await holes())
  { const { data } = await f.send('Page.captureScreenshot', { format: 'png' }); const file = join(SHOTS, `dcfix-${tag}-fail-end.png`); writeFileSync(file, Buffer.from(data, 'base64')); console.log('shot', file) }
  // The gallery and the video further up.
  await f.fn(() => { const m = [...document.querySelectorAll('.exo-dc-msg')].find(x => x.textContent.includes('the whole set')); m?.scrollIntoView({ block: 'center' }) }); await sleep(900)
  console.log('fail-set', await holes())
  { const { data } = await f.send('Page.captureScreenshot', { format: 'png' }); const file = join(SHOTS, `dcfix-${tag}-fail-set.png`); writeFileSync(file, Buffer.from(data, 'base64')); console.log('shot', file) }
  await f.close()
}

if (want('forced')) {
  // Forced colours: the message box's edge and the new-messages pill's.
  const f = await newPage('forced', W, H); await f.view(W, H, { dpr: 1, forced: true })
  await open(f, ''); await room(f, 'Discord'); await sleep(1500)
  await toWelcome(f); await markUnread(f)
  console.log('forced', await f.fn(() => JSON.stringify([...document.querySelectorAll('.exo-dc-compose-panel .exo-dc-input-row, .exo-dc-new-bar')].map(e => { const s = getComputedStyle(e); return [e.className.toString().slice(0, 40), s.borderTopWidth, s.borderTopStyle, s.borderTopColor] }))))
  { const { data } = await f.send('Page.captureScreenshot', { format: 'png' }); const file = join(SHOTS, `dcfix-${tag}-forced.png`); writeFileSync(file, Buffer.from(data, 'base64')); console.log('shot', file) }
  await f.close()
}
process.exit(0)
