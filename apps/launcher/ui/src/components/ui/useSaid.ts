import { useEffect, useRef, useState } from 'react'

/**
 * A press's result said by its own control (DESIGN.md › Notify): `say('Copied')` swaps the button's words for 2 s, then
 * its own come back. For a press whose control stays where it was; one in a menu that closes says it in the stack.
 */
export function useSaid(ms = 2000): [string | null, (words: string) => void] {
  const [said, setSaid] = useState<string | null>(null)
  const timer = useRef(0)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  const say = (words: string) => {
    window.clearTimeout(timer.current)
    setSaid(words)
    timer.current = window.setTimeout(() => setSaid(null), ms)
  }
  return [said, say]
}
