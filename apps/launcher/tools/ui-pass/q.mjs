// Ad hoc single-scene capture: node tools/ui-pass/q.mjs <name> [variant] [WxH] -- scenes defined inline below.
if (!process.env.EXO_UIPASS_SHOTS) { console.error('set EXO_UIPASS_SHOTS (never write over the baseline shots)'); process.exit(2) }
import { launch, newPage, open, room, snap, sleep } from './harness.mjs'
const [name, variant = 'void', size = '1920x1080'] = process.argv.slice(2)
const [w, h] = size.split('x').map(Number)
const scenes = {
  async banner(p) { await open(p, '', 'Search', true); await sleep(1500); await snap(p, 'shell', 'account-banner-seen', { audit: true }) },
}
scenes.continuefocus = async (p) => {
  await p.send('Emulation.setFocusEmulationEnabled', { enabled: true })
  await open(p, '')
  await p.eval(`document.documentElement.dataset.exoInput = 'key'; document.querySelector('.exo-account-banner button:last-child').focus()`); await p.key('Tab'); await p.key('Tab'); await sleep(300)
  console.log(await p.eval(`(() => { const e = document.activeElement; const a = document.activeElement; return [e && e.outerHTML.slice(0,120), a.outerHTML.slice(0,120), e && e.matches(':focus-visible'), e && getComputedStyle(e.closest('.exo-continue-card')).outline] })()`))
  await snap(p, 'kb-library', 'continue-focus-ring')
}
scenes.display = async (p) => { await open(p, ''); await room(p, 'PC'); await sleep(1000); await p.clickText('Screen'); await sleep(1000); await snap(p, 'pc', 'screen', { audit: true }) }
scenes.devices = async (p) => { await open(p, ''); await room(p, 'Devices'); await sleep(900)
  console.log(JSON.stringify(await p.eval(`(() => { const r = (e) => { const b = e.getBoundingClientRect(); return [Math.round(b.x), Math.round(b.y), Math.round(b.width), Math.round(b.height)] }; const a = document.querySelector('.exo-device-card-art'); const d = a.querySelector('.exo-device-art'); const cs = getComputedStyle(d); return { stage: r(a.parentElement), art: r(a), device: r(d), svg: r(d.querySelector('svg')), pad: cs.padding, bs: cs.boxSizing, svgbs: getComputedStyle(d.querySelector('svg')).boxSizing } })()`)))
  await snap(p, 'devices', 'overview', { audit: true }) }
scenes.discord = async (p) => { await open(p, ''); await room(p, 'Discord'); await sleep(1200)
  console.log(JSON.stringify(await p.eval(`(() => { const r = (s) => { const e = document.querySelector(s); if (!e) return null; const b = e.getBoundingClientRect(); return [Math.round(b.x), Math.round(b.y), Math.round(b.width), Math.round(b.height), getComputedStyle(e).marginBottom] }; return { rail: r('.exo-dc-rail'), list: r('.exo-dc-rail-list'), foot: r('.exo-dc-foot'), me: r('.exo-dc-me'), footh: getComputedStyle(document.querySelector('.exo-dc')).getPropertyValue('--dc-foot-h'), inset: getComputedStyle(document.querySelector('.exo-dc')).getPropertyValue('--dc-inset'), lastTile: (() => { const l = document.querySelector('.exo-dc-rail-list'); const b = l.lastElementChild.getBoundingClientRect(); return [b.y, b.bottom, l.scrollHeight, l.clientHeight] })() } })()`)))
  await snap(p, 'discord', 'home-friends', { audit: true }) }
scenes.rail = async (p) => { for (const n of [1, 3, 6, 10]) { await open(p, 'hero=' + n); await room(p, 'Store'); await sleep(1200); await snap(p, 'store', 'discover-rail-' + n, { audit: n === 1 }) } }
scenes.stress = async (p) => { await open(p, 'stress'); await snap(p, 'library', 'stress-560', { audit: true }) }
scenes.discover = async (p) => { await open(p, ''); await room(p, 'Store'); await sleep(1200); await snap(p, 'store', 'discover', { audit: true }) }
// Folder label contrast: for each folder colour, the label's own background (text hidden) against white and against near-black ink.
scenes.folderink = async (p) => {
  await open(p, 'folders')
  const out = {}
  for (const color of ['gray', 'red', 'orange', 'yellow', 'green', 'teal', 'blue', 'pink']) {
    await p.eval(`(() => { const t = document.querySelector('.exo-folder-tile [data-color], .exo-folder-tile[data-color]'); const el = t.closest('[data-color]') || t; el.dataset.color = ${JSON.stringify(color)}; window.__f = el })()`)
    await sleep(500)
    const A = await import('./audit.mjs')
    await p.fn(A.hideText, true); await sleep(150)
    const b64 = await p.pixels(); await p.fn(A.hideText, false)
    out[color] = await p.eval(`(async () => { const bin = atob(${JSON.stringify(b64)}); const u8 = Uint8Array.from(bin, (c) => c.charCodeAt(0)); const bm = await createImageBitmap(new Blob([u8])); const c = new OffscreenCanvas(bm.width, bm.height); const g = c.getContext('2d'); g.drawImage(bm, 0, 0); const l = window.__f.querySelector('.exo-folder-label'); const r = l.getBoundingClientRect(); const d = g.getImageData(Math.floor(r.x), Math.floor(r.y), Math.ceil(r.width), Math.ceil(r.height)).data; const lum = (R, G, B) => { const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }; return 0.2126 * f(R) + 0.7152 * f(G) + 0.0722 * f(B) }; let lo = 9, hi = -1; for (let i = 0; i < d.length; i += 4) { const y = lum(d[i], d[i + 1], d[i + 2]); lo = Math.min(lo, y); hi = Math.max(hi, y) } const cr = (a, b) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05); const ink = (y) => getComputedStyle(l).color; return { ink: getComputedStyle(l).color, white: +cr(1, hi).toFixed(2), black: +cr(lum(10, 10, 10), lo).toFixed(2), bgLum: [+lo.toFixed(3), +hi.toFixed(3)] } })()`)
  }
  console.log(JSON.stringify(out, null, 1))
}
// Where the Bar's boxes are, frame by frame from document start: prints every frame in which one moved or resized.
scenes.cls = async (p) => { const room = process.env.ROOM; await open(p, ''); await sleep(1500); await open(p, ''); if (room) await p.clickText(room, '.exo-nav-btn'); await sleep(3000); console.log(room || 'Library', 'CLS', await p.eval('window.__cls'), JSON.stringify(await p.eval('window.__shifts'))) }
scenes.barshift = async (p, q = process.env.Q || 'sheetseen') => {
  await p.send('Page.addScriptToEvaluateOnNewDocument', { source: `(() => { window.__bar = []; let prev = ''; const sels = ['.exo-library-pane', '.exo-account-banner,.exo-account-banner-hold', '.exo-continue-row', '.exo-gifts,.exo-gift', '.exo-windowed-game-grid', '.exo-top-rooms']; const t0 = performance.now(); const tick = () => { const cur = sels.map((s) => { const e = document.querySelector(s); if (!e) return s + ':-'; const b = e.getBoundingClientRect(); return s + ':' + Math.round(b.x) + ',' + Math.round(b.y) + ',' + Math.round(b.width) + 'x' + Math.round(b.height) }).join(' | '); if (cur !== prev) { window.__bar.push([Math.round(performance.now() - t0), cur]); prev = cur } if (performance.now() - t0 < 4000) requestAnimationFrame(tick) }; requestAnimationFrame(tick) })()` })
  await p.goto(ORIGIN + '?' + q, 'Search'); await sleep(2500)
  for (const [t, c] of await p.eval('window.__bar')) console.log(t, c)
  console.log('CLS', await p.eval('window.__cls'), JSON.stringify(await p.eval('window.__shifts')))
}
import { ORIGIN } from './harness.mjs'
// Tab inside the open Settings sheet never leaves it; closing returns focus to the gear.
scenes.trap = async (p) => {
  await p.send('Emulation.setFocusEmulationEnabled', { enabled: true })
  await open(p, ''); await p.clickSel('[aria-label="Open settings"]'); await sleep(1200)
  const where = () => p.eval(`(() => { const a = document.activeElement; return a ? (a.closest('.exo-settings') ? 'in-settings' : (a.getAttribute('aria-label') || a.tagName)) : 'none' })()`)
  const seen = new Set()
  for (let i = 0; i < 45; i++) { await p.key('Tab'); seen.add(await where()) }
  for (let i = 0; i < 5; i++) { await p.key('Tab', { shift: true }); seen.add(await where()) }
  console.log('Tab stops seen:', [...seen])
  await p.key('Escape'); await sleep(900)
  console.log('after close, focus is on:', await p.eval(`document.activeElement?.getAttribute('aria-label') || document.activeElement?.tagName`))
}
scenes.channels = async (p) => { await open(p, ''); await room(p, 'Discord'); await sleep(900); await p.clickSel('[aria-label^="Night Raid"]'); await sleep(1500)
  console.log(JSON.stringify(await p.eval(`[...document.querySelectorAll('.exo-dc-chan > .exo-dc-item')].map((b) => { const n = b.querySelector('.exo-dc-item-name'); return [n.textContent, b.className.replace('exo-dc-item', '').trim(), getComputedStyle(n).color, getComputedStyle(b).color, getComputedStyle(b).opacity, getComputedStyle(n).fontWeight] })`)))
  await snap(p, 'discord', 'server-channel', { audit: true }) }
const { proc } = await launch()
try { const p = await newPage(variant, w, h); await scenes[name](p); await p.close() } finally { proc.kill() }
