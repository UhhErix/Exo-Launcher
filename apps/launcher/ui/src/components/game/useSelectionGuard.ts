import { useEffect, useRef } from 'react'

/** Ignore late UI callbacks once their game has changed or the panel has closed. */
export function useSelectionGuard(key: string) {
  // The key on screen, set after each commit (never during render) and cleared when it changes or the panel closes.
  const current = useRef<string | null>(null)
  useEffect(() => {
    current.current = key
    return () => { current.current = null }
  }, [key])
  return <T,>(callback: (value: T) => void) => (value: T) => {
    if (current.current === key) callback(value)
  }
}
