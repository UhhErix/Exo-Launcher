// Page-side audits. Each function is serialised into the page, so it must be self-contained.
export function installObservers() {
  window.__cls = 0; window.__shifts = []
  try {
    new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) { window.__cls += e.value; window.__shifts.push({ v: Math.round(e.value * 1000) / 1000, t: Math.round(e.startTime), src: (e.sources || []).slice(0, 2).map((s) => (s.node && s.node.className ? String(s.node.className).slice(0, 60) : s.node && s.node.nodeName)) }) } }).observe({ type: 'layout-shift', buffered: true })
  } catch (e) { /* ignore */ }
}

export function layoutAudit() {
  const vis = (e) => { const b = e.getBoundingClientRect(); return b.width > 0 && b.height > 0 && e.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) }
  const sel = (e) => { const parts = []; let n = e; for (let i = 0; i < 4 && n && n.nodeType === 1; i++) { let s = n.tagName.toLowerCase(); const c = String(n.className && n.className.baseVal !== undefined ? n.className.baseVal : n.className || '').split(/\s+/).filter((x) => x && !/^(is-|has-)/.test(x)).slice(0, 2); if (c.length) s += '.' + c.join('.'); parts.unshift(s); if (n.id) break; n = n.parentElement } return parts.join(' > ') }
  const label = (e) => (e.getAttribute('aria-label') || e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40)
  const scale = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-scale')) || 1
  const out = { scale, small: [], clipped: [], overlaps: [], offscale: {}, radii: {}, overflowX: document.documentElement.scrollWidth - innerWidth, unnamed: [] }
  const INTER = 'button,[role=button],[role=tab],[role=menuitem],[role=menuitemradio],[role=menuitemcheckbox],[role=option],[role=switch],[role=checkbox],[role=slider],a[href],input,select,textarea,[tabindex="0"]'
  const els = [...document.querySelectorAll(INTER)].filter(vis).filter((e) => { const b = e.getBoundingClientRect(); if (!(b.bottom > 0 && b.top < innerHeight && b.right > 0 && b.left < innerWidth)) return false; const t = document.elementFromPoint(Math.min(innerWidth - 1, Math.max(0, b.left + b.width / 2)), Math.min(innerHeight - 1, Math.max(0, b.top + b.height / 2))); return !!t && (e === t || e.contains(t) || t.contains(e)) })
  const seen = new Set()
  for (const e of els) {
    const b = e.getBoundingClientRect()
    if (b.width < 28 || b.height < 28) { const s = sel(e) + ' ' + Math.round(b.width) + 'x' + Math.round(b.height); if (!seen.has(s)) { seen.add(s); out.small.push({ sel: sel(e), w: Math.round(b.width * 10) / 10, h: Math.round(b.height * 10) / 10, label: label(e) }) } }
    const name = e.getAttribute('aria-label') || e.getAttribute('aria-labelledby') || (e.textContent || '').trim() || e.getAttribute('placeholder')
    if (!name && e.tagName !== 'INPUT') out.unnamed.push({ sel: sel(e) })
  }
  const text = [...document.querySelectorAll('body *')].filter((e) => vis(e) && [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim()))
  const clipSeen = new Set()
  for (const e of text) {
    const cs = getComputedStyle(e); const b = e.getBoundingClientRect()
    const cutX = e.scrollWidth > e.clientWidth + 1 && /(hidden|clip)/.test(cs.overflowX)
    const cutY = e.scrollHeight > e.clientHeight + 1 && /(hidden|clip)/.test(cs.overflowY) && cs.webkitLineClamp === 'none'
    if ((cutX || cutY) && e.clientWidth > 0) { const ell = cs.textOverflow === 'ellipsis' || cs.webkitLineClamp !== 'none'; const k = sel(e) + (ell ? 'E' : 'C'); if (!clipSeen.has(k)) { clipSeen.add(k); out.clipped.push({ sel: sel(e), ellipsis: ell, text: label(e), w: Math.round(b.width), sw: e.scrollWidth, h: Math.round(b.height), sh: e.scrollHeight }) } }
    if (b.right > innerWidth + 1 || b.left < -1) { const k = sel(e) + 'W'; if (!clipSeen.has(k)) { clipSeen.add(k); out.clipped.push({ sel: sel(e), pastWindow: true, text: label(e), left: Math.round(b.left), right: Math.round(b.right) }) } }
    const px = parseFloat(cs.fontSize) / scale
    if (![10, 12, 13, 15, 20, 28, 32].includes(Math.round(px * 2) / 2)) { const k = Math.round(px * 10) / 10 + 'px'; const a = (out.offscale[k] ||= []); if (a.length < 4) a.push(sel(e)) }
  }
  for (const e of document.querySelectorAll('body *')) {
    if (!vis(e)) continue
    const cs = getComputedStyle(e); const rad = cs.borderTopLeftRadius
    if (rad === '0px') continue
    const b = e.getBoundingClientRect(); if (b.width < 8 || b.height < 8) continue
    const v = Math.min(parseFloat(rad), 999); const k = (v >= b.height / 2 - 0.5 ? 'pill' : Math.round(v * 10) / 10 + 'px')
    const o = (out.radii[k] ||= { n: 0, ex: [] }); o.n++; if (o.ex.length < 3) o.ex.push(sel(e))
  }
  const L = els.slice(0, 350).map((e) => [e, e.getBoundingClientRect()])
  for (let i = 0; i < L.length; i++) for (let j = i + 1; j < L.length; j++) {
    const [a, ra] = L[i], [c, rc] = L[j]
    if (a.contains(c) || c.contains(a)) continue
    const w = Math.min(ra.right, rc.right) - Math.max(ra.left, rc.left), h = Math.min(ra.bottom, rc.bottom) - Math.max(ra.top, rc.top)
    if (w > 3 && h > 3 && out.overlaps.length < 12) out.overlaps.push({ a: sel(a) + ' [' + label(a) + ']', b: sel(c) + ' [' + label(c) + ']', w: Math.round(w), h: Math.round(h) })
  }
  return out
}

export function textItems() {
  const vis = (e) => { const b = e.getBoundingClientRect(); return b.width > 3 && b.height > 3 && b.bottom > 0 && b.top < innerHeight && b.right > 0 && b.left < innerWidth && e.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) }
  const sel = (e) => { const parts = []; let n = e; for (let i = 0; i < 3 && n && n.nodeType === 1; i++) { let s = n.tagName.toLowerCase(); const c = String(n.className && n.className.baseVal !== undefined ? n.className.baseVal : n.className || '').split(/\s+/).filter((x) => x && !/^(is-|has-)/.test(x)).slice(0, 2); if (c.length) s += '.' + c.join('.'); parts.unshift(s); n = n.parentElement } return parts.join(' > ') }
  const items = []
  for (const e of document.querySelectorAll('body *')) {
    if (!vis(e)) continue
    const own = [...e.childNodes].filter((n) => n.nodeType === 3).map((n) => n.textContent).join('').replace(/\s+/g, ' ').trim()
    if (!own) continue
    const cs = getComputedStyle(e)
    let op = 1; for (let n = e; n && n.nodeType === 1; n = n.parentElement) op *= +getComputedStyle(n).opacity
    if (op < 0.05) continue
    const rects = []; for (const n of e.childNodes) if (n.nodeType === 3 && n.textContent.trim()) { const r = document.createRange(); r.selectNodeContents(n); rects.push(r.getBoundingClientRect()) }
    const b = { left: Math.min(...rects.map((x) => x.left)), top: Math.min(...rects.map((x) => x.top)), right: Math.max(...rects.map((x) => x.right)), bottom: Math.max(...rects.map((x) => x.bottom)) }
    const x0 = Math.max(0, b.left), x1 = Math.min(innerWidth, b.right), y0 = Math.max(0, b.top), y1 = Math.min(innerHeight, b.bottom)
    if (x1 - x0 < 3 || y1 - y0 < 3) continue
    const top = document.elementFromPoint((x0 + x1) / 2, (y0 + y1) / 2)
    if (!top || !(e === top || e.contains(top) || top.contains(e))) continue
    items.push({ sel: sel(e), text: own.slice(0, 40), color: cs.color, fill: cs.webkitTextFillColor, op, size: parseFloat(cs.fontSize), weight: +cs.fontWeight, rect: [x0, y0, x1, y1] })
  }
  return items
}

export function hideText(on) {
  let s = document.getElementById('__hide')
  if (!on) { if (s) s.remove(); return true }
  s = document.createElement('style'); s.id = '__hide'
  s.textContent = '*,*::before,*::after{color:transparent!important;-webkit-text-fill-color:transparent!important;text-shadow:none!important;transition:none!important;caret-color:transparent!important}input::placeholder{color:transparent!important}'
  document.head.appendChild(s); return true
}

export async function contrast(items, b64) {
  const bin = atob(b64); const u8 = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i)
  const bmp = await createImageBitmap(new Blob([u8], { type: 'image/png' }))
  const c = new OffscreenCanvas(bmp.width, bmp.height); const g = c.getContext('2d', { willReadFrequently: true }); g.drawImage(bmp, 0, 0)
  const lin = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4) }
  const L = (r, gg, b) => 0.2126 * lin(r) + 0.7152 * lin(gg) + 0.0722 * lin(b)
  const parse = (s) => { const m = s && s.match(/rgba?\(([^)]+)\)/); if (!m) return null; const p = m[1].split(/[ ,\/]+/).filter(Boolean).map(Number); return { r: p[0], g: p[1], b: p[2], a: p[3] === undefined ? 1 : p[3] } }
  const out = []
  const dpr = bmp.width / innerWidth
  for (const it of items) {
    const col = parse(it.fill) || parse(it.color)
    if (!col) continue
    const [x0, y0, x1, y1] = it.rect.map((v) => v * dpr)
    const w = Math.max(1, Math.floor(x1 - x0)), h = Math.max(1, Math.floor(y1 - y0))
    const data = g.getImageData(Math.floor(x0), Math.floor(y0), w, h).data
    const step = Math.max(1, Math.floor(Math.sqrt((w * h) / 300)))
    const cs = []
    for (let y = 0; y < h; y += step) for (let x = 0; x < w; x += step) {
      const i = (y * w + x) * 4; const a = col.a * it.op
      const R = col.r * a + data[i] * (1 - a), G = col.g * a + data[i + 1] * (1 - a), B = col.b * a + data[i + 2] * (1 - a)
      const l1 = L(R, G, B), l2 = L(data[i], data[i + 1], data[i + 2]); const hi = Math.max(l1, l2), lo = Math.min(l1, l2)
      cs.push((hi + 0.05) / (lo + 0.05))
    }
    cs.sort((p, q) => p - q)
    const large = it.size >= 24 || (it.size >= 18.66 && it.weight >= 700)
    out.push({ sel: it.sel, text: it.text, size: it.size, p5: Math.round(cs[Math.floor(cs.length * 0.05)] * 100) / 100, med: Math.round(cs[Math.floor(cs.length / 2)] * 100) / 100, need: large ? 3 : 4.5, color: it.color })
  }
  return out
}

export function animations() {
  return document.getAnimations().map((a) => { const t = a.effect && a.effect.getComputedTiming ? a.effect.getComputedTiming() : {}; const tg = a.effect && a.effect.target; return { d: Math.round(t.duration || 0), it: t.iterations, delay: t.delay, name: a.animationName || a.transitionProperty || '', cls: tg ? String(tg.className && tg.className.baseVal !== undefined ? tg.className.baseVal : tg.className || tg.tagName).slice(0, 60) : '', ease: t.easing } })
}
