import { useEffect, useState } from 'react'
import { accountSettings } from './discordPrefs'
import type { DiscordVoiceNow } from './discord'
import { host, onHostEvent } from './host'
import { onPageActivityChanged, pageIsVisible } from './pageActivity'
import { report } from './report'

/** Reads now, then once per burst of what `listen` hears, and only while on screen: out of sight it waits for the way back. */
function useLiveRead<T>(ask: () => Promise<T>, empty: T, wait: number, listen: (soon: () => void) => () => void, key = ''): T {
  const [value, setValue] = useState<T>(empty)
  useEffect(() => {
    let live = true, stale = false, timer = 0
    const read = () => {
      void ask().then(next => { if (live) setValue(current => JSON.stringify(current) === JSON.stringify(next) ? current : next) },
        (error: unknown) => report(error, 'discordNow.read', { level: 'warn' }))
    }
    read()
    const soon = () => {
      if (!pageIsVisible()) { stale = true; return }
      if (!timer) timer = window.setTimeout(() => { timer = 0; read() }, wait)
    }
    const offHeard = listen(soon)
    const offPage = onPageActivityChanged(() => { if (stale && pageIsVisible()) { stale = false; read() } })
    return () => { live = false; window.clearTimeout(timer); offHeard(); offPage() }
  // ask, empty and listen are the module's own constants below.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, wait])
  return value
}

const NO_ROOMS: DiscordVoiceNow[] = []
const askRooms = () => host.discordVoiceNow().then(result => result.rooms)
const hearVoice = (soon: () => void) => onHostEvent('discord.changed', event => { if (event.voice && event.guildId) soon() })

/**
 * Who is in voice across your servers, from what the connection holds. A busy server sends a voice change every moment
 * and most of them are nobody you can see from here, so it reads at most once a second, and only while its page shows.
 */
export const useVoiceNow = () => useLiveRead(askRooms, NO_ROOMS, 1000, hearVoice)



/** The clock as Discord settings › Language & Time has it: Auto, 12-hour or 24-hour. */
export function clockOf(date: Date) {
  const cycle = accountSettings().timestampHourCycle
  return date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', ...(cycle === 1 ? { hour12: true } : cycle === 2 ? { hourCycle: 'h23' as const } : {}) })
}

/** When, on a row: the clock today, Yesterday, the weekday this week, else the date. Absolute, as Mail and Notes have it, so a long list needs no timer. */
export function rowTime(at: number, now = Date.now()) {
  const date = new Date(at)
  const today = new Date(now).setHours(0, 0, 0, 0)
  const days = Math.round((today - new Date(at).setHours(0, 0, 0, 0)) / 86_400_000)
  if (days <= 0) return clockOf(date)
  if (days === 1) return 'Yesterday'
  if (days < 7) return date.toLocaleDateString(undefined, { weekday: 'short' })
  return date.toLocaleDateString(undefined, date.getFullYear() === new Date(now).getFullYear() ? { month: 'short', day: 'numeric' } : { year: 'numeric', month: 'short', day: 'numeric' })
}

/** How long since: "12 min", "1 hr 5 min". */
export function sinceWords(from: number, now: number) {
  const minutes = Math.max(1, Math.floor((now - from) / 60_000))
  if (minutes < 60) return `${minutes} min`
  const rest = minutes % 60
  return `${Math.floor(minutes / 60)} hr${rest ? ` ${rest} min` : ''}`
}
