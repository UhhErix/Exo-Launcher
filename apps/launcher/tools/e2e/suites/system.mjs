// PC, read: one page with no page list (This PC with its chips on its name's line, the sections), This PC's parts, the
// driver, the Graphics, Display and Motherboard menus (opened, never changed), Shaders' games, and the Apps sheet.
import { sleep } from '../cdp.mjs'

/** A section of the PC room by its anchor, scrolled to the top of the room. */
const section = (id) => { const at = document.querySelector(`.exo-pc [data-pc-part="${id}"]`); at?.scrollIntoView({ block: 'start' }); return !!at }

/** Opens a part's menu (best, graphics, display, board) the way a press does: focused, then pressed, so it hangs from the part. */
const openMenu = (menu) => { const b = document.querySelector(`.exo-pc [data-pc-menu="${menu}"]`); if (!b) return false; b.focus(); b.click(); return true }

export async function run(t) {
  const { run, k } = t
  await k.nav('PC')

  await run.check('system', 'overview', "This PC leads with its name, Best settings on its line, and what it's made of, all on one page", async (c) => {
    const top = await t.shell.waitFor(() => {
      const name = document.querySelector('.exo-pc-about-name')?.textContent?.trim()
      const facts = [...document.querySelectorAll('.exo-pc-facts .exo-pc-fact-label')].map(d => d.textContent.trim())
      return name && facts.length >= 2 ? { name, facts } : null
    }, { timeoutMs: 15000 })
    c.evidence.top = top
    await t.shell.call(section, 'overview')
    await c.shot('overview', t.shell)
    const failures = top ? [] : ['This PC shows no name or what it runs']
    // What the PC is made of comes from Windows' own records on every PC: the processor, the memory and Windows as parts.
    const made = await t.shell.waitFor(() => {
      const facts = [...document.querySelectorAll('.exo-pc-about .exo-pc-facts .exo-pc-fact-label')].map(d => d.textContent.trim())
      return ['Processor', 'Memory', 'Windows'].every(f => facts.includes(f)) ? { facts } : null
    }, { timeoutMs: 15000 })
    if (!made) failures.push('This PC lacks the processor, memory or Windows')
    // The room reads as the other rooms do: the PC's name larger than the sections' 20px names (at the chosen Text size:
    // 18px at Small), and each section's name on the page, not inside its pane.
    const type = await t.shell.eval(`({
      scale: parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-scale')) || 1,
      name: parseFloat(getComputedStyle(document.querySelector('.exo-pc-about-name')).fontSize),
      heads: [...document.querySelectorAll('.exo-pc-section > h2')].filter(h => h.checkVisibility()).map(h => ({ text: h.textContent, size: parseFloat(getComputedStyle(h).fontSize), inPane: !!h.closest('.exo-system-panel') })),
    })`)
    c.evidence.type = type
    for (const head of type.heads) {
      if (head.inPane) failures.push(`"${head.text}" is named inside its pane`)
      if (head.size < 20 * type.scale - 0.5) failures.push(`"${head.text}" is named at ${head.size}px, under the rooms' ${+(20 * type.scale).toFixed(1)}px`)
      if (type.name <= head.size) failures.push(`the PC's name (${type.name}px) is no larger than "${head.text}"`)
    }
    // With nothing to show, Updates is only as tall as its title line, not a box filling the column.
    const updates = await t.shell.waitFor(() => { const u = document.querySelector('.exo-pc [data-pc-part="drivers"]'); return u?.querySelector('.exo-pc-section-count')?.textContent.trim() === 'Up to date' ? { empty: !u.querySelector('.exo-pc-pane')?.childElementCount, height: Math.round(u.getBoundingClientRect().height) } : null }, { timeoutMs: 8000 })
    c.evidence.updates = updates
    if (updates?.empty && updates.height > 80) failures.push(`Updates is up to date with nothing in it, yet ${updates.height}px tall`)
    const shape = await t.shell.eval(`({
      nav: !!document.querySelector('.exo-pc .exo-room-nav'),
      status: document.querySelector('.exo-pc [data-pc-menu="best"]')?.textContent?.trim() ?? '',
      sections: [...document.querySelectorAll('.exo-pc [data-pc-part]')].filter(s => s.checkVisibility()).map(s => s.dataset.pcPart),
      room: (() => { const r = document.querySelector('.exo-pc'); return { scroll: r.scrollHeight, height: r.clientHeight, width: innerWidth, tall: innerHeight } })(),
    })`)
    c.evidence.shape = shape
    if (shape.nav) failures.push('the room still has a page list')
    // Best settings sits on the PC's line: on, or how many things are left, never a lone word.
    if (!/^Best settings on$|^\d+ things? left to set$/.test(shape.status)) failures.push(`Best settings beside the name reads "${shape.status || 'nothing'}"`)
    for (const id of ['overview', 'drivers', 'shaders']) if (!shape.sections.includes(id)) failures.push(`no ${id} section`)
    // On a 1080p window the whole room is one page: nothing to scroll.
    if (shape.room.width >= 1280 && shape.room.tall >= 800 && shape.room.scroll > shape.room.height + 1) failures.push(`the room scrolls ${shape.room.scroll - shape.room.height}px on a ${shape.room.width}x${shape.room.tall} window`)
    // Its menu names every step this PC has, set or not (3.17.2 showed only "Set" on a PC with all set).
    if (await t.shell.call(openMenu, 'best')) {
      const steps = await t.shell.waitFor(() => { const li = [...document.querySelectorAll('.exo-pc-menu .exo-pc-best-steps li')]; return li.length ? li.map(l => l.textContent.trim()) : null }, { timeoutMs: 5000 })
      c.evidence.bestSteps = steps
      if (!steps) failures.push('Best settings opens with no step named')
      await c.shot('best-settings', t.shell)
      await t.shell.key('Escape')
      await sleep(300)
    } else failures.push("no Best settings on the PC's line")
    failures.push(...await k.look(c, 'overview'))
    return failures
  })

  await run.check('system', 'graphics', "The card opens its Graphics menu, which always shows the driver's version; Updates shows the driver only with an update, as 581.42 → 581.57", async (c) => {
    await t.shell.call(section, 'drivers')
    // Updates has the driver only while it has something to do.
    const line = await t.shell.waitFor(() => { const row = [...document.querySelectorAll('.exo-pc [data-pc-part="drivers"] .exo-set-item')].find(i => /driver/i.test(i.querySelector('.exo-set-item-label')?.textContent ?? '')); return row ? row.innerText : document.querySelector('.exo-pc [data-pc-part="drivers"] .exo-pc-section-count')?.textContent.trim() || null }, { timeoutMs: 15000 })
    c.evidence.updates = line
    await c.shot('drivers', t.shell)
    const failures = []
    if (line && /driver/i.test(line) && !/\d{3}\.\d{2}/.test(line)) failures.push(`the driver's row in Updates has no version: ${line}`)
    // The card in This PC opens its menu: the driver's version and the driver profile's rows, read only here.
    if (await t.shell.call(openMenu, 'graphics')) {
      const rows = await t.shell.waitFor(() => { const r = [...document.querySelectorAll('.exo-pc-menu .exo-set-item-label')]; return r.length ? r.map(l => l.textContent.trim()) : null }, { timeoutMs: 5000 })
      c.evidence.graphicsMenu = rows
      if (!rows) failures.push('the Graphics menu holds no rows')
      // Its first row is the graphics driver, whatever the card's maker: what runs, and what is newer or left out.
      const driver = await t.shell.waitFor(() => { const row = document.querySelector('.exo-pc-menu .exo-set-item'); const detail = row?.querySelector('.exo-set-item-detail')?.textContent ?? ''; return /\b\d+\.\d+/.test(detail) ? detail : null }, { timeoutMs: 15000 })
      c.evidence.driver = driver
      if (!driver) failures.push('the Graphics menu shows no driver version')
      await c.shot('graphics', t.shell)
      await t.shell.key('Escape')
      await sleep(300)
    } else failures.push('the card in This PC opens no menu')
    failures.push(...await k.look(c, 'graphics'))
    return failures
  })

  await run.check('system', 'parts', "This PC reads each drive's letters with its free space or what is wrong with it, and the board opens BIOS and Recovery", async (c) => {
    await t.shell.call(section, 'overview')
    // The first read sizes the shader caches and game folders: give it the host's own 45 s.
    const drives = await t.shell.waitFor(() => {
      const cells = [...document.querySelectorAll('.exo-pc-facts .exo-pc-fact')].filter(f => f.querySelector('.exo-pc-fact-label')?.textContent.startsWith('Drive'))
      return cells.length ? cells.map(f => ({ name: f.querySelector('.exo-pc-fact-value')?.textContent ?? '', line: f.querySelector('.exo-pc-fact-detail')?.textContent ?? '' })) : null
    }, { timeoutMs: 60000 })
    c.evidence.drives = drives
    const failures = drives ? [] : ['no drive in This PC']
    for (const d of drives ?? []) if (!d.line) failures.push(`${d.name} says nothing about its free space`)
    if (await t.shell.call(openMenu, 'board')) {
      const rows = await t.shell.waitFor(() => { const r = [...document.querySelectorAll('.exo-pc-menu .exo-set-item-label')]; return r.length ? r.map(l => l.textContent.trim()) : null }, { timeoutMs: 5000 })
      c.evidence.boardMenu = rows
      for (const row of ['BIOS', 'Recovery']) if (!rows?.includes(row)) failures.push(`the Motherboard menu has no ${row} row`)
      await c.shot('board', t.shell)
      await t.shell.key('Escape')
      await sleep(300)
    } else failures.push('the board in This PC opens no menu')
    failures.push(...await k.look(c, 'parts'))
    return failures
  })

  await run.check('system', 'display', "The screen in This PC opens Display, with settings whose menus open with choices", async (c) => {
    await t.shell.call(section, 'overview')
    if (!await t.shell.call(openMenu, 'display')) return ['the screen in This PC opens no menu']
    await sleep(1200)
    const menus = await t.shell.eval(`[...document.querySelectorAll('.exo-pc-menu button[aria-haspopup="menu"]')].filter(b => b.checkVisibility()).map(b => b.textContent.trim()).slice(0, 12)`)
    c.evidence.menus = menus
    if (!menus.length) { await k.closeAll(); return ['no settings with choices'] }
    const failures = []
    for (let i = 0; i < Math.min(3, menus.length); i++) {
      await t.shell.click((n) => [...document.querySelectorAll('.exo-pc-menu button[aria-haspopup="menu"]')].filter(b => b.checkVisibility())[n], i)
      // A menu fills as it opens (a screen's modes are read then): its choices, once there.
      await t.shell.waitFor(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width && m.querySelector('[role^="menuitem"]')) || null, { timeoutMs: 2000 })
      const items = await k.menuItems()
      if (!items?.length) failures.push(`"${menus[i]}" opened no choices`)
      await t.shell.key('Escape')
      await sleep(300)
    }
    await c.shot('display', t.shell)
    failures.push(...await k.look(c, 'display'))
    await k.closeAll()
    return failures
  })

  await run.check('system', 'shaders', 'Shaders lists the games with their state, without starting its engine', async (c) => {
    // The board shows the state the engine left; only Compile and Refresh start it.
    const before = (await t.host('shaders.addon'))?.state
    if (!await t.shell.call(section, 'shaders')) return ['no Shaders in the room']
    const games = await t.shell.waitFor(() => { const n = [...document.querySelectorAll('.exo-pc [data-pc-part="shaders"] img')].filter(i => i.checkVisibility()).length; return n > 2 ? n : null }, { timeoutMs: 15000 })
    c.metric('games', games ?? 0)
    await c.shot('shaders', t.shell)
    const after = (await t.host('shaders.addon'))?.state
    c.evidence.engine = { before, after }
    const failures = games ? [] : ['no games listed']
    if (before !== 'running' && (after === 'running' || after === 'starting')) failures.push('showing the board started the engine')
    failures.push(...await k.look(c, 'shaders'))
    await t.shell.call(section, 'overview')
    return failures
  })

  await run.check('system', 'apps', "Apps opens as a sheet with one list under its columns, its actions out of the way until pointed at, Startup and Removed one press away, scrolling on its own and fading at its foot; the game runtimes are in Updates", async (c) => {
    await t.shell.call(section, 'overview')
    // Open in the middle of the room, clear of a notice in the top corner (a new driver's sat over it at the top and took
    // the press), and pressed only where it is the one under the pointer.
    const at = await t.shell.eval(`(() => {
      const open = document.querySelector('.exo-pc [data-pc-menu="apps"]')
      if (!open) return null
      const r = open.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2, top = document.elementFromPoint(x, y)
      return open.contains(top) ? { x, y } : { under: top ? top.tagName.toLowerCase() + '.' + [...top.classList].slice(0, 2).join('.') : 'nothing' }
    })()`)
    if (!at) return ['no Apps chip']
    if (at.under) return [`the Apps chip is under ${at.under}`]
    await t.shell.clickAt(at.x, at.y)
    const listed = await t.shell.waitFor(() => document.querySelector('.exo-pc-apps-sheet .exo-apps-list .exo-apps-row') ? true : null, { timeoutMs: 20000 })
    if (!listed) { await k.closeAll(); return ['the apps never listed'] }
    const state = await t.shell.eval(`(() => {
      const sheet = document.querySelector('.exo-pc-apps-sheet')
      const lists = ['.exo-apps-list'].map(sel => sheet.querySelector(sel)).filter(Boolean).map(list => ({ sel: '.' + list.classList[0], scrolls: list.scrollHeight > list.clientHeight + 1, fade: getComputedStyle(list).getPropertyValue('--exo-pool-bottom') }))
      const rows = [...sheet.querySelectorAll('.exo-apps-row')]
      return {
        sheet: sheet.scrollHeight - sheet.clientHeight, lists, runtimesInSheet: !!sheet.querySelector('.exo-runtimes'), rows: rows.length,
        columns: [...sheet.querySelectorAll('.exo-apps-sort')].map(b => b.textContent.trim()),
        // Nothing here is pointed at: no row's action shows, so the list isn't a wall of buttons. A row ends level with the next.
        shownActions: rows.filter(r => r.querySelector('button') && !r.matches('.is-kept, :hover, :focus-within') && +getComputedStyle(r.querySelector('.exo-apps-act')).opacity > 0).length,
        sideBySide: rows.length > 1 && rows[0].getBoundingClientRect().top === rows[1].getBoundingClientRect().top,
        hasPaths: sheet.textContent.includes(':' + String.fromCharCode(92) + 'Users' + String.fromCharCode(92)),
      }
    })()`)
    c.evidence.state = state
    await c.shot('apps', t.shell)
    const failures = []
    if (!state.columns.includes('Name') || !state.columns.includes('Size')) failures.push(`the list's columns are ${state.columns.join(', ')}`)
    if (state.shownActions > 0) failures.push(`${state.shownActions} rows show their action at rest`)
    if (state.sideBySide) failures.push('the rows stand side by side, not as one list')
    if (state.hasPaths) failures.push("the sheet shows a user's folder")
    // The two other views, by their chips only: neither press removes anything.
    for (const view of ['Starts with Windows', 'Removed']) {
      const pressed = await t.shell.click((words) => [...document.querySelectorAll('.exo-pc-apps-sheet .exo-shelf-chip')].find(b => b.textContent.trim().startsWith(words)), view)
      if (!pressed) { failures.push(`no ${view} chip`); continue }
      const shows = await t.shell.waitFor(() => document.querySelector('.exo-pc-apps-sheet .exo-apps-list section, .exo-pc-apps-sheet .exo-apps-none') ? true : null, { timeoutMs: 15000 })
      if (!shows) failures.push(`${view} shows nothing`)
      await c.shot(view.toLowerCase().replace(/ /g, '-'), t.shell)
    }
    if (state.sheet > 0) failures.push(`the sheet itself scrolls ${state.sheet}px`)
    for (const list of state.lists) if (list.scrolls && list.fade !== '40px') failures.push(`${list.sel} runs on without fading at its foot`)
    if (state.runtimesInSheet) failures.push('the game runtimes are still in the Apps sheet')
    await k.closeAll()
    return failures
  })

  await run.check('system', 'shaders-engine', "Shaders' engine leaves soon after its work", async (c) => {
    // Only Compile and Refresh start the engine (the board shown above must not have); one that runs exits by itself
    // 15 s after its last scan or compile ends, whether or not the board is on screen. shaders.addon never starts it.
    const status = await t.host('shaders.addon')
    if (!status?.present) { c.skip('Exo Shaders is not installed'); return [] }
    c.evidence.withBoard = status.state
    const t0 = Date.now()
    let state = status.state
    while (state === 'running' && Date.now() - t0 < 60_000) { await sleep(5000); state = (await t.host('shaders.addon'))?.state }
    c.metric('exitSeconds', Math.round((Date.now() - t0) / 1000))
    c.evidence.after = state
    if (state !== 'running') return []
    const snap = (await t.host('shaders.call', { method: 'snapshot', params: {} }))?.result
    // Work that waits for the PC to be idle doesn't keep it: only a compile, the started queue or a scan does.
    const working = !!(snap?.queue?.current || snap?.queue?.running || snap?.app?.scanning)
    c.evidence.working = working
    return working ? [] : ['the engine still runs a minute after the board left the screen, with nothing to do']
  })
}
