import { lazy, memo, Suspense, useEffect, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { startDiscordShell } from '../../lib/discordPrefs'

// The call's capsule and mini player bring Discord's voice code with them: loaded once a call is on, not with Exo.
const ExoIslandCall = lazy(() => import('./ExoIslandCall').then(m => ({ default: m.ExoIslandCall })))

/** Whether a Discord call has been on since Exo started (its state now, then each change); once it has, it stays true. */
function useCallSeen(): boolean {
  const [seen, setSeen] = useState(false)
  useEffect(() => {
    if (seen) return
    let live = true
    const take = (state: string | undefined) => { if (live && state && state !== 'off') setSeen(true) }
    host.discordVoice('state').then(result => take(result.voice?.state), () => { })
    const off = onHostEvent('discord.voice', next => take(next.state))
    return () => { live = false; off() }
  }, [seen])
  return seen
}

/**
 * The Exo Island (DESIGN.md › The Exo Island): the capsule left of the Rooms, only while something is live that its room
 * isn't showing: a Discord call outside the Discord room, and the stream you watch in it as a mini player. Downloads show
 * in the Library; the song has its own capsule right of the Rooms. Nothing live: nothing drawn. What Discord does
 * wherever you are (Camera On and Off, Toggle Streamer Mode) starts with Exo; the call's own code comes with the first call.
 */
export const ExoIsland = memo(function ExoIsland(props: { inDiscord: boolean; onOpenDiscord: () => void }) {
  useEffect(startDiscordShell, [])
  const call = useCallSeen()
  return call ? <Suspense fallback={null}><ExoIslandCall {...props} /></Suspense> : null
})
