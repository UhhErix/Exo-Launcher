CREATE TABLE IF NOT EXISTS identities (
  provider TEXT NOT NULL,
  subject_mac TEXT NOT NULL,
  account TEXT NOT NULL REFERENCES accounts (id),
  refresh BLOB,
  created INTEGER NOT NULL,
  PRIMARY KEY (provider, subject_mac)
);
CREATE UNIQUE INDEX IF NOT EXISTS identities_by_account ON identities (account, provider);

CREATE TABLE IF NOT EXISTS grants (
  grant_hash TEXT PRIMARY KEY,
  purpose TEXT NOT NULL,
  provider TEXT NOT NULL,
  subject_mac TEXT NOT NULL,
  refresh BLOB,
  challenge TEXT,
  account TEXT,
  expires INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS grants_by_expiry ON grants (expires);
