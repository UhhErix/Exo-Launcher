// The newer-upscaler notice (src/lib/upscalerNotice.ts). `npm test` (Node's runner).
//
// How it could fail: a notice is shown when nothing is newer; the notice's text names the wrong family or repeats one;
// its button does not run the download; two families read as one.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { newerNotice } from '../src/lib/upscalerNotice.ts'

test('nothing newer means no notice', () => {
  assert.equal(newerNotice([], () => assert.fail('no download without a notice')), null)
})

test('one newer family is one quiet notice whose button runs the download', () => {
  let pressed = 0
  const notice = newerNotice([{ family: 'DLSS', version: '310.9.17' }], () => { pressed += 1 })
  assert.ok(notice)
  assert.equal(notice.text, 'A newer DLSS is available')
  assert.equal(notice.tone, undefined, 'a quiet notice, not a good or bad one')
  assert.equal(notice.action?.label, 'Download')
  notice.action?.onPress()
  assert.equal(pressed, 1)
})

test('two families are one notice that names each once', () => {
  const notice = newerNotice([{ family: 'DLSS', version: '310.9.17' }, { family: 'XeSS', version: '2.0.2.69' }, { family: 'DLSS', version: '310.9.18' }], () => {})
  assert.equal(notice?.text, 'Newer DLSS, XeSS are available')
})
