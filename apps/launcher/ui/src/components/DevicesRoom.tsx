import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react'
import { ChevronLeft, Refresh } from '../brand/icons'
import { host, onHostEvent, type Peripheral } from '../lib/host'
import { MOUSE_ORIGIN, postToMouse } from '../lib/mouseRelay'
import { onMouseState, type MouseState } from '../lib/mouseState'
import { usePageActive, usePageVisible } from '../lib/pageActivity'
import { report } from '../lib/report'
import { cn } from '../lib/utils'
import { transition, visible } from '../lib/viewTransition'
import { DeviceArt, type ArtKind } from './devices/art'
import { AudioPage, type Wearing } from './devices/AudioPage'
import { ControllerPage } from './devices/ControllerPage'
import { portShort } from './devices/ports'
import { railWidth } from './devices/rail'
import { readWearing, saveWearing } from './devices/wearing'
import { onUiPrefs } from '../lib/uiPrefs'
import { UsbMap } from './devices/UsbMap'
import { ErrorState } from './ui/ErrorState'
import { deviceHasPage } from '../lib/canUse'

/** How long Exo Mouse has to say anything after its frame starts loading before the room says it didn't load. */
const LOAD_MS = 15_000

/** What devices.image is asked for a device's official picture. */
type PictureAsk = { kind: string; model?: string | null; name?: string | null; maker?: string | null; color?: string | null; layout?: string | null }

/** One card in the grid and one page: the mouse, a keyboard, your audio, a controller. */
type Card = {
  id: string; kind: 'mouse' | 'keyboard' | 'audio' | 'controller'; name: string; status: string
  /** Its official picture, asked for by what it is; Exo's drawing stands in until it comes, or when there is none. */
  picture: PictureAsk; art: ArtKind; device?: Peripheral; mic?: Peripheral
}

/**
 * Devices (DESIGN.md › Devices): what you plug in, each as itself. The room is its devices: their cards fill it, laid
 * out by how many there are (four two by two), one per device Exo has a page for (your mouse, what you listen on, your
 * controller), each its official picture as large as its card allows, name, battery and link, and which USB it's on;
 * they rise in as the room first opens. A press grows a card into its page, and Devices at its top folds the page back
 * into its card. The mouse's page is Exo Mouse, a separate program (AGPL, in
 * /mouse) in a frame, kept running behind the grid so its battery stays current; Exo relays its HID calls
 * (lib/mouseRelay.ts). Your audio's page holds your pick and Lucid on one screen; a controller's holds the Controller
 * Lab. Keyboards and controllers the lab can't read show what they are; Exo doesn't set them up yet. Each card says which
 * USB it's on, green where that's best for it, red with what to do where it isn't.
 */
export function DevicesRoom({ active, onBack }: { active: boolean; onBack?: () => void }) {
  const foreground = usePageActive()
  // On screen, focused or not: the controller's live view is watched beside whatever has focus.
  const onScreen = usePageVisible()
  const [page, setPage] = useState('overview')
  const { devices, known, failed, scanning, rescan } = usePeripherals(active && onScreen)
  const mouse = useMouse()
  const [wearing, setWearing] = useWearing()
  // Only what has a page: a keyboard, or a controller the Controller Lab can't read, has nothing to open (lib/canUse.ts).
  const cards = cardsFor(devices, mouse, wearing).filter(card => deviceHasPage(card.kind, card.device?.lab))
  const pictures = usePictures(cards)
  // A device that went (unplugged) takes its page with it.
  useEffect(() => { if (page !== 'overview' && !cards.some(card => card.id === page)) setPage('overview') }, [cards, page])
  const current = cards.find(card => card.id === page) ?? null
  // Back from a page the cards are simply there, the one you left folding back into place: they rose in once.
  const [returned, setReturned] = useState(false)
  const open = (id: string) => {
    void transition(() => { setReturned(true); setPage(id) }, { kind: 'morph', morph: { name: MORPH, from: artOfCard(id), to: artOfPage } })
  }
  const back = () => {
    const from = page
    const art = artOfPage()
    void transition(() => setPage('overview'), art ? { kind: 'morph', back: true, morph: { name: MORPH, from: art, to: () => artOfCard(from) } } : { kind: 'fade' })
  }

  return (
    <main className={cn('exo-devices', page !== 'overview' && 'is-device', page === 'mouse' && 'is-mouse')} aria-label="Devices">
      {page !== 'overview' ? (
        <header className="exo-devices-back">
          <button type="button" className="exo-set-btn" onClick={back}><ChevronLeft size={16} />Devices</button>
        </header>
      ) : onBack ? (
        <header className="exo-devices-back">
          <button type="button" className="exo-set-btn" onClick={onBack}><ChevronLeft size={16} />PC</button>
        </header>
      ) : null}
      <div className="exo-devices-page">
        {/* Kept while a device's page shows, hidden, so coming back finds the cards as they were: built again, every card's
            picture loaded again (and flashed in) each time. */}
        <Overview hidden={page !== 'overview'} cards={cards} pictures={pictures} known={known} failed={failed} scanning={scanning} rise={!returned} onRescan={rescan} onOpen={open} />
        {current?.kind === 'audio' ? (
          <AudioPage name={current.name} status={current.status} art={<Picture card={current} url={pictures[current.id]} wash />}
            output={current.device} mic={current.mic} wearing={wearing} onWearing={setWearing} visible={active && onScreen} />
        ) : null}
        {current?.kind === 'controller' && current.device ? (
          <ControllerPage card={current.device} name={current.name} status={current.status} art={<Picture card={current} url={pictures[current.id]} wash />}
            visible={active && onScreen} />
        ) : null}
        <MouseFrame shown={page === 'mouse'} visible={active && foreground && page === 'mouse'} />
      </div>
    </main>
  )
}

/** A device's picture is one thing in two places (a view transition's shared element): it flies from its card to its
    page's picture and back, as a game's art does, while the page fades in around it (transitions.css fits it whole in
    the flying box). A page with no picture of its own (Exo Mouse's) just fades. */
const MORPH = 'exo-device-art'
const artOfCard = (id: string) => visible(`[data-device-card="${CSS.escape(id)}"] .exo-device-card-art`)
const artOfPage = () => visible('.exo-device-hero-art')

const LINK: Record<string, string> = { usb: 'Wired', wireless: 'Wireless', bluetooth: 'Bluetooth', jack: '3.5 mm jack', hdmi: 'HDMI' }

/** How a device is linked, with its battery when Windows knows it ("Bluetooth · 60%"). */
function linked(device: Peripheral) {
  return [device.connection ? LINK[device.connection] : null, device.battery != null ? `${Math.round(device.battery * 100)}%` : null].filter(Boolean).join(' · ') || 'Connected'
}

function cardsFor(devices: Peripheral[], mouse: MouseState | null, wearing: Wearing): Card[] {
  const audio = audioCard(devices, wearing)
  return [
    ...mouseCard(devices, mouse),
    ...(audio ? [audio] : []),
    ...devices.filter(device => device.kind === 'controller').map((pad): Card => ({
      // "DualSense Edge Wireless Controller" is a DualSense Edge.
      id: pad.id, kind: 'controller', name: pad.name.replace(/\s+(wireless\s+)?controller$/i, '') || pad.name, status: linked(pad), device: pad,
      art: 'controller', picture: pad,
    })),
  ]
}

/**
 * The mouse as Exo Mouse knows it (its own name, battery and link), else as Windows lists it. Its picture is asked
 * for by the render Exo Mouse picked for it ("logitech-g502-x-plus"), which Exo's list carries as the model's id, so
 * the card and Exo Mouse agree on which mouse it is; by its name until Exo Mouse has read it.
 */
function mouseCard(devices: Peripheral[], mouse: MouseState | null): Card[] {
  const pointer = devices.find(device => device.kind === 'mouse')
  if (!mouse && !pointer) return []
  const link = mouse?.wireless === true ? 'Wireless' : mouse?.wireless === false ? 'Wired' : pointer?.connection ? LINK[pointer.connection] : null
  const status = [mouse?.battery != null ? `${mouse.battery}%` : null, link].filter(Boolean).join(' · ') || 'Connected'
  const name = mouse?.name ?? pointer?.name ?? 'Mouse'
  return [{ id: 'mouse', kind: 'mouse', name, status, device: pointer, art: 'mouse', picture: { kind: 'mouse', model: renderOf(mouse), name, maker: mouse?.maker ?? pointer?.maker } }]
}

/** The render's file name without its type ("logitech-g502-x-plus"); null before Exo Mouse has one. */
const renderOf = (mouse: MouseState | null) => mouse?.image?.split('/').pop()?.replace(/\.\w+$/, '') ?? null

/** What you listen on and speak into: the pair you picked names what's on your head; speakers and a monitor stay themselves. */
function audioCard(devices: Peripheral[], wearing: Wearing): Card | null {
  const output = devices.find(device => device.kind === 'audio' && device.role === 'output')
  const mic = devices.find(device => device.kind === 'audio' && device.role === 'input')
  if (!output && !mic) return null
  const speakers = output?.model === 'speakers' || output?.model === 'monitor'
  const worn = output && !speakers ? wearing : null
  const name = worn?.name ?? output?.name ?? mic?.name ?? 'Audio'
  return {
    id: 'audio', kind: 'audio', name, status: output ? linked(output) : 'Microphone', device: output, mic, art: audioArt(output, worn, speakers),
    picture: worn ? { kind: 'audio', model: worn.look, name: worn.name } : { kind: 'audio', model: output?.model ?? 'microphone', name },
  }
}

/** What an audio card shows when it has no picture: speakers, a mic alone, or what you wear. */
function audioArt(output: Peripheral | undefined, worn: Wearing, speakers: boolean): ArtKind {
  if (speakers) return 'speakers'
  if (!output) return 'microphone'
  return worn?.look ?? (output.model === 'headset' ? 'headset' : 'headphones')
}

/** A device's picture: its official one once it has come, else Exo's drawing of its kind. A page's picture stands in
    its own light, as Exo Mouse's does: itself, blurred far back. */
/** Photos already decoded once this run: shown at once when a card or page comes back, never through Exo's drawing. */
const shownPhotos = new Set<string>()

/**
 * A device's official photo, else Exo's drawing of it. The drawing holds until the photo has loaded, then the photo fades
 * in over it: a link that arrives before its picture never leaves the card empty.
 */
function Picture({ card, url, wash = false }: { card: Card; url: string | null | undefined; wash?: boolean }) {
  const [loaded, setLoaded] = useState<string | null>(null)
  const art = <DeviceArt kind={card.art} model={card.device?.model} layout={card.device?.layout} color={card.device?.color} />
  if (!url) return art
  const ready = loaded === url || shownPhotos.has(url)
  const fresh = loaded === url
  const land = () => { shownPhotos.add(url); setLoaded(url) }
  return <>
    {ready ? null : art}
    {wash && ready && !isBlack(card) ? <img className="exo-device-wash" src={url} alt="" draggable={false} aria-hidden="true" /> : null}
    <img className={cn('exo-device-render', fresh && 'is-fresh', isBlack(card) && 'is-black')} src={url} alt="" draggable={false} hidden={!ready} onLoad={ready ? undefined : land} />
  </>
}

/** A black pad on a black page (Midnight Black): its photo is lifted, with a hairline around it and no wash (devices.css). */
const isBlack = (card: Card) => card.device?.color === 'Midnight Black'

/** A card's place in the grid, for its turn rising in (DESIGN.md › Motion › Arrive). */
const turn = (i: number) => ({ '--i': i } as CSSProperties)

function Overview({ hidden, cards, pictures, known, failed, scanning, rise, onRescan, onOpen }: {
  hidden: boolean; cards: Card[]; pictures: Record<string, string | null>; known: boolean; failed: boolean; scanning: boolean; rise: boolean; onRescan: () => void; onOpen: (id: string) => void
}) {
  const [usbMap, setUsbMap] = useState(false)
  // Each card reads what you'd open a page for: its battery and link, and which USB it's on, green where it's best for the
  // device, red where it isn't. A red one says what to do when there's something to do: a free port on the processor's
  // own USB, or a hub to take out of the way. Cards update as plugs move, so nothing needs a mode to watch them.
  const tagOf = (card: Card) => {
    const port = card.device?.port
    const short = port ? portShort(port) : null
    if (!port || !short) return null
    // A mouse, keyboard or controller is best on the processor's own USB with no hub; sound doesn't care where.
    const good = card.kind === 'audio' || port.where === 'cpu' && !port.hubs
    const text = good ? short : port.better === 'free' ? 'Move to CPU USB' : port.better === 'hub' ? 'Plug in without the hub' : short
    return { text, good }
  }
  return (
    <section className="exo-devices-overview" aria-label="Devices" hidden={hidden}>
      <header className="exo-devices-head">
        {/* Nothing found only once Windows has been asked: the first list takes a moment. */}
        <h1>{cards.length === 0 && known && !failed ? 'No devices to set up' : 'Devices'}</h1>
        <div className="exo-devices-head-end">
          <button type="button" className="exo-set-btn" aria-expanded={usbMap} onClick={() => setUsbMap(true)}>USB map</button>
          {usbMap ? <UsbMap onClose={() => setUsbMap(false)} /> : null}
          <button type="button" className="exo-set-btn" disabled={scanning} onClick={onRescan}>
            <Refresh size={16} className={scanning ? 'exo-spin' : undefined} />Rescan
          </button>
        </div>
      </header>
      {/* Laid out by how many there are (devices.css); seven or more are a fuller grid that scrolls. */}
      {failed && cards.length === 0 ? <ErrorState title="Devices unavailable" message="Exo couldn’t read your devices." onRetry={onRescan} busy={scanning} /> : null}
      <div className="exo-devices-grid" data-count={cards.length > 6 ? 'many' : cards.length}>
        {cards.map((card, i) => {
          const tag = tagOf(card)
          const said = [card.name, card.status, tag?.text].filter(Boolean).join(', ')
          const inside = <>
            <span className="exo-device-card-stage">
              <span className="exo-device-card-art"><Picture card={card} url={pictures[card.id]} /></span>
            </span>
            <span className="exo-device-card-text">
              <strong>{card.name}</strong>
              <span className="exo-device-card-facts">
                <span>{card.status}</span>
                {/* The port is said only when a better one would help: a good port needs no tag. */}
                {tag && !tag.good ? <span className="exo-device-port is-bad">{tag.text}</span> : null}
              </span>
            </span>
          </>
          return (
            <button key={card.id} type="button" data-device-card={card.id} className={cn('exo-device-card exo-glass is-panel', rise && 'exo-motion-arrive')} style={turn(i)}
              onClick={() => onOpen(card.id)} aria-label={`${said}. Open.`}>
              {inside}
            </button>
          )
        })}
      </div>
    </section>
  )
}

/**
 * The devices Windows has (devices.list), read when the room first shows and again when one comes or goes. One that
 * comes or goes while the room is away (behind another room, Exo hidden, a game in front) is read when it shows again,
 * not at once: a list reads every USB port.
 */
function usePeripherals(shown: boolean) {
  const [devices, setDevices] = useState<Peripheral[]>([])
  // Whether a list has come back, for "No devices found" to mean it.
  const [known, setKnown] = useState(false)
  const [scanning, setScanning] = useState(false)
  // The last list could not be read: the room says so and offers Try again, rather than "No devices found".
  const [failed, setFailed] = useState(false)
  // The list is out of date: before its first read, and after a device came or went while the room was away.
  const stale = useRef(true)
  const rescan = useCallback(() => {
    stale.current = false
    setScanning(true)
    host.devicesList()
      .then(result => { setFailed(!result.ok); if (result.ok) setDevices(result.devices) })
      .catch((error: unknown) => { setFailed(true); report(error, 'DevicesRoom.list') })
      .finally(() => { setScanning(false); setKnown(true) })
  }, [])
  useEffect(() => { if (shown && stale.current) rescan() }, [shown, rescan])
  useEffect(() => onHostEvent('devices.changed', () => {
    stale.current = true
    if (shown) rescan()
  }), [shown, rescan])
  return { devices, known, failed, scanning, rescan }
}

/** Each card's official picture (devices.image: fetched once, cut out and framed alike, kept on this PC), by what it is. */
function usePictures(cards: Card[]) {
  const [pictures, setPictures] = useState<Record<string, string | null>>({})
  const asked = useRef(new Map<string, string>())
  useEffect(() => {
    for (const card of cards) {
      const key = JSON.stringify(card.picture)
      if (asked.current.get(card.id) === key) continue
      asked.current.set(card.id, key)
      host.devicesImage(card.picture)
        .then(result => { if (asked.current.get(card.id) === key) setPictures(current => ({ ...current, [card.id]: result.ok ? result.url : null })) })
        .catch((error: unknown) => report(error, 'DevicesRoom.picture', { level: 'warn' }))
    }
  }, [cards])
  return pictures
}

function useMouse() {
  const [mouse, setMouse] = useState<MouseState | null>(null)
  useEffect(() => onMouseState(setMouse), [])
  return mouse
}

/** The headphones you picked, kept in Exo's settings; null until you pick. */
function useWearing(): [Wearing, (wearing: Wearing) => void] {
  const [wearing, setWearing] = useState<Wearing>(readWearing)
  useEffect(() => onUiPrefs(() => setWearing(readWearing())), [])
  const choose = useCallback((next: Wearing) => {
    setWearing(next)
    saveWearing(next)
  }, [])
  return [wearing, choose]
}

/**
 * Exo Mouse in its frame: the mouse's page. It runs from Exo's start whichever page shows (its battery feeds the
 * grid and the Bar), reading the mouse fast only while its page is on screen in front. Until it has said anything it
 * isn't running, and after a while the page says so with Try again.
 */
function MouseFrame({ shown, visible }: { shown: boolean; visible: boolean }) {
  useEffect(() => {
    postToMouse({ event: 'room.visible', data: visible })
    return () => postToMouse({ event: 'room.visible', data: false })
  }, [visible])

  const frame = useRef<HTMLIFrameElement>(null)
  // One rail for every device (rail.ts): the frame is told it, from the room's width inside its gutters, as Exo sees it.
  const sendRail = useCallback(() => {
    const box = frame.current
    if (!box || box.clientWidth === 0) return
    const gutter = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-gutter')) || 10
    const scale = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-scale')) || 1
    postToMouse({ event: 'room.rail', data: railWidth(box.clientWidth - 2 * gutter, scale) })
  }, [])
  useEffect(() => {
    if (!frame.current) return
    const sized = new ResizeObserver(sendRail)
    sized.observe(frame.current)
    return () => sized.disconnect()
  }, [sendRail, shown])
  const [load, setLoad] = useState(0)
  const [failed, setFailed] = useState(false)
  // The load whose frame has said anything: it is running from then on, however quiet it goes after (behind a game it
  // reads the mouse slowly or not at all). Judged per load, not per showing; once a load has spoken, nothing listens
  // here (the relay hears the rest).
  const heard = useRef(-1)
  useEffect(() => {
    const listen = (event: MessageEvent) => {
      if (event.origin !== MOUSE_ORIGIN || event.source === null || event.source !== frame.current?.contentWindow) return
      heard.current = load
      setFailed(false)
      window.removeEventListener('message', listen)
    }
    window.addEventListener('message', listen)
    return () => window.removeEventListener('message', listen)
  }, [load])
  useEffect(() => {
    if (!visible || heard.current === load) return
    const timer = window.setTimeout(() => { if (heard.current !== load) setFailed(true) }, LOAD_MS)
    return () => window.clearTimeout(timer)
  }, [load, visible])

  return (
    <div className={cn('exo-devices-mouse', !shown && 'is-away')} aria-hidden={!shown} inert={!shown}>
      {failed && shown ? (
        <ErrorState title="Exo Mouse unavailable" message="Exo Mouse didn't load." onRetry={() => { setFailed(false); setLoad(value => value + 1) }} />
      ) : null}
      <iframe
        key={load}
        ref={frame}
        className="exo-devices-frame"
        hidden={failed && shown}
        src={`${MOUSE_ORIGIN}/index.html`}
        title="Mouse"
        onLoad={() => { postToMouse({ event: 'room.visible', data: visible }); sendRail() }}
      />
    </div>
  )
}
