import { host } from './host'
import type { LauncherSettings } from './host'
import { createSettingsWriter, readUntilSettled } from './settingsWriter'

// Every settings save from the page goes through here (the shell, Settings, onboarding, the Riot ID row): one order, and
// only the newest answer reaches the page's state. LauncherApp registers where an answer lands.
let sink: ((saved: LauncherSettings) => void) | null = null

export function setSettingsSink(next: ((saved: LauncherSettings) => void) | null): void {
  sink = next
}

type Patch = Parameters<typeof host.setSettings>[0]

const writer = createSettingsWriter<LauncherSettings, Patch>({
  send: patch => host.setSettings(patch),
  apply: saved => sink?.(saved),
})

/** Saves a change; resolves with the host's answer (rejects when the host refused). The page's state takes it only if no newer save is on its way. */
export function saveSettingsPatch(patch: Patch): Promise<LauncherSettings> {
  return writer.save(patch)
}

/** Reads the settings; null when a save was made meanwhile or is on its way, so the answer cannot bring back an older value. */
export function readSettings(): Promise<LauncherSettings | null> {
  return writer.read(() => host.getSettings())
}

/** The settings, read until one stands; throws when none does (the page shows its settings error and a retry). */
export function loadSettingsOrFail(): Promise<LauncherSettings> {
  return readUntilSettled(readSettings)
}
