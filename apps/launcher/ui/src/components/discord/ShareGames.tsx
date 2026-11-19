import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'

/** One event for both switches: turning Share my games off changes what Share when I'm getting ready reads. */
const CHANGED = 'exo-dc-share-changed'
type Answer = { ok: boolean; on: boolean; message?: string | null }

/**
 * A per-account sharing switch kept by the host: read when it mounts, again when another instance of the switch changes it
 * (`changed`) or another account signs in, and set with the host's answer winning over the press.
 */
function useSharingSwitch(ask: (on?: boolean) => Promise<Answer>, changed: string, area: string) {
  const [chosen, setChosen] = useState<boolean | null>(null)
  const read = useCallback(() => {
    ask().then(result => setChosen(result.on), (error: unknown) => report(error, `${area}.read`, { level: 'warn' }))
  }, [ask, area])
  useEffect(() => {
    read()
    window.addEventListener(changed, read)
    // Another account signed in has its own choice.
    const off = onHostEvent('discord.state', event => { if (event.state === 'ready') read() })
    return () => { window.removeEventListener(changed, read); off() }
  }, [read, changed])
  const set = useCallback(async (on: boolean): Promise<string | null> => {
    setChosen(on)
    try {
      const result = await ask(on)
      setChosen(result.on)
      window.dispatchEvent(new Event(changed))
      return result.ok ? null : result.message ?? "Discord didn't take that."
    } catch (error) {
      report(error, `${area}.set`)
      read()
      return "Discord didn't take that."
    }
  }, [ask, changed, area, read])
  return { ready: chosen !== null, on: chosen === true, set }
}

const askGames = (on?: boolean) => host.discordShareGames(on)
const askReady = (on?: boolean) => host.discordShareReady(on)

/**
 * Share my games for the account signed in (DiscordSession.Games.cs): off until the account turns it on. It tells
 * friends' Exo apps over Exo's relay, never Discord. Settings holds its switch.
 */
export function useShareGames() {
  return useSharingSwitch(askGames, CHANGED, 'ShareGames')
}

/**
 * Share when I'm getting ready (DiscordSession.Prep.cs): off until the account turns it on, and reads off while Share my games
 * is, since it rides the same link. Friends' Exo apps see a game installing, building its shaders or ready. Settings holds its switch.
 */
export function useShareReady() {
  return useSharingSwitch(askReady, CHANGED, 'ShareReady')
}
