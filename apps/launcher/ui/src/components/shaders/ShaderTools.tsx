import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Group, Row, Section } from '../settings/parts'
import { call, message, type Snapshot } from './shaderCall'

/** What a game's last launch says a compile did (game.diagnostics). */
type Diagnostics = {
  creates: number
  tupleKnown: number
  knownMedianMs?: number | null
  verdict: 'helped' | 'partly' | 'missed' | 'noData'
  needsFirstLaunch?: boolean
}

const VERDICTS: Record<Diagnostics['verdict'], string | null> = {
  helped: 'Compile helped',
  partly: 'Compile helped partly',
  missed: 'Compile didn’t help',
  noData: null,
}

function verdictLine(data: Diagnostics) {
  const verdict = data.needsFirstLaunch ? 'Needs its first launch' : VERDICTS[data.verdict]
  if (!verdict || !data.creates) return verdict
  return `${verdict} · ${Math.round(data.tupleKnown / data.creates * 100)}% known`
}

/**
 * Each game: its own recorder switch and, once it has been played, what its compile did. Only read once asked, since each
 * read asks the engine, which starts it.
 */
function GameRows({ games, run }: { games: Snapshot['games']; run: (method: string, params?: unknown) => void }) {
  const [results, setResults] = useState<Record<string, Diagnostics | null>>({})
  const key = games.map(game => game.id).join('|')
  useEffect(() => {
    let live = true
    void (async () => {
      for (const id of key.split('|').filter(Boolean)) {
        const data = await call('game.diagnostics', { id }).then(reply => reply as Diagnostics, (error: unknown) => {
          report(error, 'ShaderTools.diagnostics', { level: 'warn' })
          return null
        })
        if (!live) return
        setResults(previous => ({ ...previous, [id]: data }))
      }
    })()
    return () => { live = false }
  }, [key])
  return games.map(game => {
    const data = results[game.id]
    return (
      <Row key={game.id} label={game.name} detail={game.recorder?.refusal ?? (data ? verdictLine(data) : null)}>
        {game.recorder?.canSet ? <Switch checked={!!game.recorder.on} label={`Record ${game.name}`} onChange={on => run('recorder.set', { id: game.id, on, takeOver: on })} /> : null}
      </Row>
    )
  })
}

/**
 * Settings › Library › Shaders: what the Shaders page runs by itself, opted into here. Recording puts a small d3d12.dll in a
 * game's folder, which is why it asks; the engine refuses games with anti-cheat.
 */
export function ShadersSection() {
  const [snap, setSnap] = useState<Snapshot | null>(null)
  const [auto, setAuto] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [games, setGames] = useState(false)
  const read = useCallback(() => call('snapshot').then(next => setSnap(next as Snapshot | null)).catch((caught: unknown) => report(caught, 'ShaderTools.snapshot', { level: 'warn' })), [])
  useEffect(() => {
    void read()
    void host.shadersPrepareAuto().then(next => setAuto(next.auto)).catch((caught: unknown) => report(caught, 'ShaderTools.prepare'))
    return onHostEvent('shaders.event', ({ name, data }) => {
      if (name === 'state') setSnap(data as Snapshot)
      else if (name === 'addon' || name === 'ready') void read()
    })
  }, [read])
  const run = (method: string, params?: unknown) => {
    setError(null)
    void call(method, params).then(read, (caught: unknown) => {
      report(caught, `ShaderTools.${method}`, { level: 'warn' })
      setError(message(caught, 'The shader engine did not answer.'))
    })
  }
  return (
    <Section id="shaders" title="Shaders">
      <Row label="First launches while you’re away">
        <Switch checked={auto} label="First launches while you’re away" onChange={on => {
          setAuto(on)
          void host.shadersPrepareAuto(on).catch((caught: unknown) => report(caught, 'ShaderTools.auto'))
        }} />
      </Row>
      <Row label="Record games to improve compiles" detail="Adds a small d3d12.dll to each game’s folder" error={error}>
        <Switch checked={!!snap?.recorder?.auto} label="Record games to improve compiles" onChange={on => run('recorder.auto', { on })} />
      </Row>
      <Row label="Games">
        <button type="button" className="exo-set-btn" aria-expanded={games} onClick={() => setGames(open => !open)}>{games ? 'Hide' : 'Show'}</button>
      </Row>
      {games && snap ? <Group id="shaders-games" title="Recording and how compiles did"><GameRows games={snap.games} run={run} /></Group> : null}
    </Section>
  )
}
