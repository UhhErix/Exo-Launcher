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

/** "3 folders (1.2 GB), 9 registry entries and 2 shortcuts": what an app left, or what was cleared of it. */
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

/**
 * First run's clean up: the apps Exo does the job of, each removed on request as Revo Uninstaller does it: its own
 * uninstaller, then a deep clean of what it leaves (folders, registry entries, shortcuts, and what Windows keeps about it).
 * Nothing goes without a press; what removed apps left in this user's folders was already cleared when setup opened.
 * Then junk past a gigabyte (old shader caches, mostly), as PC › Needs you has it.
 */
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

/** One row per replaced app on this PC, with Remove (or Clear, for what a removed one left) as PC › Apps has it. */
function ReplacedAppRows({ apps, onApps, empty }: { apps: ReplacedApp[]; onApps: (next: ReplacedApp[]) => void; empty: boolean }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string | undefined>>({})
  // The apps there when the step opened (or cleared as it opened) stay listed, a removed one marked Removed.
  const [listed] = useState(() => new Set(apps.filter(app => app.installed || app.leftovers > 0 || app.cleared).map(app => app.id)))
  const shown = apps.filter(app => listed.has(app.id))

  // The button says it in itself (ConfirmButton): its ring, then its check, and only then does the row say Removed; a
  // refusal turns it Bad with the reason under the row.
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
        // What a Clear will delete says so before it asks; what is done needs no word beyond Removed.
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
