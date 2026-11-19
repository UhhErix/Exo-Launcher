/** GitHub's bug report form with Exo's version filled in (the form's `version` field). */
export function bugReportUrl(version: string | null): string {
  const url = new URL('https://github.com/UhhErix/Exo-Launcher/issues/new')
  url.searchParams.set('template', 'bug_report.yml')
  if (version?.trim()) url.searchParams.set('version', version.trim())
  return url.toString()
}
