// Shared capture harness: a page with fake art, appearance variants, shot naming, audits.
import { writeFileSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { Page, ORIGIN, sleep, launch } from './cdp.mjs'
import * as A from './audit.mjs'

export const SHOTS = process.env.EXO_UIPASS_SHOTS ?? 'C:/Users/Erix/Code/Exo/misc/ui-pass-3.25/shots'
export const DATA = join(import.meta.dirname, 'out')
mkdirSync(SHOTS, { recursive: true }); mkdirSync(DATA, { recursive: true })

/** What the three Appearances are in this build: one look (Void + Liquid Glass). The variants are what the page does honour. */
export const VARIANTS = {
  void: { app: 'void', glass: 'glass', view: {} },
  solid: { app: 'void', glass: 'noglass', view: { contrast: true } }, // @media (prefers-contrast: more): glass becomes solid fills
  forced: { app: 'void', glass: 'forced', view: { forced: true } }, // Windows High Contrast
}

const hash = (s) => { let h = 2166136261; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619) } return h >>> 0 }
import { deflateSync, crc32 } from 'node:zlib'
const chunk = (type, data) => { const t = Buffer.from(type); const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(Buffer.concat([t, data])) >>> 0); return Buffer.concat([len, t, data, crc]) }
const hsl = (h, s, l) => { s /= 100; l /= 100; const k = (n) => (n + h / 30) % 12; const a = s * Math.min(l, 1 - l); const f = (n) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1))); return [f(0) * 255, f(8) * 255, f(4) * 255] }
/** A fake cover: a hue gradient with a soft disc, as a 2:3 PNG (no art is real, none is a real game's). */
const pngCache = new Map()
const art = (url) => {
  const h = hash(url); if (pngCache.has(h)) return pngCache.get(h)
  const wide = /hero|banner|header|capsule_616/.test(url); const W = wide ? 480 : 240, H = wide ? 270 : 360, hue = h % 360
  const raw = Buffer.alloc((W * 4 + 1) * H)
  const cx = 60 + h % 120, cy = 80 + h % 160, r = 50 + h % 40
  for (let y = 0; y < H; y++) {
    raw[y * (W * 4 + 1)] = 0
    for (let x = 0; x < W; x++) {
      const t = (x / W + y / H) / 2
      let [R, G, B] = hsl((hue + t * 60) % 360, 58, 38 - t * 24)
      const d = Math.hypot(x - cx, y - cy)
      if (d < r) { const [r2, g2, b2] = hsl((hue + 120) % 360, 70, 60); R = R * 0.65 + r2 * 0.35; G = G * 0.65 + g2 * 0.35; B = B * 0.65 + b2 * 0.35 }
      if (y > H * 0.7) { R *= 0.65; G *= 0.65; B *= 0.65 }
      const i = y * (W * 4 + 1) + 1 + x * 4; raw[i] = R; raw[i + 1] = G; raw[i + 2] = B; raw[i + 3] = 255
    }
  }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 6
  const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))])
  pngCache.set(h, png); return png
}

/** A fake clip for every video, GIF and sound the mock names (none is real): 1.5 s of a moving gradient as WebM, recorded once in the browser. */
let clip = null
async function fakeClip() {
  if (clip) return clip
  const q = await Page.create()
  clip = await q.eval(`(async () => {
    const c = document.createElement('canvas'); c.width = 320; c.height = 240
    const g = c.getContext('2d'); let t = 0
    const draw = () => { const h = (200 + t * 3) % 360; const f = g.createLinearGradient(0, 0, 320, 240); f.addColorStop(0, 'hsl(' + h + ' 58% 34%)'); f.addColorStop(1, 'hsl(' + (h + 60) + ' 58% 12%)'); g.fillStyle = f; g.fillRect(0, 0, 320, 240); g.fillStyle = 'hsl(' + (h + 120) + ' 70% 60%)'; g.beginPath(); g.arc(70 + (t * 5) % 180, 120, 44, 0, 7); g.fill(); t++ }
    draw()
    const rec = new MediaRecorder(c.captureStream(30), { mimeType: 'video/webm;codecs=vp8' }); const parts = []
    rec.ondataavailable = (e) => parts.push(e.data); rec.start()
    const tick = setInterval(draw, 33); await new Promise((r) => setTimeout(r, 1500)); clearInterval(tick)
    await new Promise((r) => { rec.onstop = r; rec.stop() })
    const bytes = new Uint8Array(await new Blob(parts).arrayBuffer()); let s = ''
    for (let i = 0; i < bytes.length; i += 8192) s += String.fromCharCode(...bytes.subarray(i, i + 8192))
    return btoa(s)
  })()`)
  await q.close()
  return clip
}

/**
 * A page whose pictures and media are faked (one in seven pictures and clips fails on purpose, as a real CDN sometimes
 * does). `p.failMedia = true` fails every picture and clip that is not a face or a server's icon, to see what a failure leaves.
 */
export async function newPage(variantKey = 'void', w = 1920, h = 1080, opts = {}) {
  const video = await fakeClip()
  const p = await Page.create()
  await p.view(w, h, { ...VARIANTS[variantKey].view, reduce: opts.reduce })
  await p.send('Page.addScriptToEvaluateOnNewDocument', { source: `(${A.installObservers.toString()})();` + (opts.textSize ? `try{localStorage.setItem('exo.textTheme',JSON.stringify({textColour:${JSON.stringify(opts.textColour ?? 'white')},textSize:${JSON.stringify(opts.textSize)}}))}catch(e){}` : '') })
  await p.send('Fetch.enable', { patterns: [{ urlPattern: '*', resourceType: 'Image' }, { urlPattern: '*', resourceType: 'Media' }] })
  p.handlers.push((m) => {
    if (m.method !== 'Fetch.requestPaused') return
    const { requestId, request, resourceType } = m.params
    const u = new URL(request.url)
    if (u.hostname === '127.0.0.1' || u.protocol === 'data:' || u.protocol === 'blob:') return void p.send('Fetch.continueRequest', { requestId }).catch(() => {})
    if ((p.failMedia || hash(request.url) % 7 === 0) && !/avatars|icons/.test(request.url)) return void p.send('Fetch.failRequest', { requestId, errorReason: 'NameNotResolved' }).catch(() => {})
    const media = resourceType === 'Media'
    p.send('Fetch.fulfillRequest', { requestId, responseCode: 200, responseHeaders: [{ name: 'Content-Type', value: media ? 'video/webm' : 'image/png' }, { name: 'Access-Control-Allow-Origin', value: '*' }, { name: 'Cache-Control', value: 'no-store' }], body: media ? video : art(request.url).toString('base64') }).catch(() => {})
  })
  p.variant = variantKey; p.w = w; p.h = h
  return p
}

const NOISE = /ERR_NAME_NOT_RESOLVED|ERR_FAILED|mouse\.exo-launcher\.local|ShaderTools|Mock: no|favicon/
export function drain(p) { const c = p.console.filter((x) => !NOISE.test(x.text)); const e = p.errors.slice(); p.console.length = 0; p.errors.length = 0; return { console: [...new Map(c.map((x) => [x.type + x.text, x])).values()].slice(0, 12), errors: e.slice(0, 6) } }

export const names = []
export function shotName(room, state, p) {
  const v = VARIANTS[p.variant]
  const size = p.w === 1920 && p.h === 1080 ? '' : `-${p.w}x${p.h}`
  return `${room}-${state}-${v.app}-${v.glass}${p.extra ?? ''}${size}.png`
}

export async function audit(p) {
  const layout = await p.fn(A.layoutAudit)
  const items = await p.fn(A.textItems)
  await p.fn(A.hideText, true); await sleep(120)
  const b64 = await p.pixels()
  await p.fn(A.hideText, false)
  const all = await p.eval(`(${A.contrast.toString()})(${JSON.stringify(items)}, ${JSON.stringify(b64)})`)
  const fails = all.filter((x) => x.p5 < x.need).sort((a, b) => a.p5 - b.p5)
  return { layout, contrast: { n: all.length, min: fails[0]?.p5 ?? null, failing: fails.slice(0, 25), under3: fails.filter((f) => f.p5 < 3).length, under45: fails.length, bySel: summarize(all) } }
}
function summarize(all) { const m = {}; for (const x of all) { const k = x.sel; if (!m[k] || x.p5 < m[k].p5) m[k] = { p5: x.p5, need: x.need, text: x.text } } return Object.fromEntries(Object.entries(m).filter(([, v]) => v.p5 < v.need + 1.5).slice(0, 40)) }

export const results = {}
/** Take a named screenshot (+ optional audit) of the current page. */
export async function snap(p, room, state, o = {}) {
  const file = shotName(room, state, p)
  await sleep(o.wait ?? 450)
  await p.shot(join(SHOTS, file))
  const rec = { room, state, file, variant: p.variant, size: [p.w, p.h], ...drain(p) }
  rec.cls = await p.eval('Math.round((window.__cls || 0) * 1000) / 1000').catch(() => null)
  if (rec.cls > 0.01) rec.shifts = await p.eval('window.__shifts.slice(0, 6)').catch(() => null)
  if (o.audit) { try { Object.assign(rec, await audit(p)) } catch (e) { rec.auditError = String(e.message).slice(0, 200) } }
  if (o.anim) rec.anim = (await p.fn(A.animations)).filter((a) => a.it !== Infinity)
  results[file] = rec
  return rec
}
export const flush = (tag = 'results') => writeFileSync(join(DATA, tag + '.json'), JSON.stringify(results, null, 1))

/** Opens the mock app. The account is required, so a room is shown signed in; `signedIn` false shows the account gate instead. */
export async function open(p, query, marker = 'Search', signedIn = true) {
  const q = [signedIn && !/(^|&)signedin(&|$)/.test(query) ? 'signedin' : '', query].filter(Boolean).join('&')
  await p.goto(ORIGIN + '?' + q, marker)
  await sleep(900)
}
export const room = async (p, name) => { await p.clickText(name, '.exo-nav-btn'); await sleep(1300) }
export { launch, sleep, Page, ORIGIN }
