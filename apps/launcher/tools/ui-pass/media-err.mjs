import { launch, newPage, open, room, sleep, drain } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await open(p, ''); await room(p, 'Discord'); await sleep(1500)
console.log(JSON.stringify(drain(p), null, 1))
process.exit(0)
