import { useEffect, useRef, useState } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { can, type Step, type useMouse } from './mouse'
import { call, onHostEvent, tell } from './exo-hid'
import { GamePicker, LinkedCovers, type Game } from './GamePicker'
import { AskButton, Panel, Row, Segmented, Spinner, hz } from './ui'
import { indicatorColor } from './Finalmouse'
import { linkRates } from './brands/logitech'
import { frameGet, frameRemove } from './frameStore'
import { diffSteps, profileMatches, profilesEqual, sharedAutoSwitcher, type ProfileRecord } from './profile-logic'

type Client = ReturnType<typeof useMouse>['client']
/** games: the Exo game ids this profile is for; it applies while one of them runs. */
/**
 * isDefault marks Desktop: built in, always there, always first, the profile the
 * mouse goes back to whenever no linked game is running. It is never renamed,
 * deleted or linked to games; Update saves the mouse's current settings into it.
 */
type Profile = { id: string; name: string; steps: Step[]; savedAt: string; games?: string[]; isDefault?: boolean; polling?: Polling }
/** A profile's polling rate on each connection, as saved on it: a wireless mouse often allows less on its cable, or the other way round. */
type Polling = Partial<Record<'Wired' | 'Wireless', number>>

const KEY = 'exo-mouse.profiles.v1'

type All = Record<string, Profile[]>

/** Profiles an earlier Exo Mouse kept in this frame's own storage. */
function readOld(): All | null {
  try {
    const raw = frameGet(KEY)
    return raw ? JSON.parse(raw) as All : null
  } catch { return null }
}

/**
 * Exo keeps the profiles in its own data folder, so they survive anything that
 * clears this frame. Older ones move there once, and leave the frame only after
 * Exo has saved them.
 */
async function readAll(): Promise<All> {
  const reply = await call('exo.profiles.read') as unknown as { data?: All | null }
  if (reply.data) return reply.data
  const old = readOld()
  if (!old) return {}
  await writeAll(old)
  frameRemove(KEY)
  return old
}

async function writeAll(all: All): Promise<void> {
  await call('exo.profiles.write', { data: all })
}

/**
 * The settings this mouse has now, as the setter calls that would put them
 * back: only setters its driver has, only values the mouse reported, never a
 * value the mouse treats as fixed (PerfectPolling, a hidden Motion Sync).
 */
export function snapshot(status: MouseStatus, client: Client): Step[] {
  const ui = status.ui ?? {}
  const steps: Step[] = []
  const add = (method: string, ...args: unknown[]) => { if (can(client, method) && args.every(a => a !== null && a !== undefined)) steps.push([method, args]) }
  const slx = !!status.finalmouseIsSlx
  add('setDpi', status.dpi)
  if (!ui.pollingReadOnly && (status.supportedPollingRates?.length ?? 1) > 0) add('setPollingRate', status.pollingRateHz)
  if (slx) {
    const mm = status.finalmousePawLodMm
    if (mm != null) status.finalmousePawLodCustom ? add('setLiftOffScale', Math.round(mm * 10)) : add('setPawLodMm', mm)
  } else if (status.liftOffDistance) add('setLiftOffDistance', status.liftOffDistance)
  if (!ui.hideMotionSync && !ui.hideProcessingCard) add('setMotionSync', status.motionSync)
  if (!ui.hideAngleSnapping && !ui.hideProcessingCard) add('setAngleSnapping', status.angleSnapping)
  if (!ui.hideRippleControl && !ui.hideProcessingCard) add('setRippleControl', status.rippleControl)
  add('setDebounceTime', status.debounceMs)
  // HITS (Logitech's hall-effect buttons): the live tuning for each button, never the onboard copy, so switching
  // profiles doesn't wear the mouse's memory. The rapid trigger switch only where the mouse reports it.
  const hits = status.analogButtonTuning
  if (hits?.buttons.length === 2 && can(client, 'setAnalogButtonTuning'))
    hits.buttons.forEach((b, i) => add('setAnalogButtonTuning', i, b.rapidTriggerEnabled === undefined
      ? { actuation: b.actuation, rapidTrigger: b.rapidTrigger, haptics: b.haptics }
      : { actuation: b.actuation, rapidTrigger: b.rapidTrigger, haptics: b.haptics, rapidTriggerEnabled: b.rapidTriggerEnabled }))
  // A Logitech mouse running from this PC: its bunny hop, as Exo last wrote it (mouse.ts fills it in only then).
  add('setHostBunnyHopMs', status.hostBunnyHopMs)
  if (!ui.hideSleepCard) add('setSleepTimeout', status.sleepTimeout)
  if (status.powerModes?.length) add('setPowerMode', status.powerMode)
  if (slx) {
    // Modes first: actuation only takes on a side that is analog. Release points only when the
    // mouse reported them; actuation only when both sides are calibrated (a side reading 0 is not).
    const relL = status.finalmouseClickReleaseL
    const relR = status.finalmouseClickReleaseR
    const intL = status.finalmouseClickIntentL
    const intR = status.finalmouseClickIntentR
    if (relL != null && relR != null && intL != null && intR != null)
      add('setClickMode', status.finalmouseClickModeL, status.finalmouseClickModeR, relL, relR, intL, intR)
    else if (relL != null && relR != null)
      add('setClickMode', status.finalmouseClickModeL, status.finalmouseClickModeR, relL, relR)
    else add('setClickMode', status.finalmouseClickModeL, status.finalmouseClickModeR)
    // Rapid trigger rides along only where the firmware reported it.
    const hyst = status.finalmouseTmrHystL != null && status.finalmouseTmrHystR != null ? [status.finalmouseTmrHystL, status.finalmouseTmrHystR] : []
    if (status.finalmouseTmrThrL && status.finalmouseTmrThrR)
      add('setTmrActuation', status.finalmouseTmrThrL / 100, status.finalmouseTmrThrR / 100, ...hyst)
    const color = indicatorColor(status)
    if (color) add('setIndicatorColor', color.r, color.g, color.b)
    add('setDongleLedMode', status.finalmouseDongleLedMode)
    add('setTournamentScrollMode', status.finalmouseTournamentScrollMode)
    if (status.finalmouseTournamentScrollMode) add('setTournamentScrollTimeout', status.finalmouseTournamentScrollTimeoutMs)
  }
  return steps
}

/** The rate a connection takes for the one wanted: that one where it is allowed, else the nearest below, else its lowest. */
function rateFor(wanted: number, allowed: readonly number[]): number {
  if (!allowed.length || allowed.includes(wanted)) return wanted
  const below = allowed.filter(rate => rate <= wanted)
  return below.length ? Math.max(...below) : Math.min(...allowed)
}

/** A profile's polling rate on a connection: the one saved for it, else the profile's own as that connection takes it. */
const rateOn = (profile: Pick<Profile, 'steps' | 'polling'>, link: keyof Polling, allowed: readonly number[]) =>
  rateFor(profile.polling?.[link] ?? (profile.steps.find(s => s[0] === 'setPollingRate')?.[1][0] as number), allowed)

/**
 * A profile's settings as the mouse takes them now: the polling rate saved for the connection it is on, else the
 * nearest rate this connection allows, never one it would refuse (which would leave the profile retrying); bunny hop
 * for a mouse running from this PC only while it does.
 */
function fit(profile: Pick<Profile, 'steps' | 'polling'>, status: MouseStatus): Step[] {
  const on = status.connectionType ?? 'Wired'
  return profile.steps.flatMap((step): Step[] => {
    if (step[0] === 'setHostBunnyHopMs') return status.hostBunnyHopMs == null ? [] : [step]
    if (step[0] !== 'setPollingRate') return [step]
    return [['setPollingRate', [rateOn(profile, on, status.supportedPollingRates ?? [])]]]
  })
}

/** What a save keeps of the polling rate: this connection's rate now, the other connection's as it was. */
const pollingNow = (status: MouseStatus, before?: Polling): Polling => ({ ...before, [status.connectionType ?? 'Wired']: status.pollingRateHz })

/**
 * The mouse has a profile when every setting the profile holds is what the
 * mouse has now. A profile saved before Exo Mouse learned a setting still
 * matches, instead of being rewritten on every connect.
 */
const same = profileMatches

const DESKTOP = 'Desktop'
const isDesktopName = (name: string) => name.trim().toLowerCase() === DESKTOP.toLowerCase()

function withDesktop(list: Profile[], now: Step[]): Profile[] {
  const desktop = list.find(p => p.isDefault) ?? list.find(p => isDesktopName(p.name))
    ?? { id: crypto.randomUUID(), name: DESKTOP, steps: now, savedAt: new Date().toISOString() }
  const rest = list.filter(p => p.id !== desktop.id).map(p => p.isDefault ? { ...p, isDefault: false } : p)
  return [{ ...desktop, name: DESKTOP, isDefault: true, games: [] }, ...rest]
}

export const changes = diffSteps

const autoSwitcher = sharedAutoSwitcher

onHostEvent('game.runState', data => {
  const d = data as { id?: string; isRunning?: boolean }
  if (!d.id) return
  autoSwitcher.onGameRunState(d.id, !!d.isRunning)
})

/**
 * Exo's own profiles for this mouse model: named sets of settings, as many as
 * you like, saved on this PC. Save current stores what the mouse has now;
 * Apply puts it back; the one that matches the mouse right now says Active.
 * A profile can be linked to games: when one starts (Exo says so, room open
 * or not), the profile is written; when it closes, Desktop goes back.
 */
export function Profiles({ device, status, client, applyAll, busy }: {
  device: string
  status: MouseStatus
  client: Client
  applyAll: (steps: Step[], options?: { source?: 'user' | 'profile' }) => Promise<MouseStatus | null>
  busy: boolean
}) {
  const [all, setAll] = useState<All>({})
  useEffect(() => {
    readAll().then(all => { setAll(all); setLoaded(true) }).catch(() => tell({ notice: 'Exo could not read your saved mouse profiles.' }))
  }, [])
  const [name, setName] = useState('')
  const [renaming, setRenaming] = useState<string | null>(null)
  const [draft, setDraft] = useState('')
  const [applying, setApplying] = useState<string | null>(null)
  const [applied, setApplied] = useState<string | null>(null)
  const list = all[device] ?? []
  const now = snapshot(status, client)
  const apply = async (p: Profile) => {
    autoSwitcher.setActiveProfile(p.id, false)
    const diff = changes(fit(p, status), now)
    if (diff.length === 0) {
      setApplied(p.id)
      window.setTimeout(() => setApplied(a => a === p.id ? null : a), 3000)
      return
    }
    setApplying(p.id)
    const took = await applyAll(diff, { source: 'profile' })
    setApplying(a => a === p.id ? null : a)
    if (!took) return
    setApplied(p.id)
    window.setTimeout(() => setApplied(a => a === p.id ? null : a), 3000)
  }
  const active = (p: Profile) => same(fit(p, status), now) || applied === p.id

  const [installed, setInstalled] = useState<Game[]>([])
  const [linking, setLinking] = useState<string | null>(null)
  const pickerOpen = linking !== null
  useEffect(() => {
    let live = true
    call('exo.games')
      .then(r => { if (live) setInstalled((r as unknown as { games?: Game[] }).games ?? []) })
      .catch(() => { if (live && !pickerOpen) setInstalled([]) })
    return () => { live = false }
  }, [pickerOpen])
  const byId = new Map(installed.map(g => [g.id, g]))
  const titles = new Map(installed.map(g => [g.id, g.title]))
  const titlesNow = useRef(titles)
  titlesNow.current = titles

  const live = useRef({ list, now, status, applyAll })
  live.current = { list, now, status, applyAll }

  useEffect(() => {
    autoSwitcher.attach({
      profiles: () => live.current.list,
      resolveSteps: (profile: ProfileRecord) => fit(profile, live.current.status),
      currentSteps: () => live.current.now,
      apply: (_profile, diff, notice) => {
        void live.current.applyAll(diff, { source: 'profile' }).then(took => {
          if (took && notice) tell({ notice })
        })
      },
      gameTitle: id => titlesNow.current.get(id),
    })
    return () => {
      autoSwitcher.detach()
    }
  }, [])

  const [loaded, setLoaded] = useState(false)
  const settled = useRef(false)
  useEffect(() => {
    if (!loaded || settled.current) return
    settled.current = true
    const fixed = withDesktop(list, now)
    if (!profilesEqual(fixed, list)) save(fixed)
    const matched = fixed.find(p => same(fit(p, status), now))
    autoSwitcher.setActiveProfile(matched?.id ?? null, false)
    if (autoSwitcher.findRunningProfile(fixed)) autoSwitcher.syncInitialRunning()
  }, [loaded, list, now, status])

  /** A game is linked to one profile at most (never Desktop): linking it here unlinks it elsewhere. */
  const toggleGame = (profile: Profile, game: string) => {
    if (profile.isDefault) return
    const on = !profile.games?.includes(game)
    save(list.map(p => p.id === profile.id
      ? { ...p, games: on ? [...(p.games ?? []), game] : (p.games ?? []).filter(g => g !== game) }
      : on ? { ...p, games: (p.games ?? []).filter(g => g !== game) } : p))
  }

  const save = (next: Profile[]) => {
    // Profiles Exo could not read would be overwritten with this mouse's list alone.
    if (!loaded) return
    const updated = { ...all, [device]: next }
    setAll(updated)
    writeAll(updated).catch(() => tell({ notice: 'Exo could not save your mouse profiles. Try again.' }))
  }
  /** Save current: a new profile, or the one with that name (Desktop included) updated to what the mouse has now. */
  const add = () => {
    const clean = name.trim().slice(0, 40)
    if (!clean) return
    const existing = list.find(p => p.name.toLowerCase() === clean.toLowerCase())
    save(existing
      ? list.map(p => p.id === existing.id ? { ...p, steps: now, polling: pollingNow(status, p.polling), savedAt: new Date().toISOString() } : p)
      : [...list, { id: crypto.randomUUID(), name: clean, steps: now, polling: pollingNow(status), savedAt: new Date().toISOString() }])
    setName('')
  }
  const update = (id: string) => save(list.map(p => p.id === id ? { ...p, steps: now, polling: pollingNow(status, p.polling), savedAt: new Date().toISOString() } : p))
  /** Each connection's rate where the mouse knows both; both are kept, so the one not changed stays as shown. */
  const links = linkRates(status)
  const setRates = (id: string, polling: Polling) => save(list.map(p => p.id === id ? { ...p, polling } : p))
  const rename = (id: string) => {
    const clean = draft.trim().slice(0, 40)
    // Desktop's name is taken: another profile cannot become it.
    if (clean && !isDesktopName(clean)) save(list.map(p => p.id === id && !p.isDefault ? { ...p, name: clean } : p))
    setRenaming(null)
  }
  /** Delete asks on its button first (Desktop is never deleted). */
  const remove = (id: string) => save(list.filter(p => p.id !== id || p.isDefault))

  return (
    <Panel title="Profiles">
      {list.map(p => (
        <div key={p.id} className="m-row">
          {renaming === p.id && !p.isDefault ? (
            <input className="m-text" aria-label="Profile name" autoFocus maxLength={40} value={draft}
              onChange={e => setDraft(e.target.value)} onBlur={() => rename(p.id)}
              onKeyDown={e => { if (e.key === 'Enter') rename(p.id); if (e.key === 'Escape') setRenaming(null) }} />
          ) : (
            <span className="m-row-label">
              <span className="m-profile-name">
                {p.name}
                {active(p) ? (
                  <span className="m-row-value is-good m-switch-in">
                    {applied === p.id ? <svg className="m-check" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 12L9 17L20 6" pathLength="1" /></svg> : null}
                    Active
                  </span>
                ) : null}
              </span>
              <LinkedCovers ids={p.games ?? []} games={byId} />
            </span>
          )}
          <span className="m-row-control m-actions">
            {/* The profile on the mouse says Active beside its name; it has nothing to apply. */}
            {active(p) ? null : (
              <button type="button" className="m-button is-compact" disabled={busy} aria-busy={applying === p.id} onClick={() => void apply(p)}>
                {applying === p.id ? <Spinner label={`Applying ${p.name}`} /> : null}Apply
              </button>
            )}
            {p.isDefault ? (
              active(p) ? null : <button type="button" className="m-button is-compact" onClick={() => update(p.id)}>Update</button>
            ) : (
              <>
                <button type="button" className="m-button is-compact" aria-haspopup="dialog" onClick={() => setLinking(p.id)}>Games</button>
                <button type="button" className="m-button is-compact" onClick={() => { setRenaming(p.id); setDraft(p.name) }}>Rename</button>
                <AskButton className="m-button is-compact is-danger" question="Delete profile?" danger onConfirm={() => remove(p.id)}>Delete</AskButton>
              </>
            )}
          </span>
        </div>
      ))}
      {(() => {
        const picking = list.find(p => p.id === linking)
        if (!picking) return null
        // A mouse whose other connection's rates are known (a Logitech's profile format keeps both) sets each here,
        // without plugging in or out; the one it is on is used when a game starts.
        const shown = links && picking.steps.some(s => s[0] === 'setPollingRate')
          ? { Wireless: rateOn(picking, 'Wireless', links.Wireless), Wired: rateOn(picking, 'Wired', links.Wired) }
          : null
        return (
          <GamePicker profile={picking} profiles={list} games={installed}
            onToggle={game => toggleGame(picking, game)} onClose={() => setLinking(null)}
            settings={links && shown ? (['Wireless', 'Wired'] as const).map(link => (
              <Row key={link} label={`${link} polling rate`}>
                <Segmented label={`${link} polling rate`} options={links[link].map(rate => ({ value: rate, label: hz(rate) }))}
                  value={shown[link]} onChange={rate => setRates(picking.id, { ...shown, [link]: rate })} />
              </Row>
            )) : null} />
        )
      })()}
      <div className="m-row m-profile-new">
        <input className="m-text" aria-label="New profile name" placeholder="Name" maxLength={40} value={name}
          onChange={e => setName(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') add() }} />
        <span className="m-row-control">
          <button type="button" className="m-button" disabled={!loaded || !name.trim()} onClick={add}>
            {list.some(p => p.name.toLowerCase() === name.trim().toLowerCase()) ? 'Update' : 'Save current'}
          </button>
        </span>
      </div>
    </Panel>
  )
}
