// Exo's Settings, used: it opens from the gear on its Library page, no wider than
// the settings width, scrolling brings every section's heading into view, the column shows Exo's scrollbar while it scrolls (no width, then gone),
// every switch in Downloads turns and turns back, Update DLSS, FSR and XeSS automatically stays off through a restart, Steam's Offline mode shows how Steam starts, Text size scales the page and goes back, About's What's
// new holds the window until Continue and lets go, Escape closes it where you were. Settings is Exo's own (Library, Discord,
// Music, General); capture options are checked in the capture suite, where they live.
import { openPage, sleep } from '../cdp.mjs'

async function open(t) {
  await t.k.closeAll()
  await t.shell.click(() => document.querySelector('.exo-settings-btn'))
  return !!await t.shell.waitFor(() => !!document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 5000 })
}

// Settings is pages down its left (lib/settingsSections.ts › SETTINGS_PAGES), one drawn at a time; it opens on the page of
// the room under it. Text size is on General; Check for updates and What's new are on About.
const page = (t, name) => t.shell.click((n) => [...document.querySelectorAll('.exo-settings-nav button')].find(b => b.textContent.trim() === n), name)
const general = (t) => page(t, 'General')
const about = async (t) => { await page(t, 'About'); await sleep(400) }
/** Where each section the suite looks for lives: its page. */
const SECTION_PAGE = { 'Game stores': 'Library', Downloads: 'Downloads', Achievements: 'In game' }

export async function run(t) {
  const { run, k } = t
  await k.nav('Library')

  await run.check('settings', 'sections', 'Settings opens from the gear at the settings width, with its sections', async (c) => {
    const t0 = Date.now()
    if (!await open(t)) return ['Settings did not open']
    c.metric('open', Date.now() - t0, ' ms', 500)
    const state = await t.shell.eval(`(() => {
      const sheet = document.querySelector('.exo-set-room > .exo-settings')
      const scale = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-scale')) || 1
      return {
        sections: [...sheet.querySelectorAll('.exo-set-section > .exo-set-section-head h2')].map(h => h.textContent.trim()),
        nav: !!sheet.querySelector('nav'),
        riotIdInStores: [...sheet.querySelectorAll('.exo-set-section')].some(s => /^Game stores/.test(s.querySelector('h2')?.textContent.trim() ?? '') && [...s.querySelectorAll('.exo-set-item-label')].some(l => l.textContent.trim() === 'Riot ID')),
        width: Math.round(sheet.getBoundingClientRect().width),
        max: Math.round(Math.min(1160 * scale, sheet.parentElement.clientWidth)),
      }
    })()`)
    c.evidence.state = state
    const failures = []
    // Each section on its own page: the page opened from the list, its heading drawn.
    for (const [s, where] of Object.entries(SECTION_PAGE)) {
      if (!await page(t, where)) { failures.push(`no ${where} page`); continue }
      await sleep(400)
      const heads = await t.shell.eval(`[...document.querySelectorAll('.exo-settings .exo-set-section > .exo-set-section-head h2, .exo-settings .exo-settings-page-title')].map(h => h.textContent.trim())`)
      if (!heads.includes(s)) failures.push(`no ${s} heading on ${where}`)
    }
    await page(t, 'Library')
    await sleep(400)
    state.riotIdInStores = await t.shell.eval(`[...document.querySelectorAll('.exo-set-section')].some(s => /^Game stores/.test(s.querySelector('h2')?.textContent.trim() ?? '') && [...s.querySelectorAll('.exo-set-item-label')].some(l => l.textContent.trim() === 'Riot ID'))`)
    // Keys are gone: no Optional keys section, and Riot ID (a name, not a key) is a row of Game stores.
    if (state.sections.includes('Optional keys')) failures.push('the Optional keys section is still there')
    if (!state.riotIdInStores) failures.push('Riot ID is not a row of Game stores')
    if (state.width > state.max + 1) failures.push(`the sheet is ${state.width}px wide, more than ${state.max}px`)
    await c.shot('settings', t.shell)
    failures.push(...await k.look(c, 'settings'))
    return failures
  })

  await run.check('settings', 'one-column', "scrolling a page's column brings each of its section headings into view, down to the end", async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    // Library is the longest page: its column scrolls; every heading it has comes into view.
    await page(t, 'Library')
    await sleep(400)
    const want = await t.shell.eval(`[...document.querySelectorAll('.exo-settings-scroll .exo-set-section > .exo-set-section-head h2')].map(h => h.textContent.trim())`)
    const at = await t.shell.eval(`(() => { const p = document.querySelector('.exo-settings-scroll'); p.scrollTop = 0; const r = p.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 } })()`)
    const read = () => {
      const pane = document.querySelector('.exo-settings-scroll')
      const box = pane.getBoundingClientRect()
      const shown = [...pane.querySelectorAll('.exo-set-section > .exo-set-section-head h2')].filter(h => { const r = h.getBoundingClientRect(); return r.top >= box.top - 1 && r.bottom <= box.bottom + 1 }).map(h => h.textContent.trim())
      return { top: Math.round(pane.scrollTop), end: pane.scrollHeight - pane.clientHeight - pane.scrollTop < 2, shown }
    }
    await sleep(400)
    const seen = new Set((await t.shell.call(read)).shown)
    let end = false
    for (let i = 0; i < 60 && !end; i++) {
      await t.shell.wheel(at.x, at.y, 240)
      await sleep(350)
      const now = await t.shell.call(read)
      for (const s of now.shown) seen.add(s)
      end = now.end
    }
    c.evidence.seen = [...seen]
    const failures = []
    if (!end) failures.push('the column never reached its end')
    for (const s of want) if (!seen.has(s)) failures.push(`${s} never came into view`)
    return failures
  })

  await run.check('settings', 'scrollbar', 'the column scrolls with no scrollbar drawn and takes no width for one', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    const read = () => {
      const p = document.querySelector('.exo-settings-scroll')
      return { width: p.clientWidth, offset: p.offsetWidth, scrollbar: getComputedStyle(p).scrollbarWidth, thumb: !!p.shadowRoot }
    }
    const state = await t.shell.call(read)
    c.evidence.state = state
    const failures = []
    if (state.scrollbar !== 'none') failures.push(`the column draws a scrollbar (scrollbar-width ${state.scrollbar})`)
    if (state.offset - state.width > 1) failures.push(`a scrollbar takes ${state.offset - state.width}px of the column`)
    if (state.thumb) failures.push('something still floats a thumb over the column')
    return failures
  })

  await run.check('settings', 'switches', 'every switch in Downloads turns and turns back', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    const ids = await t.shell.eval(`[...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => { const sec = b.closest('section'); return /^Downloads/.test(sec?.querySelector('h2, h3')?.textContent.trim() ?? '') }).map((b, i) => b.id || ('#' + i))`)
    c.metric('switches', ids.length)
    const failures = []
    for (let i = 0; i < ids.length; i++) {
      const pick = (n) => [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^Downloads/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? ''))[n]
      const before = await t.shell.call((n) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^Downloads/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.getAttribute('aria-checked') }, i)
      await t.shell.click(pick, i)
      const flipped = await t.shell.waitFor((n, was) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^Downloads/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.getAttribute('aria-checked') !== was }, { timeoutMs: 3000, args: [i, before] })
      await sleep(300)
      await t.shell.click(pick, i)
      const back = await t.shell.waitFor((n, was) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^Downloads/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.getAttribute('aria-checked') === was }, { timeoutMs: 3000, args: [i, before] })
      const label = await t.shell.call((n) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^Downloads/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.closest('.exo-set-item')?.querySelector('.exo-set-item-label')?.textContent.trim() ?? `switch ${n}` }, i)
      if (!flipped) failures.push(`${label} did not turn`)
      else if (!back) failures.push(`${label} did not turn back`)
    }
    return failures
  })

  // First run's Update DLSS, FSR and XeSS automatically (onboarding/PcStep.tsx) and this one in Downloads are one setting,
  // saved the one way (lib/settingsSave.ts): turned off here, it is still off after Exo restarts, then it goes back on.
  await run.check('settings', 'upscalers-kept', 'Update DLSS, FSR and XeSS automatically, turned off, stays off after a restart', async (c) => {
    if (t.quick || t.run.meta.attach) c.skip(t.quick ? 'quick run' : 'attached: Exo is not restarted')
    const sw = () => document.querySelector('#set-auto-upscalers')
    const reopen = async () => {
      if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return false
      if (!await page(t, 'Downloads')) return false
      return !!await t.shell.waitFor(() => !!document.querySelector('#set-auto-upscalers'), { timeoutMs: 5000 })
    }
    if (!await reopen()) return ['no Update DLSS, FSR and XeSS automatically switch in Settings › Downloads']
    const was = (await t.host('settings.get'))?.autoUpdateUpscalers !== false
    c.evidence.was = was
    const failures = []
    try {
      if (was) {
        await t.shell.click(sw)
        if (!await t.shell.waitFor(() => document.querySelector('#set-auto-upscalers')?.getAttribute('aria-checked') === 'false', { timeoutMs: 3000 })) return ['the switch did not turn off']
        await sleep(500)
      }
      await t.relaunch({})
      const saved = (await t.host('settings.get'))?.autoUpdateUpscalers
      c.evidence.afterRestart = saved
      if (saved !== false) failures.push(`after a restart the setting is ${saved}, not off`)
      if (!await reopen()) return [...failures, 'Settings › Downloads did not open after the restart']
      const shown = await t.shell.eval(`document.querySelector('#set-auto-upscalers')?.getAttribute('aria-checked')`)
      if (shown !== 'false') failures.push(`after a restart the switch shows ${shown}, not off`)
      await c.shot('upscalers-kept', t.shell)
    } finally {
      // Back as it was: on, through the switch, or through the host if the page can't.
      if (was && (await t.host('settings.get').catch(() => null))?.autoUpdateUpscalers === false) {
        if (await reopen().catch(() => false)) await t.shell.click(sw).catch(() => {})
        if (!await t.shell.waitFor(() => document.querySelector('#set-auto-upscalers')?.getAttribute('aria-checked') === 'true', { timeoutMs: 3000 }).catch(() => false)) {
          failures.push('the switch did not turn back on')
          await t.host('settings.set', { autoUpdateUpscalers: true }).catch(() => {})
        }
      }
    }
    return failures
  })

  // Read only: pressing it restarts Steam. The row shows where Steam can play offline (a saved sign-in) or is offline now.
  await run.check('settings', 'steam-offline', "Game stores' Steam Offline mode shows how Steam starts", async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    const steam = (await t.host('accounts.list'))?.steam ?? null
    c.evidence.steam = steam ? { offline: steam.offline, canOffline: steam.canOffline } : null
    const want = steam && (steam.canOffline || steam.offline) ? String(steam.offline) : 'none'
    const read = `document.querySelector('.exo-settings button[role="switch"][aria-label="Steam offline mode"]')?.getAttribute('aria-checked') ?? 'none'`
    const shown = await t.shell.waitFor((w) => (document.querySelector('.exo-settings button[role="switch"][aria-label="Steam offline mode"]')?.getAttribute('aria-checked') ?? 'none') === w, { timeoutMs: 5000, args: [want] })
    if (shown) return []
    const now = await t.shell.eval(read)
    return [`Steam's Offline mode shows ${now === 'none' ? 'no row' : now === 'true' ? 'on' : 'off'}, but Steam ${want === 'none' ? 'has no saved sign-in' : want === 'true' ? 'starts offline' : 'starts online'}`]
  })

  await run.check('settings', 'text-size', 'Text size scales the page and goes back', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    await general(t)
    const choose = async (label) => {
      await t.shell.click(() => document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]') ?? null)
      await sleep(300)
      await t.shell.click((l) => [...document.querySelectorAll('[role^="menuitem"]')].find(i => i.getBoundingClientRect().width && i.textContent.trim() === l), label)
      await sleep(600)
      return t.shell.eval(`getComputedStyle(document.documentElement).getPropertyValue('--exo-scale').trim()`)
    }
    const was = await t.shell.eval(`document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]')?.textContent.trim()`)
    const large = await choose('Large')
    const small = await choose('Small')
    await choose(was || 'Normal')
    c.evidence.scales = { large, small, was }
    const failures = []
    if (+large <= 1) failures.push(`Large set the scale to ${large}`)
    if (+small >= 1) failures.push(`Small set the scale to ${small}`)
    return failures
  })

  await run.check('settings', 'updates', 'About › Check for updates asks and answers (nothing is installed)', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    await about(t)
    const button = () => [...document.querySelectorAll('.exo-settings button')].find(b => /^(Check for updates|Checking…|Install update)$/.test(b.textContent.trim()))
    const was = await t.shell.eval(`(${button.toString()})()?.textContent.trim() ?? null`)
    c.evidence.was = was
    if (!was) return ['no Check for updates in About']
    if (was === 'Install update') c.skip('an update is already waiting (not installed by the suite)')
    const t0 = Date.now()
    await t.shell.click(button)
    const answer = await t.shell.waitFor(() => {
      const b = [...document.querySelectorAll('.exo-settings button')].find(b => /^(Check for updates|Install update)$/.test(b.textContent.trim()))
      const row = b?.closest('.exo-set-item')
      const said = row?.querySelector('[role="alert"]')?.textContent.trim() || row?.querySelector('.exo-set-state')?.textContent.trim()
      return b && !b.disabled && said && !/^Version /.test(said) ? said : null
    }, { timeoutMs: 20000 })
    c.metric('answer', Date.now() - t0, ' ms', 5000)
    c.evidence.answer = answer
    return answer ? [] : ['no answer within 20 s']
  })

  // Preview shows the real banner window: a picture its CSP let in (3.22.0's was a broken one), its name, its entrance.
  await run.check('settings', 'achievement-preview', 'Preview banner shows a real picture and arrives', async (c) => {
    await t.host('achievements.preview', { tier: 'rare' })
    const overlay = await openPage(t.port, 'overlay.exo-launcher.local', 10000)
    const seen = await overlay.waitFor(() => {
      const art = document.querySelector('.card .art'), card = document.querySelector('.card.in')
      return art?.complete && art.naturalWidth >= 64 && card ? { src: art.src.slice(0, 11), width: art.naturalWidth, name: card.querySelector('.name').textContent, moves: card.getAnimations().length, font: document.fonts.check('600 16px Goeist') } : null
    }, { timeoutMs: 8000 })
    overlay.close()
    c.evidence.banner = seen
    if (!seen) return ['the banner never had a loaded picture']
    const failures = []
    if (seen.src !== 'data:image/') failures.push(`picture from ${seen.src}`)
    if (!seen.name) failures.push('no name')
    if (!seen.font) failures.push('Goeist not loaded')
    return failures
  })

  // What's New holds the window until Continue (the update sheet as Exo opens holds it the same way, HoldWindow.tsx): every
  // other child of <body> inert, focus in it, the window's buttons drawn over the Bar's, no shortcut behind it heard; then
  // Continue gives back exactly what it took.
  await run.check('settings', 'whats-new-hold', "About › What's new holds the window until Continue, then lets go", async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    await about(t)
    const before = await t.shell.eval(`[...document.body.children].filter(n => n.inert).length`)
    // About's Changelog is What's New (its link with no address).
    if (!await t.shell.click(() => [...document.querySelectorAll('.exo-settings button')].find(b => /^(What's new|Changelog)$/.test(b.textContent.trim())))) return ["no Changelog (What's new) in About"]
    if (!await t.shell.waitFor(() => !!document.querySelector('.exo-whats-new')?.checkVisibility(), { timeoutMs: 5000 })) return ["What's New did not open"]
    await sleep(300)
    const held = await t.shell.eval(`(() => {
      const back = document.querySelector('.exo-whats-new-back')
      return {
        // Elements only: index.html's 0x0 SVG of glass filters can't be inert, and takes no input.
        othersInert: [...document.body.children].filter(n => n !== back && n instanceof HTMLElement && !(n instanceof HTMLScriptElement)).every(n => n.inert),
        focusIn: !!document.querySelector('.exo-whats-new')?.contains(document.activeElement),
        windowButtons: back?.querySelectorAll('.exo-window-buttons .exo-winbtn').length ?? 0,
      }
    })()`)
    await t.shell.key('k', { ctrl: true })
    await t.shell.key('Backspace')
    await sleep(400)
    const after = await t.shell.eval(`({
      search: [...document.querySelectorAll('[role="combobox"]')].some(e => e.checkVisibility()),
      settings: !!document.querySelector('.exo-settings')?.checkVisibility(),
      sheet: !!document.querySelector('.exo-whats-new')?.checkVisibility(),
    })`)
    await t.shell.click(() => [...document.querySelectorAll('.exo-whats-new-foot button')].find(b => b.textContent.trim() === 'Continue'))
    const gone = await t.shell.waitFor(() => !document.querySelector('.exo-whats-new'), { timeoutMs: 3000 })
    const released = await t.shell.eval(`({ inert: [...document.body.children].filter(n => n.inert).length, overflow: document.documentElement.style.overflow, settings: !!document.querySelector('.exo-settings')?.checkVisibility() })`)
    c.evidence.hold = { before, held, after, released }
    return [
      !held.othersInert && 'something under it still takes input',
      !held.focusIn && 'focus is not in the sheet',
      held.windowButtons !== 3 && `the window's buttons over it: ${held.windowButtons}`,
      after.search && 'Ctrl+K opened search behind it',
      !after.settings && 'a key behind it closed Settings',
      !after.sheet && 'a key closed it',
      !gone && 'Continue did not close it',
      released.inert !== before && `inert left behind: ${released.inert} (was ${before})`,
      released.overflow && `html overflow left set: ${released.overflow}`,
      !released.settings && 'Settings closed with it',
    ].filter(Boolean)
  })

  await run.check('settings', 'close', 'Escape closes Settings back to the room you were in', async () => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    await t.shell.key('Escape')
    const closed = await t.shell.waitFor(() => !document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 3000 })
    const s = await k.state()
    return [!closed && 'Escape did not close it', closed && s.room !== 'Library' && `it came back to ${s.room}`].filter(Boolean)
  })
}
