// Discord settings layer, a member's profile card, a DM's profile: node tools/ui-pass/pages-more.mjs
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => { for (const [w, h] of [[1920, 1080], [1280, 720]]) { await p.view(w, h); await sleep(500); const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `pm-${name}-${w}.png`), Buffer.from(data, 'base64')) } await p.view(1920, 1080) }
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
await p.fn(() => window.dispatchEvent(new CustomEvent('exo-discord-settings', { detail: 'profiles' }))); await sleep(1800)
await shot('discord-settings')
await p.key('Escape'); await sleep(500)
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800)
await p.clickText('general', '.exo-dc-channels .exo-dc-item'); await sleep(1200)
await p.clickSel('.exo-dc-people-pill'); await sleep(900)
console.log(await p.fn(() => [...document.querySelectorAll('.exo-dc-members .exo-dc-member')].slice(0, 5).map((e) => e.className + '|' + e.textContent.slice(0, 20)).join('\n')))
await p.fn(() => { const m = [...document.querySelectorAll('.exo-dc-members .exo-dc-member')].find((e) => !e.classList.contains('is-pending')); m && m.click() }); await sleep(1000)
await shot('profile-card')
process.exit(0)
