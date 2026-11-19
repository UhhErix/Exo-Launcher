import { lazy, Suspense, useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import { DISCORD_SETTINGS, discordPageOf, type DiscordSettingsSection } from '../../lib/settingsSections'
import { SheetNav, useLayer } from './admin'
import { SettingsLayer } from './SettingsLayer'

// Discord's pages load the first time the sheet opens, not with the room.
const DiscordSettingsPage = lazy(() => import('./DiscordSettings').then(m => ({ default: m.DiscordSettingsPage })))

const GROUPS = [{ heading: null, items: DISCORD_SETTINGS.map(page => ({ id: page.id as DiscordSettingsSection, label: page.label })) }]

/**
 * Discord's settings: the sheet Server Settings is, over the Discord room, its pages down the left (Voice and video,
 * Chat, Notifications, Privacy, Account, Keybinds) and the one open at the right, landing on the part asked for (your
 * gear, your menu's Edit profile or Voice settings, Ctrl+K).
 */
export function UserSettings({ start, onClose }: { start: DiscordSettingsSection; onClose: () => void }) {
  const [section, setSection] = useState<DiscordSettingsSection>(start)
  const [pane, setPane] = useState<HTMLDivElement | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  return (
    <SettingsLayer onClose={onClose}>
      <div ref={box} className="exo-dc-settings is-paged" role="dialog" aria-modal="true" aria-label="Discord settings" tabIndex={-1}>
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <SheetNav title="Discord settings" groups={GROUPS} current={discordPageOf(section).id} onPick={setSection} />
        <div className="exo-settings-main">
          <button type="button" className="exo-props-close exo-settings-close" aria-label="Close Discord settings" onClick={onClose}><Close size={16} /></button>
          <div ref={setPane} className="exo-settings-scroll exo-dc-settings-body">
            <h2 className="exo-settings-page-title">{discordPageOf(section).label}</h2>
            <Suspense fallback={null}>
              <DiscordSettingsPage pane={pane} section={section} onSection={setSection} onClose={onClose} />
            </Suspense>
          </div>
        </div>
      </div>
    </SettingsLayer>
  )
}
