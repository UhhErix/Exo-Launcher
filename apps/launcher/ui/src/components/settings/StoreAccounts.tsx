import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent, type AccountStore, type StoreAccounts, type SwitchStore } from '../../lib/host'
import { Menu, type MenuItem } from '../ui/Menu'
import { ChevronDown } from '../../brand/icons'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Spinner } from '../ui/Spinner'
import { Row } from './parts'
import { errorText } from '../../lib/errorWords'
import { report } from '../../lib/report'

type Result = { ok: boolean; message?: string | null }

/** An action picked from a store's Account menu that asks first, in its row (MenuItem `confirm`): the row's line while it runs. */
type Pending = { line: string; run: () => Promise<Result> }

/** The last accounts read, so Settings or setup built again draws them at once and reads again behind. */
let lastAccounts: StoreAccounts | null = null

/**
 * Reads who each store is signed in as: on each `active`, and once before that when nothing has been read yet (Settings
 * built hidden after the library is usable, so its first open has them); `reload` after any change.
 */
export function useStoreAccounts(active: boolean) {
  const [accounts, setAccounts] = useState<StoreAccounts | null>(lastAccounts)
  const reload = useCallback(async () => {
    const next = await host.accountsList().catch(() => null)
    lastAccounts = next
    setAccounts(next)
  }, [])
  useEffect(() => { if (active || lastAccounts === null) void reload() }, [active, reload])
  return { accounts, reload }
}

/** Who a store row is signed in as, or null when Exo does not sign into that store. */
export function accountName(store: string, accounts: StoreAccounts | null): string | null {
  if (!accounts) return null
  switch (store) {
    case 'steam': return accounts.steam?.current ?? null
    case 'riot': return accounts.riot?.current ?? null
    case 'epic': return accounts.epic.signedIn ? accounts.epic.name ?? 'Signed in' : null
    case 'gog': return accounts.gog.signedIn ? 'Signed in' : null
    case 'amazon': return accounts.amazon.signedIn ? accounts.amazon.name ?? 'Signed in' : null
    default: return null
  }
}

const CLIENT: Record<SwitchStore, string> = { steam: 'Steam', riot: 'Riot Client' }
const NAME: Record<AccountStore | SwitchStore, string> = { steam: 'Steam', riot: 'Riot', epic: 'Epic', gog: 'GOG', amazon: 'Amazon' }

type AccountItem = MenuItem & { pending?: Pending; run?: () => Promise<Result>; line?: string }

/** One of Steam's remembered accounts or the Riot accounts Exo saved. */
type SwitchAccount = { key: string; name: string; current: boolean; saved?: boolean }

/**
 * Steam's and Riot's Account menu, the same menu as every store's: the accounts on this PC (the one in use checked; any
 * other switches to it), Add account, anything else the row has (`extra`), and Forget for the others. Switching runs at
 * once, its steps in the row's line; Forget asks first.
 */
function switchMenuFor(store: SwitchStore, accounts: StoreAccounts, extra: AccountItem[]): AccountItem[] {
  const list: SwitchAccount[] = (store === 'steam' ? accounts.steam?.accounts : accounts.riot?.accounts) ?? []
  const items: AccountItem[] = list.map(account => ({
    id: `switch:${account.key}`,
    label: account.name,
    section: 'Accounts',
    on: account.current,
    line: account.current ? undefined : `${CLIENT[store]} restarts to sign in as ${account.name}…`,
    run: account.current ? undefined : () => host.accountsSwitch(store, account.key),
  }))
  items.push({ id: 'add', label: 'Add account', line: `${CLIENT[store]} closes and opens its sign-in`, run: () => host.accountsAdd(store) }, ...extra)
  for (const account of list.filter(entry => !entry.current && (store === 'steam' || entry.saved === true))) {
    items.push({
      id: `forget:${account.key}`,
      label: `Forget ${account.name}`,
      section: 'Forget',
      confirm: `Forget ${account.name}?`,
      danger: true,
      pending: { line: `Forgetting ${account.name}`, run: () => host.accountsRemove(store, account.key) },
    })
  }
  return items
}

function menuFor(store: Exclude<AccountStore, 'steam'>, accounts: StoreAccounts): AccountItem[] {
  const name = NAME[store]
  const items: AccountItem[] = [{
    id: 'switch',
    label: 'Switch account',
    confirm: `Sign out of ${name}?`,
    pending: { line: `Exo signs out of ${name}, then opens its sign-in`, run: () => host.accountsSwitch(store) },
  }]
  if (store === 'epic' && accounts.epic.canImport) {
    items.push({
      id: 'import',
      label: "Use the Epic Games Launcher's account",
      confirm: 'Sign the launcher out?',
      pending: { line: 'The Epic Games Launcher signs out and Exo uses its account', run: host.accountsEpicImport },
    })
  }
  if (store === 'epic') items.push({ id: 'add', label: 'Add another account', run: host.accountsEpicAdd })
  if (store === 'epic' && accounts.epic.canSwitchLauncher) {
    items.push({
      id: 'newlauncher',
      label: "Switch to Epic's new launcher",
      confirm: 'Download it and switch?',
      pending: { line: "Downloading Epic's new launcher", run: host.accountsEpicNewLauncher },
    })
  }
  const canSignOut = store === 'epic' ? accounts.epic.signedIn : store === 'gog' ? accounts.gog.canSignOut : accounts.amazon.signedIn
  if (canSignOut) {
    items.push({
      id: 'signout',
      label: 'Sign out',
      confirm: `Sign out of ${name}?`,
      pending: { line: `Exo signs out of ${name}`, run: () => host.accountsSignOut(store) },
    })
  }
  return items
}

/**
 * The Account menu on a store row. What signs out asks first; while it runs, the row's line says what happens.
 */
export function AccountControl({ store, accounts, extra = [], busy, onBusy, onLine, onError, onDone }: {
  store: AccountStore | SwitchStore
  accounts: StoreAccounts
  extra?: AccountItem[]
  busy: boolean
  onBusy: (busy: boolean) => void
  onLine: (line: string | null) => void
  onError: (error: string | null) => void
  onDone: () => void
}) {
  const [running, setRunning] = useState(false)
  const switches = store === 'steam' || store === 'riot'
  useEffect(() => {
    if (!running || !switches) return
    return onHostEvent('accounts.progress', progress => { if (progress.store === store) onLine(progress.line) })
  }, [running, switches, store, onLine])
  const items = switches ? switchMenuFor(store, accounts, extra) : menuFor(store, accounts)
  if (items.length === 0) return null

  async function run(action: () => Promise<Result>) {
    const failed = `Could not change the ${NAME[store]} account. Try again.`
    onBusy(true)
    setRunning(true)
    onError(null)
    try {
      const result = await action()
      onLine(result.ok ? result.message ?? null : null)
      if (!result.ok) onError(errorText(result.message, failed))
    } catch (cause) {
      report(cause, `StoreAccounts.${store}`)
      onLine(null)
      onError(errorText(cause, failed))
    } finally {
      setRunning(false)
      onBusy(false)
      onDone()
    }
  }

  return (
    <Menu
      label="Account"
      anchor={`--exo-account-${store}`}
      offset={4}
      triggerClassName="exo-set-btn"
      trigger={<>{running ? <Spinner label="Changing the account" /> : null}Account<ChevronDown size={12} /></>}
      items={items}
      disabled={busy}
      onAction={(id) => {
        const item = items.find(entry => entry.id === id)
        if (item?.line) onLine(item.line)
        if (item?.run) void run(item.run)
        else if (item?.pending) {
          onLine(item.pending.line)
          void run(item.pending.run)
        }
      }}
    />
  )
}

/** The Epic accounts added for their hours (hours always count every account on this PC). */
export function AccountHours({ accounts, onChanged }: {
  accounts: StoreAccounts | null
  onChanged: () => void
}) {
  const [removing, setRemoving] = useState<string | null>(null)
  const [removeError, setRemoveError] = useState<{ key: string; message: string } | null>(null)
  // The button says it in itself (ConfirmButton): its ring, then its check, and then the row goes as the list reads
  // again; a refusal turns it Bad with the reason under the row.
  async function remove(key: string) {
    setRemoving(key)
    setRemoveError(null)
    try { return await host.accountsEpicRemove(key) } finally { setRemoving(null) }
  }
  return (
    <>
      {(accounts?.epic.added ?? []).map(account => (
        <Row key={account.key} label={account.name} icon={<CompanyLogo company="epic" size={22} />} error={removeError?.key === account.key ? removeError.message : null}>
          <ConfirmButton className="exo-set-btn" question="Remove?" danger disabled={removing !== null && removing !== account.key}
            context="StoreAccounts.epicRemove" fallback="Could not remove that account. Try again."
            why="none" onFailed={error => { setRemoveError({ key: account.key, message: error.text }); onChanged() }}
            onConfirm={() => remove(account.key)} onDone={onChanged}>
            Remove
          </ConfirmButton>
        </Row>
      ))}
    </>
  )
}
