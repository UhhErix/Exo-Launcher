import { useCallback, useEffect, useRef, useState } from 'react'
import { isTourDue, TOUR_EVENT, TOUR_VERSION } from '../../lib/tour'
import { onUiPrefs, setUiPref, uiPref } from '../../lib/uiPrefs'

export function useExoTour(
  prefsLoaded: boolean,
  onboardingComplete: boolean | undefined,
  busy: readonly boolean[],
): {
  open: boolean
  step: number
  setStep: (next: number) => void
  close: () => void
} {
  const [open, setOpen] = useState(false)
  const [step, setStep] = useState(0)
  const [seen, setSeen] = useState<unknown>(() => uiPref('tourSeen'))
  const autoShown = useRef(false)
  const free = prefsLoaded && onboardingComplete === true && !busy.includes(true)

  useEffect(() => {
    setSeen(uiPref('tourSeen'))
    return onUiPrefs(() => setSeen(uiPref('tourSeen')))
  }, [])

  useEffect(() => {
    if (!free || autoShown.current) return
    const search = typeof location !== 'undefined' ? location.search : ''
    if (!isTourDue(seen, search)) return
    const timer = window.setTimeout(() => {
      if (autoShown.current) return
      if (document.querySelector('[aria-modal="true"]:not([hidden], [hidden] *, [inert] *)')) return
      autoShown.current = true
      setStep(0)
      setOpen(true)
    }, 750)
    return () => window.clearTimeout(timer)
  }, [free, seen])

  useEffect(() => {
    const onStart = () => {
      autoShown.current = true
      setStep(0)
      setOpen(true)
    }
    window.addEventListener(TOUR_EVENT, onStart)
    return () => window.removeEventListener(TOUR_EVENT, onStart)
  }, [])

  const close = useCallback(() => {
    setOpen(false)
    setUiPref('tourSeen', TOUR_VERSION)
  }, [])

  return { open, step, setStep, close }
}
