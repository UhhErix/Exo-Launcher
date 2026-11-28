import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { DISCORD_SETTINGS, SETTINGS_PAGES, SETTINGS_SEARCH, discordPageOf, settingsPageOf, type SettingsSectionId } from '../src/lib/settingsSections.ts'
import { searchSettings } from '../src/lib/settingsIndex.ts'

const SRC = join(import.meta.dirname, '..', 'src')
const read = (rel: string) => readFileSync(join(SRC, rel), 'utf8')
// Every file that draws a Settings page's sections (a page is drawn by SettingsPanel and the components it lists).
const PANEL_FILES = ['components/SettingsPanel.tsx', 'components/settings/GeneralSection.tsx', 'components/settings/AboutSection.tsx', 'components/settings/StoresSection.tsx', 'components/settings/AppsSection.tsx', 'components/settings/PrivacySection.tsx', 'components/settings/AccountSection.tsx', 'components/shaders/ShaderTools.tsx']
const panelSource = PANEL_FILES.map(read).join('\n')

test('the sidebar lists the six pages with Account and Privacy inside About', () => {
  assert.deepEqual(SETTINGS_PAGES.map(page => page.page), ['general', 'library', 'downloads', 'ingame', 'music', 'about'])
  assert.deepEqual(SETTINGS_PAGES.map(page => page.label), ['General', 'Library', 'Downloads', 'In game', 'Music', 'About'])
})

test('every section is named once, and belongs to the page that lists it', () => {
  const owners = new Map<string, string>()
  for (const page of SETTINGS_PAGES) {
    for (const section of page.sections) {
      assert.equal(owners.has(section), false, `${section} is listed on two pages`)
      owners.set(section, page.page)
      assert.equal(settingsPageOf(section as SettingsSectionId), page.page, `${section} opens ${settingsPageOf(section as SettingsSectionId)}, not ${page.page}`)
    }
  }
  for (const page of SETTINGS_PAGES) assert.equal(settingsPageOf(page.page), page.page)
})

test('every deep link opens the page that holds its rows', () => {
  // The links the app opens Settings with: the Library's Sign in to a store, Music's service, Discord's gear.
  const links: [SettingsSectionId, string][] = [
    ['accounts', 'library'],
    ['music', 'music'],
    ['account', 'about'],
    ['updates', 'about'],
    ['downloads', 'downloads'],
    ['achievements', 'ingame'],
    ['fps', 'ingame'],
    ['privacy', 'about'],
    ['about', 'about'],
  ]
  for (const [section, page] of links) assert.equal(settingsPageOf(section), page, section)
  assert.equal(settingsPageOf('nope' as SettingsSectionId), 'general', 'an unknown section falls back to General')
})

test('every section a link can open at is drawn with that id', () => {
  for (const page of SETTINGS_PAGES) {
    for (const section of page.sections) {
      assert.match(panelSource, new RegExp(`<Section[^>]*id="${section}"`), `${section} has no <Section id="${section}">`)
    }
  }
})

test('Ctrl+K walks every page and Discord page, and each opens the right place', () => {
  assert.equal(SETTINGS_SEARCH.length, SETTINGS_PAGES.length + DISCORD_SETTINGS.length)
  for (const entry of SETTINGS_SEARCH) {
    if (entry.opens.discord) {
      // A Discord entry opens the sheet over the Discord room at its page: it names no Exo Settings page.
      assert.equal(discordPageOf(entry.opens.discord).id, entry.opens.discord)
    } else {
      assert.equal(settingsPageOf(entry.opens.id), entry.opens.id, entry.id)
    }
  }
})

test('every row keeps a Ctrl+K keyword, so search still finds it', () => {
  const keywords = SETTINGS_PAGES.flatMap(page => [...page.keywords]).map(word => word.toLowerCase())
  // Rows are the labels of the row components of the files that draw Exo's own pages (spinners and choices are not rows).
  const labels = ['GeneralSection.tsx', 'PrivacySection.tsx', 'AboutSection.tsx'].flatMap(name => {
    const text = read(`components/settings/${name}`)
    return [...text.matchAll(/<(?:Row|SwitchRow|ChoiceRow|SliderRow|CopyRow)[^>]*?label="([^"]+)"/g)].map(match => match[1])
  })
  assert.ok(labels.length > 14, 'the label scan found the rows: ' + labels.length)
  for (const label of labels) {
    const lower = label.toLowerCase()
    assert.ok(keywords.some(word => lower.includes(word)), `no Ctrl+K keyword covers the row "${label}"`)
  }
})

test('SettingsPanel is a full-page searchable room without exo-sheet-glass and searchSettings indexes rows', () => {
  const settingsPanel = read('components/SettingsPanel.tsx')
  assert.match(settingsPanel, /Search settings/)
  assert.doesNotMatch(settingsPanel, /exo-sheet-glass/)
  assert.deepEqual(searchSettings(''), [])
  assert.ok(searchSettings('riot hours').some(hit => hit.page === 'library' && hit.label === 'Riot hours'))
  assert.ok(searchSettings('fps').some(hit => hit.page === 'ingame' && hit.label === 'Show FPS counter'))
  assert.ok(searchSettings('backup').some(hit => hit.page === 'general' && hit.sectionId === 'backups'))
})
