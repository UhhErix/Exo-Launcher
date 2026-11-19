import { useCallback, useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from './host'
import { onPageActivityChanged, pageIsVisible } from './pageActivity'
import { report } from './report'
import type { FromWorker, ToWorker, WorkerStats } from './discordVideo.worker'

/**
 * Discord video in the page (docs/architecture/discord-video.md): the host hands over whole H.264
 * or H.265 frames (Annex B, already decrypted) per person, and each feed passes them to a worker
 * (discordVideo.worker.ts) that decodes them with WebCodecs (hardware where the PC has it), holds the
 * pictures in a small jitter buffer and hands each to the feed's video track at its own time on the
 * sender's clock; every <video> showing that person plays the track. The browser shows a video track
 * as its own layer, off the page's thread and as an overlay where the card has one, where a canvas
 * cost a draw into it and the page's composite on every picture, each waiting behind a game's work,
 * and pictures that came bunched up (a resent packet holds back the ones after it) were shown at
 * once, one replacing the next. A feed starts at a keyframe and starts over at the next one after a
 * decoding error. Nothing is decoded while no video shows a feed or Exo is off screen, and the host
 * sends call video only while one does (asking the senders for a keyframe when it starts again).
 */
/** A camera in the call, a stream being watched, or your own stream (its tile, the frames viewers get). */
export type VideoSource = 'call' | 'stream' | 'self'
// rotation: quarter turns clockwise that show the sender's picture upright, flip: mirrored first (their video-orientation).
type FrameEvent = { source: VideoSource; userId: string; timestamp: number; keyframe: boolean; bytes?: Uint8Array; data?: string; rotation?: number; flip?: boolean; codec?: 'h264' | 'h265' }

// Chromium's track made from VideoFrames (WebView2 has had it since Chromium 94); not in TypeScript's DOM types.
declare const MediaStreamTrackGenerator: (new (init: { kind: 'video' }) => MediaStreamTrack & { writable: WritableStream<VideoFrame> }) | undefined

// H.265 Main up to level 5.1 (4K 60). Discord's apps offer to receive H.265 only where it decodes in hardware, and so
// does Exo: the host hears once whether this PC can.
if (typeof VideoDecoder !== 'undefined')
  void VideoDecoder.isConfigSupported({ codec: 'hvc1.1.6.L153.B0', hardwareAcceleration: 'prefer-hardware' })
    .then(result => host.discordVideoDecode(result.supported === true))
    .catch(() => { })

const key = (source: VideoSource, userId: string) => `${source}:${userId}`

// A frame's base64 as bytes, in a plain loop: Uint8Array.from with a map function walks the string as an iterator,
// several times slower on every frame of a 60 fps stream.
function decode(base64: string): Uint8Array<ArrayBuffer> {
  const text = atob(base64)
  const bytes = new Uint8Array(text.length)
  for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i)
  return bytes
}

// The one worker every feed decodes and times its pictures in, started with the first feed shown.
let worker: Worker | null = null
function post(message: ToWorker, transfer: Transferable[] = []) {
  if (!worker) {
    worker = new Worker(new URL('./discordVideo.worker.ts', import.meta.url), { type: 'module' })
    worker.onmessage = ({ data }: MessageEvent<FromWorker>) => feeds.get(data.id)?.heard(data)
    worker.onerror = event => report(new Error(event.message), 'DiscordVideo.worker', { level: 'warn' })
  }
  worker.postMessage(message, transfer)
}

const tenths = (ms: number) => Math.round(ms * 10) / 10
const avg = (sum: number, n: number) => (n ? tenths(sum / n) : 0)
const noScreen = () => ({ shown: 0, gapMs: 0, gaps: 0, gapMax: 0 })

class Feed {
  private stream: MediaStream | null = null
  // Without Chromium's track made from frames (another browser running the dev build), the worker sends each picture
  // back to be drawn here into a canvas, captured as the track.
  private canvas: HTMLCanvasElement | null = null
  private videos = new Set<HTMLVideoElement>()
  private listeners = new Set<() => void>()
  // The worker was told nothing shows the feed (it lets its pictures go and waits for a keyframe).
  private idle = true
  // Counts the stops, so a "live" the worker sent before one isn't taken for the feed starting again.
  private generation = 0
  // A watched stream's video as it showed pictures since the last report (requestVideoFrameCallback): the screen's
  // own gaps, beside the worker's count of what it handed over.
  private counted: HTMLVideoElement | null = null
  private countHandle = 0
  private screen = noScreen()
  private lastAt = 0
  private lastFrames = 0
  live = false
  /** The host said this feed ended (a stream that closed or never opened). */
  ended = false

  constructor(readonly id: string, readonly source: VideoSource) { }

  private open(): MediaStream {
    if (this.stream) return this.stream
    let writable: WritableStream<VideoFrame> | null = null
    if (typeof MediaStreamTrackGenerator === 'function') {
      const track = new MediaStreamTrackGenerator({ kind: 'video' })
      this.stream = new MediaStream([track])
      // Handed to the worker, which then writes pictures straight to the track, never through this thread.
      writable = track.writable
    } else {
      this.canvas = document.createElement('canvas')
      this.stream = this.canvas.captureStream(0)
    }
    post({ type: 'open', id: this.id, writable, counts: this.source === 'stream', generation: this.generation }, writable ? [writable] : [])
    return this.stream
  }

  attach(video: HTMLVideoElement) {
    this.videos.add(video)
    video.muted = true
    video.srcObject = this.open()
    void video.play().catch(() => { })
    // A video that starts showing a live feed (a stream moving between the call's stage and its mini player) starts on
    // the newest picture, not blank until the next one: sent again once the video is listening to the track (its load
    // has begun), as a picture written before that never reaches it.
    if (this.live) video.addEventListener('loadstart', () => post({ type: 'refresh', id: this.id }), { once: true })
    this.count()
    // A video newly showing the feed starts at a keyframe: the host is told again that frames are wanted (it asks the
    // senders for one), even when it was last told so, rather than the picture waiting for the sender's own next one.
    framesOn = null
    wantFrames()
    return () => {
      this.videos.delete(video)
      video.srcObject = null
      if (this.counted === video) this.count()
      if (!this.shown && !this.idle) { this.idle = true; post({ type: 'idle', id: this.id }) }
      wantFrames()
    }
  }
  get shown() { return this.videos.size > 0 }
  subscribe(listener: () => void) { this.listeners.add(listener); return () => { this.listeners.delete(listener) } }
  private changed() { this.listeners.forEach(listener => listener()) }

  push(event: FrameEvent) {
    // Nowhere to show it, or Exo off screen: skipped, and the feed starts again at the next keyframe.
    if (!this.shown || !pageIsVisible()) {
      if (!this.idle) { this.idle = true; post({ type: 'idle', id: this.id }) }
      return
    }
    this.idle = false
    // Its own copy, moved to the worker: the host's shared buffer is released as this returns.
    const bytes = event.bytes ? event.bytes.slice() : decode(event.data ?? '')
    post({
      type: 'chunk', id: this.id, data: bytes.buffer, timestamp: event.timestamp, keyframe: event.keyframe, codec: event.codec ?? 'h264',
      rotation: event.rotation ?? 0, flip: event.flip ?? false, arrived: performance.timeOrigin + performance.now(),
    }, [bytes.buffer])
  }

  heard(message: FromWorker) {
    switch (message.type) {
      case 'live':
        if (message.generation !== this.generation || this.live) return
        this.live = true
        this.ended = false
        this.changed()
        return
      case 'frame': this.draw(message.frame, message.rotation, message.flip); return
      case 'stats': this.tell(message.stats); return
      case 'error': report(new Error(message.message), message.where, { level: 'warn' }); return
      case 'keyframe': void host.discordKeyframe().catch(() => undefined)
    }
  }

  // The fallback's picture into its canvas, upright: about the center, turned clockwise and mirrored as the sender says.
  private draw(frame: VideoFrame, rotation: number, flip: boolean) {
    const canvas = this.canvas
    const draw = canvas?.getContext('2d')
    if (canvas && draw) {
      const w = frame.displayWidth, h = frame.displayHeight
      const [width, height] = rotation % 2 ? [h, w] : [w, h]
      if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height }
      draw.setTransform(1, 0, 0, 1, width / 2, height / 2)
      draw.rotate(rotation * Math.PI / 2)
      if (flip) draw.scale(-1, 1)
      draw.drawImage(frame, -w / 2, -h / 2)
      ;(this.stream?.getVideoTracks()[0] as CanvasCaptureMediaStreamTrack | undefined)?.requestFrame()
    }
    frame.close()
  }

  // A watched stream counts what its video shows: each call back says how many pictures it has shown and when the
  // newest shows, so the report's gaps are the screen's (a busy page calls back late, once for several: each counts
  // its share of the time between).
  private count() {
    if (this.source !== 'stream') return
    if (this.counted && this.countHandle) this.counted.cancelVideoFrameCallback(this.countHandle)
    this.countHandle = 0
    const video = this.videos.values().next().value ?? null
    this.counted = video
    this.lastAt = this.lastFrames = 0
    if (!video) return
    const shown: VideoFrameRequestCallback = (_, meta) => {
      const s = this.screen
      const frames = meta.presentedFrames - this.lastFrames
      if (this.lastAt && frames > 0) {
        const between = meta.expectedDisplayTime - this.lastAt
        s.shown += frames
        s.gapMs += between
        s.gaps += frames
        if (between / frames > s.gapMax) s.gapMax = between / frames
      }
      this.lastAt = meta.expectedDisplayTime
      this.lastFrames = meta.presentedFrames
      if (this.counted === video) this.countHandle = video.requestVideoFrameCallback(shown)
    }
    this.countHandle = video.requestVideoFrameCallback(shown)
  }

  // A watched stream's last 30 s on screen to the host's app.log (the host's own line says what arrived and decrypted;
  // this says what the page made of it beside a game): the worker's counts, and the screen's where the video said.
  private tell(w: WorkerStats) {
    const s = this.screen
    const counted = s.gaps > 0
    const presented = counted ? s.shown : w.written
    void host.discordStreamDrawn({
      received: w.received, decoded: w.decoded, presented, dropped: w.dropped + Math.max(0, w.written - presented), waiting: w.waiting,
      errors: w.errors, late: w.late, decodeMs: avg(w.decodeMs, w.decodes), decodeMax: tenths(w.decodeMax), queueMax: w.queueMax,
      heldMs: avg(w.heldMs, w.written), heldMax: tenths(w.heldMax), depthMax: w.depthMax, leadMs: w.leadMs,
      gapMs: counted ? avg(s.gapMs, s.gaps) : avg(w.gapMs, w.gaps), gapMax: tenths(counted ? s.gapMax : w.gapMax),
      seconds: w.seconds, codec: w.codec, decoder: w.hardware ? 'hardware' : 'any',
    }).catch(() => { })
    this.screen = noScreen()
  }

  /** The video stopped; ended when the host says it closed rather than that it is still connecting. */
  stop(ended: boolean) {
    this.generation++
    if (this.stream) post({ type: 'stop', id: this.id, generation: this.generation })
    this.screen = noScreen()
    if (this.live || this.ended !== ended) { this.live = false; this.ended = ended; this.changed() }
  }
}

const feeds = new Map<string, Feed>()
const feedOf = (source: VideoSource, userId: string) => {
  const id = key(source, userId)
  let feed = feeds.get(id)
  if (!feed) { feed = new Feed(id, source); feeds.set(id, feed) }
  return feed
}

// Whether the host should send call video: a camera or watched stream has a video, and Exo is on screen. Settled once
// the task's videos are all in: a stream moving from the stage to its mini player is one video leaving and another
// arriving, and the host stopping and starting again would ask the sender for a keyframe. framesOn is what the host was
// last told; null until this page has told it (it may still send from before the page loaded) or after a call failed,
// so the first answer always goes out: one taken as already given left a restarted Exo's stream without a picture.
let framesOn: boolean | null = null
let framesQueued = false
function wantFrames() {
  if (framesQueued) return
  framesQueued = true
  queueMicrotask(() => {
    framesQueued = false
    const on = pageIsVisible() && [...feeds.values()].some(feed => feed.source !== 'self' && feed.shown)
    if (on === framesOn) return
    framesOn = on
    // Refused or failed: not told, and asked again in a second (it said "not now" once and was never asked again).
    const retry = () => { framesOn = null; window.setTimeout(wantFrames, 1000) }
    void host.discordFrames(on).then(answer => { if (!answer.ok) retry() }, retry)
  })
}

let listening = false
function listen() {
  if (listening) return
  listening = true
  onHostEvent('discord.video.frame', event => feedOf(event.source, event.userId).push(event))
  onHostEvent('discord.video', event => { if (!event.active) feeds.get(key(event.source, event.userId))?.stop(event.state !== undefined && event.state !== 'connecting') })
  onPageActivityChanged(wantFrames)
  wantFrames()
}

/** A person's video: a ref for the <video> that shows it, whether pictures are arriving, and whether the host said it ended. */
export function useVideoFeed(source: VideoSource, userId: string | null) {
  // A feed already live shows from the first frame (the mini player taking over the stage's stream).
  const [state, setState] = useState(() => ({ live: userId ? feeds.get(key(source, userId))?.live ?? false : false, ended: false }))
  useEffect(() => {
    if (!userId) return
    listen()
    const feed = feedOf(source, userId)
    // A stream watched again starts fresh, not from how the last watch ended.
    if (source === 'stream' && !feed.live) feed.ended = false
    setState({ live: feed.live, ended: feed.ended })
    return feed.subscribe(() => setState({ live: feed.live, ended: feed.ended }))
  }, [source, userId])
  const detach = useRef<(() => void) | null>(null)
  const ref = useCallback((video: HTMLVideoElement | null) => {
    detach.current?.()
    detach.current = null
    if (!video || !userId) return
    listen()
    detach.current = feedOf(source, userId).attach(video)
  }, [source, userId])
  return { ref, ...state }
}
