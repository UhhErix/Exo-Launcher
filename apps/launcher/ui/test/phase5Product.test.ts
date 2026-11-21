// Regression tests for Phase 5.2 product polish: vendor BIOS hints, opt-in startup toggle and error handling
// in onboarding, BoardRows BIOS paths for disabled Secure Boot / missing TPM, and achievement preview errors.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { biosVendorHints } from '../src/lib/biosHints.ts'

const pcStepSource = readFileSync(new URL('../src/components/onboarding/PcStep.tsx', import.meta.url), 'utf8')
const onboardingSource = readFileSync(new URL('../src/components/OnboardingPanel.tsx', import.meta.url), 'utf8')
const pcOverviewSource = readFileSync(new URL('../src/components/gpu/PcOverview.tsx', import.meta.url), 'utf8')
const generalSectionSource = readFileSync(new URL('../src/components/settings/GeneralSection.tsx', import.meta.url), 'utf8')

test('biosVendorHints matches ASUS, MSI, Gigabyte, and ASRock motherboards and returns null otherwise', () => {
  assert.equal(biosVendorHints('ASUSTeK COMPUTER INC. ROG STRIX B650E-F')?.vendor, 'ASUS')
  assert.match(biosVendorHints('ASUS TUF GAMING')?.secureBoot ?? '', /Boot → Secure Boot/)

  assert.equal(biosVendorHints('Micro-Star International Co., Ltd. MS-7D75')?.vendor, 'MSI')
  assert.match(biosVendorHints('MSI MAG B650')?.tpm ?? '', /Trusted Computing/)

  assert.equal(biosVendorHints('Gigabyte Technology Co., Ltd. B650 AORUS ELITE')?.vendor, 'Gigabyte')
  assert.match(biosVendorHints('Gigabyte X670')?.xmp ?? '', /Tweaker → Extreme Memory Profile/)

  assert.equal(biosVendorHints('ASRock B650M Pro RS')?.vendor, 'ASRock')
  assert.match(biosVendorHints('ASRock X670E')?.rebar ?? '', /C\.A\.M\./)

  assert.equal(biosVendorHints(null), null)
  assert.equal(biosVendorHints(undefined), null)
  assert.equal(biosVendorHints('Custom OEM Board'), null)
})

test('Onboarding PcStep exposes opt-in StartWithWindows and OnboardingPanel saveSettings catches failures', () => {
  assert.match(pcStepSource, /id="ob-start-with-windows"/)
  assert.match(pcStepSource, /checked=\{settings\.startWithWindows === true\}/)
  assert.match(pcStepSource, /onSave\(\{ startWithWindows: on \}\)/)
  assert.match(onboardingSource, /catch\s*\(error\)\s*\{[\s\S]*report\(error,\s*'OnboardingPanel\.saveSettings'\)[\s\S]*return false/)
})

test('BoardRows shows BIOS paths when Secure Boot is off or TPM is None, and AchievementRows surfaces previewError', () => {
  assert.match(pcOverviewSource, /biosVendorHints\(about\?\.motherboard\)/)
  assert.match(pcOverviewSource, /about\.secureBoot === false && hints \? `BIOS: \$\{hints\.secureBoot\}` : undefined/)
  assert.match(pcOverviewSource, /about\.tpm === 'None' && hints \? `BIOS: \$\{hints\.tpm\}` : undefined/)

  assert.match(generalSectionSource, /const \[previewError, setPreviewError\] = useState<string \| null>\(null\)/)
  assert.match(generalSectionSource, /setPreviewError\(errorText\(error,\s*'Could not show the preview banner\.'\)\)/)
  assert.match(generalSectionSource, /<Row label="Test pop-up" error=\{previewError\}>/)
})
