import { useCallback, useEffect, useState } from 'react'
import {
  host,
  onHostEvent,
  type GpuFanPoint,
  type GpuManualProfile,
  type GpuManualTuningState,
  type GpuStabilityProgress,
  type GpuTuningCapabilities,
  type GpuTuningRange,
  type GpuVfPoint,
} from '../../lib/host'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
import { Progress } from '../ui/Progress'
import { Slider } from '../ui/Slider'
import { FanCurveEditor } from './FanCurveEditor'
import { VfCurveEditor } from './VfCurveEditor'
import '../../styles/gpu-tuning.css'

const MODE_CHOICES = [
  { value: 'auto', label: 'Auto' },
  { value: 'manual', label: 'Manual' },
] as const

const TEST_DURATIONS = [
  { value: '60s', label: '60 s' },
  { value: '5m', label: '5 min' },
] as const

const DEFAULT_FAN_POINTS: readonly GpuFanPoint[] = [
  { tempC: 35, speedPercent: 30 },
  { tempC: 50, speedPercent: 45 },
  { tempC: 65, speedPercent: 65 },
  { tempC: 78, speedPercent: 85 },
  { tempC: 85, speedPercent: 100 },
]

function pickRange(primary: number | null | undefined, fallback: number | null | undefined, range: GpuTuningRange): number | null {
  return range.supported ? (primary ?? fallback ?? range.default) : null
}

function initialFanPoints(src: GpuManualProfile, def: GpuManualProfile): GpuFanPoint[] {
  if (src.fanCurve && src.fanCurve.length > 0) return src.fanCurve
  if (def.fanCurve && def.fanCurve.length > 0) return def.fanCurve
  return [...DEFAULT_FAN_POINTS]
}

function draftFromState(state: GpuManualTuningState): GpuManualProfile {
  const caps = state.capabilities
  const src = state.mode === 'manual' ? state.saved : state.current
  const def = state.factoryDefaults
  const hasFan = caps.supportsHardwareFanCurve || caps.supportsSoftwareFanCurve
  const vf = src.vfCurve && src.vfCurve.length > 0 ? src.vfCurve : caps.vfCurvePoints
  return {
    mode: state.mode,
    coreClockOffsetMhz: pickRange(src.coreClockOffsetMhz, def.coreClockOffsetMhz, caps.coreClockOffsetMhz),
    coreMinClockMhz: pickRange(src.coreMinClockMhz, def.coreMinClockMhz, caps.coreMinClockMhz),
    coreMaxClockMhz: pickRange(src.coreMaxClockMhz, def.coreMaxClockMhz, caps.coreMaxClockMhz),
    voltageMv: pickRange(src.voltageMv, def.voltageMv, caps.voltageMv),
    voltageOffsetMv: pickRange(src.voltageOffsetMv, def.voltageOffsetMv, caps.voltageOffsetMv),
    memoryClockOffsetMhz: pickRange(src.memoryClockOffsetMhz, def.memoryClockOffsetMhz, caps.memoryClockOffsetMhz),
    memoryMaxClockMhz: pickRange(src.memoryMaxClockMhz, def.memoryMaxClockMhz, caps.memoryMaxClockMhz),
    memoryTimingLevel: caps.memoryTimingLevels.length > 0 ? (src.memoryTimingLevel ?? def.memoryTimingLevel ?? caps.memoryTimingLevels[0]) : null,
    powerLimitPercent: pickRange(src.powerLimitPercent, def.powerLimitPercent, caps.powerLimitPercent),
    powerLimitWatts: pickRange(src.powerLimitWatts, def.powerLimitWatts, caps.powerLimitWatts),
    tempLimitC: pickRange(src.tempLimitC, def.tempLimitC, caps.tempLimitC),
    clockLockMhz: pickRange(src.clockLockMhz, def.clockLockMhz, caps.clockLockMhz),
    vfCurve: caps.supportsCurveEditor ? vf : null,
    zeroRpm: caps.supportsZeroRpm ? (src.zeroRpm ?? def.zeroRpm ?? true) : null,
    fanCurve: hasFan ? initialFanPoints(src, def) : null,
    fanChannels: src.fanChannels ?? null,
  }
}

function RangeSliderRow({
  label,
  range,
  value,
  unit,
  signed,
  disabled,
  onChange,
}: {
  label: string
  range: GpuTuningRange
  value: number | null | undefined
  unit: string
  signed?: boolean
  disabled?: boolean
  onChange: (next: number) => void
}) {
  if (!range.supported) return null
  const current = value ?? range.default
  const text = `${signed && current > 0 ? `+${current}` : current} ${unit}`
  return (
    <Row label={label} detail={`${range.min} to ${range.max} ${unit}`}>
      <div className="exo-gpu-tune-slider">
        <Slider
          aria-label={label}
          min={range.min}
          max={range.max}
          step={Math.max(1, range.step)}
          value={current}
          disabled={disabled}
          onChange={event => onChange(Number(event.currentTarget.value))}
        />
        <span className="exo-gpu-tune-val">{text}</span>
      </div>
    </Row>
  )
}

function StatusHeader({ state }: { state: GpuManualTuningState }) {
  const warn = state.pausedAfterCrash || state.queuedPending || state.capabilities.lockedByMaker
  const tone = warn ? 'is-warn' : state.mode === 'manual' ? 'is-good' : ''
  const live = state.live
  const hasTelemetry = live.tempC != null || live.clockMhz != null || live.watts != null
  return (
    <div className={`exo-gpu-tune-status ${tone}`}>
      <span>{state.statusLine}</span>
      {hasTelemetry ? (
        <span className="exo-gpu-tune-telemetry">
          {live.tempC != null ? <span>{Math.round(live.tempC)} °C</span> : null}
          {live.clockMhz != null ? <span>{live.clockMhz} MHz</span> : null}
          {live.watts != null ? <span>{Math.round(live.watts)} W</span> : null}
        </span>
      ) : null}
    </div>
  )
}

function TestProgressBox({ test }: { test: GpuStabilityProgress }) {
  if (!test.running && !test.summary) return null
  const summaryTone = test.passed === true ? 'is-good' : test.passed === false ? 'is-bad' : ''
  const ratio = test.totalSec > 0 ? test.elapsedSec / test.totalSec : null
  return (
    <div className="exo-gpu-test-box">
      {test.running ? (
        <Progress value={ratio} label={test.currentStep ?? 'Running GPU stability test'} caption />
      ) : null}
      {test.summary ? (
        <p className={`exo-gpu-test-summary ${summaryTone}`}>{test.summary}</p>
      ) : null}
    </div>
  )
}

function ManualControls({
  caps,
  draft,
  disabled,
  selectedFan,
  onSelectFan,
  onPatch,
}: {
  caps: GpuTuningCapabilities
  draft: GpuManualProfile
  disabled: boolean
  selectedFan: number
  onSelectFan: (fan: number) => void
  onPatch: (patch: Partial<GpuManualProfile>) => void
}) {
  const activeFanPoints = (): GpuFanPoint[] => {
    const ch = draft.fanChannels?.find(item => item.fanIndex === selectedFan)
    if (ch && ch.points.length > 0) return ch.points
    return draft.fanCurve && draft.fanCurve.length > 0 ? draft.fanCurve : [...DEFAULT_FAN_POINTS]
  }

  const updateFanPoints = (nextPoints: GpuFanPoint[]) => {
    if (caps.fanCount <= 1) {
      onPatch({ fanCurve: nextPoints })
      return
    }
    const base = draft.fanChannels ?? Array.from({ length: caps.fanCount }, (_, idx) => ({
      fanIndex: idx,
      manual: true,
      points: draft.fanCurve && draft.fanCurve.length > 0 ? draft.fanCurve : [...DEFAULT_FAN_POINTS],
    }))
    const fanChannels = base.map(ch => (ch.fanIndex === selectedFan ? { ...ch, manual: true, points: nextPoints } : ch))
    onPatch({ fanCurve: selectedFan === 0 ? nextPoints : draft.fanCurve, fanChannels })
  }

  const updateVfCurve = (nextPoints: GpuVfPoint[]) => {
    if (caps.vendor === 'amd') {
      onPatch({ voltageOffsetMv: nextPoints[0]?.offsetMhz ?? draft.voltageOffsetMv ?? 0, vfCurve: nextPoints })
      return
    }
    onPatch({ vfCurve: nextPoints })
  }

  const showFan = caps.supportsHardwareFanCurve || caps.supportsSoftwareFanCurve
  const showCurve = caps.supportsCurveEditor && Boolean(draft.vfCurve?.length)

  return (
    <>
      <RangeSliderRow label="Core clock offset" range={caps.coreClockOffsetMhz} value={draft.coreClockOffsetMhz} unit="MHz" signed disabled={disabled} onChange={v => onPatch({ coreClockOffsetMhz: v })} />
      <RangeSliderRow label="GPU min clock" range={caps.coreMinClockMhz} value={draft.coreMinClockMhz} unit="MHz" disabled={disabled} onChange={v => onPatch({ coreMinClockMhz: v })} />
      <RangeSliderRow label="GPU max clock" range={caps.coreMaxClockMhz} value={draft.coreMaxClockMhz} unit="MHz" disabled={disabled} onChange={v => onPatch({ coreMaxClockMhz: v })} />
      <RangeSliderRow label="GPU voltage" range={caps.voltageMv} value={draft.voltageMv} unit="mV" disabled={disabled} onChange={v => onPatch({ voltageMv: v })} />
      <RangeSliderRow label="Voltage offset" range={caps.voltageOffsetMv} value={draft.voltageOffsetMv} unit="mV" signed disabled={disabled} onChange={v => onPatch({ voltageOffsetMv: v })} />
      <RangeSliderRow label="Memory clock offset" range={caps.memoryClockOffsetMhz} value={draft.memoryClockOffsetMhz} unit="MHz" signed disabled={disabled} onChange={v => onPatch({ memoryClockOffsetMhz: v })} />
      <RangeSliderRow label="VRAM clock limit" range={caps.memoryMaxClockMhz} value={draft.memoryMaxClockMhz} unit={caps.vendor === 'intel' ? 'MT/s' : 'MHz'} disabled={disabled} onChange={v => onPatch({ memoryMaxClockMhz: v })} />
      {caps.memoryTimingLevels.length > 1 ? (
        <Row label="Memory timing">
          <Choice
            label="Memory timing level"
            value={draft.memoryTimingLevel ?? caps.memoryTimingLevels[0]}
            choices={caps.memoryTimingLevels.map(level => ({ value: level, label: level }))}
            disabled={disabled}
            onChange={value => onPatch({ memoryTimingLevel: value })}
          />
        </Row>
      ) : null}
      <RangeSliderRow label="Power limit" range={caps.powerLimitPercent} value={draft.powerLimitPercent} unit="%" signed={caps.vendor === 'amd'} disabled={disabled} onChange={v => onPatch({ powerLimitPercent: v })} />
      <RangeSliderRow label="Power limit" range={caps.powerLimitWatts} value={draft.powerLimitWatts} unit="W" disabled={disabled} onChange={v => onPatch({ powerLimitWatts: v })} />
      <RangeSliderRow label="Temperature limit" range={caps.tempLimitC} value={draft.tempLimitC} unit="°C" disabled={disabled} onChange={v => onPatch({ tempLimitC: v })} />
      <RangeSliderRow label="Clock ceiling lock" range={caps.clockLockMhz} value={draft.clockLockMhz} unit="MHz" disabled={disabled} onChange={v => onPatch({ clockLockMhz: v })} />

      {showCurve && draft.vfCurve ? (
        <VfCurveEditor
          points={draft.vfCurve}
          offsetRange={caps.vendor === 'amd' ? caps.voltageOffsetMv : caps.coreClockOffsetMhz}
          isAmdCurveOffset={caps.vendor === 'amd'}
          disabled={disabled}
          onChange={updateVfCurve}
        />
      ) : null}

      {showFan ? (
        <FanCurveEditor
          points={activeFanPoints()}
          tempRange={caps.fanTempC}
          speedRange={caps.fanSpeedPercent}
          fanCount={caps.fanCount}
          selectedFan={selectedFan}
          onSelectFan={onSelectFan}
          supportsZeroRpm={caps.supportsZeroRpm}
          zeroRpm={draft.zeroRpm === true}
          onZeroRpm={v => onPatch({ zeroRpm: v })}
          needsResident={caps.needsResidentForFanCurve}
          disabled={disabled}
          onChange={updateFanPoints}
        />
      ) : null}
    </>
  )
}

function ActionBar({
  state,
  duration,
  onDuration,
  needsWaiver,
  controlsDisabled,
  onToggleTest,
  onToggleUndervolt,
  onReapply,
  onReset,
  onApply,
  onSync,
  onError,
}: {
  state: GpuManualTuningState
  duration: '60s' | '5m'
  onDuration: (next: '60s' | '5m') => void
  needsWaiver: boolean
  controlsDisabled: boolean
  onToggleTest: () => Promise<GpuManualTuningState>
  onToggleUndervolt: () => Promise<GpuManualTuningState>
  onReapply: () => Promise<GpuManualTuningState>
  onReset: () => Promise<GpuManualTuningState>
  onApply: () => Promise<GpuManualTuningState>
  onSync: (next: GpuManualTuningState, resetDraft?: boolean) => void
  onError: (msg: string) => void
}) {
  const testing = state.test.running
  const testLabel = testing && state.test.kind === 'stability' ? 'Stop test' : 'Test'
  const uvLabel = testing && state.test.kind === 'undervolt' ? 'Stop search' : 'Find my undervolt'
  return (
    <div className="exo-gpu-actions">
      <div className="exo-gpu-actions-left">
        <Choice
          label="Stability test length"
          value={duration}
          choices={TEST_DURATIONS}
          disabled={testing || needsWaiver}
          onChange={value => onDuration(value === '5m' ? '5m' : '60s')}
        />
        <ActionButton
          className="exo-set-btn"
          why="none"
          disabled={needsWaiver}
          timeoutMs={null}
          context="GpuManualTuning.test"
          fallback="Stability test could not start."
          onPress={onToggleTest}
          onDone={next => onSync(next as GpuManualTuningState)}
          onFailed={failure => onError(failure.text)}
        >
          {testLabel}
        </ActionButton>
        {state.capabilities.supportsUndervoltSearch ? (
          <ActionButton
            className="exo-set-btn"
            why="none"
            disabled={needsWaiver || (testing && state.test.kind !== 'undervolt')}
            timeoutMs={null}
            context="GpuManualTuning.undervolt"
            fallback="Undervolt search could not start."
            onPress={onToggleUndervolt}
            onDone={next => onSync(next as GpuManualTuningState)}
            onFailed={failure => onError(failure.text)}
          >
            {uvLabel}
          </ActionButton>
        ) : null}
      </div>

      <div className="exo-gpu-actions-right">
        {state.pausedAfterCrash && state.canReapply ? (
          <ActionButton
            className="exo-set-btn is-primary"
            why="none"
            disabled={testing}
            timeoutMs={null}
            context="GpuManualTuning.reapply"
            fallback="Could not re-apply saved tuning."
            onPress={onReapply}
            onDone={next => onSync(next as GpuManualTuningState, true)}
            onFailed={failure => onError(failure.text)}
          >
            Re-apply
          </ActionButton>
        ) : null}
        <ConfirmButton
          className="exo-set-btn is-danger"
          question="Reset GPU to factory defaults?"
          why="none"
          disabled={testing}
          timeoutMs={null}
          context="GpuManualTuning.reset"
          fallback="Reset failed."
          onConfirm={onReset}
          onDone={next => onSync(next as GpuManualTuningState, true)}
          onFailed={failure => onError(failure.text)}
        >
          Reset
        </ConfirmButton>
        <ActionButton
          className="exo-set-btn is-primary"
          why="none"
          disabled={controlsDisabled}
          timeoutMs={null}
          context="GpuManualTuning.apply"
          fallback="Could not apply GPU tuning."
          onPress={onApply}
          onDone={next => onSync(next as GpuManualTuningState, true)}
          onFailed={failure => onError(failure.text)}
        >
          Apply
        </ActionButton>
      </div>
    </div>
  )
}

export function GpuManualTuning({ active }: { active: boolean }) {
  const [state, setState] = useState<GpuManualTuningState | null>(null)
  const [draft, setDraft] = useState<GpuManualProfile | null>(null)
  const [duration, setDuration] = useState<'60s' | '5m'>('60s')
  const [selectedFan, setSelectedFan] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [waiverBusy, setWaiverBusy] = useState(false)

  const syncFromHost = useCallback((next: GpuManualTuningState, resetDraft = false) => {
    setState(next)
    setDraft(prev => (resetDraft || !prev ? draftFromState(next) : { ...prev, mode: next.mode }))
    if (!next.ok && next.message) setError(next.message)
  }, [])

  const load = useCallback(() => {
    host.gpuTuningState().then(
      next => syncFromHost(next, true),
      (e: unknown) => report(e, 'GpuManualTuning.state', { level: 'warn' }),
    )
  }, [syncFromHost])

  useEffect(() => {
    if (!active) return
    load()
    const offChanged = onHostEvent('gpu.tuningChanged', () => { load() })
    const offProgress = onHostEvent('gpu.tuningTestProgress', progress => {
      setState(prev => (prev ? { ...prev, test: progress } : prev))
    })
    return () => {
      offChanged()
      offProgress()
    }
  }, [active, load])

  if (!state || !draft) return null

  const caps = state.capabilities
  const testing = state.test.running
  const locked = caps.lockedByMaker
  const needsWaiver = caps.requiresWaiver && !caps.waiverAccepted
  const controlsDisabled = locked || needsWaiver || testing || draft.mode !== 'manual'

  const patchDraft = (patch: Partial<GpuManualProfile>) => {
    setDraft(prev => (prev ? { ...prev, ...patch } : prev))
  }

  const acceptWaiver = async () => {
    setError(null)
    setWaiverBusy(true)
    try {
      const next = await host.gpuTuningWaiver(true, caps.deviceId)
      syncFromHost(next, true)
    } catch (e) {
      report(e, 'GpuManualTuning.waiver')
      setError('Could not save waiver consent. Try again.')
    } finally {
      setWaiverBusy(false)
    }
  }

  const resetToStock = async () => {
    setError(null)
    const next = await host.gpuTuningReset()
    syncFromHost(next, true)
    return next
  }

  const applyDraft = async () => {
    setError(null)
    const next = await host.gpuTuningApply(draft)
    syncFromHost(next, next.ok)
    return next
  }

  const reapplySaved = async () => {
    setError(null)
    const next = await host.gpuTuningApply({ mode: 'manual', reapply: true })
    syncFromHost(next, next.ok)
    return next
  }

  const toggleStabilityTest = async () => {
    setError(null)
    const next = testing
      ? await host.gpuTuningTest('stop')
      : await host.gpuTuningTest('start', duration, draft.mode === 'manual' ? draft : undefined)
    syncFromHost(next)
    return next
  }

  const toggleUndervolt = async () => {
    setError(null)
    const next = testing ? await host.gpuTuningUndervolt('stop') : await host.gpuTuningUndervolt('start')
    syncFromHost(next)
    return next
  }

  return (
    <div className="exo-gpu-tune">
      <StatusHeader state={state} />

      {locked ? (
        <Row label="Tuning mode" detail={caps.lockReason ?? 'Locked by your GPU maker'}>
          <State>Locked</State>
        </Row>
      ) : (
        <>
          <Row label="Tuning mode" detail={caps.fallbackReason ?? caps.persistenceNote ?? undefined}>
            <Choice
              label="GPU tuning mode"
              value={draft.mode}
              choices={MODE_CHOICES}
              disabled={testing}
              onChange={value => {
                const nextMode = value === 'manual' ? 'manual' : 'auto'
                patchDraft({ mode: nextMode })
                if (nextMode === 'auto' && state.mode === 'manual') void resetToStock()
              }}
            />
          </Row>

          {needsWaiver ? (
            <div className="exo-gpu-tune-waiver">
              <p>{caps.waiverText}</p>
              <div className="exo-gpu-tune-waiver-actions">
                <button type="button" className="exo-set-btn is-primary" disabled={waiverBusy} onClick={() => void acceptWaiver()}>
                  I understand, unlock tuning
                </button>
              </div>
            </div>
          ) : null}

          {draft.mode === 'manual' && !needsWaiver ? (
            <ManualControls
              caps={caps}
              draft={draft}
              disabled={controlsDisabled}
              selectedFan={selectedFan}
              onSelectFan={setSelectedFan}
              onPatch={patchDraft}
            />
          ) : null}

          <TestProgressBox test={state.test} />

          <ActionBar
            state={state}
            duration={duration}
            onDuration={setDuration}
            needsWaiver={needsWaiver}
            controlsDisabled={controlsDisabled}
            onToggleTest={toggleStabilityTest}
            onToggleUndervolt={toggleUndervolt}
            onReapply={reapplySaved}
            onReset={resetToStock}
            onApply={applyDraft}
            onSync={syncFromHost}
            onError={setError}
          />
        </>
      )}

      {error ? <ErrorNote className="exo-set-error" message={error} /> : null}
    </div>
  )
}
