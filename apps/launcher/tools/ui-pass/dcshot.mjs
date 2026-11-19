// The Discord room's three main pages as pictures, quickly: Now, a server's channel, a conversation.
//   node tools/ui-pass/dcshot.mjs [dpr]      (shots in EXO_UIPASS_SHOTS: dc-friends, dc-server, dc-dm)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const dpr = Number(process.argv[2] ?? 1)
await launch()
const p = await newPage('void', Math.round(1920 / dpr), Math.round(1080 / dpr))
await p.view(Math.round(1920 / dpr), Math.round(1080 / dpr), { dpr })
const shot = async (name) => {
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(join(SHOTS, `${name}${dpr === 1 ? '' : '-' + Math.round(dpr * 100)}.png`), Buffer.from(data, 'base64'))
}
await open(p, '')
await room(p, 'Discord'); await sleep(1200); await shot('dc-friends')
await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800); await shot('dc-server')
await p.clickSel('.exo-dc-rail .exo-dc-side-list > .exo-dc-item.is-big', 0).catch(() => {}); await sleep(900)
await p.clickSel('.exo-dc-side-list .exo-dc-item.is-dm', 0).catch(() => {}); await sleep(1500); await shot('dc-dm')
console.log('done')
process.exit(0)
