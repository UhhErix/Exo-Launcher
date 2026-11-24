import { useState, type CSSProperties } from 'react'
import type { KeyboardFormFactor } from '../../lib/host'
import { buildKeyboardLayout, type KeyCap } from './keyboardLayouts'

function KeyRow({
  keys,
  selectedKey,
  onSelect,
}: {
  keys: KeyCap[]
  selectedKey: string | null
  onSelect: (id: string) => void
}) {
  return (
    <div className="exo-kb-row">
      {keys.map(key => {
        if (key.kind === 'gap') {
          return (
            <span
              key={key.id}
              className="exo-kb-gap"
              style={{ '--u': key.u } as CSSProperties}
              aria-hidden="true"
            />
          )
        }
        if (key.kind === 'led-bar') {
          return (
            <button
              key={key.id}
              type="button"
              className="exo-kb-led-bar"
              aria-label={key.label}
              aria-pressed={selectedKey === key.id}
              style={{ '--u': key.u } as CSSProperties}
              onClick={() => onSelect(selectedKey === key.id ? '' : key.id)}
            >
              {Array.from({ length: 10 }, (_, i) => (
                <span key={i} className="exo-kb-led-seg" aria-hidden="true" />
              ))}
            </button>
          )
        }
        return (
          <button
            key={key.id}
            type="button"
            className="exo-kb-key"
            data-kind={key.kind ?? 'alpha'}
            aria-pressed={selectedKey === key.id}
            style={{ '--u': key.u } as CSSProperties}
            onClick={() => onSelect(selectedKey === key.id ? '' : key.id)}
          >
            <span>{key.label}</span>
          </button>
        )
      })}
    </div>
  )
}

function actuationTravelPct(actuationMm?: number | null, minMm = 0.1, maxMm = 4.0): number {
  if (actuationMm == null || maxMm <= minMm) return 0
  const clamped = Math.min(maxMm, Math.max(minMm, actuationMm))
  return Math.round(((clamped - minMm) / (maxMm - minMm)) * 100)
}

function keyNoteText(
  selected: KeyCap | null,
  profileName?: string | null,
  actuationMm?: number | null,
  rapidTrigger?: boolean | null,
  rapidTriggerMm?: number | null,
): string {
  if (!selected) {
    return 'Click any key to inspect active profile settings. Global actuation applies to all keys; open Wootility for per-key DKS, remaps, and RGB layers.'
  }
  if (selected.kind === 'led-bar') {
    return `${selected.note ?? selected.label}. Brightness follows the active profile lighting setting; configure reactive effects in Wootility.`
  }
  const mmText = actuationMm != null ? `${actuationMm.toFixed(1)} mm` : 'default'
  const rtText = rapidTrigger
    ? ` · Rapid Trigger ${rapidTriggerMm != null ? `${rapidTriggerMm.toFixed(2)} mm` : 'on'}`
    : ''
  const prefix = selected.note ? `${selected.note}` : selected.label
  return `${prefix} uses ${profileName ?? 'the active profile'} global actuation (${mmText}${rtText}). Open Wootility for per-key DKS and remaps.`
}

function KeyboardTelemetry({
  profileName,
  actuationMm,
  minActuationMm,
  maxActuationMm,
  rapidTrigger,
  rapidTriggerMm,
  tachyon,
  maxScanHz,
  rgbBrightness,
  selected,
}: {
  profileName?: string | null
  actuationMm?: number | null
  minActuationMm: number
  maxActuationMm: number
  rapidTrigger?: boolean | null
  rapidTriggerMm?: number | null
  tachyon?: boolean | null
  maxScanHz?: number | null
  rgbBrightness?: number | null
  selected: KeyCap | null
}) {
  const travelPct = actuationTravelPct(actuationMm, minActuationMm, maxActuationMm)
  const rtLabel = rapidTrigger
    ? `Rapid Trigger ${rapidTriggerMm != null ? `${rapidTriggerMm.toFixed(2)} mm` : 'On'}`
    : 'Rapid Trigger Off'
  const tachyonLabel = tachyon
    ? `Tachyon · ${(maxScanHz ?? 1000).toLocaleString()} Hz`
    : 'Tachyon Off'

  return (
    <div className="exo-kb-telemetry">
      <div className="exo-kb-telemetry-tags">
        {profileName ? <span className="exo-kb-tag is-strong">{profileName}</span> : null}
        {actuationMm != null ? <span className="exo-kb-tag">Actuation {actuationMm.toFixed(1)} mm</span> : null}
        {rapidTrigger != null ? (
          <span className={rapidTrigger ? 'exo-kb-tag is-good' : 'exo-kb-tag'}>{rtLabel}</span>
        ) : null}
        {tachyon != null ? (
          <span className={tachyon ? 'exo-kb-tag is-strong' : 'exo-kb-tag'}>{tachyonLabel}</span>
        ) : null}
        {rgbBrightness != null ? (
          <span className="exo-kb-tag">Brightness {rgbBrightness}%</span>
        ) : null}
      </div>
      {actuationMm != null ? (
        <div
          className="exo-kb-travel"
          aria-label={`Actuation travel ${actuationMm.toFixed(1)} of ${maxActuationMm.toFixed(1)} millimeters`}
        >
          <span className="exo-kb-travel-track">
            <span className="exo-kb-travel-fill" style={{ width: `${travelPct}%` }} />
          </span>
          <span className="exo-kb-travel-scale">
            {minActuationMm.toFixed(1)}–{maxActuationMm.toFixed(1)} mm
          </span>
        </div>
      ) : null}
      <p className="exo-kb-map-note">
        {keyNoteText(selected, profileName, actuationMm, rapidTrigger, rapidTriggerMm)}
      </p>
    </div>
  )
}

export function KeyboardMap({
  model,
  formFactor,
  layout,
  layoutCode,
  profileName,
  actuationMm,
  minActuationMm = 0.1,
  maxActuationMm = 4.0,
  rapidTrigger,
  rapidTriggerMm,
  tachyon,
  maxScanHz,
  rgbBrightness,
}: {
  model?: string | null
  formFactor?: KeyboardFormFactor | string | null
  layout?: string | null
  layoutCode?: string | null
  profileName?: string | null
  actuationMm?: number | null
  minActuationMm?: number
  maxActuationMm?: number
  rapidTrigger?: boolean | null
  rapidTriggerMm?: number | null
  tachyon?: boolean | null
  maxScanHz?: number | null
  rgbBrightness?: number | null
}) {
  const [selectedKeyId, setSelectedKeyId] = useState<string | null>(null)
  const spec = buildKeyboardLayout(formFactor, layoutCode, model, layout)
  const allKeys = [...spec.mainRows.flat(), ...spec.navRows.flat(), ...spec.numRows.flat()]
  const selected = allKeys.find(k => k.id === selectedKeyId) ?? null

  return (
    <div className="exo-kb-map-wrap">
      <div
        className="exo-kb-board"
        data-form={spec.form}
        data-layout={spec.layoutCode}
        data-rt={rapidTrigger ? 'on' : 'off'}
        data-tachyon={tachyon ? 'on' : 'off'}
      >
        <div className="exo-kb-cluster is-main">
          {spec.mainRows.map((row, index) => (
            <KeyRow
              key={index}
              keys={row}
              selectedKey={selectedKeyId}
              onSelect={id => setSelectedKeyId(id || null)}
            />
          ))}
        </div>
        {spec.navRows.length > 0 ? (
          <div className="exo-kb-cluster is-nav">
            {spec.navRows.map((row, index) => (
              <KeyRow
                key={index}
                keys={row}
                selectedKey={selectedKeyId}
                onSelect={id => setSelectedKeyId(id || null)}
              />
            ))}
          </div>
        ) : null}
        {spec.numRows.length > 0 ? (
          <div className="exo-kb-cluster is-num">
            {spec.numRows.map((row, index) => (
              <KeyRow
                key={index}
                keys={row}
                selectedKey={selectedKeyId}
                onSelect={id => setSelectedKeyId(id || null)}
              />
            ))}
          </div>
        ) : null}
      </div>

      <KeyboardTelemetry
        profileName={profileName}
        actuationMm={actuationMm}
        minActuationMm={minActuationMm}
        maxActuationMm={maxActuationMm}
        rapidTrigger={rapidTrigger}
        rapidTriggerMm={rapidTriggerMm}
        tachyon={tachyon}
        maxScanHz={maxScanHz}
        rgbBrightness={rgbBrightness}
        selected={selected}
      />
    </div>
  )
}
