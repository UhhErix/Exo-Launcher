import type { ReactNode } from 'react'
import { ChevronRight, ExternalLink } from '../../brand/icons'
import { ConfirmLabel, useConfirmInPlace } from '../ui/ConfirmButton'
import { WaitWord } from '../ui/Wait'
import { WorkingWord } from '../ui/WorkingWord'

/** A line's state in a word; working: it is being done now (the word with its moving dots). */
export type LineWord = { text: string; good?: boolean; bad?: boolean; working?: boolean }

/**
 * One of the game page's lines (Upscalers, Shaders, Optimize) as a single row, like an iOS Settings row: the
 * name, its state, and a chevron to the PC page that handles it (an arrow out when it opens a web page instead;
 * nothing for a line that is itself the action, as Optimize is). While its state is read, a word-sized skeleton
 * shines in its place (DESIGN.md › Waits).
 */
export function GameLine({ name, word, state, external, action, disabled, confirm, control, onOpen }: {
  name: string
  /** Its state in a word; null while it is read. */
  word: LineWord | null
  /** What the line reads (Shaders: the engine's state), for the E2E run. */
  state?: string
  external?: boolean
  /** The row does the thing itself: no chevron. */
  action?: boolean
  disabled?: boolean
  /** The row asks this in place before it runs (Optimize's Put back). */
  confirm?: string
  /** Its own control at the right edge, beside the row (Optimize's window mode). */
  control?: ReactNode
  /** A line of words under the row, in the state's column (Optimize's "in the game's menu"). */
  onOpen: () => void
}) {
  const ask = useConfirmInPlace<HTMLButtonElement>(confirm ?? '', onOpen)
  const where = action ? null : external ? 'open in browser' : 'open in Exo AI'
  return (
    <section className={control ? 'exo-game-line has-control' : 'exo-game-line'} aria-label={name} data-state={state}>
      <button ref={ask.ref} type="button" className={ask.className ? `exo-game-line-row exo-confirm ${ask.className}` : 'exo-game-line-row'}
        aria-label={[name, ask.armed ? confirm : word?.text, where].filter(Boolean).join(', ')} disabled={disabled} onClick={confirm ? ask.press : onOpen}>
        <span className="exo-game-line-copy">
          <span className="exo-game-line-name">{name}</span>
          <span className={`exo-game-line-word${word?.good ? ' is-good' : ''}${word?.bad ? ' is-bad' : ''}`}>
            {ask.armed && confirm ? <ConfirmLabel armed question={confirm}>{null}</ConfirmLabel>
              : !word ? <WaitWord label={`Checking ${name.toLowerCase()}`} /> : word.working ? <WorkingWord word={word.text} /> : word.text}
          </span>
        </span>
        {action ? null : external ? <ExternalLink size={16} /> : <ChevronRight size={16} />}
      </button>
      {control}
    </section>
  )
}

/**
 * A line that is a reading, not a button (HDR, frame generation): the name and its state in the same places as the
 * lines above (with its moving dots while working), and at the right edge its own control when it has one (HDR's
 * choice, frame generation's switch), or nothing when Exo decides. While it is read (`waiting`), a word-sized skeleton
 * shines in its place, so the line is there from the start and the page never grows.
 */
export function GameReading({ name, word, waiting, children }: { name: string; word: LineWord | null; waiting?: boolean; children?: ReactNode }) {
  return (
    <section className="exo-game-line" aria-label={name}>
      <div className="exo-game-line-row is-reading">
        <span className="exo-game-line-copy">
          <span className="exo-game-line-name">{name}</span>
          <span className={`exo-game-line-word${word?.good ? ' is-good' : ''}${word?.bad ? ' is-bad' : ''}`} role={word?.bad ? 'alert' : undefined}>
            {waiting && !word ? <WaitWord label={`Checking ${name.toLowerCase()}`} /> : word?.working ? <WorkingWord word={word.text} /> : word?.text}
          </span>
        </span>
        {children}
      </div>
    </section>
  )
}
