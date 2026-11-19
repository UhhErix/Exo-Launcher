import { useEffect, useState } from 'react'
import { host } from '../../../lib/host'
import type { DiscordHome, DiscordRestricted } from '../../../lib/discord'
import { Avatar } from '../presence'
import { changeAccountSettings, useAccountSettings, useStreamerHides, type AccountSettings, type DiscordAuthorizedApp, type DiscordConnection } from '../../../lib/discordPrefs'
import { useChoiceMenu } from '../../ui/Menu'
import { connectionLabel } from '../profileParts'
import { ConfirmButton } from '../../ui/ConfirmButton'
import { ChoiceRow, Row, Section, State, SwitchRow } from '../../settings/parts'
import { AccountToggle, LinkRow, LoadError, useLoad } from './parts'
import { useAccount } from './AccountPages'
import { ErrorNote } from '../../ui/ErrorNote'

const REDACTION = { show: 1, blur: 2, block: 3 } as const

// Discord's own defaults for an unset filter: shown to adults among friends and in servers, blocked from others; blurred for teens.
function redaction(value: number, adult: boolean, dm: boolean, friend: boolean) {
  if (value !== 0) return value
  if (!adult) return dm && !friend ? REDACTION.block : REDACTION.blur
  return dm && !friend ? REDACTION.block : REDACTION.show
}

type FilterKind = 'explicit' | 'gore'

function FilterSet({ kind, settings, adult }: { kind: FilterKind; settings: AccountSettings; adult: boolean }) {
  const field = (where: 'FriendDm' | 'NonFriendDm' | 'Guilds') => `${kind}Content${where}` as const
  const dm = [...(adult ? [{ value: REDACTION.show as number, label: 'Show' }] : []), { value: REDACTION.blur, label: 'Blur' }, { value: REDACTION.block, label: 'Block' }]
  const servers = [...(adult ? [{ value: REDACTION.show as number, label: 'Show' }] : []), { value: REDACTION.blur, label: 'Blur' }]
  const save = (key: ReturnType<typeof field>) => (value: number) => changeAccountSettings({ [key]: value })
  return (
    <Section title={kind === 'explicit' ? 'Mature sexual media' : 'Graphic media'}>
      <ChoiceRow label="From friends" value={redaction(settings[field('FriendDm')], adult, true, true)} choices={dm} onChange={save(field('FriendDm'))} />
      <ChoiceRow label="From others" value={redaction(settings[field('NonFriendDm')], adult, true, false)} choices={dm} onChange={save(field('NonFriendDm'))} />
      <ChoiceRow label="In server channels" value={redaction(settings[field('Guilds')], adult, false, false)} choices={servers} disabled={!adult} onChange={save(field('Guilds'))} />
    </Section>
  )
}

// The spam filter as Discord shows it: dm_spam_filter_v2, else what the older setting said.
function spamFilter(settings: AccountSettings, adult: boolean) {
  if (settings.dmSpamFilterV2 !== 0) return settings.dmSpamFilterV2
  if (!adult) return 3
  return [1, 2, 3][settings.explicitContentFilter] ?? 2
}

/**
 * Discord settings > Privacy: who sees what you do, who can reach you, what media you see, who you've blocked, and your
 * data. Discord's data-use, sponsored content, web pages and profile-update switches aren't here: Exo keeps them all off
 * (DiscordSession.Privacy.cs).
 */
export function PrivacyPage({ guilds }: { guilds: DiscordHome['guilds'] }) {
  const settings = useAccountSettings()
  const adult = useAdult()
  const dmsRestricted = settings.defaultGuildsRestrictedV2 ?? settings.defaultGuildsRestricted
  const flags = settings.friendSourceFlags
  const everyone = (flags & 8) !== 0
  const setFlag = (bit: number, on: boolean) => changeAccountSettings({ friendSourceFlags: on ? flags | bit : flags & ~bit & ~8 })
  // Discord asks whether a new default also goes to the servers you're in now: a menu from the switch, and closing it
  // with neither changes nothing.
  const choice = useChoiceMenu()
  const applyDefault = async (kind: 'dms' | 'requests', restricted: boolean) => {
    const answer = await choice.ask('Apply to', [{ id: 'all', label: 'All your servers' }, { id: 'new', label: 'New servers only' }])
    if (!answer) return null
    const ids = answer === 'all' ? (restricted ? guilds.map(g => g.id) : []) : undefined
    return changeAccountSettings(kind === 'dms'
      ? { defaultGuildsRestrictedV2: restricted, ...(ids ? { restrictedGuildIds: ids } : {}) }
      : { defaultMessageRequestRestricted: restricted, ...(ids ? { messageRequestRestrictedGuildIds: ids } : {}) })
  }
  return (
    <>
      <Section title="Activity">
        <AccountToggle label="Share when I come online" settings={settings} field="notifyFriendsOnComeOnline" />
        <AccountToggle label="Share when I start streaming" settings={settings} field="notifyServerMembersOnGoLive" />
        <ChoiceRow label="Share my activity in new servers" value={activityDefault(settings)} onChange={value => changeAccountSettings({ defaultGuildsActivityRestrictedV2: value })}
          choices={[{ value: 1, label: 'All servers' }, { value: 2, label: 'Up to 200 members' }, { value: 3, label: 'None' }]} />
        <AccountToggle label="Friends can join my game" settings={settings} field="allowActivityPartyPrivacyFriends" />
        <AccountToggle label="Voice channel members can join my game" settings={settings} field="allowActivityPartyPrivacyVoiceChannel" />
      </Section>
      <Section title="Direct messages">
        <SwitchRow label="Allow DMs from server members" checked={!dmsRestricted} onChange={value => applyDefault('dms', !value)} />
        <SwitchRow label="Filter DMs from people I may not know" checked={!dmsRestricted && !settings.defaultMessageRequestRestricted}
          disabled={dmsRestricted} onChange={value => applyDefault('requests', !value)} />
        <ChoiceRow label="Filter spam" value={spamFilter(settings, adult)} onChange={value => changeAccountSettings({ dmSpamFilterV2: value })}
          choices={[{ value: 3, label: 'All' }, { value: 2, label: 'From non-friends' }, { value: 1, label: 'Off' }]} />
      </Section>
      <Section title="Friend requests from">
        <SwitchRow label="Everyone" checked={everyone} onChange={value => changeAccountSettings({ friendSourceFlags: value ? 14 : 14 & ~8 })} />
        <SwitchRow label="Friends of friends" checked={everyone || (flags & 2) !== 0} disabled={everyone} onChange={value => setFlag(2, value)} />
        <SwitchRow label="Server members" checked={everyone || (flags & 4) !== 0} disabled={everyone} onChange={value => setFlag(4, value)} />
        <AccountToggle label="Show notes on friend requests" settings={settings} field="hideFriendRequestNotes" invert />
      </Section>
      <FilterSet kind="explicit" settings={settings} adult={adult} />
      <FilterSet kind="gore" settings={settings} adult={adult} />
      <Section title="Age-restricted">
        <AccountToggle label="Commands from apps in DMs" settings={settings} field="viewNsfwCommands" disabled={!adult} />
        <AccountToggle label="Servers on iOS" settings={settings} field="viewNsfwGuilds" disabled={!adult} />
      </Section>
      <IgnoreAndBlock />
      <Section title="Your data">
        <ChoiceRow label="Share my full profile with" value={settings.profileVisibility === 0 ? 3 : settings.profileVisibility}
          onChange={value => changeAccountSettings({ profileVisibility: value })}
          choices={[{ value: 3, label: 'Friends and all servers' }, { value: 2, label: 'Friends and small servers' }, { value: 1, label: 'Friends only' }]} />
        <LinkRow label="Request my data" button="Request" url="https://discord.com/settings/data-and-privacy" />
      </Section>
      {choice.menu}
    </>
  )
}

type Restricted = { blocked: DiscordRestricted[]; ignored: DiscordRestricted[] }

/** Discord's Ignore & Block: the people you blocked and ignored, each with Unblock or Unignore; shown while there are any. */
function IgnoreAndBlock() {
  const [lists, setLists] = useState<Restricted | null>(null)
  const load = () => host.discordFriends().then(result => setLists(result.restricted ?? null)).catch(() => { })
  useEffect(() => { void load() }, [])
  if (!lists || (!lists.blocked.length && !lists.ignored.length)) return null
  return (
    <>
      {lists.blocked.length ? <RestrictedList title="Blocked" people={lists.blocked} action="Unblock" run={userId => host.discordFriend('remove', { userId })} onDone={load} /> : null}
      {lists.ignored.length ? <RestrictedList title="Ignored" people={lists.ignored} action="Unignore" run={userId => host.discordFriend('unignore', { userId })} onDone={load} /> : null}
    </>
  )
}

// Five at a time, then See more, as Discord lists them; a press that Discord refused says why under the row.
function RestrictedList({ title, people, action, run, onDone }: { title: string; people: DiscordRestricted[]; action: string; run: (userId: string) => Promise<{ ok: boolean; message?: string | null }>; onDone: () => void }) {
  const [shown, setShown] = useState(5)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ id: string; message: string } | null>(null)
  const act = (id: string) => {
    setBusy(id)
    setError(null)
    void run(id).then(result => { if (!result.ok) setError({ id, message: result.message ?? "Discord didn't take that." }); onDone() },
      () => setError({ id, message: "Discord didn't take that." })).finally(() => setBusy(null))
  }
  return (
    <Section title={title}>
      {people.slice(0, shown).map(person => (
        <Row key={person.id} label={person.name} detail={person.username} icon={<Avatar src={person.avatar} name={person.name} status={null} size={32} />}
          error={error?.id === person.id ? error.message : null}>
          <button type="button" className="exo-set-btn" disabled={busy === person.id} onClick={() => act(person.id)}>{action}</button>
        </Row>
      ))}
      {people.length > shown ? <button type="button" className="exo-set-btn" onClick={() => setShown(n => n + 5)}>See {Math.min(5, people.length - shown)} more</button> : null}
    </Section>
  )
}

/** Whether the account may see age-restricted content (Discord's nsfw_allowed): adults get Show among the filters. */
function useAdult() {
  return useAccount().me?.nsfwAllowed ?? true
}

// What an unset default shows: the older setting (0 off, 1 large servers, 2 on) in the newer one's terms.
function activityDefault(settings: AccountSettings) {
  if (settings.defaultGuildsActivityRestrictedV2 !== 0) return settings.defaultGuildsActivityRestrictedV2
  return [1, 2, 3][settings.defaultGuildsActivityRestricted] ?? 1
}

/** Discord settings > Connected Apps: your connections, and the apps you've authorized (with Deauthorize). */
export function ConnectedAppsPage() {
  const hides = useStreamerHides()
  const connections = useLoad('connections', () => host.discordAccount('connections'), data => data as DiscordConnection[])
  const apps = useLoad('apps', () => host.discordAccount('apps'), data => data as DiscordAuthorizedApp[])
  const [failed, setFailed] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const deauthorize = (id: string) => {
    if (busy) return
    setFailed(null); setBusy(id)
    const why = "Discord didn't deauthorize that app."
    void host.discordAccount('deauthorize', { id })
      .then(result => { if (result.ok) apps.reload(); else setFailed(result.message ?? why) }, () => setFailed(why))
      .finally(() => setBusy(null))
  }
  return (
    <>
      <Section title="Connections">
        <LoadError error={connections.error} onRetry={connections.reload} />
        {connections.value?.map((c, i) => (
          <Row key={`${c.type}-${i}`} label={connectionLabel(c.type)}>
            <State>{[hides.personal ? 'Hidden' : c.name, c.revoked ? 'Disconnected' : c.verified ? null : 'Not verified'].filter(Boolean).join(' · ')}</State>
          </Row>
        ))}
        <LinkRow label="Add a connection" button="Add" url="https://discord.com/settings/connections" />
      </Section>
      {apps.error || failed || apps.value?.length ? (
        <Section title="Authorized apps">
          <LoadError error={apps.error} onRetry={apps.reload} />
          {failed ? <ErrorNote className="exo-dc-settings-note" message={failed} /> : null}
          {apps.value?.map(app => (
            <Row key={app.id} label={app.name ?? 'App'}>
              {/* Discord asks before an app loses its link to the account: here, on its button. */}
              <ConfirmButton className="exo-set-btn is-danger" question="Deauthorize?" danger disabled={busy !== null} onConfirm={() => deauthorize(app.id)}>Deauthorize</ConfirmButton>
            </Row>
          ))}
        </Section>
      ) : null}
    </>
  )
}
