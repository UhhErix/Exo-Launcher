import './styles'
import { useEffect, useLayoutEffect, useRef } from 'react'
import { useDiscordHome, type DiscordHome } from '../../lib/discord'
import { loadAccountSettings } from '../../lib/discordPrefs'
import { scrollToSection } from '../../lib/scrollToSection'
import { discordPageOf, type DiscordSettingsPart, type DiscordSettingsSection } from '../../lib/settingsSections'
import { AccountPage, DevicesPage, ProfilesPage } from './settings/AccountPages'
import { ChatPage, LogOut, NotificationsPage } from './settings/AppPages'
import { KeybindsPage } from './settings/KeybindsPage'
import { ConnectedAppsPage, PrivacyPage } from './settings/PrivacyPages'
import { VoicePage } from './settings/VoicePage'

export type { DiscordSettingsSection }

/**
 * Discord's settings (the sheet over the Discord room, UserSettings.tsx): what applies to Exo's client (not Billing, Clips, Family Center, Registered Games or the Game
 * Overlay), on Exo's own pages (lib/settingsSections.ts › DISCORD_SETTINGS), one page drawn at a time. Opening at a part
 * scrolls it into view; Logged-in devices opens in its place, with a way back. The account's settings are Discord's (every
 * Discord app shares them); what Discord keeps per device is kept on this PC. Only the page shown is drawn, so Voice's live
 * mic, Hear your voice, Test video and a keybind being recorded never run unseen.
 */
export function DiscordSettingsPage({ pane, section, onSection, onClose }: {
  /** Settings' scrolling column, which the sections scroll in. */
  pane: HTMLElement | null
  section: DiscordSettingsSection
  onSection: (section: DiscordSettingsSection) => void
  /** Logged out: Settings closes. */
  onClose: () => void
}) {
  const home = useDiscordHome()
  useEffect(loadAccountSettings, [])
  const shown = useRef(false)
  const page = discordPageOf(section)
  useLayoutEffect(() => {
    const first = !shown.current
    shown.current = true
    if (!pane) return
    if (section === 'devices' || section === page.id || section === page.parts[0]) pane.scrollTop = 0
    else scrollToSection(pane, `#dcs-${section}`, !first)
  }, [pane, section, page])
  // A part already open is scrolled to again (Account's Edit after scrolling away from Profiles).
  const go = (id: DiscordSettingsSection) => id === section ? void scrollToSection(pane, '#dcs-' + id) : onSection(id)
  return section === 'devices' ? (
    <div key="devices" className="exo-dc-vars exo-dc-settings-page exo-motion-switch">
      <DevicesPage onBack={() => onSection('account')} />
    </div>
  ) : (
    <div key={page.id} className="exo-dc-vars exo-dc-settings-page exo-motion-switch">
      {page.parts.map(id => (
        <section key={id} id={`dcs-${id}`} className="exo-dc-settings-section">
          <Page section={id} me={home?.me ?? null} guilds={home?.guilds ?? []} onSection={go} />
        </section>
      ))}
      {page.id === 'you' ? (
        <section className="exo-dc-settings-section">
          <LogOut onDone={onClose} />
        </section>
      ) : null}
    </div>
  )
}

function Page({ section, me, guilds, onSection }: {
  section: DiscordSettingsPart
  me: DiscordHome['me']
  guilds: DiscordHome['guilds']
  onSection: (section: DiscordSettingsSection) => void
}) {
  switch (section) {
    case 'profiles': return <ProfilesPage home={me} guilds={guilds} />
    case 'account': return <AccountPage home={me} onProfiles={() => onSection('profiles')} onDevices={() => onSection('devices')} />
    case 'privacy': return <PrivacyPage guilds={guilds} />
    case 'notifications': return <NotificationsPage />
    case 'voice': return <VoicePage />
    case 'chat': return <ChatPage />
    case 'keybinds': return <KeybindsPage />
    case 'apps': return <ConnectedAppsPage />
  }
}
