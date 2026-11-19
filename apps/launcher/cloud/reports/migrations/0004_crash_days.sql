-- Triage (README.md, "Triage"): how many crash reports of each error type an app's version sent on each UTC day. No install
-- ID, no time finer than the day, no excerpt: a count, and the key of the newest report of the group (for a sample).
CREATE TABLE crash_days (
  app TEXT NOT NULL,
  version TEXT NOT NULL,
  error_type TEXT NOT NULL,
  day TEXT NOT NULL,               -- UTC day (YYYY-MM-DD)
  count INTEGER NOT NULL,
  sample_key TEXT NOT NULL,        -- R2 key of the newest report of this group that day
  PRIMARY KEY (app, version, error_type, day)
);

CREATE INDEX crash_days_day ON crash_days (day);
