import { useEffect, useState } from 'react'
import { host, onHostEvent, type RuntimeItem, type RuntimeResult, type RuntimesProgress } from '../../lib/host'
import { report } from '../../lib/report'
import { CopyLogs } from '../ui/ErrorNote'
import { Progress, ProgressWords } from '../ui/Progress'
import { Row, State } from '../settings/parts'

type Mode = 'update' | 'repair'
type Run = { mode: Mode; progress: RuntimesProgress | null }

const OUTCOME: Record<RuntimeResult['outcome'], string> = { installed: 'Installed', newer: 'Already newer', restart: 'Restart to finish', failed: 'Failed' }

function mb(bytes: number) {
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / 1e6))} MB`
}

/** What one press does: what isn't here (but what Microsoft no longer supports), and every older version. */
function runtimesToDo(items: RuntimeItem[]) {
  return {
    missing: items.filter(item => item.state === 'missing' && item.bulk),
    outdated: items.filter(item => item.state === 'outdated'),
  }
}

/** What a press runs: Update, what's missing and older; Repair all, every Visual C++. */
function planOf(items: RuntimeItem[], mode: Mode) {
  const { missing, outdated } = runtimesToDo(items)
  return mode === 'repair' ? items.filter(item => item.group === 'vc') : [...missing, ...outdated]
}

/** About how much a run downloads (an item of unknown size counted at 60 MB). */
function download(plan: RuntimeItem[]) {
  return mb(plan.reduce((sum, item) => sum + (item.size || 60e6), 0))
}

/**
 * "3 to install, 2 to update · about 240 MB": the size is read before the press, so the press asks nothing; Windows' one
 * prompt for the whole run is its confirm (DESIGN.md › Confirm).
 */
function status(items: RuntimeItem[]) {
  const { missing, outdated } = runtimesToDo(items)
  const todo = [missing.length > 0 && `${missing.length} to install`, outdated.length > 0 && `${outdated.length} to update`].filter(Boolean).join(', ')
  return todo ? `${todo} · about ${download([...missing, ...outdated])}` : ''
}

function working(progress: RuntimesProgress | null, mode: Mode) {
  if (!progress) return mode === 'repair' ? 'Getting Visual C++ ready' : 'Getting ready'
  const of = `${progress.index} of ${progress.count}`
  if (progress.phase === 'download') return `Downloading ${progress.name} · ${of} · ${mb(progress.doneBytes)} of ${mb(progress.allBytes)}`
  if (progress.phase === 'verify') return `Checking ${progress.name}`
  if (progress.phase === 'install') return `Installing ${progress.name} · ${of}`
  return 'Checking what installed'
}

/**
 * How far the whole run is, 0 to 1: the downloads by their bytes (checked as each lands), then Windows' run by the
 * installers it has finished; null for the last check and before the first word.
 */
function runShare(progress: RuntimesProgress | null) {
  if (!progress) return null
  if (progress.phase === 'install') return progress.count > 0 ? (progress.index - 1) / progress.count : null
  if (progress.phase === 'check') return null
  return progress.allBytes > 0 ? progress.doneBytes / progress.allBytes : null
}

/** "5 installed, 1 needs a restart, 1 failed". */
function summary(results: RuntimeResult[]) {
  const count = (outcome: RuntimeResult['outcome']) => results.filter(result => result.outcome === outcome).length
  const parts = [
    count('installed') > 0 && `${count('installed')} installed`,
    count('newer') > 0 && `${count('newer')} already newer`,
    count('restart') > 0 && `${count('restart')} ${count('restart') === 1 ? 'needs' : 'need'} a restart`,
    count('failed') > 0 && `${count('failed')} failed`,
  ].filter(Boolean)
  return parts.length > 0 ? `${parts.join(', ')}.` : 'Nothing needed installing.'
}

/**
 * PC › Updates' game runtimes (runtimes.list, read with the room), from their makers' own installers: what is missing or
 * older, each gone once it's done. One press installs and updates them all, the downloads carrying the Exo bar, Windows
 * asking once, then each row says how it went; Repair all runs every Visual C++ installer again. Nothing is ever
 * uninstalled. A run hands the list what it found after.
 */
export function RuntimesPanel({ items, onItems }: { items: RuntimeItem[]; onItems: (items: RuntimeItem[]) => void }) {
  const [run, setRun] = useState<Run | null>(null)
  const [results, setResults] = useState<Record<string, RuntimeResult>>({})
  const [note, setNote] = useState<{ text: string; error: boolean } | null>(null)
  const [canceling, setCanceling] = useState(false)
  // What a press would do (never one Microsoft no longer supports, which waits for a game to ask), a result, or a download.
  const shown = (item: RuntimeItem) => (item.state === 'missing' && item.bulk) || item.state === 'outdated' || results[item.id]?.outcome === 'failed' || run?.progress?.id === item.id

  useEffect(() => onHostEvent('runtimes.progress', progress => setRun(current => current && { ...current, progress })), [])

  async function start(mode: Mode) {
    if (run || planOf(items, mode).length === 0) return
    setRun({ mode, progress: null })
    setResults({})
    setNote(null)
    try {
      const result = await host.runtimesInstall(mode)
      if (result.items) onItems(result.items)
      setResults(Object.fromEntries(result.results.map(item => [item.id, item])))
      if (result.declined || result.canceled || result.results.length === 0) setNote(result.message ? { text: result.message, error: !result.ok && !result.canceled } : null)
      else setNote({ text: summary(result.results), error: result.results.some(item => item.outcome === 'failed') })
    } catch (error) {
      report(error, 'RuntimesPanel.install')
      setNote({ text: 'The runtimes couldn’t be installed. Try again.', error: true })
    } finally {
      setRun(null)
      setCanceling(false)
    }
  }

  async function cancel() {
    setCanceling(true)
    try {
      const result = await host.runtimesCancel()
      if (!result.ok && result.message) setNote({ text: result.message, error: false })
    } catch (error) {
      report(error, 'RuntimesPanel.cancel')
    }
  }

  return (
    <div className="exo-runtimes">
      <Head items={items} run={run} note={note} canceling={canceling} onStart={mode => void start(mode)} onCancel={() => void cancel()} />
      <div className="exo-set-list">
        {items.filter(shown).map(item => <RuntimeRow key={item.id} item={item} result={results[item.id]} progress={run?.progress?.id === item.id ? run.progress : null} />)}
      </div>
    </div>
  )
}

/** The status line and its two buttons; while they install, the words and the Exo bar for all of it, and Cancel while it downloads. */
function Head({ items, run, note, canceling, onStart, onCancel }: {
  items: RuntimeItem[]
  run: Run | null
  note: { text: string; error: boolean } | null
  canceling: boolean
  onStart: (mode: Mode) => void
  onCancel: () => void
}) {
  const { missing, outdated } = runtimesToDo(items)
  const phase = run?.progress?.phase
  const downloading = run !== null && (phase === undefined || phase === 'download' || phase === 'verify')
  const overall = runShare(run?.progress ?? null)
  const action = missing.length > 0 && outdated.length > 0 ? 'Install and update' : missing.length > 0 ? 'Install' : 'Update'
  if (run) {
    return (
      <div className="exo-runtimes-head">
        <div className="exo-runtimes-run">
          <span className="exo-runtimes-status"><ProgressWords words={working(run.progress, run.mode)} value={overall} /></span>
          {/* Windows' run measures from its own start: a new bar, never the downloads' sliding back. */}
          <Progress key={phase === 'install' || phase === 'check' ? 'install' : 'download'} value={overall} label={working(run.progress, run.mode)} />
        </div>
        <div className="exo-runtimes-actions">
          {downloading ? <button type="button" className="exo-set-btn" disabled={canceling} onClick={onCancel}>Cancel</button> : null}
        </div>
      </div>
    )
  }
  return (
    <div className="exo-runtimes-head">
      <Status items={items} note={note} />
      <div className="exo-runtimes-actions">
        {missing.length + outdated.length > 0 ? (
          <button type="button" className="exo-set-btn is-primary" onClick={() => onStart('update')}>{action}</button>
        ) : null}
        <button type="button" className="exo-set-btn" aria-label={`Repair all, about ${download(planOf(items, 'repair'))}`} onClick={() => onStart('repair')}>Repair all</button>
      </div>
    </div>
  )
}

/**
 * Where the runtimes stand. A run that went well says so for a few seconds, then the line is what's left to do again
 * (a "7 installed" that stayed read as all done beside an Install button); one that failed stays, with Copy logs and
 * what's left beside it.
 */
function Status({ items, note }: { items: RuntimeItem[]; note: { text: string; error: boolean } | null }) {
  const [shown, setShown] = useState(note)
  useEffect(() => {
    setShown(note)
    if (!note || note.error) return
    const timer = window.setTimeout(() => setShown(null), 6000)
    return () => window.clearTimeout(timer)
  }, [note])
  if (!shown?.error) return <span className="exo-runtimes-status" role="status">{shown?.text ?? status(items)}</span>
  const left = runtimesToDo(items)
  return (
    <span className="exo-runtimes-status is-bad" role="status">
      {shown.text}{left.missing.length + left.outdated.length > 0 ? ` ${status(items)}.` : ''} <CopyLogs error={shown.text} />
    </span>
  )
}

/** A runtime's versions: an older one's and the newest; Microsoft's end of support always shows. */
function versionsOf(item: RuntimeItem) {
  const version = item.state === 'outdated' && item.installed ? `${item.installed} → ${item.latest}` : null
  return [version, item.endOfSupport ? 'End of support' : null].filter(Boolean).join(' · ')
}

function RuntimeRow({ item, result, progress }: { item: RuntimeItem; result: RuntimeResult | undefined; progress: RuntimesProgress | null }) {
  const label = progress ? working(progress, 'update') : ''
  const share = progress?.phase === 'download' && progress.total ? progress.bytes / progress.total : null
  return (
    <Row label={item.name} detail={versionsOf(item) || undefined} error={result?.outcome === 'failed' ? result.message : null}>
      {progress?.phase === 'download' ? (
        <>
          {share === null ? null : <State><ProgressWords words="Downloading" value={share} /></State>}
          <Progress size="small" value={share} label={label} />
        </>
      ) : progress ? (
        <Progress size="small" value={null} label={label} />
      ) : result ? (
        <State on={result.outcome !== 'failed'}>{OUTCOME[result.outcome]}</State>
      ) : item.state === 'missing' ? (
        <State>Missing</State>
      ) : null}
    </Row>
  )
}

