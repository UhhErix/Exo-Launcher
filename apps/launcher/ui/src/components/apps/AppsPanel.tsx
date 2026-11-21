import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Check, ChevronDown, ChevronUp, Close, Search } from '../../brand/icons'
import { cleanOutcome } from '../../lib/junkClean'
import { host, onHostEvent, type AppFind, type AppLeftover, type InstalledApp, type JunkItem, type LeftoverSummary, type StartupItem, type StartupState, type WindowsApp, type WindowsAppLine } from '../../lib/host'
import { report } from '../../lib/report'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { errorText, type PlainError } from '../../lib/errorWords'
import { Switch } from '../Switch'
import { ErrorNote } from '../ui/ErrorNote'
import { Wait, sample, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'
import { ArtImg } from '../ui/ArtImg'

type View = 'apps' | 'windows' | 'startup'
type Sort = 'name' | 'used' | 'size'
type Removed = { id: string; name: string; find?: AppFind; message?: string }

const DAY = 86_400_000
/** How long unopened counts as unused, and how big an app has to be for that to be worth saying. */
const UNUSED_DAYS = 90
const UNUSED_BYTES = 200e6
/** Junk Exo hasn't got to yet is only worth a row past this: it cleans the rest itself. */
const JUNK_WORTH = 100e6

function size(bytes: number | null | undefined): string {
  if (!bytes || bytes <= 0) return ''
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`
  if (bytes >= 1e6) return `${Math.round(bytes / 1e6)} MB`
  return `${Math.max(1, Math.round(bytes / 1e3))} KB`
}

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/** "3 folders (1.2 GB), 9 registry entries and 2 shortcuts": what an app left, or what was cleared of it. */
function said(left: LeftoverSummary, sized = true): string {
  const parts: string[] = []
  if (left.folders > 0) parts.push(count(left.folders, 'folder', 'folders'))
  if (left.files > 0) parts.push(count(left.files, 'file', 'files'))
  const amount = size(left.bytes)
  if (sized && amount && parts.length > 0) parts[parts.length - 1] += ` (${amount})`
  if (left.registry > 0) parts.push(count(left.registry, 'registry entry', 'registry entries'))
  if (left.shortcuts > 0) parts.push(count(left.shortcuts, 'shortcut', 'shortcuts'))
  return parts.length < 2 ? parts.join('') : `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`
}

/** "Cleared 1 folder and 13 registry entries: 6 MB back." */
function clearedLine(what: LeftoverSummary): string {
  const back = size(what.bytes)
  return `Cleared ${said(what, false)}${back ? `: ${back} back` : ''}.`
}

function summary(items: AppLeftover[]): LeftoverSummary {
  const of = (...kinds: AppLeftover['kind'][]) => items.filter(item => kinds.includes(item.kind)).length
  return { folders: of('folder'), files: of('file'), registry: of('registry', 'service', 'task', 'driver'), shortcuts: of('shortcut'), bytes: items.reduce((sum, item) => sum + item.bytes, 0) }
}

const fullDay = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
const month = new Intl.DateTimeFormat(undefined, { month: 'short', year: 'numeric' })
const at = (date: string) => new Date(`${date}T12:00:00`)

/** When it was last opened, as a person says it; nothing when nobody knows, never a dash. */
function lastUsed(app: InstalledApp): string | null {
  if (app.quietSince) return `Before ${month.format(at(app.quietSince))}`
  if (!app.lastOpened) return null
  const days = Math.floor((Date.now() - at(app.lastOpened).getTime()) / DAY)
  if (days < 1) return 'Today'
  if (days < 2) return 'Yesterday'
  return days < 14 ? `${days} days ago` : month.format(at(app.lastOpened))
}

const unused = (app: InstalledApp) => (app.bytes ?? 0) >= UNUSED_BYTES && app.removable && !app.replaces &&
  (!!app.quietSince || (!!app.lastOpened && Date.now() - at(app.lastOpened).getTime() > UNUSED_DAYS * DAY))

/** What Windows, games or Exo need: a part of how the PC works rather than an app you'd look for. Listed after the apps. */
const isPart = (app: InstalledApp) => !app.removable || app.care === 'runtime'

/** What Select can pick: an app with its uninstaller, that can go, and isn't one Exo does the job of (those go one by one). */
const pickable = (app: InstalledApp) => app.removable && app.uninstaller && !app.replaces

function sorted(list: InstalledApp[], sort: Sort, back: boolean): InstalledApp[] {
  const longest = (app: InstalledApp) => app.quietSince ?? app.lastOpened ?? '9999'
  const by: Record<Sort, (a: InstalledApp, b: InstalledApp) => number> = {
    name: (a, b) => a.name.localeCompare(b.name),
    size: (a, b) => (b.bytes ?? 0) - (a.bytes ?? 0),
    // Longest unopened first; what Windows has no record of opening after them.
    used: (a, b) => longest(a).localeCompare(longest(b)) || (b.bytes ?? 0) - (a.bytes ?? 0),
  }
  const next = [...list].sort(by[sort])
  return back ? next.reverse() : next
}

/**
 * PC › Apps, Exo's uninstaller (DESIGN.md › PC › Apps), as one list: every app with its publisher, when it was last used, its
 * size, and whether it starts with Windows; pointed at, it offers Uninstall, asked in place. What Windows, games and Exo need
 * follows out of the way. The work is done before you look: above the list, only while there is any, whatever is already
 * prepared, each one press (startup entries that can go, what Exo does the job of, apps not opened in months). The rest needs no
 * press: pressing Uninstall covers what the program left, cleared by itself once its uninstaller is done, and old leftovers wait for
 * the next uninstall (the idle sweep is off: JunkAutoClean.OrphansBySelf). One line says what went, with Put back; what needs Windows' permission waits for a press or
 * the next uninstall's prompt. What goes can come back from Removed. Startup and Removed are the list's two other views.
 */
/** Merge freshly loaded icon data URIs without overwriting cached icons with null. */
function mergeIcons(current: Record<string, string | null>, incoming: Record<string, string | null>): Record<string, string | null> {
  const merged = { ...current }
  for (const [id, icon] of Object.entries(incoming)) {
    if (icon || !(id in merged)) merged[id] = icon
  }
  return merged
}

export function AppsPanel({ active }: { active: boolean }) {
  const [programs, setPrograms] = useState<InstalledApp[] | null>(null)
  const [icons, setIcons] = useState<Record<string, string | null>>({})
  const [view, setView] = useState<View>('apps')
  const [query, setQuery] = useState('')
  const [sort, setSort] = useState<{ by: Sort; back: boolean }>({ by: 'size', back: false })
  const [onlyUnused, setOnlyUnused] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  const [removed, setRemoved] = useState<Removed[]>([])
  const [tidy, setTidy] = useState<AppFind | null>(null)
  const [wentByItself, setWentByItself] = useState<NonNullable<AppFind['autoCleared']>>([])
  // Select: the apps picked to uninstall together (null: not selecting), and how far a run of them has got.
  const [picked, setPicked] = useState<Set<string> | null>(null)
  const [run, setRun] = useState<{ at: number; of: number } | null>(null)
  const startup = useStartup(active)
  const read = useRef(false)

  /** The list (the host's last at once, apps.changed when its read behind differs), then the icons it hasn't drawn yet. */
  async function load(known: Record<string, string | null> = {}) {
    try {
      const result = await host.appsList()
      setPrograms(result.programs)
      const missing = result.programs.map(app => app.id).filter(id => !(id in known) || !known[id])
      for (let from = 0; from < missing.length; from += 100) {
        const chunk = await host.appsIcons(missing.slice(from, from + 100))
        setIcons(current => mergeIcons(current, chunk.icons))
      }
    } catch (error) {
      report(error, 'AppsPanel.list')
      setPrograms(current => current ?? [])
    }
  }

  /** What programs already gone left: found by the host in the background, shown when it's ready. */
  const readTidy = (fresh = false) => host.appsTidy(fresh).then(
    result => {
      setTidy(result.ok && result.items.length > 0 ? result : null)
      // Said once by the host: kept here for as long as the sheet is open.
      if (result.autoCleared?.length) setWentByItself(current => [...current, ...(result.autoCleared ?? []).filter(item => !current.some(other => other.id === item.id))])
    },
    (error: unknown) => report(error, 'AppsPanel.tidy', { level: 'warn' }),
  )

  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    void load()
    void readTidy()
  }, [active])

  // The list shown first was the host's last; its read behind found another (an app installed or gone outside Exo).
  const iconsNow = useRef(icons)
  iconsNow.current = icons
  useEffect(() => onHostEvent('apps.changed', () => {
    if (!read.current) return
    void load(iconsNow.current)
    void readTidy()
  }), [])

  const startsWithWindows = useMemo(() => new Set(startup.state?.items.filter(item => item.enabled && item.app).map(item => item.app) ?? []), [startup.state])

  const { apps, parts } = useMemo(() => {
    const words = query.trim().toLowerCase()
    let list = programs ?? []
    if (words) list = list.filter(app => `${app.name} ${app.publisher ?? ''}`.toLowerCase().includes(words))
    if (onlyUnused) list = list.filter(unused)
    const inOrder = sorted(list, sort.by, sort.back)
    return { apps: inOrder.filter(app => !isPart(app)), parts: inOrder.filter(isPart) }
  }, [programs, query, sort, onlyUnused])

  if (!programs) return (
    <div className="exo-apps">
      <Head view={view} onView={setView} query={query} onQuery={setQuery} />
      <Wait label="Reading this PC's apps"><Columns knowsUse sort={sort} onSort={() => { }} run={null} picked={null} busy={false} onUninstallPicked={() => { }} onSelect={() => { }} /></Wait>
      <div className="exo-apps-list"><Wait>{sampleLines(10)}</Wait></div>
    </div>
  )

  const mainApps = programs.filter(app => !isPart(app))
  const total = mainApps.reduce((sum, app) => sum + (app.bytes ?? 0), 0)
  const knowsUse = programs.some(app => app.lastOpened || app.quietSince)
  const setError = (id: string, message: string | null) => setErrors(current => ({ ...current, [id]: message }))

  /** One app's removal: its uninstaller, Exo's clean-up for one it does the job of, or only a look at what's left when its uninstaller is gone. */
  const removal = (app: InstalledApp, quiet: boolean) => app.replaces ? host.replacedRemove(app.replaces)
    : app.uninstaller ? host.appsUninstall(app.id, quiet) : host.appsScan(app.id)

  /** What went, said in the Ready view; then the list read again. */
  async function afterRemoval(app: InstalledApp, result: Awaited<ReturnType<typeof removal>>) {
    if (result.ok) {
      if (app.replaces) setRemoved(current => [{ id: app.id, name: app.name, message: `${app.name} is gone.` }, ...current])
      else setRemoved(current => [{ id: app.id, name: app.name, find: result as AppFind }, ...current.filter(other => other.id !== app.id)])
    }
    const next = await host.appsList()
    setPrograms(next.programs)
  }

  /**
   * One of a confirmed run, quietly where its installer allows. True when it went; a refusal shows under its row.
   */
  async function uninstall(app: InstalledApp, quiet = false): Promise<boolean> {
    setBusy(app.id)
    setError(app.id, null)
    let gone = false
    try {
      const result = await removal(app, quiet)
      if (!result.ok) setError(app.id, errorText(result.message, `${app.name} wasn't removed. Try again.`))
      gone = result.ok
      await afterRemoval(app, result)
    } catch (error) {
      report(error, 'AppsPanel.uninstall')
      setError(app.id, errorText(error, `${app.name} wasn't removed. Try again.`))
    } finally {
      setBusy(null)
    }
    return gone
  }

  /**
   * A row's own press (ActionButton): its ring while it works, then its check, and only then does the row go (`onDone`);
   * a refusal turns the button Bad with the reason beside it, and the next press is Retry. The list is read again
   * either way.
   */
  async function pressUninstall(app: InstalledApp) {
    setBusy(app.id)
    setError(app.id, null)
    try {
      const result = await removal(app, false)
      if (!result.ok) void afterRemoval(app, result).catch(error => report(error, 'AppsPanel.list'))
      return { ...result, settle: () => void afterRemoval(app, result).catch(error => report(error, 'AppsPanel.list')) }
    } finally {
      setBusy(null)
    }
  }

  /**
   * Several, asked once in place, then each in turn, quietly where its installer allows; a restore point first when asked
   * for (no restore point, nothing goes). One line after says how many went.
   */
  async function uninstallMany(ids: string[]) {
    if (ids.length === 0 || busy || run || !programs) return
    setError('many', null)
    try {
      const agreed = await host.appsConfirm(ids)
      // Each app left out says why, beside its own row (Exo logs the same reason).
      for (const skip of agreed.skipped) setError(skip.id, skip.reason)
      if (!agreed.ok) {
        setError('many', errorText(agreed.message, 'Nothing was removed.'))
        return
      }
      setRun({ at: 0, of: agreed.ids.length })
      const point = await host.appsRestorePoint()
      if (!point.ok && point.declined) {
        setError('many', errorText(point.message, 'Windows’ permission was declined. Nothing was removed.'))
        return
      }
      setPicked(null)
      const chosen = agreed.ids.map(id => programs.find(app => app.id === id)).filter((app): app is InstalledApp => !!app)
      let went = 0
      for (const [index, app] of chosen.entries()) {
        setRun({ at: index + 1, of: chosen.length })
        if (await uninstall(app, true)) went++
      }
      if (chosen.length > 1) {
        const stayed = chosen.length - went
        const line = `Uninstalled ${went} of ${chosen.length}${stayed > 0 ? `: ${count(stayed, 'app says', 'apps say')} why beside ${stayed === 1 ? 'it' : 'them'}` : ''}.`
        setRemoved(current => [{ id: `run-${Date.now()}`, name: '', message: line }, ...current])
      }
    } catch (error) {
      report(error, 'AppsPanel.uninstallMany')
      setError('many', 'The apps weren’t removed.')
    } finally {
      setRun(null)
    }
  }

  const pick = (id: string) => setPicked(current => {
    const next = new Set(current)
    if (!next.delete(id)) next.add(id)
    return next
  })

  const line = (app: InstalledApp) => (
    <AppLine key={app.id} app={app} icon={icons[app.id] ?? null} starts={startsWithWindows.has(app.id)} showUsed={knowsUse} error={errors[app.id] ?? null}
      working={busy === app.id} disabled={(busy !== null && busy !== app.id) || run !== null} picked={picked && pickable(app) ? picked.has(app.id) : null}
      onPick={() => pick(app.id)} onUninstall={() => pressUninstall(app)} onFailed={why => setError(app.id, why)} />
  )

  const searching = query.trim().length > 0
  const pressSort = (by: Sort) => setSort(current => current.by === by ? { by, back: !current.back } : { by, back: false })
  const headFacts = [mainApps.length > 0 ? `${mainApps.length} apps` : null, total > 0 ? size(total) : null].filter(Boolean).join(' · ')

  return (
    <div className="exo-apps">
      <Head view={view} onView={setView} query={query} onQuery={setQuery}
        facts={headFacts || undefined} />
      {view === 'apps' ? (
        <Columns knowsUse={knowsUse} sort={sort} onSort={pressSort} run={run} picked={picked} busy={busy !== null}
          onUninstallPicked={ids => void uninstallMany(ids)} onSelect={() => setPicked(current => current ? null : new Set())} />
      ) : null}
      {errors.many ? <ErrorNote className="exo-set-error" message={errors.many} /> : null}
      {/* The list scrolls under the head and fades into the sheet's foot. */}
      <div className="exo-apps-list">
        {view === 'apps' ? (
          <>
            {!searching && !onlyUnused ? (
              <Ready active={active} programs={programs} removed={removed} tidy={tidy} wentByItself={wentByItself} busy={busy !== null || run !== null} startup={startup}
                onLeaveRemoved={id => setRemoved(current => current.filter(item => item.id !== id))} onLeaveTidy={() => setTidy(null)}
                onChanged={() => { void load(iconsNow.current); void readTidy(true) }} onUninstallMany={ids => void uninstallMany(ids)} onShowUnused={() => { setOnlyUnused(true); setSort({ by: 'size', back: false }) }} />
            ) : null}
            {onlyUnused ? (
              <div className="exo-apps-filter">
                <span>Not opened in three months</span>
                <button type="button" className="exo-set-btn is-compact" onClick={() => setOnlyUnused(false)}>Show all</button>
              </div>
            ) : null}
            <section aria-label="Apps">{apps.map(line)}{apps.length === 0 && parts.length === 0 ? <p className="exo-apps-none">No apps match</p> : null}</section>
            <Parts count={parts.length} searching={searching}>{parts.map(line)}</Parts>
          </>
        ) : view === 'windows' ? (
          <WindowsAppsView active={active} query={query} onChanged={() => void load(iconsNow.current)} />
        ) : (
          <StartupView startup={startup} query={query} />
        )}
      </div>
    </div>
  )
}

/**
 * The columns over the list: what sorts it, and Select at the end, where a row's action is. With apps picked: Restore point
 * (a System Restore point first, Windows asks) and Uninstall, asked in place; the run goes quietly where each installer allows.
 */
function Columns({ knowsUse, sort, onSort, run, picked, busy, onUninstallPicked, onSelect }: {
  knowsUse: boolean
  sort: { by: Sort; back: boolean }
  onSort: (by: Sort) => void
  run: { at: number; of: number } | null
  picked: Set<string> | null
  busy: boolean
  onUninstallPicked: (ids: string[]) => void
  onSelect: () => void
}) {
  // A run, or apps picked: its controls take the columns after Name, so nothing sits under them.
  const wide = run !== null || (picked?.size ?? 0) > 0
  return (
    <div className={knowsUse ? 'exo-apps-columns' : 'exo-apps-columns is-short'} role="row">
      <span />
      <SortHead label="Name" by="name" sort={sort} onSort={onSort} />
      {knowsUse && !wide ? <SortHead label="Last used" by="used" sort={sort} onSort={onSort} /> : null}
      {!wide ? <SortHead label="Size" by="size" sort={sort} onSort={onSort} end /> : null}
      <div className={wide ? 'exo-apps-select is-wide' : 'exo-apps-select'}>
        {run ? <RunStatus run={run} /> : (
          <>
            {picked && picked.size > 0 ? <PickedActions picked={picked} busy={busy} onUninstallPicked={onUninstallPicked} /> : null}
            <button type="button" className="exo-set-btn is-compact" aria-pressed={picked !== null} onClick={onSelect}>{picked ? 'Cancel' : 'Select'}</button>
          </>
        )}
      </div>
    </div>
  )
}

/** How far a run has got: its restore point first (at 0), then each app. */
function RunStatus({ run }: { run: { at: number; of: number } }) {
  const making = run.at === 0
  return (
    <span className="exo-apps-run" role="status">
      <Spinner label={making ? 'Preparing uninstall' : `Uninstalling ${run.at} of ${run.of}`} />{making ? 'Preparing…' : `${run.at} of ${run.of}`}
    </span>
  )
}

/** With apps picked: Uninstall, asked in place (automatic daily restore point behind Windows' prompt). */
function PickedActions({ picked, busy, onUninstallPicked }: {
  picked: Set<string>
  busy: boolean
  onUninstallPicked: (ids: string[]) => void
}) {
  return (
    <ConfirmButton className="exo-set-btn is-compact is-danger" danger disabled={busy} question={`Uninstall ${picked.size}?`} onConfirm={() => onUninstallPicked([...picked])}>Uninstall {picked.size}</ConfirmButton>
  )
}

/** What Windows, games and Exo need: out of the way until asked for, or until a search finds it. */
function Parts({ count: how, searching, children }: { count: number; searching: boolean; children: ReactNode }) {
  const [open, setOpen] = useState(false)
  if (how === 0) return null
  return (
    <section aria-label="Needed by Windows, games or Exo">
      {searching ? <h3 className="exo-apps-group">Needed by Windows, games or Exo ({how})</h3> : (
        <button type="button" className="exo-apps-more" aria-expanded={open} onClick={() => setOpen(value => !value)}>
          <span>Needed by Windows, games or Exo ({how})</span>
          {open ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
        </button>
      )}
      {open || searching ? children : null}
    </section>
  )
}

function Head({ view, onView, query, onQuery, facts }: { view: View; onView: (next: View) => void; query: string; onQuery: (next: string) => void; facts?: string }) {
  const views: { id: View; label: string }[] = [{ id: 'apps', label: 'Apps' }, { id: 'windows', label: 'Windows' }, { id: 'startup', label: 'Startup' }]
  return (
    <div className="exo-apps-head">
      <h2 id="pc-apps-title">Apps{facts ? <span className="exo-apps-facts">{facts}</span> : null}</h2>
      <div className="exo-shelf-chips" role="group" aria-label="What to show">
        {views.map(choice => <button key={choice.id} type="button" className="exo-shelf-chip" aria-pressed={view === choice.id} onClick={() => onView(choice.id)}>{choice.label}</button>)}
      </div>
      <label className="exo-apps-search">
        <Search size={14} />
        <input type="search" value={query} placeholder="Search" aria-label="Search apps" onChange={event => onQuery(event.target.value)}
          onKeyDown={event => {
            if (event.key !== 'Escape' || !query) return
            event.preventDefault()
            event.stopPropagation()
            onQuery('')
          }} />
        {query ? (
          <button type="button" className="exo-apps-search-clear" aria-label="Clear search" onClick={() => onQuery('')}>
            <Close size={12} />
          </button>
        ) : null}
      </label>
    </div>
  )
}

function SortHead({ label, by, sort, onSort, end }: { label: string; by: Sort; sort: { by: Sort; back: boolean }; onSort: (by: Sort) => void; end?: boolean }) {
  const current = sort.by === by
  // Each column's natural first order: names from A, the biggest, the longest unopened.
  const ascending = (by === 'size') === sort.back
  return (
    <button type="button" className={end ? 'exo-apps-sort is-end' : 'exo-apps-sort'} aria-sort={current ? (ascending ? 'ascending' : 'descending') : undefined} data-current={current || undefined} onClick={() => onSort(by)}>
      {label}{current ? (ascending ? <ChevronUp size={12} /> : <ChevronDown size={12} />) : null}
    </button>
  )
}

type AppTag = { text: string; tone?: 'warn' | 'good' }

/** A row of the list: its icon, its name with optional status tags over one line of detail, the columns, and what can be done with it. */
function Line({ icon, name, tags, detail, used, amount, error, showUsed, keep, startup, children }: {
  icon?: ReactNode
  name: string
  tags?: AppTag[]
  detail?: ReactNode
  used?: string | null
  amount?: string
  error?: string | null
  showUsed?: boolean
  /** The action stays in view: it is what the row is for, or it is at work. */
  keep?: boolean
  startup?: boolean
  children?: ReactNode
}) {
  return (
    <div className={`exo-apps-row${showUsed === false ? ' is-short' : ''}${keep ? ' is-kept' : ''}${startup ? ' is-startup' : ''}`}>
      <span className="exo-apps-icon">{icon}</span>
      <div className="exo-apps-copy">
        <span className="exo-apps-title">
          <span className="exo-apps-name">{name}</span>
          {tags?.length ? (
            <span className="exo-apps-tags">
              {tags.map(tag => <span key={tag.text} className={tag.tone ? `exo-apps-tag is-${tag.tone}` : 'exo-apps-tag'}>{tag.text}</span>)}
            </span>
          ) : null}
        </span>
        {detail ? <span className="exo-apps-detail">{detail}</span> : null}
      </div>
      {showUsed === false ? null : <span className="exo-apps-used">{used}</span>}
      <span className="exo-apps-size">{amount}</span>
      <div className="exo-apps-act">{children}</div>
      {error ? <ErrorNote className="exo-apps-error" message={error} /> : null}
    </div>
  )
}

/** A list's rows on their way. */
const sampleLines = (n: number) => <section>{times(n, i => <Line icon={<span style={{ display: 'block', width: '100%', height: '100%', borderRadius: 'inherit' }} skullet-block="" />} name={sample(i)} detail={sample(i + 2)} used="Last month" amount="000 MB" />)}</section>

function AppIcon({ name, icon }: { name: string; icon: string | null }) {
  return <ArtImg src={icon} loading="lazy" decoding="async" draggable={false} fallback={<span aria-hidden="true">{name.slice(0, 1)}</span>} />
}

function AppLine({ app, icon, starts, showUsed, error, working, disabled, picked, onPick, onUninstall, onFailed }: {
  app: InstalledApp
  icon: string | null
  starts: boolean
  showUsed: boolean
  error: string | null
  working: boolean
  disabled: boolean
  picked: boolean | null
  onPick: () => void
  onUninstall: () => Promise<{ ok: boolean; message?: string | null; settle: () => void }>
  onFailed: (why: string) => void
}) {
  const tags: AppTag[] = [
    ...(starts ? [{ text: 'Startup' }] : []),
    ...(app.replaces ? [{ text: 'Exo replaces', tone: 'good' as const }] : app.bloat ? [{ text: 'Bloat', tone: 'warn' as const }] : []),
  ]
  const detail = working
    ? (app.replaces ? `Removing ${app.name}` : app.uninstaller ? 'Finish in its window' : 'Looking for what it left')
    : [!app.uninstaller ? 'No uninstaller' : null, whatItIs(app)].filter(Boolean).join(' · ')
  return (
    <Line icon={<AppIcon name={app.name} icon={icon} />} name={app.name} tags={tags} detail={detail} used={lastUsed(app)} amount={size(app.bytes)} error={error} showUsed={showUsed} keep={working || picked !== null}>
      {picked !== null ? (
        <button type="button" className={picked ? 'exo-apps-pick is-picked' : 'exo-apps-pick'} aria-pressed={picked} aria-label={`Pick ${app.name}`} onClick={onPick}>
          {picked ? <Check size={12} /> : null}
        </button>
      ) : !app.removable ? null : (
        <AppAction app={app} disabled={disabled} onUninstall={onUninstall} onFailed={onFailed} />
      )}
    </Line>
  )
}

/** The one thing worth saying under its name: why it stays or why you'd miss it, else who makes it. */
function whatItIs(app: InstalledApp): string | null {
  if (app.replaces) return 'Exo does its job'
  if (app.kept) return app.kept
  if (app.bloat) return app.bloat
  if (app.care === 'runtime') return 'Other apps may need it'
  return app.caution?.replace(/\.$/, '') ?? app.publisher
}

/**
 * Uninstall or Remove, asked in place; Look only looks first, so what it found shows before anything goes. The button
 * says how it went (ActionButton): its ring, then its check, and then the row goes; or Retry with why.
 */
function AppAction({ app, disabled, onUninstall, onFailed }: {
  app: InstalledApp
  disabled: boolean
  onUninstall: () => Promise<{ ok: boolean; message?: string | null; settle: () => void }>
  onFailed: (why: string) => void
}) {
  const settle = (value: unknown) => (value as { settle?: () => void } | undefined)?.settle?.()
  // The row's columns leave no room beside the button: the reason is the row's error line, under it.
  const failed = (error: PlainError) => onFailed(error.text)
  // An uninstaller runs in its own window for as long as the player takes: no time limit.
  if (!app.uninstaller && !app.replaces) {
    return <ActionButton className="exo-set-btn is-compact" disabled={disabled} timeoutMs={null} context="AppsPanel.look"
      fallback={`Exo couldn’t look at what ${app.name} left. Try again.`} why="none" onFailed={failed} onPress={onUninstall} onDone={settle}>Look</ActionButton>
  }
  const action = app.replaces ? 'Remove' : 'Uninstall'
  return <ConfirmButton className="exo-set-btn is-compact is-danger" danger disabled={disabled} question={`${action}?`} timeoutMs={null}
    context="AppsPanel.uninstall" fallback={`${app.name} wasn't removed. Try again.`} why="none" onFailed={failed} onConfirm={onUninstall} onDone={settle}>{action}</ConfirmButton>
}

// ---- what's ready ----

function Ready({ active, programs, removed, tidy, wentByItself, busy, startup, onLeaveRemoved, onLeaveTidy, onChanged, onUninstallMany, onShowUnused }: {
  active: boolean
  programs: InstalledApp[]
  removed: Removed[]
  tidy: AppFind | null
  wentByItself: NonNullable<AppFind['autoCleared']>
  busy: boolean
  startup: Startup
  onLeaveRemoved: (id: string) => void
  onLeaveTidy: () => void
  onChanged: () => void
  onUninstallMany: (ids: string[]) => void
  onShowUnused: () => void
}) {
  const bloat = programs.filter(app => app.bloat && pickable(app))
  const replaced = programs.filter(app => app.replaces && app.removable)
  const idle = programs.filter(unused)
  const unneeded = startup.unneeded
  return (
    <section className="exo-apps-ready" aria-label="Ready for you">
      {removed.map(item => item.find
        ? <GoneRow key={item.id} name={item.name} find={item.find} onLeave={() => onLeaveRemoved(item.id)} onChanged={onChanged} busy={busy} />
        : <ReadyRow key={item.id} title={item.message ?? `${item.name} is gone`} done />)}
      {wentByItself.map(item => <ClearedRow key={item.id} line={`${item.name}: ${clearedLine(item.cleared)}`} undo={item.id} onChanged={onChanged} />)}
      {tidy ? <FindRow find={tidy} title="Left by programs that are gone" names waiting onLeave={onLeaveTidy} onChanged={onChanged} busy={busy} /> : null}
      {replaced.length > 0 ? (
        <ReadyRow title={`Exo does the job of ${names(replaced.map(app => app.name))}`} detail={size(replaced.reduce((sum, app) => sum + (app.bytes ?? 0), 0))}>
          <ConfirmButton className="exo-set-btn is-danger" danger disabled={busy} question={`Remove ${replaced.length}?`} onConfirm={() => onUninstallMany(replaced.map(app => app.id))}>Remove {replaced.length}</ConfirmButton>
        </ReadyRow>
      ) : null}
      {bloat.length > 0 ? (
        <ReadyRow title={count(bloat.length, 'app you didn’t ask for', 'apps you didn’t ask for')} detail={names(bloat.map(app => app.name))}>
          <ConfirmButton className="exo-set-btn is-danger" danger disabled={busy} question={`Uninstall ${bloat.length}?`} onConfirm={() => onUninstallMany(bloat.map(app => app.id))}>Remove {bloat.length}</ConfirmButton>
        </ReadyRow>
      ) : null}
      {unneeded > 0 ? (
        <ReadyRow title={`${count(unneeded, 'thing', 'things')} starting with Windows can go`} error={startup.errors.clean || undefined}
          detail={names(startup.state?.items.filter(item => item.unneeded).map(item => item.name) ?? [])}>
          <ActionButton className="exo-set-btn" context="AppsPanel.startupClean" fallback="Startup wasn’t cleaned up. Try again." why="none"
            onPress={startup.clean} onFailed={failure => startup.tell('clean', failure.text)} onDone={startup.settle}>Turn off {unneeded}</ActionButton>
        </ReadyRow>
      ) : null}
      <JunkRow active={active} />
      <InstallerCacheRow active={active} busy={busy} onChanged={onChanged} />
      {idle.length > 0 ? (
        <ReadyRow title={`${count(idle.length, 'app', 'apps')} not opened in three months`} detail={`${size(idle.reduce((sum, app) => sum + (app.bytes ?? 0), 0))}: ${names(idle.map(app => app.name))}`}>
          <button type="button" className="exo-set-btn" onClick={onShowUnused}>Show</button>
        </ReadyRow>
      ) : null}
    </section>
  )
}

/** "Discord, Game Bar and 3 more". */
function names(list: string[]): string {
  const unique = [...new Set(list)]
  if (unique.length <= 3) return unique.length < 2 ? unique.join('') : `${unique.slice(0, -1).join(', ')} and ${unique[unique.length - 1]}`
  return `${unique.slice(0, 2).join(', ')} and ${unique.length - 2} more`
}

function ReadyRow({ title, detail, done, error, children }: { title: string; detail?: ReactNode; done?: boolean; error?: string; children?: ReactNode }) {
  return (
    <div className="exo-apps-ready-row">
      <div className="exo-apps-copy">
        <span className={done ? 'exo-apps-name is-done' : 'exo-apps-name'}>{title}</span>
        {detail ? <span className="exo-apps-detail">{detail}</span> : null}
      </div>
      <div className="exo-apps-ready-act">{children}</div>
      {error ? <ErrorNote className="exo-apps-error" message={error} /> : null}
    </div>
  )
}

/** What went, in one plain line, with Put back beside it. */
function ClearedRow({ line, undo, onChanged }: { line: string; undo: string | null | undefined; onChanged: () => void }) {
  const [back, setBack] = useState(false)
  const [error, setError] = useState<string | null>(null)
  if (back) return <ReadyRow title="Put back" done />
  return (
    <ReadyRow title={line} done error={error ?? undefined}>
      {undo ? (
        <ActionButton className="exo-set-btn" context="AppsPanel.undo" fallback="It wasn’t put back. Try again." why="none"
          onPress={() => { setError(null); return host.appsUndo(undo) }}
          onFailed={failure => { setError(failure.text); onChanged() }} onDone={() => { setBack(true); onChanged() }}>Put back</ActionButton>
      ) : null}
    </ReadyRow>
  )
}

/**
 * After an uninstall. Pressing Uninstall covered what it left, so that has gone by itself: one line of what went, and Put back.
 * What's still there says why under it: Windows' permission declined (a press asks again), or not this program's to take.
 * Where something went wrong, or for a look at what's left of a program with no uninstaller, it's only found, for a press.
 */
function GoneRow({ name, find, onLeave, onChanged, busy }: { name: string; find: AppFind; onLeave: () => void; onChanged: () => void; busy: boolean }) {
  if (!find.auto) return <FindRow find={find} title={`What’s left of ${name}`} onLeave={onLeave} onChanged={onChanged} busy={busy} />
  const went = find.cleared && find.cleared.folders + find.cleared.files + find.cleared.registry + find.cleared.shortcuts > 0 ? find.cleared : null
  const line = went ? `${name} is gone. ${clearedLine(went)}` : find.items.length > 0 ? `${name} is gone.` : `${name} is gone, and left nothing behind.`
  return (
    <>
      <ClearedRow line={line} undo={find.undo} onChanged={onChanged} />
      {find.items.length > 0 ? <FindRow find={find} title={find.declined ? 'Windows’ permission was declined' : 'Remaining files'} onLeave={onLeave} onChanged={onChanged} busy={busy} /> : null}
      {find.items.length === 0 ? <DefaultsRow name={name} types={find.defaults ?? []} /> : null}
    </>
  )
}

/**
 * A find, ready: what's selected, one press to clear it, Review to see every item and change the selection, Leave to keep it all
 * and not be asked again. Nothing here needs a question: files go to the Recycle Bin and entries to a saved copy first, and once
 * cleared, Put back is right here.
 */
function FindRow({ find, title, names: showNames, waiting, onLeave, onChanged, busy }: {
  find: AppFind
  title: string
  names?: boolean
  waiting?: boolean
  onLeave: () => void
  onChanged: () => void
  busy: boolean
}) {
  const [picks, setPicks] = useState(() => new Set(find.items.filter(item => item.selected).map(item => item.id)))
  const [open, setOpen] = useState(false)
  const [cleared, setCleared] = useState<{ what: LeftoverSummary; undo: string | null } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const chosen = find.items.filter(item => picks.has(item.id))

  /** The button's ring, then its check, then what went (and Put back) in its place; a refusal shows under the row. */
  async function clear() {
    setError(null)
    const result = await host.appsClean(find.token, [...picks])
    // Some went before it stopped: what went shows, with the reason.
    if (!result.ok && result.cleared) { setCleared({ what: result.cleared, undo: result.undo ?? null }); onChanged() }
    return result
  }
  const cleanedUp = (value: unknown) => {
    const result = value as Awaited<ReturnType<typeof host.appsClean>>
    if (result.cleared) setCleared({ what: result.cleared, undo: result.undo ?? null })
    setOpen(false)
    onChanged()
  }

  const defaults = <DefaultsRow name={showNames ? 'apps that are gone' : find.name} types={find.defaults ?? []} />
  if (cleared) return <><ClearedRow line={clearedLine(cleared.what)} undo={cleared.undo} onChanged={onChanged} />{defaults}</>
  if (find.items.length === 0) return <><ReadyRow title={title} done />{defaults}</>
  return (
    <>
      <ReadyRow title={title} error={error ?? undefined} detail={findDetail(find, showNames, waiting)}>
        {picks.size > 0 ? (
          <ActionButton className="exo-set-btn is-primary" disabled={busy} context="AppsPanel.clean" fallback="Nothing was deleted. Try again." why="none"
            onPress={clear} onFailed={failure => setError(failure.text)} onDone={cleanedUp}>Clean up {chosen.length}</ActionButton>
        ) : null}
        <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(value => !value)}>{open ? 'Hide' : 'Review'}</button>
        <button type="button" className="exo-set-btn" onClick={() => { void host.appsLeave(find.token); onLeave() }}>Leave</button>
      </ReadyRow>
      {open ? (
        <ul className="exo-apps-items">
          {find.items.map(item => (
            <li key={item.id}>
              <button type="button" className={picks.has(item.id) ? 'exo-apps-pick is-picked' : 'exo-apps-pick'} aria-pressed={picks.has(item.id)} aria-label={`Clear ${item.path}`}
                onClick={() => setPicks(current => {
                  const next = new Set(current)
                  if (!next.delete(item.id)) next.add(item.id)
                  return next
                })}>
                {picks.has(item.id) ? <Check size={12} /> : null}
              </button>
              <span className="exo-apps-item">
                <span className="exo-apps-path">{item.path}</span>
                <span className="exo-apps-note">{[item.because, item.store ? 'With its driver store package, saved first' : null, item.note].filter(Boolean).join(' · ')}</span>
              </span>
              <span className="exo-apps-item-size">{size(item.bytes)}</span>
            </li>
          ))}
        </ul>
      ) : null}
      {defaults}
    </>
  )
}

/**
 * File types Windows still opens with an app that's gone. Windows signs that choice (UserChoice), so Exo never writes it:
 * Default apps opens, where each type gets a new app.
 */
function DefaultsRow({ name, types }: { name: string; types: string[] }) {
  const [error, setError] = useState<string | null>(null)
  if (types.length === 0) return null
  return (
    <ReadyRow title={`${count(types.length, 'file type still opens', 'file types still open')} with ${name}`} error={error ?? undefined}
      detail={`${types.join(', ')} · Choose another app for ${types.length === 1 ? 'it' : 'each'} in Default apps`}>
      <ActionButton className="exo-set-btn" context="AppsPanel.defaults" fallback="Default apps didn’t open. Try again." why="none"
        onPress={() => { setError(null); return host.appsOpenDefaults() }} onFailed={failure => setError(failure.text)}>Default apps</ActionButton>
    </ReadyRow>
  )
}

/** What's in a find, whose it is, and why what isn't its own stayed. */
function findDetail(find: AppFind, showNames?: boolean, waiting?: boolean): string {
  const whose = showNames ? names(find.items.map(item => item.from).filter(Boolean)) : ''
  const what = said(summary(find.items))
  const unsure = find.items.filter(item => item.certainty !== 'certain').length
  const why = [...new Set(find.items.map(item => item.note).filter(Boolean)), unsure > 0 ? `${unsure} only likely or possible: not picked` : null].filter(Boolean).join(' · ')
  const when = waiting && find.items.some(item => item.selected && item.machine) ? 'Goes with your next uninstall, or now' : ''
  return [whose ? `${whose}: ${what}` : what, why, when].filter(Boolean).join(' · ')
}

/**
 * Junk Exo hasn't got to yet, past a little: Clean, asked in place, since it doesn't go to the Recycle Bin; what it deleted, or
 * why it didn't, stays. Exo clears the rest while the PC is idle; only the whole Recycle Bin waits for this press.
 */
function JunkRow({ active }: { active: boolean }) {
  const [items, setItems] = useState<JunkItem[] | null>(null)
  const [cleaned, setCleaned] = useState<{ done?: string; error?: string } | null>(null)
  const read = useRef(false)
  const rescan = () => host.junkScan().then(
    result => setItems(result.items),
    (error: unknown) => { report(error, 'AppsPanel.junk'); setItems(current => current ?? []) },
  )
  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    void rescan()
  }, [active])
  /** The button's ring and check; a clean that deleted nothing is a failure with its reason. */
  async function clean(junk: JunkItem[]) {
    setCleaned(null)
    const result = await host.junkClean(junk.map(item => item.id))
    const outcome = cleanOutcome(result, size)
    return outcome.done ? result : { ok: false, message: outcome.error ?? null }
  }
  // A partial clean keeps the holder's line beside what it deleted: "Deleted 1.2 GB" and "3 files kept. ...".
  const cleanedUp = (value: unknown) => { setCleaned(cleanOutcome(value as Awaited<ReturnType<typeof host.junkClean>>, size)); void rescan() }
  if (!items) return null
  const total = items.reduce((sum, item) => sum + item.bytes, 0)
  if (cleaned?.done && total < JUNK_WORTH) return <ReadyRow title={cleaned.done} done error={cleaned.error} />
  if (total < JUNK_WORTH && !cleaned) return null
  return (
    <ReadyRow title={`${size(total)} of junk`} detail={names(items.filter(item => item.bytes > 0).map(item => item.name.toLowerCase())).replace(/^./, first => first.toUpperCase())} error={cleaned?.error}>
      <ConfirmButton className="exo-set-btn is-danger" danger question={`Delete ${size(total)}?`} context="AppsPanel.junkClean" fallback="Nothing was deleted. Try again." why="none"
        onConfirm={() => clean(items)} onFailed={failure => setCleaned({ error: failure.text })} onDone={cleanedUp}>Clean</ConfirmButton>
    </ReadyRow>
  )
}

/** Windows Installer's cache keeps a copy of every MSI; copies no product or patch needs any more are worth a row past this. */
const CACHE_WORTH = 50e6

/**
 * Windows Installer files no app needs: copies in Windows' installer cache that no installed product or patch is registered
 * with. Clear, asked in place, moves them to the Recycle Bin under one Windows prompt; Put back brings them back.
 */
function InstallerCacheRow({ active, busy, onChanged }: { active: boolean; busy: boolean; onChanged: () => void }) {
  const [found, setFound] = useState<{ files: number; bytes: number } | null>(null)
  const [done, setDone] = useState<{ line: string; undo: string | null } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const read = useRef(false)
  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    host.appsInstallerCache().then(result => setFound(result), (failure: unknown) => report(failure, 'AppsPanel.installerCache', { level: 'warn' }))
  }, [active])
  const moved = (result: Awaited<ReturnType<typeof host.appsInstallerCacheClean>>) => {
    if (result.files > 0) setDone({ line: `Moved ${count(result.files, 'Windows Installer file', 'Windows Installer files')} to the Recycle Bin: ${size(result.bytes)} back.`, undo: result.undo ?? null })
    onChanged()
  }
  async function clear() {
    setError(null)
    const result = await host.appsInstallerCacheClean()
    // Some moved before it stopped: what moved shows, with the reason.
    if (!result.ok && result.files > 0) moved(result)
    return result
  }
  if (done) return <ClearedRow line={done.line} undo={done.undo} onChanged={onChanged} />
  if (!found || found.bytes < CACHE_WORTH) return error ? <ReadyRow title="Windows Installer files" error={error} /> : null
  return (
    <ReadyRow title={`${size(found.bytes)} of Windows Installer files no app needs`} detail={`${count(found.files, 'copy', 'copies')} of removed installers · To the Recycle Bin`} error={error ?? undefined}>
      <ConfirmButton className="exo-set-btn is-danger" danger disabled={busy} question={`Move ${size(found.bytes)}?`} context="AppsPanel.installerCacheClean" fallback="Nothing was moved. Try again." why="none"
        onConfirm={clear} onFailed={failure => setError(failure.text)} onDone={value => moved(value as Awaited<ReturnType<typeof host.appsInstallerCacheClean>>)}>Clear</ConfirmButton>
    </ReadyRow>
  )
}

// ---- starts with Windows ----

type Startup = ReturnType<typeof useStartup>

/** Everything that starts when you sign in, refreshed whenever Apps opens. */
function useStartup(active: boolean) {
  const [state, setState] = useState<StartupState | null>(null)
  const [icons, setIcons] = useState<Record<string, string | null>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})

  async function take(next: StartupState) {
    setState(next)
    const wanted = [
      ...next.items.map(item => item.icon),
      ...next.removed.map(item => item.icon),
    ].filter((icon): icon is string => !!icon)
    if (wanted.length > 0) {
      const chunk = await host.appsIcons(wanted)
      setIcons(current => mergeIcons(current, chunk.icons))
    }
  }

  useEffect(() => {
    if (!active) return
    let live = true
    host.startupList().then(next => { if (live) void take(next) }, (error: unknown) => report(error, 'AppsPanel.startup'))
    return () => { live = false }
  }, [active])

  const tell = (id: string, text: string) => setErrors(current => ({ ...current, [id]: text }))
  // The row's button (ActionButton) shows the work; the list takes the answer after its check (`settle`), or at once on a refusal.
  const settle = (value: unknown) => { const next = value as StartupState | null; if (next) void take(next) }

  // One row may stand for several entries with one name (a game's two tracking values, Edge's two updaters): act on each and report any failure.
  async function change(ids: string[], restore: boolean) {
    for (const one of ids) tell(one, '')
    let next: StartupState | null = null
    const failures: string[] = []
    for (const one of ids) {
      next = restore ? await host.startupRestore(one) : await host.startupRemove(one)
      if (!next.ok) {
        const message = next.message || (restore ? 'It wasn’t turned on. Try again.' : 'It wasn’t turned off. Try again.')
        failures.push(message)
        tell(one, message)
      }
    }
    if (next) await take(next)
    if (failures.length > 0) return { ...(next ?? { ok: false }), ok: false, message: failures[0] }
    return next
  }

  // Everything safe to remove at once; Windows asks once for the scheduled tasks it guards.
  async function clean() {
    tell('clean', '')
    const next = await host.startupClean()
    if (!next.ok) await take(next)
    return next
  }

  // Clean up counts Windows' telemetry tasks too; they have no row of their own.
  const unneeded = (state?.items.filter(item => item.unneeded).length ?? 0) + (state?.extras ?? 0)
  return { state, icons, errors, tell, settle, change, clean, unneeded }
}

const VERDICT: Record<NonNullable<StartupItem['verdict']>, string | null> = { remove: 'Safe to turn off', optional: null, keep: 'Worth keeping', locked: null }

/** What going without it costs, else Exo's word on it, else what it runs. */
function startupDetail(item: StartupItem): string {
  // What the guard knows of it (it came back and was turned off again, you turned it on yourself, it is never turned off).
  if (item.status) return item.status
  if (item.missing) return 'Its program is gone: safe to turn off'
  const word = item.verdict ? VERDICT[item.verdict] : null
  if (item.verdict === 'remove') return word ?? ''
  return item.without ?? item.does ?? word ?? `Runs ${item.command}`
}

/** Entries that share a name as one row (the first's words), with every id it stands for. */
function byName<T extends { id: string; name: string }>(list: T[]): (T & { ids: string[] })[] {
  const rows = new Map<string, T & { ids: string[] }>()
  for (const item of list) {
    const row = rows.get(item.name)
    if (row) row.ids.push(item.id)
    else rows.set(item.name, { ...item, ids: [item.id] })
  }
  return [...rows.values()]
}

/**
 * Starts with Windows: three clear groups — Broken (app removed), Starts with Windows (On / Required), and Turned off
 * (unified Exo-disabled + externally-disabled entries, each with a full-opacity switch to turn back on).
 */
function StartupView({ startup, query }: { startup: Startup; query: string }) {
  const { state, icons, errors, tell, settle, change } = startup
  const [toggling, setToggling] = useState<string | null>(null)
  if (!state) return <Wait label="Reading what starts with Windows">{sampleLines(5)}</Wait>
  const words = query.trim().toLowerCase()
  const matches = (text: string) => !words || text.toLowerCase().includes(words)
  const items = byName(state.items.filter(item => matches(`${item.name} ${item.publisher ?? ''} ${item.command}`)))
  const removed = byName(state.removed.filter(item => matches(item.name)))

  const brokenItems = items.filter(item => item.missing)
  const onItems = items.filter(item => !item.missing && item.enabled)
  type OffRow =
    | { kind: 'item'; id: string; ids: string[]; name: string; icon: string | null; detail: string; amount: string; locked: boolean }
    | { kind: 'removed'; id: string; ids: string[]; name: string; icon: string | null; detail: string }
  const offRows: OffRow[] = [
    ...items.filter(item => !item.missing && !item.enabled).map(item => ({
      kind: 'item' as const,
      id: item.id,
      ids: item.ids,
      name: item.name,
      icon: item.icon ?? null,
      detail: startupDetail(item),
      amount: size(item.memory),
      locked: item.locked || item.readOnly,
    })),
    ...removed.map(rem => ({
      kind: 'removed' as const,
      id: rem.id,
      ids: rem.ids,
      name: rem.name,
      icon: rem.icon ?? null,
      detail: `Turned off ${fullDay.format(new Date(rem.at))}`,
    })),
  ].sort((a, b) => a.name.localeCompare(b.name))

  const toggle = async (rowId: string, ids: string[], restore: boolean) => {
    setToggling(rowId)
    try { await change(ids, restore) }
    finally { setToggling(null) }
  }

  const removeAllBroken = async () => {
    const allIds = brokenItems.flatMap(item => item.ids)
    return await change(allIds, false)
  }

  if (words && brokenItems.length + onItems.length + offRows.length === 0) return <p className="exo-apps-none">Nothing matches</p>
  if (!words && brokenItems.length + onItems.length + offRows.length === 0) return <p className="exo-apps-none">Nothing starts with Windows</p>

  return (
    <>
      {brokenItems.length > 0 ? (
        <section aria-label="Broken startup items">
          <div className="exo-apps-group-head">
            <h3 className="exo-apps-group">Broken ({brokenItems.length})</h3>
            {brokenItems.length > 1 ? (
              <ActionButton className="exo-set-btn is-compact" context="AppsPanel.startupCleanBroken" fallback="Could not remove broken items." why="none"
                onPress={removeAllBroken} onFailed={failure => tell('broken', failure.text)} onDone={settle}>Clean up broken</ActionButton>
            ) : null}
          </div>
          {brokenItems.map(item => {
            const rowError = item.ids.map(id => errors[id]).find(Boolean)
            return (
              <Line key={item.id} startup name={item.name} icon={<AppIcon name={item.name} icon={item.icon ? icons[item.icon] ?? null : null} />}
                tags={[{ text: 'App removed', tone: 'warn' }]} detail="Its app was uninstalled · safe to remove" showUsed={false} error={rowError}>
                <ActionButton className="exo-set-btn is-compact"
                  context="AppsPanel.startupRemove"
                  fallback="It wasn’t removed. Try again."
                  why="none"
                  onPress={() => change(item.ids, false)}
                  onFailed={failure => tell(item.id, failure.text)}
                  onDone={settle}>Remove</ActionButton>
              </Line>
            )
          })}
        </section>
      ) : null}

      {onItems.length > 0 ? (
        <section aria-label="Starts with Windows">
          <h3 className="exo-apps-group">Starts with Windows ({onItems.length})</h3>
          {onItems.map(item => {
            const rowError = item.ids.map(id => errors[id]).find(Boolean)
            const required = item.locked || item.readOnly
            return (
              <Line key={item.id} startup name={item.name} icon={<AppIcon name={item.name} icon={item.icon ? icons[item.icon] ?? null : null} />}
                tags={required ? [{ text: 'Required' }] : [{ text: 'On', tone: 'good' }]} detail={startupDetail(item)}
                amount={size(item.memory)} showUsed={false} error={rowError}>
                {required ? <span className="exo-apps-tag">Required</span> : (
                  <Switch checked={true} disabled={toggling !== null} label={`Turn off ${item.name}`}
                    onChange={() => void toggle(item.id, item.ids, false)} />
                )}
              </Line>
            )
          })}
        </section>
      ) : null}

      {offRows.length > 0 ? (
        <section aria-label="Turned off">
          <h3 className="exo-apps-group">Turned off ({offRows.length})</h3>
          {offRows.map(row => {
            const rowError = row.ids.map(id => errors[id]).find(Boolean)
            const locked = row.kind === 'item' && row.locked
            return (
              <Line key={row.id} startup name={row.name} icon={<AppIcon name={row.name} icon={row.icon ? icons[row.icon] ?? null : null} />}
                tags={[{ text: 'Off' }]} detail={row.detail}
                amount={row.kind === 'item' ? row.amount : ''} showUsed={false} error={rowError}>
                {locked ? <span className="exo-apps-tag">Required</span> : (
                  <Switch checked={false} disabled={toggling !== null} label={`Turn on ${row.name}`}
                    onChange={() => void toggle(row.id, row.ids, true)} />
                )}
              </Line>
            )
          })}
        </section>
      ) : null}
    </>
  )
}

// ---- Windows apps ----

/** Where a Store app is: for you, for other accounts, for new accounts. */
function whereItIs(app: WindowsApp): string {
  const parts: string[] = []
  if (app.forMe) parts.push(app.others > 0 ? `For you and ${count(app.others, 'other account', 'other accounts')}` : 'For you')
  else parts.push(app.others > 0 ? `Not for you: ${count(app.others, 'other account has', 'other accounts have')} it` : 'Not for you')
  if (app.provisioned) parts.push('New accounts get it')
  return parts.join(' · ')
}

/**
 * Windows apps: the Store and preinstalled apps on this PC across its accounts, as Win11Debloat and BCU list them. What Windows,
 * Store apps, games or Exo need is never listed (only counted). Select, then For you (no prompt; Add back while its files stay
 * on this PC) or Every account (one Windows prompt; can't be undone here). Review shows what each removal does and how it
 * comes back before anything goes. A restore point doesn't bring Store apps back, so none is made here.
 */
const needsAllAccounts = (ids: Set<string>, list: readonly WindowsApp[]) =>
  ids.size > 0 && !list.some(app => ids.has(app.id) && app.forMe)

function WindowsAppsView({ active, query, onChanged }: { active: boolean; query: string; onChanged: () => void }) {
  const [state, setState] = useState<{ items: WindowsApp[]; hidden: number } | null>(null)
  const [picked, setPicked] = useState<Set<string> | null>(null)
  const [everyone, setEveryone] = useState(false)
  const [plan, setPlan] = useState<{ token: string; admin: boolean; lines: WindowsAppLine[] } | null>(null)
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  const [done, setDone] = useState<string | null>(null)
  const read = () => host.appsPackages().then(
    result => setState({ items: result.items, hidden: result.hidden }),
    (error: unknown) => { report(error, 'AppsPanel.packages'); setState(current => current ?? { items: [], hidden: 0 }) },
  )
  useEffect(() => { if (active) void read() }, [active])
  const setError = (id: string, message: string | null) => setErrors(current => ({ ...current, [id]: message }))

  async function review() {
    if (!picked || picked.size === 0) return { ok: false, message: 'Pick an app first.' }
    setError('plan', null)
    const result = await host.appsPackagesPlan([...picked], everyone)
    if (result.ok && !result.token) return { ok: false, message: 'Nothing there can be removed that way.' }
    if (result.ok && result.token) {
      setEveryone(result.admin)
      setPlan({ token: result.token, admin: result.admin, lines: result.lines })
    }
    return result
  }

  /** Removes the plan's apps; what went, the list again and the picks cleared once its check has shown. */
  async function remove() {
    if (!plan) return { ok: false }
    setError('plan', null)
    const forAll = plan.admin
    const result = await host.appsPackagesRemove(plan.token)
    if (!result.ok) {
      if (result.removed > 0) setDone(`Removed ${count(result.removed, 'app', 'apps')} ${forAll ? 'for every account' : 'for you'}.`)
      await read()
    }
    return result
  }
  const removed = (value: unknown) => {
    const result = value as Awaited<ReturnType<typeof host.appsPackagesRemove>>
    const forAll = plan?.admin ?? everyone
    if (result.removed > 0) setDone(`Removed ${count(result.removed, 'app', 'apps')} ${forAll ? 'for every account' : 'for you'}.`)
    setPlan(null)
    setPicked(null)
    void read().then(onChanged)
  }

  async function addBack(app: WindowsApp) {
    setError(app.id, null)
    const result = await host.appsPackageAddBack(app.id)
    if (result.ok && result.store) {
      void read()
      return { ok: false, message: 'Windows couldn’t add it back from this PC: its Microsoft Store page is open.' }
    }
    return result
  }

  if (!state) return <Wait label="Reading Windows apps">{sampleLines(6)}</Wait>
  const words = query.trim().toLowerCase()
  const items = state.items.filter(app => !words || `${app.name} ${app.publisher ?? ''}`.toLowerCase().includes(words))
  const bloat = state.items.filter(app => app.bloat && (app.forMe || app.provisioned))
  const pick = (id: string) => {
    setPlan(null)
    setPicked(current => {
      const next = new Set(current)
      if (!next.delete(id)) next.add(id)
      if (needsAllAccounts(next, state.items)) setEveryone(true)
      return next
    })
  }
  return (
    <>
      {done ? <ReadyRow title={done} done /> : null}
      {!words && !picked && bloat.length > 0 ? (
        <ReadyRow title={count(bloat.length, 'Windows app you didn’t ask for', 'Windows apps you didn’t ask for')} detail={names(bloat.map(app => app.name))}>
          <button type="button" className="exo-set-btn" onClick={() => {
            const next = new Set(bloat.map(app => app.id))
            setPicked(next)
            setPlan(null)
            if (needsAllAccounts(next, state.items)) setEveryone(true)
          }}>Pick them</button>
        </ReadyRow>
      ) : null}
      <div className="exo-apps-filter">
        <span>{picked ? `${picked.size} picked` : 'Store and preinstalled apps'}</span>
        <span className="exo-apps-ready-act">
          {picked ? (
            <>
              <span className="exo-shelf-chips" role="group" aria-label="Remove for">
                <button type="button" className="exo-shelf-chip" aria-pressed={!everyone} onClick={() => { setEveryone(false); setPlan(null) }}>For you</button>
                <button type="button" className="exo-shelf-chip" aria-pressed={everyone} onClick={() => { setEveryone(true); setPlan(null) }}>Every account</button>
              </span>
              <ActionButton className="exo-set-btn is-compact" disabled={picked.size === 0} context="AppsPanel.packagesPlan" fallback="Exo couldn’t look at what goes. Try again." why="none"
                onPress={review} onFailed={failure => setError('plan', failure.text)}>Review</ActionButton>
            </>
          ) : null}
          <button type="button" className="exo-set-btn is-compact" aria-pressed={picked !== null} onClick={() => { setPicked(current => current ? null : new Set()); setPlan(null) }}>{picked ? 'Cancel' : 'Select'}</button>
        </span>
      </div>
      {errors.plan ? <ErrorNote className="exo-set-error" message={errors.plan} /> : null}
      {plan ? <PackagePlan plan={plan} onRemove={remove} onRemoved={removed} onFailed={failure => setError('plan', failure.text)} onBack={() => setPlan(null)} /> : null}
      <section aria-label="Windows apps">
        {items.map(app => (
          <Line key={app.id} name={app.name} icon={<AppIcon name={app.name} icon={null} />} detail={[app.bloat, whereItIs(app)].filter(Boolean).join(' · ')} showUsed={false} error={errors[app.id] ?? null} keep={picked !== null || app.addBack}>
            {picked ? (
              <button type="button" className={picked.has(app.id) ? 'exo-apps-pick is-picked' : 'exo-apps-pick'} aria-pressed={picked.has(app.id)} aria-label={`Pick ${app.name}`} onClick={() => pick(app.id)}>
                {picked.has(app.id) ? <Check size={12} /> : null}
              </button>
            ) : app.addBack ? (
              <ActionButton className="exo-set-btn is-compact" context="AppsPanel.packageAddBack" fallback="Windows didn’t add it back. Try again." why="none"
                onPress={() => addBack(app)} onFailed={failure => setError(app.id, failure.text)} onDone={() => void read().then(onChanged)}>Add for you</ActionButton>
            ) : null}
          </Line>
        ))}
        {items.length === 0 ? <p className="exo-apps-none">{words ? 'No Windows apps match' : 'No Windows apps to remove'}</p> : null}
      </section>
    </>
  )
}

/** Before anything goes: what each removal does and how it comes back, and the one press that does it (asked in place). */
function PackagePlan({ plan, onRemove, onRemoved, onFailed, onBack }: {
  plan: { admin: boolean; lines: WindowsAppLine[] }
  onRemove: () => Promise<unknown>
  onRemoved: (value: unknown) => void
  onFailed: (failure: PlainError) => void
  onBack: () => void
}) {
  const lasting = plan.lines.filter(line => !line.undoable).length
  const apps = count(plan.lines.length, 'app', 'apps')
  return (
    <section className="exo-apps-ready" aria-label="What goes">
      <ReadyRow title={plan.admin ? `Remove ${apps} for every account` : `Remove ${apps} for you`}
        detail={[plan.admin ? 'Windows asks for permission' : 'No permission needed', lasting > 0 ? `${lasting} can’t be undone here` : 'Each can be added back', 'A restore point doesn’t bring Store apps back'].join(' · ')}>
        <ConfirmButton className="exo-set-btn is-danger" danger question={lasting > 0 ? 'Remove for good?' : `Remove ${plan.lines.length}?`} timeoutMs={null}
          context="AppsPanel.packagesRemove" fallback="Nothing was removed. Try again." why="none" onConfirm={onRemove} onFailed={onFailed} onDone={onRemoved}>Remove {plan.lines.length}</ConfirmButton>
        <button type="button" className="exo-set-btn" onClick={onBack}>Back</button>
      </ReadyRow>
      <ul className="exo-apps-items">
        {plan.lines.map(line => (
          <li key={line.id}>
            <span />
            <span className="exo-apps-item">
              <span className="exo-apps-path">{line.name}</span>
              <span className="exo-apps-note">{line.does} · {line.back}</span>
            </span>
            <span className="exo-apps-item-size">{line.undoable ? '' : 'For good'}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}
