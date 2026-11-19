-- Crash reports by install (README.md, "Crash reports"). One row per stored report that carried an installId; the delete
-- endpoint removes those reports' R2 objects through it. Rows are pruned after 30 days, the same as the R2 objects.

CREATE TABLE crash_index (
  install_id TEXT NOT NULL,        -- 8-4-4-4-12 lower-case, the telemetry install ID
  r2_key TEXT NOT NULL,            -- reports/<UTC date>/<id>.json, the report's object in R2
  received_day TEXT NOT NULL,      -- UTC day the report was stored (YYYY-MM-DD); the 30-day prune counts from it
  PRIMARY KEY (install_id, r2_key)
);

CREATE INDEX crash_index_day ON crash_index (received_day);
