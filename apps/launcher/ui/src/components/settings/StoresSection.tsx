import { Fragment, memo, useEffect, useState } from 'react'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { host, onHostEvent, type AccountStore, type LauncherSettings, type RiotBaseline, type StoreAccounts, type StoreStatus, type SwitchStore } from '../../lib/host'
import { readSettings, saveSettingsPatch } from '../../lib/settingsSave'
import { AccountControl, AccountHours, accountName, useStoreAccounts } from './StoreAccounts'
import {
  availableStoreRows,
  canConnectStore,
  canOpenStoreClient,
  settingsStoreRows,
  signsInInOwnApp,
  storeClientDownloadUrl,
  storeLayoutNote,
  storeState,
} from '../../lib/stores'
import { Spinner } from '../ui/Spinner'
import { ActionButton } from '../ui/ConfirmButton'
import { errorText, type PlainError } from '../../lib/errorWords'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Wait, sample, times } from '../ui/Wait'
import { Row, Section, State, SwitchRow } from './parts'
import { SteamLibraryPanel, useSteamLibrary, type SteamLibrary } from './SteamSignInRow'
import { riotIdShown } from '../../lib/canUse'
import { ErrorNote } from '../ui/ErrorNote'

export type StoreAction = 'connect' | 'open'
/** A store row's last result: a plain line, or an error under the row. */
export type StoreNote = { text: string; error: boolean }

const ACCOUNT_STORES = new Set<string>(['steam', 'riot', 'epic', 'gog', 'amazon'])

/** Sign in is offered only when no account is known: a signed-in account (its name shown) has its menu, which holds Sign out. */
function canOfferSignIn(store: StoreStatus, accounts: StoreAccounts | null): boolean {
  return canConnectStore(store) && accountName(store.store, accounts) === null
}

function InstalledStore({ store, busy, note, accounts, library, onConnect, onOpen, onAccountsChanged }: {
  library?: SteamLibrary
  store: StoreStatus
  busy: StoreAction | undefined
  note: StoreNote | undefined
  accounts: StoreAccounts | null
  onConnect: (store: StoreStatus) => Promise<void>
  onOpen: (store: StoreStatus) => Promise<void>
  onAccountsChanged: () => void
}) {
  const [accountBusy, setAccountBusy] = useState(false)
  const [accountNote, setAccountNote] = useState<string | null>(null)
  const [accountError, setAccountError] = useState<string | null>(null)
  const state = storeState(store)
  const connect = canOfferSignIn(store, accounts)
  const handoff = signsInInOwnApp(store)
  const open = !handoff && canOpenStoreClient(store)
  const who = accountName(store.store, accounts)
  const showAccount = accounts !== null && ACCOUNT_STORES.has(store.store) && (who !== null || store.store === 'steam' || store.store === 'riot')
  const account = {
    busy: accountBusy || !!busy,
    onBusy: setAccountBusy,
    onLine: setAccountNote,
    onError: setAccountError,
    onDone: onAccountsChanged,
  }
  return (
    <Row
      label={store.displayName}
      icon={<CompanyLogo company={store.store} size={22} />}
      error={accountError ?? noteError(note)}
      detail={accountNote ?? rowLine(note, library, store)}
    >
      <State on={state.on}>{who ?? state.label}</State>
      {connect ? (
        <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void onConnect(store)}>
          {busy === 'connect' ? <Spinner label="Signing in" /> : null}Sign in
        </button>
      ) : null}
      {handoff ? (
        <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void onConnect(store)}>
          {busy === 'connect' ? <Spinner label={`Opening ${store.displayName}`} /> : null}{`Open ${store.displayName}`}
        </button>
      ) : null}
      {open ? (
        <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void onOpen(store)}>
          {busy === 'open' ? <Spinner label={`Opening ${store.displayName}`} /> : null}Open
        </button>
      ) : null}
      {showAccount && accounts ? (store.store === 'steam'
        ? <SteamAccount accounts={accounts} library={library} {...account} />
        : <AccountControl store={store.store as AccountStore | SwitchStore} accounts={accounts} {...account} />) : null}
    </Row>
  )
}

/** Steam's Account menu also holds the sign-in to Steam's website (the Library's gifts). */
function SteamAccount({ library, ...props }: Omit<Parameters<typeof AccountControl>[0], 'store' | 'extra'> & { library?: SteamLibrary }) {
  const { signedIn, change } = useSteamWebSignIn()
  const web = signedIn === null ? [] : [{
    id: 'steam-web',
    label: signedIn ? "Sign out of Steam's website" : "Sign in to Steam's website",
    run: () => change(!signedIn),
  }]
  // The library sign-in: opens the panel in this row, or signs the library out (asks first, as the menu's other sign-outs do).
  const libraryItems = library?.account === null || library === undefined ? [] : library.account.signedIn
    ? [{ id: 'library-out', label: 'Sign out of Steam library', confirm: 'Sign out of the Steam library?', pending: { line: 'Exo signs out of the Steam library', run: library.signOut } }]
    : [{ id: 'library-in', label: 'Sign in to Steam…', run: library.begin }]
  return <AccountControl store="steam" extra={[...libraryItems, ...web]} {...props} />
}

/** The Steam row's line while the library sign-in is on. */
const libraryLine = (library: SteamLibrary | undefined): string | null =>
  library?.account?.signedIn ? `Library: ${library.account.accountName ?? 'Steam account'}. Signed in.` : null
/** The Steam row's line: its last note, else the library's sign-in, else the store's layout note. */
const rowLine = (note: StoreNote | undefined, library: SteamLibrary | undefined, store: StoreStatus) =>
  noteLine(note) ?? libraryLine(library) ?? storeLayoutNote(store)

const noteError = (note: StoreNote | undefined) => (note?.error ? note.text : null)
const noteLine = (note: StoreNote | undefined) => (note && !note.error ? note.text : null)

function MissingStore({ store, onError }: { store: StoreStatus; onError: (error: string | null) => void }) {
  const [busy, setBusy] = useState(false)
  const url = storeClientDownloadUrl(store.store)
  if (!url) return null
  async function get() {
    if (!url) return
    setBusy(true)
    onError(null)
    const failed = `Could not open the ${store.displayName} download. Try again.`
    try {
      const result = await host.openUrl(url)
      if (!result.ok) onError(failed)
    } catch (cause) {
      report(cause, 'StoresSection.getStore')
      onError(errorText(cause, failed))
    } finally {
      setBusy(false)
    }
  }
  return (
    <li>
      <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void get()}>
        <CompanyLogo company={store.store} size={16} />
        {store.displayName}
      </button>
    </li>
  )
}

/** Whether Exo is signed in to Steam's website, as last read; null until the first answer. */
let steamSignedIn: boolean | null = null

/**
 * Steam's sign-in, as Epic's goes through Legendary: Valve's own sign-in page in Exo's window, once, kept until Sign
 * out. It is what Steam's website needs (the Library's gifts); Exo reads the rest from the Steam app. A row built again
 * (Settings after it was put away) draws the last answer at once.
 */
function useSteamWebSignIn() {
  const [signedIn, setSignedIn] = useState<boolean | null>(steamSignedIn)
  useEffect(() => {
    const known = (next: boolean) => { steamSignedIn = next; setSignedIn(next) }
    host.giftsList().then(result => known(result.steamSignedIn)).catch(() => known(false))
    return onHostEvent('gifts.changed', result => known(result.steamSignedIn))
  }, [])
  const change = async (signIn: boolean): Promise<{ ok: boolean; message?: string | null }> => {
    const failed = signIn ? 'Could not sign in to Steam. Try again.' : 'Could not sign out of Steam. Try again.'
    try {
      const result = await (signIn ? host.giftsSteamSignIn() : host.giftsSteamSignOut())
      if (!result.ok) return { ok: false, message: errorText(result.message, failed) }
      steamSignedIn = signIn
      setSignedIn(signIn)
      return result
    } catch {
      return { ok: false, message: failed }
    }
  }
  return { signedIn, change }
}

/**
 * Steam's own offline mode for the account it signs in as, under Steam's row: Steam restarts out of sight into it, or
 * back online, its steps in the row's line while it runs and the result after; why it can't (a Steam game running, a
 * download, no saved sign-in) under the row. Shown only where Steam can play offline, or is offline now.
 */
function SteamOfflineRow({ offline, onChanged }: { offline: boolean; onChanged: () => void }) {
  const [pending, setPending] = useState<boolean | null>(null)
  const [line, setLine] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    if (pending === null) return
    return onHostEvent('accounts.progress', progress => { if (progress.store === 'steam') setLine(progress.line) })
  }, [pending])
  async function change(next: boolean) {
    const failed = next ? 'Could not turn on offline mode. Try again.' : 'Could not turn off offline mode. Try again.'
    setPending(next)
    setError(null)
    setLine(null)
    try {
      const result = await host.steamSetOffline(next)
      setLine(result.ok ? result.message ?? null : null)
      if (!result.ok) setError(errorText(result.message, failed))
    } catch (cause) {
      report(cause, 'StoresSection.steamOffline')
      setError(errorText(cause, failed))
    } finally {
      setPending(null)
      onChanged()
    }
  }
  return (
    <Row label="Offline mode" icon={<CompanyLogo company="steam" size={22} />} detail={line} error={error}>
      {pending !== null ? <Spinner label={pending ? 'Turning on offline mode' : 'Turning off offline mode'} /> : null}
      <Switch checked={pending ?? offline} disabled={pending !== null} label="Steam offline mode" onChange={next => void change(next)} />
    </Row>
  )
}

type Note = { text: string; error: boolean } | null

const RIOT_ID = /^[^#]+#[^#]{2,}$/

/** Riot ID (Name#TAG): keys this PC's Riot playtime ledger. */
function RiotIdRow({ riotId, onSettings }: { riotId: string; onSettings: (next: LauncherSettings) => void }) {
  const [editing, setEditing] = useState(riotId === '')
  const [draft, setDraft] = useState(riotId)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<Note>(null)
  const value = draft.trim()
  const ready = value !== '' && value !== riotId && RIOT_ID.test(value)

  async function save(next: string) {
    setBusy(true)
    setNote(null)
    try {
      await saveSettingsPatch({ riotId: next })
      const read = await readSettings()
      if (read) onSettings(read)
      return { ok: true }
    } finally {
      setBusy(false)
    }
  }
  function saved() {
    setEditing(false)
  }
  const failed = (error: PlainError) => setNote({ text: error.text, error: true })

  return (
    <Row label="Riot ID" detail={note && !note.error ? note.text : undefined} error={note?.error ? note.text : null}>
      {editing ? (
        <form className="exo-set-key-form" onSubmit={event => { event.preventDefault(); event.currentTarget.querySelector<HTMLButtonElement>('.exo-action:not(:disabled)')?.click() }}>
          <input type="text" className="exo-field" value={draft} autoComplete="off" spellCheck={false} placeholder="Name#TAG"
            aria-label="Riot ID" disabled={busy} onChange={event => setDraft(event.target.value)} />
          <ActionButton className="exo-set-btn" disabled={!ready} context="StoresSection.riotId" fallback="Riot ID could not be saved. Try again."
            why="none" onFailed={failed} onPress={() => save(value)} onDone={saved}>Save</ActionButton>
          {riotId ? <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { setDraft(riotId); setEditing(false) }}>Cancel</button> : null}
        </form>
      ) : (
        <>
          <State on>{riotId}</State>
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => setEditing(true)}>Replace</button>
          <ActionButton className="exo-set-btn" context="StoresSection.riotIdRemove" fallback="Riot ID could not be removed. Try again."
            why="none" onFailed={failed} onPress={() => save('')} onDone={saved}>Remove</ActionButton>
        </>
      )}
    </Row>
  )
}

function trackerUrlFor(riotId: string): string {
  const hash = riotId.indexOf('#')
  if (hash <= 0) return 'https://tracker.gg/valorant'
  return `https://tracker.gg/valorant/profile/riot/${encodeURIComponent(riotId.slice(0, hash))}%23${encodeURIComponent(riotId.slice(hash + 1))}/overview`
}

function formatBaselineHours(minutes: number | null | undefined): string {
  if (!minutes || minutes <= 0) return ''
  const hours = minutes / 60
  return Number.isInteger(hours) ? String(hours) : hours.toFixed(1)
}

function parseHoursInput(raw: string): number | null {
  const trimmed = raw.trim()
  if (!trimmed) return null
  const n = Number(trimmed)
  if (!Number.isFinite(n) || n <= 0) return Number.NaN
  return n
}

function RiotBaselineRow({
  riotId,
  baseline,
  onSettings,
}: {
  riotId: string
  baseline: RiotBaseline | null
  onSettings: (next: LauncherSettings) => void
}) {
  const hasSaved = !!(baseline?.valorantMinutes || baseline?.leagueMinutes)
  const [editing, setEditing] = useState(!hasSaved)
  const [valorant, setValorant] = useState(() => formatBaselineHours(baseline?.valorantMinutes))
  const [league, setLeague] = useState(() => formatBaselineHours(baseline?.leagueMinutes))
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<Note>(null)

  const syncFromSaved = () => {
    setValorant(formatBaselineHours(baseline?.valorantMinutes))
    setLeague(formatBaselineHours(baseline?.leagueMinutes))
  }

  const parsedValorant = parseHoursInput(valorant)
  const parsedLeague = parseHoursInput(league)
  const invalid = Number.isNaN(parsedValorant) || Number.isNaN(parsedLeague)
  const ready = !invalid && (parsedValorant !== null || parsedLeague !== null)

  async function saveBaseline() {
    if (!ready) return { ok: false, message: 'Enter starting hours greater than zero.' }
    setBusy(true)
    setNote(null)
    try {
      const result = await host.setRiotBaseline({
        riotId,
        ...(parsedValorant !== null ? { valorantHours: parsedValorant } : {}),
        ...(parsedLeague !== null ? { leagueHours: parsedLeague } : {}),
      })
      const read = await readSettings()
      if (read) onSettings(read)
      return result
    } finally {
      setBusy(false)
    }
  }

  function savedBaseline(result: unknown) {
    const reply = (result ?? {}) as { message?: string | null }
    setNote({ text: reply.message ?? 'Starting hours saved.', error: false })
    setEditing(false)
  }

  const failed = (error: PlainError) => setNote({ text: error.text, error: true })
  const summaryParts = [
    baseline?.valorantMinutes ? `VALORANT ${formatBaselineHours(baseline.valorantMinutes)} h` : null,
    baseline?.leagueMinutes ? `League ${formatBaselineHours(baseline.leagueMinutes)} h` : null,
  ].filter(Boolean)

  const detail = note && !note.error
    ? note.text
    : (
      <>
        tracker.gg blocks automated imports. Add hours you played before Exo so new sessions add onto them.{' '}
        <button type="button" className="exo-set-link" onClick={() => void host.openUrl(trackerUrlFor(riotId))}>Open tracker.gg</button>
      </>
    )

  return (
    <Row label="Starting hours" detail={detail} error={note?.error ? note.text : null}>
      {editing ? (
        <form className="exo-set-key-form" onSubmit={event => { event.preventDefault(); event.currentTarget.querySelector<HTMLButtonElement>('.exo-action:not(:disabled)')?.click() }}>
          <input type="number" min="0.5" max="166666" step="0.5" className="exo-field is-short" value={valorant}
            placeholder="VALORANT (h)" aria-label="VALORANT starting hours" disabled={busy}
            onChange={event => setValorant(event.target.value)} />
          <input type="number" min="0.5" max="166666" step="0.5" className="exo-field is-short" value={league}
            placeholder="League (h)" aria-label="League of Legends starting hours" disabled={busy}
            onChange={event => setLeague(event.target.value)} />
          <ActionButton className="exo-set-btn" disabled={!ready || busy} context="StoresSection.riotBaseline"
            fallback="Starting hours could not be saved. Try again." why="none"
            onFailed={failed} onPress={saveBaseline} onDone={savedBaseline}>Save</ActionButton>
          {hasSaved ? (
            <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { syncFromSaved(); setEditing(false) }}>Cancel</button>
          ) : null}
        </form>
      ) : (
        <>
          <State on>{summaryParts.join(' · ')}</State>
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { syncFromSaved(); setNote(null); setEditing(true) }}>Edit</button>
        </>
      )}
    </Row>
  )
}

export const StoresSection = memo(function StoresSection({
  stores,
  storeBusy,
  storeCheckBusy,
  storeCheckCopy,
  storeNotes,
  onCheckStores,
  onConnect,
  onOpen,
  active,
  settings,
  onSettings,
}: {
  active: boolean
  settings: LauncherSettings | null
  onSettings: (next: LauncherSettings) => void
  stores: StoreStatus[]
  storeBusy: Record<string, StoreAction | undefined>
  storeCheckBusy: boolean
  storeCheckCopy: string | null
  storeNotes: Record<string, StoreNote | undefined>
  onCheckStores: () => Promise<void>
  onConnect: (store: StoreStatus) => Promise<void>
  onOpen: (store: StoreStatus) => Promise<void>
}) {
  const installed = settingsStoreRows(stores)
  const missing = availableStoreRows(stores).filter(store => storeClientDownloadUrl(store.store))
  const { accounts, reload } = useStoreAccounts(active)
  const changed = () => { void reload(); void onCheckStores() }
  const steamLibrary = useSteamLibrary()
  const hasAccounts = installed.some(store => store.store === 'steam' || store.store === 'epic')
  const [getError, setGetError] = useState<string | null>(null)
  return (
    <Section
      id="accounts"
      title="Game stores"
      action={
        <button type="button" className="exo-set-btn" disabled={storeCheckBusy} onClick={() => void onCheckStores()}>
          {storeCheckBusy ? <Spinner label="Looking for stores" /> : null}Check again
        </button>
      }
    >
      {installed.length === 0 ? (
        storeCheckBusy ? <Wait label="Looking for stores">{times(3, i => <Row label={sample(i)} detail={sample(i + 2)} icon={<svg width="22" height="22" />}><span className="exo-set-btn">Connect</span></Row>)}</Wait> : <Row label="No store apps found on this PC" error={storeCheckCopy} />
      ) : (
        installed.map(store => (
          <Fragment key={store.store}>
            <InstalledStore
              store={store}
              busy={storeBusy[store.store]}
              note={storeNotes[store.store]}
              accounts={accounts}
              library={store.store === 'steam' ? steamLibrary : undefined}
              onConnect={onConnect}
              onOpen={onOpen}
              onAccountsChanged={changed}
            />
            {store.store === 'steam' && steamLibrary.open ? <SteamLibraryPanel library={steamLibrary} /> : null}
            {store.store === 'steam' && accounts?.steam && (accounts.steam.canOffline || accounts.steam.offline)
              ? <SteamOfflineRow offline={accounts.steam.offline} onChanged={changed} />
              : null}
          </Fragment>
        ))
      )}
      {installed.length > 0 && storeCheckCopy ? (
        <div className="exo-set-item"><ErrorNote className="exo-set-error" message={storeCheckCopy} /></div>
      ) : null}
      {hasAccounts ? <AccountHours accounts={accounts} onChanged={changed} /> : null}
      {hasAccounts && settings ? (
        <SwitchRow label="Keep store apps quiet" checked={settings.storeClientsQuiet !== false}
          onChange={quiet => {
            onSettings({ ...settings, storeClientsQuiet: quiet })
            return saveSettingsPatch({ storeClientsQuiet: quiet }).then(() => null, () => {
              onSettings({ ...settings, storeClientsQuiet: !quiet })
              return 'Not saved. Try again.'
            })
          }} />
      ) : null}
      {riotIdShown(installed.map(store => store.store), settings?.riotId) ? (
        <>
          <RiotIdRow riotId={settings?.riotId?.trim() || ''} onSettings={onSettings} />
          {settings?.riotId?.trim() ? (
            <RiotBaselineRow
              key={settings.riotId.trim()}
              riotId={settings.riotId.trim()}
              baseline={settings.riotBaseline ?? null}
              onSettings={onSettings}
            />
          ) : null}
        </>
      ) : null}
      {missing.length > 0 ? (
        <Row label="Get another store" stacked error={getError}>
          <ul className="exo-set-chips" aria-label="Stores not on this PC">
            {missing.map(store => (
              <MissingStore key={store.store} store={store} onError={setGetError} />
            ))}
          </ul>
        </Row>
      ) : null}
    </Section>
  )
})
