import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent, type CompressAction, type CompressState } from '../../lib/host'
import { report } from '../../lib/report'
import { formatSize } from '../../lib/utils'
import { Row } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'
import { compressShown } from '../../lib/canUse'
import { Progress } from '../ui/Progress'

/** "Saved 18.3 GB", "Compressing 42%", "Saves about 12.4 GB": a game's compression in one line. */
export function compressWords(c: CompressState): string {
  const pct = c.percent != null ? ` ${Math.round(c.percent * 100)}%` : ''
  switch (c.state) {
    case 'compressing': return c.held ?? `Compressing${pct}`
    case 'uncompressing': return c.held ?? `Putting back${pct}`
    case 'queued': return 'Waiting its turn'
    case 'paused': return 'Paused'
    case 'compressed': return `Saved ${formatSize(c.saved)}`
    case 'changed': return `Saved ${formatSize(c.saved)} · an update added ${formatSize(c.changed)}`
    case 'needsConsent': return c.antiCheat ? `Uses ${c.antiCheat}` : c.message ?? 'Not compressed'
    case 'refused': return c.message ?? 'Not compressed'
    default: return c.estimate ? `${c.sampled ? 'Saves' : 'Could save'} about ${formatSize(c.estimate)}` : 'Not compressed'
  }
}

/** What stopped the last job ("The drive is full."), shown as the row's error; refusals are its words already. */
export const stopped = (c: CompressState) => c.message && c.state !== 'refused' && c.state !== 'needsConsent' ? c.message : null

/** A game's compression, kept current by compress.progress; `act` answers with the new state or why not. */
export function useCompress(id: string, sample: boolean) {
  const [compress, setCompress] = useState<CompressState | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    host.compressState(id).then(async (answer) => {
      if (!live || !answer.compress) return
      setCompress(answer.compress)
      // Not yet sampled: a few MB of each kind of file, for what it would really save. Compressed: what an update wrote back.
      if (sample && ((answer.compress.state === 'none' && !answer.compress.sampled) || answer.compress.state === 'compressed')) {
        const read = await host.compressState(id, true)
        if (live && read.compress) setCompress(read.compress)
      }
    }).catch((e: unknown) => report(e, 'Compress.state', { level: 'warn' }))
    const off = onHostEvent('compress.progress', event => { if (event.id === id) setCompress(event.compress) })
    return () => { live = false; off() }
  }, [id, sample])
  const act = useCallback(async (action: CompressAction) => {
    setError(null)
    try {
      const answer = await host.compressAct(id, action, action === 'allow' ? compress?.antiCheat ?? undefined : undefined)
      if (answer.compress) setCompress(answer.compress)
      if (!answer.ok) setError(answer.message ?? 'Exo couldn’t do that. Try again.')
    } catch (e) {
      report(e, 'Compress.act')
      setError('Exo couldn’t do that. Try again.')
    }
  }, [id, compress?.antiCheat])
  return { compress, error, act }
}

/** The buttons for a game's state: compress, pause, resume, stop, put back, allow. */
export function CompressActions({ compress: c, locked, act }: { compress: CompressState; locked: boolean; act: (action: CompressAction) => void }) {
  const size = c.size ? formatSize(c.size) : null
  switch (c.state) {
    case 'compressing':
      return <>
        <button type="button" className="exo-set-btn" onClick={() => act('pause')}>Pause</button>
        <button type="button" className="exo-set-btn" onClick={() => act('cancel')}>Stop</button>
      </>
    case 'uncompressing':
      return <button type="button" className="exo-set-btn" onClick={() => act('cancel')}>Stop</button>
    case 'queued':
      return <button type="button" className="exo-set-btn" onClick={() => act('cancel')}>Cancel</button>
    case 'paused':
      return <>
        <button type="button" className="exo-set-btn" disabled={locked} onClick={() => act('resume')}>Resume</button>
        <Uncompress compress={c} locked={locked} act={act} />
      </>
    case 'compressed':
      return <Uncompress compress={c} locked={locked} act={act} />
    case 'changed':
      return <>
        <button type="button" className="exo-set-btn" disabled={locked} onClick={() => act('compress')}>Compress again</button>
        <Uncompress compress={c} locked={locked} act={act} />
      </>
    case 'needsConsent':
      return c.antiCheat ? (
        <ConfirmButton className="exo-set-btn" disabled={locked} question="Allow? Its programs stay as they are." onConfirm={() => act('allow')}>Allow</ConfirmButton>
      ) : null
    case 'refused':
      return null
    default:
      return <ConfirmButton className="exo-set-btn" disabled={locked} question={size ? `Compress ${size}?` : 'Compress it?'} onConfirm={() => act('compress')}>Compress</ConfirmButton>
  }
}

function Uncompress({ compress: c, locked, act }: { compress: CompressState; locked: boolean; act: (action: CompressAction) => void }) {
  return (
    <ConfirmButton className="exo-set-btn" disabled={locked} question={c.saved ? `Use ${formatSize(c.saved)} again?` : 'Put it back?'} onConfirm={() => act('uncompress')}>
      Uncompress
    </ConfirmButton>
  )
}

/**
 * Properties › Installed files: the game's compression (Windows' own, the game reads it as before) in one row, its state
 * beside the control, the bar while it works. A game not yet sampled is sampled as the row opens.
 */
export function CompressRow({ gameId, locked }: { gameId: string; locked: boolean }) {
  const { compress, error, act } = useCompress(gameId, !locked)
  // A game Exo will never compress has no row, as in PC › Compress games.
  if (!compress || !compressShown(compress)) return null
  const working = compress.state === 'compressing' || compress.state === 'uncompressing'
  return (
    <div className="exo-props-row">
      <Row label="Compress" detail={compressWords(compress)} error={error ?? stopped(compress)}>
        {working && !compress.held ? <Progress size="small" value={compress.percent} label="Compressing the game" /> : null}
        <CompressActions compress={compress} locked={locked} act={action => void act(action)} />
      </Row>
    </div>
  )
}
