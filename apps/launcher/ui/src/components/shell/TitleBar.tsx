import { useRef, type ReactNode } from 'react'
import { Button } from '../ui/Button'
import { Spinner } from '../ui/Spinner'
import { useGlide } from '../../lib/glide'
import { useLens } from '../../lib/glass'
import { ExoMark } from '../../brand/ExoMark'
import { Chats, ThisPc, Library, MusicNotes, Settings, StoreBag } from '../../brand/icons'
import { warmMusic, type MusicService } from '../../lib/music'
import { DiscordBadge } from './DiscordBadge'
import { MusicMiniPlayer } from './MusicMiniPlayer'
import { CaptureButton } from './CaptureButton'
import { WindowChrome } from '../WindowChrome'

export type View =
  'library' | 'store' | 'friends' | 'music' | 'system' | 'devices' | 'settings'

const NAV_TABS: ReadonlyArray<{
  id: View
  label: string
  Icon: typeof Library
}> = [
    { id: 'library', label: 'Library', Icon: Library },
    { id: 'store', label: 'Store', Icon: StoreBag },
    { id: 'friends', label: 'Discord', Icon: Chats },
    { id: 'music', label: 'Music', Icon: MusicNotes },
    { id: 'system', label: 'PC', Icon: ThisPc },
  ]

/** The rooms in tab order: Ctrl+1 is the first. */
export const ROOM_ORDER: readonly View[] = NAV_TABS.map(tab => tab.id)

export type TitleBarProps = {
  view: View
  /** A room being built for its first showing: its tab shows the ring in its glyph's place until it opens. */
  opening?: View | null
  onView: (view: View) => void
  /** The mini player: the Music room on Now playing. */
  onNowPlaying: () => void
  /** Gear: opens Settings, or returns to the room Settings was opened from. */
  onToggleSettings: () => void
  musicService: MusicService
  /** The Exo Island, left of the Rooms: a call outside Discord, or a download. */
  island?: ReactNode
  /** The Search capsule: opens Ctrl+K. */
  onSearch: () => void
  /** A room tab under the pointer or focused: its room is built out of sight before the press. */
  onWarm?: (view: View) => void
}

/** A room tab: icon and name, filled while it is the current room, with an optional count. */
function NavTab({ label, Icon, active, opening = false, badge, onOpen, onWarm }: {
  label: string
  Icon: typeof Library
  active: boolean
  opening?: boolean
  badge?: ReactNode
  onOpen: () => void
  onWarm?: () => void
}) {
  return (
    <button
      type="button"
      className={`exo-nav-btn${active ? ' is-on' : ''}`}
      aria-current={active ? 'page' : undefined}
      aria-busy={opening || undefined}
      onClick={onOpen}
      onPointerEnter={onWarm}
      onFocus={onWarm}
    >
      {/* The glyph holds while the ring waits its 300ms (waits.css), then gives way to it: swapped at once, the tab
          showed no glyph at all for a room that opened in under 300ms. */}
      <span className="exo-nav-glyph">
        <Icon size={16} current={active} />
        {opening ? <Spinner size={16} label={`Opening ${label}`} /> : null}
      </span>
      <span className="exo-nav-label">{label}</span>
      {badge}
    </button>
  )
}

function RoomNavigation({ view, opening, onView, onWarm }: Pick<TitleBarProps, 'view' | 'opening' | 'onView' | 'onWarm'>) {
  const glide = useGlide<HTMLElement>()
  return (
    <nav ref={glide} className="exo-glass exo-top-rooms exo-glide" aria-label="Rooms">
      {NAV_TABS.map((tab) => (
        <NavTab
          key={tab.id}
          label={tab.label}
          Icon={tab.Icon}
          // Devices is a page of the PC room (its This PC line, or the mouse in the Bar): PC stays lit there.
          active={view === tab.id || (tab.id === 'system' && view === 'devices')}
          opening={opening === tab.id}
          badge={tab.id === 'friends' ? <DiscordBadge /> : null}
          onOpen={() => onView(tab.id)}
          onWarm={() => { if (tab.id === 'music') warmMusic(); onWarm?.(tab.id) }}
        />
      ))}
    </nav>
  )
}

/**
 * The Bar (DESIGN.md › Exo's language), one row of evenly spaced capsules: Search (the Exo mark;
 * Ctrl+K), the call or a download, the Rooms, the song, the mouse, and Tools (Capture, and you or Settings:
 * what applies everywhere; a room's own controls stay in the room) with the window controls in the
 * same capsule. Each live capsule has a slot of its own and slides in and out there, so the tabs never move.
 */
function NavDock({ view, opening, onView, onWarm, onToggleSettings, onNowPlaying, onSearch, island, ...music }: TitleBarProps) {
  const search = useRef<HTMLButtonElement>(null)
  useLens(search)
  return (
    <div className="exo-bar">
      <div className="exo-bar-start">
        <button ref={search} type="button" className="exo-glass exo-bar-search" onClick={onSearch} aria-label="Search Exo" aria-keyshortcuts="Control+K">
          <ExoMark size={18} className="exo-brand-logo" />
          <span className="exo-bar-search-label">Search</span>
          <kbd className="exo-bar-kbd" aria-hidden="true">Ctrl K</kbd>
        </button>
      </div>
      <div className="exo-bar-center">
        <div className="exo-bar-side is-start">{island}</div>
        <RoomNavigation view={view} opening={opening} onView={onView} onWarm={onWarm} />
        <div className="exo-bar-side is-end">
          <MusicMiniPlayer key={music.musicService} service={music.musicService} hidden={view === 'music'} onOpen={onNowPlaying} />
        </div>
      </div>
      <div className="exo-bar-end">
        <div className="exo-glass exo-titlebar-tools">
          {/* Capture, then Settings (and Ctrl+, as ever). */}
          <CaptureButton />
          <Button
            type="button"
            variant="ghost"
            size="sm"
            isIconOnly
            className={`exo-settings-btn${view === 'settings' ? ' is-on' : ''}`}
            aria-label="Open settings"
            aria-pressed={view === 'settings'}
            onPress={onToggleSettings}
          >
            <Settings size={16} />
          </Button>
          <span className="exo-titlebar-divider" aria-hidden="true" />
          <WindowChrome />
        </div>
      </div>
    </div>
  )
}

export function TitleBar({
  view,
  onView,
  onToggleSettings,
  ...music
}: TitleBarProps) {
  return (
    <header className="exo-titlebar">
      <div className="exo-titlebar-main">
        <NavDock
          view={view}
          onView={onView}
          onToggleSettings={onToggleSettings}
          {...music}
        />
      </div>
    </header>
  )
}
