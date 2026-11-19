-- Exo account (README.md). Times are Unix seconds. No row holds an email address in clear: an address is found by its
-- HMAC-SHA256 under a key derived from ACCOUNT_SECRET, and the one copy that passes back to the app (in a grant or an
-- email code, until it is used or expires) is sealed under that grant or ticket, which only the app holds.

-- An account: 16 random bytes as hex.
CREATE TABLE accounts (
  id TEXT PRIMARY KEY,
  created INTEGER NOT NULL
);

-- Each way into an account. google or apple: subject is the ID token's sub. email: subject is the address's email_mac.
CREATE TABLE identities (
  provider TEXT NOT NULL,
  subject TEXT NOT NULL,
  account TEXT NOT NULL REFERENCES accounts (id),
  created INTEGER NOT NULL,
  PRIMARY KEY (provider, subject)
);
CREATE INDEX identities_account ON identities (account);

-- The verified addresses that lead to an account, as HMAC-SHA256 hex. One account per address.
CREATE TABLE emails (
  email_mac TEXT PRIMARY KEY,
  account TEXT NOT NULL REFERENCES accounts (id)
);
CREATE INDEX emails_account ON emails (account);

-- A signed-in PC: the token as SHA-256 hex, one per account and device (a 32-hex Exo device id). last_used moves at
-- most once a day.
CREATE TABLE sessions (
  token_hash TEXT PRIMARY KEY,
  account TEXT NOT NULL REFERENCES accounts (id),
  device TEXT NOT NULL,
  created INTEGER NOT NULL,
  last_used INTEGER NOT NULL
);
CREATE INDEX sessions_account ON sessions (account, device);
CREATE INDEX sessions_last_used ON sessions (last_used);

-- A provider sign-in waiting to be swapped, with its PKCE verifier, for a session: SHA-256 of the grant, once, within
-- 2 minutes. email: the ID token's address sealed under a key derived from the grant (AES-256-GCM, nonce || ciphertext),
-- or NULL when the token had none. created: 1 when this sign-in made the account.
CREATE TABLE grants (
  grant_hash TEXT PRIMARY KEY,
  account TEXT NOT NULL REFERENCES accounts (id),
  challenge TEXT NOT NULL,
  email BLOB,
  provider TEXT NOT NULL,
  created INTEGER NOT NULL,
  expires INTEGER NOT NULL
);
CREATE INDEX grants_account ON grants (account);
CREATE INDEX grants_expires ON grants (expires);

-- An emailed 6-digit code: SHA-256 of the ticket, the code as an HMAC bound to the ticket, the address sealed under a
-- key derived from the ticket. Used once, 5 tries, 10 minutes; the address goes at expiry and the row after a day,
-- which is how long it counts against the address's sends.
CREATE TABLE email_codes (
  ticket_hash TEXT PRIMARY KEY,
  email_mac TEXT NOT NULL,
  code_mac TEXT NOT NULL,
  email BLOB,
  sent INTEGER NOT NULL,
  expires INTEGER NOT NULL,
  tries INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX email_codes_address ON email_codes (email_mac, sent);
CREATE INDEX email_codes_sent ON email_codes (sent);
CREATE INDEX email_codes_expires ON email_codes (expires);

-- The identity's recovery code, AES-256-GCM sealed (nonce || ciphertext) under HKDF-SHA256(ACCOUNT_SECRET, salt the
-- account id, info "exo-account/recovery v1"), the account id as associated data. The Worker can open it.
CREATE TABLE recovery (
  account TEXT PRIMARY KEY REFERENCES accounts (id),
  sealed BLOB NOT NULL,
  updated INTEGER NOT NULL
);
