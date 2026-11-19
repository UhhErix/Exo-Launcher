import { useRef, useState } from 'react'
import { host } from '../../lib/host'
import type { DiscordChannel, DiscordDm, DiscordDmMember, DiscordHome } from '../../lib/discord'
import { useDismiss } from '../../lib/dismiss'
import { ConfirmButton } from '../ui/ConfirmButton'
import { showProfile } from './extras'
import { Icons } from './icons'
import { Avatar } from './presence'
import { Popover } from '../ui/Dialog'
import { ErrorNote } from '../ui/ErrorNote'
import { errorText } from '../../lib/errorWords'

const MAX_ICON = 10 * 1024 * 1024

/**
 * A group DM's member list beside it, as Discord's: everyone in it with the owner's crown; the owner's right-click on
 * someone has Remove from group and Make group owner (asked first).
 */
export function GroupMembers({ channel, me }: { channel: DiscordChannel; me: DiscordHome['me'] }) {
  const dm = channel.dm
  const [menu, setMenu] = useState<{ member: DiscordDmMember; x: number; y: number } | null>(null)
  if (!dm?.members) return null
  const owner = !!me && dm.ownerId === me.id
  // You, then everyone else, as Discord lists a group.
  const everyone: DiscordDmMember[] = [...(me ? [{ id: me.id, name: me.name, avatar: me.avatar, status: me.status ?? 'online' }] : []), ...dm.members]
  return (
    <aside className="exo-dc-members exo-dc-group-members" aria-label="Members">
      <p className="exo-dc-side-label">Members ({everyone.length})</p>
      {everyone.map(member => (
        <button key={member.id} type="button" className={`exo-dc-member${member.status === 'offline' ? ' is-offline' : ''}`} data-dc-user={member.id}
          onClick={event => { const box = event.currentTarget.getBoundingClientRect(); showProfile(member.id, null, box.left - 316, box.top) }}
          onContextMenu={event => { if (!owner || member.id === me?.id) return; event.preventDefault(); setMenu({ member, x: event.clientX, y: event.clientY }) }}>
          <Avatar src={member.avatar} name={member.name} status={member.status} size={32} />
          <span className="exo-dc-member-names">
            <span className="exo-dc-member-name">
              {member.name}
              {member.id === dm.ownerId ? <span className="exo-dc-member-mark" role="img" aria-label="Group owner"><Icons.Owner size={14} /></span> : null}
            </span>
          </span>
        </button>
      ))}
      {menu ? <MemberMenu channelId={channel.id} {...menu} onClose={() => setMenu(null)} /> : null}
    </aside>
  )
}

// The owner's Remove from group and Make group owner for one member.
function MemberMenu({ channelId, member, x, y, onClose }: { channelId: string; member: DiscordDmMember; x: number; y: number; onClose: () => void }) {
  const [note, setNote] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  const run = (action: 'removeMember' | 'makeOwner') => void host.discordGroup(channelId, action, { userId: member.id })
    .then(result => { if (result.ok) onClose(); else setNote(errorText(result.message, 'Discord did not take that.')) }, () => setNote('Discord did not take that.'))
  return (
    <div ref={box} className="exo-dc-menu exo-dc-channel-menu" role="menu" aria-label={member.name} style={{ left: x, top: y }}>
      <button type="button" role="menuitem" className="is-danger" onClick={() => run('removeMember')}>Remove from group</button>
      <ConfirmButton role="menuitem" question="Make them owner?" onConfirm={() => run('makeOwner')}>Make group owner</ConfirmButton>
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
  )
}

/** Discord's Edit Group: the group's name and its icon (a picture of up to 10 MB, or none). */
export function EditGroup({ dm, onClose }: { dm: DiscordDm; onClose: () => void }) {
  const [name, setName] = useState(dm.name)
  const [icon, setIcon] = useState<{ data: string } | 'remove' | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const file = useRef<HTMLInputElement>(null)
  const pick = (picked: File | undefined) => {
    if (!picked) return
    if (picked.size > MAX_ICON) { setError('Pick a picture of up to 10 MB.'); return }
    const reader = new FileReader()
    reader.onload = () => { setError(null); setIcon({ data: String(reader.result) }) }
    reader.readAsDataURL(picked)
  }
  const save = () => {
    setBusy(true)
    setError(null)
    const changes = { ...(name.trim() !== dm.name ? { name: name.trim() } : {}), ...(icon === 'remove' ? { removeIcon: true } : icon ? { icon: icon.data } : {}) }
    void host.discordGroup(dm.id, 'editGroup', changes).then(result => {
      setBusy(false)
      if (result.ok) onClose(); else setError(errorText(result.message, 'Discord did not save that.'))
    }, () => { setBusy(false); setError('Discord did not save that.') })
  }
  const shown = icon === 'remove' ? null : icon ? icon.data : dm.avatar
  return (
    <Popover portal={false} className="exo-dc-modal exo-dc-edit-group" label="Edit group" onClick={event => event.stopPropagation()} onClose={onClose}>
      <strong>Edit group</strong>
      <span className="exo-dc-edit-group-icon">
        <Avatar src={shown} name={name || dm.name} status={null} size={80} />
        <span className="exo-dc-settings-buttons">
          <button type="button" className="exo-set-btn" onClick={() => file.current?.click()}>Change icon</button>
          {shown ? <button type="button" className="exo-set-btn" onClick={() => setIcon('remove')}>Remove icon</button> : null}
        </span>
        <input ref={file} type="file" accept="image/png,image/jpeg,image/gif,image/webp" hidden onChange={event => { pick(event.target.files?.[0]); event.target.value = '' }} />
      </span>
      <label className="exo-dc-modal-field"><span>Group name</span>
        <input className="exo-dc-modal-input" value={name} maxLength={100} onChange={event => setName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') save() }} />
      </label>
      {error ? <ErrorNote className="is-bad" message={error} /> : null}
      <div className="exo-dc-modal-row">
        <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={save}>Save</button>
      </div>
    </Popover>
  )
}
