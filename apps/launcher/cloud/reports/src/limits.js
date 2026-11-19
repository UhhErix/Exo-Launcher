/**
 * The limits (README.md › Limits). One object so the tests can check them without writing thousands of reports; nothing
 * else changes it.
 */
export const limits = {
  // The whole request body. The excerpt alone is at most 8192 characters: 24 KiB of UTF-8 at the very worst, plus the rest.
  bodyBytes: 32 * 1024,
  excerptChars: 8192,
  // Keys in R2 are reports/<UTC date>/<random id>.json; the date prefix is what the 30-day lifecycle rule expires.
  prefix: 'reports/',
  keepDays: 30,
}

const DAY_MS = 24 * 60 * 60 * 1000

/**
 * The telemetry limits (README.md › Telemetry). Same rule as above: one object, changed nowhere else.
 */
export const telemetryLimits = {
  // One request holds at most 64 KB (the apps split bigger batches, specs 5.5).
  bodyBytes: 64 * 1024,
  maxEvents: 500,
  // Props: at most 32 keys, strings at most 256 characters.
  maxProps: 32,
  maxStringChars: 256,
  // An event whose whole envelope is over 1 KB is dropped whole (spec 5.4), not refused.
  maxEventBytes: 1024,
  // ts must be within 7 days of the server's clock.
  skewMs: 7 * DAY_MS,
  // Raw events are kept 90 days; the daily job writes aggregates for each day it removes.
  keepDays: 90,
  // Best-effort cap per install per hour, in code (counted in D1 by received_at). Over it: 429.
  eventsPerInstallHour: 500,
  // A daily run removes at most this many days; more than one day only happens after a missed run.
  retentionDaysPerRun: 31,
  // The delete body is an install ID and two optional fields.
  deleteBodyBytes: 1024,
}
