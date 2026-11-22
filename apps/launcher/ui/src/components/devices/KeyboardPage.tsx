import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { ExternalLink, Refresh } from '../../brand/icons'
import { errorText } from '../../lib/errorWords'
import { host, type KeyboardBackupSummary, type KeyboardRead, type Peripheral } from '../../lib/host'
import { report } from '../../lib/report'
import { Row, SliderRow, State, SwitchRow } from '../settings/parts'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { Spinner } from '../ui/Spinner'
import { DevicePage, DevicePanel as Panel } from './DevicePage'
import { KeyboardMap } from './KeyboardMap'
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

function formatBackupTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date)
}

function useKeyboard(id: string) {
  const [read, setRead] = useState<KeyboardRead | null>(null)
  const [backups, setBackups] = useState<KeyboardBackupSummary[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [pendingSlot, setPendingSlot] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadFailed, setLoadFailed] = useState<string | null>(null)

  const loadBackups = useCallback(() => {
    return host.keyboardBackups(id)
      .then(res => {
        if (res.ok) setBackups(res.backups)
      })
      .catch((cause: unknown) => report(cause, 'KeyboardPage.backups'))
  }, [id])

  const refresh = useCallback(() => {
    setBusy('read')
    setLoadFailed(null)
    return Promise.all([host.keyboardRead(id), host.keyboardBackups(id).catch(() => null)])
      .then(([next, bk]) => {
        setRead(next)
        if (bk?.ok) setBackups(bk.backups)
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
    Promise.all([host.keyboardRead(id), host.keyboardBackups(id).catch(() => null)])
      .then(([next, bk]) => {
        if (!active) return
        setRead(next)
        if (bk?.ok) setBackups(bk.backups)
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

  const switchProfile = useCallback(async (slot: number) => {
    setPendingSlot(slot)
    try {
      return await runAction('profile', "Couldn't switch profile.", () => host.keyboardProfile(id, slot))
    } finally {
      setPendingSlot(null)
    }
  }, [id, runAction])

  const saveSettings = useCallback(async (patch: KeyboardPatch) => {
    const msg = await runAction('settings', "Couldn't update keyboard settings.", () => host.keyboardSettings(id, patch))
    void loadBackups()
    return msg
  }, [id, loadBackups, runAction])

  const createBackup = useCallback(async () => {
    const res = await host.keyboardBackup(id)
    if (!res.ok) throw new Error(res.message ?? "Couldn't save a keyboard backup.")
    setBackups(res.backups)
  }, [id])

  const restoreBackup = useCallback(async (fileName: string) => {
    setError(null)
    const next = await host.keyboardRestore(id, fileName)
    if (!next.ok) throw new Error(next.message ?? "Couldn't restore this backup.")
    setRead(next)
    void loadBackups()
  }, [id, loadBackups])

  return {
    read,
    backups,
    busy,
    pendingSlot,
    error,
    loadFailed,
    refresh,
    switchProfile,
    saveSettings,
    createBackup,
    restoreBackup,
  }
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

function ProfilesPanel({
  profiles,
  activeProfile,
  busy,
  pendingSlot,
  onSelect,
}: {
  profiles: { index: number; name: string }[]
  activeProfile: number
  busy: boolean
  pendingSlot: number | null
  onSelect: (slot: number) => void
}) {
  return (
    <Panel title="Profiles">
      <Row label="Onboard profiles" detail="Switch active slot stored on keyboard memory" stacked>
        <div className="exo-kb-tabs" role="tablist" aria-label="Onboard keyboard profiles">
          {profiles.map(profile => {
            const selected = profile.index === activeProfile
            const pending = pendingSlot === profile.index
            return (
              <button
                key={profile.index}
                type="button"
                role="tab"
                aria-selected={selected}
                aria-busy={pending || undefined}
                className={selected ? 'exo-kb-tab is-active' : 'exo-kb-tab'}
                disabled={busy}
                onClick={() => { if (!selected) onSelect(profile.index) }}
              >
                <span className="exo-kb-tab-slot">P{profile.index + 1}</span>
                <span>{profile.name}</span>
                {pending ? <Spinner size={12} label="Switching profile" /> : null}
              </button>
            )
          })}
        </div>
      </Row>
    </Panel>
  )
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

function BackupsPanel({
  backups,
  canEdit,
  onSaveBackup,
  onRestoreBackup,
}: {
  backups: KeyboardBackupSummary[]
  canEdit: boolean
  onSaveBackup: () => Promise<void>
  onRestoreBackup: (fileName: string) => Promise<void>
}) {
  return (
    <Panel title="Backups">
      <Row label="Save snapshot" detail="Stores all onboard profiles locally for this keyboard">
        <ActionButton
          className="exo-set-btn is-primary"
          disabled={!canEdit}
          working="Saving"
          done="Saved"
          context="KeyboardPage.backup"
          fallback="Couldn't save keyboard backup."
          onPress={onSaveBackup}
        >
          Save backup
        </ActionButton>
      </Row>
      {backups.length > 0 ? (
        <div className="exo-kb-backups">
          {backups.map(backup => (
            <div key={backup.fileName} className="exo-kb-backup-row">
              <div className="exo-kb-backup-copy">
                <span className="exo-kb-backup-date">{formatBackupTime(backup.createdUtc)}</span>
                <span className="exo-kb-backup-meta">
                  {backup.model} · {backup.profileCount} {backup.profileCount === 1 ? 'profile' : 'profiles'}
                </span>
              </div>
              <ConfirmButton
                className="exo-set-btn"
                question="Restore backup?"
                danger
                disabled={!canEdit}
                working="Restoring"
                done="Restored"
                context="KeyboardPage.restore"
                fallback="Couldn't restore keyboard backup."
                onConfirm={() => onRestoreBackup(backup.fileName)}
              >
                Restore
              </ConfirmButton>
            </div>
          ))}
        </div>
      ) : (
        <p className="exo-kb-backups-empty">
          No backups saved yet. A safety backup is created automatically before Exo writes settings.
        </p>
      )}
    </Panel>
  )
}

function RetryPanel({
  loadFailed,
  retrying,
  disabled,
  onRetry,
}: {
  loadFailed: string | null
  retrying: boolean
  disabled: boolean
  onRetry: () => void
}) {
  if (!loadFailed) return null
  return (
    <Panel title="Keyboard">
      <Row label="Couldn't read keyboard" detail={loadFailed}>
        <button type="button" className="exo-set-btn" disabled={disabled} onClick={onRetry}>
          {retrying ? <Spinner label="Retrying" /> : <Refresh size={14} />}
          Retry
        </button>
      </Row>
    </Panel>
  )
}

function KeyboardProfileControls({
  read,
  isBusy,
  canEdit,
  pendingSlot,
  error,
  onSelectProfile,
  onSave,
}: {
  read: KeyboardRead | null
  isBusy: boolean
  canEdit: boolean
  pendingSlot: number | null
  error: string | null
  onSelectProfile: (slot: number) => void
  onSave: (patch: KeyboardPatch) => Promise<string | null>
}) {
  if (!read) return null
  return (
    <>
      {read.profiles.length > 0 ? (
        <ProfilesPanel
          profiles={read.profiles}
          activeProfile={read.activeProfile}
          busy={isBusy}
          pendingSlot={pendingSlot}
          onSelect={onSelectProfile}
        />
      ) : null}
      {read.actuationMm != null ? (
        <ActuationPanel actuationMm={read.actuationMm} canEdit={canEdit} error={error} onSave={onSave} />
      ) : null}
      {read.rapidTrigger != null ? (
        <RapidTriggerPanel enabled={read.rapidTrigger} sensitivityMm={read.rapidTriggerMm} canEdit={canEdit} onSave={onSave} />
      ) : null}
    </>
  )
}

export function KeyboardPage({ card, name, status, art }: {
  card: Peripheral
  name: string
  status: string
  art: ReactNode
}) {
  const {
    read,
    backups,
    busy,
    pendingSlot,
    error,
    loadFailed,
    refresh,
    switchProfile,
    saveSettings,
    createBackup,
    restoreBackup,
  } = useKeyboard(card.id)
  const isBusy = busy !== null
  const canEdit = read?.ok === true && read.canWrite === true && !isBusy
  const activeProfileName = read?.profiles.find(p => p.index === read.activeProfile)?.name ?? null
  const displayName = read?.model ?? name
  const mapModel = read?.model ?? card.model ?? name
  const mapLayout = read?.layout ?? card.layout

  return (
    <DevicePage
      name={displayName}
      status={status}
      art={art}
      stats={heroStats(read)}
      credit="Direct Wooting USB HID protocol"
    >
      <RetryPanel
        loadFailed={loadFailed}
        retrying={busy === 'read'}
        disabled={isBusy}
        onRetry={() => { void refresh() }}
      />

      <KeyboardProfileControls
        read={read}
        isBusy={isBusy}
        canEdit={canEdit}
        pendingSlot={pendingSlot}
        error={error}
        onSelectProfile={slot => { void switchProfile(slot) }}
        onSave={saveSettings}
      />

      <Panel title="Keyboard map">
        <KeyboardMap
          model={mapModel}
          layout={mapLayout}
          profileName={activeProfileName}
          actuationMm={read?.actuationMm}
          rapidTrigger={read?.rapidTrigger}
          rapidTriggerMm={read?.rapidTriggerMm}
          tachyon={read?.tachyon}
          maxScanHz={read?.maxScanHz}
        />
      </Panel>

      <PerformancePanel card={card} read={read} canEdit={canEdit} onSave={saveSettings} />

      <BackupsPanel
        backups={backups}
        canEdit={canEdit}
        onSaveBackup={createBackup}
        onRestoreBackup={restoreBackup}
      />
    </DevicePage>
  )
}
