import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { ExternalLink, Refresh } from '../../brand/icons'
import { host, type KeyboardRead, type Peripheral } from '../../lib/host'
import { errorText } from '../../lib/errorWords'
import { report } from '../../lib/report'
import { ChoiceRow, Row, SliderRow, State, SwitchRow } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { DevicePage, DevicePanel as Panel } from './DevicePage'
import { portShort, portWords } from './ports'

const ACTUATION_PRESETS = [
  { label: '0.1 mm', value: 0.1 },
  { label: '0.4 mm', value: 0.4 },
  { label: '1.2 mm', value: 1.2 },
  { label: '2.0 mm', value: 2.0 },
] as const

type KeyboardPatch = {
  actuationMm?: number
  rapidTrigger?: boolean
  rapidTriggerMm?: number
  tachyon?: boolean
}

function useKeyboard(id: string) {
  const [read, setRead] = useState<KeyboardRead | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadFailed, setLoadFailed] = useState<string | null>(null)

  const refresh = useCallback(() => {
    setBusy('read')
    setLoadFailed(null)
    return host.keyboardRead(id)
      .then(next => {
        setRead(next)
        if (!next.ok && next.message) setLoadFailed(next.message)
      })
      .catch((cause: unknown) => {
        report(cause, 'KeyboardPage.read')
        setLoadFailed(errorText(cause, "Exo couldn't read this keyboard. Try again."))
      })
      .finally(() => setBusy(null))
  }, [id])

  useEffect(() => {
    let active = true
    host.keyboardRead(id)
      .then(next => {
        if (!active) return
        setRead(next)
        if (!next.ok && next.message) setLoadFailed(next.message)
      })
      .catch((cause: unknown) => {
        report(cause, 'KeyboardPage.read')
        if (active) setLoadFailed(errorText(cause, "Exo couldn't read this keyboard. Try again."))
      })
    return () => { active = false }
  }, [id])

  const runAction = useCallback(async (tag: string, fallback: string, call: () => Promise<KeyboardRead>): Promise<string | null> => {
    setBusy(tag)
    setError(null)
    try {
      const next = await call()
      setRead(next)
      if (!next.ok) {
        const msg = next.message ?? fallback
        setError(msg)
        return msg
      }
      return null
    } catch (cause) {
      report(cause, `KeyboardPage.${tag}`)
      const msg = errorText(cause, fallback)
      setError(msg)
      return msg
    } finally {
      setBusy(null)
    }
  }, [])

  const switchProfile = useCallback(
    (slot: number) => runAction('profile', "Couldn't switch profile.", () => host.keyboardProfile(id, slot)),
    [id, runAction],
  )
  const saveSettings = useCallback(
    (patch: KeyboardPatch) => runAction('settings', "Couldn't update keyboard settings.", () => host.keyboardSettings(id, patch)),
    [id, runAction],
  )

  return { read, busy, error, loadFailed, refresh, switchProfile, saveSettings }
}

function rapidTriggerStat(read: KeyboardRead | null): string | null {
  if (read?.rapidTrigger == null) return null
  if (!read.rapidTrigger) return 'Off'
  return read.rapidTriggerMm != null ? `${read.rapidTriggerMm.toFixed(2)} mm` : 'On'
}

function heroStats(read: KeyboardRead | null) {
  const activeProfileName = read?.profiles.find(p => p.index === read.activeProfile)?.name ?? null
  return [
    { label: 'Profile', value: activeProfileName },
    { label: 'Actuation', value: read?.actuationMm != null ? `${read.actuationMm.toFixed(1)} mm` : null },
    { label: 'Rapid Trigger', value: rapidTriggerStat(read) },
    { label: 'Scan rate', value: read?.maxScanHz ? `${read.maxScanHz.toLocaleString()} Hz` : null },
    { label: 'Firmware', value: read?.firmware ?? null },
    { label: 'Layout', value: read?.layout ?? null },
  ]
}

function ActuationPanel({ actuationMm, canEdit, error, onSave }: {
  actuationMm: number
  canEdit: boolean
  error: string | null
  onSave: (patch: KeyboardPatch) => Promise<string | null>
}) {
  return (
    <Panel title="Actuation">
      <SliderRow
        label="Actuation point"
        min={0.1}
        max={4.0}
        step={0.1}
        value={Number(actuationMm.toFixed(1))}
        render={v => `${v.toFixed(1)} mm`}
        disabled={!canEdit}
        error={error}
        onCommit={next => { void onSave({ actuationMm: next }) }}
      />
      <Row label="Presets" detail="Instant global travel presets">
        <div className="exo-kb-presets">
          {ACTUATION_PRESETS.map(preset => (
            <button
              key={preset.value}
              type="button"
              className={Math.abs(actuationMm - preset.value) < 0.05 ? 'exo-set-btn is-primary' : 'exo-set-btn'}
              disabled={!canEdit}
              onClick={() => void onSave({ actuationMm: preset.value })}
            >
              {preset.label}
            </button>
          ))}
        </div>
      </Row>
    </Panel>
  )
}

function RapidTriggerPanel({ enabled, sensitivityMm, canEdit, onSave }: {
  enabled: boolean
  sensitivityMm: number | null
  canEdit: boolean
  onSave: (patch: KeyboardPatch) => Promise<string | null>
}) {
  return (
    <Panel title="Rapid Trigger">
      <SwitchRow
        id="kb-rt"
        label="Rapid Trigger"
        detail="Resets key actuation the instant it moves upward"
        checked={enabled}
        disabled={!canEdit}
        onChange={rapidTrigger => onSave({ rapidTrigger })}
      />
      {enabled && sensitivityMm != null ? (
        <SliderRow
          label="Sensitivity"
          min={0.15}
          max={2.35}
          step={0.05}
          value={Number(sensitivityMm.toFixed(2))}
          render={v => `${v.toFixed(2)} mm`}
          disabled={!canEdit}
          onCommit={rapidTriggerMm => { void onSave({ rapidTriggerMm }) }}
        />
      ) : null}
    </Panel>
  )
}

function PerformancePanel({ card, read, canEdit, onSave }: {
  card: Peripheral
  read: KeyboardRead | null
  canEdit: boolean
  onSave: (patch: KeyboardPatch) => Promise<string | null>
}) {
  const portText = card.port ? portWords(card.port) : null
  const portBadge = card.port ? portShort(card.port) : null
  const tachyonDetail = (read?.maxScanHz ?? 1000) >= 8000
    ? 'Enables 8,000 Hz ultra-low latency scanning'
    : 'Prioritizes minimum key scan latency over RGB animations'
  const wootilityDetail = read?.backgroundServiceRunning
    ? 'Wooting Background Service running · Open for per-key DKS, remap & RGB'
    : 'Open for per-key DKS, remapping & RGB layers'

  return (
    <Panel title="Performance">
      {read?.tachyon != null ? (
        <SwitchRow
          id="kb-tachyon"
          label="Tachyon mode"
          detail={tachyonDetail}
          checked={read.tachyon}
          disabled={!canEdit}
          onChange={tachyon => onSave({ tachyon })}
        />
      ) : null}
      {portText ? (
        <Row label="USB port" detail={portText}>
          <State on={card.port?.where === 'cpu' && !card.port.hubs}>{portBadge ?? 'USB'}</State>
        </Row>
      ) : null}
      <Row label="Wootility" detail={wootilityDetail}>
        <button type="button" className="exo-set-btn" onClick={() => void host.keyboardOpenApp()}>
          <ExternalLink size={14} />
          {read?.wootilityInstalled ? 'Open Wootility' : 'Wootility Web'}
        </button>
      </Row>
    </Panel>
  )
}

export function KeyboardPage({ card, name, status, art }: {
  card: Peripheral
  name: string
  status: string
  art: ReactNode
}) {
  const { read, busy, error, loadFailed, refresh, switchProfile, saveSettings } = useKeyboard(card.id)
  const canEdit = read?.ok === true && read.canWrite === true && busy === null

  return (
    <DevicePage
      name={read?.model ?? name}
      status={status}
      art={art}
      stats={heroStats(read)}
      credit="Direct Wooting USB HID protocol"
    >
      {loadFailed ? (
        <Panel title="Keyboard">
          <Row label="Couldn't read keyboard" detail={loadFailed}>
            <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void refresh()}>
              {busy === 'read' ? <Spinner label="Retrying" /> : <Refresh size={14} />}
              Retry
            </button>
          </Row>
        </Panel>
      ) : null}

      {read?.profiles.length ? (
        <Panel title="Profile">
          <ChoiceRow
            id="kb-profile"
            label="Active profile"
            detail="Switches onboard keyboard profile immediately"
            value={read.activeProfile}
            disabled={busy !== null}
            choices={read.profiles.map(p => ({ value: p.index, label: p.name }))}
            onChange={slot => switchProfile(Number(slot))}
          />
        </Panel>
      ) : null}

      {read?.actuationMm != null ? (
        <ActuationPanel actuationMm={read.actuationMm} canEdit={canEdit} error={error} onSave={saveSettings} />
      ) : null}

      {read?.rapidTrigger != null ? (
        <RapidTriggerPanel enabled={read.rapidTrigger} sensitivityMm={read.rapidTriggerMm} canEdit={canEdit} onSave={saveSettings} />
      ) : null}

      <PerformancePanel card={card} read={read} canEdit={canEdit} onSave={saveSettings} />
    </DevicePage>
  )
}
