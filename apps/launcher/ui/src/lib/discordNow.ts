import { accountSettings } from './discordPrefs'

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

