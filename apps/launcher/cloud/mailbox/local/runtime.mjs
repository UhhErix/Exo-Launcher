/**
 * A local stand-in for the parts of Cloudflare's Workers runtime the mailbox uses, so the real worker.js runs under
 * plain Node (22.13+, no packages) for tests and local runs. It grows relay/local/runtime.mjs by what the mailbox
 * needs beyond the relay: several Durable Object classes, SQLite storage (ctx.storage.sql.exec through node:sqlite,
 * BLOBs back as ArrayBuffer as in workerd), alarms, deleteAll, and a rate-limit binding counted per key per period.
 * evict: an object is thrown away after every event and made again, as hibernation does, so state kept only in
 * memory shows up as a failure; its storage stays. Alarms fire when runAlarms() is called (tests move Date.now), and
 * once a minute when serving. The WebSocket server is RFC 6455 over node:http.
 */
import { createHash } from 'node:crypto'
import { createServer } from 'node:http'
import { DatabaseSync } from 'node:sqlite'
import { serialize } from 'node:v8'

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
const MAX_FRAME = 1 << 20

const NodeResponse = globalThis.Response
class CloudflareResponse extends NodeResponse {
  constructor(body, init = {}) {
    const upgrade = init.status === 101
    super(upgrade ? null : body, upgrade ? { status: 200 } : init)
    if (upgrade) {
      Object.defineProperty(this, 'status', { value: 101 })
      this.webSocket = init.webSocket
    }
  }
}
globalThis.Response = CloudflareResponse

/** The server end the Durable Object holds; its peer is the TCP connection once the 101 goes out. */
class ServerSocket {
  #attachment = null
  #queue = []
  #wire = null
  #closing = null
  closed = false

  get readyState() {
    return this.closed ? 3 : 1
  }

  send(data) {
    if (this.closed) throw new TypeError('WebSocket is closed')
    const bytes = typeof data === 'string' ? Buffer.from(data) : Buffer.from(ArrayBuffer.isView(data) ? data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength) : data)
    const opcode = typeof data === 'string' ? 1 : 2
    if (this.#wire) this.#wire.write(encode(opcode, bytes))
    else this.#queue.push([opcode, bytes])
  }

  close(code = 1000, reason = '') {
    if (this.closed) return
    this.closed = true
    const body = Buffer.alloc(2 + Buffer.byteLength(reason))
    body.writeUInt16BE(code)
    body.write(reason, 2)
    this.#closing = encode(8, body)
    this.#wire?.end(this.#closing)
    this.onClosed?.()
  }

  serializeAttachment(value) {
    if (serialize(value).length > 16384) throw new RangeError('Attachment over 16 KB')
    this.#attachment = structuredClone(value)
  }

  deserializeAttachment() {
    return this.#attachment === null ? null : structuredClone(this.#attachment)
  }

  bind(wire) {
    this.#wire = wire
    for (const [opcode, bytes] of this.#queue.splice(0)) wire.write(encode(opcode, bytes))
    if (this.#closing) wire.end(this.#closing)
  }
}

globalThis.WebSocketPair = class {
  constructor() {
    const server = new ServerSocket()
    this[0] = { server }
    this[1] = server
  }
}

/** ctx.storage.sql over node:sqlite, with workerd's cursor shape (toArray, one, iteration) and BLOBs as ArrayBuffer. */
function sqlStorage(entry) {
  return {
    exec(query, ...bindings) {
      const statement = entry.db.prepare(query)
      const args = bindings.map(value => value instanceof ArrayBuffer ? new Uint8Array(value) : value)
      const rows = statement.columns().length ? statement.all(...args) : (statement.run(...args), [])
      const out = rows.map(row => Object.fromEntries(Object.entries(row).map(([key, value]) =>
        [key, value instanceof Uint8Array ? value.buffer.slice(value.byteOffset, value.byteOffset + value.byteLength) : value])))
      return {
        toArray: () => out,
        one: () => {
          if (out.length !== 1) throw new Error(`Expected exactly one row, got ${out.length}`)
          return out[0]
        },
        [Symbol.iterator]: () => out[Symbol.iterator](),
      }
    },
  }
}

class Namespace {
  #objects = new Map()
  constructor(Class, env, evict) {
    Object.assign(this, { Class, env, evict })
  }

  idFromName(name) {
    return name
  }

  #entry(name) {
    let entry = this.#objects.get(name)
    if (!entry) this.#objects.set(name, entry = { sockets: new Set(), instance: null, db: new DatabaseSync(':memory:'), alarm: null })
    return entry
  }

  #wake(entry) {
    if (!entry.instance) {
      const ctx = {
        acceptWebSocket: ws => {
          entry.sockets.add(ws)
          ws.dispatch = (event, ...args) => this.#run(entry, instance => instance[event]?.(ws, ...args))
          ws.onClosed = () => entry.sockets.delete(ws)
        },
        getWebSockets: () => [...entry.sockets].filter(ws => !ws.closed),
        storage: {
          sql: sqlStorage(entry),
          getAlarm: async () => entry.alarm,
          setAlarm: async at => { entry.alarm = Number(at) },
          deleteAlarm: async () => { entry.alarm = null },
          deleteAll: async () => { entry.db.close(); entry.db = new DatabaseSync(':memory:') },
        },
      }
      entry.instance = new this.Class(ctx, this.env)
    }
    return entry.instance
  }

  async #run(entry, act) {
    try { return await act(this.#wake(entry)) } finally { if (this.evict) entry.instance = null }
  }

  get(name) {
    const entry = this.#entry(name)
    return { fetch: request => this.#run(entry, instance => instance.fetch(request)) }
  }

  /** Runs every alarm that is due by Date.now(). */
  async runAlarms() {
    for (const entry of this.#objects.values()) {
      if (entry.alarm === null || entry.alarm > Date.now()) continue
      entry.alarm = null
      await this.#run(entry, instance => instance.alarm?.())
    }
  }

  /** Rows in an object's table, for tests to see what's kept. */
  rows(name, table) {
    return this.#entry(name).db.prepare(`SELECT count(*) AS n FROM sqlite_master WHERE name = ?`).get(table).n
      ? this.#entry(name).db.prepare(`SELECT * FROM ${table} ORDER BY 1`).all()
      : []
  }
}

/** A rate limiter like Cloudflare's: limit per key per period seconds, counted in memory. */
function rateLimiter({ limit, period }) {
  const windows = new Map()
  return {
    async limit({ key }) {
      const now = Date.now()
      const window = windows.get(key)
      if (!window || now - window.start >= period * 1000) {
        windows.set(key, { start: now, count: 1 })
        return { success: true }
      }
      window.count++
      return { success: window.count <= limit }
    },
  }
}

/**
 * Starts the worker on a loopback port (0: any free one). bindings maps each Durable Object binding to its class
 * name; ratelimits each limiter binding to { limit, period }; buckets names each R2 binding, kept in memory.
 * Resolves to { port, env, runAlarms, close }.
 */
export async function serve(worker, { port = 0, evict = false, bindings = {}, ratelimits = {}, buckets = [], alarmEveryMs = 0 } = {}) {
  const env = {}
  for (const [binding, className] of Object.entries(bindings)) env[binding] = new Namespace(worker[className], env, evict)
  for (const [binding, config] of Object.entries(ratelimits)) env[binding] = rateLimiter(config)
  for (const binding of buckets) env[binding] = bucket()
  const runAlarms = async () => {
    for (const binding of Object.keys(bindings)) await env[binding].runAlarms()
  }
  const toRequest = req => new Request(`http://127.0.0.1${req.url}`, {
    headers: { ...req.headers, 'cf-connecting-ip': req.socket.remoteAddress ?? '' },
  })
  const server = createServer(async (req, res) => {
    const chunks = []
    for await (const chunk of req) chunks.push(chunk)
    const body = Buffer.concat(chunks)
    const request = new Request(`http://127.0.0.1${req.url}`, {
      method: req.method,
      headers: { ...req.headers, 'cf-connecting-ip': req.socket.remoteAddress ?? '' },
      body: req.method === 'GET' || req.method === 'HEAD' ? undefined : body,
    })
    const response = await worker.default.fetch(request, env)
    res.writeHead(response.status, Object.fromEntries(response.headers))
    res.end(Buffer.from(await response.arrayBuffer()))
  })
  const wires = new Set()
  server.on('upgrade', async (req, wire) => {
    wires.add(wire)
    wire.on('close', () => wires.delete(wire))
    const key = req.headers['sec-websocket-key']
    const response = await worker.default.fetch(toRequest(req), env)
    if (response.status !== 101 || !key) {
      const body = await response.text()
      wire.end(`HTTP/1.1 ${response.status} No\r\nContent-Length: ${Buffer.byteLength(body)}\r\nConnection: close\r\n\r\n${body}`)
      return
    }
    const accept = createHash('sha1').update(key + GUID).digest('base64')
    wire.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`)
    const ws = response.webSocket.server
    ws.bind(wire)
    listen(wire, ws)
  })
  await new Promise(resolve => server.listen(port, '127.0.0.1', resolve))
  const timer = alarmEveryMs ? setInterval(() => void runAlarms(), alarmEveryMs) : null
  timer?.unref()
  return {
    port: server.address().port,
    env,
    runAlarms,
    close: () => new Promise(resolve => {
      if (timer) clearInterval(timer)
      for (const wire of wires) wire.destroy()
      server.closeAllConnections()
      server.close(resolve)
    }),
  }
}

/** An R2 bucket in memory: put, get (its body), delete and list by prefix, as the Worker uses them. */
function bucket() {
  const objects = new Map()
  return {
    async put(key, value) { objects.set(key, Buffer.from(value)) },
    async get(key) {
      const value = objects.get(key)
      return value ? { body: new Uint8Array(value), size: value.length, arrayBuffer: async () => value.buffer.slice(value.byteOffset, value.byteOffset + value.length) } : null
    },
    async delete(key) { objects.delete(key) },
    async list({ prefix = '' } = {}) {
      return { objects: [...objects].filter(([key]) => key.startsWith(prefix)).map(([key, value]) => ({ key, size: value.length })), truncated: false }
    },
  }
}

function listen(wire, ws) {
  let buffer = Buffer.alloc(0)
  let parts = []
  let partOpcode = 0
  let ended = false
  const end = (code, reason, clean) => {
    if (ended) return
    ended = true
    const wasOpen = !ws.closed
    ws.closed = true
    ws.onClosed?.()
    if (wasOpen) void ws.dispatch('webSocketClose', code, reason, clean)
  }
  wire.on('data', chunk => {
    buffer = Buffer.concat([buffer, chunk])
    for (;;) {
      const frame = decode(buffer)
      if (frame === null) return
      if (frame === undefined) { wire.destroy(); return end(1009, '', false) }
      buffer = buffer.subarray(frame.size)
      const { fin, opcode, payload } = frame
      if (opcode === 9) { wire.write(encode(10, payload)); continue }
      if (opcode === 10) continue
      if (opcode === 8) {
        const code = payload.length >= 2 ? payload.readUInt16BE(0) : 1005
        if (!ws.closed) wire.end(encode(8, payload.subarray(0, 2)))
        return end(code, payload.subarray(2).toString(), true)
      }
      if (opcode === 0) parts.push(payload)
      else { parts = [payload]; partOpcode = opcode }
      if (!fin) continue
      const whole = Buffer.concat(parts)
      parts = []
      if (ws.closed) continue
      const message = partOpcode === 1 ? whole.toString() : whole.buffer.slice(whole.byteOffset, whole.byteOffset + whole.byteLength)
      void ws.dispatch('webSocketMessage', message)
    }
  })
  wire.on('close', () => end(1006, '', false))
  wire.on('error', () => end(1006, '', false))
}

function encode(opcode, payload) {
  const length = payload.length
  const head = length < 126 ? Buffer.from([0x80 | opcode, length])
    : length < 65536 ? Buffer.from([0x80 | opcode, 126, length >> 8, length & 255])
      : Buffer.concat([Buffer.from([0x80 | opcode, 127]), (() => { const b = Buffer.alloc(8); b.writeBigUInt64BE(BigInt(length)); return b })()])
  return Buffer.concat([head, payload])
}

/** One frame from the front of buffer: null when more bytes are needed, undefined when it is too big. */
function decode(buffer) {
  if (buffer.length < 2) return null
  const fin = (buffer[0] & 0x80) !== 0
  const opcode = buffer[0] & 0x0f
  const masked = (buffer[1] & 0x80) !== 0
  let length = buffer[1] & 0x7f
  let offset = 2
  if (length === 126) {
    if (buffer.length < 4) return null
    length = buffer.readUInt16BE(2)
    offset = 4
  } else if (length === 127) {
    if (buffer.length < 10) return null
    const big = buffer.readBigUInt64BE(2)
    if (big > BigInt(MAX_FRAME)) return undefined
    length = Number(big)
    offset = 10
  }
  if (length > MAX_FRAME) return undefined
  const mask = masked ? buffer.subarray(offset, offset + 4) : null
  if (masked) offset += 4
  if (buffer.length < offset + length) return null
  const payload = Buffer.from(buffer.subarray(offset, offset + length))
  if (mask) for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i & 3]
  return { fin, opcode, payload, size: offset + length }
}
