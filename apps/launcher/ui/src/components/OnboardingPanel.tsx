import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import {
  host,
  type DependencyItem,
  type DrivesState,
  type GpuProfile,
  type JunkItem,
  type LauncherSettings,
  type LucidState,
  type PcHealth,
  type ReplacedApp,
  type StoreStatus,
} from '../lib/host'
import { loadSettingsOrFail, saveSettingsPatch } from '../lib/settingsSave'
import { useCapture } from '../lib/capture'
import { useHdrState } from '../lib/hdr'
import { stayFor } from '../lib/notify'
import { Button } from './ui/Button'
import { SegmentedProgress } from './onboarding/SegmentedProgress'
import { DoneStep } from './onboarding/DoneStep'
import { RestoreStep } from './onboarding/RestoreStep'
import { LeavingStep, useLeavingStep } from './onboarding/useLeavingStep'
import { Spinner } from './ui/Spinner'
import { CaptureStep } from './onboarding/CaptureStep'
import { ClientsStep, useClientsStatus, useInstallerLong } from './clients/ClientsCard'
import { clientsOffered, footerHidden, footerNext } from '../lib/clients'
import { CleanupStep } from './onboarding/CleanupStep'
import { report } from '../lib/report'
import { markShellReady } from '../lib/shellReady'
import { focusWhenActive } from '../lib/focusTrap'
import { settingsStoreRows } from '../lib/stores'
import { AppsStep } from './onboarding/AppsStep'
import { AccountStep, type AccountScreen } from './onboarding/AccountStep'
import { STEPS, junkScanDue, waitsForJunkScan, type Feedback, type MissingRuntime, type StepId, type StoreCheckState } from './onboarding/model'
import { StoresStep } from './onboarding/StoresStep'
import { DiscordStep } from './onboarding/DiscordStep'
import { PcStep } from './onboarding/PcStep'
import { continueWithoutAccount, refreshAccount, useAccount } from './account/useAccount'
import { setupSkipsAccount } from '../lib/accountFlow'
import { bestSteps, runBest, type BestKey } from './gpu/BestSetup'
import { junkWorth } from './gpu/JunkRow'

let onboardingStoreCheck: Promise<StoreStatus[]> | null = null

function checkOnboardingStoresOnce(): Promise<StoreStatus[]> {
  onboardingStoreCheck ??= host.storesCheck().then((result) => {
    if (result.state === 'failed') throw new Error('Local store check failed.')
    return host.storesMatrix()
  }).catch((error: unknown) => {
    onboardingStoreCheck = null
    throw error
  })
  return onboardingStoreCheck
}

/** Runtimes the host reports as absent and is able to offer for install. */
function missingInstallableRuntimes(result: { items?: DependencyItem[] | null }): MissingRuntime[] {
  return (result.items ?? [])
    .filter((item) => item.status.toLowerCase() !== 'present' && item.canOfferInstall)
    .map((item) => ({ id: item.id, name: item.name }))
}

/** Clean up has something: an app Exo does the job of (or what a removed one left), or junk past a gigabyte. */
function cleanupHas(apps: ReplacedApp[] | undefined, junk: JunkItem[]) {
  return !!apps?.some(app => app.installed || app.leftovers > 0) || junkWorth(junk)
}


/** Back shows on every step after Welcome; on the account step only while its own screens are at the choice. */
function backShown(index: number, id: StepId, accountScreen: AccountScreen): boolean {
  return index > 0 && (id !== 'account' || accountScreen === 'choose')
}

/** Steps with nothing to offer on this PC are left out; the one on screen stays, so it never drops from under the user. */
function visibleSteps(offers: Record<'restore' | 'clients' | 'pc' | 'capture' | 'cleanup', boolean>, current: StepId) {
  return STEPS.filter(item => item.id === current || !(item.id in offers) || offers[item.id as keyof typeof offers])
}

export interface OnboardingPanelProps {
  stores: StoreStatus[]
  onSettings: (next: LauncherSettings) => void
  onStores: (next: StoreStatus[]) => void
  onComplete: () => Promise<void>
  /** Return false when the picker was canceled. Void and true mean the library changed. */
  onAddFolder?: () => boolean | void | Promise<boolean | void>
}

export function OnboardingPanel({
  stores,
  onSettings,
  onStores,
  onComplete,
  onAddFolder,
}: OnboardingPanelProps) {
  const [step, setStep] = useState<StepId>('welcome')
  // The daily backup the restore step offers (null: none, or not read yet; the step is left out then).
  const [restoreOffer, setRestoreOffer] = useState<{ day: string; device: string; files: number } | null>(null)
  // The restore is running, or done and restarting: Start fresh waits on it.
  const [restoring, setRestoring] = useState(false)
  // The restore is in and Exo could not restart: the step holds with its message, and the footer is not shown (no Start fresh, no Continue).
  const [restoreHeld, setRestoreHeld] = useState(false)
  // First-run setup is up: a usable start (an update's startup check).
  useEffect(markShellReady, [])
  useEffect(() => {
    host.backupStatus().then(status => setRestoreOffer(status.offer), (error: unknown) => report(error, 'OnboardingPanel.backupStatus', { level: 'warn' }))
  }, [])
  // The glow on the welcome logo breathes only while the window has focus (SPEC-v2: idle CPU stays flat).
  const [focused, setFocused] = useState(() => document.hasFocus())
  const stageRef = useRef<HTMLElement>(null)
  const leavingStep = useLeavingStep(stageRef)
  useEffect(() => {
    const on = () => setFocused(true)
    const off = () => setFocused(false)
    window.addEventListener('focus', on)
    window.addEventListener('blur', off)
    return () => { window.removeEventListener('focus', on); window.removeEventListener('blur', off) }
  }, [])
  /** 1 going forward, -1 back: which side the next step slides in from. */
  const [direction, setDirection] = useState(1)
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  /** A failed sign in or open, under its store's row. */
  const [storeErrors, setStoreErrors] = useState<Record<string, string | undefined>>({})
  const [storeCheckState, setStoreCheckState] = useState<StoreCheckState>('checking')
  /** Bumped by Try again after a failed check. */
  const [storeCheckAt, setStoreCheckAt] = useState(0)
  const [storeBusy, setStoreBusy] = useState<Record<string, boolean | undefined>>({})
  const [folderBusy, setFolderBusy] = useState(false)
  const [completing, setCompleting] = useState(false)
  const [missingRuntimes, setMissingRuntimes] = useState<MissingRuntime[]>([])
  const [runtimeBusy, setRuntimeBusy] = useState(false)
  // The Game clients card: Epic's launcher layout and Steam's branch, read as setup opens.
  const { status: clients, setStatus: setClients, failed: clientsFailed, failures: clientsFailures, checking: clientsChecking, recheck: recheckClients } = useClientsStatus()
  // Continue for now, after a failed Fix: setup may go on, and the card returns on the next start.
  const [clientsLater, setClientsLater] = useState(false)
  // Epic's installer running 30 s: setup offers Continue for now rather than waiting on it.
  const installerLong = useInstallerLong(clients)
  // The account step owns its own screens and its own primary button (the footer stays out of it).
  const [accountScreen, setAccountScreen] = useState<AccountScreen>('choose')
  const { overview: account } = useAccount()
  const runtimeBusyRef = useRef(false)
  const completingRef = useRef(false)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const focusPending = useRef(false)

  // Best settings for this PC: what the PC room's Best settings lists (gpu/BestSetup.tsx), read as setup opens so it is
  // there by the last step. Null until the card's profile and the PC's setup have answered.
  const [gpu, setGpu] = useState<GpuProfile | null>(null)
  const [health, setHealth] = useState<PcHealth | null>(null)
  const [drives, setDrives] = useState<DrivesState | null>(null)
  const [pcRead, setPcRead] = useState(false)
  const [settings, setLocalSettings] = useState<LauncherSettings | null>(null)
  const screen = useHdrState(false)
  const [lucid, setLucid] = useState<LucidState | null>(null)
  /** The step at work, whether Use best settings has run, the steps it set (their checks draw in) and why any failed. */
  const [running, setRunning] = useState<BestKey | null>(null)
  const [bestBusy, setBestBusy] = useState(false)
  const [bestRan, setBestRan] = useState(false)
  const [landed, setLanded] = useState<ReadonlySet<BestKey>>(() => new Set())
  const [bestFailed, setBestFailed] = useState<Partial<Record<BestKey, string>>>({})
  const capture = useCapture()
  // The apps Exo replaces; opening setup first clears what removed ones left in this user's folders.
  const [replaced, setReplaced] = useState<ReplacedApp[] | null>(null)
  // Junk past a gigabyte (old shader caches, mostly) is Clean up's too, with its one Clean.
  const [junk, setJunk] = useState<JunkItem[]>([])
  const [junkScanning, setJunkScanning] = useState(false)
  const junkStarted = useRef(false)
  const cleanupOffered = cleanupHas(replaced ?? undefined, junk)
  const best = pcRead ? bestSteps(gpu, health, screen, settings, saveSettings, drives) : null
  const bestLeft = !!best?.some(item => !item.done)
  const steps = visibleSteps({ restore: restoreOffer !== null, clients: clientsOffered(clients, clientsFailed), pc: best === null || best.length > 0 || !!lucid, capture: capture.state?.present !== false, cleanup: cleanupOffered }, step)
  const index = Math.max(0, steps.findIndex(item => item.id === step))
  const current = steps[index]
  const next = footerNext(current.id, clients, clientsLater, clientsFailed, restoring, installerLong)
  const last = index === steps.length - 1
  const storeRows = settingsStoreRows(stores)

  useEffect(() => {
    host.replacedScan(true).then(({ apps }) => setReplaced(apps), (error: unknown) => report(error, 'OnboardingPanel.replacedScan'))
  }, [])

  // The junk scan waits until setup reaches the steps that can lead into Clean up (see junkScanDue), then runs once.
  useEffect(() => {
    if (!junkScanDue(step, junkStarted.current)) return
    junkStarted.current = true
    setJunkScanning(true)
    void host.junkScan()
      .then(({ items }) => setJunk(items), (error: unknown) => report(error, 'OnboardingPanel.junkScan', { level: 'warn' }))
      .finally(() => setJunkScanning(false))
  }, [step])

  const readPc = useCallback(async () => {
    await Promise.all([
      host.gpuProfile().then(setGpu, (error: unknown) => report(error, 'OnboardingPanel.gpuProfile')),
      host.systemHealth().then(setHealth, (error: unknown) => report(error, 'OnboardingPanel.systemHealth')),
      host.systemDrives().then(setDrives, (error: unknown) => report(error, 'OnboardingPanel.systemDrives', { level: 'warn' })),
    ])
    setPcRead(true)
  }, [])

  useEffect(() => {
    void readPc()
    host.lucid().then(setLucid, (error: unknown) => report(error, 'OnboardingPanel.lucid'))
  }, [readPc])

  useEffect(() => {
    void host.listDeps().then((result) => {
      setMissingRuntimes(missingInstallableRuntimes(result))
    }).catch((e: unknown) => report(e, 'OnboardingPanel.listDeps'))
  }, [])

  useEffect(() => {
    let active = true
    void checkOnboardingStoresOnce().then(
      (next) => {
        if (!active) return
        onStores(next)
        setStoreCheckState('ready')
      },
      () => {
        if (active) setStoreCheckState('error')
      },
    )
    return () => {
      active = false
    }
  }, [onStores, storeCheckAt])

  useEffect(() => {
    let active = true
    void loadSettingsOrFail().then(
      (next) => { if (!active) return; setLocalSettings(next); onSettings(next) },
      (error: unknown) => report(error, 'OnboardingPanel.getSettings'),
    )
    return () => {
      active = false
    }
  }, [onSettings])

  useEffect(() => {
    if (!focusPending.current) return
    focusPending.current = false
    const frame = window.requestAnimationFrame(() => focusWhenActive(headingRef.current))
    return () => window.cancelAnimationFrame(frame)
  }, [step])

  // A confirmation passes as a notice does; an error stays until the next action.
  useEffect(() => {
    if (!feedback || feedback.error) return
    const timer = window.setTimeout(() => setFeedback(null), stayFor({ text: feedback.message }))
    return () => window.clearTimeout(timer)
  }, [feedback])

  /** The account step is over (signed in, or continued offline): an upgrader is not asked again, and setup goes on. */
  function accountFinished() {
    void host.exoAccountMarkAsked().catch((error: unknown) => report(error, 'OnboardingPanel.markAsked', { level: 'warn' }))
    go(steps[index + 1].id)
  }

  // Setup resumed on a PC that is already signed in, with no code left to confirm: the account step is not asked again.
  useEffect(() => {
    if (step === 'account' && setupSkipsAccount(account, accountScreen)) accountFinished()
  })

  function go(next: StepId) {
    if (next === step) return
    leavingStep.leave()
    setFeedback(null)
    focusPending.current = true
    setDirection(steps.findIndex(item => item.id === next) > index ? 1 : -1)
    setStep(next)
  }

  async function runStoreAction(store: StoreStatus) {
    if (storeBusy[store.store]) return
    setStoreBusy((busy) => ({ ...busy, [store.store]: true }))
    setFeedback(null)
    const fail = (message: string) => setStoreErrors(errors => ({ ...errors, [store.store]: message }))
    setStoreErrors(errors => ({ ...errors, [store.store]: undefined }))
    try {
      const result = await host.storesAuth(store.store)
      if (!result.ok) {
        fail(result.message ?? `Couldn’t sign in to ${store.displayName}. Try again.`)
        return
      }
      try {
        onStores(await host.storesMatrix())
      } catch (e) {
        report(e, 'OnboardingPanel.storesMatrix')
      }
      if (result.message) setFeedback({ message: result.message })
    } catch (cause) {
      fail(cause instanceof Error ? cause.message : `Couldn’t reach ${store.displayName}. Try again.`)
    } finally {
      setStoreBusy((busy) => {
        const next = { ...busy }
        delete next[store.store]
        return next
      })
    }
  }

  async function addFolder() {
    if (!onAddFolder || folderBusy) return
    setFolderBusy(true)
    setFeedback(null)
    try {
      const result = await onAddFolder()
      if (result !== false) {
        setFeedback({ message: 'Game folder added.' })
      }
    } catch (cause) {
      setFeedback({ message: cause instanceof Error ? cause.message : 'The folder wasn’t added. Try again.', error: true })
    } finally {
      setFolderBusy(false)
    }
  }

  async function installMissingRuntimes() {
    if (runtimeBusyRef.current) return
    runtimeBusyRef.current = true
    setRuntimeBusy(true)
    setFeedback(null)
    try {
      // All of them in one install: Windows asks once.
      const result = await host.runtimesInstall('missing', missingRuntimes.map((item) => item.id))
      const failures = result.results.filter(item => item.outcome === 'failed').map(item => `${item.name}: ${item.message}`)
      if (!result.ok && failures.length === 0 && result.message && !result.canceled) failures.push(result.message)
      const restart = result.results.some(item => item.outcome === 'restart')
      let stillMissing = missingRuntimes.length
      try {
        const next = missingInstallableRuntimes(await host.listDeps())
        stillMissing = next.length
        setMissingRuntimes(next)
      } catch (error) {
        report(error, 'OnboardingPanel.listDeps')
      }
      setFeedback(failures.length
        ? { message: failures.join(' '), error: true }
        : stillMissing > 0
          ? { message: 'Windows doesn’t list them all yet. Restart Windows, then check again.' }
          : { message: restart ? 'Runtimes installed. Restart Windows to finish.' : 'Runtimes installed.' })
    } finally {
      runtimeBusyRef.current = false
      setRuntimeBusy(false)
    }
  }

  /** HDR for HDR games, Best settings' one step that is a setting. */
  async function saveSettings(patch: Partial<LauncherSettings>) {
    const next = await saveSettingsPatch(patch)
    setLocalSettings(next)
    return true
  }

  /** Use best settings: each step left, in turn, its row turning its ring; then this PC is read again for the checks. */
  async function runBestSettings() {
    if (bestBusy || !best) return
    setBestBusy(true)
    setFeedback(null)
    const failed = await runBest(best, setRunning)
    setRunning(null)
    await readPc()
    setLanded(new Set(best.filter(item => !item.done && !failed[item.key]).map(item => item.key)))
    setBestFailed(failed)
    setBestRan(true)
    setBestBusy(false)
  }

  async function finish() {
    if (completingRef.current) return
    completingRef.current = true
    setCompleting(true)
    setFeedback(null)
    try {
      await onComplete()
    } catch (cause) {
      completingRef.current = false
      setCompleting(false)
      setFeedback({ message: cause instanceof Error ? cause.message : 'Setup didn’t finish. Try again.', error: true })
    }
  }

  // One renderer per step, looked up rather than chained.
  const body: Record<StepId, () => ReactNode> = {
    welcome: () => null,
    done: () => <DoneStep />,
    restore: () => (restoreOffer ? <RestoreStep offer={restoreOffer} busy={restoring} onBusy={setRestoring} onRefused={() => setRestoreHeld(true)} /> : null),
    // One account state with the shell's gate (useAccount): a sign-in here, or Continue without an account, is not asked again after setup.
    account: () => <AccountStep onScreen={state => setAccountScreen(state.screen)} onDone={() => { void refreshAccount(); accountFinished() }} onLater={() => { continueWithoutAccount(); accountFinished() }} />,
    clients: () => <ClientsStep status={clients} failed={clientsFailed} failures={clientsFailures} checking={clientsChecking} onStatus={setClients} onLater={() => setClientsLater(true)} onRecheck={recheckClients} />,
    stores: () => (
      <StoresStep
        storeCheckState={storeCheckState}
        onRetryStoreCheck={() => { setStoreCheckState('checking'); setStoreCheckAt(at => at + 1) }}
        storeRows={storeRows}
        storeBusy={storeBusy}
        storeErrors={storeErrors}
        onStoreAction={store => void runStoreAction(store)}
        missingRuntimes={missingRuntimes}
        runtimeBusy={runtimeBusy}
        onInstallRuntimes={() => void installMissingRuntimes()}
        canAddFolder={!!onAddFolder}
        folderBusy={folderBusy}
        onAddFolder={() => void addFolder()}
      />
    ),
    apps: () => <AppsStep onFeedback={setFeedback} />,
    discord: () => <DiscordStep />,
    capture: () => <CaptureStep />,
    pc: () => <PcStep steps={best} running={running} landed={landed} failed={bestFailed} lucid={lucid} onLucid={setLucid} />,
    cleanup: () => <CleanupStep apps={replaced ?? []} onApps={setReplaced} junk={junk}
      onCleaned={() => void host.junkScan().then(({ items }) => setJunk(items), (error: unknown) => report(error, 'OnboardingPanel.junkScan', { level: 'warn' }))} />,
  }
  return (
    <div className={`exo-app exo-ob${focused ? '' : ' is-unfocused'}`}>

      <main className="exo-ob-stage" ref={stageRef}>
        <LeavingStep html={leavingStep.leaving} />
        <SegmentedProgress count={steps.length - 1} done={Math.max(0, index - 1)} label={`Step ${index} of ${steps.length - 1}`} />
        <section className={`exo-ob-step${current.id === 'welcome' ? ' is-welcome' : ''}`} key={current.id} data-direction={direction < 0 ? 'back' : 'forward'} aria-labelledby="exo-ob-title">
          {current.id === 'welcome' ? (
            <>
              <span className="exo-ob-mark-wrap">
                <img className="exo-ob-mark" src="logo.svg" alt="" decoding="async" draggable={false} />
              </span>
              <h1 id="exo-ob-title" ref={headingRef} tabIndex={-1} className="exo-ob-title is-display">{current.title}</h1>
              <Button variant="primary" className="exo-ob-cta exo-ob-get-started" onPress={() => go(steps[1].id)}>Get started</Button>
            </>
          ) : (
            <>
              <p className="exo-ob-step-label">Step {index} of {steps.length - 1}</p>
              <h1 id="exo-ob-title" ref={headingRef} tabIndex={-1} className="exo-ob-title">{current.title}</h1>
              <div className="exo-ob-body">
                {body[current.id]()}
              </div>
            </>
          )}
        </section>

        {footerHidden(current.id, restoreHeld) ? null : (
          <Footer
            feedback={feedback}
            index={index}
            last={last}
            busy={completing || bestBusy || (junkScanning && waitsForJunkScan(step, capture.state?.present !== false))}
            inline={current.id === 'account'}
            backShown={backShown(index, current.id, accountScreen)}
            // Best settings asks first: Use best settings runs it, Not now goes on without it.
            offer={current.id === 'pc' && bestLeft && !bestRan}
            // The clients step is required: Continue waits until Fix has brought this PC up to them.
            nextBlocked={next.blocked}
            nextLabel={next.label}
            quiet={next.quiet}
            working={completing ? 'Opening Exo' : bestBusy ? 'Setting up this PC' : null}
            onBest={() => void runBestSettings()}
            onBack={() => go(steps[index - 1].id)}
            onNext={() => {
              // Continuing past the restore offer is "start fresh": this install is not offered the backup again.
              if (next.defers) setClientsLater(true)
              if (current.id === 'restore') host.backupDecline().catch((error: unknown) => report(error, 'OnboardingPanel.decline', { level: 'warn' }))
              if (last) void finish()
              else go(steps[index + 1].id)
            }}
          />
        )}
      </main>
    </div>
  )
}

/** The note on the left; Back (and Not now while Best settings is offered) and the one white button on the right. */
function Footer({ feedback, index, last, busy, inline, backShown, offer, nextBlocked, nextLabel, quiet, working, onBest, onBack, onNext }: {
  feedback: Feedback | null
  index: number
  last: boolean
  busy: boolean
  /** The step draws its own primary button (the account step): the footer keeps only Back. */
  inline: boolean
  backShown: boolean
  /** Best settings has steps left: the white button runs them and Not now goes on without them. */
  offer: boolean
  /** The white button waits (the clients step, until its check passes). */
  nextBlocked: boolean
  /** The white button's words when they are not the step's default (the restore offer: Start fresh). */
  nextLabel?: string
  /** The step's one primary is elsewhere on it (the restore offer's Restore, a check's Try again): the footer's button is quiet. */
  quiet: boolean
  /** What the white button is doing while it works. */
  working: string | null
  onBest: () => void
  onBack: () => void
  onNext: () => void
}) {
  const label = offer ? 'Use best settings' : nextLabel ?? (last ? 'Open library' : index === 0 ? 'Get started' : 'Continue')
  return (
    <footer className={`exo-ob-foot${index === 0 ? ' is-welcome' : ''}`}>
      <p className={`exo-ob-note${feedback?.error ? ' is-error' : ''}`} role={feedback?.error ? 'alert' : 'status'} aria-live="polite">
        {feedback?.message ?? ' '}
      </p>
      <div className="exo-ob-actions">
        {backShown ? <Button variant="ghost" isDisabled={busy} onPress={onBack}>Back</Button> : null}
        {offer ? <Button variant="ghost" isDisabled={busy} onPress={onNext}>Not now</Button> : null}
        {inline ? null : (
          <Button variant={quiet ? 'ghost' : 'primary'} className="exo-ob-cta" isDisabled={busy || nextBlocked} onPress={offer ? onBest : onNext}>
            {working ? <Spinner label={working} /> : null}
            {label}
          </Button>
        )}
      </div>
    </footer>
  )
}
