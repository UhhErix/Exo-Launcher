import { useRef, useState } from 'react'
import type { GpuFanPoint, GpuTuningRange } from '../../lib/host'
import { Choice } from '../settings/Choice'
import { Switch } from '../Switch'

const WIDTH = 420
const HEIGHT = 160
const PAD_L = 40
const PAD_R = 14
const PAD_T = 14
const PAD_B = 26

export function FanCurveEditor({
  points,
  tempRange,
  speedRange,
  fanCount,
  selectedFan,
  onSelectFan,
  supportsZeroRpm,
  zeroRpm,
  onZeroRpm,
  needsResident,
  disabled,
  onChange,
}: {
  points: readonly GpuFanPoint[]
  tempRange: GpuTuningRange
  speedRange: GpuTuningRange
  fanCount: number
  selectedFan: number
  onSelectFan: (fanIndex: number) => void
  supportsZeroRpm: boolean
  zeroRpm: boolean
  onZeroRpm: (next: boolean) => void
  needsResident: boolean
  disabled?: boolean
  onChange: (next: GpuFanPoint[]) => void
}) {
  const svgRef = useRef<SVGSVGElement | null>(null)
  const [selected, setSelected] = useState(0)
  const [dragging, setDragging] = useState<number | null>(null)

  if (points.length === 0) return null

  const minT = tempRange.supported ? tempRange.min : 30
  const maxT = tempRange.supported ? tempRange.max : 100
  const minS = speedRange.supported ? speedRange.min : 0
  const maxS = speedRange.supported ? speedRange.max : 100
  const plotW = WIDTH - PAD_L - PAD_R
  const plotH = HEIGHT - PAD_T - PAD_B

  const xForT = (t: number) => PAD_L + ((t - minT) / Math.max(1, maxT - minT)) * plotW
  const yForS = (s: number) => PAD_T + plotH - ((s - minS) / Math.max(1, maxS - minS)) * plotH
  const tForX = (x: number) => Math.round(minT + ((x - PAD_L) / Math.max(1, plotW)) * (maxT - minT))
  const sForY = (y: number) => Math.round(minS + ((PAD_T + plotH - y) / Math.max(1, plotH)) * (maxS - minS))

  const updatePoint = (idx: number, rawTemp: number, rawSpeed: number) => {
    const prev = idx > 0 ? points[idx - 1] : null
    const next = idx < points.length - 1 ? points[idx + 1] : null
    const loT = prev ? prev.tempC + 1 : minT
    const hiT = next ? next.tempC - 1 : maxT
    const loS = prev ? prev.speedPercent : minS
    const hiS = next ? next.speedPercent : maxS
    const clampedT = Math.min(hiT, Math.max(loT, rawTemp))
    const clampedS = Math.min(hiS, Math.max(loS, rawSpeed))
    onChange(points.map((pt, i) => (i === idx ? { tempC: clampedT, speedPercent: clampedS } : pt)))
  }

  const fanChoices = Array.from({ length: Math.max(1, fanCount) }, (_, idx) => ({
    value: String(idx),
    label: `Fan ${idx + 1}`,
  }))

  const path = points
    .map((pt, i) => `${i === 0 ? 'M' : 'L'}${xForT(pt.tempC).toFixed(1)},${yForS(pt.speedPercent).toFixed(1)}`)
    .join(' ')

  const activePt = points[Math.min(selected, points.length - 1)]

  return (
    <div className="exo-gpu-curve">
      <div className="exo-gpu-curve-head">
        <span className="exo-gpu-curve-title">Fan curve</span>
        <div className="exo-gpu-curve-meta">
          <span>{activePt.tempC} °C → {activePt.speedPercent}%</span>
          {fanCount > 1 ? (
            <Choice
              label="GPU fan channel"
              value={String(selectedFan)}
              choices={fanChoices}
              disabled={disabled}
              onChange={value => onSelectFan(Number(value))}
            />
          ) : null}
          {supportsZeroRpm ? (
            <span className="exo-gpu-zero-rpm">
              <span>Zero RPM</span>
              <Switch checked={zeroRpm} disabled={disabled} label="Zero RPM" onChange={onZeroRpm} />
            </span>
          ) : null}
        </div>
      </div>

      <svg
        ref={svgRef}
        className="exo-gpu-curve-svg"
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label="GPU fan curve editor"
        onPointerMove={event => {
          if (dragging === null || disabled || !svgRef.current) return
          const rect = svgRef.current.getBoundingClientRect()
          const svgX = ((event.clientX - rect.left) / Math.max(1, rect.width)) * WIDTH
          const svgY = ((event.clientY - rect.top) / Math.max(1, rect.height)) * HEIGHT
          updatePoint(dragging, tForX(svgX), sForY(svgY))
        }}
        onPointerUp={event => {
          if (dragging === null) return
          event.currentTarget.releasePointerCapture(event.pointerId)
          setDragging(null)
        }}
        onPointerCancel={() => setDragging(null)}
      >
        {[0, 25, 50, 75, 100].map(pct => {
          const y = yForS(pct)
          return (
            <g key={pct}>
              <line x1={PAD_L} y1={y} x2={WIDTH - PAD_R} y2={y} className="exo-gpu-grid-line" />
              <text x={PAD_L - 6} y={y + 4} textAnchor="end" className="exo-gpu-axis-label">{pct}%</text>
            </g>
          )
        })}

        <path d={path} className="exo-gpu-curve-line" />

        {points.map((pt, idx) => {
          const cx = xForT(pt.tempC)
          const cy = yForS(pt.speedPercent)
          const isSelected = idx === selected
          return (
            <g key={idx}>
              <circle
                cx={cx}
                cy={cy}
                r={isSelected ? 6 : 4.5}
                className={isSelected ? 'exo-gpu-curve-dot is-selected' : 'exo-gpu-curve-dot'}
                tabIndex={disabled ? -1 : 0}
                role="slider"
                aria-label={`Fan point ${idx + 1} at ${pt.tempC} degrees`}
                aria-valuemin={minS}
                aria-valuemax={maxS}
                aria-valuenow={pt.speedPercent}
                onFocus={() => setSelected(idx)}
                onKeyDown={event => {
                  if (disabled) return
                  if (event.key === 'ArrowUp') { event.preventDefault(); updatePoint(idx, pt.tempC, pt.speedPercent + 5) }
                  else if (event.key === 'ArrowDown') { event.preventDefault(); updatePoint(idx, pt.tempC, pt.speedPercent - 5) }
                  else if (event.key === 'ArrowRight') { event.preventDefault(); updatePoint(idx, pt.tempC + 2, pt.speedPercent) }
                  else if (event.key === 'ArrowLeft') { event.preventDefault(); updatePoint(idx, pt.tempC - 2, pt.speedPercent) }
                }}
                onPointerDown={event => {
                  if (disabled) return
                  event.stopPropagation()
                  setSelected(idx)
                  setDragging(idx)
                  svgRef.current?.setPointerCapture(event.pointerId)
                }}
              />
              <text x={cx} y={HEIGHT - 6} textAnchor="middle" className="exo-gpu-axis-label">
                {pt.tempC}°
              </text>
            </g>
          )
        })}
      </svg>

      {needsResident ? (
        <p className="exo-gpu-note">
          Software fan curves stay active while Exo is open; automatic fan control restores when Exo closes.
        </p>
      ) : null}
    </div>
  )
}
