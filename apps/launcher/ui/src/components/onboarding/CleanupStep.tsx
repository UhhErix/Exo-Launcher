import { useState } from 'react'
import { host, type JunkItem, type LeftoverSummary, type ReplacedApp } from '../../lib/host'
import { JunkRow, junkWorth } from '../gpu/JunkRow'
import { Row, State } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'

function size(bytes: number): string {
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`
  if (bytes >= 1e6) return `${Math.round(bytes / 1e6)} MB`
  return bytes > 0 ? `${Math.max(1, Math.round(bytes / 1e3))} KB` : ''
}

function said(left: LeftoverSummary): string {
  const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
  const parts: string[] = []
  if (left.folders > 0) parts.push(count(left.folders, 'folder', 'folders'))
  if (left.files > 0) parts.push(count(left.files, 'file', 'files'))
  const amount = size(left.bytes)
  if (amount && parts.length > 0) parts[parts.length - 1] += ` (${amount})`
  if (left.registry > 0) parts.push(count(left.registry, 'registry entry', 'registry entries'))
  if (left.shortcuts > 0) parts.push(count(left.shortcuts, 'shortcut', 'shortcuts'))
  return parts.length < 2 ? parts.join('') : `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`
}

export function CleanupStep({ apps, onApps, junk, onCleaned }: {
  apps: ReplacedApp[]
  onApps: (next: ReplacedApp[]) => void
  junk: JunkItem[]
  onCleaned: () => void
}) {
  return (
    <div className="exo-set-list">
      <ReplacedAppRows apps={apps} onApps={onApps} empty={!junkWorth(junk)} />
      <JunkRow junk={junk} onCleaned={onCleaned} />
    </div>
  )
}

function ReplacedAppRows({ apps, onApps, empty }: { apps: ReplacedApp[]; onApps: (next: ReplacedApp[]) => void; empty: boolean }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string | undefined>>({})
  const [listed] = useState(() => new Set(apps.filter(app => app.installed || app.leftovers > 0 || app.cleared).map(app => app.id)))
  const shown = apps.filter(app => listed.has(app.id))

  async function remove(app: ReplacedApp) {
    setBusy(app.id)
    setErrors(current => ({ ...current, [app.id]: undefined }))
    try {
      const result = await host.replacedRemove(app.id)
      if (!result.ok) onApps(result.apps)
      return result
    } finally {
      setBusy(null)
    }
  }
  const removed = (result: unknown) => onApps((result as { apps: ReplacedApp[] }).apps)

  if (shown.length === 0) return empty ? <Row label="Nothing to clean up" /> : null
  return (
    <>
      {shown.map(app => {
        const gone = !app.installed && app.leftovers === 0
        const amount = size(app.bytes)
        const detail = !app.installed && !gone ? `Left behind: ${said(app.found)}` : undefined
        return (
          <Row key={app.id} label={app.name} detail={detail} error={errors[app.id]}>
            {gone ? <State on>Removed</State> : (
              <>
                {app.installed && amount ? <State>{amount}</State> : null}
                <ConfirmButton className="exo-set-btn is-danger" danger disabled={busy !== null && busy !== app.id} question={app.installed ? 'Remove?' : 'Clear?'}
                  timeoutMs={null} context="CleanupStep.remove" fallback={`${app.name} wasn't removed. Try again.`}
                  why="none" onFailed={error => setErrors(current => ({ ...current, [app.id]: error.text }))}
                  onConfirm={() => remove(app)} onDone={removed}>
                  {app.installed ? 'Remove' : 'Clear'}
                </ConfirmButton>
              </>
            )}
          </Row>
        )
      })}
    </>
  )
}
