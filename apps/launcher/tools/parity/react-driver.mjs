import { execFile } from 'node:child_process'
import { mkdir, rm, writeFile } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { openShell, sleep } from '../e2e/cdp.mjs'
import { seedDataFrom } from "./seed.mjs"

const LAUNCHER_ROOT = fileURLToPath(new URL('../..', import.meta.url))
const TEST_SCRIPT = join(LAUNCHER_ROOT, 'tools', 'test-instance.ps1')
const INSTALLED_DATA = resolve(process.env.LOCALAPPDATA || '', 'ExoLauncher')

const q = (s) => String(s).replace(/'/g, "''")

function pwsh(script, timeout = 90000) {
  return new Promise((resolvePromise, reject) => {
    execFile(
      'pwsh',
      ['-NoProfile', '-NonInteractive', '-Command', script],
      { windowsHide: true, timeout },
      (err, stdout, stderr) => {
        if (err) reject(new Error((stderr || err.message).trim()))
        else resolvePromise(stdout.trim())
      },
    )
  })
}

export function assertSafeDataDir(dataDir) {
  const full = resolve(dataDir)
  if (
    full.toLowerCase() === INSTALLED_DATA.toLowerCase() ||
    full.toLowerCase().startsWith(`${INSTALLED_DATA.toLowerCase()}\\`)
  ) {
    throw new Error(`Refused: data dir ${full} is inside installed ExoLauncher data folder.`)
  }
  return full
}

export async function startWebTestInstance({ folder, dataDir, fresh = true, hostExe = null, seedFrom = null, discordReady = false }) {
  const safeFolder = assertSafeDataDir(folder)
  const safeData = assertSafeDataDir(dataDir)
  if (fresh) {
    await rm(safeData, { recursive: true, force: true })
  }
  await mkdir(safeData, { recursive: true })
  await seedDataFrom(seedFrom, safeData)
  const seededSettings = {
    onboardingComplete: true,
    accountAsked: true,
    whatsNewSeen: '99.0.0',
    background: 'black',
    framelessPinTop: true,
    framelessPinSide: false,
    reduceMotion: true,
  }
  await writeFile(join(safeData, 'settings.json'), JSON.stringify(seededSettings, null, 2))

  const exeArg = hostExe ? `-Exe '${q(hostExe)}'` : ''
  const envArg = `-Env @{ 'EXO_UI' = 'web' }`
  const cmd = `& '${q(TEST_SCRIPT)}' -Folder '${q(safeFolder)}' -DataDir '${q(safeData)}' ${exeArg} ${envArg}`
  const out = await pwsh(cmd, 90000)
  const info = JSON.parse(out.split(/\r?\n/).pop())
  try {
    const shell = await openShell(info.port, 90000)
    await shell.s.send('Emulation.setDeviceMetricsOverride', {
      width: 1920,
      height: 1080,
      deviceScaleFactor: 1,
      mobile: false,
    })
    const deadline = Date.now() + 35000
    let dismissedAccount = false
    while (Date.now() < deadline) {
      const state = await shell.eval(`(() => {
        const sheet = document.querySelector('.exo-account-sheet')
        if (sheet) {
          const fk = Object.keys(sheet).find(k => k.startsWith('__reactFiber$'))
          let cur = fk ? sheet[fk] : null
          while (cur) {
            if (cur.memoizedProps && typeof cur.memoizedProps.onOffline === 'function') {
              cur.memoizedProps.onOffline()
              return 'account-dismissed'
            }
            cur = cur.return
          }
        }
        const buttons = Array.from(document.querySelectorAll('button'))
        for (const btn of buttons) {
          const text = (btn.textContent || btn.getAttribute('aria-label') || '').trim()
          if (/^(later|not now|continue to library|skip|skip tour)$/i.test(text)) {
            btn.click()
            return 'dismissed'
          }
        }
        const bar = document.querySelector('.exo-bar')
        if (bar && !document.querySelector('.exo-account-sheet')) {
          const hasLib =
            document.querySelector('.exo-home-deck') ||
            document.querySelector('.exo-deck-card-btn') ||
            document.querySelector('.exo-tile-hit') ||
            document.querySelector('main')
          if (hasLib) return 'ready'
        }
        return 'waiting'
      })()`)
      if (state === 'account-dismissed' || state === 'dismissed') {
        dismissedAccount = true
        await sleep(600)
        continue
      }
      if (state === 'ready') {
        if (dismissedAccount) break
        await sleep(1200)
        const stillClean = await shell.eval(
          `!document.querySelector('.exo-account-sheet') && !Array.from(document.querySelectorAll('button')).some(b => /^skip tour$/i.test((b.textContent || '').trim()))`,
        )
        if (stillClean) break
      }
      await sleep(400)
    }
    if (discordReady) {
      // The Discord gateway takes ~10 s after launch; the signed-in markers appear when it is ready.
      const until = Date.now() + 40000
      while (Date.now() < until) {
        const ready = await shell.eval("!!document.querySelector('.exo-dc-me, .exo-dc-band-guild, .exo-dc-signin')")
        if (ready) break
        await sleep(500)
      }
    }
    await sleep(4000)
    return { info, shell }
  } catch (err) {
    await stopWebTestInstance(info)
    throw err
  }
}

export async function stopWebTestInstance(info) {
  if (!info?.pid) return
  const exeArg = info.exe ? `-Exe '${q(info.exe)}'` : ''
  await pwsh(`& '${q(TEST_SCRIPT)}' -Stop ${info.pid} ${exeArg}`, 30000).catch(() => {})
  await pwsh(
    `if (Get-Process -Id ${Number(info.pid)} -ErrorAction SilentlyContinue) { taskkill /PID ${Number(info.pid)} /T /F | Out-Null }`,
    15000,
  ).catch(() => {})
  await sleep(800)
}

function findElementCenter(selector, textPattern = null) {
  const re = textPattern ? new RegExp(textPattern.source, textPattern.flags) : null
  const nodes = Array.from(document.querySelectorAll(selector))
  for (const el of nodes) {
    const rect = el.getBoundingClientRect()
    if (rect.width < 2 || rect.height < 2) continue
    if (rect.bottom <= 0 || rect.top >= window.innerHeight || rect.right <= 0 || rect.left >= window.innerWidth) continue
    const style = getComputedStyle(el)
    if (style.visibility === 'hidden' || style.display === 'none' || Number(style.opacity) === 0) continue
    const text = (el.getAttribute('aria-label') || el.textContent || '').replace(/\s+/g, ' ').trim()
    if (re && !re.test(text)) continue
    return {
      x: Math.round(rect.left + rect.width / 2),
      y: Math.round(rect.top + rect.height / 2),
      w: Math.round(rect.width),
      h: Math.round(rect.height),
      text: text.slice(0, 80),
      selector,
    }
  }
  return null
}

function probeDomState(openSelector, menuSelector, lastTarget) {
  const isVisible = (el) => {
    const r = el.getBoundingClientRect()
    if (r.width < 1 || r.height < 1) return false
    if (r.bottom <= 0 || r.top >= window.innerHeight || r.right <= 0 || r.left >= window.innerWidth) return false
    const st = getComputedStyle(el)
    return st.display !== 'none' && st.visibility !== 'hidden' && Number(st.opacity) > 0.01
  }

  let opened = false
  if (openSelector) {
    for (const el of document.querySelectorAll(openSelector)) {
      if (isVisible(el)) {
        opened = true
        break
      }
    }
  } else {
    opened = true
  }

  const glass = []
  for (const el of document.querySelectorAll('*')) {
    if (!isVisible(el)) continue
    const st = getComputedStyle(el)
    const bf = st.backdropFilter || st.webkitBackdropFilter || 'none'
    if (bf && bf !== 'none') {
      const r = el.getBoundingClientRect()
      glass.push({
        rect: [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)],
        backdropFilter: bf,
        backgroundColor: st.backgroundColor,
        className: typeof el.className === 'string' ? el.className.slice(0, 80) : '',
        label: (el.getAttribute('aria-label') || el.getAttribute('role') || el.tagName.toLowerCase()).slice(0, 60),
      })
    }
  }

  const menuSel =
    menuSelector ||
    '[role="menu"] [role^="menuitem"], [role="listbox"] [role="option"], [popover]:popover-open button, .exo-dc-menu button'
  const menuItems = []
  for (const el of document.querySelectorAll(menuSel)) {
    if (!isVisible(el)) continue
    const text = (el.getAttribute('aria-label') || el.textContent || '').replace(/\s+/g, ' ').trim()
    if (text && !menuItems.includes(text)) menuItems.push(text.slice(0, 80))
  }

  const cursors = []
  const seenCoords = new Set()
  const addCursorSample = (name, el) => {
    if (!el || !isVisible(el)) return
    const r = el.getBoundingClientRect()
    const x = Math.round(r.left + r.width / 2)
    const y = Math.round(r.top + r.height / 2)
    const coordKey = `${Math.round(x / 8)},${Math.round(y / 8)}`
    if (seenCoords.has(coordKey)) return
    seenCoords.add(coordKey)
    const hit = document.elementFromPoint(x, y) || el
    const cur = getComputedStyle(hit).cursor || getComputedStyle(el).cursor || 'auto'
    cursors.push({
      name,
      x,
      y,
      cursor: cur === 'auto' ? 'default' : cur,
      text: (el.getAttribute('aria-label') || el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 60),
    })
  }

  addCursorSample('bar:search', document.querySelector('.exo-bar-search'))
  for (const btn of document.querySelectorAll('.exo-nav-btn, .exo-titlebar nav button')) {
    const label = (btn.getAttribute('aria-label') || btn.textContent || '').trim().replace(/\s*\d+$/, '')
    if (label) addCursorSample(`bar:nav:${label}`, btn)
  }
  addCursorSample('bar:capture', document.querySelector('button.exo-capture-btn'))
  addCursorSample('bar:settings', document.querySelector('.exo-settings-btn'))

  if (lastTarget && Number.isFinite(lastTarget.x) && Number.isFinite(lastTarget.y)) {
    const hit = document.elementFromPoint(lastTarget.x, lastTarget.y)
    if (hit) addCursorSample('state:target', hit)
  }

  const scope =
    Array.from(document.querySelectorAll('[role="menu"], [role="listbox"], [aria-modal="true"], [role="dialog"]'))
      .filter(isVisible)
      .pop() ||
    document.querySelector('main') ||
    document.body

  const interactives = scope.querySelectorAll(
    'button, [role="button"], [role="tab"], [role^="menuitem"], [role="option"], a[href], input, select, [role="switch"]',
  )
  let sampled = 0
  for (const el of interactives) {
    if (sampled >= 10) break
    if (el.closest('.exo-titlebar')) continue
    if (!isVisible(el)) continue
    const label = (el.getAttribute('aria-label') || el.textContent || el.getAttribute('placeholder') || el.tagName).replace(/\s+/g, ' ').trim().slice(0, 40)
    addCursorSample(`ui:${sampled}:${label}`, el)
    sampled++
  }

  const headings = Array.from(document.querySelectorAll('h1, h2, h3'))
    .filter(isVisible)
    .slice(0, 8)
    .map((h) => (h.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 80))

  return { opened, glass, menuItems, cursors, headings }
}

async function dispatchKey(shell, step) {
  const modifiers = (step.alt ? 1 : 0) | (step.ctrl ? 2 : 0) | (step.meta ? 4 : 0) | (step.shift ? 8 : 0)
  const key = step.key
  const code = key.length === 1 ? `Key${key.toUpperCase()}` : key
  const vk =
    key === 'Escape' ? 27 : key === 'Enter' ? 13 : key === 'Tab' ? 9 : key.length === 1 ? key.toUpperCase().charCodeAt(0) : 0
  await shell.s.send('Input.dispatchKeyEvent', {
    type: 'rawKeyDown',
    key,
    code,
    modifiers,
    windowsVirtualKeyCode: vk,
    nativeVirtualKeyCode: vk,
  })
  await shell.s.send('Input.dispatchKeyEvent', {
    type: 'keyUp',
    key,
    code,
    modifiers,
    windowsVirtualKeyCode: vk,
    nativeVirtualKeyCode: vk,
  })
}

async function resetReactShell(shell, roomLabel) {
  await shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 2, y: 2 }).catch(() => {})
  await shell.call(() => {
    for (const btn of document.querySelectorAll('button')) {
      const label = (btn.getAttribute('aria-label') || btn.textContent || '').trim()
      if (/^(skip tour|close captures|close search|clear search|clear)$/i.test(label)) {
        btn.click()
      }
    }
  }).catch(() => {})
  await sleep(150)
  for (let i = 0; i < 3; i++) {
    await dispatchKey(shell, { key: 'Escape' }).catch(() => {})
    await sleep(120)
  }
  await shell.call(() => {
    const back = document.querySelector('.exo-sgp-back, .exo-game-back, .exo-settings-back, button[aria-label="Back to Home"]')
    if (back instanceof HTMLElement) back.click()
  }).catch(() => {})
  await sleep(180)
  if (roomLabel) {
    await shell.call((label) => {
      const btn = Array.from(document.querySelectorAll('.exo-nav-btn, .exo-titlebar nav button')).find(
        (b) => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === label,
      )
      if (btn instanceof HTMLElement) btn.click()
    }, roomLabel)
    await sleep(350)
  }
}

export async function runReactPass({ states, outDir, folder, dataDir, hostExe = null, seedFrom = null }) {
  const discordReady = states.some((s) => s.room === 'discord')
  const reactDir = join(outDir, 'react')
  await mkdir(reactDir, { recursive: true })
  const { info, shell } = await startWebTestInstance({ folder, dataDir, fresh: true, hostExe, seedFrom, discordReady })
  const results = {}
  try {
    for (const state of states) {
      let heldAt = null
      let lastTarget = null
      let stepError = null
      try {
        await resetReactShell(shell, state.react?.room || 'Library')
        for (const step of state.react?.steps || []) {
          if (step.type === 'wait') {
            await sleep(step.ms || 300)
          } else if (step.type === 'click') {
            const pt = await shell.call(findElementCenter, step.selector, null)
            if (!pt) {
              if (step.optional) continue
              stepError = `click target not found: ${step.selector}`
              break
            }
            lastTarget = pt
            await shell.clickAt(pt.x, pt.y)
            await sleep(200)
          } else if (step.type === 'clickText') {
            const pt = await shell.call(findElementCenter, step.selector, {
              source: step.text.source,
              flags: step.text.flags,
            })
            if (!pt) {
              if (step.optional) continue
              stepError = `clickText target not found: ${step.selector} /${step.text.source}/`
              break
            }
            lastTarget = pt
            await shell.clickAt(pt.x, pt.y)
            await sleep(200)
          } else if (step.type === 'rightClick') {
            const pt = await shell.call(findElementCenter, step.selector, null)
            if (!pt) {
              stepError = `rightClick target not found: ${step.selector}`
              break
            }
            lastTarget = pt
            await shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pt.x, y: pt.y })
            await shell.s.send('Input.dispatchMouseEvent', {
              type: 'mousePressed',
              x: pt.x,
              y: pt.y,
              button: 'right',
              clickCount: 1,
            })
            await shell.s.send('Input.dispatchMouseEvent', {
              type: 'mouseReleased',
              x: pt.x,
              y: pt.y,
              button: 'right',
              clickCount: 1,
            })
            await sleep(250)
          } else if (step.type === 'hover') {
            const pt = await shell.call(findElementCenter, step.selector, null)
            if (!pt) {
              stepError = `hover target not found: ${step.selector}`
              break
            }
            lastTarget = pt
            await shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pt.x, y: pt.y })
          } else if (step.type === 'hoverText') {
            const pt = await shell.call(findElementCenter, step.selector, {
              source: step.text.source,
              flags: step.text.flags,
            })
            if (!pt) {
              stepError = `hoverText target not found: ${step.selector} /${step.text.source}/`
              break
            }
            lastTarget = pt
            await shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pt.x, y: pt.y })
          } else if (step.type === 'pressHold') {
            const pt = await shell.call(findElementCenter, step.selector, null)
            if (!pt) {
              stepError = `pressHold target not found: ${step.selector}`
              break
            }
            lastTarget = pt
            await shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pt.x, y: pt.y })
            await shell.s.send('Input.dispatchMouseEvent', {
              type: 'mousePressed',
              x: pt.x,
              y: pt.y,
              button: 'left',
              clickCount: 1,
            })
            heldAt = pt
          } else if (step.type === 'key') {
            await dispatchKey(shell, step)
          } else if (step.type === 'type') {
            await shell.s.send('Input.insertText', { text: step.text })
          } else if (step.type === 'scroll') {
            await shell.call(
              (sel, top) => {
                const el = document.querySelector(sel)
                if (el) el.scrollTop = top
              },
              step.selector,
              step.top || 600,
            )
          }
        }

        const shotPath = join(reactDir, `${state.id}.png`)
        await shell.screenshot(shotPath)
        const dom = await shell.call(
          probeDomState,
          state.react?.openSelector || null,
          state.react?.menuSelector || null,
          lastTarget,
        )
        const record = {
          id: state.id,
          room: state.room,
          opened: !stepError && dom.opened,
          stepError,
          screenshot: shotPath,
          lastTarget,
          glass: dom.glass,
          menuItems: dom.menuItems,
          cursors: dom.cursors,
          headings: dom.headings,
        }
        results[state.id] = record
        await writeFile(join(reactDir, `${state.id}.json`), JSON.stringify(record, null, 2))
        process.stdout.write(`[react]  ${record.opened ? 'OK  ' : 'MISS'} ${state.id}${stepError ? ` (${stepError})` : ''}\n`)
      } catch (err) {
        const record = {
          id: state.id,
          room: state.room,
          opened: false,
          stepError: String(err.message || err),
          screenshot: null,
          lastTarget,
          glass: [],
          menuItems: [],
          cursors: [],
          headings: [],
        }
        results[state.id] = record
        await writeFile(join(reactDir, `${state.id}.json`), JSON.stringify(record, null, 2))
        process.stdout.write(`[react]  ERR  ${state.id} (${record.stepError})\n`)
      } finally {
        if (heldAt) {
          await shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 2, y: 2 }).catch(() => {})
          await shell.s
            .send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 2, y: 2, button: 'left', clickCount: 1 })
            .catch(() => {})
        }
      }
    }
  } finally {
    shell.close()
    await stopWebTestInstance(info)
  }
  return { info, results }
}
