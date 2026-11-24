// PC, read: one page with no page list (This PC with its chips on its name's line, the sections), This PC's parts, the
// driver, the Graphics, Display and Motherboard menus (opened, never changed), Shaders' games, and the Apps sheet.
import { sleep } from '../cdp.mjs'

/** A section of the Exo AI room by its anchor, scrolled to the top of the room. */
const section = (id) => { const at = document.querySelector(`.exo-ai-room [data-pc-part="${id}"]`); at?.scrollIntoView({ block: 'start' }); return !!at }

/** Opens a part's menu (graphics, display, board, storage) the way a press does: focused, then pressed, so it hangs from the part. */
const openMenu = (menu) => { const b = document.querySelector(`.exo-ai-room [data-pc-menu="${menu}"]`); if (!b) return false; b.focus(); b.click(); return true }

export async function run(t) {
  const { run, k } = t
  await k.nav('Exo AI')

  await run.check('system', 'overview', "Exo AI leads with Auto Tune and This PC's parts, all on one page", async (c) => {
    const top = await t.shell.waitFor(() => {
      const name = document.querySelector('.exo-ai-room .exo-pc-about-name')?.textContent?.trim()
      const facts = [...document.querySelectorAll('.exo-ai-room .exo-pc-facts .exo-pc-fact-label')].map(d => d.textContent.trim())
      return name && facts.length >= 2 ? { name, facts } : null
    }, { timeoutMs: 15000 })
    c.evidence.top = top
    await t.shell.call(section, 'overview')
    await c.shot('overview', t.shell)
    const failures = top ? [] : ['This PC shows no name or what it runs']
    // What the PC is made of comes from Windows' own records on every PC: the processor, the memory and Windows as parts.
    const made = await t.shell.waitFor(() => {
      const facts = [...document.querySelectorAll('.exo-ai-room .exo-pc-about .exo-pc-facts .exo-pc-fact-label')].map(d => d.textContent.trim())
      return ['Processor', 'Memory', 'Windows'].every(f => facts.includes(f)) ? { facts } : null
    }, { timeoutMs: 15000 })
    if (!made) failures.push('This PC lacks the processor, memory or Windows')
    const updates = await t.shell.waitFor(() => { const u = document.querySelector('.exo-ai-room [data-pc-part="drivers"]'); return u?.querySelector('.exo-pc-section-count')?.textContent.trim() === 'Up to date' ? { empty: !u.querySelector('.exo-pc-pane')?.childElementCount, height: Math.round(u.getBoundingClientRect().height) } : null }, { timeoutMs: 8000 })
    c.evidence.updates = updates
    if (updates?.empty && updates.height > 80) failures.push(`Updates is up to date with nothing in it, yet ${updates.height}px tall`)
    const shape = await t.shell.eval(`({
      nav: !!document.querySelector('.exo-ai-room .exo-room-nav'),
      title: document.querySelector('.exo-ai-room .exo-ai-title')?.textContent?.trim() ?? '',
      stages: document.querySelectorAll('.exo-ai-room .exo-at-stage').length,
      sections: [...document.querySelectorAll('.exo-ai-room [data-pc-part]')].filter(s => s.checkVisibility()).map(s => s.dataset.pcPart),
    })`)
    c.evidence.shape = shape
    if (shape.nav) failures.push('the room still has a page list')
    if (shape.title !== 'Exo AI') failures.push(`room title reads "${shape.title || 'nothing'}", not "Exo AI"`)
    if (!shape.stages) failures.push('Auto Tune rendered no stages')
    for (const id of ['overview', 'drivers', 'shaders']) if (!shape.sections.includes(id)) failures.push(`no ${id} section`)
    const tuningState = await t.host('system.tuning')
    c.evidence.systemTuning = tuningState
    if (!tuningState?.ok) failures.push('system.tuning did not answer ok')
    failures.push(...await k.look(c, 'overview'))
    return failures
  })

  await run.check('system', 'graphics', "The card opens its Graphics menu, which always shows the driver's version; Updates shows the driver only with an update, as 581.42 → 581.57", async (c) => {
    await t.shell.call(section, 'drivers')
    // Updates has the driver only while it has something to do.
    const line = await t.shell.waitFor(() => { const row = [...document.querySelectorAll('.exo-ai-room [data-pc-part="drivers"] .exo-set-item')].find(i => /driver/i.test(i.querySelector('.exo-set-item-label')?.textContent ?? '')); return row ? row.innerText : document.querySelector('.exo-ai-room [data-pc-part="drivers"] .exo-pc-section-count')?.textContent.trim() || null }, { timeoutMs: 15000 })
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
    const games = await t.shell.waitFor(() => { const n = [...document.querySelectorAll('.exo-ai-room [data-pc-part="shaders"] img')].filter(i => i.checkVisibility()).length; return n > 2 ? n : null }, { timeoutMs: 15000 })
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

  await run.check('system', 'apps', "Apps opens as its own room with one list under its columns, its actions out of the way until pointed at, and Startup one press away", async (c) => {
    await k.nav('Apps')
    const listed = await t.shell.waitFor(() => document.querySelector('.exo-apps-room .exo-apps-list .exo-apps-row') ? true : null, { timeoutMs: 20000 })
    if (!listed) return ['the apps never listed']
    await t.shell.eval('new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))')
    const state = await t.shell.eval(`(() => {
      const sheet = document.querySelector('.exo-apps-room-surface') ?? document.querySelector('.exo-apps-room')
      const lists = ['.exo-apps-list'].map(sel => sheet.querySelector(sel)).filter(Boolean).map(list => ({ sel: '.' + list.classList[0], scrolls: list.scrollHeight > list.clientHeight + 1, fade: getComputedStyle(list).getPropertyValue('--exo-pool-bottom') }))
      const rows = [...sheet.querySelectorAll('.exo-apps-row')]
      return {
        sheet: sheet.scrollHeight - sheet.clientHeight, lists, runtimesInSheet: !!sheet.querySelector('.exo-runtimes'), rows: rows.length,
        columns: [...sheet.querySelectorAll('.exo-apps-sort')].map(b => b.textContent.trim()),
        // Nothing here is pointed at: no row's action shows at full emphasis, so the list isn't a wall of bright buttons.
        shownActions: rows.filter(r => r.querySelector('button') && !r.matches('.is-kept, :hover, :focus-within') && +getComputedStyle(r.querySelector('.exo-apps-act')).opacity >= 0.9).length,
        sideBySide: rows.length > 1 && rows[0].getBoundingClientRect().top === rows[1].getBoundingClientRect().top,
        hasPaths: sheet.textContent.includes(':' + String.fromCharCode(92) + 'Users' + String.fromCharCode(92)),
      }
    })()`)
    c.evidence.state = state
    await c.shot('apps', t.shell)
    const failures = []
    if (!state.columns.includes('Name') || !state.columns.includes('Size')) failures.push(`the list's columns are ${state.columns.join(', ')}`)
    if (state.sideBySide) failures.push('the rows stand side by side, not as one list')
    if (state.hasPaths) failures.push("the room shows a user's folder")
    const pressed = await t.shell.click((words) => [...document.querySelectorAll('.exo-apps-room .exo-shelf-chip')].find(b => b.textContent.trim().startsWith(words)), 'Startup')
    if (!pressed) failures.push('no Startup chip')
    else {
      const shows = await t.shell.waitFor(() => document.querySelector('.exo-apps-room .exo-apps-list section, .exo-apps-room .exo-apps-none, .exo-apps-room .exo-apps-row') ? true : null, { timeoutMs: 15000 })
      if (!shows) failures.push('Startup shows nothing')
      await c.shot('startup', t.shell)
      await t.shell.click((words) => [...document.querySelectorAll('.exo-apps-room .exo-shelf-chip')].find(b => b.textContent.trim().startsWith(words)), 'Apps')
    }
    if (state.runtimesInSheet) failures.push('the game runtimes are still in the Apps room')
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
