import { useEffect, useRef, useState } from 'react'
import { Check } from '../../brand/icons'
import { StepTile } from '../onboarding/StepTile'
import { host, type ClientsStatus } from '../../lib/host'
import { clientsFixCopy, clientsHeading, offersLater } from '../../lib/clients'
import { report } from '../../lib/report'
import { errorText } from '../../lib/errorWords'
import { markShellReady } from '../../lib/shellReady'
import { Button } from '../ui/Button'
import { Spinner } from '../ui/Spinner'
import { WindowChrome } from '../WindowChrome'

/**
 * The Game clients status, read once when enabled (setup on first run; the start card once onboarding is done). Null until
 * answered; `failed` when the check could not be made, so the start card never holds the app on a check that cannot answer.
 * `recheck` reads it again (Try again after a failed read).
 */
export type ClientsCheck = {
  status: ClientsStatus | null
  setStatus: (next: ClientsStatus) => void
  /** The last read could not be made. */
  failed: boolean
  /** How many reads in a row have failed (a repeat failure says so). */
  failures: number
  /** A read is running now (Try again shows busy until it answers). */
  checking: boolean
  recheck: () => void
}

/** How long Epic's installer may run before the card and the setup footer offer Continue for now (a hidden window can't hold setup). */
export const INSTALLER_WAIT_MS = 30_000

/** True once an installer that is running has run for INSTALLER_WAIT_MS; false again when it closes. */
export function useInstallerLong(status: ClientsStatus | null): boolean {
  const installing = !!status?.epic.installing
  const [long, setLong] = useState(false)
  useEffect(() => {
    if (!installing) return
    const timer = window.setTimeout(() => setLong(true), INSTALLER_WAIT_MS)
    return () => {
      window.clearTimeout(timer)
      setLong(false)
    }
  }, [installing])
  return installing && long
}

export function useClientsStatus(enabled = true): ClientsCheck {
  const [status, setStatus] = useState<ClientsStatus | null>(null)
  const [failures, setFailures] = useState(0)
  const [checking, setChecking] = useState(false)
  // `again`: the person pressed Try again, so a failure is a repeat. The first read (on mount, twice under StrictMode) is not.
  const read = (again: boolean) => {
    setChecking(true)
    host.clientsStatus().then((next) => {
      setStatus(next)
      setFailures(0)
    }, (cause: unknown) => {
      report(cause, 'Clients.status', { level: 'warn' })
      setFailures(count => again ? count + 1 : Math.max(count, 1))
    }).finally(() => setChecking(false))
  }
  useEffect(() => {
    if (enabled) read(false)
  }, [enabled])
  return { status, setStatus, failed: failures > 0, failures, checking, recheck: () => read(true) }
}

/**
 * The clients step's body: the card once the check has answered; a check that failed shows its reason with Try again. The setup
 * footer's Continue for now defers it, so a read that cannot answer never traps setup.
 */
export function ClientsStep({ status, failed = false, failures = 1, checking = false, onStatus, onLater, onRecheck }: {
  status: ClientsStatus | null; failed?: boolean; failures?: number; checking?: boolean
  onStatus: (next: ClientsStatus) => void; onLater: () => void; onRecheck: () => void
}) {
  if (status) return <ClientsCard status={status} onStatus={onStatus} onLater={onLater} />
  if (!failed) return <Spinner label="Checking your game clients" />
  return (
    <div className="flex flex-col gap-4">
      <p className="text-[length:var(--exo-type-body)] leading-relaxed text-bad" role="alert">
        {failures > 1 ? 'Exo still could not check your game clients.' : 'Exo could not check your game clients.'} Check that Steam and Epic&apos;s files are readable, then try again.
      </p>
      <div className="flex items-center justify-end gap-3">
        {checking ? <Spinner label="Checking your game clients" /> : null}
        <Button variant="primary" isDisabled={checking} onPress={onRecheck}>{checking ? 'Checking…' : 'Try again'}</Button>
      </div>
    </div>
  )
}

/**
 * The Game clients card's content, shared by the onboarding step and the card on every start. One Fix button: Steam moves to
 * its Beta client (Steam restarts), and Epic's installer opens for an old launcher. While that installer runs the card checks
 * again every few seconds; the last check, once it has closed, is the new layout (or the old one left in place, as a warning).
 * The card takes first focus on Fix (or Try again) and traps nothing. A failed Fix says why and offers Try again; a Steam
 * branch that can't be read says why and offers Try again as a fresh check. After a failed Fix, Continue for now sits beside Try again;
 * a check that cannot answer is deferred by the setup footer's button instead (footerNext), so there is one such control per step.
 */
export function ClientsCard({ status, onStatus, onLater }: { status: ClientsStatus; onStatus: (next: ClientsStatus) => void; onLater?: () => void }) {
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<{ text: string; failed: boolean } | null>(null)
  const ready = !status.needsFix
  const installing = status.epic.installing
  const longWait = useInstallerLong(status)
  const unreadable = status.problem
  const fixRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!installing) return
    const timer = window.setInterval(() => {
      host.clientsStatus().then(onStatus, () => { })
    }, 3000)
    return () => window.clearInterval(timer)
  }, [installing, onStatus])

  // Initial focus: the Fix (or Try again) button, when the card is due and first shows.
  useEffect(() => {
    if (!ready) fixRef.current?.focus()
    // Only on first show: a later status change keeps the focus where the user put it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function fix() {
    if (busy) return
    setBusy(true)
    setNote(null)
    try {
      const result = await host.clientsFix()
      setNote(result.message ? { text: result.ok ? result.message : errorText(result.message, 'Exo could not update your clients. Try again.'), failed: !result.ok } : null)
      onStatus(await host.clientsStatus())
    } catch (cause) {
      report(cause, 'ClientsCard.fix')
      setNote({ text: errorText(cause, 'Exo could not update your clients. Try again.'), failed: true })
    } finally {
      setBusy(false)
    }
  }

  async function recheck() {
    if (busy) return
    setBusy(true)
    setNote(null)
    try {
      onStatus(await host.clientsStatus())
    } catch (cause) {
      report(cause, 'ClientsCard.recheck', { level: 'warn' })
      setNote({ text: 'Exo could not check your game clients. Try again.', failed: true })
    } finally {
      setBusy(false)
    }
  }

  const retry = !!unreadable || !!note?.failed

  return (
    <div className="flex flex-col gap-4">
      {unreadable ? null : (
        <p className="text-[length:var(--exo-type-body)] leading-relaxed text-fg">
          {ready ? 'Your game clients are current.' : clientsFixCopy(status)}
        </p>
      )}
      <ClientTiles status={status} />
      {status.epic.oldStillThere ? (
        <p className="text-[length:var(--exo-type-small)] text-fg" role="status">
          The old Epic launcher is still installed. Epic&apos;s installer should have removed it; remove it in Windows Settings if you no longer use it.
        </p>
      ) : null}
      {unreadable ? (
        <p className="text-[length:var(--exo-type-small)] text-bad" role="alert">{unreadable}</p>
      ) : note ? (
        <p className={`text-[length:var(--exo-type-small)] ${note.failed ? 'text-bad' : 'text-fg'}`} role={note.failed ? 'alert' : 'status'}>{note.text}</p>
      ) : null}
      {installing ? (
        <p className="text-[length:var(--exo-type-small)] text-fg" role="status">
          <Spinner label="Waiting for Epic's installer" /> Finish Epic&apos;s installer. Exo checks again when it closes.
        </p>
      ) : null}
      {ready && !unreadable ? null : (
        <div className="flex items-center justify-end gap-3">
          {busy ? <Spinner label={retry ? 'Checking your game clients' : 'Fixing your clients'} /> : null}
          {offersLater(!!note?.failed, longWait, onLater) ? <Button variant="ghost" isDisabled={busy} onPress={onLater}>Continue for now</Button> : null}
          <Button
            ref={fixRef}
            variant="primary"
            isDisabled={busy || installing}
            onPress={() => void (unreadable ? recheck() : fix())}
          >
            {retry ? 'Try again' : 'Fix'}
          </Button>
        </div>
      )}
    </div>
  )
}

/**
 * The start card, after onboarding, when the Game clients need a Fix: a page on Void like onboarding, with the library
 * not mounted behind it. It leaves as soon as the check passes, or when Continue for now defers it to the next start.
 */
export function ClientsStart({ status, onStatus, onLater }: { status: ClientsStatus; onStatus: (next: ClientsStatus) => void; onLater: () => void }) {
  // The start card stands where the Library would: a usable start (an update's startup check).
  useEffect(markShellReady, [])
  return (
    <div className="exo-app exo-ob">
      <header className="exo-titlebar exo-ob-titlebar">
        <div className="exo-titlebar-actions">
          <WindowChrome />
        </div>
      </header>
      <main className="exo-ob-stage">
        <section className="exo-ob-step" aria-labelledby="clients-title">
          <h1 id="clients-title" className="exo-ob-title">{clientsHeading(status)}</h1>
          <div className="exo-ob-body">
            <ClientsCard status={status} onStatus={onStatus} onLater={onLater} />
          </div>
        </section>
      </main>
    </div>
  )
}

/** The two client status tiles: a check on a current layout (Epic's new launcher, Steam Beta), none on an old or stable one. */
function ClientTiles({ status }: { status: ClientsStatus }) {
  const epic = status.epic.layout === 'new' ? 'Epic Games Launcher' : status.epic.layout === 'old' ? 'Epic Games Launcher' : null
  const steam = status.steam.channel === 'beta' ? 'Beta client' : status.steam.channel === 'stable' ? 'Stable client' : null
  if (!epic && !steam) return null
  return (
    <div className="exo-step-tiles">
      {epic ? <StepTile icon={status.epic.layout === 'new' ? <Check size={18} /> : null} title={epic} detail={status.epic.layout === 'old' ? 'Old layout' : status.epic.version ?? null} /> : null}
      {steam ? <StepTile icon={status.steam.channel === 'beta' ? <Check size={18} /> : null} title="Steam" detail={steam} /> : null}
    </div>
  )
}
