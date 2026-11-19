-- The caps (README.md › Limits) read each account's item count and sealed bytes from its row, not by counting its items.
-- Order matters. The columns come first (a partial apply that stops here leaves the old Worker's writes untouched). Then
-- the triggers on items keep both counts right for every write, edit, tombstone and delete, inside the same statement that
-- changes the row. Then the counts are recounted from the rows in full: a write from the old Worker that lands while this
-- migration runs is counted by a trigger, and the recount is absolute, so no drift remains.
-- ADD COLUMN has no IF NOT EXISTS in SQLite: if a partial apply left these columns, README.md's recovery steps apply.
ALTER TABLE accounts ADD COLUMN item_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE accounts ADD COLUMN item_bytes INTEGER NOT NULL DEFAULT 0;

CREATE TRIGGER IF NOT EXISTS items_counted_insert AFTER INSERT ON items BEGIN
  UPDATE accounts SET item_count = item_count + 1, item_bytes = item_bytes + COALESCE(length(NEW.value), 0) WHERE id = NEW.account;
END;

CREATE TRIGGER IF NOT EXISTS items_counted_update AFTER UPDATE OF value ON items BEGIN
  UPDATE accounts SET item_bytes = item_bytes + COALESCE(length(NEW.value), 0) - COALESCE(length(OLD.value), 0) WHERE id = NEW.account;
END;

CREATE TRIGGER IF NOT EXISTS items_counted_delete AFTER DELETE ON items BEGIN
  UPDATE accounts SET item_count = item_count - 1, item_bytes = item_bytes - COALESCE(length(OLD.value), 0) WHERE id = OLD.account;
END;

UPDATE accounts SET
  item_count = (SELECT count(*) FROM items WHERE items.account = accounts.id),
  item_bytes = (SELECT COALESCE(SUM(length(value)), 0) FROM items WHERE items.account = accounts.id);
