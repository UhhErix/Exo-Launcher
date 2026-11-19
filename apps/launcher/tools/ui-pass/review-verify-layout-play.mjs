import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); await p.eval(`localStorage.setItem('exo.discordPrefs', JSON.stringify({playRooms:true}))`)
await open(p, ''); await room(p, 'Discord'); await sleep(900)
await shot('play-0')
const r = await p.clickText('Play', '.exo-dc-rail-item').catch(e => String(e)); console.log(r); await sleep(1500)
await shot('play-1')
console.log(await p.fn(() => JSON.stringify({ rail: document.querySelector('.exo-dc-rail')?.getBoundingClientRect(), home: getComputedStyle(document.querySelector('.exo-dc-rail-item.is-home')).display, stage: document.querySelector('.exo-dc-stage').className })))
process.exit(0)
