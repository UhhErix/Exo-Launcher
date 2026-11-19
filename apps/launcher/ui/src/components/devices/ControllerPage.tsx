import { useEffect, useRef, useState, type ReactNode } from 'react'
import { host, onHostEvent, type ButtonCheck, type TriggerSweep, type ControllerCalibration, type ControllerPolling, type ControllerRead, type ControllerSlot, type ControllerState, type ControllerTested, type Peripheral, type StickEdge, type StickRest } from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/cn'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { CalibrationRows } from './Calibration'
import { DevicePage, DevicePanel as Panel } from './DevicePage'
import { GameProfileRows } from './GameProfiles'
import { ControllerSoundRow, PadSettingsRows } from './PadSettings'
import { portWords } from './ports'

/**
 * A controller's page (DESIGN.md › Devices): the Controller Lab, on one screen. The hero holds its picture, its link, and
 * as figures its charge, polling rate, color and firmware. Beside it, in columns sized so none scrolls: its sticks live
 * with the last stick test and on a DualSense or Edge the controller's own calibration; its triggers with how far each
 * has gone; an Edge's back and Fn buttons and the profiles it holds (read, not edited); how it reports (measured once as
 * the page opens); game profiles; its light and sound; what Exo remembers for it; the USB port it's on; and what sits
 * between it and games. Live readings run only while the page shows. What changes a controller does so over USB, never
 * during a game, and can be undone.
 */
export function ControllerPage({ card, name, status, art, visible }: { card: Peripheral; name: string; status: string; art: ReactNode; visible: boolean }) {
  const [loaded, setLoaded] = useState<{ id: string; read: ControllerRead } | null>(null)
  const [readAt, setReadAt] = useState(0)
  const live = useLive(card.id, visible)
  const polling = usePolling(card.id, visible)
  // Read again when its color arrives: its serial has read, and what Exo remembers for it is known now.
  useEffect(() => {
    let current = true
    host.controllerRead(card.id).then(next => { if (current) setLoaded({ id: card.id, read: next }) }, (error: unknown) => report(error, 'ControllerPage.read'))
    return () => { current = false }
  }, [card.id, card.color, readAt])
  // Never another controller's answer, while this one's is on its way.
  const read = loaded?.id === card.id ? loaded.read : null
  const edge = card.model === 'dualsense-edge'
  const sony = edge || card.model === 'dualsense'
  const usb = card.connection === 'usb'
  const firmware = read?.firmware
  return (
    <DevicePage name={name} status={status} art={art} stats={heroStats(card, firmware, live, polling.result)}>
      <Panel title="Sticks">
        <Sticks id={card.id} live={live} tested={read?.tested ?? null} />
        {sony ? <CalibrationRows id={card.id} edge={edge} usb={usb} live={live} state={read?.calibration ?? undefined} onChanged={() => setReadAt(n => n + 1)} /> : null}
      </Panel>
      <Panel title="Triggers"><Triggers id={card.id} live={live} edge={edge} /></Panel>
      <Panel title="Buttons"><Buttons id={card.id} /></Panel>
      {edge ? <EdgePanels live={live} slots={read?.slots ?? null} /> : null}
      <Panel title="Polling"><Polling polling={polling} /></Panel>
      {sony ? <Panel title="Game profiles"><GameProfileRows /></Panel> : null}
      {read ? <ReadPanels id={card.id} read={read} sony={sony} usb={usb} /> : null}
      {card.port ? <Panel title="USB port"><PortRows port={card.port} /></Panel> : null}
      {firmware?.current === false ? (
        <Panel title="Firmware">
          <Row label={`Firmware ${firmware.latest} is out`}>
            <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('https://www.playstation.com/support/hardware/ps5-controller-update/')}>How to update</button>
          </Row>
        </Panel>
      ) : null}
      {read?.layers ? <Panel title="Between it and your games"><LayerRows layers={read.layers} /></Panel> : null}
    </DevicePage>
  )
}

/** The hero's figures, as Exo Mouse's: its charge, measured rate, color and firmware; what it doesn't say isn't drawn. */
function heroStats(card: Peripheral, firmware: ControllerRead['firmware'], live: ControllerState | null, polling: ControllerPolling | null) {
  const charge = card.battery != null ? Math.round(card.battery * 100) : live?.battery ?? null
  return [
    { label: 'Battery', value: charge != null ? <Charge percent={charge} charging={live?.charging === true} /> : null },
    { label: 'Polling', value: polling ? `${polling.changes ? 'At least ' : ''}${polling.rate} Hz` : null },
    { label: 'Color', value: card.color ?? null },
    { label: 'Firmware', value: firmware?.version ?? null },
  ]
}

/** An Edge's own: its back and Fn buttons, and the profiles it holds. */
function EdgePanels({ live, slots }: { live: ControllerState | null; slots: ControllerSlot[] | null }) {
  return (
    <>
      <Panel title="Back and Fn buttons"><PadKeys extras={live?.extras ?? 0} /></Panel>
      {slots ? <Panel title="Profiles on the controller"><SlotRows slots={slots} active={live?.profile ?? null} /></Panel> : null}
    </>
  )
}

/** What waits on controller.read: its light and sound, shown as saved from the first frame they show, and what Exo remembers. */
function ReadPanels({ id, read, sony, usb }: { id: string; read: ControllerRead; sony: boolean; usb: boolean }) {
  return (
    <>
      <Panel title="Lights and sound">
        {sony ? <PadSettingsRows key={`${id}:${JSON.stringify(read.settings)}`} id={id} usb={usb} settings={read.settings} /> : null}
        <ControllerSoundRow on={read.sound} />
      </Panel>
      {sony ? <Panel title="What Exo remembers"><MemoryRows read={read} usb={usb} /></Panel> : null}
    </>
  )
}

/** Its charge as Exo Mouse draws a mouse's: Windows doesn't know a DualSense's; the controller says it in every report (in tens). */
function Charge({ percent, charging }: { percent: number; charging: boolean }) {
  return (
    <>
      {percent}%{charging && percent < 100 ? <small> charging</small> : null}
      <span className="exo-device-stat-gauge" aria-hidden="true"><span style={{ width: `${percent}%` }} /></span>
    </>
  )
}

/**
 * The controller's state, at most 60 a second, only while its page is on screen, focused or not (Devices stays
 * mounted behind other rooms). The host stops a live view after ten minutes; it is asked again every nine. The host
 * sends what it reads whether or not anything moved, so a state like the last draws nothing.
 */
function useLive(id: string, visible: boolean) {
  const [live, setLive] = useState<ControllerState | null>(null)
  useEffect(() => {
    if (!visible) return
    const off = onHostEvent('controller.state', state => { if (state.id === id) setLive(last => sameState(last, state) ? last : state) })
    const watch = () => void host.controllerWatch(id, true).catch((error: unknown) => report(error, 'ControllerPage.watch', { level: 'warn' }))
    watch()
    const again = window.setInterval(watch, 9 * 60_000)
    return () => {
      off()
      window.clearInterval(again)
      void host.controllerWatch(id, false).catch(() => { })
    }
  }, [id, visible])
  return live
}

const LIVE_FIELDS = ['id', 'lx', 'ly', 'rx', 'ry', 'l2', 'r2', 'profile', 'stopL', 'stopR', 'battery', 'charging', 'extras'] as const satisfies readonly (keyof ControllerState)[]
const sameState = (last: ControllerState | null, next: ControllerState) => last !== null && LIVE_FIELDS.every(field => last[field] === next[field])

/** A stick's place as a dot in its circle, 0..255 on each axis. */
function StickView({ x, y, label }: { x: number | undefined; y: number | undefined; label: string }) {
  const cx = x == null ? 50 : 8 + (x / 255) * 84
  const cy = y == null ? 50 : 8 + (y / 255) * 84
  return (
    <svg className="exo-stick" viewBox="0 0 100 100" role="img" aria-label={label}>
      <circle className="exo-stick-gate" cx="50" cy="50" r="42" />
      <line className="exo-stick-axis" x1="8" y1="50" x2="92" y2="50" />
      <line className="exo-stick-axis" x1="50" y1="8" x2="50" y2="92" />
      <circle className="exo-stick-dot" cx={cx} cy={cy} r="5" />
    </svg>
  )
}

/** A stick's axis as % of its half travel from its true center, signed: what it reads at rest is its drift. */
const axis = (value: number | undefined) => {
  if (value == null) return '–'
  const percent = ((value - 127.5) / 127.5) * 100
  return `${percent >= 0 ? '+' : '−'}${Math.abs(percent).toFixed(1)}`
}

function StickReading({ x, y, label }: { x: number | undefined; y: number | undefined; label: string }) {
  return (
    <figure className="exo-stick-read">
      <StickView x={x} y={y} label={label} />
      <figcaption>X {axis(x)}% · Y {axis(y)}%</figcaption>
    </figure>
  )
}

type Step = 'idle' | 'rest' | 'edge'
const STOPPED = 'The test stopped. Try again.'
type Result = { rest?: { left: StickRest | null; right: StickRest | null }; edge?: { left: StickEdge | null; right: StickEdge | null } }

const resultOf = (tested: ControllerTested | null): Result => tested ? {
  rest: tested.restLeft || tested.restRight ? { left: tested.restLeft, right: tested.restRight } : undefined,
  edge: tested.edgeLeft || tested.edgeRight ? { left: tested.edgeLeft, right: tested.edgeRight } : undefined,
} : {}

const dayOf = (at: string) => new Date(at).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })

/** The stick test: three seconds at rest (center and wander), then six rolled around the edge (reach and roundness). The last one kept with the controller shows until it's run again. */
function Sticks({ id, live, tested }: { id: string; live: ControllerState | null; tested: ControllerTested | null }) {
  const [step, setStep] = useState<Step>('idle')
  const [ran, setRan] = useState<Result | null>(null)
  const result = ran ?? resultOf(tested)
  const setResult = setRan
  const [error, setError] = useState<string | null>(null)
  const running = useRef(false)
  async function run() {
    if (running.current) return
    running.current = true
    setError(null)
    setResult({})
    try {
      setStep('rest')
      const rest = await host.controllerTest(id, 'rest', 5)
      if (!rest.ok) throw new Error(rest.message ?? STOPPED)
      setResult({ rest: { left: rest.left as StickRest | null, right: rest.right as StickRest | null } })
      setStep('edge')
      const edge = await host.controllerTest(id, 'edge', 6)
      if (!edge.ok) throw new Error(edge.message ?? STOPPED)
      // A stick the host has no edge for (null) wasn't rolled far enough round to measure.
      setResult(current => ({ ...current ?? {}, edge: { left: (edge.left ?? null) as StickEdge | null, right: (edge.right ?? null) as StickEdge | null } }))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : STOPPED)
    } finally {
      running.current = false
      setStep('idle')
    }
  }
  const asked = step === 'rest' ? 'Leave both sticks alone' : step === 'edge' ? 'Roll both sticks slowly around their edge, a few times' : null
  return (
    <>
      <div className="exo-sticks">
        <StickReading x={live?.lx} y={live?.ly} label="Left stick" />
        <StickReading x={live?.rx} y={live?.ry} label="Right stick" />
      </div>
      <Row label={asked ?? 'Stick test'} detail={!asked && !ran && tested ? `Last run ${dayOf(tested.at)}` : undefined} error={error}>
        <button type="button" className="exo-set-btn" disabled={step !== 'idle'} onClick={() => void run()}>
          {step !== 'idle' ? <Spinner label="Testing the sticks" /> : null}{result.edge ? 'Test again' : 'Test'}
        </button>
      </Row>
      {result.rest || result.edge ? (
        <>
          <StickRow label="Left stick" rest={result.rest?.left} edge={result.edge?.left} />
          <StickRow label="Right stick" rest={result.rest?.right} edge={result.edge?.right} />
        </>
      ) : null}
    </>
  )
}

const sentence = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)

/** The least deadzone that hides a stick's rest: how far its center is off plus how far it wanders, rounded up. */
const deadzoneFor = (rest: StickRest) => Math.ceil(Math.max(Math.abs(rest.offsetX), Math.abs(rest.offsetY)) + (rest.noise / 127.5) * 100)

/** One stick's numbers and a verdict: centered within 2%, wandering 2 steps or less, reaching 97%, round within 6%. */
function StickRow({ label, rest, edge }: { label: string; rest?: StickRest | null; edge?: StickEdge | null }) {
  const parts: string[] = []
  let good = true
  if (rest) {
    const off = Math.max(Math.abs(rest.offsetX), Math.abs(rest.offsetY))
    parts.push(`center ${off.toFixed(1)}% off`, `wanders ${rest.noise} ${rest.noise === 1 ? 'step' : 'steps'}`, `a ${deadzoneFor(rest)}% deadzone hides it`)
    if ((rest.creep ?? 0) >= 1) parts.push(`crept ${rest.creep}% while left alone`)
    good &&= off <= 2 && rest.noise <= 2 && (rest.creep ?? 0) < 1
  }
  if (edge === null || (edge && edge.covered < 0.75)) parts.push('roll it all the way around to measure its edge')
  else if (edge) {
    parts.push(`reaches ${edge.reach}%`, `${edge.circularity}% out of round`)
    good &&= edge.reach >= 97 && edge.circularity <= 6
  }
  return (
    <Row label={label} detail={sentence(parts.join(' · '))}>
      {edge && edge.covered >= 0.75 ? <State on={good}>{good ? 'Good' : 'Needs calibration'}</State> : null}
    </Row>
  )
}

const share = (value: number) => Math.round((value / 255) * 100)

/**
 * The triggers live, and how far each has gone since the page opened: an Edge's stop sliders shorten the travel, and this
 * shows what's left. An Edge says where each slider sits as a number; which end is which isn't documented, so that's what shows.
 */
function Triggers({ id, live, edge }: { id: string; live: ControllerState | null; edge: boolean }) {
  const [most, setMost] = useState({ l2: 0, r2: 0 })
  const [swept, setSwept] = useState<{ left: TriggerSweep | null; right: TriggerSweep | null } | null>(null)
  const [testing, setTesting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function test() {
    setTesting(true)
    setError(null)
    try {
      const answer = await host.controllerQuality(id, 'triggers', 6)
      if (answer.ok) setSwept({ left: answer.left ?? null, right: answer.right ?? null })
      else setError(answer.message ?? STOPPED)
    } catch (cause) {
      report(cause, 'ControllerPage.triggers')
      setError(STOPPED)
    } finally {
      setTesting(false)
    }
  }
  const l2 = live?.l2 ?? 0
  const r2 = live?.r2 ?? 0
  if (l2 > most.l2 || r2 > most.r2) setMost({ l2: Math.max(l2, most.l2), r2: Math.max(r2, most.r2) })
  const row = (label: string, value: number, furthest: number, sweep?: TriggerSweep | null) => (
    <Row label={label} detail={sweepWords(sweep, swept !== null) ?? (furthest ? `Pulled to ${share(furthest)}%` : 'Pull it to see its range')}>
      <span className="exo-trigger-read">
        {share(value)}%
        <div className="exo-trigger" role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={255} aria-valuenow={value}>
          <span style={{ transform: `translateX(${share(value) - 100}%)` }} />
        </div>
      </span>
    </Row>
  )
  return (
    <>
      {row('L2', l2, most.l2, swept?.left)}
      {row('R2', r2, most.r2, swept?.right)}
      <Row label={testing ? 'Pull both triggers slowly all the way, a few times' : 'Trigger test'} error={error}>
        <button type="button" className="exo-set-btn" disabled={testing} onClick={() => void test()}>
          {testing ? <Spinner label="Testing the triggers" /> : null}{swept ? 'Test again' : 'Test'}
        </button>
      </Row>
      {edge && live?.stopL != null && live.stopR != null ? <Row label="Stop sliders"><State>L2 {live.stopL + 1} · R2 {live.stopR + 1} of 3</State></Row> : null}
    </>
  )
}

/** A trigger's sweep in words: how far it went and how many values it sent, at least, since a quick pull skips some. */
function sweepWords(sweep: TriggerSweep | null | undefined, tested: boolean): string | null {
  if (!tested) return null
  if (!sweep) return 'Pull it all the way to measure it'
  const reach = sweep.full ? 'full travel' : `${sweep.low}% to ${sweep.high}%, short of full travel`
  return `At least ${sweep.steps} of 256 steps · ${reach}`
}

/** Press every button a few times: real presses, and bounces a finger can't make. A bounce quicker than a report can't be seen, so no bounce is "none seen". */
function Buttons({ id }: { id: string }) {
  const [check, setCheck] = useState<ButtonCheck | null>(null)
  const [testing, setTesting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function test() {
    setTesting(true)
    setError(null)
    try {
      const answer = await host.controllerQuality(id, 'buttons', 8)
      if (answer.ok && answer.buttons) setCheck(answer.buttons)
      else setError(answer.message ?? STOPPED)
    } catch (cause) {
      report(cause, 'ControllerPage.buttons')
      setError(STOPPED)
    } finally {
      setTesting(false)
    }
  }
  const bouncing = check?.buttons.filter(b => b.bounces > 0) ?? []
  const presses = check?.buttons.reduce((all, b) => all + b.presses, 0) ?? 0
  return (
    <>
      <Row label={testing ? 'Press every button a few times, firmly' : 'Button test'} error={error}>
        <button type="button" className="exo-set-btn" disabled={testing} onClick={() => void test()}>
          {testing ? <Spinner label="Testing the buttons" /> : null}{check ? 'Test again' : 'Test'}
        </button>
      </Row>
      {check ? (
        presses === 0 ? <Row label="No presses read" detail="Press its buttons during the test" /> : (
          <>
            <Row label={bouncing.length ? 'Chatter' : 'No chatter seen'} detail={`${presses} presses on ${check.buttons.length} ${check.buttons.length === 1 ? 'button' : 'buttons'} · Quicker than a report (${check.reportMs} ms) can't be seen`}>
              <State on={bouncing.length === 0}>{bouncing.length ? `${bouncing.length} ${bouncing.length === 1 ? 'button' : 'buttons'}` : 'Clean'}</State>
            </Row>
            {bouncing.map(b => <Row key={b.name} label={b.name} detail={`Bounced ${b.bounces} ${b.bounces === 1 ? 'time' : 'times'} in ${b.presses} presses, as quick as ${b.shortest} ms`} />)}
          </>
        )
      ) : null}
    </>
  )
}

const PAD_KEYS = [
  { bit: 0x40, label: 'Left back' }, { bit: 0x80, label: 'Right back' },
  { bit: 0x10, label: 'Left Fn' }, { bit: 0x20, label: 'Right Fn' },
] as const

/** An Edge's own buttons, lit while held: a quick check that each one works. What a back button is mapped to lives in its profile, in a format nobody documents. */
function PadKeys({ extras }: { extras: number }) {
  return (
    <div className="exo-pad-keys">
      {PAD_KEYS.map(key => <span key={key.bit} className={cn('exo-pad-key', (extras & key.bit) !== 0 && 'is-held')}>{key.label}</span>)}
    </div>
  )
}

/** What an Edge holds in its four slots, as it says, and which one is on. Read only: its profiles are made on a PS5 or in Sony's app. */
function SlotRows({ slots, active }: { slots: ControllerSlot[]; active: number | null }) {
  return (
    <div className="exo-pad-keys">
      {slots.map(slot => (
        <span key={slot.number} className={cn('exo-pad-key', active === slot.number && 'is-on')}>
          {slot.empty ? `Slot ${slot.number} empty` : slot.name ?? `Slot ${slot.number}`}
        </span>
      ))}
    </div>
  )
}

/** Whether Exo sets any of its light, sound and vibration, or leaves them to the controller. */
const changed = ({ settings: s }: ControllerRead) => !!s && (s.light !== null || s.playerLight || s.micOff || s.speakerOff || s.vibration !== 'full' || s.triggerEffects !== 'full')

/** Only what's true: what Exo keeps for this controller, whether the controller holds it now, and when it last went back. */
function MemoryRows({ read, usb }: { read: ControllerRead; usb: boolean }) {
  const calibration: ControllerCalibration | null | undefined = read.calibration
  if (!read.remembered) return <Row label={usb ? 'Reading which controller this is' : 'Connect it with a cable'} />
  const when = calibration?.putBack ? new Date(calibration.putBack) : null
  return (
    <>
      <Row label="Light, sound and vibration">
        {changed(read) ? <State on>Saved</State> : <State>The controller’s own</State>}
      </Row>
      <Row label="Calibration">
        {!calibration?.calibrated ? <State>Not calibrated</State>
          : calibration.holds === true ? <State on>On the controller</State>
          : calibration.holds === false ? <State>Goes back when it reconnects</State>
          : <State on>Saved</State>}
      </Row>
      {when ? <Row label="Last put back"><State>{when.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}, {when.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</State></Row> : null}
    </>
  )
}

type PollingRun = { result: ControllerPolling | null; busy: boolean; error: string | null; measure: () => Promise<void> }

/** How it reports, over three seconds: measured once when the page first shows, and when asked. Nothing runs while it's away. */
function usePolling(id: string, visible: boolean): PollingRun {
  const [result, setResult] = useState<ControllerPolling | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const asked = useRef<string | null>(null)
  async function measure() {
    setBusy(true)
    setError(null)
    try {
      const answer = await host.controllerMeasure(id, 3)
      if (answer.ok && answer.polling) setResult(answer.polling)
      else setError(answer.message ?? 'Exo couldn’t measure it. Try again.')
    } catch (cause) {
      report(cause, 'ControllerPage.measure')
      setError('Exo couldn’t measure it. Try again.')
    } finally {
      setBusy(false)
    }
  }
  useEffect(() => {
    if (!visible || asked.current === id) return
    asked.current = id
    void measure()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once per controller, as the page first shows
  }, [id, visible])
  return { result, busy, error, measure }
}

/** Its rate, fresh readings, the slowest gaps, jitter and repeats. */
function Polling({ polling: run }: { polling: PollingRun }) {
  const { result: polling, busy, error, measure } = run
  return (
    <>
      <Row label="Polling rate" detail={polling?.changes ? 'Keep a stick moving while it measures' : undefined} error={error}>
        {polling ? <State on>{polling.changes ? 'At least ' : ''}{polling.rate} Hz</State> : null}
        <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void measure()}>{busy ? <Spinner label="Measuring" /> : null}{polling ? 'Again' : 'Measure'}</button>
      </Row>
      {polling ? (
        <>
          {polling.changes ? null : <Row label="Slowest 1%"><State>{polling.p99} ms</State></Row>}
          {polling.repeats != null ? <Row label="Repeats"><State on={polling.repeats < 5}>{polling.repeats}%</State></Row> : null}
        </>
      ) : null}
    </>
  )
}

/** Where it meets the PC. */
function PortRows({ port }: { port: NonNullable<Peripheral['port']> }) {
  return (
    <>
      <Row label="Port"><State on={port.where === 'cpu' && port.hubs === 0}>{portWords(port)}</State></Row>
      {port.interrupts ? <Row label="Interrupts"><State on={port.interrupts === 'msi'}>{port.interrupts === 'msi' ? 'MSI' : 'Shared line'}</State></Row> : null}
      {port.better === 'free' ? <Row label="Use a free port on the processor’s own USB" /> : null}
      {port.better === 'hub' ? <Row label="Plug it straight into the PC, not into a hub" /> : null}
    </>
  )
}

/**
 * What sits between the controller and games: Steam Input (Exo's one switch for it: games with their own PlayStation
 * support read the controller themselves; the others keep Steam's translation), then remappers and their drivers and a
 * polling driver, found and never changed here.
 */
function LayerRows({ layers }: { layers: NonNullable<ControllerRead['layers']> }) {
  return (
    <>
      <SteamInputRow />
      {layers.tools.map(tool => (
        <Row key={tool.name} label={tool.name}>
          <State on={!tool.running}>{tool.running ? 'Running' : 'Not running'}</State>
        </Row>
      ))}
      {layers.pollingDriver ? (
        <Row label="Polling driver">
          <State>{layers.memoryIntegrity ? 'Blocked by Memory Integrity' : 'Installed'}</State>
        </Row>
      ) : null}
      {layers.tools.length === 0 && !layers.pollingDriver && layers.steamPlayStation === null ? <Row label="Nothing in the way" /> : null}
    </>
  )
}

/**
 * Steam Input, decided per Steam game with no choice to make: off where the game reads any controller itself, kept where
 * the game needs it to work with one (Exo sets it when Steam next closes, or closes Steam for a moment).
 */
function SteamInputRow() {
  const [state, setState] = useState<Awaited<ReturnType<typeof host.steamInputState>> | null>(null)
  useEffect(() => {
    let current = true
    host.steamInputState().then(next => { if (current) setState(next) }, (error: unknown) => report(error, 'ControllerPage.steamInput', { level: 'warn' }))
    return () => { current = false }
  }, [])
  if (!state?.steam) return null
  return (
    <Row label="Games read it themselves" detail={state.translated ? `${state.translated} keep Steam Input` : undefined}>
      <State on>{state.native} {state.native === 1 ? 'game' : 'games'}</State>
    </Row>
  )
}
