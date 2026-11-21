import { limits } from './limits.js'
import { utcDay } from './telemetry-store.js'

const DAY_MS = 24 * 60 * 60 * 1000
const R2_DELETE_MAX = 1000

export async function addCrashIndex(db, installId, r2Key, day) {
  await db.prepare('INSERT INTO crash_index (install_id, r2_key, received_day) VALUES (?, ?, ?)').bind(installId, r2Key, day).run()
}

export async function removeCrashIndex(db, installId, r2Key) {
  await db.prepare('DELETE FROM crash_index WHERE install_id = ? AND r2_key = ?').bind(installId, r2Key).run()
}

export async function deleteCrashReports(db, bucket, installId) {
  const { results } = await db.prepare('SELECT r2_key FROM crash_index WHERE install_id = ?').bind(installId).all()
  const keys = results.map((row) => row.r2_key)
  for (let at = 0; at < keys.length; at += R2_DELETE_MAX) await bucket.delete(keys.slice(at, at + R2_DELETE_MAX))
  await db.prepare('DELETE FROM crash_index WHERE install_id = ?').bind(installId).run()
  return keys.length
}

export async function pruneCrashIndex(db, now) {
  const cutoff = utcDay(now - limits.keepDays * DAY_MS)
  await db.prepare('DELETE FROM crash_index WHERE received_day < ?').bind(cutoff).run()
}
