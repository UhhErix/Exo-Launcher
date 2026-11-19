import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { host, type DlssRunResult, type DlssStatus, type Game, type GameProperties, type GamePropertyKey } from '../../lib/host'
import { useDialogFocus } from '../../lib/focusTrap'
import { useDismiss } from '../../lib/dismiss'
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
import { ArtworkTools, type Artwork } from './Artwork'
import { CompressRow } from './CompressRow'
import { sheetMemory } from './useGameDetails'

/** The last few games' properties, so Properties opened again draws at once while it reads again. */
const propertiesRead = sheetMemory<GameProperties>(8)

/** While Steam moves a game its progress is read every 2 s; a read that finds it no further waits twice as long, up to 8 s. */
const MOVE_READ_MS = 2000
const MOVE_READ_MAX_MS = 8000

const READ_FAILED = 'Exo couldn’t read this game\'s properties. Try again.'
const CHANGE_FAILED = 'Exo couldn’t make that change. Try again.'

/**
 * A change once it is asked for. One that downloads, deletes or moves files (or lets Exo change an anti-cheat game's)
 * asks first, in place on the control pressed (DESIGN.md › Confirm), saying what it costs.
 */
type Change = { key: GamePropertyKey | 'verify' | 'restore' | 'consent'; value: string }

/** What every section needs: the properties, what is in flight, and the row helpers. */
type Rows = {
  props: GameProperties
  /** The game is running: nothing that changes its files can start. */
  locked: boolean
  busy: string | null
  /** Verify answers with its promise, for its button to show (ConfirmButton); every other change shows on its row. */
  run: (change: Change) => void | Promise<unknown>
  apply: (key: GamePropertyKey, value?: string, secret?: string) => Promise<boolean>
  line: (row: string) => ReactNode
  /** Puts what happened on a row's line (a failure in Bad). */
  note: (row: string, text: string, bad?: boolean) => void
}

type SectionId = 'general' | 'files' | 'betas' | 'content' | 'artwork'

/** A failure in plain words (lib/errorWords.ts): what went wrong and what to do, never an exception's own text. */
function errorText(error: unknown, fallback = CHANGE_FAILED): string {
  return plainErrorText(error, fallback)
}

/** A download's question, with its size when the store gives one: "Download 22.4 GB?" */
function download(bytes: number | null | undefined): string {
  return bytes && bytes > 0 ? `Download ${formatSize(bytes)}?` : 'Download it?'
}

/** How much of a move is done, 0 to 1; null (gliding, no figure) until its size is known. */
function movedShare(moving: { done: number; total: number }): number | null {
  return moving.total > 0 ? moving.done / moving.total : null
}

/** Which row a change belongs to: one row per DLC, Verify's own row. */
function rowOf(change: Change): string {
  return change.key === 'content' ? `content:${change.value.slice(0, change.value.lastIndexOf(':'))}` : change.key
}

/** A change going through: the ring beside the row's control, or before the words of the button that made it. */
function Working({ on, label }: { on: boolean; label: string }) {
  return on ? <Spinner label={label} /> : null
}

/** The sections this game's store has, in Steam's order, with Artwork (Exo's own) last. */
function sectionsFor(props: GameProperties): { id: SectionId; title: string }[] {
  return [
    // General only with something in it (a Riot game has none of these).
    ...(props.launchOptions !== null || props.launchOptionsNote || props.language !== null || props.cloudSync || props.storeProperties || props.throughSteam != null || props.steamOverlay != null || props.antiCheatLaunch
      ? [{ id: 'general' as const, title: 'General' }] : []),
    { id: 'files' as const, title: 'Installed files' },
    ...(props.beta !== null && props.betaChoices.length > 0 ? [{ id: 'betas' as const, title: 'Betas' }] : []),
    ...(props.content && props.content.length > 0 ? [{ id: 'content' as const, title: props.contentLabel }] : []),
    { id: 'artwork' as const, title: 'Artwork' },
  ]
}

/**
 * An installed game's Properties over its page: a sheet of one column of rows, with no title and no close (Escape or a
 * press outside closes it); only a list of a store's content has a heading. What the game's store offers (Steam: launch options, language, files,
 * betas, DLC, and Steam's own controller page where Steam Input maps the
 * controller; Epic: optional content and cloud saves), only the rows that store
 * has, and Exo's own: the folder, the upscalers' original files and the cover.
 * Steam's overlay, Steam Input, cloud saves and updates have no row: Exo keeps
 * them right for each game itself. A row that downloads, deletes or
 * moves files asks first, saying how much; the store's own download then shows
 * in Exo's downloads.
 */
export function PropertiesSheet({ game, artwork, upscalers: upscalerStatus, onUpscalersChanged, uninstall, onClose }: {
  game: Game
  artwork: Artwork
  upscalers: DlssStatus | null
  onUpscalersChanged: () => void
  /** Uninstall (or Remove), on the row with the game's size. */
  uninstall: ReactNode
  onClose: () => void
}) {
  const dialog = useRef<HTMLDivElement>(null)
  useDialogFocus(dialog)
  useDismiss(dialog, onClose, { modal: true })
  const [props, setProps] = useState<GameProperties | null>(() => propertiesRead.get(game.id))
  const [loadError, setLoadError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [notes, setNotes] = useState<Record<string, { text: string; bad?: boolean }>>({})

  /** Reads the properties (the last read stays on screen meanwhile); how far a move has got, for its reads. */
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

  // While Steam moves the game its progress is read again: only while Exo is on screen (this panel is open while it
  // is mounted), one read at a time, every 2 s while it moves on and up to 8 s while it doesn't.
  const moving = !!props?.moving
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

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      // A dropdown open in the panel (language, beta, folder) takes the key first; a control asking puts itself back.
      if (document.querySelector('[popover]:popover-open') || (event.target as Element | null)?.closest?.('[data-exo-armed]')) return
      // Escape closes this panel, not the game page under it.
      event.preventDefault()
      event.stopPropagation()
      onClose()
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [onClose])

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

  /** An upscaler change (the originals back, or the anti-cheat consent), then the game page reads the files again. */
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
      upscalers('restore', () => host.dlssRestore(game.id), 'The game\'s own files are back.')
      return
    }
    if (asked.key === 'consent') {
      // Consent changes no file; Stop and restore also puts the game's own files back.
      const allow = value === 'allow'
      upscalers('consent', async () => {
        const answer = await host.dlssConsent(game.id, allow, upscalerView(upscalerStatus).antiCheatName ?? undefined)
        if (!answer.ok || value !== 'restore') return { ok: answer.ok, message: answer.message }
        return host.dlssRestore(game.id)
      }, allow ? 'Exo may update this game\'s upscalers.' : value === 'restore' ? 'Stopped. The game\'s own files are back.' : 'Stopped.')
      return
    }
    if (asked.key !== 'verify') {
      void apply(asked.key, asked.value, undefined, rowOf(asked))
      return
    }
    // The button shows the check (its ring, then its check, or Bad); what the store said stays on the row.
    note('verify', '')
    return host.repair(game.id).then(result => {
      if (result.ok) note('verify', result.message ?? 'Files verified.')
      return result
    })
  }

  /** The row's own line: what the last change said; a failure as every row's error, with Copy logs (settings › Row). */
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
  const readAgain = () => {
    setLoadError(null)
    void load()
  }

  return createPortal(
    <div className="exo-props-back exo-portal">
      <div ref={dialog} className="exo-props" role="dialog" aria-modal="true" aria-label={`Properties of ${game.title}`} tabIndex={-1}>
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        {props?.running ? <header className="exo-props-head"><State>Playing</State></header> : null}
        <div className="exo-props-pane">
          {rows ? (
            <div className="exo-settings-layout">
              {sections.map(({ id, title }) => (
                <section key={id} className="exo-set-section" id={`props-${id}`} aria-label={title}>
                  {id === 'content' ? <header className="exo-set-section-head"><h2>{title}</h2></header> : null}
                  <div className="exo-props-section exo-set-list">
                    {id === 'general' && <GeneralSection {...rows} />}
                    {id === 'files' && <FilesSection {...rows} gameId={game.id} game={game} upscalers={upscalerStatus} uninstall={uninstall} />}
                    {id === 'betas' && <BetasSection {...rows} />}
                    {id === 'content' && <ContentSection {...rows} />}
                    {id === 'artwork' && (
                      <PropRow label="Cover" line={null}>
                        <ArtworkTools artwork={artwork} disabled={false} className="exo-set-btn" />
                      </PropRow>
                    )}
                  </div>
                </section>
              ))}
            </div>
          ) : loadError ? (
            <div className="exo-set-error exo-utility-row">
              <ErrorNote message={loadError} />
              <button type="button" className="exo-set-btn" onClick={readAgain}>Try again</button>
            </div>
          ) : (
            <Wait label="Reading properties">
              <div className="exo-settings-layout">
                <section className="exo-set-section"><div className="exo-props-section exo-set-list">{times(6, i => <Row label={sample(i)} detail={sample(i + 2)}><span className="exo-set-btn">Change</span></Row>)}</div></section>
              </div>
            </Wait>
          )}
        </div>
      </div>
    </div>,
    document.body,
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

/** Which of Steam's Play-menu entries Play starts, for a game Steam offers without its anti-cheat. */
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
        // Without: Steam's own entry for it, and the game counts as one with no anti-cheat. Back with: Exo takes out
        // what went in first.
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

/** Allow's confirmation, the one place the ban risk is said: "Risk a BattlEye ban?" ("possibly Valve Anti-Cheat" asks about Valve's). */
function banQuestion(antiCheat: string) {
  const name = antiCheat.replace(/^possibly /, '').replace(/^an /, '')
  return `Risk ${/^[aeiou]/i.test(name) ? 'an' : 'a'} ${name} ban?`
}

/**
 * An anti-cheat game's consent to upscaler changes, and the originals put back. Allow changes asks in place; Stop
 * allowing goes at once (Allow changes takes it back), or, with the originals there to put back, asks which from a menu.
 */
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

/** Call of Duty's shader cache: its size, and Rebuild shaders, which deletes it so the game builds it again as it starts. */
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

/** Every DLC in two columns when there are many: a switch where the store has one, else where it stands. */
function ContentSection({ props, locked, busy, run, line }: Rows) {
  const content = props.content ?? []
  // The DLC whose switch is asking: the switch shows where it is going while its question is beside it.
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

/**
 * A switch that downloads or removes asks beside itself as it flips (DESIGN.md › Confirm): its question appears
 * armed, its width springing open from nothing, and a press on it or Enter runs it. Put back (Escape, a press
 * elsewhere, focus leaving, 4 s), the switch goes back and has the focus again.
 */
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

/** Exo's own launch options for the game, saved on Enter or when the field loses focus. */
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

/** A private beta's access code; it goes to the store and is never kept. */
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
