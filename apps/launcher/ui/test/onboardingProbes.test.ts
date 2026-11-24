import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import {
  isPartialGpuProfile,
  isSlowProbeError,
  onboardingProbeReportLevel,
  shouldRetryOnboardingProbe,
} from '../src/components/onboarding/model.ts'

const onboardingSource = readFileSync(new URL('../src/components/OnboardingPanel.tsx', import.meta.url), 'utf8')

test('slow onboarding probe timeouts log as warn and retry quietly once instead of filing UiError', () => {
  const timeoutError = new Error('Exo did not answer in time. Try again.')
  const driverSlow = new Error('Your graphics driver didn’t answer. Try again.')
  const canceled = new Error('The request was canceled or timed out.')

  for (const err of [timeoutError, driverSlow, canceled]) {
    assert.equal(isSlowProbeError(err), true)
    assert.equal(onboardingProbeReportLevel(err), 'warn')
    assert.equal(shouldRetryOnboardingProbe(err, 0), true)
    assert.equal(shouldRetryOnboardingProbe(err, 1), false)
  }
})

test('real onboarding probe failures still report at error level and do not retry as timeouts', () => {
  const realError = new Error('Unexpected null reference in handler')
  assert.equal(isSlowProbeError(realError), false)
  assert.equal(onboardingProbeReportLevel(realError), 'error')
  assert.equal(shouldRetryOnboardingProbe(realError, 0), false)
})

test('partial GPU profile from still-starting driver triggers quiet retry', () => {
  assert.equal(
    isPartialGpuProfile({ ok: false, status: 'none', message: 'Your graphics driver is still starting. Try again.' }),
    true,
  )
  assert.equal(isPartialGpuProfile({ ok: true, status: 'ready', message: null }), false)
  assert.equal(isPartialGpuProfile({ ok: false, status: 'manual', message: 'Your NVIDIA driver is being installed.' }), false)
})

test('OnboardingPanel routes gpuProfile and systemHealth through quiet retry and slow-probe warn level', () => {
  assert.match(onboardingSource, /probe\(\(\) => host\.gpuProfile\(\),\s*'OnboardingPanel\.gpuProfile',\s*setGpu,\s*isPartialGpuProfile\)/)
  assert.match(onboardingSource, /probe\(\(\) => host\.systemHealth\(\),\s*'OnboardingPanel\.systemHealth',\s*setHealth\)/)
  assert.match(onboardingSource, /onboardingProbeReportLevel\(error\)/)
  assert.match(onboardingSource, /shouldRetryOnboardingProbe\(error,\s*0\)/)
})
