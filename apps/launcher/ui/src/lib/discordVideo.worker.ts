/**
 * Discord video's decoding and timing, off the page's thread (discordVideo.ts has the whole path). Per feed: a WebCodecs
 * decoder (hardware first), a small jitter buffer that hands each picture to the feed's video track at its own time on
 * the sender's clock (its RTP timestamps), and the numbers a watched stream reports. The page only passes on what the
 * host sends, so a long task there (a busy room, the CPU shared with a game) neither holds pictures back nor bunches
 * them up: pictures reach the video from here, through the track's writable the page handed over.
 */

export type ToWorker =
  | { type: 'open'; id: string; writable: WritableStream<VideoFrame> | null; counts: boolean; generation: number }
  // arrived: when the page had it, in ms from the epoch (performance.timeOrigin + performance.now()).
  | { type: 'chunk'; id: string; data: ArrayBuffer; timestamp: number; keyframe: boolean; codec: 'h264' | 'h265'; rotation: number; flip: boolean; arrived: number }
  | { type: 'idle' | 'refresh'; id: string }
  | { type: 'stop'; id: string; generation: number }
export type FromWorker =
  | { type: 'live'; id: string; generation: number }
  | { type: 'frame'; id: string; frame: VideoFrame; rotation: number; flip: boolean }
  | { type: 'stats'; id: string; stats: WorkerStats }
  | { type: 'error'; id: string; where: string; message: string }
  // Pictures are arriving and none can be shown until a keyframe: the sender is asked for one now, not at its next.
  | { type: 'keyframe'; id: string }
/** A watched stream's last 30 s as decoded and handed to its video: counts of pictures, sums and maxima in ms. */
export type WorkerStats = ReturnType<typeof noStats> & { seconds: number; leadMs: number; codec: 'h264' | 'h265'; hardware: boolean }
type Turn = { rotation: number; flip: boolean }

const scope = self as unknown as { onmessage: ((event: MessageEvent<ToWorker>) => void) | null; postMessage(message: FromWorker, options?: { transfer: Transferable[] }): void }

// H.265 Main up to level 5.1 (4K 60), Annex B as the host sends it.
const HEVC = 'hvc1.1.6.L153.B0'

/**
 * An H.265 keyframe's picture size, from its sequence parameter set (NAL type 33) less the conformance window. Edge's
 * hardware H.265 decoder calls every picture 1280×720 (WebCodecs' size when the config names none), so a 1440p or
 * 1080p stream was drawn cropped to its top-left corner; the SPS says what it really is.
 */
function hevcSize(bytes: Uint8Array): { width: number; height: number } | null {
  for (let i = 0; i + 5 < bytes.length; i++)
    if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1 && ((bytes[i + 3] >> 1) & 0x3f) === 33) return spsSize(spsPayload(bytes, i + 5))
  return null
}

// An SPS's payload after its two-byte header, emulation prevention (00 00 03) taken out, up to the next start code.
function spsPayload(bytes: Uint8Array, from: number): number[] {
  const sps: number[] = []
  for (let j = from, zeros = 0; j < bytes.length && sps.length < 128; j++) {
    const b = bytes[j]
    if (zeros >= 2 && b === 3) { zeros = 0; continue }
    if (zeros >= 2 && b <= 1) break
    sps.push(b)
    zeros = b === 0 ? zeros + 1 : 0
  }
  return sps
}

// H.265's seq_parameter_set_rbsp up to the conformance window (ITU-T H.265 7.3.2.2).
function spsSize(sps: number[]): { width: number; height: number } | null {
  let at = 0
  const bit = () => ((sps[at >> 3] ?? 0) >> (7 - (at++ & 7))) & 1
  const bits = (n: number) => { let v = 0; for (let k = 0; k < n; k++) v = v * 2 + bit(); return v }
  const ue = () => { let z = 0; while (z < 32 && bit() === 0) z++; return 2 ** z - 1 + bits(z) }
  at += 4 // sps_video_parameter_set_id
  const subLayers = bits(3)
  at += 1 + 96 // temporal id nesting; profile_tier_level's general profile (88 bits) and level (8)
  const present = Array.from({ length: subLayers }, () => [bit(), bit()])
  if (subLayers > 0) at += 2 * (8 - subLayers)
  for (const [profile, level] of present) at += profile * 88 + level * 8
  ue() // sps_seq_parameter_set_id
  const chroma = ue()
  if (chroma === 3) at += 1
  let width = ue(), height = ue()
  if (bit()) {
    const [left, right, top, bottom] = [ue(), ue(), ue(), ue()]
    width -= (chroma === 1 || chroma === 2 ? 2 : 1) * (left + right)
    height -= (chroma === 1 ? 2 : 1) * (top + bottom)
  }
  return width > 0 && height > 0 && width <= 8192 && height <= 8192 ? { width, height } : null
}

// The decoded picture at the size its SPS says, where the decoder said otherwise (a view of the same picture, no copy).
function atSize(frame: VideoFrame, size: { width: number; height: number } | null): VideoFrame {
  if (!size || (frame.displayWidth === size.width && frame.displayHeight === size.height)
    || size.width > frame.codedWidth || size.height > frame.codedHeight) return frame
  try {
    const sized = new VideoFrame(frame, { visibleRect: { x: 0, y: 0, ...size }, displayWidth: size.width, displayHeight: size.height })
    frame.close()
    return sized
  } catch {
    return frame
  }
}

/**
 * The jitter buffer: each picture is due at its RTP time plus an offset on this clock, set from when pictures arrive.
 * It aims LEAD_START_MS past the earliest arrival of the last two seconds, rises by half of what a picture missed its
 * time by, and comes back down up to 2 ms a second while every picture is ready early, staying between LEAD_MIN_MS
 * (decoding's own time) and LEAD_MAX_MS: about what the stream's sound waits in its own buffer (VoiceAudio holds 60 ms
 * before a speaker plays, then the device's own), so the picture, which showed the moment it decoded, ahead of the
 * sound, at most catches up with it. A picture already late plays at once, a little after the one before it (SPACING
 * of a frame) so each shows on screen; pictures a whole frame behind the next are let go.
 */
const LEAD_START_MS = 33, LEAD_MIN_MS = 12, LEAD_MAX_MS = 80
const EARLY_MS = 6 // spare time every picture had over the last second before the buffer shrinks
const SPACING = 0.5
const HELD_MOST = 6 // pictures held at most (100 ms at 60 fps); each holds one of the decoder's output pictures
const TRANSIT_KEPT = 128 // about two seconds of arrivals at 60 fps
const REPORT_MS = 30_000
// At most this often, a feed waiting for a keyframe asks its sender for one.
const KEYFRAME_ASK_MS = 1000

const noStats = () => ({
  received: 0, decoded: 0, written: 0, dropped: 0, waiting: 0, errors: 0, late: 0,
  decodeMs: 0, decodeMax: 0, decodes: 0, heldMs: 0, heldMax: 0, depthMax: 0, gapMs: 0, gapMax: 0, gaps: 0, queueMax: 0,
})

type Held = { frame: VideoFrame; due: number; arrived: number }

class Feed {
  private decoder: VideoDecoder | null = null
  private codec: 'h264' | 'h265' = 'h264'
  private waiting = true
  // When it last asked for a keyframe (performance.now()); 0 while it isn't waiting.
  private askedAt = -Infinity
  // Hardware first; a graphics card that can't take this stream (an older one, or a size or level it lacks) fails
  // before its first picture, and the feed then decodes in software instead of starting over the same way forever.
  private hardware = true
  private drew = false
  private turn: Turn = { rotation: 0, flip: false }
  // An H.265 stream's picture size, from its latest keyframe's SPS (hevcSize).
  private size: { width: number; height: number } | null = null
  // The jitter buffer, oldest first; offset: a picture's due time (this clock) less its timestamp in ms.
  private held: Held[] = []
  private offset = NaN
  private lastTs = 0
  private interval = 1000 / 60
  private readonly transit = new Float64Array(TRANSIT_KEPT)
  private transits = 0
  // The least spare time a picture had in the current second (ms; negative: late), and when that second began.
  private spare = Infinity
  private spareFrom = 0
  private timer = 0
  private lastWrite = 0
  // The newest picture handed over, kept (a reference, no copy) for a video that starts showing the feed.
  private shown: VideoFrame | null = null
  private generation = 0
  private live = false
  private stats = noStats()
  private statsFrom = 0
  // Arrival times wait in a small ring by chunk timestamp until their picture comes out: nothing allocated per picture.
  private readonly decodeTs = new Float64Array(32)
  private readonly decodeAt = new Float64Array(32)
  private decodeNext = 0
  private readonly writer: WritableStreamDefaultWriter<VideoFrame> | null

  constructor(readonly id: string, writable: WritableStream<VideoFrame> | null, readonly counts: boolean, generation: number) {
    this.writer = writable?.getWriter() ?? null
    this.generation = generation
  }

  chunk(message: Extract<ToWorker, { type: 'chunk' }>) {
    const now = performance.now()
    this.tally(now)
    this.stats.received++
    // The server moved this sender to the other codec: a decoder for it, from its next keyframe.
    if (message.codec !== this.codec) { this.reset(); this.codec = message.codec }
    if (this.waiting && !message.keyframe) {
      this.stats.waiting++
      // Asked at once and then every second while it waits: a sender keyframes every 20 to 35 s by itself, and a feed
      // that started after the last one (a watch begun again, the window back on screen, a decoder reset) would hold a
      // frozen picture that long.
      if (now - this.askedAt >= KEYFRAME_ASK_MS) { this.askedAt = now; scope.postMessage({ type: 'keyframe', id: this.id }) }
      return
    }
    const decoder = this.decoder ?? this.open()
    if (!decoder) return
    this.waiting = false
    this.turn = { rotation: message.rotation, flip: message.flip }
    const bytes = new Uint8Array(message.data)
    if (this.codec === 'h265' && message.keyframe) this.size = hevcSize(bytes) ?? this.size
    try {
      // RTP's 90 kHz clock in microseconds, as WebCodecs counts.
      const timestamp = Math.round(message.timestamp / 0.09)
      decoder.decode(new EncodedVideoChunk({ type: message.keyframe ? 'key' : 'delta', timestamp, data: bytes }))
      const slot = this.decodeNext++ & 31
      this.decodeTs[slot] = timestamp
      // When the page had it (its clock and this one start apart: both said from the epoch).
      this.decodeAt[slot] = Math.min(now, message.arrived - performance.timeOrigin)
      if (decoder.decodeQueueSize > this.stats.queueMax) this.stats.queueMax = decoder.decodeQueueSize
    } catch (error) {
      this.error('DiscordVideo.decode', error)
      this.reset()
    }
  }

  // A watched stream's last 30 s to the page (which tells the host's app.log), then counting starts over.
  private tally(now: number) {
    if (!this.counts) return
    if (!this.statsFrom) { this.statsFrom = now; return }
    if (now - this.statsFrom < REPORT_MS) return
    scope.postMessage({ type: 'stats', id: this.id, stats: {
      ...this.stats, seconds: Math.round((now - this.statsFrom) / 1000), leadMs: this.lead(), codec: this.codec, hardware: this.codec === 'h265' || this.hardware,
    } })
    this.stats = noStats()
    this.statsFrom = now
  }

  private error(where: string, error: unknown) {
    this.stats.errors++
    scope.postMessage({ type: 'error', id: this.id, where, message: error instanceof Error ? `${error.name}: ${error.message}` : String(error) })
  }

  private open(): VideoDecoder | null {
    if (typeof VideoDecoder === 'undefined') return null
    const decoder = new VideoDecoder({
      output: decoded => this.decoded(decoded),
      error: error => {
        this.error(this.hardware ? 'DiscordVideo.decoder (hardware)' : 'DiscordVideo.decoder (software)', error)
        if (!this.drew) this.hardware = false
        this.reset()
      },
    })
    this.drew = false
    try {
      // Annex B with its parameter sets in the stream, so no description is needed.
      decoder.configure(this.codec === 'h265'
        ? { codec: HEVC, hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true }
        : { codec: 'avc1.640034', hardwareAcceleration: this.hardware ? 'prefer-hardware' : 'no-preference', optimizeForLatency: true })
    } catch (error) {
      this.error('DiscordVideo.configure', error)
      this.hardware = false
      try { decoder.close() } catch { /* never opened */ }
      return null
    }
    this.decoder = decoder
    return decoder
  }

  // A decoded picture into the jitter buffer at its time on the sender's clock.
  private decoded(decoded: VideoFrame) {
    const now = performance.now()
    const s = this.stats
    s.decoded++
    let arrived = now
    for (let i = 0; i < 32; i++) {
      if (this.decodeAt[i] === 0 || this.decodeTs[i] !== decoded.timestamp) continue
      arrived = this.decodeAt[i]
      const took = now - arrived
      this.decodeAt[i] = 0
      s.decodeMs += took
      s.decodes++
      if (took > s.decodeMax) s.decodeMax = took
      break
    }
    this.drew = true
    const frame = atSize(decoded, this.codec === 'h265' ? this.size : null)
    const ms = frame.timestamp / 1000
    const step = ms - this.lastTs
    // Started, or the sender's clock jumped (a new encoder, RTP's wrap): the buffer aims afresh from this picture.
    if (Number.isNaN(this.offset) || step <= 0 || step > 1000) {
      this.offset = arrived - ms + LEAD_START_MS
      this.transits = 0
      this.spare = Infinity
      this.spareFrom = now
    } else this.interval += (Math.min(100, Math.max(4, step)) - this.interval) * 0.1
    this.lastTs = ms
    this.transit[this.transits++ % TRANSIT_KEPT] = arrived - ms
    const earliest = this.earliest()
    const spare = ms + this.offset - now
    if (spare < this.spare) this.spare = spare
    // Late: the buffer grows by half of what it missed by, so a burst after a resent packet plays out instead of
    // dropping, without one stall holding every later picture back by all of it. This picture shows at once.
    if (spare < 0) this.offset += -spare / 2
    if (now - this.spareFrom >= 1000) {
      // Every picture of the last second was ready with time to spare: the buffer gives back up to 2 ms of it, too
      // little to see in the one frame it shortens.
      if (this.spare > EARLY_MS) this.offset -= Math.min(2, (this.spare - EARLY_MS) / 2)
      this.spare = Infinity
      this.spareFrom = now
    }
    this.offset = Math.min(Math.max(this.offset, earliest + LEAD_MIN_MS), earliest + LEAD_MAX_MS)
    const due = Math.max(now, ms + this.offset)
    let at = this.held.length
    while (at > 0 && this.held[at - 1].frame.timestamp > frame.timestamp) at--
    this.held.splice(at, 0, { frame, due, arrived })
    while (this.held.length > HELD_MOST) { this.held.shift()?.frame.close(); s.dropped++ }
    if (this.held.length > s.depthMax) s.depthMax = this.held.length
    this.schedule(now)
  }

  // The earliest any picture of the last two seconds arrived, against its timestamp.
  private earliest() {
    let least = Infinity
    for (let i = Math.min(this.transits, TRANSIT_KEPT) - 1; i >= 0; i--) if (this.transit[i] < least) least = this.transit[i]
    return least
  }

  /** How far past the earliest arrival the buffer aims now (ms). */
  private lead() {
    return Number.isNaN(this.offset) || !this.transits ? 0 : Math.round((this.offset - this.earliest()) * 10) / 10
  }

  private schedule(now: number) {
    if (this.timer || !this.held.length) return
    const at = Math.max(this.held[0].due, this.lastWrite + this.interval * SPACING)
    // Within 2 ms is now: a timer that short comes late anyway (chained timers wait at least 4 ms).
    if (at - now <= 2) this.present()
    else this.timer = setTimeout(this.present, at - now)
  }

  // The next picture to the video at its time.
  private present = () => {
    this.timer = 0
    const now = performance.now()
    const s = this.stats
    // A whole frame behind the next one: let go, so the stream catches up instead of running behind.
    while (this.held.length > 1 && this.held[1].due <= now - this.interval) { this.held.shift()?.frame.close(); s.dropped++ }
    const next = this.held.shift()
    if (!next) return
    if (now - next.due > this.interval / 2) s.late++
    const held = now - next.arrived
    s.heldMs += held
    if (held > s.heldMax) s.heldMax = held
    s.written++
    if (this.lastWrite) {
      const gap = now - this.lastWrite
      s.gapMs += gap
      s.gaps++
      if (gap > s.gapMax) s.gapMax = gap
    }
    this.lastWrite = now
    this.shown?.close()
    this.shown = next.frame.clone()
    this.write(next.frame)
    if (!this.live) { this.live = true; scope.postMessage({ type: 'live', id: this.id, generation: this.generation }) }
    this.schedule(now)
  }

  // To the track, the sender's turn riding on the picture (no copy); without a track, to the page to draw.
  private write(frame: VideoFrame) {
    const { rotation, flip } = this.turn
    if (!this.writer) { scope.postMessage({ type: 'frame', id: this.id, frame, rotation, flip }, { transfer: [frame] }); return }
    let out = frame
    if (rotation || flip) {
      // VideoFrameInit's rotation and flip (Chromium 132) aren't in TypeScript's DOM types yet.
      const turned: VideoFrameInit & { rotation: number; flip: boolean } = { rotation: rotation * 90, flip }
      try { out = new VideoFrame(frame, turned); frame.close() } catch { out = frame }
    }
    this.writer.write(out).catch(() => out.close())
  }

  /** A video started showing the feed: the newest picture again, so it doesn't stay blank until the next. */
  refresh() {
    if (this.live && this.shown) this.write(this.shown.clone())
  }

  /** Nothing shows the feed: the buffer is let go and the next picture shown starts from a keyframe. */
  idle() {
    this.clear()
    this.waiting = true
    this.askedAt = -Infinity
    this.statsFrom = 0
  }

  private clear() {
    clearTimeout(this.timer)
    this.timer = 0
    for (const { frame } of this.held) frame.close()
    this.held = []
    this.offset = NaN
    this.lastWrite = 0
  }

  private reset() {
    this.clear()
    try { if (this.decoder && this.decoder.state !== 'closed') this.decoder.close() } catch { /* already closed */ }
    this.decoder = null
    this.waiting = true
    this.askedAt = -Infinity
    this.size = null
    this.decodeAt.fill(0)
  }

  /** The video stopped: everything starts over, and the next picture handed over makes the feed live again. */
  stop(generation: number) {
    this.reset()
    this.shown?.close()
    this.shown = null
    this.stats = noStats()
    this.statsFrom = 0
    this.live = false
    this.generation = generation
  }
}

const feeds = new Map<string, Feed>()
scope.onmessage = ({ data: message }) => {
  if (message.type === 'open') { feeds.set(message.id, new Feed(message.id, message.writable, message.counts, message.generation)); return }
  const feed = feeds.get(message.id)
  if (!feed) return
  switch (message.type) {
    case 'chunk': feed.chunk(message); break
    case 'idle': feed.idle(); break
    case 'refresh': feed.refresh(); break
    case 'stop': feed.stop(message.generation); break
  }
}
