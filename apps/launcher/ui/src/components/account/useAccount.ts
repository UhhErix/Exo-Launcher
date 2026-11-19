import { useEffect, useSyncExternalStore } from 'react'
import { accountRequest, accountStore, watchAccount } from '../../lib/accountStore'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'

export type AccountOverview = Awaited<ReturnType<typeof host.exoAccountOverview>>

/** The page's one account state (lib/accountStore.ts), read again whenever the host says the session changed. */
const account = accountStore<AccountOverview>(
  () => host.exoAccountOverview(),
  (error: unknown) => report(error, 'useAccount.overview', { level: 'warn' }),
)
let watching = false

function watch() {
  if (watching) return
  watching = true
  watchAccount(account, onHostEvent)
}

/** Reads the account state again (after an account action whose answer changes it). */
export const refreshAccount = () => account.refresh()

/** Continue without an account: the gate stays shut until the service answers again, wherever it was chosen. */
export const continueWithoutAccount = () => account.continueOffline()

/** The service answers again and no game runs: the gate follows the account state again. */
export const accountServiceBack = () => account.serviceBack()

/** A recovery code screen is showing this code: returns the call that says it went. */
export const recoveryCodeShown = (code: string) => account.codeShown(code)

/** An account call the page waits on: counted while on its way, its recovery code on screen from the moment it lands. */
export const sendAccountRequest = <R extends { recoveryCode?: string | null }>(call: () => Promise<R>) =>
  accountRequest(account, call, answer => answer.recoveryCode)

/** The signed-in state and the sync status, shared by every screen that shows them. */
export function useAccount() {
  useEffect(watch, [])
  const view = useSyncExternalStore(account.subscribe, account.get)
  return { overview: view.overview, laterThisRun: view.laterThisRun, codesOnScreen: view.codesOnScreen, requests: view.requests, refresh: refreshAccount }
}
