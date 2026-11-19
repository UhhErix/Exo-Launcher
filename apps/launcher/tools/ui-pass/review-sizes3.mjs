import { launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 960, 600)
await open(p, ''); await room(p, 'Discord'); await sleep(1000)
await p.fn(() => { document.querySelectorAll('.exo-dc-rail-item.is-server')[0].click() }); await sleep(1500)
console.log(JSON.stringify(await p.fn(() => {
  const l = document.querySelector('.exo-dc-rail-list'); const cs = getComputedStyle(l)
  return { sw: l.scrollWidth, cw: l.clientWidth, sbw: cs.scrollbarWidth, items: [...l.children].map(e => { const r = e.getBoundingClientRect(); return [e.className.slice(0,40), e.getAttribute('aria-label')||e.title, Math.round(r.left), Math.round(r.width), e.textContent.trim().slice(0,12)] }) }
})))
// Now page scroll
await p.fn(() => { document.querySelector('.exo-dc-rail > .is-home').click() }); await sleep(600)
await p.fn(() => { [...document.querySelectorAll('.exo-dc-side *')].find(e => e.textContent.trim().startsWith('Now') && e.children.length < 4).click() }); await sleep(900)
console.log(JSON.stringify(await p.fn(() => {
  const out = []; let e = document.querySelector('.exo-dc-now'); while (e && e !== document.body) { if (e.scrollHeight > e.clientHeight + 2) out.push([e.className.slice(0,50), e.scrollHeight, e.clientHeight, getComputedStyle(e).overflowY]); e = e.parentElement } return out
})))
process.exit(0)
