import { useCallback, useEffect, useState, type RefObject } from 'react'

/** The step being left, kept for 120 ms so it fades and rises before the next step arrives (SPEC-v2). */
export function useLeavingStep(stageRef: RefObject<HTMLElement | null>) {
  const [leaving, setLeaving] = useState<string | null>(null)
  useEffect(() => {
    if (leaving === null) return
    const timer = window.setTimeout(() => setLeaving(null), 120)
    return () => window.clearTimeout(timer)
  }, [leaving])
  const leave = useCallback(() => setLeaving(stageRef.current?.querySelector('.exo-ob-step')?.outerHTML ?? null), [stageRef])
  return { leaving, leave }
}

/** The outgoing copy: its own markup, inert, under the incoming step for the 120 ms it takes to fade. */
export function LeavingStep({ html }: { html: string | null }) {
  if (html === null) return null
  return <div className="exo-ob-leaving" aria-hidden="true" inert dangerouslySetInnerHTML={{ __html: html }} />
}
