// One-off measurement: the React command palette's rendered box at 1920x1080 (not part of the gate set).
// Usage: node tools/parity/measure-palette.mjs <folder> <dataDir>   (a temp folder; never the real data dir)
import { join, resolve } from 'node:path'
import { startWebTestInstance, stopWebTestInstance } from './react-driver.mjs'

const [folderArg, dataArg] = process.argv.slice(2)
if (!folderArg || !dataArg) throw new Error('usage: measure-palette.mjs <folder> <dataDir>')
const folder = resolve(folderArg)
const dataDir = resolve(dataArg)
process.env.EXO_E2E_TEST_INSTANCE = '1'

const { info, shell } = await startWebTestInstance({ folder, dataDir, fresh: true })
try {
  await shell.s.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 })
  await shell.s.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 })
  await new Promise(r => setTimeout(r, 1200))
  const result = await shell.eval(`(() => {
    const list = document.querySelector('[role="listbox"][aria-label="Commands"]')
    if (!list) return { open: false }
    const dialog = list.closest('.exo-glass.is-panel') || list.parentElement
    const r = dialog.getBoundingClientRect()
    const scroller = document.querySelector('.exo-command-list')
    const sr = scroller ? scroller.getBoundingClientRect() : null
    return {
      open: true,
      viewport: [window.innerWidth, window.innerHeight],
      dialog: [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)],
      scroller: sr ? [Math.round(sr.height), scroller.scrollHeight] : null,
      options: document.querySelectorAll('[role="listbox"][aria-label="Commands"] [role="option"]').length,
    }
  })()`)
  console.log(JSON.stringify(result))
} finally {
  await stopWebTestInstance(info)
}
