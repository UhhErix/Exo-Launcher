import { useEffect, useState } from 'react'
import { gamepadLabels, type GamepadKind } from '../../lib/gamepadMath'
import {
  getGamepadModality,
  getGamepadPaused,
  onGamepadModalityChange,
  onGamepadPausedChange,
} from '../../lib/motionPreference'
import type { View } from './TitleBar'

type Hint = { keys: string; label: string }

function hintsFor(kind: GamepadKind, view: View, overlayOpen: boolean): Hint[] {
  const g = gamepadLabels(kind)
  if (overlayOpen) {
    return [
      { keys: g.confirm, label: 'Select' },
      { keys: g.action, label: 'Play' },
      { keys: g.back, label: 'Back' },
      { keys: g.view, label: 'Search' },
      { keys: g.menu, label: 'Settings' },
    ]
  }
  if (view === 'settings') {
    return [
      { keys: `${g.prevTab}/${g.nextTab}`, label: 'Sections' },
      { keys: g.confirm, label: 'Select' },
      { keys: g.back, label: 'Close' },
      { keys: g.view, label: 'Search' },
    ]
  }
  return [
    { keys: `${g.prevRoom}/${g.nextRoom}`, label: 'Rooms' },
    { keys: g.confirm, label: view === 'devices' || view === 'library' ? 'Open' : 'Select' },
    { keys: g.back, label: 'Back' },
    { keys: g.view, label: 'Search' },
    { keys: g.menu, label: 'Settings' },
  ]
}

export function GamepadBar({ view, overlayOpen }: { view: View; overlayOpen: boolean }) {
  const [kind, setKind] = useState<GamepadKind | null>(getGamepadModality)
  const [paused, setPaused] = useState<boolean>(getGamepadPaused)
  useEffect(() => onGamepadModalityChange(setKind), [])
  useEffect(() => onGamepadPausedChange(setPaused), [])
  if (!kind) return null
  if (paused) {
    return (
      <aside className="exo-glass exo-gamepad-bar" aria-label="Controller shortcuts">
        <span className="exo-gamepad-hint">Controller Lab · navigation paused · Esc to exit</span>
      </aside>
    )
  }
  const hints = hintsFor(kind, view, overlayOpen)
  return (
    <aside className="exo-glass exo-gamepad-bar" aria-label="Controller shortcuts">
      {hints.map(hint => (
        <span key={hint.label} className="exo-gamepad-hint">
          <kbd className="exo-gamepad-key">{hint.keys}</kbd>
          <span>{hint.label}</span>
        </span>
      ))}
    </aside>
  )
}
