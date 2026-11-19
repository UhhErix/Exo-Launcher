// Print what a selector holds in the open conversation: media-probe.mjs "<css selector>" "<js expr on el>"
import { launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, ''); await room(p, 'Discord'); await sleep(1000)
await p.clickSel('.exo-dc-side-list .exo-dc-item.is-dm', 0).catch(() => {}); await sleep(2000)
console.log(JSON.stringify(await p.eval(`[...document.querySelectorAll(${JSON.stringify(process.argv[2])})].map(e => (${process.argv[3] ?? 'e.outerHTML.slice(0, 300)'}))`), null, 1))
process.exit(0)
