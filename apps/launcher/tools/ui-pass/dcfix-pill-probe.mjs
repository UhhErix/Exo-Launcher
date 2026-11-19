// Where the new-messages pill and the list under it sit (#welcome, a message marked unread, the list at its top).
import { launch, newPage, open, room, sleep } from './harness.mjs'
const W = Number(process.argv[2] ?? 1440), H = Number(process.argv[3] ?? 900)
await launch()
const p = await newPage('void', W, H); await p.view(W, H, { dpr: 1 })
await open(p, ''); await room(p, 'Discord'); await sleep(1500)
await p.eval(`document.querySelector('.exo-dc-rail-item.is-server').scrollIntoView({ block: 'nearest' })`); await sleep(300)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(W - 40, H - 200); await sleep(2200)
await p.fn(() => { const all = [...document.querySelectorAll('.exo-dc-msg:not(.is-mine):not(.is-system)')]; const m = all[all.length - 10]; m.scrollIntoView({ block: 'center' }); m.querySelector('.exo-dc-msg-body').dispatchEvent(new MouseEvent('click', { bubbles: true, altKey: true })) })
await sleep(1200); await p.mouse(W - 30, H - 120)
await p.eval(`(() => { const s = document.querySelector('.exo-dc-scroll'); s.scrollTop = 0 })()`); await sleep(900)
console.log(await p.fn(() => JSON.stringify(['.exo-dc-chat-head', '.exo-dc-chat-main', '.exo-dc-new-bar', '.exo-dc-scroll', '.exo-dc-history-top, .exo-dc-scroll > :first-child', '.exo-dc-day'].map(s => { const e = document.querySelector(s); if (!e) return [s, null]; const r = e.getBoundingClientRect(); const c = getComputedStyle(e); return [s, Math.round(r.top), Math.round(r.bottom), c.position, c.marginTop, c.paddingTop, c.maskImage?.slice(0, 50)] }))))
process.exit(0)
