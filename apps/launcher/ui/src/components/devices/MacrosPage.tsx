import '../../styles/macros.css'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, AntiCheat, Bolt, Check, Clock, Keyboard, Mouse, Play, Plus, Repeat, Stop, Trash } from '../../brand/icons'
import {
  host,
  onHostEvent,
  type MacroBinding,
  type MacroDeviceCapability,
  type MacroGamePolicy,
  type MacroProfile,
  type MacroStateSnapshot,
  type MacroStep,
  type MacroTestRunResult,
} from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Switch } from '../Switch'

const STEP_KINDS: MacroStep['kind'][] = [
  'keyDown',
  'keyUp',
  'mouseDown',
  'mouseUp',
  'mouseMove',
  'padButtonDown',
  'padButtonUp',
  'padAxis',
  'delay',
  'text',
  'launchApp',
  'layer',
]

function emptyBinding(): MacroBinding {
  return {
    id: `m-${Math.random().toString(36).slice(2, 10)}`,
    name: 'New macro',
    enabled: true,
    delivery: 'software',
    onboardDeviceId: null,
    trigger: {
      chord: [{ deviceKind: 'keyboard', deviceId: null, code: 'F13' }],
      chordWindowMs: 45,
      layerId: 'base',
      mode: 'once',
      repeatCount: 1,
      jitterMs: 0,
      swallowInput: true,
    },
    steps: [
      { id: 'st-1', kind: 'keyDown', code: 'E', delayMs: 0 },
      { id: 'st-2', kind: 'delay', delayMs: 12 },
      { id: 'st-3', kind: 'keyUp', code: 'E', delayMs: 0 },
    ],
    updatedAt: new Date().toISOString(),
  }
}

function stepValue(step: MacroStep): string {
  return step.text ?? step.appPath ?? step.code ?? step.layerId ?? ''
}

function patchStepValue(step: MacroStep, value: string): MacroStep {
  if (step.kind === 'text') return { ...step, text: value }
  if (step.kind === 'launchApp') return { ...step, appPath: value }
  if (step.kind === 'layer') return { ...step, layerId: value }
  return { ...step, code: value }
}

function ProfilesPanel({
  profiles,
  activeProfileId,
  selectedProfile,
  newProfileName,
  onNewProfileName,
  onSelect,
  onCreate,
  onActivate,
  onDelete,
}: {
  profiles: MacroProfile[]
  activeProfileId: string
  selectedProfile: MacroProfile | null
  newProfileName: string
  onNewProfileName: (value: string) => void
  onSelect: (id: string) => void
  onCreate: () => void
  onActivate: (id: string) => void
  onDelete: (id: string) => void
}) {
  return (
    <section className="exo-macros-panel">
      <div className="exo-macros-panel-head">
        <h2>Profiles</h2>
        <span className="exo-macros-badge">{profiles.length}</span>
      </div>
      <div className="exo-macros-list">
        {profiles.map(p => (
          <button
            key={p.id}
            type="button"
            className={cn('exo-macros-item', p.id === selectedProfile?.id && 'is-active')}
            onClick={() => onSelect(p.id)}
          >
            <span>{p.name}</span>
            <span className="exo-macros-item-meta">
              {p.id === activeProfileId ? 'Active' : `${p.bindings.length} bindings`}
            </span>
          </button>
        ))}
      </div>
      <div className="exo-macros-inline-form">
        <input
          className="exo-macros-input"
          value={newProfileName}
          placeholder="New profile name"
          onChange={e => onNewProfileName(e.target.value)}
        />
        <button type="button" className="exo-set-btn is-compact" onClick={onCreate}>
          <Plus size={14} />Add
        </button>
      </div>
      {selectedProfile ? (
        <div className="exo-macros-controls">
          <button
            type="button"
            className="exo-set-btn is-compact is-primary"
            disabled={selectedProfile.id === activeProfileId}
            onClick={() => onActivate(selectedProfile.id)}
          >
            <Check size={14} />Activate
          </button>
          {!selectedProfile.isDefault ? (
            <button
              type="button"
              className="exo-set-btn is-compact is-danger"
              onClick={() => onDelete(selectedProfile.id)}
            >
              <Trash size={14} />Delete
            </button>
          ) : null}
        </div>
      ) : null}
    </section>
  )
}

function DevicesPanel({ devices }: { devices: MacroDeviceCapability[] }) {
  return (
    <section className="exo-macros-panel">
      <div className="exo-macros-panel-head">
        <h2>Connected devices</h2>
      </div>
      <div className="exo-macros-list">
        {devices.length === 0 ? (
          <p className="exo-macros-notice">No peripherals reported; universal bindings still match any input.</p>
        ) : devices.map(dev => (
          <div key={dev.id} className="exo-macros-row">
            <div className="exo-macros-row-copy">
              <span className="exo-macros-row-title">{dev.name}</span>
              <span className="exo-macros-row-desc">{dev.kind}{dev.protocol ? ` · ${dev.protocol}` : ''}</span>
            </div>
            <span className={cn('exo-macros-badge', dev.supportsOnboard && 'is-onboard')}>
              {dev.supportsOnboard ? 'Onboard' : 'Software'}
            </span>
          </div>
        ))}
      </div>
    </section>
  )
}

function AntiCheatPanel({
  policies,
  onToggleConfirm,
}: {
  policies: MacroGamePolicy[]
  onToggleConfirm: (gameId: string, confirmed: boolean) => void
}) {
  return (
    <section className="exo-macros-panel">
      <div className="exo-macros-panel-head">
        <h2>Anti-cheat policy</h2>
        <AntiCheat size={14} />
      </div>
      <p className="exo-macros-notice">
        Online, competitive anti-cheat, and unknown games allow Tier 1 onboard hardware macros only (no Tier 2 software macros). Offline and single-player games keep Tier 2 with a one-time note.
      </p>
      {policies.length === 0 ? (
        <p className="exo-macros-notice">No installed games scanned yet.</p>
      ) : policies.slice(0, 8).map(gp => (
        <div key={gp.gameId} className="exo-macros-row">
          <div className="exo-macros-row-copy">
            <span className="exo-macros-row-title">{gp.title}</span>
            <span className="exo-macros-row-desc">
              {!gp.softwareAllowed
                ? `${gp.antiCheatLabel ?? 'Online / unknown'} · hardware onboard only`
                : gp.confirmed
                  ? 'Offline · Tier 2 software macros enabled'
                  : 'Offline · Tier 2 software macros (one-time note)'}
            </span>
          </div>
          {!gp.softwareAllowed ? (
            <span className="exo-macros-badge is-ac">Hardware only</span>
          ) : gp.requiresConfirmation ? (
            <button
              type="button"
              className="exo-set-btn is-compact"
              onClick={() => onToggleConfirm(gp.gameId, true)}
            >
              Got it
            </button>
          ) : (
            <span className="exo-macros-badge is-onboard">Tier 1 + 2</span>
          )}
        </div>
      ))}
    </section>
  )
}

function BindingsPanel({
  profile,
  selectedBindingId,
  onSelectLayer,
  onAddBinding,
  onSelectBinding,
}: {
  profile: MacroProfile
  selectedBindingId: string | null
  onSelectLayer: (layerId: string) => void
  onAddBinding: () => void
  onSelectBinding: (id: string) => void
}) {
  return (
    <section className="exo-macros-panel">
      <div className="exo-macros-panel-head">
        <h2>{profile.name} bindings &amp; layers</h2>
        <div className="exo-macros-controls">
          {profile.layers.map(layer => (
            <button
              key={layer.id}
              type="button"
              className={cn('exo-set-btn is-compact', profile.activeLayerId === layer.id && 'is-primary')}
              onClick={() => onSelectLayer(layer.id)}
            >
              {layer.name}
            </button>
          ))}
          <button type="button" className="exo-set-btn is-compact" onClick={onAddBinding}>
            <Plus size={14} />New macro
          </button>
        </div>
      </div>

      <div className="exo-macros-list">
        {profile.bindings.map(b => (
          <button
            key={b.id}
            type="button"
            className={cn('exo-macros-item', b.id === selectedBindingId && 'is-active')}
            onClick={() => onSelectBinding(b.id)}
          >
            <span>{b.name}</span>
            <span className="exo-macros-controls">
              <span className={cn('exo-macros-badge', b.delivery === 'onboard' ? 'is-onboard' : 'is-ac')}>
                {b.delivery === 'onboard' ? 'Onboard memory' : 'Software'}
              </span>
              <span className="exo-macros-item-meta">
                {b.trigger.chord.map(c => c.code).join(' + ')} · {b.trigger.mode}
              </span>
            </span>
          </button>
        ))}
      </div>
    </section>
  )
}

function TimelineEditor({
  binding,
  recording,
  runningTest,
  testResult,
  statusLine,
  onUpdate,
  onToggleRecord,
  onRunTest,
}: {
  binding: MacroBinding
  recording: boolean
  runningTest: boolean
  testResult: MacroTestRunResult | null
  statusLine: string | null
  onUpdate: (patch: (cur: MacroBinding) => MacroBinding) => void
  onToggleRecord: () => void
  onRunTest: () => void
}) {
  const firstInput = binding.trigger.chord[0] ?? { deviceKind: 'keyboard' as const, deviceId: null, code: 'F13' }

  return (
    <section className="exo-macros-panel">
      <div className="exo-macros-panel-head">
        <h2>Timeline &amp; trigger editor</h2>
        <div className="exo-macros-controls">
          <button
            type="button"
            className={cn('exo-set-btn is-compact', recording && 'is-danger')}
            onClick={onToggleRecord}
          >
            {recording ? <><Stop size={14} />Stop recording</> : <><Bolt size={14} />Record</>}
          </button>
          <button
            type="button"
            className="exo-set-btn is-compact is-primary"
            disabled={runningTest}
            onClick={onRunTest}
          >
            <Play size={14} />Test timing
          </button>
        </div>
      </div>

      <div className="exo-macros-fields">
        <label className="exo-macros-field">
          <span>Macro name</span>
          <input
            className="exo-macros-input"
            value={binding.name}
            onChange={e => onUpdate(cur => ({ ...cur, name: e.target.value }))}
          />
        </label>

        <label className="exo-macros-field">
          <span>Delivery</span>
          <select
            className="exo-macros-select"
            value={binding.delivery}
            onChange={e => onUpdate(cur => ({ ...cur, delivery: e.target.value === 'onboard' ? 'onboard' : 'software' }))}
          >
            <option value="onboard">Tier 1 onboard hardware memory (allowed everywhere)</option>
            <option value="software">Tier 2 software sidecar (offline &amp; single-player only)</option>
          </select>
        </label>

        <label className="exo-macros-field">
          <span>Trigger device</span>
          <select
            className="exo-macros-select"
            value={firstInput.deviceKind}
            onChange={e => {
              const kind = e.target.value as 'keyboard' | 'mouse' | 'controller'
              onUpdate(cur => ({
                ...cur,
                trigger: {
                  ...cur.trigger,
                  chord: [{ ...firstInput, deviceKind: kind }, ...cur.trigger.chord.slice(1)],
                },
              }))
            }}
          >
            <option value="keyboard">Keyboard</option>
            <option value="mouse">Mouse</option>
            <option value="controller">Controller</option>
          </select>
        </label>

        <label className="exo-macros-field">
          <span>Trigger chord</span>
          <input
            className="exo-macros-input"
            value={binding.trigger.chord.map(c => c.code).join(' + ')}
            onChange={e => {
              const parts = e.target.value.split('+').map(s => s.trim()).filter(Boolean)
              const codes = parts.length > 0 ? parts : ['F13']
              onUpdate(cur => ({
                ...cur,
                trigger: {
                  ...cur.trigger,
                  chord: codes.map(code => ({ deviceKind: firstInput.deviceKind, deviceId: firstInput.deviceId ?? null, code })),
                },
              }))
            }}
          />
        </label>

        <label className="exo-macros-field">
          <span>Playback mode</span>
          <select
            className="exo-macros-select"
            value={binding.trigger.mode}
            onChange={e => onUpdate(cur => ({
              ...cur,
              trigger: { ...cur.trigger, mode: e.target.value as MacroBinding['trigger']['mode'] },
            }))}
          >
            <option value="once">Once</option>
            <option value="repeat">Repeat</option>
            <option value="hold">Hold</option>
            <option value="toggle">Toggle</option>
          </select>
        </label>

        <label className="exo-macros-field">
          <span>Randomization ms (off = 0)</span>
          <input
            type="number"
            min={0}
            max={50}
            className="exo-macros-input"
            value={binding.trigger.jitterMs}
            onChange={e => onUpdate(cur => ({
              ...cur,
              trigger: { ...cur.trigger, jitterMs: Math.max(0, Math.min(50, Number(e.target.value) || 0)) },
            }))}
          />
        </label>
      </div>

      <div className="exo-macros-row">
        <div className="exo-macros-row-copy">
          <span className="exo-macros-row-title">Steps ({binding.steps.length})</span>
          <span className="exo-macros-row-desc">
            <Keyboard size={12} /> <Mouse size={12} /> <Repeat size={12} /> Cross-device actions, delays, text and app launch.
          </span>
        </div>
        <button
          type="button"
          className="exo-set-btn is-compact"
          onClick={() => onUpdate(cur => ({
            ...cur,
            steps: [...cur.steps, { id: `st-${Math.random().toString(36).slice(2, 8)}`, kind: 'delay', delayMs: 10 }],
          }))}
        >
          <Plus size={14} />Add step
        </button>
      </div>

      <div className="exo-macros-timeline">
        {binding.steps.map(step => (
          <div key={step.id} className="exo-macros-step">
            <select
              className="exo-macros-select"
              value={step.kind}
              onChange={e => onUpdate(cur => ({
                ...cur,
                steps: cur.steps.map(s => (s.id === step.id ? { ...s, kind: e.target.value as MacroStep['kind'] } : s)),
              }))}
            >
              {STEP_KINDS.map(k => <option key={k} value={k}>{k}</option>)}
            </select>
            <input
              className="exo-macros-input"
              value={stepValue(step)}
              placeholder={step.kind === 'delay' ? 'Wait' : 'Key, button, text or app'}
              onChange={e => onUpdate(cur => ({
                ...cur,
                steps: cur.steps.map(s => (s.id === step.id ? patchStepValue(s, e.target.value) : s)),
              }))}
            />
            <input
              type="number"
              min={0}
              max={10000}
              className="exo-macros-input"
              value={step.delayMs}
              onChange={e => onUpdate(cur => ({
                ...cur,
                steps: cur.steps.map(s => (s.id === step.id ? { ...s, delayMs: Math.max(0, Number(e.target.value) || 0) } : s)),
              }))}
            />
            <button
              type="button"
              className="exo-set-btn is-compact"
              aria-label="Remove step"
              onClick={() => onUpdate(cur => ({ ...cur, steps: cur.steps.filter(s => s.id !== step.id) }))}
            >
              <Trash size={14} />
            </button>
          </div>
        ))}
      </div>

      {testResult ? (
        <div className="exo-macros-trace">
          <div className="exo-macros-trace-row">
            <span>Total: {testResult.totalMs} ms</span>
            <span>P50: {testResult.latency.p50Us} us · P95: {testResult.latency.p95Us} us</span>
          </div>
          {testResult.trace.slice(0, 6).map(t => (
            <div key={`${t.stepId}-${t.targetOffsetMs}`} className="exo-macros-trace-row">
              <span>{t.kind}</span>
              <span>target {t.targetOffsetMs} ms · actual {t.actualOffsetMs} ms · err {t.errorUs} us</span>
            </div>
          ))}
        </div>
      ) : null}

      {statusLine ? <p className="exo-macros-notice">{statusLine}</p> : null}
    </section>
  )
}

function useKeyRecorder(
  recording: boolean,
  onStep: (step: MacroStep) => void,
  startRef: React.MutableRefObject<number | null>,
) {
  useEffect(() => {
    if (!recording) return
    const push = (kind: 'keyDown' | 'keyUp', code: string) => {
      const now = performance.now()
      const delta = startRef.current == null ? 0 : Math.max(0, Math.round(now - startRef.current))
      startRef.current = now
      onStep({ id: `rec-${Math.random().toString(36).slice(2, 8)}`, kind, code, delayMs: delta })
    }
    const onDown = (ev: KeyboardEvent) => { if (!ev.repeat) push('keyDown', ev.code) }
    const onUp = (ev: KeyboardEvent) => push('keyUp', ev.code)
    window.addEventListener('keydown', onDown)
    window.addEventListener('keyup', onUp)
    return () => {
      window.removeEventListener('keydown', onDown)
      window.removeEventListener('keyup', onUp)
    }
  }, [recording, onStep, startRef])
}

function MacrosHeader({
  state,
  onToggleEnabled,
}: {
  state: MacroStateSnapshot
  onToggleEnabled: (enabled: boolean) => void
}) {
  const latencyText = state.latency.samples > 0
    ? `P50 ${state.latency.p50Us} us · P95 ${state.latency.p95Us} us`
    : 'Target < 1 ms'
  return (
    <header className="exo-macros-head">
      <div className="exo-macros-head-title">
        <h1>Universal macros</h1>
        <p className="exo-macros-sub">
          Any mouse, keyboard or controller. Profile switch debounce {state.debounceMs} ms · exit grace {state.exitGraceMs} ms.
        </p>
      </div>
      <div className="exo-macros-head-actions">
        <span className={cn('exo-macros-stat', state.latency.p50Us < 1000 ? 'is-good' : 'is-warn')}>
          <Clock size={14} />{latencyText}
        </span>
        <span className="exo-macros-stat">
          <AntiCheat size={14} />Onboard memory = no software risk
        </span>
        <label className="exo-macros-toggle">
          <span>Engine</span>
          <Switch checked={state.enabled} label="Enable macro engine" onChange={onToggleEnabled} />
        </label>
      </div>
    </header>
  )
}

export function MacrosPage() {
  const [state, setState] = useState<MacroStateSnapshot | null>(null)
  const [selectedProfileId, setSelectedProfileId] = useState<string>('desktop')
  const [selectedBindingId, setSelectedBindingId] = useState<string | null>(null)
  const [newProfileName, setNewProfileName] = useState('')
  const [statusLine, setStatusLine] = useState<string | null>(null)
  const [testResult, setTestResult] = useState<MacroTestRunResult | null>(null)
  const [runningTest, setRunningTest] = useState(false)
  const recordStartRef = useRef<number | null>(null)

  const refresh = useCallback(() => {
    host.macrosState()
      .then(next => {
        setState(next)
        setSelectedProfileId(cur => (next.profiles.some(p => p.id === cur) ? cur : next.activeProfileId))
      })
      .catch((err: unknown) => report(err, 'MacrosPage.state'))
  }, [])

  useEffect(() => {
    refresh()
    return onHostEvent('macros.changed', refresh)
  }, [refresh])

  const profile = state?.profiles.find(p => p.id === selectedProfileId) ?? state?.profiles[0] ?? null
  const binding = profile?.bindings.find(b => b.id === selectedBindingId) ?? profile?.bindings[0] ?? null

  const saveProfile = useCallback(async (updated: MacroProfile, notice?: string) => {
    const res = await host.macrosSaveProfile(updated)
    if (!res.ok) {
      setStatusLine(res.message ?? 'Could not save profile.')
      return
    }
    if (notice) setStatusLine(notice)
    refresh()
  }, [refresh])

  const updateBinding = useCallback((patch: (cur: MacroBinding) => MacroBinding) => {
    if (!profile || !binding) return
    void saveProfile({ ...profile, bindings: profile.bindings.map(b => (b.id === binding.id ? patch(b) : b)) })
  }, [binding, profile, saveProfile])

  const appendRecordedStep = useCallback((step: MacroStep) => {
    updateBinding(cur => ({ ...cur, steps: [...cur.steps, step] }))
  }, [updateBinding])

  useKeyRecorder(Boolean(state?.recording && binding), appendRecordedStep, recordStartRef)

  if (!state) {
    return (
      <section className="exo-macros" aria-label="Macros">
        <p className="exo-macros-sub">Loading universal macros...</p>
      </section>
    )
  }

  const createProfile = async () => {
    const trimmed = newProfileName.trim()
    if (!trimmed) return
    const created: MacroProfile = {
      id: `p-${Math.random().toString(36).slice(2, 10)}`,
      name: trimmed,
      isDefault: false,
      gameIds: [],
      appExecutables: [],
      activeLayerId: 'base',
      layers: [
        { id: 'base', name: 'Base', activation: 'base', trigger: null },
        { id: 'shift', name: 'Shift layer', activation: 'hold', trigger: { deviceKind: 'mouse', deviceId: null, code: 'Mouse4' } },
      ],
      bindings: [emptyBinding()],
    }
    setNewProfileName('')
    await saveProfile(created, `Saved profile ${trimmed}.`)
    setSelectedProfileId(created.id)
  }

  const toggleRecording = async () => {
    if (!binding) return
    const next = !state.recording
    recordStartRef.current = next ? performance.now() : null
    await host.macrosRecord(next, binding.id)
    setStatusLine(next ? 'Recording input with millisecond timing. Press keys, then click Stop.' : 'Recording stopped.')
    refresh()
  }

  const runDryTest = async () => {
    if (!profile || !binding) return
    setRunningTest(true)
    try {
      const res = await host.macrosTestRun(profile.id, binding.id)
      setTestResult(res)
      setStatusLine(res.ok ? `Test run finished in ${res.totalMs} ms (P50 error ${res.latency.p50Us} us).` : (res.message ?? 'Test run failed.'))
    } finally {
      setRunningTest(false)
    }
  }

  return (
    <section className="exo-macros" aria-label="Universal macros">
      <MacrosHeader
        state={state}
        onToggleEnabled={checked => void host.macrosSetEnabled(checked).then(refresh)}
      />

      {state.softwarePausedForAntiCheat && state.pauseReason ? (
        <div className="exo-macros-banner" role="alert">
          <Alert size={16} className="exo-macros-banner-icon" />
          <span>{state.pauseReason}</span>
        </div>
      ) : null}

      <div className="exo-macros-grid">
        <aside className="exo-macros-rail">
          <ProfilesPanel
            profiles={state.profiles}
            activeProfileId={state.activeProfileId}
            selectedProfile={profile}
            newProfileName={newProfileName}
            onNewProfileName={setNewProfileName}
            onSelect={setSelectedProfileId}
            onCreate={() => void createProfile()}
            onActivate={id => void host.macrosActivateProfile(id).then(res => {
              if (res.message) setStatusLine(res.message)
              refresh()
            })}
            onDelete={id => void host.macrosDeleteProfile(id).then(refresh)}
          />
          <DevicesPanel devices={state.devices} />
          <AntiCheatPanel
            policies={state.gamePolicies}
            onToggleConfirm={(gid, next) => void host.macrosConfirmGame(gid, next).then(refresh)}
          />
        </aside>

        <div className="exo-macros-main">
          {profile ? (
            <BindingsPanel
              profile={profile}
              selectedBindingId={binding?.id ?? null}
              onSelectLayer={layerId => void saveProfile({ ...profile, activeLayerId: layerId })}
              onAddBinding={() => {
                const created = emptyBinding()
                void saveProfile({ ...profile, bindings: [...profile.bindings, created] })
                setSelectedBindingId(created.id)
              }}
              onSelectBinding={setSelectedBindingId}
            />
          ) : null}

          {profile && binding ? (
            <TimelineEditor
              binding={binding}
              recording={state.recording}
              runningTest={runningTest}
              testResult={testResult}
              statusLine={statusLine}
              onUpdate={updateBinding}
              onToggleRecord={() => void toggleRecording()}
              onRunTest={() => void runDryTest()}
            />
          ) : null}
        </div>
      </div>
    </section>
  )
}
