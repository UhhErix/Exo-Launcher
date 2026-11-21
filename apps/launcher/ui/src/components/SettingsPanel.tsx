import '../styles/onboarding-room.css'
import { scrollToSection } from '../lib/scrollToSection'
import { ChevronLeft, Close, Download, Info, Library, MusicNotes, Search, Sliders, Trophy } from '../brand/icons'
import { memo, useCallback, useEffect, useMemo, useRef, useState, type ComponentType } from 'react'
import {
  host,
  type LauncherSettings,
  type StoreStatus,
} from '../lib/host'
import { searchSettings, type SettingsIndexEntry } from '../lib/settingsIndex'
import { saveSettingsPatch } from '../lib/settingsSave'
import {
  SETTINGS_PAGES,
  settingsPageOf,
  type SettingsPageId,
  type SettingsSectionId,
} from '../lib/settingsSections'
import { AboutSection } from './settings/AboutSection'
import { AppsSection, preloadAppsSection } from './settings/AppsSection'
import { DownloadsSection, GeneralSection, InGameSection, LibraryToolsRow } from './settings/GeneralSection'
import { PrivacySection } from './settings/PrivacySection'
import { BackupSection } from './settings/BackupSection'
import { AccountSection } from './settings/AccountSection'
import { ShadersSection } from './shaders/ShaderTools'
import { StoresSection, type StoreAction, type StoreNote } from './settings/StoresSection'
import { errorText } from '../lib/errorWords'

const PAGE_ICONS: Record<SettingsPageId, ComponentType<{ size?: number }>> = {
  general: Sliders,
  library: Library,
  downloads: Download,
  ingame: Trophy,
  music: MusicNotes,
  about: Info,
}

export async function preloadSettingsRoom() {
  await preloadAppsSection()
}

/** The page Settings opens at from a room without a section asked for: that room's own, else General. */
function pageForRoom(room: string | undefined): SettingsPageId {
  if (room === 'library') return 'library'
  if (room === 'music') return 'music'
  return 'general'
}

export const SettingsPanel = memo(function SettingsPanel({
  settings,
  stores,
  updateBusy,
  updateError,
  updatePercent,
  updateStatus = 'Preparing…',
  updateAvailable,
  onCheckUpdate,
  onInstallUpdate,
  onSettings,
  onStores,
  active,
  section = null,
  room,
  onClose,
}: {
  settings: LauncherSettings | null
  stores: StoreStatus[]
  updateBusy: boolean
  /** Why the last update install failed. */
  updateError: string | null
  updatePercent: number | null
  updateStatus?: string
  updateAvailable: boolean
  onCheckUpdate: () => Promise<{
    ok: boolean
    message?: string
    updateAvailable?: boolean
    latest?: string | null
  }>
  onInstallUpdate: () => Promise<void> | void
  active: boolean
  /** A section to open at (the Library's Sign in to a store). Each new object jumps once. */
  section?: { id: SettingsSectionId } | null
  /** The room Settings floats over: opened without a section, it shows that room's page. */
  room?: string
  onSettings: (next: LauncherSettings) => void
  onStores?: (next: StoreStatus[]) => void | Promise<void>
  /** Its close, as every settings sheet has (Properties): back to the room it floats over. */
  onClose?: () => void
}) {
  // Not compiled (Oxc's React Compiler skips a component with a try/finally anywhere in it), so the handlers the
  // sections' memo compares stay useCallbacks.
  const settingsRef = useRef(settings)
  useEffect(() => {
    settingsRef.current = settings
  }, [settings])
  const scrollRef = useRef<HTMLDivElement | null>(null)
  const root = useRef<HTMLElement>(null)
  const paneRef = useCallback((node: HTMLDivElement | null) => { scrollRef.current = node }, [])
  const [page, setPage] = useState<SettingsPageId>(() => section ? settingsPageOf(section.id) : pageForRoom(room))
  const [searchQuery, setSearchQuery] = useState('')
  const [jumpTarget, setJumpTarget] = useState<{ entry: SettingsIndexEntry; nonce: number } | null>(null)
  const [storeNotes, setStoreNotes] = useState<Record<string, StoreNote | undefined>>({})
  /** A store row's result: a plain line, or (error) a failure under the row; an empty text clears it. */
  const storeStatus = useCallback((store: string, text: string, error = false) => {
    setStoreNotes(previous => ({ ...previous, [store]: text ? { text, error } : undefined }))
  }, [])
  const [storeBusy, setStoreBusy] = useState<Record<string, StoreAction | undefined>>({})
  const storeBusyRef = useRef<Record<string, StoreAction | undefined>>({})
  const [storeCheckBusy, setStoreCheckBusy] = useState(false)
  const [storeCheckCopy, setStoreCheckCopy] = useState<string | null>(null)
  /** Scrolls a section's heading to the column's top; keyboard users land in it, not back at the top. */
  const jump = useCallback((id: SettingsSectionId, smooth = true) => {
    if (scrollToSection(scrollRef.current, `#set-${id}`, smooth))
      scrollRef.current?.querySelector<HTMLElement>(`#set-${id}-title`)?.focus({ preventScroll: true })
  }, [])
  /** A page from the sidebar: shown from its top. */
  const show = useCallback((next: SettingsPageId) => {
    setPage(next)
    if (scrollRef.current) scrollRef.current.scrollTop = 0
  }, [])

  // Each opening lands once: on the section asked for (the Library's Sign in to a store), else on the
  // page of the room Settings floats over.
  const openedFor = useRef<typeof section>(null)
  const wasActive = useRef(false)
  const [landing, setLanding] = useState<SettingsSectionId | null>(null)
  useEffect(() => {
    const opening = active && !wasActive.current
    wasActive.current = active
    if (!active) return
    if (section && openedFor.current !== section) {
      openedFor.current = section
      setPage(settingsPageOf(section.id))
      setLanding(section.id)
    } else if (opening) {
      setPage(pageForRoom(room))
      if (scrollRef.current) scrollRef.current.scrollTop = 0
    }
  }, [active, section, room])
  // A part of a page (Library › Game stores) once the page is drawn.
  useEffect(() => {
    if (!landing) return
    setLanding(null)
    if (landing !== page) jump(landing, false)
    else if (scrollRef.current) scrollRef.current.scrollTop = 0
  }, [landing, page, jump])

  /** Saves in order; null when it took, else the host's reason (a folder it refused says why). */
  const saveWithReason = useCallback(async (patch: Partial<LauncherSettings>): Promise<string | null> => {
    if (!settingsRef.current) return 'Settings are still loading.'
    try {
      await saveSettingsPatch(patch)
      return null
    } catch (error) {
      return error instanceof Error && error.message ? error.message : 'Could not save. Try again.'
    }
  }, [])

  const refreshLibrary = useCallback(() => {
    void host.storesMatrix().then(next => onStores?.(next)).catch(() => undefined)
  }, [onStores])

  const startStoreAction = useCallback((store: string, action: StoreAction): boolean => {
    if (storeBusyRef.current[store]) return false
    const next = { ...storeBusyRef.current, [store]: action }
    storeBusyRef.current = next
    setStoreBusy(next)
    return true
  }, [])

  const finishStoreAction = useCallback((store: string, action: StoreAction) => {
    if (storeBusyRef.current[store] !== action) return
    const next = { ...storeBusyRef.current }
    delete next[store]
    storeBusyRef.current = next
    setStoreBusy(next)
  }, [])

  const connectStore = useCallback(async (store: StoreStatus) => {
    if (!startStoreAction(store.store, 'connect')) return
    storeStatus(store.store, '')
    try {
      const result = await host.storesAuth(store.store)
      if (result.message || !result.ok)
        storeStatus(store.store, result.message ?? `Could not sign in to ${store.displayName}. Try again.`, !result.ok)
      if (result.ok) {
        try {
          await onStores?.(await host.storesMatrix())
        } catch {
          storeStatus(store.store, 'Signed in, but the store list could not be read. Press Check again.', true)
        }
      }
    } catch (error) {
      storeStatus(store.store, errorText(error, `Exo couldn’t sign in to ${store.displayName}. Try again.`), true)
    } finally {
      finishStoreAction(store.store, 'connect')
    }
  }, [startStoreAction, finishStoreAction, onStores, storeStatus])

  const checkStores = useCallback(async () => {
    if (storeCheckBusy) return
    setStoreCheckBusy(true)
    setStoreCheckCopy(null)
    try {
      const check = await host.storesCheck()
      if (check.state === 'failed') {
        setStoreCheckCopy('Could not check the stores. Try again.')
        return
      }
      await onStores?.(await host.storesMatrix())
    } catch {
      setStoreCheckCopy('Could not check the stores. Try again.')
    } finally {
      setStoreCheckBusy(false)
    }
  }, [storeCheckBusy, onStores])

  const openStore = useCallback(async (store: StoreStatus) => {
    if (!startStoreAction(store.store, 'open')) return
    storeStatus(store.store, '')
    try {
      const result: { ok: boolean; message?: string } = store.store === 'microsoft-store'
        ? await host.openUrl('ms-windows-store://home')
        : await host.showStore(store.store)
      if (!result.ok) storeStatus(store.store, errorText(result.message, `Exo couldn’t open ${store.displayName}. Try again.`), true)
    } catch (error) {
      storeStatus(store.store, errorText(error, `Exo couldn’t open ${store.displayName}. Try again.`), true)
    } finally {
      finishStoreAction(store.store, 'open')
    }
  }, [startStoreAction, finishStoreAction, storeStatus])

  const searchHits = useMemo(() => searchSettings(searchQuery), [searchQuery])
  useEffect(() => {
    if (!active) setSearchQuery('')
  }, [active])
  useSettingsEscape(active, searchQuery, () => setSearchQuery(''), onClose)
  useSettingsSearchJump(scrollRef, page, jumpTarget, useCallback(() => setJumpTarget(null), []))

  const selectSearchHit = useCallback((entry: SettingsIndexEntry) => {
    setSearchQuery('')
    setPage(entry.page)
    setJumpTarget({ entry, nonce: Date.now() })
  }, [])

  const pageTitle = SETTINGS_PAGES.find(each => each.page === page)?.label

  const pageBody = (() => {
    switch (page) {
      case 'library': return (
        <div className="exo-settings-layout" key="library">
          <StoresSection
            stores={stores}
            storeBusy={storeBusy}
            storeCheckBusy={storeCheckBusy}
            storeCheckCopy={storeCheckCopy}
            storeNotes={storeNotes}
            onCheckStores={checkStores}
            onConnect={connectStore}
            onOpen={openStore}
            active={active}
            settings={settings}
            onSettings={onSettings}
          />
          <LibraryToolsRow settings={settings} onSave={saveWithReason} onLibraryChanged={refreshLibrary} />
          <ShadersSection />
        </div>
      )
      case 'downloads': return (
        <div className="exo-settings-layout" key="downloads">
          <DownloadsSection settings={settings} disabled={!settings} save={saveWithReason} />
        </div>
      )
      case 'ingame': return (
        <div className="exo-settings-layout" key="ingame">
          <InGameSection settings={settings} onSave={saveWithReason} />
        </div>
      )
      case 'music': return (
        <div className="exo-settings-layout" key="music">
          <AppsSection />
        </div>
      )
      case 'general': return (
        <div className="exo-settings-layout" key="general">
          <GeneralSection settings={settings} onSave={saveWithReason} />
          <BackupSection />
        </div>
      )
      case 'about': return (
        <div className="exo-settings-layout" key="about">
          <AboutSection
            version={settings?.appVersion ?? null}
            updateBusy={updateBusy}
            updateError={updateError}
            updatePercent={updatePercent}
            updateStatus={updateStatus}
            updateAvailable={updateAvailable}
            onCheckUpdate={onCheckUpdate}
            onInstallUpdate={onInstallUpdate}
          />
          <AccountSection />
          <PrivacySection />
        </div>
      )
    }
  })()

  return (
    <main ref={root} className="exo-settings" aria-labelledby="exo-settings-title">
      <SettingsSidebar
        page={page}
        searchQuery={searchQuery}
        hits={searchHits}
        onSearch={setSearchQuery}
        onSelectPage={show}
        onSelectHit={selectSearchHit}
        onClose={onClose}
      />
      <div className="exo-settings-main">
        <div className="exo-settings-scroll" ref={paneRef} data-room-scroll>
          <div className="exo-settings-inner">
            <h2 id="exo-settings-page-title" className="exo-settings-page-title">{pageTitle}</h2>
            {pageBody}
          </div>
        </div>
      </div>
    </main>
  )
})

function findJumpRow(container: HTMLElement, entry: SettingsIndexEntry): HTMLElement | null {
  const labels = container.querySelectorAll<HTMLElement>('.exo-set-item-label')
  for (const label of labels) {
    const text = label.textContent?.trim() ?? ''
    if (text === entry.label || (entry.label === 'Exo Launcher' && text.startsWith('Exo Launcher'))) {
      return label.closest<HTMLElement>('.exo-set-item') ?? label
    }
  }
  return entry.sectionId ? container.querySelector<HTMLElement>(`#set-${entry.sectionId}`) : null
}

function useSettingsSearchJump(
  scrollRef: { current: HTMLDivElement | null },
  page: SettingsPageId,
  jumpTarget: { entry: SettingsIndexEntry; nonce: number } | null,
  onDone: () => void,
) {
  const timerRef = useRef(0)
  useEffect(() => () => window.clearTimeout(timerRef.current), [])
  useEffect(() => {
    if (!jumpTarget || page !== jumpTarget.entry.page) return
    const frame = window.requestAnimationFrame(() => {
      const container = scrollRef.current
      if (!container) return
      const target = findJumpRow(container, jumpTarget.entry)
      if (!target) { onDone(); return }
      const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches || document.documentElement.dataset.exoMotion === 'reduce'
      target.scrollIntoView({ block: 'center', behavior: reduced ? 'auto' : 'smooth' })
      const control = target.querySelector<HTMLElement>('button, input, [role="switch"], select, [tabindex]:not([tabindex="-1"])')
      if (control) control.focus({ preventScroll: true })
      else {
        target.tabIndex = -1
        target.focus({ preventScroll: true })
      }
      for (const prev of container.querySelectorAll('.exo-set-item.is-highlight')) prev.classList.remove('is-highlight')
      target.classList.add('is-highlight')
      window.clearTimeout(timerRef.current)
      timerRef.current = window.setTimeout(() => target.classList.remove('is-highlight'), 1800)
      onDone()
    })
    return () => window.cancelAnimationFrame(frame)
  }, [scrollRef, page, jumpTarget, onDone])
}

function useSettingsEscape(active: boolean, searchQuery: string, onClear: () => void, onClose?: () => void) {
  useEffect(() => {
    if (!active || !onClose) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented) return
      if (document.querySelector('[popover]:popover-open') || (event.target as Element | null)?.closest?.('[data-exo-armed]')) return
      event.preventDefault()
      if (searchQuery) { onClear(); return }
      onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [active, searchQuery, onClear, onClose])
}

function SettingsSidebar({
  page,
  searchQuery,
  hits,
  onSearch,
  onSelectPage,
  onSelectHit,
  onClose,
}: {
  page: SettingsPageId
  searchQuery: string
  hits: readonly SettingsIndexEntry[]
  onSearch: (query: string) => void
  onSelectPage: (page: SettingsPageId) => void
  onSelectHit: (entry: SettingsIndexEntry) => void
  onClose?: () => void
}) {
  const searching = searchQuery.trim().length > 0
  return (
    <nav className="exo-settings-nav" aria-labelledby="exo-settings-title">
      {onClose ? (
        <button type="button" className="exo-glass exo-game-back exo-settings-back" onClick={onClose}>
          <ChevronLeft size={16} />
          Back
        </button>
      ) : null}
      <h2 id="exo-settings-title" className="exo-settings-nav-title">Settings</h2>
      <label className="exo-settings-search">
        <Search size={15} />
        <input
          type="search"
          value={searchQuery}
          placeholder="Search settings"
          aria-label="Search settings"
          onChange={event => onSearch(event.target.value)}
        />
        {searchQuery ? (
          <button type="button" className="exo-settings-search-clear" aria-label="Clear settings search" onClick={() => onSearch('')}>
            <Close size={13} />
          </button>
        ) : null}
      </label>
      {searching ? (
        hits.length > 0 ? (
          <ul className="exo-settings-hits" role="listbox" aria-label="Matching settings">
            {hits.map(hit => (
              <li key={hit.id}>
                <button type="button" className="exo-settings-hit" onClick={() => onSelectHit(hit)}>
                  <span className="exo-settings-hit-label">{hit.label}</span>
                  <span className="exo-settings-hit-page">{hit.pageLabel}</span>
                </button>
              </li>
            ))}
          </ul>
        ) : <p className="exo-settings-hits-empty">No matching settings</p>
      ) : (
        <ul className="exo-settings-nav-pages">
          {SETTINGS_PAGES.map(({ page: id, label }) => {
            const Icon = PAGE_ICONS[id]
            return (
              <li key={id}>
                <button type="button" aria-current={page === id ? 'page' : undefined} onClick={() => onSelectPage(id)}>
                  <Icon size={15} />
                  <span>{label}</span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </nav>
  )
}
