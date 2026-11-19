import { useCallback, useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { performanceMode, type DiscordAttachment } from '../../lib/discord'
import { mediaFilter, useAccountSetting, useAccountSettings, type MediaFilter } from '../../lib/discordPrefs'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { usePageActive } from '../../lib/pageActivity'
import { VoiceMessage } from './VoiceMessage'
import { showImage, type ViewerItem } from './extras'
import { Icons } from './icons'
import { open, spoilersShown } from './markdown'
import { Wait, sample, times } from '../ui/Wait'
import { Slider } from '../ui/Slider'
import { Play, Pause } from '../../brand/icons'

const IMAGE = /\.(png|jpe?g|gif|webp|avif)$/i
const VIDEO = /\.(mp4|webm|mov)$/i
// The kinds of text Discord previews in the chat, by the file's last extension (its own list, the common part).
const TEXT = new Set(('txt text md markdown csv json xml html xhtml svg css scss less js jsx mjs cjs ts tsx py rb rs go java kt kts c h cc cpp cxx hpp '
  + 'cs fs vb sh bash zsh ps1 bat cmd yml yaml toml ini cfg conf properties lua php pl pm sql swift dart diff patch srt vtt svelte gradle '
  + 'groovy dockerfile makefile mk lock tf hcl r scala ex exs erl hs ml nim clj coffee tex proto graphql gql').split(' '))

/** A picture shown at most maxW by maxH (520 by 420 in the chat), keeping its shape. */
export function fit(width?: number | null, height?: number | null, maxW = 520, maxH = 420) {
  if (!width || !height) return { width: undefined, height: undefined }
  const scale = Math.min(1, maxW / width, maxH / height)
  return { width: Math.round(width * scale), height: Math.round(height * scale) }
}

// The largest side Discord's media proxy is asked for (Discord's own cap).
const PROXY_MAX = 4096

/**
 * A picture from Discord's media proxy at the size it is shown, at the screen's pixel density, as Discord's client
 * asks for it (width, height and keep_aspect_ratio, the proxy scaling it down sharply rather than the page): WebP
 * unless it moves, lossless for a PNG. A known box is the picture's own shown size; without one (a mosaic cell) it
 * covers Discord's 550 by 350. The original (often 4K or larger, many megabytes) would otherwise paint top-down for
 * seconds, and one about twice the box was squeezed by the page and looked soft.
 */
export function sized(src: string, width?: number | null, height?: number | null, box?: { width: number; height: number }, animated = false, still = false) {
  let url: URL
  try { url = new URL(src) } catch { return src }
  if (!/(^|\.)discordapp\.net$/.test(url.hostname)) return src
  // still: a GIF or animated picture as its first frame (Discord's media proxy sends a WebP still unless asked to animate).
  if (still) { url.searchParams.set('format', 'webp'); url.searchParams.delete('animated') }
  if (!width || !height) return still ? url.toString() : src
  const shown = box ? Math.min(box.width / width, box.height / height) : Math.max(550 / width, 350 / height)
  const scale = Math.min(Math.min(shown, 1) * (window.devicePixelRatio || 1), PROXY_MAX / width, PROXY_MAX / height)
  if (!still && !animated && !/\.gif$/i.test(url.pathname)) {
    url.searchParams.set('format', 'webp')
    if (/\.(png|avif)$/i.test(url.pathname)) url.searchParams.set('quality', 'lossless')
  }
  url.searchParams.set('width', String(Math.max(1, Math.ceil(width * scale))))
  url.searchParams.set('height', String(Math.max(1, Math.ceil(height * scale))))
  url.searchParams.set('keep_aspect_ratio', 'true')
  return url.toString()
}

/**
 * A picture that may move (a GIF, an animated WebP or AVIF): it plays by itself when Play GIFs when Discord is focused
 * is on, Exo is active and neither Reduced Motion nor Performance mode is on; otherwise it holds its first frame until
 * pointed at, as Discord's GIFs do.
 */
/**
 * A picture's or video's error, tried again before it counts: 1.5 s later, then 6 s later, and afresh each time the page
 * comes back into view. Exo's WebView drops a playing GIF's decoder when it is minimized and low on memory, which reads as
 * an error: taken at once, a GIF that had played turned into its "would not load" line for good. `attempt` is the element's
 * key, so each try loads it anew.
 */
export function useMediaRetry(onFail?: () => void) {
  const active = usePageActive()
  const [attempt, setAttempt] = useState(0)
  const tries = useRef(0)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  useEffect(() => { if (active) tries.current = 0 }, [active])
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current) }, [])
  const onError = useCallback(() => {
    if (tries.current >= 2) { onFail?.(); return }
    const wait = tries.current === 0 ? 1500 : 6000
    tries.current += 1
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(() => setAttempt(n => n + 1), wait)
  }, [onFail])
  return { attempt, onError }
}

/** A failed picture gets another go when the page comes back into view (it may have failed only while Exo was away). */
export function useFailedUntilBack<T>(empty: T): [T, (value: T) => void] {
  const active = usePageActive()
  const [failed, setFailed] = useState<T>(empty)
  useEffect(() => { if (active) setFailed(empty) }, [active, empty])
  return [failed, setFailed]
}

export function MovingImage({ src, width, height, moving, alt, link, box, onFail }: {
  src: string; width?: number | null; height?: number | null; moving: boolean; alt: string; link?: string | null; box: { width?: number; height?: number }
  /** It would not load (only then: a picture still loading is not a failure). */
  onFail?: () => void
}) {
  const gifAutoPlay = useAccountSetting('gifAutoPlay')
  const active = usePageActive()
  const [hover, setHover] = useState(false)
  const play = moving && (hover || (gifAutoPlay && active && !performanceMode() && !shouldReduceMotion()))
  const { attempt, onError } = useMediaRetry(onFail)
  return (
    <img key={attempt} src={sized(src, width, height, box.width && box.height ? { width: box.width, height: box.height } : undefined, play, moving && !play)} alt={alt} loading="lazy" decoding="async" draggable={false} data-dc-link={link ?? undefined} {...box}
      onPointerEnter={moving ? () => setHover(true) : undefined} onPointerLeave={moving ? () => setHover(false) : undefined} onError={onError} />
  )
}

/**
 * A picture, GIF or video that would not load: never an empty box where it was, but one line in the file card's family,
 * its glyph and its name (or "GIF"), that opens the original.
 */
export function MediaFailed({ name, kind, url }: { name: string; kind: 'picture' | 'video'; url: string }) {
  const Glyph = kind === 'picture' ? Icons.Picture : Icons.Movie
  return (
    <button type="button" className="exo-dc-media-failed" aria-label={`Open ${name}`} onClick={() => open(url)}>
      <Glyph size={18} />
      <span>{name}</span>
    </button>
  )
}

export function size(bytes: number) {
  return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`
}

// Discord's sensitive media filters for this attachment. A DM takes the stricter of the friend and other-DM choices,
// since the room doesn't know which it is.
function sensitive(flags: number, guildId: string | null, settings: ReturnType<typeof useAccountSettings>): MediaFilter {
  if (guildId) return mediaFilter(flags, 'guild', settings)
  const friend = mediaFilter(flags, 'friendDm', settings), other = mediaFilter(flags, 'dm', settings)
  return friend === 'block' || other === 'block' ? 'block' : friend === 'blur' || other === 'blur' ? 'blur' : 'show'
}

/** A picture or video hidden behind Discord's SPOILER (or its sensitive content blur) until clicked. */
function Hidden({ label, children }: { label: string; children: React.ReactNode }) {
  const [shown, setShown] = useState(false)
  if (shown) return <>{children}</>
  return (
    <div className="exo-dc-hidden-media">
      <div className="exo-dc-hidden-blur" aria-hidden="true" inert>{children}</div>
      <button type="button" className="exo-dc-hidden-label" aria-label={`${label}, show`} onClick={() => setShown(true)}>{label}</button>
    </div>
  )
}

function mediaOf(file: DiscordAttachment, inline: boolean): 'image' | 'video' | null {
  const type = file.content_type ?? ''
  if (!inline) return null
  return type.startsWith('image/') || IMAGE.test(file.filename) ? 'image' : type.startsWith('video/') || VIDEO.test(file.filename) ? 'video' : null
}

const clock = (seconds: number) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`

/**
 * A video file: its first frame with one round play button and its length on a dark plate; once it plays, its
 * controls are one bar on a dark plate at the foot (play, time, a scrubber, volume, full screen) shown on hover.
 */
function VideoFile({ src, box, duration, onFail }: { src: string; box: { width?: number; height?: number }; duration?: number; onFail: () => void }) {
  const video = useRef<HTMLVideoElement>(null)
  const [playing, setPlaying] = useState(false)
  const [started, setStarted] = useState(false)
  const [at, setAt] = useState(0)
  const [length, setLength] = useState(duration ?? 0)
  const [muted, setMuted] = useState(false)
  const { attempt, onError } = useMediaRetry(onFail)
  const toggle = () => { const element = video.current; if (!element) return; if (element.paused) void element.play().catch(() => { }); else element.pause() }
  return (
    <div className={`exo-dc-media exo-dc-video${started ? ' is-started' : ''}${playing ? ' is-playing' : ''}`}>
      <video key={attempt} ref={video} src={src} muted={muted} preload="metadata" playsInline {...box} onClick={toggle}
        onLoadedMetadata={event => setLength(event.currentTarget.duration || 0)} onTimeUpdate={event => setAt(event.currentTarget.currentTime)}
        onPlay={() => { setPlaying(true); setStarted(true) }} onPause={() => setPlaying(false)} onEnded={() => { setPlaying(false); setStarted(false) }}
        onVolumeChange={event => setMuted(event.currentTarget.muted)} onError={onError} />
      {started ? null : (
        <>
          <button type="button" className="exo-dc-video-play" aria-label="Play video" onClick={toggle}><Play size={22} /></button>
          {length ? <span className="exo-dc-video-length">{clock(length)}</span> : null}
        </>
      )}
      {started ? (
        <div className="exo-dc-video-bar">
          <button type="button" className="exo-dc-icon-btn" aria-label={playing ? 'Pause' : 'Play'} onClick={toggle}>{playing ? <Pause size={16} /> : <Play size={16} />}</button>
          <span className="exo-dc-video-time">{clock(at)} / {clock(length)}</span>
          <Slider value={at} max={length || 1} step={0.1} aria-label="Position" onChange={event => { if (video.current) video.current.currentTime = Number(event.target.value) }} />
          <button type="button" className="exo-dc-icon-btn" aria-label={muted ? 'Unmute' : 'Mute'} onClick={() => { if (video.current) video.current.muted = !video.current.muted }}>{muted ? <Icons.SoundOff size={16} /> : <Icons.Sound size={16} />}</button>
          <button type="button" className="exo-dc-icon-btn" aria-label="Full screen" onClick={() => { void video.current?.requestFullscreen().catch(() => { }) }}><Icons.Expand size={16} /></button>
        </div>
      ) : null}
    </div>
  )
}

/**
 * A picture or video in the chat: behind SPOILER or the sensitive content blur when it is one, left out when
 * your settings block it; with Show image descriptions, an ALT tag that shows what the sender wrote. In a
 * mosaic it fills its cell.
 */
function Media({ file, media, guildId, moderator, cell, pictures, more }: { file: DiscordAttachment; media: 'image' | 'video'; guildId: string | null; moderator?: boolean; cell?: boolean; pictures?: DiscordAttachment[]; more?: number }) {
  const settings = useAccountSettings()
  const [alt, setAlt] = useState(false)
  const src = file.proxy_url ?? file.url
  const [failed, setFailed] = useFailedUntilBack<string | null>(null)
  const box = cell ? { width: undefined, height: undefined } : fit(file.width, file.height)
  const flags = file.flags ?? 0
  const spoiler = (file.filename.startsWith('SPOILER_') || (flags & 8) !== 0) && !spoilersShown(settings.renderSpoilers, moderator)
  const explicit = sensitive(flags, guildId, settings)
  if (explicit === 'block') return <p className="exo-dc-small">Sensitive content hidden by your settings.</p>
  if (failed === src) return <div className={`exo-dc-media-wrap${cell ? ' is-cell' : ''}`}><MediaFailed name={file.filename} kind={media === 'image' ? 'picture' : 'video'} url={file.url} /></div>
  const shown = <MediaShown file={file} media={media} src={src} box={box} pictures={pictures} onFail={() => setFailed(src)} />
  const described = settings.viewImageDescriptions && !!file.description
  return (
    <div className={`exo-dc-media-wrap${cell ? ' is-cell' : ''}`}>
      {spoiler || explicit === 'blur' ? <Hidden label={spoiler ? 'Spoiler' : 'Sensitive content'}>{shown}</Hidden> : shown}
      {more ? <span className="exo-dc-more" aria-hidden="true">+{more}</span> : null}
      {described ? <button type="button" className="exo-dc-alt-tag" aria-expanded={alt} aria-label="Image description" onClick={() => setAlt(a => !a)}>ALT</button> : null}
      {described && alt ? <span className="exo-dc-alt" role="note">{file.description}</span> : null}
    </div>
  )
}

// The picture (opening the viewer) or the video itself.
function MediaShown({ file, media, src, box, pictures, onFail }: { file: DiscordAttachment; media: 'image' | 'video'; src: string; box: { width?: number; height?: number }; pictures?: DiscordAttachment[]; onFail: () => void }) {
  if (media === 'video') return <VideoFile src={src} box={box} duration={file.duration_secs} onFail={onFail} />
  // Attachment flag 32: an animated WebP or AVIF, which keeps its own format to keep moving.
  return (
    <button type="button" className="exo-dc-media" onClick={() => showImage(src, file.url, false, pictures && { items: pictures.map(viewerItem), index: pictures.indexOf(file) })} aria-label={file.description ? `Image: ${file.description}` : 'Open image'}>
      <MovingImage src={src} width={file.width} height={file.height} moving={((file.flags ?? 0) & 32) !== 0 || /\.gif$/i.test(file.filename)} alt={file.description ?? ''} link={file.url} box={box} onFail={onFail} />
    </button>
  )
}

const viewerItem = (file: DiscordAttachment): ViewerItem => ({ src: file.proxy_url ?? file.url, original: file.url, alt: file.description })

// The gallery: 2 side by side, 3 one tall and two stacked, 4 a square of four, more a grid of six with "+N" on the last.
const SHOWN = 6

function Mosaic({ files, guildId, moderator }: { files: { file: DiscordAttachment; media: 'image' | 'video' }[]; guildId: string | null; moderator?: boolean }) {
  if (files.length === 1) return <Media file={files[0].file} media={files[0].media} guildId={guildId} moderator={moderator} />
  // The viewer goes through the message's pictures (a video plays where it is).
  const pictures = files.filter(f => f.media === 'image').map(f => f.file)
  const shown = files.slice(0, SHOWN)
  const left = files.length - shown.length
  return (
    <div className={`exo-dc-mosaic is-${shown.length}`}>
      {shown.map((f, i) => <Media key={f.file.id} file={f.file} media={f.media} guildId={guildId} moderator={moderator} cell pictures={pictures} more={left > 0 && i === shown.length - 1 ? left + 1 : undefined} />)}
    </div>
  )
}

/** A file's name, size and download, as Discord's file card. */
function FileCard({ file, children }: { file: DiscordAttachment; children?: React.ReactNode }) {
  return (
    <div className={`exo-dc-file${children ? ' has-preview' : ''}`}>
      {children}
      <div className="exo-dc-file-row">
        <Icons.File size={28} />
        <span className="exo-dc-file-meta">
          <button type="button" className="exo-dc-file-name" onClick={() => open(file.url)}>{file.filename}</button>
          <span className="exo-dc-file-size">{size(file.size)}</span>
        </span>
        <button type="button" className="exo-dc-icon-btn" aria-label={`Download ${file.filename}`} onClick={() => open(file.url)}><Icons.Download size={18} /></button>
      </div>
    </div>
  )
}

const COLLAPSED_LINES = 6

/** Discord's text file preview: the first lines in a code box, Expand for the rest it read (50 KB), the file's card under it. */
function TextPreview({ file }: { file: DiscordAttachment }) {
  const [text, setText] = useState<{ body: string; left: number } | null>(null)
  const [failed, setFailed] = useState(false)
  const [expanded, setExpanded] = useState(false)
  useEffect(() => {
    let live = true
    host.discordFileText(file.url).then(result => {
      if (!live) return
      if (result.ok && typeof result.text === 'string') setText({ body: result.text, left: result.left ?? 0 })
      else setFailed(true)
    }).catch(() => { if (live) setFailed(true) })
    return () => { live = false }
  }, [file.url])
  if (failed) return <FileCard file={file} />
  const lines = text ? text.body.replace(/\n$/, '').split('\n') : []
  const long = lines.length > COLLAPSED_LINES || (text?.left ?? 0) > 0
  const body = text ? (expanded ? lines : lines.slice(0, COLLAPSED_LINES)).join('\n') : ''
  return (
    <FileCard file={file}>
      {text ? <pre className="exo-dc-file-preview">{body}{expanded && text.left > 0 ? `\n… ${size(text.left)} more` : ''}</pre>
        : <Wait label={`Reading ${file.filename}`}><pre className="exo-dc-file-preview">{times(6, i => `${sample(i)}\n`)}</pre></Wait>}
      {long ? (
        <button type="button" className="exo-dc-file-expand" aria-expanded={expanded} onClick={() => setExpanded(e => !e)}>
          {expanded ? <Icons.Collapse size={16} /> : <Icons.Expand size={16} />}
          {expanded ? 'Collapse' : 'Expand'} ({lines.length}{text && text.left > 0 ? '+' : ''} lines)
        </button>
      ) : null}
    </FileCard>
  )
}

function Other({ file }: { file: DiscordAttachment }) {
  // A voice message plays with its waveform; any other sound file in a small player, as in Discord.
  if (file.waveform) return <VoiceMessage attachment={file} />
  if ((file.content_type ?? '').startsWith('audio/')) {
    return (
      <div className="exo-dc-audio">
        <span className="exo-dc-file-name">{file.filename}<span className="exo-dc-file-size">{size(file.size)}</span></span>
        <audio src={file.url} controls preload="metadata" />
      </div>
    )
  }
  const extension = file.filename.split('.').pop()?.toLowerCase() ?? ''
  if (file.content_type?.startsWith('text/') || TEXT.has(extension)) return <TextPreview file={file} />
  return <FileCard file={file} />
}

/**
 * A message's files, Discord's way: pictures and videos together in one mosaic (or one alone at its own size),
 * then each other file: a voice message, a sound player, a text preview, or a file card.
 */
export function Attachments({ files, guildId, moderator }: { files: DiscordAttachment[]; guildId: string | null; moderator?: boolean }) {
  // Discord settings > Appearance > Show images, videos... When uploaded directly to Discord; off, they're files to open.
  const inline = useAccountSetting('inlineAttachmentMedia')
  const media = files.flatMap(file => { const kind = mediaOf(file, inline); return kind ? [{ file, media: kind }] : [] })
  const rest = files.filter(file => !mediaOf(file, inline))
  return (
    <>
      {media.length ? <Mosaic files={media} guildId={guildId} moderator={moderator} /> : null}
      {rest.map(file => <Other key={file.id} file={file} />)}
    </>
  )
}
