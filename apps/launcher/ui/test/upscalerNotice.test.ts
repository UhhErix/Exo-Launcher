// The upscaler update setting's words and the newer-upscaler notice (src/lib/upscalerNotice.ts). `npm test` (Node's runner).
//
// How it could fail: the setting's label or line drifts from what the owner asked for; a notice is shown when nothing is newer; the
// notice's text names the wrong family or repeats one; its button does not run the download; two families read as one.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { AUTO_UPSCALERS_DETAIL, AUTO_UPSCALERS_LABEL, newerNotice } from '../src/lib/upscalerNotice.ts'

test('the setting reads as the owner worded it, plainly', () => {
  assert.equal(AUTO_UPSCALERS_LABEL, 'Update DLSS, FSR and XeSS automatically')
  assert.equal(AUTO_UPSCALERS_DETAIL, 'Newer versions come from NVIDIA, AMD and Intel and go into your games by themselves, only if they signed them.')
})

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
