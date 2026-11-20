// The conversation's head with its More menu: people pill (off, on), search glyph and field, a DM's head.
//   EXO_UIPASS_CDP=9392 EXO_UIPASS_SHOTS=... node tools/ui-pass/chat-shot.mjs
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const p = await newPage('void', 1920, 1080)
await p.view(1920, 1080, { dpr: 1 })
const shot = async (name) => {
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(join(SHOTS, `${name}.png`), Buffer.from(data, 'base64'))
}
await open(p, '')
await room(p, 'Discord'); await sleep(1200)
await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800); await shot('chat-server-off')
await p.clickSel('.exo-dc-people-pill', 0).catch(e => console.log('pill', e.message)); await sleep(1800); await shot('chat-server-people')
await p.clickSel('.exo-dc-head-search-glyph', 0).catch(e => console.log('glyph', e.message)); await sleep(500); console.log(JSON.stringify(await p.eval(`(()=>{const m=document.querySelector('.exo-dc-search-menu');const c=getComputedStyle(m);return [c.position,c.top,c.bottom,c.transform,c.maxHeight,m.children.length,m.getBoundingClientRect().height]})()`))); await shot('chat-server-search')
await p.eval(`document.activeElement?.blur()`); await sleep(500); await shot('chat-server-search-closed')
const more = '.exo-dc-chat-head button[aria-label="More"]'
const item = (label) => p.fn((l) => { const b = [...document.querySelectorAll('.exo-dc-chat-head .exo-dc-menu [role^=menuitem]')].find((x) => x.textContent.startsWith(l)); b?.click(); return !!b }, label)
await p.eval(`document.activeElement?.blur()`)
await p.clickSel(more, 0).catch(e => console.log('more', e.message)); await sleep(500); await shot('head-more-server')
console.log('focus', await p.eval(`document.activeElement?.textContent`))
await p.eval(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}))`); await sleep(100)
console.log('arrow', await p.eval(`document.activeElement?.textContent`))
await p.eval(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))`); await sleep(200)
console.log('esc focus', await p.eval(`document.activeElement?.getAttribute('aria-label')`), await p.eval(`!!document.querySelector('.exo-dc-chat-head .exo-dc-menu')`))
for (const [label, name] of [['Threads', 'threads'], ['Pinned', 'pins'], ['Notification', 'notify']]) {
  await p.clickSel(more, 0).catch(() => {}); await sleep(400)
  console.log(label, await item(label)); await sleep(900); await shot('head-' + name)
  await p.eval(`document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))`); await sleep(300)
}
await p.clickSel('.exo-dc-rail-item.is-home', 0).catch(() => {}); await sleep(900)
await p.clickSel('.exo-dc-side-list .exo-dc-item.is-dm', 0).catch(() => {}); await sleep(1500); await shot('chat-dm')
await p.clickSel(more, 0).catch(e => console.log('dm more', e.message)); await sleep(500); await shot('head-more-dm')
console.log('done')
process.exit(0)
