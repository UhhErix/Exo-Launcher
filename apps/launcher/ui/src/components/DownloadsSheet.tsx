import { useCallback, useEffect, useState } from 'react'
import { ChevronDown, ChevronUp, Pause, Play, Remove } from '../brand/icons'
import { host, onHostEvent, type DownloadLine, type DownloadsList, type InstallProgress } from '../lib/host'
import { report } from '../lib/report'
import { GameOverlay } from '../motion'
import { Switch } from './Switch'

/** "18.4 MB/s", "740 KB/s". */
function speed(bytesPerSecond?: number | null) {
  if (!bytesPerSecond || bytesPerSecond <= 0) return null
  return bytesPerSecond >= 1_048_576 ? `${(bytesPerSecond / 1_048_576).toFixed(1)} MB/s` : `${Math.round(bytesPerSecond / 1024)} KB/s`
}

const STORE: Record<string, string> = { steam: 'Steam', epic: 'Epic', gog: 'GOG', amazon: 'Amazon', riot: 'Riot', xbox: 'Xbox', ea: 'EA', ubisoft: 'Ubisoft', battlenet: 'Battle.net' }

/**
 * Downloads (owner, 2026-11-19): every download and update Exo runs, in one list. What runs shows its progress and can be
 * paused (where its store lets Exo pause it) or stopped; the queue moves up and down and loses a game with ×; what is
 * paused resumes. Pause all holds every download until Resume all. Each game's switch keeps it in or out of Update games
 * automatically. Opened from the Library's More menu.
 */
export function DownloadsSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [list, setList] = useState<DownloadsList | null>(null)
  const [progress, setProgress] = useState<InstallProgress | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const read = useCallback(() => {
    host.downloadsList().then(setList, (e: unknown) => report(e, 'DownloadsSheet.list'))
  }, [])

  useEffect(() => {
    if (!open) return
    read()
    host.progress().then(setProgress, () => { })
    const offQueue = onHostEvent('install.queue', () => read())
    const offProgress = onHostEvent('install.progress', (data) => {
      setProgress(data as InstallProgress)
      if (!(data as InstallProgress).isActive) read()
    })
    return () => { offQueue(); offProgress() }
  }, [open, read])

  // One press at a time per game; the list is read again after each.
  const act = async (key: string, run: () => Promise<unknown>) => {
    setBusy(key)
    try { await run() } catch (e) { report(e, 'DownloadsSheet.act') } finally { setBusy(null); read() }
  }

  const lines = list?.lines ?? []
  const running = lines.filter(line => line.state === 'running')
  const queued = lines.filter(line => line.state === 'queued')
  const paused = lines.filter(line => line.state === 'paused')

  return (
    <GameOverlay open={open} label="Downloads" scrim={<button type="button" className="exo-game-overlay-scrim" aria-label="Close" tabIndex={-1} onClick={onClose} />}>
      <section className="exo-downloads-sheet" aria-labelledby="downloads-title">
        <header className="exo-downloads-head">
          <h2 id="downloads-title">Downloads</h2>
          {list?.pausedByPlayer ? (
            <button type="button" className="exo-set-btn is-primary" disabled={busy === 'all'} onClick={() => void act('all', host.downloadsResumeAll)}>
              <Play size={14} />Resume all
            </button>
          ) : (
            <button type="button" className="exo-set-btn" disabled={busy === 'all' || lines.length === 0} onClick={() => void act('all', host.downloadsPauseAll)}>
              <Pause size={14} />Pause all
            </button>
          )}
        </header>
        {list?.held && !list.pausedByPlayer ? <p className="exo-downloads-note">{list.held}</p> : null}
        {list && lines.length === 0 ? <p className="exo-downloads-empty">Nothing is downloading.</p> : null}

        {running.length ? <Group title="Now">{running.map(line => (
          <Row key={line.gameId} line={line} busy={busy === line.gameId} progress={progress?.gameId === line.gameId ? progress : null}
            onAutoUpdate={on => void act(line.gameId, () => host.gameAutoUpdate(line.gameId, on))}>
            {line.canPause ? <button type="button" className="exo-set-btn" aria-label={`Pause ${line.title}`} disabled={busy === line.gameId}
              onClick={() => void act(line.gameId, () => host.pauseInstall(line.gameId))}><Pause size={14} />Pause</button> : null}
            <button type="button" className="exo-set-btn" aria-label={`Stop ${line.title}`} disabled={busy === line.gameId}
              onClick={() => void act(line.gameId, () => host.cancelInstall(line.gameId))}>Stop</button>
          </Row>
        ))}</Group> : null}

        {queued.length ? <Group title="Up next">{queued.map((line, i) => (
          <Row key={line.gameId} line={line} busy={busy === line.gameId} onAutoUpdate={on => void act(line.gameId, () => host.gameAutoUpdate(line.gameId, on))}>
            <button type="button" className="exo-set-btn is-icon" aria-label={`Move ${line.title} up`} disabled={i === 0 || busy === line.gameId}
              onClick={() => void act(line.gameId, () => host.downloadsMove(line.gameId, -1))}><ChevronUp size={16} /></button>
            <button type="button" className="exo-set-btn is-icon" aria-label={`Move ${line.title} down`} disabled={i === queued.length - 1 || busy === line.gameId}
              onClick={() => void act(line.gameId, () => host.downloadsMove(line.gameId, 1))}><ChevronDown size={16} /></button>
            <button type="button" className="exo-set-btn is-icon" aria-label={`Remove ${line.title} from the queue`} disabled={busy === line.gameId}
              onClick={() => void act(line.gameId, () => host.dequeue(line.gameId))}><Remove size={16} /></button>
          </Row>
        ))}</Group> : null}

        {paused.length ? <Group title="Paused">{paused.map(line => (
          <Row key={line.gameId} line={line} busy={busy === line.gameId} onAutoUpdate={on => void act(line.gameId, () => host.gameAutoUpdate(line.gameId, on))}>
            <button type="button" className="exo-set-btn" aria-label={`Resume ${line.title}`} disabled={busy === line.gameId}
              onClick={() => void act(line.gameId, () => host.resumeDownload(line.gameId))}><Play size={14} />Resume</button>
            <button type="button" className="exo-set-btn is-icon" aria-label={`Remove ${line.title}`} disabled={busy === line.gameId}
              onClick={() => void act(line.gameId, () => host.dequeue(line.gameId))}><Remove size={16} /></button>
          </Row>
        ))}</Group> : null}
      </section>
    </GameOverlay>
  )
}

function Group({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="exo-downloads-group" aria-label={title}>
      <h3>{title}</h3>
      <ul>{children}</ul>
    </section>
  )
}

function Row({ line, busy, progress, onAutoUpdate, children }: {
  line: DownloadLine
  busy: boolean
  progress?: InstallProgress | null
  onAutoUpdate: (on: boolean) => void
  children: React.ReactNode
}) {
  const percent = progress?.percent != null ? Math.round(progress.percent) : null
  const detail = progress ? [progress.status, speed(progress.bytesPerSecond)].filter(Boolean).join(' · ') : STORE[line.store] ?? line.store
  const label = `downloads-auto-${line.gameId}`
  return (
    <li className="exo-downloads-row" aria-busy={busy || undefined}>
      <div className="exo-downloads-what">
        <strong>{line.title}</strong>
        <span>{detail}</span>
        {percent != null ? <span className="exo-downloads-bar" role="progressbar" aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100}><span style={{ width: `${percent}%` }} /></span> : null}
      </div>
      <label className="exo-downloads-auto" id={label}>
        Update automatically
        <Switch checked={line.autoUpdate} labelledBy={label} disabled={busy} onChange={onAutoUpdate} />
      </label>
      <div className="exo-downloads-actions">{children}</div>
    </li>
  )
}
