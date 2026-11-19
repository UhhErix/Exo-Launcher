import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent, type CompressAction, type CompressList, type CompressSettings, type CompressState } from '../../lib/host'
import { report } from '../../lib/report'
import { formatSize } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { Row, State, SwitchRow } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'
import { CompressActions, compressWords, stopped } from '../game/CompressRow'

const SPEEDS = [
  { value: 'gentle', label: 'Gentle' },
  { value: 'balanced', label: 'Balanced' },
  { value: 'fast', label: 'Fast' },
] as const

/** Every game's compression, read when the room opens and kept current by compress.progress. */
export function useCompressList(active: boolean) {
  const [list, setList] = useState<CompressList | null>(null)
  const read = useCallback(() => {
    host.compressList().then(setList, (e: unknown) => report(e, 'CompressGames.list', { level: 'warn' }))
  }, [])
  useEffect(() => { if (active) read() }, [active, read])
  useEffect(() => onHostEvent('compress.progress', ({ id, compress }) => setList(current => current && {
    ...current,
    games: current.games.map(game => game.id === id ? { ...game, compress } : game),
  })), [])
  return { list, read, setList }
}

/** The chip: what compression could still save, else what it saved; nothing before the list is read or with neither. */
export function CompressChip({ list, onOpen }: { list: CompressList | null; onOpen: () => void }) {
  if (!list || (list.potential <= 0 && list.saved <= 0)) return null
  return (
    <button type="button" className="exo-pc-chip" data-pc-menu="compress" aria-haspopup="dialog" onClick={onOpen}>
      {list.potential > 0 ? `Compress · ${formatSize(list.potential)}` : `Saved ${formatSize(list.saved)}`}
    </button>
  )
}

/**
 * PC › Compress games: Windows' own file compression for the games worth it (games read it as before), what it saved,
 * Compress all, the two idle switches and how much of the processor it may use, then each game with its own control.
 */
export function CompressGames({ list, onList, onRead }: { list: CompressList | null; onList: (list: CompressList) => void; onRead: () => void }) {
  const [error, setError] = useState<string | null>(null)
  if (!list) return <Row label="Compress games" detail="Reading your games" />
  const settings = list.settings
  const save = async (patch: Partial<CompressSettings>) => {
    try {
      const answer = await host.compressSettings(patch)
      onList({ ...list, settings: answer.settings })
      return null
    } catch (e) {
      report(e, 'CompressGames.settings')
      return 'Not saved. Try again.'
    }
  }
  const all = () => {
    setError(null)
    host.compressAll().then(answer => { if (answer.message) setError(answer.message); onRead() },
      (e: unknown) => { report(e, 'CompressGames.all'); setError('Exo couldn’t start. Try again.') })
  }
  const act = (id: string, action: CompressAction, compress: CompressState) => {
    host.compressAct(id, action, action === 'allow' ? compress.antiCheat ?? undefined : undefined).then(answer => {
      if (answer.compress) onList({ ...list, games: list.games.map(game => game.id === id ? { ...game, compress: answer.compress! } : game) })
      if (!answer.ok && answer.message) setError(answer.message)
    }, (e: unknown) => report(e, 'CompressGames.act'))
  }
  const shown = list.games.filter(game => game.compress.state !== 'refused')
  return (
    <div className="exo-pc-menu-rows">
      <Row label="Saved" detail={list.potential > 0 ? `${formatSize(list.potential)} more to save` : undefined} error={error}>
        <State on={list.saved > 0}>{formatSize(list.saved)}</State>
        {list.potential > 0 ? (
          <ConfirmButton className="exo-set-btn" question={`Compress ${list.games.filter(g => g.eligible).length} games?`} onConfirm={all}>Compress all</ConfirmButton>
        ) : null}
      </Row>
      <SwitchRow id="compress-idle" label="Compress while idle" checked={settings.whileIdle} onChange={value => save({ whileIdle: value })} />
      <SwitchRow id="compress-again" label="Compress again after updates" checked={settings.afterUpdates} onChange={value => save({ afterUpdates: value })} />
      <Row label="Speed">
        <Choice label="Speed" value={settings.speed} choices={SPEEDS} onChange={value => void save({ speed: value as CompressSettings['speed'] })} />
      </Row>
      {shown.map(game => (
        <Row key={game.id} label={game.title} detail={compressWords(game.compress)} error={stopped(game.compress)}>
          <CompressActions compress={game.compress} locked={false} act={action => act(game.id, action, game.compress)} />
        </Row>
      ))}
    </div>
  )
}
