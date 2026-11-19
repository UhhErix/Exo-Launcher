/**
 * Exo's relay: a Cloudflare Worker that forwards opaque messages between the Exo apps sharing a topic. Exo derives the
 * topic and the key that seals every message from a Discord call's own end-to-end encryption group (DAVE's MLS exporter)
 * or from two friends' keys (ECDH), so only they can read them; the relay never can, and it knows no account, user or
 * call. Nothing is logged or stored: each connection's last message lives in that connection's own attachment
 * and goes with it.
 *
 * GET /v1/<topic> (32 lowercase hex, WebSocket upgrade) joins a topic. Every binary message a connection sends replaces
 * its last one and is forwarded at once to the others; someone joining later gets each connection's last message.
 * To clients:
 *   0x01 conn:u32 age:u32 payload   a connection's message; age is how long the relay held it, in ms (0 when live)
 *   0x02 conn:u32                   that connection closed
 * conn is a random number per connection. Limits: 32 connections per topic, 2048-byte messages, 20 messages per 10 s
 * per connection (more closes it, 1008), text closes it (1003).
 */

const MAX_SOCKETS = 32
const MAX_BYTES = 2048
const RATE = 20
const WINDOW_MS = 10_000
const MESSAGE = 1
const GONE = 2

export default {
  async fetch(request, env) {
    const topic = /^\/v1\/([0-9a-f]{32})$/.exec(new URL(request.url).pathname)?.[1]
    if (!topic) return new Response('Not found', { status: 404 })
    if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket') return new Response('WebSocket only', { status: 426 })
    return env.TOPICS.get(env.TOPICS.idFromName(topic)).fetch(request)
  },
}

/** One topic: its connections, through the WebSocket Hibernation API, so a quiet call costs nothing. */
export class Topic {
  constructor(ctx) {
    this.ctx = ctx
  }

  async fetch() {
    const sockets = this.ctx.getWebSockets()
    if (sockets.length >= MAX_SOCKETS) return new Response('Full', { status: 429 })
    const taken = new Set(sockets.map(ws => ws.deserializeAttachment()?.conn))
    let conn
    do conn = crypto.getRandomValues(new Uint32Array(1))[0]
    while (taken.has(conn))
    const { 0: client, 1: server } = new WebSocketPair()
    this.ctx.acceptWebSocket(server)
    server.serializeAttachment({ conn, at: 0, data: null, sent: [] })
    const now = Date.now()
    for (const ws of sockets) {
      const kept = ws.deserializeAttachment()
      if (kept?.data) server.send(frame(MESSAGE, kept.conn, now - kept.at, kept.data))
    }
    return new Response(null, { status: 101, webSocket: client })
  }

  async webSocketMessage(ws, message) {
    if (typeof message === 'string') return this.gone(ws, 1003, 'Binary only')
    if (message.byteLength === 0 || message.byteLength > MAX_BYTES) return this.gone(ws, 1009, 'Too big')
    const kept = ws.deserializeAttachment()
    if (!kept) return this.gone(ws, 1011, 'Lost')
    const now = Date.now()
    const sent = kept.sent.filter(at => now - at < WINDOW_MS)
    if (sent.length >= RATE) return this.gone(ws, 1008, 'Too many')
    sent.push(now)
    ws.serializeAttachment({ conn: kept.conn, at: now, data: message, sent })
    this.others(ws, frame(MESSAGE, kept.conn, 0, message))
  }

  async webSocketClose(ws) {
    this.gone(ws)
  }

  async webSocketError(ws) {
    this.gone(ws)
  }

  /** The connection is done (it closed, or the relay closes it with code): the others are told. */
  gone(ws, code = 1000, reason = 'Bye') {
    const kept = ws.deserializeAttachment()
    if (kept) this.others(ws, frame(GONE, kept.conn))
    try { ws.close(code, reason) } catch { /* already closed */ }
  }

  others(ws, data) {
    for (const other of this.ctx.getWebSockets())
      if (other !== ws) try { other.send(data) } catch { /* closing; its own close handler tells the rest */ }
  }
}

function frame(kind, conn, age = 0, data = null) {
  const body = data ? new Uint8Array(data) : null
  const out = new Uint8Array(body ? 9 + body.length : 5)
  const view = new DataView(out.buffer)
  out[0] = kind
  view.setUint32(1, conn)
  if (body) {
    view.setUint32(5, Math.max(0, Math.min(age, 0xffffffff)))
    out.set(body, 9)
  }
  return out
}
