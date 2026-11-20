import '../../styles/captures-room.css'
import { lazy, Suspense, useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode, type RefObject } from 'react'
import { GameOverlay } from '../../motion'
import { Check, ChevronLeft, Close, History, Info, More, Notes, Pencil, Record, Scissors, Settings, Share, Star as StarGlyph, Trash } from '../../brand/icons'
import { host, onHostEvent, type Game } from '../../lib/host'
import { cancelReel, clearReelMessage, clock, closeCaptures, keysLabel, makeReel, onReelDone, useCapture, useCapturesSheet, useReel, type CaptureItem } from '../../lib/capture'
import { byDay, filterCaptures, monthTitle, onThisDay, scrubStops, type Day, type GalleryFilter } from '../../lib/gallery'
import { reelOfGame, reelOfPicked } from '../../lib/reel'
import type { NotesRequest } from '../../lib/notes'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { allowHover } from '../../lib/hoverGate'
import { report } from '../../lib/report'
import { CaptureSettingsView } from './CaptureSettings'
import { ClipEditor, playback, playerKey, useFrameKeys } from './ClipEditor'
import { NotesView } from './NotesView'
import { SnipEditor } from './SnipEditor'
const SendToDialog = lazy(() => import('../discord/MessageTools').then((m) => ({ default: m.SendToDialog })))
import { pane, pressedArt, transition, visible } from '../../lib/viewTransition'
import { ErrorNote } from '../ui/ErrorNote'
import { Menu, type MenuItem } from '../ui/Menu'
import { Wait, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'
import { ActionButton } from '../ui/ConfirmButton'
import { Progress } from '../ui/Progress'
import { ArtImg } from '../ui/ArtImg'
import { errorText, type PlainError } from '../../lib/errorWords'

/** The captures as last read: the sheet shows them at once the next time it opens, while the folder is read again. */
let lastList: CaptureItem[] | null = null
function remember(list: CaptureItem[] | null) {
  if (list) lastList = list
}

/** Cards drawn at first, and added each time the list's end comes near: a folder of thousands draws only what is scrolled to. */
const PAGE = 60

function Star({ on }: { on: boolean }) {
  return <StarGlyph size={14} filled={on} />
}

function when(iso: string): string {
  const date = new Date(iso)
  const today = new Date()
  const time = date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  if (date.toDateString() === today.toDateString()) return `Today ${time}`
  const yesterday = new Date(today.getTime() - 86_400_000)
  if (date.toDateString() === yesterday.toDateString()) return `Yesterday ${time}`
  return `${date.toLocaleDateString(undefined, date.getFullYear() === today.getFullYear() ? { day: 'numeric', month: 'short' } : { day: 'numeric', month: 'short', year: 'numeric' })} ${time}`
}

/** The captures folder a game's title gets, as the host names it (MainWindow.Capture.cs SafeCaptureName). */
function captureFolder(title: string): string {
  // eslint-disable-next-line no-control-regex -- the host blanks control characters too
  let name = title.replace(/[\u0000-\u001f<>:"/\\|?*]/g, ' ').trim().replace(/\.+$/, '').trim()
  if (name.length > 80) name = name.slice(0, 80).replace(/[ .]+$/, '')
  return name || 'Capture'
}

function size(bytes: number): string {
  return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GB` : `${Math.max(0.1, bytes / 1024 ** 2).toFixed(1)} MB`
}


/** The Captures sheet over any room: Exo Capture's clips, recordings and snips, and Exo Notes. */
export function CapturesOverlay({ games }: { games: readonly Game[] }) {
  const sheet = useCapturesSheet()
  return (
    <GameOverlay
      open={sheet.open}
      label="Captures"
      scrim={<button type="button" className="exo-game-overlay-scrim" aria-label="Close" tabIndex={-1} onClick={closeCaptures} />}
    >
      <CapturesSheet key={sheet.seq} gameId={sheet.gameId ?? null} itemId={sheet.itemId ?? null} edit={!!sheet.edit} settings={!!sheet.settings} notes={sheet.notes ?? null} games={games} />
    </GameOverlay>
  )
}

/**
 * One tile of the grid, edge to edge with its neighbours: the thumbnail; a video previews itself while hovered.
 * Favorite and Delete sit on its corner while hovered; while selecting, a click picks the tile instead.
 */
function CaptureCard({ item, selected, badge, onOpen, onFavourite, onDelete }: {
  item: CaptureItem
  /** Null: not selecting. */
  selected: boolean | null
  /** A word over the tile's corner (On this day's "1 year ago"). */
  badge?: string
  onOpen: () => void
  onFavourite: () => void
  onDelete: () => void
}) {
  const [hover, setHover] = useState(false)
  const name = `${item.kind === 'image' ? 'Screenshot' : 'Clip'} ${when(item.created)}`
  return (
    <div className={`exo-capture-card${selected ? ' is-selected' : ''}`} onPointerEnter={event => { if (allowHover(event)) setHover(true) }} onPointerLeave={() => setHover(false)}>
      <button type="button" className="exo-capture-thumb" data-capture-id={item.id} onClick={onOpen} aria-label={name} aria-pressed={selected ?? undefined}>
        <ArtImg src={item.thumb} loading="lazy" decoding="async" fallback={<span className="exo-capture-thumb-empty" />} />
        {hover && selected === null && item.kind === 'video' && !shouldReduceMotion() ? <video src={item.url} muted autoPlay loop playsInline preload="metadata" /> : null}
        {item.durationMs ? <span className="exo-capture-duration">{clock(item.durationMs)}</span> : null}
        {badge ? <span className="exo-capture-badge">{badge}</span> : null}
        {selected !== null ? (
          <span className="exo-capture-check" aria-hidden>
            {selected ? <Check size={14} /> : null}
          </span>
        ) : null}
      </button>
      {selected === null ? (
        <div className="exo-capture-corner">
          <button type="button" className="exo-capture-corner-btn exo-capture-delete" aria-label={`Delete ${name}`} onClick={onDelete}>
            <Trash size={14} />
          </button>
          <button
            type="button"
            className={`exo-capture-corner-btn exo-capture-fav${item.favourite ? ' is-on' : ''}`}
            aria-label={item.favourite ? 'Remove from favorites' : 'Favorite'}
            aria-pressed={item.favourite}
            onClick={onFavourite}
          >
            <Star on={item.favourite} />
          </button>
        </div>
      ) : null}
    </div>
  )
}

/** Where the pointer is from an element's middle. */
function fromMiddle(element: Element, event: { clientX: number; clientY: number }): [number, number] {
  const box = element.getBoundingClientRect()
  return [event.clientX - box.left - box.width / 2, event.clientY - box.top - box.height / 2]
}

/**
 * A snip, fitted to the stage. The wheel zooms in at the pointer, a drag moves it, and a double-click fits it again
 * (or, fitted, shows the snip's own pixels). Past twice its own size, its pixels stay square.
 */
function ZoomImage({ src, onSize }: { src: string; onSize: (size: { width: number; height: number }) => void }) {
  const image = useRef<HTMLImageElement>(null)
  const [view, setView] = useState({ scale: 1, x: 0, y: 0, square: false })
  const drag = useRef<{ x: number; y: number } | null>(null)
  const [panning, setPanning] = useState(false)

  // Kept over the stage: centered on an axis it doesn't fill, its edges never pulled inside on one it does.
  const placed = useCallback((scale: number, x: number, y: number) => {
    const img = image.current, stage = img?.parentElement
    if (!img || !stage || scale <= 1) return { scale: 1, x: 0, y: 0, square: false }
    const room = (fitted: number, space: number) => Math.max(0, (fitted * scale - space) / 2)
    const rx = room(img.offsetWidth, stage.clientWidth), ry = room(img.offsetHeight, stage.clientHeight)
    return { scale, x: Math.min(rx, Math.max(-rx, x)), y: Math.min(ry, Math.max(-ry, y)), square: scale * img.offsetWidth >= 2 * img.naturalWidth }
  }, [])

  // The point under the pointer (from the stage's middle, where the fitted snip's middle is) stays under it.
  const zoom = useCallback((to: (scale: number) => number, px: number, py: number) => setView(v => {
    const img = image.current
    const most = img ? Math.max(4, (16 * img.naturalWidth) / Math.max(1, img.offsetWidth)) : 4
    const scale = Math.min(most, Math.max(1, to(v.scale)))
    return placed(scale, px - ((px - v.x) * scale) / v.scale, py - ((py - v.y) * scale) / v.scale)
  }), [placed])

  // React's wheel listener is passive; this one keeps the wheel from scrolling anything else.
  useEffect(() => {
    const stage = image.current?.parentElement
    if (!stage) return
    const onWheel = (event: WheelEvent) => {
      event.preventDefault()
      const [px, py] = fromMiddle(stage, event)
      zoom(scale => scale * Math.exp(-event.deltaY * (event.deltaMode === 1 ? 0.05 : 0.002)), px, py)
    }
    stage.addEventListener('wheel', onWheel, { passive: false })
    return () => stage.removeEventListener('wheel', onWheel)
  }, [zoom])

  const stop = () => { drag.current = null; setPanning(false) }
  const zoomed = view.scale > 1
  return (
    <img
      ref={image}
      src={src}
      alt=""
      draggable={false}
      onLoad={event => onSize({ width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight })}
      className={zoomed ? `is-zoomed${panning ? ' is-panning' : ''}${view.square ? ' is-square' : ''}` : undefined}
      style={zoomed ? { transform: `translate(${view.x}px, ${view.y}px) scale(${view.scale})` } : undefined}
      onPointerDown={event => {
        if (event.button !== 0 || !zoomed) return
        drag.current = { x: event.clientX - view.x, y: event.clientY - view.y }
        event.currentTarget.setPointerCapture(event.pointerId)
        setPanning(true)
      }}
      onPointerMove={event => {
        const from = drag.current
        if (from) setView(v => placed(v.scale, event.clientX - from.x, event.clientY - from.y))
      }}
      onPointerUp={stop}
      onPointerCancel={stop}
      onLostPointerCapture={stop}
      onDoubleClick={event => {
        const img = event.currentTarget, stage = img.parentElement
        if (!stage) return
        const [px, py] = fromMiddle(stage, event)
        zoom(scale => scale > 1 ? 1 : Math.max(2, img.naturalWidth / Math.max(1, img.offsetWidth)), px, py)
      }}
    />
  )
}


/** What the viewer last said: a result, or what went wrong (bad). */
type Note = { text: string; bad?: boolean }

/** A result reads as a status line; a failure as an error with Copy logs. */
function NoteLine({ note }: { note: Note | null }) {
  if (!note) return null
  return note.bad
    ? <ErrorNote className="exo-capture-note is-bad" message={note.text} />
    : <p className="exo-capture-note" role="status">{note.text}</p>
}

/** The Info pane's facts: what the capture is, from the list and from the picture itself once it has loaded. */
function InfoPane({ item, size: shown, game }: { item: CaptureItem; size: { width: number; height: number } | null; game: string }) {
  const facts: [string, string][] = [
    ['Game', game],
    ['Taken', new Date(item.created).toLocaleString(undefined, { dateStyle: 'long', timeStyle: 'short' })],
    ['Kind', item.kind === 'image' ? 'Screenshot' : item.derived ? 'Clip, made from another' : 'Clip'],
  ]
  if (shown) facts.push(['Resolution', `${shown.width} × ${shown.height}`])
  if (item.durationMs) facts.push(['Length', clock(item.durationMs)])
  facts.push(['Size', size(item.size)], ['Folder', `Exo Captures › ${item.game}`])
  return (
    <aside className="exo-viewer-info" aria-label="Info">
      <dl className="tabular-nums">
        {facts.map(([term, value]) => <div key={term}><dt>{term}</dt><dd>{value}</dd></div>)}
      </dl>
    </aside>
  )
}

/**
 * One capture, edge to edge (Windows Photos' viewer): a slim bar of Back, then Edit, Share, Favorite, Info, Delete and
 * More; Left and Right step through the list, Escape and the mouse's Back go back to it. Info is a pane on demand.
 * Edit opens the snip editor or the clip editor in its place.
 */
function Viewer({ item, game, edit, onStep, onBack, onFavourite, onChanged, onDeleted, onAdded }: {
  item: CaptureItem
  game: string
  edit: boolean
  /** Left and Right: the capture before or after this one in the list. */
  onStep: (by: -1 | 1) => void
  onBack: () => void
  onFavourite: () => void
  onChanged: (item: CaptureItem) => void
  onDeleted: () => void
  onAdded: (item: CaptureItem) => void
}) {
  const video = useRef<HTMLVideoElement>(null)
  const [note, setNote] = useState<Note | null>(null)
  const [editing, setEditing] = useState(edit)
  const [info, setInfo] = useState(false)
  const [sending, setSending] = useState(false)
  const [shrinking, setShrinking] = useState(false)
  const [shown, setShown] = useState<{ width: number; height: number } | null>(null)
  const busy = sending || shrinking
  useFrameKeys(video, !editing)

  // Escape and the mouse's Back button go back to the list, not out of the sheet: captured ahead of the sheet's own
  // Escape. A menu or dialog that has focus over the viewer keeps its own Escape. Left and Right step, outside full screen.
  useEffect(() => {
    if (editing) return
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return
      if (document.activeElement?.closest('[popover], dialog, [role="menu"], [role="listbox"]')) return
      event.preventDefault()
      event.stopPropagation()
      onBack()
    }
    const mouseBack = (event: MouseEvent) => {
      if (event.button !== 3) return
      event.preventDefault()
      onBack()
    }
    const arrows = (event: KeyboardEvent) => {
      if (!playerKey(event) || document.fullscreenElement || sending) return
      if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
      event.preventDefault()
      onStep(event.key === 'ArrowLeft' ? -1 : 1)
    }
    window.addEventListener('keydown', escape, true)
    window.addEventListener('mouseup', mouseBack, true)
    window.addEventListener('keydown', arrows)
    return () => {
      window.removeEventListener('keydown', escape, true)
      window.removeEventListener('mouseup', mouseBack, true)
      window.removeEventListener('keydown', arrows)
    }
  }, [editing, sending, onBack, onStep])

  // Make smaller: the host says how it went when it is done.
  useEffect(() => onHostEvent('capture.shrunk', ({ id, item: next, before, after, message }) => {
    if (id !== item.id) return
    setShrinking(false)
    if (next) {
      onChanged(next)
      setNote({ text: `Smaller now: ${size(before)} to ${size(after)}.` })
    } else setNote({ text: message ?? 'Left as it was.' })
  }), [item.id, onChanged])

  // Delete says how it went in itself (ActionButton: its ring, its check, or Bad and a shake), and why it failed on the
  // viewer's line; what the menus' rows did reads there too, as the menu that was pressed has closed (DESIGN.md › Notify).
  const failedHere = (error: PlainError) => setNote({ text: error.text, bad: true })
  const remove = () => { setNote(null); return host.captureItem(item.id, 'delete') }

  async function act(action: 'reveal' | 'copy' | 'shrink') {
    setNote(null)
    const failed = ACT_FAILED[action]
    try {
      const result = await host.captureItem(item.id, action)
      if (!result.ok) { setNote({ text: errorText(result.message, failed), bad: true }); return }
      if (result.item) onChanged(result.item)
      // DESIGN.md › Notify: the result of a press shows where it was, on the viewer's line.
      if (action === 'copy') setNote({ text: 'Copied' })
      if (action === 'shrink') { setShrinking(true); setNote({ text: result.message ?? 'Making it smaller…' }) }
    } catch (error) { setNote({ text: errorText(error, failed), bad: true }) }
  }
  async function copyText() {
    setNote(null)
    try {
      const result = await host.captureText(item.id)
      if (!result.ok || !result.text) { setNote({ text: errorText(result.message, 'No text found.'), bad: true }); return }
      await navigator.clipboard.writeText(result.text)
      setNote({ text: 'Copied the text.' })
    } catch (error) { setNote({ text: errorText(error, 'Exo couldn’t read the text. Try again.'), bad: true }) }
  }

  if (editing) {
    return item.kind === 'image'
      ? <SnipEditor item={item} onDone={saved => { setEditing(false); if (saved) { onChanged(saved); setNote({ text: 'Saved and copied.' }) } }} />
      : <ClipEditor item={item} onDone={() => setEditing(false)} onAdded={onAdded} />
  }

  const share: MenuItem[] = [
    { id: 'discord', label: 'Send to Discord', icon: <Share size={16} /> },
    { id: 'copy', label: item.kind === 'image' ? 'Copy picture' : 'Copy file' },
  ]
  const more: MenuItem[] = [
    ...(item.kind === 'image' ? [{ id: 'text', label: 'Copy text' }] : [{ id: 'shrink', label: 'Make smaller', disabled: shrinking }]),
    { id: 'reveal', label: 'Show in folder' },
  ]
  return (
    <div className="exo-viewer exo-motion-switch">
      <div className="exo-viewer-bar">
        <button type="button" className="exo-set-btn" onClick={onBack}><ChevronLeft size={14} />Back</button>
        <div className="exo-viewer-title min-w-0">
          <strong>{game}</strong>
          <span className="tabular-nums">{when(item.created)}</span>
        </div>
        <span className="exo-capture-actions-gap" />
        <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={() => setEditing(true)}><Pencil size={14} />Edit</button>
        <Menu label="Share" anchor="--exo-capture-share" offset={6} triggerClassName="exo-set-btn" trigger={<><Share size={14} />Share</>}
          items={share} onAction={id => { if (id === 'discord') setSending(true); else void act('copy') }} />
        <button type="button" className={`exo-set-btn exo-editor-icon exo-capture-fav-btn${item.favourite ? ' is-on' : ''}`} aria-pressed={item.favourite}
          aria-label={item.favourite ? 'Remove from favorites' : 'Favorite'} onClick={onFavourite}><Star on={item.favourite} /></button>
        <button type="button" className="exo-set-btn exo-editor-icon" aria-pressed={info} aria-label="Info" onClick={() => setInfo(on => !on)}><Info size={14} /></button>
        <ActionButton className="exo-set-btn exo-editor-icon is-danger" aria-label="Delete" disabled={busy} context="Captures.delete" fallback={ACT_FAILED.delete}
          why="none" onFailed={failedHere} onDone={onDeleted} onPress={remove}><Trash size={14} /></ActionButton>
        <Menu label="More" anchor="--exo-capture-more" offset={6} triggerClassName="exo-set-btn exo-editor-icon" trigger={<More size={16} />}
          items={more} onAction={id => { if (id === 'text') void copyText(); else void act(id as 'reveal' | 'shrink') }} />
      </div>
      <div className="exo-viewer-body">
        <div className="exo-capture-stage is-full">
          {item.kind === 'video' ? (
            <video
              ref={video}
              src={item.url}
              controls
              playsInline
              /* Plays on open, as the hover preview does; Reduce motion holds it still, as it does there. */
              autoPlay={!shouldReduceMotion()}
              onLoadedMetadata={event => {
                event.currentTarget.volume = playback.volume
                event.currentTarget.muted = playback.muted
                setShown({ width: event.currentTarget.videoWidth, height: event.currentTarget.videoHeight })
              }}
              onVolumeChange={event => { playback.volume = event.currentTarget.volume; playback.muted = event.currentTarget.muted }}
            />
          ) : <ZoomImage src={item.url} onSize={setShown} />}
        </div>
        {info ? <InfoPane item={item} size={shown} game={game} /> : null}
      </div>
      <NoteLine note={note} />
      {sending ? (
        // Inside Discord's scope, so the picker's words take Discord's colors as they do in the room.
        <div className="exo-dc exo-capture-send">
          <Suspense fallback={null}>
            <SendToDialog
              title="Send to"
              failed="Discord didn’t take it. Try again."
              send={async (ids, text) => {
                const result = await host.captureSend(item.id, ids, text, 0, 0)
                if (result.item) onAdded(result.item)
                return result
              }}
              onSent={names => setNote({ text: names.length ? `Sent to ${names.join(', ')}.` : 'Sent.' })}
              onClose={() => setSending(false)}
            />
          </Suspense>
        </div>
      ) : null}
    </div>
  )
}

/** Why a viewer action failed, when Exo Capture didn't say. */
const ACT_FAILED = {
  delete: 'Windows could not move it to the Recycle Bin. Try again.',
  reveal: 'Exo could not open its folder. Try again.',
  copy: 'Exo could not copy it. Try again.',
  shrink: 'Exo Capture could not make it smaller. Try again.',
} as const

/** Nothing saved yet: the three ways to capture, each one press away, with its keys. */
function CapturesEmpty({ onSettings }: { onSettings: () => void }) {
  const { state, settings } = useCapture()
  const [note, setNote] = useState<string | null>(null)
  const ways = [
    { action: 'saveReplay' as const, label: 'Save replay', keys: settings?.saveReplayKeys, Icon: History },
    { action: 'record' as const, label: 'Record', keys: settings?.recordKeys, Icon: Record },
    { action: 'snip' as const, label: 'Snip', keys: settings?.snipKeys, Icon: Scissors },
  ]
  const run = (action: 'saveReplay' | 'record' | 'snip') => {
    setNote(null)
    // Nothing is held to save while instant replay is off.
    if (action === 'saveReplay' && state && !state.buffering) {
      setNote(settings?.replayMode === 'off' ? 'Instant replay is off. Turn it on in Capture settings.'
        : settings?.replayMode === 'games' ? 'Instant replay runs only while a game is open.' : "Instant replay isn't running.")
      return
    }
    const failed = 'Exo Capture didn’t answer. Try again.'
    void host.captureAct(action).then(result => { if (!result.ok) setNote(errorText(result.message, failed)) })
      .catch((error: unknown) => {
        report(error, 'capture.act', { level: 'warn' })
        setNote(errorText(error, failed))
      })
  }
  // Without the add-on nothing can be captured: say so instead of buttons that do nothing.
  if (state && !state.present) {
    return (
      <div className="exo-captures-empty">
        <strong>Exo Capture isn't installed</strong>
      </div>
    )
  }
  return (
    <div className="exo-captures-empty">
      <strong>No captures yet</strong>
      {note ? <ErrorNote className="exo-capture-note is-bad" message={note} /> : null}
      {note && settings?.replayMode === 'off' ? <button type="button" className="exo-set-btn" onClick={onSettings}><Settings size={14} />Capture settings</button> : null}
      <div className="exo-captures-empty-ways">
        {ways.map(({ action, label, keys, Icon }) => (
          <button key={action} type="button" className="exo-captures-way" onClick={() => run(action)}>
            <Icon size={16} />
            {label}
            {keys ? <kbd className="exo-capture-keys">{keysLabel(keys)}</kbd> : null}
          </button>
        ))}
      </div>
    </div>
  )
}


/** The first `limit` captures of the days shown, in their days. */
function firstOf(days: readonly Day[], limit: number): Day[] {
  const out: Day[] = []
  let left = limit
  for (const day of days) {
    if (left <= 0) break
    out.push(day.items.length > left ? { ...day, items: day.items.slice(0, left) } : day)
    left -= day.items.length
  }
  return out
}

/** At the list's end: more cards once it comes within a screen of the scroller's view (a new one per page, so it asks again). */
function MoreCards({ root, onNear }: { root: RefObject<HTMLDivElement | null>; onNear: () => void }) {
  const mark = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const el = mark.current
    if (!el) return
    const watch = new IntersectionObserver(([entry]) => { if (entry.isIntersecting) onNear() }, { root: root.current, rootMargin: '0px 0px 100% 0px' })
    watch.observe(el)
    return () => watch.disconnect()
  }, [root, onNear])
  return <div ref={mark} aria-hidden="true" />
}


/** A reel being made: its bar and Cancel; or waiting for the game to end; or why none started (with Wait, when a game is why). */
function ReelLine() {
  const reel = useReel()
  if (reel.phase === 'working') {
    return (
      <div className="exo-capture-reel">
        <Progress value={reel.percent / 100} label="Making your reel" caption />
        <button type="button" className="exo-set-btn" onClick={cancelReel}>Cancel</button>
      </div>
    )
  }
  if (reel.phase === 'waiting') {
    return (
      <div className="exo-capture-reel">
        <p className="exo-capture-note" role="status">Waiting for your game to end</p>
        <button type="button" className="exo-set-btn" onClick={cancelReel}>Cancel</button>
      </div>
    )
  }
  if (!reel.message) return null
  return reel.canWait ? (
    <div className="exo-capture-reel">
      <p className="exo-capture-note" role="status">{reel.message}</p>
      <button type="button" className="exo-set-btn" onClick={() => void makeReel(reel.ids, true)}>Wait</button>
    </div>
  ) : <ErrorNote className="exo-capture-note is-bad exo-captures-note" message={reel.message} />
}

/** In place of the grid: its shine while the folder is first read, the ways to capture when it is empty, or that the chips leave none. */
function ListState({ items, shown, failed, onSettings }: {
  items: readonly CaptureItem[] | null
  /** How many captures the chips leave. */
  shown: number
  failed: boolean
  onSettings: () => void
}) {
  if (items === null) return failed ? null : (
    <Wait label="Reading the captures folder">
      <div className="exo-captures-grid">
        {times(12, () => <div className="exo-capture-card"><span className="exo-capture-thumb" skullet-block="" /></div>)}
      </div>
    </Wait>
  )
  if (items.length === 0) return <CapturesEmpty onSettings={onSettings} />
  return shown === 0 ? <p className="exo-hub-sub">Nothing here with those chips</p> : null
}

/** Left and Right in the viewer: the capture before or after, in the order the sheet lists them (or all of them, for one it doesn't list). */
function neighbour(listed: readonly CaptureItem[], all: readonly CaptureItem[] | null, id: string, by: -1 | 1): CaptureItem | undefined {
  const order = listed.some(item => item.id === id) ? listed : all ?? []
  const at = order.findIndex(item => item.id === id)
  return at < 0 ? undefined : order[at + by]
}


type Group = { key: string; title: string; gameId?: string | null }

/** Each game with captures, once, in the order the list first has it. */
function gamesOf(items: readonly CaptureItem[] | null): Group[] {
  const groups = new Map<string, Group>()
  for (const item of items ?? []) {
    const key = item.gameId ?? item.game
    if (!groups.has(key)) groups.set(key, { key, title: item.game, gameId: item.gameId })
  }
  return [...groups.values()]
}

/** The captures folder of the game a filter names (its id or a store variant's), lowercased; null for none. */
function folderOf(filter: string | null, games: readonly Game[]): string | null {
  const id = filter?.toLowerCase()
  const game = id ? games.find(g => g.id.toLowerCase() === id || g.variants?.some(v => v.id.toLowerCase() === id)) : undefined
  return game ? captureFolder(game.title).toLowerCase() : null
}


/** Clips, Screenshots and Favorites, then one chip per game: each narrows the others; pressed again, it lets go. */
function Chips({ games, filter, gameTitle, onChange, children }: {
  games: readonly Group[]
  filter: GalleryFilter
  gameTitle: (group: Group) => string
  onChange: (next: GalleryFilter) => void
  children?: ReactNode
}) {
  const kind = (value: 'video' | 'image') => onChange({ ...filter, kind: filter.kind === value ? 'all' : value })
  return (
    <div className="exo-shelf-chips exo-captures-chips" role="toolbar" aria-label="Show">
      <button type="button" className="exo-shelf-chip" aria-pressed={filter.kind === 'video'} onClick={() => kind('video')}>Clips</button>
      <button type="button" className="exo-shelf-chip" aria-pressed={filter.kind === 'image'} onClick={() => kind('image')}>Screenshots</button>
      <button type="button" className="exo-shelf-chip" aria-pressed={filter.favorites} onClick={() => onChange({ ...filter, favorites: !filter.favorites })}><Star on={filter.favorites} />Favorites</button>
      {games.length > 1 || filter.game ? <span className="exo-captures-chips-gap" aria-hidden="true" /> : null}
      {games.length > 1 || filter.game ? games.map(group => {
        const on = filter.game === group.key
        return <button key={group.key} type="button" className="exo-shelf-chip" aria-pressed={on} onClick={() => onChange({ ...filter, game: on ? null : group.key })}>{gameTitle(group)}</button>
      }) : null}
      {children}
    </div>
  )
}

/**
 * The scrubber on the grid's right edge: one stop a month (the year where one starts), newest at the top. A press, or
 * a drag down it, jumps to that month's newest day.
 */
function Scrubber({ stops, current, onJump }: { stops: ReturnType<typeof scrubStops>; current: string | null; onJump: (day: string) => void }) {
  const dragging = useRef(false)
  if (stops.length < 2) return null
  const jumpAt = (event: ReactPointerEvent<HTMLElement>) => {
    const row = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-scrub-day]')
    if (row?.dataset.scrubDay) onJump(row.dataset.scrubDay)
  }
  return (
    <nav className="exo-scrub" aria-label="Jump to a month"
      onPointerDown={event => { dragging.current = true; event.currentTarget.setPointerCapture(event.pointerId); jumpAt(event) }}
      onPointerMove={event => { if (dragging.current) jumpAt(event) }}
      onPointerUp={() => { dragging.current = false }}
      onLostPointerCapture={() => { dragging.current = false }}>
      {stops.map(stop => (
        <button key={stop.month} type="button" data-scrub-day={stop.day} className={stop.year ? 'is-year' : undefined}
          aria-current={current === stop.month || undefined} aria-label={monthTitle(stop.month)}
          onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onJump(stop.day) } }}>
          {stop.label}
        </button>
      ))}
    </nav>
  )
}

/** The folder could not be read: why, and Try again, not an empty sheet (over the captures last read, if any). */
function ReadFailed({ failed, onRetry }: { failed: boolean; onRetry: () => void }) {
  if (!failed) return null
  return (
    <>
      <ErrorNote className="exo-capture-note is-bad" message="Exo could not read the captures folder." />
      <button type="button" className="exo-set-btn" onClick={onRetry}>Try again</button>
    </>
  )
}


/** Select, then Make reel N, Delete N or Cancel. */
function SelectActions({ selection, deleting, reelClips, onSelect, onDelete, onReel }: {
  selection: ReadonlySet<string> | null
  deleting: boolean
  /** How many of the picked are clips (a snip cannot be in a reel). */
  reelClips: number
  onSelect: (next: ReadonlySet<string> | null) => void
  onDelete: () => void
  onReel: () => void
}) {
  const reel = useReel()
  if (!selection) return <button type="button" className="exo-set-btn" onClick={() => onSelect(new Set<string>())}>Select</button>
  return (
    <>
      <button type="button" className="exo-set-btn" disabled={reelClips < 2 || deleting || reel.phase !== 'idle'} onClick={onReel}>
        {reelClips > 1 ? `Make reel ${reelClips}` : 'Make reel'}
      </button>
      <button type="button" className="exo-set-btn is-danger" disabled={!selection.size || deleting} onClick={onDelete}>
        {deleting ? <Spinner label="Deleting" /> : null}{selection.size ? `Delete ${selection.size}` : 'Delete'}
      </button>
      <button type="button" className="exo-set-btn" disabled={deleting} onClick={() => onSelect(null)}>Cancel</button>
    </>
  )
}

/** The sheet closes, then Exo Capture's snip overlay freezes the screen as it is: the sheet is gone from it by then. */
function snipFromSheet(onFail: (message: string) => void) {
  closeCaptures()
  window.setTimeout(() => {
    void host.captureAct('snip')
      .then(result => { if (!result.ok) onFail(errorText(result.message, 'Exo Capture didn’t answer. Try again.')) })
      .catch((error: unknown) => { report(error, 'capture.act', { level: 'warn' }) })
  }, 420)
}

/**
 * The sheet's title line: Captures, bold, with the month the grid is on as a chip beside it (or Capture settings); then
 * the one primary action (Snip), its other actions (Select and the rest, given as children), Settings, Notes and Close.
 */
function Head({ setting, list, month, onSettings, onNotes, onSnip, children }: {
  setting: boolean
  /** The list is shown and nothing is being picked: Snip, Settings and Notes are offered. */
  list: boolean
  /** The month at the top of the grid, for the chip. */
  month: string | null
  onSettings: (open: boolean) => void
  onNotes: () => void
  onSnip: () => void
  children: ReactNode
}) {
  const { state, settings } = useCapture()
  const present = state?.present !== false
  return (
    <header className="exo-hub-head exo-captures-head">
      <div className="exo-captures-title min-w-0">
        <h2 id="exo-captures-title" className="exo-hub-title">{setting ? 'Capture settings' : 'Captures'}</h2>
        {!setting && month ? <span className="exo-captures-date tabular-nums">{monthTitle(month)}</span> : null}
      </div>
      <div className="exo-hub-head-actions">
        {setting ? <button type="button" className="exo-set-btn" onClick={() => onSettings(false)}><ChevronLeft size={14} />All captures</button> : children}
        {/* Without the add-on there is nothing to set or take. */}
        {setting || !list || !present ? null : (
          <button type="button" className="exo-set-btn" onClick={() => onSettings(true)}><Settings size={14} />Settings</button>
        )}
        {setting || !list ? null : <button type="button" className="exo-set-btn" onClick={onNotes}><Notes size={14} />Notes</button>}
        {setting || !list || !present ? null : (
          <button type="button" className="exo-set-btn is-primary" aria-keyshortcuts={settings?.snipKeys?.replaceAll('Ctrl', 'Control')} onClick={onSnip}>
            <Scissors size={14} />Snip
          </button>
        )}
        <button type="button" className="exo-game-close" aria-label="Close Captures" onClick={closeCaptures}><Close size={16} /></button>
      </div>
    </header>
  )
}

/**
 * What the grid shows for the chips on: the captures left, by day; the scrubber's months; On this day (only with no chip
 * on); each game once for its chip; and with a game's chip on, what its Make reel takes (its newest sitting, or the
 * starred clips of it).
 */
function useGallery(items: CaptureItem[] | null, filter: GalleryFilter, games: readonly Game[], now: Date) {
  // A game's chip (or a game page's filter) matches like the host's list: the game id in any case, or the folder named for its title.
  const folder = folderOf(filter.game, games)
  const inGame = (item: CaptureItem) => !filter.game || (item.gameId ?? item.game) === filter.game
    || item.gameId?.toLowerCase() === filter.game.toLowerCase() || (folder !== null && item.game.toLowerCase() === folder)
  const days = byDay(items ? filterCaptures(items, filter, inGame) : [], now)
  const plain = !filter.game && filter.kind === 'all' && !filter.favorites
  return {
    days,
    flat: days.flatMap(day => day.items),
    stops: scrubStops(days, now),
    resurfaced: plain && items ? onThisDay(items, now).slice(0, 6) : [],
    gameGroups: gamesOf(items),
    gameTitle: (group: Group) => games.find(g => g.id === group.gameId)?.title ?? group.title,
    reelClips: filter.game ? reelOfGame(items?.filter(inGame) ?? []) : [],
  }
}

/** With a game's chip on, its Make reel N (two clips or more, nothing being made). */
function ReelButton({ clips }: { clips: readonly CaptureItem[] }) {
  const reeling = useReel().phase !== 'idle'
  if (clips.length < 2) return null
  return (
    <button type="button" className="exo-set-btn is-compact exo-captures-reel-btn" disabled={reeling} onClick={() => void makeReel(clips.map(item => item.id))}>
      Make reel {clips.length}
    </button>
  )
}

/** On this day: captures from today's date in earlier years, over the grid, each with how long ago. */
function OnThisDay({ items, now, card }: { items: readonly CaptureItem[]; now: Date; card: (item: CaptureItem, badge?: string) => ReactNode }) {
  if (!items.length) return null
  return (
    <section className="exo-captures-day" aria-label="On this day">
      <h3>On this day</h3>
      <div className="exo-captures-grid">{items.map(item => card(item, yearsAgo(item.created, now)))}</div>
    </section>
  )
}

/** Years ago, as a badge says it. */
function yearsAgo(iso: string, now: Date): string {
  const years = now.getFullYear() - new Date(iso).getFullYear()
  return years === 1 ? '1 year ago' : `${years} years ago`
}

function CapturesSheet({ gameId, itemId, edit, settings, notes, games }: {
  gameId: string | null
  itemId: string | null
  edit: boolean
  /** Open on Capture settings (the Capture menu's). */
  settings: boolean
  /** Open on Notes (Capture's menu, Ctrl+K, a text file Windows opened with Exo). */
  notes: NotesRequest | null
  games: readonly Game[]
}) {
  // The captures last read show at once (stale while the folder is read again), so the sheet never opens empty; not when
  // it opens on one they don't have yet (a new snip, straight into its editor), which would show the list first.
  const [items, setItems] = useState<CaptureItem[] | null>(() => itemId && !lastList?.some(item => item.id === itemId) ? null : lastList)
  useEffect(() => remember(items), [items])
  const [limit, setLimit] = useState(PAGE)
  /** Capture settings in place of the captures, pushed in over the same body. */
  const [setting, setSetting] = useState(settings)
  /** Notes in the captures' place: the sheet's other half, pushed in over it. */
  const [notesShown, setNotesShown] = useState(notes !== null)
  const showNotes = (next: boolean) => void transition(() => setNotesShown(next), { kind: 'push', back: !next, morph: pane(sheetRef.current) })
    .then(() => { if (!sheetRef.current?.contains(document.activeElement)) sheetRef.current?.focus({ preventScroll: true }) })
  const body = useRef<HTMLDivElement>(null)
  const sheetRef = useRef<HTMLElement>(null)
  // The button pressed goes with the view it was in: focus stays in the sheet.
  const showSettings = (next: boolean) => void transition(() => { body.current?.scrollTo(0, 0); setSetting(next) }, { kind: 'push', back: !next, morph: pane(body.current) })
    .then(() => { if (!sheetRef.current?.contains(document.activeElement)) sheetRef.current?.focus({ preventScroll: true }) })
  const [filter, setFilter] = useState<GalleryFilter>({ game: gameId, kind: 'all', favorites: false })
  const [openId, setOpenId] = useState<string | null>(itemId)
  const [now] = useState(() => new Date())

  // A failed read is not an empty folder: it says so, with Try again.
  const [listFailed, setListFailed] = useState(false)
  const load = useCallback(() => {
    setListFailed(false)
    void host.captureList().then(result => {
      if (result.ok && result.items) setItems(result.items)
      else setListFailed(true)
    }).catch((error: unknown) => { report(error, 'capture.list', { level: 'warn' }); setListFailed(true) })
  }, [])
  useEffect(() => { load() }, [load])
  useEffect(() => onHostEvent('capture.saved', ({ item }) => {
    setItems(list => list ? [item, ...list.filter(other => other.id !== item.id)] : list)
  }), [])

  const { flat, days, stops, resurfaced, gameGroups, gameTitle, reelClips } = useGallery(items, filter, games, now)
  /** Anything saved: the chips, Select and the month's chip show. */
  const any = !!items?.length
  const open = openId ? items?.find(item => item.id === openId) ?? null : null
  // A new snip opens in the editor once; stepped away from or left, it opens as the others do.
  const [editId, setEditId] = useState(edit ? itemId : null)
  // A reel that finishes while the sheet shows opens like a clip you picked; what an earlier press said is forgotten.
  useEffect(() => {
    clearReelMessage()
    return onReelDone(item => { setEditId(null); setOpenId(item.id) })
  }, [])
  const stepTo = (by: -1 | 1) => {
    const next = open && neighbour(flat, items, open.id, by)
    if (!next) return
    setEditId(null)
    setOpenId(next.id)
  }
  const update = (next: CaptureItem) => setItems(list => list?.map(item => item.id === next.id ? next : item) ?? list)
  // The star turns at once; the host's answer settles it (and puts it back if the change did not save).
  const favourite = (item: CaptureItem) => {
    const failed = 'Exo Capture couldn’t change the favorite. Try again.'
    update({ ...item, favourite: !item.favourite })
    void host.captureItem(item.id, item.favourite ? 'unfavourite' : 'favourite')
      .then(result => {
        if (result.item) update(result.item)
        else if (!result.ok) { update(item); setNote(errorText(result.message, failed)) }
      }).catch((error: unknown) => { update(item); setNote(errorText(error, failed)) })
  }
  // Null while not selecting; a filter change starts the pick again (no hidden captures in a delete).
  const [selection, setSelection] = useState<ReadonlySet<string> | null>(null)
  const [deleting, setDeleting] = useState(false)
  /** What Make reel takes while selecting: the picked clips. */
  const picked = selection && items ? reelOfPicked(items, selection) : []
  const [note, setNote] = useState<string | null>(null)
  const pickFilter = (next: GalleryFilter) => { setFilter(next); setSelection(s => s && new Set<string>()); body.current?.scrollTo(0, 0) }
  const toggle = (id: string) => setSelection(s => {
    if (!s) return s
    const next = new Set(s)
    if (!next.delete(id)) next.add(id)
    return next
  })

  // The month chip and the scrubber follow the day at the top of the grid as it scrolls (one read a frame).
  const [topMonth, setTopMonth] = useState<string | null>(null)
  const frame = useRef(0)
  const readTop = useCallback(() => {
    frame.current = 0
    const scroller = body.current
    if (!scroller) return
    const top = scroller.getBoundingClientRect().top
    const day = [...scroller.querySelectorAll<HTMLElement>('[data-day-month]')].find(el => el.getBoundingClientRect().bottom > top + 8)
    setTopMonth(day?.dataset.dayMonth ?? null)
  }, [])
  useEffect(() => { readTop() }, [readTop, items, filter, open, setting])
  /** A scrubber stop: its day drawn first (past the page drawn so far), then at the top of the grid. */
  const jump = (dayKey: string) => {
    const at = flat.findIndex(item => days.find(day => day.key === dayKey)?.items[0]?.id === item.id)
    if (at >= 0) setLimit(was => Math.max(was, at + PAGE))
    requestAnimationFrame(() => body.current?.querySelector(`[data-day="${CSS.escape(dayKey)}"]`)?.scrollIntoView({ block: 'start' }))
  }

  /** To the Recycle Bin, one at a time like Delete on a capture; what Windows refused stays. */
  async function remove(ids: readonly string[]) {
    setNote(null)
    setDeleting(true)
    const gone = new Set<string>()
    let failed: string | null = null
    for (const id of ids) {
      try {
        const result = await host.captureItem(id, 'delete')
        if (result.ok) gone.add(id)
        else failed = errorText(result.message, ACT_FAILED.delete)
      } catch (error) { failed = errorText(error, ACT_FAILED.delete) }
    }
    setItems(list => list?.filter(item => !gone.has(item.id)) ?? list)
    setDeleting(false)
    if (failed) setNote(ids.length > 1 ? `${ids.length - gone.size} not deleted: ${failed}` : failed)
    else setSelection(null)
  }

  /** Back to the list from a clip: the picture flies back down into its thumbnail, drawn by then even if the arrows stepped past the tiles drawn. */
  const backToList = () => {
    if (!open) return
    const id = open.id
    const at = flat.findIndex(item => item.id === id)
    setEditId(null)
    void transition(() => { setLimit(was => Math.max(was, at + 1)); setOpenId(null) }, {
      kind: 'morph',
      back: true,
      morph: { name: 'exo-capture', from: visible('.exo-capture-stage > :is(img, video)'), to: () => visible(`.exo-capture-thumb[data-capture-id="${CSS.escape(id)}"]`) },
    })
  }
  const openItem = (item: CaptureItem) => {
    if (selection) toggle(item.id)
    // The thumbnail flies up into the viewer.
    else void transition(() => setOpenId(item.id), { kind: 'morph', morph: { name: 'exo-capture', from: pressedArt('.exo-capture-thumb'), to: () => visible('.exo-capture-stage > :is(img, video)') } })
  }
  const card = (item: CaptureItem, badge?: string) => (
    <CaptureCard key={item.id} item={item} badge={badge} selected={selection ? selection.has(item.id) : null}
      onOpen={() => openItem(item)} onFavourite={() => favourite(item)} onDelete={() => void remove([item.id])} />
  )

  return (
    <section ref={sheetRef} className="exo-hub exo-captures" aria-labelledby="exo-captures-title" tabIndex={-1} data-autofocus>
      {notesShown ? <NotesView request={notes} games={games} onCaptures={() => showNotes(false)} /> : open && !setting ? (
        <Viewer
          key={open.id}
          item={open}
          game={gameTitle({ key: open.gameId ?? open.game, title: open.game, gameId: open.gameId })}
          edit={open.id === editId}
          onStep={stepTo}
          onBack={backToList}
          onFavourite={() => favourite(open)}
          onChanged={update}
          onDeleted={() => { setItems(list => list?.filter(item => item.id !== open.id) ?? list); setOpenId(null) }}
          onAdded={item => setItems(list => list ? [item, ...list.filter(other => other.id !== item.id)] : [item])}
        />
      ) : (
        <>
          <Head setting={setting} list={!selection} month={any ? topMonth : null} onSettings={showSettings} onNotes={() => showNotes(true)}
            onSnip={() => snipFromSheet(message => setNote(message))}>
            {!any ? null : (
              <SelectActions selection={selection} deleting={deleting} reelClips={picked.length}
                onSelect={next => { setSelection(next); setNote(null) }} onDelete={() => void remove([...selection ?? []])}
                onReel={() => { void makeReel(picked.map(item => item.id)); setSelection(null) }} />
            )}
          </Head>
          {setting ? (
            <div ref={body} className="exo-captures-body">
              <CaptureSettingsView />
            </div>
          ) : (
            <div className="exo-captures-frame">
              <div ref={body} className="exo-captures-body" onScroll={() => { if (!frame.current) frame.current = requestAnimationFrame(readTop) }}>
                {any ? (
                  <Chips games={gameGroups} filter={filter} gameTitle={gameTitle} onChange={pickFilter}>
                    <ReelButton clips={selection ? [] : reelClips} />
                  </Chips>
                ) : null}
                {note ? <ErrorNote className="exo-capture-note is-bad exo-captures-note" message={note} /> : null}
                <ReadFailed failed={listFailed} onRetry={load} />
                <ReelLine />
                <ListState items={items} shown={flat.length} failed={listFailed} onSettings={() => showSettings(true)} />
                <OnThisDay items={resurfaced} now={now} card={card} />
                {firstOf(days, limit).map(day => (
                  <section key={day.key} className="exo-captures-day" data-day={day.key} data-day-month={day.month} aria-label={day.title}>
                    <h3>{day.title}</h3>
                    <div className="exo-captures-grid">{day.items.map(item => card(item))}</div>
                  </section>
                ))}
                {flat.length > limit ? <MoreCards key={limit} root={body} onNear={() => setLimit(was => was + PAGE)} /> : null}
              </div>
              <Scrubber stops={stops} current={topMonth} onJump={jump} />
            </div>
          )}
        </>
      )}
    </section>
  )
}
