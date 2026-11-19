// Layout, everywhere a person goes: every room and the states it opens (sheets, menus, Ctrl+K, each Settings section,
// a Store page, Music's pages and panels, each page of PC, Discord's channel with its member list, threads, pins,
// inbox, server pages, a Server Guide resource, a forum and each section of its settings), then at three window sizes and
// at the largest text size. In each, no two controls may overlap, no control may be cut by what holds it and no words
// may be cut off (lib/page.mjs › layoutFaults); every fault is reported with its path, its box and a crop of it.
// Nothing is sent, joined or changed: Discord is only looked at, in the test place.
import { sleep } from '../cdp.mjs'
import { ROOMS } from '../lib/kit.mjs'

const TEST_SERVER = 'Exo', TEST_CHANNEL = 'mod-chat'
const LOOKED_AT = ROOMS.filter(r => r !== 'Devices') // Exo Mouse is its own program in a frame (mouse/ has its own checks).

/**
 * Every room's layout as it first shows; used by the window-size, text-size and big-screen passes too. `go` changes
 * room (zoom's passes click for real: a page drawn zoomed takes DevTools' clicks off target).
 */
export async function roomsLayout(t, c, tag, go = (room) => t.k.nav(room)) {
  const failures = []
  for (const room of LOOKED_AT) {
    await go(room)
    await sleep(900)
    await t.k.settle(300, 6000)
    failures.push(...await t.k.layout(c, `${tag}-${room.toLowerCase()}`))
  }
  return failures
}

/** The first of a selector's elements that shows (rooms stay mounted, hidden); passed to the page as a finder. */
const visible = (sel) => [...document.querySelectorAll(sel)].find(e => e.checkVisibility())

async function openTestChannel(t) {
  await t.k.closeAll()
  await t.shell.key('k', { ctrl: true })
  if (!await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 3000 })) return false
  await t.shell.type(TEST_CHANNEL)
  const pick = (n, s) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].find(o => { const x = o.querySelectorAll('span span'); return x[0]?.textContent.trim() === n && x[1]?.textContent.trim() === s })
  if (!await t.shell.waitFor(pick, { timeoutMs: 8000, args: [TEST_CHANNEL, TEST_SERVER] })) { await t.shell.key('Escape'); return false }
  await t.shell.click(pick, TEST_CHANNEL, TEST_SERVER)
  return !!await t.shell.waitFor((n) => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') === n, { timeoutMs: 10000, args: [TEST_CHANNEL] })
}

/**
 * Opens something, looks, closes it: each `[label, open, close]` in `steps`. open returns true when it is on screen,
 * false (or throws) when it did not open, which FAILS the check (a renamed selector must not turn into a pass with
 * nothing looked at), or `notHere(why)` when this PC has nothing to open there (no forum in the test server), which is
 * said in the report as a warning.
 */
export async function walk(t, c, steps) {
  const failures = []
  for (const [label, open, close] of steps) {
    let shown = false
    try { shown = await open() } catch (error) { c.evidence[`${label}.error`] = String(error?.message ?? error) }
    if (shown?.notHere) { c.warn(`${label} not tried: ${shown.notHere}`); if (close) await close().catch(() => {}); continue }
    if (!shown) {
      failures.push(`${label} did not open`)
      await c.shot(`${label}-did-not-open`, t.shell).catch(() => {})
      if (close) await close().catch(() => {})
      continue
    }
    await sleep(500)
    await t.k.settle(300, 6000)
    failures.push(...await t.k.layout(c, label))
    if (close) await close().catch(() => {})
  }
  return failures
}

/** What a walk step returns when this PC has nothing to open there. */
export const notHere = (why) => ({ notHere: why })

export async function run(t) {
  const { run, k } = t
  const click = (finder, ...args) => t.shell.click(finder, ...args)
  const wait = (fn, ms = 5000, args = []) => t.shell.waitFor(fn, { timeoutMs: ms, args })

  await run.check('layout', 'rooms', 'every room: no controls over each other, none cut, no words cut off', async (c) => {
    return roomsLayout(t, c, 'room')
  }, { shell: t.shell })

  await run.check('layout', `library`, `the Library's sheet, menus and Ctrl+K`, async (c) => {
    await k.nav('Library')
    return walk(t, c, [
      [`game-sheet`, async () => { await click(() => [...document.querySelectorAll('.exo-game-grid button[data-game-id]')].find(b => !b.closest('.exo-folder-tile'))); return wait(() => !!document.querySelector('.exo-game-overlay-stage .exo-game-title')) }, () => k.closeAll()],
      [`add-menu`, async () => { await click(visible, '.exo-library-more'); return wait(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width)) }, () => k.closeAll()],
      [`sort-menu`, async () => { await click(visible, '.exo-shelf-sort-trigger'); return wait(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width)) }, () => k.closeAll()],
      [`ctrl-k`, async () => { await t.shell.key('k', { ctrl: true }); if (!await wait(() => document.activeElement?.getAttribute('role') === 'combobox', 3000)) return false; await t.shell.type('a'); return wait(() => document.querySelectorAll('[role="dialog"] [role="option"]').length > 2, 4000) }, () => k.closeAll()],
    ])
  }, { shell: t.shell })

  await run.check('layout', `settings`, `every Settings section`, async (c) => {
    await k.closeAll()
    await click(() => document.querySelector('.exo-settings-btn'))
    if (!await wait(() => !!document.querySelector('.exo-settings')?.checkVisibility())) return ['Settings did not open']
    // One column: each section found by its heading and scrolled to the column's top.
    const sections = await t.shell.eval(`[...document.querySelectorAll('.exo-settings .exo-set-section > .exo-set-section-head h2')].map(h => h.textContent.trim())`)
    const failures = await walk(t, c, sections.map(s => [`settings-${s.toLowerCase().replace(/\W+/g, '-')}`, async () => {
      await t.shell.call((n) => { const h = [...document.querySelectorAll('.exo-settings .exo-set-section > .exo-set-section-head h2')].find(e => e.textContent.trim() === n); const p = h?.closest('.exo-settings-scroll'); if (p) p.scrollTop += h.getBoundingClientRect().top - p.getBoundingClientRect().top - 26 }, s)
      await sleep(700)
      return true
    }]))
    await k.closeAll()
    return failures
  }, { shell: t.shell })

  await run.check('layout', `store`, `the Store and a game's page`, async (c) => {
    await k.nav('Store')
    await wait(() => document.querySelectorAll('section.exo-store-row').length > 1, 12000)
    return walk(t, c, [[`store-page`, async () => {
      await click(() => document.querySelector('.exo-store-hero-stage'))
      if (!await wait(() => !!document.querySelector('.exo-sgp'), 15000)) return false
      await wait(() => !document.querySelector('.exo-sgp-placeholder'), 15000)
      return true
    }, () => k.closeAll()]])
  }, { shell: t.shell })

  await run.check('layout', `music`, `Music's search, a playlist and its panels`, async (c) => {
    await k.nav('Music')
    if (await wait(() => document.querySelectorAll('.exo-music-card, .exo-music-row').length > 0 ? 'ok' : document.querySelector('.exo-music-signin-note, .exo-music-error') ? 'off' : null, 25000) !== 'ok') c.skip('no music service signed in')
    const field = '.exo-music-search input[aria-label^="Search"]'
    const panels = await t.shell.eval(`[...document.querySelectorAll('.exo-music-bar .exo-music-toggle[aria-pressed="false"]')].map(b => b.getAttribute('aria-label')).filter(Boolean)`)
    return walk(t, c, [
      [`music-search`, async () => { if (!await k.fill(field, 'lofi')) return false; await t.shell.key('Enter'); return wait(() => document.querySelectorAll('.exo-music-row, .exo-music-card').length > 3, 12000) },
        async () => { await k.fill(field, ''); await click(() => [...document.querySelectorAll('.exo-music-side button, nav button')].find(b => b.textContent.trim() === 'Home')); await sleep(800) }],
      [`music-playlist`, async () => { await click(() => document.querySelector('.exo-music-card .exo-music-card-open, .exo-music-card button')); return wait(() => document.querySelectorAll('.exo-music-row').length > 2 && !!document.querySelector('.exo-music-header-text'), 12000) }],
      ...panels.map(p => [`music-${p.toLowerCase().replace(/\W+/g, '-')}`,
        async () => click((l) => document.querySelector(`.exo-music-bar .exo-music-toggle[aria-label="${l}"]`), p),
        async () => { await click((l) => document.querySelector(`.exo-music-bar .exo-music-toggle[aria-label="${l}"]`), p); await sleep(400) }]),
      // Jam, Friend activity, Devices and Sound wait in More.
      ...['Jam', 'Friend activity', 'Devices', 'Sound'].map(p => [`music-${p.toLowerCase().replace(/\W+/g, '-')}`,
        async () => {
          if (!await click(() => document.querySelector('.exo-music-bar [aria-label="More panels"]'))) return false
          await sleep(200)
          // Jam and Friend activity are Spotify's: another service's More has no such line.
          if (!await t.shell.call((l) => [...document.querySelectorAll('[role^="menuitem"]')].some(b => b.textContent.trim() === l), p)) return notHere(`this music service has no ${p}`)
          return !!await click((l) => [...document.querySelectorAll('[role^="menuitem"]')].find(b => b.textContent.trim() === l), p)
        },
        async () => { await k.closeAll(); await sleep(400) }]),
    ])
  }, { shell: t.shell })

  await run.check('layout', `pc`, `each section of PC, and its Apps sheet,`, async (c) => {
    await k.nav('PC')
    const parts = await t.shell.eval(`[...document.querySelectorAll('.exo-pc [data-pc-part]')].filter(s => s.checkVisibility()).map(s => s.dataset.pcPart)`)
    return walk(t, c, [
      ...parts.map(p => [`pc-${p}`, async () => t.shell.call((id) => { const at = document.querySelector(`.exo-pc [data-pc-part="${id}"]`); at?.scrollIntoView({ block: 'start' }); return !!at }, p)]),
      [`pc-apps-sheet`, async () => !!await click(() => document.querySelector('.exo-pc [data-pc-menu="apps"]')) && wait(() => !!document.querySelector('.exo-pc-apps-sheet .exo-apps-row'), 20000),
        () => k.closeAll()],
      // Best settings on the PC's line, and the card's, the screen's and the board's menus.
      ...['best', 'graphics', 'display', 'board'].map(menu => [`pc-${menu}-menu`,
        async () => !!await click((m) => document.querySelector(`.exo-pc [data-pc-menu="${m}"]`), menu) && wait(() => !!document.querySelector('.exo-pc-menu .exo-set-item, .exo-pc-menu .exo-pc-best'), 5000),
        () => k.closeAll()]),
      [`pc-upscalers-sheet`, async () => !!await click(() => [...document.querySelectorAll('.exo-pc .exo-pc-chip')].find(b => b.textContent.includes('Upscalers'))) && wait(() => !!document.querySelector('.exo-pc-upscalers-sheet'), 5000),
        () => k.closeAll()],
    ])
  }, { shell: t.shell })

  await run.check('layout', `discord`, `Discord's channel, side panels, server pages, a forum and its settings`, async (c) => {
    await k.nav('Discord')
    if (await wait(() => document.querySelector('main.exo-dc') ? 'client' : document.querySelector('main.exo-dc-signin') ? 'signin' : null, 30000) !== 'client') c.skip('Discord is not signed in')
    if (!await openTestChannel(t)) c.skip(`no ${TEST_SERVER} #${TEST_CHANNEL}`)
    const header = (label) => click(visible, `main.exo-dc button[aria-label="${label}"]`)
    const toggle = (label) => async () => !!await header(label)
    const home = await t.host('discord.home').catch(() => null)
    const guild = home?.guilds?.find(g => g.name === TEST_SERVER)
    const list = guild ? await t.host('discord.channels', { guildId: guild.id }).catch(() => null) : null
    const forum = list?.channels?.find(ch => ch.kind === 'forum')?.name ?? null
    const channels = () => t.shell.waitFor(() => !!document.querySelector('nav.exo-dc-channels')?.checkVisibility(), { timeoutMs: 3000 })
    const closePanel = async () => { await k.closeAll(); if (await t.shell.eval(`!!document.querySelector('nav.exo-dc-channels.exo-dc-popover')?.checkVisibility()`)) await t.shell.key('Escape'); await sleep(300) }
    const page = (name) => async () => {
      await channels()
      if (!await wait(() => !!document.querySelector('nav.exo-dc-channels')?.checkVisibility(), 2000)) return false
      if (!await t.shell.call((p) => [...document.querySelectorAll('.exo-dc-channels button, .exo-dc-channels a')].some(b => b.textContent.trim() === p), name)) return notHere(`${TEST_SERVER} has no ${name}`)
      if (!await click((p) => [...document.querySelectorAll('.exo-dc-channels button, .exo-dc-channels a')].find(b => b.textContent.trim() === p), name)) return false
      await sleep(1200)
      return true
    }
    // Pinned messages, Threads and the rest wait in More.
    const more = (name) => async () => { if (!await header('More')) return false; await sleep(200); return !!await click((p) => [...document.querySelectorAll('main.exo-dc [role="menu"][aria-label="More"] [role="menuitem"]')].find(b => b.textContent.trim() === p), name) }
    const failures = await walk(t, c, [
      [`discord-channel`, async () => true],
      [`discord-threads`, more('Threads'), () => k.closeAll()],
      [`discord-thread`, async () => {
        if (!await more('Threads')()) return false
        await sleep(800)
        const thread = '[role="dialog"][aria-label="Threads"] .exo-dc-item, [role="dialog"][aria-label="Threads"] li button'
        if (!await t.shell.call((s) => !!document.querySelector(s), thread)) return notHere(`#${TEST_CHANNEL} has no thread`)
        return !!await click(visible, thread)
      }, () => openTestChannel(t)],
      [`discord-pins`, more('Pinned messages'), () => k.closeAll()],
      [`discord-inbox`, toggle('Inbox'), () => k.closeAll()],
      // The channel column, or with no room for it the dropdown from the title.
      ['discord-channels', async () => { await channels(); return wait(() => !!document.querySelector('nav.exo-dc-channels')?.checkVisibility(), 2000) }, closePanel],
      // Events opens a dialog over the column; the others open as the page.
      ...['Server Guide', 'Events', 'Browse channels', 'Members'].map(p => [`discord-${p.toLowerCase().replace(/\W+/g, '-')}`, page(p), p === 'Events' ? closePanel : undefined]),
      // A Server Guide resource opens its channel in a side panel beside the guide.
      [`discord-guide-resource`, async () => {
        const guide = await page('Server Guide')()
        if (guide !== true) return guide
        if (!await t.shell.call(() => !!document.querySelector('main.exo-dc .exo-dc-resource'))) return notHere(`${TEST_SERVER}'s guide has no resource`)
        return !!await click(visible, 'main.exo-dc .exo-dc-resource') && await sleep(1500).then(() => true)
      }, () => openTestChannel(t)],
      [`discord-forum`, async () => { if (!forum) return notHere(`${TEST_SERVER} has no forum`); await channels(); return !!await click((n) => [...document.querySelectorAll('.exo-dc-channels .exo-dc-item')].find(b => b.textContent.trim() === n), forum) && await sleep(1500).then(() => true) }],
      [`discord-forum-post`, async () => {
        if (!forum) return notHere(`${TEST_SERVER} has no forum`)
        if (!await t.shell.call(() => !!document.querySelector('.exo-dc-post'))) return notHere('the forum has no post')
        return !!await click(visible, '.exo-dc-post') && await sleep(1500).then(() => true)
      }, () => openTestChannel(t)],
    ])
    // Discord's settings, section by section.
    await k.closeAll()
    if (await click(() => document.querySelector('.exo-dc-me-who')) && await click(() => [...document.querySelectorAll('.exo-dc-me [role="menuitem"]')].find(b => b.textContent.trim() === 'Edit profile')) && await wait(() => !!document.querySelector('.exo-dc-settings-page')?.getClientRects().length, 6000)) {
      // Each of Discord's pages, from Settings' own list.
      const sections = await t.shell.eval(`[...document.querySelectorAll('.exo-settings-nav-sub button')].map(b => b.textContent.trim()).filter(Boolean)`)
      failures.push(...await walk(t, c, sections.map(s => [`discord-settings-${s.toLowerCase().replace(/\W+/g, '-')}`,
        async () => { await click((n) => [...document.querySelectorAll('.exo-settings-nav-sub button')].find(b => b.textContent.trim() === n), s); await sleep(600); return true }])))
      await t.shell.key('Escape')
    } else {
      failures.push("Discord's settings did not open from Edit profile")
      await c.shot('discord-settings-did-not-open', t.shell).catch(() => {})
    }
    return failures
  }, { shell: t.shell })

  await run.check('layout', 'sizes', 'every room at three window sizes', async (c) => {
    const w0 = await t.win.window()
    const m = w0.monitor.work
    const failures = []
    for (const [fw, fh] of [[0.5, 0.6], [0.7, 0.75], [1, 1]]) {
      await t.win.place(m.x + 20, m.y + 20, Math.round(m.w * fw) - 40, Math.round(m.h * fh) - 40)
      await sleep(700)
      failures.push(...await roomsLayout(t, c, `${Math.round(fw * 100)}x${Math.round(fh * 100)}`))
    }
    await t.win.maximize()
    await sleep(600)
    return failures
  }, { shell: t.shell })

  await run.check('layout', 'text-large', 'every room and Settings at the largest text size', async (c) => {
    const choose = async (label) => {
      await k.closeAll()
      await click(() => document.querySelector('.exo-settings-btn'))
      await wait(() => !!document.querySelector('.exo-settings')?.checkVisibility())
      await click(() => document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]') ?? null)
      await sleep(300)
      await click((l) => [...document.querySelectorAll('[role^="menuitem"]')].find(i => i.getBoundingClientRect().width && i.textContent.trim() === l), label)
      await sleep(600)
    }
    await k.closeAll()
    await click(() => document.querySelector('.exo-settings-btn'))
    await wait(() => !!document.querySelector('.exo-settings')?.checkVisibility())
    const was = await t.shell.eval(`document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]')?.textContent.trim()`)
    const sizes = await t.shell.call(() => { document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]')?.click(); return new Promise(r => setTimeout(() => { const items = [...document.querySelectorAll('[role^="menuitem"]')].filter(i => i.getBoundingClientRect().width).map(i => i.textContent.trim()); r(items) }, 300)) })
    await k.closeAll()
    const largest = sizes[sizes.length - 1]
    if (!largest) return ['no Text size choices']
    c.evidence.textSize = { was, largest }
    const failures = []
    try {
      await choose(largest)
      failures.push(...await k.layout(c, `text-${largest.toLowerCase()}-settings`))
      await k.closeAll()
      failures.push(...await roomsLayout(t, c, `text-${largest.toLowerCase()}`))
    } finally { await choose(was || 'Normal'); await k.closeAll() }
    return failures
  }, { shell: t.shell })
}
