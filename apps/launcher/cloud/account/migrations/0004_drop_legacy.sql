-- Drops the 0001 and 0002 tables that 0003 renamed to legacy_*. Checked before this ran on production: every legacy_*
-- table had 0 rows. Children go before their parent (legacy_accounts) so the foreign keys never block a drop. IF EXISTS
-- makes a re-run after a partial apply harmless.
DROP TABLE IF EXISTS legacy_entry_claims;
DROP TABLE IF EXISTS legacy_recovery;
DROP TABLE IF EXISTS legacy_email_codes;
DROP TABLE IF EXISTS legacy_grants;
DROP TABLE IF EXISTS legacy_emails;
DROP TABLE IF EXISTS legacy_identities;
DROP TABLE IF EXISTS legacy_sessions;
DROP TABLE IF EXISTS legacy_accounts;
