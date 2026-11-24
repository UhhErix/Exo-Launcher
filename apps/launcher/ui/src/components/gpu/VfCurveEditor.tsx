import { useRef, useState } from 'react'
import type { GpuTuningRange, GpuVfPoint } from '../../lib/host'

const WIDTH = 420
const HEIGHT = 176
const PAD_L = 44
const PAD_R = 14
const PAD_T = 14
const PAD_B = 26

function clampOffset(value: number, range: GpuTuningRange): number {
  const clamped = Math.min(range.max, Math.max(range.min, value))
  const step = Math.max(1, range.step)
  return Math.min(range.max, Math.max(range.min, range.min + Math.round((clamped - range.min) / step) * step))
}

export function VfCurveEditor({
  points,
  offsetRange,
  isAmdCurveOffset,
  disabled,
  onChange,
}: {
  points: readonly GpuVfPoint[]
  offsetRange: GpuTuningRange
  isAmdCurveOffset: boolean
  disabled?: boolean
  onChange: (next: GpuVfPoint[]) => void
}) {
  const svgRef = useRef<SVGSVGElement | null>(null)
  const [selected, setSelected] = useState<number>(Math.max(0, Math.floor(points.length / 2)))
  const [dragging, setDragging] = useState<number | null>(null)

  if (points.length === 0) return null

  const minV = Math.min(...points.map(p => p.voltageMv))
  const maxV = Math.max(...points.map(p => p.voltageMv), minV + 50)
  const minF = Math.max(300, Math.min(...points.map(p => p.baseClockMhz)) - 200)
  const maxF = Math.max(...points.map(p => p.baseClockMhz + p.offsetMhz)) + 250

  const plotW = WIDTH - PAD_L - PAD_R
  const plotH = HEIGHT - PAD_T - PAD_B
  const xForV = (v: number) => PAD_L + ((v - minV) / Math.max(1, maxV - minV)) * plotW
  const yForF = (f: number) => PAD_T + plotH - ((f - minF) / Math.max(1, maxF - minF)) * plotH
  const fForY = (y: number) => minF + ((PAD_T + plotH - y) / Math.max(1, plotH)) * (maxF - minF)

  const basePath = points
    .map((p, i) => `${i === 0 ? 'M' : 'L'}${xForV(p.voltageMv).toFixed(1)},${yForF(p.baseClockMhz).toFixed(1)}`)
    .join(' ')

  const activePath = points
    .map((p, i) => `${i === 0 ? 'M' : 'L'}${xForV(p.voltageMv).toFixed(1)},${yForF(p.baseClockMhz + p.offsetMhz).toFixed(1)}`)
    .join(' ')

  const updatePointFromClientY = (idx: number, clientY: number) => {
    const svg = svgRef.current
    if (!svg) return
    const rect = svg.getBoundingClientRect()
    const svgY = ((clientY - rect.top) / Math.max(1, rect.height)) * HEIGHT
    const targetFreq = fForY(Math.min(PAD_T + plotH, Math.max(PAD_T, svgY)))
    const pt = points[idx]
    if (!pt) return
    const nextOffset = clampOffset(Math.round(targetFreq - pt.baseClockMhz), offsetRange)
    if (isAmdCurveOffset) {
      onChange(points.map(item => ({ ...item, offsetMhz: nextOffset })))
      return
    }
    onChange(points.map((item, i) => (i === idx ? { ...item, offsetMhz: nextOffset } : item)))
  }

  const flattenFromSelected = () => {
    const anchor = points[selected]
    if (!anchor) return
    const ceiling = anchor.baseClockMhz + anchor.offsetMhz
    onChange(
      points.map((pt, idx) => {
        if (idx < selected) return pt
        const desired = clampOffset(ceiling - pt.baseClockMhz, offsetRange)
        return { ...pt, offsetMhz: desired }
      }),
    )
  }

  const activePt = points[Math.min(selected, points.length - 1)]

  return (
    <div className="exo-gpu-curve">
      <div className="exo-gpu-curve-head">
        <span className="exo-gpu-curve-title">
          {isAmdCurveOffset ? 'Voltage-frequency curve offset' : 'Voltage-frequency curve'}
        </span>
        <div className="exo-gpu-curve-meta">
          <span>
            {activePt.voltageMv} mV · {activePt.baseClockMhz + activePt.offsetMhz} MHz (
            {activePt.offsetMhz >= 0 ? `+${activePt.offsetMhz}` : activePt.offsetMhz}
            {isAmdCurveOffset ? ' mV' : ' MHz'})
          </span>
          {!isAmdCurveOffset ? (
            <button
              type="button"
              className="exo-set-btn is-compact"
              disabled={disabled}
              onClick={flattenFromSelected}
            >
              Flatten right
            </button>
          ) : null}
        </div>
      </div>

      <svg
        ref={svgRef}
        className="exo-gpu-curve-svg"
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label="Voltage-frequency curve editor"
        onPointerMove={event => {
          if (dragging === null || disabled) return
          updatePointFromClientY(dragging, event.clientY)
        }}
        onPointerUp={event => {
          if (dragging === null) return
          event.currentTarget.releasePointerCapture(event.pointerId)
          setDragging(null)
        }}
        onPointerCancel={() => setDragging(null)}
      >
        {[0, 0.25, 0.5, 0.75, 1].map(frac => {
          const y = PAD_T + plotH * frac
          const freq = Math.round(maxF - frac * (maxF - minF))
          return (
            <g key={frac}>
              <line x1={PAD_L} y1={y} x2={WIDTH - PAD_R} y2={y} className="exo-gpu-grid-line" />
              <text x={PAD_L - 6} y={y + 4} textAnchor="end" className="exo-gpu-axis-label">{freq}</text>
            </g>
          )
        })}

        <path d={basePath} className="exo-gpu-curve-base" />
        <path d={activePath} className="exo-gpu-curve-line" />

        {points.map((pt, idx) => {
          const cx = xForV(pt.voltageMv)
          const cy = yForF(pt.baseClockMhz + pt.offsetMhz)
          const isSelected = idx === selected
          return (
            <g key={pt.index}>
              <circle
                cx={cx}
                cy={cy}
                r={isSelected ? 6 : 4.5}
                className={isSelected ? 'exo-gpu-curve-dot is-selected' : 'exo-gpu-curve-dot'}
                tabIndex={disabled ? -1 : 0}
                role="slider"
                aria-label={`${pt.voltageMv} mV clock offset`}
                aria-valuemin={offsetRange.min}
                aria-valuemax={offsetRange.max}
                aria-valuenow={pt.offsetMhz}
                onFocus={() => setSelected(idx)}
                onKeyDown={event => {
                  if (disabled) return
                  const step = Math.max(1, offsetRange.step)
                  if (event.key === 'ArrowUp' || event.key === 'ArrowRight') {
                    event.preventDefault()
                    const next = clampOffset(pt.offsetMhz + step, offsetRange)
                    onChange(points.map((item, i) => (isAmdCurveOffset || i === idx ? { ...item, offsetMhz: next } : item)))
                  } else if (event.key === 'ArrowDown' || event.key === 'ArrowLeft') {
                    event.preventDefault()
                    const next = clampOffset(pt.offsetMhz - step, offsetRange)
                    onChange(points.map((item, i) => (isAmdCurveOffset || i === idx ? { ...item, offsetMhz: next } : item)))
                  }
                }}
                onPointerDown={event => {
                  if (disabled) return
                  event.stopPropagation()
                  setSelected(idx)
                  setDragging(idx)
                  svgRef.current?.setPointerCapture(event.pointerId)
                }}
              />
              {idx === 0 || idx === points.length - 1 || idx === Math.floor(points.length / 2) ? (
                <text x={cx} y={HEIGHT - 6} textAnchor="middle" className="exo-gpu-axis-label">
                  {pt.voltageMv} mV
                </text>
              ) : null}
            </g>
          )
        })}
      </svg>
    </div>
  )
}
