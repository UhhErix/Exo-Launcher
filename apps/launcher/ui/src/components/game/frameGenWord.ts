import type { FrameGenMod, FrameGenRoute, GameFrameGen } from '../../lib/host'
import type { LineWord } from './GameLine'
import type { MenuItem } from '../ui/Menu'

/** The page's own apostrophe (’) in words the host sends with the straight one, so one line never mixes the two. */
const curly = (text: string) => text.replace(/'/g, '’')

/**
 * The Frame generation line's word for Exo's own mod: why it is broken, what Exo did by itself, else the mod and
 * release in the game. One plain sentence each, never a file name: another mod over Exo's loader is named when Exo
 * knows it.
 */
function brokenWord(state: FrameGenMod): LineWord {
  if (state.reason === 'replaced') return { text: `${curly(state.mod ?? 'Another mod')} replaced Exo’s loader`, bad: true }
  if (state.reason === 'changed') return { text: 'Exo’s files were changed', bad: true }
  if (state.reason === 'gone') return { text: 'This game no longer has DLSS Frame Generation', bad: true }
  return { text: 'Exo’s files were removed', bad: true }
}

export function frameGenWord(state: FrameGenMod): LineWord | null {
  if (state.state === 'hidden') return { text: state.reason === 'unread' ? 'Could not check' : 'Not supported in this game' }
  if (state.state === 'native') return { text: state.package ? `Built into the game · ${curly(state.package)}` : 'Built into the game' }
  if (state.state === 'mod') return { text: curly(state.mod ?? 'A mod') }
  if (state.state === 'note') return { text: curly(state.note ?? '') }
  if (state.state === 'broken') return brokenWord(state)
  if (state.note) return { text: curly(state.note) }
  if (state.state === 'off') {
    return { text: state.package ? `Off · ${curly(state.package)}` : 'Off' }
  }
  if (state.state === 'on') {
    const label = state.version ?? state.package
    return label ? { text: curly(label) } : null
  }
  return null
}

const TIER_HINTS: Record<string, string | undefined> = {
  native: 'Built in',
  mod: 'Mod',
  driver: 'Driver',
}

const XESS_LABELS: Record<string, string> = {
  game: 'Game’s choice',
  '2x': '2x',
  '3x': '3x',
  '4x': '4x',
}

export function routeShortLabel(route: FrameGenRoute | undefined): string {
  if (!route) return 'Unavailable'
  switch (route.kind) {
    case 'native_dlss': return 'Built in · DLSS'
    case 'native_fsr': return 'Built in · FSR 3'
    case 'native_xess': return 'Built in · XeSS'
    case 'mod_optiscaler': return route.label.includes('OptiScaler FG') ? 'OptiScaler FG' : 'OptiScaler'
    case 'mod_dlssg': return 'DLSSG SM86'
    case 'mod_mfg': return 'MFG Unlocker'
    case 'mod_other': return curly(route.label)
    case 'driver_smooth_motion': return route.active ? 'Smooth Motion' : 'Smooth Motion · Off'
    case 'driver_afmf': return route.active ? 'AFMF' : 'AFMF · Off'
    case 'driver_xess_override': return route.choiceValue && route.choiceValue !== 'game' ? `XeSS ${route.choiceValue}` : 'XeSS override'
    default: return 'Not available'
  }
}

export type FrameGenViewModel = {
  word: LineWord | null
  waiting: boolean
  triggerLabel: string | null
  items: MenuItem[]
  checkedId: string | undefined
}

function buildRouteMenuItems(routes: FrameGenRoute[], selectableRoutes: FrameGenRoute[]): MenuItem[] {
  const items: MenuItem[] = []
  for (const route of routes) {
    if (route.kind === 'driver_xess_override') continue
    items.push({
      id: `route:${route.id}`,
      label: curly(route.label),
      section: 'Routes',
      disabled: !route.selectable,
      hint: TIER_HINTS[route.tier],
    })
  }
  if (selectableRoutes.some(r => r.tier !== 'native')) {
    items.push({ id: 'route:off', label: 'Off', section: 'Routes' })
  }
  return items
}

function buildXessMenuItems(xessRoute: FrameGenRoute | undefined): MenuItem[] {
  if (!xessRoute?.choices?.length) return []
  const current = xessRoute.choiceValue ?? 'game'
  return xessRoute.choices.map(choice => ({
    id: `xess:${choice}`,
    label: XESS_LABELS[choice] ?? choice,
    section: 'XeSS override',
    on: current === choice,
  }))
}

function buildDetailMenuItems(selected: FrameGenRoute | undefined, how: string | null | undefined): MenuItem[] {
  const items: MenuItem[] = []
  if (selected?.latencyNote) {
    items.push({ id: 'info:latency', label: curly(selected.latencyNote), section: 'Details', disabled: true })
  }
  if (selected?.antiCheatNote) {
    items.push({ id: 'info:anticheat', label: curly(selected.antiCheatNote), section: 'Details', disabled: true })
  }
  if (how) {
    items.push({ id: 'info:how', label: curly(how), section: 'How to enable', disabled: true })
  }
  return items
}

function resolveWord(state: GameFrameGen, selected: FrameGenRoute | undefined, error: string | null): LineWord {
  if (error) return { text: curly(error), bad: true }
  if (!state.word && state.legacyMod) {
    const legacy = frameGenWord(state.legacyMod)
    if (legacy) return legacy
  }
  return {
    text: curly(state.word || selected?.summary || 'Not available'),
    good: state.good || undefined,
    bad: state.bad || undefined,
  }
}

export function buildFrameGenView(state: GameFrameGen | null, error: string | null): FrameGenViewModel {
  if (!state) {
    return {
      word: error ? { text: curly(error), bad: true } : null,
      waiting: !error,
      triggerLabel: null,
      items: [],
      checkedId: undefined,
    }
  }

  if (!state.ok && state.routes.length === 0) {
    return {
      word: { text: curly(error ?? state.message ?? 'Could not check'), bad: true },
      waiting: false,
      triggerLabel: null,
      items: [],
      checkedId: undefined,
    }
  }

  const routes = state.routes
  const selected = routes.find(r => r.id === state.selectedRouteId) ?? routes[0]
  const selectableRoutes = routes.filter(r => r.selectable)
  const xessRoute = routes.find(r => r.kind === 'driver_xess_override')
  const how = state.howToEnable ?? selected?.howToEnable

  const items = [
    ...buildRouteMenuItems(routes, selectableRoutes),
    ...buildXessMenuItems(xessRoute),
    ...buildDetailMenuItems(selected, how),
  ]
  const checkedId = selected?.selectable ? `route:${selected.id}` : undefined
  const showTrigger = items.length > 0 && (selectableRoutes.length > 0 || Boolean(xessRoute) || Boolean(how))

  return {
    word: resolveWord(state, selected, error),
    waiting: false,
    triggerLabel: showTrigger ? routeShortLabel(selected) : null,
    items,
    checkedId,
  }
}
