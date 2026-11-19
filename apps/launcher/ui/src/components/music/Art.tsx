import { useEffect, useState } from 'react'
import { Folder, Heart, MusicNotes } from '../../brand/icons'

/**
 * Cover art that never flashes empty: a new picture is fetched and decoded
 * first, and the previous one stays until it is ready. Liked Songs is a heart
 * and a folder its icon; art the service has no picture for shows a quiet note.
 */

/** Liked Songs (Spotify) and Liked Music (YouTube Music, playlist LM) draw the heart when they have no picture. */
export function isLikedCard(card: { kind: string; uri: string; image?: string }): boolean {
  return !card.image && (card.kind === 'collection' || /:VLLM$/.test(card.uri))
}

export function Art({ src, round = false, liked = false, folder = false, size, className }: {
  src?: string
  round?: boolean
  liked?: boolean
  folder?: boolean
  size?: number
  className?: string
}) {
  const [shown, setShown] = useState(src ?? '')
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const next = src ?? ''
    if (!next) {
      setShown('')
      setFailed(false)
      return
    }
    if (next === shown && !failed) return
    let live = true
    let timer = 0
    let tries = 0
    const load = () => {
      const image = new Image()
      image.referrerPolicy = 'no-referrer'
      image.decoding = 'async'
      image.src = next
      image.decode().then(
        () => { if (live) { setShown(next); setFailed(false) } },
        () => {
          if (!live) return
          if (tries < 2) {
            tries += 1
            timer = window.setTimeout(load, tries * 350)
          } else {
            setShown(next)
            setFailed(true)
          }
        })
    }
    load()
    return () => { live = false; window.clearTimeout(timer) }
  }, [src, shown, failed])
  const style = size ? { width: size, height: size } : undefined
  const classes = ['exo-music-art', round && 'is-round', (liked || folder) && 'is-icon', className].filter(Boolean).join(' ')
  if (liked || folder) {
    const glyph = size ? Math.round(size / 3) : 16
    return <span className={classes} style={style}>{liked ? <Heart size={glyph} filled /> : <Folder size={glyph} />}</span>
  }
  return (
    <span className={classes} style={style}>
      {shown && !failed
        ? <img src={shown} alt="" decoding="async" referrerPolicy="no-referrer" onError={() => { if (shown === (src ?? '')) setFailed(true) }} />
        : src && !failed ? null : <MusicNotes size={size ? Math.round(size / 4) : 14} />}
    </span>
  )
}
