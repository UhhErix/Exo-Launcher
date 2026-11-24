import type { GraphicsFeatureRow } from '../../lib/host'
import type { LineWord } from './GameLine'

export function graphicsFeatureStateLabel(state: GraphicsFeatureRow['state']): string {
  if (state === 'on') return 'On'
  if (state === 'inGame') return 'Set in game'
  return 'Off'
}

export function graphicsFeatureWord(row: GraphicsFeatureRow, busy: boolean, error: string | null): LineWord {
  if (busy) return { text: row.state === 'on' ? 'Reverting' : 'Applying', working: true }
  if (error) return { text: error, bad: true }
  const status = graphicsFeatureStateLabel(row.state)
  const route = row.route.trim()
  const text = route ? `${status} · ${route}` : status
  return {
    text,
    good: row.state === 'on',
  }
}
