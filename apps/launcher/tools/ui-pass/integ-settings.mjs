// Discord's own settings (Edit profile): the preview card's picture and the rows' rules, measured and pictured.
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await p.view(1920, 1080, { dpr: 1 })
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
await p.fn(() => window.dispatchEvent(new CustomEvent('exo-discord-settings', { detail: 'profiles' }))); await sleep(1800)
console.log(await p.fn(() => {
  const pv = document.querySelector('.exo-dc-profile-preview'); if (!pv) return 'no preview'
  const out = []
  for (const e of pv.querySelectorAll('*')) { const r = e.getBoundingClientRect(); if (r.width > 60 && r.width < 140 && r.height > 60) out.push(e.tagName + '.' + [...e.classList].join('.') + ' ' + [r.left, r.top, r.width, r.height].map(Math.round).join(',')) }
  const rows = [...document.querySelectorAll('.exo-set-row, .exo-dc-settings-row, [class*="row"]')].filter(e => getComputedStyle(e).borderBottomWidth !== '0px' && e.closest('.exo-settings, .exo-sheet, [role=dialog]')).slice(0, 6).map(e => [...e.classList].join('.') + ' ' + getComputedStyle(e).borderBottom)
  return JSON.stringify({ out, rows }, null, 1)
}))
const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, 'integ-discord-settings.png'), Buffer.from(data, 'base64'))
process.exit(0)
