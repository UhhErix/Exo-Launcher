// Builds the vendored OpenMouse protocol library (vendor/openmouse-protocol, see UPSTREAM.md) into its dist/, which is
// what the package @openmouse/protocol (a file: dependency) resolves to. It uses Exo Mouse's own TypeScript (the native
// compiler its `tsc -b` runs) with the library's own tsconfig, and skips the build when dist is newer than every source
// file, so `check` and `build` stay quick after `npm ci`. --force builds anyway.
import { execFileSync } from 'node:child_process'
import { existsSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const mouse = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const lib = resolve(mouse, 'vendor/openmouse-protocol')
const stamp = resolve(lib, 'dist/index.js')

const newest = (dir) => readdirSync(dir, { withFileTypes: true }).reduce((latest, entry) => {
  const path = join(dir, entry.name)
  return Math.max(latest, entry.isDirectory() ? newest(path) : statSync(path).mtimeMs)
}, 0)

const sources = Math.max(newest(resolve(lib, 'src')), statSync(resolve(lib, 'tsconfig.json')).mtimeMs, statSync(resolve(lib, 'package.json')).mtimeMs)
if (existsSync(stamp) && statSync(stamp).mtimeMs >= sources && !process.argv.includes('--force')) {
  console.log('build-protocol: vendor/openmouse-protocol is built.')
} else {
  const tsc = resolve(mouse, 'node_modules/@typescript/native/bin/tsc')
  execFileSync(process.execPath, [tsc, '-p', resolve(lib, 'tsconfig.json')], { stdio: 'inherit' })
  console.log('build-protocol: built vendor/openmouse-protocol.')
}
