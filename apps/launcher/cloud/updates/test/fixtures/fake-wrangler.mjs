// A stand-in for `wrangler r2 object put|get|delete <bucket>/<key> [--file f] --remote`, for publish.test.mjs: the bucket is a
// folder (FAKE_R2_DIR) and every command is logged in order (FAKE_R2_LOG). A missing key answers as wrangler does.
import { appendFileSync, copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

const [area, kind, verb, target, ...rest] = process.argv.slice(2)
const root = process.env.FAKE_R2_DIR
if (area !== 'r2' || kind !== 'object' || !root || !target) {
  console.error('fake wrangler: unsupported command')
  process.exit(2)
}
const key = target.slice(target.indexOf('/') + 1)
const path = join(root, ...key.split('/'))
const file = rest[rest.indexOf('--file') + 1]
appendFileSync(process.env.FAKE_R2_LOG, `${verb} ${key}\n`)
// FAKE_R2_GET_ERROR: every get fails as an outage or a sign-in problem would, never as a missing key.
if (verb === 'get' && process.env.FAKE_R2_GET_ERROR) {
  console.error('A request to the Cloudflare API failed. Authentication error [code: 10000]. Not found: the account.')
  process.exit(1)
}
// FAKE_R2_FAIL_PUT=<key>:<n>: the n-th put of that key fails (counted in the log).
const failPut = process.env.FAKE_R2_FAIL_PUT?.split(':')
if (verb === 'put' && failPut && failPut[0] === key) {
  const puts = readFileSync(process.env.FAKE_R2_LOG, 'utf8').split('\n').filter((line) => line === `put ${key}`).length
  if (puts === Number(failPut[1])) {
    console.error('upload failed')
    process.exit(1)
  }
}
if (verb === 'put') {
  mkdirSync(dirname(path), { recursive: true })
  copyFileSync(file, path)
} else if (verb === 'get') {
  if (!existsSync(path)) {
    // What wrangler 4.165.0 prints for a missing key, colour codes and all (and it leaves an empty --file behind).
    writeFileSync(file, '')
    console.error('\x1b[31mX \x1b[41;31m[\x1b[41;97mERROR\x1b[41;31m]\x1b[0m \x1b[1mThe specified key does not exist.\x1b[0m')
    process.exit(1)
  }
  copyFileSync(path, file)
} else if (verb === 'delete') {
  rmSync(path, { force: true })
} else {
  console.error('fake wrangler: unsupported verb')
  process.exit(2)
}
