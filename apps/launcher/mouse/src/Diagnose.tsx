import { useEffect, useState, type CSSProperties } from 'react'
import { call } from './exo-hid'
import { formatCauseLabel, formatPlacement, formatUs } from './diagnose-format'
import { Panel, Progress, Spinner, Switch } from './ui'

export type DiagBucket = { id: string; label: string; rangeMs: string; count: number; percent: number; kind: 'ok' | 'warn' | 'bad' }
export type DiagJitter = {
  pollingRateHz: number; nominalMs: number; totalReports: number; checkedGaps: number
  medianMs: number; spreadMs: number; p95Ms: number; p99Ms: number; p999Ms: number; longestMs: number
  lateCount: number; droppedCount: number; bunchedCount: number; stabilityScore: number
  onScreenRefresh: boolean; mouseCpu: number | null
  cause: 'steady' | 'pc_busy' | 'usb_uneven' | 'both' | 'insufficient'; causeDetail: string
  histogram: DiagBucket[]
}
export type DiagUsbDevice = {
  name: string; kind: string | null; portNumber: number; speedCode: number; speedLabel: string
  bIntervalUs: number | null; pollingHz: number | null; highRate: boolean; highRateReason: string | null
  sharesHubWithHighRate: boolean; sharedWith: string[]; isMouse: boolean
}
export type DiagUsbHub = {
  name: string; isRoot: boolean; portNumber: number | null; portCount: number; selectiveSuspendEnabled: boolean | null
  devices: DiagUsbDevice[]; hubs: DiagUsbHub[]
}
export type DiagUsbController = {
  name: string; vendorName: string; pciLocation: string; placement: string; interruptMode: string
  msiSupported: boolean; freeCpuPorts: number | null; rootHub: DiagUsbHub
}
export type DiagUsb = {
  controllers: DiagUsbController[]; advice: string[]; mouseOnCpu: boolean; mouseOnHub: boolean; mouseSharesHighRateHub: boolean
}
export type DiagCore = {
  core: number; isrCount: number; isrTotalUs: number; isrMaxUs: number; dpcCount: number; dpcTotalUs: number; dpcMaxUs: number; isMouseCore: boolean
}
export type DiagDriver = {
  driver: string; dpcCount: number; dpcTotalUs: number; dpcMaxUs: number; isrCount: number; isrTotalUs: number; isrMaxUs: number; topCore: number; cores: number[]
}
export type DiagEtw = {
  at: string; seconds: number; totalIsrs: number; totalDpcs: number; totalIsrUs: number; totalDpcUs: number
  maxIsrUs: number; maxDpcUs: number; eventsLost: number; mouseControllerCore: number | null
  cores: DiagCore[]; topDrivers: DiagDriver[]
}
export type DiagSnapshot = {
  pollingRateHz: number; stabilityScore: number; p99Ms: number; p999Ms: number; longestMs: number
  lateCount: number; droppedCount: number; bunchedCount: number; mouseCpu: number | null
}
export type DiagFixId = 'affinity' | 'msi' | 'usb_suspend'
export type DiagFixCard = {
  id: DiagFixId; title: string; summary: string; actionLabel: string; honestyNote: string
  needsRestart: boolean; needsAdmin: boolean; status: 'available' | 'applied' | 'restart'; canApply: boolean; canUndo: boolean
  detail: string | null; before: DiagSnapshot | null; after: DiagSnapshot | null; improved: boolean | null
}
export type DiagState = {
  ok: boolean; message?: string | null; running: boolean
  jitter: DiagJitter | null; usb: DiagUsb; etw: DiagEtw | null; etwProblem?: string | null; fixes: DiagFixCard[]
}

const TEST_MS = 10_000

const n = (v: number) => v.toLocaleString('en-US')

async function fetchStatus(): Promise<DiagState> {
  return await call('exo.mouseDiag.status', {}, 15_000) as unknown as DiagState
}

async function runMeasure(withEtw: boolean): Promise<DiagState> {
  return await call('exo.mouseDiag.measure', { withEtw }, 120_000) as unknown as DiagState
}

async function cancelMeasure(): Promise<void> {
  await call('exo.mouseDiag.cancel', {}, 10_000)
}

async function runFix(id: DiagFixId, action: 'apply' | 'undo'): Promise<DiagState> {
  return await call('exo.mouseDiag.fix', { id, action }, 120_000) as unknown as DiagState
}

export function DiagnoseView() {
  const [state, setState] = useState<DiagState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [withEtw, setWithEtw] = useState(false)
  const [measuring, setMeasuring] = useState(false)
  const [busyFix, setBusyFix] = useState<string | null>(null)
  const [leftMs, setLeftMs] = useState(TEST_MS)

  useEffect(() => {
    let live = true
    fetchStatus().then(
      next => { if (live) setState(next) },
      e => { if (live) setError(e instanceof Error ? e.message : 'Diagnostics are unavailable here.') },
    )
    return () => { live = false }
  }, [])

  const startMeasure = async (includeEtw: boolean) => {
    if (measuring || busyFix) return
    setMeasuring(true)
    setError(null)
    const end = performance.now() + TEST_MS
    setLeftMs(TEST_MS)
    const timer = window.setInterval(() => setLeftMs(Math.max(0, end - performance.now())), 200)
    try {
      const next = await runMeasure(includeEtw)
      setState(next)
      if (!next.ok && next.message) setError(next.message)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not complete measurement.')
      await fetchStatus().then(setState, () => undefined)
    } finally {
      window.clearInterval(timer)
      setMeasuring(false)
    }
  }

  const handleCancel = async () => {
    try { await cancelMeasure() }
    catch { undefined }
  }

  const handleFix = async (id: DiagFixId, action: 'apply' | 'undo') => {
    if (measuring || busyFix) return
    setBusyFix(`${id}:${action}`)
    setError(null)
    try {
      const next = await runFix(id, action)
      setState(next)
      if (!next.ok && next.message) setError(next.message)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not update setting.')
      await fetchStatus().then(setState, () => undefined)
    } finally {
      setBusyFix(null)
    }
  }

  const secondsLeft = Math.ceil(leftMs / 1000)
  const jitter = state?.jitter ?? null
  const usb = state?.usb ?? null
  const etw = state?.etw ?? null
  const fixes = state?.fixes ?? []

  return (
    <div className="m-diag">
      {error ? <p className="m-diag-alert" role="alert">{error}</p> : null}

      <Panel
        title="Report delivery jitter"
        actions={(
          <span className="m-inline">
            {measuring ? (
              <button type="button" className="m-button" onClick={() => void handleCancel()}>Cancel</button>
            ) : (
              <button type="button" className="m-button is-primary" disabled={!!busyFix} onClick={() => void startMeasure(withEtw)}>
                {jitter ? 'Measure again' : 'Measure 10 s'}
              </button>
            )}
          </span>
        )}
      >
        <div className="m-row m-core-row">
          <span className="m-row-label">
            Include ISR and DPC trace (admin)
            <span className="m-row-value">Captures per-core interrupt and driver DPC time during the 10-second move test.</span>
          </span>
          <span className="m-row-control">
            <Switch label="Include ISR and DPC trace" checked={withEtw} disabled={measuring || !!busyFix} onChange={setWithEtw} />
          </span>
        </div>

        {measuring ? (
          <div className="m-row m-core-row">
            <span className="m-row-label">
              {secondsLeft > 0 ? `Move your mouse in smooth circles · ${secondsLeft} s` : 'Finishing analysis'}
              <span className="m-row-value">{withEtw ? 'Approve the Windows prompt if shown, then move steadily.' : 'Keep moving at moderate speed to fill every polling slot.'}</span>
            </span>
            <span className="m-row-control">
              <span className="m-core-bar">
                <Progress value={1 - leftMs / TEST_MS} label={`Measuring mouse jitter · ${secondsLeft} seconds left`} />
              </span>
            </span>
          </div>
        ) : null}

        {jitter ? (
          <>
            <div className="m-diag-metrics">
              <Metric label="Polling rate" value={jitter.pollingRateHz > 0 ? `${n(jitter.pollingRateHz)} Hz` : '—'} sub={jitter.nominalMs > 0 ? `${jitter.nominalMs} ms nominal` : undefined} />
              <Metric label="Stability score" value={`${jitter.stabilityScore} / 100`} tone={jitter.stabilityScore >= 90 ? 'good' : jitter.stabilityScore < 75 ? 'bad' : undefined} />
              <Metric label="Median / p99" value={`${jitter.medianMs.toFixed(2)} / ${jitter.p99Ms.toFixed(2)} ms`} sub={`p99.9 ${jitter.p999Ms.toFixed(2)} ms`} />
              <Metric label="Longest gap" value={`${jitter.longestMs.toFixed(2)} ms`} sub={jitter.mouseCpu !== null ? `CPU ${jitter.mouseCpu}` : undefined} />
              <Metric label="Dropped / bunched" value={`${n(jitter.droppedCount)} / ${n(jitter.bunchedCount)}`} sub={`${n(jitter.lateCount)} late of ${n(jitter.checkedGaps)}`} />
            </div>

            <div className="m-row m-core-row">
              <span className="m-row-label">
                {formatCauseLabel(jitter.cause)}
                <span className="m-row-value">{jitter.causeDetail}</span>
              </span>
              <span className={`m-diag-badge is-${jitter.cause === 'steady' ? 'good' : jitter.cause === 'insufficient' ? 'muted' : 'bad'}`}>
                {formatCauseLabel(jitter.cause)}
              </span>
            </div>

            {jitter.histogram.length > 0 ? <Histogram buckets={jitter.histogram} /> : null}
          </>
        ) : (
          <div className="m-row m-core-row">
            <span className="m-row-label">
              Ready to test
              <span className="m-row-value">Run a 10-second circle test to measure real polling rate, p99 and p99.9 gaps, dropped or bunched reports, and stability score.</span>
            </span>
          </div>
        )}
      </Panel>

      <Panel
        title="USB topology map"
        actions={(
          <button type="button" className="m-button" disabled={measuring || !!busyFix} onClick={() => void fetchStatus().then(setState, () => undefined)}>
            Refresh
          </button>
        )}
      >
        {usb && usb.advice.length > 0 ? (
          <div className="m-diag-advice-list">
            {usb.advice.map((line, idx) => (
              <p key={idx} className={`m-diag-advice${usb.mouseOnCpu && !usb.mouseOnHub && !usb.mouseSharesHighRateHub ? ' is-good' : ''}`}>{line}</p>
            ))}
          </div>
        ) : null}

        {usb && usb.controllers.length > 0 ? (
          <div className="m-diag-tree">
            {usb.controllers.map((ctrl, idx) => (
              <div key={`${ctrl.name}-${idx}`} className="m-diag-ctrl">
                <div className="m-diag-ctrl-head">
                  <span className="m-diag-ctrl-name">{ctrl.name}</span>
                  <span className="m-diag-tags">
                    <span className={`m-diag-tag${ctrl.placement === 'cpu' ? ' is-good' : ''}`}>{formatPlacement(ctrl.placement)}</span>
                    <span className="m-diag-tag">{ctrl.vendorName}</span>
                    <span className="m-diag-tag">{ctrl.pciLocation}</span>
                    <span className={`m-diag-tag${ctrl.interruptMode === 'line' && ctrl.msiSupported ? ' is-bad' : ''}`}>{ctrl.interruptMode.toUpperCase()}</span>
                  </span>
                </div>
                <UsbHubTree hub={ctrl.rootHub} depth={0} />
              </div>
            ))}
          </div>
        ) : (
          <div className="m-row m-core-row">
            <span className="m-row-label">
              No USB controllers reported
              <span className="m-row-value">USB controller topology loads from the Exo host.</span>
            </span>
          </div>
        )}
      </Panel>

      <Panel
        title="ISR and DPC attribution"
        actions={(
          <button type="button" className="m-button" disabled={measuring || !!busyFix} onClick={() => void startMeasure(true)}>
            {measuring && withEtw ? <Spinner label="Tracing ISR and DPC events" /> : null}
            {etw ? 'Trace again (admin)' : 'Trace drivers (admin)'}
          </button>
        )}
      >
        {state?.etwProblem ? (
          <div className="m-row m-core-row">
            <span className="m-row-label">
              Trace notice
              <span className="m-row-value is-bad">{state.etwProblem}</span>
            </span>
          </div>
        ) : null}

        {etw ? (
          <>
            <div className="m-diag-metrics">
              <Metric label="Total DPC time" value={formatUs(etw.totalDpcUs)} sub={`${n(etw.totalDpcs)} DPCs · max ${formatUs(etw.maxDpcUs)}`} />
              <Metric label="Total ISR time" value={formatUs(etw.totalIsrUs)} sub={`${n(etw.totalIsrs)} ISRs · max ${formatUs(etw.maxIsrUs)}`} />
              <Metric label="Mouse controller core" value={etw.mouseControllerCore !== null ? `CPU ${etw.mouseControllerCore}` : 'Unknown'} sub={etw.eventsLost > 0 ? `${n(etw.eventsLost)} events lost` : `${etw.seconds.toFixed(1)} s trace`} />
            </div>
            <CoreBars cores={etw.cores} />
            <DriverList drivers={etw.topDrivers} />
          </>
        ) : (
          <div className="m-row m-core-row">
            <span className="m-row-label">
              Opt-in kernel ETW trace
              <span className="m-row-value">Runs a 10-second real-time ETW session to measure per-core ISR and DPC time and rank top drivers like usbxhci.sys, nvlddmkm.sys, and ndis.sys.</span>
            </span>
          </div>
        )}
      </Panel>

      {fixes.length > 0 ? (
        <Panel title="Reversible fixes">
          {fixes.map(fix => {
            const applying = busyFix === `${fix.id}:apply`
            const undoing = busyFix === `${fix.id}:undo`
            return (
              <div key={fix.id} className="m-diag-fix">
                <div className="m-diag-fix-head">
                  <div className="m-diag-fix-text">
                    <span className="m-diag-fix-title">
                      {fix.title}
                      <span className={`m-diag-tag${fix.status === 'applied' ? ' is-good' : fix.status === 'restart' ? ' is-warn' : ''}`}>
                        {fix.status === 'applied' ? 'Applied' : fix.status === 'restart' ? 'Restart needed' : 'Suggested'}
                      </span>
                    </span>
                    <span className="m-row-value">{fix.summary}</span>
                    {fix.detail ? <span className="m-row-value">{fix.detail}</span> : null}
                    <span className="m-diag-honesty">{fix.honestyNote}</span>
                  </div>
                  <span className="m-inline">
                    {fix.canApply ? (
                      <button type="button" className="m-button is-primary" disabled={measuring || !!busyFix} aria-busy={applying} onClick={() => void handleFix(fix.id, 'apply')}>
                        {applying ? <Spinner label={`Applying ${fix.title}`} /> : null}
                        {fix.actionLabel}
                      </button>
                    ) : null}
                    {fix.canUndo ? (
                      <button type="button" className="m-button" disabled={measuring || !!busyFix} aria-busy={undoing} onClick={() => void handleFix(fix.id, 'undo')}>
                        {undoing ? <Spinner label={`Undoing ${fix.title}`} /> : null}
                        Undo
                      </button>
                    ) : null}
                  </span>
                </div>
                {fix.before && fix.after ? (
                  <div className="m-diag-compare">
                    <span>Before: score {fix.before.stabilityScore}, p99 {fix.before.p99Ms.toFixed(2)} ms, {n(fix.before.lateCount)} late</span>
                    <span className={fix.improved ? 'is-good' : undefined}>
                      After: score {fix.after.stabilityScore}, p99 {fix.after.p99Ms.toFixed(2)} ms, {n(fix.after.lateCount)} late
                    </span>
                  </div>
                ) : null}
              </div>
            )
          })}
        </Panel>
      ) : null}
    </div>
  )
}

function Metric({ label, value, sub, tone }: { label: string; value: string; sub?: string; tone?: 'good' | 'bad' }) {
  return (
    <div className="m-diag-metric">
      <span className="m-diag-metric-label">{label}</span>
      <span className={`m-diag-metric-value${tone ? ` is-${tone}` : ''}`}>{value}</span>
      {sub ? <span className="m-diag-metric-sub">{sub}</span> : null}
    </div>
  )
}

function Histogram({ buckets }: { buckets: DiagBucket[] }) {
  const peak = Math.max(1, ...buckets.map(b => b.percent))
  return (
    <div className="m-diag-hist" role="list" aria-label="Inter-report gap histogram">
      {buckets.map(b => {
        const width = b.count === 0 ? 0 : Math.max(2, (b.percent / peak) * 100)
        return (
          <div key={b.id} className="m-diag-hist-row" role="listitem">
            <span className="m-diag-hist-label">
              <span>{b.label}</span>
              <span className="m-diag-hist-range">{b.rangeMs}</span>
            </span>
            <span className="m-diag-bar-track" aria-hidden="true">
              <span className={`m-diag-bar-fill is-${b.kind}`} style={{ '--m-w': `${width}%` } as CSSProperties} />
            </span>
            <span className="m-diag-hist-stat">{b.percent.toFixed(1)}% ({n(b.count)})</span>
          </div>
        )
      })}
    </div>
  )
}

function UsbHubTree({ hub, depth }: { hub: DiagUsbHub; depth: number }) {
  return (
    <div className="m-diag-hub" style={{ '--m-depth': depth } as CSSProperties}>
      <div className="m-diag-hub-head">
        <span>{hub.isRoot ? hub.name : `Port ${hub.portNumber ?? '?'} · ${hub.name}`}</span>
        <span className="m-diag-tags">
          {hub.portCount > 0 ? <span className="m-diag-tag">{hub.portCount} ports</span> : null}
          {hub.selectiveSuspendEnabled === true ? <span className="m-diag-tag">Selective suspend on</span> : hub.selectiveSuspendEnabled === false ? <span className="m-diag-tag is-good">Suspend off</span> : null}
        </span>
      </div>
      {hub.devices.map((dev, idx) => (
        <div key={`${dev.name}-${dev.portNumber}-${idx}`} className={`m-diag-dev${dev.isMouse ? ' is-mouse' : ''}`}>
          <span className="m-diag-dev-name">
            Port {dev.portNumber > 0 ? dev.portNumber : '?'} · {dev.name}
          </span>
          <span className="m-diag-tags">
            <span className="m-diag-tag">{dev.speedLabel}</span>
            {dev.pollingHz ? <span className="m-diag-tag">{n(dev.pollingHz)} Hz ({dev.bIntervalUs} µs)</span> : null}
            {dev.highRateReason ? <span className="m-diag-tag">{dev.highRateReason}</span> : null}
            {dev.isMouse && dev.sharesHubWithHighRate ? (
              <span className="m-diag-tag is-bad">Shared with {dev.sharedWith.join(', ')}</span>
            ) : null}
          </span>
        </div>
      ))}
      {hub.hubs.map((child, idx) => <UsbHubTree key={`${child.name}-${idx}`} hub={child} depth={depth + 1} />)}
    </div>
  )
}

function CoreBars({ cores }: { cores: DiagCore[] }) {
  if (cores.length === 0) return null
  const peak = Math.max(1, ...cores.map(c => c.dpcTotalUs + c.isrTotalUs))
  return (
    <div className="m-diag-section">
      <div className="m-diag-subhead">Per-core ISR and DPC time</div>
      <div className="m-diag-hist">
        {cores.map(c => {
          const dpcW = Math.min(100, (c.dpcTotalUs / peak) * 100)
          const isrW = Math.min(100 - dpcW, (c.isrTotalUs / peak) * 100)
          return (
            <div key={c.core} className="m-diag-core-row">
              <span className="m-diag-core-id">
                CPU {c.core}
                {c.isMouseCore ? <span className="m-diag-tag is-good">Mouse</span> : null}
              </span>
              <span className="m-diag-bar-track" aria-hidden="true">
                <span className="m-diag-bar-fill is-dpc" style={{ '--m-w': `${dpcW}%` } as CSSProperties} />
                <span className="m-diag-bar-fill is-isr" style={{ '--m-w': `${isrW}%` } as CSSProperties} />
              </span>
              <span className="m-diag-hist-stat">DPC {formatUs(c.dpcTotalUs)} · ISR {formatUs(c.isrTotalUs)}</span>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function DriverList({ drivers }: { drivers: DiagDriver[] }) {
  if (drivers.length === 0) return null
  const peak = Math.max(1, ...drivers.map(d => d.dpcTotalUs))
  return (
    <div className="m-diag-section">
      <div className="m-diag-subhead">Top drivers by DPC time</div>
      <div className="m-diag-hist">
        {drivers.map(d => {
          const width = Math.max(2, (d.dpcTotalUs / peak) * 100)
          return (
            <div key={d.driver} className="m-diag-driver-row">
              <span className="m-diag-driver-name">
                {d.driver}
                {d.topCore >= 0 ? <span className="m-diag-hist-range">CPU {d.cores.join(', ')}</span> : null}
              </span>
              <span className="m-diag-bar-track" aria-hidden="true">
                <span className={`m-diag-bar-fill${d.dpcMaxUs >= 500 ? ' is-bad' : ' is-dpc'}`} style={{ '--m-w': `${width}%` } as CSSProperties} />
              </span>
              <span className="m-diag-hist-stat">{formatUs(d.dpcTotalUs)} · max {formatUs(d.dpcMaxUs)}</span>
            </div>
          )
        })}
      </div>
    </div>
  )
}
