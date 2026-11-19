// Geometry audit of the Discord room, run inside the page (passed to shell.call, so it is self-contained). It finds
// what a person would see as a layout defect that a control-only check misses: boxes drawn past the window edge or
// past the box that holds them, text cut without an ellipsis, siblings lying over each other, absolutely positioned
// boxes over words, and rules that run past their panel. Each finding is { kind, path, rect, detail }.

export function geometryAudit() {
  const vw = innerWidth, vh = innerHeight
  const out = []
  // Intended overlays: a box matching one of these floats over the page on purpose, so it is not a fault when it covers
  // words, and a pair where one of them sits over the other is not an overlap. Each entry says why.
  const INTENDED = [
    // Menus and pickers: a context menu, the status menu, the plus menu, the emoji and GIF pickers, the user and server menus.
    '[role="menu"]', '[role="listbox"]', '[role="dialog"]', '[role="alertdialog"]', '[role="tooltip"]', '[popover]', 'dialog',
    '.exo-dc-menu', '.exo-dc-ctx', '.exo-dc-plus-menu', '.exo-dc-picker', '.exo-dc-popover',
    // Tooltips and hover hints.
    '.exo-dc-tip', '.exo-dc-tooltip',
    // Floating call-to-action on a scrolled conversation ("Jump to present"): it sits over the newest messages by design.
    '.exo-dc-present',
    // Discord's profile card: the avatar overlaps the banner's bottom edge on purpose (see the pf-head check below).
    '.exo-dc-pf-avatar',
  ]
  const intended = (el) => !!el && INTENDED.some(sel => el.closest(sel))
  // The profile card's head is drawn over the banner's lower edge: allowed only when the head holds the avatar.
  const bannerOverlap = (a, b) => (a.matches('.exo-dc-pf-banner') && b.querySelector('.exo-dc-pf-avatar, .exo-dc-avatar-wrap')) ||
    (b.matches('.exo-dc-pf-banner') && a.querySelector('.exo-dc-pf-avatar, .exo-dc-avatar-wrap'))
  const round = (r) => ({ x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) })
  const path = (el) => {
    const parts = []
    for (let e = el; e && e !== document.body && parts.length < 4; e = e.parentElement) {
      const cls = [...(e.classList || [])].filter(c => !/^(is|has)-/.test(c)).slice(0, 2).join('.')
      const label = e === el ? (e.getAttribute('aria-label') || e.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 28) : ''
      parts.unshift(e.tagName.toLowerCase() + (cls ? '.' + cls : '') + (label ? ` "${label}"` : ''))
    }
    return parts.join(' > ')
  }
  const shown = (el) => {
    if (!el.checkVisibility({ opacityProperty: true, visibilityProperty: true })) return false
    for (let a = el; a && a !== document.documentElement; a = a.parentElement) {
      if (a.getAttribute('aria-hidden') === 'true' || a.inert || a.classList.contains('is-backstage') || +getComputedStyle(a).opacity < 0.05) return false
    }
    return true
  }
  // Scope: the Discord room, and whatever floats over the page (menus, sheets, popovers), but no other room.
  const inScope = (el) => !!el.closest('.exo-dc') || !el.closest('.exo-room')
  // What a box shows of itself: its rect cut by every ancestor that clips. `scroll` marks a clip that scrolls on an axis.
  const clips = (el) => {
    const list = []
    for (let a = el.parentElement; a && a !== document.documentElement; a = a.parentElement) {
      const cs = getComputedStyle(a)
      if (cs.overflowX === 'visible' && cs.overflowY === 'visible') continue
      list.push({ el: a, r: a.getBoundingClientRect(), x: cs.overflowX !== 'visible', y: cs.overflowY !== 'visible', sx: /auto|scroll/.test(cs.overflowX), sy: /auto|scroll/.test(cs.overflowY) })
    }
    return list
  }
  // Clips are read nearest first; past a box that scrolls on an axis, the boxes beyond it are out of reach by design
  // (a rail or a list scrolled to its end), so they stop cutting on that axis.
  const visibleOf = (r, list) => {
    let l = Math.max(r.left, 0), t = Math.max(r.top, 0), rt = Math.min(r.right, vw), b = Math.min(r.bottom, vh)
    let scrolledX = false, scrolledY = false
    for (const c of list) {
      if (c.x && !scrolledX) { l = Math.max(l, c.r.left); rt = Math.min(rt, c.r.right) }
      if (c.y && !scrolledY) { t = Math.max(t, c.r.top); b = Math.min(b, c.r.bottom) }
      scrolledX ||= c.sx
      scrolledY ||= c.sy
    }
    return { left: l, top: t, right: rt, bottom: b }
  }
  const all = [...document.querySelectorAll('body *')].filter(el => inScope(el) && shown(el))
  const boxes = []
  for (const el of all) {
    const r = el.getBoundingClientRect()
    if (r.width < 2 || r.height < 2) continue
    boxes.push({ el, r, cs: getComputedStyle(el) })
  }

  // 1. Drawn past the window edge (what a person sees, after every clip).
  for (const { el, r, cs } of boxes) {
    if (cs.pointerEvents === 'none' && !el.textContent.trim()) continue
    const list = clips(el)
    const v = visibleOf(r, list)
    if (v.right - v.left < 1 || v.bottom - v.top < 1) continue
    if (v.right > vw + 0.5 || v.left < -0.5) {
      out.push({ kind: 'past-window-edge', path: path(el), rect: round(r), detail: `drawn to x ${Math.round(v.right)} of ${vw}` })
    }
  }

  // 2. A box cut by a clip that does not scroll: part of it is drawn past the edge of the box that holds it.
  for (const { el, r } of boxes) {
    // The nearest clip that cuts it, up to a scroller on that axis (a scroller's far content is reached by scrolling).
    let scrolledX = false, scrolledY = false
    for (const c of clips(el)) {
      const cutX = c.x && !c.sx && !scrolledX && (r.left < c.r.left - 1.5 || r.right > c.r.right + 1.5)
      const cutY = c.y && !c.sy && !scrolledY && (r.top < c.r.top - 1.5 || r.bottom > c.r.bottom + 1.5)
      // A clip that truncates with an ellipsis (a name in a narrow column, with its glow or per-letter style inside) cuts
      // on purpose: section 3 still catches text cut without one.
      const truncates = (() => { for (let e = el; e && e !== c.el.parentElement; e = e.parentElement) if (getComputedStyle(e).textOverflow === 'ellipsis') return true; return false })()
      if ((cutX || cutY) && truncates) break
      if (cutX || cutY) {
        out.push({ kind: 'cut-by-container', path: path(el), rect: round(r), detail: `cut by ${path(c.el)} (${round(c.r).x},${round(c.r).y} ${round(c.r).w}x${round(c.r).h})` })
        break
      }
      scrolledX ||= c.sx
      scrolledY ||= c.sy
    }
  }

  // 3. Content wider or taller than its own box (scrollWidth past clientWidth), and words cut without an ellipsis.
  // The right edge of what sits in flow inside a box (a status dot or a badge held out past its avatar is drawn on
  // purpose, so absolutely positioned descendants don't count), and whether a text truncates with an ellipsis (it is
  // meant to be wider than its box).
  const flowRight = (el) => {
    let max = -Infinity
    for (const d of el.querySelectorAll('*')) {
      let inAbs = false
      for (let a = d; a && a !== el; a = a.parentElement) {
        if (/absolute|fixed/.test(getComputedStyle(a).position)) { inAbs = true; break }
      }
      if (inAbs || d.getClientRects().length === 0) continue
      max = Math.max(max, d.getBoundingClientRect().right)
    }
    return max
  }
  for (const { el, cs } of boxes) {
    if (cs.textOverflow === 'ellipsis') continue
    if (el.clientWidth && el.scrollWidth > el.clientWidth + 1 && !/auto|scroll/.test(cs.overflowX)) {
      const edge = el.getBoundingClientRect().left + el.clientLeft + el.clientWidth
      const flow = flowRight(el)
      if (flow > edge + 1) out.push({ kind: 'content-wider-than-box', path: path(el), rect: round(el.getBoundingClientRect()), detail: `in-flow content to x ${Math.round(flow)}, box ends ${Math.round(edge)} (overflow-x ${cs.overflowX})` })
    }
    const ownText = [...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim().length > 0)
    if (ownText && !el.matches('input, textarea, select, svg, svg *') && cs.textOverflow !== 'ellipsis' && !/auto|scroll/.test(cs.overflowX)) {
      // The words themselves, measured: past the box's right edge when the box doesn't scroll.
      const range = document.createRange()
      // Trailing spaces hang past a wrapped line's end (CSS hangs them); they are not words, so they are not measured.
      for (const n of el.childNodes) {
        if (n.nodeType !== 3 || !n.textContent.trim()) continue
        range.setStart(n, n.textContent.length - n.textContent.trimStart().length)
        range.setEnd(n, n.textContent.trimEnd().length)
      }
      const edge = el.getBoundingClientRect().left + el.clientLeft + el.clientWidth
      const words = range.getBoundingClientRect()
      if (words.width > 0 && words.right > edge + 1 && el.clientWidth > 0) {
        out.push({ kind: 'text-past-box', path: path(el), rect: round(el.getBoundingClientRect()), detail: `words to x ${Math.round(words.right)}, box ends ${Math.round(edge)}` })
      }
    }
    if (ownText && el.clientWidth && el.scrollWidth > el.clientWidth + 1 && !el.matches('input, textarea, select') && cs.textOverflow !== 'ellipsis' && /hidden|clip/.test(cs.overflowX)) {
      out.push({ kind: 'text-cut-no-ellipsis', path: path(el), rect: round(el.getBoundingClientRect()), detail: `text ${el.scrollWidth} > ${el.clientWidth}` })
    }
    if (ownText && el.clientHeight && el.scrollHeight > el.clientHeight + 1 && /hidden|clip/.test(cs.overflowY) && cs.display !== 'inline') {
      out.push({ kind: 'text-cut-vertically', path: path(el), rect: round(el.getBoundingClientRect()), detail: `text ${el.scrollHeight} > ${el.clientHeight}` })
    }
  }

  // 4. Siblings in flow lying over each other.
  for (const { el } of boxes) {
    const kids = [...el.children].filter(k => {
      const s = getComputedStyle(k)
      return shown(k) && s.position !== 'absolute' && s.position !== 'fixed' && s.display !== 'contents' && k.getBoundingClientRect().width >= 4 && k.getBoundingClientRect().height >= 4
    })
    // An inline box that wraps is several line fragments: each is checked, not their union.
    const boxesOf = (k) => getComputedStyle(k).display === 'inline' ? [...k.getClientRects()] : [k.getBoundingClientRect()]
    for (let i = 0; i < kids.length; i++) {
      for (let j = i + 1; j < kids.length; j++) {
        if (intended(kids[i]) || intended(kids[j]) || bannerOverlap(kids[i], kids[j])) continue
        const hit = boxesOf(kids[i]).find(a => boxesOf(kids[j]).some(b => {
          const w = Math.min(a.right, b.right) - Math.max(a.left, b.left)
          const h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top)
          return w > 2 && h > 2
        }))
        if (hit) out.push({ kind: 'siblings-overlap', path: path(kids[i]), rect: round(hit), detail: `over ${path(kids[j])}` })
      }
    }
  }

  // 5. An absolutely positioned box drawn over words it does not hold (the words are on top of nothing else).
  const abs = boxes.filter(({ cs, r }) => (cs.position === 'absolute' || cs.position === 'fixed') && r.width >= 8 && r.height >= 8)
  const texts = boxes.filter(({ el }) => [...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim().length > 1) && !el.matches('input, textarea, select'))
  for (const { el: a, r: ar } of abs) {
    if (intended(a)) continue
    for (const { el: t } of texts) {
      if (a === t || a.contains(t) || t.contains(a)) continue
      const range = document.createRange()
      range.selectNodeContents(t)
      const tr0 = [...range.getClientRects()].find(q => q.width > 2 && q.height > 2)
      if (!tr0) continue
      // Words a scroller has moved out of its view are not drawn there: only what is visible counts.
      const vis = visibleOf(tr0, clips(t))
      if (vis.right - vis.left <= 2 || vis.bottom - vis.top <= 2) continue
      const tr = { left: vis.left, top: vis.top, right: vis.right, bottom: vis.bottom, width: vis.right - vis.left, height: vis.bottom - vis.top }
      const w = Math.min(ar.right, tr.right) - Math.max(ar.left, tr.left)
      const h = Math.min(ar.bottom, tr.bottom) - Math.max(ar.top, tr.top)
      if (w <= 2 || h <= 2) continue
      const hit = document.elementFromPoint(tr.left + tr.width / 2, tr.top + tr.height / 2)
      if (hit && (a === hit || a.contains(hit)) && !t.contains(hit)) {
        out.push({ kind: 'absolute-covers-text', path: path(a), rect: round(ar), detail: `over words "${t.textContent.trim().slice(0, 24)}" (${path(t)})` })
      }
    }
  }

  // 6. Rules and hairlines that run past the panel they sit in.
  for (const { el, r, cs } of boxes) {
    const rule = /(^|[\s_-])(rule|hairline|divider|separator|line)s?($|[\s_-])/i.test(el.className?.toString?.() || '') || el.tagName === 'HR'
    const thin = Math.min(r.width, r.height) <= 2 && (cs.borderTopWidth !== '0px' || cs.borderBottomWidth !== '0px' || cs.backgroundColor !== 'rgba(0, 0, 0, 0)' || rule)
    if (!rule && !thin) continue
    const p = el.parentElement
    if (!p || !shown(p)) continue
    const pr = p.getBoundingClientRect()
    const past = r.left < pr.left - 0.5 || r.right > pr.right + 0.5
    if (past) out.push({ kind: 'rule-past-panel', path: path(el), rect: round(r), detail: `panel ${round(pr).x}..${round(pr).x + round(pr).w}` })
  }

  // De-duplicate identical findings.
  const seen = new Set()
  return out.filter(f => { const k = f.kind + '|' + f.path + '|' + f.detail; if (seen.has(k)) return false; seen.add(k); return true })
}
