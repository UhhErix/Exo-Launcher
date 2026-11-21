import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { host, onHostEvent, type Game, type ShaderPrepareGame, type ShaderRebuild } from '../../lib/host'
import { thumbUrl } from '../../lib/coverThumb'
import { usePageVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'
import { onUiPrefs, setUiPref, uiPref } from '../../lib/uiPrefs'
import { Progress, ProgressWords } from '../ui/Progress'
import { Wait, sample, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'
import { ErrorNote } from '../ui/ErrorNote'
import { SectionActions } from '../gpu/SectionHead'
import { call, message, type Estimate, type Queue, type QueueItem, type ShaderGame, type Snapshot } from './shaderCall'
import { ConfirmLabel, useConfirmInPlace } from '../ui/ConfirmButton'
import { rebase, stale } from './snapshotOrder'
import { ArtImg } from '../ui/ArtImg'
import { errorText } from '../../lib/errorWords'

const NEVER_RAN: Snapshot = { rev: 0, games: [], queue: { waiting: [], running: false, paused: false, canStart: false }, app: {} }

function useShaderEngine(shown: boolean) {
  const [snap, setSnap] = useState<Snapshot | null>(null)
  const [missing, setMissing] = useState(false)
  const [failed, setFailed] = useState<string | null>(null)
  const take = useCallback((next: Snapshot) => setSnap(prev => stale(prev, next) ? prev : next), [])
  const read = useCallback(() => call('snapshot').then(next => { take((next as Snapshot | null) ?? NEVER_RAN); setFailed(null) }).catch((error: unknown) => {
    report(error, 'ShadersBoard.snapshot', { level: 'warn' })
    setFailed(message(error, 'The shader engine did not answer.'))
  }), [take])
  useEffect(() => {
    let live = true
    void host.shadersAddon().then(status => {
      if (!live) return
      if (!status.present) setMissing(true)
      else if (status.snapshot) take(status.snapshot as Snapshot)
    }).catch(() => { if (live) setMissing(true) })
    return () => { live = false }
  }, [take])
  useEffect(() => {
    if (!shown) return
    let live = true
    setSnap(rebase)
    void host.shadersAddon().then(status => {
      if (!live) return
      if (!status.present) { setMissing(true); return }
      void call('ui.visible', { visible: true }).catch(() => { })
      void read()
    }).catch(() => { if (live) setMissing(true) })
    const off = onHostEvent('shaders.event', ({ name, data }) => {
      if (name === 'state') take(data as Snapshot)
      else if (name === 'progress') {
        const next = data as { rev: number; queue: Queue }
        setSnap(prev => prev && next.rev > prev.rev ? { ...prev, rev: next.rev, queue: next.queue } : prev)
      } else if (name === 'ready') { setSnap(rebase); void read() }
      else if (name === 'addon') {
        const state = (data as { state?: string } | null)?.state
        if (state === 'starting') return
        if (state !== 'running') setSnap(rebase)
        void read()
      }
    })
    return () => {
      live = false
      off()
      void call('ui.visible', { visible: false }).catch(() => { })
    }
  }, [shown, read, take])
  return { snap, missing, failed, read }
}

function useShown(element: HTMLElement | null, active: boolean) {
  const visible = usePageVisible()
  const [near, setNear] = useState(false)
  useEffect(() => {
    if (!element || !active) return
    const watch = new IntersectionObserver(([entry]) => setNear(entry.isIntersecting), { rootMargin: '200px 0px' })
    watch.observe(element)
    return () => { watch.disconnect(); setNear(false) }
  }, [element, active])
  return active && visible && near
}

function useEstimates(active: boolean, key: string, games: readonly ShaderGame[] | undefined) {
  const [seconds, setSeconds] = useState<Record<string, number>>({})
  useEffect(() => {
    if (!active) return
    let live = true
    void host.shadersEstimates().then(reply => { if (live && reply.ok) setSeconds(reply.seconds) })
      .catch((error: unknown) => report(error, 'ShadersBoard.estimates', { level: 'warn' }))
    return () => { live = false }
  }, [active, key])
  const sized = Object.fromEntries(Object.entries(seconds).map(([id, value]): [string, Estimate] => [id, { seconds: value, learned: false }]))
  const live = Object.fromEntries((games ?? []).flatMap(game => game.estimate ? [[game.id, game.estimate] as const] : []))
  return { ...sized, ...live }
}

function savedOrder(): string[] {
  const saved = uiPref('shadersOrder')
  return Array.isArray(saved) ? saved.filter((id): id is string => typeof id === 'string') : []
}

function useSavedOrder() {
  const [order, setOrder] = useState<string[]>(savedOrder)
  useEffect(() => onUiPrefs(() => setOrder(savedOrder())), [])
  const save = (next: string[]) => {
    setOrder(next)
    setUiPref('shadersOrder', next)
  }
  return [order, save] as const
}

const norm = (value: string) => value.toLowerCase().replace(/[^a-z0-9]/g, '')

function coverFinder(games: readonly Game[]) {
  const installed = games.filter(game => game.installed && game.coverUrl)
  const byId = new Map(installed.map(game => [game.id, game]))
  const byTitle = new Map(installed.map(game => [norm(game.title), game]))
  return (item: { id: string; name: string; installDir?: string }) => {
    const dir = item.installDir?.toLowerCase()
    const game = byId.get(item.id)
      ?? (dir ? installed.find(g => g.path && dir.startsWith(g.path.toLowerCase())) : undefined)
      ?? byTitle.get(norm(item.name))
    return game?.coverUrl ? thumbUrl(game.coverUrl, 240) : null
  }
}

export function ShadersBoard({ active, games }: { active: boolean; games: readonly Game[] }) {
  const [root, setRoot] = useState<HTMLElement | null>(null)
  const shown = useShown(root, active)
  const { snap, missing, failed, read } = useShaderEngine(shown)
  const cover = coverFinder(games)
  const prepare = usePrepare(shown)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [order, saveOrder] = useSavedOrder()
  const [pending, setPending] = useState<string[] | null>(null)
  const [moved, setMoved] = useState('')
  const estimates = useEstimates(active, `${snap?.games.length ?? 0}:${snap?.queue.finished?.length ?? 0}:${snap?.app.scanning ? 1 : 0}`, snap?.games)
  const run = (key: string, ...steps: Step[]) => {
    setBusy(key)
    setError(null)
    void steps.reduce<Promise<unknown>>((chain, [method, params]) => chain.then(() => call(method, params)), Promise.resolve())
      .catch((caught: unknown) => {
        report(caught, `ShadersBoard.${key}`, { level: 'warn' })
        setError(message(caught, 'The shader engine did not answer. Try again.'))
      })
      .finally(() => { setBusy(null); void read().finally(() => setPending(null)) })
  }
  if (missing) return (
    <div ref={setRoot} className="exo-shaders-board">
      <p className="exo-shaders-empty">Exo Shaders isn't installed</p>
      <Posters posters={prepare.posters} lane={[]} onMove={() => { }} render={game => <PreparePoster item={prepare.byPoster.get(game.id)} cover={cover(game)} />} />
    </div>
  )
  if (!snap) return (
    <div ref={setRoot} className="exo-shaders-board" aria-busy={failed ? undefined : true}>
      {failed
        ? <>
          <ErrorNote className="exo-shaders-error" message={failed} />
          <SectionActions><button type="button" className="exo-set-btn" onClick={() => void read()}>Try again</button></SectionActions>
        </>
        : <PostersWait />}
    </div>
  )
  const { queue } = snap
  const current = queue.current ?? null
  const view = arrange(snap, order, pending, estimates)
  const { lane, waitingIds } = view
  const posters = [...view.posters, ...prepare.without(view.posters)]

  const move = (id: string, target: number) => {
    const waiting = waitingIds.length
    const from = lane.indexOf(id)
    if (from < 0) return
    const to = Math.max(0, Math.min(from < waiting ? waiting - 1 : lane.length - 1, target))
    if (to === from) return
    const next = lane.filter(other => other !== id)
    next.splice(to, 0, id)
    setMoved(`${view.byId.get(id)?.name ?? 'The game'} is ${to === 0 ? 'first' : `number ${to + 1}`} of ${next.length}.`)
    if (to >= waiting) {
      const ready = next.slice(waiting)
      saveOrder([...ready, ...order.filter(other => !ready.includes(other))])
      return
    }
    setPending(next)
    run(`move:${id}`, ...(from < waiting ? [] : [['queue.add', { id }] as Step]), ['queue.move', { id, index: to }])
  }

  return (
    <div ref={setRoot} className="exo-shaders-board">
      <Actions snap={snap} view={view} estimates={estimates} busy={busy} run={run} />
      <CacheStrip snap={snap} busy={busy} run={run} />
      {error || failed ? <ErrorNote className="exo-shaders-error" message={error ?? failed} /> : null}
      {posters.length === 0 && !snap.app.scanning ? <p className="exo-shaders-empty">{snap.games.length ? 'Every game is compiled' : 'No games found'}</p> : null}
      <Posters
        posters={posters}
        lane={lane}
        onMove={move}
        render={(game, drop) => {
          if (prepare.byPoster.has(game.id)) return <PreparePoster item={prepare.byPoster.get(game.id)} cover={cover(game)} />
          const at = lane.indexOf(game.id)
          return (
            <Poster game={game} cover={cover(game)} current={current?.id === game.id ? current : null} estimate={estimates[game.id]}
              place={at >= 0 && at < waitingIds.length ? at + 1 : undefined} orderable={at >= 0} drop={drop}
              error={game.error || queue.finished?.find(item => item.id === game.id && item.error)?.error || null}
              onCompile={() => run(`game:${game.id}`, game.queued ? ['queue.remove', { id: game.id }] : ['game.compile', { id: game.id }])}
              onKey={event => {
                const to = at >= 0 ? reorderKey(event, at, lane.length - 1) : undefined
                if (to === undefined) return
                event.preventDefault()
                move(game.id, to)
              }} />
          )
        }}
      />
      <span id="exo-shaders-move-help" className="sr-only">Drag it, or press Ctrl and an arrow key, to change where it is in the queue.</span>
      <span className="sr-only" role="status" aria-live="polite">{moved}</span>
    </div>
  )
}

type Step = [string, unknown?]
type View = ReturnType<typeof arrange>

function arrange(snap: Snapshot, order: string[], pending: string[] | null, estimates: Record<string, Estimate>) {
  const current = snap.queue.current ?? null
  const byId = new Map(snap.games.map(game => [game.id, game]))
  const waitingIds = snap.queue.waiting.map(item => item.id).filter(id => id !== current?.id)
  const inQueue = new Set([current?.id, ...waitingIds])
  const saved = new Map(order.map((id, index) => [id, index]))
  const time = (id: string) => estimates[id]?.seconds ?? Infinity
  const readyIds = snap.games
    .filter(game => game.inAddAllReady && !inQueue.has(game.id))
    .map((game, index) => ({ id: game.id, at: saved.get(game.id) ?? Infinity, index }))
    .sort((a, b) => (a.at - b.at) || (time(a.id) - time(b.id)) || a.index - b.index)
    .map(game => game.id)
  const engineLane = [...waitingIds, ...readyIds]
  const lane = pending && pending.length === engineLane.length && pending.every(id => engineLane.includes(id)) ? pending : engineLane
  const inLane = new Set(lane)
  const rest = snap.games.filter(game => !inQueue.has(game.id) && !inLane.has(game.id) && (!!game.error || game.tone === 'warn' || game.tone === 'primary')).sort((a, b) => rank(a) - rank(b))
  const posters = [current ? byId.get(current.id) : undefined, ...lane.map(id => byId.get(id)), ...rest].filter((game): game is ShaderGame => !!game)
  return { byId, waitingIds, readyIds: lane.filter(id => !waitingIds.includes(id)), lane, posters }
}

function reorderKey(event: KeyboardEvent<HTMLButtonElement>, at: number, last: number): number | undefined {
  if (!event.ctrlKey || event.altKey || event.metaKey) return undefined
  return ({ ArrowLeft: at - 1, ArrowUp: at - 1, ArrowRight: at + 1, ArrowDown: at + 1, Home: 0, End: last } as Record<string, number>)[event.key]
}

function Actions({ snap, view, estimates, busy, run }: {
  snap: Snapshot
  view: View
  estimates: Record<string, Estimate>
  busy: string | null
  run: (key: string, ...steps: Step[]) => void
}) {
  const { queue } = snap
  const current = queue.current ?? null
  const { waitingIds, readyIds, lane } = view
  const working = !!current || queue.running
  const scanning = !!snap.app.scanning || busy === 'scan'
  const time = working || lane.length ? eta(queueTime(current, working ? waitingIds : lane, estimates), working) : null
  const left = working ? waitingIds.length + (current ? 1 : 0) : lane.length
  const games = left ? `${left} ${left === 1 ? 'game' : 'games'}` : null
  const rebuild = useRebuild()
  const forDriver = rebuild && rebuild.state !== 'idle' && rebuild.state !== 'done'
    ? `Getting ready for the new driver · ${rebuild.ready} of ${rebuild.total} games` : null
  return (
    <SectionActions>
      {forDriver ? <span className="exo-pc-section-count" role="status">{forDriver}</span>
        : games || time ? <span className="exo-pc-section-count">{[games, time].filter(Boolean).join(' · ')}</span> : null}
      <button type="button" className="exo-set-btn" disabled={scanning} aria-busy={scanning} onClick={() => run('scan', ['scan'])}>
        {scanning ? <Spinner label="Looking for games" /> : null}
        Refresh
      </button>
      <QueueButtons queue={queue} working={working} readyIds={readyIds} busy={busy} run={run} />
    </SectionActions>
  )
}

function cacheSteps(snap: Snapshot): Step[] {
  const { cache, gpu } = snap.app
  if (gpu?.vendor !== 'nvidia') return []
  const steps: Step[] = []
  if (cache?.configurable && !cache.largest && !cache.off) steps.push(['driver.setCacheLimit', { index: 12 }])
  if (gpu.windowsCleanup && !gpu.windowsCleanup.kept) steps.push(['driver.setCleanupGuard', { on: true }])
  return steps
}

function cacheMetaText(snap: Snapshot) {
  const { cache, gpu } = snap.app
  const cacheText = cache?.amdDx12?.text
    ?? (cache?.usedText ? `Cache: ${cache.usedText}${cache.limitCurrent ? ` · ${cache.limitCurrent}` : ''}` : null)
  const deliveryText = gpu?.shaderDelivery?.text ? `Shader Delivery: ${gpu.shaderDelivery.text}` : null
  return [cacheText, deliveryText].filter(Boolean).join(' · ')
}

function cacheStripState(snap: Snapshot) {
  const cache = snap.app.cache
  return {
    off: cache?.off ?? null,
    oldDriverNote: cache?.oldDriverNote ?? null,
    steps: cacheSteps(snap),
    meta: cacheMetaText(snap),
  }
}

function CacheStrip({ snap, busy, run }: {
  snap: Snapshot
  busy: string | null
  run: (key: string, ...steps: Step[]) => void
}) {
  const { off, oldDriverNote, steps, meta } = cacheStripState(snap)
  if (!off && !oldDriverNote && steps.length === 0 && !meta) return null
  return (
    <div className="exo-shaders-cache">
      {off ? (
        <div className="exo-shaders-cache-alert">
          <span>{off}</span>
          <button type="button" className="exo-set-btn is-primary" disabled={busy !== null}
            onClick={() => run('cache-limit', ['driver.setCacheLimit', { index: 12 }])}>
            Set Unlimited
          </button>
        </div>
      ) : null}
      <div className="exo-shaders-cache-row">
        {meta ? <span className="exo-shaders-cache-meta">{meta}</span> : <span />}
        <div className="exo-shaders-cache-actions">
          {steps.length > 0 ? (
            <button type="button" className="exo-set-btn" disabled={busy !== null}
              onClick={() => run('optimize-cache', ...steps)}>
              {busy === 'optimize-cache' ? <Spinner label="Optimizing cache" /> : null}
              Optimize shader cache
            </button>
          ) : null}
          {oldDriverNote ? (
            <button type="button" className="exo-set-btn" disabled={busy !== null}
              onClick={() => run('free-old', ['cache.freeOldDriver'])}>
              {busy === 'free-old' ? <Spinner label="Freeing old cache" /> : null}
              Free old cache ({oldDriverNote})
            </button>
          ) : null}
        </div>
      </div>
    </div>
  )
}

function useRebuild() {
  const [rebuild, setRebuild] = useState<ShaderRebuild | null>(null)
  useEffect(() => {
    let live = true
    host.shadersRebuild().then(next => { if (live && next.ok) setRebuild(next) }).catch(e => report(e, 'ShadersBoard.rebuild'))
    const off = onHostEvent('shaders.rebuild', next => setRebuild(next))
    return () => { live = false; off() }
  }, [])
  return rebuild
}

function QueueButtons({ queue, working, readyIds, busy, run }: {
  queue: Queue
  working: boolean
  readyIds: string[]
  busy: string | null
  run: (key: string, ...steps: Step[]) => void
}) {
  if (!working) {
    return (
      <button type="button" className="exo-set-btn is-primary" disabled={(readyIds.length === 0 && !queue.canStart) || busy === 'compile'}
        onClick={() => run('compile', ...(readyIds.length ? [['queue.add', { ids: readyIds }] as Step] : []), ['queue.start'])}>
        {busy === 'compile' ? <Spinner label="Starting the compile" /> : null}Compile
      </button>
    )
  }
  return (
    <>
      <button type="button" className="exo-set-btn" disabled={busy === 'pause'} onClick={() => run('pause', [queue.paused ? 'queue.resume' : 'queue.pause'])}>
        {busy === 'pause' ? <Spinner label={queue.paused ? 'Resuming' : 'Pausing'} /> : null}{queue.paused ? 'Resume' : 'Pause'}
      </button>
      <button type="button" className="exo-set-btn" disabled={busy === 'stop'} onClick={() => run('stop', ['queue.stop'])}>
        {busy === 'stop' ? <Spinner label="Stopping" /> : null}Stop
      </button>
    </>
  )
}

type Drop = 'before' | 'after' | 'lifted' | null
type Dragging = { id: string; pointer: number; x: number; y: number; to: number | null }

const GAP_X = 16
const GAP_Y = 20
const WIDEST = 240

function fitFor(count: number, width: number, height: number, words: number) {
  let best = { columns: 0, wide: 0 }
  for (let c = 1; c <= count; c++) {
    const wide = (width - (c - 1) * GAP_X) / c
    if (wide > WIDEST) continue
    if (wide < 112) break
    const rows = Math.ceil(count / c)
    const size = Math.floor(Math.min(wide, ((height - (rows - 1) * GAP_Y) / rows - words) / 1.5))
    if (size >= 112 && size >= best.wide) best = { columns: c, wide: size }
  }
  return best
}

function useFit(list: React.RefObject<HTMLUListElement | null>, count: number) {
  useLayoutEffect(() => {
    const ul = list.current
    const pane = ul?.closest<HTMLElement>('.exo-pc-pane')
    if (!ul || !pane || count === 0) return
    const fit = () => {
      const height = pane.clientHeight - parseFloat(getComputedStyle(pane).paddingBottom) - (ul.getBoundingClientRect().top - pane.getBoundingClientRect().top) - 20
      const first = ul.querySelector<HTMLElement>('li')
      const art = first?.querySelector<HTMLElement>('.exo-shaders-art')
      const words = first && art ? first.offsetHeight - art.offsetHeight : 50
      const { columns, wide } = fitFor(count, ul.clientWidth - 24, height, words)
      ul.style.setProperty('--exo-shaders-columns', columns ? String(columns) : null)
      ul.style.setProperty('--exo-shaders-wide', columns ? `${wide}px` : null)
      ul.classList.toggle('is-fit', columns > 0)
    }
    fit()
    const watch = new ResizeObserver(fit)
    watch.observe(pane)
    return () => watch.disconnect()
  }, [list, count])
}

function PostersWait() {
  const list = useRef<HTMLUListElement>(null)
  useFit(list, 8)
  return (
    <Wait label="Reading your games' shaders">
      <ul ref={list} className="exo-shaders-posters">
        {times(8, i => <li className="exo-shaders-slot"><div className="exo-shaders-poster"><button type="button"><span className="exo-shaders-art" skullet-block="" /><span className="exo-shaders-name">{sample(i)}</span><span className="exo-shaders-state">Ready to compile</span></button></div></li>)}
      </ul>
    </Wait>
  )
}

function Posters({ posters, lane, onMove, render }: {
  posters: ShaderGame[]
  lane: string[]
  onMove: (id: string, to: number) => void
  render: (game: ShaderGame, drop: Drop) => React.ReactNode
}) {
  const list = useRef<HTMLUListElement>(null)
  useFit(list, posters.length)
  const [drag, setDrag] = useState<Dragging | null>(null)
  const dragged = useRef(false)
  const rects = useRef<DOMRect[] | null>(null)
  const measureLane = () => {
    rects.current = Array.from(list.current?.querySelectorAll<HTMLElement>('[data-lane]') ?? []).map(item => item.getBoundingClientRect())
  }
  const slotAt = (x: number, y: number) => {
    const items = rects.current ?? []
    const index = items.findIndex(box => y < box.top || (y <= box.bottom && x < box.left + box.width / 2))
    return index < 0 ? items.length : index
  }
  const down = (event: PointerEvent<HTMLLIElement>, id: string) => {
    dragged.current = false
    if (event.button !== 0 || !lane.includes(id)) return
    setDrag({ id, pointer: event.pointerId, x: event.clientX, y: event.clientY, to: null })
  }
  const moveTo = (event: PointerEvent<HTMLLIElement>) => {
    if (!drag || event.pointerId !== drag.pointer) return
    if (drag.to === null) {
      if (Math.hypot(event.clientX - drag.x, event.clientY - drag.y) < 6) return
      try { event.currentTarget.setPointerCapture(event.pointerId) } catch {}
      dragged.current = true
      measureLane()
    }
    const scroller = list.current?.closest<HTMLElement>('.exo-pc-pane') ?? list.current
    const box = scroller?.getBoundingClientRect()
    if (box && scroller) {
      const edge = event.clientY < box.top + 48 ? -1 : event.clientY > box.bottom - 48 ? 1 : 0
      if (edge) { scroller.scrollBy({ top: edge * 16 }); measureLane() }
    }
    setDrag({ ...drag, to: slotAt(event.clientX, event.clientY) })
  }
  const up = (event: PointerEvent<HTMLLIElement>) => {
    if (!drag || event.pointerId !== drag.pointer) return
    if (drag.to !== null) {
      const from = lane.indexOf(drag.id)
      onMove(drag.id, drag.to > from ? drag.to - 1 : drag.to)
    }
    rects.current = null
    setDrag(null)
  }
  const target = drag?.to ?? null
  const from = drag ? lane.indexOf(drag.id) : -1
  const shows = target !== null && target !== from && target !== from + 1
  return (
    <ul ref={list} className={`exo-shaders-posters${drag?.to != null ? ' is-dragging' : ''}`}
      onClickCapture={event => { if (dragged.current) { dragged.current = false; event.preventDefault(); event.stopPropagation() } }}>
      {posters.map(game => {
        const at = lane.indexOf(game.id)
        const drop: Drop = drag?.to != null && game.id === drag.id ? 'lifted'
          : shows && at === target ? 'before'
          : shows && target === lane.length && at === lane.length - 1 ? 'after' : null
        return (
          <li key={game.id} data-lane={at >= 0 ? at : undefined}
            className={`exo-shaders-slot${drop ? ` is-${drop}` : ''}`}
            onPointerDown={event => down(event, game.id)}
            onPointerMove={moveTo}
            onPointerUp={up}
            onPointerCancel={() => { rects.current = null; setDrag(null) }}>
            {render(game, drop)}
          </li>
        )
      })}
    </ul>
  )
}

const ROUGH = 1.5

type Span = { low: number; high: number; learned: boolean }

function spanOf({ seconds, lowSeconds, learned }: Estimate, share = 1): Span {
  const sure = learned && lowSeconds === undefined
  return {
    low: (lowSeconds ?? (sure ? seconds : seconds / ROUGH)) * share,
    high: (lowSeconds !== undefined || sure ? seconds : seconds * ROUGH) * share,
    learned: sure,
  }
}

function queueTime(current: QueueItem | null, after: string[], estimates: Record<string, Estimate>) {
  const total: Span = { low: 0, high: 0, learned: true }
  let unknown = 0
  const add = (span: Span) => { total.low += span.low; total.high += span.high; total.learned &&= span.learned }
  if (current) {
    const usual = estimates[current.id]
    if (typeof current.timeLeftSeconds === 'number') add({ low: current.timeLeftSeconds, high: current.timeLeftSeconds, learned: true })
    else if (usual) add(spanOf(usual, 1 - Math.min(100, current.percent ?? 0) / 100))
    else unknown++
  }
  for (const id of after) {
    const usual = estimates[id]
    if (usual) add(spanOf(usual))
    else unknown++
  }
  return { ...total, floor: unknown > 0 }
}

function eta({ low, high, learned, floor }: Span & { floor: boolean }, running: boolean) {
  if (high <= 0) return null
  const left = running ? ' left' : ''
  if (floor) return low >= 60 ? `over ${roundSpan(low).span}${left}` : null
  if (!learned) return `${rangeText(low, high)}${left}`
  if (high < 60) return `under a minute${left}`
  return `about ${roundSpan(high).span}${left}`
}

function roundSpan(seconds: number) {
  const minutes = seconds / 60
  const step = minutes < 10 ? 1 : minutes < 60 ? 5 : minutes < 600 ? 10 : 60
  const rounded = Math.max(step, Math.round(minutes / step) * step)
  return { rounded, span: seconds < 60 ? 'under a minute' : minuteText(rounded) }
}

function rangeText(low: number, high: number) {
  if (high < 60) return 'under a minute'
  const minutes = high / 60
  const step = minutes < 10 ? 1 : minutes < 60 ? 5 : minutes < 600 ? 30 : 60
  const top = Math.max(step, Math.round(minutes / step) * step)
  const bottom = Math.round(low / 60 / step) * step
  if (bottom <= 0) return `up to ${minuteText(top)}`
  if (bottom >= top) return `about ${minuteText(top)}`
  if (top < 60) return `${bottom}–${minuteText(top)}`
  return bottom % 60 === 0 && top % 60 === 0 ? `${bottom / 60}–${top / 60}\u00a0h` : `${minuteText(bottom)}–${minuteText(top)}`
}

function minuteText(minutes: number) {
  const h = Math.floor(minutes / 60), m = minutes % 60
  return h ? (m ? `${h}\u00a0h ${m}\u00a0min` : `${h}\u00a0h`) : `${m}\u00a0min`
}

function timeText(estimate: Estimate) {
  const { low, high, learned } = spanOf(estimate)
  const text = !learned ? rangeText(low, high) : high < 60 ? 'under a minute' : `about ${roundSpan(high).span}`
  return text.replaceAll(' ', '\u00a0').replaceAll('–', '\u2060–\u2060')
}

function rank(game: ShaderGame) {
  if (game.tone === 'warn' || game.tone === 'primary') return 1000
  if (game.tone === 'good') return 2000
  return 3000
}

function otherCompile(game: ShaderGame) {
  const text = game.actions?.compileText
  return text && text !== 'Compile' && text.startsWith('Compile') ? text : null
}

function fraction(current: QueueItem) {
  return typeof current.percent === 'number' ? current.percent / 100 : null
}

function posterLine(game: ShaderGame, current: QueueItem | null, place: number | undefined, estimate: Estimate | undefined) {
  if (current) return typeof current.percent === 'number' ? <ProgressWords words="Compiling" value={fraction(current)} /> : current.stageText ?? 'Compiling'
  const time = estimate && (place || game.inAddAllReady) ? timeText(estimate) : null
  if (place) return [place === 1 ? 'Next' : `#${place}`, time].filter(Boolean).join(' · ')
  return time ?? game.statusText ?? ''
}

function usePrepare(shown: boolean) {
  const [items, setItems] = useState<ShaderPrepareGame[]>([])
  const read = useCallback(() => host.shadersPrepareGames()
    .then(next => setItems(next.games))
    .catch((error: unknown) => report(error, 'ShadersBoard.prepare')), [])
  useEffect(() => {
    if (!shown) return
    void read()
    return onHostEvent('shaders.changed', () => { void read() })
  }, [shown, read])
  const waiting = items.filter(item => item.state !== 'prepared' && item.state !== 'none')
  const posters = waiting.map(item => ({ id: `prepare:${item.id}`, name: item.title, installDir: item.installDir ?? undefined }))
  return {
    posters,
    byPoster: new Map(waiting.map(item => [`prepare:${item.id}`, item])),
    without: (others: readonly ShaderGame[]) => {
      const names = new Set(others.map(game => norm(game.name)))
      const dirs = new Set(others.map(game => game.installDir?.toLowerCase()).filter(Boolean))
      return posters.filter(poster => !names.has(norm(poster.name)) && !dirs.has(poster.installDir?.toLowerCase()))
    },
  }
}

function prepareWord(item: ShaderPrepareGame) {
  if (item.state === 'running') return 'Compiling its first launch'
  if (item.state === 'stale') return 'Driver changed'
  return item.message ?? 'First launch'
}

function PreparePoster({ item, cover }: { item: ShaderPrepareGame | undefined; cover: string | null }) {
  const [failed, setFailed] = useState<string | null>(null)
  const [opening, setOpening] = useState(false)
  const running = item?.state === 'running'
  if (opening && (running || item?.state === 'failed')) setOpening(false)
  const run = () => {
    if (!item) return
    setFailed(null)
    if (!running) setOpening(true)
    void (running ? host.shadersPrepareCancel(item.id) : host.shadersPrepareStart(item.id))
      .then(result => { if (!result.ok) { setOpening(false); setFailed(errorText(result.message, 'Could not start')) } })
      .catch((error: unknown) => { report(error, 'ShadersBoard.prepareRun'); setOpening(false); setFailed('Could not start') })
  }
  const ask = useConfirmInPlace<HTMLButtonElement>('Opens the game, then closes it', run)
  if (!item) return null
  return (
    <div className={`exo-shaders-poster is-${running || opening ? 'live' : 'muted'}`}>
      <button ref={ask.ref} type="button" onClick={running ? run : opening ? undefined : ask.press}
        aria-label={running ? `Stop watching ${item.title}` : `Run ${item.title}'s first launch`}>
        <span className="exo-shaders-art">
          <ArtImg src={cover} loading="lazy" decoding="async" draggable={false} fallback={<span className="exo-shaders-art-name">{item.title}</span>} />
        </span>
        <span className="exo-shaders-name">{item.title}</span>
        <span className={`exo-shaders-state${failed ? ' is-bad' : ''}`}>
          {ask.armed ? <ConfirmLabel armed question="Opens the game, then closes it">{null}</ConfirmLabel> : opening ? 'Opening the game' : failed ?? prepareWord(item)}
        </span>
      </button>
    </div>
  )
}

function Poster({ game, cover, current, estimate, place, orderable, drop, error, onCompile, onKey }: {
  game: ShaderGame
  cover: string | null
  current: QueueItem | null
  estimate: Estimate | undefined
  place: number | undefined
  orderable: boolean
  drop: Drop
  error: string | null
  onCompile: () => void
  onKey: (event: KeyboardEvent<HTMLButtonElement>) => void
}) {
  const can = game.queued || game.actions?.compile === true
  const line = posterLine(game, current, place, estimate)
  const tone = current || place ? 'live' : game.tone ?? 'muted'
  const other = can && !game.queued && !current ? otherCompile(game) : null
  return (
    <div className={`exo-shaders-poster is-${tone}${drop === 'lifted' ? ' is-lifted' : ''}`}>
      <button type="button" disabled={!can} onClick={onCompile} onKeyDown={onKey}
        aria-label={game.queued ? `Remove ${game.name} from the queue` : (otherCompile(game) ?? 'Compile').replace('Compile', `Compile ${game.name}`)}
        aria-describedby={orderable ? 'exo-shaders-move-help' : undefined}>
        <span className="exo-shaders-art">
          <ArtImg src={cover} loading="lazy" decoding="async" draggable={false} fallback={<span className="exo-shaders-art-name">{game.name}</span>} />
          {current ? <Progress edge size="small" value={fraction(current)} label={`Compiling ${game.name}`} /> : null}
        </span>
        <span className="exo-shaders-name">{game.name}</span>
        <span className="exo-shaders-state">{line}</span>
        {other ? <span className="exo-shaders-state">{other}</span> : null}
        {error ? <span className="exo-shaders-state is-bad">{error}</span> : null}
      </button>
    </div>
  )
}
