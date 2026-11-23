// Exo's markup rules for Exo Mouse (CSS: scripts/css-design.mjs). Types are tsc's job.
import path from 'node:path'
import oxlint from 'eslint-plugin-oxlint'
import tseslint from 'typescript-eslint'

const STORAGES = ['localStorage', 'sessionStorage', 'indexedDB']
const FRAME_STORAGE = "This frame's own storage is lost when its origin is cleared and is never backed up: what the player chooses goes to Exo through the host (saved.ts); a convenience goes through frameStore.ts, under a key named there with why it may be lost."

export default tseslint.config({
  files: ['src/**/*.{ts,tsx}'],
  linterOptions: { reportUnusedDisableDirectives: 'off' },
  languageOptions: { parser: tseslint.parser },
  rules: {
    'no-restricted-syntax': ['error',
      {
        // Lowercase only: a component's own `title` prop (Panel, Centered) is a heading, not a tooltip.
        selector: "JSXOpeningElement[name.name=/^[a-z]/] > JSXAttribute[name.name='title']",
        message: 'No native title= tooltips. Name the control with aria-label.',
      },
      {
        selector: "JSXAttribute[name.name='style'] Property[key.name='fontSize']",
        message: 'Font size lives in mouse.css on the type scale (--type-title / --type-body / --type-small), not inline.',
      },
    ],
  },
}, {
  files: ['src/**/*.{ts,tsx}'],
  ignores: ['src/frameStore.ts'],
  languageOptions: { parser: tseslint.parser },
  rules: {
    'no-restricted-globals': ['error', ...STORAGES.map(name => ({ name, message: FRAME_STORAGE }))],
    'no-restricted-properties': ['error', ...['window', 'globalThis', 'self'].flatMap(object => STORAGES.map(property => ({ object, property, message: FRAME_STORAGE })))],
  },
}, ...oxlint.buildFromOxlintConfigFile(path.resolve(import.meta.dirname, '.oxlintrc.json')))
