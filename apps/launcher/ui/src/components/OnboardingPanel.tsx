import '../styles/onboarding-room.css'
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import {
  host,
  type DependencyItem,
  type DrivesState,
  type GpuProfile,
  type JunkItem,
  type LauncherSettings,
  type PcHealth,
  type ReplacedApp,
  type StoreStatus,
  type Tuning,
} from '../lib/host'
import { loadSettingsOrFail, saveSettingsPatch } from '../lib/settingsSave'
import { useHdrState } from '../lib/hdr'
import { stayFor } from '../lib/notify'
import { Button } from './ui/Button'
import { SegmentedProgress } from './onboarding/SegmentedProgress'
import { RestoreStep } from './onboarding/RestoreStep'
import { LeavingStep, useLeavingStep } from './onboarding/useLeavingStep'
import { Spinner } from './ui/Spinner'
import { ClientsStep, useClientsStatus, useInstallerLong } from './clients/ClientsCard'
import { clientsOffered, footerHidden, footerNext } from '../lib/clients'
import { CleanupStep } from './onboarding/CleanupStep'
import { report } from '../lib/report'
import { markShellReady } from '../lib/shellReady'
import { focusWhenActive } from '../lib/focusTrap'
import { settingsStoreRows } from '../lib/stores'
import { AppsStep } from './onboarding/AppsStep'
import { AccountStep, type AccountScreen } from './onboarding/AccountStep'
import {
  STEPS,
  isPartialGpuProfile,
  junkScanDue,
  onboardingProbeReportLevel,
  shouldRetryOnboardingProbe,
  waitsForJunkScan,
  type Feedback,
  type MissingRuntime,
  type StepId,
  type StoreCheckState,
} from './onboarding/model'
import { StoresStep } from './onboarding/StoresStep'
import { DiscordStep } from './onboarding/DiscordStep'
import { PcStep } from './onboarding/PcStep'
import { continueWithoutAccount, refreshAccount, useAccount } from './account/useAccount'
import { setupSkipsAccount } from '../lib/accountFlow'
import { bestSteps, runBest, type BestKey } from './gpu/BestSetup'
import { junkWorth } from './gpu/JunkRow'
import { errorText } from '../lib/errorWords'

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

function missingInstallableRuntimes(result: { items?: DependencyItem[] | null }): MissingRuntime[] {
  return (result.items ?? [])
    .filter((item) => item.status.toLowerCase() !== 'present' && item.canOfferInstall)
    .map((item) => ({ id: item.id, name: item.name }))
}

function cleanupHas(apps: ReplacedApp[] | undefined, junk: JunkItem[]) {
  return !!apps?.some(app => app.installed || app.leftovers > 0) || junkWorth(junk)
}


function backShown(index: number, id: StepId, accountScreen: AccountScreen): boolean {
  return index > 0 && (id !== 'account' || accountScreen === 'choose')
}

function visibleSteps(offers: Record<'restore' | 'clients' | 'cleanup', boolean>, current: StepId) {
  return STEPS.filter(item => item.id === current || !(item.id in offers) || offers[item.id as keyof typeof offers])
}

export interface OnboardingPanelProps {
  stores: StoreStatus[]
  onSettings: (next: LauncherSettings) => void
  onStores: (next: StoreStatus[]) => void
  onComplete: () => Promise<void>
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
  const [restoreOffer, setRestoreOffer] = useState<{ day: string; device: string; files: number } | null>(null)
  const [restoring, setRestoring] = useState(false)
  const [restoreHeld, setRestoreHeld] = useState(false)
  useEffect(markShellReady, [])
  useEffect(() => {
    host.backupStatus().then(status => setRestoreOffer(status.offer), (error: unknown) => report(error, 'OnboardingPanel.backupStatus', { level: 'warn' }))
  }, [])
  const stageRef = useRef<HTMLElement>(null)
  const leavingStep = useLeavingStep(stageRef)
  const [direction, setDirection] = useState(1)
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  const [storeErrors, setStoreErrors] = useState<Record<string, string | undefined>>({})
  const [storeCheckState, setStoreCheckState] = useState<StoreCheckState>('checking')
  const [storeCheckAt, setStoreCheckAt] = useState(0)
  const [storeBusy, setStoreBusy] = useState<Record<string, boolean | undefined>>({})
  const [folderBusy, setFolderBusy] = useState(false)
  const [completing, setCompleting] = useState(false)
  const [missingRuntimes, setMissingRuntimes] = useState<MissingRuntime[]>([])
  const [runtimeBusy, setRuntimeBusy] = useState(false)
  const { status: clients, setStatus: setClients, failed: clientsFailed, failures: clientsFailures, checking: clientsChecking, recheck: recheckClients } = useClientsStatus()
  const [clientsLater, setClientsLater] = useState(false)
  const installerLong = useInstallerLong(clients)
  const [accountScreen, setAccountScreen] = useState<AccountScreen>('choose')
  const { overview: account } = useAccount()
  const runtimeBusyRef = useRef(false)
  const completingRef = useRef(false)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const focusPending = useRef(false)

  const [gpu, setGpu] = useState<GpuProfile | null>(null)
  const [health, setHealth] = useState<PcHealth | null>(null)
  const [drives, setDrives] = useState<DrivesState | null>(null)
  const [tuning, setTuning] = useState<Tuning | null>(null)
  const [pcRead, setPcRead] = useState(false)
  const [settings, setLocalSettings] = useState<LauncherSettings | null>(null)
  const screen = useHdrState(false)
  const [running, setRunning] = useState<BestKey | null>(null)
  const [bestBusy, setBestBusy] = useState(false)
  const [bestRan, setBestRan] = useState(false)
  const [landed, setLanded] = useState<ReadonlySet<BestKey>>(() => new Set())
  const [bestFailed, setBestFailed] = useState<Partial<Record<BestKey, string>>>({})
  const [replaced, setReplaced] = useState<ReplacedApp[] | null>(null)
  const [junk, setJunk] = useState<JunkItem[]>([])
  const [junkScanning, setJunkScanning] = useState(false)
  const junkStarted = useRef(false)
  const cleanupOffered = cleanupHas(replaced ?? undefined, junk)
  const best = pcRead ? bestSteps(gpu, health, screen, settings, saveSettings, drives, tuning) : null
  const bestLeft = !!best?.some(item => !item.done)
  const steps = visibleSteps({ restore: restoreOffer !== null, clients: clientsOffered(clients, clientsFailed), cleanup: cleanupOffered }, step)
  const index = Math.max(0, steps.findIndex(item => item.id === step))
  const current = steps[index]
  const next = footerNext(current.id, clients, clientsLater, clientsFailed, restoring, installerLong)
  const last = index === steps.length - 1
  const storeRows = settingsStoreRows(stores)

  useEffect(() => {
    host.replacedScan(true).then(({ apps }) => setReplaced(apps), (error: unknown) => report(error, 'OnboardingPanel.replacedScan'))
  }, [])

  useEffect(() => {
    if (!junkScanDue(step, junkStarted.current)) return
    junkStarted.current = true
    setJunkScanning(true)
    void host.junkScan()
      .then(({ items }) => setJunk(items), (error: unknown) => report(error, 'OnboardingPanel.junkScan', { level: 'warn' }))
      .finally(() => setJunkScanning(false))
  }, [step])

  const mountedRef = useRef(true)
  useEffect(() => {
    mountedRef.current = true
    return () => { mountedRef.current = false }
  }, [])

  const readPc = useCallback(async () => {
    const retryLater = <T,>(call: () => Promise<T>, context: string, apply: (value: T) => void, delayMs: number) => {
      window.setTimeout(() => {
        if (!mountedRef.current) return
        void call().then(
          value => { if (mountedRef.current) apply(value) },
          (error: unknown) => report(error, context, { level: onboardingProbeReportLevel(error) }),
        )
      }, delayMs)
    }
    const probe = <T,>(
      call: () => Promise<T>,
      context: string,
      apply: (value: T) => void,
      shouldRetryValue?: (value: T) => boolean,
    ) => call().then(
      value => {
        if (!mountedRef.current) return
        apply(value)
        if (shouldRetryValue?.(value)) retryLater(call, context, apply, 2_000)
      },
      (error: unknown) => {
        if (!mountedRef.current) return
        if (shouldRetryOnboardingProbe(error, 0)) {
          report(error, context, { level: 'warn' })
          retryLater(call, context, apply, 1_500)
          return
        }
        report(error, context, { level: onboardingProbeReportLevel(error) })
      },
    )
    await Promise.all([
      probe(() => host.gpuProfile(), 'OnboardingPanel.gpuProfile', setGpu, isPartialGpuProfile),
      probe(() => host.systemHealth(), 'OnboardingPanel.systemHealth', setHealth),
      host.systemDrives().then(setDrives, (error: unknown) => report(error, 'OnboardingPanel.systemDrives', { level: 'warn' })),
      host.systemTuning().then(setTuning, (error: unknown) => report(error, 'OnboardingPanel.systemTuning', { level: 'warn' })),
    ])
    if (mountedRef.current) setPcRead(true)
  }, [])

  useEffect(() => {
    void readPc()
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

  useEffect(() => {
    if (!feedback || feedback.error) return
    const timer = window.setTimeout(() => setFeedback(null), stayFor({ text: feedback.message }))
    return () => window.clearTimeout(timer)
  }, [feedback])

  function accountFinished() {
    void host.exoAccountMarkAsked().catch((error: unknown) => report(error, 'OnboardingPanel.markAsked', { level: 'warn' }))
    go(steps[index + 1].id)
  }

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
        fail(errorText(result.message, `Couldn’t sign in to ${store.displayName}. Try again.`))
        return
      }
      try {
        onStores(await host.storesMatrix())
      } catch (e) {
        report(e, 'OnboardingPanel.storesMatrix')
      }
      if (result.message) setFeedback({ message: result.message })
    } catch (cause) {
      fail(errorText(cause, `Couldn’t reach ${store.displayName}. Try again.`))
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
      setFeedback({ message: errorText(cause, 'The folder wasn’t added. Try again.'), error: true })
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

  async function saveSettings(patch: Partial<LauncherSettings>) {
    try {
      const next = await saveSettingsPatch(patch)
      setLocalSettings(next)
      return true
    } catch (error) {
      report(error, 'OnboardingPanel.saveSettings')
      return false
    }
  }

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
    if (Object.keys(failed).length === 0) await finish()
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
      setFeedback({ message: errorText(cause, 'Setup didn’t finish. Try again.'), error: true })
    }
  }

  const body: Record<StepId, () => ReactNode> = {
    welcome: () => null,
    restore: () => (restoreOffer ? <RestoreStep offer={restoreOffer} busy={restoring} onBusy={setRestoring} onRefused={() => setRestoreHeld(true)} /> : null),
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
    pc: () => <PcStep steps={best} running={running} landed={landed} failed={bestFailed} settings={settings} onSave={saveSettings} />,
    cleanup: () => <CleanupStep apps={replaced ?? []} onApps={setReplaced} junk={junk}
      onCleaned={() => void host.junkScan().then(({ items }) => setJunk(items), (error: unknown) => report(error, 'OnboardingPanel.junkScan', { level: 'warn' }))} />,
  }
  return (
    <div className="exo-app exo-ob">

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
            busy={completing || bestBusy || (junkScanning && waitsForJunkScan(step))}
            inline={current.id === 'account'}
            backShown={backShown(index, current.id, accountScreen)}
            offer={current.id === 'pc' && bestLeft && !bestRan}
            nextBlocked={next.blocked}
            nextLabel={next.label}
            quiet={next.quiet}
            working={completing ? 'Opening Exo' : bestBusy ? 'Setting up this PC' : null}
            onBest={() => void runBestSettings()}
            onBack={() => go(steps[index - 1].id)}
            onNext={() => {
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

function Footer({ feedback, index, last, busy, inline, backShown, offer, nextBlocked, nextLabel, quiet, working, onBest, onBack, onNext }: {
  feedback: Feedback | null
  index: number
  last: boolean
  busy: boolean
  inline: boolean
  backShown: boolean
  offer: boolean
  nextBlocked: boolean
  nextLabel?: string
  quiet: boolean
  working: string | null
  onBest: () => void
  onBack: () => void
  onNext: () => void
}) {
  const label = offer ? 'Optimize & open Exo' : nextLabel ?? (last ? 'Open Exo' : index === 0 ? 'Get started' : 'Continue')
  return (
    <footer className={`exo-ob-foot${index === 0 ? ' is-welcome' : ''}`}>
      <p className={`exo-ob-note${feedback?.error ? ' is-error' : ''}`} role={feedback?.error ? 'alert' : 'status'} aria-live="polite">
        {feedback?.message ?? ' '}
      </p>
      <div className="exo-ob-actions">
        {backShown ? <Button variant="ghost" isDisabled={busy} onPress={onBack}>Back</Button> : null}
        {offer ? <Button variant="ghost" isDisabled={busy} onPress={onNext}>Skip for now</Button> : null}
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
