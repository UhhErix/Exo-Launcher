import type { ReactNode } from 'react'
import { Popover } from '../ui'
import './magnetic-tools.css'

export function Overlay({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return (
    <Popover className="mt-sheet" label={title} onClose={onClose}>
      <div className="mt-sheet-head">
        <h2 className="m-panel-title">{title}</h2>
        <button type="button" className="m-link" onClick={onClose}>Close</button>
      </div>
      {children}
    </Popover>
  )
}

export function DepthMeter({ depth, scale, marks }: { depth: [number, number]; scale: number; marks?: [number, number] }) {
  return (
    <span className="mt-meters">
      {(['Left', 'Right'] as const).map((side, i) => (
        <span key={side} className="mt-meter" role="meter" aria-label={`${side} press depth`} aria-valuemin={0} aria-valuemax={scale} aria-valuenow={depth[i]}>
          <span>{side}</span>
          <span className="mt-meter-bar">
            {Array.from({ length: 10 }, (_, step) => (
              <i key={step} data-on={(step * scale) / 10 < depth[i]} data-mark={marks ? step === marks[i] : false} />
            ))}
          </span>
        </span>
      ))}
    </span>
  )
}
