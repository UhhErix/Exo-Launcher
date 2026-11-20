// UI pass part 2: sizes, keyboard walk, control states, text sizes, motion, error/loading states.
//   node tools/ui-pass/run2.mjs [--groups sizes,keyboard,hover,text,motion,errors]
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { launch, newPage, open, room, snap, sleep, flush, results, SHOTS, shotName, drain, DATA } from './harness.mjs'
import * as A from './audit.mjs'

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > -1 ? process.argv[i + 1] : d }
const GROUPS = arg('groups', 'sizes,keyboard,hover,text,motion,errors').split(',')
const failures = []
const step = async (name, fn) => { try { await fn() } catch (e) { failures.push({ step: name, error: String(e.message).slice(0, 200) }); console.log('  FAIL', name, '-', String(e.message).slice(0, 140)) } }
const esc = (p) => p.key('Escape').then(() => sleep(350))
const AUDIT = { audit: true }
const gear = (p) => p.clickSel('[aria-label="Open settings"]').then(() => sleep(1000))

async function pixDiff(p, a, b, rect) {
  return p.fn(async (xa, xb, r) => { const L = async (x) => { const bin = atob(x); const u8 = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i); return createImageBitmap(new Blob([u8], { type: 'image/png' })) }; const [A, B] = await Promise.all([L(xa), L(xb)]); const x0 = Math.max(0, Math.floor(r.x - 10)), y0 = Math.max(0, Math.floor(r.y - 10)), w = Math.min(A.width - x0, Math.ceil(r.w + 20)), h = Math.min(A.height - y0, Math.ceil(r.h + 20)); const g = (I) => { const c = new OffscreenCanvas(w, h); const q = c.getContext('2d'); q.drawImage(I, x0, y0, w, h, 0, 0, w, h); return q.getImageData(0, 0, w, h).data }; const da = g(A), db = g(B); let n = 0; for (let i = 0; i < da.length; i += 4) if (Math.abs(da[i] - db[i]) + Math.abs(da[i + 1] - db[i + 1]) + Math.abs(da[i + 2] - db[i + 2]) > 24) n++; return n }, a, b, rect)
}
const focusInfo = (p) => p.fn(() => { const e = document.activeElement; if (!e || e === document.body) return null; const b = e.getBoundingClientRect(); const cs = getComputedStyle(e); const lab = (e.getAttribute('aria-label') || e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40); return { tag: e.tagName.toLowerCase(), cls: String(e.className).slice(0, 50), lab, role: e.getAttribute('role'), rect: { x: b.x, y: b.y, w: b.width, h: b.height }, outline: cs.outlineStyle + ' ' + cs.outlineWidth, ring: e.dataset.exoRing || null, inView: b.bottom > 0 && b.top < innerHeight } })

async function walk(p, room, n, keep) {
  const stops = []
  await p.mouse(1, 1800, 'move').catch(() => {})
  for (let i = 1; i <= n; i++) {
    const before = await p.pixels()
    await p.key('Tab'); await sleep(260)
    const fi = await focusInfo(p)
    const after = await p.pixels()
    let changed = null
    if (fi) changed = await pixDiff(p, before, after, fi.rect)
    stops.push({ i, ...fi, changedPx: changed, visible: changed === null ? null : changed > 40 })
    if (keep.includes(i)) await snap(p, 'kb-' + room, String(i).padStart(2, '0'))
  }
  results['kb-' + room + '-stops'] = stops
  return stops
}

const groups = {
  async sizes(p0, _v, launchFn) {
    for (const [w, h] of [[1280, 720], [2560, 1440]]) {
      const p = await newPage('void', w, h)
      await step(`lib-${w}`, async () => { await open(p, ''); await snap(p, 'library', 'default', AUDIT); await p.clickSel('.exo-tile-hit', 1); await sleep(900); await snap(p, 'library', 'game-sheet', AUDIT); await esc(p) })
      await step(`lib-stress-${w}`, async () => { await open(p, 'stress'); await snap(p, 'library', 'stress-560', AUDIT) })
      await step(`settings-${w}`, async () => { await open(p, ''); await gear(p); await snap(p, 'settings', 'general', AUDIT); await p.clickExact('Library', 'li > button'); await sleep(800); await snap(p, 'settings', 'library', AUDIT) })
      await step(`discord-${w}`, async () => { await open(p, ''); await room(p, 'Discord'); await sleep(1000); await snap(p, 'discord', 'home-friends', AUDIT); await p.clickSel('[aria-label^="Ada"]', 0).catch(() => {}); await sleep(900); await snap(p, 'discord', 'after-click', AUDIT) })
      await step(`pc-${w}`, async () => { await open(p, ''); await room(p, 'PC'); await sleep(900); await snap(p, 'pc', 'overview', AUDIT) })
      await step(`store-${w}`, async () => { await open(p, ''); await room(p, 'Store'); await sleep(900); await snap(p, 'store', 'discover', AUDIT) })
      await step(`music-${w}`, async () => { await open(p, ''); await room(p, 'Music'); await sleep(900); await snap(p, 'music', 'home', AUDIT) })
      await step(`devices-${w}`, async () => { await open(p, ''); await room(p, 'Devices'); await sleep(900); await snap(p, 'devices', 'overview', AUDIT) })
      await p.close()
    }
    // narrow: below the 1280x720 floor, to see how it breaks
    const p = await newPage('void', 1024, 640)
    await step('narrow', async () => { await open(p, ''); await snap(p, 'library', 'default', AUDIT); await gear(p); await snap(p, 'settings', 'general', AUDIT) })
    await p.close()
  },
  async keyboard() {
    const p = await newPage('void')
    await step('kb-library', async () => { await open(p, ''); await p.eval('document.body.focus()'); await walk(p, 'library', 24, [1, 2, 7, 12, 14, 19]) })
    await step('kb-settings', async () => { await open(p, ''); await gear(p); await p.eval('document.activeElement && document.activeElement.blur()'); await walk(p, 'settings', 16, [1, 3, 6, 10]) })
    await step('kb-store', async () => { await open(p, ''); await room(p, 'Store'); await sleep(800); await p.eval('document.activeElement && document.activeElement.blur()'); await walk(p, 'store', 14, [3, 8]) })
    await step('kb-palette', async () => { await open(p, ''); await p.key('k', { ctrl: true }); await sleep(500); for (let i = 0; i < 4; i++) await p.key('ArrowDown'); await sleep(300); await snap(p, 'kb-palette', 'arrow-down-4') })
    await step('kb-discord', async () => { await open(p, ''); await room(p, 'Discord'); await sleep(900); await p.eval('document.activeElement && document.activeElement.blur()'); await walk(p, 'discord', 14, [4, 9]) })
    await step('kb-menu-nav', async () => { await open(p, ''); await p.clickSel('.exo-shelf-sort-trigger'); await sleep(500); await p.key('ArrowDown'); await p.key('ArrowDown'); await sleep(300); await snap(p, 'kb-library', 'sort-menu-arrow') })
    await p.close()
  },
  async hover() {
    const p = await newPage('void')
    // each: [name, open fn, selector, nth]
    const targets = [
      ['set-btn', async () => { await open(p, ''); await gear(p); await p.clickExact('General', 'li > button'); await sleep(600) }, '.exo-set-btn', 0],
      ['set-btn-primary', async () => { await open(p, ''); await room(p, 'PC'); await sleep(600) }, '.exo-set-btn.is-primary, button.is-primary', 0],
      ['switch', async () => { await open(p, ''); await gear(p); await p.clickExact('General', 'li > button'); await sleep(600) }, '[role=switch]', 0],
      ['bar-nav', async () => { await open(p, '') }, '.exo-nav-btn', 2],
      ['bar-search', async () => { await open(p, '') }, '.exo-bar-search', 0],
      ['capture-btn', async () => { await open(p, '') }, '.exo-capture-btn', 0],
      ['gear', async () => { await open(p, '') }, '[aria-label="Open settings"]', 0],
      ['winbtn-close', async () => { await open(p, '') }, '[aria-label="Close"]', 0],
      ['sort-trigger', async () => { await open(p, '') }, '.exo-shelf-sort-trigger', 0],
      ['continue-play', async () => { await open(p, '') }, '.exo-continue-play', 0],
      ['continue-card', async () => { await open(p, '') }, '.exo-continue-open', 1],
      ['game-tile', async () => { await open(p, '') }, '.exo-tile-hit', 1],
      ['gift-accept', async () => { await open(p, '') }, '.exo-set-btn.is-primary', 0],
      ['store-chip', async () => { await open(p, ''); await room(p, 'Store'); await sleep(700) }, '[role=tab], .exo-store-tabs button', 1],
      ['discord-row', async () => { await open(p, ''); await room(p, 'Discord'); await sleep(900) }, '[aria-label^="Message Bea"]', 0],
      ['music-row', async () => { await open(p, ''); await room(p, 'Music'); await sleep(900) }, '.exo-music-side button', 4],
      ['music-play', async () => { await open(p, ''); await room(p, 'Music'); await sleep(900) }, '[aria-label="Play"], [aria-label="Pause"]', 0],
      ['input', async () => { await open(p, ''); await room(p, 'Store'); await sleep(700) }, 'input', 0],
    ]
    for (const [name, prep, sel, nth] of targets) await step('ctl-' + name, async () => {
      await prep()
      const r = await p.fn((s, n) => { const e = [...document.querySelectorAll(s)].filter((x) => x.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) && x.getBoundingClientRect().width > 0)[n]; if (!e) return null; e.scrollIntoView({ block: 'center' }); const b = e.getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height } }, sel, nth)
      if (!r) throw new Error('no ' + sel)
      const cx = r.x + r.w / 2, cy = r.y + r.h / 2
      const clip = { x: Math.max(0, r.x - 24), y: Math.max(0, r.y - 24), width: Math.min(r.w + 48, 560), height: Math.min(r.h + 48, 300) }
      const scale = clip.width < 300 ? 2 : 1
      await p.mouse(2, 1070, 'move'); await sleep(350)
      const states = [['rest', async () => {}], ['hover', async () => { await p.mouse(cx, cy, 'move'); await sleep(350) }], ['pressed', async () => { await p.mouse(cx, cy, 'down', 'left'); await sleep(200) }]]
      for (const [st, fn] of states) { await fn(); const f = shotName('ctl-' + name, st, p); await p.shot(join(SHOTS, f), clip, scale); results[f] = { room: 'ctl-' + name, state: st, file: f, crop: true } }
      await p.mouse(2, 1070, 'up', 'left').catch(() => {}); await p.mouse(2, 1070, 'move')
      // keyboard focus on the same element
      await p.fn((s, n) => { const e = [...document.querySelectorAll(s)].filter((x) => x.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) && x.getBoundingClientRect().width > 0)[n]; if (e) { document.documentElement.dataset.exoInput = 'key'; e.focus({ focusVisible: true }) } }, sel, nth)
      await sleep(300)
      const f2 = shotName('ctl-' + name, 'focus', p); await p.shot(join(SHOTS, f2), clip, scale); results[f2] = { room: 'ctl-' + name, state: 'focus', file: f2, crop: true, focus: await focusInfo(p) }
    })
    await p.close()
  },
  async text() {
    for (const [size, colour, w, h] of [['small', 'white', 1920, 1080], ['large', 'white', 1920, 1080], ['large', 'white', 1280, 720], ['normal', 'grey', 1920, 1080]]) {
      const p = await newPage('void', w, h, { textSize: size, textColour: colour })
      p.extra = `-text-${size}${colour === 'grey' ? '-grey' : ''}`
      await step(`text-${size}-${colour}-${w}`, async () => {
        await open(p, ''); await snap(p, 'library', 'default', AUDIT)
        await p.clickSel('.exo-tile-hit', 1); await sleep(900); await snap(p, 'library', 'game-sheet', AUDIT); await esc(p)
        await gear(p); await snap(p, 'settings', 'general', AUDIT); await p.clickExact('Library', 'li > button'); await sleep(800); await snap(p, 'settings', 'library', AUDIT); await esc(p)
        await room(p, 'PC'); await sleep(800); await snap(p, 'pc', 'overview', AUDIT)
        await room(p, 'Discord'); await sleep(900); await snap(p, 'discord', 'home-friends', AUDIT)
        await room(p, 'Store'); await sleep(900); await snap(p, 'store', 'discover', AUDIT)
        await room(p, 'Music'); await sleep(900); await snap(p, 'music', 'home', AUDIT)
      })
      await p.close()
    }
  },
  async motion() {
    for (const reduce of [false, true]) {
      const p = await newPage('void', 1920, 1080, { reduce })
      const tag = reduce ? 'reduce' : 'full'
      const rec = { tag, scenes: {} }
      const sample = async (name, fn) => { await fn(); await sleep(40); const a1 = await p.fn(A.animations); await sleep(260); const a2 = await p.fn(A.animations); const all = [...a1, ...a2]; rec.scenes[name] = { motion: await p.eval('document.documentElement.dataset.exoMotion'), longFinite: all.filter((a) => a.it !== Infinity && a.d + (a.delay || 0) > 700).map((a) => `${a.cls}:${a.name}:${a.d}ms`).slice(0, 8), looping: [...new Set(all.filter((a) => a.it === Infinity).map((a) => a.cls + ':' + a.name))].slice(0, 6), finiteCount: all.filter((a) => a.it !== Infinity && a.d > 20).length, finite: [...new Set(all.filter((a) => a.it !== Infinity && a.d > 20).map((a) => `${a.name || 'transition'}:${a.d}ms`))].slice(0, 10) } }
      await step('motion-' + tag, async () => {
        await open(p, ''); await sleep(1500)
        for (const r of ['Store', 'Discord', 'Music', 'PC', 'Devices', 'Library']) await sample('room-' + r, async () => { await p.clickText(r, '.exo-nav-btn') }) , await sleep(500)
        await sample('settings-open', async () => { await p.clickSel('[aria-label="Open settings"]') }); await sleep(600)
        await sample('settings-page-push', async () => { await p.clickExact('Library', 'li > button') }); await sleep(600); await esc(p)
        await sample('game-sheet', async () => { await p.clickSel('.exo-tile-hit', 1) }); await sleep(600); await esc(p)
        await sample('sort-menu', async () => { await p.clickSel('.exo-shelf-sort-trigger') }); await sleep(300); await esc(p)
        await sample('palette', async () => { await p.key('k', { ctrl: true }) }); await sleep(300); await esc(p)
      })
      results['motion-' + tag] = rec
      await p.close()
    }
    // Settings > Reduce motion switch sets the page's motion
    const p = await newPage('void')
    await step('reduce-toggle', async () => { await open(p, ''); await gear(p); await p.clickExact('General', 'li > button'); await sleep(500); const before = await p.eval('document.documentElement.dataset.exoMotion'); await p.clickSel('[role=switch]', 0); await sleep(500); results['motion-toggle'] = { before, after: await p.eval('document.documentElement.dataset.exoMotion') }; await snap(p, 'settings', 'reduce-motion-on', AUDIT) })
    await p.close()
  },
  async errors() {
    const p = await newPage('void')
    const rooms = ['Library', 'Store', 'Discord', 'Music', 'PC', 'Devices']
    for (const [flag, label, wait] of [['fail', 'host-refused', 1500], ['slow=3500', 'loading', 700]]) {
      for (const r of rooms) await step(`${label}-${r}`, async () => {
        await open(p, flag, 'Search'); await sleep(label === 'loading' ? 300 : 600)
        if (r !== 'Library') await p.clickText(r, '.exo-nav-btn')
        await sleep(wait); await snap(p, r.toLowerCase(), label, AUDIT)
      })
    }
    await step('fail-settings', async () => { await open(p, 'fail'); await gear(p); await sleep(800); await snap(p, 'settings', 'host-refused', AUDIT) })
    await step('fail-palette', async () => { await open(p, 'fail'); await p.key('k', { ctrl: true }); await sleep(700); await snap(p, 'palette', 'host-refused', AUDIT) })
    await step('race', async () => { await open(p, 'race', 'Search', true); await sleep(1200); await snap(p, 'shell', 'accounts-sheet-race-with-seen-flag', AUDIT) })
    await p.close()
  },
}

const { proc } = await launch()
try {
  for (const g of GROUPS) if (groups[g]) { console.log(g); await groups[g](); flush('results2-' + g) }
} finally { proc.kill() }
writeFileSync(join(DATA, 'failures2.json'), JSON.stringify(failures, null, 1))
console.log('done', Object.keys(results).length, 'records,', failures.length, 'failures')
