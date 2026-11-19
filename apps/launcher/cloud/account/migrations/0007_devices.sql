-- The devices an account has signed in from. token_hash: SHA-256 of the device's token (32 random bytes, given to the client
-- once, README.md › Limits); prev_token_hash: the token before it, kept once more in case its answer was lost. last_seen
-- moves with the device's sessions: a device unseen for idleSeconds is no longer known. A row is deleted when its last
-- session goes (sign-out, revoke, eviction) and with a password change or recovery reset (the others). No backfill: no token
-- exists for a session made before this, so the device's next sign-in issues one.
CREATE TABLE IF NOT EXISTS devices (
  account TEXT NOT NULL REFERENCES accounts (id),
  device TEXT NOT NULL,
  token_hash TEXT NOT NULL,
  prev_token_hash TEXT,
  first_seen INTEGER NOT NULL,
  last_seen INTEGER NOT NULL,
  PRIMARY KEY (account, device)
);
