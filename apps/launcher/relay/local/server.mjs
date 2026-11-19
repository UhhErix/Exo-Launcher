/**
 * Runs the relay on this PC under the local stand-in for Cloudflare's runtime (runtime.mjs), on loopback only.
 *   node relay/local/server.mjs [--port 8787] [--evict]
 * Prints "relay listening <port>" once it takes connections, at ws://127.0.0.1:<port>/v1/<topic>.
 */
import { parseArgs } from 'node:util'
import { serve } from './runtime.mjs'

const { values } = parseArgs({ options: { port: { type: 'string', default: '8787' }, evict: { type: 'boolean', default: false } } })
const worker = await import('../src/worker.js')
const relay = await serve(worker, { port: Number(values.port), evict: values.evict })
console.log(`relay listening ${relay.port}`)
process.on('SIGINT', () => relay.close().then(() => process.exit(0)))
process.stdin.on('end', () => relay.close().then(() => process.exit(0)))
process.stdin.resume()
