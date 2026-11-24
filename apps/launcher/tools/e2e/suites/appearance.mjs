// The look beyond the rooms: what floats (a game's sheet, Ctrl+K, a menu, Settings, Discord's pages in it), and that
// every surface is solid.
import { sleep } from '../cdp.mjs'

const OVERLAYS = {
  'game sheet': async (t) => { await t.k.nav('Library'); await t.shell.click(() => document.querySelector('.exo-home-more-btn') ?? [...document.querySelectorAll('.exo-game-grid button[data-game-id]')].find(b => !b.closest('.exo-folder-tile'))); return t.shell.waitFor(() => !!document.querySelector('.exo-game-room .exo-game-page')?.checkVisibility(), { timeoutMs: 5000 }) },
  'search': async (t) => {
    await t.k.nav('Library')
    for (let i = 0; i < 2; i++) {
      await t.shell.key('k', { ctrl: true })
      if (await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 2500 })) break
      await t.k.closeAll()
    }
    await t.shell.type('a')
    return t.shell.waitFor(() => document.querySelectorAll('[role="dialog"] [role="option"]').length > 2, { timeoutMs: 4000 })
  },
  'menu': async (t) => { await t.k.nav('Library'); await t.shell.click(() => document.querySelector('main.exo-library-pane button[aria-label="More"], .exo-library-more')); return t.shell.waitFor(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width), { timeoutMs: 3000 }) },
  'settings': async (t) => { await t.k.closeAll(); await t.shell.click(() => document.querySelector('.exo-settings-btn')); return t.shell.waitFor(() => !!document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 4000 }) },
  "Discord's pages in Settings": async (t) => { await t.k.nav('Discord'); if (!await t.shell.waitFor(() => !!document.querySelector('main.exo-dc'), { timeoutMs: 15000 })) return 'skip'; await t.shell.click(() => document.querySelector('.exo-dc-me-who')); await t.shell.click(() => [...document.querySelectorAll('.exo-dc-me [role="menuitem"]')].find(b => b.textContent.trim() === 'Edit profile')); return t.shell.waitFor(() => !!document.querySelector('.exo-dc-settings-page'), { timeoutMs: 5000 }) },
}

export async function run(t) {
  const { run, k } = t

  await run.check('appearance', 'overlays', 'what floats reads right', async (c) => {
    const failures = []
    for (const [name, open] of Object.entries(OVERLAYS)) {
      const shown = await open(t)
      if (shown === 'skip') continue
      if (!shown) {
        c.evidence[`${name}-state`] = await k.state()
        c.evidence[`${name}-active`] = await t.shell.eval(`document.activeElement?.tagName + '.' + (document.activeElement?.className?.toString().slice(0, 40) ?? '')`)
        await c.shot(`${name.replace(/ /g, '-')}-failed`, t.shell)
        failures.push(`${name} did not open`); await k.closeAll(); continue
      }
      await sleep(600)
      failures.push(...await k.look(c, name))
      await c.shot(name.replace(/ /g, '-'), t.shell)
      await k.closeAll()
      await sleep(300)
    }
    return failures
  }, { shell: t.shell })

  // One appearance since 3.26 (DESIGN.md): solid surfaces, nothing blurs what is behind it. The CSS gate bans the
  // property in the source; this checks what the page really computes, and that the Bar does not let a white sheet through.
  await run.check('appearance', 'solid', 'Liquid Glass is only what floats and the controls (no page or panel of content blurs), and the Bar hides what passes under it', async (c) => {
    await k.nav('Library')
    const scan = await t.shell.eval(`(() => {
      const blurred = []
      for (const e of document.querySelectorAll('*')) {
        const s = getComputedStyle(e)
        const bf = s.backdropFilter || s.webkitBackdropFilter || 'none'
        // Glass is menus, popovers, sheets and controls (glass.css); a box over most of the window that blurs is content.
        const r = e.getBoundingClientRect()
        if (bf !== 'none' && e.checkVisibility() && !e.matches('.exo-glass.is-panel, [role="dialog"], [role="dialog"] *') && r.width * r.height > innerWidth * innerHeight * 0.6)
          blurred.push((e.className?.toString() || e.tagName).slice(0, 60) + ' { backdrop-filter: ' + bf + ' }')
      }
      const r = document.querySelector('.exo-top-rooms')?.getBoundingClientRect()
      return { blurred, bar: r && { x: r.left + 8, y: r.top + 4, width: r.width - 16, height: r.height - 8 } }
    })()`)
    c.evidence.scan = scan
    const failures = []
    if (scan.blurred.length) failures.push(`${scan.blurred.length} page-sized element(s) blur what is behind them: ${scan.blurred.slice(0, 3).join(' | ')}`)
    if (!scan.bar) return [...failures, 'no Rooms capsule']
    // The Bar has no surface of its own (DESIGN.md: bare on the black), and "solid" means nothing real shows through
    // it: its capsules' words stay legible because no room content passes under it. Scroll the Library down so covers
    // would be under the Bar if anything ran there, hide the Bar's words and read what is behind them.
    const mean = (img) => { let s = 0, n = 0; for (let i = 0; i < img.data.length; i += 16) { s += 0.2126 * img.data[i] + 0.7152 * img.data[i + 1] + 0.0722 * img.data[i + 2]; n++ } return s / n }
    const bg = await t.shell.eval(`(() => { const s = getComputedStyle(document.querySelector('.exo-top-rooms')); return s.backgroundColor })()`)
    const alpha = (/rgba\(\s*\d+,\s*\d+,\s*\d+,\s*([\d.]+)\s*\)/.exec(bg) || [])[1]
    if (alpha !== undefined && Number(alpha) > 0 && Number(alpha) < 1) failures.push(`the Bar's own fill is see-through (${bg})`)
    await t.shell.eval(`(() => { for (const e of document.querySelectorAll('.exo-library-pane, .exo-library-pane *')) if (e.scrollHeight > e.clientHeight + 40 && /auto|scroll/.test(getComputedStyle(e).overflowY)) { e.scrollTop = 400; break } })()`)
    // The Bar's own drawing goes for the reading (its words, the chosen tab's pill, its glass and lines): what is left is
    // only what passes under it.
    await t.shell.eval(`(() => { const s = document.createElement('style'); s.id = 'e2e-bar-off'; s.textContent = '.exo-titlebar, .exo-titlebar *, .exo-titlebar *::before, .exo-titlebar *::after { color: transparent !important; background: transparent !important; box-shadow: none !important; border-color: transparent !important; outline-color: transparent !important; backdrop-filter: none !important; fill: transparent !important; stroke: transparent !important } .exo-titlebar img { opacity: 0 !important }'; document.head.append(s) })()`)
    await sleep(300)
    // The page's own color (pure black): what shows behind the Bar must be it and nothing else.
    const page = await t.shell.eval(`(() => { const m = /(\\d+),\\s*(\\d+),\\s*(\\d+)/.exec(getComputedStyle(document.body).backgroundColor) || [0, 0, 0, 0]; return 0.2126 * m[1] + 0.7152 * m[2] + 0.0722 * m[3] })()`)
    const behind = -page + mean(await t.shell.pixelsClip({ ...scan.bar, y: Math.max(0, scan.bar.y - 4), height: scan.bar.height + 8 }))
    if (Math.abs(behind) >= 3) await c.shot('behind-bar', t.shell)
    await t.shell.eval(`document.getElementById('e2e-bar-off')?.remove()`)
    c.evidence.bar = { behind: Math.round(behind * 10) / 10, page: Math.round(page), fill: bg }
    if (Math.abs(behind) >= 3) failures.push(`something shows behind the Bar (mean ${Math.round(behind + page)} of 255 where the page is ${Math.round(page)}): room content runs under it`)
    return failures
  })

}
