import { useState, type CSSProperties } from 'react'

type KeyCap = {
  id: string
  label: string
  u: number
  kind?: 'mod' | 'wasd' | 'space' | 'gap'
}

type FormFactor = '60' | '80' | 'full'

function detectFormFactor(model?: string | null): FormFactor {
  const m = (model ?? '').toLowerCase()
  if (m.includes('two') || m.includes('full') || m.includes('100')) return 'full'
  if (m.includes('80') || m.includes('tkl')) return '80'
  return '60'
}

function buildMainRows(splitSpace: boolean): KeyCap[][] {
  const row1: KeyCap[] = [
    { id: 'esc', label: 'Esc', u: 1, kind: 'mod' },
    ...['1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '='].map(k => ({ id: k, label: k, u: 1 })),
    { id: 'bksp', label: 'Bksp', u: 2, kind: 'mod' },
  ]
  const row2: KeyCap[] = [
    { id: 'tab', label: 'Tab', u: 1.5, kind: 'mod' },
    ...['Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P'].map(k => ({
      id: k,
      label: k,
      u: 1,
      kind: k === 'W' ? 'wasd' as const : undefined,
    })),
    { id: '[', label: '[', u: 1 },
    { id: ']', label: ']', u: 1 },
    { id: '\\', label: '\\', u: 1.5, kind: 'mod' },
  ]
  const row3: KeyCap[] = [
    { id: 'caps', label: 'Caps', u: 1.75, kind: 'mod' },
    ...['A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L'].map(k => ({
      id: k,
      label: k,
      u: 1,
      kind: k === 'A' || k === 'S' || k === 'D' ? 'wasd' as const : undefined,
    })),
    { id: ';', label: ';', u: 1 },
    { id: "'", label: "'", u: 1 },
    { id: 'enter', label: 'Enter', u: 2.25, kind: 'mod' },
  ]
  const row4: KeyCap[] = [
    { id: 'lshift', label: 'Shift', u: 2.25, kind: 'mod' },
    ...['Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/'].map(k => ({ id: k, label: k, u: 1 })),
    { id: 'rshift', label: 'Shift', u: 2.75, kind: 'mod' },
  ]
  const spaceKeys: KeyCap[] = splitSpace
    ? [
        { id: 'space-l', label: 'Space', u: 2.75, kind: 'space' },
        { id: 'fn-mid', label: 'Fn', u: 1.25, kind: 'mod' },
        { id: 'space-r', label: 'Space', u: 2.25, kind: 'space' },
      ]
    : [{ id: 'space', label: 'Space', u: 6.25, kind: 'space' }]
  const row5: KeyCap[] = [
    { id: 'lctrl', label: 'Ctrl', u: 1.25, kind: 'mod' },
    { id: 'win', label: 'Win', u: 1.25, kind: 'mod' },
    { id: 'lalt', label: 'Alt', u: 1.25, kind: 'mod' },
    ...spaceKeys,
    { id: 'ralt', label: 'Alt', u: 1.25, kind: 'mod' },
    { id: 'fn', label: 'Fn', u: 1.25, kind: 'mod' },
    { id: 'menu', label: 'Menu', u: 1.25, kind: 'mod' },
    { id: 'rctrl', label: 'Ctrl', u: 1.25, kind: 'mod' },
  ]
  return [row1, row2, row3, row4, row5]
}

const FN_ROW: KeyCap[] = [
  { id: 'f-esc', label: 'Esc', u: 1, kind: 'mod' },
  { id: 'gap-1', label: '', u: 1, kind: 'gap' },
  ...['F1', 'F2', 'F3', 'F4'].map(k => ({ id: k, label: k, u: 1 })),
  { id: 'gap-2', label: '', u: 0.5, kind: 'gap' },
  ...['F5', 'F6', 'F7', 'F8'].map(k => ({ id: k, label: k, u: 1, kind: 'mod' as const })),
  { id: 'gap-3', label: '', u: 0.5, kind: 'gap' },
  ...['F9', 'F10', 'F11', 'F12'].map(k => ({ id: k, label: k, u: 1 })),
]

const NAV_ROWS: KeyCap[][] = [
  [
    { id: 'prtsc', label: 'Prt', u: 1, kind: 'mod' },
    { id: 'scrlk', label: 'Scr', u: 1, kind: 'mod' },
    { id: 'pause', label: 'Pau', u: 1, kind: 'mod' },
  ],
  [
    { id: 'ins', label: 'Ins', u: 1, kind: 'mod' },
    { id: 'home', label: 'Hm', u: 1, kind: 'mod' },
    { id: 'pgup', label: 'PU', u: 1, kind: 'mod' },
  ],
  [
    { id: 'del', label: 'Del', u: 1, kind: 'mod' },
    { id: 'end', label: 'End', u: 1, kind: 'mod' },
    { id: 'pgdn', label: 'PD', u: 1, kind: 'mod' },
  ],
  [
    { id: 'nav-gap-1', label: '', u: 3, kind: 'gap' },
  ],
  [
    { id: 'nav-gap-2', label: '', u: 1, kind: 'gap' },
    { id: 'up', label: '^', u: 1, kind: 'wasd' },
    { id: 'nav-gap-3', label: '', u: 1, kind: 'gap' },
  ],
  [
    { id: 'left', label: '<', u: 1, kind: 'wasd' },
    { id: 'down', label: 'v', u: 1, kind: 'wasd' },
    { id: 'right', label: '>', u: 1, kind: 'wasd' },
  ],
]

const NUM_ROWS: KeyCap[][] = [
  [{ id: 'num-gap-top', label: '', u: 4, kind: 'gap' }],
  [
    { id: 'numlk', label: 'Num', u: 1, kind: 'mod' },
    { id: 'kp/', label: '/', u: 1, kind: 'mod' },
    { id: 'kp*', label: '*', u: 1, kind: 'mod' },
    { id: 'kp-', label: '-', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp7', label: '7', u: 1 },
    { id: 'kp8', label: '8', u: 1 },
    { id: 'kp9', label: '9', u: 1 },
    { id: 'kp+', label: '+', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp4', label: '4', u: 1 },
    { id: 'kp5', label: '5', u: 1 },
    { id: 'kp6', label: '6', u: 1 },
    { id: 'kpeq', label: '=', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp1', label: '1', u: 1 },
    { id: 'kp2', label: '2', u: 1 },
    { id: 'kp3', label: '3', u: 1 },
    { id: 'kpent', label: 'Ent', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp0', label: '0', u: 2 },
    { id: 'kp.', label: '.', u: 1 },
    { id: 'kpret', label: 'Ok', u: 1, kind: 'mod' },
  ],
]

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
      {keys.map(key => key.kind === 'gap' ? (
        <span
          key={key.id}
          className="exo-kb-gap"
          style={{ '--u': key.u } as CSSProperties}
          aria-hidden="true"
        />
      ) : (
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
      ))}
    </div>
  )
}

function actuationTravelPct(actuationMm?: number | null): number {
  if (actuationMm == null) return 0
  return Math.round((Math.min(4, Math.max(0.1, actuationMm)) / 4) * 100)
}

function keyNoteText(selectedLabel: string | null, profileName?: string | null, actuationMm?: number | null): string {
  if (!selectedLabel) {
    return 'Click any key to inspect active profile settings. Global actuation applies to all keys; open Wootility for per-key DKS, remaps, and RGB.'
  }
  const mmText = actuationMm != null ? `${actuationMm.toFixed(1)} mm` : 'default'
  return `${selectedLabel} uses ${profileName ?? 'the active profile'} global actuation (${mmText}). Open Wootility for per-key DKS, remaps, and RGB layers.`
}

function KeyboardTelemetry({
  profileName,
  actuationMm,
  rapidTrigger,
  rapidTriggerMm,
  tachyon,
  maxScanHz,
  selectedLabel,
}: {
  profileName?: string | null
  actuationMm?: number | null
  rapidTrigger?: boolean | null
  rapidTriggerMm?: number | null
  tachyon?: boolean | null
  maxScanHz?: number | null
  selectedLabel: string | null
}) {
  const travelPct = actuationTravelPct(actuationMm)
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
      </div>
      {actuationMm != null ? (
        <div className="exo-kb-travel" aria-label={`Actuation travel ${actuationMm.toFixed(1)} of 4.0 millimeters`}>
          <span className="exo-kb-travel-track">
            <span className="exo-kb-travel-fill" style={{ width: `${travelPct}%` }} />
          </span>
          <span className="exo-kb-travel-scale">0.1–4.0 mm</span>
        </div>
      ) : null}
      <p className="exo-kb-map-note">{keyNoteText(selectedLabel, profileName, actuationMm)}</p>
    </div>
  )
}

export function KeyboardMap({
  model,
  layout,
  profileName,
  actuationMm,
  rapidTrigger,
  rapidTriggerMm,
  tachyon,
  maxScanHz,
}: {
  model?: string | null
  layout?: string | null
  profileName?: string | null
  actuationMm?: number | null
  rapidTrigger?: boolean | null
  rapidTriggerMm?: number | null
  tachyon?: boolean | null
  maxScanHz?: number | null
}) {
  const [selectedKeyId, setSelectedKeyId] = useState<string | null>(null)
  const form = detectFormFactor(model)
  const splitSpace = /split/i.test(layout ?? '')
  const mainRows = buildMainRows(splitSpace)
  const rows = form === '60' ? mainRows : [FN_ROW, ...mainRows]
  const selectedLabel = [...rows.flat(), ...NAV_ROWS.flat(), ...NUM_ROWS.flat()].find(k => k.id === selectedKeyId)?.label ?? null

  return (
    <div className="exo-kb-map-wrap">
      <div
        className="exo-kb-board"
        data-form={form}
        data-rt={rapidTrigger ? 'on' : 'off'}
        data-tachyon={tachyon ? 'on' : 'off'}
      >
        <div className="exo-kb-cluster is-main">
          {rows.map((row, index) => (
            <KeyRow
              key={index}
              keys={row}
              selectedKey={selectedKeyId}
              onSelect={id => setSelectedKeyId(id || null)}
            />
          ))}
        </div>
        {form !== '60' ? (
          <div className="exo-kb-cluster is-nav">
            {NAV_ROWS.map((row, index) => (
              <KeyRow
                key={index}
                keys={row}
                selectedKey={selectedKeyId}
                onSelect={id => setSelectedKeyId(id || null)}
              />
            ))}
          </div>
        ) : null}
        {form === 'full' ? (
          <div className="exo-kb-cluster is-num">
            {NUM_ROWS.map((row, index) => (
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
        rapidTrigger={rapidTrigger}
        rapidTriggerMm={rapidTriggerMm}
        tachyon={tachyon}
        maxScanHz={maxScanHz}
        selectedLabel={selectedLabel}
      />
    </div>
  )
}
