import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { createPortal } from 'react-dom'
import { AppWindow, ChangeShare, ChevronLeft, Close, Controller, Monitor, Stop } from '../../brand/icons'
import { useGlide } from '../../lib/glide'
import { goLivePreview, goLiveSources, startGoLive, stopGoLive, useGoLive, useGoLiveValue, type GoLiveStats } from '../../lib/goLive'
import { host, type GoLiveSource } from '../../lib/host'
import { useVideoFeed } from '../../lib/discordVideo'
import { usePageActive, usePageVisible } from '../../lib/pageActivity'
import { onUiPrefs, setUiPref, uiPref } from '../../lib/uiPrefs'
import { Switch } from '../Switch'
import { Icons } from './icons'
import { Popover } from '../ui/Dialog'
import { useDismiss } from '../../lib/dismiss'
import { Spinner } from '../ui/Spinner'
import { Wait, sample, times } from '../ui/Wait'
import { ErrorNote } from '../ui/ErrorNote'
import { Avatar } from './presence'
import type { DiscordVoiceMember } from '../../lib/discord'

type Quality = { height: number; fps: number; sound: boolean }
// note: what the choice really sends when it isn't what it says (no notes explaining choices: Exo has none).
type Option = { key: number; label: string; note?: string }

const RESOLUTIONS: Option[] = [{ key: 720, label: '720p' }, { key: 1080, label: '1080p' }, { key: 1440, label: '1440p' }, { key: 0, label: 'Source' }]
// A stream is never larger than what it shows: a size above the screen or window says what it really sends.
function fitTo(source: GoLiveSource): Option[] {
  const tall = source.height ?? 0
  if (tall <= 0) return RESOLUTIONS
  const what = source.kind === 'screen' ? 'screen' : 'window'
  return RESOLUTIONS.map(r => r.key > tall ? { ...r, note: `Your ${what} is ${tall}p, so this streams at ${tall}p.` } : r)
}

const RATES: Option[] = [{ key: 15, label: '15 fps' }, { key: 30, label: '30 fps' }, { key: 60, label: '60 fps' }, { key: 120, label: '120 fps' }]
// No rate above what the source's screen draws (a 60 Hz screen has no 120 fps to send); unknown: all of them.
const ratesFor = (source: GoLiveSource): Option[] => (source.hz && source.hz >= 15 ? RATES.filter(r => r.key <= source.hz! + 1) : RATES)

// The last quality chosen, for next time (kept in Exo's settings).
function savedQuality(): Quality {
  const saved = (uiPref('goLiveQuality') ?? null) as Partial<Quality> | null
  return {
    height: RESOLUTIONS.some(r => r.key === saved?.height) ? saved!.height! : 1080,
    fps: RATES.some(r => r.key === saved?.fps) ? saved!.fps! : 30,
    sound: saved?.sound !== false,
  }
}
function saveQuality(quality: Quality) {
  setUiPref('goLiveQuality', quality)
}

/** Polls a picture (a source's, or the live stream's with no source) while mounted and Exo is in use; null until the first. */
function usePicture(source: string | null, width: number, every: number, first: string | null = null) {
  const [picture, setPicture] = useState<string | null>(first)
  const active = usePageVisible()
  useEffect(() => {
    if (!active) return
    let alive = true
    let busy = false
    const tick = () => {
      if (busy) return
      busy = true
      void goLivePreview(source, width).then(next => {
        busy = false
        if (alive && next) setPicture(next)
      })
    }
    tick()
    const timer = window.setInterval(tick, every)
    return () => { alive = false; window.clearInterval(timer) }
  }, [source, width, every, active])
  return picture
}

function useEscape(onEscape: () => void) {
  useEffect(() => {
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); onEscape() } }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [onEscape])
}

// The rate shown is the stream's ceiling: a still picture sends almost nothing, which is no measure of its quality.
// An app the stream's sound leaves out because it plays the call or your mic is said after it.
const formatStats = (stats: GoLiveStats) =>
  `${stats.width}×${stats.height} · ${stats.fps} fps · ${stats.codec ? `${stats.codec} · ` : ''}${stats.max ? `up to ${(stats.max / 1_000_000).toFixed(1)}` : (stats.bitrate / 1_000_000).toFixed(1)} Mbps${stats.sound ? ` · ${stats.sound}` : ''}`

// Whether your own stream shows as a tile in the call (Discord's Hide Preview), kept in Exo's settings.
const previewListeners = new Set<() => void>()
function previewShown() {
  return uiPref('goLivePreview') !== false
}
function setPreviewShown(shown: boolean) {
  setUiPref('goLivePreview', shown)
  previewListeners.forEach(listener => listener())
}
onUiPrefs(() => previewListeners.forEach(listener => listener()))
/** Your stream's tile is in the call: live, and its preview not hidden. */
export function useStreamTile() {
  const live = useGoLiveValue(state => state.state !== 'off')
  const shown = useSyncExternalStore(listener => { previewListeners.add(listener); return () => { previewListeners.delete(listener) } }, previewShown)
  return live && shown
}

/**
 * Share Your Screen, as Discord's Go Live: Exo's own picker (Applications and Screens, each a picture
 * with its title and app icon, the running game first), then the stream's quality with a live
 * preview, then Go Live. While live your stream is a tile among the people in the call (never over
 * them), and this button's menu shows or hides that preview or stops the stream.
 */
export function ShareScreen() {
  const live = useGoLive()
  // The dialog opens in the Discord room itself: the call's bar is moved into place, which would hold it.
  const [picking, setPicking] = useState<HTMLElement | null>(null)
  const [menu, setMenu] = useState(false)
  const shown = useStreamTile()
  const box = useRef<HTMLSpanElement>(null)
  const on = live.state !== 'off'
  useDismiss(box, () => setMenu(false), { on: menu })
  useEscape(() => setMenu(false))
  return (
    <span ref={box} className="exo-dc-share">
      <button type="button" className={`exo-dc-voice-btn${on ? ' is-live' : ''}`} aria-label={on ? 'Your stream' : 'Share your screen'}
        aria-expanded={on ? menu : picking !== null} aria-haspopup={on ? 'menu' : 'dialog'}
        onClick={event => on ? setMenu(open => !open) : setPicking(event.currentTarget.closest<HTMLElement>('.exo-dc') ?? document.body)}>
        <Icons.Streaming size={20} /><span className="exo-dc-voice-word">{on ? 'Live' : 'Share'}</span>
      </button>
      {on && menu ? (
        <div className="exo-dc-menu exo-dc-golive-menu" role="menu">
          <span className="exo-dc-golive-menu-head">{live.title ?? 'Your stream'}
            <small>{live.stats ? formatStats(live.stats) : <><Spinner label="Connecting your stream" /> Connecting</>}</small>
          </span>
          <button type="button" role="menuitem" onClick={() => { setMenu(false); setPreviewShown(!shown) }}>{shown ? 'Hide preview' : 'Show preview'}</button>
          <button type="button" role="menuitem" onClick={() => { setMenu(false); setPicking(box.current?.closest<HTMLElement>('.exo-dc') ?? document.body) }}>Change what you share</button>
          <button type="button" role="menuitem" className="is-danger" onClick={() => { setMenu(false); void stopGoLive() }}>Stop streaming</button>
        </div>
      ) : null}
      {picking ? createPortal(<GoLiveDialog onClose={() => setPicking(null)} onLive={() => { setPicking(null); setPreviewShown(true) }} />, picking) : null}
    </span>
  )
}

/**
 * The voice bar's stream controls, beside hang up: share your screen, and while live, change what it shows (going live
 * with another source replaces the stream) or stop it.
 */
export function PanelStream() {
  const on = useGoLiveValue(state => state.state !== 'off')
  const [picking, setPicking] = useState<HTMLElement | null>(null)
  return (
    <>
      <button type="button" className={`exo-dc-icon-btn${on ? ' is-on' : ''}`} aria-label={on ? 'Change what you share' : 'Share your screen'}
        aria-haspopup="dialog" aria-expanded={picking !== null}
        onClick={event => setPicking(event.currentTarget.closest<HTMLElement>('.exo-dc') ?? document.body)}>
        {on ? <ChangeShare size={18} /> : <Icons.Streaming size={18} />}
      </button>
      {on ? (
        <button type="button" className="exo-dc-icon-btn is-danger" aria-label="Stop streaming" onClick={() => void stopGoLive()}>
          <Stop size={18} />
        </button>
      ) : null}
      {picking ? createPortal(<GoLiveDialog onClose={() => setPicking(null)} onLive={() => { setPicking(null); setPreviewShown(true) }} />, picking) : null}
    </>
  )
}

/**
 * Your stream as a tile in the call, as Discord shows it: the very frames viewers get, at the stream's
 * own rate (a still picture stands in until the first one decodes), its real numbers, and the pictures of
 * whoever in the call is watching it, each gone when they stop; Hide Preview and Stop Streaming on it.
 */
export function MyStreamTile({ meId, members }: { meId: string | null; members: DiscordVoiceMember[] }) {
  const live = useGoLive()
  const viewers = live.viewers.flatMap(id => members.filter(member => member.id === id))
  const feed = useVideoFeed('self', meId)
  const picture = usePicture(null, 960, feed.live ? 60_000 : 500)
  // Your stream's own frames come while Exo is on screen to see them, focused or not (Exo beside the game on another
  // screen shows it live); minimized or hidden, none.
  const active = usePageVisible()
  useEffect(() => {
    if (!active) return
    void host.discordGoLive('self', { on: true }).catch(() => { })
    return () => { void host.discordGoLive('self', { on: false }).catch(() => { }) }
  }, [active])
  return (
    <article className={`exo-dc-voice-tile has-video exo-dc-my-stream${viewers.length ? ' has-viewers' : ''}`}>
      <video ref={feed.ref} className="exo-dc-voice-video" hidden={!feed.live} aria-label="What your stream shows" muted playsInline disablePictureInPicture />
      {!feed.live && picture ? <img className="exo-dc-voice-video" src={picture} alt="What your stream shows" draggable={false} /> : null}
      <span className="exo-dc-my-stream-actions">
        <button type="button" className="exo-dc-tab" onClick={() => setPreviewShown(false)}>Hide preview</button>
        <button type="button" className="exo-dc-tab exo-dc-golive-stop" onClick={() => void stopGoLive()}>Stop streaming</button>
      </span>
      <span className="exo-dc-voice-tile-name"><span className="exo-dc-voice-tile-label">{live.title ?? 'Your stream'}{live.stats ? <small> · {formatStats(live.stats)}</small> : null}</span></span>
      <StreamViewers viewers={viewers} />
    </article>
  )
}

/** Who is watching, in the tile's corner: up to three pictures, then how many more; nothing while no one is. */
function StreamViewers({ viewers }: { viewers: DiscordVoiceMember[] }) {
  if (!viewers.length) return null
  const shown = viewers.slice(0, 3)
  return (
    <span className="exo-dc-voice-tile-name exo-dc-stream-viewers" role="img" aria-label={`Watching: ${viewers.map(viewer => viewer.name).join(', ')}`}>
      {shown.map(viewer => <Avatar key={viewer.id} src={viewer.avatar} name={viewer.name} status={null} size={20} />)}
      {viewers.length > shown.length ? <span>+{viewers.length - shown.length}</span> : null}
    </span>
  )
}

function GoLiveDialog({ onClose, onLive }: { onClose: () => void; onLive: () => void }) {
  const [chosen, setChosen] = useState<GoLiveSource | null>(null)
  return (
    <Popover portal={false} className={`exo-dc-modal exo-dc-golive${chosen ? '' : ' is-picking'}`} label="Share your screen" onClick={event => event.stopPropagation()} onClose={onClose}>
      {chosen
        ? <GoLiveSettings source={chosen} onBack={() => setChosen(null)} onClose={onClose} onLive={onLive} />
        : <SourcePicker onPick={setChosen} onClose={onClose} />}
    </Popover>
  )
}

/** Applications and Screens, refreshed every four seconds so pictures stay current and new windows appear. */
function SourcePicker({ onPick, onClose }: { onPick: (source: GoLiveSource) => void; onClose: () => void }) {
  const [sources, setSources] = useState<GoLiveSource[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<'window' | 'screen'>('window')
  const tabs = useGlide<HTMLSpanElement>()
  const active = usePageActive()
  useEffect(() => {
    if (!active) return
    let alive = true
    const load = () => void goLiveSources().then(result => {
      if (!alive) return
      setSources(result.sources)
      setError(result.message)
    })
    load()
    const timer = window.setInterval(load, 4000)
    return () => { alive = false; window.clearInterval(timer) }
  }, [active])
  const game = sources?.find(source => source.game)
  const shown = (sources ?? []).filter(source => source.kind === tab && source !== game)
  return (
    <>
      <header className="exo-dc-golive-head">
        <span ref={tabs} className="exo-dc-tabs exo-glide" role="tablist" aria-label="What to share">
          <button type="button" role="tab" className="exo-dc-tab" aria-selected={tab === 'window'} onClick={() => setTab('window')}>Applications</button>
          <button type="button" role="tab" className="exo-dc-tab" aria-selected={tab === 'screen'} onClick={() => setTab('screen')}>Screens</button>
        </span>
        <button type="button" className="exo-dc-icon-btn" aria-label="Close" onClick={onClose}><Close size={18} /></button>
      </header>
      {game && tab === 'window' ? (
        <button type="button" className="exo-dc-golive-game" onClick={() => onPick(game)}>
          <span className="exo-dc-golive-thumb">{game.thumb ? <img src={game.thumb} alt="" draggable={false} decoding="async" /> : <SourceMark source={game} size={28} />}</span>
          <span className="exo-dc-golive-game-text">
            <small><Controller size={14} /> Game</small>
            <strong>{game.game}</strong>
          </span>
          <span className="exo-dc-tab is-add">Stream</span>
        </button>
      ) : null}
      {sources === null ? <Wait label="Finding your windows"><div className="exo-dc-golive-grid">{times(6, i => <span className="exo-dc-golive-tile"><span className="exo-dc-golive-thumb" skullet-block="" /><span className="exo-dc-golive-name"><span>{sample(i)}</span></span></span>)}</div></Wait>
        : shown.length === 0 ? <p className="exo-dc-note">{error ?? (tab === 'window' ? (game ? 'No other windows to share.' : 'No windows to share.') : 'No screens found.')}</p>
          : (
            <div className="exo-dc-golive-grid exo-motion-switch" key={tab}>
              {shown.map(source => (
                <button key={source.id} type="button" className="exo-dc-golive-tile" aria-label={`Share ${source.title}`} onClick={() => onPick(source)}>
                  <span className="exo-dc-golive-thumb">{source.thumb ? <img src={source.thumb} alt="" draggable={false} decoding="async" /> : <SourceMark source={source} size={28} />}</span>
                  <span className="exo-dc-golive-name"><SourceMark source={source} size={16} /><span>{source.title}</span></span>
                </button>
              ))}
            </div>
          )}
    </>
  )
}

function SourceMark({ source, size }: { source: GoLiveSource; size: number }) {
  if (source.icon) return <img className="exo-dc-golive-icon" src={source.icon} width={size} height={size} alt="" draggable={false} />
  return source.kind === 'screen' ? <Monitor size={size} /> : <AppWindow size={size} />
}

/** The chosen source's live preview, resolution, frame rate and sound, then Go Live. */
function GoLiveSettings({ source, onBack, onClose, onLive }: { source: GoLiveSource; onBack: () => void; onClose: () => void; onLive: () => void }) {
  const [quality, setQuality] = useState(savedQuality)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const preview = usePicture(source.id, 960, 250, source.thumb ?? null)
  const go = async () => {
    setStarting(true)
    setError(null)
    saveQuality(quality)
    // What is sent is what the picker shows: a saved 120 fps on a 60 Hz screen streams at 60.
    const why = await startGoLive({ source: source.id, ...quality, fps: Math.min(quality.fps, ratesFor(source).at(-1)!.key) })
    setStarting(false)
    if (why) setError(why)
    else onLive()
  }
  const sound = source.kind === 'window'
    ? `Sends ${source.app || 'this app'}'s sound only.`
    : 'Sends everything your PC plays, except Exo and the call.'
  return (
    <>
      <header className="exo-dc-golive-head">
        <button type="button" className="exo-dc-icon-btn" aria-label="Pick something else" onClick={onBack}><ChevronLeft size={18} /></button>
        <strong className="exo-dc-golive-title"><SourceMark source={source} size={18} /><span>{source.game ?? source.title}</span></strong>
        <button type="button" className="exo-dc-icon-btn" aria-label="Close" onClick={onClose}><Close size={18} /></button>
      </header>
      <div className="exo-dc-golive-body">
        <div className="exo-dc-golive-preview">
          {preview ? <Still src={preview} alt={`Preview of ${source.title}`} /> : <SourceMark source={source} size={40} />}
        </div>
        <div className="exo-dc-golive-options">
          <Choice label="Resolution" options={fitTo(source)} value={quality.height} onChange={height => setQuality(q => ({ ...q, height }))} />
          <Choice label="Frame rate" options={ratesFor(source)} value={Math.min(quality.fps, ratesFor(source).at(-1)!.key)} onChange={fps => setQuality(q => ({ ...q, fps }))} />
          <div className="exo-dc-golive-choice is-row">
            <span>
              <strong id="exo-dc-golive-sound">Stream sound</strong>
              <small>{sound}</small>
            </span>
            <Switch checked={quality.sound} labelledBy="exo-dc-golive-sound" onChange={on => setQuality(q => ({ ...q, sound: on }))} />
          </div>
          {error ? <ErrorNote className="exo-dc-golive-error" message={error} /> : null}
          <button type="button" className="exo-dc-tab is-add exo-dc-golive-go" disabled={starting} onClick={() => void go()}>
            {starting ? <Spinner label="Starting your stream" /> : null}Go Live
          </button>
        </div>
      </div>
    </>
  )
}

/** Draws the next frame only after it has decoded, so a new picture never blanks the one on screen. */
function Still({ src, alt }: { src: string; alt: string }) {
  const ref = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const canvas = ref.current
    if (!canvas) return
    let dead = false
    const img = new Image()
    img.decoding = 'async'
    img.onload = () => {
      if (dead) return
      const w = img.naturalWidth || canvas.width || 1
      const h = img.naturalHeight || canvas.height || 1
      // Assigning width clears the bitmap. Do it only when the size changed, then draw before the browser paints.
      if (canvas.width !== w) canvas.width = w
      if (canvas.height !== h) canvas.height = h
      canvas.getContext('2d')?.drawImage(img, 0, 0, canvas.width, canvas.height)
    }
    img.src = src
    return () => { dead = true }
  }, [src])
  return <canvas ref={ref} role="img" aria-label={alt} />
}

function Choice({ label, options, value, onChange }: { label: string; options: Option[]; value: number; onChange: (key: number) => void }) {
  const group = useGlide<HTMLSpanElement>()
  const current = options.find(option => option.key === value) ?? options[0]
  return (
    <div className="exo-dc-golive-choice">
      <strong>{label}</strong>
      <span ref={group} className="exo-dc-tabs exo-glide exo-dc-golive-seg" role="radiogroup" aria-label={label}>
        {options.map(option => (
          <button key={option.key} type="button" role="radio" className="exo-dc-tab" aria-checked={option.key === value} onClick={() => onChange(option.key)}>{option.label}</button>
        ))}
      </span>
      {options.some(option => option.note) ? <small>{current.note ?? '\u00a0'}</small> : null}
    </div>
  )
}
