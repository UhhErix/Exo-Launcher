/**
 * Exo's mailbox (README.md, plan §2.6): a Cloudflare Worker that keeps end-to-end encrypted messages for Exo's own
 * devices (each row 30 days behind the box's newest, so a quiet box keeps its last month; two years untouched and it
 * goes), and a rendezvous where a new PC meets one already linked. It knows no account, name, email or
 * person: a box is named by the hash of a P-256 public key, and whoever holds the private key logs in by signing a
 * challenge. What it stores is ciphertext it can't read. No logs, no console calls, no IP addresses stored.
 *
 * GET /v1/box/<32 hex> (WebSocket): one box, one SQLite Durable Object.
 *   to the client    0x10 challenge(32)   0x11 ready first:u32 last:u32   0x12 message seq:u32 at:u32 blob
 *                    0x13 stored seq:u32  0x14 caught-up last:u32 more:u8
 *   from the client  0x20 auth pub(65) sig(64, low s), over "exo-box-auth/1" || box id(16) || challenge; nothing else first
 *                    0x21 put blob   0x22 since seq:u32   0x23 trim seq:u32
 * PUT, GET, DELETE /v1/media/<32 hex>/<64 hex>: a box's media parts in R2, signed by the box's key (media.js).
 * GET /v1/link (WebSocket): a new PC asks for a nameplate; 0x30 nameplate(8 ASCII digits) comes back.
 * GET /v1/link/<8 digits> (WebSocket): the linked PC joins it. Both get 0x31 joined, then each binary frame goes to
 *   the other as it is. When one side leaves (or breaks a limit) the other gets 0x32 gone and the session ends; a
 *   second joiner crowds the nameplate: both PCs get 0x33 and the session ends. Joins are
 *   limited per address at Cloudflare's edge (LINK_GUESSES); the Worker keeps no count of its own.
 */

import { limits } from './limits.js'
import { MEDIA_PATH, media } from './media.js'

const BOX_PATH = /^\/v1\/box\/([0-9a-f]{32})$/
const LINK_PATH = /^\/v1\/link\/([0-9]{8})$/
const NEW_LINK_PATH = /^\/v1\/link\/new\/([0-9]{8})$/

const CHALLENGE = 0x10, READY = 0x11, MESSAGE = 0x12, STORED = 0x13, CAUGHT_UP = 0x14
const AUTH = 0x20, PUT = 0x21, SINCE = 0x22, TRIM = 0x23
const NAMEPLATE = 0x30, JOINED = 0x31, GONE = 0x32, CROWDED = 0x33
const AUTH_LABEL = new TextEncoder().encode('exo-box-auth/1')
const HALF_ORDER = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551n >> 1n
const OPEN = 1

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname
    const stored = MEDIA_PATH.exec(path)
    if (stored) return media(request, env, stored[1], stored[2])
    const box = BOX_PATH.exec(path)?.[1]
    const join = LINK_PATH.exec(path)?.[1]
    if (!box && !join && path !== '/v1/link') return new Response('Not found', { status: 404 })
    if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket') return new Response('WebSocket only', { status: 426 })
    if (box) return env.BOXES.get(env.BOXES.idFromName(box)).fetch(request)
    if (join) {
      // A join is a guess at a live nameplate: limited per address in Cloudflare's own counter, never stored here.
      const address = request.headers.get('CF-Connecting-IP') ?? ''
      if (!(await env.LINK_GUESSES.limit({ key: address })).success) return new Response('Slow down', { status: 429 })
      return env.LINKS.get(env.LINKS.idFromName(join)).fetch(request)
    }
    for (let tries = 0; tries < 5; tries++) {
      const nameplate = randomNameplate()
      const forward = new Request(`https://mailbox/v1/link/new/${nameplate}`, { headers: request.headers })
      const response = await env.LINKS.get(env.LINKS.idFromName(nameplate)).fetch(forward)
      if (response.status !== 409) return response
    }
    return new Response('Busy', { status: 503 })
  },
}

/** One box: its rows in SQLite, its connections through the WebSocket Hibernation API, so a quiet box costs nothing. */
export class Box {
  constructor(ctx) {
    this.ctx = ctx
    this.sql = ctx.storage.sql
    this.tableMade = false
    this.armed = false
  }

  async fetch(request) {
    const box = BOX_PATH.exec(new URL(request.url).pathname)[1]
    const now = Date.now()
    if (this.ctx.getWebSockets().length >= limits.sockets) {
      // Connections that never logged in make room first.
      for (const ws of this.ctx.getWebSockets()) {
        const kept = ws.deserializeAttachment()
        if (!kept?.authed && now - (kept?.opened ?? 0) > limits.authMs) close(ws, 1008, 'Too slow')
      }
      if (this.ctx.getWebSockets().length >= limits.sockets) return new Response('Full', { status: 429 })
    }
    const challenge = crypto.getRandomValues(new Uint8Array(32))
    const { 0: client, 1: server } = new WebSocketPair()
    this.ctx.acceptWebSocket(server)
    server.serializeAttachment({ box, challenge, opened: now, authed: false, frames: [limits.frames[0], now], puts: [limits.puts[0], now] })
    server.send(frame(CHALLENGE, challenge))
    return new Response(null, { status: 101, webSocket: client })
  }

  async webSocketMessage(ws, message) {
    if (typeof message === 'string') return close(ws, 1003, 'Binary only')
    const data = new Uint8Array(message)
    if (data.length === 0 || data.length > 1 + limits.message) return close(ws, 1009, 'Too big')
    const kept = ws.deserializeAttachment()
    if (!kept) return close(ws, 1011, 'Lost')
    const now = Date.now()
    if (!take(kept.frames, limits.frames, now)) return close(ws, 1008, 'Too many')
    if (!kept.authed) {
      // Nothing but the login, within ten seconds; the client waits for ready before anything else.
      if (data[0] !== AUTH || data.length !== 1 + 65 + 64 || now - kept.opened > limits.authMs) return close(ws, 1008, 'Log in first')
      if (!(await signedIn(kept.box, kept.challenge, data))) return close(ws, 1008, 'Wrong key')
      ws.serializeAttachment({ ...kept, authed: true, challenge: null })
      const { first, last } = this.bounds()
      ws.send(frame(READY, u32(first), u32(last)))
      // A PC that only reads still proves the account is in use.
      this.touch(now)
      return this.arm(now)
    }
    if (data[0] === PUT) {
      if (data.length < 2) return close(ws, 1002, 'Empty')
      if (!take(kept.puts, limits.puts, now)) return close(ws, 1008, 'Too many')
      ws.serializeAttachment(kept)
      const blob = data.slice(1)
      const { seq, at } = this.put(blob, now)
      ws.send(frame(STORED, u32(seq)))
      const out = frame(MESSAGE, u32(seq), u32(at), blob)
      for (const other of this.ctx.getWebSockets())
        if (other !== ws && other.deserializeAttachment()?.authed) try { other.send(out) } catch { /* closing */ }
      return this.arm(now, true)
    }
    ws.serializeAttachment(kept)
    if (data[0] === SINCE && data.length === 5) return this.since(ws, readU32(data, 1))
    if (data[0] === TRIM && data.length === 5) return this.trim(readU32(data, 1))
    return close(ws, 1002, 'Unknown frame')
  }

  async webSocketClose(ws) {
    close(ws)
  }

  async webSocketError(ws) {
    close(ws)
  }

  /**
   * Rows go 30 days behind the box's newest row, not behind the clock, so a box nobody writes to keeps its last month
   * whole (its newest snapshot and kit among it): a PC that comes back after months, or a new PC after the only one was
   * lost, finds the account as it was. While rows come in it's checked daily; once the box goes quiet, only when two
   * years without a login or a write (idleMs) are up, and then the box is deleted whole.
   */
  async alarm() {
    this.table()
    const now = Date.now()
    const used = this.lastUsed()
    if (now - used >= limits.idleMs) {
      await this.ctx.storage.deleteAlarm()
      await this.ctx.storage.deleteAll()
      this.tableMade = false
      this.armed = false
      return
    }
    const newest = this.sql.exec('SELECT seq, at FROM rows ORDER BY seq DESC LIMIT 1').toArray()[0]
    if (newest) {
      // Rows are in time order, so this reads only the expired rows and one more.
      const keep = this.sql.exec('SELECT seq FROM rows WHERE at >= ? ORDER BY seq LIMIT 1', newest.at - limits.keepMs / 1000).toArray()[0]
      if (keep) this.sql.exec('DELETE FROM rows WHERE seq < ?', keep.seq)
    }
    const busy = newest && now - newest.at * 1000 < limits.keepMs
    await this.ctx.storage.setAlarm(busy ? now + limits.alarmGapMs : Math.max(used + limits.idleMs, now + limits.alarmGapMs))
  }

  table() {
    if (this.tableMade) return
    // total: the bytes stored in the box through this row, so the box's size is two row reads, not a scan.
    this.sql.exec('CREATE TABLE IF NOT EXISTS rows (seq INTEGER PRIMARY KEY, at INTEGER NOT NULL, total INTEGER NOT NULL, blob BLOB NOT NULL)')
    // used: when a device last logged in (seconds), written at most once a day.
    this.sql.exec('CREATE TABLE IF NOT EXISTS used (one INTEGER PRIMARY KEY CHECK (one = 1), at INTEGER NOT NULL)')
    this.tableMade = true
  }

  /** When the box was last used (ms): its newest row or its last login, whichever is later; 0 for neither. */
  lastUsed() {
    this.table()
    const row = this.sql.exec('SELECT at FROM rows ORDER BY seq DESC LIMIT 1').toArray()[0]
    const login = this.sql.exec('SELECT at FROM used').toArray()[0]
    return Math.max(row?.at ?? 0, login?.at ?? 0) * 1000
  }

  /** A login, kept to the day: a write per login would spend the free plan's writes for nothing. */
  touch(now) {
    this.table()
    const at = Math.floor(now / 1000)
    const kept = this.sql.exec('SELECT at FROM used').toArray()[0]
    if (kept && at - kept.at < limits.alarmGapMs / 1000) return
    this.sql.exec('INSERT INTO used (one, at) VALUES (1, ?) ON CONFLICT (one) DO UPDATE SET at = excluded.at', at)
  }

  bounds() {
    this.table()
    const row = this.sql.exec('SELECT min(seq) AS first, max(seq) AS last FROM rows').toArray()[0]
    const last = row?.last ?? 0
    return { first: row?.first ?? last + 1, last }
  }

  put(blob, now) {
    this.table()
    const newest = this.sql.exec('SELECT seq, total FROM rows ORDER BY seq DESC LIMIT 1').toArray()[0]
    const seq = (newest?.seq ?? 0) + 1
    const total = (newest?.total ?? 0) + blob.byteLength
    const at = Math.floor(now / 1000)
    this.sql.exec('INSERT INTO rows (seq, at, total, blob) VALUES (?, ?, ?, ?)', seq, at, total, blob.buffer)
    // Over a cap, the oldest rows go; a reader that missed them sees it in ready's first seq.
    if (seq > limits.rows) this.sql.exec('DELETE FROM rows WHERE seq <= ?', seq - limits.rows)
    const cut = total - limits.boxBytes
    if (cut > 0) {
      const oldest = this.sql.exec('SELECT total - length(blob) AS start FROM rows ORDER BY seq LIMIT 1').toArray()[0]
      if (oldest.start < cut) this.sql.exec('DELETE FROM rows WHERE seq < (SELECT seq FROM rows WHERE total - length(blob) >= ? ORDER BY seq LIMIT 1)', cut)
    }
    return { seq, at }
  }

  /** Up to 256 rows or 1 MiB after seq, then caught-up with whether there's more to ask for. */
  since(ws, after) {
    this.table()
    let sent = after
    let count = 0
    let bytes = 0
    let more = false
    for (const row of this.sql.exec('SELECT seq, at, blob FROM rows WHERE seq > ? ORDER BY seq LIMIT ?', after, limits.pageRows + 1)) {
      if (count === limits.pageRows || bytes >= limits.pageBytes) {
        more = true
        break
      }
      const blob = new Uint8Array(row.blob)
      ws.send(frame(MESSAGE, u32(row.seq), u32(row.at), blob))
      sent = row.seq
      bytes += blob.length
      count++
    }
    ws.send(frame(CAUGHT_UP, u32(sent), new Uint8Array([more ? 1 : 0])))
  }

  /** Rows up to seq go (after a device wrote a full snapshot), never the newest, so seq numbers never restart. */
  trim(upTo) {
    this.table()
    this.sql.exec('DELETE FROM rows WHERE seq <= ? AND seq < (SELECT max(seq) FROM rows)', upTo)
  }

  /** The next check: within a day while rows come in, else when the box's two idle years would be up. */
  async arm(now, wrote = false) {
    if (this.armed && !wrote) return
    const due = await this.ctx.storage.getAlarm()
    const want = wrote ? now + limits.alarmGapMs : now + limits.idleMs
    if (due === null || (wrote && due > want)) await this.ctx.storage.setAlarm(want)
    this.armed = true
  }
}

/** One nameplate: the new PC and, once, the PC joining it, for ten minutes. Nothing is stored. */
export class Link {
  constructor(ctx) {
    this.ctx = ctx
  }

  async fetch(request) {
    const path = new URL(request.url).pathname
    const created = NEW_LINK_PATH.exec(path)?.[1]
    const now = Date.now()
    const sockets = this.ctx.getWebSockets()
    if (created) {
      // A nameplate in use, or used, within its ten minutes is never handed out again.
      if (sockets.some(ws => now - (ws.deserializeAttachment()?.at ?? 0) < limits.linkMs)) return new Response('Taken', { status: 409 })
      for (const ws of sockets) close(ws, 1000, 'Expired')
      const server = this.accept({ role: 'new', at: now, joined: false, frames: [limits.linkFrames[0], now] })
      server.ws.send(frame(NAMEPLATE, new TextEncoder().encode(created)))
      return server.response
    }
    const creator = sockets.find(ws => ws.deserializeAttachment()?.role === 'new')
    const kept = creator?.deserializeAttachment()
    if (!kept || now - kept.at >= limits.linkMs) return new Response('No such nameplate', { status: 404 })
    if (kept.joined) {
      // Crowded (Magic Wormhole's rule): someone else came for this nameplate, so neither side can trust it.
      for (const ws of sockets) {
        try { ws.send(frame(CROWDED)) } catch { /* closing */ }
        close(ws, 1008, 'Crowded')
      }
      return new Response('Crowded', { status: 409 })
    }
    creator.serializeAttachment({ ...kept, joined: true })
    const joiner = this.accept({ role: 'join', at: kept.at, frames: [limits.linkFrames[0], now] })
    creator.send(frame(JOINED))
    joiner.ws.send(frame(JOINED))
    return joiner.response
  }

  async webSocketMessage(ws, message) {
    if (typeof message === 'string') return this.end(ws, 1003, 'Binary only')
    const data = new Uint8Array(message)
    const kept = ws.deserializeAttachment()
    const now = Date.now()
    if (!kept || now - kept.at >= limits.linkMs) return this.end(ws, 1000, 'Expired')
    if (data.length === 0 || data.length > limits.message) return this.end(ws, 1009, 'Too big')
    // The rendezvous' own frames are 0x30 and up; a PC can't send one in the other's name.
    if (data[0] >= NAMEPLATE) return this.end(ws, 1008, 'Not yours')
    if (!take(kept.frames, limits.linkFrames, now)) return this.end(ws, 1008, 'Too many')
    ws.serializeAttachment(kept)
    for (const other of this.ctx.getWebSockets()) if (other !== ws) try { other.send(data) } catch { /* closing */ }
  }

  async webSocketClose(ws) {
    // workerd doesn't answer a close by itself at this compatibility date: answering it completes the PC's handshake.
    close(ws)
    this.end(ws, 1000, 'Done')
  }

  async webSocketError(ws) {
    close(ws)
    this.end(ws, 1011, 'Gone')
  }

  /**
   * A link is one exchange: when either side goes, so does the other. The other PC is told with 0x32 before its close,
   * since a close sent to the socket on the far side of an event can leave its connection open under workerd for a
   * while. Only open sockets are closed; a second close on one already closing never finishes.
   */
  end(cause, code, reason) {
    for (const ws of this.ctx.getWebSockets()) {
      if (ws.readyState !== OPEN) continue
      if (ws !== cause) try { ws.send(frame(GONE)) } catch { /* closing */ }
      close(ws, code, reason)
    }
  }

  accept(attachment) {
    const { 0: client, 1: server } = new WebSocketPair()
    this.ctx.acceptWebSocket(server)
    server.serializeAttachment(attachment)
    return { ws: server, response: new Response(null, { status: 101, webSocket: client }) }
  }
}

/** What a box key signs to log in: "exo-box-auth/1" || box id (16 raw bytes) || challenge (Mailbox.AuthMessage). */
export function authMessage(box, challenge) {
  return new Uint8Array([...AUTH_LABEL, ...hexBytes(box), ...challenge])
}

export async function signedIn(box, challenge, data) {
  const publicKey = data.slice(1, 66)
  const signature = data.slice(66, 130)
  if (publicKey[0] !== 4 || !lowS(signature)) return false
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', publicKey))
  if (hex(hash.subarray(0, 16)) !== box) return false
  try {
    const key = await crypto.subtle.importKey('raw', publicKey, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify'])
    return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, signature, authMessage(box, challenge))
  } catch {
    return false // not a point on the curve
  }
}

/**
 * s at most n / 2 (plan §2.13): every Exo signature is low-s, since (r, n - s) verifies too and WebCrypto takes either.
 */
function lowS(signature) {
  return BigInt('0x' + hex(signature.subarray(32, 64))) <= HALF_ORDER
}

/** A token bucket kept in the connection's attachment: [tokens, at]. False when it's empty. */
function take(bucket, [size, every], now) {
  const tokens = Math.min(size, bucket[0] + Math.floor((now - bucket[1]) / every))
  const at = tokens === size ? now : bucket[1] + Math.floor((now - bucket[1]) / every) * every
  if (tokens < 1) return false
  bucket[0] = tokens - 1
  bucket[1] = at
  return true
}

function randomNameplate() {
  // 4,200,000,000 is a multiple of 100,000,000: no number is likelier than another.
  let value
  do value = crypto.getRandomValues(new Uint32Array(1))[0]
  while (value >= 4_200_000_000)
  return String(value % 100_000_000).padStart(8, '0')
}

function close(ws, code = 1000, reason = 'Bye') {
  try { ws.close(code, reason) } catch { /* already closed */ }
}

function frame(kind, ...parts) {
  const out = new Uint8Array(1 + parts.reduce((sum, part) => sum + part.length, 0))
  out[0] = kind
  let at = 1
  for (const part of parts) {
    out.set(part, at)
    at += part.length
  }
  return out
}

function u32(value) {
  const out = new Uint8Array(4)
  new DataView(out.buffer).setUint32(0, value)
  return out
}

function readU32(data, at) {
  return new DataView(data.buffer, data.byteOffset).getUint32(at)
}

function hex(bytes) {
  return [...bytes].map(b => b.toString(16).padStart(2, '0')).join('')
}

function hexBytes(text) {
  return Uint8Array.from(text.match(/../g), b => parseInt(b, 16))
}
