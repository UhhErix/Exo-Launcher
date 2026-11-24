CREATE TABLE IF NOT EXISTS passkeys (
  credential_id TEXT PRIMARY KEY,
  account TEXT NOT NULL REFERENCES accounts (id),
  public_key_spki BLOB NOT NULL,
  alg INTEGER NOT NULL,
  sign_count INTEGER NOT NULL DEFAULT 0,
  sign_count_flagged INTEGER NOT NULL DEFAULT 0,
  label TEXT NOT NULL DEFAULT '',
  created INTEGER NOT NULL,
  last_used INTEGER NOT NULL,
  UNIQUE (account, credential_id)
);
CREATE INDEX IF NOT EXISTS passkeys_by_account ON passkeys (account);

CREATE TABLE IF NOT EXISTS webauthn_challenges (
  challenge_hash TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  account TEXT,
  expires INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS webauthn_challenges_expires ON webauthn_challenges (expires);
