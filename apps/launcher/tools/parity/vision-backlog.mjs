import { readFile, writeFile } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const LAUNCHER_ROOT = fileURLToPath(new URL('../..', import.meta.url))

const WP_ORDER = [
  'WP1',
  'WP2',
  'WP3',
  'WP4',
  'WP5',
  'WP6/WP7',
  'WP8',
  'WP9',
  'WP10',
  'Rune',
]

const WP_META = {
  WP1: {
    title: 'WP1 — Game page and game actions',
    owners: [
      'native-ui/crates/exo-ui/src/game_page.rs',
      'native-ui/crates/exo-ui/src/game_perf.rs',
      'native-ui/crates/exo-ui/src/game_props.rs',
      'native-ui/crates/exo-ui/src/game_actions.rs',
    ],
    branch: 'origin/UhhErix/native-WP1 (97717ef, merged into origin/feat/native-polish-2 @ 79eae6e)',
  },
  WP2: {
    title: 'WP2 — Library',
    owners: [
      'native-ui/crates/exo-ui/src/library.rs',
      'native-ui/crates/exo-ui/src/menus.rs',
      'native-ui/crates/exo-ui/src/folders.rs',
      'native-ui/crates/exo-ui/src/select_bar.rs',
      'native-ui/crates/exo-ui/src/gifts.rs',
    ],
    branch: 'origin/UhhErix/native-WP2 (1d5f073)',
  },
  WP3: {
    title: 'WP3 — Shell chrome, Bar, Palette, Tour, Activate Key & Notices',
    owners: [
      'native-ui/crates/exo-ui/src/shell.rs',
      'native-ui/crates/exo-ui/src/palette.rs',
      'native-ui/crates/exo-ui/src/key_modal.rs',
      'native-ui/crates/exo-ui/src/notice.rs',
      'native-ui/crates/exo-ui/src/setup.rs',
      'native-ui/crates/exo-ui/src/gamepad.rs',
    ],
    branch: 'origin/UhhErix/native-WP3 (c467c14)',
  },
  WP4: {
    title: 'WP4 — Accounts, Settings, Privacy & Onboarding',
    owners: [
      'native-ui/crates/exo-ui/src/settings.rs',
      'native-ui/crates/exo-ui/src/settings_state.rs',
      'native-ui/crates/exo-ui/src/settings_stores.rs',
      'native-ui/crates/exo-ui/src/settings_account.rs',
      'native-ui/crates/exo-ui/src/settings_privacy.rs',
      'native-ui/crates/exo-ui/src/report.rs',
      'native-ui/crates/exo-ui/src/setup.rs',
    ],
    branch: 'origin/UhhErix/native-WP4 (637d5f2)',
  },
  WP5: {
    title: 'WP5 — Store',
    owners: [
      'native-ui/crates/exo-ui/src/store.rs',
      'native-ui/crates/exo-ui/src/store_page.rs',
      'native-ui/crates/exo-ui/src/store_media.rs',
    ],
    branch: 'origin/UhhErix/native-WP5 (d7c4b63, merged into origin/feat/native-polish-2 @ 9820672)',
  },
  'WP6/WP7': {
    title: 'WP6/WP7 — Discord (chat, voice, settings)',
    owners: [
      'native-ui/crates/exo-ui/src/discord.rs',
      'native-ui/crates/exo-ui/src/discord_state.rs',
      'native-ui/crates/exo-ui/src/discord_chat.rs',
      'native-ui/crates/exo-ui/src/discord_chat_state.rs',
      'native-ui/crates/exo-ui/src/discord_md.rs',
      'native-ui/crates/exo-ui/src/discord_voice.rs',
      'native-ui/crates/exo-ui/src/discord_voice_state.rs',
    ],
    branch: 'origin/UhhErix/native-WP6 (8fe6185), origin/UhhErix/native-WP7 (3b95a54)',
  },
  WP8: {
    title: 'WP8 — Music',
    owners: [
      'native-ui/crates/exo-ui/src/music.rs',
      'native-ui/crates/exo-ui/src/media_state.rs',
    ],
    branch: 'none pushed to origin yet',
  },
  WP9: {
    title: 'WP9 — PC room, Exo AI, Storage & Apps',
    owners: [
      'native-ui/crates/exo-ui/src/pc.rs',
      'native-ui/crates/exo-ui/src/pc_ai.rs',
      'native-ui/crates/exo-ui/src/pc_apps.rs',
      'native-ui/crates/exo-ui/src/pc_storage.rs',
      'native-ui/crates/exo-ui/src/pc_menus.rs',
      'native-ui/crates/exo-ui/src/pc_actions.rs',
      'native-ui/crates/exo-ui/src/pc_state.rs',
    ],
    branch: 'origin/UhhErix/native-WP9 (6bda970)',
  },
  WP10: {
    title: 'WP10 — Devices, Captures and Notes',
    owners: [
      'native-ui/crates/exo-ui/src/pc_devices.rs',
      'native-ui/crates/exo-ui/src/devices_audio.rs',
      'native-ui/crates/exo-ui/src/devices_controller.rs',
      'native-ui/crates/exo-ui/src/captures.rs',
      'native-ui/crates/exo-ui/src/video_decoder.rs',
    ],
    branch: 'origin/UhhErix/native-WP10 (eb8b193)',
  },
  Rune: {
    title: 'Rune — Shared component, glass material, icon & type gaps',
    owners: [
      'native-ui/crates/exo-kit/src/components.rs',
      'native-ui/crates/exo-kit/src/tokens.rs',
      'native-ui/crates/exo-kit/src/icons.rs',
      'native-ui/crates/exo-kit/src/menu.rs',
      'native-ui/crates/exo-kit/src/select.rs',
    ],
    branch: 'UhhErix/rune fork + exo-kit (no open visual-parity PR)',
  },
}

const SEV_RANK = { blocker: 0, major: 1, minor: 2 }
const STATUS_RANK = { open_gap: 0, partial_on_branch: 1, fixed_on_branch: 2 }

export function scrubPii(text) {
  return String(text ?? '')
    .replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, '<redacted-email>')
    .replace(/C:\\Users\\[^\\/\s"']+/gi, 'C:\\Users\\<user>')
}

function isTopBarFinding(finding) {
  const hay = `${finding.area} ${finding.react} ${finding.native} ${finding.fix_hint}`.toLowerCase()
  const topY = Array.isArray(finding.bbox) && finding.bbox[1] <= 56 && finding.bbox[3] <= 80
  return (
    topY ||
    /\b(top[\s-]?(bar|nav|chrome|navigation|toolbar|right\s+toolbar|left\s+search)|title[\s-]?bar|window\s+controls|green\s+(status\s+)?dot\s+on\s+the\s+(capture|camera|screenshot)|navigation\s+pill|nav\s+capsule)\b/i.test(
      hay,
    )
  )
}

function isRuneComponentGap(item, finding) {
  const hay = `${finding.area} ${finding.react} ${finding.native} ${finding.fix_hint}`.toLowerCase()
  const area = String(finding.area || '').toLowerCase()
  if (
    /\b(liquid\s+glass|backdrop[\s-]?blur|glass\s+material|glass\s+surface|backdrop\s+scrim|backdrop\s+dimming)\b/i.test(
      area,
    ) ||
    (finding.severity !== 'blocker' &&
      /\b(liquid\s+glass|backdrop[\s-]?blur|glass\s+material|glass\s+surface|backdrop\s+scrim|backdrop\s+dimming)\b/i.test(
        hay,
      ))
  ) {
    return {
      wp: 'Rune',
      owners: ['native-ui/crates/exo-kit/src/components.rs', 'native-ui/crates/exo-kit/src/tokens.rs'],
    }
  }
  if (
    /\b(lucide|1\.5px\s+stroke|stroke\s+width|sparkle\s+lucide|square-pen\s+icon|gamepad\s+icon\s+variant|icon\s+stroke)\b/i.test(
      hay,
    ) &&
    finding.severity !== 'blocker'
  ) {
    return {
      wp: 'Rune',
      owners: ['native-ui/crates/exo-kit/src/icons.rs'],
    }
  }
  if (
    /\b(geist|goeist|font\s+metrics|letter-spacing|tracking|unintended\s+(text\s+)?underlines?|focus\s+ring\s+stroke)\b/i.test(
      hay,
    ) &&
    finding.severity !== 'blocker'
  ) {
    return {
      wp: 'Rune',
      owners: ['native-ui/crates/exo-kit/src/tokens.rs', 'native-ui/crates/exo-kit/src/components.rs'],
    }
  }
  return null
}

function classifyFinding(item, finding) {
  const runeHit = isRuneComponentGap(item, finding)
  if (runeHit) return runeHit

  if (item.room !== 'shell' && isTopBarFinding(finding)) {
    return { wp: 'WP3', owners: ['native-ui/crates/exo-ui/src/shell.rs'] }
  }

  switch (item.room) {
    case 'game':
      return {
        wp: 'WP1',
        owners: ['native-ui/crates/exo-ui/src/game_page.rs', 'native-ui/crates/exo-ui/src/game_perf.rs'],
      }
    case 'library':
      if (item.id === 'library-capture-click' && /capture|popover/i.test(finding.area)) {
        return { wp: 'WP10', owners: ['native-ui/crates/exo-ui/src/captures.rs'] }
      }
      if (/menu|dropdown|popover/i.test(item.kind)) {
        return {
          wp: 'WP2',
          owners: ['native-ui/crates/exo-ui/src/library.rs', 'native-ui/crates/exo-ui/src/menus.rs'],
        }
      }
      return { wp: 'WP2', owners: ['native-ui/crates/exo-ui/src/library.rs'] }
    case 'shell':
      if (item.id.startsWith('shell-palette')) {
        return { wp: 'WP3', owners: ['native-ui/crates/exo-ui/src/palette.rs', 'native-ui/crates/exo-ui/src/shell.rs'] }
      }
      if (item.id === 'shell-activate-key-modal') {
        return { wp: 'WP3', owners: ['native-ui/crates/exo-ui/src/key_modal.rs'] }
      }
      if (item.id === 'shell-tour-modal') {
        return { wp: 'WP3', owners: ['native-ui/crates/exo-ui/src/setup.rs'] }
      }
      return { wp: 'WP3', owners: ['native-ui/crates/exo-ui/src/shell.rs'] }
    case 'settings':
      return { wp: 'WP4', owners: ['native-ui/crates/exo-ui/src/settings.rs'] }
    case 'store':
      if (item.id.startsWith('store-game')) {
        return {
          wp: 'WP5',
          owners: [
            'native-ui/crates/exo-ui/src/store.rs',
            'native-ui/crates/exo-ui/src/store_page.rs',
            'native-ui/crates/exo-ui/src/store_media.rs',
          ],
        }
      }
      return { wp: 'WP5', owners: ['native-ui/crates/exo-ui/src/store.rs'] }
    case 'discord':
      if (item.id === 'discord-settings-sheet') {
        return { wp: 'WP6/WP7', owners: ['native-ui/crates/exo-ui/src/discord.rs', 'native-ui/crates/exo-ui/src/discord_state.rs'] }
      }
      if (/markdown|embed|link|mention/i.test(`${finding.area} ${finding.native}`)) {
        return { wp: 'WP6/WP7', owners: ['native-ui/crates/exo-ui/src/discord_md.rs', 'native-ui/crates/exo-ui/src/discord_chat.rs'] }
      }
      return {
        wp: 'WP6/WP7',
        owners: ['native-ui/crates/exo-ui/src/discord.rs', 'native-ui/crates/exo-ui/src/discord_chat.rs'],
      }
    case 'music':
      return { wp: 'WP8', owners: ['native-ui/crates/exo-ui/src/music.rs', 'native-ui/crates/exo-ui/src/media_state.rs'] }
    case 'pc':
      if (item.id === 'pc-storage') {
        return { wp: 'WP9', owners: ['native-ui/crates/exo-ui/src/pc_storage.rs'] }
      }
      if (item.id === 'pc-display-popover') {
        return { wp: 'WP9', owners: ['native-ui/crates/exo-ui/src/pc_menus.rs', 'native-ui/crates/exo-ui/src/pc_ai.rs'] }
      }
      return { wp: 'WP9', owners: ['native-ui/crates/exo-ui/src/pc_ai.rs', 'native-ui/crates/exo-ui/src/pc.rs'] }
    case 'apps':
      return { wp: 'WP9', owners: ['native-ui/crates/exo-ui/src/pc_apps.rs'] }
    case 'devices':
      return { wp: 'WP10', owners: ['native-ui/crates/exo-ui/src/pc_devices.rs'] }
    case 'captures':
      return { wp: 'WP10', owners: ['native-ui/crates/exo-ui/src/captures.rs'] }
    default:
      return { wp: 'WP3', owners: ['native-ui/crates/exo-ui/src/shell.rs'] }
  }
}

function evaluateBranchStatus(item, finding, wp) {
  const hay = `${finding.area} ${finding.react} ${finding.native} ${finding.fix_hint}`.toLowerCase()

  if (wp === 'WP5') {
    if (
      item.id === 'store-game-page' ||
      item.id === 'store-game-media-tab' ||
      item.id === 'store-game-requirements' ||
      (item.id === 'store-search' && /search\s+results|discover\s+landing/i.test(hay))
    ) {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP5 (d7c4b63, merged @ 9820672)',
        branchNote: 'WP5 rebuilt store_page.rs + store_media.rs (hero/trailer/tags/BuyPanel/Facts/7-row System requirements, formatted Historical low, store names) and fixed store-search.',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP5 (d7c4b63, merged @ 9820672)',
      branchNote: 'WP5 modified store.rs (wishlist/search/discover); re-verify visual alignment & card sort order post-merge.',
    }
  }

  if (wp === 'WP1') {
    if (/performance|display\s+mode|properties|skeleton|upscaler|frame\s+gen|hdr/i.test(hay)) {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP1 (97717ef, merged @ 79eae6e)',
        branchNote: 'WP1 replaced hardcoded game_page.rs perf/props with live game_perf.rs + game_props.rs + game_actions.rs.',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP1 (97717ef, merged @ 79eae6e)',
      branchNote: 'WP1 updated game_page.rs + states.mjs; verify game-page fixture selection and button hover styling post-merge.',
    }
  }

  if (wp === 'WP4') {
    if (
      /sidebar|shifted|left\s+edge|left\s+rail|x=350|x≈350|dock\s+the\s+settings\s+sidebar/i.test(hay)
    ) {
      return {
        branchStatus: 'open_gap',
        branchRef: 'origin/UhhErix/native-WP4 (637d5f2)',
        branchNote: 'WP4 adds all Settings sections/controls, but sidebar anchoring (x≈350px vs left rail x=16px) still needs explicit visual check/fix in settings.rs.',
      }
    }
    if (
      /game\s+stores|install\s+folder|downloads|achievements|fps\s+counter|account|security|sessions|privacy|your\s+data|check\s+for\s+updates|clear\s+caches|missing-sections|missing\s+the\s+'downloads'/i.test(
        hay,
      )
    ) {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP4 (637d5f2)',
        branchNote: 'WP4 implements Settings Library/Stores, Downloads, Achievements, Account, Security, Sessions, Backups, Privacy, and About cards (FPS counter + web prefs wired by WP9).',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP4 (637d5f2)',
      branchNote: 'WP4 rewrote settings.rs; re-verify section routing, spacing, and dropdown polish post-merge.',
    }
  }

  if (wp === 'WP9') {
    if (
      /compress\s+games|display\s+popover|junk\s+cleanup|uninstall\s+buttons|starts\s+with\s+windows|turned\s+off|toggle\s+switch|auto\s+tune|shaders|updates\s+card/i.test(
        hay,
      )
    ) {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP9 (6bda970)',
        branchNote: 'WP9 implements Compress games in pc_storage.rs, full Display/Graphics/Board popovers in pc_menus.rs, real Auto Tune + Shaders in pc_ai.rs, and Apps junk/uninstall/startup in pc_apps.rs.',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP9 (6bda970)',
      branchNote: 'WP9 refactored pc_ai.rs, pc_storage.rs, and pc_apps.rs; re-verify visual alignment, CTA button styling, and header stacking post-merge.',
    }
  }

  if (wp === 'WP3') {
    if (item.id === 'shell-activate-key-modal' && /platform\s+selector|steam|epic|gog|copy\s+key|footer/i.test(hay)) {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP3 (c467c14)',
        branchNote: 'WP3 updated key_modal.rs with Steam/Epic/GOG chips and Copy key footer action.',
      }
    }
    if (item.id === 'shell-tour-modal') {
      return {
        branchStatus: 'open_gap',
        branchRef: 'none (setup.rs not modified by WP3)',
        branchNote: 'Tour modal mismatch (centered Welcome 1/9 modal with backdrop scrim vs top-left 3-step coachmark) is NOT touched on WP3.',
      }
    }
    if (
      /green\s+(status\s+)?dot|top\s+navigation\s+pill|center\s+the\s+top\s+nav|global\s+search\s+input|top-left\s+search|press-search|search\s+query\s+'a'|background\s+(page|room)|renders\s+the\s+settings\s+screen/i.test(
        hay,
      )
    ) {
      return {
        branchStatus: 'open_gap',
        branchRef: 'origin/UhhErix/native-WP3 (c467c14)',
        branchNote: 'Shell visual/state gap (top-bar pill width/centering, spurious green capture dot, missing top-left Search on Settings/Apps, palette query/background room) not confirmed fixed in WP3 diff.',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP3 (c467c14)',
      branchNote: 'WP3 touched shell.rs/palette.rs/key_modal.rs; verify visual spacing/alignment post-merge.',
    }
  }

  if (wp === 'WP2') {
    if (item.id === 'library-all-filter-installed' && /popover|filter\s+chip|clipping|300px/i.test(hay)) {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP2 (1d5f073)',
        branchNote: 'WP2 widened the Library filter popover to 420px flex-wrap matching React.',
      }
    }
    if (/cover\s+art|blank\s+dark|uninstalled\s+game\s+cards|last\s+launched|installed\s+first/i.test(hay)) {
      return {
        branchStatus: 'open_gap',
        branchRef: 'origin/UhhErix/native-WP2 (1d5f073)',
        branchNote: 'Uninstalled card cover art loading in All Games grid and default sort label (Installed first vs Last launched) are real gaps to verify/fix.',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP2 (1d5f073)',
      branchNote: 'WP2 modified library.rs + menus.rs; re-verify card geometry, hover/press states, and dropdown alignment post-merge.',
    }
  }

  if (wp === 'WP6/WP7') {
    if (item.id === 'discord-settings-sheet') {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP7 (3b95a54)',
        branchNote: 'WP7 implements discord_settings.rs (Account, Voice, App, Privacy, Keybinds).',
      }
    }
    if (item.id === 'discord-voice-call' || item.id === 'discord-voice-stream') {
      return {
        branchStatus: 'partial_on_branch',
        branchRef: 'origin/UhhErix/native-WP7 (3b95a54)',
        branchNote: 'WP7 updated discord_voice.rs / discord_voice_state.rs; re-verify voice stage and Go Live stream picker post-merge.',
      }
    }
    if (/raw\s+unparsed\s+markdown|channel\s+mention/i.test(hay)) {
      return {
        branchStatus: 'open_gap',
        branchRef: 'origin/UhhErix/native-WP6 (8fe6185)',
        branchNote: 'Verify raw markdown link / channel mention parsing in discord_md.rs on WP6 post-merge.',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP6 (8fe6185), origin/UhhErix/native-WP7 (3b95a54)',
      branchNote: 'WP6/WP7 heavily updated discord.rs, discord_chat.rs, and discord_voice.rs; re-verify post-merge.',
    }
  }

  if (wp === 'WP8') {
    return {
      branchStatus: 'open_gap',
      branchRef: 'none (no origin/UhhErix/native-WP8 branch)',
      branchNote: 'Open Music parity gap; no WP8 branch pushed to origin yet.',
    }
  }

  if (wp === 'WP10') {
    if (item.id === 'devices-keyboard') {
      return {
        branchStatus: 'open_gap',
        branchRef: 'origin/UhhErix/native-WP10 (eb8b193)',
        branchNote: 'Wooting 60HE v2 3-column keyboard dashboard gap is outside WP10 audio/controller/captures scope.',
      }
    }
    if (
      item.id === 'devices-audio' ||
      item.id === 'devices-controller' ||
      item.id === 'shell-capture-sheet' ||
      item.id === 'shell-capture-settings'
    ) {
      return {
        branchStatus: 'fixed_on_branch',
        branchRef: 'origin/UhhErix/native-WP10 (eb8b193)',
        branchNote: 'WP10 implements devices_audio.rs, devices_controller.rs, captures.rs, and notes.rs; re-verify post-merge.',
      }
    }
    return {
      branchStatus: 'partial_on_branch',
      branchRef: 'origin/UhhErix/native-WP10 (eb8b193)',
      branchNote: 'WP10 updated Devices & Captures; re-verify mouse embed and capture popover post-merge.',
    }
  }

  return {
    branchStatus: 'open_gap',
    branchRef: 'none (exo-kit / UhhErix/rune)',
    branchNote: 'Shared Rune / exo-kit visual material, icon stroke, or typography gap.',
  }
}

export async function generateVisionBacklog({
  resultsPath = join(LAUNCHER_ROOT, 'parity-report', 'vision-qa', 'results.json'),
  outJsonPath = join(LAUNCHER_ROOT, 'improve', 'vision-parity.json'),
  outMdPath = join(LAUNCHER_ROOT, 'improve', 'vision-parity.md'),
  testedCommit = '526b2df',
  totalSpendUsd = 3.1412,
  totalApiCalls = 71,
  followupSpendUsd = 0.7366,
} = {}) {
  const report = JSON.parse(await readFile(resultsPath, 'utf8'))
  const entries = []
  const skippedStates = []

  for (const item of report.items || []) {
    const v = item.vision
    if (!v || v.status === 'skipped') {
      skippedStates.push({
        stateId: item.id,
        room: item.room,
        kind: item.kind,
        reason: v?.reason || 'skipped',
        openedReact: Boolean(item.checks?.opened?.react),
        openedNative: Boolean(item.checks?.opened?.native),
      })
      continue
    }
    const findings = v.findings || []
    for (let i = 0; i < findings.length; i++) {
      const f = findings[i]
      const { wp, owners } = classifyFinding(item, f)
      const branchInfo = evaluateBranchStatus(item, f, wp)
      const cropPath = f.crop
        ? `apps/launcher/parity-report/vision-qa/${f.crop}`
        : `apps/launcher/parity-report/vision-qa/${item.checks?.pixels?.sheet || item.nativeShot || ''}`
      entries.push({
        id: `${item.id}#${String(i + 1).padStart(2, '0')}`,
        stateId: item.id,
        stateName: item.name,
        room: item.room,
        kind: item.kind,
        wp,
        owners,
        severity: f.severity,
        area: scrubPii(f.area),
        react: scrubPii(f.react),
        native: scrubPii(f.native),
        fix_hint: scrubPii(f.fix_hint),
        bbox: f.bbox,
        cropPath,
        compareSheet: item.checks?.pixels?.sheet
          ? `apps/launcher/parity-report/vision-qa/${item.checks.pixels.sheet}`
          : null,
        pixelDiff: {
          meanAbsDiff: item.checks?.pixels?.meanAbsDiff ?? null,
          maxCellDiff: item.checks?.pixels?.maxCellDiff ?? null,
          status: item.checks?.pixels?.status || 'na',
        },
        opened: {
          react: Boolean(item.checks?.opened?.react),
          native: Boolean(item.checks?.opened?.native),
        },
        ...branchInfo,
      })
    }
  }

  const sortEntries = (list) =>
    [...list].sort((a, b) => {
      const sr = (SEV_RANK[a.severity] ?? 9) - (SEV_RANK[b.severity] ?? 9)
      if (sr !== 0) return sr
      const br = (STATUS_RANK[a.branchStatus] ?? 9) - (STATUS_RANK[b.branchStatus] ?? 9)
      if (br !== 0) return br
      return (b.pixelDiff.meanAbsDiff || 0) - (a.pixelDiff.meanAbsDiff || 0)
    })

  const byWp = {}
  const wpSummary = []
  for (const wp of WP_ORDER) {
    const list = sortEntries(entries.filter((e) => e.wp === wp))
    byWp[wp] = list
    const count = (pred) => list.filter(pred).length
    wpSummary.push({
      wp,
      title: WP_META[wp].title,
      owners: WP_META[wp].owners,
      branch: WP_META[wp].branch,
      total: list.length,
      blocker: count((e) => e.severity === 'blocker'),
      major: count((e) => e.severity === 'major'),
      minor: count((e) => e.severity === 'minor'),
      openGap: count((e) => e.branchStatus === 'open_gap'),
      openBlockers: count((e) => e.severity === 'blocker' && e.branchStatus === 'open_gap'),
      openMajors: count((e) => e.severity === 'major' && e.branchStatus === 'open_gap'),
      partialOnBranch: count((e) => e.branchStatus === 'partial_on_branch'),
      fixedOnBranch: count((e) => e.branchStatus === 'fixed_on_branch'),
    })
  }

  const totals = {
    statesTotal: (report.items || []).length,
    statesAnalyzed: (report.items || []).length - skippedStates.length,
    statesSkipped: skippedStates.length,
    findingsTotal: entries.length,
    blocker: entries.filter((e) => e.severity === 'blocker').length,
    major: entries.filter((e) => e.severity === 'major').length,
    minor: entries.filter((e) => e.severity === 'minor').length,
    openGap: entries.filter((e) => e.branchStatus === 'open_gap').length,
    openBlockers: entries.filter((e) => e.severity === 'blocker' && e.branchStatus === 'open_gap').length,
    openMajors: entries.filter((e) => e.severity === 'major' && e.branchStatus === 'open_gap').length,
    partialOnBranch: entries.filter((e) => e.branchStatus === 'partial_on_branch').length,
    fixedOnBranch: entries.filter((e) => e.branchStatus === 'fixed_on_branch').length,
  }

  const topOpenBlockers = sortEntries(
    entries.filter((e) => e.severity === 'blocker' && e.branchStatus === 'open_gap'),
  ).slice(0, 15)

  const jsonPayload = {
    generatedAt: new Date().toISOString(),
    testedCommit,
    baseBranch: 'origin/feat/native-polish-2',
    model: report.visionSummary?.model || 'gemini-4-hydrogen',
    budgetUsd: 15.0,
    totalSpendUsd: Number(totalSpendUsd.toFixed(4)),
    followupSpendUsd: Number(followupSpendUsd.toFixed(4)),
    totalApiCalls,
    totals,
    wpSummary,
    topOpenBlockers,
    skippedStates,
    workPackages: byWp,
  }

  await writeFile(outJsonPath, JSON.stringify(jsonPayload, null, 2))
  await writeFile(outMdPath, renderBacklogMarkdown(jsonPayload))
  return jsonPayload
}

const mdCell = (s) => String(s ?? '').replaceAll('|', '\\|').replace(/\r?\n/g, ' ').trim()

function renderBacklogMarkdown(data) {
  const lines = []
  lines.push('# Native UI Vision Parity Backlog (Gemini QA)')
  lines.push('')
  lines.push(`- **Generated:** ${data.generatedAt}`)
  lines.push(`- **Tested commit:** \`${data.testedCommit}\` (\`origin/feat/native-polish-2\` base before WP1/WP5 merge)`)
  lines.push(`- **Model:** \`${data.model}\` via \`gemini-api analyze\` (1920x1080 React + Native + 2x2 overlay/diff sheet)`)
  lines.push(`- **Coverage:** **${data.totals.statesAnalyzed} / ${data.totals.statesTotal}** states in \`tools/parity/states.mjs\` analyzed (**${data.totals.statesSkipped}** skipped where React precondition was unmet in the test instance)`)
  lines.push(`- **Findings:** **${data.totals.blocker}** blockers, **${data.totals.major}** major, **${data.totals.minor}** minor (**${data.totals.findingsTotal}** total)`)
  lines.push(`- **Branch triage:** **${data.totals.openGap}** open gaps (**${data.totals.openBlockers}** open blockers, **${data.totals.openMajors}** open major) | **${data.totals.partialOnBranch}** partial/needs re-check on open WP branches | **${data.totals.fixedOnBranch}** already fixed on open/merged \`origin/UhhErix/native-WP*\` branches`)
  lines.push(`- **API spend:** **$${data.totalSpendUsd.toFixed(4)}** cumulative across ${data.totalApiCalls} calls (follow-up 18-state pass: **$${Number(data.followupSpendUsd || 0).toFixed(4)}** with 52/69 cached hits at $0.0000; budget cap: $${data.budgetUsd.toFixed(2)})`)
  lines.push('')
  lines.push('> Pixel metrics from `tools/parity/compare.mjs` remain ground truth. Vision adds structured descriptions, fix hints, and side-by-side crops (`apps/launcher/parity-report/vision-qa/crops/`, gitignored locally).')
  lines.push('')

  lines.push('## Summary by Work Package')
  lines.push('')
  lines.push('| WP | Total | Blockers (Open) | Major (Open) | Minor | Open Gaps | Fixed on WP Branch | Partial / Re-check | Open Branch Status |')
  lines.push('|---|---:|---:|---:|---:|---:|---:|---:|---|')
  for (const s of data.wpSummary) {
    lines.push(
      `| **${s.wp}** | ${s.total} | ${s.blocker} (**${s.openBlockers}**) | ${s.major} (**${s.openMajors}**) | ${s.minor} | **${s.openGap}** | ${s.fixedOnBranch} | ${s.partialOnBranch} | ${mdCell(s.branch)} |`,
    )
  }
  lines.push('')

  lines.push('## Top 15 Open Blockers (Not Yet Fixed on Open WP Branches)')
  lines.push('')
  lines.push('| # | WP | State | px diff | Area | Native vs React Gap | Owner | Crop |')
  lines.push('|---:|---|---|---:|---|---|---|---|')
  data.topOpenBlockers.forEach((e, i) => {
    lines.push(
      `| ${i + 1} | **${e.wp}** | \`${e.stateId}\` | ${e.pixelDiff.meanAbsDiff ?? 'n/a'} | ${mdCell(e.area)} | ${mdCell(e.native.slice(0, 140))} | \`${e.owners[0]}\` | \`${e.cropPath}\` |`,
    )
  })
  lines.push('')

  for (const s of data.wpSummary) {
    const list = data.workPackages[s.wp] || []
    lines.push(`## ${s.title}`)
    lines.push('')
    lines.push(`- **Owners:** ${s.owners.map((o) => `\`${o}\``).join(', ')}`)
    lines.push(`- **Open branch:** ${s.branch}`)
    lines.push(`- **Counts:** ${s.total} total (${s.blocker} blocker, ${s.major} major, ${s.minor} minor) — **${s.openGap} open gaps**, ${s.partialOnBranch} partial/re-check, ${s.fixedOnBranch} fixed on branch`)
    lines.push('')
    if (list.length === 0) {
      lines.push('No vision findings assigned to this work package.')
      lines.push('')
      continue
    }
    lines.push('| Sev | Status | State | px | Area | React | Native | Fix Hint | Crop |')
    lines.push('|---|---|---|---:|---|---|---|---|---|')
    for (const e of list) {
      const statusBadge =
        e.branchStatus === 'open_gap'
          ? '**OPEN**'
          : e.branchStatus === 'fixed_on_branch'
            ? 'fixed on branch'
            : 're-check branch'
      lines.push(
        `| **${e.severity}** | ${statusBadge} | \`${e.stateId}\` | ${e.pixelDiff.meanAbsDiff ?? 'n/a'} | ${mdCell(e.area)} | ${mdCell(e.react)} | ${mdCell(e.native)} | ${mdCell(e.fix_hint)} | \`${e.cropPath}\` |`,
      )
    }
    lines.push('')
  }

  lines.push('## Skipped States (React Precondition Unmet in Seeded Test Instance)')
  lines.push('')
  lines.push('| State | Room | Kind | React Opened | Native Opened | Reason |')
  lines.push('|---|---|---|---|---|---|')
  for (const sk of data.skippedStates) {
    lines.push(
      `| \`${sk.stateId}\` | ${sk.room} | ${sk.kind} | ${sk.openedReact ? 'Y' : 'N'} | ${sk.openedNative ? 'Y' : 'N'} | ${sk.reason} |`,
    )
  }
  lines.push('')

  lines.push('## Re-running Locally')
  lines.push('')
  lines.push('```powershell')
  lines.push('cd apps/launcher')
  lines.push('node tools/parity/run.mjs --seed-from "$env:TEMP\\exo-parity-seed-qa" --report-dir parity-report/vision-qa --vision')
  lines.push('node tools/parity/vision.mjs --report-dir parity-report/vision-qa')
  lines.push('```')
  lines.push('')
  return lines.join('\n')
}

const isMain = process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url
if (isMain) {
  generateVisionBacklog()
    .then((data) => {
      console.log('Generated apps/launcher/improve/vision-parity.md and vision-parity.json')
      console.log('Totals:', JSON.stringify(data.totals))
      for (const s of data.wpSummary) {
        console.log(
          `${s.wp.padEnd(8)} total=${String(s.total).padStart(3)} B=${String(s.blocker).padStart(2)}(open:${s.openBlockers}) M=${String(s.major).padStart(2)}(open:${s.openMajors}) m=${String(s.minor).padStart(2)} | open=${s.openGap} partial=${s.partialOnBranch} fixed=${s.fixedOnBranch}`,
        )
      }
      console.log('Top 10 Open Blockers:')
      data.topOpenBlockers.slice(0, 10).forEach((b, i) => {
        console.log(`  ${i + 1}. [${b.wp}] ${b.stateId}: ${b.area} — ${b.native.slice(0, 110)}`)
      })
    })
    .catch((err) => {
      console.error(err?.stack || String(err))
      process.exit(1)
    })
}
