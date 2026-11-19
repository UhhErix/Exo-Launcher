// Polish, as a design review reads a screen (owner, 2026-11-14: "apple level checks"): every room and what floats over it
// looked at for the small things that make an app feel unfinished. Fails: words that leak code ("undefined", "NaN",
// "[object Object]"), a control too small to hit, controls in one row not sharing a middle line, things that move after
// the page has settled, what floats without its glass. Warns: type outside Exo's scale, corner radii outside its set,
// one-off greys for lines, a blurry picture, keyboard focus that cannot be seen, controls of one kind at two heights.
import { sleep } from '../cdp.mjs'
import { ROOMS } from '../lib/kit.mjs'

/** Exo's type scale (DESIGN.md › Type), at text size 100%: label, small, body, read, title, page, first run, plus the hero sizes. */
const TYPE = [10, 12, 13, 15, 20, 28, 32]
/** Corner radii a surface or control may take (tokens.css): none, hairline-round, small, card, panel, sheet, pill. */
const RADII = [0, 1, 2, 3, 4, 6, 8, 10, 12, 14, 16, 18, 20, 24]

/**
 * The page's polish faults in the view as it is now (runs in the page). `floats` lists what floats over the room
 * (menus, popovers, sheets) whose glass is looked at too.
 */
export function polishScan(TYPE, RADII) {
  // Shown to a person: on screen, not hidden, and not a screen reader's own text (sr-only: a 1px box clipped away).
  const shown = (e) => {
    if (e.checkVisibility?.({ opacityProperty: true, visibilityProperty: true }) === false) return false
    const r = e.getBoundingClientRect()
    if (r.width <= 1 || r.height <= 1) return false
    return !e.closest('.sr-only, [class*="visually-hidden"]')
  }
  const path = (e) => {
    const parts = []
    for (let x = e; x && x !== document.body && parts.length < 3; x = x.parentElement) parts.unshift(x.tagName.toLowerCase() + ([...x.classList].slice(0, 2).map(c => '.' + c).join('') || (x.getAttribute('aria-label') ? `[${x.getAttribute('aria-label').slice(0, 24)}]` : '')))
    return parts.join(' > ')
  }
  const inView = (r) => r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth
  const scale = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-scale')) || 1
  const out = { junk: [], small: [], tiny: [], rows: [], type: {}, radii: {}, greys: {}, blurry: [], heights: [], floats: [] }

  // Words that leak code.
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const text = n.textContent
    if (!/\b(undefined|NaN|null)\b|\[object Object\]|\{\{|\}\}/.test(text)) continue
    const host = n.parentElement
    if (!host || !shown(host) || host.closest('code, pre, .exo-dc-msg, .exo-dc-text, .exo-dc-embed, input, textarea, [contenteditable]')) continue
    out.junk.push(`"${text.trim().slice(0, 40)}" in ${path(host)}`)
  }

  const controls = [...document.querySelectorAll('button, a[href], [role="button"], [role="tab"], [role="menuitem"], [role="switch"], select, input:not([type="hidden"]), [tabindex="0"]')]
    .filter(e => shown(e) && inView(e.getBoundingClientRect()) && !e.closest('[aria-hidden="true"], [inert], iframe'))

  // Too small to hit: under 24 px across is a warning, under 16 a failure (a tap target, not the glyph inside it).
  for (const e of controls) {
    const r = e.getBoundingClientRect()
    if (e.matches('input[type="range"], a:not([role])') && r.height >= 14) continue
    const least = Math.min(r.width, r.height)
    if (least < 16) out.tiny.push(`${path(e)} ${Math.round(r.width)}x${Math.round(r.height)}`)
    else if (least < 24) out.small.push(`${path(e)} ${Math.round(r.width)}x${Math.round(r.height)}`)
  }

  // Controls side by side in one row share their middle line (±1.5 px); controls of one kind in that row share one height.
  const parents = new Set(controls.map(e => e.parentElement).filter(Boolean))
  for (const p of parents) {
    const s = getComputedStyle(p)
    if (!(s.display.includes('flex') && !s.flexDirection.startsWith('column')) || s.flexWrap === 'wrap') continue
    const kids = [...p.children].filter(e => controls.includes(e))
    if (kids.length < 2) continue
    const mids = kids.map(e => { const r = e.getBoundingClientRect(); return r.top + r.height / 2 })
    if (s.alignItems !== 'baseline' && Math.max(...mids) - Math.min(...mids) > 1.5) out.rows.push(`${path(p)}: middles ${mids.map(m => Math.round(m)).join(', ')}`)
    const byKind = new Map()
    for (const e of kids) { const k = e.className?.toString().split(' ')[0] || e.tagName; byKind.set(k, [...(byKind.get(k) ?? []), Math.round(e.getBoundingClientRect().height)]) }
    for (const [kind, hs] of byKind) if (new Set(hs).size > 1) out.heights.push(`${path(p)} ${kind}: ${[...new Set(hs)].join(' and ')}px`)
  }

  // Type, radii and line greys over everything shown.
  for (const e of document.body.querySelectorAll('*')) {
    if (!shown(e) || !inView(e.getBoundingClientRect()) || e.closest('iframe, svg, .exo-dc-msg, .exo-dc-embed, .exo-dc-pf, [class*="collectible"], [class*="nameplate"]')) continue
    const s = getComputedStyle(e)
    const own = [...e.childNodes].some(n => n.nodeType === 3 && n.textContent.trim())
    if (own) {
      const px = Math.round(parseFloat(s.fontSize) / scale * 2) / 2
      if (!TYPE.includes(px)) out.type[px] = (out.type[px] ?? []).concat(path(e)).slice(0, 3)
    }
    const r = parseFloat(s.borderTopLeftRadius)
    const box = e.getBoundingClientRect()
    if (r > 0 && r < Math.min(box.width, box.height) / 2 - 0.5 && (s.backgroundColor !== 'rgba(0, 0, 0, 0)' || s.borderTopWidth !== '0px' || s.boxShadow !== 'none')) {
      const rr = Math.round(r / scale)
      if (!RADII.includes(rr)) out.radii[rr] = (out.radii[rr] ?? []).concat(path(e)).slice(0, 3)
    }
    for (const c of [s.borderTopWidth !== '0px' ? s.borderTopColor : null, (/inset 0px 0px 0px [\d.]+px (rgba?\([^)]+\))/.exec(s.boxShadow) ?? [])[1]]) {
      if (!c) continue
      const m = /(\d+), (\d+), (\d+)(?:, ([\d.]+))?/.exec(c)
      if (!m || !(m[1] === m[2] && m[2] === m[3])) continue
      const v = +m[1]
      if (v > 20 && v < 230) out.greys[c] = (out.greys[c] ?? 0) + 1
    }
  }

  // Pictures drawn bigger than they are (blurry at this screen's scale).
  for (const img of document.images) {
    const r = img.getBoundingClientRect()
    if (!img.complete || !img.naturalWidth || !shown(img) || !inView(r) || r.width < 48) continue
    const need = r.width * devicePixelRatio
    if (img.naturalWidth < need * 0.6 && !img.src.startsWith('data:')) out.blurry.push(`${path(img)} ${img.naturalWidth}px drawn ${Math.round(need)}px`)
  }

  // What floats carries its glass (unless Windows asks for more contrast or forced colours, where it is solid).
  const solid = matchMedia('(prefers-contrast: more), (forced-colors: active)').matches
  for (const f of document.querySelectorAll('[role="menu"], [role="listbox"], .exo-dc-menu, .exo-dc-popover, .exo-dc-picker, .exo-shelf-sort-menu, .exo-glass.is-panel')) {
    // A list inside a sheet or a menu is that float's own part: its glass is the float's.
    if (!shown(f) || solid || f.parentElement?.closest('[role="dialog"], [role="menu"], .exo-glass, .exo-dc-menu, .exo-dc-popover, .exo-dc-picker')) continue
    const s = getComputedStyle(f), b = getComputedStyle(f, '::before')
    const glass = s.backdropFilter !== 'none' || (b.content !== 'none' && b.backdropFilter !== 'none') || [...f.children].some(c => c.classList.contains('exo-glass') && getComputedStyle(c).backdropFilter !== 'none')
    if (!glass) out.floats.push(path(f))
  }
  return out
}

/** Where the view's controls are, to see whether anything moves once the page is still. */
export function controlBoxes() {
  return [...document.querySelectorAll('button, a[href], [role="button"], input')].filter(e => e.checkVisibility?.() && e.getBoundingClientRect().width > 0).slice(0, 120)
    .map(e => { const r = e.getBoundingClientRect(); return { k: (e.getAttribute('aria-label') || e.textContent || e.className?.toString() || '').trim().slice(0, 30), x: Math.round(r.left), y: Math.round(r.top) } })
}

export async function run(t) {
  const { run, k } = t

  /** The polish read of the view now, as failures (and warnings on c). */
  async function review(c, label) {
    const p = await t.shell.call(polishScan, TYPE, RADII)
    c.evidence[`${label}.polish`] = p
    const failures = []
    if (p.junk.length) failures.push(`${label}: words leak code: ${p.junk.slice(0, 3).join(' | ')}`)
    if (p.tiny.length) failures.push(`${label}: controls under 16px: ${p.tiny.slice(0, 4).join(' | ')}`)
    if (p.small.length) c.warn(`${label}: ${p.small.length} control(s) under 24px: ${p.small.slice(0, 3).join(' | ')}`)
    if (p.rows.length) failures.push(`${label}: controls in a row off their shared middle: ${p.rows.slice(0, 3).join(' | ')}`)
    if (p.heights.length) c.warn(`${label}: one kind of control at two heights: ${p.heights.slice(0, 3).join(' | ')}`)
    if (p.floats.length) failures.push(`${label}: what floats has no glass: ${p.floats.slice(0, 3).join(' | ')}`)
    const type = Object.entries(p.type)
    if (type.length) c.warn(`${label}: type off the scale: ${type.slice(0, 4).map(([px, where]) => `${px}px (${where[0]})`).join(' | ')}`)
    const radii = Object.entries(p.radii)
    if (radii.length) c.warn(`${label}: corners off the set: ${radii.slice(0, 4).map(([r, where]) => `${r}px (${where[0]})`).join(' | ')}`)
    const greys = Object.keys(p.greys)
    if (greys.length > 2) c.warn(`${label}: ${greys.length} different greys draw lines: ${greys.slice(0, 5).join(', ')}`)
    if (p.blurry.length) c.warn(`${label}: blurry pictures: ${p.blurry.slice(0, 3).join(' | ')}`)
    // Nothing moves once the page has settled (a late font, a list that reflows, a badge that pushes its row).
    const before = await t.shell.call(controlBoxes)
    await sleep(1500)
    const after = await t.shell.call(controlBoxes)
    const moved = after.filter((a, i) => before[i] && before[i].k === a.k && (Math.abs(before[i].x - a.x) > 2 || Math.abs(before[i].y - a.y) > 2))
    if (moved.length) failures.push(`${label}: ${moved.length} control(s) moved after the page settled: ${moved.slice(0, 3).map(m => `${m.k || '?'} to ${m.x},${m.y}`).join(' | ')}`)
    return failures
  }

  /** Tab through the view's first controls: each one focused shows it (an outline, a ring or a fill of its own). */
  async function focusSeen(c, label, presses = 10) {
    const unseen = []
    for (let i = 0; i < presses; i++) {
      await t.shell.key('Tab')
      await sleep(120)
      const f = await t.shell.eval(`(() => { const e = document.activeElement; if (!e || e === document.body) return null
        const s = getComputedStyle(e), r = e.getBoundingClientRect()
        const ring = (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0) || s.boxShadow !== 'none' || e.matches(':focus-visible') && [...document.styleSheets].length && s.backgroundColor !== 'rgba(0, 0, 0, 0)'
        return { name: (e.getAttribute('aria-label') || e.textContent || e.tagName).trim().slice(0, 30), ring, shown: r.width > 0 && r.bottom > 0 && r.top < innerHeight } })()`)
      if (f && f.shown && !f.ring) unseen.push(f.name)
    }
    c.evidence[`${label}.focus`] = unseen
    if (unseen.length) c.warn(`${label}: keyboard focus can't be seen on ${unseen.slice(0, 4).join(', ')}`)
  }

  for (const room of ROOMS) {
    await run.check('polish', room.toLowerCase(), `${room}: no code in its words, every control big enough and on its row's line, nothing moving once still, type, corners and lines from Exo's set`, async (c) => {
      await k.nav(room)
      await sleep(1200)
      await k.settle(300, 8000)
      const failures = await review(c, room)
      await focusSeen(c, room)
      await c.shot(`polish-${room.toLowerCase()}`, t.shell)
      await k.closeAll()
      return failures
    })
  }

  await run.check('polish', 'floats', 'what floats (a game\'s sheet, Ctrl+K, a menu, Settings, Discord\'s menus and settings) is glass and as tidy as the rooms', async (c) => {
    const failures = []
    const open = async (label, go, ready) => {
      await k.closeAll()
      await go()
      if (!await t.shell.waitFor(ready, { timeoutMs: 5000 })) { c.warn(`${label} did not open`); return }
      await sleep(500)
      failures.push(...await review(c, label))
      await c.shot(`polish-${label}`, t.shell)
    }
    await k.nav('Library')
    await open('sort-menu', () => t.shell.click(() => document.querySelector('.exo-shelf-sort-trigger')), () => !!document.querySelector('[role="menu"], .exo-shelf-sort-menu'))
    await open('ctrl-k', () => t.shell.key('k', { ctrl: true }), () => !!document.querySelector('[role="dialog"] [role="combobox"]'))
    await open('settings', () => t.shell.click(() => document.querySelector('.exo-settings-btn')), () => !!document.querySelector('.exo-settings-nav')?.getClientRects().length)
    await k.nav('Discord')
    await sleep(1500)
    await open('discord-me-menu', () => t.shell.click(() => document.querySelector('.exo-dc-me-who')), () => !!document.querySelector('.exo-dc-me [role="menu"]'))
    await open('discord-settings', () => t.shell.click(() => document.querySelector('.exo-dc-me button[aria-label="Discord settings"]')), () => !!document.querySelector('[aria-label="Discord settings"] .exo-dc-settings-page'))
    await open('discord-server-menu', () => t.shell.call(() => { const f = document.querySelector('.exo-dc-band-place.is-server'); if (!f) return false; const r = f.getBoundingClientRect(); f.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: r.left + 10, clientY: r.bottom })); return true }),
      () => !!document.querySelector('.exo-dc-menu'))
    await k.closeAll()
    return failures
  })

  await run.check('polish', 'discord-views', 'Discord\'s Home, a server, a conversation and a voice channel: as tidy as the rooms, each panel the same corner, edge and gap', async (c) => {
    await k.nav('Discord')
    await sleep(1500)
    const failures = []
    const frame = () => t.shell.eval(`(() => {
      const panels = [...document.querySelectorAll('.exo-dc-band, .exo-dc-pane, .exo-dc-view > :not(.exo-dc-home, .exo-dc-fit-probe, .exo-dc-voice-split), .exo-dc-beside, .exo-dc-block')].filter(e => e.checkVisibility())
      const s = panels.map(e => getComputedStyle(e))
      const pane = document.querySelector('.exo-dc-pane')?.getBoundingClientRect(), view = document.querySelector('.exo-dc-view')?.getBoundingClientRect(), band = document.querySelector('.exo-dc-band')?.getBoundingClientRect()
      return { radii: [...new Set(s.map(x => x.borderTopLeftRadius))], edges: [...new Set(s.map(x => x.borderTopColor + ' ' + x.borderTopWidth))],
        gapX: pane && view ? Math.round(view.left - pane.right) : null, gapY: band && pane ? Math.round(pane.top - band.bottom) : null,
        tops: pane && view ? Math.round(view.top - pane.top) : null }
    })()`)
    for (const [label, go] of [
      ['home', async () => t.shell.click(() => document.querySelector('.exo-dc-band-place'))],
      ['server', async () => t.shell.click(() => document.querySelector('.exo-dc-band-place.is-server'))],
      ['dm', async () => { await t.shell.click(() => document.querySelector('.exo-dc-band-place')); await sleep(800); await t.shell.click(() => document.querySelector('.exo-dc-pane .exo-dc-item.is-dm')) }],
    ]) {
      await go()
      await sleep(1600)
      await k.settle(300, 8000)
      const f = await frame()
      c.evidence[`${label}.frame`] = f
      if (f.radii.length > 1) failures.push(`${label}: panels round at ${f.radii.join(', ')}`)
      if (f.edges.length > 1) failures.push(`${label}: panels edged ${f.edges.join(' / ')}`)
      if (f.gapX !== null && f.gapY !== null && Math.abs(f.gapX - f.gapY) > 1) failures.push(`${label}: the gap beside the left panel is ${f.gapX}px, under the band ${f.gapY}px`)
      if (f.tops) failures.push(`${label}: the page starts ${f.tops}px off the left panel's top`)
      failures.push(...await review(c, `discord-${label}`))
      await c.shot(`polish-discord-${label}`, t.shell)
    }
    await t.shell.click(() => document.querySelector('.exo-dc-band-place'))
    return failures
  })
}
