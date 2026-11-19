// Starts the shipped ui/ under Vite (unchanged) with a dev-only plugin that, in the dev mock host only, adds review data:
//   ?stress   560 games (long titles, CJK, emoji, no art, updates), ?empty  no games, ?fail  every host call but settings fails,
//   ?slow=ms  every host call waits.  Nothing under ui/src is edited; the plugin rewrites the module as it is served.
import { pathToFileURL } from 'node:url'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
const here = dirname(fileURLToPath(import.meta.url))
const ui = resolve(here, '../../ui')
const { createServer } = await import(pathToFileURL(resolve(ui, 'node_modules/vite/dist/node/index.js')).href)

const STRESS = `
;(() => {
  const q = new URLSearchParams(location.search); window.__stress = [q.toString(), MOCK_GAMES.length]
  if (q.has('empty')) MOCK_GAMES.splice(1)
  if (q.has('stress')) {
    const long = ['The Legend of Zelda: Breath of the Wild — Master Edition Deluxe Collector\u2019s Bundle With Every DLC', 'ファイナルファンタジーⅠ リミックス エクストリーム版', 'اللعبة العربية الطويلة جدا', 'Балдурс Гейт 3', '🔥 Hot Wheels Unleashed™ 2 🏎️ Turbocharged', 'A', 'WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW', 'Tom Clancy\u2019s Rainbow Six® Siege X: Deluxe Operator Edition']
    const stores = ['steam', 'epic', 'gog', 'riot', 'local']
    for (let i = 0; i < 560; i++) {
      const title = i % 9 === 0 ? long[(i / 9 | 0) % long.length] : 'Stress Game ' + String(i).padStart(3, '0') + (i % 5 === 0 ? ' with a rather long subtitle that keeps going' : '')
      MOCK_GAMES.push({ id: 'stress:' + i, title, store: stores[i % 5], coverUrl: i % 7 === 3 ? undefined : 'https://covers.exo-launcher.local/stress/' + i + '_2x.jpg', installed: i % 3 === 0, owned: true, canInstall: i % 3 !== 0, primaryAction: i % 3 === 0 ? 'play' : 'install', status: i % 11 === 0 ? 'Update' : 'Ready', playtimeMinutes: i % 4 === 0 ? i * 37 : 0, sizeBytes: (i % 40 + 1) * 1024 ** 3, deps: [], launchNote: '', lastPlayedUtc: i % 6 === 0 ? new Date(Date.now() - i * 3600e3).toISOString() : undefined })
    }
  }
})()
`
const WRAP = `
const __qs = typeof location === 'undefined' ? new URLSearchParams('') : new URLSearchParams(location.search)
export async function mockCall(m, p) {
  if (m === 'exoAccount.overview' && !__qs.has('race')) await new Promise((r) => setTimeout(r, 150))
  if (__qs.has('slow')) await new Promise((r) => setTimeout(r, Number(__qs.get('slow')) || 1500))
  if (__qs.has('fail') && !/^(settings|window|app|exo\.theme)/.test(m) && !(__qs.get('fail') && !m.startsWith(__qs.get('fail')))) throw new Error('Host refused: ' + m)
  // ?live[=service]: a song playing, a Discord call connected (three people), so the Bar's mini players can be seen.
  if (__qs.has('live')) {
    if (m === 'music.playback') return { ok: true, playback: { seq: 1, provider: __qs.get('live') || 'spotify', status: 'playing', title: __qs.get('song') || 'Midnight City', artist: 'M83', artworkDataUrl: null, positionMs: 61000, durationMs: 243000, positionAgeMs: 0, rate: 1, canPlay: true, canPause: true, canPrev: true, canNext: true, canSeek: true } }
    if (m === 'music.volume') return { ok: true, volume: 0.6, level: 0.6, muted: false }
    if (m === 'discord.voice' && (!p || !p.action || p.action === 'state')) return { ok: true, voice: { state: 'connected', guildId: 'live', channelId: 'live-voice', muted: false, deafened: false, e2ee: true } }
    if (m === 'discord.channels' && p && p.guildId === 'live') return { name: 'Night Raid', icon: null, channels: [{ id: 'live-voice', name: 'Lounge', members: [{ id: '1', name: 'Ada', avatar: null }, { id: '2', name: 'Rook', avatar: null }, { id: '3', name: 'Mira', avatar: null }] }], hideMuted: false }
  }
  const out = await __mockCallInner(m, p)
  // ?hero=N: the Store home's banner list holds N picks (the mock has one), copies of the home's games under distinct ids.
  if (m === 'store.home' && __qs.has('hero') && out && out.hero) {
    const pool = [out.hero[0], ...out.shelves.flatMap((x) => x.items)]
    return { ...out, hero: Array.from({ length: Number(__qs.get('hero')) || 1 }, (_, i) => { const t = pool[i % pool.length]; return i >= pool.length ? { ...t, appId: t.appId + '-' + i, name: t.name + ' ' + (i + 1) } : t }) }
  }
  return out
}
`
const plugin = {
  name: 'ui-pass-stress', enforce: 'post',
  transform(code, id) {
    if (!id.split('?')[0].endsWith('/src/lib/hostMock.ts')) return null
    if (!code.includes('export async function mockCall')) throw new Error('hostMock.ts changed shape; update serve.mjs')
    return { code: code.replace('export async function mockCall', 'async function __mockCallInner') + STRESS + WRAP, map: null }
  },
}
// EXO_UIPASS_CACHE_DIR keeps Vite's prebundle cache out of ui/node_modules (a worktree's node_modules is a shared link).
const server = await createServer({ root: ui, configFile: resolve(ui, 'vite.config.ts'), ...(process.env.EXO_UIPASS_CACHE_DIR ? { cacheDir: process.env.EXO_UIPASS_CACHE_DIR } : {}), server: { port: Number(process.env.EXO_UIPASS_PORT ?? 5291), strictPort: true, host: '127.0.0.1' }, plugins: [plugin], logLevel: 'warn' })
await server.listen()
console.log('ui-pass server on', server.resolvedUrls?.local?.[0])
