import { host, onHostEvent, type HidReply } from './host'
import { thumbUrl } from './coverThumb'
import { publishMouseNotice, publishMouseProfile, publishMouseState } from './mouseState'
import { report } from './report'
import { onTextScale, textScale } from './exoTheme'

/** The HID calls the mouse module may make. */
const HID: Record<string, (p: Record<string, unknown>) => Promise<HidReply>> = {
  'hid.list': host.hidList,
  'hid.open': host.hidOpen,
  'hid.close': host.hidClose,
  'hid.sendReport': host.hidSendReport,
  'hid.sendFeatureReport': host.hidSendFeatureReport,
  'hid.receiveFeatureReport': host.hidReceiveFeatureReport,
  'hid.receiveInputReport': host.hidReceiveInputReport,
}

/**
 * Each exact source id to its library card id. Profiles link to card ids, while
 * game.runState names the exact source that started, so a card owned on two
 * stores still switches its profile.
 */
let cardOf = new Map<string, string>()

async function readLibrary() {
  const library = await host.getLibrary()
  const next = new Map<string, string>()
  for (const g of library.games ?? []) {
    next.set(g.id, g.id)
    for (const v of g.variants ?? []) if (!next.has(v.id)) next.set(v.id, g.id)
  }
  cardOf = next
  return library
}

/** The card a source id belongs to, reading the library again for a source not seen yet. */
async function cardId(id: string): Promise<string> {
  if (!cardOf.has(id)) await readLibrary().catch(() => undefined)
  return cardOf.get(id) ?? id
}

/**
 * The installed games, by id, title and Exo's local cover thumbnail, so a mouse profile can be linked
 * to a game. Exo then tells the module when games start and stop (game.runState).
 */
async function installedGames() {
  const library = await readLibrary()
  const games = (library.games ?? [])
    .filter(g => g.installed)
    // The cover is Exo's own cached thumbnail (a local URL), never a store's.
    .map(g => ({ id: g.id, title: g.title, cover: g.coverUrl?.startsWith('https://covers.exo-launcher.local/') ? thumbUrl(g.coverUrl, 240) : null }))
    .sort((a, b) => a.title.localeCompare(b.title))
  return { ok: true, games }
}

/** The module's own reads and writes besides HID: the games list, its saved profiles and its saved presets. */
const EXO: Record<string, (p: Record<string, unknown>) => Promise<unknown>> = {
  'exo.games': installedGames,
  'exo.profiles.read': () => host.mouseProfilesGet(),
  'exo.profiles.write': p => host.mouseProfilesSet({ data: p.data }),
  'exo.presets.read': () => host.mousePresetsGet(),
  'exo.presets.write': p => host.mousePresetsSet({ data: p.data }),
  // Exo's text size, so the module's type follows Settings > General > Text size.
  'exo.textScale': () => Promise.resolve({ ok: true, data: String(textScale()) }),
  // Exo's motion ('reduce' or 'full'), so the module stops the moves Exo stops.
  'exo.motion': () => Promise.resolve({ ok: true, data: motion() }),
  // Exo's Appearance (light, dark or void) and the glass it is drawn in, so the module is drawn in both.
  'exo.theme': () => Promise.resolve({ ok: true, data: 'void', look: look() }),
  // The module's CPU core panel: where the mouse's core stands, or one of the host's actions. Only the action
  // crosses; the host keeps the device, the CPU it offers and the backup, and asks Windows itself before it changes anything.
  'exo.mouseCore': async p => host.systemMouseCore(coreAction(p.action)),
}

const CORE_ACTIONS = ['measure', 'drivers', 'spread', 'unspread', 'apply', 'undo'] as const

/** No action reads the state; anything but the host's actions is refused before it reaches the host. */
function coreAction(action: unknown): (typeof CORE_ACTIONS)[number] | undefined {
  if (action === undefined) return undefined
  const known = CORE_ACTIONS.find(a => a === action)
  if (!known) throw new Error('Exo Mouse asked for an unknown CPU core action.')
  return known
}

/** Exo's motion setting as the shell writes it on the root (useShellLayout). */
const motion = () => document.documentElement.dataset.exoMotion === 'reduce' ? 'reduce' : 'full'

/** Where each of Exo Mouse's look values is read: the page, a pane (a device page's panel), its glass layer, a control
    on it, large glass (a sheet, a menu, a dialog) and a device page's hero. */
type Surface = 'page' | 'pane' | 'paneGlass' | 'control' | 'large' | 'hero'

/** Exo Mouse's look value, and the property of the Exo surface it is read from. */
const LOOK: [name: string, surface: Surface, property: string][] = [
  ['--bg', 'page', '--color-bg'], ['--ink', 'page', '--color-fg'], ['--muted', 'page', '--color-muted'],
  ['--faint', 'page', '--color-faint'], ['--good', 'page', '--color-good'], ['--bad', 'page', '--color-bad'],
  ['--primary-ink', 'page', '--color-primary-fg'], ['--primary-hover', 'page', '--exo-primary-hover'],
  ['--track', 'page', '--exo-track'], ['--hair', 'page', '--exo-float-line'],
  ['--pane-fill', 'paneGlass', 'background-color'],
  ['--pane-edge', 'paneGlass', 'box-shadow'], ['--line-soft', 'pane', '--exo-float-line'],
  ['--hover-fill', 'pane', '--hover-fill'], ['--selected', 'pane', '--exo-selected'], ['--pressed', 'pane', '--exo-pressed'],
  ['--fill', 'control', 'background-color'], ['--rim', 'control', 'box-shadow'],
  ['--rim-pressed', 'control', '--exo-rim-pressed'], ['--field-rim', 'control', '--exo-field-rim'],
  ['--panel-fill', 'large', 'background-color'], ['--panel-edge', 'large', 'box-shadow'],
  ['--panel-ink', 'large', '--color-fg'], ['--panel-muted', 'large', '--color-muted'], ['--panel-hover', 'large', '--hover-fill'],
  ['--panel-selected', 'large', '--exo-selected'], ['--panel-pressed', 'large', '--exo-pressed'],
  ['--hero-fill', 'hero', 'background-color'], ['--hero-edge', 'hero', 'box-shadow'],
]

const element = (tag: string, className: string, ...children: Element[]) => {
  const made = document.createElement(tag)
  made.className = className
  made.append(...children)
  return made
}

/**
 * Exo's glass as Exo Mouse draws it (DESIGN.md › Glass), as CSS values only: each is read off a hidden copy of the Exo
 * surface it stands for, so the frame's panels, sheets and controls are Exo's own in every Appearance and Material,
 * and stay so whatever glass.css changes. No code or style sheet crosses, only the values.
 */
function look(): Record<string, string> {
  const control = element('button', 'exo-set-btn')
  const pane = element('section', 'exo-system-panel', control)
  const large = element('div', 'exo-glass is-panel')
  const hero = element('aside', 'exo-device-page-hero')
  const probe = element('div', '', pane, large, hero)
  probe.setAttribute('aria-hidden', 'true')
  probe.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;overflow:hidden;visibility:hidden;pointer-events:none'
  document.body.append(probe)
  const read: Record<Surface, CSSStyleDeclaration> = {
    page: getComputedStyle(document.documentElement), pane: getComputedStyle(pane), paneGlass: getComputedStyle(pane, '::before'),
    control: getComputedStyle(control), large: getComputedStyle(large), hero: getComputedStyle(hero),
  }
  const values = Object.fromEntries(LOOK.map(([name, surface, property]) => [name, read[surface].getPropertyValue(property).trim()]))
  probe.remove()
  return values
}

/** The Appearance and its glass to the module, when its frame is there: Appearance, Material and contrast change it. */
const sendLook = () => { if (frameWindow()) postToMouse({ event: 'exo.theme', data: { theme: 'void', look: look() } }) }

type Request = {
  exo?: string; id?: number; method?: string; params?: Record<string, unknown>
  notice?: unknown; state?: { name?: unknown; maker?: unknown; battery?: unknown; charging?: unknown; low?: unknown; image?: unknown; wireless?: unknown } | null; profile?: unknown
}

/** A notice is one short line; anything longer or not text is dropped. */
const noticeText = (v: unknown) => typeof v === 'string' && v.length > 0 && v.length <= 160 ? v : null

/** Exo Mouse runs on its own host, in a frame in the Devices room (DevicesRoom). */
export const MOUSE_ORIGIN = 'https://mouse.exo-launcher.local'

// The frame, found again only once it has left the page (Try again mounts a new one): every HID report and message
// passes through here, and searching the page for it each time walks the page's tree up to it.
let frame: HTMLIFrameElement | null = null
const frameWindow = () => {
  if (!frame?.isConnected) frame = document.querySelector<HTMLIFrameElement>('.exo-devices-frame')
  return frame?.contentWindow ?? null
}

/** A message to Exo Mouse's frame, when it is there. */
export function postToMouse(message: object) {
  frameWindow()?.postMessage({ exo: 'mouse', ...message }, MOUSE_ORIGIN)
}

/** A notice or the mouse's state, posted by the module without asking for a reply. */
function fromModule(m: Request) {
  const text = noticeText(m.notice)
  if (text) publishMouseNotice(text)
  if (m.profile === null || (typeof m.profile === 'string' && m.profile.length > 0)) publishMouseProfile(m.profile === null ? null : m.profile.slice(0, 40))
  if (m.state === null) { publishMouseState(null); return }
  if (!m.state || typeof m.state.name !== 'string') return
  const battery = typeof m.state.battery === 'number' && m.state.battery >= 0 && m.state.battery <= 100 ? Math.round(m.state.battery) : null
  // A picture only from OpenMouse's render server, by a plain file name: the frame can't point Exo anywhere else.
  const image = typeof m.state.image === 'string' && /^https:\/\/img\.openmouse\.app\/[a-z0-9-]+\.(png|webp)$/.test(m.state.image) ? m.state.image : null
  const wireless = typeof m.state.wireless === 'boolean' ? m.state.wireless : null
  const maker = typeof m.state.maker === 'string' && m.state.maker.trim() ? m.state.maker.trim().slice(0, 40) : null
  publishMouseState({ name: m.state.name.slice(0, 60), maker, battery, charging: m.state.charging === true, low: m.state.low === true, image, wireless })
}

let relaying = false

/**
 * Exo Mouse's line to Exo. Started with Exo and never removed: the module runs
 * from startup with the Devices room hidden, and a hidden room's effects do not
 * run, so the relay cannot live in the room.
 */
export function startMouseRelay() {
  if (relaying) return
  relaying = true
  window.addEventListener('message', event => {
    if (event.source === null || event.source !== frameWindow() || event.origin !== MOUSE_ORIGIN) return
    const m = event.data as Request | null
    if (m?.exo === 'mouse' && m.id === undefined) { fromModule(m); return }
    const call = m && m.exo === 'mouse' && typeof m.id === 'number' && m.method
      ? Object.hasOwn(EXO, m.method) ? EXO[m.method] : Object.hasOwn(HID, m.method) ? HID[m.method] : undefined
      : undefined
    if (!m || !call) return
    const { id, params } = m
    // The answer goes to the frame that asked: one reloaded since (its request ids start over) is not given another's answer.
    const asker = event.source
    const answer = (message: object) => { if (asker === frameWindow()) postToMouse(message) }
    call(params ?? {})
      .then(result => answer({ id, result }))
      .catch((error: unknown) => {
        report(error, 'DevicesRoom.hid', { level: 'warn' })
        answer({ id, error: error instanceof Error ? error.message : 'Exo could not reach the device.' })
      })
  })
  onTextScale(scale => postToMouse({ event: 'exo.textScale', data: { scale } }))
  new MutationObserver(() => postToMouse({ event: 'exo.motion', data: { motion: motion() } }))
    .observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-motion'] })
  for (const query of ['(prefers-contrast: more)', '(forced-colors: active)']) matchMedia(query).addEventListener('change', sendLook)
  onHostEvent('hid.input', data => postToMouse({ event: 'hid.input', data }))
  onHostEvent('hid.changed', data => postToMouse({ event: 'hid.changed', data }))
  // In order: a stop never overtakes the start it follows while a library read is pending.
  let runStates = Promise.resolve()
  onHostEvent('game.runState', data => {
    runStates = runStates
      .then(() => cardId(data.id))
      .then(id => postToMouse({ event: 'game.runState', data: { id, isRunning: data.isRunning } }))
  })
}
