/**
 * A backup's day (yyyy-MM-dd, UTC, as its file is named) in words for the page: "Today" on today's day, else "Nov 8, 2026".
 * Unreadable input is shown as it came.
 */
export function backupDayLabel(day: string, now: Date = new Date()): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day)
  if (!match) return day
  if (day === now.toISOString().slice(0, 10)) return 'Today'
  const [, year, month, date] = match
  return new Date(Number(year), Number(month) - 1, Number(date)).toLocaleDateString('en-US', { dateStyle: 'medium' })
}
