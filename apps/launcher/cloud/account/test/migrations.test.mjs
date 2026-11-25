/**
 * The migrations (README.md › Before deploy): 0003 rewrites the account tables for passwords, and must not destroy what
 * 0001 and 0002 left. It renames the old tables legacy_* and keeps their rows; a later migration drops them after a
 * checked export.
 *
 * How it could fail: the rename drops or empties an old table; a legacy row becomes unreadable; the new tables clash
 * with an old index name and the migration stops half way.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'
import { test } from 'node:test'

const migration = name => readFileSync(new URL(`../migrations/${name}`, import.meta.url), 'utf8')

test('0003 keeps the 0001 and 0002 tables as legacy_* with their rows, and creates the password tables', () => {
  const db = new DatabaseSync(':memory:', { enableForeignKeyConstraints: true })
  db.exec(migration('0001_init.sql'))
  db.exec(migration('0002_entry_claims.sql'))
  db.exec("INSERT INTO accounts (id, created) VALUES ('old-account', 1)")
  db.exec("INSERT INTO sessions (token_hash, account, device, created, last_used) VALUES ('h', 'old-account', 'd', 1, 1)")
  db.exec(migration('0003_passwords.sql'))
  const tables = db.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").all().map(r => r.name)
  for (const table of ['legacy_accounts', 'legacy_sessions', 'legacy_identities', 'legacy_recovery', 'legacy_entry_claims']) assert.ok(tables.includes(table), table)
  for (const table of ['accounts', 'sessions', 'sign_in_failures', 'items']) assert.ok(tables.includes(table), table)
  assert.deepEqual(db.prepare('SELECT id FROM legacy_accounts').all().map(r => r.id), ['old-account'])
  assert.deepEqual(db.prepare('SELECT token_hash FROM legacy_sessions').all().map(r => r.token_hash), ['h'])
  assert.equal(db.prepare('SELECT count(*) AS n FROM accounts').get().n, 0)
})

test('0003 adds the pending count the try reservation needs', () => {
  const db = new DatabaseSync(':memory:', { enableForeignKeyConstraints: true })
  db.exec(migration('0001_init.sql'))
  db.exec(migration('0002_entry_claims.sql'))
  db.exec(migration('0003_passwords.sql'))
  const columns = db.prepare('PRAGMA table_info(sign_in_failures)').all().map(c => c.name)
  assert.ok(columns.includes('pending'))
})

const migrated = names => {
  const db = new DatabaseSync(':memory:', { enableForeignKeyConstraints: true })
  for (const name of names) db.exec(migration(name))
  return db
}
const BEFORE_0004 = ['0001_init.sql', '0002_entry_claims.sql', '0003_passwords.sql']

test('0004 drops the legacy_* tables and only those: the account tables and their rows stay', () => {
  const db = migrated(BEFORE_0004)
  db.exec("INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES ('a', 'k1:x', 'h', 'k1:y', X'00', 1)")
  db.exec(migration('0004_drop_legacy.sql'))
  const tables = db.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").all().map(r => r.name)
  assert.deepEqual(tables.filter(t => t.startsWith('legacy_')), [])
  for (const table of ['accounts', 'sessions', 'sign_in_failures', 'items']) assert.ok(tables.includes(table), table)
  assert.deepEqual(db.prepare('SELECT id FROM accounts').all().map(r => r.id), ['a'])
})

test('0005 counts each account\'s items and bytes from its rows, and keeps the counts as items change', () => {
  const db = migrated([...BEFORE_0004, '0004_drop_legacy.sql'])
  db.exec("INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES ('a', 'k1:x', 'h', 'k1:y', X'00', 1)")
  db.exec("INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES ('b', 'k1:z', 'h', 'k1:y', X'00', 1)")
  db.exec("INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES ('a', 'l', 't', '1', 1, 0, 0, X'010203'), ('a', 'l', 't', '2', 2, 0, 0, NULL), ('b', 'l', 't', '1', 1, 0, 0, X'0102')")
  db.exec(migration('0005_counters.sql'))
  const counts = () => db.prepare('SELECT id, item_count, item_bytes FROM accounts ORDER BY id').all().map(r => [r.id, r.item_count, r.item_bytes])
  assert.deepEqual(counts(), [['a', 2, 3], ['b', 1, 2]])
  db.exec("INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES ('a', 'l', 't', '3', 3, 0, 0, X'0102030405')")
  assert.deepEqual(counts(), [['a', 3, 8], ['b', 1, 2]])
  db.exec("UPDATE items SET value = X'01' WHERE account = 'a' AND id = '3'")
  assert.deepEqual(counts(), [['a', 3, 4], ['b', 1, 2]])
  db.exec("UPDATE items SET value = NULL WHERE account = 'a' AND id = '1'")
  db.exec("DELETE FROM items WHERE account = 'b'")
  assert.deepEqual(counts(), [['a', 3, 1], ['b', 0, 0]])
})

test('0006 adds the keyring check values and the age of the pending slots', () => {
  const db = migrated([...BEFORE_0004, '0004_drop_legacy.sql', '0005_counters.sql', '0006_keyring_and_pending.sql'])
  const columns = db.prepare('PRAGMA table_info(sign_in_failures)').all().map(c => c.name)
  assert.ok(columns.includes('pending_at'))
  assert.deepEqual(db.prepare('PRAGMA table_info(keyring)').all().map(c => c.name), ['key_id', 'check_mac'])
})

test('0007 adds devices with the hash of each device token, and starts empty: no token exists for a session made before it', () => {
  const db = migrated([...BEFORE_0004, '0004_drop_legacy.sql', '0005_counters.sql', '0006_keyring_and_pending.sql'])
  db.exec("INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES ('a', 'k1:x', 'h', 'k1:y', X'00', 1)")
  db.exec("INSERT INTO sessions (token_hash, account, device, created, last_used) VALUES ('h1', 'a', 'pc', 5, 5)")
  db.exec(migration('0007_devices.sql'))
  assert.deepEqual(db.prepare('PRAGMA table_info(devices)').all().map(c => c.name), ['account', 'device', 'token_hash', 'prev_token_hash', 'first_seen', 'last_seen'])
  assert.equal(db.prepare('SELECT count(*) AS n FROM devices').get().n, 0)
})
test('0008 adds identities and grants without disturbing existing accounts or sessions', () => {
  const db = migrated([...BEFORE_0004, '0004_drop_legacy.sql', '0005_counters.sql', '0006_keyring_and_pending.sql', '0007_devices.sql'])
  db.exec("INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES ('a', 'k1:x', 'h', 'k1:y', X'00', 1)")
  db.exec(migration('0008_identities.sql'))
  assert.deepEqual(
    db.prepare('PRAGMA table_info(identities)').all().map(c => c.name),
    ['provider', 'subject_mac', 'account', 'refresh', 'created'],
  )
  assert.deepEqual(
    db.prepare('PRAGMA table_info(grants)').all().map(c => c.name),
    ['grant_hash', 'purpose', 'provider', 'subject_mac', 'refresh', 'challenge', 'account', 'expires'],
  )
  assert.deepEqual(db.prepare('SELECT id FROM accounts').all().map(r => r.id), ['a'])
})

test('0010 adds passkeys and webauthn_challenges without disturbing existing accounts or identities', () => {
  const db = migrated([...BEFORE_0004, '0004_drop_legacy.sql', '0005_counters.sql', '0006_keyring_and_pending.sql', '0007_devices.sql', '0008_identities.sql'])
  db.exec("INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES ('a', 'k1:x', 'h', 'k1:y', X'00', 1)")
  db.exec("INSERT INTO identities (provider, subject_mac, account, refresh, created) VALUES ('google', 'k1:sub', 'a', NULL, 1)")
  db.exec(migration('0010_passkeys.sql'))
  assert.deepEqual(
    db.prepare('PRAGMA table_info(passkeys)').all().map(c => c.name),
    ['credential_id', 'account', 'public_key_spki', 'alg', 'sign_count', 'sign_count_flagged', 'label', 'created', 'last_used'],
  )
  assert.deepEqual(
    db.prepare('PRAGMA table_info(webauthn_challenges)').all().map(c => c.name),
    ['challenge_hash', 'kind', 'account', 'expires'],
  )
  assert.deepEqual(db.prepare('SELECT id FROM accounts').all().map(r => r.id), ['a'])
  assert.deepEqual(db.prepare('SELECT provider, account FROM identities').all().map(r => [r.provider, r.account]), [['google', 'a']])
})

test('0011 adds app column to sessions with empty default without disturbing existing sessions', () => {
  const db = migrated([...BEFORE_0004, '0004_drop_legacy.sql', '0005_counters.sql', '0006_keyring_and_pending.sql', '0007_devices.sql', '0008_identities.sql', '0010_passkeys.sql'])
  db.exec("INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES ('a', 'k1:x', 'h', 'k1:y', X'00', 1)")
  db.exec("INSERT INTO sessions (token_hash, account, device, created, last_used) VALUES ('h1', 'a', 'pc', 5, 5)")
  db.exec(migration('0011_session_app.sql'))
  assert.ok(db.prepare('PRAGMA table_info(sessions)').all().map(c => c.name).includes('app'))
  assert.deepEqual(db.prepare('SELECT token_hash, app FROM sessions').all().map(r => [r.token_hash, r.app]), [['h1', '']])
})
