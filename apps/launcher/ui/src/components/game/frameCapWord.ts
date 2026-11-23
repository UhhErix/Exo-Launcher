import type { CapLabStatus, GameFrameCap } from '../../lib/host'
import type { MenuItem } from '../ui/Menu'
import type { LineWord } from './GameLine'

const curly = (text: string) => text.replace(/'/g, '’')

export type FrameCapViewModel = {
  word: LineWord | null
  waiting: boolean
  triggerLabel: string | null
  items: MenuItem[]
  checkedId: string | undefined
}

function tierTriggerLabel(state: GameFrameCap): string {
  if (state.ab.status === 'running') {
    return `A/B · ${state.ab.elapsedSeconds}/${state.ab.totalSeconds}s`
  }
  if (state.selectedTier === 'uncapped') return 'Uncapped'
  if (state.activeCapFps && state.activeCapFps > 0) {
    const tierName = state.tiers.find(t => t.id === state.selectedTier)?.label
    return tierName ? `${tierName} · ${state.activeCapFps} fps` : `${state.activeCapFps} fps`
  }
  return 'Off'
}

function resolveWord(state: GameFrameCap, error: string | null): LineWord {
  if (error) return { text: curly(error), bad: true }
  if (state.ab.status === 'running') {
    return {
      text: `A/B test ${state.ab.capAFps} vs ${state.ab.capBFps} fps · Arm ${state.ab.currentArm ?? 'A'}`,
      working: true,
    }
  }
  if (state.ab.status === 'completed' && state.ab.verdict) {
    return {
      text: curly(`${state.word} · ${state.ab.verdict}`),
      good: state.good || state.ab.winner === 'a' || state.ab.winner === 'b' || undefined,
    }
  }
  return {
    text: curly(state.word || 'Off'),
    good: state.good || undefined,
    bad: state.bad || undefined,
  }
}

function buildTierItems(state: GameFrameCap): MenuItem[] {
  const items: MenuItem[] = []
  for (const tier of state.tiers) {
    const isSuggested = state.suggestion.tier === tier.id
    const fpsHint = tier.capFps
      ? (state.fgMultiplier > 1 && tier.outputFps ? `${tier.capFps} base / ${tier.outputFps} out` : `${tier.capFps} fps`)
      : ''
    const stdHint = `±${tier.predictedStdDevMs.toFixed(1)} ms`
    items.push({
      id: `tier:${tier.id}`,
      label: curly(isSuggested ? `${tier.label} (Suggested)` : tier.label),
      section: 'Cap tiers',
      disabled: !state.backendAvailable,
      hint: [fpsHint, stdHint].filter(Boolean).join(' · '),
    })
  }
  if (!state.inVrrRange) {
    for (const fps of state.refreshLockedCaps) {
      if (state.tiers.some(t => t.capFps === fps)) continue
      items.push({
        id: `refresh:${fps}`,
        label: `Refresh lock · ${fps} fps`,
        section: 'Cap tiers',
        disabled: !state.backendAvailable,
        hint: `${Math.round(state.refreshHz)} Hz divisor`,
      })
    }
    if (state.uncappedOption) {
      items.push({
        id: 'tier:uncapped',
        label: curly(state.uncappedOption.label),
        section: 'Cap tiers',
        hint: 'Lowest lag',
      })
    }
  }
  items.push({ id: 'tier:off', label: 'Off', section: 'Cap tiers' })
  return items
}

function buildBackendItems(state: GameFrameCap): MenuItem[] {
  if (state.backends.length === 0) return []
  return state.backends.map(b => ({
    id: `backend:${b.id}`,
    label: curly(b.label),
    section: 'Limiter',
    disabled: !b.available,
    on: b.id === state.backend,
    hint: b.available ? (b.perGameNative ? 'Per-game' : 'Session') : 'Unavailable',
  }))
}

function buildLabItems(state: GameFrameCap): MenuItem[] {
  const items: MenuItem[] = []
  const ab: CapLabStatus = state.ab
  if (ab.status === 'running') {
    items.push({
      id: 'lab:cancel',
      label: `Cancel A/B test (${ab.elapsedSeconds}/${ab.totalSeconds}s)`,
      section: 'A/B cap lab',
    })
  } else if (state.backendAvailable && state.tiers.length >= 2) {
    const capA = state.activeCapFps ?? state.tiers[state.tiers.length - 1]?.capFps ?? 138
    const alt = state.tiers.find(t => t.id === 'balanced' && t.capFps !== capA)?.capFps
      ?? state.tiers.find(t => t.id === 'smooth' && t.capFps !== capA)?.capFps
      ?? Math.max(30, capA - 12)
    items.push({
      id: `lab:run:${capA}:${alt}`,
      label: `Run 20 s A/B test (${capA} vs ${alt} fps)`,
      section: 'A/B cap lab',
      hint: '5 s blocks',
    })
  }
  if (ab.status === 'completed' && ab.verdict) {
    items.push({
      id: 'info:ab-verdict',
      label: curly(ab.verdict),
      section: 'A/B cap lab',
      disabled: true,
    })
  } else if (ab.status === 'cancelled' && ab.reason) {
    items.push({
      id: 'info:ab-cancel',
      label: curly(ab.reason),
      section: 'A/B cap lab',
      disabled: true,
    })
  }
  return items
}

function buildDiagnosisItems(state: GameFrameCap): MenuItem[] {
  const items: MenuItem[] = []
  if (state.suggestion.reason) {
    items.push({
      id: 'info:suggest',
      label: curly(state.suggestion.reason),
      section: 'Exo AI suggestion',
      disabled: true,
    })
  }
  if (state.stutter.summary) {
    items.push({
      id: 'info:stutter',
      label: curly(state.stutter.summary),
      section: 'Stutter attribution',
      disabled: true,
    })
  }
  if (!state.backendAvailable && state.backendNote) {
    items.push({
      id: 'info:backend',
      label: curly(state.backendNote),
      section: 'Limiter note',
      disabled: true,
    })
  }
  return items
}

export function buildFrameCapView(state: GameFrameCap | null, error: string | null): FrameCapViewModel {
  if (!state) {
    return {
      word: error ? { text: curly(error), bad: true } : null,
      waiting: !error,
      triggerLabel: null,
      items: [],
      checkedId: undefined,
    }
  }

  if (!state.ok && state.tiers.length === 0) {
    return {
      word: { text: curly(error ?? state.message ?? 'Could not check'), bad: true },
      waiting: false,
      triggerLabel: null,
      items: [],
      checkedId: undefined,
    }
  }

  const items = [
    ...buildTierItems(state),
    ...buildBackendItems(state),
    ...buildLabItems(state),
    ...buildDiagnosisItems(state),
  ]

  let checkedId: string | undefined
  if (state.selectedTier === 'refresh' && state.activeCapFps) {
    checkedId = `refresh:${state.activeCapFps}`
  } else if (state.selectedTier) {
    checkedId = `tier:${state.selectedTier}`
  }

  return {
    word: resolveWord(state, error),
    waiting: false,
    triggerLabel: items.length > 0 ? tierTriggerLabel(state) : null,
    items,
    checkedId,
  }
}
