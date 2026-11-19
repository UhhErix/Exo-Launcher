import { useMemo, useState } from 'react'
import { requestDiscordSettings, type DiscordHome } from '../../lib/discord'
import { useAccountSettings } from '../../lib/discordPrefs'
import { Settings } from '../../brand/icons'
import { Icons } from './icons'
import { Avatar } from './presence'
import type { VoiceControls } from './voice'
import { AudioQuickMenu, MeMenu } from './MeMenus'
import { Inbox } from './Inbox'
import type { MarkdownContext } from './markdown'

const STATUS: Record<string, string> = { online: 'Online', idle: 'Idle', dnd: 'Do not disturb', invisible: 'Invisible', offline: 'Invisible' }
const statusWord = (status: string | null) => STATUS[status ?? 'online'] ?? 'Online'

/**
 * You, at the left panel's foot: your picture and name over your custom status (else your status), whose press is your
 * menu (status, profile, accounts, voice, Shop); then Mute, Deafen and Discord's settings.
 */
export function MePanel({ home, voice }: { home: DiscordHome | null; voice: VoiceControls }) {
  const { muted, deafened } = voice.voice
  const [menu, setMenu] = useState<'me' | 'input' | 'output' | null>(null)
  const custom = useAccountSettings().customStatus
  const me = home?.me
  if (!me) return null
  const status = me.status ?? null
  const line = custom?.text?.trim() || statusWord(status)
  return (
    <footer className="exo-dc-me">
      {menu === 'me' ? <MeMenu meId={me.id} status={status} onClose={() => setMenu(null)} /> : null}
      {menu === 'input' || menu === 'output' ? <AudioQuickMenu kind={menu} onClose={() => setMenu(null)} /> : null}
      <button type="button" className="exo-dc-me-who" aria-label={`${me.name}, ${statusWord(status)}`}
        aria-haspopup="menu" aria-expanded={menu === 'me'} onClick={() => setMenu(now => now === 'me' ? null : 'me')}>
        <Avatar src={me.avatar} name={me.name} status={status ?? 'online'} size={36} look={me.look} scope=".exo-dc-me-who" />
        <span className="exo-dc-me-text"><b>{me.name}</b><span>{line}</span></span>
      </button>
      <span className="exo-dc-me-pair">
        <button type="button" className={`exo-dc-icon-btn${muted ? ' is-off' : ''}`} aria-label={muted ? 'Unmute' : 'Mute'} aria-pressed={muted}
          onClick={() => void voice.setMuted(!muted)}
          onContextMenu={event => { event.preventDefault(); setMenu(now => now === 'input' ? null : 'input') }}>
          {muted ? <Icons.Muted size={18} /> : <Icons.Mic size={18} />}
        </button>
        <button type="button" className="exo-dc-icon-btn exo-dc-me-caret" aria-label="Input options" aria-haspopup="menu" aria-expanded={menu === 'input'}
          onClick={() => setMenu(now => now === 'input' ? null : 'input')}>
          <Icons.Collapse size={12} />
        </button>
      </span>
      <span className="exo-dc-me-pair">
        <button type="button" className={`exo-dc-icon-btn${deafened ? ' is-off' : ''}`} aria-label={deafened ? 'Undeafen' : 'Deafen'} aria-pressed={deafened}
          onClick={() => void voice.setDeafened(!deafened)}
          onContextMenu={event => { event.preventDefault(); setMenu(now => now === 'output' ? null : 'output') }}>
          {deafened ? <Icons.Deafened size={18} /> : <Icons.Headphones size={18} />}
        </button>
        <button type="button" className="exo-dc-icon-btn exo-dc-me-caret" aria-label="Output options" aria-haspopup="menu" aria-expanded={menu === 'output'}
          onClick={() => setMenu(now => now === 'output' ? null : 'output')}>
          <Icons.Collapse size={12} />
        </button>
      </span>
      <button type="button" className="exo-dc-icon-btn" aria-label="Discord settings" onClick={() => requestDiscordSettings('account')}>
        <Settings size={18} />
      </button>
    </footer>
  )
}

/** The Inbox, at the room's top right, where Discord's is. */
export function InboxButton({ meId, place, open, channelName }: {
  meId: string | null
  /** The server you're in, if any: the Inbox can keep to it. */
  place: string | null
  /** The conversation on screen: the Inbox's rows there don't repeat where they are. */
  open: string | null
  channelName: (id: string) => string | null
}) {
  const [shown, setShown] = useState(false)
  const ctx = useMemo<MarkdownContext>(() => ({ users: new Map(), colors: new Map(), channelName, meId, guildId: place }), [channelName, meId, place])
  return (
    <div className="exo-dc-inbox-anchor">
      <button type="button" className="exo-dc-icon-btn" aria-label="Inbox" aria-pressed={shown} onClick={() => setShown(now => !now)}><Icons.Inbox size={18} /></button>
      {shown ? <Inbox ctx={ctx} here={open ?? ''} onClose={() => setShown(false)} /> : null}
    </div>
  )
}
