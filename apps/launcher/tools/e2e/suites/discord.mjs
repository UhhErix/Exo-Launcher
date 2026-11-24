// Exo's Discord client, used. Anything that sends or changes something happens only in the test place, the Exo
// server, and is undone: a test message in #mod-chat is reacted to and deleted, and a call in hangout is joined muted and
// deafened, then left.
import { sleep } from '../cdp.mjs'
import { geometryAudit } from '../lib/geometry.mjs'

// The test place, the Exo server: #mod-chat for messages (each deleted after), hangout for a call (muted and deafened).
const TEST_SERVER = 'Exo', TEST_CHANNEL = 'mod-chat', TEST_VOICE = 'hangout'
const chat = () => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') ?? null

async function openTestChannel(t) {
  await t.shell.key('k', { ctrl: true })
  await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 3000 })
  await t.shell.type(TEST_CHANNEL)
  const option = await t.shell.waitFor((n, s) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].some(o => { const x = o.querySelectorAll('span span'); return x[0]?.textContent.trim() === n && x[1]?.textContent.trim() === s }), { timeoutMs: 8000, args: [TEST_CHANNEL, TEST_SERVER] })
  if (!option) { await t.shell.key('Escape'); return false }
  await t.shell.click((n, s) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].find(o => { const x = o.querySelectorAll('span span'); return x[0]?.textContent.trim() === n && x[1]?.textContent.trim() === s }), TEST_CHANNEL, TEST_SERVER)
  return !!await t.shell.waitFor((n) => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') === n && document.querySelectorAll('.exo-dc-msg').length > 0, { timeoutMs: 10000, args: [TEST_CHANNEL] })
}

export async function run(t) {
  const { run, k } = t
  await k.nav('Discord')
  const ready = await t.shell.waitFor(() => document.querySelector('main.exo-dc') ? 'client' : document.querySelector('main.exo-dc-signin') ? 'signin' : null, { timeoutMs: 30000 })

  // The room's band (Band.tsx): Home, then your servers. Home and Messages are the Home pill (your conversations are its
  // left panel); Servers is the first server in the band.
  const place = (name) => t.shell.click((n) => n === 'Servers'
    ? document.querySelector('.exo-dc-band .exo-dc-band-place.is-server')
    : document.querySelector('.exo-dc-band .exo-dc-band-place'), name)

  await run.check('discord', 'signed-in', 'Discord opens signed in, with servers, DMs and you', async (c) => {
    if (ready !== 'client') c.skip(ready === 'signin' ? 'not signed in to Discord' : 'the room did not load')
    await place('Messages')
    const dms = await t.shell.waitFor(() => document.querySelectorAll('.exo-dc-item.is-dm').length || null, { timeoutMs: 3000 }) ?? 0
    await place('Servers')
    const servers = await t.shell.waitFor(() => document.querySelectorAll('.exo-dc-band .exo-dc-band-place.is-server').length || null, { timeoutMs: 3000 }) ?? 0
    const me = await t.shell.eval(`!!document.querySelector('.exo-dc-me-who')`)
    await place('Home')
    c.evidence.parts = { servers, dms }
    return [!servers && 'no servers', !me && 'no user panel'].filter(Boolean)
  })
  if (ready !== 'client') return

  // Geometry (lib/geometry.mjs): nothing drawn past the window or its box, no words cut without an ellipsis, no siblings
  // lying over each other, no absolutely positioned box over words, no rule past its panel. Looked at on each of the
  // room's places as it is; the menus and popovers that float over a page are not faults.
  await run.check('discord', 'geometry', 'the room has no overlapping boxes, lines past their panels or the window edge, or words cut off', async (c) => {
    const failures = []
    for (const [name, go] of [['Home', () => place('Home')], ['Messages', () => place('Messages')], ['Servers', () => place('Servers')]]) {
      await go()
      await sleep(600)
      await t.k.settle(300, 6000)
      const found = await t.shell.call(geometryAudit)
      c.evidence[`geometry.${name}`] = found.slice(0, 40)
      for (const f of found.slice(0, 10)) failures.push(`${name}: ${f.kind} ${f.path} ${JSON.stringify(f.rect)} ${f.detail}`)
      if (found.length > 10) failures.push(`${name}: ${found.length - 10} more`)
    }
    await place('Home')
    return failures
  })

  await run.check('discord', 'home-is-home', "Home is the band, your conversations on the left with you at their foot (mute, deafen, settings), and Friends (Online, All, Pending) on the panel color", async (c) => {
    // Home's pill was just pressed: its page is still coming in.
    await t.shell.waitFor(() => document.querySelector('main.exo-dc .exo-dc-home .exo-dc-friends') || null, { timeoutMs: 5000 })
    const home = await t.shell.eval(`({
      band: !!document.querySelector('.exo-dc-band .exo-dc-band-place.is-chosen'),
      rail: !!document.querySelector('.exo-dc-rail'),
      name: document.querySelector('main.exo-dc')?.getAttribute('aria-label'),
      blocks: [...document.querySelectorAll('.exo-dc-home .exo-dc-block-title')].map(h => h.textContent.trim()),
      dms: document.querySelectorAll('.exo-dc-pane .exo-dc-item.is-dm').length,
      me: ['Mute', 'Unmute', 'Deafen', 'Undeafen', 'Discord settings'].filter(l => document.querySelector('.exo-dc-pane .exo-dc-me [aria-label="' + l + '"]')).length,
      // The room's panels are the panel color (--exo-float: pure black): glass is only for what floats.
      panels: [...document.querySelectorAll('.exo-dc-band, .exo-dc-pane, .exo-dc-block')].map(e => getComputedStyle(e).backgroundColor),
      panel: (() => { const e = document.createElement('div'); e.style.background = 'var(--exo-float)'; document.body.append(e); const v = getComputedStyle(e).backgroundColor; e.remove(); return v })(),
    })`)
    c.evidence.home = home
    await c.shot('home', t.shell)
    const black = (c) => c === 'rgba(0, 0, 0, 0)' || c === home.panel
    return [!home.band && 'Home is not the chosen place in the band', home.rail && 'the rail is back', home.name !== 'Discord' && `the room is named ${home.name}`,
      home.blocks.join() !== 'Friends' && `Home shows ${home.blocks.join(', ')}`,
      !home.dms && 'no conversations on the left', home.me < 3 && 'mute, deafen or settings is missing by you',
      home.panels.some(p => !black(p)) && `a panel is ${home.panels.find(p => !black(p))}`].filter(Boolean)
  })

  await run.check('discord', 'band', 'the band: one chosen pill with its name, every face the same size and reachable, its name tip on hover, and the left panel ending in you with mute, deafen and settings', async (c) => {
    await place('Home')
    await sleep(500)
    const band = await t.shell.eval(`(() => {
      const strip = document.querySelector('.exo-dc-band-strip'), box = strip?.getBoundingClientRect()
      const places = [...document.querySelectorAll('.exo-dc-band-place')]
      const faces = places.map(p => p.querySelector('.exo-dc-band-face')?.getBoundingClientRect()).filter(Boolean)
      const chosen = places.filter(p => p.getAttribute('aria-current') === 'true')
      const pill = chosen[0], ps = pill && getComputedStyle(pill), name = pill?.querySelector('.exo-dc-band-name')
      const overflow = strip ? strip.scrollWidth > strip.clientWidth + 1 : false
      const next = document.querySelector('.exo-dc-band-step.is-next')
      return { chosen: chosen.length, pillName: name?.textContent.trim() ?? null, pillBg: ps?.backgroundColor, pillInk: ps?.color,
        sizes: [...new Set(faces.map(f => Math.round(f.width) + 'x' + Math.round(f.height)))], overflow, step: !!next && !next.hidden,
        rowTop: [...new Set(faces.map(f => Math.round(f.top + f.height / 2)))], inside: box ? faces.every(f => f.top >= box.top - 1 && f.bottom <= box.bottom + 1) : false }
    })()`)
    c.evidence.band = band
    const failures = []
    if (band.chosen !== 1) failures.push(`${band.chosen} places are chosen in the band`)
    if (band.pillName !== 'Home') failures.push(`the chosen pill reads ${band.pillName}`)
    if (band.pillBg === band.pillInk) failures.push("the chosen pill's words are its own colour")
    if (band.sizes.length > 1) failures.push(`the band's faces come in ${band.sizes.join(', ')}`)
    if (band.rowTop.length > 1) failures.push(`the band's faces sit on ${band.rowTop.length} lines (${band.rowTop.join(', ')})`)
    if (!band.inside) failures.push('a face is cut by the band')
    if (band.overflow && !band.step) failures.push('servers run past the band with no way to them')
    // A server's name tip, by a real hover.
    const face = await t.shell.eval(`(() => { const f = document.querySelector('.exo-dc-band-place.is-server:not(.is-chosen)'); if (!f) return null; const r = f.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, bottom: r.bottom, label: f.getAttribute('aria-label') } })()`)
    if (face) {
      await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: face.x, y: face.y })
      const tip = await t.shell.waitFor(() => document.querySelector('.exo-dc-tip.is-band')?.textContent.trim() || null, { timeoutMs: 3000 })
      c.evidence.tip = tip
      if (!tip) failures.push(`no name tip over ${face.label}`)
      else {
        const place = await t.shell.eval(`(() => { const r = document.querySelector('.exo-dc-tip.is-band').getBoundingClientRect(); return { inWindow: r.left >= 0 && r.right <= innerWidth && r.top >= 0, top: Math.round(r.top) } })()`)
        if (!place.inWindow) failures.push('the name tip runs past the window')
        // It hangs under the face, never over it.
        if (place.top < face.bottom) failures.push(`the name tip starts at ${place.top}px, over the face (its foot at ${Math.round(face.bottom)}px)`)
      }
      await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 800, y: 600 })
    }
    // You at the left panel's foot.
    const me = await t.shell.eval(`(() => { const pane = document.querySelector('.exo-dc-pane')?.getBoundingClientRect(), you = document.querySelector('.exo-dc-pane .exo-dc-me')?.getBoundingClientRect()
      return { foot: pane && you ? Math.round(pane.bottom - you.bottom) : null, buttons: [...document.querySelectorAll('.exo-dc-pane .exo-dc-me button')].map(b => b.getAttribute('aria-label')) } })()`)
    c.evidence.me = me
    for (const want of [/^(Mute|Unmute)$/, /^(Deafen|Undeafen)$/, /^Discord settings$/]) if (!me.buttons.some(b => want.test(b ?? ''))) failures.push(`no ${want.source} by you`)
    if (me.foot === null || me.foot > 4) failures.push(`you sit ${me.foot}px above the left panel's foot`)
    await c.shot('band', t.shell)
    return failures
  })

  await run.check('discord', 'chosen-rows', 'the row you are in reads clearly: its words dark on its white pill, in the conversations and in a server\'s channels', async (c) => {
    const read = () => t.shell.eval(`(() => { const r = document.querySelector('.exo-dc-pane .exo-dc-item[aria-current="true"]'); if (!r) return null
      const name = r.querySelector('.exo-dc-item-name') ?? r, s = getComputedStyle(r), n = getComputedStyle(name)
      return { bg: s.backgroundColor, ink: n.color, text: name.textContent.trim().slice(0, 30) } })()`)
    const lum = (c) => { const m = /(\d+),\s*(\d+),\s*(\d+)/.exec(c); return m ? (0.2126 * m[1] + 0.7152 * m[2] + 0.0722 * m[3]) / 255 : 0 }
    const failures = []
    await place('Home')
    await t.shell.click(() => document.querySelector('.exo-dc-pane .exo-dc-item.is-dm'))
    await sleep(800)
    const dm = await read()
    await place('Servers')
    await sleep(1500)
    const channel = await read()
    c.evidence.rows = { dm, channel }
    for (const [name, row] of [['conversation', dm], ['channel', channel]]) {
      if (!row) { failures.push(`no chosen ${name} row`); continue }
      if (Math.abs(lum(row.bg) - lum(row.ink)) < 0.5) failures.push(`the chosen ${name} "${row.text}" is ${row.ink} on ${row.bg}`)
    }
    await place('Home')
    return failures
  })

  await run.check('discord', 'lists-end', 'the conversation list and the band of servers reach their last item', async (c) => {
    const end = (sel) => t.shell.eval(`(async () => {
      const els = [...document.querySelectorAll(${JSON.stringify(sel)})]; const el = els[els.length - 1]
      if (!el) return 'none'
      el.scrollIntoView({ block: 'end', inline: 'end' }); await new Promise(r => setTimeout(r, 300))
      el.scrollIntoView({ block: 'end', inline: 'end' }); await new Promise(r => setTimeout(r, 300))
      const r = el.getBoundingClientRect(), top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
      return top === el || el.contains(top) ? 'clear' : 'under ' + ((top?.getAttribute?.('class') ?? top?.tagName?.toLowerCase() ?? '?').slice(0, 40))
    })()`)
    await place('Messages')
    await sleep(400)
    const dms = await end('.exo-dc-item.is-dm')
    const servers = await end('.exo-dc-band .exo-dc-band-place.is-server')
    c.evidence.ends = { dms, servers }
    return Object.entries(c.evidence.ends).filter(([, v]) => v !== 'clear' && v !== 'none').map(([n, v]) => `${n}'s last item is ${v}`)
  })

  await run.check('discord', 'dm-lands', "a conversation opens from Home's left panel as the chosen row, and opening it changes no other unread count", async (c) => {
    const unread = async () => (await t.host('discord.home', {}).catch(() => null))?.dms?.filter(d => d.unread || d.mentions > 0).map(d => `${d.id}:${d.mentions}`) ?? []
    await place('Home')
    await sleep(400)
    const before = await unread()
    const id = await t.shell.eval(`document.querySelector('.exo-dc-pane .exo-dc-item.is-dm:not(.is-unread)')?.dataset.dcChannel ?? null`)
    if (!id) c.skip('every conversation is unread')
    await t.shell.click((i) => document.querySelector('.exo-dc-pane .exo-dc-item.is-dm[data-dc-channel="' + i + '"]'), id)
    const opened = await t.shell.waitFor(() => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') || null, { timeoutMs: 5000 })
    const chosen = await t.shell.eval(`document.querySelector('.exo-dc-pane .exo-dc-item[aria-current="true"]')?.dataset.dcChannel ?? null`)
    await sleep(1500)
    const after = await unread()
    c.evidence.landing = { opened: !!opened, chosen: chosen === id, before, after }
    await c.shot('dm', t.shell)
    await place('Home')
    return [!opened && 'no conversation opened', chosen !== id && 'its row is not the chosen one', before.join() !== after.join() && 'opening it changed the unread counts'].filter(Boolean)
  })

  await run.check('discord', 'rail-voice', 'hovering a server with people in voice lists its voice channels and who is in them', async (c) => {
    // A server whose rail label says "N in voice" (the count its aria-label carries); none in voice skips.
    const target = await t.shell.eval(`(() => {
      const b = [...document.querySelectorAll('.exo-dc-band .exo-dc-band-place.is-server:not(.is-chosen)')].find(b => b.getClientRects().length && /\\d+ in voice/.test(b.getAttribute('aria-label') ?? ''))
      if (!b) return null
      b.scrollIntoView({ block: 'nearest' })
      const r = b.getBoundingClientRect()
      return { x: r.left + r.width / 2, y: r.top + r.height / 2, label: b.getAttribute('aria-label') }
    })()`)
    if (!target) c.skip('no server has anyone in voice')
    c.evidence.server = target.label
    await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: target.x, y: target.y })
    const people = await t.shell.waitFor(() => document.querySelectorAll('.exo-dc-tip.is-voice .exo-dc-tip-face').length || null, { timeoutMs: 8000 })
    const tip = await t.shell.eval(`(() => { const el = document.querySelector('.exo-dc-tip.is-voice'); if (!el) return null; const r = el.getBoundingClientRect(); return { people: el.querySelectorAll('.exo-dc-tip-face').length, rooms: el.querySelectorAll('.exo-dc-tip-room').length, more: el.querySelector('.exo-dc-tip-more-inline')?.textContent ?? null, inWindow: r.top >= -1 && r.bottom <= innerHeight + 1 } })()`)
    c.evidence.tip = tip
    await c.shot('rail-voice', t.shell)
    // The pointer moves on, so the tip goes.
    await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 600, y: 300 })
    const failures = []
    if (!people) failures.push('the tip listed nobody in voice')
    else {
      if (!tip?.rooms) failures.push('no voice channel in the tip')
      if (tip && !tip.inWindow) failures.push('the tip left the window')
    }
    return failures
  })

  await run.check('discord', 'channel', 'Ctrl+K opens the test channel with its messages', async (c) => {
    const t0 = Date.now()
    if (!await openTestChannel(t)) return [`could not open ${TEST_SERVER} #${TEST_CHANNEL}`]
    c.metric('open', Date.now() - t0, ' ms', 2500)
    await sleep(600)
    await c.shot('channel', t.shell)
    return k.look(c, 'channel')
  })

  await run.check('discord', 'conversation-reads', 'in a server one thing in the band is lit, words stop at a reading width, a time is a clock and short conversations sit on the box', async (c) => {
    const read = await t.shell.eval(`(() => {
      const scroller = document.querySelector('.exo-dc-scroll'), box = document.querySelector('.exo-dc-compose')
      const last = scroller?.lastElementChild?.getBoundingClientRect(), words = [...document.querySelectorAll('.exo-dc-text p')].map(p => p.getBoundingClientRect().width)
      const ring = (() => { const b = document.querySelector('.exo-dc-channels .exo-glide'); return b ? b.style.getPropertyValue('--glide-w') : null })()
      return {
        tabLit: document.querySelectorAll('.exo-dc-band [aria-current="true"]').length > 1,
        chipLit: !!document.querySelector('.exo-dc-band .exo-dc-band-place.is-server[aria-current="true"]'),
        widest: Math.max(0, ...words),
        dated: [...document.querySelectorAll('.exo-dc-msg-time')].filter(t => t.textContent.includes('/')).length,
        gap: scroller && last ? scroller.getBoundingClientRect().bottom - last.bottom : null,
        boxWidth: box?.getBoundingClientRect().width ?? 0, ring,
        // A card's outline runs all the way round, whatever its accent: one color and width on every side.
        cutCards: [...document.querySelectorAll('.exo-dc-embed, .exo-dc-cv-container')].filter(e => { const c = getComputedStyle(e); return new Set(['Top', 'Right', 'Bottom', 'Left'].map(k => c['border' + k + 'Width'] + c['border' + k + 'Color'])).size > 1 }).length,
      }
    })()`)
    c.evidence.read = read
    return [read.tabLit && 'a place is lit as well as the server', !read.chipLit && "the server's chip isn't lit", read.widest > 1040 && `words run ${read.widest}px wide`,
      read.dated && `${read.dated} times carry a date`, read.cutCards && `${read.cutCards} cards have an uneven outline`, read.gap !== null && read.gap > 60 && `${read.gap}px between the last message and the box`].filter(Boolean)
  })

  await run.check('discord', 'server-frame', 'a server is channels, then the conversation, then people, all ending on one line; Pinned and notifications in the three dots, and the search on the title line', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    await sleep(600)
    const read = await t.shell.eval(`(() => {
      const box = (s) => { const e = document.querySelector(s); if (!e || !e.getClientRects().length) return null; const b = e.getBoundingClientRect(); return { left: b.left, right: b.right, bottom: b.bottom } }
      return {
        fit: document.querySelector('.exo-dc-view')?.dataset.columns,
        chat: box('.exo-dc-view > :not(.exo-dc-beside)'), channels: box('.exo-dc-body > .exo-dc-pane'), people: box('.exo-dc-beside'), search: box('.exo-dc-chat-head .exo-dc-head-search-glyph, .exo-dc-chat-head .exo-dc-search'),
        toggle: !!document.querySelector('main.exo-dc button[aria-label="Show member list"], main.exo-dc button[aria-label="Hide member list"]'),
        mic: !!document.querySelector('.exo-dc-input-row button[aria-label="Record a voice message"]'),
        headTools: [...document.querySelectorAll('.exo-dc-chat-head .exo-dc-head-tools > button, .exo-dc-chat-head .exo-dc-head-tools > span > button')].map(b => b.getAttribute('aria-label')),
        lit: document.querySelector('.exo-dc-channels .exo-glide')?.style.getPropertyValue('--glide-w') ?? null,
      }
    })()`)
    c.evidence.frame = read
    const near = (a, b) => Math.abs(a - b) < 1
    const fails = [read.toggle && 'the people toggle is back', read.mic && 'the message box has a mic', read.headTools.some(l => /Pinned|Notifications/.test(l ?? '')) && 'Pinned or the bell is on the title line']
    if (read.fit === 'both') {
      if (!read.chat || !read.channels || !read.people) fails.push('a column is missing')
      else {
        fails.push(read.channels.right + 8 > read.chat.left && 'the channels touch the chat', read.chat.right + 8 > read.people.left && 'the chat touches the people')
        fails.push(!read.search && 'no search on the title line')
        for (const [name, y] of [['channels', read.channels.bottom], ['people', read.people.bottom]]) fails.push(!near(y, read.chat.bottom) && `the ${name} end at ${y}, the conversation at ${read.chat.bottom}`)
      }
    }
    await t.shell.click(() => document.querySelector('main.exo-dc button[aria-label="Inbox"]'))
    await sleep(300)
    const inbox = await t.shell.eval(`document.querySelector('.is-inbox [role="tab"][aria-selected="true"]')?.textContent.trim() ?? null`)
    if (inbox !== 'For you') fails.push(`the Inbox opened on ${inbox}`)
    await k.closeAll()
    return fails.filter(Boolean)
  })

  await run.check('discord', 'people-column', 'a server\'s people column fills on a voice channel or a server page too, not only beside a chat', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    if (await t.shell.eval(`document.querySelector('.exo-dc-view')?.dataset.columns`) !== 'both') c.skip('the window has no room for the column')
    const failures = []
    for (const name of ['Members', TEST_VOICE]) {
      if (!await t.shell.click((n) => [...document.querySelectorAll('.exo-dc-channels button')].find(b => b.textContent.trim().startsWith(n)), name)) continue
      if (!await t.shell.waitFor(() => !!document.querySelector('.exo-dc-members [data-dc-user]'), { timeoutMs: 12000 })) failures.push(`no people beside ${name}`)
    }
    if (!await openTestChannel(t)) failures.push('could not go back to the test channel')
    return failures
  })

  // A header holds one of each control. Two siblings with one React key (a server's first channel can share the server's
  // id) once kept every old bell, dozens across the header; switching away and back, with messages flowing, shows it.
  await run.check('discord', 'header-once', 'the channel header holds one of each control after switching channels', async () => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) return ['not in the test channel']
    const dupes = await t.shell.eval(`(async () => {
      const w = ms => new Promise(r => setTimeout(r, ms))
      // Channels, not the server's own rows above them (Events opens a dialog, which stayed over every check after this).
      const rows = () => [...document.querySelectorAll('main.exo-dc .exo-dc-channels .exo-dc-item[data-dc-channel]')]
      for (const at of [0, 1, 2, 3]) {
        await w(100)
        rows()[at]?.click()
        await w(400)
      }
      const labels = [...document.querySelectorAll('.exo-dc-chat-head .exo-dc-head-tools [aria-label]')].map(e => e.getAttribute('aria-label'))
      return labels.filter((l, i) => labels.indexOf(l) !== i)
    })()`)
    return dupes.length ? [`repeated in the header: ${[...new Set(dupes)].join(', ')}`] : []
  })

  /** Deletes your test message through its right-click menu, its row asking in place; true once it is gone. Tried up to three times. */
  async function deleteMessage(text) {
    for (let attempt = 0; attempt < 3; attempt++) {
      await k.closeAll()
      const hit = await t.shell.call((x) => { const m = [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x)); if (!m) return 'gone'; m.scrollIntoView({ block: 'center' }); const r = m.getBoundingClientRect(); m.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: r.left + 200, clientY: r.top + 20, button: 2 })); return 'menu' }, text)
      if (hit === 'gone') return true
      await sleep(600)
      await t.shell.click(() => [...document.querySelectorAll('[role^="menuitem"], button')].find(b => b.textContent.trim() === 'Delete message'))
      await sleep(600)
      // The row asks in place (DESIGN.md › Confirm): pressed again, it deletes.
      await t.shell.click(() => [...document.querySelectorAll('[role^="menuitem"]')].find(b => b.textContent.trim() === 'Delete this message?'))
      if (await t.shell.waitFor((x) => ![...document.querySelectorAll('.exo-dc-msg')].some(e => e.textContent.includes(x)), { timeoutMs: 6000, args: [text] })) return true
    }
    return false
  }

  await run.check('discord', 'leftover', 'no test message left in the test channel from an earlier run', async (c) => {
    if (t.noPost) c.skip('--no-post')
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    // A run that stopped between sending and deleting leaves "Exo e2e hh:mm:ss" behind: each one found is deleted now.
    const left = await t.shell.eval(`[...document.querySelectorAll('.exo-dc-msg')].map(m => /Exo e2e \\d\\d:\\d\\d:\\d\\d/.exec(m.textContent)?.[0]).filter(Boolean)`)
    c.evidence.left = left
    const failures = []
    for (const text of [...new Set(left)]) if (!await deleteMessage(text)) failures.push(`"${text}" could not be deleted: delete it by hand`)
    return failures
  })

  await run.check('discord', 'message', 'a message is sent, reacted to, and deleted (test channel only)', async (c) => {
    if (t.noPost) c.skip('--no-post')
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    const text = `Exo e2e ${new Date().toISOString().slice(11, 19)}`
    const failures = []
    let sent = false
    try {
      await t.shell.click(() => document.querySelector('main.exo-dc [contenteditable="true"], main.exo-dc textarea'))
      await t.shell.type(text)
      await t.shell.key('Enter')
      sent = !!await t.shell.waitFor((x) => [...document.querySelectorAll('.exo-dc-msg')].slice(-3).some(m => m.textContent.includes(x)), { timeoutMs: 10000, args: [text] })
      if (!sent) return ['the message did not appear']
      // Hover it until Discord's bar shows, Add Reaction, the first emoji, then the reaction taken back.
      let bar = false
      for (let i = 0; i < 4 && !bar; i++) {
        const at = await t.shell.call((x, n) => { const m = [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x)); if (!m) return null; m.scrollIntoView({ block: 'center' }); const r = m.getBoundingClientRect(); return { x: r.left + r.width / 2 + n * 7, y: r.top + r.height / 2 } }, text, i)
        if (!at) break
        await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y })
        bar = !!await t.shell.waitFor((x) => { const b = [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('button[aria-label="Add reaction"]'); return b && b.getBoundingClientRect().width > 0 }, { timeoutMs: 1500, args: [text] })
      }
      if (!bar) failures.push('the hover bar did not show')
      else if (await t.shell.click((x) => [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('button[aria-label="Add reaction"]') ?? null, text)) {
        await t.shell.waitFor(() => document.querySelector('.exo-dc-picker .exo-dc-picker-emoji'), { timeoutMs: 4000 })
        // The picker rises in (Motion › Arrive): a click mid-rise lands where an emoji is about to be, not where it is.
        await sleep(500)
        await t.shell.click(() => document.querySelector('.exo-dc-picker .exo-dc-picker-emoji:not(.is-locked)'))
        const reacted = await t.shell.waitFor((x) => !![...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('.exo-dc-reaction'), { timeoutMs: 6000, args: [text] })
        if (!reacted) {
          await c.shot('reaction-missing', t.shell)
          c.evidence.picker = await t.shell.eval(`(() => { const p = document.querySelector('.exo-dc-picker'); const e = p?.querySelector('.exo-dc-picker-emoji:not(.is-locked)'); const r = e?.getBoundingClientRect(); const top = r ? document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2) : null; return { open: !!p, emoji: !!e, rect: r && { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width) }, onTop: top ? (top === e || e.contains(top)) : null, topIs: top?.className?.toString().slice(0, 60) ?? null } })()`)
          failures.push('the reaction did not show')
        }
        else {
          await t.shell.click((x) => [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('.exo-dc-reaction') ?? null, text)
          if (!await t.shell.waitFor((x) => ![...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('.exo-dc-reaction'), { timeoutMs: 6000, args: [text] })) failures.push('the reaction could not be taken back')
        }
      }
    } finally {
      // Whatever happened above, the test message goes.
      if (sent && !await deleteMessage(text)) failures.push('THE TEST MESSAGE COULD NOT BE DELETED: delete it by hand')
    }
    return failures
  })

  await run.check('discord', 'invites','an invite link joins in the room; a dead one shows why', async (c) => {
    if (t.noPost) c.skip('--no-post')
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    const home = await t.host('discord.home').catch(() => null)
    const guild = home?.guilds?.find(g => g.name === TEST_SERVER)
    const list = guild ? await t.host('discord.channels', { guildId: guild.id }).catch(() => null) : null
    const channelId = list?.channels?.find(ch => ch.name === TEST_CHANNEL)?.id ?? null
    const invite = channelId ? await t.host('discord.channel', { channelId, action: 'invite' }).catch(() => null) : null
    const failures = []
    if (invite?.url) {
      await place('Home')
      await sleep(800)
      await t.shell.eval(`window.dispatchEvent(new CustomEvent('exo-dc-invite', { detail: ${JSON.stringify(invite.url.split('/').pop())} }))`)
      if (!await t.shell.waitFor((n) => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') === n, { timeoutMs: 8000, args: [TEST_CHANNEL] })) failures.push('the invite did not open its channel')
    } else c.warn('no channel id to make an invite (the live invite step was left out)')
    await t.shell.eval(`window.dispatchEvent(new CustomEvent('exo-dc-invite', { detail: 'exoE2eNoSuchInvite' }))`)
    const why = await t.shell.waitFor(() => document.querySelector('.exo-dc-modal')?.innerText.match(/Discord: .+/)?.[0] ?? null, { timeoutMs: 8000 })
    c.evidence.dead = why
    if (!why) failures.push('a dead invite showed no reason')
    await k.closeAll()
    return failures
  })

  await run.check('discord', 'server-pages', 'the test server\'s Guide, Events, Browse Channels and Members open', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test server')
    const failures = []
    for (const page of ['Server Guide', 'Events', 'Browse channels', 'Members']) {
      await t.shell.waitFor(() => !!document.querySelector('nav.exo-dc-channels')?.checkVisibility(), { timeoutMs: 3000 })
      if (!await t.shell.click((p) => [...document.querySelectorAll('.exo-dc-channels button, .exo-dc-channels a')].find(b => b.textContent.trim() === p), page)) { failures.push(`no ${page}`); continue }
      await sleep(1200)
      const shown = await t.shell.eval(`!!document.querySelector('main.exo-dc .exo-dc-page, main.exo-dc [class*="guide"], main.exo-dc [class*="events"], main.exo-dc [class*="browse"], main.exo-dc [class*="members"]')`)
      if (!shown) failures.push(`${page} showed nothing`)
    }
    await openTestChannel(t)
    return failures
  })

  // hangout is public: Exo joins it muted and deafened, so nothing is said into the call or heard from it, and leaves.
  await run.check('discord', 'voice', 'the test voice channel joins muted and deafened, shows Voice connected, and Disconnect leaves it', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test server')
    const dock = (label) => document.querySelector(`.exo-dc-me button[aria-label="${label}"]`)
    const was = await t.shell.eval(`({ muted: !!document.querySelector('.exo-dc-me button[aria-label="Unmute"]'), deafened: !!document.querySelector('.exo-dc-me button[aria-label="Undeafen"]') })`)
    c.evidence.was = was
    if (!was.deafened) await t.shell.click(dock, 'Deafen')
    if (!await t.shell.waitFor(() => !!document.querySelector('.exo-dc-me button[aria-label="Undeafen"]'), { timeoutMs: 3000 })) c.skip('could not deafen first, so the call was not joined')
    if (!await t.shell.eval(`!!document.querySelector('.exo-dc-me button[aria-label="Unmute"]')`)) await t.shell.click(dock, 'Mute')
    if (!await t.shell.waitFor(() => !!document.querySelector('.exo-dc-me button[aria-label="Unmute"]'), { timeoutMs: 3000 })) c.skip('could not mute first, so the call was not joined')
    const failures = []
    await t.shell.waitFor(() => !!document.querySelector('nav.exo-dc-channels')?.checkVisibility(), { timeoutMs: 3000 })
    if (!await t.shell.click((n) => [...document.querySelectorAll('.exo-dc-voice-channel button.exo-dc-item')].find(b => b.querySelector('.exo-dc-item-name')?.textContent.trim() === n), TEST_VOICE)) {
      failures.push(`no ${TEST_VOICE} voice channel`)
    } else {
      await sleep(900)
      await t.shell.click(() => document.querySelector('.exo-dc-voice-join'))
      const connected = await t.shell.waitFor(() => document.querySelector('.exo-dc-voice-status strong.is-on')?.textContent.includes('Voice connected') || null, { timeoutMs: 20000 })
      await c.shot('voice', t.shell)
      if (!connected) failures.push(`did not connect to ${TEST_VOICE}: ${await t.shell.eval(`document.querySelector('.exo-dc-voice-status')?.innerText ?? 'no voice panel'`)}`)
      else {
        const state = await t.shell.eval(`({ place: document.querySelector('.exo-dc-voice-place')?.textContent ?? '', muted: !!document.querySelector('.exo-dc-me button[aria-label="Unmute"]'), deafened: !!document.querySelector('.exo-dc-me button[aria-label="Undeafen"]') })`)
        c.evidence.connected = state
        if (!state.place.includes(TEST_VOICE)) failures.push(`the voice panel names ${state.place}, not ${TEST_VOICE}`)
        if (!state.muted || !state.deafened) failures.push('joining the call undid mute or deafen')
        // The room's head says who is here and that you are connected; the call's controls say what they do when there is room.
        const room = await t.shell.eval(`(() => {
          const head = document.querySelector('.exo-dc-voice-room .exo-dc-chat-head')
          const bar = document.querySelector('.exo-dc-voice-bar')
          const words = [...(bar?.querySelectorAll('.exo-dc-voice-word') ?? [])].filter(w => w.checkVisibility()).map(w => w.textContent.trim())
          const wide = (document.querySelector('.exo-dc-voice-room')?.getBoundingClientRect().width ?? 0) >= 760
          return { here: head?.querySelector('.exo-dc-voice-here')?.textContent.trim() ?? null, state: head?.querySelector('.exo-dc-voice-state')?.textContent.trim() ?? null, on: !!head?.querySelector('.exo-dc-voice-state.is-on'), words, wide,
            heights: [...(bar?.querySelectorAll('.exo-dc-voice-btn') ?? [])].map(b => Math.round(b.getBoundingClientRect().height)) }
        })()`)
        c.evidence.room = room
        if (!/\d+ here/.test(room.here ?? '')) failures.push(`the voice head says "${room.here}" for who is here`)
        if (!room.on) failures.push(`the voice head's state reads "${room.state}", not Voice connected`)
        if (room.wide && room.words.length < 4) failures.push(`a wide call's controls carry ${room.words.length} words`)
        if (new Set(room.heights).size > 1) failures.push(`the call's controls are ${[...new Set(room.heights)].join(' and ')}px tall`)
      }
    }
    // Always out of the call, and mute and deafen as they were.
    await t.shell.click(() => document.querySelector('.exo-dc-voice button[aria-label="Disconnect"]'))
    const left = await t.shell.waitFor(() => !document.querySelector('.exo-dc-voice') || null, { timeoutMs: 10000 })
    if (!left) failures.push('Disconnect did not leave the call')
    // Undeafen first: while deafened the mic stays muted. Each press is waited out before the next.
    for (const [key, on, off] of [['deafened', 'Undeafen', 'Deafen'], ['muted', 'Unmute', 'Mute']]) {
      if (was[key] || !await t.shell.eval(`!!document.querySelector('.exo-dc-me button[aria-label="${on}"]')`)) continue
      await t.shell.click(dock, on)
      if (!await t.shell.waitFor((l) => !!document.querySelector(`.exo-dc-me button[aria-label="${l}"]`), { timeoutMs: 3000, args: [off] })) failures.push(`${key} was not put back`)
    }
    await openTestChannel(t)
    return failures
  })

  await run.check('discord', 'settings', "Discord's settings are a sheet over the room: from your menu and your gear, each page under its title, Escape closes it, and Exo's Settings no longer lists Discord", async (c) => {
    await k.closeAll()
    await t.shell.click(() => document.querySelector('.exo-dc-me-who'))
    // Your menu opens a moment after the press.
    await t.shell.waitFor(() => document.querySelector('.exo-dc-me [role="menuitem"]') || null, { timeoutMs: 2000 })
    const clicked = await t.shell.click(() => [...document.querySelectorAll('.exo-dc-me [role="menuitem"]')].find(b => b.textContent.trim() === 'Edit profile'))
    if (!await t.shell.waitFor(() => !!document.querySelector('main.exo-dc [role="dialog"][aria-label="Discord settings"] .exo-dc-settings-page')?.getClientRects().length, { timeoutMs: 6000 })) {
      await c.shot('not-open', t.shell)
      c.evidence.why = await t.shell.eval(`({ dialogs: [...document.querySelectorAll('[role=dialog]')].map(d => d.getAttribute('aria-label')), active: document.activeElement?.className?.toString().slice(0, 40) })`)
      return [`the settings did not open (clicked: ${clicked}; ${JSON.stringify(c.evidence.why)})`]
    }
    const failures = []
    // Each of Discord's pages, from the sheet's own list, each under its own title.
    for (const page of ['Voice and video', 'Chat', 'Notifications', 'Privacy', 'Account', 'Keybinds']) {
      if (!await t.shell.click((p) => [...document.querySelectorAll('.exo-dc-sheet-nav button')].find(b => b.textContent.trim() === p), page)) { failures.push(`no ${page} page`); continue }
      await sleep(500)
      const shown = await t.shell.eval(`({ page: !!document.querySelector('.exo-dc-settings-page')?.getClientRects().length, title: document.querySelector('[aria-label="Discord settings"] .exo-settings-page-title')?.textContent.trim() ?? null })`)
      if (!shown.page) failures.push(`${page} showed nothing`)
      if (shown.title !== page) failures.push(`${page} is titled ${shown.title}`)
    }
    await c.shot('settings', t.shell)
    failures.push(...await k.look(c, 'settings'))
    await t.shell.key('Escape')
    if (!await t.shell.waitFor(() => !document.querySelector('.exo-dc-settings-page')?.getClientRects().length, { timeoutMs: 3000 })) failures.push('Escape did not close the settings')
    // The gear by you opens the same sheet, on Account.
    await t.shell.click(() => document.querySelector('.exo-dc-me button[aria-label="Discord settings"]'))
    const gear = await t.shell.waitFor(() => document.querySelector('[aria-label="Discord settings"] .exo-settings-page-title')?.textContent.trim() || null, { timeoutMs: 4000 })
    if (gear !== 'Account') failures.push(`the gear opened ${gear ?? 'nothing'}`)
    await t.shell.key('Escape')
    await sleep(400)
    // Exo's own Settings has no Discord page any more.
    await t.shell.click(() => document.querySelector('.exo-titlebar button[aria-label="Settings"], .exo-settings-btn'))
    await t.shell.waitFor(() => !!document.querySelector('.exo-settings-nav')?.getClientRects().length, { timeoutMs: 4000 })
    const listed = await t.shell.eval(`[...document.querySelectorAll('.exo-settings-nav > ul > li > button')].map(b => b.textContent.trim())`)
    c.evidence.settingsPages = listed
    if (listed.includes('Discord')) failures.push("Exo's Settings still lists Discord")
    await k.closeAll()
    return failures
  })

  await run.check('discord', 'profile', 'a member\'s profile opens from the member list and closes', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    await t.shell.waitFor(() => !!document.querySelector('.exo-dc-members [data-dc-user]'), { timeoutMs: 3000 })
    const opened = await t.shell.click(() => document.querySelector('.exo-dc-members [data-dc-user], .exo-dc-members button'))
    if (!opened) c.skip('no member list')
    const card = await t.shell.waitFor(() => !!document.querySelector('.exo-dc-profile')?.checkVisibility(), { timeoutMs: 3000 })
    await c.shot('profile', t.shell)
    await t.shell.key('Escape')
    const closed = await t.shell.waitFor(() => !document.querySelector('.exo-dc-profile')?.checkVisibility(), { timeoutMs: 2000 })
    return [!card && 'no profile card', !closed && 'Escape did not close it'].filter(Boolean)
  })
}
