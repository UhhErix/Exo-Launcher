-- Telemetry for exo-telemetry (README.md, "Telemetry"). The draft tables raw and aggregates were empty and unused
-- (checked 2026-11-06 before this migration), so they are replaced. d1_migrations already holds 0001_init.sql, the draft.

DROP INDEX IF EXISTS raw_received_day;
DROP INDEX IF EXISTS aggregates_day;
DROP TABLE IF EXISTS raw;
DROP TABLE IF EXISTS aggregates;

-- One row per accepted event. No IP address, user agent or request header is stored.
CREATE TABLE events (
  id INTEGER PRIMARY KEY,
  received_day TEXT NOT NULL,      -- UTC day the Worker received it (YYYY-MM-DD); the 90-day rule counts from it
  received_at INTEGER NOT NULL,    -- epoch ms; the per-install hourly cap reads it
  app TEXT NOT NULL,
  version TEXT NOT NULL,
  channel TEXT NOT NULL,
  install_id TEXT NOT NULL,        -- 8-4-4-4-12 lower-case; the delete endpoint removes by it
  session_id TEXT NOT NULL,
  ts TEXT NOT NULL,                -- the client's clock, ISO 8601 with milliseconds (checked to within 7 days)
  event TEXT NOT NULL,
  props_json TEXT NOT NULL         -- the scrubbed props, a flat JSON object
);

CREATE INDEX events_app_day ON events (app, received_day);
CREATE INDEX events_install ON events (install_id, received_at);
CREATE INDEX events_day ON events (received_day);

-- Daily aggregates written by the retention job before raw rows are removed. No install ID: the distinct count is all that stays.
CREATE TABLE aggregates (
  day TEXT NOT NULL,
  app TEXT NOT NULL,
  version TEXT NOT NULL,
  event TEXT NOT NULL,
  count INTEGER NOT NULL,
  installs INTEGER NOT NULL,
  PRIMARY KEY (day, app, version, event)
);
