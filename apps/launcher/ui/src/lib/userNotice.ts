export function isUserNotice(message: string | null | undefined): boolean {
  const text = message?.trim()
  if (!text) return false
  const lower = text.toLowerCase()
  if (lower.includes('is running, so the update waits')) return true
  if (lower.startsWith('close ') && lower.includes(' first')) return true
  if (lower.startsWith('leave your discord call first')) return true
  return false
}
