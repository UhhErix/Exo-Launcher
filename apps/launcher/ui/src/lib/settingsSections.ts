/**
 * Settings' sidebar (SettingsPanel): the rooms' settings, in the Bar's order, then Exo's own. Each entry is one page;
 * `sections` are the parts of a page a link can open at (the Library's Sign in to a store opens Library › Game stores),
 * and `keywords` name the rows it holds, which is how Ctrl+K finds them (LauncherApp). A tool's options still live with
 * the tool (Captures has its own, Lucid is in Devices, Graphics in the PC room).
 */
export const SETTINGS_PAGES = [
  {
    page: 'account',
    label: 'Account',
    sections: [],
    keywords: ['account', 'sign in', 'sign out', 'devices', 'password', 'recovery code', 'export my data', 'delete my account', 'sync', 'sync status'],
  },
  {
    page: 'general',
    label: 'General',
    sections: [],
    keywords: [
      'dark mode', 'amoled', 'pure black', 'text size', 'reduce motion', 'animation', 'start maximized', 'startup', 'start with windows', 'startup behaviour', 'window',
      'keep pc awake', 'keep awake', 'awake', 'sleep', 'downloads and shader warming',
    ],
  },
  {
    page: 'library',
    label: 'Library',
    sections: ['accounts'],
    keywords: [
      'game stores', 'sign in', 'account', 'switch account', 'steam', 'epic', 'gog', 'amazon', 'riot', 'ea', 'ubisoft', 'battle.net', 'xbox',
      'xbox sign in', 'offline mode', 'steam offline', 'go offline', 'go online', 'get another store',
      'riot id', 'xbox time played',
      'tools and soundtracks', 'show tools and soundtracks', 'soundtracks', 'tools', 'servers', 'sdks',
      'shaders', 'exo shaders', 'shader warming',
    ],
  },
  {
    page: 'downloads',
    label: 'Downloads',
    sections: ['downloads'],
    keywords: [
      'downloads', 'install folder', 'download hours', 'pause downloads while playing', 'update games automatically', 'automatic updates',
      'chipset driver', 'amd chipset', 'intel chipset',
    ],
  },
  {
    page: 'ingame',
    label: 'In game',
    sections: ['achievements', 'fps'],
    keywords: [
      'in game', 'achievements', 'achievement banners', 'banners in games', 'unlock', 'trophy', 'banner sound', 'achievement sound', 'preview banner', 'obs browser source',
      'fps counter', 'frame rate', 'frames per second', 'overlay', '1% low', 'frame time', 'gpu use', 'gpu temperature', 'cpu use', 'vram', 'ram', 'battery', 'mouse battery', 'headset battery', 'time of day', 'position',
    ],
  },
  {
    page: 'music',
    label: 'Music',
    sections: ['music'],
    keywords: ['music', 'music service', 'spotify', 'apple music', 'youtube music', 'lossless', 'jam'],
  },
  {
    page: 'privacy',
    label: 'Privacy',
    sections: [],
    keywords: ['privacy', 'app data', 'telemetry', 'crash reports', "view what's sent", "what's sent", 'delete my data'],
  },
  {
    page: 'about',
    label: 'About',
    sections: ['updates'],
    keywords: ['about', 'version', 'exo launcher', 'updates', 'check for updates', 'install update', 'update exo', 'clear caches', 'caches', "what's new", 'changelog', 'report a problem', 'support exo', 'license', 'licenses'],
  },
] as const

export type SettingsPageId = (typeof SETTINGS_PAGES)[number]['page']
/** A page, or a part of one (its heading's id without `set-`). */
export type SettingsSectionId = SettingsPageId | (typeof SETTINGS_PAGES)[number]['sections'][number]

/** The page a section is on. */
export function settingsPageOf(id: SettingsSectionId): SettingsPageId {
  return SETTINGS_PAGES.find(page => page.page === id || (page.sections as readonly string[]).includes(id))?.page ?? 'general'
}

/**
 * Discord's settings, the sheet over the Discord room (components/discord/UserSettings.tsx), its pages down the left, each
 * drawn alone (components/discord/DiscordSettings.tsx). `parts` are what a page holds, in its order; a link can open at any of them.
 */
export const DISCORD_SETTINGS = [
  {
    id: 'voice',
    label: 'Voice and video',
    parts: ['voice'],
    keywords: ['voice', 'video', 'microphone', 'speaker', 'soundboard', 'noise suppression', 'only my voice', 'hear your voice', 'push to talk', 'attenuation', 'camera', 'echo cancellation', 'input sensitivity'],
  },
  {
    id: 'chat',
    label: 'Chat',
    parts: ['chat'],
    keywords: ['play', 'party', 'game rooms', 'clips', 'chat', 'embeds', 'link previews', 'reactions', 'spoilers', 'emoticons', 'stickers', 'text size in chat', 'compact', 'zoom', 'saturation', 'role colors', 'reduced motion', 'gifs', 'text-to-speech', 'tts', 'image descriptions', 'streamer mode', 'developer mode', 'accessibility'],
  },
  {
    id: 'notifications',
    label: 'Notifications',
    parts: ['notifications'],
    keywords: ['notifications', 'desktop notifications', 'taskbar', 'quiet while playing', 'unread badge', 'sounds', 'sound volume', 'ring', 'preview sound'],
  },
  {
    id: 'privacy',
    label: 'Privacy',
    parts: ['privacy'],
    keywords: ['privacy', 'share my games', 'share when i\'m getting ready', 'compiling', 'activity', 'join my game', 'direct messages', 'dms', 'spam', 'friend requests', 'content filters', 'mature', 'graphic', 'blocked', 'ignored', 'request my data', 'profile privacy'],
  },
  {
    id: 'you',
    label: 'Account',
    parts: ['account', 'profiles', 'apps'],
    keywords: ['account', 'username', 'email', 'phone', 'status', 'custom status', 'password', 'two-factor', 'multi-factor', 'logged-in devices', 'delete account', 'disable account', 'profiles', 'avatar', 'banner', 'bio', 'pronouns', 'server tag', 'collectibles', 'nickname', 'connections', 'authorized apps', 'log out'],
  },
  { id: 'keybinds', label: 'Keybinds', parts: ['keybinds'], keywords: ['keybinds', 'shortcuts', 'keys'] },
] as const

type DiscordPage = (typeof DISCORD_SETTINGS)[number]
export type DiscordSettingsPart = DiscordPage['parts'][number]

/** One of those pages or a part of one, or Logged-in devices (opened from Account, in its place). */
export type DiscordSettingsSection = DiscordPage['id'] | DiscordSettingsPart | 'devices'

/** The page a section is on. */
export function discordPageOf(section: DiscordSettingsSection): DiscordPage {
  if (section === 'devices') return DISCORD_SETTINGS[4]
  return DISCORD_SETTINGS.find(page => page.id === section || (page.parts as readonly string[]).includes(section)) ?? DISCORD_SETTINGS[0]
}

/** What Ctrl+K finds in Settings, by the rows each page holds: searched, never listed, as the PC room's sections are. */
export const SETTINGS_SEARCH: readonly { id: string; label: string; keywords: readonly string[]; opens: { id: SettingsSectionId } | { discord: DiscordSettingsSection } }[] = [
  ...SETTINGS_PAGES.map(page => ({ id: `settings-${page.page}`, label: page.label, keywords: page.keywords, opens: { id: page.page } })),
  // Discord's open its sheet over the Discord room.
  ...DISCORD_SETTINGS.map(page => ({ id: `settings-discord-${page.id}`, label: page.label, keywords: ['discord', ...page.keywords], opens: { discord: page.id } })),
]
