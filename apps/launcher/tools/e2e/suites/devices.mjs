// Devices: the grid of what you plug in (each card with its picture, only devices Exo has a page for), the line about the processor's own USB and Find them, a card growing into its page and folding back, your
// audio's page and a DualSense's Controller Lab on one screen; and Exo Mouse's page loads in its frame, finds the mouse
// (or says there is none), and wears Exo's Appearance.
import { literal, openFrame, sleep } from '../cdp.mjs'

/** Every value the view transition's kind took while `act` ran in the page. */
const transitionsDuring = (t, act) => t.shell.eval(`(async () => {
  const seen = []
  const watch = new MutationObserver(() => seen.push(document.documentElement.dataset.exoVt || '-'))
  watch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-vt'] })
  ${act}
  await new Promise(resolve => setTimeout(resolve, 1500))
  watch.disconnect()
  return seen
})()`)

export async function run(t) {
  const { run, k } = t
  await k.nav('Devices')

  const grid = await t.shell.waitFor(() => document.querySelectorAll('[data-device-card]').length || null, { timeoutMs: 15000 })
  await run.check('devices', 'grid', "each device Exo has a page for is a card with its picture that opens it; one with no page (a keyboard, a controller the lab can't read) has no card", async (c) => {
    const listed = await t.host('devices.list')
    const detected = Array.isArray(listed?.devices) ? listed.devices : []
    const expected = new Set([
      ...(detected.some(d => d.kind === 'mouse') ? ['mouse'] : []),
      ...(detected.some(d => d.kind === 'audio') ? ['audio'] : []),
      ...detected.filter(d => (d.kind === 'controller' || d.kind === 'keyboard') && d.lab).map(d => d.id),
    ])
    const hidden = detected.filter(d => (d.kind === 'keyboard' || d.kind === 'controller') && !d.lab)
    if (!grid && expected.size === 0) c.skip('no devices found on this PC')
    await t.shell.waitFor(() => document.querySelectorAll('[data-device-card] .exo-device-card-art > *').length === document.querySelectorAll('[data-device-card]').length || null, { timeoutMs: 15000 })
    // A picture from the catalog loads (or fails) before it is judged; one that fails is a broken picture, not drawn art.
    await t.shell.waitFor(() => [...document.querySelectorAll('[data-device-card] img.exo-device-render')].every(i => i.complete) || null, { timeoutMs: 15000 })
    const cards = await t.shell.eval(`[...document.querySelectorAll('[data-device-card]')].map(card => ({ id: card.dataset.deviceCard, button: card.tagName === 'BUTTON', picture: !!card.querySelector('.exo-device-card-art > *'), render: (img => img ? (img.naturalWidth > 0 ? img.naturalWidth + 'x' + img.naturalHeight : 'broken') : 'drawn')(card.querySelector('.exo-device-card-art img.exo-device-render')), name: card.querySelector('strong')?.textContent ?? '' }))`)
    const head = await t.shell.eval(`document.querySelector('.exo-devices-head')?.innerText ?? ''`)
    c.evidence.cards = cards.map(({ id, picture, render }) => ({ id, picture, render }))
    c.evidence.detected = detected.map(({ id, kind, lab }) => ({ id, kind, lab }))
    await c.shot('grid', t.shell)
    const failures = []
    const renderedIds = new Set(cards.map(card => card.id))
    for (const id of expected) if (!renderedIds.has(id)) failures.push(`expected ${id} card is missing`)
    for (const d of hidden) if (renderedIds.has(d.id)) failures.push(`${d.name || d.id} (${d.kind}) has a card that opens nothing`)
    for (const card of cards) {
      if (!card.picture) failures.push(`${card.name} has no picture`)
      if (card.render === 'broken') failures.push(`${card.name}'s picture did not load`)
      if (!card.button) failures.push(`${card.name} has a card that opens nothing`)
    }
    if (/found/i.test(head)) failures.push('a line under the heading counts the devices')
    return failures
  })

  // DESIGN.md › Devices: the picture flies from the card to its page's picture and back; a page with no picture of its own
  // (Exo Mouse's) fades.
  await run.check('devices', 'morph', "a card's picture flies into its page and Devices flies it back, every card the same way (Exo Mouse's page fades)", async (c) => {
    const open = await t.shell.eval(`[...document.querySelectorAll('button[data-device-card]')].map(card => card.dataset.deviceCard)`)
    if (!open.length) c.skip('no device with a page')
    const failures = []
    const reduced = await t.shell.eval(`document.documentElement.dataset.exoMotion === 'reduce'`)
    const card = (id) => `document.querySelector('[data-device-card="' + CSS.escape(${literal(id)}) + '"]')`
    for (const id of open) {
      const opening = await transitionsDuring(t, `${card(id)}?.click()`)
      const page = await t.shell.eval(`({ back: !!document.querySelector('.exo-devices-back button'), art: !!document.querySelector('.exo-device-hero-art')?.checkVisibility() })`)
      if (!page.back) { failures.push(`${id} opened no page with Devices at its top`); break }
      const folding = await transitionsDuring(t, `document.querySelector('.exo-devices-back button')?.click()`)
      c.evidence[id] = { opening, folding, art: page.art }
      const want = page.art ? 'morph' : 'fade'
      if (!reduced && opening[0] !== 'morph' && opening[0] !== want) failures.push(`${id} opened without growing from its card (${opening.join(',') || 'nothing'})`)
      if (!reduced && folding[0] !== want) failures.push(`${id} closed with ${folding.join(',') || 'nothing'}, not a ${want}`)
      if (!await t.shell.eval(`!!${card(id)}`)) failures.push(`${id}'s card did not come back`)
    }
    return failures
  })

  await run.check('devices', 'audio', 'your audio page fits one screen beside its hero, credits AutoEq and Equalizer APO, and its headphone search lists under its row', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('button[data-device-card="audio"]')`)) c.skip('no audio device')
    await t.shell.click(() => document.querySelector('button[data-device-card="audio"]'))
    await t.shell.waitFor(() => document.querySelector('.exo-device-page-hero') || null, { timeoutMs: 8000 })
    await sleep(1200)
    const read = () => t.shell.eval(`(() => { const page = document.querySelector('.exo-devices-page'), panels = document.querySelector('.exo-device-page-panels'); return { page: page.scrollHeight - page.clientHeight, panels: panels ? panels.scrollHeight - panels.clientHeight : null, credit: document.querySelector('.exo-device-page-credit')?.textContent.trim() ?? '' } })()`)
    const shown = await read()
    c.evidence.shown = shown
    await c.shot('audio', t.shell)
    const failures = []
    if (shown.page > 0 || shown.panels > 0) failures.push(`the page scrolls (${shown.page}px, its panels ${shown.panels}px)`)
    if (shown.credit !== 'Corrections by AutoEq, sound by Equalizer APO') failures.push(`the hero's foot says "${shown.credit}", not Exo EQ's credit`)
    if (!await t.shell.eval(`!!document.querySelector('.exo-device-page-credit .exo-glyph')`)) failures.push('no heart beside the credit')
    if (await t.shell.eval(`!!document.querySelector('.exo-device-page-panel[aria-label="Lucid"]')`)) failures.push('a Lucid panel is still on the page')
    // Read only: a run never changes a real device's format, since its sound would restart.
    const audio = (await t.host('devices.list')).devices.filter(d => d.kind === 'audio')
    for (const device of audio) {
      const offered = await t.host('audio.formats', { id: device.id })
      c.evidence[`formats-${device.role}`] = offered.formats?.map(f => f.key)
      if (offered.ok && offered.current && !offered.formats.some(f => f.key === offered.current)) failures.push(`the ${device.role}'s current format isn't among those offered`)
      if (offered.ok && !offered.current) failures.push(`Exo couldn't read the ${device.role}'s format`)
    }
    if (audio.length) {
      const rows = await t.shell.eval(`[...document.querySelectorAll('.exo-device-page-panel[aria-label="Output"] .exo-set-item-label')].map(l => l.textContent)`)
      c.evidence.outputRows = rows
      for (const row of ['Format', 'Volume', 'Mute']) if (audio.some(d => d.role === 'output') && !rows.includes(row)) failures.push(`no ${row} row in Output`)
      if (audio.some(d => d.role === 'output') && !await t.shell.eval(`!!document.querySelector('.exo-device-page-panel[aria-label="Output"] input[type=range]')`)) failures.push('Volume in Output is no slider')
    }
    if (await t.shell.click(() => [...document.querySelectorAll('.exo-device-page-panel button')].find(b => /^(Change|Pick)$/.test(b.textContent.trim())))) {
      await sleep(1200)
      const picking = await read()
      c.evidence.picking = picking
      if (picking.page > 0 || picking.panels > 0) failures.push(`the open headphone search pushes the page into a scroll (${picking.panels}px)`)
      await t.shell.key('Escape')
    }
    await t.shell.click(() => document.querySelector('.exo-devices-back button'))
    await sleep(800)
    return failures
  })

  await run.check('devices', 'ports', "each card on USB says which USB it's on, green where it's best, and a mouse, keyboard or controller with a free processor port says to move", async (c) => {
    const list = await t.host('devices.list')
    const ports = { cpu: list.cpuPorts ?? null, devices: list.devices.map(d => ({ id: d.id, kind: d.kind, name: d.name, port: d.port ?? null })) }
    c.evidence.ports = ports
    if (!ports.devices.some(d => d.port)) c.skip('no device on USB')
    const tags = await t.shell.eval(`Object.fromEntries([...document.querySelectorAll('[data-device-card]')].map(c => [c.dataset.deviceCard, c.querySelector('.exo-device-port') ? { text: c.querySelector('.exo-device-port').textContent, good: c.querySelector('.exo-device-port').classList.contains('is-good') } : null]))`)
    c.evidence.tags = tags
    await c.shot('ports', t.shell)
    const failures = []
    if (await t.shell.eval(`[...document.querySelectorAll('.exo-devices-head button')].some(b => /find/i.test(b.textContent))`)) failures.push('a Find ports button is back: the cards say where they are')
    for (const d of ports.devices.filter(d => d.port && ['mouse', 'keyboard', 'controller'].includes(d.kind) && d.port.better === 'free')) {
      const tag = Object.entries(tags).find(([id]) => id === d.id || id.includes(d.id))?.[1]
      if (tag && (tag.good || tag.text !== 'Move to CPU USB')) failures.push(`${d.name} has a free processor port and its card says "${tag.text}"`)
    }
    return failures
  })

  await run.check('devices', 'controller', 'a DualSense opens its Controller Lab on one screen: firmware in the hero, its sticks and triggers live, a measured rate and its USB port', async (c) => {
    const pad = (await t.host('devices.list')).devices.find(d => d.kind === 'controller' && /^dualsense/.test(d.model ?? ''))
    if (!pad) c.skip('no DualSense on this PC')
    const id = pad.id
    await t.shell.click((card) => document.querySelector(`button[data-device-card="${CSS.escape(card)}"]`), id)
    await t.shell.waitFor(() => document.querySelector('.exo-device-page-hero') || null, { timeoutMs: 8000 })
    const failures = []
    const live = await t.shell.waitFor(() => document.querySelector('.exo-trigger[aria-valuenow]') && document.querySelector('.exo-stick-dot') ? true : null, { timeoutMs: 8000 })
    const stats = await t.shell.waitFor(() => { const dts = [...document.querySelectorAll('.exo-device-page-stats dt')].map(d => d.textContent); return dts.includes('Firmware') ? dts : null }, { timeoutMs: 15000 })
    c.evidence.stats = stats
    if (!live) failures.push('the sticks and triggers never drew')
    if (!stats) failures.push('no firmware in the hero (a DualSense on a cable reports it)')
    if (await t.shell.click(() => [...document.querySelectorAll('.exo-device-page-panel button')].find(b => b.textContent.trim() === 'Measure'))) {
      const rate = await t.shell.waitFor(() => { const text = document.querySelector('.exo-device-page-panel[aria-label="Polling"] .exo-set-state')?.textContent ?? ''; return /\d+ Hz/.test(text) ? text : null }, { timeoutMs: 12000 })
      c.evidence.rate = rate
      if (!rate) failures.push('Measure gave no rate')
    }
    const fit = await t.shell.eval(`(() => { const page = document.querySelector('.exo-devices-page'), panels = document.querySelector('.exo-device-page-panels'); return { page: page.scrollHeight - page.clientHeight, panels: panels.scrollHeight - panels.clientHeight, port: !!document.querySelector('.exo-device-page-panel[aria-label="USB port"]') } })()`)
    c.evidence.fit = fit
    await c.shot('controller', t.shell)
    if (fit.page > 0 || fit.panels > 0) {
      c.evidence.knownAppBug = '3.29.1 ControllerPage wraps DevicePage in div.contents (display: contents), so DevicePage.useBand reads room.clientHeight=0 and skips multi-column banding'
      failures.push(`known 3.29.1 app bug (ControllerPage display:contents breaks DevicePage useBand): the page scrolls (${fit.page}px, its panels ${fit.panels}px)`)
    }
    if (pad.connection === 'usb' && !fit.port) failures.push('no USB port panel for a controller on a cable')
    await t.shell.click(() => document.querySelector('.exo-devices-back button'))
    await sleep(800)
    return failures
  })

  await run.check('devices', 'controller-setup', "a DualSense's calibration opens and closes without writing anything; an Edge's on-board profiles are read, never edited; what Exo remembers shows; games read it themselves, always", async (c) => {
    const pad = (await t.host('devices.list')).devices.find(d => d.kind === 'controller' && /^dualsense/.test(d.model ?? ''))
    if (!pad) c.skip('no DualSense on this PC')
    const failures = []
    await t.shell.click((card) => document.querySelector(`button[data-device-card="${CSS.escape(card)}"]`), pad.id)
    await t.shell.waitFor(() => document.querySelector('.exo-device-page-hero') || null, { timeoutMs: 8000 })
    const rows = await t.shell.waitFor(() => {
      const labels = [...document.querySelectorAll('.exo-device-page-panel .exo-set-item-label')].map(l => l.textContent)
      return labels.includes('Calibration') ? labels : null
    }, { timeoutMs: 8000 })
    c.evidence.rows = rows
    if (!rows) failures.push('no Calibration row in Sticks')
    if (await t.shell.eval(`!!document.querySelector('.exo-device-page-panel[aria-label="Profiles"]')`)) failures.push("an Edge's on-board profile editor is back")
    if (await t.shell.eval(`!!document.querySelector('.exo-device-page-panel[aria-label="Profiles on the controller"] button')`)) failures.push("an Edge's on-board profiles can be edited: they are only read")
    const remembers = await t.shell.waitFor(() => !!document.querySelector('.exo-device-page-panel[aria-label="What Exo remembers"]'), { timeoutMs: 8000 })
    if (!remembers) failures.push('no What Exo remembers panel')
    if (await t.shell.click(() => [...document.querySelectorAll('.exo-device-page-panel[aria-label="Sticks"] button')].find(b => /^(Calibrate|Again)$/.test(b.textContent.trim())))) {
      const intro = await t.shell.waitFor(() => document.querySelector('.exo-calibrate h2')?.textContent ?? null, { timeoutMs: 5000 })
      if (intro !== 'Calibrate the sticks') failures.push(`calibration opened on "${intro}", not its first step`)
      await t.shell.click(() => [...document.querySelectorAll('.exo-calibrate button')].find(b => b.textContent.trim() === 'Cancel'))
      await sleep(500)
    }
    if (await t.shell.eval(`document.querySelectorAll('.exo-dialog').length`)) failures.push('a dialog stayed open after Cancel')
    const steam = await t.host('steamInput.state')
    c.evidence.steamInput = steam
    const native = await t.shell.eval(`[...document.querySelectorAll('.exo-device-page-panel .exo-set-item-label')].some(l => l.textContent === 'Games read it themselves')`)
    if (steam.steam && !native) failures.push('no "Games read it themselves" row with Steam installed')
    if (await t.shell.eval(`!!document.getElementById('ctl-native')`)) failures.push('"Games read it themselves" is still a switch')
    await t.shell.click(() => document.querySelector('.exo-devices-back button'))
    await sleep(800)
    return failures
  })

  await run.check('devices', 'rail', "every device's left side is one width: Exo Mouse's is the audio page's", async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('button[data-device-card="audio"]')`)) c.skip('no audio device')
    await t.shell.click(() => document.querySelector('button[data-device-card="audio"]'))
    const page = await t.shell.waitFor(() => { const w = document.querySelector('.exo-device-page-hero')?.getBoundingClientRect().width; return w && document.querySelector('.exo-device-page')?.style.getPropertyValue('--exo-device-hero-w') ? w : null }, { timeoutMs: 8000 })
    await t.shell.click(() => document.querySelector('.exo-devices-back button'))
    await sleep(800)
    const frame = await openFrame(t.shell, 'mouse.exo-launcher.local', 20000).catch(() => null)
    if (!frame) return ['the mouse page did not load']
    const mouse = await frame.waitFor(() => document.querySelector('.m-hero')?.getBoundingClientRect().width || null, { timeoutMs: 8000 })
    frame.close()
    c.evidence.widths = { page, mouse }
    if (!mouse) c.skip('no mouse on this PC')
    return Math.abs(page - mouse) > 1 ? [`the audio page's hero is ${page}px, Exo Mouse's ${mouse}px`] : []
  })

  await run.check('devices', 'mouse', 'Exo Mouse loads and shows the mouse or how to connect one', async (c) => {
    const frame = await openFrame(t.shell, 'mouse.exo-launcher.local', 20000).catch(() => null)
    if (!frame) return ['the mouse page did not load']
    const state = await frame.waitFor(() => { const text = document.body.innerText; return text.length > 40 ? { text: text.slice(0, 80), theme: document.documentElement.dataset.exoTheme ?? null } : null }, { timeoutMs: 15000 })
    const theme = 'void'
    c.evidence.state = state
    frame.close()
    await c.shot('devices', t.shell)
    const failures = []
    if (!state) failures.push('the mouse page stayed empty')
    else if (state.theme !== theme) failures.push(`the mouse page shows ${state.theme}, Exo ${theme}`)
    return failures
  })

  // Opened and closed only: measuring takes the pointer (Pointer Lock) and a hand, which a run never does.
  await run.check('devices', 'mouse-angle', "Exo Mouse's sensor angle measurement opens on its pad and closes", async (c) => {
    const frame = await openFrame(t.shell, 'mouse.exo-launcher.local', 20000).catch(() => null)
    if (!frame) return ['the mouse page did not load']
    const sheet = await frame.eval(`(async () => {
      const named = b => (b.closest('.m-row')?.querySelector('.m-row-label') ?? b.closest('.m-panel')?.querySelector('.m-panel-title'))?.textContent ?? ''
      const measure = [...document.querySelectorAll('.m-panel button')].find(b => b.textContent === 'Measure' && /^(Sensor angle|Angle tuning)/.test(named(b)))
      if (!measure) return null
      measure.click()
      await new Promise(r => setTimeout(r, 400))
      const pad = document.querySelector('.mt-sheet .sa-pad')
      const seen = { from: named(measure), pad: !!pad, focused: !!pad && document.activeElement === pad, label: pad?.textContent.trim() ?? null }
      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
      await new Promise(r => setTimeout(r, 300))
      return { ...seen, closed: !document.querySelector('.mt-sheet') }
    })()`)
    frame.close()
    c.evidence.sheet = sheet
    if (!sheet) c.skip('no mouse open in Exo Mouse')
    const failures = []
    if (!sheet.pad) failures.push('Measure opened no pad')
    else if (!sheet.focused) failures.push('the pad did not take the keyboard')
    if (sheet.pad && sheet.label !== 'Click, then swipe side to side' && !/Click, then swipe to check$/.test(sheet.label)) failures.push(`the pad read "${sheet.label}"`)
    if (!sheet.closed) failures.push('Escape left the sheet open')
    return failures
  })
}
