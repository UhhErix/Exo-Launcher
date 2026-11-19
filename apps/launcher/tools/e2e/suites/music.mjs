// Music, used: its home, search, opening a playlist and coming back, the player's parts. Nothing is played (no sound
// leaves the PC).
import { sleep } from '../cdp.mjs'

export async function run(t) {
  const { run, k } = t
  await k.nav('Music')
  const ready = await t.shell.waitFor(() => document.querySelectorAll('.exo-music-card, .exo-music-row').length > 0 ? 'ok' : document.querySelector('.exo-music-signin-note, .exo-music-error') ? 'off' : null, { timeoutMs: 25000 })

  await run.check('music', 'whole', 'the room shows whole: its stand-in is gone once your library and Home are in, and nothing under it was reachable', async (c) => {
    if (ready !== 'ok') c.skip(ready === 'off' ? 'no music service signed in' : 'Music did not load')
    const gone = await t.shell.waitFor(() => !document.querySelector('.exo-music-cover.is-over') || null, { timeoutMs: 12000 })
    const state = await t.shell.eval(`({ cover: !!document.querySelector('.exo-music-cover.is-over'), inert: document.querySelector('main.exo-music')?.inert ?? null, library: document.querySelectorAll('.exo-music-library-item').length, shelves: document.querySelectorAll('.exo-music-shelf, .exo-music-recent').length })`)
    c.evidence.state = state
    return [!gone && 'the stand-in stayed over the room', state.inert && 'the room stayed out of reach', !state.library && 'no library under it', !state.shelves && 'no Home under it'].filter(Boolean)
  })

  await run.check('music', 'home', 'Music opens on its home with shelves and your library', async (c) => {
    if (ready !== 'ok') c.skip(ready === 'off' ? 'no music service signed in' : 'Music did not load')
    const parts = await t.shell.eval(`({ sections: document.querySelectorAll('.exo-music-section').length, cards: document.querySelectorAll('.exo-music-card').length, library: document.querySelectorAll('.exo-music-side .exo-music-row, .exo-music-side [class*="library"] button').length })`)
    c.evidence.parts = parts
    return [parts.sections < 2 && `only ${parts.sections} shelves`, !parts.cards && 'no cards'].filter(Boolean)
  })
  if (ready !== 'ok') return

  await run.check('music', 'search', 'searching finds songs, artists or playlists', async (c) => {
    const field = '.exo-music-search input[aria-label^="Search"]'
    if (!await k.fill(field, 'lofi')) c.skip('no search field')
    await t.shell.key('Enter')
    const results = await t.shell.waitFor(() => document.querySelectorAll('.exo-music-row, .exo-music-card').length > 3 && document.body.innerText.includes('lofi') ? document.querySelectorAll('.exo-music-row, .exo-music-card').length : null, { timeoutMs: 12000 })
    c.metric('results', results ?? 0)
    await c.shot('search', t.shell)
    await k.fill(field, '')
    await t.shell.click(() => [...document.querySelectorAll('.exo-music-side button, nav button')].find(b => b.textContent.trim() === 'Home'))
    await sleep(800)
    return results ? [] : ['no results for "lofi"']
  })

  await run.check('music', 'playlist', 'a playlist opens with its songs, and Back returns home', async (c) => {
    const t0 = Date.now()
    // One of your library's playlists: Home's first card can be a song, and a song's card plays it (it did, 3.18.0).
    const opened = await t.shell.click(() => document.querySelector('.exo-music-library-row[data-kind="playlist"]'))
    if (!opened) c.skip('no playlist in your library')
    // Its songs, on the page (the library down the left is rows too).
    const rows = await t.shell.waitFor(() => { const n = document.querySelectorAll('.exo-music-main .exo-music-tracks > li').length; return n > 2 && document.querySelector('.exo-music-main .exo-music-header-text') ? n : null }, { timeoutMs: 12000 })
    c.metric('open', Date.now() - t0, ' ms', 1500)
    c.metric('songs', rows ?? 0)
    if (!rows) return ['the playlist showed no songs']
    await sleep(500)
    const failures = await k.look(c, 'playlist')
    await c.shot('playlist', t.shell)
    if (!await t.shell.click(() => document.querySelector('.exo-music-back') ?? [...document.querySelectorAll('.exo-music-side button')].find(b => b.textContent.trim() === 'Home'))) failures.push('no way back')
    await sleep(900)
    return failures
  })

  await run.check('music', 'player', 'the player shows the song and its controls', async (c) => {
    const parts = await t.shell.eval(`({ bar: !!document.querySelector('.exo-music-bar'), song: document.querySelector('.exo-music-bar-song')?.textContent.trim().slice(0, 40) ?? null, play: !!document.querySelector('.exo-music-bar-play') })`)
    c.evidence.parts = parts
    if (!parts.bar) c.skip('no player (nothing queued)')
    return [!parts.play && 'no play button'].filter(Boolean)
  })

  await run.check('music', 'lyrics', "Now playing shows the song's lyrics, or says it has none (YouTube Music's synced when LRCLIB knows the song)", async (c) => {
    if (!await t.shell.click(() => document.querySelector('.exo-music-toggle[aria-label="Now playing"]:not(:disabled)'))) c.skip('no player (nothing queued)')
    const state = await t.shell.waitFor(() => {
      const panel = document.querySelector('.exo-music-now')
      if (!panel) return null
      // No song loaded: the panel names none and has no lyrics to show (a run plays nothing).
      if (panel.querySelector('h2')?.textContent.trim() === 'Nothing playing') return { nothing: true }
      if (panel.querySelector('.exo-music-error')) return { error: panel.querySelector('.exo-music-error').textContent.trim().slice(0, 120) }
      const lines = panel.querySelectorAll('.exo-music-lyrics li:not(.exo-music-lyric-wait)').length
      if (lines) return { lines, synced: !!panel.querySelector('.exo-music-lyrics.is-synced') }
      return panel.querySelector('.exo-music-empty') ? { lines: 0 } : null
    }, { timeoutMs: 12000 })
    c.evidence.state = state
    if (!state?.nothing) await c.shot('lyrics', t.shell)
    await t.shell.click(() => document.querySelector('.exo-music-toggle[aria-label="Now playing"].is-on'))
    await sleep(500)
    if (state?.nothing) c.skip('nothing playing: no song to show lyrics for')
    return [!state && 'lyrics kept loading past 12 s', state?.error && `lyrics failed: ${state.error}`].filter(Boolean)
  })
}
