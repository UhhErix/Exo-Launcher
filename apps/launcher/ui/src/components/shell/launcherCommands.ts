import {
  AppUpdate,
  Apps,
  Burst,
  Chats,
  Close,
  Controller,
  Download,
  Key,
  Library,
  Minus,
  Mouse,
  MusicNotes,
  Play,
  Refresh,
  Settings,
  StoreBag,
} from '../../brand/icons'
import type { Game } from '../../lib/host'
import { gameSearchKeywords } from '../../lib/library'
import { requestDiscordSettings } from '../../lib/discord'
import { runNoteCommand } from '../../lib/notes'
import { SETTINGS_SEARCH, type SettingsSectionId } from '../../lib/settingsSections'
import { sortGames } from '../../lib/utils'
import type { Command } from '../blocks/command-menu-1'
import { PC_SECTIONS, pcLinkRoom, type PcLink } from '../gpu/pcSections'
import { runPaletteExtra } from './paletteExtras'
import type { View } from './TitleBar'

const NAV_COMMANDS: Readonly<Record<string, View>> = {
  'nav-library': 'library',
  'nav-store': 'store',
  'nav-friends': 'friends',
  'nav-music': 'music',
  'nav-exo-ai': 'exo-ai',
  'nav-apps': 'apps',
  'nav-devices': 'devices',
  'nav-settings': 'settings',
}

export function buildShellCommands({
  library,
  hidden,
  selected,
  noteCommands,
  paletteExtras,
}: {
  library: readonly Game[]
  hidden: readonly Game[]
  selected: Game | null
  noteCommands: readonly Command[]
  paletteExtras: readonly Command[]
}): Command[] {
  const nav: Command[] = [
    { id: 'nav-library', group: 'Go to', title: 'Library', icon: Library },
    { id: 'nav-store', group: 'Go to', title: 'Store', icon: StoreBag },
    { id: 'nav-friends', group: 'Go to', title: 'Discord', icon: Chats, keywords: ['home', 'friends', 'messages', 'dms', 'servers', 'playing', 'voice'] },
    { id: 'nav-music', group: 'Go to', title: 'Music', icon: MusicNotes },
    { id: 'nav-exo-ai', group: 'Go to', title: 'Exo AI', icon: Burst, keywords: ['auto tune', 'specs', 'overclock', 'undervolt', 'autopilot', 'system tuning', 'shaders', 'updates', 'drivers', 'storage', 'park control', 'islc', 'standby list'] },
    { id: 'nav-apps', group: 'Go to', title: 'Apps', icon: Apps, keywords: ['uninstall', 'programs', 'bloat', 'startup'] },
    { id: 'nav-devices', group: 'Go to', title: 'Devices', icon: Mouse },
    { id: 'nav-settings', group: 'Go to', title: 'Settings', icon: Settings },
    ...PC_SECTIONS.map((section): Command => ({
      id: `pc-${section.id}`,
      group: 'Go to',
      title: section.label,
      detail: section.id === 'apps' ? 'Apps' : 'Exo AI',
      icon: section.id === 'apps' ? Apps : Burst,
      searchOnly: true,
      keywords: section.keywords,
    })),
    ...SETTINGS_SEARCH.map((page): Command => ({
      id: page.id,
      group: 'Go to',
      title: page.label,
      detail: 'Settings',
      icon: Settings,
      searchOnly: true,
      keywords: page.keywords,
    })),
  ]

  const seenTitles = new Set<string>()
  const gameCommands: Command[] = sortGames([...library, ...hidden], 'name', []).flatMap((game) => {
    const key = game.title.trim().toLowerCase()
    if (seenTitles.has(key)) return []
    seenTitles.add(key)
    return [{
      id: `game-${game.id}`,
      group: 'Library',
      title: game.title,
      keywords: gameSearchKeywords(game),
      icon: Controller,
    }]
  })

  const actions: Command[] = [
    { id: 'act-add-game', group: 'Actions', title: 'Add a game', icon: Controller },
    { id: 'act-activate-key', group: 'Actions', title: 'Activate a key', icon: Key },
    { id: 'act-refresh', group: 'Actions', title: 'Refresh library', icon: Refresh },
    { id: 'act-update', group: 'Actions', title: 'Check for updates', icon: AppUpdate },
    { id: 'act-minimize', group: 'Actions', title: 'Minimize window', icon: Minus },
    { id: 'act-close', group: 'Actions', title: 'Close window', icon: Close },
    ...(selected
      ? [
          { id: 'act-play', group: 'Actions', title: `Play ${selected.title}`, icon: Play },
          { id: 'act-install', group: 'Actions', title: `Install ${selected.title}`, icon: Download },
        ]
      : []),
  ]

  return [...nav, ...gameCommands, ...actions, ...noteCommands, ...paletteExtras]
}

export function executeShellCommand(
  command: Command,
  deps: {
    selected: Game | null
    goToView: (next: View) => Promise<void>
    openGamePage: (id: string, motion?: 'pointer' | 'instant') => void
    setGpuJump: (jump: { tab: PcLink } | null) => void
    setSettingsSection: (section: { id: SettingsSectionId } | null) => void
    setActivateKeyOpen: (open: boolean) => void
    addGame: () => void
    refreshLibrary: () => void
    checkUpdate: () => void
    minimize: () => void
    close: () => void
    runPrimary: (game: Game, action: 'play' | 'install') => void
    setErrorStatus: (message: string) => void
  },
): void {
  const navView = NAV_COMMANDS[command.id]
  if (navView) {
    void deps.goToView(navView)
    return
  }
  const actionHandlers: Record<string, () => void> = {
    'act-add-game': deps.addGame,
    'act-activate-key': () => void deps.goToView('library').then(() => deps.setActivateKeyOpen(true)),
    'act-refresh': () => { void deps.goToView('library'); deps.refreshLibrary() },
    'act-update': deps.checkUpdate,
    'act-minimize': deps.minimize,
    'act-close': deps.close,
    'act-play': () => { if (deps.selected) deps.runPrimary(deps.selected, 'play') },
    'act-install': () => { if (deps.selected) deps.runPrimary(deps.selected, 'install') },
  }
  const action = actionHandlers[command.id]
  if (action) {
    action()
    return
  }
  const section = PC_SECTIONS.find((entry) => `pc-${entry.id}` === command.id)
  if (section) {
    const room = pcLinkRoom(section.id)
    deps.setGpuJump(room === 'exo-ai' ? { tab: section.id } : null)
    void deps.goToView(room)
    return
  }
  const setting = SETTINGS_SEARCH.find((entry) => entry.id === command.id)
  if (setting) {
    if ('discord' in setting.opens) {
      requestDiscordSettings(setting.opens.discord)
      return
    }
    deps.setSettingsSection(setting.opens)
    void deps.goToView('settings')
    return
  }
  if (command.id.startsWith('game-')) {
    deps.openGamePage(command.id.slice(5), 'instant')
    return
  }
  if (runNoteCommand(command.id)) return
  const room = runPaletteExtra(command, deps.setErrorStatus)
  if (room === 'friends' || room === 'music') void deps.goToView(room)
}
