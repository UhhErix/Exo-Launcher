import { limits } from './limits.js'
import { utcDay } from './telemetry-store.js'

/**
 * The crash index in D1 (`exo-telemetry`, migrations/0003_crash_index.sql): which R2 report objects came from which install.
 * Every statement takes its values as bound parameters. A row is the install ID, the object's key and the day; nothing else.
 */

const DAY_MS = 24 * 60 * 60 * 1000
// R2's delete takes at most this many keys per call.
const R2_DELETE_MAX = 1000

/** Records one stored report under its install. */
export async function addCrashIndex(db, installId, r2Key, day) {
  await db.prepare('INSERT INTO crash_index (install_id, r2_key, received_day) VALUES (?, ?, ?)').bind(installId, r2Key, day).run()
}

/** Removes a report's index row after its object was deleted (or never stored). */
export async function removeCrashIndex(db, installId, r2Key) {
  await db.prepare('DELETE FROM crash_index WHERE install_id = ? AND r2_key = ?').bind(installId, r2Key).run()
}

/**
 * Deletes every crash report object of one install, then its index rows. The objects go first, so a failure leaves the
 * index in place and a retry finds them again (deleting a missing object is harmless). Returns the number of objects listed.
 */
export async function deleteCrashReports(db, bucket, installId) {
  const { results } = await db.prepare('SELECT r2_key FROM crash_index WHERE install_id = ?').bind(installId).all()
  const keys = results.map((row) => row.r2_key)
  for (let at = 0; at < keys.length; at += R2_DELETE_MAX) await bucket.delete(keys.slice(at, at + R2_DELETE_MAX))
  await db.prepare('DELETE FROM crash_index WHERE install_id = ?').bind(installId).run()
  return keys.length
}

/** The daily job: index rows older than the R2 lifecycle (30 days) go too, since their objects have expired. */
export async function pruneCrashIndex(db, now) {
  const cutoff = utcDay(now - limits.keepDays * DAY_MS)
  await db.prepare('DELETE FROM crash_index WHERE received_day < ?').bind(cutoff).run()
}
