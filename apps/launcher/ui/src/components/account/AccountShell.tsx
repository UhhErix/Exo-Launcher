import { useEffect, useState } from 'react'
import { accountSheetOpen, laterEnds } from '../../lib/accountPrompt'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { AccountSheet } from './AccountSheet'
import { accountServiceBack, continueWithoutAccount, refreshAccount, useAccount } from './useAccount'

/** While the player continued without an account: how often Exo asks whether the account service answers again. */
const LATER_PROBE_MS = 5 * 60 * 1000

/**
 * The account gate for the shell (lib/accountPrompt.ts): while this PC is signed out, or a recovery code waits to be
 * confirmed, the account sheet is up with nothing else to press. It waits for the What's new sheet, so the two never show
 * together. Once up it holds itself while a recovery code is on screen or an account request is on its way (counted in the
 * page's one account state by the screen and the request themselves), so a session saved before the code is typed back
 * never closes the sheet under the code.
 */
export function useAccountShell(whatsNewOpen: boolean) {
  const { overview, laterThisRun, codesOnScreen, requests } = useAccount()
  const [wasOpen, setWasOpen] = useState(false)
  // A new key starts the sheet's step again at its first screen: after it finished, while the gate is still due.
  const [round, setRound] = useState(0)

  // Continue without an account holds only until the service answers again with no game running.
  useEffect(() => {
    if (!laterThisRun) return
    const timer = window.setInterval(() => {
      void host.exoAccountProbe().then(
        (probe) => { if (laterEnds(probe)) accountServiceBack() },
        (error: unknown) => report(error, 'AccountShell.probe', { level: 'warn' }),
      )
    }, LATER_PROBE_MS)
    return () => window.clearInterval(timer)
  }, [laterThisRun])

  const open = accountSheetOpen({ wasOpen, whatsNewOpen, overview, laterThisRun, codesOnScreen, requests })
  useEffect(() => { if (open !== wasOpen) setWasOpen(open) }, [open, wasOpen])
  return {
    sheetOpen: open,
    round,
    continueOffline: continueWithoutAccount,
    onSignedIn: () => { setRound(value => value + 1); void refreshAccount() },
  }
}

type AccountShellState = ReturnType<typeof useAccountShell>

export function AccountOverlays({ shell }: { shell: AccountShellState }) {
  return shell.sheetOpen ? <AccountSheet key={shell.round} onOffline={shell.continueOffline} onSignedIn={shell.onSignedIn} /> : null
}
