-- Email and password accounts (README.md). This file HAS been applied to production (accounts.getexo.app) and to the dev
-- database: its SQL is never edited, and a change goes in a new migration (only this comment was corrected). The tables of
-- 0001 and 0002 are renamed legacy_*, not dropped: their rows stay until 0004 drops them, after the checks in README.md.
-- Their indexes keep their old names, so the new tables use names of their own.
ALTER TABLE entry_claims RENAME TO legacy_entry_claims;
ALTER TABLE recovery RENAME TO legacy_recovery;
ALTER TABLE email_codes RENAME TO legacy_email_codes;
ALTER TABLE grants RENAME TO legacy_grants;
ALTER TABLE emails RENAME TO legacy_emails;
ALTER TABLE identities RENAME TO legacy_identities;
ALTER TABLE sessions RENAME TO legacy_sessions;
ALTER TABLE accounts RENAME TO legacy_accounts;

-- An account. email_mac: "id:digest", the address keyed HMAC-SHA256 under the ACCOUNT_SECRETS key named id (no address in
-- clear). password_hash: the password's Argon2id hash, a self-describing PHC string (its salt and parameters inside).
-- recovery_mac: "id:digest" of the recovery
-- code, as email_mac. item_key: the random item key, sealed under the key id it names (1 byte id length, id, then
-- nonce || ciphertext). seq: the last change number given to an item of this account.
CREATE TABLE accounts (
  id TEXT PRIMARY KEY,
  email_mac TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,
  recovery_mac TEXT NOT NULL,
  item_key BLOB NOT NULL,
  seq INTEGER NOT NULL DEFAULT 0,
  created INTEGER NOT NULL
);

-- A signed-in PC: the token as SHA-256 hex, one per account and device. last_used moves at most once a day.
CREATE TABLE sessions (
  token_hash TEXT PRIMARY KEY,
  account TEXT NOT NULL REFERENCES accounts (id),
  device TEXT NOT NULL,
  created INTEGER NOT NULL,
  last_used INTEGER NOT NULL
);
CREATE INDEX sessions_by_account ON sessions (account, device);

-- Wrong tries, counted under a key of their own: 'p|<address digest>|<client address digest>' (10 in 15 minutes locks
-- that pair) and 'a|<address digest>' (100 in an hour slows its wrong answers). window_start opens the count;
-- locked_until is when a lock ends (0 when none); pending is the tries admitted and not yet answered, so that concurrent
-- tries count against the limit before they are checked. No address is kept in clear: the digests are keyed.
CREATE TABLE sign_in_failures (
  scope TEXT PRIMARY KEY,
  failures INTEGER NOT NULL,
  window_start INTEGER NOT NULL,
  locked_until INTEGER NOT NULL,
  pending INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX sign_in_failures_window ON sign_in_failures (window_start);

-- Synced items, keyed by app, type and id. version: the account's change number when the item was last written (the
-- server orders writes; a client sends the version it saw). updated: the client's own clock, kept as metadata only.
-- written: the server's time (ms). value: the item's JSON as sealed text (AES-256-GCM under the account's item key),
-- NULL for a deletion (a tombstone keeps its version, so a stale write still loses).
CREATE TABLE items (
  account TEXT NOT NULL REFERENCES accounts (id),
  app TEXT NOT NULL,
  type TEXT NOT NULL,
  id TEXT NOT NULL,
  version INTEGER NOT NULL,
  updated INTEGER NOT NULL,
  written INTEGER NOT NULL,
  value BLOB,
  PRIMARY KEY (account, app, type, id)
);
CREATE INDEX items_version ON items (account, version);
