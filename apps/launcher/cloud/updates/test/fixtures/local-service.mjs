// The update address on this PC: the real src/worker.js, answering over http from a folder that stands in for the R2
// bucket (the same folder fixtures/fake-wrangler.mjs writes to, key for key). For publish-payload.test.mjs, and for a
// walk-through of a release and an update with nothing published (misc/updates/file-level-updates.md):
//   node test/fixtures/local-service.mjs <bucket folder> [port]      (EXO_LOCAL_TAMPER=<path> damages that one download)
// The newest-version answer is not kept here (Node has no edge cache), so a new latest.json is served at once.
import { createServer } from 'node:http'
import { createReadStream, existsSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { Readable } from 'node:stream'
import { fileURLToPath } from 'node:url'
import worker from '../../src/worker.js'

/** R2 as the Worker uses it (get with a byte range or a condition, head), over a folder. */
function folderMirror(root) {
  const find = (key) => {
    if (key.split('/').some((part) => part === '' || part === '.' || part === '..')) return null
    const path = join(root, ...key.split('/'))
    return existsSync(path) && statSync(path).isFile() ? path : null
  }
  const describe = (path) => {
    const stat = statSync(path)
    return {
      size: stat.size,
      httpEtag: `"${stat.size}-${Math.round(stat.mtimeMs)}"`,
      writeHttpMetadata: () => {},
      json: async () => JSON.parse(readFileSync(path, 'utf8')),
    }
  }
  return {
    head: async (key) => {
      const path = find(key)
      return path ? describe(path) : null
    },
    get: async (key, options = {}) => {
      const path = find(key)
      if (!path) return null
      const object = describe(path)
      if (options.onlyIf?.get?.('if-none-match') === object.httpEtag) return object
      const asked = options.range?.get?.('range')
      const m = asked ? /^bytes=(\d+)-(\d*)$/.exec(asked) : null
      if (m) {
        const offset = Number(m[1])
        const end = m[2] ? Math.min(Number(m[2]), object.size - 1) : object.size - 1
        return { ...object, range: { offset, length: end - offset + 1 }, body: Readable.toWeb(createReadStream(path, { start: offset, end })) }
      }
      return { ...object, body: Readable.toWeb(createReadStream(path)) }
    },
  }
}

/**
 * Serves `bucket` on 127.0.0.1. `tamper(pathname)` may return other bytes for a GET (a test's damaged download).
 * Resolves to { url, requests, close }; `requests` lists every "<METHOD> <path>" answered, with its status and bytes sent.
 */
export async function serve(bucket, { port = 0, tamper = null } = {}) {
  const env = { MIRROR: folderMirror(bucket) }
  const requests = []
  const server = createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`)
    let sent = 0
    let status = 500
    try {
      const headers = new Headers()
      for (const [name, value] of Object.entries(req.headers)) if (typeof value === 'string') headers.set(name, value)
      const response = await worker.fetch(new Request(url, { method: req.method, headers }), env, { waitUntil() {} })
      status = response.status
      const other = req.method === 'GET' && response.status === 200 && tamper ? tamper(url.pathname) : null
      const out = Object.fromEntries(response.headers)
      if (other) out['content-length'] = String(other.length)
      res.writeHead(status, out)
      if (other) {
        sent = other.length
        res.end(other)
      } else if (response.body && req.method !== 'HEAD') {
        for await (const chunk of response.body) {
          sent += chunk.length
          res.write(chunk)
        }
        res.end()
      } else res.end()
    } catch (error) {
      console.error(error)
      if (!res.headersSent) res.writeHead(500)
      res.end('Internal server error')
    }
    requests.push({ method: req.method, path: url.pathname, status, bytes: sent })
  })
  await new Promise((resolve) => server.listen(port, '127.0.0.1', resolve))
  return { url: `http://127.0.0.1:${server.address().port}`, requests, close: () => new Promise((resolve) => server.close(resolve)) }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [bucket, port] = process.argv.slice(2)
  if (!bucket) {
    console.error('usage: node local-service.mjs <bucket folder> [port]')
    process.exit(2)
  }
  // EXO_LOCAL_TAMPER=<path>: that one path is answered with other bytes (a walk-through's damaged download).
  const damaged = process.env.EXO_LOCAL_TAMPER
  const service = await serve(bucket, { port: Number(port ?? 8787), tamper: damaged ? (path) => (path === damaged ? Buffer.from('damaged on the way') : null) : null })
  console.log(`exo-updates (local, from ${bucket}): ${service.url}`)
  // Each answered request on its own line, for a walk-through's byte counts.
  let shown = 0
  setInterval(() => {
    for (; shown < service.requests.length; shown++) {
      const r = service.requests[shown]
      console.log(`${r.method} ${r.path} ${r.status} ${r.bytes}`)
    }
  }, 200)
}
