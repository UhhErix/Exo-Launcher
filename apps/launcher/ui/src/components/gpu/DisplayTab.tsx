import { useState, type ReactNode } from 'react'
import { host, type ColorControl, type DisplayChange, type DisplayScreen, type DisplayState, type HdrState, type LauncherSettings, type OutputColor, type ScalingMode } from '../../lib/host'
import { report } from '../../lib/report'
import { GraphicsControl } from '../graphics/GraphicsControl'
import { Row, SliderRow, State, SwitchRow } from '../settings/parts'
import { Choice } from '../settings/Choice'
import { Spinner } from '../ui/Spinner'

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

type Action = 'set' | 'adjust' | 'video' | 'override'

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
  const { busy, errorFor, run } = useDisplayChange(onDisplay)
  if (!display) return null
  const several = display.displays.length > 1
  return (
    <>
      {display.displays.filter(screen => !screen.isBest).map(screen => (
        <Row key={screen.id} label={several ? `Best for ${screen.name}` : 'Best for this screen'} detail={bestLine(screen.best)} error={errorFor(screen.id)}>
          <button type="button" className="exo-set-btn is-primary" disabled={!!busy} aria-busy={busy === screen.id}
            onClick={() => { if (!busy) void run('set', { display: screen.id, best: true }, screen.id) }}>
            {busy === screen.id ? <Spinner label="Setting the screen up" /> : null}Use
          </button>
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
      {display.video && (
        <VideoRows
          video={display.video}
          hdrScreen={display.displays.some(item => item.hdr)}
          busy={busy}
          error={error}
          onVideo={(key, change) => { if (!busy) void run('video', { display: screen.id, ...change }, key) }}
        />
      )}
      {screen.scaling && (
        <ScalingRows scaling={screen.scaling} busy={busy} errorFor={errorFor} onSet={set}
          onOverride={on => busy ? Promise.resolve(null) : run('override', { display: screen.id, override: on }, 'override')} />
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

/** PC › Display, HDR: the main screen's verdict, HDR for HDR games, and calibration; nothing on a screen without HDR. */
export function HdrRows({ screen, settings, onSave }: {
  screen: HdrState | null
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  if (!screen) return null
  // A main screen without HDR says so, and offers no HDR controls.
  if (!screen.supported) return <Row label="HDR"><State on={false}>The main screen can't show HDR</State></Row>
  return (
    <>
      <Row label="HDR">
        <State on={screen.verdict !== 'weak'}>{VERDICT[screen.verdict ?? 'unknown']}</State>
      </Row>
      <SwitchRow
        id="set-hdr-games"
        label="HDR for HDR games"
        checked={settings?.hdrForGames === true}
        disabled={!settings}
        onChange={async value => await onSave({ hdrForGames: value }) ? null : 'Not saved. Try again.'}
      />
      <SwitchRow
        id="set-hdr-sdr-gamma"
        label="SDR looks right in HDR"
        checked={settings?.hdrSdrGamma !== false}
        disabled={!settings}
        onChange={async value => await onSave({ hdrSdrGamma: value }) ? null : 'Not saved. Try again.'}
      />
      <Row label="HDR calibration">
        {screen.calibrated
          ? <State on>Calibrated</State>
          : <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('ms-windows-store://pdp/?productid=9N7F2SM5D1LR')}>Calibrate</button>}
      </Row>
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
      {changed ? (
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

/** NVIDIA Control Panel's "Use default color settings" (the driver picks) or "Use NVIDIA color settings" (format, depth, range below). */
function OutputColorRow({ auto, color, busy, error, onSet }: {
  auto: boolean
  color: OutputColor
  busy: string | null
  error: string | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  return (
    <Row label="Output color" detail={auto ? describe(color) : undefined} error={error}>
      <Busy on={busy === 'policy'} label="Changing the output color">
        <Choice
          label="Output color"
          value={auto ? 'default' : 'nvidia'}
          choices={[{ value: 'default', label: 'Default' }, { value: 'nvidia', label: 'NVIDIA settings' }]}
          disabled={!!busy}
          onChange={value => onSet('policy', value === 'default' ? { auto: true } : { format: color.format, bpc: color.bpc, full: color.full })}
        />
      </Busy>
    </Row>
  )
}

const SUPER_RESOLUTION = [
  { value: '0', label: 'Off' },
  { value: '5', label: 'Auto' },
  { value: '1', label: 'Quality 1' },
  { value: '2', label: 'Quality 2' },
  { value: '3', label: 'Quality 3' },
  { value: '4', label: 'Quality 4' },
]

/**
 * NVIDIA Control Panel's RTX video enhancement: Super resolution (Auto or quality 1-4, higher looks
 * sharper and costs more of the GPU) and RTX Video HDR. Shown only where the card has them, and RTX Video HDR only
 * while a screen runs HDR (it does nothing on the others) or while it's on; they change video in browsers and players
 * only, so they apply at once.
 */
function VideoRows({ video, hdrScreen, busy, error, onVideo }: {
  video: NonNullable<DisplayState['video']>
  hdrScreen: boolean
  busy: string | null
  error: { key: string; text: string } | null
  onVideo: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  return (
    <>
      {video.superResolution !== null && (
        <Row label="RTX Video Super Resolution" error={error?.key === 'vsr' ? error.text : null}>
          <Busy on={busy === 'vsr'} label="Changing RTX Video Super Resolution">
            <Choice label="RTX Video Super Resolution" value={String(video.superResolution)} choices={SUPER_RESOLUTION} disabled={!!busy}
              onChange={value => onVideo('vsr', { superResolution: Number(value) })} />
          </Busy>
        </Row>
      )}
      {video.hdr !== null && (hdrScreen || video.hdr > 0) && (
        <Row label="RTX Video HDR" error={error?.key === 'vhdr' ? error.text : null}>
          <Busy on={busy === 'vhdr'} label="Changing RTX Video HDR">
            <Choice label="RTX Video HDR" value={video.hdr > 0 ? 'on' : 'off'} choices={[{ value: 'off', label: 'Off' }, { value: 'on', label: 'On' }]} disabled={!!busy}
              onChange={value => onVideo('vhdr', { hdr: value === 'on' })} />
          </Busy>
        </Row>
      )}
    </>
  )
}

const SCALING: { value: ScalingMode; label: string }[] = [
  { value: 'aspect', label: 'Aspect ratio' },
  { value: 'full', label: 'Full-screen' },
  { value: 'none', label: 'No scaling' },
  { value: 'integer', label: 'Integer scaling' },
]

/** NVIDIA Control Panel's Adjust desktop size and position > Scaling: the mode, and where it runs (integer runs on the GPU). */
function ScalingRows({ scaling, busy, errorFor, onSet, onOverride }: {
  scaling: NonNullable<DisplayScreen['scaling']>
  busy: string | null
  errorFor: (key: string) => string | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
  onOverride: (on: boolean) => Promise<string | null>
}) {
  return (
    <>
      <Row label="Scaling" error={errorFor('scaling')}>
        <Busy on={busy === 'scaling'} label="Changing the scaling">
          <Choice label="Scaling" value={scaling.mode} choices={SCALING} disabled={!!busy}
            onChange={mode => onSet('scaling', { scaling: mode as ScalingMode, scalingGpu: scaling.gpu })} />
        </Busy>
      </Row>
      {scaling.mode !== 'integer' && scaling.mode !== 'default' && (
        <Row label="Perform scaling on" error={errorFor('scalingOn')}>
          <Busy on={busy === 'scalingOn'} label="Changing where scaling runs">
            <Choice label="Perform scaling on" value={scaling.gpu ? 'gpu' : 'display'}
              choices={[{ value: 'gpu', label: 'GPU' }, { value: 'display', label: 'Display' }]} disabled={!!busy}
              onChange={on => onSet('scalingOn', { scaling: scaling.mode as ScalingMode, scalingGpu: on === 'gpu' })} />
          </Busy>
        </Row>
      )}
      {scaling.over !== null && (
        <SwitchRow id="display-override" label="Override the scaling set by games"
          checked={scaling.over} disabled={!!busy} onChange={onOverride} />
      )}
    </>
  )
}

/**
 * "165 Hz, RGB 10-bit, full range, no scaling on the GPU". The host's why follows only where the
 * cable or port holds the screen off RGB, the one case it says how to fix.
 */
function bestLine(best: DisplayScreen['best']) {
  const line = [
    `${best.refresh} Hz`,
    best.color ? describe(best.color) : null,
    best.scaling ? 'no scaling on the GPU' : null,
    best.videoHdr ? 'RTX Video HDR on' : null,
  ].filter(Boolean).join(', ')
  return best.why && best.color && best.color.format !== 'rgb' ? `${line}. ${best.why}` : line
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
      {screen.refreshes.length > 1 && (
        <Row label="Refresh rate" error={errorFor('refresh')}>
          <Busy on={busy === 'refresh'} label="Changing the refresh rate">
            <GraphicsControl
              row={{ key: 'refresh', label: 'Refresh rate', detail: '', choice: String(screen.refresh), choices: screen.refreshes.map(hz => ({ id: String(hz), label: `${hz} Hz` })) }}
              busy={!!busy}
              onPick={hz => onSet('refresh', { refresh: Number(hz) })}
            />
          </Busy>
        </Row>
      )}
      {screen.vendor === 'nvidia' && color && <OutputColorRow auto={screen.auto} color={color} busy={busy} error={errorFor('policy')} onSet={onSet} />}
      {/* Without the screen's graphics driver only the refresh rate changes here. */}
      {!screen.vendor || !color || screen.auto ? null : (
        <>
          <Row label="Color format" error={errorFor('format')}>
            {formats.length > 1 ? (
              <Busy on={busy === 'format'} label="Changing the color format">
                <GraphicsControl
                  row={{ key: 'format', label: 'Color format', detail: '', choice: color.format, choices: formats }}
                  busy={!!busy}
                  onPick={pickFormat}
                />
              </Busy>
            ) : <State on>{FORMAT_NAME[color.format]}</State>}
          </Row>
          <Row label="Color depth" error={errorFor('bpc')}>
            {depths.length > 1 ? (
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
            ) : <State on>{`${color.bpc}-bit`}</State>}
          </Row>
          {color.format === 'rgb' && (
            <Row label="Dynamic range" error={errorFor('range')}>
              {ranges.length > 1 ? (
                <Busy on={busy === 'range'} label="Changing the dynamic range">
                  <GraphicsControl
                    row={{ key: 'range', label: 'Dynamic range', detail: '', choice: color.full ? 'full' : 'limited', choices: [{ id: 'full', label: 'Full' }, { id: 'limited', label: 'Limited' }] }}
                    busy={!!busy}
                    onPick={range => onSet('range', { format: 'rgb', bpc: color.bpc, full: range === 'full' })}
                  />
                </Busy>
              ) : <State on={color.full}>{color.full ? 'Full' : 'Limited'}</State>}
            </Row>
          )}
        </>
      )}
    </>
  )
}
