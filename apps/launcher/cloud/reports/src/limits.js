export const limits = {
  bodyBytes: 32 * 1024,
  excerptChars: 8192,
  prefix: 'reports/',
  keepDays: 30,
}

const DAY_MS = 24 * 60 * 60 * 1000

export const telemetryLimits = {
  bodyBytes: 64 * 1024,
  maxEvents: 500,
  maxProps: 32,
  maxStringChars: 256,
  maxEventBytes: 1024,
  skewMs: 7 * DAY_MS,
  keepDays: 90,
  eventsPerInstallHour: 500,
  retentionDaysPerRun: 31,
  deleteBodyBytes: 1024,
}
