import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')
const controllerPage = read('../src/components/devices/ControllerPage.tsx')
const devicePage = read('../src/components/devices/DevicePage.tsx')

test('ControllerPage renders DevicePage directly without a display:contents wrapper while preserving data-controller-lab', () => {
  assert.doesNotMatch(controllerPage, /className="contents"/)
  assert.match(controllerPage, /<DevicePage\b[^>]*\bcontrollerLab\b/)
  assert.match(devicePage, /data-controller-lab=\{controllerLab \? 'active' : undefined\}/)
})

test('DevicePage resolves its scrollable room past any display:contents ancestor', () => {
  assert.match(devicePage, /function roomOf\(/)
  assert.match(devicePage, /display === 'contents'/)
  assert.match(devicePage, /const room = roomOf\(p\)/)
})
