/**
 * Runs the mailbox on this PC under the local stand-in for Cloudflare's runtime (runtime.mjs), on loopback only.
 *   node cloud/mailbox/local/server.mjs [--port 8788] [--evict] [--puts 20,30000] [--frames 60,2000]
 * Prints "mailbox listening <port>" once it takes connections, at ws://127.0.0.1:<port>/v1/box/<id> and /v1/link.
 * Storage is in memory and goes when it stops. --puts and --frames set the per-connection token buckets [size, ms per
 * token] (src/limits.js), so a client's pacing can be tested in seconds against buckets of the same shape.
 */
import { parseArgs } from 'node:util'
import { bindings, buckets, ratelimits } from './config.mjs'
import { serve } from './runtime.mjs'
import { limits } from '../src/limits.js'

const { values } = parseArgs({
  options: {
    port: { type: 'string', default: '8788' },
    evict: { type: 'boolean', default: false },
    puts: { type: 'string' },
    frames: { type: 'string' },
  },
})
const bucket = text => {
  const [size, every] = text.split(',').map(Number)
  if (!(size >= 1 && every >= 1)) throw new Error(`A bucket is size,ms: ${text}`)
  return [size, every]
}
if (values.puts) limits.puts = bucket(values.puts)
if (values.frames) limits.frames = bucket(values.frames)
const worker = await import('../src/worker.js')
const mailbox = await serve(worker, { port: Number(values.port), evict: values.evict, bindings, ratelimits, buckets, alarmEveryMs: 60_000 })
console.log(`mailbox listening ${mailbox.port}`)
process.on('SIGINT', () => mailbox.close().then(() => process.exit(0)))
process.stdin.on('end', () => mailbox.close().then(() => process.exit(0)))
process.stdin.resume()
