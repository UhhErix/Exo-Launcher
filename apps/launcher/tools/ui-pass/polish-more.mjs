// The polish pass's remaining states: the long-lists Now at 960x600 (a voice card whose picture fails), a muted server's
// row, and a server under forced colours at 1440x900.  node tools/ui-pass/polish-more.mjs
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const save = async (p, name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); const f = join(SHOTS, `polish-${name}.png`); writeFileSync(f, Buffer.from(data, 'base64')); console.log('shot', f) }
const p = await newPage('void', 960, 600)
await p.view(960, 600, { dpr: 1 })
await open(p, 'dc=long'); await room(p, 'Discord'); await sleep(2500)
console.log('cards', await p.fn(() => JSON.stringify([...document.querySelectorAll('.exo-dc-now-card')].map(c => [c.getAttribute('aria-label'), c.classList.contains('has-art'), getComputedStyle(c).boxShadow !== 'none']))))
await save(p, '960-long-now')
const muted = await p.fn(() => {
  const css = (e) => { const s = getComputedStyle(e); return [s.color, s.fontWeight] }
  const m = document.querySelector('.exo-dc-rail-item.is-server.is-muted'); const r = document.querySelector('.exo-dc-rail-item.is-server:not(.is-muted):not(.is-unread)')
  const pic = (e) => { const i = e?.querySelector(':scope > :is(img, .exo-dc-initials, span)'); return i ? getComputedStyle(i).opacity : null }
  return JSON.stringify({ muted: m ? [m.textContent.trim(), ...css(m), pic(m)] : null, read: r ? [r.textContent.trim(), ...css(r), pic(r)] : null })
})
console.log('muted', muted)
const f = await newPage('forced', 1440, 900)
await f.view(1440, 900, { dpr: 1, forced: true })
await open(f, ''); await room(f, 'Discord'); await sleep(1500)
await f.clickSel('.exo-dc-rail-item.is-server', 0); await f.mouse(1400, 700); await sleep(2000)
await save(f, '1440-forced-server')
process.exit(0)
