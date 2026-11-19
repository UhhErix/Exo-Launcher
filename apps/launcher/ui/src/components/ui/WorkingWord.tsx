import { ProgressWords, wholePercent } from './Progress'

/**
 * A word for work at hand, such as a game starting: "Preparing" with its three dots taking turns, in place of a bar or a
 * spinner that would claim a progress nobody knows. Once the work has a measure (`value`, 0 to 1), the word says how far
 * instead, live ("Downloading · 54%"). Still dots under reduced motion (styles/progress.css).
 */
export function WorkingWord({ word, value }: { word: string; value?: number | null }) {
  if (wholePercent(value) !== null) return <span className="exo-working"><ProgressWords words={word} value={value} /></span>
  return (
    <span className="exo-working">
      {word}
      <span className="exo-working-dots" aria-hidden="true"><span>.</span><span>.</span><span>.</span></span>
    </span>
  )
}
