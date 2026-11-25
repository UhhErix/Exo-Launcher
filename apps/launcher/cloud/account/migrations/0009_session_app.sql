ALTER TABLE sessions ADD COLUMN app TEXT NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS sessions_by_account_device_app ON sessions (account, device, app);
