import { useEffect, useRef, useState } from 'react'
import { JamBox } from './JamBox'
import { VoicePanel, type VoiceControls } from './voice'

/** How long a call's voice row stays on screen while it closes: the panel's motion (system.css › the foot), so the rows shrink as they go. */
const CLOSE_MS = 220

/** A call in progress and the Jam box: the rows at the foot of the rail and sidebar, above your user row. Nothing on, nothing drawn.
    callIds: who is in your call. A call's row holds on for the motion as it ends, then goes. */
export function CallDock({ voice, voicePlace, callIds, onOpenVoice }: {
  voice: VoiceControls
  voicePlace: string | null
  callIds: readonly string[]
  onOpenVoice: () => void
}) {
  const live = voice.voice.state !== 'off' || !!voice.error
  const held = useRef<VoiceControls | null>(null)
  const [closing, setClosing] = useState(false)
  if (live) held.current = voice
  useEffect(() => {
    if (live) { setClosing(false); return }
    if (!held.current) return
    // Reduce motion: nothing moves, so the row goes at once.
    const reduce = document.documentElement.dataset.exoMotion === 'reduce'
    if (reduce) { held.current = null; return }
    setClosing(true)
    const timer = window.setTimeout(() => { setClosing(false); held.current = null }, CLOSE_MS)
    return () => window.clearTimeout(timer)
  }, [live])
  const shown = live ? voice : closing ? held.current : null
  return (
    <div className={closing ? 'exo-dc-dock is-closing' : 'exo-dc-dock'}>
      <JamBox callIds={callIds} />
      {shown ? (
        <VoicePanel controls={shown} place={voicePlace} onOpen={onOpenVoice} />
      ) : null}
    </div>
  )
}
