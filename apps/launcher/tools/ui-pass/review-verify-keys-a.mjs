import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const out = {}
const act = () => { const a = document.activeElement; if (!a || a === document.body) return 'BODY'; return a.tagName + ' ' + (a.getAttribute('aria-label') || a.textContent.trim().slice(0, 30)) + ' @' + Math.round(a.getBoundingClientRect().top) }
const shot = async (p, n) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, n + '.png'), Buffer.from(data, 'base64')) }
{
  const p = await newPage('void', 1600, 900)
  await open(p, ''); await room(p, 'Discord'); await sleep(1200)
  out.domOrder = await p.fn(() => [...document.querySelector('main.exo-dc').children].map(e => e.tagName + '.' + String(e.className).split(' ')[0]).join(' > '))
  out.boxes = await p.fn(() => ['.exo-dc-rail', '.exo-dc-stage > .exo-dc-side', '.exo-dc-foot'].map(s => { const e = document.querySelector(s); const r = e?.getBoundingClientRect(); return s + ' top ' + (r && Math.round(r.top)) + ' h ' + (r && Math.round(r.height)) }))
  await p.fn(() => { document.querySelector('.exo-dc-rail-item.is-server').focus() })
  out.tabFromServer = []
  for (let i = 0; i < 14; i++) { await p.key('Tab'); await sleep(50); out.tabFromServer.push(await p.fn(act)) }
  await p.fn(() => { document.querySelector('.exo-dc-rail-item.is-server').focus() }); await p.key('Shift+Tab').catch(()=>{}); await sleep(50)
  out.labels = await p.fn(() => [...document.querySelectorAll('.exo-dc-side-list .exo-dc-item, .exo-dc-rail-item')].slice(0, 12).map(e => (e.getAttribute('aria-label') ?? '(text)') + ' | ' + e.textContent.trim().slice(0, 30)))
  // find escape
  await p.fn(() => document.querySelector('button[aria-label="Find a conversation"]').click()); await sleep(200)
  out.findFocus = await p.fn(act)
  await p.key('Escape'); await sleep(200); out.afterEsc = await p.fn(act)
  // new conversation parent
  await p.fn(() => document.querySelector('button[aria-label="New conversation"]').click()); await sleep(300)
  out.startParent = await p.fn(() => { const m = document.querySelector('.exo-dc-menu.is-start'); return m ? m.parentElement.tagName + ' role=' + m.getAttribute('role') : 'none' })
  await shot(p, 'vk-newconv')
  out.nowHeadings = await p.fn(() => ({ h: [...document.querySelectorAll('main.exo-dc h1,main.exo-dc h2,main.exo-dc h3,[role=heading]')].map(e => e.tagName + ' ' + e.textContent.trim().slice(0, 20)), labels: [...document.querySelectorAll('.exo-dc-home .exo-dc-side-label, [class*=exo-dc-now] .exo-dc-side-label')].map(e => e.tagName + ' ' + e.textContent.trim().slice(0, 20)), btns: [...document.querySelectorAll('[class*=exo-dc-now] button')].map(b => b.getAttribute('aria-label') || b.textContent.trim().slice(0, 20)).slice(0, 20) }))
  await p.close()
}
{
  const p = await newPage('forced', 1600, 900)
  await open(p, ''); await room(p, 'Discord'); await sleep(1200)
  await shot(p, 'vk-home-forced')
  out.forced = await p.fn(() => { const g = document.querySelector(".exo-dc-side-list"); const b = getComputedStyle(g, "::before"); const glide = g.className; const cur = g.querySelector('[aria-current="true"]'); const cs = getComputedStyle(cur); return { glide, bg: b.backgroundColor, content: b.content, canvas: getComputedStyle(document.body).backgroundColor, cur: cur.textContent.trim(), curBg: cs.backgroundColor, curOutline: cs.outlineStyle + cs.outlineWidth, curBorder: cs.borderStyle + cs.borderWidth, card: (() => { const c = document.querySelector('.exo-dc-now-card'); if (!c) return 'nocard'; const s = getComputedStyle(c); return s.borderStyle + ' ' + s.borderWidth + ' ' + s.boxShadow + ' ' + s.outlineStyle })(), search: (() => { const c = document.querySelector('.exo-dc-friend-search'); if (!c) return 'none'; const s = getComputedStyle(c); return c.tagName + ' ' + s.borderStyle + ' ' + s.borderWidth + ' ' + s.outlineStyle })() } })
  await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800); await shot(p, 'vk-server-forced')
  out.forcedStrip = await p.fn(() => { const cur = document.querySelector('.exo-dc-rail [aria-current="true"]'); const a = getComputedStyle(cur, '::after'); const s = getComputedStyle(cur); return { after: a.backgroundColor, outline: s.outlineStyle, border: s.borderStyle + s.borderWidth } })
  await p.close()
}
{
  const p = await newPage('void', 1600, 900)
  await open(p, ''); await room(p, 'Discord'); await sleep(1200)
  await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(() => {}); await sleep(1800)
  out.strip = await p.fn(() => { const l = document.querySelector('.exo-dc-rail-list'); const r = l.getBoundingClientRect(); const items = [...l.querySelectorAll('.exo-dc-rail-item')]; const f = items[0].getBoundingClientRect(); const last = items[items.length - 1].getBoundingClientRect(); const cs = getComputedStyle(l); return { list: [Math.round(r.left), Math.round(r.right), Math.round(r.top), Math.round(r.bottom)], first: [Math.round(f.left), Math.round(f.top), Math.round(f.bottom)], last: Math.round(last.right), ov: cs.overflowX + '/' + cs.overflowY, pad: cs.padding, n: items.length } })
  await p.fn(() => { document.querySelector('.exo-dc-rail-item.is-home').focus() }); await p.key('Tab'); await sleep(200)
  out.stripFocus = await p.fn(() => { const a = document.activeElement; const s = getComputedStyle(a); return a.className + ' ' + s.outlineStyle + ' ' + s.outlineWidth + ' off ' + s.outlineOffset })
  const { data } = await p.send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width: 400, height: 80, scale: 4 } }); writeFileSync(join(SHOTS, 'vk-strip-kb0.png'), Buffer.from(data, 'base64'))
  await p.close()
}
writeFileSync(join(SHOTS, 'vk.json'), JSON.stringify(out, null, 1))
console.log(JSON.stringify(out, null, 1))
process.exit(0)
