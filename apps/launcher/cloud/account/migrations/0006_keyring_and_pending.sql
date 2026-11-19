-- The keyring's check values: HMAC-SHA256 of a constant under each key of ACCOUNT_SECRETS, stored the first time the key
-- is seen, after an item key sealed under it has opened with that secret (accounts.js, proveKeyId). A configured key whose
-- value differs from the stored one is refused (a server error), so a wrong secret under a known id can't make a second,
-- silent set of accounts.
CREATE TABLE IF NOT EXISTS keyring (
  key_id TEXT PRIMARY KEY,
  check_mac TEXT NOT NULL
);

-- When the pending count of a row was last reserved. A pending count older than 5 minutes is a try that never answered
-- (a D1 error between the reservation and its answer), and is treated as none.
ALTER TABLE sign_in_failures ADD COLUMN pending_at INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS sign_in_failures_pending_at ON sign_in_failures (pending_at);
