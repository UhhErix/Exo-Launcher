import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, 'dc=long'); await room(p, 'Discord'); await sleep(1000)
await p.view(1600, 720); await sleep(600)
const info = () => p.fn(() => { const q = s => { const e = document.querySelector(s); if (!e) return null; const b = e.getBoundingClientRect(); return [b.left, b.top, b.width, b.height].map(Math.round).join(',') }; const m = getComputedStyle(document.querySelector('main.exo-dc')); return JSON.stringify({ foot: q('.exo-dc-foot'), dock: q('.exo-dc-dock'), rail: q('.exo-dc-rail'), footh: m.getPropertyValue('--dc-foot-h'), radius: getComputedStyle(document.querySelector('.exo-dc-foot')).borderRadius }) })
console.log('before', await info())
try { await p.clickText('Join', '.exo-dc button'); } catch (e) { console.log(String(e)) }
await sleep(1500)
console.log('call', await info()); await shot('call-home')
await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(e => console.log(String(e))); await sleep(1500)
console.log('call-srv', await info()); await shot('call-srv')
process.exit(0)
