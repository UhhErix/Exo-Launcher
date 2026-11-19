// Every save of Exo's settings from the page goes through lib/settingsSave.ts (one order, only the newest answer taken).
//
// How it could fail: a new component calls host.setSettings (or the settings.set call) itself, so its answer can arrive
// after a newer save and put the old value back. This walks the page's sources for such a caller.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'

const src = join(import.meta.dirname, '..', 'src')
// The transport itself, the one path, and the in-browser stand-in host.
const allowed = new Set(['lib/host.ts', 'lib/settingsSave.ts', 'lib/hostMock.ts'])

function files(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? files(path) : /\.(ts|tsx)$/.test(name) ? [path] : []
  })
}

test('no page code saves settings except through settingsSave', () => {
  const direct = files(src)
    .map(path => relative(src, path).replaceAll('\\', '/'))
    .filter(path => !allowed.has(path))
    .filter(path => {
      const code = readFileSync(join(src, path), 'utf8').replace(/\/\/.*$/gm, '')
      return /\bhost\s*\.\s*setSettings\b|['"]settings\.set['"]/.test(code)
    })
  assert.deepEqual(direct, [])
})
