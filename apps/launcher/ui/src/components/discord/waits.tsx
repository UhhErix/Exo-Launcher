import type { CSSProperties, ReactNode } from 'react'
import { Row } from '../settings/parts'
import { Wait, sample, times } from '../ui/Wait'

/** A person's picture on its way, at the size it will be. */
export const sampleFace = (size: number) => <span className="exo-dc-avatar-wrap" style={{ width: size, height: size, borderRadius: '50%' }} skullet-block="" />

/** Messages on their way, in a message's own markup (Chat, pins, mentions, search, saved). */
export function MessagesWait({ label, rows = 4 }: { label: string; rows?: number }) {
  return (
    <Wait label={label}>
      {times(rows, i => (
        <div className="exo-dc-msg">
          <div className="exo-dc-msg-row">
            <span className="exo-dc-avatar exo-dc-msg-face" style={{ width: 36, height: 36, borderRadius: '50%' }} skullet-block="" />
            <div className="exo-dc-msg-body">
              <p className="exo-dc-msg-head"><strong>{sample(i)}</strong><span className="exo-dc-msg-time">Today at 8:40 PM</span></p>
              <div>{sample(i + 1)} {sample(i + 3)} {sample(i + 4)}</div>
            </div>
          </div>
        </div>
      ))}
    </Wait>
  )
}

/** A settings page's rows on their way, each with the control it will have. */
export function RowsWait({ label, rows = 4, control = 'Edit' }: { label: string; rows?: number; control?: ReactNode }) {
  return <Wait label={label}>{times(rows, i => <Row label={sample(i)} detail={sample(i + 2)}><span className="exo-set-btn">{control}</span></Row>)}</Wait>
}

/** A moderation list's people on their way: a picture over a name and a username. */
export function PeopleWait({ label, rows = 4 }: { label: string; rows?: number }) {
  return (
    <Wait label={label}>
      <ul className="exo-dc-mod-list">
        {times(rows, i => <li>{sampleFace(32)}<span className="exo-dc-mod-name"><span>{sample(i)}</span><small>{sample(i + 2)}</small></span></li>)}
      </ul>
    </Wait>
  )
}

/** A table's rows on their way: a person, then its columns' words. */
export function TableWait({ label, rows = 8, columns, className }: { label: string; rows?: number; columns: string[]; className?: string }) {
  return (
    <Wait label={label}>
      <div className={className ? `exo-dc-table ${className}` : 'exo-dc-table'}>
        <div className="exo-dc-table-row is-head">{columns.map(column => <span key={column}>{column}</span>)}</div>
        {times(rows, i => (
          <div className="exo-dc-table-row">
            <span className="exo-dc-table-name">{sampleFace(32)}<span className="exo-dc-friend-names"><strong>{sample(i)}</strong><span>{sample(i + 2)}</span></span></span>
            {columns.slice(1).map(column => <span key={column}>{column}</span>)}
          </div>
        ))}
      </div>
    </Wait>
  )
}

/** A profile on its way: its banner, its picture, the name and two lines. */
export function ProfileWait({ label, size, tall }: { label: string; size: number; tall?: boolean }) {
  return (
    <Wait label={label}>
      <div className={`exo-dc-pf-banner${tall ? ' is-tall' : ''}`} skullet-block="" />
      <div className="exo-dc-pf-head">{sampleFace(size)}</div>
      <div className="exo-dc-pf-body">
        <div className="exo-dc-pf-who"><span className="exo-dc-pf-name-row"><strong className="exo-dc-pf-name">{sample(0)}</strong></span></div>
        <p>{sample(1)}</p>
        <p>{sample(4)}</p>
      </div>
    </Wait>
  )
}

/** Home on its way: its place cards, each with its title, its art, the faces in it and its foot. */
export function LobbyWait({ label }: { label: string }) {
  return (
    <div className="exo-dc-lobby-scroll">
      <Wait label={label}>
        <div className="exo-dc-lobby">
          {[4, 2, 2, 2, 1, 1, 3, 2].map((people, at) => (
            <div key={at} className="exo-dc-place-block" style={{ '--people': people } as CSSProperties}>
              <section className="exo-dc-lobby-place">
                <header className="exo-dc-lobby-head"><span className="exo-dc-lobby-title"><h2>{sample(at)}</h2><p className="exo-dc-now-live">{sample(at + 2)}</p></span></header>
                <ul className="exo-dc-lobby-faces">{times(people, i => <li><button type="button" className="exo-dc-lobby-face">{sampleFace(56)}<span>{sample(i + 5)}</span></button></li>)}</ul>
                <footer className="exo-dc-lobby-foot"><span className="exo-dc-lobby-facts">{sample(at + 1)}</span></footer>
              </section>
            </div>
          ))}
        </div>
      </Wait>
    </div>
  )
}
