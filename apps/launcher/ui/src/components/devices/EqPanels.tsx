import { useCallback, useEffect, useRef, useState } from 'react'
import { Search } from '../../brand/icons'
import { host, type EqAction, type EqHeadphone, type EqSound, type EqState } from '../../lib/host'
import { errorText } from '../../lib/errorWords'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { ChoiceRow, Row, SliderRow, State, SwitchRow } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { Wait, sample, times } from '../ui/Wait'
import { DevicePanel as Panel } from './DevicePage'
import type { Wearing } from './AudioPage'

/**
 * Exo EQ on your audio's page: Exo's own headphone EQ, written for Equalizer APO, which plays it the moment it changes.
 * Panels: Equalizer APO (its install and anything keeping it from this output), Headphones (AutoEq's correction for the
 * pair, which also names the hero's card), Sound (the switch, Compare, the sound and Custom's bands), Games (a sound of
 * their own while they run) and Night Shift. Everything is for the output Windows plays on; each output keeps its own.
 */

const SOUNDS: { value: EqSound; label: string }[] = [
  { value: 'flat', label: 'Flat' },
  { value: 'bass', label: 'Bass' },
  { value: 'vocal', label: 'Vocal' },
  { value: 'footsteps', label: 'Footsteps' },
  { value: 'custom', label: 'Custom' },
]

const SOUND_WORDS: Record<EqSound, string> = {
  flat: 'Flat', bass: 'Bass', vocal: 'Vocal', footsteps: 'Footsteps', custom: 'Custom',
}

/** Night Shift's times, every half hour. */
const TIMES = Array.from({ length: 48 }, (_, i) => {
  const hour = Math.floor(i / 2), minute = i % 2 ? 30 : 0
  const value = `${String(hour).padStart(2, '0')}:${minute ? '30' : '00'}`
  return { value, label: `${hour % 12 || 12}:${minute ? '30' : '00'} ${hour < 12 ? 'AM' : 'PM'}` }
})

/** The card's look for an AutoEq kind: earbuds and in-ears are drawn as IEMs. */
export const wearingOfEq = (headphone: EqHeadphone): Wearing =>
  ({ name: headphone.name, look: headphone.form === 'over-ear' ? 'headphones' : 'iem', id: headphone.id })

type Params = Parameters<typeof host.eq>[1]

/** Exo EQ's state for this output, read again when Windows' output changes; a change answers with the new state. */
export function useEq(output?: string) {
  const [state, setState] = useState<EqState | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  const newest = useRef(0)
  useEffect(() => {
    let live = true
    host.eq('state', { output }).then(next => { if (live) setState(next) }, (cause: unknown) => report(cause, 'Eq.state'))
    return () => { live = false }
  }, [output])
  // Equalizer APO's own windows (its installer, the Device Selector) change what the page shows: read until they're done.
  const waiting = state !== null && (state.apo.installing || (state.apo.installed && !state.apo.onOutput))
  useEffect(() => {
    if (!waiting) return
    const again = window.setInterval(() => void host.eq('state', { output }).then(setState, () => {}), 4000)
    return () => window.clearInterval(again)
  }, [waiting, output])

  /** One press: the new state, and what went wrong (also shown under its row), or null. */
  const act = useCallback(async (row: string, action: EqAction, params: Params = {}): Promise<string | null> => {
    const mine = ++newest.current
    setBusy(row)
    setError(null)
    let problem: string | null = null
    try {
      const next = await host.eq(action, { output, ...params })
      if (mine === newest.current) setState(next)
      if (!next.ok && next.message) problem = errorText(next.message, "Exo EQ didn't answer. Try again.")
    } catch (cause) {
      report(cause, `Eq.${action}`)
      problem = errorText(cause, "Exo EQ didn't answer. Try again.")
    }
    if (problem) setError({ row, text: problem })
    if (mine === newest.current) setBusy(null)
    return problem
  }, [output])
  const errorFor = (row: string) => error?.row === row ? error.text : null
  return { state, busy, act, errorFor }
}

export type Eq = ReturnType<typeof useEq>

/** Equalizer APO: its install, then whatever keeps it from playing on this output. Nothing once it plays here. */
export function ApoPanel({ eq }: { eq: Eq }) {
  const state = eq.state
  if (!state) return null
  const apo = state.apo
  const output = state.output?.name ?? 'this output'
  if (!apo.installed) {
    return (
      <Panel title="Exo EQ">
        <Row label={apo.installing ? 'Installing Equalizer APO' : 'Install Equalizer APO'} labelId="eq-install" error={eq.errorFor('install')}
          detail={apo.installing ? 'Say yes when Windows asks, then tick your headphones and speakers in its Device Selector.'
            : 'Exo EQ plays through Equalizer APO, a free audio engine. Exo downloads it from SourceForge (12 MB), Windows asks once for permission, then you pick your outputs and restart the PC.'}>
          <button type="button" className="exo-set-btn is-primary" disabled={eq.busy !== null || apo.installing} onClick={() => void eq.act('install', 'install')}>
            {eq.busy === 'install' || apo.installing ? <Spinner label="Installing Equalizer APO" /> : null}Install
          </button>
        </Row>
      </Panel>
    )
  }
  const devices = (
    <button type="button" className="exo-set-btn" disabled={eq.busy !== null} onClick={() => void eq.act('devices', 'devices')}>
      {eq.busy === 'devices' ? <Spinner label="Opening the Device Selector" /> : null}Choose outputs
    </button>
  )
  const rows = [
    !apo.onOutput ? (
      <Row key="on" label={`Equalizer APO isn't on ${output}`} labelId="eq-apo-off" error={eq.errorFor('devices')}
        detail="Tick it in Equalizer APO's Device Selector, then restart the PC once.">{devices}</Row>
    ) : apo.restart ? (
      <Row key="restart" label="Restart to hear Exo EQ" labelId="eq-restart" detail={`Equalizer APO was just turned on for ${output}. It plays after the PC restarts.`}>
        <State>Restart</State>
      </Row>
    ) : null,
    apo.effectsOff ? (
      <Row key="fx" label="Audio enhancements are off" labelId="eq-fx" error={eq.errorFor('sound-settings')}
        detail={`Windows keeps Equalizer APO out of ${output} while they're off. Turn them on in its Properties, under Advanced.`}>
        <button type="button" className="exo-set-btn" disabled={eq.busy !== null} onClick={() => void eq.act('sound-settings', 'sound-settings')}>Sound settings</button>
      </Row>
    ) : null,
    state.lucidPlays ? (
      <Row key="lucid" label="Lucid's EQ plays too" labelId="eq-lucid" error={eq.errorFor('lucid-off')}
        detail="Its block in Equalizer APO's config corrects the sound a second time.">
        <button type="button" className="exo-set-btn" disabled={eq.busy !== null} onClick={() => void eq.act('lucid-off', 'lucid-off')}>Turn Lucid's off</button>
      </Row>
    ) : null,
  ].filter(Boolean)
  if (rows.length === 0) return null
  return <Panel title="Exo EQ">{rows}</Panel>
}

/** The pair you wear: a suggestion from the output's own name, and AutoEq's correction picked by search. */
export function HeadphonePanel({ eq, wearing, onWearing }: { eq: Eq; wearing: Wearing; onWearing: (wearing: Wearing) => void }) {
  const state = eq.state
  // A correction picked here names the card.
  const picked = state?.headphone ?? null
  useEffect(() => {
    if (picked && picked.id !== wearing?.id) onWearing(wearingOfEq(picked))
  }, [picked, wearing?.id, onWearing])
  if (!state) return null
  const use = (headphone: EqHeadphone | null) => {
    if (headphone) onWearing(wearingOfEq(headphone))
    void eq.act('headphone', 'headphone', { id: headphone?.id ?? null })
  }
  return (
    <Panel title="Headphones">
      {state.guess ? (
        <Row label={`Looks like ${state.guess.name}`} labelId="eq-guess">
          <button type="button" className="exo-set-btn" disabled={eq.busy !== null} onClick={() => use(state.guess)}>Use it</button>
        </Row>
      ) : null}
      <HeadphonePicker picked={picked} busy={eq.busy === 'headphone'} error={eq.errorFor('headphone')} onPick={use} />
    </Panel>
  )
}

/**
 * AutoEq's models, searched by name: the field takes the row's button's place and its matches list under it. A pick
 * downloads that model's correction; None takes it off.
 */
function HeadphonePicker({ picked, busy, error, onPick }: { picked: EqHeadphone | null; busy: boolean; error: string | null; onPick: (headphone: EqHeadphone | null) => void }) {
  const [list, setList] = useState<EqHeadphone[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  useEffect(() => {
    if (!open || list) return
    host.eqHeadphones().then(result => { setList(result.ok ? result.headphones : []); setFailed(!result.ok) }, (cause: unknown) => {
      report(cause, 'Eq.headphones', { level: 'warn' })
      setList([])
      setFailed(true)
    })
  }, [open, list])
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  const hits = words.length === 0 ? [] : (list ?? []).filter(h => words.every(word => h.name.toLowerCase().includes(word))).slice(0, 6)
  const pick = (headphone: EqHeadphone) => {
    onPick(headphone)
    setOpen(false)
    setQuery('')
  }
  const detail = busy ? 'Downloading its correction' : picked ? `${picked.name}, measured by ${picked.source}` : 'None: pick yours for its correction'
  return (
    <>
      <Row label="Correction" labelId="eq-headphone" detail={detail} error={failed && open ? "AutoEq's list didn't load. Check your connection, then try again." : error}>
        {busy ? <Spinner label="Downloading the correction" /> : null}
        {open ? (
          <label className="exo-field exo-devices-pick-field">
            <Search size={14} />
            <input type="search" value={query} autoFocus placeholder="HD 600" aria-label="Search headphones" onChange={event => setQuery(event.target.value)}
              onKeyDown={event => { if (event.key === 'Escape') setOpen(false) }} />
          </label>
        ) : (
          <>
            {picked ? <button type="button" className="exo-set-btn" disabled={busy} onClick={() => onPick(null)}>None</button> : null}
            <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { setFailed(false); setList(failed ? null : list); setOpen(true) }}>{picked ? 'Change' : 'Pick'}</button>
          </>
        )}
      </Row>
      {open ? (
        <ul className="exo-devices-pick-list" aria-label="Headphones">
          {list === null ? <li><Wait label="Reading AutoEq's list">{times(5, i => <button type="button"><strong>{sample(i)}</strong><span>In-ear</span></button>)}</Wait></li>
            : words.length === 0 ? <li className="exo-devices-pick-wait">{list.length ? `Type a model: ${list.length.toLocaleString()} to search` : 'No list'}</li>
            : hits.length === 0 ? <li className="exo-devices-pick-wait">No match</li>
            : hits.map(headphone => (
              <li key={headphone.id}>
                <button type="button" onClick={() => pick(headphone)}><strong>{headphone.name}</strong><span>{headphone.source}</span></button>
              </li>
            ))}
        </ul>
      ) : null}
    </>
  )
}

/** The switch with Compare beside it, the sound, Custom's ten bands and the headroom the chain takes. */
export function SoundPanel({ eq }: { eq: Eq }) {
  const state = eq.state
  if (!state) return null
  const gains = state.custom.map(band => band.gain)
  const setBand = (at: number, gain: number) => void eq.act('custom', 'custom', { gains: gains.map((value, i) => i === at ? gain : value) })
  const compare = (
    <button type="button" className="exo-set-btn" aria-pressed={state.comparing} disabled={!state.on || eq.busy === 'compare'}
      onClick={() => void eq.act('compare', 'compare', { on: !state.comparing })}>{state.comparing ? 'Back to EQ' : 'Compare'}</button>
  )
  const others = state.others.map(o => `${o.name}: ${[o.headphone, o.sound === 'flat' ? null : SOUND_WORDS[o.sound]].filter(Boolean).join(', ')}`).join('; ')
  return (
    <Panel title="Sound">
      <SwitchRow id="eq-on" label="Exo EQ" detail={state.comparing ? 'Off while you compare' : state.playing?.sound ? `${SOUND_WORDS[state.playing.sound]} for ${state.playing.title}` : undefined}
        checked={state.on} onChange={on => eq.act('on', 'on', { on })}>
        {compare}
      </SwitchRow>
      <ChoiceRow<EqSound> id="eq-sound" label="Sound" value={state.sound} choices={SOUNDS} disabled={!state.on}
        onChange={sound => eq.act('sound', 'sound', { sound })} />
      {state.sound === 'custom' ? state.custom.map((band, i) => (
        <SliderRow key={band.hz} label={band.hz >= 1000 ? `${band.hz / 1000} kHz` : `${band.hz} Hz`} min={-12} max={12} step={0.5} value={band.gain}
          render={value => `${value > 0 ? '+' : ''}${value.toFixed(1)} dB`} disabled={!state.on} error={i === 0 ? eq.errorFor('custom') : null} onCommit={gain => setBand(i, gain)} />
      )) : null}
      {state.preamp < 0 ? (
        <Row label="Headroom" labelId="eq-preamp" detail="Exo lowers the level this much, so the boosts never clip.">
          <State>{`${state.preamp.toFixed(1)} dB`}</State>
        </Row>
      ) : null}
      {others ? <Row label="Other outputs" labelId="eq-others" detail={others}><State>Saved</State></Row> : null}
    </Panel>
  )
}

/** Games with a sound of their own: on as the game starts, the output's own back once it exits. */
export function GamesPanel({ eq }: { eq: Eq }) {
  const state = eq.state
  if (!state) return null
  const choices = [...SOUNDS.filter(s => s.value !== 'custom' || state.custom.some(b => b.gain !== 0)), { value: '' as const, label: 'Remove' }]
  return (
    <Panel title="Games">
      {state.games.map(game => (
        <Row key={game.id} label={game.title} labelId={`eq-game-${game.id}`} error={eq.errorFor(`game-${game.id}`)}
          detail={state.playing?.id === game.id ? 'Playing now' : undefined}>
          <Choice label={`${game.title}'s sound`} value={game.sound} choices={choices}
            onChange={sound => void eq.act(`game-${game.id}`, 'game', { game: game.id, sound: sound === '' ? null : sound as EqSound })} />
        </Row>
      ))}
      {state.library.length ? (
        <Row label={state.games.length ? 'Add a game' : 'A sound for a game'} labelId="eq-game-add" error={eq.errorFor('game-add')}
          detail={state.games.length ? undefined : 'Footsteps for a shooter, Vocal for a story: on while it runs.'}>
          <Choice label="Add a game" value="" choices={[{ value: '', label: 'Pick a game' }, ...state.library.map(g => ({ value: g.id, label: g.title }))]}
            onChange={id => { if (id) void eq.act('game-add', 'game', { game: id, sound: 'footsteps' }) }} />
        </Row>
      ) : null}
    </Panel>
  )
}

/** Night Shift: a warmer sound late at night, on Windows' Night light schedule or its own hours. */
export function NightPanel({ eq }: { eq: Eq }) {
  const night = eq.state?.night
  if (!night) return null
  return (
    <Panel title="Night Shift">
      <SwitchRow id="eq-night" label="Night Shift" detail={night.on && night.now ? 'On now' : undefined} checked={night.on}
        onChange={on => eq.act('night', 'night', { on })} />
      {night.on ? (
        <>
          <SwitchRow id="eq-night-sync" label="Follow Windows Night light" checked={night.sync}
            onChange={sync => eq.act('night', 'night', { sync })} />
          {night.sync ? null : (
            <Row label="Hours" labelId="eq-night-hours" error={eq.errorFor('hours')}>
              <Choice label="Night Shift starts" value={night.start} choices={TIMES} onChange={start => void eq.act('hours', 'night', { start })} />
              <Choice label="Night Shift ends" value={night.end} choices={TIMES} onChange={end => void eq.act('hours', 'night', { end })} />
            </Row>
          )}
        </>
      ) : null}
    </Panel>
  )
}
