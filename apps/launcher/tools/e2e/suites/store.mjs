// The Store, used: its shelves, the banner's picks, a shelf's arrows, search, and a game's page (with its
// requirements against this PC). Nothing is bought or wishlisted (the calendar is only opened and read).
import { sleep } from '../cdp.mjs'

export async function run(t) {
  const { run, k } = t
  await k.nav('Store')
  await t.shell.waitFor(() => document.querySelector('.exo-store-hero') && document.querySelectorAll('.exo-store-row').length > 1, { timeoutMs: 20000 })

  await run.check('store', 'shelves', 'the Store shows its banner and shelves, each with games', async (c) => {
    const rows = await t.shell.eval(`[...document.querySelectorAll('section.exo-store-row')].map(r => ({ title: r.querySelector('h2, h3')?.textContent.trim(), items: r.querySelectorAll('button, a').length }))`)
    c.evidence.rows = rows
    const failures = []
    if (!await t.shell.eval(`!!document.querySelector('.exo-store-hero')`)) failures.push('no banner')
    if (rows.length < 2) failures.push(`only ${rows.length} shelves`)
    for (const r of rows) if (r.items < 2) failures.push(`${r.title} is empty`)
    return failures
  })

  await run.check('store', 'banner-pick', 'picking another game beside the banner shows it in the banner', async (c) => {
    const before = await t.shell.eval(`document.querySelector('.exo-store-hero-stage')?.getAttribute('aria-label')`)
    const picked = await t.shell.click(() => document.querySelectorAll('.exo-store-hero-pick:not(.is-on)')[1] ?? null)
    if (!picked) c.skip('one game in the banner')
    const after = await t.shell.waitFor((b) => { const now = document.querySelector('.exo-store-hero-stage')?.getAttribute('aria-label'); return now && now !== b ? now : null }, { timeoutMs: 3000, args: [before] })
    c.evidence.before = before
    c.evidence.after = after
    return after ? [] : ['the banner did not change']
  })

  await run.check('store', 'shelf-arrows', 'a shelf\'s next arrow scrolls it', async (c) => {
    const moved = await t.shell.eval(`(async () => {
      const row = [...document.querySelectorAll('section.exo-store-row')].find(r => { const s = r.querySelector('[class*="track"], [class*="scroll"]'); return s && s.scrollWidth > s.clientWidth + 10 })
      if (!row) return null
      const track = row.querySelector('[class*="track"], [class*="scroll"]')
      const next = [...row.querySelectorAll('button')].find(b => /^More /.test(b.getAttribute('aria-label') || '') && !b.disabled)
      if (!next) return 'no next arrow'
      const x0 = track.scrollLeft
      next.click()
      await new Promise(r => setTimeout(r, 700))
      const x1 = track.scrollLeft
      track.scrollLeft = 0
      return x1 > x0 ? true : 'did not scroll'
    })()`)
    if (moved === null) c.skip('no shelf wider than the room')
    return moved === true ? [] : [moved]
  })

  await run.check('store', 'search', 'searching the store lists matching games', async (c) => {
    await k.fill('.exo-store-search input', 'portal')
    // Real cards, not the loading grid's twelve placeholders (which this once counted as results).
    const results = await t.shell.waitFor(() => { const n = document.querySelectorAll('.exo-store-grid .exo-store-card:not(.is-skeleton)').length; return n || null }, { timeoutMs: 15000 })
    c.metric('results', results ?? 0)
    await c.shot('results', t.shell)
    await k.fill('.exo-store-search input', '')
    await sleep(800)
    return results ? [] : ['no results for "portal"']
  })

  await run.check('store', 'owned', 'a game you own has Library in place of its price, on its card and its page, and it opens that game', async (c) => {
    // A Steam game the library owns, installed or not, found on the Store by its own name.
    const library = (await t.host('library.get', {}))?.games ?? []
    const mine = library.find(g => g.owned && /^steam:\d+$/.test(g.id) && !/[®™]/.test(g.title))
    if (!mine) c.skip('no Steam game in the library')
    await k.fill('.exo-store-search input', mine.title)
    const appId = mine.id.slice('steam:'.length)
    const name = mine.title
    const slot = await t.shell.waitFor((title) => {
      const hit = [...document.querySelectorAll('.exo-store-grid .exo-store-card-slot')].find(s => s.querySelector('.exo-store-card-title')?.textContent.trim() === title)
      return hit ? { library: hit.querySelector('.exo-store-library')?.textContent.trim() ?? null, price: !!hit.querySelector('.exo-store-price') } : null
    }, { timeoutMs: 15000, args: [name] })
    c.evidence.game = appId
    if (!slot) { await k.fill('.exo-store-search input', ''); c.skip(`the Store did not list "${name}"`) }
    const failures = []
    if (slot.library !== 'Library') failures.push('its card has no Library button')
    if (slot.price) failures.push('its card still shows a price')
    await c.shot('card', t.shell)
    await t.shell.click((title) => [...document.querySelectorAll('.exo-store-grid .exo-store-card')].find(b => b.querySelector('.exo-store-card-title')?.textContent.trim() === title) ?? null, name)
    const page = await t.shell.waitFor(() => { const buy = document.querySelector('.exo-sgp-buy'); return buy ? buy.innerText : null }, { timeoutMs: 8000 })
    await sleep(600)
    if (!/Library/.test(page ?? '')) failures.push('its page has no Library button')
    if (/Get on /.test(await t.shell.eval(`document.querySelector('.exo-sgp-buy')?.innerText ?? ''`))) failures.push('its page still offers Get on a store')
    if (await t.shell.eval(`!!document.querySelector('.exo-sgp-buy .exo-store-price')`)) failures.push('its page still shows a price')
    await c.shot('page', t.shell)
    await t.shell.key('Escape')
    await t.shell.waitFor(() => !document.querySelector('.exo-sgp'), { timeoutMs: 3000 })
    // Its card's Library, a quiet button that only opens a page: a run presses nothing primary.
    await t.shell.click((title) => [...document.querySelectorAll('.exo-store-grid .exo-store-card-slot')].find(s => s.querySelector('.exo-store-card-title')?.textContent.trim() === title)?.querySelector('.exo-store-library') ?? null, name)
    const room = await t.shell.waitFor(() => document.querySelector('.exo-game-room .exo-game-page')?.checkVisibility() ? true : null, { timeoutMs: 5000 })
    if (!room) failures.push('Library did not open the game')
    await t.shell.key('Escape')
    await k.nav('Store')
    await k.fill('.exo-store-search input', '')
    await sleep(500)
    return failures
  })

  await run.check('store', 'page', 'a game\'s page opens with its price, About and requirements against this PC', async (c) => {
    await t.shell.waitFor(() => document.querySelectorAll('section.exo-store-row').length > 1, { timeoutMs: 10000 })
    const t0 = Date.now()
    // The banner's game opens in the Store (Free on Epic's cards open Epic's own site: never clicked here).
    const opened = await t.shell.click(() => document.querySelector('.exo-store-hero-stage'))
    if (!opened) return ['no game to open']
    const page = await t.shell.waitFor(() => document.querySelector('.exo-sgp') && document.querySelector('.exo-sgp h1, .exo-sgp h2, .exo-sgp-side h2')?.textContent.trim(), { timeoutMs: 15000 })
    c.metric('open', Date.now() - t0, ' ms', 1500)
    if (!page) return ['the page did not open']
    await t.shell.waitFor(() => !document.querySelector('.exo-sgp-placeholder, .exo-sgp-facts .exo-wait, .exo-sgp-specs .exo-wait'), { timeoutMs: 15000 })
    await sleep(600)
    const parts = await t.shell.eval(`({ about: /About/.test(document.querySelector('.exo-sgp')?.innerText ?? ''), reqs: /System requirements/.test(document.querySelector('.exo-sgp')?.innerText ?? ''), thisPc: /This PC/.test(document.querySelector('.exo-sgp')?.innerText ?? '') || [...document.querySelectorAll('.exo-sgp-spec-mine')].some(e => e.textContent.trim().length > 0), specTable: !!document.querySelector('.exo-sgp-spec-grid'), buy: !!document.querySelector('.exo-sgp-buy'), facts: Object.fromEntries([...document.querySelectorAll('.exo-sgp-facts dl > div')].filter(d => d.querySelector('dt')).map(d => [d.querySelector('dt')?.textContent, d.querySelector('dd')?.textContent])) })`)
    c.evidence.title = page
    c.evidence.parts = parts
    await c.shot('page', t.shell)
    const failures = await k.look(c, 'page')
    if (!parts.buy) failures.push('no buying section')
    if (!parts.about) c.warn('no About')
    const appId = (await t.host('store.search', { query: page }).catch(() => null))?.items?.find(r => r.name === page)?.appId
    const answer = appId ? await t.host('store.game', { appId: String(appId) }).catch(() => null) : null
    if (!answer?.comingSoon) {
      for (const name of ['Reviews', 'Languages']) if (!parts.facts?.[name]) failures.push(`no ${name} fact`)
    } else if (!parts.facts?.Languages) {
      failures.push('no Languages fact')
    }
    const named = await t.shell.eval(`[...document.querySelectorAll('.exo-sgp-facts dl > div')].filter(d => d.querySelector('dt') && !d.querySelector('dd')?.textContent.trim()).map(d => d.querySelector('dt').textContent)`)
    if (named.length) failures.push(`facts with a name and no value: ${named.join(', ')}`)
    if (answer) for (const [name, value] of [['Other account', answer.account], ['DRM', answer.drm]]) {
      if (value && !parts.facts?.[name]) failures.push(`no ${name} fact, though Steam names "${value}"`)
      if (!value && parts.facts?.[name]) failures.push(`${name} reads "${parts.facts[name]}", though Steam names none`)
    }
    const hostHasMine = Boolean(answer?.specs?.some(s => s.mine))
    if (parts.reqs && !parts.specTable) failures.push('requirements section missing spec grid')
    if (parts.reqs && hostHasMine && !parts.thisPc) failures.push('requirements without this PC')
    await t.shell.key('Escape')
    if (!await t.shell.waitFor(() => !document.querySelector('.exo-sgp'), { timeoutMs: 3000 })) failures.push('Escape did not close the page')
    return failures
  })
}
