import { describe, it } from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  diffParity,
  formatMarkdownReport,
  indexNativeHandlers,
  indexReactCallers,
  isTraceSafe,
  parseTrace,
  sliceTraceByWindow,
  summarizeEntries,
} from './lib.mjs'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const APP_ROOT = path.resolve(HERE, '../..')

describe('parity trace parsing and window slicing', () => {
  it('parses valid jsonl lines and filters by window, client, and debug.mark', () => {
    const raw = [
      JSON.stringify({ t: 1000, client: 'web', id: '1', method: 'debug.mark', params_shape: { phase: 'string' } }),
      JSON.stringify({ t: 1010, client: 'web', id: '2', method: 'library.get', params_shape: { force: 'boolean' } }),
      JSON.stringify({ t: 1020, client: 'web', id: '2', ok: true, ms: 9.5 }),
      JSON.stringify({ t: 1025, client: 'native', id: '3', method: 'library.get', params_shape: null }),
      JSON.stringify({ t: 1030, client: 'web', event: 'library.updated', payload_shape: { games: [] } }),
      JSON.stringify({ t: 2500, client: 'web', id: '4', method: 'store.home', params_shape: null }),
      'not-json',
    ].join('\n')

    const parsed = parseTrace(raw)
    assert.equal(parsed.length, 6)

    const sliced = sliceTraceByWindow(parsed, 1000, 1500, 'web')
    assert.equal(sliced.length, 3)
    assert.equal(sliced[0].method, 'library.get')
    assert.equal(sliced[1].ok, true)
    assert.equal(sliced[2].event, 'library.updated')

    const sum = summarizeEntries(sliced)
    assert.equal(sum.totalRequests, 1)
    assert.equal(sum.totalEvents, 1)
    assert.equal(sum.methods['library.get'].medianMs, 9.5)
  })

  it('flags token-like, email, and user-path values in trace safety scan', () => {
    const clean = JSON.stringify({ t: 1, client: 'web', id: '1', method: 'discord.login', params_shape: { token: 'string', email: 'string' } })
    assert.equal(isTraceSafe(clean).safe, true)

    const dirtyJwt = JSON.stringify({ t: 1, client: 'web', id: '1', method: 'x', params_shape: { token: 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc' } })
    assert.equal(isTraceSafe(dirtyJwt).safe, false)

    const dirtyEmail = JSON.stringify({ t: 1, client: 'web', id: '1', method: 'x', params_shape: { email: 'alice@example.com' } })
    assert.equal(isTraceSafe(dirtyEmail).safe, false)

    const dirtyPath = JSON.stringify({ t: 1, client: 'web', id: '1', method: 'x', params_shape: { p: 'C:\\Users\\Alice\\file.txt' } })
    assert.equal(isTraceSafe(dirtyPath).safe, false)
  })
})

describe('parity diff and report formatting', () => {
  it('identifies react-only, native-only, push event gaps, chattiness >2x, and sorts biggest gaps first', () => {
    const actions = [
      { id: 'room-a', label: 'Room A', group: 'rooms' },
      { id: 'room-b', label: 'Room B', group: 'rooms' },
    ]
    const webTraces = {
      'room-a': [
        { t: 1, client: 'web', id: '1', method: 'a.shared', params_shape: null },
        { t: 2, client: 'web', id: '1', ok: true, ms: 5 },
      ],
      'room-b': [
        { t: 10, client: 'web', id: '1', method: 'b.onlyReact', params_shape: null },
        { t: 11, client: 'web', id: '2', method: 'b.chatty', params_shape: null },
        { t: 12, client: 'web', id: '3', method: 'b.chatty', params_shape: null },
        { t: 13, client: 'web', id: '4', method: 'b.chatty', params_shape: null },
        { t: 14, client: 'web', event: 'b.event', payload_shape: { ok: 'boolean' } },
      ],
    }
    const nativeTraces = {
      'room-a': [
        { t: 1, client: 'native', id: '1', method: 'a.shared', params_shape: null },
        { t: 2, client: 'native', id: '1', ok: true, ms: 4 },
      ],
      'room-b': [
        { t: 10, client: 'native', id: '1', method: 'b.onlyNative', params_shape: null },
        { t: 11, client: 'native', id: '2', method: 'b.chatty', params_shape: null },
      ],
    }

    const diff = diffParity({
      actions,
      webTraces,
      nativeTraces,
      webStatus: { 'room-a': { status: 'ok' }, 'room-b': { status: 'ok' } },
      nativeStatus: { 'room-a': { status: 'ok' }, 'room-b': { status: 'ok' } },
      reactCallers: {
        'b.onlyReact': ['ui/src/components/RoomB.tsx:12'],
        'b.chatty': ['ui/src/components/RoomB.tsx:20'],
      },
      reactEventSubscribers: {
        'b.event': ['ui/src/components/RoomB.tsx:30'],
      },
      nativeHandledEvents: [],
    })

    assert.equal(diff.perAction[0].id, 'room-b')
    assert.equal(diff.perAction[1].id, 'room-a')
    assert.equal(diff.sessionReactOnly[0].method, 'b.onlyReact')
    assert.equal(diff.sessionReactOnly[0].reactCallers[0], 'ui/src/components/RoomB.tsx:12')
    assert.equal(diff.sessionNativeOnly[0].method, 'b.onlyNative')
    assert.equal(diff.sessionChattiness[0].method, 'b.chatty')
    assert.equal(diff.sessionChattiness[0].ratio, 3)
    assert.equal(diff.observedEventGaps[0].event, 'b.event')

    const md = formatMarkdownReport(diff)
    assert.match(md, /b\.onlyReact/)
    assert.match(md, /ui\/src\/components\/RoomB\.tsx:12/)
    assert.match(md, /3x/)
  })

  it('indexes real React callers and native event handlers from this repo', () => {
    const { callers, eventSubscribers } = indexReactCallers(path.join(APP_ROOT, 'ui', 'src'))
    const { handledEvents } = indexNativeHandlers(path.join(APP_ROOT, 'native-ui'))
    assert.ok(Object.keys(callers).length > 100)
    assert.ok((callers['library.get'] ?? []).length > 0)
    assert.ok((callers['downloads.list'] ?? []).some((r) => r.includes('DownloadsSheet.tsx')))
    assert.ok(Object.keys(eventSubscribers).length > 20)
    assert.ok(handledEvents.includes('library.updated'))
  })
})
