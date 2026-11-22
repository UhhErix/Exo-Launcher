import { useCallback, useEffect, useRef, useState, type ReactNode, type RefObject } from 'react'
import { host, type DlssRunResult, type DlssStatus, type Game, type GameProperties, type GamePropertyKey } from '../../lib/host'
import { onPageActivityChanged, pageIsVisible } from '../../lib/pageActivity'
import { invalidateUpscalerStatus } from '../../lib/upscalerCache'
import { gateReason, upscalerView } from '../../lib/upscalers'
import { cn, formatSize } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { Switch } from '../Switch'
import { ConfirmButton, ConfirmLabel, useConfirmInPlace } from '../ui/ConfirmButton'
import { errorText as plainErrorText } from '../../lib/errorWords'
import { ErrorNote } from '../ui/ErrorNote'
import { useChoiceMenu } from '../ui/Menu'
import { Progress, ProgressWords } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'
import { Wait, sample, times } from '../ui/Wait'
import { ArtworkTools, useArtwork, type Artwork } from './Artwork'
import { CompressRow } from './CompressRow'
import { UninstallButton } from './ManageStrip'
import { sheetMemory } from './useGameDetails'

const propertiesRead = sheetMemory<GameProperties>(8)

const MOVE_READ_MS = 2000
const MOVE_READ_MAX_MS = 8000

const READ_FAILED = "Exo couldn’t read this game's properties. Try again."
const CHANGE_FAILED = 'Exo couldn’t make that change. Try again.'

type Change = { key: GamePropertyKey | 'verify' | 'restore' | 'consent'; value: string }

type Rows = {
  props: GameProperties
  locked: boolean
  busy: string | null
  run: (change: Change) => void | Promise<unknown>
  apply: (key: GamePropertyKey, value?: string, secret?: string) => Promise<boolean>
  line: (row: string) => ReactNode
  note: (row: string, text: string, bad?: boolean) => void
}

type SectionId = 'general' | 'files' | 'betas' | 'content' | 'artwork'

function errorText(error: unknown, fallback = CHANGE_FAILED): string {
  return plainErrorText(error, fallback)
}

function download(bytes: number | null | undefined): string {
  return bytes && bytes > 0 ? `Download ${formatSize(bytes)}?` : 'Download it?'
}

function movedShare(moving: { done: number; total: number }): number | null {
  return moving.total > 0 ? moving.done / moving.total : null
}

function rowOf(change: Change): string {
  return change.key === 'content' ? `content:${change.value.slice(0, change.value.lastIndexOf(':'))}` : change.key
}

function Working({ on, label }: { on: boolean; label: string }) {
  return on ? <Spinner label={label} /> : null
}

function sectionsFor(props: GameProperties): { id: SectionId; title: string }[] {
  return [
    ...(props.launchOptions !== null || props.launchOptionsNote || props.language !== null || props.cloudSync || props.storeProperties || props.throughSteam != null || props.steamOverlay != null || props.antiCheatLaunch
      ? [{ id: 'general' as const, title: 'General' }] : []),
    { id: 'files' as const, title: 'Installed files' },
    ...(props.beta !== null && props.betaChoices.length > 0 ? [{ id: 'betas' as const, title: 'Betas' }] : []),
    ...(props.content && props.content.length > 0 ? [{ id: 'content' as const, title: props.contentLabel }] : []),
    { id: 'artwork' as const, title: 'Artwork' },
  ]
}

function defaultSectionId(sections: { id: SectionId }[]): SectionId {
  if (sections.some(s => s.id === 'general')) return 'general'
  if (sections.some(s => s.id === 'files')) return 'files'
  return sections[0]?.id ?? 'files'
}

function useMovePolling(moving: boolean, load: () => Promise<number | null>) {
  useEffect(() => {
    if (!moving) return
    let timer = 0
    let reading = false
    let stopped = false
    let wait = MOVE_READ_MS
    let last: number | null = null
    const schedule = () => {
      if (stopped || reading || timer || !pageIsVisible()) return
      timer = window.setTimeout(read, wait)
    }
    const read = () => {
      timer = 0
      if (!pageIsVisible()) return
      reading = true
      void load().then((done) => {
        reading = false
        wait = done !== null && done !== last ? MOVE_READ_MS : Math.min(wait * 2, MOVE_READ_MAX_MS)
        last = done
        schedule()
      })
    }
    schedule()
    const off = onPageActivityChanged(() => {
      if (pageIsVisible()) { schedule(); return }
      window.clearTimeout(timer)
      timer = 0
    })
    return () => { stopped = true; window.clearTimeout(timer); off() }
  }, [moving, load])
}

function usePropertiesFocus(panelRef: RefObject<HTMLElement | null>, propertiesOpen: boolean, onPropertiesOpen: (open: boolean) => void) {
  useEffect(() => {
    if (!propertiesOpen) return
    const frame = window.requestAnimationFrame(() => {
      const panel = panelRef.current
      if (!panel) return
      const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches || document.documentElement.dataset.exoMotion === 'reduce'
      panel.scrollIntoView({ behavior: reduced ? 'auto' : 'smooth', block: 'nearest' })
      panel.focus({ preventScroll: true })
      onPropertiesOpen(false)
    })
    return () => window.cancelAnimationFrame(frame)
  }, [panelRef, propertiesOpen, onPropertiesOpen])
}

function SectionPanelContent({
  id,
  rows,
  game,
  upscalers,
  uninstall,
  artwork,
}: {
  id: SectionId
  rows: Rows
  game: Game
  upscalers: DlssStatus | null
  uninstall: ReactNode
  artwork: Artwork
}) {
  switch (id) {
    case 'general': return <GeneralSection {...rows} />
    case 'files': return <FilesSection {...rows} gameId={game.id} game={game} upscalers={upscalers} uninstall={uninstall} />
    case 'betas': return <BetasSection {...rows} />
    case 'content': return <ContentSection {...rows} />
    case 'artwork': return (
      <PropRow label="Cover" line={null}>
        <ArtworkTools artwork={artwork} disabled={false} className="exo-set-btn" />
      </PropRow>
    )
  }
}

export function GamePropertiesPanel({
  game,
  coverSource,
  locked,
  upscalers: upscalerStatus,
  onUpscalersChanged,
  onStatus,
  onArtwork,
  onUninstalled,
  onBusyChange,
  propertiesOpen,
  onPropertiesOpen,
}: {
  game: Game
  coverSource: string | null | undefined
  locked: boolean
  upscalers: DlssStatus | null
  onUpscalersChanged: () => void
  onStatus: (msg: string | null, sticky?: boolean) => void
  onArtwork: (game: Game) => void
  onUninstalled: () => void
  onBusyChange: (busy: boolean) => void
  propertiesOpen: boolean
  onPropertiesOpen: (open: boolean) => void
}) {
  const panelRef = useRef<HTMLElement>(null)
  const [props, setProps] = useState<GameProperties | null>(() => propertiesRead.get(game.id))
  const [tab, setTab] = useState<SectionId | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [notes, setNotes] = useState<Record<string, { text: string; bad?: boolean }>>({})
  const [uninstallBusy, setUninstallBusy] = useState(false)
  const artwork = useArtwork(game, coverSource, onArtwork, onStatus)

  useEffect(() => {
    onBusyChange(uninstallBusy || artwork.action !== null)
  }, [uninstallBusy, artwork.action, onBusyChange])

  useEffect(() => {
    setProps(propertiesRead.get(game.id))
    setTab(null)
    setLoadError(null)
    setBusy(null)
    setNotes({})
  }, [game.id])

  usePropertiesFocus(panelRef, propertiesOpen, onPropertiesOpen)

  const load = useCallback(() => host.properties(game.id).then((result) => {
    if (!result.ok) {
      setLoadError(errorText(result.message, READ_FAILED))
      return null
    }
    propertiesRead.set(game.id, result)
    setProps(result)
    return result.moving?.done ?? null
  }).catch((error: unknown) => {
    setLoadError(errorText(error, READ_FAILED))
    return null
  }), [game.id])

  useEffect(() => { void load() }, [load])
  useMovePolling(!!props?.moving, load)

  const note = (row: string, text: string, bad = false) => setNotes(current => ({ ...current, [row]: { text, bad } }))

  const apply = async (key: GamePropertyKey, value = '', secret?: string, row: string = key) => {
    setBusy(row)
    note(row, '')
    try {
      const result = await host.setProperty(game.id, key, value, secret)
      if (result.properties?.ok) {
        propertiesRead.set(game.id, result.properties)
        setProps(result.properties)
      }
      note(row, result.ok ? result.message ?? '' : errorText(result.message, CHANGE_FAILED), !result.ok)
      return result.ok
    } catch (error) {
      note(row, errorText(error), true)
      return false
    } finally {
      setBusy(null)
    }
  }

  const upscalers = (row: 'restore' | 'consent', change: () => Promise<DlssRunResult>, done: string) => {
    setBusy(row)
    note(row, '')
    void change()
      .then((result) => {
        invalidateUpscalerStatus(game.id)
        onUpscalersChanged()
        const failed = result.files?.find(file => file.state === 'failed')
        note(row, failed?.message ?? (result.ok ? done : result.message ?? CHANGE_FAILED), !result.ok || !!failed)
      })
      .catch((error: unknown) => note(row, errorText(error), true))
      .finally(() => setBusy(null))
  }

  const run = (asked: Change) => {
    const { value } = asked
    if (asked.key === 'restore') {
      upscalers('restore', () => host.dlssRestore(game.id), "The game's own files are back.")
      return
    }
    if (asked.key === 'consent') {
      const allow = value === 'allow'
      upscalers('consent', async () => {
        const answer = await host.dlssConsent(game.id, allow, upscalerView(upscalerStatus).antiCheatName ?? undefined)
        if (!answer.ok || value !== 'restore') return { ok: answer.ok, message: answer.message }
        return host.dlssRestore(game.id)
      }, allow ? "Exo may update this game's upscalers." : value === 'restore' ? "Stopped. The game's own files are back." : 'Stopped.')
      return
    }
    if (asked.key !== 'verify') {
      void apply(asked.key, asked.value, undefined, rowOf(asked))
      return
    }
    note('verify', '')
    return host.repair(game.id).then(result => {
      if (result.ok) note('verify', result.message ?? 'Files verified.')
      return result
    })
  }

  const line = (row: string): ReactNode => {
    const said = notes[row]
    if (!said?.text) return null
    return said.bad ? <ErrorNote className="exo-set-error" message={said.text} /> : <p className="exo-props-note" role="status">{said.text}</p>
  }

  const rows: Rows | null = props && {
    props,
    locked: props.running,
    busy,
    run,
    apply: (key, value, secret) => apply(key, value, secret),
    line,
    note,
  }
  const sections = props ? sectionsFor(props) : []
  const activeId = tab && sections.some(s => s.id === tab) ? tab : defaultSectionId(sections)
  const activeSection = sections.find(s => s.id === activeId) ?? null
  const readAgain = () => {
    setLoadError(null)
    void load()
  }

  const uninstall = (
    <UninstallButton
      selected={game}
      locked={locked}
      onStatus={onStatus}
      onUninstalled={onUninstalled}
      onBusyChange={setUninstallBusy}
    />
  )

  return (
    <section
      ref={panelRef}
      className="exo-glass is-panel exo-game-panel exo-game-props"
      aria-labelledby="exo-game-props-title"
      tabIndex={-1}
    >
      <header className="exo-game-panel-head">
        <h2 id="exo-game-props-title">Properties</h2>
        {props?.running ? <State>Playing</State> : null}
      </header>
      {rows && sections.length > 0 ? (
        <div className="exo-game-props-tabs" role="tablist" aria-label="Properties sections">
          {sections.map(({ id, title }) => {
            const active = id === activeId
            return (
              <button
                key={id}
                id={`exo-props-tab-${id}`}
                type="button"
                role="tab"
                aria-selected={active}
                aria-controls={`exo-props-panel-${id}`}
                className={cn('exo-game-props-tab', active && 'is-active')}
                onClick={() => setTab(id)}
              >
                {title}
              </button>
            )
          })}
        </div>
      ) : null}
      <div
        className="exo-props-pane"
        role={activeSection ? 'tabpanel' : undefined}
        id={activeSection ? `exo-props-panel-${activeSection.id}` : undefined}
        aria-labelledby={activeSection ? `exo-props-tab-${activeSection.id}` : undefined}
      >
        {rows && activeSection ? (
          <div className="exo-props-section exo-set-list">
            <SectionPanelContent id={activeSection.id} rows={rows} game={game} upscalers={upscalerStatus} uninstall={uninstall} artwork={artwork} />
          </div>
        ) : loadError ? (
          <div className="exo-set-error exo-utility-row exo-game-props-error">
            <ErrorNote message={loadError} />
            <button type="button" className="exo-set-btn" onClick={readAgain}>Try again</button>
          </div>
        ) : (
          <Wait label="Reading properties">
            <div className="exo-props-section exo-set-list">
              {times(5, i => <Row key={i} label={sample(i)} detail={sample(i + 2)}><span className="exo-set-btn">Change</span></Row>)}
            </div>
          </Wait>
        )}
      </div>
    </section>
  )
}

function PropRow({ label: name, detail, line, children }: { label: string; detail?: string; line: ReactNode; children: ReactNode }) {
  return (
    <div className="exo-props-row">
      <Row label={name} detail={detail}>{children}</Row>
      {line}
    </div>
  )
}

const ANTI_CHEAT_LAUNCH = [
  { value: 'with', label: 'Launch with anti-cheat' },
  { value: 'without', label: 'Launch without anti-cheat (offline)' },
] as const

function GeneralSection({ props, locked, busy, run, apply, line }: Rows) {
  const languages = props.language !== null && props.languageChoices.length > 1 ? props.languageChoices : null
  return (
    <>
      {props.launchOptions !== null && (
        <LaunchOptions value={props.launchOptions} busy={busy === 'launchOptions'} onSave={value => apply('launchOptions', value)} line={line('launchOptions')} />
      )}
      {props.launchOptions === null && props.launchOptionsNote && <PropRow label="Launch options" detail={props.launchOptionsNote} line={null}>{null}</PropRow>}
      {languages && (
        <PropRow label="Language" line={line('language')}>
          <Working on={busy === 'language'} label="Switching the language" />
          <Choice
            label="Language"
            value={props.language ?? ''}
            choices={languages}
            disabled={locked || busy !== null}
            confirm={value => download(languages.find(choice => choice.value === value)?.sizeBytes)}
            onChange={value => run({ key: 'language', value })}
          />
        </PropRow>
      )}
      {props.cloudSync && (
        <PropRow label="Cloud saves" line={line('cloudSync')}>
          <button type="button" className="exo-set-btn" disabled={busy !== null || locked} onClick={() => void apply('cloudSync')}>
            <Working on={busy === 'cloudSync'} label="Syncing cloud saves" />Sync now
          </button>
        </PropRow>
      )}
      {props.throughSteam != null && (
        <PropRow label="Steam Input" line={line('throughSteam')}>
          <Working on={busy === 'throughSteam'} label="Changing Steam Input" />
          <Switch checked={props.throughSteam} label="Steam Input" disabled={busy !== null} onChange={on => void apply('throughSteam', on ? 'on' : 'off')} />
        </PropRow>
      )}
      {props.steamOverlay != null && (
        <PropRow label="Steam overlay" detail={props.steamOverlayNote ?? undefined} line={line('steamOverlay')}>
          <Working on={busy === 'steamOverlay'} label="Changing the Steam overlay" />
          <Switch checked={props.steamOverlay} label="Steam overlay" disabled={busy !== null} onChange={on => void apply('steamOverlay', on ? 'on' : 'off')} />
        </PropRow>
      )}
      {props.antiCheatLaunch && (
        <PropRow label="Anti-cheat" detail={props.antiCheatLaunch.detail} line={line('antiCheatLaunch')}>
          <Working on={busy === 'antiCheatLaunch'} label="Saving your choice" />
          <Choice
            label="Anti-cheat"
            value={props.antiCheatLaunch.value}
            choices={ANTI_CHEAT_LAUNCH}
            disabled={locked || busy !== null}
            confirm={value => value === 'without' ? 'Play offline only?' : ''}
            onChange={value => run({ key: 'antiCheatLaunch', value })}
          />
        </PropRow>
      )}
      {props.storeProperties && (
        <PropRow label={`Controller in ${props.storeProperties}`} line={line('storeProperties')}>
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void apply('storeProperties')}>
            <Working on={busy === 'storeProperties'} label={`Opening ${props.storeProperties}`} />Open {props.storeProperties}
          </button>
        </PropRow>
      )}
    </>
  )
}

function FilesSection({ props, locked, busy, run, line, note, gameId, game, upscalers, uninstall }: Rows & { gameId: string; game: Game; upscalers: DlssStatus | null; uninstall: ReactNode }) {
  const size = props.sizeBytes ? formatSize(props.sizeBytes) : null
  const folders = props.folder !== null && props.folderChoices.length > 1 ? props.folderChoices : null
  const openFolder = () => {
    const failed = 'Exo couldn’t open the folder. Try again.'
    note('openFolder', '')
    void host.openFolder(gameId)
      .then(result => { if (!result.ok) note('openFolder', errorText(result.message, failed), true) })
      .catch((error: unknown) => note('openFolder', errorText(error, failed), true))
  }
  return (
    <>
      <PropRow label="Size on disk" line={line('openFolder')}>
        <State>{size ?? 'Unknown'}</State>
        {props.canBrowse && <button type="button" className="exo-set-btn" onClick={openFolder}>Open folder</button>}
        {uninstall}
      </PropRow>
      <CompressRow gameId={gameId} locked={locked} />
      {props.verifyLabel && (
        <PropRow label="Damaged or missing files" line={line('verify')}>
          <ConfirmButton className="exo-set-btn" disabled={locked || busy !== null} question={size ? `Check all ${size}?` : 'Check every file?'}
            timeoutMs={null} why="none" context="PropertiesSheet.verify" fallback="Exo couldn’t check the files. Try again."
            onConfirm={() => run({ key: 'verify', value: '' })} onFailed={failure => note('verify', failure.text, true)}>
            {props.verifyLabel}
          </ConfirmButton>
        </PropRow>
      )}
      <ShadersRow bytes={props.shaderCacheBytes} locked={locked} busy={busy} run={run} line={line} />
      {folders && (
        <PropRow label="Library folder" line={line('folder')}>
          {props.moving ? <><State><ProgressWords words="Moving" value={movedShare(props.moving)} /></State><Progress size="small" value={movedShare(props.moving)} label="Moving the game" /></> : (
            <Choice
              label="Library folder"
              value={props.folder ?? ''}
              choices={folders.map(choice => ({ value: choice.value, label: choice.detail ? `${choice.label} · ${choice.detail}` : choice.label }))}
              disabled={locked || busy !== null}
              confirm={() => size ? `Move ${size}?` : 'Move the game?'}
              onChange={value => run({ key: 'folder', value })}
            />
          )}
        </PropRow>
      )}
      <UpscalerRows view={upscalerView(upscalers)} gate={gateReason({ gameRunning: !!game.canStop })} busy={busy} run={run} line={line} />
    </>
  )
}

function banQuestion(antiCheat: string) {
  const name = antiCheat.replace(/^possibly /, '').replace(/^an /, '')
  return `Risk ${/^[aeiou]/i.test(name) ? 'an' : 'a'} ${name} ban?`
}

function UpscalerRows({ view, gate, busy, run, line }: Pick<Rows, 'busy' | 'run' | 'line'> & { view: ReturnType<typeof upscalerView>; gate: string | null }) {
  const choice = useChoiceMenu()
  const restorable = view.restorable > 0 && !gate
  const stop = async (from: Element) => {
    const answer = restorable
      ? await choice.ask('Stop allowing', [{ id: 'stop', label: 'Stop allowing' }, { id: 'restore', label: 'Stop and restore' }], from)
      : 'stop'
    if (answer) run({ key: 'consent', value: answer })
  }
  return (
    <>
      {view.antiCheatName && (view.consent === 'available' || view.consent === 'given') && (
        <PropRow label="Upscaler changes" line={line('consent')}>
          <Working on={busy === 'consent'} label="Saving your choice" />
          <State>{view.consent === 'given' ? 'Allowed' : 'Not allowed'}</State>
          {view.consent === 'given' ? (
            <button type="button" className="exo-set-btn" disabled={busy !== null} aria-haspopup={restorable ? 'menu' : undefined}
              onClick={event => void stop(event.currentTarget)}>
              Stop allowing
            </button>
          ) : (
            <ConfirmButton className="exo-set-btn" disabled={busy !== null} danger
              question={banQuestion(view.antiCheatName)}
              onConfirm={() => run({ key: 'consent', value: 'allow' })}>
              Allow changes
            </ConfirmButton>
          )}
          {choice.menu}
        </PropRow>
      )}
      {view.restorable > 0 && (
        <PropRow label="Upscaler files" line={line('restore')}>
          <Working on={busy === 'restore'} label="Putting the original files back" />
          <button type="button" className="exo-set-btn" disabled={!!gate || busy !== null} aria-label={gate ? `Restore the originals: ${gate}` : undefined}
            onClick={() => run({ key: 'restore', value: '' })}>
            Restore originals
          </button>
        </PropRow>
      )}
    </>
  )
}

function ShadersRow({ bytes, locked, busy, run, line }: Pick<Rows, 'locked' | 'busy' | 'run' | 'line'> & { bytes?: number | null }) {
  if (bytes == null) return null
  return (
    <PropRow label="Shaders" line={line('shaders')}>
      <Working on={busy === 'shaders'} label="Clearing the shaders" />
      <State>{bytes > 0 ? formatSize(bytes) : 'Builds at next start'}</State>
      <ConfirmButton className="exo-set-btn" disabled={locked || busy !== null || bytes === 0} question="Rebuild? The next start takes minutes"
        onConfirm={() => run({ key: 'shaders', value: '' })}>
        Rebuild shaders
      </ConfirmButton>
    </PropRow>
  )
}

function BetasSection({ props, locked, busy, run, apply, line }: Rows) {
  if (props.beta === null) return null
  return (
    <>
      <PropRow label="Beta" line={line('beta')}>
        <Working on={busy === 'beta'} label="Switching the beta" />
        <Choice
          label="Beta"
          value={props.beta}
          choices={props.betaChoices}
          disabled={locked || busy !== null}
          confirm={value => value ? 'Download its build?' : 'Download the release?'}
          onChange={value => run({ key: 'beta', value })}
        />
      </PropRow>
      {props.betaCode && (
        <AccessCode busy={busy === 'betaCode'} disabled={locked || busy !== null} onCheck={code => apply('betaCode', '', code)} line={line('betaCode')} />
      )}
    </>
  )
}

function ContentSection({ props, locked, busy, run, line }: Rows) {
  const content = props.content ?? []
  const [asking, setAsking] = useState<string | null>(null)
  return (
    <div className={`exo-props-content${content.length > 6 ? ' is-columns' : ''}`}>
      {content.map((item) => {
        const row = `content:${item.id}`
        return (
          <PropRow key={item.id} label={item.name} detail={item.toggle && item.sizeBytes ? formatSize(item.sizeBytes) : undefined} line={line(row)}>
            {item.toggle ? (
              <>
                <Working on={busy === row} label={`Changing ${item.name}`} />
                {asking === item.id ? (
                  <SwitchAsk question={item.enabled ? 'Remove it?' : download(item.sizeBytes)} danger={item.enabled}
                    onConfirm={() => { setAsking(null); run({ key: 'content', value: `${item.id}:${item.enabled ? 'off' : 'on'}` }) }}
                    onCancel={() => setAsking(null)} />
                ) : null}
                <Switch
                  checked={asking === item.id ? !item.enabled : item.enabled}
                  label={item.name}
                  disabled={locked || busy !== null}
                  onChange={on => setAsking(on === item.enabled ? null : item.id)}
                />
              </>
            ) : item.state ? <State>{item.state}</State> : null}
          </PropRow>
        )
      })}
    </div>
  )
}

function SwitchAsk({ question, danger, onConfirm, onCancel }: { question: string; danger: boolean; onConfirm: () => void; onCancel: () => void }) {
  const ask = useConfirmInPlace<HTMLButtonElement>(question, onConfirm, danger)
  const arm = useRef(ask.arm)
  const cancel = useRef(onCancel)
  const asked = useRef(false)
  useEffect(() => { cancel.current = onCancel })
  useEffect(() => arm.current(), [])
  useEffect(() => {
    if (ask.armed) { asked.current = true; return }
    if (!asked.current) return
    const button = ask.ref.current
    if (button?.contains(document.activeElement)) button.parentElement?.querySelector<HTMLElement>('[role="switch"]')?.focus({ preventScroll: true })
    cancel.current()
  }, [ask.armed, ask.ref])
  return (
    <button type="button" ref={ask.ref} className={cn('exo-set-btn is-compact exo-confirm', ask.className)} onClick={ask.press}>
      <ConfirmLabel armed={ask.armed} question={question}>{null}</ConfirmLabel>
    </button>
  )
}

function LaunchOptions({ value, busy, onSave, line }: { value: string; busy: boolean; onSave: (value: string) => Promise<boolean>; line: ReactNode }) {
  const [draft, setDraft] = useState(value)
  useEffect(() => setDraft(value), [value])
  const save = () => { if (draft.trim() !== value.trim()) void onSave(draft) }
  return (
    <PropRow label="Launch options" line={line}>
      <Working on={busy} label="Saving launch options" />
      <input
        className="exo-field exo-props-field"
        aria-label="Launch options"
        spellCheck={false}
        value={draft}
        maxLength={1024}
        onChange={event => setDraft(event.target.value)}
        onBlur={save}
        onKeyDown={(event) => { if (event.key === 'Enter') save() }}
      />
    </PropRow>
  )
}

function AccessCode({ busy, disabled, onCheck, line }: { busy: boolean; disabled: boolean; onCheck: (code: string) => Promise<boolean>; line: ReactNode }) {
  const [code, setCode] = useState('')
  const check = () => {
    const value = code.trim()
    if (value) void onCheck(value).then((ok) => { if (ok) setCode('') })
  }
  return (
    <PropRow label="Access code" line={line}>
      <input
        className="exo-field exo-props-field"
        aria-label="Access code"
        type="password"
        autoComplete="off"
        value={code}
        maxLength={256}
        disabled={disabled}
        onChange={event => setCode(event.target.value)}
        onKeyDown={(event) => { if (event.key === 'Enter') check() }}
      />
      <button type="button" className="exo-set-btn" disabled={disabled || !code.trim()} onClick={check}>
        <Working on={busy} label="Checking the code" />Check
      </button>
    </PropRow>
  )
}
