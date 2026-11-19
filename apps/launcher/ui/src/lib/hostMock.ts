import { MOCK_SONG_MS, mockArtwork, spotifyMock } from './spotifyMock'
import { mockAdmin, mockAppMessages, mockFew, mockFewMessages, mockApps, mockChannels, mockChannelsFor, mockDiscordStatus, mockMessageId, mockFriends, mockHome, mockMembers, mockMessages, mockProfile } from './discordMock'
/** Browser development fixtures. Dynamically loaded only outside the packaged host. */
import type { AutopilotState, CompressSettings, CompressState, Game, GameProperties, LauncherSettings, InstallProgress, InstalledApp, ReplacedApp, RuntimeItem, StartupItem, StartupState, StoreGift } from './host'
import type { CaptureItem } from './capture'
import type { NoteInfo } from './notes'
import { emitHostEvent } from './host'
let mockMusicLevel = 0.6
/** PC › Autopilot: Counter-Strike 2 in front now, and two sessions' reports. */
let mockAutopilotChoices: Pick<AutopilotState, 'on' | 'gamePriority' | 'keepGameMode' | 'coreParking' | 'alwaysDemote' | 'suspend'> = {
  on: true, gamePriority: 'above', keepGameMode: false, coreParking: true, alwaysDemote: ['OneDrive'], suspend: [],
}
const mockAutopilot = (): AutopilotState => ({
  ...mockAutopilotChoices,
  gameMode: { off: mockAutopilotChoices.on && !mockAutopilotChoices.keepGameMode, keptOn: mockAutopilotChoices.keepGameMode ? 'you keep it on' : null },
  cpu: 'One CCD: every core shares one cache, so there is nothing to pin',
  now: mockAutopilotChoices.on ? { game: 'steam:730', did: ['Gave Counter-Strike 2 above normal priority', 'Held back OneDrive, steamwebhelper (efficiency mode, lower priority, low disk and memory priority)'] } : null,
  live: {
    game: 'steam:730', title: 'Counter-Strike 2', seconds: 1260, fps: 287.4, low1: 191.2, low01: 140.6, stutters: 4,
    cpu: 46, gpu: 88, gpuTemp: 71, vramGb: 5.8, vramTotalGb: 8, ramGb: 13.2, ramTotalGb: 32, standbyGb: 9.4, freeGb: 6.1, dpc: 0.6, clock: 112,
  },
  reports: [
    {
      id: 'steam:730:2026-11-19T18:02:00Z', game: 'steam:730', title: 'Counter-Strike 2', started: '2026-11-19T18:02:00Z', minutes: 47,
      fps: 281.2, low1: 186.4, low01: 121.8, stutters: 23,
      findings: [
        { kind: 'background', stutters: 14, app: 'OneDrive', detail: '14 stutters came as OneDrive jumped to 1.8 cores in the background.', suggestion: 'Autopilot holds OneDrive back the next time you play Counter-Strike 2.' },
        { kind: 'shaders', stutters: 6, app: null, detail: '6 stutters came while the game wrote to its shader cache: most likely compiling shaders.', suggestion: 'These go away as the shaders are kept; Exo Shaders can compile them ahead (PC › Shaders).' },
        { kind: 'unexplained', stutters: 3, app: null, detail: '3 stutters came with nothing Exo can see on the PC.', suggestion: 'Most likely the game itself: streaming a new area, or its own work on the main thread.' },
      ],
      did: ['Gave Counter-Strike 2 above normal priority', 'Held back 6 background apps (efficiency mode, low disk and memory priority)', 'Kept every processor core awake'],
      learned: ['OneDrive'],
      peaks: { cpu: 71, gpu: 99, gpuTemp: 76, vramGb: 6.4, vramTotalGb: 8, ramGb: 14.1, ramTotalGb: 32 },
      islcSeconds: 0,
    },
    {
      id: 'steam:1086940:2026-11-18T20:10:00Z', game: 'steam:1086940', title: "Baldur's Gate 3", started: '2026-11-18T20:10:00Z', minutes: 132,
      fps: 96.3, low1: 61.0, low01: 38.2, stutters: 61,
      findings: [
        { kind: 'vram', stutters: 41, app: null, detail: "41 stutters came with the graphics card's memory 99% full (8 GB).", suggestion: 'Lower texture quality or the resolution a step: what doesn’t fit is read over PCIe from system memory.' },
        { kind: 'gpuHeat', stutters: 12, app: null, detail: '12 stutters came with the graphics card at 88 °C, where cards lower their clocks.', suggestion: 'Check the card’s fans and the case’s airflow, or cap the frame rate a little below your screen’s refresh.' },
      ],
      did: [], learned: [],
      peaks: { cpu: 58, gpu: 100, gpuTemp: 88, vramGb: 7.9, vramTotalGb: 8, ramGb: 18.7, ramTotalGb: 32 },
      islcSeconds: 0,
    },
  ],
})
const MOCK_GAMES: Game[] = [
  {
    id: 'local:add',
    title: 'Add portable game',
    store: 'local',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Ready',
    deps: [],
    launchNote: '',
    isAddPortable: true,
  },
  {
    id: 'mock:valorant',
    title: 'VALORANT',
    store: 'riot',
    coverUrl: 'https://cdn.cloudflare.steamstatic.com/steam/apps/1145360/library_hero.jpg',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Demo',
    playtimeMinutes: 0,
    sizeBytes: 30 * 1024 ** 3,
    deps: ['Riot Client', 'Vanguard'],
    launchNote: '',
  },
  {
    id: 'mock:hades',
    title: 'Hades',
    store: 'steam',
    installed: true,
    owned: true,
    primaryAction: 'play',
    status: 'Ready',
    playtimeMinutes: 1240,
    sizeBytes: 15 * 1024 ** 3,
    deps: ['Steam client'],
    launchNote: '',
    lastPlayedUtc: new Date(Date.now() - 26 * 3600_000).toISOString(),
  },
  {
    // Lent by the Steam Family: not installed, its owner named on its page.
    id: 'steam:1145350',
    title: 'Hades II',
    store: 'steam',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Not installed',
    deps: ['Steam client'],
    launchNote: 'Shared by Sam through Steam Family. Installs through Steam.',
    familyShared: true,
    sharedBy: 'Sam',
  },
  {
    // Steam's Play menu offers it without its anti-cheat: Properties › Anti-cheat.
    id: 'steam:3472040',
    title: 'NBA 2K26',
    store: 'steam',
    installed: true,
    owned: true,
    primaryAction: 'play',
    status: 'Ready',
    playtimeMinutes: 310,
    sizeBytes: 140 * 1024 ** 3,
    deps: ['Steam client'],
    launchNote: '',
  },
  {
    // One Steam app holding the games bought as its DLC: named by the newest, and found by any of them.
    id: 'steam:1938090',
    title: 'Call of Duty®: Modern Warfare® 4',
    store: 'steam',
    installed: true,
    owned: true,
    includes: [
      { appId: '4435490', title: 'Call of Duty®: Modern Warfare® 4' },
      { appId: '3606480', title: 'Call of Duty®: Black Ops 7' },
      { appId: '1938090', title: 'Call of Duty®' },
    ],
    coverUrl: 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4435490/c71bc2cb60e440d1a0d2435d0c06c4d4a4050edf/library_capsule_2x.jpg',
    playtimeMinutes: 14804,
    sizeBytes: 66418512230,
    status: 'Ready',
    deps: [],
    launchNote: '',
    launchTarget: '1938090',
  },
  {
    id: 'mock:eldenring',
    title: 'Elden Ring',
    store: 'steam',
    installed: true,
    owned: true,
    primaryAction: 'play',
    status: 'Ready',
    playtimeMinutes: 4380,
    sizeBytes: 49 * 1024 ** 3,
    deps: [],
    launchNote: '',
    lastPlayedUtc: new Date(Date.now() - 3 * 3600_000).toISOString(),
    isRunning: true,
  },
  {
    id: 'mock:rivals',
    title: 'Marvel Rivals',
    store: 'steam',
    launchTarget: 'steam://rungameid/2767030',
    installed: true,
    owned: true,
    primaryAction: 'play',
    status: 'Ready',
    playtimeMinutes: 3125,
    sizeBytes: 74083193346,
    deps: [],
    launchNote: '',
    lastPlayedUtc: new Date(Date.now() - 2 * 86400_000).toISOString(),
  },
  {
    id: 'mock:fortnite',
    title: 'Fortnite',
    store: 'epic',
    installed: true,
    owned: true,
    primaryAction: 'play',
    status: 'Ready',
    playtimeMinutes: 980,
    sizeBytes: 31 * 1024 ** 3,
    deps: [],
    launchNote: '',
  },
  {
    id: 'mock:cyberpunk',
    title: 'Cyberpunk 2077',
    store: 'gog',
    installed: true,
    owned: true,
    primaryAction: 'play',
    status: 'Ready',
    playtimeMinutes: 2210,
    sizeBytes: 70 * 1024 ** 3,
    deps: [],
    launchNote: '',
    updateAvailable: true,
    lastPlayedUtc: new Date(Date.now() - 5 * 86400_000).toISOString(),
  },
  {
    id: 'mock:control',
    title: 'Control',
    store: 'epic',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Demo',
    playtimeMinutes: 720,
    sizeBytes: 42 * 1024 ** 3,
    deps: [],
    launchNote: '',
  },
  {
    id: 'mock:disco',
    title: 'Disco Elysium',
    store: 'gog',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Demo',
    playtimeMinutes: 2100,
    sizeBytes: 20 * 1024 ** 3,
    deps: [],
    launchNote: '',
  },
]

/** Genres and modes as Steam's record words them (SteamAppTags.cs), for the Library's search and Filter menu in the mock. */
const MOCK_TAGS: Record<string, string[]> = {
  'mock:hades': ['action', 'rpg', 'indie', 'single-player', 'controller'],
  'steam:3472040': ['sports', 'simulation', 'single-player', 'multiplayer', 'co-op', 'local co-op', 'controller'],
  'steam:1938090': ['action', 'single-player', 'multiplayer', 'co-op', 'controller'],
  'mock:eldenring': ['action', 'rpg', 'single-player', 'multiplayer', 'co-op', 'controller'],
  'mock:rivals': ['action', 'free to play', 'multiplayer', 'controller'],
}
for (const game of MOCK_GAMES) if (MOCK_TAGS[game.id]) game.tags = MOCK_TAGS[game.id]
const familyGame = MOCK_GAMES.find(game => game.id === 'steam:3472040')
if (familyGame) familyGame.familyShared = true

/**
 * `?games=N` in browser dev: N more made-up games (no real titles), for the grid's windowing, the scrubber and search
 * at the size of a big library.
 */
const mockExtraGames = typeof location === 'undefined' ? 0 : Math.min(2000, parseInt(new URLSearchParams(location.search).get('games') ?? '', 10) || 0)
{
  const first = ['Ashen', 'Bright', 'Cinder', 'Distant', 'Echo', 'Fallen', 'Glass', 'Hollow', 'Iron', 'Jade', 'Kindred', 'Lunar', 'Molten', 'Night', 'Outer', 'Pale', 'Quiet', 'Rust', 'Silent', 'Tidal', 'Umbral', 'Velvet', 'Wild', 'Xeno', 'Yonder', 'Zero']
  const second = ['Circuit', 'Drift', 'Frontier', 'Harbor', 'Kingdom', 'Legion', 'Orbit', 'Pilgrim', 'Rally', 'Signal', 'Tactics', 'Voyage']
  const stores = ['steam', 'steam', 'steam', 'epic', 'gog', 'xbox', 'ea', 'ubisoft']
  const genres = ['action', 'adventure', 'rpg', 'strategy', 'simulation', 'racing', 'sports', 'casual', 'indie']
  const modes = [['single-player'], ['multiplayer'], ['single-player', 'co-op'], ['multiplayer', 'co-op', 'local co-op'], ['single-player', 'controller']]
  for (let i = 0; i < mockExtraGames; i++) {
    const store = stores[i % stores.length]
    const installed = i % 3 === 0
    const played = i % 4 !== 1
    MOCK_GAMES.push({
      id: `mock:extra-${i}`,
      title: `${first[i % first.length]} ${second[Math.floor(i / first.length) % second.length]}${i >= first.length * second.length ? ` ${Math.floor(i / (first.length * second.length)) + 1}` : ''}`,
      // Made-up art addresses: the UI pass's harness draws a picture for each; anywhere else they fail and the card shows its name.
      coverUrl: i % 5 === 4 ? null : `https://cdn.cloudflare.steamstatic.com/steam/apps/${900000 + i}/library_600x900.jpg`,
      store,
      installed,
      owned: true,
      canInstall: !installed,
      primaryAction: installed ? 'play' : 'install',
      status: installed ? 'Ready' : 'Demo',
      playtimeMinutes: played ? (i * 137) % 9000 : 0,
      lastPlayedUtc: played ? new Date(Date.now() - ((i * 9) % 800) * 86_400_000).toISOString() : null,
      sizeBytes: ((i * 13) % 140 + 2) * 1024 ** 3,
      deps: [],
      launchNote: '',
      tags: store === 'steam' ? [genres[i % genres.length], ...modes[i % modes.length]] : undefined,
      familyShared: store === 'steam' && i % 11 === 0,
    })
  }
}

/** `?onboarding` in browser dev replays first-run setup against the mock. */
const mockFirstRun = typeof location !== 'undefined' && new URLSearchParams(location.search).has('onboarding')
// ?backup: a daily backup from yesterday exists (Last backup in Settings; the restore offer on first run); Back up now makes today's.
let mockBackupDay: string | null = typeof location !== 'undefined' && new URLSearchParams(location.search).has('backup') ? new Date(Date.now() - 86_400_000).toISOString().slice(0, 10) : null

/** `?shaders` in browser dev: an Exo Shaders engine with two games whose compile time a scan learns; `?shaders=N` lists N. */
const mockShaders = typeof location !== 'undefined' && new URLSearchParams(location.search).has('shaders')
const mockShaderCount = typeof location !== 'undefined' ? Math.max(0, parseInt(new URLSearchParams(location.search).get('shaders') ?? '', 10) || 0) : 0
/** `?shaders-gone`: after the first snapshot read the engine answers none and keeps posting its stopped state, as a real exit with no saved state does. */
const mockShaderGone = typeof location !== 'undefined' && new URLSearchParams(location.search).has('shaders-gone')
let mockShaderReads = 0
if (mockShaderGone) setInterval(() => emitHostEvent('shaders.event', { name: 'addon', data: { state: 'notRunning' } }), 2500)
let mockShadersScanned = false
const mockShadersSnapshot = () => ({
  rev: mockShadersScanned ? 2 : 1,
  app: { scanning: false },
  queue: { current: null, waiting: [], finished: [], running: false, paused: false, canStart: false },
  games: [
    { id: 'cyberpunk', name: 'Cyberpunk 2077', statusText: 'Ready to compile', tone: 'primary', inAddAllReady: true, actions: { compile: true }, compileTime: mockShadersScanned ? '1 h 5 min' : undefined },
    { id: 'hogwarts', name: 'Hogwarts Legacy', statusText: 'Ready to compile', tone: 'primary', inAddAllReady: true, actions: { compile: true }, compileTime: mockShadersScanned ? '12 min' : undefined },
    ...Array.from({ length: mockShaderCount }, (_, i) => ({ id: `shader-game-${i}`, name: `Shader Game ${i + 1} With A Fairly Long Name`, statusText: i % 3 ? 'Ready to compile' : 'Compiled', tone: i % 3 ? 'primary' : 'muted', inAddAllReady: true, actions: { compile: i % 3 !== 0 }, compileTime: `${(i % 7) + 2} min` })),
  ],
})

/** `?gpu=amd` or `?gpu=intel` in browser dev: System's profile and a game's frame generation for that GPU instead of a GeForce. */
const mockGpu = typeof location !== 'undefined' ? new URLSearchParams(location.search).get('gpu') : null

/** `?hdr=none`: a main screen without HDR (PC › Display has no HDR rows). `?pad=bluetooth`: the DualSense Edge over Bluetooth (its cable-only rows go). */
const mockNoHdr = typeof location !== 'undefined' && new URLSearchParams(location.search).get('hdr') === 'none'
const mockPadLink = typeof location !== 'undefined' && new URLSearchParams(location.search).get('pad') === 'bluetooth' ? 'bluetooth' as const : 'usb' as const

/** PC > Auto tune on the mock: each read moves a run on one step, ?tune=crashed starts after a crash, ?tune=none has nothing to tune, and the Ryzen row is hidden as on the host (RyzenAvailable is false) unless ?tune=ryzen shows it needing PawnIO or ?tune=pawnio has the driver. */
const mockTuneFlag = typeof location !== 'undefined' ? new URLSearchParams(location.search).get('tune') : null
const mockTuneReads: Record<string, number | undefined> = {}
const mockTuneDone = new Set<string>()
function mockTune(p?: Record<string, unknown>) {
  if (mockTuneFlag === 'none') return { ok: true, parts: [] }
  const id = typeof p?.part === 'string' ? p.part : ''
  if (p?.action === 'start') mockTuneReads[id] = 0
  if (p?.action === 'stop') delete mockTuneReads[id]
  if (p?.action === 'undo') { delete mockTuneReads[id]; mockTuneDone.delete(id) }
  const gpu = mockGpu === 'amd' ? 'radeon' : 'nvidia'
  const words: Record<string, string> = {
    nvidia: 'Moves your card\u2019s clock up at each voltage and tests each step, about 40 minutes. It\u2019s outside NVIDIA\u2019s specifications, can void your warranty, and can crash your PC while it tests, so save your work first. Exo goes back to stock by itself if anything goes wrong.',
    radeon: 'Runs AMD\u2019s own tuning, then tests it, about 32 minutes. It replaces any tuning you\u2019ve set in AMD Software, and Undo clears that too. It\u2019s outside AMD\u2019s specifications, can void your warranty, and can crash your PC while it tests, so save your work first. Exo goes back to stock by itself if anything goes wrong.',
    ryzen: 'Lowers your processor\u2019s voltage with Curve Optimizer and tests one core at a time, about 78 minutes. AMD says running outside its specifications voids your warranty. It can crash your PC while it tests, so save your work first. Exo goes back to stock by itself if anything goes wrong. What passes comes back at each sign-in.',
  }
  const pawnio = 'Needs PawnIO, a signed driver from pawnio.eu that stays loaded until you uninstall it from Windows. FACEIT has blocked it, and other anti-cheats haven\u2019t said either way: if a game won\u2019t start, uninstall it.'
  const part = (one: 'nvidia' | 'radeon' | 'ryzen') => {
    const base = { id: one, modes: one !== 'ryzen', mode: 'cooler', minutes: one === 'nvidia' ? 40 : one === 'radeon' ? 32 : 78 }
    if (one === 'ryzen' && mockTuneFlag !== 'pawnio') return { ...base, state: 'needs-driver', warning: pawnio, canUndo: false }
    const reads = mockTuneReads[one]
    if (reads !== undefined) {
      if (reads > 5) { delete mockTuneReads[one]; mockTuneDone.add(one) } else {
        mockTuneReads[one] = reads + 1
        const offset = reads * 30
        return { ...base, state: 'testing', testing: offset ? `Testing ${one === 'ryzen' ? `Curve Optimizer -${reads * 5}` : `+${offset} MHz`}` : 'Measuring at stock', minutesLeft: 30 - reads * 5, canUndo: true }
      }
    }
    if (mockTuneDone.has(one)) return { ...base, state: 'tuned', applied: one === 'ryzen' ? 'Curve Optimizer -15' : one === 'radeon' ? 'AMD\u2019s tuning' : '+120 MHz', before: { score: 412, peakC: 72 }, after: { score: 410, peakC: 67 }, canUndo: true }
    if (mockTuneFlag === 'crashed') return { ...base, state: 'crashed', note: 'Your PC restarted while Exo tested this. That value is marked failed, and you\u2019re back at stock.', canUndo: true }
    return { ...base, state: 'ready', warning: words[one], canUndo: false }
  }
  return { ok: true, parts: mockTuneFlag === 'ryzen' || mockTuneFlag === 'pawnio' ? [part(gpu), part('ryzen')] : [part(gpu)] }
}
let mockFrameGen = 'game'
const mockRiotPartial = typeof location !== 'undefined' && new URLSearchParams(location.search).get('riot') === 'partial'
// Best settings' steps stay set once applied, as on a real PC.
const mockApplied = new Set<string>()
const mockApply = (step: string, p?: { action?: unknown }) => { if (p?.action === 'apply') mockApplied.add(step); return mockApplied.has(step) }

/** `?cpu=intel` in browser dev: This PC and Settings' chipset switch for an Intel processor instead of a Ryzen. */
const mockIntelCpu = typeof location !== 'undefined' && new URLSearchParams(location.search).get('cpu') === 'intel'

/**
 * Best settings › Network in browser dev: an Intel I226-V with Energy-Efficient Ethernet on, DHCP DNS and Windows' TCP
 * defaults, so the group has its adapter and Cloudflare DNS items. network sets every item kept on (with a check before
 * and after), network-on and network-off one item. `?net=fine` has nothing to set on the adapter, `?net=slow` a link held
 * at 100 Mbps by its cable, `?net=wifi` a Wi-Fi adapter with no IPv6, `?net=tcp` receive window tuning disabled.
 */
const mockNet = typeof location !== 'undefined' ? new URLSearchParams(location.search).get('net') : null
const mockNetOn = new Set<string>()
const mockNetOff = new Set<string>()
let mockCheck: Record<string, unknown> | null = null
const mockNetItems = (p?: { action?: unknown; item?: unknown }) => {
  const offered = [...(mockNet === 'wifi' || mockNet === 'fine' ? [] : ['adapter']), 'dns', ...(mockNet === 'tcp' ? ['tcp'] : [])]
  const before = { ping: 21, jitter: 4, router: 2, lookup: 38 }
  if (p?.action === 'network') {
    for (const key of offered) if (!mockNetOff.has(key)) mockNetOn.add(key)
    mockCheck = { before, after: { ping: 20, jitter: 4, router: 2, lookup: 11 }, said: 'Lookups fell from 38 ms to 11 ms.' }
  }
  if (p?.action === 'netcheck') mockCheck = { before: null, after: before, said: '' }
  if (typeof p?.item === 'string') {
    if (p.action === 'network-on') { mockNetOn.add(p.item); mockNetOff.delete(p.item) }
    if (p.action === 'network-off') { mockNetOn.delete(p.item); mockNetOff.add(p.item) }
  }
  const words: Record<string, [string, string, string]> = {
    adapter: ['Adapter settings for games', 'Energy-efficient Ethernet on.', 'Power saving that drops the link is off.'],
    dns: ['Cloudflare DNS, encrypted', mockNet === 'wifi' ? 'Faster lookups, and your DNS is private. IPv4 only on Wi-Fi: it has no IPv6.' : 'Faster lookups, and your DNS is private.', ''],
    tcp: ["Windows' TCP defaults", 'Receive window tuning disabled.', 'Downloads tune their own speed.'],
  }
  const items: Record<string, unknown>[] = offered.map(key => {
    const on = mockNetOn.has(key)
    const [label, off, set] = words[key]
    return { key: `net-${key}`, label, detail: on && set ? set : off, applied: on, canUndo: on, off: mockNetOff.has(key) }
  })
  const left = offered.filter(key => !mockNetOn.has(key) && !mockNetOff.has(key)).map(key => words[key][0])
  items.unshift({ key: 'network', label: 'Network', detail: left.length ? left.join(', ') : "Exo's network settings for games", applied: left.length === 0, canUndo: mockNetOn.size > 0 })
  if (mockNet === 'slow') items.push({ key: 'netlink', label: 'Ethernet', detail: '100 Mbps of 2.5 Gbps. Try another cable or router port.' })
  return items
}

/**
 * Best settings › System in browser dev: a PC a tweak left on the platform clock and without a page file, two services at
 * boot and Memory Integrity on. `?tuning=vanguard` has Riot Vanguard installed, so Memory Integrity stays on.
 */
const mockTuningVanguard = typeof location !== 'undefined' && new URLSearchParams(location.search).get('tuning') === 'vanguard'
const mockTuningOn = new Set<string>()
const mockTuningOff = new Set<string>()
const mockTuning = (p?: { action?: unknown; item?: unknown }) => {
  const defaults = ['timer', 'pagefile']
  if (p?.action === 'apply') for (const key of defaults) if (!mockTuningOff.has(key)) mockTuningOn.add(key)
  const blocked = mockTuningVanguard ? 'Riot Vanguard (VALORANT, League of Legends) can require Memory Integrity, so Exo leaves it on: games with it may refuse to start without it.' : null
  if (typeof p?.item === 'string' && !(p.item === 'integrity' && blocked)) {
    if (p.action === 'on') { mockTuningOn.add(p.item); mockTuningOff.delete(p.item) }
    if (p.action === 'off') { mockTuningOn.delete(p.item); if (defaults.includes(p.item)) mockTuningOff.add(p.item) }
  }
  const words: Record<string, [string, string, boolean]> = {
    timer: ["Windows' own timer", 'A tweak forced the platform clock (HPET), so every clock read a game makes waits on it. Takes a restart.', false],
    pagefile: ['A page file, managed by Windows', "There's no page file, so a game that needs more memory than your RAM crashes instead of slowing down.", false],
    services: ['Services set to Manual', 'Downloaded Maps Manager and Program Compatibility Assistant Service start with Windows; Manual starts them only when something needs them. Takes a restart.', true],
    integrity: ['Memory Integrity off', blocked ?? 'Less secure: Windows stops checking that every driver is signed and safe, which is what keeps malicious drivers out. Only for a PC you keep clean. Up to about 5-10% more frames in some games. Takes a restart.', true],
  }
  const items = Object.entries(words).map(([key, [label, detail, optIn]]) => {
    const on = mockTuningOn.has(key)
    return { key, label, detail: on ? 'Restart to finish.' : detail, on, canUndo: on, off: mockTuningOff.has(key), optIn, restart: on, blocked: key === 'integrity' ? blocked : null }
  })
  const message = p?.item === 'integrity' && p.action === 'on' && blocked ? blocked : null
  return { ok: !message, message, applied: defaults.every(key => mockTuningOn.has(key) || mockTuningOff.has(key)), restart: mockTuningOn.size > 0, items }
}

/**
 * PC › Drives in browser dev: this PC's 990 PRO with flushing off and 132 unsafe shutdowns and a game off DirectStorage's
 * fast path, a worn SATA SSD with bad sectors (the problem case), and a hard drive with games on it. `?drives=fine`
 * shows the same drives with nothing wrong. flush, optimize and all set their row.
 */
/** A game's compression on the mock host: a 62 GB game a sample says would save 12.4 GB. */
const mockCompress = (id: string): CompressState => ({
  state: 'none', percent: null, saved: 0, size: 62e9, estimate: id ? 12.4e9 : null, sampled: !!id, changed: 0, algorithm: null, message: null, antiCheat: null, held: null,
})
const mockCompressSettings: CompressSettings = { whileIdle: false, afterUpdates: false, speed: 'balanced' }
const mockDrivesFine = typeof location !== 'undefined' && new URLSearchParams(location.search).get('drives') === 'fine'
const mockDriveFixes = { flush: mockDrivesFine, optimize: mockDrivesFine, set: new Set<string>() }
const MOCK_SAMSUNG = { name: 'Samsung', tool: 'Samsung Magician', url: 'https://semiconductor.samsung.com/consumer-storage/support/tools/' }
const MOCK_FIX_ASKS = {
  flush: { key: 'flush', label: 'Write-cache flushing', detail: 'Off on Samsung SSD 990 PRO with Heatsink 2TB · shut down without warning 132 times', applied: false, canUndo: false, restart: false },
  optimize: { key: 'optimize', label: 'Weekly optimize', detail: "Off: Windows' Automatic Maintenance is off", applied: false, canUndo: false, restart: false },
}
const MOCK_FIX_SET = {
  flush: { key: 'flush', label: 'Write-cache flushing', detail: 'On after a restart', applied: true, canUndo: true, restart: true },
  optimize: { key: 'optimize', label: 'Weekly optimize', detail: 'On', applied: true, canUndo: true, restart: false },
}

/** One fix's row: asking while off, with Undo once the mock set it, none when it was fine to begin with. */
/** PC › Apps › Startup in the browser: what a gaming PC usually starts, one that's gone and one that's locked. */
function mockStartup(message: string | null): StartupState {
  const item = (id: string, name: string, more: Partial<StartupItem>): StartupItem => {
    const one: StartupItem = {
      id, source: 'run', machine: false, name, publisher: null, command: `"C:\\Program Files\\${name}\\${name}.exe"`, enabled: true,
      does: null, without: null, verdict: null, missing: false, locked: false, readOnly: false, memory: 0, icon: null, unneeded: false, ...more,
    }
    return { ...one, unneeded: !one.locked && (one.missing || (one.enabled && one.verdict === 'remove')) }
  }
  return {
    ok: message === null,
    message,
    items: [
      item('run:cu:Steam', 'Steam', { publisher: 'Valve', does: 'Opens Steam in the tray at sign-in so friends see you online and updates download.', without: 'Game updates wait until Steam opens; Exo opens it when a game needs it.', verdict: 'remove', memory: 182e6 }),
      item('run:cu:Discord', 'Discord', { publisher: 'Discord Inc.', does: 'Opens Discord in the tray at sign-in so you get messages and calls.', without: "Nothing you'd notice in Exo: Discord is built in; the separate app opens when you start it.", verdict: 'remove', memory: 412e6 }),
      item('run:cu:Lucid', 'Lucid', { does: 'Starts the Lucid EQ app at sign-in so your headphone EQ and per-game profiles apply.', without: "Your headphones play without Lucid's EQ and game profiles until you open Lucid.", verdict: 'keep', memory: 64e6 }),
      item('run:lm:Riot Vanguard', 'Riot Vanguard', { machine: true, publisher: 'Riot Games, Inc.', does: "Shows Riot Vanguard's tray icon, the anti-cheat that Valorant and League of Legends need.", without: "Exo leaves Riot's anti-cheat alone; change it only through Riot's own settings.", verdict: 'locked', locked: true, memory: 9e6 }),
      item('run:cu:AF_uuid_4126040', 'AppsFlyer game tracking values', { command: '765aa5ec-50a2-4916-aba3-8ef112d65372', does: "Not a program: a game's ad-tracking SDK stores an install ID and launch count here.", without: "Nothing you'd notice: nothing runs from these values; the game may simply write them again.", verdict: 'remove' }),
      item('task:\\Driver Booster Scheduler', 'IObit Driver Booster scheduler', { source: 'task', machine: true, publisher: 'IObit', does: "Runs Driver Booster's scheduled driver scans in the background.", without: "Nothing you'd notice: you can still scan for drivers when you open Driver Booster.", verdict: 'remove' }),
      item('folder:cu:OldTool.lnk', 'OldTool', { source: 'folder', missing: true }),
      item('run:cu:OneDrive', 'Microsoft OneDrive', { enabled: false, publisher: 'Microsoft Corporation', does: 'Starts OneDrive at sign-in so your files sync in the background.', without: 'Your files only sync while OneDrive is open.', verdict: 'optional' }),
    ],
    removed: [{ id: 'run:cu:EpicGamesLauncher', name: 'Epic Games Launcher', at: new Date(Date.now() - 2 * 86_400_000).toISOString(), locked: false }],
    extras: 6,
  }
}

function mockDriveFix(key: 'flush' | 'optimize') {
  if (!mockDriveFixes[key]) return [MOCK_FIX_ASKS[key]]
  return mockDriveFixes.set.has(key) ? [MOCK_FIX_SET[key]] : []
}

/** The three drives with what fills them; `?drives=fine` drops every verdict. */
function mockDriveList() {
  const flushVerdict = { key: 'flush', text: 'A power cut can damage files: write-cache flushing is off.', bad: false }
  return [
    {
      id: '0', model: 'Samsung SSD 990 PRO with Heatsink 2TB', kind: 'NVMe SSD', link: { gen: 4, width: 4, maxGen: 4, maxWidth: 4 }, firmware: '8B2QJXD7', firmwareSlots: 3,
      vendor: MOCK_SAMSUNG, temperature: { now: 36, warn: 82, critical: 85 },
      health: { lifeUsed: 5, spare: 100, writtenTb: 126, hours: 4109, powerCycles: 527, unsafeShutdowns: 132, mediaErrors: 0 },
      volumes: [{ letter: 'C:', totalGb: 1862, freeGb: 308.5 }],
      driver: 'Microsoft NVMe (StorNVMe)', writeCache: true, build: null, headroom: 10,
      power: { state: 0, full: true, plan: 'Exo', exo: true, apst: null }, nvme: { hmbMb: null, hmbOn: null, sector: 512, fourK: false },
      space: { total: 1862 * GB, free: 308.5 * GB, games: 1180 * GB, caches: 66.1 * GB, oldCaches: 36.6 * GB, exo: 5.9 * GB, leftovers: 12.4 * GB, other: 289.1 * GB },
      games: [
        { id: 'steam:3513350', title: 'Wuchang: Fallen Feathers', bytes: 152 * GB, cacheBytes: 0 },
        { id: 'steam:2515020', title: 'FINAL FANTASY XVI', bytes: 102 * GB, cacheBytes: 2.6 * GB },
        { id: 'steam:2001760', title: 'Beast of Reincarnation', bytes: 98 * GB, cacheBytes: 3.8 * GB },
        { id: 'steam:1501750', title: 'Lords of the Fallen', bytes: 72 * GB, cacheBytes: 6.6 * GB },
        { id: 'steam:2622380', title: 'Stellar Blade', bytes: 70 * GB, cacheBytes: 7.1 * GB },
      ],
      verdicts: [
        ...(mockDriveFixes.flush && !mockDriveFixes.set.has('flush') ? [] : [flushVerdict]),
        { key: 'bypass:steam:2515020', text: "FINAL FANTASY XVI can't load through DirectStorage's fast path: its files are sparse.", bad: false },
      ],
    },
    {
      id: '1', model: 'Samsung SSD 870 EVO 1TB', kind: 'SSD', link: null, firmware: 'SVT02B6Q', firmwareSlots: null,
      vendor: MOCK_SAMSUNG, temperature: { now: 52, warn: 70, critical: 80 },
      health: { lifeUsed: 91, hours: 9120, powerCycles: 1310, unsafeShutdowns: 41, badSectors: 12 },
      volumes: [{ letter: 'D:', totalGb: 931.5, freeGb: 77.4 }],
      driver: 'Microsoft AHCI', writeCache: true, build: null, headroom: 12,
      space: { total: 931.5 * GB, free: 77.4 * GB, games: 801 * GB, caches: 1.2 * GB, oldCaches: 0.4 * GB, exo: 0, leftovers: 0, other: 51.9 * GB },
      games: [
        { id: 'steam:1174180', title: 'Red Dead Redemption 2', bytes: 121 * GB, cacheBytes: 0.8 * GB },
        { id: 'steam:1091500', title: 'Cyberpunk 2077', bytes: 88 * GB, cacheBytes: 0.4 * GB },
      ],
      verdicts: [
        { key: 'sectors', text: '12 bad sectors. Copy your files off it.', bad: true },
        { key: 'worn', text: 'Nearly worn out. Plan to replace it.', bad: false },
      ],
    },
    {
      id: '2', model: 'WDC WD40EZAZ-00SF3B0', kind: 'Hard drive', link: null, firmware: '80.00A80', firmwareSlots: null,
      vendor: { name: 'SanDisk', tool: 'SanDisk Dashboard', url: 'https://www.sandisk.com/en-us/topics/upgrades/drive-monitoring-sandisk-dashboard' },
      temperature: { now: 38, warn: null, critical: null },
      health: { hours: 21450, powerCycles: 2104, unsafeShutdowns: 77, badSectors: 0 },
      volumes: [{ letter: 'E:', totalGb: 3726, freeGb: 2410 }],
      driver: 'Microsoft AHCI', writeCache: true, build: null, headroom: 12,
      space: { total: 3726 * GB, free: 2410 * GB, games: 214 * GB, caches: 0, oldCaches: 0, exo: 0, leftovers: 31 * GB, other: 1071 * GB },
      games: [
        { id: 'steam:1245620', title: 'ELDEN RING', bytes: 49 * GB, cacheBytes: 0 },
        { id: 'steam:367520', title: 'Hollow Knight', bytes: 9 * GB, cacheBytes: 0 },
      ],
      verdicts: [{ key: 'hdd', text: '2 games load slowly from this hard drive.', bad: false }],
    },
  ]
}

function mockDrives(p?: { action?: unknown; space?: unknown }) {
  const action = typeof p?.action === 'string' ? p.action : null
  for (const key of ['flush', 'optimize'] as const) {
    if (action === key || action === 'all') { mockDriveFixes[key] = true; mockDriveFixes.set.add(key) }
  }
  const space = p?.space === true
  return {
    ok: true,
    fixes: [...mockDriveFix('flush'), ...mockDriveFix('optimize')],
    drives: mockDriveList().map(drive => ({
      ...drive,
      space: space ? drive.space : null,
      games: space ? drive.games : [],
      verdicts: mockDrivesFine ? [] : drive.verdicts.filter(verdict => space || !verdict.key.startsWith('bypass:')),
    })),
  }
}

// ?whatsnew: a user just updated to 3.14.1 from 3.13.2, so the sheet opens over the Library with 3.14.1 and the 3.14.0 they skipped.
const mockWhatsNew = typeof location !== 'undefined' && new URLSearchParams(location.search).has('whatsnew')
// ?update: 3.15.0 is offered as Exo opens (the update sheet), with its What's New.
const mockUpdate = typeof location !== 'undefined' && new URLSearchParams(location.search).has('update')

// ?folders: two Library folders to start with, Hades in both, one sorted by time played.
const mockFolders = typeof location !== 'undefined' && new URLSearchParams(location.search).has('folders')
// ?noapo: Equalizer APO isn't installed (Install takes 6 seconds, then it isn't on the output until Choose outputs).
const OUTPUT_ID = '{0.0.0.00000000}.{9709e4ab-6969-4817-ad38-1704eab1c079}'
const MIC_ID = '{0.0.1.00000000}.{0deb347c-90b6-43d6-b549-63e63b1b23a9}'
const mockAudio: Record<string, { rate: number; bits: number; channels: number; volume: number; muted: boolean }> = {
  [OUTPUT_ID]: { rate: 48000, bits: 24, channels: 2, volume: 62, muted: false },
  [MIC_ID]: { rate: 48000, bits: 16, channels: 1, volume: 100, muted: false },
}
const mockEndpoint = (id: string) => {
  const { rate, bits, channels, volume, muted } = mockAudio[id]
  return { format: { rate, bits, channels }, volume, muted }
}
const mockNoApo = typeof location !== 'undefined' && new URLSearchParams(location.search).has('noapo')
let mockApoPressed = 0
let mockApoOn = !mockNoApo
const MOCK_EQ_HEADPHONES = [
  { id: 'Super Review/in-ear/ZiiGaat Lush', name: 'ZiiGaat Lush', source: 'Super Review', form: 'in-ear' as const },
  { id: 'oratory1990/over-ear/Sennheiser HD 600', name: 'Sennheiser HD 600', source: 'oratory1990', form: 'over-ear' as const },
  { id: 'Rtings/Bruel & Kjaer 5128 over-ear/SteelSeries Arctis Nova 7', name: 'SteelSeries Arctis Nova 7', source: 'Rtings', form: 'over-ear' as const },
]
const mockEq: { on: boolean; comparing: boolean; headphone: (typeof MOCK_EQ_HEADPHONES)[number] | null; sound: string; custom: number[]; games: Record<string, string>; night: { on: boolean; start: string; end: string; sync: boolean } } = {
  on: true, comparing: false, headphone: MOCK_EQ_HEADPHONES[0], sound: 'footsteps', custom: [0, 2, 3, 1, 0, 0, -1, 1.5, 2, 0],
  games: { 'mock:valorant': 'footsteps' }, night: { on: false, start: '21:00', end: '06:00', sync: false },
}
type MockEqParams = Record<string, unknown> | undefined
const mockNight = (p: MockEqParams) => {
  const night = mockEq.night
  if (typeof p?.on === 'boolean') night.on = p.on
  if (typeof p?.sync === 'boolean') night.sync = p.sync
  if (typeof p?.start === 'string') night.start = p.start
  if (typeof p?.end === 'string') night.end = p.end
}
const mockEqGame = (p: MockEqParams) => {
  if (typeof p?.game !== 'string') return
  if (typeof p.sound === 'string') mockEq.games[p.game] = p.sound
  else delete mockEq.games[p.game]
}
const MOCK_EQ_ACTIONS: Record<string, (p: MockEqParams) => void> = {
  install: () => { mockApoPressed = Date.now() },
  devices: () => { mockApoOn = true },
  headphone: p => { mockEq.headphone = MOCK_EQ_HEADPHONES.find(h => h.id === p?.id) ?? null },
  sound: p => { if (typeof p?.sound === 'string') mockEq.sound = p.sound },
  custom: p => { if (Array.isArray(p?.gains)) { mockEq.custom = p.gains as number[]; mockEq.sound = 'custom' } },
  on: p => { mockEq.on = p?.on === true },
  compare: p => { mockEq.comparing = p?.on === true },
  game: mockEqGame,
  night: mockNight,
}
function mockEqState(p?: Record<string, unknown>) {
  MOCK_EQ_ACTIONS[String(p?.action)]?.(p)
  const installed = !mockNoApo || (mockApoPressed > 0 && Date.now() - mockApoPressed > 6000)
  const title = (id: string) => MOCK_GAMES.find(game => game.id === id)?.title ?? id
  const boosts = mockEq.sound === 'custom' ? Math.max(0, ...mockEq.custom) : mockEq.sound === 'flat' ? 0 : 3.5
  return {
    ok: true, message: null,
    apo: { installed, installing: mockApoPressed > 0 && !installed, version: installed ? '1.4.2' : null, onOutput: installed && mockApoOn, restart: false, effectsOff: false, outputs: mockApoOn ? 1 : 0, installerVersion: '1.4.2' },
    on: mockEq.on, comparing: mockEq.comparing, output: { name: 'Headphones (High Definition Audio Device)' },
    headphone: mockEq.headphone, sound: mockEq.sound,
    custom: [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000].map((hz, i) => ({ hz, gain: mockEq.custom[i] ?? 0 })),
    preamp: -Math.round((boosts + (mockEq.headphone ? 2.9 : 0)) * 10) / 10,
    playing: null,
    games: Object.entries(mockEq.games).map(([id, sound]) => ({ id, title: title(id), sound })),
    library: MOCK_GAMES.filter(game => game.installed && !mockEq.games[game.id]).map(game => ({ id: game.id, title: game.title })),
    night: { ...mockEq.night, now: false },
    others: [{ name: 'Speakers (Realtek(R) Audio)', headphone: null, sound: 'bass' }],
    guess: null,
    lucidPlays: false,
  }
}

const mockSettings: LauncherSettings = {
  onboardingComplete: !mockFirstRun,
  appVersion: mockWhatsNew ? '3.14.1' : '1.0.0-dev',
  whatsNewSeen: mockWhatsNew ? '3.13.2' : '1.0.0-dev',
  reduceMotion: false,
  startMaximized: false,
  framelessPinTop: false,
  framelessPinSide: false,
  textColour: 'white',
  background: 'grey',
  textSize: 'normal',
  sortMode: 'name',
  riotId: null,
  recent: [],
  showLibraryTools: false,
  achievementBanners: true,
  achievementVolume: 60,
  fpsCounter: false,
  fpsCounterPosition: 'right',
  fpsCounterItems: [],
  fpsCounterHideOverBuiltIn: false,
  hdrForGames: false,
  frameGenEverywhere: false,
  frameGenEverywhereCard: true,
    hdrSdrGamma: true,
    discordNoiseSuppression: true,
    discordQuietWhilePlaying: true,
  autoUpdateChipset: true,
  autoUpdateUpscalers: true,
  upscalerAntiCheatEverywhere: false,
  keepAwakeDownloads: true,
  storeClientsQuiet: true,
  chipset: mockIntelCpu ? 'intel' : 'amd',
  ...(mockFolders ? {
    libraryFolders: [
      { id: 'mockfav', name: 'Favorites', color: 'blue', sort: 'played', games: ['mock:hades', 'mock:eldenring', 'mock:valorant'] },
      { id: 'mockrogue', name: 'Roguelikes', color: 'orange', games: ['mock:hades'] },
    ],
  } : {}),
}

/** The browser mock has no file dialog and no cover cache to copy a picture into. */
const MOCK_NO_PICKER = 'Browser mock — picking a picture needs the Exo host.'

let mockProgress: InstallProgress = {
  gameId: '',
  phase: 'idle',
  status: '',
  canCancel: false,
  isActive: false,
}

/** The account step's answers for the browser mock (no host): the address picks the screen state. */
const MOCK_RECOVERY_CODE = 'K7Q2-9TMA-XB4R-LZ8P-WH3D-6NVC'
const MOCK_DEVICE_HERE = '0123456789abcdef0123456789abcdef'
/** Signed in with ?signedin in the URL; the account calls flip it, so a flow can be shot from either side. */
let mockAccountSignedIn = typeof location !== 'undefined' && new URLSearchParams(location.search).has('signedin')
/** The account prompts' stored state, device-only. */
function mockAccountOverview() {
  return {
    signedIn: mockAccountSignedIn, codePending: false, sessionEnded: false, email: mockAccountSignedIn ? 'you@example.com' : null, accountId: mockAccountSignedIn ? 'acc-1f3a9c' : null, accountAsked: true,
    sync: { lastSyncAt: mockAccountSignedIn ? '2026-11-07T11:40:00.000Z' : null, pending: mockAccountSignedIn ? 2 : 0,
      held: mockAccountSignedIn ? [{ key: 'cover-image/game-1', reason: 'Over 64 KiB: this value stays on this PC.' }] : [], signedOut: false, pushStopped: false },
  }
}
function mockPasswordAnswer(params: Record<string, unknown> | undefined) {
  if (String(params?.current ?? '') === 'wrong') return { ok: false, message: "That password isn't right.", error: 'wrong-password', offline: false }
  if (String(params?.password ?? '').length < 12) return { ok: false, message: 'Use at least 12 characters.', error: 'bad-password', offline: false }
  return { ok: true, message: '', error: null, offline: false }
}
function mockAccountAnswer(params: Record<string, unknown> | undefined, creating: boolean) {
  const email = String(params?.email ?? '')
  const password = String(params?.password ?? '')
  if (email.includes('offline')) return { ok: false, message: "Can't reach Exo's account service.", error: null, offline: true }
  if (email.includes('locked')) return { ok: false, message: 'Too many tries for this email from this PC. Wait 1:15 and try again.', error: 'locked', retryAfterSeconds: 75, offline: false }
  if (email.includes('slow')) return { ok: false, message: 'Too many attempts. Wait a minute and try again.', error: 'slow-down', offline: false }
  if (creating && email.includes('taken')) return { ok: false, message: 'An account already exists for this email. Sign in instead.', error: 'email-taken', offline: false }
  if (!creating && password === 'wrong') return { ok: false, message: "That email and password don't match. Check both and try again.", error: 'wrong-credentials', offline: false }
  return { ok: true, message: '', error: null, offline: false, recoveryCode: creating ? MOCK_RECOVERY_CODE : null }
}

type MockHandler = (params?: Record<string, unknown>) => unknown

function mockParamId(params?: Record<string, unknown>): string {
  return String(params?.id ?? '')
}

function mockLibraryGet() {
  return {
    games: MOCK_GAMES,
    count: MOCK_GAMES.length,
    stores: mockStoresMatrix(),
    progress: mockProgress,
    sortMode: mockSettings.sortMode,
    recent: mockSettings.recent,
  }
}

function mockGameGet(params?: Record<string, unknown>) {
  const id = mockParamId(params)
  const game = MOCK_GAMES.find((g) => g.id === id)
  return game ? { ok: true, game } : { ok: false, message: 'Game not found.' }
}

function mockGameInstall(params?: Record<string, unknown>) {
  const id = mockParamId(params)
  mockProgress = {
    gameId: id,
    phase: 'downloading',
    percent: 42,
    bytesPerSecond: 12.5 * 1024 * 1024,
    status: 'Mock progress (host required for real install)',
    canCancel: true,
    isActive: true,
  }
  emitHostEvent('install.progress', mockProgress)
  return {
    ok: false,
    message: 'Browser mock — install only works inside the WinUI host.',
    progress: mockProgress,
  }
}

function mockArtRefresh(reset: boolean): MockHandler {
  return (params) => {
    const id = mockParamId(params)
    const index = MOCK_GAMES.findIndex((game) =>
      game.id === id || game.variants?.some((variant) => variant.id === id),
    )
    if (index < 0) return { ok: false, message: 'Game not found.' }
    const current = MOCK_GAMES[index]
    const next: Game = {
      ...current,
      ...(reset ? { coverUrl: null, coverSource: null } : {}),
      artRevision: (current.artRevision ?? 0) + 1,
    }
    MOCK_GAMES[index] = next
    emitHostEvent('library.updated', { games: [...MOCK_GAMES], count: MOCK_GAMES.length })
    return {
      ok: true,
      message: reset ? 'Cover reset.' : 'Artwork refreshed.',
      game: next,
      artRevision: next.artRevision,
    }
  }
}

/** The host binds the game process a moment after launch and pushes each change. */
function mockRunState(running: boolean): MockHandler {
  return (params) => {
    const id = mockParamId(params)
    const index = MOCK_GAMES.findIndex((game) => game.id === id)
    if (index < 0 || !MOCK_GAMES[index].installed) return { ok: false, message: 'Browser mock — only installed demo games run.' }
    setTimeout(() => {
      MOCK_GAMES[index] = { ...MOCK_GAMES[index], isRunning: running, canStop: running }
      emitHostEvent('game.runState', { id, isRunning: running, canStop: running })
    }, running ? 1200 : 150)
    return { ok: true, message: running ? 'Mock: game started.' : 'Game closed.' }
  }
}

/** Games Update all brought up in this mock session: every file current from then on. */
const mockDlssUpdated = new Set<string>()

/** HelixSR's FSR drop-in: a mod's file, left alone whatever Update all does. */
const mockModFsr = { key: 'amd_fi' + 'delity' + 'fx_dx1' + '2.dll', fileName: 'amd_fi' + 'delity' + 'fx_dx1' + '2.dll', folder: null, displayName: 'FSR 3.1', currentVersion: '3.1.5', latestVersion: '3.1.5', latestRelease: 'fsr-3.1.5', state: 'mod', mod: 'HelixSR', origin: 'shipped', canUpdate: false, canRestore: false }

/** One game ships DLSS in two folders, one copy Exo's and one the game's own, and HelixSR in place of its FSR. */
function mockDlssStatus(params?: Record<string, unknown>) {
  if (mockDlssUpdated.has(String(params?.id))) {
    return { ok: true, items: [{ key: 'nvngx_dlss.dll', fileName: 'nvngx_dlss.dll', folder: null, displayName: 'DLSS Super Resolution', currentVersion: '310.9.1', currentRelease: 'dlss-310.9.1', latestVersion: '310.9.1', latestRelease: 'dlss-310.9.1', state: 'current', origin: 'exo', originalVersion: '310.2.1', originalState: 'saved', canUpdate: false, canRestore: true }, mockModFsr] }
  }
  return {
    ok: true,
    items: [
      { key: 'nvngx_dlss.dll', fileName: 'nvngx_dlss.dll', folder: null, displayName: 'DLSS Super Resolution', currentVersion: '310.9.1', currentRelease: 'dlss-310.9.1', latestVersion: '310.9.1', latestRelease: 'dlss-310.9.1', state: 'current', origin: 'exo', originalVersion: '310.2.1', originalState: 'saved', canUpdate: false, canRestore: true },
      { key: 'Engine/Plugins/DLSS/nvngx_dlss.dll', fileName: 'nvngx_dlss.dll', folder: 'Engine/Plugins/DLSS', displayName: 'DLSS Super Resolution', currentVersion: '310.2.1', latestVersion: '310.9.1', latestRelease: 'dlss-310.9.1', state: 'outdated', origin: 'reverted', originalVersion: '310.2.1', originalState: 'saved', canUpdate: true, canRestore: false },
      mockModFsr,
    ],
  }
}

function mockNvidiaLatencyLabel(lowLatency: unknown): string {
  if (lowLatency === 'ultra') return 'Ultra'
  return lowLatency === 'off' ? 'Off' : 'On'
}

function mockNvidiaDetails(applied: boolean, latencyLabel: string, vrrEnabled: boolean) {
  const state = applied ? 'on' as const : 'changed' as const
  return [
    { key: 'lowLatency', label: 'Low latency mode', value: latencyLabel, state },
    { key: 'power', label: 'Power management', value: 'Normal (per-game max)', state },
    { key: 'textureFiltering', label: 'Texture filtering', value: 'Quality', state },
    { key: 'shaderCache', label: 'Shader cache', value: 'Unlimited', state },
    { key: 'gsync', label: 'G-SYNC / VRR', value: vrrEnabled ? 'Full screen' : 'Off', state },
    { key: 'dlss', label: 'DLSS overrides', value: 'Latest · Preset K / F / A', state },
    { key: 'rebar', label: 'Resizable BAR', value: 'On', state: 'on' as const },
  ]
}

function mockGpuProfile(p?: Record<string, unknown>) {
  const applied = mockApply('profile', p)
  const status = applied ? 'applied' : 'ready'
  if (mockGpu === 'intel' || mockGpu === 'amd') {
    const isIntel = mockGpu === 'intel'
    return {
      ok: true, vendor: mockGpu, gpu: isIntel ? 'Arc B580' : 'Radeon RX 9070 XT', series: 0, screen: { name: 'VG27AQ', hz: 165, vrr: true },
      status, canUndo: mockApplied.has('profile'), driverUpdated: false, items: [],
      lowLatency: isIntel ? p?.lowLatency ?? 'on' : null,
      summary: isIntel
        ? "Adaptive Sync on, Low latency on, Intel's prebuilt shaders; Endurance Gaming and sharpening off"
        : 'FreeSync on, Anti-Lag on; Chill, Boost and Image Sharpening off',
    }
  }
  const lowLatency = (p?.lowLatency as 'off' | 'on' | 'ultra' | undefined) ?? 'on'
  const vrrEnabled = typeof p?.vrr === 'boolean' ? p.vrr : true
  return {
    ok: true, vendor: 'nvidia', gpu: 'RTX 3070', series: 30, screen: { name: 'VG248', hz: 165, vrr: true },
    status, canUndo: mockApplied.has('profile'), driverUpdated: false, items: [],
    lowLatency, vrrEnabled, rebar: true, cacheFreeGb: 180,
    details: mockNvidiaDetails(applied, mockNvidiaLatencyLabel(lowLatency), vrrEnabled),
    skipped: false,
    features: [
      { key: 'lowLatency', label: 'Low latency', state: applied ? 'on' : 'off' },
      { key: 'dlssPresets', label: 'DLSS presets', state: applied ? 'on' : 'off' },
    ],
  }
}


/** Shaders per demo game, as the Exo Shaders engine would list them. */
let mockPrepareRunning = false
let mockPrepareAuto = true

function mockShadersStatus(params?: Record<string, unknown>) {
  const id = String(params?.id ?? '')
  if (!mockShaders) return { ok: true, state: 'noApp' }
  switch (id) {
    case 'mock:valorant': return { ok: true, state: 'unsupported', reason: 'Needs a recording, which its anti-cheat blocks.' }
    case 'mock:control': return { ok: true, state: 'warmed', reason: 'Compiled for driver 617.42.', warmed: { at: '2026-10-12T21:04:00-05:00', driver: '617.42' } }
    case 'mock:disco': return { ok: true, state: 'stale', reason: 'Driver changed', engineId: 'disco' }
    default: return { ok: true, state: 'ready', engineId: id.replace(/^mock:/, '') }
  }
}

function mockCancelInstall() {
  mockProgress = { ...mockProgress, phase: 'cancelled', isActive: false, canCancel: false, status: 'Canceled.' }
  return { ok: true, message: 'Cancel requested.' }
}

/** Settings' Steam Offline mode in the mock host. */
let MOCK_STEAM_OFFLINE = false
let MOCK_STEAM_ACCOUNT: string | null = null
let MOCK_STEAM_POLLS = 0

let MOCK_GIFTS: StoreGift[] = [
  { store: 'steam', id: '237160366753599703', title: 'Hollow Signal', image: null, sender: 'Rocket alt', message: null },
  { store: 'epic', id: 'a1b2c3d4e5f6', title: 'Night Harbor', image: null, sender: 'Mira', message: 'Happy birthday! Saw you wishlisted this.' },
]

const NONE_LEFT = { folders: 0, files: 0, registry: 0, shortcuts: 0, bytes: 0 }
const MOCK_APP = { publisher: null, version: null, installedOn: null, bytes: null, store: false, removable: true, uninstaller: true, care: null, replaces: null } as const
let MOCK_PROGRAMS = [
  { ...MOCK_APP, id: 'HKLM:7-Zip', name: '7-Zip 26.01 (x64)', publisher: 'Igor Pavlov', version: '26.01', bytes: 5_200_000 },
  { ...MOCK_APP, id: 'HKCU:Discord', name: 'Discord', publisher: 'Discord Inc.', bytes: 412_000_000, replaces: 'discord' },
  { ...MOCK_APP, id: 'HKLM:Steam', name: 'Steam', publisher: 'Valve Corporation', bytes: 1_200_000_000, care: 'store', caution: 'Your games from it need it to start.' },
  { ...MOCK_APP, id: 'HKLM:Vanguard', name: 'Riot Vanguard', publisher: 'Riot Games, Inc.', bytes: 273_000_000, care: 'anticheat', removable: false },
  { ...MOCK_APP, id: 'HKLM:VC14', name: 'Microsoft Visual C++ v14 Redistributable (x64)', publisher: 'Microsoft Corporation', bytes: 21_000_000, care: 'runtime', caution: 'Games and apps that need it stop working until it’s installed again.' },
  { ...MOCK_APP, id: 'store:Clipchamp.Clipchamp_yxz26nhyzhsrt', name: 'Clipchamp', publisher: 'Microsoft Corp.', store: true, installedOn: '2026-10-12', bytes: 160_000_000, replaces: 'clipchamp' },
  { ...MOCK_APP, id: 'HKCU:Broken', name: 'Old Tool', publisher: 'Somebody', uninstaller: false, bytes: 48_000_000 },
] as InstalledApp[]
let MOCK_REPLACED: ReplacedApp[] = [
  { id: 'discord', name: 'Discord', detail: 'Exo has its own Discord: servers, DMs and calls', installed: true, leftovers: 9, bytes: 412_000_000, needsAdmin: false, found: { folders: 4, files: 0, registry: 4, shortcuts: 1, bytes: 412_000_000 }, cleared: null },
  { id: 'snipping', name: 'Snipping Tool', detail: 'Exo snips with Print Screen and edits in Captures', installed: true, leftovers: 0, bytes: 0, needsAdmin: false, found: NONE_LEFT, cleared: null },
  { id: 'photos', name: 'Photos', detail: 'Exo shows and trims your captures', installed: true, leftovers: 0, bytes: 0, needsAdmin: false, found: NONE_LEFT, cleared: null },
  { id: 'gamebar', name: 'Xbox Game Bar', detail: 'Exo records clips and replays', installed: true, leftovers: 0, bytes: 0, needsAdmin: false, found: NONE_LEFT, cleared: null },
  { id: 'clipchamp', name: 'Clipchamp', detail: 'Exo trims and shares your clips', installed: false, leftovers: 0, bytes: 0, needsAdmin: false, found: NONE_LEFT, cleared: { folders: 1, files: 2, registry: 6, shortcuts: 0, bytes: 38_000_000 } },
  { id: 'nvidia', name: 'NVIDIA App and Control Panel', detail: 'Exo updates the driver, sets its profile and your screens; the driver stays', installed: true, leftovers: 6, bytes: 1_300_000_000, needsAdmin: true, found: { folders: 5, files: 0, registry: 1, shortcuts: 0, bytes: 1_300_000_000 }, cleared: null },
]

function mockDepsList() {
  return {
    items: [
      { id: 'vcredist', name: 'Visual C++ Redistributable', status: 'Present', detail: 'Mock', canOfferInstall: true },
      { id: 'directx', name: 'DirectX', status: 'Present', detail: 'Mock', canOfferInstall: true },
      { id: 'dotnet', name: '.NET Desktop Runtime', status: 'Present', detail: 'Mock', canOfferInstall: true },
      { id: 'webview2', name: 'WebView2 Runtime', status: 'Present', detail: 'Mock', canOfferInstall: true },
    ],
  }
}

/** Game runtimes as on a PC that has most of them: two missing, two older, .NET 6 past its support. */
let MOCK_RUNTIMES: RuntimeItem[] = [
  ...(['2005', '2008', '2010', '2012', '2013'] as const).flatMap(year => (['x86', 'x64'] as const).map(arch => ({
    id: `vc${year}-${arch}`, name: `Visual C++ ${year} (${arch})`, group: 'vc' as const, bulk: true,
    installed: year === '2005' && arch === 'x86' ? null : '1.0', latest: '1.0', state: year === '2005' && arch === 'x86' ? 'missing' as const : 'current' as const,
  }))),
  { id: 'vc14-x86', name: 'Visual C++ 2015–2026 (x86)', group: 'vc', installed: '14.44.35211.0', latest: '14.51.36247.0', state: 'outdated', bulk: true },
  { id: 'vc14-x64', name: 'Visual C++ 2015–2026 (x64)', group: 'vc', installed: '14.44.35211.0', latest: '14.51.36247.0', state: 'outdated', bulk: true },
  { id: 'directx', name: 'DirectX (June 2010)', group: 'directx', installed: 'June 2010', latest: 'June 2010', state: 'current', bulk: true },
  { id: 'gameinput', name: 'GameInput', group: 'directx', installed: null, latest: '4.0.306', state: 'missing', bulk: true },
  { id: 'd3d12', name: 'DirectX 12', group: 'directx', installed: null, latest: 'Built into Windows', state: 'info', bulk: false, detail: 'DirectX 12 Ultimate · feature level 12_2 · shader model 6.8 on NVIDIA GeForce RTX 3070' },
  { id: 'directstorage', name: 'DirectStorage', group: 'directx', installed: null, latest: 'Inside the games that use it', state: 'info', bulk: false, detail: 'Games that load with it bring their own copy. Nothing to install.' },
  { id: 'netfx481', name: '.NET Framework 4.8.1', group: 'dotnet', installed: '4.8.1', latest: '4.8.1', state: 'current', bulk: true },
  { id: 'dotnet10-x64', name: '.NET Desktop Runtime 10 (x64)', group: 'dotnet', installed: '10.0.14', latest: '10.0.14', state: 'current', bulk: true },
  { id: 'dotnet6-x64', name: '.NET Desktop Runtime 6 (x64)', group: 'dotnet', installed: null, latest: '6.0.36', state: 'missing', bulk: false, endOfSupport: true },
  { id: 'xna40', name: 'XNA Framework 4.0', group: 'other', installed: '4.0.30901.0', latest: '4.0.30901.0', state: 'current', bulk: true },
  { id: 'vulkan', name: 'Vulkan', group: 'other', installed: '1.4.357.0', latest: 'With your graphics driver', state: 'info', bulk: false },
]

/** runtimes.install in the browser: each download in steps, then the "Windows" run, then all current. */
async function mockRuntimesInstall(params?: Record<string, unknown>) {
  const mode = params?.mode as string
  const ids = params?.ids as string[] | undefined
  const plan = MOCK_RUNTIMES.filter(item => item.state !== 'info' && (ids ? ids.includes(item.id) : mode === 'repair' ? item.group === 'vc' : item.state === 'missing' && item.bulk || mode === 'update' && item.state === 'outdated'))
  const size = 20_000_000
  for (const [at, item] of plan.entries()) {
    for (let bytes = 0; bytes <= size; bytes += size / 5) {
      emitHostEvent('runtimes.progress', { phase: 'download', id: item.id, name: item.name, index: at + 1, count: plan.length, bytes, total: size, doneBytes: at * size + bytes, allBytes: plan.length * size })
      await new Promise(resolve => setTimeout(resolve, 150))
    }
  }
  for (const [at, item] of plan.entries()) {
    emitHostEvent('runtimes.progress', { phase: 'install', id: item.id, name: item.name, index: at + 1, count: plan.length, bytes: 0, total: null, doneBytes: 0, allBytes: 0 })
    await new Promise(resolve => setTimeout(resolve, 500))
  }
  MOCK_RUNTIMES = MOCK_RUNTIMES.map(item => plan.includes(item) ? { ...item, installed: item.latest, state: 'current' } : item)
  return {
    ok: true,
    results: plan.map((item, at) => ({ id: item.id, name: item.name, outcome: at === 0 && mode !== 'repair' ? 'restart' : 'installed', message: at === 0 && mode !== 'repair' ? 'Installed. Restart Windows to finish.' : 'Installed.', code: at === 0 ? 3010 : 0 })),
    items: MOCK_RUNTIMES,
  }
}

function mockStoresCheck() {
  return {
    state: 'complete',
    checkedAtUtc: new Date().toISOString(),
    code: 'local_check_complete',
    stores: [
      {
        store: 'local',
        client: 'missing',
        backend: 'present',
        session: 'present',
        cache: 'present',
        readiness: 'ready',
        code: 'ready',
      },
    ],
  }
}

function mockStoresMatrix() {
  return [
    {
      store: 'local',
      displayName: 'Local',
      agentPresent: true,
      backendPresent: true,
      signedIn: true,
      cachePresent: true,
      detail: 'Ready',
      checkCode: 'checked',
      checkedAtUtc: new Date().toISOString(),
    },
    { store: 'steam', displayName: 'Steam', agentPresent: true, clientPresent: true, backendPresent: true, signedIn: true },
    { store: 'epic', displayName: 'Epic Games', agentPresent: true, clientPresent: true, backendPresent: true, signedIn: false },
    { store: 'gog', displayName: 'GOG', agentPresent: true, clientPresent: false, backendPresent: true, signedIn: false },
    { store: 'riot', displayName: 'Riot Games', agentPresent: true, clientPresent: true, signedIn: true },
  ]
}

function mockAbsorbIdSettings(next: Record<string, unknown>) {
  if (typeof next.riotId === 'string') {
    const trimmed = next.riotId.trim()
    mockSettings.riotId = trimmed.length > 0 ? trimmed : null
    delete next.riotId
  }
}

/** Two captures, so Favorite and its chip can be tried without the add-on. */
const mockClipsFlag = typeof location !== 'undefined' ? new URLSearchParams(location.search).get('clips') : null
const mockShot = (from: string, to: string) => 'data:image/svg+xml,' + encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient></defs><rect width="320" height="180" fill="url(#g)"/></svg>`)
/** A snip to draw on in the browser: a scoreboard with names and a chat line, so Blur and Copy text have something to work on. */
const mockSnip = 'data:image/svg+xml,' + encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#14203a"/><stop offset="1" stop-color="#5a2b1a"/></linearGradient></defs><rect width="1920" height="1080" fill="url(#g)"/><g font-family="Segoe UI, sans-serif" fill="#fff"><text x="120" y="200" font-size="72" font-weight="700">VICTORY</text><text x="120" y="320" font-size="40">PlayerOne    24 / 3 / 11</text><text x="120" y="390" font-size="40">NightOwl     18 / 6 / 9</text><text x="120" y="900" font-size="34" fill="#9fd">[Team] NightOwl: gg that last fight was clean</text></g></svg>`)
const mockCaptures: CaptureItem[] = [
  { id: 'cap0000000000001', kind: 'image', game: 'Desktop', name: 'Snip 1', created: '2026-10-19T16:08:00Z', size: 120_000, favourite: false, url: mockSnip, thumb: mockSnip },
  { id: 'mock-rivals-snip', kind: 'image', game: 'Marvel Rivals', gameId: 'mock:rivals', name: 'Scoreboard', created: new Date(Date.now() - 2 * 3_600_000).toISOString(), size: 2_400_000, favourite: false, url: mockSnip, thumb: mockSnip },
  { id: 'mock-lastyear', kind: 'video', game: 'Hades', gameId: 'mock:hades', name: 'First clear', created: new Date(new Date().setFullYear(new Date().getFullYear() - 1)).toISOString(), size: 31_000_000, durationMs: 34_000, favourite: true, url: 'mock-clip.webm', thumb: mockShot('#3a1430', '#d05a8a') },
  { id: 'cap0000000000002', kind: 'video', game: 'Elden Ring', gameId: 'mock:eldenring', name: 'Clip 1', created: '2026-10-18T20:00:00Z', size: 48_000_000, durationMs: 60_000, favourite: false, url: 'mock-clip.webm', thumb: mockShot('#3b2a14', '#a9792c') },
  { id: 'mock-rivals-1', kind: 'video', game: 'Marvel Rivals', gameId: 'mock:rivals', name: 'Triple on Tokyo 2099', created: new Date(Date.now() - 3_600_000).toISOString(), size: 18_400_000, durationMs: 42_000, favourite: true, url: 'mock-clip.webm', thumb: mockShot('#1a2a5a', '#d0452b') },
  { id: 'mock-rivals-2', kind: 'video', game: 'Marvel Rivals', gameId: 'mock:rivals', name: 'Last second capture', created: new Date(Date.now() - 86_400_000).toISOString(), size: 9_700_000, durationMs: 21_000, favourite: false, url: 'mock-clip.webm', thumb: mockShot('#2b1a4a', '#3b9bd0') },
  { id: 'mock-big', kind: 'video', game: 'Marvel Rivals', gameId: 'mock:rivals', name: 'Full match', created: new Date(Date.now() - 2 * 86_400_000).toISOString(), size: 412_000_000, durationMs: 1_440_000, favourite: false, url: 'mock-clip.webm', thumb: mockShot('#143a2b', '#7bd05a') },
  { id: 'mock-hades-1', kind: 'video', game: 'Hades', gameId: 'mock:hades', name: 'Final boss, no hit', created: new Date(Date.now() - 4 * 86_400_000).toISOString(), size: 14_200_000, durationMs: 58_000, favourite: false, url: 'mock-clip.webm', thumb: mockShot('#4a1414', '#d0a82b') },
]

// A reel in the browser: ?reel=game answers as a game is running (Wait then holds it), anything else makes one.
let mockReelTimer: ReturnType<typeof setInterval> | undefined
function mockReel(params?: Record<string, unknown>) {
  const ids = Array.isArray(params?.ids) ? params.ids as string[] : []
  if (ids.length < 2) return { ok: false, message: 'Pick at least two clips.' }
  const game = typeof location !== 'undefined' && new URLSearchParams(location.search).get('reel') === 'game'
  if (game && params?.wait !== true) return { ok: false, message: "A game is running. Exo makes a reel when you aren't playing.", wait: true }
  if (game) { setTimeout(() => emitHostEvent('capture.reel', { state: 'waiting', percent: 0, wait: false }), 50); return { ok: true } }
  let percent = 0
  setTimeout(() => emitHostEvent('capture.reel', { state: 'working', percent, wait: false }), 50)
  mockReelTimer = setInterval(() => {
    percent += 12
    if (percent < 100) { emitHostEvent('capture.reel', { state: 'working', percent, wait: false }); return }
    clearInterval(mockReelTimer)
    const made: CaptureItem = { id: 'cap0000000000009', kind: 'video', game: 'Elden Ring', gameId: 'steam:1245620', name: 'Elden Ring reel 2026-10-18', created: '2026-10-19T09:00:00Z', size: 120_000_000, durationMs: 135_000, favourite: false, url: 'mock-clip.webm', thumb: null, derived: true }
    mockCaptures.unshift(made)
    emitHostEvent('capture.reel', { state: 'done', percent: 100, wait: false, item: made })
  }, 600)
  return { ok: true }
}

/** Optimize on the mock: Modern Warfare 4 has best settings; a press writes them, a press again puts the player's back. */
let mockOptimized = false
function mockGameSettings(params?: Record<string, unknown>) {
  if (params?.id !== 'steam:1938090') return { ok: true, supported: false }
  if (params.all === true) mockOptimized = params.on === true
  return {
    ok: true, supported: true, title: 'Call of Duty: Modern Warfare 4', found: true, groups: [], windowMode: 'borderless' as const,
    standing: mockOptimized ? 'applied' as const : 'off' as const,
  }
}

function mockCaptureItem(params?: Record<string, unknown>) {
  const at = mockCaptures.findIndex(item => item.id === mockParamId(params))
  if (at < 0) return { ok: false, message: 'That capture is gone.' }
  if (params?.action === 'favourite' || params?.action === 'unfavourite') mockCaptures[at] = { ...mockCaptures[at], favourite: params.action === 'favourite' }
  if (params?.action === 'shrink') {
    const before = mockCaptures[at].size
    mockCaptures[at] = { ...mockCaptures[at], size: Math.round(before / 20) }
    const item = mockCaptures[at]
    setTimeout(() => emitHostEvent('capture.shrunk', { id: item.id, item, before, after: item.size }), 1200)
  }
  return { ok: true, item: mockCaptures[at] }
}

function mockSettingsSet(params?: Record<string, unknown>) {
  const next = { ...(params ?? {}) } as Record<string, unknown>
  mockAbsorbIdSettings(next)
  Object.assign(mockSettings, next)
  return { ...mockSettings }
}

// Discord settings > Voice & Video and the rest, kept for the session so the pages can be clicked through.
let mockDiscordClient: Record<string, unknown> = {
  inputProfile: 'exo', noiseSuppression: true, noiseSuppressionKind: 'ai', othersNoiseSuppression: false, onlyMyVoice: false, myVoiceLearned: false, myVoiceSeconds: 0, evenVoices: true, echoCancellation: true, voiceFocus: true, pushToTalk: false, pttDelay: 20,
  autoSensitivity: true, sensitivity: -60, attenuation: 0, attenuateSelf: false, attenuateOthers: true, keybinds: [], desktopNotifications: true,
  unreadBadge: true, taskbarFlash: true, disabledSounds: [], disableAllSounds: false, selectedChannelSound: false, soundVolume: 60,
}

const mockOk: MockHandler = () => ({ ok: true })
// What the mock has saved for later: bookmarked messages and reminders (message id -> due, ms).
const mockSaved = { bookmarks: new Set<string>(), reminders: new Map<string, number>() }
// Hide Muted Channels in the mock's server, as its menu sets it.
let mockHideMuted = false

const GB = 1024 ** 3
/** Properties as the host builds them: Marvel Rivals' real Steam shape (its High-Resolution Pack switched off), an Epic game's optional content. */
const mockProperties: Record<string, GameProperties> = {}
const STEAM_PROPERTIES: Partial<GameProperties> = {
  verifyLabel: 'Verify files',
  language: 'english',
  languageChoices: [
    { value: 'english', label: 'English', sizeBytes: 0 }, { value: 'french', label: 'French', sizeBytes: 0 },
    { value: 'german', label: 'German', sizeBytes: 0 }, { value: 'japanese', label: 'Japanese', sizeBytes: 0 },
    { value: 'schinese', label: 'Simplified Chinese', sizeBytes: 0 },
  ],
  beta: '',
  betaChoices: [{ value: '', label: 'None' }],
  betaCode: true,
  // A pack with files has a switch; cosmetics (no files) say whether the account holds them, as 007 First Light's do.
  content: [
    { id: '4406010', name: 'Marvel Rivals - High-Resolution Pack', enabled: false, sizeBytes: 22376494559, toggle: true, state: null },
    ...['Day of the Dead (Exclusive Outfit)', 'Desert Explorer (Exclusive Outfit)', 'Gentleman Operator (Exclusive Outfit)',
      'Silent Anchor (Exclusive Outfit)', 'Gleaming Earphones (Gadget Skin)', 'Gleaming Lighter (Gadget Skin)', 'Gleaming Pen (Gadget Skin)',
      'Gleaming Phone (Gadget Skin)', "Agent's Mark (Weapon Skin)", 'Deluxe Edition Upgrade']
      .map((name, index) => ({ id: String(3950810 + index), name, enabled: true, sizeBytes: null, toggle: false, state: 'Owned' })),
    { id: '4707780', name: 'Season Pass', enabled: true, sizeBytes: null, toggle: false, state: 'Not owned' },
  ],
  folder: '0',
  folderChoices: [{ value: '0', label: 'C:', detail: '412 GB free' }, { value: '1', label: 'D:', detail: '1210 GB free' }],
  // Marvel Rivals reads the controller itself: Steam Input stays out, so no Controller in Steam row.
  storeProperties: null,
}
const EPIC_PROPERTIES: Partial<GameProperties> = {
  cloudSync: true,
  contentLabel: 'Optional content',
  content: [
    { id: 'hd_textures', name: 'High Resolution Textures', enabled: true, sizeBytes: 22 * GB, toggle: true, state: null },
    { id: 'stw', name: 'Fortnite Save the World', enabled: false, sizeBytes: 9 * GB, toggle: true, state: null },
  ],
}
function mockPropertiesFor(id: string): GameProperties {
  const game = MOCK_GAMES.find(entry => entry.id === id)
  mockProperties[id] ??= {
    ok: true, store: game?.store ?? 'local', launchOptions: '', sizeBytes: game?.sizeBytes ?? null, verifyLabel: 'Repair',
    canBrowse: true, running: !!game?.isRunning,
    cloudSync: false, language: null, languageChoices: [], beta: null, betaChoices: [], betaCode: false,
    contentLabel: 'DLC', content: null, folder: null, folderChoices: [], moving: null, storeProperties: null,
    ...(game?.store === 'steam' ? STEAM_PROPERTIES : game?.store === 'epic' ? EPIC_PROPERTIES : {}),
    ...(id === 'steam:3472040' ? { antiCheatLaunch: { value: 'with' as const, detail: 'NBA 2K26 without EAC (offline only)' } } : {}),
  }
  return mockProperties[id]
}

function mockSetProperty(params?: Record<string, unknown>) {
  const id = String(params?.id ?? '')
  const key = String(params?.key ?? '')
  const value = String(params?.value ?? '')
  const current = mockPropertiesFor(id)
  if (key === 'betaCode') return { ok: false, message: 'Steam did not accept that code.' }
  const next: GameProperties = { ...current }
  let message: string | null = null
  if (key === 'content') {
    const [itemId, state] = value.split(':')
    next.content = (current.content ?? []).map(item => item.id === itemId ? { ...item, enabled: state === 'on' } : item)
    message = state === 'on' ? 'Steam is downloading it.' : 'Steam is removing its files.'
  } else if (key === 'launchOptions') next.launchOptions = value
  else if (key === 'language') { next.language = value; message = 'Steam is updating the game.' }
  else if (key === 'storeProperties') message = "Opened in Steam. Its Properties are under the gear on the game's page."
  else if (key === 'antiCheatLaunch' && current.antiCheatLaunch) {
    next.antiCheatLaunch = { ...current.antiCheatLaunch, value: value === 'without' ? 'without' : 'with' }
    message = value === 'without' ? 'Plays offline, without anti-cheat. Frame generation and mods can go in.' : "Plays with anti-cheat. Exo's frame generation came out of the game."
  }
  mockProperties[id] = next
  return { ok: true, message, properties: next }
}

// Go Live's picker in the browser: a game, two windows and two screens, drawn as gradients.
const goLivePicture = (from: string, to: string) => `data:image/svg+xml,${encodeURIComponent(
  `<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><defs><linearGradient id="g" x2="1" y2="1"><stop stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient></defs><rect width="640" height="360" fill="url(#g)"/></svg>`)}`
const mockFavoriteEmojis = ['heart', 'fire', '968812152267866172']
const mockFavoriteStickers = ['749054660769218631']

const mockGoLive = [
  { id: 'screen:1', kind: 'screen', title: 'Screen 1', app: 'Screen', thumb: goLivePicture('#1d2b64', '#f8cdda') },
  { id: 'screen:2', kind: 'screen', title: 'Screen 2', app: 'Screen', thumb: goLivePicture('#0f2027', '#2c5364') },
  { id: 'window:1', kind: 'window', title: 'VALORANT', app: 'VALORANT', thumb: goLivePicture('#ff4655', '#0f1923'), game: 'VALORANT' },
  { id: 'window:2', kind: 'window', title: 'Exo Launcher - GitHub - Google Chrome', app: 'Google Chrome', thumb: goLivePicture('#232526', '#414345') },
  { id: 'window:3', kind: 'window', title: 'notes.txt - Notepad', app: 'Notepad', thumb: null },
]
const mockGoLiveHandler: MockHandler = (params?: { action?: string; source?: string }) => {
  if (params?.action === 'sources') return { ok: true, sources: mockGoLive }
  if (params?.action === 'preview') return { ok: true, thumb: (mockGoLive.find(s => s.id === params.source) ?? mockGoLive[0]).thumb }
  if (params?.action === 'start') {
    const title = mockGoLive.find(s => s.id === params.source)?.title ?? null
    setTimeout(() => emitHostEvent('discord.golive', { state: 'live', title }), 400)
    setTimeout(() => emitHostEvent('discord.golive.stats', { width: 1920, height: 1080, fps: 60, bitrate: 8_100_000 }), 1400)
    return { ok: true }
  }
  if (params?.action === 'stop') setTimeout(() => emitHostEvent('discord.golive', { state: 'off' }), 50)
  return { ok: true }
}

const mockCaptureState = {
  present: true, running: true, buffering: true, recording: false, holdsCall: false, recordingMs: 0, codec: 'HEVC',
  replayRetainedMs: 38_000, replayLimitSeconds: 60,
  encoder: 'NVIDIA HEVC Encoder MFT', hdr: false, hotkeys: { saveReplay: true, record: true, snip: true, printScreen: true },
}
const mockCaptureSettings: Record<string, unknown> = {
  quality: 50, replaySeconds: 60, maxFps: 60, replayMode: 'games',
  cursor: true, saveReplayKeys: 'Ctrl+Alt+F10', recordKeys: 'Ctrl+Alt+F9', snipKeys: 'Ctrl+Alt+F8', printScreen: false, afterSnip: 'edit',
  folder: 'C:\\Users\\Player\\Videos\\Exo Captures', keepGb: 0, keepDays: 0, autoShrink: true, callVoices: false,
}

/** Exo Notes in the browser: notes kept in memory, one to start from. */
const mockNotes = new Map<string, { info: NoteInfo; text: string }>()
const mockNote = (id: string, text: string, kind: NoteInfo['kind'] = 'text') => {
  const [title = 'New note', ...rest] = text.split('\n').map(line => line.replace(/^[#\s-]*(\[[ x]\] )?/, '').trim()).filter(Boolean)
  const info: NoteInfo = {
    id, title, snippet: rest.join(' ').slice(0, 140), kind, name: `${title}.${kind === 'markdown' ? 'md' : 'txt'}`, edited: new Date().toISOString(),
    size: text.length, pinned: mockNotes.get(id)?.info.pinned ?? false, external: false, unsaved: false, gameId: null, encoding: 'utf-8', eol: 'crlf', spelling: true,
  }
  mockNotes.set(id, { info, text })
  return info
}
mockNote('mock-raid', '# Raid night\n- [x] Repair gear\n- [ ] Bring potions\n\nBoss two: **stack** at the pillar.', 'markdown')

/** One Store game with Steam's longest kind of page (a long description and requirements), to lay the game page out against. */
const MOCK_STORE_TILE = { appId: '1091500', name: 'Cyberpunk 2077', final: 2999, initial: 5999, discount: 50, currency: 'USD', priceText: '$29.99', wasText: '$59.99' }
/** The library has this one, so the Store shows it with Library in place of its price. */
const MOCK_OWNED_TILE = { appId: '1145360', name: 'Hades', final: 974, initial: 2499, discount: 61, currency: 'USD', priceText: '$9.74', wasText: '$24.99' }
function mockStoreGame() {
  const art = (file: string) => `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1091500/${file}`
  return {
    ok: true, ...MOCK_STORE_TILE, comingSoon: false, released: 'Dec 9, 2020', free: false,
    description: Array.from({ length: 8 }, (_, i) => i % 3 === 2 ? 'What you get\n\u2022 An open world\n\u2022 A long story\n\u2022 A city that never sleeps' : 'Cyberpunk 2077 is an open-world, action-adventure RPG set in the dark future of Night City, a dangerous megalopolis obsessed with power, glamor, and ceaseless body modification. You play as V, a mercenary outlaw going after a one-of-a-kind implant that is the key to immortality.').join('\n\n'),
    developers: ['CD PROJEKT RED'], publishers: ['CD PROJEKT RED'], genres: ['RPG'], metacritic: 86,
    reviewWord: 'Very Positive', reviewPercent: 88, reviewCount: 812345,
    tags: ['Cyberpunk', 'Open World', 'RPG', 'Sci-fi', 'Singleplayer', 'Story Rich', 'Atmospheric'],
    controller: 'full', playStation: true, drm: null, account: null,
    languages: ['English', 'French', 'Italian', 'German', 'Spanish - Spain', 'Japanese', 'Korean', 'Polish', 'Portuguese - Brazil', 'Russian', 'Simplified Chinese'],
    offers: [{ store: '1', name: 'Steam', final: 2999, initial: 5999, discount: 50, currency: 'USD', priceText: '$29.99', wasText: '$59.99', url: 'https://store.steampowered.com/app/1091500' }],
    media: [],
    screenshots: Array.from({ length: 10 }, (_, i) => `${art(i % 2 ? 'library_hero.jpg' : 'header.jpg')}?${i}`),
    specs: [
      { label: 'OS', minimum: 'Windows 10', recommended: 'Windows 10', mine: 'Windows 11', meets: 'recommended' },
      { label: 'Processor', minimum: 'i7-6700 · Ryzen 5 1600', recommended: 'i7-12700 · Ryzen 7 7800X3D', mine: 'Ryzen 7 9800X3D', meets: 'recommended' },
      { label: 'Graphics', minimum: 'GTX 1060 · RX 580 · Arc A380', recommended: 'RTX 2060 Super · RX 5700 XT · Arc A770', mine: 'RTX 4080 Super', meets: 'recommended' },
      { label: 'Memory', minimum: '12 GB', recommended: '16 GB', mine: '32 GB', meets: 'recommended' },
      { label: 'Storage', minimum: '70 GB SSD', recommended: '70 GB SSD', mine: '412 GB free', meets: 'recommended' },
      { label: 'DirectX', minimum: 'DirectX 12', recommended: 'DirectX 12', mine: null, meets: null },
    ],
    fit: 'recommended',
  }
}

/** Exo Mouse's CPU core, through the relay: a test that offers CPU 6 after its 10 s; `?mousecore` starts after the restart, measured again. */
const MOCK_MOUSE_BEFORE = { rate: 1000, checked: 9951, missed: 40, longest: 2.1, p999: 2, refresh: true, cpu: 0, flow: { median: 1, spread: 0.03, batched: 0.4, batch: 2, filled: 99.6, madeUp: 31, stretch: false } }
const MOCK_MOUSE_AFTER = { rate: 1000, checked: 9963, missed: 2, longest: 1.6, p999: 1.1, refresh: false, cpu: 6, flow: { median: 1, spread: 0.02, batched: 0, batch: 1, filled: 99.9, madeUp: 2, stretch: false } }
let mockMouseStage: 'none' | 'restart' | 'set' = typeof location !== 'undefined' && new URLSearchParams(location.search).has('mousecore') ? 'set' : 'none'
let mockMouseTested = false

let mockMouseRecorded = false
let mockAfmf3 = false
async function mockMouseCore(params?: Record<string, unknown>) {
  if (params?.action === 'measure') {
    await new Promise(resolve => setTimeout(resolve, 10_000))
    mockMouseTested = true
  }
  if (params?.action === 'drivers') {
    await new Promise(resolve => setTimeout(resolve, 10_000))
    mockMouseTested = mockMouseRecorded = true
  }
  if (params?.action === 'apply') mockMouseStage = 'restart'
  if (params?.action === 'undo') mockMouseStage = 'none'
  const set = mockMouseStage === 'set'
  return {
    ok: true,
    stage: mockMouseStage,
    detail: mockMouseStage === 'restart' ? "CPU 6 takes your mouse's input after you restart Windows."
      : set ? "Fewer late reports since CPU 6 took your mouse's input." : null,
    cpu: mockMouseStage === 'none' ? null : 6,
    better: set ? true : null,
    canUndo: mockMouseStage !== 'none',
    canName: mockMouseTested && mockMouseStage !== 'restart' && !set,
    drivers: !mockMouseRecorded ? null : { late: 38, explained: 31, cpu: 0, problem: null, names: [
      { file: 'nvlddmkm.sys', gaps: 26, own: 1.8, gap: 2.1, product: 'NVIDIA Windows Kernel Mode Driver', version: '581.42', origin: 'Interrupts as Windows set them', path: false },
    ] },
    before: mockMouseStage === 'none' ? null : MOCK_MOUSE_BEFORE,
    after: set ? MOCK_MOUSE_AFTER : null,
    test: !mockMouseTested ? null : set
      ? { verdict: 'fine', detail: '1000 Hz, 2 of 9,963 reports late, the longest 1.6 ms. Too few to matter, so nothing to change.', offer: null, result: MOCK_MOUSE_AFTER }
      : { verdict: 'fix', detail: "1000 Hz, 40 of 9,951 reports late, the longest 2.1 ms. They come with your screen's refresh. Its input lands on CPU 0, where Windows and most drivers go first. CPU 6 is quiet.", offer: 6, result: MOCK_MOUSE_BEFORE },
  }
}

/** The mock's song (spotifyMock › Song 1), playing from the page's start and looping, so the room and Immersive have one. */
const mockSong = { playing: true, from: 0, since: 0 }
const mockSongAt = () => (mockSong.from + (mockSong.playing ? performance.now() - mockSong.since : 0)) % MOCK_SONG_MS
const mockPlayback = () => ({ provider: 'spotify', status: mockSong.playing ? 'playing' : 'paused', title: 'Song 1', artist: 'Artist 1', artworkDataUrl: mockArtwork,
  positionMs: mockSongAt(), durationMs: MOCK_SONG_MS, positionAgeMs: 0, rate: mockSong.playing ? 1 : 0, canPlay: true, canPause: true, canPrev: true, canNext: true, canSeek: true })

const MOCK_HANDLERS: Record<string, MockHandler> = {
  ...spotifyMock,
  'library.get': mockLibraryGet,
  'library.refresh': mockLibraryGet,
  'game.get': mockGameGet,
  'game.launch': mockRunState(true),
  'game.stop': mockRunState(false),
  'game.install': mockGameInstall,
  'game.update': mockGameInstall,
  'game.resumeDownload': () => ({ ok: false, message: 'Browser mock — saved downloads resume only inside the Exo host.' }),
  'game.uninstall': () => ({ ok: false, message: 'Browser mock.' }),
  'game.openFolder': mockOk,
  'game.properties': (params) => mockPropertiesFor(String(params?.id ?? '')),
  'game.properties.set': mockSetProperty,
  'art.replace': () => ({ ok: false, cancelled: false, message: MOCK_NO_PICKER }),
  'art.reset': mockArtRefresh(true),
  'art.refetch': mockArtRefresh(false),
  // No cover cache here: store art off Steam's CDN, for the games the dev preview's friends play.
  'art.base': () => ({ covers: 'https://covers.exo-launcher.local/mock/' }),
  // No cover cache here: the picture comes back as itself.
  'library.folderArt': (params) => ({ ok: true, art: `data:image/png;base64,${String(params?.image ?? '')}` }),
  'library.refreshAll': () => ({ ok: true, achievements: 0, covers: 0 }),
  'game.repair': () => ({ ok: true, queued: false, message: 'Mock: would verify files.' }),
  'game.extras': () => ({ ok: true, achievementsUnlocked: 23, achievementsTotal: 50, driveHeat: { peak: 79, warn: 82, slowedSeconds: 40 } }),
  'game.cancelInstall': mockCancelInstall,
  'game.dequeue': mockOk,
  'downloads.list': () => ({ ok: true, held: null, pausedByPlayer: false, autoUpdateGames: true, lines: [
    { gameId: 'mock:eldenring', title: 'Elden Ring', store: 'epic', state: 'running', canPause: true, autoUpdate: true },
    { gameId: 'mock:hades', title: 'Hades', store: 'gog', state: 'queued', canPause: false, autoUpdate: true },
    { gameId: 'mock:valorant', title: 'VALORANT', store: 'riot', state: 'queued', canPause: false, autoUpdate: false },
    { gameId: 'mock:cyberpunk', title: 'Cyberpunk 2077', store: 'gog', state: 'paused', canPause: false, autoUpdate: true },
  ] }),
  'downloads.move': mockOk,
  'downloads.pauseAll': mockOk,
  'downloads.resumeAll': mockOk,
  'game.autoUpdate': (params) => ({ ok: true, on: params?.on ?? true }),
  'game.progress': () => mockProgress,
  'stores.auth': () => ({ ok: false, message: 'Browser mock — auth requires host.', requiresUserAction: true }),
  'stores.outages': () => ({ ok: true, outages: [] }),
  'stores.playOffline': () => ({ ok: false, message: 'Mock: offline mode needs the Exo host.' }),
  'steam.setOffline': async (params) => {
    const offline = (params as { offline?: boolean } | undefined)?.offline === true
    await new Promise(resolve => setTimeout(resolve, 1200))
    MOCK_STEAM_OFFLINE = offline
    return { ok: true, message: offline ? 'Steam is in offline mode.' : 'Steam is online.' }
  },
  // Steam's library sign-in: the QR waits two polls, then the account is signed in.
  'steam.account': () => ({ signedIn: MOCK_STEAM_ACCOUNT !== null, accountName: MOCK_STEAM_ACCOUNT }),
  'steam.signInCancel': () => ({ ok: true }),
  'steam.signInQr': () => {
    MOCK_STEAM_POLLS = 0
    return { state: 'waiting', challengeUrl: 'https://s.team/q/1/77', intervalSeconds: 5 }
  },
  'steam.signInPoll': () => {
    MOCK_STEAM_POLLS += 1
    if (MOCK_STEAM_POLLS < 2) return { state: 'waiting', challengeUrl: 'https://s.team/q/1/77', intervalSeconds: 5 }
    MOCK_STEAM_ACCOUNT = 'exo-player'
    return { state: 'done', accountName: 'exo-player' }
  },
  'steam.signInPassword': () => ({ state: 'guard', guard: 'DeviceCode', accountName: 'exo-player', intervalSeconds: 5 }),
  'steam.signInGuard': () => ({ state: 'approve', accountName: 'exo-player', intervalSeconds: 5 }),
  'steam.downloads': () => ({ ok: true }),
  'steam.signOut': () => {
    MOCK_STEAM_ACCOUNT = null
    return { ok: true }
  },
  // What tracker.gg answers apps now (TrackerGgPlaytime.BlockedMessage): a browser check, so nothing is imported.
  'playtime.riotRefresh': () => ({
    ok: false,
    profilePrivate: false,
    message: "tracker.gg now blocks apps from reading its profiles, so Exo can't import your Riot hours from it. Exo still counts every session you play.",
    profileUrl: 'https://tracker.gg/valorant',
  }),
  'web.preferences.get': () => ({ microphone: 'ask', camera: 'ask', clipboard: 'ask', notifications: 'ask' }),
  // The state the engine left when it last exited (rev 0), shown until the board is on screen and the engine runs.
  'shaders.addon': () => ({ ok: true, present: mockShaders, snapshot: mockShaders ? { ...mockShadersSnapshot(), rev: 0 } : undefined }),
  'capture.status': () => ({ ok: true, state: mockCaptureState, settings: { ...mockCaptureSettings } }),
  // A new object each time, as the host's reply is: the same one changed in place never redraws the rows.
  'capture.settings': (params) => {
    Object.assign(mockCaptureSettings, params)
    return { ok: true, state: mockCaptureState, settings: { ...mockCaptureSettings } }
  },
  'capture.act': () => ({ ok: true, state: mockCaptureState }),
  // ?clips=none has no captures and ?clips=fail cannot read them, for looking at Play's Clips in those states.
  'capture.list': (params) => mockClipsFlag === 'fail' ? { ok: false } : { ok: true, items: mockClipsFlag === 'none' ? [] : params?.gameId ? mockCaptures.filter(item => item.gameId === params.gameId) : mockCaptures },
  'capture.item': mockCaptureItem,
  'capture.edit': () => ({ ok: false, message: 'Mock: no capture add-on.' }),
  'capture.reel': mockReel,
  'capture.reel.cancel': () => {
    clearInterval(mockReelTimer)
    emitHostEvent('capture.reel', { state: 'stopped', percent: 0, wait: false })
    return { ok: true }
  },
  'capture.saveImage': () => ({ ok: false, message: 'Mock: no capture add-on.' }),
  'capture.text': (params) => params?.width !== undefined && Number(params.width) < 4 ? { ok: false, message: 'No text found there.' } : { ok: true, text: ['VICTORY', 'PlayerOne 24 / 3 / 11'].join('\n') },
  'capture.send': (params) => {
    const item = mockCaptures.find(entry => entry.id === mockParamId(params))
    if (!item) return { ok: false, message: 'That capture is gone.' }
    const channels = Array.isArray(params?.channelIds) ? params.channelIds as string[] : []
    return { ok: true, sent: channels, item: null }
  },
  'notes.list': (params) => {
    const query = typeof params?.query === 'string' ? params.query.toLowerCase() : ''
    return { ok: true, notes: [...mockNotes.values()].filter(note => !query || note.text.toLowerCase().includes(query)).map(note => note.info), tabs: [], active: null }
  },
  'notes.read': (params) => {
    const note = mockNotes.get(String(params?.id))
    return note ? { ok: true, text: note.text, note: note.info } : { ok: false, message: 'That note is gone.' }
  },
  'notes.save': (params) => {
    const id = typeof params?.id === 'string' ? params.id : `mock-${mockNotes.size + 1}`
    const kind = params?.markdown === true ? 'markdown' : mockNotes.get(id)?.info.kind ?? 'text'
    return { ok: true, note: mockNote(id, String(params?.text ?? ''), kind) }
  },
  'notes.photo': () => ({ ok: true, src: 'Photos/photo-2026-10-29-153012.png' }),
  'notes.act': (params) => {
    const id = String(params?.id)
    const note = mockNotes.get(id)
    if (params?.action === 'delete') { mockNotes.delete(id); return { ok: true } }
    if (!note) return { ok: params?.action === 'folder' }
    if (params?.action === 'pin' || params?.action === 'unpin') note.info = { ...note.info, pinned: params.action === 'pin' }
    if (params?.action === 'markdown' || params?.action === 'text') note.info = { ...note.info, kind: params.action }
    return { ok: true, note: note.info }
  },
  'notes.session': () => ({ ok: true }),
  'notes.open': () => ({ ok: false, cancelled: true }),
  'notes.saveAs': () => ({ ok: false, message: 'Mock: no save dialog in the browser.' }),
  'notes.pending': () => ({ id: null }),
  'app.pendingOpen': () => ({ room: null, part: null }),
  'notes.spell': (params) => ({ ok: true, wrong: params?.word === 'teh', suggestions: params?.word === 'teh' ? ['the', 'tech'] : [] }),
  'notes.textFiles': () => ({ ok: true, offered: false, isDefault: false }),
  'shell.pickProgram': () => ({ ok: false, cancelled: true }),
  'shaders.call': (params) => {
    if (!mockShaders) return { ok: false, message: 'Mock: no shader add-on.' }
    if (params?.method === 'scan') mockShadersScanned = true
    if (params?.method === 'game.diagnostics') return { ok: true, result: { creates: 79500, tupleKnown: 60796, stateObjects: 5672, stateObjectsWarmed: 0, knownMedianMs: 146, verdict: 'missed', needsFirstLaunch: true } }
    if (params?.method === 'snapshot') {
      mockShaderReads++
      return { ok: true, result: mockShaderGone && mockShaderReads > 1 ? null : mockShadersSnapshot() }
    }
    return { ok: true, result: {} }
  },
  'shaders.estimates': () => ({ ok: true, seconds: {} }),
  'shaders.rebuild': () => ({ ok: true, state: 'idle', ready: 0, total: 0 }),
  'friends.badge': () => ({ count: 0 }),
  'discord.status': () => mockDiscordStatus,
  'discord.signIn': mockOk,
  'discord.signOut': mockOk,
  'discord.home': () => mockHome,
  'discord.channels': (params?: { guildId?: string }) => ({ ...mockChannelsFor(params?.guildId ?? ''), hideMuted: mockHideMuted }),
  'discord.admin': mockAdmin,
  'discord.messages': (params?: { before?: string }) => ({ ok: true, messages: params?.before ? [] : (mockFew ? mockFewMessages : [...mockMessages, ...mockAppMessages]).slice().sort((a, b) => (BigInt(b.id) > BigInt(a.id) ? 1 : -1)) }),
  'discord.apps': (params?: { action?: string }) => params?.action === 'commands' ? mockApps : { ok: true, nonce: '900000000000000001' },
  'discord.send': mockOk,
  'discord.edit': mockOk,
  'discord.delete': mockOk,
  'discord.ack': mockOk,
  'discord.thread': () => ({ ok: true, thread: { id: '600000000000000003', name: 'New post' } }),
  'discord.typing': mockOk,
  'discord.upload': () => ({ ok: true, uploadId: 'mock' }),
  'discord.react': mockOk,
  'discord.emojis': () => ({ premium: false, guilds: [{ id: '200000000000000001', name: 'Exo Lab', icon: null, emojis: [{ id: '968812152267866172', name: 'Troll', animated: false, native: true }, { id: '968812607832203274', name: 'Yoshi', animated: false, native: false }] }] }),
  'discord.emojiFavorites': (p?: { action?: string; emoji?: string }) => {
    if (p?.emoji && p.action === 'add' && !mockFavoriteEmojis.includes(p.emoji)) mockFavoriteEmojis.push(p.emoji)
    if (p?.emoji && p.action === 'remove') mockFavoriteEmojis.splice(mockFavoriteEmojis.indexOf(p.emoji), 1)
    return { ok: true, favorites: [...mockFavoriteEmojis], frequent: ['joy', 'thumbsup', 'fire', '968812152267866172', 'heart', 'skull', 'sob', 'eyes', 'pray', 'copyright'], reactions: ['thumbsup', 'joy', 'heart'] }
  },
  'discord.view': mockOk,
  // Watching a stream in the mock: always taken (no pictures arrive), so its view, controls and menus can be seen.
  'discord.stream': () => ({ ok: true }),
  'discord.voice': (params?: { action?: string; guildId?: string; channelId?: string }) => ({ ok: true, voice: params?.action === 'join' ? { state: 'connected', guildId: params.guildId ?? null, channelId: params.channelId ?? null, muted: false, deafened: false, e2ee: true, recorded: params.channelId === '400000000000000005' } : { state: 'off', guildId: null, channelId: null, muted: false, deafened: false, e2ee: false } }),
  'discord.ring': mockOk,
  'discord.golive': mockGoLiveHandler,
  'discord.friends': () => ({ friends: mockFriends, restricted: { blocked: [{ id: '100000000000000009', name: 'Spammy', username: 'spammy.bot', avatar: null }], ignored: [{ id: '100000000000000008', name: 'Loud Larry', username: 'larry', avatar: null }] } }),
  'discord.friend': mockOk,
  'discord.callSongs': () => ({ songs: [] }),
  'discord.markRead': mockOk,
  'discord.channel': () => ({ ok: true, muted: true, url: 'https://discord.gg/exo' }),
  'discord.mentions': () => ({ ok: true, messages: mockMessages.filter(m => m.mentions.length), places: { '400000000000000002': { name: 'general', guildId: '300000000000000001', guild: 'Night Raid', parentId: null } } }),
  'discord.forYou': () => ({ ok: true, hasMore: false, items: [
    { id: '1300000000000000000', type: 'friend_request_accepted', body: '**Kai** accepted your friend request.', acked: false, other_user: mockMessages[2].author },
    { id: '1290000000000000000', type: 'scheduled_guild_event_started', body: '**Scrim night** is starting in **Night Raid**.', acked: true, deeplink: 'https://discord.com/channels/300000000000000001/400000000000000002' },
  ] }),
  'discord.unreads': () => ({ channels: [
    { channelId: '400000000000000002', name: 'general', guildId: '300000000000000001', guild: 'Night Raid', icon: null, parentId: null, lastRead: mockMessageId(8), lastMessage: mockMessageId(12), mentions: 1 },
    { channelId: '410000000000000002', name: 'clips', guildId: '300000000000000002', guild: 'Frame Pacing Club', icon: null, parentId: null, lastRead: mockMessageId(1), lastMessage: mockMessageId(10), mentions: 12 },
    { channelId: '410000000000000003', name: 'lfg', guildId: '300000000000000002', guild: 'Frame Pacing Club', icon: null, parentId: null, lastRead: mockMessageId(1), lastMessage: mockMessageId(6), mentions: 0 },
  ] }),
  'discord.reactors': () => ({ ok: true, canRemove: true, users: mockMessages.slice(0, 3).map(m => m.author) }),
  'discord.fileText': () => ({ ok: true, left: 0, text: 'sensitivity "0.42"\nm_yaw "0.022"\nfps_max "0"\ncl_crosshairsize "2"\ncl_crosshairgap "-2"\ncl_crosshaircolor "1"\nbind "mouse4" "+voicerecord"\nbind "f" "+lookatweapon"\nhost_writeconfig\n' }),
  'discord.audio': () => ({ inputs: [{ id: 'mic', name: 'Microphone' }], outputs: [{ id: 'spk', name: 'Speakers' }], input: null, output: null, inputVolume: 100, outputVolume: 100, soundboardVolume: 100, soundboardMuted: false }),
  'discord.userSettings': (params?: { changes?: Record<string, unknown> }) => ({ ok: true, values: params?.changes ?? {} }),
  'discord.account': (params?: { action?: string }) => params?.action === 'tags'
    ? { ok: true, mine: null, servers: [{ id: '200000000000000001', name: 'Exo Lab', tag: 'EXO', badge: null }] }
    : params?.action === 'setTag' ? { ok: true }
    : params?.action === 'me'
    ? { ok: true, me: { id: '1', username: 'exo', globalName: 'Exo', avatar: null, banner: null, accentColor: null, bio: '', pronouns: '', email: 'e***@example.com', phone: null, verified: true, mfa: false, nsfwAllowed: true, premium: false } }
    : { ok: true, data: params?.action === 'consent' ? { usageStatistics: true, personalization: true } : [] },
  'discord.clientSettings': (params?: Record<string, unknown>) => (mockDiscordClient = { ...mockDiscordClient, ...params }),
  'discord.micTest': mockOk,
  'discord.micMonitor': () => ({ ok: true, on: false, onHead: true }),
  'discord.sound': mockOk,
  'discord.server': (p?: { action?: string; invite?: string; hideMuted?: boolean }) => p?.action === 'notify' && p.hideMuted !== undefined
    ? (mockHideMuted = p.hideMuted, { ok: true })
    : p?.action === 'profile'
    ? { ok: true, profile: { id: '200000000000000001', name: 'Exo Lab', icon_hash: null, description: 'Where Exo gets built.', member_count: 1204, online_count: 312, tag: 'EXO', traits: [{ label: 'Gaming', emoji_name: '🎮', position: 0 }] } }
    : p?.action === 'invite'
    ? p.invite === 'expired' ? { ok: false, invalid: true, message: 'Discord: Unknown Invite' }
      : { ok: true, invite: p.invite === 'exo'
        ? { guildId: '300000000000000001', channelId: '400000000000000002', name: 'Night Raid', icon: null, verified: false, partnered: false, online: 12, members: 48, joined: true }
        : { guildId: '300000000000000090', channelId: '400000000000000090', name: 'VALORANT', icon: 'https://cdn.discordapp.com/embed/avatars/0.png', verified: true, partnered: false, online: 182340, members: 1204512, joined: false } }
    : p?.action === 'members'
    ? { ok: true, roles: { '300000000000000001': { name: 'Mods', color: '#e91e63', position: 2 } }, members: { total_result_count: 1, members: [{ member: { user: { id: '100000000000000002', username: 'ada', global_name: 'Ada' }, roles: ['300000000000000001'], joined_at: '2026-09-01T12:00:00Z' }, source_invite_code: 'exo' }] } }
    : p?.action === 'events'
    ? { ok: true, events: [{ interested: false, event: { id: '700000000000000001', name: 'Ranked night', scheduled_start_time: '2026-10-20T01:00:00Z', status: 1, user_count: 6, entity_metadata: { location: 'Voice' } } }] }
    : p?.action === 'guide'
      ? { ok: true, guide: { welcome_message: { message: 'Read the rules, grab a role, say hi.' }, new_member_actions: [{ channel_id: '400000000000000001', title: 'Say hi', description: 'Introduce yourself', emoji: { name: '👋' } }], resource_channels: [] }, resources: [{ channelId: '400000000000000002', title: 'Rules', description: 'Read before posting' }] }
      : { ok: true },
  'discord.profile': (p?: { mutualFriends?: boolean; note?: string }) => p?.mutualFriends ? { ok: true, friends: [{ id: '100000000000000003', username: 'kai', global_name: 'Kai' }] } : p?.note !== undefined ? mockOk() : mockProfile,
  'discord.threads': () => ({ ok: true, threads: [{ id: '600000000000000001', name: 'Best crosshair codes?', parent_id: '400000000000000007', message_count: 42 }, { id: '600000000000000002', name: 'Patch 9.2 tier list', parent_id: '400000000000000007', message_count: 7 }] }),
  'discord.stickers': (p?: { action?: string; stickerId?: string }) => {
    if (p?.stickerId && p.action === 'add' && !mockFavoriteStickers.includes(p.stickerId)) mockFavoriteStickers.push(p.stickerId)
    if (p?.stickerId && p.action === 'remove') mockFavoriteStickers.splice(mockFavoriteStickers.indexOf(p.stickerId), 1)
    return { premium: false, favorites: [...mockFavoriteStickers], groups: [
      { id: 'pack:847199849233514549', name: 'Wumpus Beyond', icon: null, usable: true, stickers: [{ id: '749054660769218631', name: 'Wave', format: 1 }, { id: '749052944682582036', name: 'Lurk', format: 1 }] },
      { id: '300000000000000002', name: 'Frame Pacing Club', icon: null, usable: false, stickers: [{ id: '749043879713767424', name: 'Hi', format: 1 }] },
    ] }
  },
  'discord.sticker': mockOk,
  'discord.pins': () => ({ ok: true, messages: [] }),
  'discord.search': (p?: { filters?: { offset?: number } }) => ({ ok: true, result: { messages: mockMessages.slice(0, 4).map(m => [m]), total_results: 87 }, places: {}, offset: p?.filters?.offset }),
  'discord.gifs': () => ({ ok: true, gifs: [] }),
  'discord.members': () => mockMembers,
  'discord.saved': (p?: { action?: string; messageId?: string; dueAt?: number }) => {
    if (p?.action === 'save') { if (p.dueAt) mockSaved.reminders.set(p.messageId ?? '', p.dueAt); else mockSaved.bookmarks.add(p.messageId ?? ''); return { ok: true } }
    if (p?.action === 'remove') { mockSaved.reminders.delete(p.messageId ?? ''); mockSaved.bookmarks.delete(p.messageId ?? ''); return { ok: true } }
    const saved = (id: string, due?: number) => ({ message: mockMessages.find(m => m.id === id) ?? null, save_data: { channel_id: '400000000000000002', message_id: id, saved_at: new Date().toISOString(), due_at: due ? new Date(due).toISOString() : null } })
    if (p?.action === 'bookmarks') return { ok: true, results: [...mockSaved.bookmarks].map(id => saved(id)), hasMore: false, places: {} }
    return { ok: true, reminders: [...mockSaved.reminders].map(([id, due]) => saved(id, due)), bookmarkIds: [...mockSaved.bookmarks], places: {} }
  },
  'discord.mentionable': (p?: { query?: string; channels?: boolean }) => {
    const q = (p?.query ?? '').toLowerCase()
    if (p?.channels) return { users: [], channels: mockChannels.channels.filter(c => c.kind !== 'category' && c.name.includes(q)).map(c => ({ id: c.id, name: c.name, type: c.type, kind: c.kind, category: 'Text channels' })) }
    return {
      users: mockMessages.map(m => m.author).filter((u, i, all) => all.findIndex(x => x.id === u.id) === i && u.username.startsWith(q)),
      roles: [{ id: '700000000000000001', name: 'Moderators', color: '#3498db' }, { id: '700000000000000002', name: 'Members', color: null }].filter(r => r.name.toLowerCase().includes(q)),
    }
  },
  'discord.colors': () => ({ colors: { '100000000000000002': '#e91e63', '100000000000000004': '#3498db' }, icons: { '100000000000000002': { name: 'Admins', emoji: '🛡️' } } }),
  'discord.setStatus': mockOk,
  'web.preferences.set': (p) => ({ microphone: 'ask', camera: 'ask', clipboard: 'ask', notifications: 'ask', ...p }),
  'shell.prepare': () => ({ ok: true }),
  'shell.painted': () => ({ ok: true }),
  'shell.ready': () => ({ ok: true }),
  'music.playback': () => ({ ok: true, playback: mockPlayback() }),
  // The mock's music level: a set answers with the level it took (the room's slider follows it).
  'music.volume': (p) => { if (typeof p?.level === 'number') mockMusicLevel = p.level; return { ok: true, level: mockMusicLevel, muted: mockMusicLevel <= 0, source: 'app' } },
  'music.control': (p) => {
    const at = mockSongAt()
    if (p?.action === 'toggle') { mockSong.playing = !mockSong.playing; mockSong.from = at; mockSong.since = performance.now() }
    else if (p?.action === 'seek' && typeof p.positionMs === 'number') { mockSong.from = p.positionMs; mockSong.since = performance.now() }
    else return { ok: false, message: 'Open Exo to control music.' }
    emitHostEvent('music.playback', mockPlayback())
    return { ok: true }
  },
  'deps.list': mockDepsList,
  'replaced.scan': () => ({ ok: true, cleared: 2, apps: MOCK_REPLACED }),
  'apps.list': () => ({ ok: true, programs: MOCK_PROGRAMS }),
  'apps.tidy': () => ({ ok: true, token: 'mock-tidy', name: 'Programs that are gone', defaults: ['.oldproj', '.oldtool'], autoCleared: [{ id: 'mock-3', name: 'Old leftovers', cleared: { folders: 1, files: 0, registry: 2, shortcuts: 3, bytes: 310_000_000 } }], items: [
    { id: 0, kind: 'service', path: 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\OldToolUpdate', bytes: 0, selected: true, note: null, certainty: 'certain', because: 'Certain: points into the app’s folder', machine: true, from: 'Old Tool' },
    { id: 2, kind: 'driver', path: 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\OldToolFilter', bytes: 0, selected: false, note: 'A driver: only take it if no device needs it', certainty: 'certain', because: 'Certain: points into the app’s folder', store: true, machine: true, from: 'Old Tool' },
    { id: 3, kind: 'registry', path: 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\SharedAccess\\Parameters\\FirewallPolicy\\FirewallRules › {OLD-TOOL-PORT}', bytes: 0, selected: false, note: null, certainty: 'possible', because: 'Possible: in a firewall group with its rules', machine: true, from: 'Old Tool' },
    { id: 1, kind: 'folder', path: 'Program Files\\Old Tool', bytes: 1_204_000_000, selected: false, note: null, certainty: 'likely', because: 'Likely: named after it', machine: true, from: 'Old Tool' },
  ] }),
  'apps.leave': () => ({ ok: true }),
  'apps.history': () => ({ ok: true, items: [
    { id: 'mock-1', name: 'Paint Pro', at: '2026-11-01T18:00:00Z', cleared: { folders: 2, files: 0, registry: 9, shortcuts: 1, bytes: 412_000_000 }, canPutBack: true, undone: false },
    { id: 'mock-2', name: 'Old Tool', at: '2026-10-12T18:00:00Z', cleared: { folders: 0, files: 0, registry: 0, shortcuts: 0, bytes: 0 }, canPutBack: false, undone: false },
  ] }),
  'apps.undo': () => ({ ok: true, back: 3 }),
  'apps.icons': () => ({ ok: true, icons: {} }),
  'apps.confirm': (p) => ({ ok: true, ids: (p as { ids?: string[] } | undefined)?.ids ?? [], skipped: [] }),
  // Browser mock: the consent is decided, so the sheet stays away and Settings > Privacy shows the switches off.
  'telemetry.sent': () => ({ ok: true, payloads: [] }),
  'telemetry.delete': () => ({ ok: true, message: null }),
  'telemetry.track': () => ({ ok: true }),
  'apps.uninstall': (p) => {
    const id = (p as { id?: string } | undefined)?.id
    const name = MOCK_PROGRAMS.find(app => app.id === id)?.name ?? 'App'
    MOCK_PROGRAMS = MOCK_PROGRAMS.filter(app => app.id !== id)
    return {
      ok: true, token: 'mock', name,
      items: [
        { id: 0, kind: 'folder', path: `AppData\\Roaming\\${name}`, bytes: 820_000, selected: false, note: null, certainty: 'likely', because: 'Likely: named after it', machine: false, from: name },
      ],
      auto: true, cleared: { folders: 1, files: 0, registry: 1, shortcuts: 1, bytes: 4_101_200 }, undo: 'mock-1', declined: false,
    }
  },
  'apps.restorePoint': () => ({ ok: true, made: true, message: null }),
  'apps.installerCache': () => ({ ok: true, files: 7, bytes: 1_380_000_000 }),
  'apps.installerCacheClean': () => ({ ok: true, files: 7, bytes: 1_380_000_000, undo: 'mock-1', message: null }),
  'apps.scan': () => ({ ok: false, message: 'Mock: nothing to scan.' }),
  'apps.clean': () => ({ ok: true, cleared: { folders: 1, files: 0, registry: 1, shortcuts: 1, bytes: 4_101_200 }, undo: 'mock-1', left: 0 }),
  'apps.allowEdge': () => ({ ok: true }),
  'apps.openDefaults': () => ({ ok: true, message: null }),
  'apps.packages': () => ({
    ok: true, hidden: 41, items: [
      { id: 'Microsoft.BingNews_8wekyb3d8bbwe', name: 'Microsoft News', publisher: 'Microsoft Corporation', forMe: true, others: 1, provisioned: true, bloat: 'Comes with Windows; nothing needs it', addBack: false },
      { id: 'king.com.CandyCrushSaga_kgqvnymyfvs32', name: 'Candy Crush Saga', publisher: 'king.com', forMe: true, others: 0, provisioned: false, bloat: 'Windows installed it as an ad', addBack: false },
      { id: 'Microsoft.WindowsMaps_8wekyb3d8bbwe', name: 'Windows Maps', publisher: null, forMe: false, others: 0, provisioned: true, bloat: 'Comes with Windows; nothing needs it', addBack: true },
      { id: 'Microsoft.WindowsCalculator_8wekyb3d8bbwe', name: 'Windows Calculator', publisher: 'Microsoft Corporation', forMe: true, others: 2, provisioned: true, bloat: null, addBack: false },
    ],
  }),
  'apps.packagesPlan': (p) => {
    const params = (p ?? {}) as { ids?: string[]; everyone?: boolean }
    return {
    ok: true, token: 'mock-plan', admin: Boolean(params.everyone),
    lines: (params.ids ?? []).map(id => params.everyone
      ? { id, name: id.split('_')[0], does: 'Removed for every account, and new accounts won’t get it, with their data', back: 'Can’t be undone here: only the Microsoft Store brings it back, if it has it', undoable: false }
      : { id, name: id.split('_')[0], does: 'Removed for you, with its data; new accounts still get it', back: 'Add back puts it back here (its files stay on this PC); its data doesn’t come back', undoable: true }),
    message: null,
    }
  },
  'apps.packagesRemove': () => ({ ok: true, removed: 1, left: 0, message: null }),
  'apps.packageAddBack': () => ({ ok: true, store: false, message: null }),
  'replaced.remove': (p) => {
    const id = (p as { id?: string } | undefined)?.id
    MOCK_REPLACED = MOCK_REPLACED.map(app => app.id === id ? { ...app, installed: false, leftovers: 0, bytes: 0, needsAdmin: false, found: NONE_LEFT, cleared: { ...app.found, registry: app.found.registry + 7 } } : app)
    return { ok: true, removed: 9, left: 0, apps: MOCK_REPLACED }
  },
  'runtimes.list': () => ({ ok: true, items: MOCK_RUNTIMES }),
  'runtimes.install': mockRuntimesInstall,
  'runtimes.cancel': () => ({ ok: true, message: 'Stopping the downloads.' }),
  'stores.check': mockStoresCheck,
  'stores.matrix': mockStoresMatrix,
  'settings.get': () => ({ ...mockSettings }),
  'settings.set': mockSettingsSet,
  'gifts.list': () => ({ ok: true, steamSignedIn: true, gifts: MOCK_GIFTS }),
  'gifts.answer': (params) => {
    const id = (params as { id?: string } | undefined)?.id
    MOCK_GIFTS = MOCK_GIFTS.filter(gift => gift.id !== id)
    return { ok: true, message: (params as { accept?: boolean } | undefined)?.accept ? 'Added to your library.' : 'Declined.' }
  },
  'gifts.steamSignIn': () => ({ ok: true, message: "Signed in to Steam's website." }),
  'gifts.steamSignOut': () => ({ ok: true, message: "Signed out of Steam's website." }),
  'accounts.list': () => ({
    ok: true,
    steam: {
      current: 'Player One',
      offline: MOCK_STEAM_OFFLINE,
      canOffline: true,
      accounts: [
        { key: 'mock-steam-main', name: 'Player One', current: true, remembered: true },
        { key: 'mock-steam-alt', name: 'Rocket alt', current: false, remembered: true },
        { key: 'mock-steam-old', name: 'Old login', current: false, remembered: false },
      ],
    },
    riot: {
      current: 'PlayerOne#NA1',
      accounts: [
        { key: 'mock-riot-main', name: 'PlayerOne#NA1', current: true, saved: true, lastIn: [] },
        { key: 'mock-riot-val', name: 'Smurf#VAL', current: false, saved: true, lastIn: ['VALORANT'] },
      ],
    },
    epic: { signedIn: true, name: 'PlayerOne', launcherDiffers: true, canImport: true, added: [{ key: 'mock-epic-alt', name: 'RocketAlt' }] },
    gog: { signedIn: true, canSignOut: true },
    amazon: { signedIn: false, name: null },
  }),
  'accounts.switch': () => ({ ok: false, message: 'Mock: switching accounts needs the Exo host.' }),
  'accounts.signOut': () => ({ ok: false, message: 'Mock: signing out needs the Exo host.' }),
  'accounts.add': () => ({ ok: false, message: 'Mock: adding an account needs the Exo host.' }),
  'accounts.remove': () => ({ ok: false, message: 'Mock: removing an account needs the Exo host.' }),
  'accounts.epicImport': () => ({ ok: false, message: 'Mock: needs the Exo host.' }),
  'accounts.epicAdd': () => ({ ok: false, message: 'Mock: needs the Exo host.' }),
  'accounts.epicRemove': () => ({ ok: false, message: 'Mock: needs the Exo host.' }),
  'accounts.epicNewLauncher': () => ({ ok: false, message: 'Mock: needs the Exo host.' }),
  // A PC already on the current clients, so the browser preview doesn't open the Game clients start card; `?clients`
  // shows a PC that needs its Fix (Epic's old launcher, Steam on stable), for the start card's own check.
  // ?clients: a PC that needs its Fix; ?clients=unreadable: a check Exo could not answer (Steam's branch unreadable); ?clients=failed:
  // the read itself fails (the step's own Try again).
  'clients.status': () => {
    const mode = typeof location !== 'undefined' ? new URLSearchParams(location.search).get('clients') : null
    if (mode === 'failed') throw new Error('Mock: the game clients check could not be read.')
    if (mode === 'unreadable') return { ok: true, needsFix: false, problem: 'Exo couldn’t read Steam.', epic: { layout: 'new', version: '0.113.0', oldStillThere: false, installing: false }, steam: { channel: 'unknown' } }
    if (mode !== null) return { ok: true, needsFix: true, epic: { layout: 'old', version: '0.112.0', oldStillThere: false, installing: false }, steam: { channel: 'stable' } }
    return { ok: true, needsFix: false, epic: { layout: 'new', version: '0.113.0', oldStillThere: false, installing: false }, steam: { channel: 'beta' } }
  },
  'clients.fix': () => ({ ok: false, message: 'Mock: fixing clients needs the Exo host.' }),
  // Backups. ?backup: yesterday's daily backup is there (Settings' Last backup; on first run the restore offer, from another PC).
  'backup.status': () => {
    const day = mockBackupDay
    return {
      ok: true,
      last: day ? { day, at: new Date(Date.now() - 86_400_000).toISOString() } : null,
      offer: day && mockFirstRun ? { day, device: 'a1b2c3d4', files: 9 } : null,
    }
  },
  'backup.now': () => {
    mockBackupDay = new Date().toISOString().slice(0, 10)
    return { ok: true, message: 'Backed up 9 files.', files: 9 }
  },
  'backup.export': () => ({ ok: true, message: 'Saved 9 files to the file you chose.', files: 9 }),
  'backup.import': () => ({ ok: true, message: 'Restored 9 files.', files: 9, restarting: true }),
  'backup.restore': () => ({ ok: true, message: 'Restored 9 files.', files: 9, restarting: true }),
  'backup.decline': () => ({ ok: true }),
  'shell.minimize': mockOk,
  'shell.maximize': mockOk,
  'shell.windowState': mockOk,
  'shell.heartbeat': mockOk,
  'shell.close': mockOk,
  'shell.openUrl': mockOk,
  'app.repairCache': mockOk,
  'shell.showStore': mockOk,
  'shell.setChromeLayout': mockOk,
  'music.warm': mockOk,
  'app.checkUpdate': () => mockUpdate
    ? { ok: true, updateAvailable: true, offerOnOpen: true, latest: '3.15.0', message: 'Version 3.15.0 is available.', current: '1.0.0-dev', upcoming: { version: '3.15.0', items: [
        { icon: 'Download', title: "See what's coming", detail: "What's New shows the next version's changes before you install it." },
        { icon: 'Sparkle', title: 'Catch up on what you missed', detail: "Skip a few versions and What's New shows every one you missed." },
      ] } }
    : { ok: true, alreadyLatest: true, updateAvailable: false, message: 'Up to date.', current: '1.0.0-dev' },
  'app.log': mockOk,
  // The Exo account step, for screens and tests: an address with taken, locked, slow or offline in it shows that answer.
  'exoAccount.state': () => ({ signedIn: mockAccountSignedIn, email: mockAccountSignedIn ? 'you@example.com' : null, accountId: null, accountAsked: false }),
  'exoAccount.overview': () => mockAccountOverview(),
  'exoAccount.sessions': () => ({ ok: true, sessions: [
    { device: MOCK_DEVICE_HERE, created: 1794000000, lastUsed: 1794090000, current: true, name: 'DESKTOP-EXO' },
    { device: 'a1b2c3d4e5f60718293a4b5c6d7e8f90', created: 1794010000, lastUsed: 1794080000, current: false },
  ] }),
  'exoAccount.revoke': () => ({ ok: true, message: '', error: null, offline: false }),
  'exoAccount.changePassword': (params) => mockPasswordAnswer(params),
  'exoAccount.newRecoveryCode': (params) => (String(params?.password ?? '').length >= 12 ? { ok: true, message: '', error: null, offline: false, recoveryCode: 'Q4LM-7PXR-2KTB-9VHC-3WDN-8FGJ' } : { ok: false, message: "That password isn't right.", error: 'wrong-password', offline: false }),
  'exoAccount.export': () => ({ ok: true }),
  'exoAccount.delete': (params) => (String(params?.password ?? '') === 'wrong' ? { ok: false, message: "That password isn't right.", error: 'wrong-password', offline: false } : (mockAccountSignedIn = false, { ok: true, message: '', error: null, offline: false })),
  'exoAccount.signOut': () => (mockAccountSignedIn = false, { ok: true, message: '', error: null, offline: false }),
  'exoAccount.reset': () => (mockAccountSignedIn = true, { ok: true, message: '', error: null, offline: false, recoveryCode: 'Q4LM-7PXR-2KTB-9VHC-3WDN-8FGJ' }),
  'exoAccount.create': (params) => (mockAccountSignedIn = true, mockAccountAnswer(params, true)),
  'exoAccount.signIn': (params) => (mockAccountSignedIn = true, mockAccountAnswer(params, false)),
  'exoAccount.saveCode': () => ({ ok: true }),
  'exoAccount.markAsked': () => ({ ok: true }),
  'exoAccount.codeConfirmed': () => ({ ok: true }),
  'exoAccount.probe': () => ({ reachable: true, gameRunning: false }),
  'app.openLogs': () => ({ ok: false, message: 'No logs folder in the browser mock.' }),
  'report.preview': () => ({ ok: true, excerpt: [
    'Exo Launcher 1.0.0 · Windows 10.0.26300 · 2026-11-19 10:00 UTC',
    'Error (app): Report a problem',
    '',
    "Exo's log (1 entries):",
    '[2026-11-19T09:58:00.000Z] WARN: Steam: the library did not answer in <path>',
  ].join('\n') }),
  'report.send': () => ({ ok: true, issue: 123 }),
  'app.installUpdate': () => ({
    ok: true,
    alreadyLatest: true,
    updateAvailable: false,
    message: 'Already up to date.',
    current: '1.0.0-dev',
  }),
  'dlss.status': mockDlssStatus,
  'dlss.updateAll': (params) => { mockDlssUpdated.add(String(params?.id)); return { ok: true, updated: 1, failed: 0 } },
  'dlss.restore': () => ({ ok: false, message: 'Mock: no upscaler files.' }),
  'dlss.consent': (p) => p?.antiCheat ? { ok: true, message: 'Allowed for this game. Any ban is your risk.' } : { ok: false, message: 'Mock: no anti-cheat game.' },
  // An RTX 3070 (no ?gpu): an earlier DLSSG SM86 install (Cyberpunk; Marvel Rivals with anti-cheat), FSR's through
  // OptiScaler (Control). ?riot=partial: another mod replaced Exo's loader.
  'dlss.frameGen': (p) => {
    const id = String(p?.id)
    if (mockRiotPartial) return { ok: true, state: 'broken', path: 'dlss', version: 'DLSSG SM86 0.4.1', reason: 'replaced', mod: 'UE4SS' }
    if (mockGpu || !/cyberpunk|control|rivals/.test(id)) return { ok: true, state: 'hidden' }
    if (id.includes('rivals')) return { ok: true, state: 'note', path: 'dlss', antiCheat: 'NetEase anti-cheat', note: 'Not in games with anti-cheat' }
    return id.includes('control') ? { ok: true, state: 'on', path: 'fsr', version: 'OptiScaler v0.9.4' } : { ok: true, state: 'on', path: 'dlss', version: 'DLSSG SM86 0.4.1', session: 'ran' }
  },
  'shaders.status': mockShadersStatus,
  'shaders.prepare.games': () => ({ ok: true, games: MOCK_GAMES.filter(game => game.installed).slice(0, 2).map((game, at) => ({ id: game.id, title: at ? game.title : 'A Very Long Unreal Game Name: The Definitive Remastered Edition', state: mockPrepareRunning && !at ? 'running' : 'ready', message: null })) }),
  'shaders.prepare.auto': (p) => { if (typeof p?.on === 'boolean') { mockPrepareAuto = p.on; emitHostEvent('shaders.changed', {}) } return { ok: true, auto: mockPrepareAuto } },
  'shaders.prepare.start': () => { mockPrepareRunning = true; emitHostEvent('shaders.changed', {}); return { ok: true, message: null } },
  'shaders.prepare.cancel': () => { mockPrepareRunning = false; emitHostEvent('shaders.changed', {}); return { ok: true } },
  eq: (p) => mockEqState(p),
  'devices.image': () => ({ ok: false, url: null }),
  'audio.formats': (p) => {
    const id = (p as { id: string }).id
    const channels = id === MIC_ID ? 1 : 2
    const formats = [[44100, 16], [44100, 24], [48000, 16], [48000, 24], [96000, 24]].map(([rate, bits]) => ({ key: `${rate}-${bits}-${bits}-${channels}`, rate, bits, container: bits, channels }))
    const on = mockAudio[id]
    return { ok: true, current: formats.find(f => f.rate === on?.rate && f.bits === on.bits)?.key ?? null, formats }
  },
  'audio.setVolume': (p) => { mockAudio[(p as { id: string }).id].volume = (p as { volume: number }).volume; return { ok: true } },
  'audio.setMute': (p) => { mockAudio[(p as { id: string }).id].muted = (p as { muted: boolean }).muted; return { ok: true } },
  'audio.setFormat': (p) => {
    const [rate, bits] = (p as { format: string }).format.split('-').map(Number)
    Object.assign(mockAudio[(p as { id: string }).id], { rate, bits })
    return { ok: true }
  },
  'controller.read': () => ({
    ok: true, link: 'usb', edge: true,
    firmware: { version: '2.17', built: 'Sep 19 2025', latest: '2.17', current: true },
    layers: { steamPlayStation: 'on', tools: [], pollingDriver: false, memoryIntegrity: true },
    calibration: { calibrated: true, kept: false, canUndo: true, holds: true, putBack: new Date(Date.now() - 3_600_000).toISOString() },
    settings: { light: '#FFFFFF', playerLight: false, micOff: true, speakerOff: true, vibration: 'off', triggerEffects: 'off' },
    sound: false,
    remembered: true,
    slots: [{ number: 1, empty: false, name: 'Default Profile' }, { number: 2, empty: true, name: null }, { number: 3, empty: true, name: null }, { number: 4, empty: true, name: null }],
    tested: {
      at: new Date(Date.now() - 86_400_000).toISOString(),
      restLeft: { offsetX: 0.4, offsetY: -2, noise: 0 }, restRight: { offsetX: 0.4, offsetY: 2, noise: 0 },
      edgeLeft: { reach: 100, circularity: 2.2, covered: 1 }, edgeRight: { reach: 100, circularity: 2.6, covered: 1 },
    },
  }),
  'controller.measure': () => ({ ok: true, polling: { rate: 1000, fresh: 248, p99: 1.41, jitter: 0.08, repeats: 75.2, reports: 3000, batched: 0, changes: false } }),
  'controller.quality': async (p) => {
    await new Promise(resolve => setTimeout(resolve, 2_000))
    return (p as { kind?: string }).kind === 'triggers'
      ? { ok: true, left: { low: 0, high: 100, steps: 214, full: true }, right: { low: 0, high: 94, steps: 180, full: false } }
      : { ok: true, buttons: { reportMs: 1, buttons: [
        { name: 'Cross', presses: 9, bounces: 2, shortest: 4.2 }, { name: 'Circle', presses: 8, bounces: 0, shortest: null }, { name: 'L1', presses: 6, bounces: 0, shortest: null },
      ] } }
  },
  'controller.gameProfiles': () => ({ ok: true, profiles: [] }),
  'controller.watch': (p) => {
    if ((p as { on?: boolean }).on) emitHostEvent('controller.state', { id: (p as { id: string }).id, lx: 128, ly: 125, rx: 128, ry: 130, l2: 0, r2: 186, profile: 1, stopL: 0, stopR: 0, battery: 85, charging: true, extras: 0x40 })
    return { ok: true }
  },
  'audio.read': () => ({
    ok: true,
    output: mockEndpoint(OUTPUT_ID),
    mic: mockEndpoint(MIC_ID),
    voice: { custom: false, noise: 'exo', pushToTalk: false, sensitivity: null, focus: true },
  }),
  'devices.usbMap': () => ({
    ok: true,
    controllers: [
      { name: 'AMD USB 3.20 eXtensible Host Controller', where: 'cpu', interrupts: 'msi', devices: [
        { name: 'StarlightX Dongle', kind: 'mouse', speed: 2, hubs: 0, interval: 125 },
        { name: 'DualSense Edge Wireless Controller', kind: 'controller', speed: 2, hubs: 0, interval: 1000 },
        { name: 'Logitech Webcam C920', speed: 2, hubs: 0 },
      ] },
      { name: 'AMD USB 3.10 eXtensible Host Controller', where: 'chipset', interrupts: 'msi', devices: [
        { name: 'Wooting 60HE v2', kind: 'keyboard', speed: 2, hubs: 1, hub: 'Generic USB Hub', interval: 125 },
        { name: 'fifine Microphone', speed: 1, hubs: 1, hub: 'Generic USB Hub' },
      ] },
      { name: 'ASMedia USB 3.20 eXtensible Host Controller', where: 'addin', interrupts: 'line', devices: [] },
    ],
  }),
  'devices.list': () => ({
    ok: true,
    devices: [
      { id: 'usb-361d-0300-747353800033', kind: 'mouse', name: 'StarlightX Dongle', maker: 'Finalmouse', model: null, connection: 'wireless', role: null, battery: null, color: null, layout: null },
      { id: 'usb-31e3-1342-95be6909a013', kind: 'keyboard', name: 'Wooting 60HE v2', maker: 'Wooting', model: '60', connection: 'usb', role: null, battery: null, color: null, layout: 'ansi-split',
        port: { where: 'chipset', chips: 1, hubs: 0, interrupts: 'msi', better: 'free' }, app: { id: 'wootility', name: 'Wootility', where: 'web' } },
      { id: 'usb-054c-0df2-41a6f6ff466d', kind: 'controller', name: 'DualSense Edge Wireless Controller', maker: 'Sony', model: 'dualsense-edge', connection: mockPadLink, role: null, battery: null, color: 'Midnight Black', layout: null, lab: true,
        port: { where: 'cpu', chips: 0, hubs: 0, interrupts: 'msi', better: null } },
      { id: '{0.0.0.00000000}.{9709e4ab-6969-4817-ad38-1704eab1c079}', kind: 'audio', name: 'Headphones', maker: null, model: 'headphones', connection: 'jack', role: 'output', battery: null, color: null, layout: null },
      { id: '{0.0.1.00000000}.{0deb347c-90b6-43d6-b549-63e63b1b23a9}', kind: 'audio', name: 'fifine Microphone', maker: 'Fifine', model: 'microphone', connection: 'usb', role: 'input', battery: null, color: null, layout: null },
    ],
    cpuPorts: { free: 2, total: 4, freeTypeC: false },
  }),
  'eq.headphones': () => ({ ok: true, headphones: MOCK_EQ_HEADPHONES }),
  'system.tune': (p) => mockTune(p),
  'gpu.profile': (p) => mockGpuProfile(p),
  'system.about': () => ({
    name: 'Erix-PC', processor: mockIntelCpu ? 'Intel Core i7-14700K' : 'AMD Ryzen 5 5600X', cores: mockIntelCpu ? 20 : 6, threads: mockIntelCpu ? 28 : 12,
    graphics: 'NVIDIA GeForce RTX 3070', videoMemoryGb: 8, directX: '12 Ultimate', resizableBar: true,
    memoryGb: 16, memoryType: 'DDR4', memorySpeed: 3600, memoryRated: 3600, memorySlotsUsed: 2, memorySlots: 4,
    motherboard: 'ASUS ROG STRIX B550-F GAMING WIFI II', bios: '3636', biosDate: '2026-01-04',
    windows: 'Windows 11 Pro', windowsVersion: '26H2', build: '26300.9711', secureBoot: true, tpm: '2.0', memoryIntegrity: false,
    ...(mockNet === 'wifi'
      ? { network: 'Wi-Fi', networkMbps: 1201, networkAdapter: 'Intel Wi-Fi 6E AX211 160MHz' }
      : { network: 'Ethernet', networkMbps: mockNet === 'slow' ? 100 : 2500, networkAdapter: 'Intel Ethernet Controller I226-V' }),
  }),
  'system.health': (p) => {
    const cpu = mockIntelCpu ? 'Core i7-14700K' : 'Ryzen 5 5600X'
    const [label, from, to, drop] = mockIntelCpu
      ? ['Intel chipset driver', '10.1.19867.8574', '10.1.20658.8883', 'Devices may reset']
      : ['AMD chipset driver', '8.08.12.551', '8.09.28.1124', 'USB and sound may drop']
    return {
      ok: true,
      items: [
        mockApply('power', p)
          ? { key: 'power', label: 'Power plan', detail: "Exo's plan", applied: true, canUndo: true, cpu }
          : { key: 'power', label: 'Power plan', detail: 'Nexus LiteOS Power Plan', cpu },
        p?.action === 'chipset'
          ? { key: 'chipset', label, detail: `Updating to ${to}. ${drop} for a moment`, busy: true }
          : { key: 'chipset', label, detail: `${from} installed · ${to} installs while you're away` },
        ...mockNetItems(p),
      ],
      check: mockCheck,
    }
  },
  'system.tuning': (p) => mockTuning(p),
  'system.interrupts': (p) => {
    const applied = p?.action === 'apply'
    return {
      ok: true, restart: applied, result: null, canUndo: applied,
      devices: [
        { id: 'a1', name: 'NVIDIA GeForce RTX 3070', kind: 'graphics card', mode: 'line', plan: applied ? 'restart' : 'change', reason: applied ? 'Switches to MSI mode after a restart' : 'Uses a shared line. Exo will switch it to MSI mode', changed: applied },
        { id: 'a2', name: 'AMD USB 3.10 eXtensible Host Controller', kind: 'USB controller', mode: 'msi', plan: 'done', reason: 'Uses MSI mode', changed: false },
        { id: 'a3', name: 'Standard NVM Express Controller', kind: 'storage controller', mode: 'msi', plan: 'skip', reason: 'Left alone: Windows starts from the drive behind it', changed: false },
      ],
    }
  },
  'autopilot.state': mockAutopilot,
  'autopilot.set': (p) => {
    mockAutopilotChoices = { ...mockAutopilotChoices, ...(p as Partial<typeof mockAutopilotChoices>) }
    return { ok: true, message: null, state: mockAutopilot() }
  },
  'system.mouseCore': mockMouseCore,
  'system.drives': mockDrives,
  'system.drivers': () => ({ ok: true, busy: null, running: false, restart: false, items: [
    { id: 'windows:740c4b6c-bcfa-4f97-ae49-7bcc6933e696', kind: 'windows', name: 'Voice Clarity', maker: 'Microsoft Corporation', version: '10.0.26100.1', date: '2006-06-20', latest: '1.0.4.7057', latestDate: '2026-06-16', how: 'install', page: null, source: 'Windows Update', app: false, skipped: null, advised: null },
    { id: 'bios:board', kind: 'bios', name: 'BIOS', maker: 'ASUS ROG STRIX B550-F GAMING WIFI II', version: '3636', date: '2026-01-04', latest: '3645', latestDate: '2026-09-14', how: 'page', page: 'https://www.asus.com/supportonly/ROG%20STRIX%20B550-F%20GAMING%20WIFI%20II/helpdesk_bios/', source: 'ASUS', app: false, skipped: null, advised: { version: '3636', why: 'fTPM security flaws', action: 'update', to: '3644' } },
    { id: 'network:PCI\\VEN_8086&DEV_125C', kind: 'network', name: 'Intel(R) Ethernet Controller I226-V', maker: 'Intel', version: '2.1.3.10', date: '2023-06-01', latest: '2.1.3.12', latestDate: '2023-09-01', how: 'none', page: null, source: 'Intel', app: false, skipped: { version: '2.1.3.12', why: 'the link drops at random' }, advised: { version: '2.1.3.10', why: 'the link drops at random', action: 'update', to: '2.1.3.15' } },
    { id: 'windows:11111111-2222-3333-4444-555555555555', kind: 'windows', name: 'Realtek Audio Universal Service', maker: 'Realtek', version: null, date: null, latest: '1.0.0.55', latestDate: '2026-05-02', how: 'install', page: null, source: 'Windows Update', app: true, skipped: null, advised: null },
  ] }),
  'junk.scan': () => ({ ok: true, items: [
    { id: 'temp', name: 'Temporary files', detail: '', bytes: 0.8e9, files: 412, auto: true },
    { id: 'shaders', name: 'Old shader caches', detail: '', bytes: 36.6e9, files: 57, auto: false },
    { id: 'exo', name: "Exo's old backups", detail: '', bytes: 0.35e9, files: 1, auto: true },
    { id: 'bin', name: 'Recycle Bin', detail: '', bytes: 1.2e9, files: 38, auto: false },
  ], cleaned: { at: new Date(Date.now() - 5 * 3_600_000).toISOString(), bytes: 1.9e9 } }),
  'junk.clean': () => ({ ok: true, bytes: 37.7e9, files: 470 }),
  'compress.state': (p) => ({ ok: true, compress: mockCompress(String(p?.id ?? '')) }),
  'compress.list': () => {
    const games = [
      { id: 'steam:1091500', title: 'Cyberpunk 2077', compress: mockCompress('steam:1091500') },
      { id: 'steam:1245620', title: 'ELDEN RING', compress: { ...mockCompress(''), state: 'compressed' as const, saved: 18.3e9, size: 49e9, estimate: null } },
      { id: 'steam:578080', title: 'PUBG: BATTLEGROUNDS', compress: { ...mockCompress(''), state: 'needsConsent' as const, estimate: 9.1e9, antiCheat: 'BattlEye', message: 'This game uses BattlEye, so Exo compresses it only if you allow it for this game.' } },
    ].map(game => ({ ...game, eligible: game.compress.state === 'none' }))
    return { ok: true, games, saved: 18.3e9, potential: 21.4e9, settings: { ...mockCompressSettings } }
  },
  'compress.act': (p) => ({ ok: true, message: null, compress: { ...mockCompress(String(p?.id ?? '')), state: p?.action === 'compress' ? 'compressing' : 'none', percent: p?.action === 'compress' ? 0.42 : null } }),
  'compress.all': () => ({ ok: true, queued: 1, message: null }),
  'compress.settings': (p) => {
    if (typeof p?.whileIdle === 'boolean') mockCompressSettings.whileIdle = p.whileIdle
    if (typeof p?.afterUpdates === 'boolean') mockCompressSettings.afterUpdates = p.afterUpdates
    if (p?.speed === 'gentle' || p?.speed === 'balanced' || p?.speed === 'fast') mockCompressSettings.speed = p.speed
    return { ok: true, settings: { ...mockCompressSettings } }
  },
  'startup.list': () => mockStartup(null),
  'achievements.preview': () => ({ ok: true }),
  'fps.counter': (p) => { const on = p?.on === true; return { on, state: on ? 'on' : 'off', message: null } },
  'bios.update': (p) => ({ ok: true, message: null, staged: p?.action === 'prepare' ? { version: '3645', file: 'R550FGW2.CAP', bitlocker: false } : null }),
  'startup.remove': () => mockStartup(null),
  'startup.restore': () => mockStartup(null),
  'startup.clean': () => ({ ...mockStartup(null), cleaned: 4 }),
  'hdr.state': () => mockNoHdr ? { supported: false } : ({ supported: true, on: false, name: 'Mock OLED', peakNits: 1000, calibrated: false, verdict: 'great' }),
  'hdr.game': (p) => ({ ok: true, hdr: 'true', mode: typeof p?.mode === 'string' ? p.mode : 'auto' }),
  'game.settings': mockGameSettings,
  // As on the RX 9070 XT of ?gpu=amd.
  'gpu.afmf3': (p) => {
    const offer = mockGpu === 'amd'
    if (typeof p?.on === 'boolean') mockAfmf3 = p.on
    return { ok: true, offer, on: offer && mockAfmf3 }
  },
  'gpu.display': (p) => ({
    ok: true,
    pending: p?.action === 'set' ? { display: 'DISPLAY1', seconds: 15 } : null,
    displays: [{
      id: 'DISPLAY1', name: 'VG248', main: true, hdr: false, hdrSupported: !mockNoHdr, vendor: mockGpu === 'amd' || mockGpu === 'intel' ? mockGpu : 'nvidia', refresh: 144, refreshLabel: '144 Hz', refreshes: [60, 100, 120, 144],
      color: { format: 'rgb', bpc: 8, full: true }, auto: false,
      supported: ['rgb', '444', '422'].flatMap(format => [6, 8].flatMap(bpc =>
        (format === 'rgb' ? [true, false] : [false]).map(full => ({ format, bpc, full })))),
      best: { refresh: 144, color: { format: 'rgb', bpc: 8, full: true }, why: null, scaling: true, over: true },
      isBest: false,
      scaling: { mode: 'aspect', gpu: false, over: false },
      ddc: { brightness: 75, brightnessMin: 0, brightnessMax: 100, contrast: 50, contrastMin: 0, contrastMax: 100 },
      adjust: {
        nvidia: p?.action === 'adjust' && p.nvidia === true,
        controls: [
          { key: 'brightness', label: 'Brightness', value: 50, min: 0, max: 100, step: 1, normal: 50, unit: '%' },
          { key: 'contrast', label: 'Contrast', value: 50, min: 0, max: 100, step: 1, normal: 50, unit: '%' },
          { key: 'gamma', label: 'Gamma', value: 100, min: 30, max: 280, step: 1, normal: 100, unit: '' },
          { key: 'vibrance', label: 'Digital vibrance', value: 50, min: 0, max: 100, step: 1, normal: 50, unit: '%' },
          { key: 'hue', label: 'Hue', value: 0, min: 0, max: 359, step: 1, normal: 0, unit: '°' },
        ],
      },
    }],
  }),
  'gpu.import': () => ({ ok: false, cancelled: true, undo: null }),
  'gpu.controlPanel': () => ({ ok: true }),
  'gpu.driver': () => mockGpu === 'amd'
    ? { ok: true, vendor: 'amd', card: 'Radeon RX 9070 XT', installed: '26.1.1', update: false, latest: null }
    : mockGpu === 'intel'
      ? { ok: true, vendor: 'intel', card: 'Arc B580', installed: '32.0.101.6559', update: false, latest: null }
      : { ok: true, vendor: 'nvidia', card: 'RTX 3070', installed: '581.42', update: true, latest: { version: '581.57', size: 900_000_000, released: '2026-10-14', kind: 'game' } },
  'gpu.game': (p) => {
    if (mockGpu === 'intel' && typeof p?.frameGen === 'string') mockFrameGen = p.frameGen
    return mockGpu === 'intel' ? { ok: true, vendor: 'intel', supported: true, frameGen: mockFrameGen }
      // ?gpu=amd: an RX 6600 as Exo decides (Helpers/FluidMotionFrames.cs): off for competitive games and Cyberpunk's own frame generation.
      : { ok: true, vendor: 'amd', supported: mockGpu === 'amd', afmf: !/valorant|rivals|fortnite|cyberpunk/.test(String(p?.id)), amdSoftware: true }
  },
  'store.home': () => ({
    ok: true, at: new Date().toISOString(), hero: [{ ...MOCK_STORE_TILE, wished: true }],
    shelves: [{ id: 'mock', title: 'Top sellers', items: [
      { ...MOCK_STORE_TILE, review: 'Very Positive', reviewPercent: 88 },
      { appId: '1245620', name: 'ELDEN RING', final: 5999, initial: 5999, discount: 0, currency: 'USD', priceText: '$59.99', review: 'Very Positive', reviewPercent: 93 },
      { ...MOCK_OWNED_TILE, review: 'Overwhelmingly Positive', reviewPercent: 98 },
      { appId: '730', name: 'Counter-Strike 2', free: true, discount: 0, review: 'Very Positive', reviewPercent: 86 },
      { appId: '2358720', name: 'Black Myth: Wukong', soon: true, when: 'Dec 12', discount: 0 },
    ] }],
  }),
  'store.calendar': () => ({
    ok: true, at: new Date().toISOString(), interests: ['Roguelike', 'Co-op'],
    days: [{ date: new Date().toISOString().slice(0, 10), entries: [
      { tile: MOCK_STORE_TILE, wished: true, earlyAccess: false, why: ['On your wishlist', 'Like Roguelike and Co-op'] },
      { tile: MOCK_OWNED_TILE, wished: false, earlyAccess: false, why: [] },
    ] }],
  }),
  'store.search': () => ({ ok: true, items: [MOCK_STORE_TILE, MOCK_OWNED_TILE] }),
  'store.open': () => ({ ok: true }),
  'store.gameOffers': () => ({ ok: true, offers: [
    { store: '25', name: 'Epic Games Store', final: 2999, initial: 5999, discount: 50, currency: 'USD', url: 'https://store.epicgames.com/' },
    { store: '7', name: 'GOG', final: 2999, initial: 5999, discount: 50, currency: 'USD', url: 'https://www.gog.com/' },
  ], lowest: { final: 1979, at: '2026-06-25T17:00:00Z' } }),
  'store.gameReviews': () => ({ ok: true, word: 'Mostly Positive', percent: 78, count: 1025 }),
  'store.game': mockStoreGame,
  'store.wishlist': () => ({ ok: true, items: [] }),
  'keys.preview': () => ({ ok: true, looksLikeKey: false, store: 'unknown', key: '' }),
  'keys.activate': () => ({ ok: true, message: 'Mock key.' }),
  'keep.status': () => ({ pc: 'none', games: {} }),
}

// ?hold=store.,discord.chat holds every call whose method starts so, for looking at a wait.
const mockHold = typeof location === 'undefined' ? [] : new URLSearchParams(location.search).get('hold')?.split(',') ?? []

// ?fail=game.uninstall,stores. fails every call whose method starts so, as the host would (a file in use, by turns
// with no network and a refusal), for looking at an action's error; ?slow=apps. answers those after 1.6 s, for its ring.
const mockFail = typeof location === 'undefined' ? [] : new URLSearchParams(location.search).get('fail')?.split(',').filter(Boolean) ?? []
const mockSlow = typeof location === 'undefined' ? [] : new URLSearchParams(location.search).get('slow')?.split(',').filter(Boolean) ?? []
let mockFailures = 0
const MOCK_FAILURES = [
  'A file is in use. Close the game or app using it, then try again.',
  'System.Net.Http.HttpRequestException: No such host is known. (api.example:443)',
  { ok: false, message: 'Something went wrong.' },
]

export async function mockCall<T>(method: string, params?: Record<string, unknown>): Promise<T> {
  if (mockHold.some(held => method.startsWith(held))) await new Promise(() => {})
  await new Promise((r) => setTimeout(r, mockSlow.some(slow => method.startsWith(slow)) ? 1600 : 40))
  if (mockFail.some(fail => method.startsWith(fail))) {
    const failure = MOCK_FAILURES[mockFailures++ % MOCK_FAILURES.length]
    if (typeof failure === 'string') throw new Error(failure)
    return failure as T
  }
  const handler = MOCK_HANDLERS[method]

  if (!handler) throw new Error(`Unknown mock method: ${method}`)
  return handler(params) as T
}
