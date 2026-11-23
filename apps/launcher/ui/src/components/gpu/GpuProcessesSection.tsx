import { useCallback, useEffect, useState } from 'react'
import { host, type GpuProcessesState, type GpuProcessUsage } from '../../lib/host'
import { report } from '../../lib/report'
import { formatSize } from '../../lib/utils'
import { SectionActions } from './SectionHead'

const SHARED_NOTE_MIN = 64 * 1024 * 1024

function formatProcessMetrics(proc: GpuProcessUsage): string {
  const parts: string[] = []
  if (proc.totalPercent >= 0.1) parts.push(`${Math.round(proc.totalPercent)}%`)
  if (proc.localBytes > 0) parts.push(`${formatSize(proc.localBytes)} VRAM`)
  if (proc.nonLocalBytes >= SHARED_NOTE_MIN) parts.push(`${formatSize(proc.nonLocalBytes)} shared`)
  return parts.join(' · ') || '0%'
}

function formatProcessSubtitle(proc: GpuProcessUsage, multiAdapter: boolean): string | null {
  const parts: string[] = []
  if (proc.engines.length > 0) {
    parts.push(proc.engines.slice(0, 2).map(e => `${e.type} ${Math.round(e.percent)}%`).join(', '))
  }
  if (multiAdapter && proc.adapterName) parts.push(proc.adapterName)
  return parts.length > 0 ? parts.join(' · ') : null
}

export function GpuProcessesSection({ active }: { active: boolean }) {
  const [data, setData] = useState<GpuProcessesState | null>(null)
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      const next = await host.gpuProcesses(8)
      setData(next)
    } catch (e: unknown) {
      report(e, 'GpuProcessesSection.read', { level: 'warn' })
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    if (!active) return
    void refresh()
    const timer = window.setInterval(() => {
      if (!document.hidden) void refresh()
    }, 3000)
    return () => window.clearInterval(timer)
  }, [active, refresh])

  const multiAdapter = (data?.adapters.length ?? 0) > 1
  const rows = data?.processes.slice(0, 6) ?? []

  return (
    <div className="exo-gpu-usage">
      <SectionActions>
        <button
          type="button"
          className="exo-set-btn is-compact"
          disabled={busy}
          onClick={() => void refresh()}
        >
          Refresh
        </button>
      </SectionActions>
      {data?.answer.summary ? (
        <p className="exo-gpu-usage-summary">{data.answer.summary}</p>
      ) : (
        <p className="exo-gpu-usage-summary">Reading GPU activity…</p>
      )}
      {rows.length > 0 ? (
        <ul className="exo-gpu-proc-list">
          {rows.map(proc => {
            const sub = formatProcessSubtitle(proc, multiAdapter)
            return (
              <li key={`${proc.pid}-${proc.adapterLuid}`} className="exo-gpu-proc-row">
                <div className="exo-gpu-proc-main">
                  <span className="exo-gpu-proc-name">{proc.name}</span>
                  {sub ? <span className="exo-gpu-proc-sub">{sub}</span> : null}
                </div>
                <span className="exo-gpu-proc-metrics">{formatProcessMetrics(proc)}</span>
              </li>
            )
          })}
        </ul>
      ) : null}
    </div>
  )
}
