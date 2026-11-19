import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const out = {}
const tabInfo = () => {
  const a = document.activeElement; if (!a || a === document.body) return null
  const r = a.getBoundingClientRect(); const cs = getComputedStyle(a)
  // outline box
  const off = parseFloat(cs.outlineOffset) || 0, w = parseFloat(cs.outlineWidth) || 0
  const ox = r.left - off - w, oy = r.top - off - w, ow = r.width + 2 * (off + w), oh = r.height + 2 * (off + w)
  // clipped by ancestors?
  let clip = []
  for (let e = a.parentElement; e && e !== document.documentElement; e = e.parentElement) {
    const c = getComputedStyle(e)
    if (/(hidden|auto|scroll|clip)/.test(c.overflowX + c.overflowY)) { const b = e.getBoundingClientRect(); const L = ox < b.left - 0.5, R = ox + ow > b.right + 0.5, T = oy < b.top - 0.5, B = oy + oh > b.bottom + 0.5; if ((L && /(hidden|auto|scroll|clip)/.test(c.overflowX)) || (R && /(hidden|auto|scroll|clip)/.test(c.overflowX)) || (T && /(hidden|auto|scroll|clip)/.test(c.overflowY)) || (B && /(hidden|auto|scroll|clip)/.test(c.overflowY))) clip.push((e.className || e.tagName) + ` L${+L}R${+R}T${+T}B${+B}`) }
  }
  const vis = cs.visibility
  return { tag: a.tagName, cls: String(a.className).slice(0, 60), name: a.getAttribute('aria-label'), text: (a.textContent || '').trim().slice(0, 30), cur: a.getAttribute('aria-current'), w: Math.round(r.width), h: Math.round(r.height), x: Math.round(r.left), y: Math.round(r.top), outline: cs.outlineStyle + ' ' + cs.outlineWidth + ' ' + cs.outlineColor, vis, clip }
}
const small = () => [...document.querySelectorAll('main.exo-dc button, main.exo-dc [role=button], main.exo-dc input, main.exo-dc a')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.width < 24 || r.height < 24) && getComputedStyle(e).visibility !== 'hidden' }).map(e => { const r = e.getBoundingClientRect(); return (e.getAttribute('aria-label') || e.textContent.trim().slice(0, 20)) + ' ' + String(e.className).slice(0, 40) + ' ' + Math.round(r.width) + 'x' + Math.round(r.height) })
for (const [vk, reduce, tag] of [['void', false, 'void'], ['forced', false, 'forced'], ['solid', false, 'solid'], ['void', true, 'reduce']]) {
  const p = await newPage(vk, 1920, 1080, { reduce })
  if (reduce) await p.fn(() => document.documentElement.setAttribute('data-exo-motion', 'reduce'))
  await open(p, ''); await room(p, 'Discord'); await sleep(1200)
  if (reduce) await p.fn(() => document.documentElement.setAttribute('data-exo-motion', 'reduce'))
  const shot = async (n) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `${n}-${tag}.png`), Buffer.from(data, 'base64')) }
  await shot('rk-home')
  out[tag] = { small: await p.fn(small), tabs: [] }
  if (tag === 'void') {
    await p.fn(() => { const el = document.querySelector('main.exo-dc'); el.focus?.(); document.activeElement.blur(); const b = document.querySelector('.exo-dc-side-list button'); b.focus() })
    for (let i = 0; i < 24; i++) { await p.key('Tab'); await sleep(60); out[tag].tabs.push(await p.fn(tabInfo)); if (i === 1 || i===5||i===9) await shot('rk-tab' + i) }
  }
  // select now and check selection marker
  out[tag].glide = await p.fn(() => { const g = document.querySelector('.exo-dc-side-list.exo-glide'); const b = getComputedStyle(g, '::before'); const cur = g.querySelector('[aria-current="true"]'); return { bg: b.backgroundColor, op: b.opacity, w: b.width, h: b.height, cur: cur && cur.textContent.trim(), curColor: cur && getComputedStyle(cur).color, curBg: cur && getComputedStyle(cur).backgroundColor } })
  // go to a server
  await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800); await shot('rk-server')
  out[tag].strip = await p.fn(() => { const cur = document.querySelector('.exo-dc-rail [aria-current="true"]'); const a = cur && getComputedStyle(cur, '::after'); const g = document.querySelector('.exo-dc-channels .exo-glide, .exo-dc-channels-list'); return { cur: cur && cur.getAttribute('aria-label'), afterBg: a && a.backgroundColor, afterH: a && a.height, afterContent: a && a.content } })
  out[tag].small2 = await p.fn(small)
  await p.close()
}
writeFileSync(join(SHOTS, 'rk.json'), JSON.stringify(out, null, 1))
console.log(JSON.stringify(out, null, 1))
process.exit(0)
