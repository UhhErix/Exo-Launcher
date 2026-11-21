import { useEffect, useState } from 'react'
import { Cpu, GraphicsCard } from '../../brand/icons'
import type { PlainError } from '../../lib/errorWords'
import { host, type AutoTune, type GpuProfile, type PcAbout, type TuneMeasure, type TunePart } from '../../lib/host'
import { report } from '../../lib/report'
import { State } from '../settings/parts'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
import { Progress } from '../ui/Progress'

type TuneAction = 'start' | 'stop' | 'undo'

type Press = {
  onPress: () => Promise<unknown>
  onDone: (next: unknown) => void
  onFailed: (failure: PlainError) => void
  why: 'none'
  context: string
  fallback: string
}

type TuneMetric = { label: string; value: string }

function tuneMetrics(applied?: string | null, before?: TuneMeasure | null, after?: TuneMeasure | null): TuneMetric[] {
  const out: TuneMetric[] = []
  if (applied) out.push({ label: 'Applied', value: applied })
  if (before && after) {
    if (before.score > 0) {
      const delta = Math.round((after.score / before.score - 1) * 100)
      out.push({ label: 'Score', value: delta > 0 ? `+${delta}%` : delta < 0 ? `${delta}%` : 'Same' })
    }
    if (before.peakC != null && after.peakC != null) {
      out.push({ label: 'Peak temp', value: `${Math.round(before.peakC)} to ${Math.round(after.peakC)} °C` })
    }
  }
  return out
}

export function useTuneReads(active: boolean, tune: AutoTune | null, open: boolean, onTune: (next: AutoTune) => void) {
  const testing = tune?.parts.some(part => part.state === 'testing') ?? false
  useEffect(() => {
    if (!active || (!testing && !open)) return
    const timer = window.setInterval(() => {
      host.systemTune().then(onTune, (e: unknown) => report(e, 'AutoTune.read', { level: 'warn' }))
    }, 3000)
    return () => window.clearInterval(timer)
  }, [active, testing, open, onTune])
}

export function AutoTuneRows({ tune, profile, about, onTune }: {
  tune: AutoTune | null
  profile: GpuProfile | null
  about: PcAbout | null
  onTune: (next: AutoTune) => void
}) {
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  const said = (part: TunePart['id'], text: string | null) => setErrors(current => ({ ...current, [part]: text }))
  const act = async (part: TunePart['id'], action: TuneAction) => {
    said(part, null)
    const next = await host.systemTune(action, part, action === 'start' ? 'faster' : undefined)
    if (!next.ok) onTune(next)
    return next
  }
  const partName = (part: TunePart) =>
    part.id === 'ryzen' ? about?.processor ?? 'Processor' : profile?.gpu ?? about?.graphics ?? 'Graphics'
  const parts = tune?.parts ?? []

  if (parts.length === 0) {
    return <p className="exo-at-empty">No supported desktop GPU or Ryzen desktop CPU detected on this PC.</p>
  }

  return (
    <div className="exo-at-hw-list">
      {parts.map(part => (
        <HardwareRow
          key={part.id}
          part={part}
          name={partName(part)}
          error={errors[part.id] ?? null}
          onAct={action => act(part.id, action)}
          onSettled={next => onTune(next as AutoTune)}
          onFailed={text => said(part.id, text)}
        />
      ))}
    </div>
  )
}

function makePress(
  action: TuneAction,
  onAct: (action: TuneAction) => Promise<unknown>,
  onSettled: (next: unknown) => void,
  onFailed: (text: string) => void,
): Press {
  return {
    onPress: () => onAct(action),
    onDone: onSettled,
    onFailed: failure => onFailed(failure.text),
    why: 'none',
    context: `AutoTune.${action}`,
    fallback: action === 'start' ? 'That didn’t start. Try again.' : 'Not changed. Try again.',
  }
}

function openPawnIo() {
  host.openUrl('https://pawnio.eu').catch((e: unknown) => report(e, 'AutoTune.pawnio'))
}

function HardwareRow({ part, name, error, onAct, onSettled, onFailed }: {
  part: TunePart
  name: string
  error: string | null
  onAct: (action: TuneAction) => Promise<unknown>
  onSettled: (next: unknown) => void
  onFailed: (text: string) => void
}) {
  const press = (action: TuneAction) => makePress(action, onAct, onSettled, onFailed)
  const ready = part.state === 'ready' || part.state === 'crashed'
  const isCpu = part.id === 'ryzen'
  const roleLabel = isCpu ? 'CPU undervolt (Curve Optimizer)' : 'GPU overclock & undervolt'
  const tuneLabel = isCpu ? 'Tune CPU' : 'Tune GPU'
  const testingLabel = `${part.testing ?? 'Testing'}${part.minutesLeft ? ` · about ${part.minutesLeft} min left` : ''}`
  const metrics = part.state === 'tuned' ? tuneMetrics(part.applied, part.before, part.after) : []
  const fallbackDetail = part.blocked ?? part.warning ?? part.note ?? 'Measures stock and applies maximum stable headroom'

  return (
    <div className="exo-at-hw-row">
      <div className="exo-at-hw-head">
        <div className="exo-at-hw-id">
          {isCpu ? <Cpu size={16} /> : <GraphicsCard size={16} />}
          <div className="exo-at-hw-copy">
            <span className="exo-at-hw-role">{roleLabel}</span>
            <span className="exo-at-hw-name">{name}</span>
          </div>
        </div>
        <div className="exo-at-hw-actions">
          {part.state === 'tuned' ? <State on>Tuned</State> : null}
          {part.state === 'needs-driver'
            ? <button type="button" className="exo-set-btn" onClick={openPawnIo}>Get PawnIO</button>
            : null}
          {ready ? <TuneButton warned={!!part.warning} blocked={!!part.blocked} label={tuneLabel} press={press('start')} /> : null}
          <EndButton part={part} press={press} />
        </div>
      </div>

      {part.state === 'testing' ? (
        <div className="exo-at-hw-progress">
          <Progress value={null} label={testingLabel} caption />
        </div>
      ) : metrics.length > 0 ? (
        <div className="exo-at-hw-results">
          {metrics.map(item => (
            <div key={item.label} className="exo-at-hw-metric">
              <span className="exo-at-hw-metric-label">{item.label}</span>
              <span className="exo-at-hw-metric-value">{item.value}</span>
            </div>
          ))}
        </div>
      ) : (
        <p className="exo-at-hw-detail">{fallbackDetail}</p>
      )}

      {error ? <ErrorNote className="exo-set-error" message={error} /> : null}
    </div>
  )
}

function TuneButton({ warned, blocked, label, press }: { warned: boolean; blocked: boolean; label: string; press: Press }) {
  const { onPress, ...rest } = press
  if (warned) {
    return (
      <ConfirmButton
        className="exo-set-btn is-primary"
        question="Start at your own risk?"
        disabled={blocked}
        timeoutMs={null}
        onConfirm={onPress}
        {...rest}
      >
        {label}
      </ConfirmButton>
    )
  }
  return (
    <ActionButton className="exo-set-btn is-primary" disabled={blocked} timeoutMs={null} onPress={onPress} {...rest}>
      {label}
    </ActionButton>
  )
}

function EndButton({ part, press }: { part: TunePart; press: (action: TuneAction) => Press }) {
  if (part.state === 'testing') return <ActionButton className="exo-set-btn" {...press('stop')}>Stop</ActionButton>
  if (!part.canUndo) return null
  return <ActionButton className="exo-set-btn" timeoutMs={null} {...press('undo')}>Undo</ActionButton>
}
