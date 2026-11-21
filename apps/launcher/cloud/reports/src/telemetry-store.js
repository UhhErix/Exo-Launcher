import { telemetryLimits as L } from './limits.js'

const HOUR_MS = 60 * 60 * 1000
const DAY_MS = 24 * HOUR_MS

export const utcDay = (ms) => new Date(ms).toISOString().slice(0, 10)

const INSERT_EVENTS = `INSERT INTO events (received_day, received_at, app, version, channel, install_id, session_id, ts, event, props_json)
  SELECT ?, ?, json_extract(value, '$.app'), json_extract(value, '$.version'), json_extract(value, '$.channel'),
    json_extract(value, '$.install_id'), json_extract(value, '$.session_id'), json_extract(value, '$.ts'),
    json_extract(value, '$.event'), json_extract(value, '$.props_json')
  FROM json_each(?)`

export async function insertEvents(db, rows, now) {
  await db.prepare(INSERT_EVENTS).bind(utcDay(now), now, JSON.stringify(rows)).run()
}

export async function installEventsSince(db, installId, sinceMs) {
  const row = await db.prepare('SELECT COUNT(*) AS n FROM events WHERE install_id = ? AND received_at >= ?').bind(installId, sinceMs).first()
  return row?.n ?? 0
}

export async function deleteInstall(db, installId) {
  await db.prepare('DELETE FROM events WHERE install_id = ?').bind(installId).run()
}

export async function runRetention(db, now) {
  const cutoff = utcDay(now - L.keepDays * DAY_MS)
  const { results } = await db
    .prepare('SELECT DISTINCT received_day AS day FROM events WHERE received_day < ? ORDER BY received_day LIMIT ?')
    .bind(cutoff, L.retentionDaysPerRun)
    .all()
  for (const { day } of results) {
    await db.batch([
      db
        .prepare(
          `INSERT INTO aggregates (day, app, version, event, count, installs)
           SELECT received_day, app, version, event, COUNT(*), COUNT(DISTINCT install_id)
           FROM events WHERE received_day = ? GROUP BY received_day, app, version, event`,
        )
        .bind(day),
      db.prepare('DELETE FROM events WHERE received_day = ?').bind(day),
    ])
  }
  return results.length
}
