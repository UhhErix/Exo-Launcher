// Every page and layer of the Discord room other than Now, a text channel and a plain DM, at 1920x1080 and 1280x720.
//   node tools/ui-pass/pages-shot.mjs [filter]      (PNGs in EXO_UIPASS_SHOTS as pg-<name>-<w>.png; the filter is a substring of a step name)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const filter = process.argv[2] ?? ''
await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name, w) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `pg-${name}-${w}.png`), Buffer.from(data, 'base64')) }
const SIZES = [[1920, 1080], [1280, 720]]
const log = []
const AUDIT = !!process.env.PAGES_AUDIT
/** Boxes that draw a fill, a rim or a border: what is left of a panel. */
const audit = () => {
  const out = []
  for (const e of document.querySelectorAll('main.exo-dc *, body > [class*="exo-dc"] *')) {
    if (e.closest('.exo-dc-msg, .exo-dc-embed, .exo-dc-rail-list, .exo-dc-side-list, .exo-dc-channels-list, svg, .exo-glass')) continue
    const r = e.getBoundingClientRect(); if (r.width * r.height < 2500 || r.width < 40 || r.height < 30) continue
    const s = getComputedStyle(e)
    const bw = ['Top', 'Right', 'Bottom', 'Left'].filter((k) => parseFloat(s['border' + k + 'Width']) > 0 && s['border' + k + 'Style'] !== 'none' && !/rgba?\(\d+, \d+, \d+, 0\)/.test(s['border' + k + 'Color'])).map((k) => k[0]).join('')
    const bg = s.backgroundColor, bgv = /rgba\(\d+, \d+, \d+, 0\)/.test(bg) ? '' : bg
    const sh = s.boxShadow === 'none' ? '' : 'shadow'
    if (!bw && !bgv && !sh) continue
    out.push(`${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)} ${e.tagName.toLowerCase()}.${[...e.classList].slice(0, 3).join('.')} r=${s.borderRadius.split(' ')[0]} ${bw ? 'B:' + bw : ''} ${bgv} ${sh}`)
    if (out.length > 22) break
  }
  return out
}
const home = async (q = '') => { for (let i = 0; i < 3; i++) { try { await open(p, q); await room(p, 'Discord'); break } catch (e) { if (i === 2) throw e; await sleep(800) } } await sleep(900) }
const server = async (i = 0) => { for (let t = 0; t < 3; t++) { await home(); try { await p.clickSel('.exo-dc-rail-item.is-server', i); break } catch (e) { if (t === 2) throw e } } await sleep(1500) }
const chan = async (t) => { await p.clickText(t, '.exo-dc-channels .exo-dc-item'); await sleep(1200) }
const dm = async (t) => { await home(); await p.clickText(t, '.exo-dc-side-list .exo-dc-item.is-dm'); await sleep(1400) }
const T = (t, scope) => p.clickText(t, scope)
const esc = async () => { await p.key('Escape'); await sleep(250) }
const openServerMenu = async () => { await p.fn(() => document.querySelector('.exo-dc-rail-more').click()); await sleep(600) }
const menuOf = async (t) => { await openServerMenu(); await p.clickText(t, '[role=menuitem],.exo-dc-menu button,button'); await sleep(900) }
const settings = async (page) => { await server(); await menuOf('Server settings'); if (page) { await p.clickExact(page, '.exo-dc-menu button'); await sleep(1100) } }

const STEPS = {
  'voice-out': async () => { await server(); await chan('Lobby') },
  'voice-in': async () => { await server(); await chan('Lobby'); await T('Join voice', 'button').catch(() => T('Join', 'button')); await sleep(1400) },
  'thread': async () => { await server(); await chan('general'); await T('Patch 9 hot takes', '.exo-dc-item').catch(() => {}); await sleep(1200) },
  'forum': async () => { await server(); await chan('strategies') },
  'announce': async () => { await server(); await chan('patch-notes') },
  'requests': async () => { await home(); await T('Message requests', '.exo-dc-item') ; await sleep(1000) },
  'shop': async () => { await home(); await T('Shop', '.exo-dc-item'); await sleep(1000) },
  'group-dm': async () => { await dm('Kai, Ada') },
  'dm-profile-open': async () => { await dm('Ada'); await sleep(900) },
  'dm-profile-closed': async () => { await dm('Ada'); await p.clickSel('.exo-dc-head-tools .exo-dc-icon-btn[aria-pressed="true"]'); await sleep(900) },
  'server-menu': async () => { await server(); await openServerMenu() },
  'server-settings': async () => { await settings() },
  ...Object.fromEntries(['Server profile', 'Server tag', 'Engagement', 'Emoji', 'Stickers', 'Soundboard', 'Members', 'Roles', 'Invites', 'Access', 'Integrations', 'App directory', 'Safety setup', 'Audit log', 'Bans', 'AutoMod'].map((n) => ['ss-' + n.toLowerCase().replace(/ /g, '-'), async () => { await settings(n) }])),
  'go-live': async () => { await server(); await chan('Lobby'); await T('Join voice', 'button'); await sleep(1400); await p.clickSel('[aria-label="Share your screen"]'); await sleep(1200) },
  'channel-settings': async () => { await server(); await p.fn(() => document.querySelector('[aria-label="Edit general"]').click()); await sleep(1200) },
  'channel-settings-perms': async () => { await server(); await p.fn(() => document.querySelector('[aria-label="Edit general"]').click()); await sleep(1200); await p.clickExact('Permissions', '.exo-dc-sheet-nav button'); await sleep(900) },
  'user-menu-status': async () => { await home(); await p.clickSel('.exo-dc-me-who'); await sleep(500); await p.clickText('Idle', '.exo-dc-menu button, .exo-dc-menu [role=menuitem]'); await sleep(700) },
  'user-menu-accounts': async () => { await home(); await p.clickSel('.exo-dc-me-who'); await sleep(500); await p.clickText('Switch accounts', '.exo-dc-menu button, .exo-dc-menu [role=menuitem]'); await sleep(700) },
  'discord-settings': async () => { await home(); await p.clickSel('.exo-dc-me-who'); await sleep(500); await p.clickText('Edit profile', '.exo-dc-menu button'); await sleep(1500) },
  'notif-settings': async () => { await server(); await menuOf('Notification') },
  'privacy': async () => { await server(); await menuOf('Privacy') },
  'user-menu': async () => { await home(); await p.clickSel('.exo-dc-me-who'); await sleep(700) },
  'inbox': async () => { await home(); await p.clickSel('.exo-dc-inbox-anchor .exo-dc-icon-btn, [aria-label="Inbox"]'); await sleep(900) },
  'ctx-server': async () => { await home(); const r = await p.fn(() => { const b = document.querySelector('.exo-dc-rail-item.is-server').getBoundingClientRect(); return { x: b.x + 30, y: b.y + 10 } }); await p.rclick(r.x, r.y); await sleep(600) },
  'ctx-channel': async () => { await server(); const r = await p.fn(() => { const b = [...document.querySelectorAll('.exo-dc-channels .exo-dc-item')].find((e) => e.textContent.includes('general')).getBoundingClientRect(); return { x: b.x + 60, y: b.y + 10 } }); await p.rclick(r.x, r.y); await sleep(600) },
  'ctx-message': async () => { await server(); await chan('general'); const r = await p.fn(() => { const b = [...document.querySelectorAll('.exo-dc-msg')].find((e) => e.textContent.includes('my config')).getBoundingClientRect(); return { x: b.x + 300, y: b.y + 20 } }); await p.rclick(r.x, r.y); await sleep(600) },
  'ctx-person': async () => { await server(); await chan('general'); await p.clickSel('.exo-dc-people-pill'); await sleep(700); const r = await p.fn(() => { const b = document.querySelector('.exo-dc-members-list .exo-dc-member:not(.is-pending)').getBoundingClientRect(); return { x: b.x + 60, y: b.y + 10 } }); await p.rclick(r.x, r.y); await sleep(600) },
  'profile-card': async () => { await server(); await chan('general'); await p.clickSel('.exo-dc-people-pill'); await sleep(700); await p.clickSel('.exo-dc-members-list .exo-dc-member:not(.is-pending)'); await sleep(900) },
  'signedout': async () => { await home('dc=signedout') },
  'connecting': async () => { await home('dc=connecting') },
  'empty': async () => { await home('dc=empty') },
}
for (const [name, f] of Object.entries(STEPS)) {
  if (filter && !filter.split(',').some((f) => name.includes(f))) continue
  try {
    await p.view(1920, 1080)
    await f()
    for (const [w, h] of SIZES) { await p.view(w, h); await sleep(500); await shot(name, w); if (AUDIT && w === 1920) log.push(...(await p.fn(audit)).map((l) => '     ' + l)) }
    log.push(`ok   ${name}`)
  } catch (e) { log.push(`FAIL ${name}: ${String(e.message).split('\n')[0]}`) }
}
console.log(log.join('\n'))
process.exit(0)
