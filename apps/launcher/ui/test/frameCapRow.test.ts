import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { buildFrameCapView } from '../src/components/game/frameCapWord.ts'
import type { GameFrameCap } from '../src/lib/host.ts'

function sampleCap(overrides: Partial<GameFrameCap> = {}): GameFrameCap {
  return {
    ok: true,
    message: null,
    gameId: 'steam:1091500',
    refreshHz: 144,
    vrr: true,
    inVrrRange: true,
    vrrFloorHz: 48,
    fgMultiplier: 1,
    fgRouteLabel: null,
    vendor: 'nvidia',
    adapter: 'RTX 3070',
    isHybrid: false,
    backend: 'nvidia_drs',
    backendLabel: 'NVIDIA Driver (DRS)',
    backendAvailable: true,
    backendNote: 'NVIDIA Driver (DRS)',
    backends: [
      {
        id: 'nvidia_drs',
        vendor: 'nvidia',
        label: 'NVIDIA Driver (DRS)',
        available: true,
        perGameNative: true,
        latencyNote: 'Low latency',
        antiCheatNote: 'Safe on every anti-cheat class',
      },
    ],
    selectedTier: 'max',
    activeCapFps: 138,
    targetOutputFps: 138,
    tiers: [
      { id: 'smooth', label: 'Smooth', capFps: 102, outputFps: 102, predictedVarianceMs2: 0.25, predictedStdDevMs: 0.5, predictedDipsPerMinute: 0.2, measured: true, summary: '1% low · 102 fps' },
      { id: 'balanced', label: 'Balanced', capFps: 120, outputFps: 120, predictedVarianceMs2: 0.9, predictedStdDevMs: 0.95, predictedDipsPerMinute: 2.5, measured: true, summary: '5% low · 120 fps' },
      { id: 'max', label: 'Max (VRR)', capFps: 138, outputFps: 138, predictedVarianceMs2: 2.25, predictedStdDevMs: 1.5, predictedDipsPerMinute: 10, measured: true, summary: 'VRR ceiling · 138 fps' },
    ],
    refreshLockedCaps: [],
    uncappedOption: null,
    suggestion: { tier: 'balanced', capFps: 120, reason: 'Balanced cuts predicted jitter to ±1.0 ms.' },
    stutter: { totalSpikes: 4, topCause: 'shader_compile', topCauseLabel: 'Shader compile', summary: 'Shader compile caused 4 spikes.', perProcessVramAvailable: false, breakdown: [] },
    ab: {
      gameId: 'steam:1091500',
      status: 'idle',
      capAFps: 0,
      capBFps: 0,
      currentArm: null,
      elapsedSeconds: 0,
      totalSeconds: 20,
      verdict: null,
      winner: null,
      deltaPercent: 0,
      noiseThresholdPercent: 3,
      reason: null,
      armA: null,
      armB: null,
    },
    word: '138 fps · NVIDIA Driver (DRS)',
    good: true,
    bad: false,
    ...overrides,
  }
}

describe('buildFrameCapView', () => {
  it('shows waiting state before data arrives', () => {
    const view = buildFrameCapView(null, null)
    assert.equal(view.waiting, true)
    assert.equal(view.triggerLabel, null)
  })

  it('builds tiers, suggestion, stutter diagnosis, and A/B trigger', () => {
    const view = buildFrameCapView(sampleCap(), null)
    assert.equal(view.triggerLabel, 'Max (VRR) · 138 fps')
    assert.equal(view.checkedId, 'tier:max')
    assert.ok(view.items.some(i => i.id === 'tier:smooth'))
    assert.ok(view.items.some(i => i.id === 'tier:balanced' && i.label.includes('(Suggested)')))
    assert.ok(view.items.some(i => i.id.startsWith('lab:run:138:120')))
    assert.ok(view.items.some(i => i.id === 'info:stutter' && i.label.includes('Shader compile')))
  })

  it('shows live A/B progress and cancel item while running', () => {
    const view = buildFrameCapView(sampleCap({
      ab: {
        gameId: 'steam:1091500',
        status: 'running',
        capAFps: 138,
        capBFps: 120,
        currentArm: 'B',
        elapsedSeconds: 8,
        totalSeconds: 20,
        verdict: null,
        winner: null,
        deltaPercent: 0,
        noiseThresholdPercent: 3,
        reason: null,
        armA: null,
        armB: null,
      },
    }), null)
    assert.equal(view.triggerLabel, 'A/B · 8/20s')
    assert.equal(view.word?.working, true)
    assert.ok(view.items.some(i => i.id === 'lab:cancel'))
  })

  it('shows completed A/B verdict and VRR-off uncapped option', () => {
    const view = buildFrameCapView(sampleCap({
      vrr: false,
      inVrrRange: false,
      refreshLockedCaps: [144, 72],
      uncappedOption: { id: 'uncapped', label: 'Uncapped (NVIDIA Reflex)', capFps: null, outputFps: null, predictedVarianceMs2: 3, predictedStdDevMs: 1.7, predictedDipsPerMinute: 0, measured: false, summary: 'Run uncapped' },
      ab: {
        gameId: 'steam:1091500',
        status: 'completed',
        capAFps: 138,
        capBFps: 120,
        currentArm: null,
        elapsedSeconds: 20,
        totalSeconds: 20,
        verdict: 'B is smoother by 14.2%',
        winner: 'b',
        deltaPercent: 14.2,
        noiseThresholdPercent: 3,
        reason: null,
        armA: null,
        armB: null,
      },
    }), null)
    assert.ok(view.word?.text.includes('B is smoother by 14.2%'))
    assert.ok(view.items.some(i => i.id === 'tier:uncapped'))
    assert.ok(view.items.some(i => i.id === 'refresh:72'))
  })
})
