// Pure-black audit: for big surfaces in each room/sheet/menu, the colour of the rendered pixels (text hidden) and any non-black computed fill.
//   node tools/ui-pass/black.mjs  -> out/black.json
import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { launch, newPage, open, room, sleep, DATA } from './harness.mjs'
import * as A from './audit.mjs'

function surfaces() {
  const sel = (e) => { const parts = []; let n = e; for (let i = 0; i < 3 && n && n.nodeType === 1; i++) { let s = n.tagName.toLowerCase(); const c = String(n.className && n.className.baseVal !== undefined ? n.className.baseVal : n.className || '').split(/\s+/).filter((x) => x && !/^(is-|has-)/.test(x)).slice(0, 2); if (c.length) s += '.' + c.join('.'); parts.unshift(s); n = n.parentElement } return parts.join(' > ') }
  const out = []
  for (const e of document.querySelectorAll('body *')) {
    const b = e.getBoundingClientRect(); if (b.width * b.height < 14000 || !e.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) continue
    if (b.right < 0 || b.bottom < 0 || b.left > innerWidth || b.top > innerHeight) continue
    const cs = getComputedStyle(e); const bg = cs.backgroundColor
    const pb = getComputedStyle(e, '::before').backgroundColor
    const m = (c) => { const q = c.match(/rgba?\(([^)]+)\)/); if (!q) return null; const p = q[1].split(/[ ,\/]+/).filter(Boolean).map(Number); return { r: p[0], g: p[1], b: p[2], a: p[3] === undefined ? 1 : p[3] } }
    const bf = cs.backdropFilter, pbf = getComputedStyle(e, '::before').backdropFilter
    if ((bf && bf !== 'none') || (pbf && pbf !== 'none')) out.push({ sel: sel(e), which: 'glass', color: 'backdrop ' + ((bf && bf !== 'none') ? bf : pbf).slice(0, 40), w: Math.round(b.width), h: Math.round(b.height), rect: [b.left, b.top, b.right, b.bottom] })
    for (const [which, c] of [['bg', bg], ['::before', pb]]) { const v = m(c); if (v && v.a > 0.05 && (v.r > 4 || v.g > 4 || v.b > 4)) out.push({ sel: sel(e), which, color: c, w: Math.round(b.width), h: Math.round(b.height), rect: [b.left, b.top, b.right, b.bottom] }) }
  }
  return out.slice(0, 60)
}

async function pixelMeans(p, rects) {
  await p.fn(A.hideText, true); await sleep(150)
  const b64 = await p.pixels(); await p.fn(A.hideText, false)
  return p.eval(`(async () => { const bin = atob(${JSON.stringify(b64)}); const u8 = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i); const bmp = await createImageBitmap(new Blob([u8], { type: 'image/png' })); const c = new OffscreenCanvas(bmp.width, bmp.height); const g = c.getContext('2d', { willReadFrequently: true }); g.drawImage(bmp, 0, 0); return ${JSON.stringify(rects)}.map((r) => { const x0 = Math.max(0, Math.floor(r[0] + 6)), y0 = Math.max(0, Math.floor(r[1] + 6)), w = Math.max(1, Math.min(bmp.width - x0, Math.floor(r[2] - r[0] - 12))), h = Math.max(1, Math.min(bmp.height - y0, Math.floor(r[3] - r[1] - 12))); const d = g.getImageData(x0, y0, w, h).data; let R = 0, G = 0, B = 0, n = 0, mx = 0; const lv = new Set(); for (let i = 0; i < d.length; i += 16) { R += d[i]; G += d[i + 1]; B += d[i + 2]; n++; mx = Math.max(mx, d[i], d[i + 1], d[i + 2]); lv.add(d[i] >> 2) } return { mean: [Math.round(R / n), Math.round(G / n), Math.round(B / n)], max: mx, levels: lv.size } }) })()`)
}

const SCENES = [
  ['onboarding-welcome', async (p) => { await open(p, 'onboarding', 'Welcome', false); await sleep(1200) }],
  ['library', async (p) => { await open(p, '') }],
  ['library-game-sheet', async (p) => { await open(p, ''); await p.clickSel('.exo-tile-hit', 1); await sleep(1000) }],
  ['library-sort-menu', async (p) => { await open(p, ''); await p.clickSel('.exo-shelf-sort-trigger'); await sleep(600) }],
  ['palette', async (p) => { await open(p, ''); await p.key('k', { ctrl: true }); await sleep(700) }],
  ['settings', async (p) => { await open(p, ''); await p.clickSel('[aria-label="Open settings"]'); await sleep(1100) }],
  ['whatsnew', async (p) => { await open(p, 'whatsnew'); await sleep(1300) }],
  ['accounts-sheet', async (p) => { await open(p, '', 'Search', false); await sleep(1000) }],
  ['store', async (p) => { await open(p, ''); await room(p, 'Store') }],
  ['store-game-page', async (p) => { await open(p, ''); await room(p, 'Store'); await p.clickSel('[aria-label^="Open Cyberpunk"]'); await sleep(1500) }],
  ['discord-home', async (p) => { await open(p, ''); await room(p, 'Discord') }],
  ['discord-server', async (p) => { await open(p, ''); await room(p, 'Discord'); await p.clickSel('[aria-label^="Night Raid"]'); await sleep(1400) }],
  ['music', async (p) => { await open(p, ''); await room(p, 'Music') }],
  ['pc', async (p) => { await open(p, ''); await room(p, 'PC') }],
  ['pc-display-menu', async (p) => { await open(p, ''); await room(p, 'PC'); await p.clickText('Screen'); await sleep(900) }],
  ['pc-apps-sheet', async (p) => { await open(p, ''); await room(p, 'PC'); await p.clickText('Apps', 'button'); await sleep(1000) }],
  ['devices', async (p) => { await open(p, ''); await room(p, 'PC'); await p.clickText('Devices', 'button.exo-pc-chip'); await sleep(1300) }],
  ['capture-menu', async (p) => { await open(p, ''); await p.clickSel('.exo-capture-btn'); await sleep(600) }],
]

const { proc } = await launch()
const res = {}
try {
  for (const vk of (process.env.BLACK_VARIANTS ?? 'void,solid').split(',')) {
    const p = await newPage(vk)
    for (const [name, go] of SCENES) {
      try {
        await go(p); await sleep(400)
        const body = await p.eval('getComputedStyle(document.body).backgroundColor + ' + JSON.stringify(' | ') + ' + getComputedStyle(document.documentElement).backgroundColor')
        const s = await p.fn(surfaces)
        const px = s.length ? await pixelMeans(p, s.map((x) => x.rect)) : []
        res[vk + ':' + name] = { page: body, surfaces: s.map((x, i) => ({ ...x, rect: undefined, px: px[i] })) }
        console.log(vk, name, s.length)
      } catch (e) { res[vk + ':' + name] = { error: String(e.message).slice(0, 160) }; console.log('FAIL', name, e.message.slice(0, 100)) }
    }
    await p.close()
  }
} finally { proc.kill() }
writeFileSync(join(DATA, process.env.BLACK_OUT ?? 'black.json'), JSON.stringify(res, null, 1))
