import { useDeferredValue, useEffect, useMemo, useRef, useState } from 'react'
import { Star } from '../../brand/icons'
import { host, type LucidChange, type LucidHeadphone, type LucidSettings, type LucidState } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Wait, sample, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'
import { Row, SliderRow, State, SwitchRow } from './parts'
import { ErrorNote } from '../ui/ErrorNote'
import { ActionButton } from '../ui/ConfirmButton'
import type { PlainError } from '../../lib/errorWords'

/**
 * Lucid, the headphone EQ that ships in Exo, set up from Exo's Devices › audio page (devices/AudioPage.tsx), in panels:
 * the app (its set up, Start with Exo, Repair, the output its EQ is on with Equalizer APO's device picker, its on/off
 * key), Game Mode, and its sounds and notifications; the headphone its EQ is tuned for is the page's one headphone pick,
 * and its Night Shift is devices/NightShiftRows.tsx. Lucid reads its settings only when it starts, so a change closes
 * it and starts it again out of sight. Lucid Voice is kept off; Exo Voice handles the mic.
 */
/** Lucid's state, read again when Windows' output changes (its id as output), since its EQ is on one output. */
export function useLucid(output?: string) {
  const [state, setState] = useState<LucidState | null>(null)
  const newest = useRef(0)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  useEffect(() => { host.lucid().then(setState, (cause: unknown) => report(cause, 'Lucid.state')) }, [output])
  // Its first set up takes as long as Windows' permission and Equalizer APO's installer do: read until it's done.
  const settingUp = state?.found === true && state.running && !state.ready
  useEffect(() => {
    if (!settingUp) return
    const again = window.setInterval(() => void host.lucid().then(setState, () => {}), 3000)
    return () => window.clearInterval(again)
  }, [settingUp])

  /**
   * A row's press, for its ActionButton: Lucid's answer with a message is its refusal (the button in Bad, the message
   * under the row through `failed`); the button's ring and check say the rest.
   */
  async function act(row: string, action: 'setup' | 'repair' | 'devices') {
    setBusy(row)
    setError(null)
    const answer = await host.lucid(action).then(next => {
      setState(next)
      return next.message ? { ok: false, message: next.message } : { ok: true }
    }, (cause: unknown) => { setBusy(null); throw cause })
    // After the call, not in a finally: Oxc's React Compiler skips a hook with a finally, and this one feeds every row.
    setBusy(null)
    return answer
  }
  const failed = (row: string) => (failure: PlainError) => setError({ row, text: failure.text })

  /** Shows the change at once; Lucid's own file has the last word when it lands. */
  function change(row: string, patch: LucidChange, shown: Partial<LucidSettings>) {
    const mine = ++newest.current
    setError(null)
    setState(current => current?.settings ? { ...current, settings: { ...current.settings, ...shown } } : current)
    host.lucid('change', { change: patch }).then(next => {
      // Only the newest change's answer: an older one would snap a quick second change back until its own answer lands.
      if (mine === newest.current) setState(next)
      if (next.message) setError({ row, text: next.message })
    }, (cause: unknown) => {
      report(cause, 'Lucid.change')
      setError({ row, text: "Lucid's settings didn't change. Try again." })
      void host.lucid().then(setState, () => {})
    })
  }
  const errorFor = (row: string) => error?.row === row ? error.text : null
  return { state, setState, busy, act, failed, change, errorFor }
}

export type Lucid = ReturnType<typeof useLucid>

/** What Set up will do on this PC, said before any of it runs. */
export const lucidSetupWords = (state: LucidState) => state.devices
  ? 'Windows may ask for permission.'
  : 'Downloads and installs Equalizer APO (12 MB). Windows asks for permission.'

/**
 * What the one Lucid row says: Lucid is part of Exo, so on it runs out of sight while Exo is open, starting and
 * stopping with it, with no window, tray icon or sign-in entry of its own; Repair is the one way to its own window.
 */
export function engineWords(state: Pick<LucidState, 'version' | 'startWithExo' | 'running' | 'held'>) {
  // The switch says on or off; words only for what it can't show.
  return state.held ? 'Restarts after your game' : state.startWithExo && !state.running ? 'Starting' : ''
}

/** The app itself: its set up, or its one row (on or off with Exo, and Repair, the one press that shows Lucid's own window), the output its EQ is on, and its on/off key. */
export function LucidAppRows({ lucid: l }: { lucid: Lucid }) {
  const state = l.state
  if (!state?.found) return null
  const repair = (
    <ActionButton className="exo-set-btn" disabled={l.busy !== null && l.busy !== 'repair'} context="Lucid.repair" fallback="Lucid didn't answer. Try again."
      why="none" onFailed={l.failed('repair')} onPress={() => l.act('repair', 'repair')}>Repair</ActionButton>
  )
  if (!state.ready) {
    return state.running ? (
      <Row label="Setting up Lucid" labelId="set-lucid-open" detail="Say yes when Windows asks, then choose your devices." error={l.errorFor('repair')}>
        <Spinner label="Setting up Lucid" />
        {repair}
      </Row>
    ) : (
      <Row label="Set up Lucid" labelId="set-lucid-open" detail={lucidSetupWords(state)} error={l.errorFor('setup')}>
        <ActionButton className="exo-set-btn" disabled={l.busy !== null && l.busy !== 'setup'} timeoutMs={null} context="Lucid.setup" fallback="Lucid didn't answer. Try again."
          why="none" onFailed={l.failed('setup')} onPress={() => l.act('setup', 'setup')}>Set up</ActionButton>
      </Row>
    )
  }
  const settings = state.settings
  return (
    <>
      <SwitchRow id="set-lucid-start" label="Lucid" detail={engineWords(state)} checked={state.startWithExo} disabled={false}
        onChange={on => host.lucid('set', { on }).then(next => { l.setState(next); return next.message ?? null }, () => "Lucid couldn't be changed. Try again.")}>
        {repair}
      </SwitchRow>
      {l.errorFor('repair') ? <ErrorNote className="exo-set-error" message={l.errorFor('repair')!} /> : null}
      {state.updateNote ? <ErrorNote className="exo-set-error" message={state.updateNote} /> : null}
      <EqRow lucid={l} />
      {settings ? <HotkeyRow hotkey={settings.hotkey} error={l.errorFor('hotkey')} onChange={hotkey => l.change('hotkey', { hotkey }, { hotkey })} /> : null}
    </>
  )
}

/**
 * Where Lucid's EQ plays and whether that's what you hear: Windows may play on a headset or a controller's speaker that
 * Equalizer APO isn't on. Choose devices opens Equalizer APO's picker.
 */
function EqRow({ lucid: l }: { lucid: Lucid }) {
  const state = l.state
  const eq = state?.eq
  if (!state || !eq && !state.devices) return null
  const detail = !eq?.on ? 'None' : eq.covered ? eq.on : `${eq.on}, not ${eq.playing ?? 'the output Windows plays on'}`
  return (
    <Row label="Lucid's EQ" labelId="set-lucid-eq" detail={detail} error={l.errorFor('devices')}>
      {eq?.covered && state.settings ? <State on={state.settings.eqOn}>{state.settings.eqOn ? 'On' : 'Off'}</State> : null}
      {state.devices ? (
        <ActionButton className="exo-set-btn" disabled={l.busy !== null && l.busy !== 'devices'} context="Lucid.devices" fallback="Lucid didn't answer. Try again."
          why="none" onFailed={l.failed('devices')} onPress={() => l.act('devices', 'devices')}>Choose devices</ActionButton>
      ) : null}
    </Row>
  )
}

/** A USB or wireless headset Windows plays on that Lucid has a tuning for, offered in one press. */
export function LucidGuessRow({ lucid: l, onPicked }: { lucid: Lucid; onPicked: (headphone: LucidHeadphone) => void }) {
  const guess = l.state?.found ? l.state.guess : null
  if (!guess) return null
  const use = () => {
    l.change('headphone', { headphone: guess.id }, { headphone: { id: guess.id, name: guess.name, chosen: true } })
    void host.lucidHeadphones().then(result => {
      const headphone = result.ok ? result.headphones.find(h => h.id === guess.id) : undefined
      if (headphone) onPicked(headphone)
    }, () => undefined)
  }
  return (
    <Row label={`Looks like ${guess.name}`} labelId="set-lucid-guess">
      <button type="button" className="exo-set-btn" disabled={l.busy !== null} onClick={use}>Use it</button>
    </Row>
  )
}

/** Lucid's EQ switches to the tuning of the headset Windows moves to; a pick by hand stands until then. */
export function LucidFollowRow({ lucid: l }: { lucid: Lucid }) {
  const state = l.state
  if (!state?.found || !state.settings) return null
  return (
    <SwitchRow id="set-lucid-follow" label="Switch EQ with my headset" checked={state.followHeadset} disabled={false}
      onChange={on => host.lucid('follow', { on }).then(next => { l.setState(next); return next.message ?? null }, () => "Lucid couldn't be changed. Try again.")} />
  )
}

/** The headphone Lucid's EQ is tuned for: the page's one headphone pick (devices/AudioPage.tsx). */
export function LucidHeadphoneRow({ lucid: l, label, onPicked }: { lucid: Lucid; label: string; onPicked: (headphone: LucidHeadphone) => void }) {
  const settings = l.state?.found ? l.state.settings : null
  if (!settings) return null
  return (
    <HeadphoneRow lucid={settings} label={label} error={l.errorFor('headphone')}
      onPick={h => { l.change('headphone', { headphone: h.id }, { headphone: { id: h.id, name: h.model, chosen: true } }); onPicked(h) }}
      onFavorite={(id, on) => l.change('headphone', { favorites: { [id]: on } },
        { favorites: on ? [...settings.favorites, id] : settings.favorites.filter(f => f.toLowerCase() !== id.toLowerCase()) })} />
  )
}

/** Game Mode: each game its own EQ, and how it behaves around the game. */
export function LucidGameRows({ lucid: l }: { lucid: Lucid }) {
  const settings = l.state?.found ? l.state.settings : null
  if (!settings) return null
  return (
    <>
      <LucidSwitch id="set-lucid-game" label="Game Mode" error={l.errorFor('gameMode')}
        checked={settings.gameMode} onChange={on => l.change('gameMode', { gameMode: on }, { gameMode: on })} />
      {settings.gameMode ? (
        <>
          <LucidSwitch id="set-lucid-alt" label="Keep the game's EQ on Alt+Tab" error={l.errorFor('keepOnAltTab')}
            checked={settings.keepOnAltTab} onChange={on => l.change('keepOnAltTab', { keepOnAltTab: on }, { keepOnAltTab: on })} />
          <LucidSwitch id="set-lucid-only" label="Only while gaming" error={l.errorFor('onlyWhileGaming')}
            checked={settings.onlyWhileGaming} onChange={on => l.change('onlyWhileGaming', { onlyWhileGaming: on }, { onlyWhileGaming: on })} />
        </>
      ) : null}
    </>
  )
}

/** Lucid's cue sounds and notifications, and its line in your Discord status. */
export function LucidAlertRows({ lucid: l }: { lucid: Lucid }) {
  const settings = l.state?.found ? l.state.settings : null
  if (!settings) return null
  return (
    <>
      <LucidSwitch id="set-lucid-notify" label="Lucid's notifications" error={l.errorFor('notifications')}
        checked={settings.notifications} onChange={on => l.change('notifications', { notifications: on }, { notifications: on })} />
      {settings.notifications ? (
        <>
          <SliderRow label="Switch sound" min={0} max={100} step={5} value={settings.toggleSound} render={soundWord} error={l.errorFor('toggleSound')}
            onCommit={toggleSound => l.change('toggleSound', { toggleSound }, { toggleSound })} />
          <SliderRow label="Game sound" min={0} max={100} step={5} value={settings.detectSound} render={soundWord} error={l.errorFor('detectSound')}
            onCommit={detectSound => l.change('detectSound', { detectSound }, { detectSound })} />
        </>
      ) : null}
      <LucidSwitch id="set-lucid-discord" label="Show Lucid in Discord" error={l.errorFor('discord')}
        checked={settings.discord} onChange={on => l.change('discord', { discord: on }, { discord: on })} />
    </>
  )
}

function LucidSwitch({ id, label, checked, error, onChange }: {
  id: string; label: string; checked: boolean; error: string | null; onChange: (on: boolean) => void
}) {
  return (
    <Row label={label} labelId={id} error={error}>
      <Switch checked={checked} labelledBy={id} onChange={onChange} />
    </Row>
  )
}

/** The headphone Lucid tunes for, and its list to pick from: search, favorites first, then each kind. */
function HeadphoneRow({ lucid, label, error, onPick, onFavorite }: {
  lucid: LucidSettings; label: string; error: string | null; onPick: (headphone: LucidHeadphone) => void; onFavorite: (id: string, on: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const [list, setList] = useState<LucidHeadphone[] | null>(null)
  const [listError, setListError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  // The field takes each key at once; Lucid's whole list follows behind it.
  const search = useDeferredValue(query)
  const field = useRef<HTMLInputElement>(null)

  // Read when the picker opens; closing and opening it again is the retry after a failed read.
  useEffect(() => {
    if (!open || list) return
    host.lucidHeadphones().then(result => {
      if (result.ok) { setList(result.headphones); setListError(null) }
      else setListError("Lucid's headphone list couldn't be read. Try again.")
    }, (cause: unknown) => { report(cause, 'LucidSection.headphones'); setListError("Lucid's headphone list couldn't be read. Try again.") })
  }, [open, list])
  useEffect(() => { if (open) field.current?.focus() }, [open])

  // useMemo although this component compiles: a try/finally added here would silently stop the compiler, and Lucid's
  // whole list would be filtered again on every render.
  const favorites = useMemo(() => new Set(lucid.favorites.map(id => id.toLowerCase())), [lucid.favorites])
  const groups = useMemo(() => {
    if (!list) return []
    const words = search.trim().toLowerCase().split(/\s+/).filter(Boolean)
    const hits = list.filter(h => words.every(word => `${h.brand} ${h.model} ${h.keywords}`.toLowerCase().includes(word)))
    const byKind = new Map<string, LucidHeadphone[]>()
    const starred = hits.filter(h => favorites.has(h.id.toLowerCase()))
    if (starred.length && !words.length) byKind.set('Favorites', starred)
    // Each kind's list grows in place: copying it per headphone made the search quadratic.
    for (const h of hits) {
      const kind = byKind.get(h.category)
      if (kind) kind.push(h)
      else byKind.set(h.category, [h])
    }
    return [...byKind]
  }, [list, search, favorites])

  return (
    <div className="exo-set-item">
      <div className="exo-set-item-main">
        <div className="exo-set-item-copy">
          <span id="set-lucid-headphone" className="exo-set-item-label">{label}</span>
          {lucid.headphone.chosen ? <span className="exo-set-item-detail">{lucid.headphone.name}</span> : null}
        </div>
        <div className="exo-set-item-control">
          <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Done' : 'Change'}</button>
        </div>
      </div>
      {open ? (
        <div className="exo-lucid-picker">
          <input ref={field} type="search" className="exo-field" value={query} placeholder="Search headphones" aria-label="Search headphones"
            onChange={event => setQuery(event.target.value)} />
          {listError ? <ErrorNote className="exo-set-error" message={listError} /> : !list ? (
            <Wait label="Reading Lucid's list">
              <div className="exo-lucid-list">
                <h3 className="exo-lucid-kind">In-ear</h3>
                {times(5, i => <div className="exo-lucid-headphone"><span className="exo-lucid-pick"><span>{sample(i)}</span><small>{sample(i + 2)}</small></span></div>)}
              </div>
            </Wait>
          ) : (
            <div className="exo-lucid-list" role="listbox" aria-labelledby="set-lucid-headphone">
              {groups.length === 0 ? <p className="exo-lucid-empty">No headphone by that name</p> : groups.map(([kind, items]) => (
                <div key={kind} role="group" aria-label={kind}>
                  <h3 className="exo-lucid-kind">{kind}</h3>
                  {items.map(h => {
                    const selected = h.id === lucid.headphone.id
                    const starred = favorites.has(h.id.toLowerCase())
                    return (
                      <div key={`${kind}-${h.id}`} className={`exo-lucid-headphone${selected ? ' is-current' : ''}`}>
                        <button type="button" role="option" aria-selected={selected} className="exo-lucid-pick" onClick={() => { if (!selected) onPick(h) }}>
                          <span>{h.model}</span>
                          <small>{h.brand ? `${h.brand} · ${h.grade}` : h.grade}</small>
                        </button>
                        {h.id === 'lucid_iems' ? null : (
                          <button type="button" className={`exo-lucid-star${starred ? ' is-on' : ''}`} aria-pressed={starred}
                            aria-label={`${starred ? 'Remove' : 'Add'} ${h.model} ${starred ? 'from' : 'to'} favorites`} onClick={() => onFavorite(h.id, !starred)}>
                            <Star size={14} filled={starred} />
                          </button>
                        )}
                      </div>
                    )
                  })}
                </div>
              ))}
            </div>
          )}
        </div>
      ) : null}
      {error ? <ErrorNote className="exo-set-error" message={error} /> : null}
    </div>
  )
}

type Hotkey = LucidSettings['hotkey']

// Windows' modifier bits, as Lucid stores them, in the order a shortcut is written.
const MODIFIERS: Array<[number, string]> = [[2, 'Ctrl'], [1, 'Alt'], [4, 'Shift'], [8, 'Win']]

/** "Ctrl+Alt+L", "F11"; null for no key. */
function hotkeyName({ modifiers, key }: Hotkey): string | null {
  if (key === 0) return null
  const name = key >= 112 && key <= 135 ? `F${key - 111}`
    : key >= 48 && key <= 57 || key >= 65 && key <= 90 ? String.fromCharCode(key)
      : key >= 96 && key <= 105 ? `Num ${key - 96}`
        : key === 32 ? 'Space' : `Key ${key}`
  return [...MODIFIERS.filter(([bit]) => (modifiers & bit) !== 0).map(([, word]) => word), name].join('+')
}

/** The key the browser reports, as Windows' virtual key and modifier bits; null for a modifier pressed alone. */
function hotkeyOf(event: KeyboardEvent): Hotkey | null {
  if (['Control', 'Alt', 'Shift', 'Meta', 'OS'].includes(event.key) || event.keyCode === 0) return null
  const modifiers = (event.ctrlKey ? 2 : 0) | (event.altKey ? 1 : 0) | (event.shiftKey ? 4 : 0) | (event.metaKey ? 8 : 0)
  return { modifiers, key: event.keyCode }
}

/**
 * Lucid's on/off key. It holds the key for the whole PC while Lucid runs, so a key alone (Lucid's F11) never reaches a
 * browser's full screen or a game: Change takes the next keys pressed, Escape keeps the old one, Clear leaves none.
 */
function HotkeyRow({ hotkey, error, onChange }: { hotkey: Hotkey; error: string | null; onChange: (hotkey: Hotkey) => void }) {
  const [listening, setListening] = useState(false)
  useEffect(() => {
    if (!listening) return
    const onKey = (event: KeyboardEvent) => {
      event.preventDefault()
      event.stopPropagation()
      if (event.key === 'Escape') { setListening(false); return }
      const next = hotkeyOf(event)
      if (!next) return
      setListening(false)
      if (next.key !== hotkey.key || next.modifiers !== hotkey.modifiers) onChange(next)
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [listening, hotkey, onChange])
  const name = hotkeyName(hotkey)
  const detail = listening ? 'Press the keys' : name ?? 'None'
  return (
    <Row label="On/off key" labelId="set-lucid-hotkey" detail={detail} error={error}>
      {name && !listening ? <button type="button" className="exo-set-btn" onClick={() => onChange({ modifiers: 0, key: 0 })}>Clear</button> : null}
      <button type="button" className="exo-set-btn" aria-pressed={listening} onClick={() => setListening(on => !on)}>{listening ? 'Cancel' : 'Change'}</button>
    </Row>
  )
}

/** One of Lucid's cue sounds: 0 is off. */
const soundWord = (value: number) => value === 0 ? 'Off' : `${value}%`
