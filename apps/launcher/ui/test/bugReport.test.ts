/**
 * Report a problem's form (src/lib/bugReport.ts). Ways it could fail: Send posts the logs with Attach logs off, or a
 * contact nobody typed; an empty message is sent; the public GitHub address carries an email, a path, a web address or an
 * id, or the logs; the sent button says nothing useful.
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { MAX_MESSAGE, bugReportUrl, reportPayload, scrubForUrl, sentWords } from '../src/lib/bugReport.ts'

test('Open on GitHub opens the bug form with the version filled in', () => {
  assert.equal(bugReportUrl('3.26.0'), 'https://github.com/UhhErix/Exo-Launcher/issues/new?template=bug_report.yml&version=3.26.0')
})

test('with no version known and no words, the form opens empty', () => {
  assert.equal(bugReportUrl(null), 'https://github.com/UhhErix/Exo-Launcher/issues/new?template=bug_report.yml')
  assert.equal(bugReportUrl('  '), 'https://github.com/UhhErix/Exo-Launcher/issues/new?template=bug_report.yml')
})

test('the words fill the title and What happened, with anything personal taken out', () => {
  const url = new URL(bugReportUrl('3.26.0', 'Library is blank\nMail ada@example.com, see C:\\Users\\ada\\x.log, https://x.example/a?t=1, id 123456789012'))
  assert.equal(url.searchParams.get('template'), 'bug_report.yml')
  assert.equal(url.searchParams.get('title'), 'Library is blank')
  const words = url.searchParams.get('what-happened') ?? ''
  assert.match(words, /<email>/)
  assert.match(words, /<path>/)
  assert.match(words, /<url>/)
  assert.match(words, /<id>/)
  for (const leak of ['ada@example.com', 'Users', 'x.example', '123456789012']) assert.ok(!url.toString().includes(leak), leak)
})

test('a long message is cut in the address', () => {
  const url = new URL(bugReportUrl('3.26.0', 'x'.repeat(5000)))
  assert.ok((url.searchParams.get('what-happened') ?? '').length <= 1500)
  assert.ok((url.searchParams.get('title') ?? '').length <= 80)
})

test('scrubbing leaves ordinary words alone', () => {
  assert.equal(scrubForUrl('Steam showed error 53 after 3.26.0'), 'Steam showed error 53 after 3.26.0')
  assert.equal(scrubForUrl('in /home/ada/x and \\\\nas\\share\\f'), 'in <path> and <path>')
})

test('Send posts the message, the logs only with Attach logs on, and the contact only when typed', () => {
  const draft = { message: '  The library went blank\r\n  ', attachLogs: true, contact: '', excerpt: 'Error (app): Report a problem' }
  assert.deepEqual(reportPayload(draft), { message: 'The library went blank', excerpt: 'Error (app): Report a problem' })
  assert.deepEqual(reportPayload({ ...draft, attachLogs: false }), { message: 'The library went blank' })
  assert.deepEqual(reportPayload({ ...draft, attachLogs: true, excerpt: null }), { message: 'The library went blank' })
  assert.deepEqual(reportPayload({ ...draft, contact: '  ada  on  discord ' }), { message: 'The library went blank', excerpt: draft.excerpt, contact: 'ada on discord' })
})

test('an empty message sends nothing; a long one is cut to the service limit', () => {
  assert.equal(reportPayload({ message: '  \n ', attachLogs: true, contact: 'x', excerpt: 'y' }), null)
  assert.equal(reportPayload({ message: 'x'.repeat(MAX_MESSAGE + 10), attachLogs: false, contact: '', excerpt: null })?.message.length, MAX_MESSAGE)
})

test('the sent button names the issue, else the reference', () => {
  assert.equal(sentWords({ ok: true, issue: 123 }), 'Sent, #123')
  assert.equal(sentWords({ ok: true, ref: '1a2b3c4d' }), 'Sent, ref 1a2b3c4d')
  assert.equal(sentWords({ ok: true }), 'Sent')
})
