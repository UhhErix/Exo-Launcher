// The game page's Upscalers line (src/lib/upscalers.ts upscalerLineWord). `npm test` (Node's runner).
//
// How it could fail: the line names an upscaler mod (OptiScaler, from Exo's own frame generation or a third-party build)
// instead of saying whether the game's DLSS, FSR and XeSS files are up to date; up to date says nothing, so the line
// vanishes; a mod's file counts as an update or hides one; a game without upscalers or with anti-cheat reads as up to date.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { DlssStatus, DlssStatusItem } from '../src/lib/host.ts'
import { upscalerLineWord, upscalerView } from '../src/lib/upscalers.ts'

const dlss = (state: DlssStatusItem['state'], currentVersion: string, latestVersion = '310.9.16'): DlssStatusItem =>
  ({ key: 'nvngx_dlss.dll', fileName: 'nvngx_dlss.dll', displayName: 'DLSS Super Resolution', currentVersion, latestVersion, state, canUpdate: state === 'outdated' }) as DlssStatusItem
const optiFsr: DlssStatusItem = ({ key: 'OptiScaler/amd_fidelityfx_dx12.dll', fileName: 'amd_fidelityfx_dx12.dll', displayName: 'FSR 3.1', currentVersion: '1.0.1', state: 'mod', mod: 'OptiScaler' }) as DlssStatusItem
const word = (status: Partial<DlssStatus>) => upscalerLineWord(upscalerView({ ok: true, ...status } as DlssStatus), null)

test('up to date says so, with the version, and never the mod beside it', () => {
  const line = word({ items: [dlss('current', '310.9.16'), optiFsr] })
  assert.equal(line.text, 'Up to date: DLSS 310.9.16')
  assert.equal(line.good, true)
  assert.doesNotMatch(line.text, /OptiScaler/)
})

test('an outdated game file is an update, whatever mod is in the game', () => {
  assert.equal(word({ items: [dlss('outdated', '310.2.1'), optiFsr] }).text, '1 update')
})

test('only a mod\'s files: Exo leaves them, and the line does not name the mod', () => {
  const line = word({ items: [optiFsr] })
  assert.equal(line.text, 'Not updated: a mod owns these files')
  assert.doesNotMatch(line.text, /OptiScaler/)
})

test('a game without upscalers says so instead of vanishing', () => {
  assert.equal(word({ items: [] }).text, 'Not supported in this game')
})

test('anti-cheat without consent names the anti-cheat', () => {
  assert.equal(word({ items: [dlss('outdated', '310.2.1')], antiCheat: 'Vanguard', consent: 'never' }).text, 'Uses Vanguard')
})

test('an unread version is never up to date, even beside a current one', () => {
  // XeSS whose version Exo can't read and can't update (an image it can't parse): no update waits, but it isn't current.
  const xess = ({ key: 'libxess.dll', fileName: 'libxess.dll', displayName: 'XeSS Super Resolution', currentVersion: null, state: 'unknown', canUpdate: false }) as DlssStatusItem
  const line = word({ items: [dlss('current', '310.9.16'), xess] })
  assert.equal(line.text, 'Version unknown: XeSS')
  assert.notEqual(line.good, true)
  assert.equal(word({ items: [xess] }).text, 'Version unknown')
})

test('a read that failed is the line\'s word, in red', () => {
  assert.deepEqual(upscalerLineWord(upscalerView(null), 'Could not check the files'), { text: 'Could not check the files', bad: true })
})
