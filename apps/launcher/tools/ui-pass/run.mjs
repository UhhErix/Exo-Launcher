// UI pass: renders every room of the shipped ui/ against the dev mock host and writes shots + measurements.
//   node tools/ui-pass/serve.mjs            (keep running; port 5291)
//   node tools/ui-pass/run.mjs [--groups shell,library,...] [--variants void,solid,forced]
// Shots: misc/ui-pass-3.25/shots; measurements: tools/ui-pass/out/results-<group>.json
import { launch, newPage, open, room, snap, sleep, flush, results, VARIANTS, drain } from './harness.mjs'
import * as A from './audit.mjs'

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > -1 ? process.argv[i + 1] : d }
const GROUPS = (arg('groups', 'shell,library,store,discord,music,pc,devices,captures,settings,onboarding,sizes,keyboard,hover,text,motion,errors')).split(',')
const VARS = (arg('variants', 'void,solid,forced')).split(',')
const failures = []
const step = async (name, fn) => { try { await fn() } catch (e) { failures.push({ step: name, error: String(e.message).slice(0, 200) }); console.log('  FAIL', name, '-', String(e.message).slice(0, 120)) } }
const esc = (p) => p.key('Escape').then(() => sleep(350))
const AUDIT = { audit: true }

const tile = (p, n = 0) => p.clickSel('.exo-tile-hit', n).then(() => sleep(900))

const groups = {
  async shell(p, v) {
    await step('accounts-sheet', async () => { await open(p, '', 'Search', false); await sleep(700); await snap(p, 'shell', 'accounts-sheet', AUDIT); await p.key('Escape'); await sleep(500) })
    await step('default', async () => { await open(p, ''); await snap(p, 'shell', 'bar-library', AUDIT) })
    await step('banner', async () => { await open(p, '', 'Search', false); await p.clickText('Not now').catch(() => {}); await sleep(900); await snap(p, 'shell', 'account-banner', AUDIT) })
    await step('capture-menu', async () => { await open(p, ''); await p.clickSel('.exo-capture-btn'); await sleep(500); await snap(p, 'shell', 'capture-menu', AUDIT); await esc(p) })
    await step('palette', async () => { await open(p, ''); await p.key('k', { ctrl: true }); await sleep(600); await snap(p, 'palette', 'open', AUDIT); await p.eval(`document.activeElement && document.execCommand && 0`); for (const ch of 'hades') await p.key(ch); await sleep(500); await snap(p, 'palette', 'query-hades', AUDIT); for (const ch of 'zzzxq') await p.key(ch); await sleep(500); await snap(p, 'palette', 'query-nothing', AUDIT); await esc(p) })
    await step('palette-settings', async () => { await open(p, ''); await p.key('k', { ctrl: true }); await sleep(500); for (const ch of 'motion') await p.key(ch); await sleep(500); await snap(p, 'palette', 'query-setting') ; await esc(p) })
    await step('whatsnew', async () => { await open(p, 'whatsnew'); await sleep(1200); await snap(p, 'whatsnew', 'sheet', AUDIT) })
    await step('update', async () => { await open(p, 'update'); await sleep(1500); await snap(p, 'shell', 'update-available', AUDIT) })
  },
  async library(p, v) {
    await step('default', async () => { await open(p, ''); await snap(p, 'library', 'default', AUDIT) })
    await step('sort-menu', async () => { await p.clickSel('.exo-shelf-sort-trigger'); await sleep(500); await snap(p, 'library', 'sort-menu', AUDIT); await esc(p) })
    await step('more-menu', async () => { await p.clickSel('.exo-library-more'); await sleep(500); await snap(p, 'library', 'more-menu', AUDIT); await esc(p) })
    await step('game-sheet-uninstalled', async () => { await tile(p, 1); await snap(p, 'library', 'game-sheet-uninstalled', AUDIT); await esc(p) })
    await step('game-sheet-installed', async () => { await p.clickSel('.exo-continue-open', 1); await sleep(1000); await snap(p, 'library', 'game-sheet-installed', { ...AUDIT, anim: true }); })
    await step('game-properties', async () => { await p.clickText('Properties'); await sleep(900); await snap(p, 'library', 'game-properties', AUDIT) })
    await step('game-sheet-more', async () => { await esc(p); await esc(p); await sleep(300) })
    await step('context-menu', async () => { const r = await p.fn(() => { const b = document.querySelectorAll('.exo-tile-hit')[0].getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 } }); await p.rclick(r.x, r.y); await sleep(500); await snap(p, 'library', 'tile-context-menu', AUDIT); await esc(p) })
    await step('folders', async () => { await open(p, 'folders'); await snap(p, 'library', 'folders', AUDIT); await p.clickText('Favorites', '.exo-folder-tile button, .exo-tile-hit'); await sleep(900); await snap(p, 'library', 'folder-open', AUDIT); await esc(p) })
    await step('stress', async () => {
      await open(p, 'stress'); await snap(p, 'library', 'stress-560', AUDIT)
      await p.eval(`(() => { const s = [...document.querySelectorAll('*')].filter((e) => /(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 200 && e.checkVisibility()).sort((a, b) => b.scrollHeight - a.scrollHeight)[0]; if (s) { s.scrollTop = 3000; window.__sc = s.className } return !!s })()`)
      await sleep(900); await snap(p, 'library', 'stress-scrolled', AUDIT)
      await p.eval(`(() => { const s = [...document.querySelectorAll('*')].filter((e) => e.className === window.__sc)[0]; if (s) s.scrollTop = 1e6 })()`); await sleep(900); await snap(p, 'library', 'stress-bottom')
    })
    await step('empty', async () => { await open(p, 'empty'); await snap(p, 'library', 'empty', AUDIT) })
  },
  async store(p, v) {
    await step('default', async () => { await open(p, ''); await room(p, 'Store'); await sleep(1200); await snap(p, 'store', 'discover', AUDIT) })
    await step('calendar', async () => { await p.clickText('Calendar'); await sleep(1000); await snap(p, 'store', 'calendar', AUDIT) })
    await step('wishlist', async () => { await p.clickText('Wishlist'); await sleep(1000); await snap(p, 'store', 'wishlist', AUDIT) })
    await step('search', async () => { await p.clickText('Discover'); await sleep(700); await p.clickSel('input'); for (const ch of 'cyber') await p.key(ch); await sleep(1200); await snap(p, 'store', 'search-cyber', AUDIT) })
    await step('search-none', async () => { await p.clickSel('[aria-label="Clear search"]').catch(() => {}); await p.clickSel('input'); for (const ch of 'qzxjv') await p.key(ch); await sleep(1200); await snap(p, 'store', 'search-no-results', AUDIT) })
    await step('page', async () => { await open(p, ''); await room(p, 'Store'); await sleep(900); await p.clickSel('[aria-label^="Open Cyberpunk"]'); await sleep(1600); await snap(p, 'store', 'game-page', AUDIT) })
  },
  async discord(p, v) {
    await step('home', async () => { await open(p, ''); await room(p, 'Discord'); await sleep(900); await snap(p, 'discord', 'home-friends', AUDIT) })
    await step('dm', async () => { await p.clickText('Ada', '.exo-dc-side button, .exo-dc button'); await sleep(1500); await snap(p, 'discord', 'dm-conversation', AUDIT) })
    await step('server', async () => { await p.clickSel('[aria-label^="Night Raid"]'); await sleep(1500); await snap(p, 'discord', 'server-channel', AUDIT) })
    await step('settings', async () => { await p.clickSel('[aria-label="User settings"], [aria-label="Discord settings"]').catch(async () => { await p.clickText('Your status') }); await sleep(900); await snap(p, 'discord', 'user-menu-or-settings', AUDIT); await esc(p) })
    for (const dc of ['long', 'empty', 'signedout', 'connecting', 'few']) await step('dc-' + dc, async () => { await open(p, 'dc=' + dc); await room(p, 'Discord'); await sleep(1200); await snap(p, 'discord', 'scenario-' + dc, AUDIT) })
  },
  async music(p, v) {
    await step('home', async () => { await open(p, ''); await room(p, 'Music'); await sleep(900); await snap(p, 'music', 'home', AUDIT) })
    await step('songs', async () => { await p.clickText('Songs'); await sleep(1000); await snap(p, 'music', 'songs', AUDIT) })
    await step('search', async () => { await p.clickSel('.exo-music-side button[aria-label*="earch"], .exo-music-side input'); await sleep(500); for (const ch of 'mock') await p.key(ch); await sleep(1000); await snap(p, 'music', 'search', AUDIT) })
    await step('playlist', async () => { await p.clickText('Playlist 1', 'button'); await sleep(1200); await snap(p, 'music', 'playlist', AUDIT) })
    await step('player-more', async () => { await p.clickSel('[aria-label="More"]').catch(() => {}); await sleep(500); await snap(p, 'music', 'player-menu', AUDIT); await esc(p) })
    await step('queue', async () => { await p.clickSel('[aria-label*="ueue"]').catch(() => {}); await sleep(800); await snap(p, 'music', 'queue', AUDIT); await esc(p) })
  },
  async pc(p, v) {
    await step('default', async () => { await open(p, ''); await room(p, 'PC'); await sleep(1000); await snap(p, 'pc', 'overview', AUDIT) })
    await step('scroll', async () => { await p.eval(`(() => { const s = [...document.querySelectorAll('*')].filter((e) => /(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 100 && e.checkVisibility()).sort((a, b) => b.scrollHeight - a.scrollHeight); s.forEach((e) => e.scrollTop = 700); return s.length })()`); await sleep(700); await snap(p, 'pc', 'scrolled-1', AUDIT) })
    await step('scroll2', async () => { await p.eval(`[...document.querySelectorAll('*')].filter((e) => /(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 100 && e.checkVisibility()).forEach((e) => e.scrollTop = 1e6)`); await sleep(700); await snap(p, 'pc', 'scrolled-end', AUDIT) })
    await step('apps', async () => { await open(p, ''); await room(p, 'PC'); await p.clickText('Apps', 'button'); await sleep(1000); await snap(p, 'pc', 'apps-sheet', AUDIT); await esc(p) })
    await step('upscalers', async () => { await p.clickText('Upscalers'); await sleep(1000); await snap(p, 'pc', 'upscalers', AUDIT); await esc(p) })
    await step('things', async () => { await p.clickText('things left to set'); await sleep(1000); await snap(p, 'pc', 'things-left-menu', AUDIT); await esc(p) })
    await step('tune', async () => { await p.clickText('Auto tune'); await sleep(1000); await snap(p, 'pc', 'auto-tune', AUDIT); await esc(p) })
    await step('gpu', async () => { await p.clickText('Graphics'); await sleep(1000); await snap(p, 'pc', 'graphics', AUDIT); await esc(p) })
    await step('screen', async () => { await p.clickText('Screen'); await sleep(1000); await snap(p, 'pc', 'screen', AUDIT); await esc(p) })
    for (const q of ['gpu=amd&cpu=intel', 'drives=fine', 'net=slow', 'shaders=30', 'tune=ryzen', 'riot=partial']) await step('pc-' + q, async () => { await open(p, q); await room(p, 'PC'); await sleep(1200); await snap(p, 'pc', 'flag-' + q.replace(/[=&]/g, '-'), AUDIT) })
  },
  async devices(p, v) {
    await step('default', async () => { await open(p, ''); await room(p, 'Devices'); await sleep(900); await snap(p, 'devices', 'overview', AUDIT) })
    await step('usbmap', async () => { await p.clickText('USB map'); await sleep(1000); await snap(p, 'devices', 'usb-map', AUDIT); await esc(p) })
    await step('device', async () => { await p.clickSel('[aria-label*="Starlight"], .exo-devices-tile, article button', 0); await sleep(1500); await snap(p, 'devices', 'mouse-page', AUDIT) })
  },
  async captures(p, v) {
    await step('captures', async () => { await open(p, 'clips=24'); await p.clickSel('.exo-capture-btn'); await sleep(500); await p.clickText('Captures', '[role=menuitem], button'); await sleep(1500); await snap(p, 'captures', 'sheet', AUDIT) })
    await step('captures-few', async () => { await open(p, 'clips=0'); await p.clickSel('.exo-capture-btn'); await sleep(500); await p.clickText('Captures', '[role=menuitem], button'); await sleep(1500); await snap(p, 'captures', 'empty', AUDIT) })
    await step('notes', async () => { await open(p, ''); await p.clickSel('.exo-capture-btn'); await sleep(500); await p.clickText('New note', '[role=menuitem], button'); await sleep(1500); await snap(p, 'notes', 'new-note', AUDIT) })
  },
  async settings(p, v) {
    const pages = ['Account', 'General', 'Library', 'Downloads', 'In game', 'Discord', 'Music', 'Privacy', 'About']
    await step('open', async () => { await open(p, ''); await p.clickSel('[aria-label="Open settings"]'); await sleep(1100) })
    for (const pg of pages) await step('settings-' + pg, async () => { await p.clickExact(pg, 'li > button'); await sleep(900); await snap(p, 'settings', pg.toLowerCase().replace(' ', '-'), AUDIT) })
    await step('settings-text-size-menu', async () => { await p.clickExact('General', 'li > button'); await sleep(600); await p.clickExact('Normal'); await sleep(500); await snap(p, 'settings', 'text-size-menu', AUDIT); await esc(p) })
    await step('signedin', async () => { await esc(p); await open(p, 'signedin'); await p.clickSel('[aria-label="Open settings"]'); await sleep(900); await p.clickExact('Account', 'li > button'); await sleep(900); await snap(p, 'settings', 'account-signed-in', AUDIT) })
    await step('profile', async () => { await open(p, 'profile'); await p.clickSel('[aria-label="Open settings"]'); await sleep(900); await snap(p, 'profile', 'settings-profile', AUDIT) })
  },
  async onboarding(p, v) {
    await step('first-run', async () => { await open(p, 'onboarding', 'Welcome', false); await sleep(1000); await snap(p, 'onboarding', 'welcome', AUDIT) })
    await step('account-choice', async () => { await p.clickSel('.exo-ob-cta'); await sleep(1200); await snap(p, 'onboarding', 'account-choice', AUDIT) })
    await step('account-create', async () => { await p.clickText('Create an account'); await sleep(1000); await snap(p, 'onboarding', 'account-create-form', AUDIT) })
    await step('account-create-empty-submit', async () => { await p.clickSel('.exo-ob-cta, button.is-primary, button[type=submit]'); await sleep(900); await snap(p, 'onboarding', 'account-create-empty-submit', AUDIT) })
    await step('account-signin', async () => { await p.clickText('Back'); await sleep(800); await p.clickText('Sign in'); await sleep(1000); await snap(p, 'onboarding', 'account-signin-form', AUDIT) })
    await step('signedin-steps', async () => {
      await open(p, 'onboarding&signedin', 'Welcome', false)
      for (let i = 1; i <= 8; i++) { await p.clickSel('.exo-ob-cta').catch(() => {}); await sleep(1100); await snap(p, 'onboarding', 'signedin-step-' + i, i <= 6 ? AUDIT : {}) }
    })
  },
}

async function main() {
  const { proc } = await launch()
  try {
    for (const vk of VARS) {
      const p = await newPage(vk)
      for (const g of GROUPS) if (groups[g]) { console.log(vk, g); await groups[g](p, vk); flush('results-' + vk + '-' + g); }
      await p.close()
    }
  } finally { proc.kill() }
  const { writeFileSync } = await import('node:fs')
  writeFileSync(new URL('./out/failures.json', import.meta.url), JSON.stringify(failures, null, 1))
  console.log('done', Object.keys(results).length, 'shots,', failures.length, 'failures')
}
export { groups, step, failures }
if (import.meta.url === new URL(process.argv[1], 'file:///').href || process.argv[1].endsWith('run.mjs')) await main()
