import { test } from 'node:test'
import assert from 'node:assert/strict'
import { bugReportUrl } from '../src/lib/bugReport.ts'

test('Report a problem opens the bug form with the version filled in', () => {
  assert.equal(bugReportUrl('3.26.0'), 'https://github.com/UhhErix/Exo-Launcher/issues/new?template=bug_report.yml&version=3.26.0')
})

test('with no version known, the form opens empty', () => {
  assert.equal(bugReportUrl(null), 'https://github.com/UhhErix/Exo-Launcher/issues/new?template=bug_report.yml')
  assert.equal(bugReportUrl('  '), 'https://github.com/UhhErix/Exo-Launcher/issues/new?template=bug_report.yml')
})
