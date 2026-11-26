import '../../styles/music-room.css'
import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { rememberSignedIn, wasSignedIn } from '../../lib/music'
import { onPageActivityChanged, pageIsVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'
import { appleAdapter, spotifyAdapter, youtubeAdapter } from '../../lib/musicAdapters'
import type { PlayerRoomState } from '../../lib/player'
import { MusicPlayer } from './MusicPlayer'
import { ChangeMusicService } from './ChangeMusicService'
import { MusicSignIn } from './MusicSignIn'
import { MusicWait } from './MusicWait'
import { ErrorState } from '../ui/ErrorState'
import { errorText } from '../../lib/errorWords'

const adapters = { spotify: spotifyAdapter, youtube: youtubeAdapter, apple: appleAdapter }

// Cache-first: an engine that is still starting can report signed out for a moment. If this
// service was signed in last time, the room keeps "Opening" that long instead of flashing sign-in.
const SIGNED_IN_GRACE_MS = 15000

/**
 * A service in Exo's own player. Its engine (the Spotify, YouTube Music or Apple Music
 * page) runs hidden; signed out, Exo's sign-in screen starts the service's own sign-in.
 */
export function NativeMusicRoom({ service, active, shown, nowPlaying }: {
  service: keyof typeof adapters
  /** On screen with nothing over it. */
  active: boolean
  /** Counts the mini player's presses: each opens Now playing. */
  nowPlaying: number
  /** The room is the one open, a dialog over it or not: the engine is wanted. */
  shown: boolean
}) {
  const adapter = adapters[service]
  const [state, setState] = useState<PlayerRoomState>('loading')
  const [message, setMessage] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  // Signed out: Exo's sign-in screen. Its button starts the service's own sign-in in its own window.
  const [signingIn, setSigningIn] = useState(false)
  const [signInMessage, setSignInMessage] = useState<string | null>(null)
  const [signInStatus, setSignInStatus] = useState<string | null>(null)

  async function signIn() {
    setSigningIn(true)
    setSignInMessage(null)
    setSignInStatus(null)
    try {
      const result = await adapter.signIn()
      if (!result.ok) {
        const message = errorText(result.message, `${adapter.name} could not start sign-in. Try again.`)
        report(message, 'NativeMusicRoom.signIn')
        setSignInMessage(message)
      }
      else setSignInStatus(result.message ?? `Finish signing in to ${adapter.name} in the window that opened.`)
    } catch (error) {
      report(error, 'NativeMusicRoom.signIn')
      setSignInMessage(`${adapter.name} could not start sign-in. Try again.`)
    } finally {
      setSigningIn(false)
    }
  }

  // The engine starts with the room: once when the room is preloaded behind the splash (so Home
  // is there the first time Music opens), then each time it shows. Left, the room hands it back
  // (cleanup) so it can sleep; a hidden room never asks for it again. A dialog over the room
  // (Ctrl+K) is not leaving it.
  const warmed = useRef(false)
  useEffect(() => {
    if (!shown && warmed.current) return
    warmed.current = true
    let live = true
    let timer = 0
    let offShown: (() => void) | null = null
    let tries = 0
    const started = Date.now()
    // The next look, a while from now. Waiting on the user (signed out, no Premium), it waits behind a game or minimized
    // until Exo is on screen again: a room left on the sign-in screen asked the service every 3 s for as long as a game
    // ran. Opening (bounded, and how Home is ready before the room first shows) never waits.
    const later = (ms: number, user: boolean) => {
      timer = window.setTimeout(() => {
        if (!user || pageIsVisible()) { void check(); return }
        offShown = onPageActivityChanged(() => {
          if (!pageIsVisible()) return
          offShown?.()
          offShown = null
          void check()
        })
      }, ms)
    }
    const check = async () => {
      try {
        const raw = await adapter.start()
        if (!live) return
        const settling = raw.state === 'signedOut' && wasSignedIn(service) && Date.now() - started < SIGNED_IN_GRACE_MS
        const result = settling ? { ...raw, state: 'loading' as const } : raw
        if (result.state === 'running') rememberSignedIn(service, true)
        else if (result.state === 'signedOut') rememberSignedIn(service, false)
        // A player already on screen stays (with its page) while the engine wakes from a nap: only
        // signed out or an error replace it.
        setState(previous => previous === 'running' && result.state === 'loading' ? previous : result.state)
        setMessage(result.message ?? null)
        // Opening, signing in or upgrading: look again until the engine is ready.
        if (result.state === 'loading' || result.state === 'signedOut' || result.state === 'notPremium') {
          // Only opening gives up; the sign-in screen waits for the user.
          if (result.state === 'loading' && ++tries > 120) {
            setState(previous => previous === 'running' ? previous : 'error')
            setMessage(`${adapter.name} did not open. Try again.`)
            return
          }
          later(result.state === 'loading' ? 1000 : 3000, result.state !== 'loading')
        }
      } catch (error) {
        report(error, 'NativeMusicRoom.start')
        if (live) { setState('error'); setMessage(`${adapter.name} could not open. Try again.`) }
      }
    }
    void check()
    return () => {
      live = false
      window.clearTimeout(timer)
      offShown?.()
      void adapter.leave().catch(() => { })
    }
  }, [adapter, service, shown, attempt])

  // The engine can end under a room that is playing (a page crashed). A room on screen starts
  // it again, on a short leash: twice a minute at most, then it says so instead of fighting someone who closed it on purpose.
  const stateRef = useRef(state)
  stateRef.current = state
  const restarts = useRef<number[]>([])
  useEffect(() => onHostEvent('music.status', data => {
    const status = data as { service?: string; state?: string } | null
    if (!shown || stateRef.current !== 'running' || status?.service !== service) return
    if (status.state !== 'error') return
    const now = Date.now()
    restarts.current = restarts.current.filter(at => now - at < 60_000)
    if (restarts.current.length >= 2) {
      setState('error')
      setMessage(`${adapter.name} stopped. Open it again.`)
      return
    }
    restarts.current.push(now)
    setState('loading')
    setAttempt(n => n + 1)
  }), [adapter, service, shown])

  if (state === 'signedOut' || state === 'notPremium') {
    // Without the subscription: Spotify's button opens Premium; the web services' signs in with another account.
    const upgrade = state === 'notPremium' ? adapter.premiumUrl : undefined
    return <MusicSignIn service={service} name={adapter.name} busy={signingIn} note={message} message={signInMessage} status={message ? null : signInStatus}
      action={upgrade ? `Get ${adapter.name} Premium` : undefined} onSignIn={() => void (upgrade ? host.openUrl(upgrade) : signIn())} />
  }
  if (state === 'error') {
    return (
      <main className="exo-hosted-room">
        <ErrorState title={`${adapter.name} unavailable`} message={`Exo could not open ${adapter.name}.`} log={message ?? `${adapter.name} could not open.`} onRetry={() => setAttempt(n => n + 1)}>
          <ChangeMusicService />
        </ErrorState>
      </main>
    )
  }
  // Opening: the room's own frame, shining, the same one the player lies under until its first page is in.
  if (state !== 'running') return <MusicWait name={adapter.name} />
  return <MusicPlayer key={service} adapter={adapter} service={service} active={active} nowPlaying={nowPlaying} />
}
