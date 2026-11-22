import { useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import { ChevronLeft, Drive } from '../../brand/icons'
import { focusWhenActive } from '../../lib/focusTrap'
import { host, type CompressList, type DriveInfo, type DrivesState } from '../../lib/host'
import { driveName } from '../../lib/partNames'
import { report } from '../../lib/report'
import { cn, formatSize } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { ActionButton } from '../ui/ConfirmButton'
import { Meter } from '../ui/Meter'
import { CompressGames } from './CompressGames'
import '../../styles/storage-room.css'

const GIB = 1024 ** 3

function formatCapacity(n: number): string {
  return n >= 1000 * GIB ? `${+(n / 1024 ** 4).toFixed(1)} TB` : `${Math.max(1, Math.round(n / GIB))} GB`
}

function driveTotals(drive: DriveInfo): { total: number; free: number } {
  if (drive.space && drive.space.total > 0) return { total: drive.space.total, free: drive.space.free }
  const total = drive.volumes.reduce((sum, v) => sum + v.totalGb, 0) * GIB
  const free = drive.volumes.reduce((sum, v) => sum + v.freeGb, 0) * GIB
  return { total, free }
}

function summaryLine(drives: DrivesState | null, list: CompressList | null): string | null {
  if (!drives || drives.drives.length === 0) return null
  let total = 0
  let free = 0
  for (const d of drives.drives) {
    const t = driveTotals(d)
    total += t.total
    free += t.free
  }
  const parts: string[] = []
  if (total > 0) parts.push(`${formatCapacity(free)} free of ${formatCapacity(total)}`)
  parts.push(`${drives.drives.length} ${drives.drives.length === 1 ? 'drive' : 'drives'}`)
  if (list && list.saved > 0) parts.push(`${formatSize(list.saved)} saved`)
  return parts.join(' · ')
}

function driveSpecs(drive: DriveInfo): string {
  const parts = [
    drive.kind,
    drive.temperature?.now != null ? `${drive.temperature.now} °C` : null,
    drive.link ? `PCIe ${drive.link.gen}.0 ×${drive.link.width}` : null,
    drive.health?.lifeUsed != null ? `${Math.max(0, 100 - drive.health.lifeUsed)}% health` : null,
    drive.health?.writtenTb != null ? `${Math.round(drive.health.writtenTb)} TB written` : null,
    drive.build,
  ]
  return parts.filter(Boolean).join(' · ')
}

function spaceBreakdown(drive: DriveInfo): string | null {
  const sp = drive.space
  if (!sp) return null
  const caches = sp.caches + sp.oldCaches
  const other = sp.other + sp.exo
  const parts = [
    sp.games > 0 ? `Games ${formatSize(sp.games)}` : null,
    caches > 0 ? `Caches ${formatSize(caches)}` : null,
    sp.leftovers > 0 ? `Leftovers ${formatSize(sp.leftovers)}` : null,
    other > 0 ? `Other ${formatSize(other)}` : null,
  ].filter(Boolean)
  return parts.length > 0 ? parts.join(' · ') : null
}

function DriveCard({ drive }: { drive: DriveInfo }) {
  const letters = drive.volumes.map(v => `${v.letter}:`).join(', ')
  const title = [letters, drive.model ? driveName(drive.model) : drive.kind].filter(Boolean).join(' · ')
  const { total, free } = driveTotals(drive)
  const share = total > 0 ? 1 - free / total : null
  const verdicts = drive.verdicts.filter(v => !v.key.startsWith('bypass:'))
  const bad = verdicts.some(v => v.bad)
  const specs = driveSpecs(drive)
  const breakdown = spaceBreakdown(drive)
  const topGames = drive.games.slice(0, 3)

  return (
    <div className="exo-st-drive">
      <div className="exo-st-drive-head">
        <div className="exo-st-drive-id">
          <Drive size={16} />
          <span className="exo-st-drive-name">{title}</span>
        </div>
        <div className="exo-st-drive-right">
          {total > 0 ? <span className="exo-st-drive-cap">{formatCapacity(free)} free of {formatCapacity(total)}</span> : null}
          <State on={!bad}>{bad ? 'Check' : 'Healthy'}</State>
        </div>
      </div>

      {share != null ? (
        <div className="exo-st-drive-meter">
          <Meter share={share} tone={share >= 0.95 ? 'is-bad' : share >= 0.85 ? 'is-warn' : null} />
        </div>
      ) : null}

      {specs ? <p className="exo-st-drive-specs">{specs}</p> : null}
      {breakdown ? <p className="exo-st-drive-breakdown">{breakdown}</p> : null}
      {topGames.length > 0 ? (
        <p className="exo-st-drive-games">
          Largest: {topGames.map(g => `${g.title} ${formatSize(g.bytes)}`).join(' · ')}
        </p>
      ) : null}

      {verdicts.length > 0 ? (
        <div className="exo-st-verdicts">
          {verdicts.map(v => (
            <p key={v.key} className={cn('exo-st-verdict', v.bad ? 'is-bad' : 'is-warn')}>{v.text}</p>
          ))}
          {drive.vendor ? (
            <button
              type="button"
              className="exo-set-btn is-compact"
              onClick={() => void host.openUrl(drive.vendor!.url).catch((e: unknown) => report(e, 'StorageRoom.vendor'))}
            >
              Open {drive.vendor.tool}
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}

export function StorageRoom({
  active,
  open,
  onClose,
  drives,
  onDrives,
  list,
  onList,
  onReadCompress,
}: {
  active: boolean
  open: boolean
  onClose: () => void
  drives: DrivesState | null
  onDrives: (next: DrivesState) => void
  list: CompressList | null
  onList: (next: CompressList) => void
  onReadCompress: () => void
}) {
  const backRef = useRef<HTMLButtonElement>(null)
  const spaceScannedRef = useRef(false)

  useEffect(() => {
    if (!open || !active) {
      spaceScannedRef.current = false
      return
    }
    let alive = true
    if (!spaceScannedRef.current && (!drives || drives.drives.some(d => !d.space))) {
      spaceScannedRef.current = true
      void host.systemDrives(undefined, true).then(
        next => { if (alive) onDrives(next) },
        (e: unknown) => report(e, 'StorageRoom.drivesSpace', { level: 'warn' }),
      )
    }
    return () => { alive = false }
  }, [open, active, drives, onDrives])

  useEffect(() => {
    if (!open || !active) return
    window.requestAnimationFrame(() => focusWhenActive(backRef.current))
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented) onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, active, onClose])

  if (!open || !active) return null

  const rescanAll = async () => {
    const nextDrives = await host.systemDrives(undefined, true)
    onDrives(nextDrives)
    onReadCompress()
    return nextDrives
  }

  const sub = summaryLine(drives, list)

  return createPortal(
    <div
      className="exo-storage-room"
      role="dialog"
      aria-modal="true"
      aria-labelledby="exo-storage-title"
      data-log-area="PC"
    >
      <div className="exo-st-shell">
        <header className="exo-st-top">
          <div className="exo-st-top-left">
            <button ref={backRef} type="button" className="exo-set-btn exo-st-back" onClick={onClose}>
              <ChevronLeft size={16} />
              Exo AI
            </button>
            <div className="exo-st-heading">
              <h1 id="exo-storage-title" className="exo-st-title">Storage</h1>
              {sub ? <span className="exo-st-sub">{sub}</span> : null}
            </div>
          </div>
          <ActionButton
            className="exo-set-btn"
            timeoutMs={60_000}
            why="none"
            context="StorageRoom.scan"
            fallback="Scan could not finish."
            onPress={rescanAll}
          >
            Scan space
          </ActionButton>
        </header>

        <div className="exo-st-grid">
          <section className="exo-system-panel exo-st-panel" aria-labelledby="exo-st-drives-title">
            <h2 id="exo-st-drives-title">Drives</h2>
            {drives && drives.drives.length > 0 ? (
              <div className="exo-st-drive-list">
                {drives.drives.map(drive => <DriveCard key={drive.id} drive={drive} />)}
              </div>
            ) : (
              <p className="exo-st-quiet">Reading drives…</p>
            )}

            {drives && drives.fixes.length > 0 ? (
              <div className="exo-st-section">
                <h3 className="exo-st-subhead">Drive settings</h3>
                <div className="exo-pc-menu-rows">
                  {drives.fixes.map(fix => (
                    <Row key={fix.key} label={fix.label}>
                      {fix.applied ? (
                        <State on>{fix.restart ? 'After restart' : 'Optimal'}</State>
                      ) : (
                        <ActionButton
                          className="exo-set-btn"
                          timeoutMs={15_000}
                          why="none"
                          context={`Storage.fix.${fix.key}`}
                          onPress={async () => {
                            const next = await host.systemDrives(fix.key)
                            onDrives(next)
                            return next
                          }}
                        >
                          Apply
                        </ActionButton>
                      )}
                    </Row>
                  ))}
                </div>
              </div>
            ) : null}
          </section>

          <section className="exo-system-panel exo-st-panel" aria-labelledby="exo-st-compress-title">
            <h2 id="exo-st-compress-title">Compress games</h2>
            <CompressGames list={list} onList={onList} onRead={onReadCompress} />
          </section>
        </div>
      </div>
    </div>,
    document.body,
  )
}
