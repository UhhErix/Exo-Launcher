import type { View } from '../components/shell/TitleBar'

export const TOUR_VERSION = 1
export const TOUR_EVENT = 'exo-tour-start'

export type TourIconName =
  | 'new'
  | 'library'
  | 'search'
  | 'store'
  | 'discord'
  | 'music'
  | 'devices'
  | 'exoai'
  | 'settings'

export type TourStep = {
  id: string
  title: string
  body: string
  hint?: string
  shortcut?: string
  room: View
  selector: string | null
  icon: TourIconName
}

export const TOUR_STEPS: readonly TourStep[] = [
  {
    id: 'welcome',
    title: 'Welcome to Exo',
    body: 'Every game, chat, song, device, and Windows tweak lives in one fast launcher. Take a quick interactive spin through each room, or skip anytime.',
    hint: 'You can replay this tour anytime from Settings or Ctrl+K.',
    room: 'library',
    selector: null,
    icon: 'new',
  },
  {
    id: 'library',
    title: 'Your unified Library',
    body: 'Steam, Epic, GOG, Riot, Xbox, and local installs sit on one poster deck. Hover or use arrow keys and controller sticks to browse, and open the menu on any game for details, upscalers, and properties.',
    shortcut: 'Ctrl+1',
    room: 'library',
    selector: '.exo-nav-btn[aria-current="page"]',
    icon: 'library',
  },
  {
    id: 'search',
    title: 'Jump anywhere in two keystrokes',
    body: 'Search launches games, switches rooms, opens any setting, and pulls up your notes without touching the mouse.',
    shortcut: 'Ctrl+K',
    room: 'library',
    selector: '.exo-bar-search',
    icon: 'search',
  },
  {
    id: 'store',
    title: 'Store, wishlist, and gifts',
    body: 'Explore trending shelves, inspect game pages and specs, track your wishlist, and claim waiting store gifts in one place.',
    shortcut: 'Ctrl+2',
    room: 'store',
    selector: '.exo-nav-btn[aria-current="page"]',
    icon: 'store',
  },
  {
    id: 'discord',
    title: 'Built-in Discord client',
    body: 'DMs, pinned chats, servers, voice calls, soundboard, and Go Live run natively inside Exo. Active calls float in a compact top-bar island whenever you switch rooms.',
    shortcut: 'Ctrl+3',
    room: 'friends',
    selector: '.exo-nav-btn[aria-current="page"]',
    icon: 'discord',
  },
  {
    id: 'music',
    title: 'Music and Immersive mode',
    body: 'Control Spotify, Apple Music, or YouTube Music from one player with queue, EQ, and synced lyrics. Press F while listening to fill the screen in Immersive mode.',
    shortcut: 'F',
    room: 'music',
    selector: '.exo-nav-btn[aria-current="page"]',
    icon: 'music',
  },
  {
    id: 'devices',
    title: 'Devices without vendor bloat',
    body: 'Tune Exo Mouse, Wooting rapid trigger and profile backups, headset EQ, and controller stick calibration right from one room.',
    shortcut: 'Ctrl+5',
    room: 'devices',
    selector: '.exo-nav-btn[aria-current="page"]',
    icon: 'devices',
  },
  {
    id: 'exoai',
    title: 'Exo AI and Apps',
    body: 'Run one-click Auto Tune, update GPU and chipset drivers, inspect shaders and storage, or switch to Apps to clean leftovers and Windows startup clutter.',
    shortcut: 'Ctrl+6',
    room: 'exo-ai',
    selector: '.exo-nav-btn[aria-current="page"]',
    icon: 'exoai',
  },
  {
    id: 'settings',
    title: 'Make Exo yours',
    body: 'Connect your game stores, sync Riot hours automatically, customize text size or Liquid Glass, and replay this tour anytime from Settings › About.',
    shortcut: 'Ctrl+,',
    room: 'library',
    selector: '.exo-settings-btn',
    icon: 'settings',
  },
]

export function requestTourStart(): void {
  if (typeof window !== 'undefined') window.dispatchEvent(new Event(TOUR_EVENT))
}

export function isTourDue(saved: unknown, search = ''): boolean {
  if (search && new URLSearchParams(search).has('tour')) return true
  return typeof saved !== 'number' || saved < TOUR_VERSION
}

export type RectBox = { left: number; top: number; width: number; height: number }

export function placeTourCard(
  spot: RectBox | null,
  card: { width: number; height: number },
  viewport: { width: number; height: number },
): { left: number; top: number; centered: boolean } {
  const edge = 20
  const gap = 16
  const maxLeft = Math.max(edge, viewport.width - card.width - edge)
  const maxTop = Math.max(edge, viewport.height - card.height - edge)
  if (!spot) {
    return {
      left: Math.round(Math.max(edge, (viewport.width - card.width) / 2)),
      top: Math.round(Math.max(edge, (viewport.height - card.height) / 2)),
      centered: true,
    }
  }
  const spotRight = spot.left + spot.width
  const spotBottom = spot.top + spot.height
  const spotCenterX = spot.left + spot.width / 2
  const idealLeft = Math.min(maxLeft, Math.max(edge, spotCenterX - card.width / 2))

  if (spotBottom + gap + card.height <= viewport.height - edge) {
    return { left: Math.round(idealLeft), top: Math.round(spotBottom + gap), centered: false }
  }
  if (spot.top - gap - card.height >= edge) {
    return { left: Math.round(idealLeft), top: Math.round(spot.top - gap - card.height), centered: false }
  }
  if (spotRight + gap + card.width <= viewport.width - edge) {
    const idealTop = Math.min(maxTop, Math.max(edge, spot.top))
    return { left: Math.round(spotRight + gap), top: Math.round(idealTop), centered: false }
  }
  if (spot.left - gap - card.width >= edge) {
    const idealTop = Math.min(maxTop, Math.max(edge, spot.top))
    return { left: Math.round(spot.left - gap - card.width), top: Math.round(idealTop), centered: false }
  }
  return {
    left: Math.round(Math.max(edge, (viewport.width - card.width) / 2)),
    top: Math.round(Math.max(edge, (viewport.height - card.height) / 2)),
    centered: true,
  }
}
