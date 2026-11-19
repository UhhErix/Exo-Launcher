-- Triage's alert mails per UTC day (src/triage.js claimAlert): a sender inventing error types or versions cannot turn every
-- report into a mail. A count per day and nothing else; the daily job removes days older than 90.
CREATE TABLE IF NOT EXISTS alert_days (
  day TEXT PRIMARY KEY,            -- UTC day (YYYY-MM-DD)
  sent INTEGER NOT NULL
);
