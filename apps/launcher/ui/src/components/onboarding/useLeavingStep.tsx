import { useCallback, useEffect, useState, type RefObject } from 'react'

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

export function LeavingStep({ html }: { html: string | null }) {
  if (html === null) return null
  return <div className="exo-ob-leaving" aria-hidden="true" inert dangerouslySetInnerHTML={{ __html: html }} />
}
