import { telemetryLimits as L } from './limits.js'

/**
 * The telemetry tables in D1 (`exo-telemetry`, migrations/0002_telemetry.sql). Every statement here touches one table and
 * takes its values as bound parameters. Nothing stores an IP address, a user agent or a request header: a row is the
 * envelope's fields and the scrubbed props.
 */

const HOUR_MS = 60 * 60 * 1000
const DAY_MS = 24 * HOUR_MS

/** The UTC day (YYYY-MM-DD) of an epoch-ms time: the day a row is received on, and the 90-day rule counts from. */
export const utcDay = (ms) => new Date(ms).toISOString().slice(0, 10)

// One statement for the whole batch: the rows go in as one JSON array, so a 500-event batch is one query.
const INSERT_EVENTS = `INSERT INTO events (received_day, received_at, app, version, channel, install_id, session_id, ts, event, props_json)
  SELECT ?, ?, json_extract(value, '$.app'), json_extract(value, '$.version'), json_extract(value, '$.channel'),
    json_extract(value, '$.install_id'), json_extract(value, '$.session_id'), json_extract(value, '$.ts'),
    json_extract(value, '$.event'), json_extract(value, '$.props_json')
  FROM json_each(?)`

/** Stores accepted rows (from parseBatch) as received at `now`. */
export async function insertEvents(db, rows, now) {
  await db.prepare(INSERT_EVENTS).bind(utcDay(now), now, JSON.stringify(rows)).run()
}

/** How many events this install sent in the last hour (the per-install cap, in code). */
export async function installEventsSince(db, installId, sinceMs) {
  const row = await db.prepare('SELECT COUNT(*) AS n FROM events WHERE install_id = ? AND received_at >= ?').bind(installId, sinceMs).first()
  return row?.n ?? 0
}

/** Deletes every raw event of one install. Aggregates have no install ID, so they stay. */
export async function deleteInstall(db, installId) {
  await db.prepare('DELETE FROM events WHERE install_id = ?').bind(installId).run()
}

/**
 * The daily job: for each UTC day older than keepDays, writes that day's aggregates (events and distinct installs per app,
 * version and event) and deletes its raw rows, in one batch so a failure leaves neither half done. Returns the days removed.
 */
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
