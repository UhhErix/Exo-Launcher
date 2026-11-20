import { useCallback, useEffect, useState } from 'react'
import { Check, Close } from '../../brand/icons'
import { host, onHostEvent, type AutopilotChange, type AutopilotLive, type AutopilotReport, type AutopilotState } from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { Unfold } from '../ui/Unfold'

/** The last state read this run, so the menu shows it at once and reads again behind it. */
let last: AutopilotState | null = null

/**
 * Autopilot's state: read when the room shows (the chip says its status), when the menu opens, every two seconds while
 * the menu is open (so a game started while open shows live at once), and again when a session's report is kept. Nothing is
 * polled while the menu is closed, so a game in front costs nothing here.
 */
export function useAutopilot(active: boolean, open: boolean) {
  const [state, setState] = useState<AutopilotState | null>(last)
  useEffect(() => {
    if (!active) return
    let alive = true
    const read = () => host.autopilotState().then(next => { if (alive) { last = next; setState(next) } },
      (e: unknown) => report(e, 'Autopilot.read', { level: 'warn' }))
    void read()
    const timer = open ? window.setInterval(read, 2000) : undefined
    return () => { alive = false; window.clearInterval(timer) }
  }, [active, open])
  useEffect(() => onHostEvent('autopilot.report', () => {
    host.autopilotState().then(next => { last = next; setState(next) }, (e: unknown) => report(e, 'Autopilot.report', { level: 'warn' }))
  }), [])
  const put = useCallback((next: AutopilotState) => { last = next; setState(next) }, [])
  return { state, setState: put }
}

/** PC's chip for Autopilot: always active in green, or amber notice only when Memory Integrity conflicts with installed anti-cheat. */
export function AutopilotChip({ state, onOpen }: { state: AutopilotState | null; onOpen: () => void }) {
  const conflict = state?.memoryIntegrity?.status === 'conflict'
  return (
    <button type="button" className={cn('exo-pc-chip', conflict ? 'is-bad' : 'is-good')} data-pc-menu="autopilot" aria-haspopup="dialog" onClick={onOpen}>
      {conflict ? 'Autopilot · Notice' : <><Check size={14} />Autopilot</>}
    </button>
  )
}

/** Updates Autopilot's per-app rules; the state the host answers with replaces the menu's. */
async function change(next: AutopilotChange, onState: (state: AutopilotState) => void): Promise<string | null> {
  try {
    const answer = await host.autopilotSet(next)
    onState(answer.state)
    return answer.ok ? null : answer.message ?? 'Windows didn’t take that.'
  } catch (e) {
    report(e, 'Autopilot.set')
    return 'Not saved. Try again.'
  }
}

/** Active always-on protections in concise plain English, plus CPU topology and Windows Game Mode / mouse precision status. */
function activeSummary(state: AutopilotState): string {
  const parts = [
    'Gives your game priority while it runs, throttles background hogs, and keeps cores awake.',
    state.gameMode.off ? 'Windows Game Mode off.' : state.gameMode.keptOn ? `Windows Game Mode stays on (${state.gameMode.keptOn}).` : null,
    state.mouseAccel?.off ? 'Mouse acceleration off (1:1).' : null,
    state.cpu || null,
  ]
  return parts.filter(Boolean).join(' ')
}

/** Always-on Autopilot status, active protections, library-aware Memory Integrity, and any per-app rules. */
export function AutopilotControls({ state, onState }: { state: AutopilotState | null; onState: (state: AutopilotState) => void }) {
  if (!state) return null
  return (
    <div className="exo-pc-menu-rows">
      <Row label="Autopilot" detail={activeSummary(state)}>
        <State on>Active</State>
      </Row>
      {state.now?.did.length ? <Row label="Doing now" detail={state.now.did.join(' · ')} /> : null}
      {state.memoryIntegrity ? <MemoryIntegrityRow integrity={state.memoryIntegrity} onState={onState} /> : null}
      <NameList label="Always held back while playing" names={state.alwaysDemote} onChange={alwaysDemote => change({ alwaysDemote }, onState)} />
      <NameList label="Paused while playing" names={state.suspend} onChange={suspend => change({ suspend }, onState)} />
    </div>
  )
}

function MemoryIntegrityRow({ integrity, onState }: {
  integrity: NonNullable<AutopilotState['memoryIntegrity']>
  onState: (state: AutopilotState) => void
}) {
  const refresh = () => {
    void host.autopilotState().then(onState, (e: unknown) => report(e, 'Autopilot.integrity', { level: 'warn' }))
  }
  const control = () => {
    switch (integrity.status) {
      case 'off':
        return (
          <>
            <State on>Off</State>
            {integrity.canUndo ? (
              <ActionButton className="exo-set-btn is-compact" context="Autopilot.integrityOff"
                onPress={() => host.systemTuning('off', 'integrity')} onDone={refresh}>Restore</ActionButton>
            ) : null}
          </>
        )
      case 'unlocked':
        return (
          <ConfirmButton className="exo-set-btn is-primary" question="Turn off Memory Integrity?" context="Autopilot.integrityOn"
            onConfirm={() => host.systemTuning('on', 'integrity')} onDone={refresh}>Optimize</ConfirmButton>
        )
      case 'pending-off':
      case 'pending-on':
        return <State>Restart</State>
      case 'required':
        return <State on>Protected</State>
      case 'conflict':
        return integrity.canUndo ? (
          <ConfirmButton className="exo-set-btn is-primary" question="Turn Memory Integrity back on?" context="Autopilot.integrityRestore"
            onConfirm={() => host.systemTuning('off', 'integrity')} onDone={refresh}>Turn back on</ConfirmButton>
        ) : <State>Required</State>
      case 'locked':
        return <State>Managed</State>
    }
  }
  return <Row label="Memory Integrity" detail={integrity.reason}>{control()}</Row>
}

/** Apps named in settings: each with Remove; hidden when empty. */
function NameList({ label, names, onChange }: { label: string; names: string[]; onChange: (names: string[]) => Promise<string | null> }) {
  const [error, setError] = useState<string | null>(null)
  if (!names.length) return null
  return (
    <Row label={label} error={error} stacked>
      <div className="exo-set-chips">
        {names.map(name => (
          <button key={name} type="button" className="exo-set-btn is-compact" aria-label={`Remove ${name}`}
            onClick={() => { void onChange(names.filter(other => other !== name)).then(setError) }}>
            {name}<Close size={12} />
          </button>
        ))}
      </div>
    </Row>
  )
}

const n = (value: number | null | undefined, unit = '') => value == null ? null : `${Math.round(value)}${unit}`
const gb = (used?: number | null, total?: number | null) => used == null ? null : total ? `${used} of ${total} GB` : `${used} GB`

/** The live line's numbers, in the order the overlay shows them; what this PC doesn't say is left out. */
function liveDetail(live: AutopilotLive): string {
  return [
    live.cpu != null ? `CPU ${n(live.cpu, '%')}` : null,
    live.clock != null ? `Clock ${n(live.clock, '%')} of base` : null,
    live.gpu != null ? `GPU ${n(live.gpu, '%')}${live.gpuTemp != null ? ` ${live.gpuTemp} °C` : ''}` : null,
    live.vramGb != null ? `VRAM ${gb(live.vramGb, live.vramTotalGb)}` : null,
    live.ramGb != null ? `RAM ${gb(live.ramGb, live.ramTotalGb)}` : null,
    live.standbyGb != null ? `Standby ${live.standbyGb} GB` : null,
    live.freeGb != null ? `Free ${live.freeGb} GB` : null,
    live.dpc != null ? `DPC ${live.dpc}%` : null,
  ].filter(Boolean).join(' · ')
}

const fps = (r: { fps: number; low1: number; low01: number }) => `${Math.round(r.fps)} fps, 1% low ${Math.round(r.low1)}, 0.1% low ${Math.round(r.low01)}`
const stutters = (count: number) => count === 1 ? '1 stutter' : `${count} stutters`
const when = (iso: string) => new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })

/** Autopilot's live line and its reports. Each report opens in place to what it found and what Autopilot did. */
export function AutopilotReports({ state }: { state: AutopilotState | null }) {
  const [open, setOpen] = useState<string | null>(null)
  if (!state) return null
  const live = state.live
  return (
    <div className="exo-pc-menu-rows">
      {live ? <Row label={`Now · ${live.title}`} detail={<>{fps(live)} · {stutters(live.stutters)}<br />{liveDetail(live)}</>} /> : null}
      {state.reports.length === 0 && !live
        ? <Row label="No sessions yet" detail="Play a game and what Autopilot saw shows here afterwards." />
        : null}
      {state.reports.map(item => (
        <ReportRow key={item.id} item={item} open={open === item.id} onToggle={() => setOpen(current => current === item.id ? null : item.id)} />
      ))}
    </div>
  )
}

function ReportRow({ item, open, onToggle }: { item: AutopilotReport; open: boolean; onToggle: () => void }) {
  const smooth = item.stutters === 0
  return (
    <div className="exo-autopilot-report">
      <Row label={`${item.title} · ${item.minutes} min · ${when(item.started)}`} detail={`${fps(item)} · ${smooth ? 'no stutters' : stutters(item.stutters)}`}>
        <button type="button" className="exo-set-btn is-compact" aria-expanded={open} onClick={onToggle}>{open ? 'Less' : 'Details'}</button>
      </Row>
      <Unfold open={open}>
        <div className="exo-autopilot-details">
          {item.findings.length ? (
            <>
              <h3>What Autopilot found</h3>
              <ul>
                {item.findings.map(finding => (
                  <li key={finding.kind + (finding.app ?? '')} className={cn(finding.kind === 'unexplained' && 'is-quiet')}>
                    <span>{finding.detail}</span>
                    <span className="exo-autopilot-hint">{finding.suggestion}</span>
                  </li>
                ))}
              </ul>
            </>
          ) : <p>Smooth all the way: nothing to report.</p>}
          {item.did.length ? (
            <>
              <h3>What Autopilot did</h3>
              <ul>{item.did.map(line => <li key={line}><span>{line}</span></li>)}</ul>
            </>
          ) : null}
          {item.peaks ? (
            <p className="exo-autopilot-hint">
              {[`Highest: CPU ${item.peaks.cpu}%`, item.peaks.gpu != null ? `GPU ${item.peaks.gpu}%` : null,
                item.peaks.gpuTemp != null ? `${item.peaks.gpuTemp} °C` : null,
                item.peaks.vramGb != null ? `VRAM ${gb(item.peaks.vramGb, item.peaks.vramTotalGb)}` : null,
                item.peaks.ramGb != null ? `RAM ${gb(item.peaks.ramGb, item.peaks.ramTotalGb)}` : null].filter(Boolean).join(' · ')}
            </p>
          ) : null}
        </div>
      </Unfold>
    </div>
  )
}
