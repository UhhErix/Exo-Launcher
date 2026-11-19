import { idAt, type DiscordChannels, type DiscordDm, type DiscordFriend, type DiscordHome, type DiscordMemberList, type DiscordMessage, type DiscordVoiceNow } from './discord'

/** Made-up people and servers for the dev preview of Exo's own Discord client (never real accounts). */
const me = { id: '100000000000000001', username: 'you', global_name: 'You' }
const ada = { id: '100000000000000002', username: 'ada', global_name: 'Ada', primary_guild: { identity_guild_id: '200000000000000001', identity_enabled: true, tag: 'EXO', badge: null } }
const kai = { id: '100000000000000003', username: 'kai', global_name: 'Kai' }
const bot = { id: '100000000000000004', username: 'scrim-bot', global_name: 'Scrim Bot', bot: true }

/** The dev preview's state of Home: ?dc=empty (no one around), long (long names, many servers and DMs), signedout or connecting, few (a short, plain conversation). */
const scenario = typeof location === 'undefined' ? null : new URLSearchParams(location.search).get('dc')
export const mockDiscordStatus = scenario === 'signedout' ? { state: 'signedOut', signedIn: false } : scenario === 'connecting' ? { state: 'connecting', signedIn: true } : { state: 'ready', signedIn: true }
const LONG = 'The Extremely Long Server Name That Nobody Asked For But Somebody Typed Anyway'

const cdn = 'https://cdn.discordapp.com'
const face = (id: string) => `${cdn}/avatars/${id}/h${id.slice(-3)}.webp?size=256`
const mark = (id: string) => `${cdn}/icons/${id}/h${id.slice(-3)}.webp?size=256&quality=lossless`
const ago = (minutes: number) => Date.now() - minutes * 60_000
/** Someone in a DM: their picture, status, what was said last and when. */
const dmWith = (n: number, name: string, status: string, minutes: number, line: string | null, extra: Partial<DiscordDm> = {}): DiscordDm => {
  const userId = `1000000000000000${String(n).padStart(2, '0')}`
  return { id: `2000000000000000${String(n).padStart(2, '0')}`, name, avatar: face(userId), group: false, unread: false, mentions: 0, userId, username: name.toLowerCase(), status, lastAt: ago(minutes), line, ...extra }
}
const plate = (skuId: string, palette: string): DiscordDm['look'] => ({ nameplate: { skuId, palette } })

const baseHome: DiscordHome = {
  me: { id: me.id, name: 'You', avatar: face(me.id), status: 'online' },
  dms: [
    dmWith(1, 'Ada', 'online', 3, 'he always is, I can do 9pm or maybe a bit earlier if the others are up', { unread: true, mentions: 2, userId: '100000000000000002', avatar: face('100000000000000002') }),
    { id: '200000000000000002', name: 'Kai, Ada', avatar: null, group: true, unread: false, mentions: 0, ownerId: '100000000000000001', subtitle: '3 Members', lastAt: ago(14), line: 'Kai: the new patch broke my sens',
      members: [{ id: '100000000000000002', name: 'Ada', username: 'ada', avatar: face('100000000000000002'), status: 'online' }, { id: '100000000000000003', name: 'Kai', username: 'kai', avatar: face('100000000000000003'), status: 'idle' }],
      call: [{ id: '100000000000000003', name: 'Kai', avatar: face('100000000000000003'), mute: false, deaf: false }, { id: '100000000000000002', name: 'Ada', avatar: face('100000000000000002'), mute: true, deaf: false }] },
    dmWith(21, 'Rowan', 'online', 42, 'You: send me your config when you get a sec', { look: plate('plate-stars', 'cobalt') }),
    dmWith(22, 'Juno', 'idle', 96, 'did you see the patch notes', { unread: true, look: plate('plate-white', 'white') }),
    dmWith(23, 'Lazarus', 'dnd', 180, null, { subtitle: 'Deadlock', look: { nameplate: { skuId: 'plate-web', palette: 'crimson' }, nameStyle: { font: 0, effect: 1, colors: [0x3a1a8a] } } }),
    dmWith(24, 'Mira', 'offline', 1500, 'night!'),
    dmWith(25, 'Theo', 'online', 3000, 'You: good luck with the release'),
    dmWith(26, 'Tickets v2', 'online', 7000, null, { subtitle: '/help' }),
    dmWith(27, 'Odin', 'offline', 20000, 'thanks, that fixed it', { muted: true }),
    { id: '200000000000000003', name: 'Rook', avatar: null, group: false, unread: true, mentions: 0, userId: '100000000000000007', request: true },
    { id: '200000000000000004', name: 'free-nitro-bot', avatar: null, group: false, unread: true, mentions: 0, userId: '100000000000000008', request: true, spam: true },
  ],
  guilds: [
    { id: '300000000000000001', name: 'Night Raid', icon: mark('300000000000000001'), banner: 'nightraid', muted: false, unread: true, mentions: 1, inVoice: 2 },
    { id: '300000000000000002', name: 'Frame Pacing Club', icon: mark('300000000000000002'), muted: false, unread: false, mentions: 0, inVoice: 7 },
    { id: '300000000000000003', name: 'Speedrun Lab', icon: mark('300000000000000003'), banner: 'speedrun', muted: true, unread: false, mentions: 0 },
    { id: '300000000000000004', name: 'Bread Bank', icon: mark('300000000000000004'), banner: 'bread', muted: false, unread: true, mentions: 12 },
    { id: '300000000000000005', name: 'Wired In', icon: mark('300000000000000005'), muted: false, unread: true, mentions: 0 },
    { id: '300000000000000006', name: 'Studio Mouse Labs', icon: mark('300000000000000006'), banner: 'mouse', muted: false, unread: false, mentions: 0 },
    { id: '300000000000000007', name: 'Odin', icon: null, muted: false, unread: false, mentions: 0 },
    { id: '300000000000000008', name: 'Quiet Corner', icon: mark('300000000000000008'), muted: false, unread: false, mentions: 0 },
    { id: '300000000000000009', name: 'Hardware Heads', icon: mark('300000000000000009'), banner: 'hardware', muted: false, unread: true, mentions: 3, inVoice: 1 },
    { id: '300000000000000010', name: 'Late Shift', icon: mark('300000000000000010'), muted: false, unread: false, mentions: 0 },
  ],
  // As the host sends it: what is null is left out.
  rail: [
    { guildIds: ['300000000000000001'] },
    { guildIds: ['300000000000000004'] },
    { guildIds: ['300000000000000006'] },
    { guildIds: ['300000000000000009'] },
    { id: '1', name: 'Gaming', color: 0x5865f2, guildIds: ['300000000000000002', '300000000000000003', '300000000000000007'] },
    { guildIds: ['300000000000000005'] },
    { guildIds: ['300000000000000008'] },
    { guildIds: ['300000000000000010'] },
  ],
}

export const mockFew = scenario === 'few'

export const mockHome: DiscordHome = scenario === 'empty' ? { ...baseHome, dms: [], guilds: [], rail: [] } : scenario === 'long' ? {
  ...baseHome,
  me: { ...baseHome.me!, name: 'Someone With A Very Long Display Name Indeed' },
  dms: [
    ...baseHome.dms,
    ...Array.from({ length: 800 }, (_, i) => ({ id: `21${String(i).padStart(16, '0')}`, name: i % 7 ? `Person ${i}` : `${LONG} ${i}`, avatar: null, group: false, unread: i % 11 === 0, mentions: i % 40 === 0 ? 120 : 0, userId: `11${String(i).padStart(16, '0')}`, status: i % 3 ? 'online' : 'offline', subtitle: i % 5 ? null : 'Playing a game with quite a long name too', lastAt: ago(i * 37 + 1), line: i % 4 ? `${LONG} and then some more words so that the line has to be cut` : null })),
  ],
  guilds: Array.from({ length: 300 }, (_, i) => ({ id: `31${String(i).padStart(16, '0')}`, name: i % 6 ? `Server ${i}` : `${LONG} ${i}`, icon: mark(`31${String(i).padStart(16, '0')}`), banner: i % 3 ? `b${i}` : null, muted: i % 9 === 0, unread: i % 4 === 0, mentions: i % 25 === 0 ? 3 : 0, inVoice: i % 8 === 0 ? 4 : 0 })),
  rail: [
    { id: '1', name: LONG, color: 0xffffff, guildIds: Array.from({ length: 12 }, (_, i) => `31${String(i).padStart(16, '0')}`) },
    ...Array.from({ length: 288 }, (_, i) => ({ guildIds: [`31${String(i + 12).padStart(16, '0')}`] })),
  ],
} : baseHome

const person = (id: string, name: string) => ({ id, name, avatar: face(id) })
export const mockVoiceNow: DiscordVoiceNow[] = scenario === 'empty' ? [] : scenario === 'long' ? Array.from({ length: 60 }, (_, i) => ({
  channelId: `41${String(i).padStart(16, '0')}`, name: i % 4 ? `Room ${i}` : `${LONG} ${i}`, guildId: `31${String(i * 8).padStart(16, '0')}`, guild: i % 6 ? `Server ${i * 8}` : `${LONG} ${i * 8}`, icon: mark(`31${String(i * 8).padStart(16, '0')}`), muted: false, canConnect: true,
  count: 4, friends: 0, streaming: false, people: Array.from({ length: 4 }, (_, n) => person(`13${String(i * 10 + n).padStart(16, '0')}`, `Person ${n}`)),
})) : [
  { channelId: '400000000000000005', name: 'Lobby', guildId: '300000000000000001', guild: 'Night Raid', icon: mark('300000000000000001'), muted: false, canConnect: true, limit: 10, count: 2, friends: 2, streaming: true,
    people: [person(ada.id, 'Ada'), person(kai.id, 'Kai')] },
  { channelId: '400000000000000030', name: 'Squad', guildId: '300000000000000001', guild: 'Night Raid', icon: mark('300000000000000001'), muted: false, canConnect: true, limit: 6, count: 3, friends: 3, streaming: false,
    people: [person('100000000000000010', 'Bea'), person('100000000000000011', 'Cole'), person('100000000000000015', 'Rowan')] },
  { channelId: '400000000000000021', name: 'Frame time talk', guildId: '300000000000000002', guild: 'Frame Pacing Club', icon: mark('300000000000000002'), muted: false, canConnect: true, count: 7, friends: 1, streaming: false,
    people: Array.from({ length: 7 }, (_, n) => person(`13000000000000000${n}`, ['Rowan', 'Vale', 'Ivo', 'Wren', 'Tam', 'Oz', 'Ash'][n])) },
  { channelId: '400000000000000022', name: 'Bench', guildId: '300000000000000009', guild: 'Hardware Heads', icon: mark('300000000000000009'), muted: false, canConnect: false, count: 1, friends: 0, streaming: false,
    people: [person('130000000000000009', 'Quinn')] },
]
export const mockChannels: DiscordChannels = {
  name: 'Night Raid',
  threads: [{ id: '400000000000000042', name: 'Patch 9 hot takes', parentId: '400000000000000002', locked: false, joined: true, archived: false, unread: true, mentions: 1 }],
  channels: [
    // The mock-up's case (DiscordFriends.png): the first channel can be written in and has a topic.
    { id: '400000000000000001', name: 'welcome', type: 0, kind: 'text', topic: 'Scrims are Tuesdays at 8', canRead: true, canSend: true },
    { id: '400000000000000009', name: 'Text', type: 4, kind: 'category' },
    { id: '400000000000000002', name: 'general', type: 0, kind: 'text', topic: 'Talk about anything. Be kind.', canRead: true, canSend: true, unread: true, mentions: 1, canManageMessages: true, canPin: true, canEdit: true },
    { id: '400000000000000003', name: 'mod-chat', type: 0, kind: 'text', canRead: true, canSend: true, private: true, locked: true },
    { id: '400000000000000004', name: 'patch-notes', type: 5, kind: 'text', canRead: true, canSend: false, muted: true },
    { id: '400000000000000007', name: 'strategies', type: 15, kind: 'forum', canRead: true, canSend: true },
    { id: '400000000000000008', name: 'Voice', type: 4, kind: 'category' },
    { id: '400000000000000005', name: 'Lobby', type: 2, kind: 'voice', canConnect: true, userLimit: 10, status: 'Ranked night <:Troll:968812152267866172>', canSetStatus: true, canEdit: true,
      members: [{ id: ada.id, name: 'Ada', avatar: null, mute: false, deaf: false, video: true, stream: true }, { id: kai.id, name: 'Kai', avatar: null, mute: true, deaf: false, serverMute: true }] },
    { id: '400000000000000006', name: 'Scrims', type: 2, kind: 'voice', canConnect: true, locked: true },
  ],
  canManage: true,
  canManageEvents: true,
  moderation: { kick: true, ban: true, manageGuild: true, auditLog: true, manageRoles: true, timeout: true, manageChannels: true, manageExpressions: true, manageWebhooks: true, manageNicknames: true, prune: true, owner: true, mfa: true },
}

/** A voice member for the mock channel lists: a real default avatar, or none for the dot the room draws instead. */
const voiceMember = (id: string, name: string, avatar: string | null = null) => ({ id, name, avatar, mute: false, deaf: false })

/** The other servers' channel lists, for the ones the rail's voice tip reads (discord.channels answers per server). */
const mockChannelsByGuild: Record<string, DiscordChannels> = {
  '300000000000000002': {
    name: 'Frame Pacing Club',
    channels: [
      { id: '410000000000000001', name: 'general', type: 0, kind: 'text', canRead: true, canSend: true },
      { id: '410000000000000002', name: 'clips', type: 0, kind: 'text', canRead: true, canSend: true, unread: true, mentions: 12 },
      { id: '410000000000000003', name: 'lfg', type: 0, kind: 'text', canRead: true, canSend: true, unread: true },
      { id: '410000000000000010', name: 'Frame time talk', type: 2, kind: 'voice', canConnect: true,
        members: [
          voiceMember('130000000000000000', 'Rowan', 'https://cdn.discordapp.com/embed/avatars/1.png'),
          voiceMember('130000000000000001', 'Vale', 'https://cdn.discordapp.com/embed/avatars/2.png'),
          voiceMember('130000000000000002', 'Ivo'),
          voiceMember('130000000000000003', 'Wren', 'https://cdn.discordapp.com/embed/avatars/4.png'),
          voiceMember('130000000000000004', 'Tam'),
          voiceMember('130000000000000005', 'Oz', 'https://cdn.discordapp.com/embed/avatars/0.png'),
          voiceMember('130000000000000006', 'Ash'),
        ] },
      // Discord's tooltip leaves the AFK channel out, so neither does the tip.
      { id: '410000000000000011', name: 'Away', type: 2, kind: 'voice', canConnect: true, afk: true,
        members: [voiceMember('130000000000000007', 'Noor')] },
    ],
  },
  '300000000000000009': {
    name: 'Hardware Heads',
    channels: [
      { id: '490000000000000002', name: 'general', type: 0, kind: 'text', canRead: true, canSend: true },
      { id: '490000000000000001', name: 'Bench', type: 2, kind: 'voice', canConnect: false,
        members: [voiceMember('130000000000000009', 'Quinn', 'https://cdn.discordapp.com/embed/avatars/3.png')] },
    ],
  },
}

// Each other server's own short list: a welcome with its name in the topic, two of these, and a voice room.
const CHANNEL_NAMES = ['general', 'clips', 'lfg', 'builds', 'memes', 'help', 'screenshots', 'off-topic']
/** A server's channels as discord.channels answers: Night Raid's full list, the two above, else the server's own. */
export function mockChannelsFor(id: string): DiscordChannels {
  if (id === guildId) return mockChannels
  if (mockChannelsByGuild[id]) return mockChannelsByGuild[id]
  const name = mockHome.guilds.find(g => g.id === id)?.name ?? 'Server'
  const channel = (n: number) => `47${id.slice(-13)}${String(n).padStart(3, '0')}`
  const pick = Number(id.slice(-3)) % CHANNEL_NAMES.length
  return {
    name,
    channels: [
      { id: channel(1), name: 'welcome', type: 0, kind: 'text', topic: `Welcome to ${name}`, canRead: true, canSend: true },
      { id: channel(2), name: CHANNEL_NAMES[pick], type: 0, kind: 'text', canRead: true, canSend: true },
      { id: channel(3), name: CHANNEL_NAMES[(pick + 3) % CHANNEL_NAMES.length], type: 0, kind: 'text', canRead: true, canSend: true },
      { id: channel(4), name: 'Lounge', type: 2, kind: 'voice', canConnect: true },
    ],
  }
}

const NOW = Date.now()
const at = (minutes: number) => new Date(NOW - minutes * 60_000).toISOString()
// A message's id is a snowflake of its time, as Discord's are (the room orders, reads and dates by id): the milliseconds
// since Discord's epoch over 22 bits, its number in the low bits for two at the same moment.
const ids = new Map<number, string>()
const idOf = (n: number, minutes: number) => String(BigInt(idAt(NOW - minutes * 60_000)) + BigInt(n))
/** The id of the dev preview's message number n. */
export const mockMessageId = (n: number) => ids.get(n) ?? null
const msg = (id: number, author: typeof me, content: string, minutes: number, extra: Partial<DiscordMessage> = {}): DiscordMessage => {
  ids.set(id, idOf(id, minutes))
  return { id: idOf(id, minutes), channel_id: '400000000000000002', type: 0, author, content, timestamp: at(minutes), attachments: [], embeds: [], mentions: [], ...extra }
}

const pic = (n: number, filename: string, width: number, height: number) => ({ id: `73000000000000${String(n).padStart(4, '0')}`, filename, url: `https://cdn.discordapp.com/attachments/1/2/${filename}?n=${n}`, size: 48213, width, height, content_type: 'image/png' })

export const mockMessages: DiscordMessage[] = [
  msg(1, ada, 'anyone up for ranked tonight?', 95),
  msg(2, ada, 'I can do **9pm**, maybe earlier', 94, { flags: 4096 }),
  msg(3, kai, 'same. new patch broke my sens though 😭', 60, { reactions: [{ count: 2, me: true, emoji: { id: null, name: '😭' } }] }),
  msg(4, me, 'turn off mouse acceleration in Windows, then `400 dpi × 1.2` is what I use', 58, { edited_timestamp: at(57) }),
  msg(5, bot, '', 40, { embeds: [{ title: 'Scrim tonight', description: 'Starts <t:1792800000:R>. Sign up in #general.', color: 0x5865f2, fields: [{ name: 'Map', value: 'Ascent', inline: true }, { name: 'Slots', value: '3 of 10', inline: true }] }] }),
  msg(6, kai, '<@100000000000000001> are you in?', 12, { mentions: [me] }),
  msg(7, ada, '> are you in?\nhe always is', 11, { reactions: [{ count: 3, me: false, emoji: { id: null, name: '😂' } }, { count: 1, me: true, emoji: { id: null, name: '🔥' } }] }),
  msg(8, kai, '||I bought the battle pass||', 3),
  msg(9, ada, '', 2, {
    message_reference: { type: 1, channel_id: '400000000000000002', message_id: idOf(1, 95) },
    message_snapshots: [{ message: { content: 'patch notes are out:\n- sens fix\n- new map', timestamp: at(30) } }],
  }),
  msg(10, bot, 'Queued **3** players for Ascent.', 1, { type: 20, interaction_metadata: { name: 'scrim join', user: kai }, thread: { id: '600000000000000004', name: 'Scrim lineup', message_count: 4 } }),
  msg(11, ada, 'crosshair spots', 1, { attachments: [{ id: '700000000000000001', filename: 'clip1.png', url: 'https://cdn.discordapp.com/embed/avatars/1.png', size: 48213, width: 640, height: 360, content_type: 'image/png' }, { id: '700000000000000002', filename: 'clip2.png', url: 'https://cdn.discordapp.com/embed/avatars/2.png', size: 48213, width: 640, height: 360, content_type: 'image/png' }, { id: '700000000000000003', filename: 'clip3.png', url: 'https://cdn.discordapp.com/embed/avatars/3.png', size: 48213, width: 640, height: 360, content_type: 'image/png' }, { id: '700000000000000004', filename: 'clip4.png', url: 'https://cdn.discordapp.com/embed/avatars/4.png', size: 48213, width: 640, height: 360, content_type: 'image/png' }, { id: '700000000000000005', filename: 'clip5.png', url: 'https://cdn.discordapp.com/embed/avatars/5.png', size: 48213, width: 640, height: 360, content_type: 'image/png', description: 'The A site from heaven' }],
    reactions: [{ count: 4, me: false, emoji: { id: null, name: '🔥' }, count_details: { normal: 2, burst: 2 }, me_burst: false, burst_colors: ['#ff7a1a', '#ffd23f'] }, { count: 1, me: true, emoji: { id: null, name: '👀' } }] }),
  msg(13, kai, 'https://spotify.link/VmIbCrLn56b', 0.5),
  msg(15, ada, 'https://discord.gg/exo', 0.4),
  msg(16, kai, 'come scrim with us https://discord.gg/valorant', 0.3),
  msg(17, me, 'https://discord.gg/expired', 0.2),
  msg(18, ada, 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', 0.15, { embeds: [{ type: 'video', url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', title: 'Rick Astley - Never Gonna Give You Up', color: 0xff0000,
    provider: { name: 'YouTube' }, author: { name: 'Rick Astley', url: 'https://www.youtube.com/@RickAstleyYT' },
    thumbnail: { url: 'https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg', width: 480, height: 360 }, video: { url: 'https://www.youtube.com/embed/dQw4w9WgXcQ', width: 1280, height: 720 } }] }),
  msg(19, kai, 'https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT', 0.1, { embeds: [{ type: 'link', url: 'https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT', title: 'Never Gonna Give You Up', provider: { name: 'Spotify' }, description: 'Rick Astley · Song · 1987' }] }),
  msg(20, kai, 'https://x.com/valorant/status/1', 0.05, { embeds: [1, 2, 3, 4].map(n => ({ type: 'rich', url: 'https://x.com/valorant/status/1',
    ...(n === 1 ? { description: 'New agent drops next week. Four looks at the kit:', author: { name: 'VALORANT (@VALORANT)' }, footer: { text: 'X' }, color: 0x1d9bf0 } : {}),
    image: { url: `https://cdn.discordapp.com/embed/avatars/${n}.png`, width: 640, height: 360 } })) }),
  // Everything a message can carry (the dev preview of the media design): link cards, galleries, a video, a voice message.
  msg(50, ada, 'https://exo-launcher.example/blog/frame-pacing', 0.049, { embeds: [{ type: 'article', url: 'https://exo-launcher.example/blog/frame-pacing', title: 'Why your frame pacing stutters even at 240 fps and what to do about it today',
    provider: { name: 'Exo Journal' }, description: 'Frame time, not frame rate, is what your eyes feel. We measured twelve games on three GPUs and found the three settings that fix most of it, and the two that make it worse.',
    image: { url: 'https://cdn.discordapp.com/embed/avatars/1.png', width: 1200, height: 630 } }] }),
  msg(51, kai, 'https://notes.example/post/crosshair', 0.048, { embeds: [{ type: 'link', url: 'https://notes.example/post/crosshair', title: 'Crosshair settings, collected', provider: { name: 'Notes' },
    description: 'Every crosshair code the team uses, with the reasoning behind each one.', thumbnail: { url: 'https://cdn.discordapp.com/embed/avatars/3.png', width: 96, height: 96 } }] }),
  msg(52, ada, '', 0.047, { attachments: [pic(1, 'one.png', 1600, 900)] }),
  msg(53, ada, '', 0.046, { attachments: [pic(2, 'a.png', 800, 800), pic(3, 'b.png', 800, 800)] }),
  msg(54, ada, '', 0.045, { attachments: [pic(4, 'a.png', 800, 800), pic(5, 'b.png', 800, 500), pic(6, 'c.png', 800, 500)] }),
  msg(55, ada, '', 0.044, { attachments: [1, 2, 3, 4].map(n => pic(10 + n, `q${n}.png`, 800, 800)) }),
  msg(56, ada, 'the whole set', 0.043, { attachments: [1, 2, 3, 4, 5, 6, 7, 8].map(n => pic(20 + n, `s${n}.png`, 800, 600)) }),
  msg(57, kai, '', 0.042, { attachments: [{ id: '720000000000000001', filename: 'clutch.mp4', url: 'https://cdn.discordapp.com/attachments/1/2/clutch.mp4', size: 8_400_000, width: 1280, height: 720, content_type: 'video/mp4', duration_secs: 74 }] }),
  msg(58, ada, '', 0.041, { flags: 8192, attachments: [{ id: '720000000000000002', filename: 'voice-message.ogg', url: 'https://cdn.discordapp.com/attachments/1/2/voice-message.ogg', size: 20_000, content_type: 'audio/ogg', duration_secs: 14, waveform: btoa(String.fromCharCode(...Array.from({ length: 64 }, (_, i) => 30 + Math.round(120 * Math.abs(Math.sin(i / 5)) * (0.5 + 0.5 * Math.cos(i / 11)))))) }] }),
  msg(59, me, 'here is the line that matters:\n```ts\nconst sens = dpi * 1.2 // accel off\n```\nand `dpi` is 400', 0.04),
  msg(60, kai, 'agreed, try it', 0.039, { message_reference: { type: 0, channel_id: '400000000000000002', message_id: idOf(59, 0.04) }, referenced_message: msg(59, me, 'here is the line that matters', 0.04) }),
  msg(61, ada, '', 0.038, { poll: { question: { text: 'Which map tonight?' }, expiry: new Date(Date.now() + 5 * 3_600_000).toISOString(), answers: [
    { answer_id: 1, poll_media: { text: 'Ascent' } }, { answer_id: 2, poll_media: { text: 'Bind' } }, { answer_id: 3, poll_media: { text: 'Lotus' } }],
    results: { answer_counts: [{ id: 1, count: 6, me_voted: true }, { id: 2, count: 3, me_voted: false }, { id: 3, count: 1, me_voted: false }] } } }),
  msg(62, kai, '', 0.037, { sticker_items: [{ id: '800000000000000001', name: 'wave', format_type: 1 }] }),
  msg(63, ada, 'https://tenor.com/view/wow-gif-1', 0.036, { embeds: [{ type: 'gifv', url: 'https://tenor.com/view/wow-gif-1', video: { url: 'https://media.tenor.com/wow.mp4', width: 320, height: 240 } }] }),
  msg(12, me, 'my config', 0, { attachments: [
    { id: '710000000000000001', filename: 'autoexec.cfg', url: 'https://cdn.discordapp.com/attachments/1/2/autoexec.cfg', size: 2311, content_type: 'text/plain' },
    { id: '710000000000000002', filename: 'patch-notes.pdf', url: 'https://cdn.discordapp.com/attachments/1/2/patch-notes.pdf', size: 1843200, content_type: 'application/pdf' },
  ] }),
]

/** ?dc=few: a quiet conversation: plain words, one long paragraph, a group, a code block and a reply. */
export const mockFewMessages: DiscordMessage[] = [
  msg(41, me, 'Yo! Just had a quick question for ya when you are free', 7300),
  msg(42, me, 'I was the one that asked about the place to talk', 7298),
  msg(43, ada, 'hi!', 1560),
  msg(44, me, 'I wanted to ask you a question before I did it. In my bio on my GitHub I have the launcher app. It is basically an all in one app for a bunch of apps I am making. I wanted to host your tool inside of it for people, but I wanted to ask you first because I do not want to just take your stuff. I am not sure how it works with apps and stuff so I figured I would ask first. Of course I would put full credit in GitHub and the app itself, or you can just say no.', 1555),
  msg(45, me, 'also hope ur day is going great', 1554),
  msg(46, ada, 'send me your link and I will take a look!', 1548),
  msg(47, me, 'sure, here is the setting that matters:\n```\nsens = 400 dpi x 1.2 and accel off, then the rest of this line runs on so that a code block can be wider than the words around it, and wider still\n```', 1530),
  msg(48, ada, 'makin sure it worked', 1520, { message_reference: { type: 0, channel_id: '400000000000000002', message_id: idOf(46, 1548) }, referenced_message: msg(46, ada, 'send me your link and I will take a look!', 1548) }),
]

// An app's answers for the dev preview: buttons and a select, a Components V2 card, one only you see, one still thinking.
export const mockAppMessages: DiscordMessage[] = [
  msg(31, bot, 'Pick your role for the scrim:', 0.9, {
    application_id: bot.id, interaction_metadata: { name: 'scrim roles', user: me },
    components: [
      { type: 1, components: [
        { type: 2, style: 1, label: 'Duelist', custom_id: 'role:duelist' },
        { type: 2, style: 2, label: 'Support', custom_id: 'role:support', emoji: { id: null, name: '🛡️' } },
        { type: 2, style: 3, label: 'Ready', custom_id: 'ready' },
        { type: 2, style: 4, label: 'Leave', custom_id: 'leave' },
        { type: 2, style: 5, label: 'Rules', url: 'https://example.com/rules' },
        { type: 2, style: 2, label: 'Full', custom_id: 'full', disabled: true },
      ] },
      { type: 1, components: [{ type: 3, custom_id: 'map', placeholder: 'Choose a map', options: [{ label: 'Ascent', value: 'ascent', description: 'Mid control' }, { label: 'Bind', value: 'bind' }, { label: 'Haven', value: 'haven' }] }] },
    ],
  }),
  msg(32, bot, '', 0.6, {
    application_id: bot.id, flags: 32768,
    components: [{ type: 17, accent_color: 0xffffff, components: [
      { type: 9, components: [{ type: 10, content: '## Scrim tonight\nAscent at **9pm**. 3 of 10 slots left.' }], accessory: { type: 2, style: 1, label: 'Join', custom_id: 'join' } },
      { type: 14, divider: true, spacing: 1 },
      { type: 10, content: '-# Hosted by Scrim Bot' },
    ] }],
  }),
  msg(33, bot, 'Your sign-up is saved. Only you see this.', 0.3, { application_id: bot.id, flags: 64, interaction_metadata: { name: 'scrim join', user: me } }),
  msg(34, bot, '', 0.1, { application_id: bot.id, flags: 128, interaction_metadata: { name: 'scrim stats', user: me } }),
]

/** The commands the dev preview offers on /. */
export const mockApps = {
  ok: true,
  applications: [{ id: bot.id, name: 'Scrim Bot', icon: null }],
  commands: [
    { id: '800000000000000001', application_id: bot.id, type: 1, name: 'scrim', description: 'Scrims', options: [
      { type: 1, name: 'join', description: 'Join tonight\'s scrim', options: [
        { type: 3, name: 'role', description: 'Your role', required: true, choices: [{ name: 'Duelist', value: 'duelist' }, { name: 'Support', value: 'support' }] },
        { type: 5, name: 'sub', description: 'Join as a sub' },
      ] },
      { type: 1, name: 'stats', description: 'Your stats', options: [{ type: 6, name: 'player', description: 'Whose stats' }] },
    ] },
    { id: '800000000000000002', application_id: bot.id, type: 1, name: 'remind', description: 'Remind you later', options: [
      { type: 3, name: 'what', description: 'What to remind you of', required: true, max_length: 100 },
      { type: 4, name: 'minutes', description: 'In how many minutes', min_value: 1, max_value: 1440 },
    ] },
    { id: '800000000000000003', application_id: bot.id, type: 2, name: 'Scrim stats' },
    { id: '800000000000000004', application_id: bot.id, type: 3, name: 'Save to notes' },
  ],
}

const baseFriends: DiscordFriend[] = [
  { id: ada.id, name: 'Ada', username: 'ada', avatar: face(ada.id), kind: 'friend', status: 'online', activity: 'VALORANT', activityType: 0, dmId: '200000000000000001',
    rich: { type: 0, name: 'VALORANT', throughExo: true, start: Date.now() - 23 * 60_000 } },
  { id: '100000000000000010', name: 'Bea', username: 'bea', avatar: face('100000000000000010'), kind: 'friend', status: 'online', activity: 'Marvel Rivals', activityType: 0,
    rich: { type: 0, name: 'Marvel Rivals', details: 'Quick match', state: 'Tokyo 2099', start: Date.now() - 12 * 60_000 } },
  { id: '100000000000000011', name: 'Cole', username: 'cole', avatar: face('100000000000000011'), kind: 'friend', status: 'dnd', activity: 'Marvel Rivals', activityType: 0,
    rich: { type: 0, name: 'Marvel Rivals', details: 'Quick match', start: Date.now() - 12 * 60_000 } },
  { id: '100000000000000012', name: 'Dex', username: 'dex', avatar: face('100000000000000012'), kind: 'friend', status: 'online', activity: 'Rocket League', activityType: 0,
    rich: { type: 0, name: 'Rocket League', details: 'Ranked 2v2', start: Date.now() - 5 * 60_000 } },
  { id: '100000000000000021', name: 'Wren', username: 'wren', avatar: face('100000000000000021'), kind: 'friend', status: 'online', activity: 'Medieval Minecraft [FABRIC]', activityType: 0,
    rich: { type: 0, name: 'Medieval Minecraft [FABRIC]', details: 'Survival', state: 'Day 41', start: Date.now() - 95 * 60_000,
      image: 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="96" height="96"><rect width="96" height="96" fill="#3b7d3a"/><rect x="24" y="24" width="48" height="48" fill="#8a6a3b"/></svg>') } },
  { id: '100000000000000013', name: 'Eli', username: 'eli', avatar: face('100000000000000013'), kind: 'friend', status: 'online', activity: 'on in 20', activityType: 4 },
  { id: '100000000000000014', name: 'Finn', username: 'finn', avatar: null, kind: 'outgoing', status: 'offline' },
  { id: kai.id, name: 'Kai', username: 'kai', avatar: face(kai.id), kind: 'friend', status: 'idle', activity: 'Daft Punk', activityType: 2,
    rich: { type: 2, name: 'Spotify', details: 'Get Lucky', state: 'Daft Punk; Pharrell Williams; Nile Rodgers', start: Date.now() - 60_000, end: Date.now() + 309_000,
      syncId: '69kOkLUCkxIZYexIgSG8rq', album: 'Random Access Memories' } },
  { id: '100000000000000005', name: 'Mira', username: 'mira', avatar: face('100000000000000005'), kind: 'friend', status: 'offline' },
  // Getting ready for Marvel Rivals, as their Exo says it (Share when I'm getting ready).
  { id: '100000000000000015', name: 'Rowan', username: 'rowan', avatar: face('100000000000000015'), kind: 'friend', status: 'online', prep: { title: 'Marvel Rivals', kind: 'compiling' } },
  { id: '100000000000000016', name: 'Juno', username: 'juno', avatar: face('100000000000000016'), kind: 'friend', status: 'idle', prep: { title: 'Marvel Rivals', kind: 'installing', percent: 60 } },
  { id: '100000000000000017', name: 'Lazarus', username: 'lazarus', avatar: face('100000000000000017'), kind: 'friend', status: 'dnd', activity: 'Rest in peace in isolation', activityType: 4 },
  { id: '100000000000000018', name: 'Pixel', username: 'pixel', avatar: face('100000000000000018'), kind: 'friend', status: 'online', activity: 'Streaming ranked', activityType: 1, rich: { type: 1, name: 'Twitch', details: 'Streaming ranked', state: 'VALORANT' } },
  { id: '100000000000000019', name: 'Sol', username: 'sol', avatar: face('100000000000000019'), kind: 'friend', status: 'online', prep: { title: 'Marvel Rivals', kind: 'ready' } },
  { id: '100000000000000020', name: 'Nix', username: 'nix', avatar: face('100000000000000020'), kind: 'friend', status: 'idle', activity: 'MW 4 and Wardogs server', activityType: 4 },
  { id: '100000000000000006', name: 'Theo', username: 'theo', avatar: null, kind: 'incoming', status: 'online' },
]
export const mockFriends: DiscordFriend[] = scenario === 'empty' ? [] : scenario === 'long' ? [
  ...baseFriends,
  ...Array.from({ length: 600 }, (_, i): DiscordFriend => ({ id: `12${String(i).padStart(16, '0')}`, name: i % 7 ? `Friend ${i}` : `${LONG} ${i}`, username: `friend${i}`, avatar: null, kind: 'friend', status: i % 2 ? 'online' : 'offline' })),
  { id: '100000000000000099', name: LONG, username: 'long', avatar: null, kind: 'friend', status: 'online', activity: LONG, activityType: 0, rich: { type: 0, name: LONG, throughExo: true, start: Date.now() } },
] : baseFriends

export const mockMembers: DiscordMemberList = {
  online: 3,
  total: 4,
  items: [
    { group: 'Admins', count: 1 },
    { id: ada.id, name: 'Ada', avatar: null, color: '#e91e63', bot: false, status: 'online', activity: 'VALORANT', owner: true, roleIcon: { name: 'Admins', emoji: '🛡️' } },
    { group: 'Online', count: 2 },
    { id: kai.id, name: 'Kai', avatar: null, color: null, bot: false, status: 'idle', boosting: '2026-03-14T18:00:00.000000+00:00', inVoice: 'Lobby' },
    { id: bot.id, name: 'Scrim Bot', avatar: null, color: '#3498db', bot: true, status: 'online' },
    { group: 'Offline', count: 1 },
    { id: '100000000000000005', name: 'Mira', avatar: null, color: '#3a1a8a', bot: false, status: 'offline', look: { nameplate: { skuId: 'plate-web', palette: 'crimson' } } },
  ],
}

/** A profile with every part Discord sends (badges, connections, mutuals, a server member) and what Exo adds. */
export const mockProfile = {
  ok: true,
  note: 'Met in ranked',
  profile: {
    user: { ...ada, bio: 'Duelist main. **Ranked** grinder.', accent_color: 0xe91e63 },
    user_profile: { pronouns: 'she/her', accent_color: 0xe91e63, bio: 'Duelist main. **Ranked** grinder.' },
    guild_member: { nick: null, roles: ['300000000000000001'], joined_at: '2025-03-14T18:00:00Z' },
    badges: [
      { id: 'premium_tenure_60_month_v2', description: 'Earned 3/2/21. 5 years: Ruby', icon: 'cd5e2cfd9d7f27a8cdcd3e8a8d5dc9f4', link: 'https://discord.com/settings/premium' },
      { id: 'hypesquad_house_3', description: 'HypeSquad Balance', icon: '3aa41de486fa12454c3761e8e223442e' },
      { id: 'guild_booster_lvl9', description: 'Server boosting since 1/16/26', icon: 'ec92202290b48d0879b7413d2dde3bab', link: 'https://discord.com/settings/guild-boosting' },
      { id: 'legacy_username', description: 'Originally known as ada#0001', icon: '6de6d34650760ba5551a79732e98ed60' },
      { id: 'quest_completed', description: 'Completed a Quest', icon: '7d9ae358c8c5e118768335dbe68b4fb8', link: 'https://discord.com/discovery/quests' },
    ],
    connected_accounts: [
      { type: 'twitch', id: '1', name: 'ada_plays', verified: true },
      { type: 'github', id: '2', name: 'ada', verified: true },
      { type: 'spotify', id: 'ada', name: 'Ada', verified: true },
      { type: 'xbox', id: '3', name: 'Ada', verified: false },
      { type: 'steam', id: '76561197960287930', name: 'ada', verified: true },
      { type: 'bluesky', id: 'did:plc:ada', name: 'ada.bsky.social', verified: true },
      { type: 'domain', id: 'ada.dev', name: 'ada.dev', verified: true },
      { type: 'amazon-music', id: '4', name: 'Ada', verified: false },
    ],
    mutual_guilds: [{ id: '300000000000000001', nick: null }, { id: '300000000000000002', nick: 'ada (FPC)' }],
    mutual_friends_count: 1,
  },
  extras: {
    roles: [{ id: '300000000000000001', name: 'Mods', color: '#e91e63' }, { id: '300000000000000009', name: 'Ranked', color: null }],
    presence: { status: 'online', activity: 'VALORANT', activityType: 0, rich: { type: 0, name: 'VALORANT', details: 'Competitive', state: 'Ascent · 7-5', start: Date.now() - 23 * 60_000, image: null },
      activities: [
        { type: 0, name: 'VALORANT', details: 'Competitive', state: 'Ascent · 7-5', start: Date.now() - 23 * 60_000, image: null },
        { type: 2, name: 'Spotify', details: 'Never Gonna Give You Up', state: 'Rick Astley', album: 'Whenever You Need Somebody', start: Date.now() - 64_000, end: Date.now() + 149_000, syncId: '4cOdK2wGLETKBW3PvgPWqT', image: 'https://i.scdn.co/image/ab67616d0000b2735755e164993798e0c9ef7d7a' },
      ] },
    voice: { guildId: '300000000000000001', guild: 'Night Raid', channelId: '400000000000000005', channel: 'Lobby', count: 2 },
    relationship: 0,
  },
}

// Server Settings, Edit Channel and the person menu (discord.admin) for the dev preview: made-up roles, invites and logs.
const guildId = '300000000000000001'
const roles = [
  { id: guildId, name: '@everyone', color: 0, position: 0, hoist: false, mentionable: false, managed: false, permissions: String(1024 + 2048 + 65536 + 1048576 + 2097152) },
  { id: '900000000000000001', name: 'Admins', color: 0xe91e63, position: 3, hoist: true, mentionable: false, managed: false, permissions: '8' },
  { id: '900000000000000002', name: 'Mods', color: 0x3498db, position: 2, hoist: true, mentionable: true, managed: false, permissions: String(2 + 4 + 8192) },
  { id: '900000000000000003', name: 'Scrim Bot', color: 0, position: 1, hoist: false, mentionable: false, managed: true, permissions: '0' },
]
const people: Record<string, { name: string; username: string; roles: string[] }> = {
  [me.id]: { name: 'You', username: me.username, roles: ['900000000000000001'] },
  [ada.id]: { name: 'Ada', username: ada.username, roles: ['900000000000000002'] },
  [kai.id]: { name: 'Kai', username: kai.username, roles: [] },
}

// Discord's step-up: the owner's 2FA switch and Delete Server wait for a code (123456 passes in the mock).
const mockAsk = { id: '0123456789abcdef0123456789abcdef', methods: ['totp', 'sms', 'backup', 'password'] }
function mockStepUp(params: Record<string, unknown>): unknown {
  if (params.action !== 'mfa') return { ok: false, message: 'Discord wants to check it’s really you.', mfa: mockAsk }
  if (params.step === 'cancel') return { ok: true }
  if (params.step === 'sms') return { ok: true, data: { phone: '*******1234' } }
  return params.code === '123456' ? { ok: true, data: { level: 0 } } : { ok: false, message: 'Discord: Invalid two-factor code', mfa: mockAsk }
}

// Server Settings pages' reads.
const mockPages: Record<string, unknown> = {
  profile: { tag: 'NR', badge: 0, badge_color_primary: '#ff1c90', badge_color_secondary: '#ff7fc0', badge_hash: null, visibility: 1 },
  sounds: { items: [{ sound_id: '640000000000000001', name: 'gg', volume: 1, emoji_name: '🎉', user: ada }] },
  templates: [],
  widget: { enabled: false, channel_id: null },
  verification: { form_fields: [{ field_type: 'TERMS', values: ['Be kind.', 'No spam.'] }] },
  welcome: { description: 'Ranked nights and scrims.', welcome_channels: [{ channel_id: '400000000000000002', description: 'Say hi' }] },
  pending: { total: 3 },
  onboarding: {
    onboarding: {
      default_channel_ids: ['400000000000000001', '400000000000000002'], enabled: true, mode: 0,
      prompts: [{ id: '1100000000000000001', title: 'What brings you here?', single_select: false, required: true, in_onboarding: true, options: [
        { id: '1100000000000000011', title: 'Ranked', description: 'Climb with us', channel_ids: ['400000000000000007'], role_ids: [], emoji: { id: null, name: '🏆', animated: false } },
        { id: '1100000000000000012', title: 'Scrims', description: '', channel_ids: [], role_ids: ['900000000000000002'], emoji: null },
      ] }],
    },
    guide: {
      welcome_message: { author_ids: ['100000000000000001'], message: 'Hi [@username]! Welcome to Night Raid.' }, enabled: false,
      new_member_actions: [{ channel_id: '400000000000000002', action_type: 1, title: 'Say hi in general', description: '', emoji: { name: '👋' } }],
      resource_channels: [{ channel_id: '400000000000000001', title: 'Rules', description: 'Read before posting', emoji: null }],
    },
    seen: ['400000000000000001', '400000000000000009', '400000000000000002', '400000000000000004', '400000000000000007', '400000000000000008', '400000000000000005'],
    chats: ['400000000000000002', '400000000000000007'],
    me: '100000000000000001',
  },
  roleLinks: [[{ connection_type: 'steam', connection_metadata_field: null, operator: null, value: null }, { connection_type: 'steam', connection_metadata_field: 'game_count', operator: 4, value: '9' }]],
  // Two made-up badges in the shape Exo's host reads Discord's art into.
  badgeArt: { badges: [0, 7].map(id => ({ id, nodes: [{ tag: 'path', attrs: { d: 'M2 2h12v12H2z', fill: '#000' } }, { tag: 'path', attrs: { d: 'M3 3h10v10H3z', fill: { p: 1 } } }, { tag: 'path', attrs: { d: 'M6 6h4v4H6z', fill: { s: 0 } } }],
    primary: { lum: [0.2, 0.5], weights: [{ base: 1, tint: 1 }, { base: 1, tint: 1 }] }, secondary: { lum: [0.7], weights: [{ base: 1, tint: 1 }] } })) },
  discovery: {
    requirements: { safe_environment: true, size: false, minimum_size: 1000, age: true, minimum_age: 56, healthy: false, health_score_pending: true, protected: true, sufficient: false, nsfw_properties: {} },
    metadata: { primary_category_id: 1, keywords: ['ranked'], emoji_discoverability_enabled: true },
    categories: [{ id: 1, name: 'Gaming', is_primary: true }, { id: 2, name: 'Music', is_primary: true }, { id: 3, name: 'Esports', is_primary: false }],
  },
  insights: { small: false, engagement: [{ visitors: 120, communicators: 40 }, { visitors: 100, communicators: 44 }], growth: [{ new_members: 12 }, { new_members: 9 }], retention: [{ pct_retained: 42.5 }, { pct_retained: 40 }] },
}

// The step-up check's actions and the pages' reads, before the rest.
function mockPreset(params: Record<string, unknown>): unknown {
  const action = String(params.action)
  if (['mfa', 'guildMfa', 'deleteGuild', 'transfer'].includes(action)) return mockStepUp(params)
  return action in mockPages ? { ok: true, data: mockPages[action] } : undefined
}

const mockGuildNamed = (id: string) => ({ id, name: mockChannelsFor(id).name })

export function mockAdmin(params?: Record<string, unknown>): unknown {
  const ok = (data: unknown) => ({ ok: true, data })
  const preset = mockPreset(params ?? {})
  if (preset !== undefined) return preset
  switch (params?.action) {
    case 'guild': return ok({ ...mockGuildNamed(String(params.guildId ?? guildId)), icon: null, banner: null, description: 'Ranked nights and scrims.', owner_id: me.id, afk_channel_id: null, afk_timeout: 300,
      system_channel_id: '400000000000000002', system_channel_flags: 4, verification_level: 1, default_message_notifications: 1, explicit_content_filter: 2, mfa_level: 1, premium_tier: 1, features: ['COMMUNITY'], approximate_member_count: 1204 })
    case 'roles': return ok({ roles, counts: { '900000000000000001': 1, '900000000000000002': 1, '900000000000000003': 1 }, top: 2147483647 })
    case 'roleMembers': case 'searchMembers': return ok({ members: [{ member: { user: ada, nick: null, roles: people[ada.id].roles } }, { member: { user: kai, nick: null, roles: [] } }] })
    case 'emojis': return ok([{ id: '600000000000000001', name: 'gg', roles: [], user: ada }])
    case 'stickers': return ok([])
    case 'invites': return ok([{ code: 'nightraid', uses: 12, max_uses: 0, expires_at: null, inviter: ada, channel: { id: '400000000000000002', name: 'general' } }])
    case 'integrations': return ok({ webhooks: [{ id: '610000000000000001', name: 'Patch notes', channel_id: '400000000000000004', user: kai }],
      integrations: [{ id: '620000000000000001', name: 'Scrim Bot', type: 'discord', user: ada,
        application: { id: '630000000000000001', name: 'Scrim Bot', bot: { id: '630000000000000001', username: 'Scrim Bot', avatar: null }, role_connections_verification_url: 'https://scrims.example/verify' },
        role_connections_metadata: [{ key: 'scrims_won', name: 'Scrims won', description: 'Scrims won with a team', type: 2 }, { key: 'captain', name: 'Captain', description: 'Leads a team', type: 7 }] }] })
    case 'automod': return ok([{ id: '700000000000000001', name: 'Block Mention Spam', event_type: 1, trigger_type: 5, trigger_metadata: { mention_total_limit: 20, mention_raid_protection_enabled: true },
      actions: [{ type: 1, metadata: {} }], enabled: true, exempt_roles: [], exempt_channels: [] }])
    case 'audit': return ok({ users: [me, ada, kai], audit_log_entries: [
      { id: '1430000000000000001', action_type: 25, user_id: ada.id, target_id: kai.id, reason: 'Helping with scrims', changes: [{ key: '$add', new_value: [{ id: '900000000000000002', name: 'Mods' }] }] },
      { id: '1420000000000000001', action_type: 10, user_id: me.id, target_id: '400000000000000003', changes: [{ key: 'name', new_value: 'clips' }, { key: 'type', new_value: 0 }] },
    ] })
    case 'bans': return ok([{ user: { id: '100000000000000009', username: 'spam-account' }, reason: 'Spam links in every channel' }])
    case 'person': return ok(mockPerson(String(params.userId)))
    case 'channelSettings': return ok({ id: params.channelId, guildId, name: 'general', type: 0, topic: 'Talk about anything. Be kind.', nsfw: false, rateLimit: 0, userLimit: 0, bitrate: 64000,
      canManage: true, canPermissions: true, maxBitrate: 128000,
      overwrites: [{ id: guildId, type: 0, allow: '0', deny: '0' }, { id: '900000000000000002', type: 0, allow: '8192', deny: '0' }],
      roles: roles.map(r => ({ id: r.id, name: r.name, color: r.color ? `#${r.color.toString(16).padStart(6, '0')}` : null })), members: [] })
    case 'createWebhook': return ok({ id: '610000000000000009', type: 1, name: 'Captain Hook', channel_id: '400000000000000002', user: me })
    case 'createRole': return ok({ ...roles[2], id: '900000000000000099', name: 'new role', position: 1 })
    default: return ok(null)
  }
}

function mockPerson(userId: string) {
  const person = people[userId] ?? { name: 'Someone', username: 'someone', roles: [] }
  const mine = userId === me.id
  return {
    ok: true, known: true, me: mine, owner: mine, outranks: !mine, timedOut: userId === ada.id, nick: null, name: person.name, username: person.username, roles: person.roles,
    choices: roles.filter(r => r.id !== guildId).sort((a, b) => b.position - a.position)
      .map(r => ({ id: r.id, name: r.name, color: r.color ? `#${r.color.toString(16).padStart(6, '0')}` : null, assignable: !r.managed })),
    mfa: true,
    can: { roles: true, nick: true, timeout: !mine, kick: !mine, ban: !mine, transfer: !mine, mute: userId === ada.id, deafen: userId === ada.id, move: userId === ada.id },
    voice: userId === ada.id ? { channelId: '400000000000000005', mute: false, deaf: false } : null,
    moveTo: [{ id: '400000000000000006', name: 'Scrims' }],
  }
}
