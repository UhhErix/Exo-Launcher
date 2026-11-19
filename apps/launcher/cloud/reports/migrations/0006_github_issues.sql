-- Issues in the private UhhErix/Exo-Reports repository (README.md › Issues, src/issues.js). They replace the alert mails and
-- the Discord posts once the secret GITHUB_REPORTS_TOKEN is set; until then those still go, so their daily tally
-- (alert_days, 0005) stays.

-- One row per error group (src/fingerprint.js): its GitHub issue and what the issue's "Seen N times" block says. No install
-- ID and no time finer than the day.
CREATE TABLE crash_groups (
  fingerprint TEXT PRIMARY KEY,       -- 16 hex: app, error type and the normalised top frames
  app TEXT NOT NULL,
  error_type TEXT NOT NULL,
  issue_number INTEGER,               -- null until the issue is filed
  count INTEGER NOT NULL DEFAULT 0,   -- reports of the group, every version
  synced_count INTEGER NOT NULL DEFAULT 0, -- the count the issue shows
  versions TEXT NOT NULL DEFAULT '[]', -- JSON array, oldest first, at most 20
  newest_version TEXT NOT NULL,
  synced_version TEXT,                -- the newest version the issue shows
  closed_version TEXT,                -- the newest version the issue showed when it was found closed
  noted_threshold INTEGER NOT NULL DEFAULT 0, -- the highest of 10/100/1000 commented on
  first_day TEXT NOT NULL,            -- UTC day (YYYY-MM-DD)
  last_day TEXT NOT NULL,
  os_build TEXT NOT NULL,             -- the first report's Windows build
  sample_key TEXT NOT NULL,           -- R2 key of the first report (its excerpt goes in the issue)
  pending INTEGER NOT NULL DEFAULT 1  -- 1 while GitHub has not been told the latest (the daily job retries)
);

CREATE INDEX crash_groups_pending ON crash_groups (pending, last_day);

-- Reports from Report a problem whose issue could not be filed yet (no token, GitHub down, the day's cap): the daily job
-- files them. The report itself is in R2.
CREATE TABLE issue_queue (
  ref TEXT PRIMARY KEY,               -- the reference the app showed (8 hex)
  r2_key TEXT NOT NULL,
  day TEXT NOT NULL,
  tries INTEGER NOT NULL DEFAULT 0
);

-- GitHub writes per UTC day and kind (src/issues.js claim): a sender inventing errors or versions cannot open issues or
-- edit them without end. A count per day and nothing else; the daily job removes days older than 90.
CREATE TABLE issue_days (
  day TEXT NOT NULL,
  kind TEXT NOT NULL,                 -- 'crash' (new group issues), 'user' (Report a problem), 'sync' (edits and comments)
  used INTEGER NOT NULL,
  PRIMARY KEY (day, kind)
);
