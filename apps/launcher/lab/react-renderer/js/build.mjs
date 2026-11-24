// SPDX-License-Identifier: MIT

import { build } from 'esbuild'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const distDir = path.join(__dirname, 'dist')
fs.mkdirSync(distDir, { recursive: true })

const reactDomShim = path.join(__dirname, 'reconciler', 'reactDomShim.ts')
const reactDir = path.join(__dirname, 'node_modules', 'react')
const launcherDir = path.resolve(__dirname, '..', '..', '..')
const mouseNodeModules = path.join(launcherDir, 'mouse', 'node_modules')
const uiNodeModules = path.join(launcherDir, 'ui', 'node_modules')

function makeCssCollectorPlugin(collected) {
  return {
    name: 'collect-css',
    setup(b) {
      b.onResolve({ filter: /\.css$/ }, args => ({
        path: path.resolve(args.resolveDir, args.path),
        namespace: 'css-collect',
      }))
      b.onLoad({ filter: /.*/, namespace: 'css-collect' }, args => {
        try {
          const css = fs.readFileSync(args.path, 'utf8')
          collected.push({ file: path.basename(args.path), css })
        } catch {
          // ignore missing optional css
        }
        return { contents: '', loader: 'js' }
      })
    },
  }
}

const aliasPlugin = {
  name: 'rune-aliases',
  setup(b) {
    b.onResolve({ filter: /^react-dom(\/client)?$/ }, () => ({ path: reactDomShim }))
    b.onResolve({ filter: /^react$/ }, () => ({ path: path.join(reactDir, 'index.js') }))
    b.onResolve({ filter: /^react\/jsx-runtime$/ }, () => ({ path: path.join(reactDir, 'jsx-runtime.js') }))
    b.onResolve({ filter: /^react\/jsx-dev-runtime$/ }, () => ({ path: path.join(reactDir, 'jsx-dev-runtime.js') }))
    b.onResolve({ filter: /^clsx$/ }, () => {
      const uiClsx = path.join(uiNodeModules, 'clsx', 'dist', 'clsx.mjs')
      if (fs.existsSync(uiClsx)) return { path: uiClsx }
      return { path: 'clsx', namespace: 'clsx-inline' }
    })
    b.onLoad({ filter: /.*/, namespace: 'clsx-inline' }, () => ({
      contents: `export function clsx(...args){const out=[];for(const a of args){if(!a)continue;if(typeof a==='string'||typeof a==='number')out.push(String(a));else if(Array.isArray(a)){const r=clsx(...a);if(r)out.push(r)}else if(typeof a==='object'){for(const [k,v] of Object.entries(a))if(v)out.push(k)}}return out.join(' ')} export default clsx;`,
      loader: 'js',
    }))
    b.onResolve({ filter: /^skullet\/react$/ }, () => ({ path: 'skullet/react', namespace: 'skullet-stub' }))
    b.onLoad({ filter: /.*/, namespace: 'skullet-stub' }, () => ({
      contents: `import { jsx } from 'react/jsx-runtime'; export function Skeleton({ children, className, style }) { return jsx('div', { className: className || 'exo-skeleton', style, children }); }`,
      loader: 'js',
    }))
    b.onResolve({ filter: /\?raw$/ }, args => {
      const clean = args.path.slice(0, -4)
      return { path: path.resolve(args.resolveDir, clean), namespace: 'vite-raw' }
    })
    b.onLoad({ filter: /.*/, namespace: 'vite-raw' }, args => ({
      contents: fs.existsSync(args.path) ? fs.readFileSync(args.path, 'utf8') : '',
      loader: 'text',
    }))
    b.onResolve({ filter: /^qrcode-generator$/ }, () => {
      const uiQr = path.join(uiNodeModules, 'qrcode-generator', 'qrcode.js')
      if (fs.existsSync(uiQr)) return { path: uiQr }
      return { path: 'qrcode-generator', namespace: 'qr-stub' }
    })
    b.onLoad({ filter: /.*/, namespace: 'qr-stub' }, () => ({
      contents: `export default function qrcode(){return {addData(){},make(){},getModuleCount(){return 0},isDark(){return false},createSvgTag(){return ''}}};`,
      loader: 'js',
    }))
  },
}

const commonOptions = {
  bundle: true,
  format: 'iife',
  target: ['es2022'],
  jsx: 'automatic',
  minify: false,
  define: {
    'process.env.NODE_ENV': '"production"',
    'import.meta.env.DEV': 'false',
    'import.meta.env.PROD': 'true',
    'import.meta.env.MODE': '"production"',
  },
  nodePaths: [path.join(__dirname, 'node_modules'), mouseNodeModules, uiNodeModules],
  loader: {
    '.png': 'dataurl',
    '.webp': 'dataurl',
    '.ico': 'dataurl',
    '.svg': 'text',
    '.md': 'text',
    '.woff2': 'empty',
  },
}

const mouseCss = []
await build({
  ...commonOptions,
  entryPoints: [path.join(__dirname, 'entries', 'mouse-entry.tsx')],
  outfile: path.join(distDir, 'mouse.bundle.js'),
  plugins: [aliasPlugin, makeCssCollectorPlugin(mouseCss)],
})

const mouseCssDir = path.join(launcherDir, 'mouse', 'src')
const mouseCssFiles = [
  path.join(mouseCssDir, 'mouse.css'),
  path.join(mouseCssDir, 'sensor-angle.css'),
  ...fs.readdirSync(path.join(mouseCssDir, 'brands'))
    .filter(f => f.endsWith('.css'))
    .map(f => path.join(mouseCssDir, 'brands', f)),
]
fs.writeFileSync(
  path.join(distDir, 'mouse.bundle.css'),
  mouseCssFiles.map(f => `/* ${path.basename(f)} */\n${fs.readFileSync(f, 'utf8')}`).join('\n\n'),
)

const settingsCss = []
await build({
  ...commonOptions,
  entryPoints: [path.join(__dirname, 'entries', 'settings-entry.tsx')],
  outfile: path.join(distDir, 'settings.bundle.js'),
  plugins: [aliasPlugin, makeCssCollectorPlugin(settingsCss)],
})

const uiSrc = path.join(launcherDir, 'ui', 'src')
const settingsCssFiles = [
  path.join(uiSrc, 'tokens.css'),
  path.join(uiSrc, 'styles', 'chrome.css'),
  path.join(uiSrc, 'styles', 'glass.css'),
  path.join(uiSrc, 'styles', 'settings.css'),
  path.join(uiSrc, 'styles', 'account.css'),
  path.join(uiSrc, 'styles', 'dialog.css'),
  path.join(uiSrc, 'styles', 'progress.css'),
  path.join(uiSrc, 'styles', 'waits.css'),
  path.join(uiSrc, 'styles', 'icons.css'),
  path.join(uiSrc, 'styles', 'discord-settings.css'),
]
fs.writeFileSync(
  path.join(distDir, 'settings.bundle.css'),
  settingsCssFiles.filter(f => fs.existsSync(f)).map(f => `/* ${path.basename(f)} */\n${fs.readFileSync(f, 'utf8')}`).join('\n\n'),
)

for (const f of ['mouse.bundle.js', 'mouse.bundle.css', 'settings.bundle.js', 'settings.bundle.css']) {
  const sz = fs.statSync(path.join(distDir, f)).size
  console.log(`${f}: ${(sz / 1024).toFixed(1)} KB`)
}
