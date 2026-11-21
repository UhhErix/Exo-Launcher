/**
 * The only door to this page's own storage (localStorage and sessionStorage). It is lost on a reinstall and never backed
 * up or synced, so it holds conveniences only, each by name below with why it may be lost. A choice the player makes goes
 * in Exo's settings instead (lib/uiPrefs.ts). ESLint refuses the storage everywhere else.
 */
const KEYS = {
  // The first-frame copy of the choices Exo keeps (uiPrefs.ts); Exo's settings replace it as they load.
  'exo.uiPrefs': 'local',
  // Where an older Exo kept those choices: read once to move them into Exo's settings, then removed.
  'exo.discordPrefs': 'local',
  'exo.discordPerformance': 'local',
  'exo.driverBranch': 'local',
  'exo.devices.headphones': 'local',
  'exo.shadersOrder.2': 'local',
  'exo.discord.goLive': 'local',
  'exo.discord.goLivePreview': 'local',
  'exo.discord.voiceChat': 'local',
  // The first-frame copy of the text colour and size, which Exo's settings own (exoTheme.ts).
  'exo.textTheme': 'local',
  // The first-frame copy of the music service (web-client-preferences.json owns it) and whether each was signed in last time.
  'exo.musicWebService': 'local',
  'exo.music.signedIn.spotify': 'local',
  'exo.music.signedIn.apple': 'local',
  'exo.music.signedIn.youtube': 'local',
  // When Music was last used, so a warm start opens it early.
  'exo.music.usedAt': 'local',
  // Unsent Discord drafts, the last place open, the people pane per server, and where the call's small stream sat.
  'exo.discord.drafts': 'local',
  'exo.discord.where': 'local',
  'exo.discord.folders': 'local',
  'exo.discord.people': 'local',
  'exo.discord.miniStream': 'local',
  // Which stop of the backups the shell already told (Settings › Backups keeps saying it).
  'exo.backupStopTold': 'local',
  // A driver install's progress across a page reload in this run.
  'exo.driverInstall': 'session',
  'exo.autoTune.completedAt': 'local',
} as const

export type PageKey = keyof typeof KEYS

function store(key: PageKey): Storage | null {
  try { return KEYS[key] === 'session' ? window.sessionStorage : window.localStorage } catch { return null }
}

/** The text kept under a key, or null (nothing kept, or the storage cannot be read). */
export function pageGet(key: PageKey): string | null {
  try { return store(key)?.getItem(key) ?? null } catch { return null }
}

/** Keeps text under a key; false when the storage refused it (the convenience lasts this run). */
export function pageSet(key: PageKey, value: string): boolean {
  try { store(key)?.setItem(key, value); return store(key) !== null } catch { return false }
}

export function pageRemove(key: PageKey) {
  try { store(key)?.removeItem(key) } catch { /* harmless: the next start reads it again */ }
}
