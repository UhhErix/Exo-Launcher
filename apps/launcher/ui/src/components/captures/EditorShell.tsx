import { useEffect, type ComponentType, type ReactNode } from 'react'
import { Redo, ResetDefault, Undo, type IconProps } from '../../brand/icons'
import { Spinner } from '../ui/Spinner'

export type EditorTab<T extends string> = { id: T; label: string; icon: ComponentType<IconProps> }

/**
 * Captures' editor, for a snip and for a clip alike (Windows Photos' layout): the picture or clip on the left, one
 * inspector on the right with a tab per intent, Revert, Undo and Redo at the top left, Cancel and the one Save at the
 * top right. Ctrl+Z and Ctrl+Y (or Ctrl+Shift+Z) undo and redo; Escape is Cancel (taken ahead of the sheet's own).
 */
export function EditorShell<T extends string>({ label, tabs, tab, onTab, history, save, onCancel, media, children, typing = false }: {
  label: string
  tabs: readonly EditorTab<T>[]
  tab: T
  onTab: (tab: T) => void
  /** What Revert, Undo and Redo can do now; a clip's editor has Revert only. */
  history: { revert: (() => void) | null; undo?: (() => void) | null; redo?: (() => void) | null }
  save: { label: string; busy: boolean; busyLabel: string; disabled: boolean; run: () => void }
  onCancel: () => void
  media: ReactNode
  children: ReactNode
  /** A field in the editor has the keys (a text mark being typed). */
  typing?: boolean
}) {
  const { undo, redo } = history
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (typing || event.defaultPrevented) return
      if (event.target instanceof HTMLElement && event.target.closest('input, textarea')) return
      const key = event.key.toLowerCase()
      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        onCancel()
      } else if ((event.ctrlKey || event.metaKey) && (key === 'y' || (key === 'z' && event.shiftKey))) {
        event.preventDefault()
        redo?.()
      } else if ((event.ctrlKey || event.metaKey) && key === 'z') {
        event.preventDefault()
        undo?.()
      }
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [typing, onCancel, undo, redo])

  return (
    <div className="exo-editor exo-motion-switch" role="region" aria-label={label}>
      <div className="exo-editor-bar">
        <button type="button" className="exo-set-btn" disabled={!history.revert} onClick={() => history.revert?.()}><ResetDefault size={14} />Revert</button>
        {history.undo !== undefined ? (
          <>
            <button type="button" className="exo-set-btn exo-editor-icon" aria-label="Undo" aria-keyshortcuts="Control+Z" disabled={!undo} onClick={() => undo?.()}><Undo size={14} /></button>
            <button type="button" className="exo-set-btn exo-editor-icon" aria-label="Redo" aria-keyshortcuts="Control+Y" disabled={!redo} onClick={() => redo?.()}><Redo size={14} /></button>
          </>
        ) : null}
        <span className="exo-capture-actions-gap" />
        <button type="button" className="exo-set-btn" onClick={onCancel}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={save.disabled || save.busy} onClick={save.run}>
          {save.busy ? <Spinner label={save.busyLabel} /> : null}{save.label}
        </button>
      </div>
      <div className="exo-editor-body">
        <div className="exo-editor-media">{media}</div>
        <aside className="exo-editor-inspector" aria-label="Edit">
          <div className="exo-shelf-chips exo-editor-tabs" role="tablist" aria-label="Edit">
            {tabs.map(entry => (
              <button key={entry.id} type="button" role="tab" className="exo-shelf-chip" aria-selected={tab === entry.id} aria-pressed={tab === entry.id} onClick={() => onTab(entry.id)}>
                <entry.icon size={14} />{entry.label}
              </button>
            ))}
          </div>
          <div className="exo-editor-panel" role="tabpanel">{children}</div>
        </aside>
      </div>
    </div>
  )
}

/** A preset in the inspector: a tile in a tidy grid, its shape drawn above its name (Photos' templates). */
export function PresetTile({ label, detail, shape, pressed, busy, disabled, onPress }: {
  label: string
  detail?: string
  /** Width over height of the little frame drawn on the tile; none for a tile with no shape. */
  shape?: number | null
  pressed?: boolean
  busy?: boolean
  disabled?: boolean
  onPress: () => void
}) {
  return (
    <button type="button" className="exo-editor-tile" aria-pressed={pressed} disabled={disabled} onClick={onPress}>
      <span className="exo-editor-tile-shape" aria-hidden="true">
        {busy ? <Spinner label={`Saving ${label}`} /> : <span style={shape ? { aspectRatio: String(shape) } : undefined} className={shape ? undefined : 'is-free'} />}
      </span>
      <strong>{label}</strong>
      {detail ? <span>{detail}</span> : null}
    </button>
  )
}
