// Empty, stale, loading and offline states as pictures, on the review server (serve.mjs: ?empty[=<prefix>], ?offline,
// ?stale, ?slow, ?fail=<prefix>; hostMock: ?hold=<prefix>). One picture per state, named for it, so a before and an
// after can be laid side by side.
//   node tools/ui-pass/serve.mjs            (keep running; port 5291)
//   EXO_UIPASS_SHOTS=<dir> node tools/ui-pass/states-shot.mjs [only-these,names]
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const only = process.argv[2]?.split(',')
const menu = async (p, trigger, item) => { await p.clickSel(trigger); await sleep(400); await p.clickText(item, '[role=menuitem]'); await sleep(1400) }
const STATES = [
  ['discord-offline', 'offline', async (p) => { await room(p, 'Discord'); await sleep(2500) }],
  ['discord-friends-empty', 'empty', async (p) => { await room(p, 'Discord'); await sleep(1500) }],
  ['discord-friends-failed', 'fail=discord.friends', async (p) => { await room(p, 'Discord'); await sleep(1500) }],
  ['discord-signed-out-stale', 'stale', async (p) => { await room(p, 'Discord'); await sleep(5000) }],
  ['downloads-empty', 'empty=downloads.list', async (p) => menu(p, '.exo-library-more', 'Downloads')],
  ['downloads-failed', 'fail=downloads.list', async (p) => menu(p, '.exo-library-more', 'Downloads')],
  ['captures-empty', 'empty', async (p) => menu(p, '.exo-capture-btn', 'Captures')],
  ['notes-loading', 'hold=notes.list', async (p) => menu(p, '.exo-capture-btn', 'New note')],
  ['store-offline', 'offline', async (p) => { await room(p, 'Store'); await sleep(1500) }],
  ['music-offline', 'offline', async (p) => { await room(p, 'Music'); await sleep(2500) }],
  ['pc-offline', 'offline', async (p) => { await room(p, 'PC'); await sleep(1500) }],
  ['apps-failed', 'fail=apps.list', async (p) => { await room(p, 'PC'); await p.clickText('Apps'); await sleep(1800) }],
  ['startup-failed', 'fail=startup.list', async (p) => { await room(p, 'PC'); await p.clickText('Apps'); await sleep(900); await p.clickText('Starts with Windows', '[role=tab],button'); await sleep(1500) }],
  ['devices-empty', 'empty', async (p) => { await room(p, 'PC'); await p.clickText('Devices'); await sleep(1500) }],
  ['library-stale', 'stale', async (p) => { await sleep(5000) }],
]
await launch()
const p = await newPage('void', 1600, 900)
for (const [name, query, go] of STATES) {
  if (only && !only.includes(name)) continue
  try {
    await open(p, query)
    await go(p)
    const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
    writeFileSync(join(SHOTS, `${name}.png`), Buffer.from(data, 'base64'))
    console.log('shot', name)
  } catch (error) { console.log('miss', name, String(error.message).slice(0, 120)) }
}
await p.close()
process.exit(0)
