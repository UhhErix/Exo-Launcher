import { memo, useEffect, useState } from 'react'
import { clock, openCaptures, type CaptureItem } from '../../../lib/capture'
import { rowTime } from '../../../lib/discordNow'
import { host, onHostEvent } from '../../../lib/host'
import { report } from '../../../lib/report'
import type { ShareTarget } from '../../../lib/playRooms'
import { formatSize } from '../../../lib/utils'
import { Gallery, Play } from '../../../brand/icons'
import { useChoiceMenu, type MenuItem } from '../../ui/Menu'
import { Spinner } from '../../ui/Spinner'
import { useSaid } from '../../ui/useSaid'


/** Discord takes 25 MB at once (ClipShare.MostBytes): a bigger clip is shrunk first, with Exo Capture's own Shrink. */
export const MOST_SHARE_BYTES = 25 * 1024 * 1024

/**
 * One capture in Play: its picture (a press opens it in Captures, to watch, trim or edit), when and how big, and one press to
 * share it. Share asks where, in a menu of the people who play this game and your recent conversations, and the chosen row asks
 * in place ("Send?") before it posts: a message can't be taken back. The host reads the file and refuses what is over
 * 25 MB; one that is says Shrink instead, and Shrink makes it smaller in place.
 */
export const ClipTile = memo(function ClipTile({ item, targets }: { item: CaptureItem; targets: ShareTarget[] }) {
  const [sending, setSending] = useState(false)
  const [said, say] = useSaid(2400)
  const [problem, setProblem] = useState<string | null>(null)
  const [shrinking, setShrinking] = useState(false)
  const choice = useChoiceMenu()
  const big = item.size > MOST_SHARE_BYTES

  async function share(from: Element) {
    if (sending) return
    const rows: MenuItem[] = targets.map(target => ({ id: target.channelId, label: target.name, confirm: 'Send?', section: target.onGame ? 'Playing this' : 'Recent' }))
    const channelId = await choice.ask('Send to', rows, from)
    if (!channelId) return
    const target = targets.find(t => t.channelId === channelId)
    setProblem(null)
    setSending(true)
    try {
      const result = await host.discordShareClip(channelId, item.id)
      if (result.ok) say(`Sent to ${target?.name ?? 'them'}`)
      else setProblem(result.message ?? 'Discord did not take that.')
    } catch (error) {
      report(error, 'ClipTile.share')
      setProblem('Discord did not take that.')
    } finally { setSending(false) }
  }

  // Shrinking runs on in the host: it ends with capture.shrunk (the smaller file, or why it could not be).
  useEffect(() => onHostEvent('capture.shrunk', event => {
    if (event.id !== item.id) return
    setShrinking(false)
    if (!event.item && event.message) setProblem(event.message)
  }), [item.id])

  function shrink() {
    setProblem(null)
    setShrinking(true)
    host.captureItem(item.id, 'shrink').then(result => { if (!result.ok) { setProblem(result.message ?? 'Could not shrink it.'); setShrinking(false) } },
      (error: unknown) => { report(error, 'ClipTile.shrink'); setProblem('Could not shrink it.'); setShrinking(false) })
  }

  const when = rowTime(Date.parse(item.created))
  return (
    <li className="exo-play-clip">
      <button type="button" className="exo-play-clip-open" aria-label={`Open ${item.name}`} onClick={() => openCaptures({ itemId: item.id, gameId: item.gameId ?? null })}>
        <span className="exo-play-art">
          {item.thumb ? <img className="exo-play-art-img" src={item.thumb} alt="" loading="lazy" decoding="async" draggable={false} /> : <span className="exo-play-art-none" aria-hidden="true">{item.kind === 'video' ? <Play size={20} /> : <Gallery size={20} />}</span>}
          {item.kind === 'video' && item.durationMs ? <span className="exo-play-duration">{clock(item.durationMs)}</span> : null}
        </span>
      </button>
      <div className="exo-play-clip-foot">
        <span className="exo-play-card-text">
          <strong>{item.name}</strong>
          <span>{[item.game, when, item.size >= 1_000_000 ? formatSize(item.size) : null].filter(Boolean).join(' · ')}</span>
        </span>
        {big && item.kind === 'video' ? (
          <button type="button" className="exo-set-btn is-compact" disabled={shrinking} aria-label={`Shrink ${item.name}`} onClick={shrink}>
            {shrinking ? <Spinner /> : null}{shrinking ? 'Shrinking' : 'Shrink to share'}
          </button>
        ) : (
          <button type="button" className="exo-set-btn is-compact" disabled={sending || !targets.length} aria-haspopup="menu" aria-label={`Share ${item.name}`} onClick={event => void share(event.currentTarget)}>
            {sending ? <Spinner /> : null}{sending ? 'Sending' : said ?? 'Share'}
          </button>
        )}
      </div>
      {problem ? <p className="exo-play-problem" role="alert">{problem}</p> : null}
      {choice.menu}
    </li>
  )
})
