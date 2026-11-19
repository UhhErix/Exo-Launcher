// The same screens at the Windows scales people use (125%, 150%), to judge lines on real device pixels.
//   node tools/ui-pass/dpr.mjs            (shots in EXO_UIPASS_SHOTS, names end -125 / -150)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
for (const dpr of [1.25, 1.5]) {
  const p = await newPage('void', Math.round(1920 / dpr), Math.round(1080 / dpr))
  await p.view(Math.round(1920 / dpr), Math.round(1080 / dpr), { dpr })
  const shot = async (name) => {
    const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
    writeFileSync(join(SHOTS, `${name}-${Math.round(dpr * 100)}.png`), Buffer.from(data, 'base64'))
  }
  await open(p, ''); await sleep(600); await shot('dpr-library')
  await room(p, 'Store'); await shot('dpr-store')
  await p.clickSel('.exo-settings-btn'); await sleep(900); await shot('dpr-settings')
  await p.close?.()
}
console.log('done')
process.exit(0)
