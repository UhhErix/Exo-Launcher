import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const out = {}
for (const vk of ['void', 'forced']) {
  const p = await newPage(vk, 1600, 900)
  await open(p, ''); await room(p, 'Discord'); await sleep(1200)
  await p.fn(() => document.querySelector('.exo-dc-rail-item.is-server').click()); await sleep(2000)
  out[vk] = await p.fn(() => {
    const ch = !!document.querySelector('.exo-dc-channels'); const l = document.querySelector('.exo-dc-rail-list'); const r = l.getBoundingClientRect(); const items = [...l.querySelectorAll('.exo-dc-rail-item')]; const f = items[0].getBoundingClientRect(); const cs = getComputedStyle(l)
    const cur = document.querySelector('.exo-dc-rail [aria-current="true"]'); const a = cur && getComputedStyle(cur, '::after')
    const chan = document.querySelector('.exo-dc-channels [aria-current="true"], .exo-dc-channels .is-selected .exo-dc-item'); const chs = chan && getComputedStyle(chan)
    return { ch, list: [Math.round(r.left), Math.round(r.right), Math.round(r.top), Math.round(r.bottom)], first: [Math.round(f.left), Math.round(f.right), Math.round(f.top), Math.round(f.bottom)], ov: cs.overflowX + '/' + cs.overflowY, pad: cs.padding, cur: cur && cur.getAttribute('aria-label'), after: a && (a.content + ' ' + a.backgroundColor), chan: chs && (chs.backgroundColor + ' ' + chs.color) }
  })
  const shot = async (n, clip) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png', ...(clip ? { clip } : {}) }); writeFileSync(join(SHOTS, n + '.png'), Buffer.from(data, 'base64')) }
  await shot('vk-server-' + vk)
  if (vk === 'void') {
    await p.fn(() => document.querySelector('.exo-dc-rail-item.is-home').focus()); await p.key('Tab'); await sleep(250)
    out.focus = await p.fn(() => { const a = document.activeElement; const r = a.getBoundingClientRect(); return (a.getAttribute('aria-label') || a.className) + ' ' + Math.round(r.left) + ',' + Math.round(r.top) })
    await shot('vk-strip-kb0', { x: 0, y: 40, width: 320, height: 60, scale: 4 })
  }
  await p.close()
}
console.log(JSON.stringify(out, null, 1))
process.exit(0)
