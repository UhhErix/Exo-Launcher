// The Now page at 1920x1080, 1440x900 and 1280x720: nowp-1920.png, nowp-1440.png, nowp-1280.png in EXO_UIPASS_SHOTS.
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const p = await newPage('void', 1920, 1080)
await open(p, '')
await room(p, 'Discord'); await sleep(1200)
for (const [w, h] of [[1920, 1080], [1440, 900], [1280, 720]]) {
  await p.view(w, h, { dpr: 1 }); await sleep(700)
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(join(SHOTS, `nowp-${w}.png`), Buffer.from(data, 'base64'))
}
console.log('done')
process.exit(0)
