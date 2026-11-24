import { useEffect, useState, type ReactNode } from 'react'
import { host, onHostEvent, type ColorControl, type DisplayChange, type DisplayScreen, type DisplayState, type HdrCalibrateResult, type HdrCalibrationProgress, type HdrState, type LauncherSettings, type OutputColor } from '../../lib/host'
import { report } from '../../lib/report'
import { GraphicsControl } from '../graphics/GraphicsControl'
import { Row, SliderRow, State, SwitchRow } from '../settings/parts'
import { Switch } from '../Switch'
import { Spinner } from '../ui/Spinner'
import { ActionButton } from '../ui/ConfirmButton'
import { hdrShown, isChoice } from '../../lib/canUse'

const FORMATS: { id: OutputColor['format']; label: string }[] = [
  { id: 'rgb', label: 'RGB' },
  { id: '444', label: '4:4:4' },
  { id: '422', label: '4:2:2' },
  { id: '420', label: '4:2:0' },
]

const FORMAT_NAME: Record<string, string> = { rgb: 'RGB', 444: 'YCbCr 4:4:4', 422: 'YCbCr 4:2:2', 420: 'YCbCr 4:2:0' }

function describe(color: OutputColor) {
  return `${FORMAT_NAME[color.format]} ${color.bpc}-bit${color.format === 'rgb' ? (color.full ? ', full range' : ', limited range') : ''}`
}

/** A menu with the ring before it while its change is written (DESIGN.md › Waits). */
function Busy({ on, label, children }: { on: boolean; label: string; children: ReactNode }) {
  return <span className="exo-display-busy">{on ? <Spinner label={label} /> : null}{children}</span>
}

type Action = 'set' | 'adjust' | 'video' | 'ddc'

/**
 * One display change at a time, for the rows that make it: the row whose change is being written (null when none) and
 * the line it failed with, keyed by the row. The answer is every screen's state, handed to the room.
 */
function useDisplayChange(onDisplay: (next: DisplayState) => void) {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ key: string; text: string } | null>(null)
  const run = async (action: Action, change: DisplayChange, key: string): Promise<string | null> => {
    setBusy(key)
    let text: string | null
    try {
      const next = await host.gpuDisplay(action, change)
      onDisplay(next)
      text = next.ok ? null : (next.message ?? 'Not changed. Try again').replace(/\.$/, '')
    } catch (e) {
      report(e, `DisplayRows.${action}`)
      text = 'Not changed. Try again'
    }
    setError(text === null ? null : { key, text })
    setBusy(null)
    return text
  }
  return { busy, error, errorFor: (key: string) => (error?.key === key ? error.text : null), clear: () => setError(null), run }
}

/**
 * PC › Needs you: a screen not at its best ("165 Hz, RGB 10-bit, full range, no scaling on the GPU", what the screen
 * reports it takes), with Use. A screen at its best shows nothing.
 */
export function BestScreenRows({ display, onDisplay }: { display: DisplayState | null; onDisplay: (next: DisplayState) => void }) {
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  if (!display) return null
  const several = display.displays.length > 1
  // Use shows its own work (ActionButton): the ring, then its check, and then the row goes as the screen is at its best.
  const use = (id: string) => async () => {
    setErrors(current => ({ ...current, [id]: null }))
    const next = await host.gpuDisplay('set', { display: id, best: true })
    if (!next.ok) onDisplay(next)
    return next
  }
  return (
    <>
      {display.displays.filter(screen => !screen.isBest).map(screen => (
        <Row key={screen.id} label={several ? `Best for ${screen.name}` : 'Best for this screen'} detail={bestLine(screen.best)} error={errors[screen.id] ?? null}>
          <ActionButton className="exo-set-btn is-primary" why="none" context="DisplayRows.set" fallback="Not changed. Try again."
            onPress={use(screen.id)} onFailed={failure => setErrors(current => ({ ...current, [screen.id]: failure.text }))}
            onDone={next => onDisplay(next as DisplayState)}>Use best settings</ActionButton>
        </Row>
      ))}
    </>
  )
}

/**
 * PC › Display: per screen (a menu picks it when there are several), the refresh rate and what the cable carries
 * (format, depth, range), RTX Video, the scaling, and the color sliders with Restore while they're off normal. A change
 * applies and keeps itself, so the next can follow at once: Exo offers only the modes the screen reports it takes.
 */
export function DisplayRows({ display, onDisplay }: { display: DisplayState | null; onDisplay: (next: DisplayState) => void }) {
  const [selected, setSelected] = useState<string | null>(null)
  const { busy, error, errorFor, clear, run } = useDisplayChange(onDisplay)

  if (!display || display.displays.length === 0) return null
  const screen = display.displays.find(item => item.id === selected) ?? display.displays.find(item => item.main) ?? display.displays[0]
  const set = (key: string, change: Omit<DisplayChange, 'display'>) => {
    if (!busy) void run('set', { display: screen.id, ...change }, key)
  }

  return (
    <>
      {display.displays.length > 1 && (
        <Row label="Screen">
          <GraphicsControl
            row={{ key: 'screen', label: 'Screen', detail: '', choice: screen.id, choices: display.displays.map(item => ({ id: item.id, label: item.name })) }}
            busy={false}
            onPick={id => { setSelected(id); clear() }}
          />
        </Row>
      )}
      <ScreenRows screen={screen} busy={busy} error={error} onSet={set} />
      {screen.hdrSupported && (
        <SwitchRow id={`display-hdr-${screen.id}`} label="HDR" checked={screen.hdr} disabled={!!busy}
          onChange={on => run('set', { display: screen.id, hdr: on }, 'hdr')} />
      )}
      {screen.ddc && (
        <DdcRows ddc={screen.ddc} busy={busy} errorFor={errorFor}
          onDdc={(key, value) => { if (!busy) void run('ddc', { display: screen.id, [key]: value }, key === 'brightness' ? 'ddcBrightness' : 'ddcContrast') }} />
      )}
      {screen.adjust && (
        <ColorRows
          key={screen.id}
          adjust={screen.adjust}
          busy={busy}
          error={error}
          onColor={(key, change) => { if (!busy) void run('adjust', { display: screen.id, ...change }, key) }}
        />
      )}
    </>
  )
}

const VERDICT: Record<string, string> = {
  great: 'Bright enough for real HDR',
  good: 'Fine for HDR',
  weak: 'Too dim for good HDR',
  unknown: 'Brightness not reported',
}

const COLOR_MODE_LABEL: Record<string, string> = {
  hdr: 'HDR',
  acm: 'Auto Color Management (ACM)',
  sdr: 'SDR',
}

function formatPeakSummary(edid?: number | null, dxgi?: number | null): string {
  if (edid && dxgi) return `${edid} nits EDID · ${dxgi} nits reported`
  if (edid) return `${edid} nits EDID`
  if (dxgi) return `${dxgi} nits reported`
  return ''
}

function PeakHealthRow({ screen }: { screen: HdrState }) {
  const health = screen.peakHealth
  const summary = formatPeakSummary(health?.edidNits, health?.dxgiNits)
  if (!health || !summary) return null
  return (
    <Row label="Peak luminance" detail={health.warning ? <span className="exo-hdr-warn">{health.warning}</span> : undefined}>
      <State on={health.status === 'ok'}>
        <span className={health.status === 'mismatch' ? 'exo-hdr-warn' : undefined}>{summary}</span>
      </State>
    </Row>
  )
}

function LaptopCalibrationRow({ screen, calibrating, progress, calResult, calError, onRun, onUndo, onToggle }: {
  screen: HdrState
  calibrating: boolean
  progress: HdrCalibrationProgress | null
  calResult: HdrCalibrateResult | null
  calError: string | null
  onRun: () => void
  onUndo: () => void
  onToggle: (enabled: boolean) => void
}) {
  if (!screen.internal) return null
  if (screen.edidPeakNits == null) {
    return (
      <Row label="Laptop HDR calibration" detail="Panel EDID peak is unavailable, so neutral-pair calibration is skipped.">
        <State>Skipped</State>
      </Row>
    )
  }
  const cal = screen.calibration
  const detail: ReactNode = calResult?.manualSteps
    ? (
      <>
        <span>{calResult.message}</span>
        <ol className="exo-hdr-steps">
          {calResult.manualSteps.map(step => <li key={step}>{step}</li>)}
        </ol>
      </>
    )
    : cal
      ? `Brightness ${cal.globalBrightness}% · HDR content brightness ${Math.round(cal.sdrWhiteNits)} nits · reports ${cal.measuredPeakNits} nits (EDID ${cal.edidPeakNits} nits)`
      : calResult?.ok
        ? calResult.message
        : 'Finds the brightness pair where games see this panel’s true EDID peak on launch and restores yours on exit.'
  const progressText = progress
    ? `Step ${progress.step} of ${progress.total}${progress.measuredNits ? ` · ${progress.measuredNits} nits` : ''}`
    : 'Calibrating…'

  return (
    <Row label="Laptop HDR calibration" detail={detail} error={calError}>
      {calibrating ? (
        <span className="exo-display-busy">
          <Spinner label="Calibrating laptop HDR sliders" />
          <State>{progressText}</State>
        </span>
      ) : cal ? (
        <>
          <Switch checked={cal.enabled} label="Apply calibrated laptop HDR brightness pair on game launch" onChange={onToggle} />
          <button type="button" className="exo-set-btn" onClick={onRun}>Recalibrate</button>
          <button type="button" className="exo-set-btn" onClick={onUndo}>Undo</button>
        </>
      ) : (
        <button type="button" className={screen.peakHealth?.status === 'mismatch' ? 'exo-set-btn is-primary' : 'exo-set-btn'} onClick={onRun}>Calibrate</button>
      )}
    </Row>
  )
}

function LaptopCheckRows({ screen }: { screen: HdrState }) {
  const colorModeText = screen.colorMode ? (COLOR_MODE_LABEL[screen.colorMode] ?? screen.colorMode) : null
  const autoHdrGlobalDetail = screen.autoHdrGlobal === true
    ? 'Windows Auto HDR is on globally (read-only).'
    : screen.autoHdrGlobal === false
      ? 'Windows Auto HDR is off globally (read-only).'
      : undefined
  const checkItems = screen.laptopChecks?.items ?? []

  return (
    <>
      {screen.brightnessWarning ? (
        <Row label="Brightness changed" detail={<span className="exo-hdr-warn">{screen.brightnessWarning}</span>}>
          <State><span className="exo-hdr-warn">Notice</span></State>
        </Row>
      ) : null}
      {colorModeText || autoHdrGlobalDetail ? (
        <Row label="Color mode" detail={autoHdrGlobalDetail}>
          <State on={screen.colorMode === 'hdr'}>{colorModeText ?? 'Unavailable'}</State>
        </Row>
      ) : null}
      {checkItems.map(item => {
        const statusLabel = item.status === 'active'
          ? (item.warn ? 'Active' : 'On')
          : item.status === 'off' ? 'Off' : "Can't detect"
        return (
          <Row key={item.id} label={item.label} detail={item.warn ? <span className="exo-hdr-warn">{item.detail}</span> : item.detail}>
            <State on={item.status === 'off'}>
              <span className={item.warn ? 'exo-hdr-warn' : undefined}>{statusLabel}</span>
            </State>
          </Row>
        )
      })}
    </>
  )
}

/** PC › Display, HDR: the main screen's verdict, HDR for HDR games, and calibration; nothing on a screen without HDR. */
export function HdrRows({ screen, settings, onSave }: {
  screen: HdrState | null
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const [override, setOverride] = useState<HdrState | null>(null)
  const [calibrating, setCalibrating] = useState(false)
  const [progress, setProgress] = useState<HdrCalibrationProgress | null>(null)
  const [calResult, setCalResult] = useState<HdrCalibrateResult | null>(null)
  const [calError, setCalError] = useState<string | null>(null)

  useEffect(() => {
    setOverride(null)
  }, [screen])

  useEffect(() => {
    const offProgress = onHostEvent('hdr.calibrationProgress', p => setProgress(p))
    const offWarn = onHostEvent('hdr.warning', () => {
      host.hdrState().then(next => setOverride(next)).catch(e => report(e, 'HdrRows.warnReload'))
    })
    return () => { offProgress(); offWarn() }
  }, [])

  const live = override ?? screen
  // A main screen without HDR has no HDR rows at all (lib/canUse.ts).
  if (!live || !hdrShown(live)) return null

  const refreshHdr = async () => {
    try {
      setOverride(await host.hdrState())
    } catch (e) {
      report(e, 'HdrRows.refresh')
    }
  }

  const runAction = async (
    params: { action: 'run' | 'undo' | 'toggle'; enabled?: boolean },
    context: string,
    fallback: string,
  ) => {
    const isLong = params.action !== 'toggle'
    if (isLong) {
      setCalibrating(true)
      setProgress(null)
      setCalResult(null)
    }
    setCalError(null)
    try {
      const res = await host.hdrCalibrate({ ...params, devicePath: live.devicePath ?? undefined })
      if (isLong) setCalResult(res)
      if (!res.ok && !res.manualSteps) setCalError(res.message)
      await refreshHdr()
    } catch (e) {
      report(e, context)
      setCalError(fallback)
    } finally {
      if (isLong) {
        setCalibrating(false)
        setProgress(null)
      }
    }
  }

  return (
    <>
      <Row label="HDR">
        {live.internal ? <span className="exo-hdr-badge">Internal panel</span> : null}
        <State on={live.verdict !== 'weak'}>{VERDICT[live.verdict ?? 'unknown']}</State>
      </Row>
      <PeakHealthRow screen={live} />
      <SwitchRow
        id="set-hdr-games"
        label="Auto-enable HDR for HDR games"
        checked={settings?.hdrForGames === true}
        disabled={!settings}
        onChange={async value => await onSave({ hdrForGames: value }) ? null : 'Not saved. Try again.'}
      />
      <LaptopCalibrationRow
        screen={live}
        calibrating={calibrating}
        progress={progress}
        calResult={calResult}
        calError={calError}
        onRun={() => void runAction({ action: 'run' }, 'HdrRows.calibrate', 'Calibration failed. Try again.')}
        onUndo={() => void runAction({ action: 'undo' }, 'HdrRows.undo', 'Could not undo calibration. Try again.')}
        onToggle={enabled => void runAction({ action: 'toggle', enabled }, 'HdrRows.toggle', 'Could not update calibration setting. Try again.')}
      />
      <Row label="HDR calibration">
        {live.calibrated
          ? <State on>Calibrated</State>
          : <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('ms-windows-store://pdp/?productid=9N7F2SM5D1LR')}>Calibrate</button>}
      </Row>
      <LaptopCheckRows screen={live} />
    </>
  )
}

/**
 * Color sliders. NVIDIA: like NVIDIA Control Panel's "Adjust desktop color settings", only while
 * "Use NVIDIA color settings" is picked (the host keeps them and puts them back when Exo starts).
 * AMD: the driver's Custom Color. None of these can blank a screen, so they apply at once.
 */
function ColorRows({ adjust, busy, error, onColor }: {
  adjust: NonNullable<DisplayScreen['adjust']>
  busy: string | null
  error: { key: string; text: string } | null
  onColor: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  // NVIDIA: moving a slider turns "Use NVIDIA color settings" on; Restore hands color back to Windows,
  // with the calibration the screen had before. AMD (nvidia null): Restore puts the sliders to normal. At normal, no row.
  const nvidia = adjust.nvidia
  const changed = adjust.controls.some(control => control.value !== control.normal)
  return (
    <>
      {adjust.controls.map(control => (
        <SliderRow key={control.key} label={control.label} min={control.min} max={control.max} step={control.step} value={control.value} render={value => colorText(control, value)} disabled={!!busy} error={error?.key === control.key ? error.text : null} onCommit={value => onColor(control.key, { key: control.key, value })} />
      ))}
      {changed || nvidia === true ? (
        <Row label="Default color" error={error?.key === 'reset' ? error.text : null}>
          <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'reset'} onClick={() => onColor('reset', nvidia === null ? { reset: true } : { nvidia: false })}>
            {busy === 'reset' ? <Spinner label="Putting the screen's color back" /> : null}Restore
          </button>
        </Row>
      ) : null}
    </>
  )
}

function colorText(control: ColorControl, value: number) {
  return control.key === 'gamma' ? (value / 100).toFixed(2) : `${value}${control.unit}`
}

function bestLine(best: DisplayScreen['best']) {
  const line = [
    `${best.refresh} Hz`,
    best.color ? describe(best.color) : null,
    best.scaling ? 'aspect ratio on the GPU' : null,
    best.videoHdr ? 'RTX Video HDR on' : null,
  ].filter(Boolean).join(', ')
  return best.why && best.color && best.color.format !== 'rgb' ? `${line}. ${best.why}` : line
}

function DdcRows({ ddc, busy, errorFor, onDdc }: {
  ddc: NonNullable<DisplayScreen['ddc']>
  busy: string | null
  errorFor: (key: string) => string | null
  onDdc: (key: 'brightness' | 'contrast', value: number) => void
}) {
  return (
    <>
      {ddc.brightness !== null && (
        <SliderRow label="Monitor brightness" min={ddc.brightnessMin} max={ddc.brightnessMax} step={1} value={ddc.brightness}
          render={value => `${value}%`} disabled={!!busy} error={errorFor('ddcBrightness')}
          onCommit={value => onDdc('brightness', value)} />
      )}
      {ddc.contrast !== null && (
        <SliderRow label="Monitor contrast" min={ddc.contrastMin} max={ddc.contrastMax} step={1} value={ddc.contrast}
          render={value => `${value}%`} disabled={!!busy} error={errorFor('ddcContrast')}
          onCommit={value => onDdc('contrast', value)} />
      )}
    </>
  )
}

function ScreenRows({ screen, busy, error, onSet }: {
  screen: DisplayScreen
  busy: string | null
  error: { key: string; text: string } | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  const color = screen.color
  const errorFor = (key: string) => (error?.key === key ? error.text : null)

  const formats = FORMATS.filter(format => screen.supported.some(item => item.format === format.id))
  const depths = color ? [...new Set(screen.supported.filter(item => item.format === color.format).map(item => item.bpc))].sort((a, b) => a - b) : []
  const ranges = color?.format === 'rgb' ? [...new Set(screen.supported.filter(item => item.format === 'rgb' && item.bpc === color.bpc).map(item => item.full))] : []

  // A new format keeps the depth and range when it can, else takes its deepest and the range it has.
  const pickFormat = (format: string) => {
    if (!color) return
    const options = screen.supported.filter(item => item.format === format)
    const bpc = options.some(item => item.bpc === color.bpc) ? color.bpc : Math.max(...options.map(item => item.bpc))
    const full = format === 'rgb' && (options.some(item => item.bpc === bpc && item.full === color.full) ? color.full : options.some(item => item.bpc === bpc && item.full))
    onSet('format', { format, bpc, full })
  }

  return (
    <>
      {isChoice(screen.refreshes) && (
        <Row label="Refresh rate" error={errorFor('refresh')}>
          <Busy on={busy === 'refresh'} label="Changing the refresh rate">
            <GraphicsControl
              row={{ key: 'refresh', label: 'Refresh rate', detail: '', choice: String(screen.refresh), choices: screen.refreshes.map(hz => ({ id: String(hz), label: hz === screen.refresh && screen.refreshLabel ? screen.refreshLabel : `${hz} Hz` })) }}
              busy={!!busy}
              onPick={hz => onSet('refresh', { refresh: Number(hz) })}
            />
          </Busy>
        </Row>
      )}
      {/* Without the screen's graphics driver only the refresh rate changes here. */}
      {!screen.vendor || !color ? null : (
        <>
          {isChoice(formats) && (
            <Row label="Color format" error={errorFor('format')}>
              <Busy on={busy === 'format'} label="Changing the color format">
                <GraphicsControl
                  row={{ key: 'format', label: 'Color format', detail: '', choice: color.format, choices: formats }}
                  busy={!!busy}
                  onPick={pickFormat}
                />
              </Busy>
            </Row>
          )}
          {isChoice(depths) && (
            <Row label="Color depth" error={errorFor('bpc')}>
              <Busy on={busy === 'bpc'} label="Changing the color depth">
                <GraphicsControl
                  row={{ key: 'bpc', label: 'Color depth', detail: '', choice: String(color.bpc), choices: depths.map(bpc => ({ id: String(bpc), label: `${bpc}-bit` })) }}
                  busy={!!busy}
                  onPick={bpc => {
                    const full = screen.supported.some(item => item.format === color.format && item.bpc === Number(bpc) && item.full === color.full) ? color.full : !color.full
                    onSet('bpc', { format: color.format, bpc: Number(bpc), full: color.format === 'rgb' && full })
                  }}
                />
              </Busy>
            </Row>
          )}
          {color.format === 'rgb' && isChoice(ranges) && (
            <Row label="Dynamic range" error={errorFor('range')}>
              <Busy on={busy === 'range'} label="Changing the dynamic range">
                <GraphicsControl
                  row={{ key: 'range', label: 'Dynamic range', detail: '', choice: color.full ? 'full' : 'limited', choices: [{ id: 'full', label: 'Full' }, { id: 'limited', label: 'Limited' }] }}
                  busy={!!busy}
                  onPick={range => onSet('range', { format: 'rgb', bpc: color.bpc, full: range === 'full' })}
                />
              </Busy>
            </Row>
          )}
        </>
      )}
    </>
  )
}
