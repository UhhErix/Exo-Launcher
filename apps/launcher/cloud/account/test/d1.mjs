/**
 * A stand-in for Cloudflare D1 over Node's own SQLite (node:sqlite), for the tests and local/server.mjs: prepare, bind,
 * first, all, run, and batch as one transaction, in memory with ../migrations applied. Like D1 it refuses undefined,
 * stores booleans as 0 or 1, takes a BLOB as an ArrayBuffer or a view and gives it back as an array of numbers, and
 * enforces foreign keys.
 */
import { readFileSync, readdirSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'

const migrations = new URL('../migrations/', import.meta.url)

function value(v) {
  if (v === undefined) throw new TypeError('D1_TYPE_ERROR: undefined is not a D1 value')
  if (typeof v === 'boolean') return v ? 1 : 0
  if (v instanceof ArrayBuffer) return new Uint8Array(v)
  if (ArrayBuffer.isView(v)) return new Uint8Array(v.buffer, v.byteOffset, v.byteLength)
  return v
}

const plain = row => Object.fromEntries(Object.entries(row).map(([k, v]) => [k, v instanceof Uint8Array ? Array.from(v) : v]))

class Statement {
  constructor(db, sql, args = []) {
    this.db = db
    this.sql = sql
    this.args = args
  }

  bind(...args) {
    return new Statement(this.db, this.sql, args.map(value))
  }

  /** Runs it whole, as D1 does even for first(): a RETURNING statement makes every change before its rows come back. */
  execute() {
    const statement = this.db.prepare(this.sql)
    if (!statement.columns().length) {
      const { changes, lastInsertRowid } = statement.run(...this.args)
      return { success: true, results: [], meta: { changes: Number(changes), last_row_id: Number(lastInsertRowid) } }
    }
    const results = statement.all(...this.args).map(plain)
    return { success: true, results, meta: { changes: results.length } }
  }

  async first(column) {
    const row = this.execute().results[0]
    if (row === undefined) return null
    return column ? row[column] : row
  }

  async all() {
    return this.execute()
  }

  async run() {
    return this.execute()
  }
}

export class Database {
  constructor() {
    this.db = new DatabaseSync(':memory:', { enableForeignKeyConstraints: true })
    for (const file of readdirSync(migrations).filter(f => f.endsWith('.sql')).sort()) this.db.exec(readFileSync(new URL(file, migrations), 'utf8'))
  }

  prepare(sql) {
    return new Statement(this.db, sql)
  }

  async batch(statements) {
    this.db.exec('BEGIN')
    try {
      const results = statements.map(statement => statement.execute())
      this.db.exec('COMMIT')
      return results
    } catch (error) {
      this.db.exec('ROLLBACK')
      throw error
    }
  }

  /** Every row of a table as D1 would give it back, for tests to see what's kept. */
  rows(table) {
    return this.db.prepare(`SELECT * FROM ${table}`).all().map(plain)
  }

  tables() {
    return this.db.prepare("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").all().map(r => r.name)
  }
}
