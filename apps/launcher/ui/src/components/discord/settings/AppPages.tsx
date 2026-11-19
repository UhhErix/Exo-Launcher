import { useEffect, useState } from 'react'
import { host, type LauncherSettings } from '../../../lib/host'
import { onPerformanceMode, performanceMode, setPerformanceMode } from '../../../lib/discord'
import { changeAccountSettings, setDiscordPrefs, useAccountSettings, useDiscordPrefs, type DiscordClientSettings } from '../../../lib/discordPrefs'
import { readSettings, saveSettingsPatch } from '../../../lib/settingsSave'
import { ConfirmButton } from '../../ui/ConfirmButton'
import { Spinner } from '../../ui/Spinner'
import { ChoiceRow, Row, Section, SliderRow, SwitchRow } from '../../settings/parts'
import { AccountToggle, LoadError, useClientSettings, useRemembered } from './parts'

/** Discord settings > Notifications: Windows notifications and the badge, what Discord tells you about, and every sound. */
export function NotificationsPage() {
  const settings = useAccountSettings()
  const { settings: client, save, error, reload } = useClientSettings()
  const [launcher, setLauncher] = useRemembered<LauncherSettings>('launcher')
  useEffect(() => { readSettings().then(read => { if (read) setLauncher(read) }).catch(() => { }) }, [setLauncher])
  const quiet = launcher?.discordQuietWhilePlaying !== false
  return (
    <>
      <LoadError error={error} onRetry={reload} />
      <Section>
        <SwitchRow label="Desktop notifications" checked={client?.desktopNotifications ?? true}
          disabled={!client} onChange={value => save({ desktopNotifications: value })} />
        <SwitchRow label="Flash the taskbar" checked={client?.taskbarFlash ?? true}
          disabled={!client} onChange={value => save({ taskbarFlash: value })} />
        <SwitchRow label="Unread badge" checked={client?.unreadBadge ?? true}
          disabled={!client} onChange={value => save({ unreadBadge: value })} />
        <SwitchRow label="Quiet while playing" checked={quiet} disabled={!launcher}
          onChange={value => {
            setLauncher(s => s && { ...s, discordQuietWhilePlaying: value })
            return saveSettingsPatch({ discordQuietWhilePlaying: value }).then(() => null, () => {
              setLauncher(s => s && { ...s, discordQuietWhilePlaying: !value })
              return "Exo didn't save that. Try again."
            })
          }} />
      </Section>
      <Section title="Notify me about">
        <AccountToggle label="People I know going live" settings={settings} field="streamNotificationsEnabled" />
        <AccountToggle label="Friendship anniversaries" settings={settings} field="enableFriendAnniversaryNotifications" />
        <AccountToggle label="Friends coming online" settings={settings} field="enableFriendOnlineNotifications" />
        <AccountToggle label="Friends' profile updates" settings={settings} field="enableProfileUpdatesNotifications" />
        <ChoiceRow label="Reactions to my messages" value={settings.reactionNotifications} onChange={value => changeAccountSettings({ reactionNotifications: value })}
          choices={[{ value: 0, label: 'All messages' }, { value: 1, label: 'Only direct messages' }, { value: 2, label: 'Never' }]} />
      </Section>
      <Sounds client={client} save={save} />
    </>
  )
}

type Save = (change: Partial<DiscordClientSettings>) => Promise<string | null>

/** A sound's Preview, before its switch. */
function Preview({ label, sound }: { label: string; sound: string }) {
  return <button type="button" className="exo-set-btn is-compact" aria-label={`Preview ${label}`} onClick={() => void host.discordSound(sound, true).catch(() => { })}>Preview</button>
}

function SoundRow({ label, sound, client, save }: { label: string; sound: string; client: DiscordClientSettings | null; save: Save }) {
  const off = client?.disabledSounds.includes(sound) ?? false
  return (
    <SwitchRow label={label} checked={!off && !client?.disableAllSounds} disabled={!client || client.disableAllSounds}
      onChange={value => client ? save({ disabledSounds: value ? client.disabledSounds.filter(s => s !== sound) : [...client.disabledSounds, sound] }) : undefined}>
      <Preview label={label} sound={sound} />
    </SwitchRow>
  )
}

const CALL_SOUNDS: [string, string][] = [
  ['call_ringing', 'Incoming ring'], ['call_calling', 'Outgoing ring'], ['mute', 'Mute'], ['unmute', 'Unmute'], ['deafen', 'Deafen'], ['undeafen', 'Undeafen'],
  ['camera_on', 'Camera on'], ['camera_off', 'Camera off'], ['ptt_start', 'Push to talk on'], ['ptt_stop', 'Push to talk off'],
  ['user_join', 'Someone joins'], ['user_leave', 'Someone leaves'], ['disconnect', 'Disconnected'],
]

/** Every sound Discord's client plays, messages and calls alike: its volume, then each one's switch and Preview. */
function Sounds({ client, save }: { client: DiscordClientSettings | null; save: Save }) {
  return (
    <Section title="Sounds">
      <SwitchRow label="Mute all sounds" checked={client?.disableAllSounds ?? false} disabled={!client} onChange={value => save({ disableAllSounds: value })} />
      <SliderRow label="Sound volume" min={0} max={100} value={client?.soundVolume ?? 60} render={v => `${v}%`} disabled={!client || client.disableAllSounds}
        onCommit={v => void save({ soundVolume: v })} />
      <SoundRow label="New message" sound="message1" client={client} save={save} />
      <SwitchRow label="New message in the channel I'm reading" checked={(client?.selectedChannelSound ?? false) && !client?.disableAllSounds} disabled={!client || client.disableAllSounds}
        onChange={value => save({ selectedChannelSound: value })}>
        <Preview label="New message in the channel I'm reading" sound="message3" />
      </SwitchRow>
      {CALL_SOUNDS.map(([sound, label]) => <SoundRow key={sound} label={label} sound={sound} client={client} save={save} />)}
    </Section>
  )
}

const FONT_SIZES = [12, 14, 15, 16, 18, 20, 24]
const GROUP_SPACINGS = [0, 4, 8, 16, 24]
const ZOOMS = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200]

/** A slider over Discord's own stops (font sizes, spacings, zoom levels): it moves stop to stop. */
function StopsRow({ label, stops, value, unit, onCommit }: { label: string; stops: number[]; value: number; unit: string; onCommit: (value: number) => void }) {
  const at = Math.max(0, stops.indexOf(value))
  return <SliderRow label={label} min={0} max={stops.length - 1} value={at} render={i => `${stops[i]}${unit}`} onCommit={i => onCommit(stops[i])} />
}

/** Discord settings > Chat: what messages show, how they're drawn, how they move, how they're read out, and Streamer Mode. */
export function ChatPage() {
  const settings = useAccountSettings()
  const prefs = useDiscordPrefs()
  const [reduced, setReduced] = useState(performanceMode)
  useEffect(() => onPerformanceMode(() => setReduced(performanceMode())), [])
  return (
    <>
      <Section title="Messages">
        <AccountToggle label="Show images from links" settings={settings} field="inlineEmbedMedia" />
        <AccountToggle label="Show uploaded images" settings={settings} field="inlineAttachmentMedia" />
        <AccountToggle label="Show link previews" settings={settings} field="renderEmbeds" />
        <AccountToggle label="Show reactions" settings={settings} field="renderReactions" />
        <ChoiceRow label="Show spoilers" value={settings.renderSpoilers} onChange={value => changeAccountSettings({ renderSpoilers: value })}
          choices={[{ value: 'ON_CLICK', label: 'On click' }, { value: 'ALWAYS', label: 'Always' }, { value: 'IF_MODERATOR', label: 'On servers I moderate' }]} />
        <AccountToggle label="Show image descriptions" settings={settings} field="viewImageDescriptions" />
        <AccountToggle label="Turn emoticons into emoji" settings={settings} field="convertEmoticons" />
        <AccountToggle label="Suggest stickers as I type" settings={settings} field="includeStickersInAutocomplete" />
        <AccountToggle label="Developer Mode" settings={settings} field="developerMode" />
      </Section>
      <Section title="Display">
        <StopsRow label="Text size in chat" stops={FONT_SIZES} value={prefs.fontSize} unit="px" onCommit={v => setDiscordPrefs({ fontSize: v })} />
        <StopsRow label="Zoom" stops={ZOOMS} value={prefs.zoom} unit="%" onCommit={v => setDiscordPrefs({ zoom: v })} />
        <ChoiceRow label="Message density" value={settings.messageDisplayCompact ? 'compact' : 'default'}
          onChange={value => changeAccountSettings({ messageDisplayCompact: value === 'compact' })}
          choices={[{ value: 'default', label: 'Default' }, { value: 'compact', label: 'Compact' }]} />
        {settings.messageDisplayCompact ? (
          <SwitchRow label="Show avatars in compact mode" checked={settings.displayCompactAvatars}
            onChange={value => changeAccountSettings({ displayCompactAvatars: value })} />
        ) : null}
        <StopsRow label="Space between message groups" stops={GROUP_SPACINGS}
          value={prefs.groupSpacing ?? (settings.messageDisplayCompact ? 0 : 16)} unit="px"
          onCommit={v => setDiscordPrefs({ groupSpacing: v })} />
        <SwitchRow label="Underline links" checked={prefs.underlineLinks} onChange={value => setDiscordPrefs({ underlineLinks: value })} />
        <SliderRow label="Saturation" min={0} max={1} step={0.05} value={prefs.saturation} render={v => `${Math.round(v * 100)}%`} onCommit={v => setDiscordPrefs({ saturation: v })} />
        {prefs.saturation < 1 ? (
          <SwitchRow label="Saturation on custom colors" checked={prefs.desaturateUserColors} onChange={value => setDiscordPrefs({ desaturateUserColors: value })} />
        ) : null}
        <ChoiceRow label="Role colors" value={prefs.roleStyle} onChange={value => setDiscordPrefs({ roleStyle: value })}
          choices={[{ value: 'username', label: 'In names' }, { value: 'dot', label: 'Next to names' }, { value: 'hidden', label: 'Hidden' }]} />
        <ChoiceRow label="Time format" value={String(settings.timestampHourCycle)} onChange={value => void changeAccountSettings({ timestampHourCycle: Number(value) })}
          choices={[{ value: '0', label: 'Auto' }, { value: '1', label: '12-hour' }, { value: '2', label: '24-hour' }]} />
      </Section>
      <Section title="Motion">
        <SwitchRow label="Reduce motion" checked={reduced} onChange={setPerformanceMode} />
        <AccountToggle label="Play GIFs" settings={settings} field="gifAutoPlay" disabled={reduced} />
        <AccountToggle label="Play animated emoji" settings={settings} field="animateEmoji" disabled={reduced} />
        <ChoiceRow label="Play stickers" value={settings.animateStickers} onChange={value => changeAccountSettings({ animateStickers: value })}
          choices={[{ value: 0, label: 'Always' }, { value: 1, label: 'On interaction' }, { value: 2, label: 'Never' }]} />
      </Section>
      <Section title="Text-to-speech">
        <AccountToggle label="/tts command" settings={settings} field="enableTtsCommand" />
        <SliderRow label="Speed" min={0.5} max={2} step={0.25} value={prefs.ttsRate}
          render={v => `x${v.toFixed(2)}`} onCommit={v => setDiscordPrefs({ ttsRate: v })} />
      </Section>
      <Section title="Streamer Mode">
        <SwitchRow label="Streamer Mode" checked={prefs.streamerMode} onChange={value => setDiscordPrefs({ streamerMode: value })} />
        <SwitchRow label="Hide personal information" checked={prefs.streamerHidePersonal} onChange={value => setDiscordPrefs({ streamerHidePersonal: value })} />
        <SwitchRow label="Hide invite links" checked={prefs.streamerHideInvites} onChange={value => setDiscordPrefs({ streamerHideInvites: value })} />
        <SwitchRow label="Turn off sounds" checked={prefs.streamerDisableSounds} onChange={value => setDiscordPrefs({ streamerDisableSounds: value })} />
        <SwitchRow label="Turn off notifications" checked={prefs.streamerDisableNotifications} onChange={value => setDiscordPrefs({ streamerDisableNotifications: value })} />
      </Section>
    </>
  )
}

/** Discord's languages, as it lists them: a server's primary language (Server Settings > Community). */
export const LANGUAGES: { value: string; label: string }[] = [
  ['en-US', 'English, US'], ['en-GB', 'English, UK'], ['bg', 'български'], ['zh-CN', '中文'], ['zh-TW', '繁體中文'], ['hr', 'Hrvatski'], ['cs', 'Čeština'],
  ['da', 'Dansk'], ['nl', 'Nederlands'], ['fi', 'Suomi'], ['fr', 'Français'], ['de', 'Deutsch'], ['el', 'Ελληνικά'], ['hi', 'हिंदी'], ['hu', 'Magyar'],
  ['it', 'Italiano'], ['ja', '日本語'], ['ko', '한국어'], ['lt', 'Lietuviškai'], ['no', 'Norsk'], ['pl', 'Polski'], ['pt-BR', 'Português do Brasil'],
  ['ro', 'Română'], ['ru', 'Русский'], ['es-ES', 'Español'], ['es-419', 'Español, LATAM'], ['sv-SE', 'Svenska'], ['th', 'ไทย'], ['tr', 'Türkçe'],
  ['uk', 'Українська'], ['vi', 'Tiếng Việt'],
].map(([value, label]) => ({ value, label }))

/** Log Out, its row at the foot of Settings, asked in place (DESIGN.md › Confirm): Exo forgets your Discord sign-in on this PC. */
export function LogOut({ onDone }: { onDone: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const failed = () => { setBusy(false); setError("Exo couldn't log you out. Try again.") }
  return (
    <Row label="Log out" error={error}>
      <ConfirmButton className="exo-set-btn is-danger" question="Log out?" danger disabled={busy} lead={busy ? <Spinner label="Logging out" /> : null}
        onConfirm={() => { setBusy(true); setError(null); void host.discordSignOut().then(result => { if (result.ok) onDone(); else failed() }, failed) }}>
        Log out
      </ConfirmButton>
    </Row>
  )
}
