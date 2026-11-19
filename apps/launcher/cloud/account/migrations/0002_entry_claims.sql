-- The account entry's adds, one per head (plan §2.17): two PCs signing in at once would each add itself after the same
-- change of the device list, an equivocation every PC drops. The first to claim a head gets it; the other takes that add
-- and signs again on top. Only hashes come here, never a change. A claim stands 2 minutes.
CREATE TABLE entry_claims (
  account TEXT NOT NULL REFERENCES accounts (id),
  prev TEXT NOT NULL,
  hash TEXT NOT NULL,
  created INTEGER NOT NULL,
  PRIMARY KEY (account, prev)
);
CREATE INDEX entry_claims_created ON entry_claims (created);
