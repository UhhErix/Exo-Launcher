import { useCallback, useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { ChevronLeft, Drive } from '../../brand/icons'
import { focusWhenActive } from '../../lib/focusTrap'
import { host, type CompressList, type DriveInfo, type DrivesState, type JunkItem } from '../../lib/host'
import { cleanOutcome } from '../../lib/junkClean'
import { driveName } from '../../lib/partNames'
import { report } from '../../lib/report'
import { cn, formatSize } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
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

function summaryLine(drives: DrivesState | null, list: CompressList | null): string {
  if (!drives || drives.drives.length === 0) return 'Drives, caches & game compression'
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
  const [junkItems, setJunkItems] = useState<JunkItem[] | null>(null)
  const [junkError, setJunkError] = useState<string | null>(null)
  const [itemErrors, setItemErrors] = useState<Record<string, string | null>>({})

  const readJunk = useCallback(async () => {
    try {
      const scan = await host.junkScan()
      setJunkItems(scan.items)
      return scan
    } catch (e) {
      report(e, 'StorageRoom.junkScan', { level: 'warn' })
      return null
    }
  }, [])

  useEffect(() => {
    if (!open || !active) {
      spaceScannedRef.current = false
      return
    }
    let alive = true
    void host.junkScan().then(
      scan => { if (alive) setJunkItems(scan.items) },
      (e: unknown) => { report(e, 'StorageRoom.junkScan', { level: 'warn' }); if (alive) setJunkItems(current => current ?? []) },
    )
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
    setJunkError(null)
    setItemErrors({})
    const [nextDrives] = await Promise.all([
      host.systemDrives(undefined, true),
      readJunk(),
    ])
    onDrives(nextDrives)
    onReadCompress()
    return nextDrives
  }

  const activeJunk = (junkItems ?? []).filter(item => item.bytes > 0)
  const safeJunk = activeJunk.filter(item => item.id !== 'bin')
  const safeBytes = safeJunk.reduce((sum, item) => sum + item.bytes, 0)
  const safeIds = safeJunk.map(item => item.id)

  const cleanBatch = async (ids: string[], key?: string) => {
    if (key) setItemErrors(c => ({ ...c, [key]: null }))
    else setJunkError(null)
    const result = await host.junkClean(ids)
    const outcome = cleanOutcome(result, formatSize)
    if (!outcome.done) return { ok: false, message: outcome.error ?? null }
    return result
  }

  const afterBatch = async (result: unknown, key?: string) => {
    const outcome = cleanOutcome(result as Awaited<ReturnType<typeof host.junkClean>>, formatSize)
    if (outcome.error) {
      if (key) setItemErrors(c => ({ ...c, [key]: outcome.error ?? null }))
      else setJunkError(outcome.error)
    }
    await readJunk()
    try {
      onDrives(await host.systemDrives(undefined, true))
    } catch (e) {
      report(e, 'StorageRoom.drivesAfterClean', { level: 'warn' })
    }
  }

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
              <p className="exo-st-sub">{summaryLine(drives, list)}</p>
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
                    <Row key={fix.key} label={fix.label} detail={fix.detail}>
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

          <section className="exo-system-panel exo-st-panel" aria-labelledby="exo-st-free-title">
            <h2 id="exo-st-free-title">Free up space</h2>
            <div className="exo-st-section is-first">
              <div className="exo-st-section-head">
                <h3 className="exo-st-subhead">Temporary &amp; cache files</h3>
                {safeBytes > 0 ? (
                  <ConfirmButton
                    className="exo-set-btn is-danger is-compact"
                    danger
                    question={`Delete ${formatSize(safeBytes)}?`}
                    why="none"
                    context="StorageRoom.cleanCaches"
                    fallback="Nothing was deleted. Try again."
                    onConfirm={() => cleanBatch(safeIds)}
                    onFailed={failure => setJunkError(failure.text)}
                    onDone={res => void afterBatch(res)}
                  >
                    Clean caches
                  </ConfirmButton>
                ) : null}
              </div>
              {junkError ? <ErrorNote className="exo-set-error" message={junkError} /> : null}
              {junkItems === null ? (
                <p className="exo-st-quiet">Scanning temporary files and caches…</p>
              ) : activeJunk.length === 0 ? (
                <p className="exo-st-quiet">Temporary files and caches are clean.</p>
              ) : (
                <div className="exo-pc-menu-rows">
                  {activeJunk.map(item => (
                    <Row key={item.id} label={item.name} detail={item.detail} error={itemErrors[item.id] ?? null}>
                      <span className="exo-st-size">{formatSize(item.bytes)}</span>
                      <ConfirmButton
                        className="exo-set-btn is-danger is-compact"
                        danger
                        question={`Delete ${formatSize(item.bytes)}?`}
                        why="none"
                        context={`StorageRoom.clean.${item.id}`}
                        fallback="Nothing was deleted. Try again."
                        onConfirm={() => cleanBatch([item.id], item.id)}
                        onFailed={failure => setItemErrors(c => ({ ...c, [item.id]: failure.text }))}
                        onDone={res => void afterBatch(res, item.id)}
                      >
                        Clean
                      </ConfirmButton>
                    </Row>
                  ))}
                </div>
              )}
            </div>

            <div className="exo-st-section">
              <h3 className="exo-st-subhead">Compress games</h3>
              <CompressGames list={list} onList={onList} onRead={onReadCompress} />
            </div>
          </section>
        </div>
      </div>
    </div>,
    document.body,
  )
}
