import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Check, ChevronDown, ChevronUp } from '../../brand/icons'
import { cleanOutcome } from '../../lib/junkClean'
import { host, onHostEvent, type AppFind, type AppLeftover, type InstalledApp, type JunkItem, type LeftoverSummary, type RemovalRecord, type StartupItem, type StartupState } from '../../lib/host'
import { report } from '../../lib/report'
import { ConfirmButton } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
import { Wait, sample, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'
import { ArtImg } from '../ui/ArtImg'

type View = 'apps' | 'startup' | 'removed'
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
  return { folders: of('folder'), files: of('file'), registry: of('registry', 'service', 'task'), shortcuts: of('shortcut'), bytes: items.reduce((sum, item) => sum + item.bytes, 0) }
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
export function AppsPanel({ active }: { active: boolean }) {
  const [programs, setPrograms] = useState<InstalledApp[] | null>(null)
  const [edgeRemoved, setEdgeRemoved] = useState(false)
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
      setEdgeRemoved(Boolean(result.edgeRemoved))
      const missing = result.programs.map(app => app.id).filter(id => !(id in known))
      for (let from = 0; from < missing.length; from += 100) {
        const chunk = await host.appsIcons(missing.slice(from, from + 100))
        setIcons(current => ({ ...current, ...chunk.icons }))
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
      <Head view={view} onView={setView} query={query} onQuery={setQuery} startup={0} />
      <Wait label="Reading this PC's apps"><Columns knowsUse sort={sort} onSort={() => { }} run={null} picked={null} busy={false} onUninstallPicked={() => { }} onSelect={() => { }} /></Wait>
      <div className="exo-apps-list"><Wait>{sampleLines(10)}</Wait></div>
    </div>
  )

  const mainApps = programs.filter(app => !isPart(app))
  const total = mainApps.reduce((sum, app) => sum + (app.bytes ?? 0), 0)
  const knowsUse = programs.some(app => app.lastOpened || app.quietSince)
  const setError = (id: string, message: string | null) => setErrors(current => ({ ...current, [id]: message }))

  /** One app, gone or not: its uninstaller, Exo's clean-up for one it does the job of, or only a look at what's left when its uninstaller is gone. */
  async function uninstall(app: InstalledApp) {
    setBusy(app.id)
    setError(app.id, null)
    try {
      if (app.replaces) {
        const result = await host.replacedRemove(app.replaces)
        if (!result.ok) setError(app.id, result.message ?? `${app.name} wasn't removed.`)
        else setRemoved(current => [{ id: app.id, name: app.name, message: `${app.name} is gone.` }, ...current])
      } else {
        const result = app.uninstaller ? await host.appsUninstall(app.id) : await host.appsScan(app.id)
        if (result.ok) setRemoved(current => [{ id: app.id, name: app.name, find: result }, ...current.filter(other => other.id !== app.id)])
        else setError(app.id, result.message ?? `${app.name} wasn't removed.`)
      }
      const next = await host.appsList()
      setPrograms(next.programs)
      setEdgeRemoved(Boolean(next.edgeRemoved))
    } catch (error) {
      report(error, 'AppsPanel.uninstall')
      setError(app.id, `${app.name} wasn't removed.`)
    } finally {
      setBusy(null)
    }
  }

  /** Several, asked once in place, then each in turn. */
  async function uninstallMany(ids: string[]) {
    if (ids.length === 0 || busy || run || !programs) return
    setError('many', null)
    try {
      const agreed = await host.appsConfirm(ids)
      // Each app left out says why, beside its own row (Exo logs the same reason).
      for (const skip of agreed.skipped) setError(skip.id, skip.reason)
      if (!agreed.ok) {
        setError('many', agreed.message ?? 'Nothing was removed.')
        return
      }
      setPicked(null)
      const chosen = agreed.ids.map(id => programs.find(app => app.id === id)).filter((app): app is InstalledApp => !!app)
      for (const [index, app] of chosen.entries()) {
        setRun({ at: index + 1, of: chosen.length })
        await uninstall(app)
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
      working={busy === app.id} disabled={busy !== null || run !== null} picked={picked && pickable(app) ? picked.has(app.id) : null}
      onPick={() => pick(app.id)} onUninstall={() => void uninstall(app)} />
  )

  const searching = query.trim().length > 0
  const pressSort = (by: Sort) => setSort(current => current.by === by ? { by, back: !current.back } : { by, back: false })

  return (
    <div className="exo-apps">
      <Head view={view} onView={setView} query={query} onQuery={setQuery} startup={startup.state?.items.filter(item => item.enabled).length ?? 0}
        facts={`${count(mainApps.length, 'app', 'apps')}${total > 0 ? ` · ${size(total)}` : ''}`} />
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
        ) : view === 'startup' ? (
          <StartupView startup={startup} query={query} />
        ) : (
          <RemovedView active={active} edgeRemoved={edgeRemoved} disabled={busy !== null} onEdgeAllowed={() => setEdgeRemoved(false)} onChanged={() => { void load(iconsNow.current); void readTidy(true) }} />
        )}
      </div>
    </div>
  )
}

/** The columns over the list: what sorts it, and Select at the end, where a row's action is. */
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
  return (
    <div className={knowsUse ? 'exo-apps-columns' : 'exo-apps-columns is-short'} role="row">
      <span />
      <SortHead label="Name" by="name" sort={sort} onSort={onSort} />
      {knowsUse ? <SortHead label="Last used" by="used" sort={sort} onSort={onSort} /> : null}
      <SortHead label="Size" by="size" sort={sort} onSort={onSort} end />
      <div className="exo-apps-select">
        {run ? <span className="exo-apps-run" role="status"><Spinner label={`Uninstalling ${run.at} of ${run.of}`} />{run.at} of {run.of}</span> : null}
        {!run && picked && picked.size > 0 ? (
          <ConfirmButton className="exo-set-btn is-compact is-danger" danger disabled={busy} question={`Uninstall ${picked.size}?`} onConfirm={() => onUninstallPicked([...picked])}>Uninstall {picked.size}</ConfirmButton>
        ) : null}
        {!run ? <button type="button" className="exo-set-btn is-compact" aria-pressed={picked !== null} onClick={onSelect}>{picked ? 'Cancel' : 'Select'}</button> : null}
      </div>
    </div>
  )
}

/** What Windows, games and Exo need: out of the way until asked for, or until a search finds it. */
function Parts({ count: how, searching, children }: { count: number; searching: boolean; children: ReactNode }) {
  const [open, setOpen] = useState(false)
  if (how === 0) return null
  return (
    <section aria-label="Needed by Windows, games or Exo">
      {searching ? <h3 className="exo-apps-group">Needed by Windows, games or Exo</h3> : (
        <button type="button" className="exo-apps-more" aria-expanded={open} onClick={() => setOpen(value => !value)}>
          <span>Needed by Windows, games or Exo · {how}</span>
          {open ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
        </button>
      )}
      {open || searching ? children : null}
    </section>
  )
}

function Head({ view, onView, query, onQuery, startup, facts }: { view: View; onView: (next: View) => void; query: string; onQuery: (next: string) => void; startup: number; facts?: string }) {
  const views: { id: View; label: string }[] = [{ id: 'apps', label: 'Apps' }, { id: 'startup', label: startup > 0 ? `Starts with Windows · ${startup}` : 'Starts with Windows' }, { id: 'removed', label: 'Removed' }]
  return (
    <div className="exo-apps-head">
      <h2 id="pc-apps-title">Apps{facts ? <span className="exo-apps-facts">{facts}</span> : null}</h2>
      <div className="exo-shelf-chips" role="group" aria-label="What to show">
        {views.map(choice => <button key={choice.id} type="button" className="exo-shelf-chip" aria-pressed={view === choice.id} onClick={() => onView(choice.id)}>{choice.label}</button>)}
      </div>
      <input type="search" className="exo-field" value={query} placeholder="Search" aria-label="Search apps" onChange={event => onQuery(event.target.value)} />
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

/** A row of the list: its icon, its name over one line of detail, the columns, and what can be done with it, shown when pointed at. */
function Line({ icon, name, detail, used, amount, error, showUsed, keep, children }: {
  icon?: ReactNode
  name: string
  detail?: ReactNode
  used?: string | null
  amount?: string
  error?: string | null
  showUsed?: boolean
  /** The action stays in view: it is what the row is for, or it is at work. */
  keep?: boolean
  children?: ReactNode
}) {
  return (
    <div className={`exo-apps-row${showUsed === false ? ' is-short' : ''}${keep ? ' is-kept' : ''}`}>
      <span className="exo-apps-icon">{icon}</span>
      <div className="exo-apps-copy">
        <span className="exo-apps-name">{name}</span>
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

function AppLine({ app, icon, starts, showUsed, error, working, disabled, picked, onPick, onUninstall }: {
  app: InstalledApp
  icon: string | null
  starts: boolean
  showUsed: boolean
  error: string | null
  working: boolean
  disabled: boolean
  picked: boolean | null
  onPick: () => void
  onUninstall: () => void
}) {
  const detail = working
    ? (app.replaces ? `Removing ${app.name}` : app.uninstaller ? 'Finish in its window' : 'Looking for what it left')
    : [!app.uninstaller ? 'Its uninstaller is gone' : null, whatItIs(app), starts ? 'Starts with Windows' : null].filter(Boolean).join(' · ')
  return (
    <Line icon={<AppIcon name={app.name} icon={icon} />} name={app.name} detail={detail} used={lastUsed(app)} amount={size(app.bytes)} error={error} showUsed={showUsed} keep={working || picked !== null}>
      {picked !== null ? (
        <button type="button" className={picked ? 'exo-apps-pick is-picked' : 'exo-apps-pick'} aria-pressed={picked} aria-label={`Pick ${app.name}`} onClick={onPick}>
          {picked ? <Check size={12} /> : null}
        </button>
      ) : !app.removable ? null : working ? <Spinner label={`Removing ${app.name}`} /> : (
        <AppAction app={app} disabled={disabled} onUninstall={onUninstall} />
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

/** Uninstall or Remove, asked in place; Clear only looks first, so what it found shows before anything goes. */
function AppAction({ app, disabled, onUninstall }: { app: InstalledApp; disabled: boolean; onUninstall: () => void }) {
  if (!app.uninstaller && !app.replaces) return <button type="button" className="exo-set-btn is-compact" disabled={disabled} onClick={onUninstall}>Look</button>
  const action = app.replaces ? 'Remove' : 'Uninstall'
  return <ConfirmButton className="exo-set-btn is-compact is-danger" danger disabled={disabled} question={`${action}?`} onConfirm={onUninstall}>{action}</ConfirmButton>
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
          <button type="button" className="exo-set-btn" disabled={startup.busy !== null} aria-busy={startup.busy === 'clean'} onClick={() => void startup.clean()}>
            {startup.busy === 'clean' ? <Spinner label="Cleaning up startup" /> : null}Turn off {unneeded}
          </button>
        </ReadyRow>
      ) : null}
      <JunkRow active={active} />
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
  const [working, setWorking] = useState(false)
  const [back, setBack] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function putBack(id: string) {
    setWorking(true)
    setError(null)
    try {
      const result = await host.appsUndo(id)
      if (result.ok) setBack(true)
      else setError(result.message ?? 'It wasn’t put back.')
      onChanged()
    } catch (failure) {
      report(failure, 'AppsPanel.undo')
      setError('It wasn’t put back.')
    } finally {
      setWorking(false)
    }
  }
  if (back) return <ReadyRow title="Put back" done />
  return (
    <ReadyRow title={line} done error={error ?? undefined}>
      {undo ? (
        <button type="button" className="exo-set-btn" disabled={working} onClick={() => void putBack(undo)}>
          {working ? <Spinner label="Putting it back" /> : null}Put back
        </button>
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
      {find.items.length > 0 ? <FindRow find={find} title={find.declined ? 'Windows’ permission was declined' : 'Left behind on purpose'} onLeave={onLeave} onChanged={onChanged} busy={busy} /> : null}
    </>
  )
}

/**
 * A find, ready: what's selected, one press to clear it, Review to see every item and change the selection, Leave to keep it all
 * and not be asked again. Nothing here needs a question: files go to the Recycle Bin and entries to a saved copy first, and once
 * cleared, Put back is right here.
 */
function FindRow({ find, title, names: showNames, waiting, onLeave, onChanged, busy }: { find: AppFind; title: string; names?: boolean; waiting?: boolean; onLeave: () => void; onChanged: () => void; busy: boolean }) {
  const [picks, setPicks] = useState(() => new Set(find.items.filter(item => item.selected).map(item => item.id)))
  const [open, setOpen] = useState(false)
  const [working, setWorking] = useState(false)
  const [cleared, setCleared] = useState<{ what: LeftoverSummary; undo: string | null } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const chosen = find.items.filter(item => picks.has(item.id))

  async function clear() {
    setWorking(true)
    setError(null)
    try {
      const result = await host.appsClean(find.token, [...picks])
      if (result.cleared) setCleared({ what: result.cleared, undo: result.undo ?? null })
      if (!result.ok) setError(result.message ?? 'Nothing was deleted.')
      setOpen(false)
      onChanged()
    } catch (failure) {
      report(failure, 'AppsPanel.clean')
      setError('Nothing was deleted.')
    } finally {
      setWorking(false)
    }
  }

  if (cleared) return <ClearedRow line={clearedLine(cleared.what)} undo={cleared.undo} onChanged={onChanged} />
  if (find.items.length === 0) return <ReadyRow title={title} done />
  return (
    <>
      <ReadyRow title={title} error={error ?? undefined} detail={findDetail(find, showNames, waiting)}>
        {picks.size > 0 ? (
          <button type="button" className="exo-set-btn is-primary" disabled={busy || working} aria-busy={working} onClick={() => void clear()}>
            {working ? <Spinner label={`Clearing what ${find.name} left`} /> : null}Clear {chosen.length}
          </button>
        ) : null}
        <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(value => !value)}>{open ? 'Hide' : 'Review'}</button>
        <button type="button" className="exo-set-btn" disabled={working} onClick={() => { void host.appsLeave(find.token); onLeave() }}>Leave</button>
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
                {item.note ? <span className="exo-apps-note">{item.note}</span> : null}
              </span>
              <span className="exo-apps-item-size">{size(item.bytes)}</span>
            </li>
          ))}
        </ul>
      ) : null}
    </>
  )
}

/** What's in a find, whose it is, and why what isn't its own stayed. */
function findDetail(find: AppFind, showNames?: boolean, waiting?: boolean): string {
  const whose = showNames ? names(find.items.map(item => item.from).filter(Boolean)) : ''
  const what = said(summary(find.items))
  const why = [...new Set(find.items.map(item => item.note).filter(Boolean))].join(' · ')
  const when = waiting && find.items.some(item => item.selected && item.machine) ? 'Goes with your next uninstall, or now' : ''
  return [whose ? `${whose}: ${what}` : what, why, when].filter(Boolean).join(' · ')
}

/**
 * Junk Exo hasn't got to yet, past a little: Clean, asked in place, since it doesn't go to the Recycle Bin; what it deleted, or
 * why it didn't, stays. Exo clears the rest while the PC is idle; only the whole Recycle Bin waits for this press.
 */
function JunkRow({ active }: { active: boolean }) {
  const [items, setItems] = useState<JunkItem[] | null>(null)
  const [busy, setBusy] = useState(false)
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
  async function clean(junk: JunkItem[]) {
    setBusy(true)
    setCleaned(null)
    try {
      const result = await host.junkClean(junk.map(item => item.id))
      // A partial clean keeps the holder's line beside what it deleted: "Deleted 1.2 GB" and "3 files kept. ...".
      setCleaned(cleanOutcome(result, size))
      if (result.ok) await rescan()
    } catch (error) {
      report(error, 'AppsPanel.junkClean')
      setCleaned({ error: 'Nothing was deleted.' })
    } finally {
      setBusy(false)
    }
  }
  if (!items) return null
  const total = items.reduce((sum, item) => sum + item.bytes, 0)
  if (cleaned?.done && total < JUNK_WORTH) return <ReadyRow title={cleaned.done} done error={cleaned.error} />
  if (total < JUNK_WORTH && !cleaned) return null
  return (
    <ReadyRow title={`${size(total)} of junk`} detail={names(items.filter(item => item.bytes > 0).map(item => item.name.toLowerCase())).replace(/^./, first => first.toUpperCase())} error={cleaned?.error}>
      <ConfirmButton className="exo-set-btn is-danger" danger disabled={busy} question={`Delete ${size(total)}?`} lead={busy ? <Spinner label="Deleting junk" /> : null} onConfirm={() => void clean(items)}>Clean</ConfirmButton>
    </ReadyRow>
  )
}

// ---- starts with Windows ----

type Startup = ReturnType<typeof useStartup>

/** Everything that starts when you sign in, read once for the sheet: the Apps list says which of its apps do, and Startup shows them all. */
function useStartup(active: boolean) {
  const [state, setState] = useState<StartupState | null>(null)
  const [icons, setIcons] = useState<Record<string, string | null>>({})
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const read = useRef(false)

  async function take(next: StartupState) {
    setState(next)
    const wanted = next.items.map(item => item.icon).filter((icon): icon is string => !!icon)
    if (wanted.length > 0) setIcons((await host.appsIcons(wanted)).icons)
  }

  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    host.startupList().then(next => void take(next), (error: unknown) => report(error, 'AppsPanel.startup'))
  }, [active])

  // One row may stand for several entries with one name (a game's two tracking values, Edge's two updaters): each goes.
  async function change(ids: string[], restore: boolean) {
    const id = ids[0]
    setBusy(id)
    setErrors(current => ({ ...current, [id]: '' }))
    try {
      let next: StartupState | null = null
      for (const one of ids) {
        next = restore ? await host.startupRestore(one) : await host.startupRemove(one)
        if (!next.ok) break
      }
      if (!next) return
      await take(next)
      if (!next.ok && next.message) setErrors(current => ({ ...current, [id]: next.message ?? '' }))
    } catch (error) {
      report(error, restore ? 'AppsPanel.startupRestore' : 'AppsPanel.startupRemove')
      setErrors(current => ({ ...current, [id]: restore ? 'It wasn’t put back. Try again.' : 'It wasn’t turned off. Try again.' }))
    } finally {
      setBusy(null)
    }
  }

  // Everything safe to remove at once; Windows asks once for the scheduled tasks it guards.
  async function clean() {
    setBusy('clean')
    setErrors(current => ({ ...current, clean: '' }))
    try {
      const next = await host.startupClean()
      await take(next)
      if (!next.ok && next.message) setErrors(current => ({ ...current, clean: next.message ?? '' }))
    } catch (error) {
      report(error, 'AppsPanel.startupClean')
      setErrors(current => ({ ...current, clean: 'Startup wasn’t cleaned up. Try again.' }))
    } finally {
      setBusy(null)
    }
  }

  // Clean up counts Windows' telemetry tasks too; they have no row of their own.
  const unneeded = (state?.items.filter(item => item.unneeded).length ?? 0) + (state?.extras ?? 0)
  return { state, icons, busy, errors, change, clean, unneeded }
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
 * Starts with Windows: the Run keys, the Startup folders, Store apps and tasks run at sign-in, each with the memory it holds now
 * and what stops without it. Turn off takes it out of startup for good, so an app that writes itself back stays off (Windows
 * asks first for one that's every user's); what Exo turned off waits below with Put back. Exo itself and anti-cheat have no button.
 */
function StartupView({ startup, query }: { startup: Startup; query: string }) {
  const { state, icons, busy, errors, change } = startup
  if (!state) return <Wait label="Reading what starts with Windows">{sampleLines(5)}</Wait>
  const words = query.trim().toLowerCase()
  const matches = (text: string) => !words || text.toLowerCase().includes(words)
  const items = byName(state.items.filter(item => matches(`${item.name} ${item.publisher ?? ''} ${item.command}`)))
  const removed = byName(state.removed.filter(item => matches(item.name)))
  if (words && items.length + removed.length === 0) return <p className="exo-apps-none">Nothing matches</p>
  return (
    <>
      {!words && items.length === 0 ? <p className="exo-apps-none">Nothing starts with Windows</p> : null}
      {items.length > 0 ? (
        <section aria-label="Starts with Windows">
          {items.map(item => (
            <Line key={item.id} name={item.name} icon={<AppIcon name={item.name} icon={item.icon ? icons[item.icon] ?? null : null} />} detail={startupDetail(item)}
              used={!item.enabled ? 'Off' : null} amount={size(item.memory)} error={errors[item.id] || undefined} keep={busy === item.id}>
              {item.locked || item.readOnly ? null : (
                <button type="button" className="exo-set-btn is-compact" disabled={busy !== null} onClick={() => void change(item.ids, false)}>
                  {busy === item.id ? <Spinner label={`Turning off ${item.name}`} /> : null}Turn off
                </button>
              )}
            </Line>
          ))}
        </section>
      ) : null}
      {removed.length > 0 ? (
        <section aria-label="Turned off">
          <h3 className="exo-apps-group">Turned off</h3>
          {removed.map(item => (
            <Line key={item.id} name={item.name} detail={fullDay.format(new Date(item.at))} error={errors[item.id] || undefined} keep>
              <button type="button" className="exo-set-btn is-compact" disabled={busy !== null} onClick={() => void change(item.ids, true)}>
                {busy === item.id ? <Spinner label={`Putting ${item.name} back`} /> : null}Put back
              </button>
            </Line>
          ))}
        </section>
      ) : null}
    </>
  )
}

// ---- removed ----

/** What Exo removed from this PC, newest first, and Put back where what was cleared can still come back. Edge, once removed here, waits to be allowed again. */
function RemovedView({ active, edgeRemoved, disabled, onEdgeAllowed, onChanged }: { active: boolean; edgeRemoved: boolean; disabled: boolean; onEdgeAllowed: () => void; onChanged: () => void }) {
  const [items, setItems] = useState<RemovalRecord[] | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  const read = () => host.appsHistory().then(result => setItems(result.items), (error: unknown) => { report(error, 'AppsPanel.history'); setItems(current => current ?? []) })
  useEffect(() => { if (active) void read() }, [active])

  async function putBack(item: RemovalRecord) {
    setBusy(item.id)
    setErrors(current => ({ ...current, [item.id]: null }))
    try {
      const result = await host.appsUndo(item.id)
      if (!result.ok) setErrors(current => ({ ...current, [item.id]: result.message ?? 'It wasn’t put back.' }))
      await read()
      onChanged()
    } catch (failure) {
      report(failure, 'AppsPanel.undo')
      setErrors(current => ({ ...current, [item.id]: 'It wasn’t put back.' }))
    } finally {
      setBusy(null)
    }
  }

  if (!items) return <Wait label="Reading what Exo removed">{sampleLines(4)}</Wait>
  if (items.length === 0 && !edgeRemoved) return <p className="exo-apps-none">Nothing removed yet</p>
  return (
    <section aria-label="Removed">
      {edgeRemoved ? <EdgeRow busy={disabled} onAllowed={onEdgeAllowed} /> : null}
      {items.map(item => {
        const what = item.cleared && (item.cleared.folders + item.cleared.files + item.cleared.registry + item.cleared.shortcuts) > 0 ? said(item.cleared) : null
        const when = fullDay.format(new Date(item.at))
        return (
          <Line key={item.id} name={item.name} detail={[when, item.undone ? 'Put back' : what && `Cleared ${what}`].filter(Boolean).join(' · ')} error={errors[item.id] ?? null} keep>
            {item.canPutBack ? (
              <button type="button" className="exo-set-btn is-compact" disabled={busy !== null} onClick={() => void putBack(item)}>
                {busy === item.id ? <Spinner label={`Putting back what ${item.name} lost`} /> : null}Put back
              </button>
            ) : null}
          </Line>
        )
      })}
    </section>
  )
}

/** Edge, removed here: Edge Update is told not to install it again until Allow again (no confirm: removing it blocks it again). */
function EdgeRow({ busy, onAllowed }: { busy: boolean; onAllowed: () => void }) {
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function allow() {
    setAsking(true)
    setError(null)
    try {
      const result = await host.appsAllowEdge()
      if (result.ok) onAllowed()
      else setError(result.message ?? 'Windows didn’t allow it.')
    } catch (failure) {
      report(failure, 'AppsPanel.allowEdge')
      setError('Windows didn’t allow it.')
    } finally {
      setAsking(false)
    }
  }
  return (
    <Line name="Microsoft Edge" detail="Won’t install itself again" error={error} keep>
      <button type="button" className="exo-set-btn is-compact" disabled={busy || asking} onClick={() => void allow()}>
        {asking ? <Spinner label="Allow Edge again" /> : null}Allow again
      </button>
    </Line>
  )
}
